using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using ChessBot;

/// <summary>
/// Same behavior as before, but driven by Canvas/UGUI elements instead of the
/// legacy OnGUI/IMGUI system. Wire up in the inspector:
///   - fenText / pgnText / statusText: any UI.Text (or swap to TMP_Text, see note below)
///   - moveInputField: an InputField for typing moves ("e2e4") or FEN strings
///   - playMoveButton / goButton / resetButton / loadFenButton / logBoardButton:
///     UI.Button components. OnClick listeners are wired automatically in
///     Start() - you do NOT need to set anything in the button's OnClick()
///     list in the Inspector.
///
/// If your project uses TextMeshPro instead of legacy UI.Text/InputField,
/// replace "Text" with "TMPro.TMP_Text" and "InputField" with
/// "TMPro.TMP_InputField" throughout (add `using TMPro;` at the top) - the
/// API surface (.text, .interactable, etc.) is the same.
/// </summary>
public class ChessBotRunner : MonoBehaviour
{
    [Header("Engine Settings")]
    public int ttSize = 1024;
    public int thinkDepth = 40;
    // Time limit in milliseconds for the search (passed to Search.FindBestMove)
    public int timeLimitMs = 1000;

    [Header("Exploitative Search (Asymmetric Alpha-Beta)")]
    [Tooltip("Our own moves are iteratively deepened up to this depth, same as Go's thinkDepth.")]
    public int exploitSelfDepth = 40;
    [Tooltip("The opponent's replies are capped at this much shallower depth, modeling them as a tactically blind/weak player. Keep well below exploitSelfDepth. 2-3 = very blind, 4-5 = sees basic tactics.")]
    public int exploitOppDepth = 3;
    [Tooltip("Only our top N ordered candidate moves per node get a full opponent-reply search (the expensive part - see Search.cs class docs). The rest get a cheap static-eval-only approximation. This is the main lever for total search cost - lower it if Go (Exploit) is too slow, raise it if it's missing good moves that got ranked outside the top N.")]
    public int exploitSelfMoveLimit = 10;

    [Header("Vs Deterministic Weak Opponent")]
    [Tooltip("The opponent's fixed plain-negamax depth (their 'lookahead'), evaluated with material-only eval.")]
    public int weakOppSearchDepth = 3;
    [Tooltip("How many further plies of captures/checks-only tactical lookahead the opponent gets once weakOppSearchDepth is exhausted.")]
    public int weakOppExtensionPlies = 3;
    [Tooltip("Same safety valve as Go Exploit - only the top N ordered self-candidates get the full opponent-reply treatment. High by default since this opponent model is cheap.")]
    public int weakOppSelfMoveLimit = 9999;

    [Header("Adaptive Search (Skill-Based Move Selection)")]
    [Tooltip("Fixed for now (per your setup) - manual/auto-detect can be added later. Strong = plays the top engine move unchanged. Intermediate/Weak bias toward objectively-close-to-best moves that are harder for that skill level to defend.")]
    public OpponentSkill opponentSkill = OpponentSkill.Intermediate;
    [Tooltip("How many top candidate moves FindTopMoves considers before the skill-based trickiness pass picks among them.")]
    public int adaptiveMultiPvCount = 6;
    [Tooltip("Time budget in ms for Go (Exploit), same role as timeLimitMs is for the normal Go button.")]
    public int exploitTimeLimitMs = 1000;

    [Header("3D Board")]
    [Tooltip("Optional. If assigned, moves are animated on the 3D board and kept in sync with the logical board state.")]
    public BoardVisualizer boardVisualizer;
    public float moveAnimationDuration = 0.25f;

    [Header("Think / Accept")]
    [Tooltip("Optional. If assigned, Think draws arrows for the top candidate moves.")]
    public ArrowDrawer arrowDrawer;
    [Tooltip("How many candidate moves Think evaluates and draws arrows for.")]
    public int topMovesCount = 3;
    [Tooltip("Best candidate is drawn in this color.")]
    public UnityEngine.Color bestMoveColor = UnityEngine.Color.green;
    [Tooltip("The weakest of the shown candidates is drawn in this color. Candidates in between are gradiented from best to worst.")]
    public UnityEngine.Color worstMoveColor = UnityEngine.Color.yellow;

    [Header("Eval Bar (optional)")]
    [Tooltip("A UI Slider with min value 0 and max value 1. 1 = fully filled toward White winning, 0 = fully filled toward Black winning, 0.5 = even.")]
    public Slider evalSlider;
    [Tooltip("Optional. Shows the raw centipawn/mate score as text next to the bar.")]
    public TMP_Text evalText;
    [Tooltip("Centipawn score at which the eval bar is considered ~99% saturated toward one side. Higher = the bar stays closer to 50/50 for longer before swinging hard.")]
    public float evalSaturationCp = 400f;

    [Header("UI - Text")]
    public TMP_Text fenText;
    public TMP_Text pgnText;
    public TMP_Text statusText;
    [Tooltip("Shows depth/nodes/time/nps stats for the last completed search (or the current one, for Think Extended). Shows 'No search results' after Reset/Load FEN until a search actually runs.")]
    public TMP_Text searchInfoText;

    [Header("Think Time")]
    [Tooltip("Slider controlling Go/Think's time budget in milliseconds. Set the Slider's own Min/Max Value in the Inspector (e.g. 100 to 10000) - this script just reads whatever range you configure there.")]
    public Slider thinkTimeSlider;
    [Tooltip("Optional. Shows the current think time next to the slider.")]
    public TMP_Text thinkTimeLabel;

    [Header("UI - Input")]
    public TMP_InputField moveInputField;

    [Header("UI - Buttons")]
    public Button playMoveButton;
    public Button goButton;
    public Button goExploitButton;
    public Button goAdaptiveButton;
    public Button goTrappyButton;
    public Button goVsWeakButton;
    public Button playWeakOpponentMoveButton;
    public Button resetButton;
    public Button loadFenButton;
    public Button logBoardButton;
    public Button thinkButton;
    public Button acceptButton;
    public Button thinkExtendedButton;
    public Button stopExtendedButton;

    private Board board;
    private TranspositionTable tt;

    private readonly object _lock = new object();
    private bool _isThinking = false;
    private bool _gameOver = false;
    private Move _pendingMove;
    private int _pendingMoveScore;
    private bool _hasPendingMove = false;
    private string _status = "";

    // Search info text (depth/nodes/time/nps line) - set from whatever
    // background thread a search is running on, applied to the UI text
    // component on the main thread in Update(), same pattern as
    // _pendingMove/_pendingCandidates.
    private string _pendingSearchInfoText;
    private bool _hasPendingSearchInfoText = false;

    // Candidate moves from the last Think click (sorted best-first), and the
    // arrows currently drawn for them.
    private List<(Move move, int score)> _candidateMoves = new List<(Move, int)>();
    private List<(Move move, int score)> _pendingCandidates;
    private bool _hasPendingCandidates = false;
    private readonly List<GameObject> _candidateArrows = new List<GameObject>();

    // Think Extended (continuous/"infinite analysis" search) state.
    private bool _isExtendedThinking = false;
    private Search _continuousSearch; // kept so the Stop button can call RequestStop() on the running instance

    void Start()
    {
        board = new Board();
        tt = new TranspositionTable(ttSize);
        Debug.Log("ChessBotRunner started. FEN: " + board.ToFen());

        // Wire up button clicks in code so nothing needs to be dragged into
        // each Button's OnClick() list in the Inspector.
        if (playMoveButton != null) playMoveButton.onClick.AddListener(OnPlayMoveClicked);
        if (goButton != null) goButton.onClick.AddListener(OnGoClicked);
        if (goExploitButton != null) goExploitButton.onClick.AddListener(OnGoExploitClicked);
        if (goAdaptiveButton != null) goAdaptiveButton.onClick.AddListener(OnGoAdaptiveClicked);
        if (goTrappyButton != null) goTrappyButton.onClick.AddListener(OnGoTrappyClicked);
        if (goVsWeakButton != null) goVsWeakButton.onClick.AddListener(OnGoVsWeakClicked);
        if (playWeakOpponentMoveButton != null) playWeakOpponentMoveButton.onClick.AddListener(OnPlayWeakOpponentMoveClicked);
        if (resetButton != null) resetButton.onClick.AddListener(OnResetClicked);
        if (loadFenButton != null) loadFenButton.onClick.AddListener(OnLoadFenClicked);
        if (logBoardButton != null) logBoardButton.onClick.AddListener(OnLogBoardClicked);
        if (thinkButton != null) thinkButton.onClick.AddListener(OnThinkClicked);
        if (thinkExtendedButton != null) thinkExtendedButton.onClick.AddListener(OnThinkExtendedClicked);
        if (stopExtendedButton != null)
        {
            stopExtendedButton.onClick.AddListener(OnStopExtendedClicked);
            stopExtendedButton.interactable = false; // nothing running yet
        }
        if (acceptButton != null)
        {
            acceptButton.onClick.AddListener(OnAcceptClicked);
            acceptButton.interactable = false; // nothing to accept until Think produces candidates
        }

        RefreshUiText();
        if (boardVisualizer != null) boardVisualizer.Sync(board);
        if (evalSlider != null) evalSlider.value = 0.5f;
        if (evalText != null) evalText.text = "0.00";
        if (searchInfoText != null) searchInfoText.text = "No search results";

        if (thinkTimeSlider != null)
        {
            // Reflects whatever value is currently set on timeLimitMs (e.g.
            // from the Inspector default) rather than forcing a fixed value,
            // and keeps the label in sync.
            thinkTimeSlider.value = timeLimitMs;
            thinkTimeSlider.onValueChanged.AddListener(OnThinkTimeChanged);
        }
        UpdateThinkTimeLabel(timeLimitMs);
    }

    public void OnPlayMoveClicked()
    {
        if (_isThinking || moveInputField == null) return;
        TryPlayMove(moveInputField.text.Trim());
        RefreshUiText();
    }

    public void OnGoClicked()
    {
        if (_isThinking || _gameOver) return;
        StartSearch();
    }

    public void OnGoExploitClicked()
    {
        if (_isThinking || _gameOver) return;
        StartSearchExploitative();
    }

    public void OnGoAdaptiveClicked()
    {
        if (_isThinking || _gameOver) return;
        StartSearchAdaptive();
    }

    public void OnGoTrappyClicked()
    {
        if (_isThinking || _gameOver) return;
        StartSearchTrappy();
    }

    public void OnGoVsWeakClicked()
    {
        if (_isThinking || _gameOver) return;
        StartSearchVsWeak();
    }

    /// <summary>
    /// Has whichever side is actually to move right now play a move using
    /// the SAME deterministic weak-opponent policy that Go (Vs Weak)
    /// simulates internally (Search.FindWeakOpponentMove) - so you can
    /// actually play against this exact policy, or watch it play both
    /// sides, rather than only seeing it modeled inside the other search.
    /// </summary>
    public void OnPlayWeakOpponentMoveClicked()
    {
        if (_isThinking || _gameOver) return;
        StartWeakOpponentMove();
    }

    public void OnResetClicked()
    {
        if (_isThinking) return; // avoid swapping the board out from under an in-flight search
        board = new Board();
        _gameOver = false;
        Debug.Log("Reset position.");
        RefreshUiText();
        if (boardVisualizer != null) boardVisualizer.AnimateToBoard(board, moveAnimationDuration);
        ClearStaleCandidates();
        if (evalSlider != null) evalSlider.value = 0.5f;
        if (evalText != null) evalText.text = "0.00";
        if (searchInfoText != null) searchInfoText.text = "No search results";
        lock (_lock) { _hasPendingSearchInfoText = false; }
    }

    public void OnLoadFenClicked()
    {
        if (_isThinking || moveInputField == null) return;
        try
        {
            board.SetFen(moveInputField.text.Trim());
            _gameOver = false;
            Debug.Log("Loaded FEN.");
        }
        catch
        {
            Debug.LogError("Invalid FEN.");
        }
        RefreshUiText();
        if (boardVisualizer != null) boardVisualizer.AnimateToBoard(board, moveAnimationDuration);
        ClearStaleCandidates();
        if (evalSlider != null) evalSlider.value = 0.5f;
        if (evalText != null) evalText.text = "0.00";
        if (searchInfoText != null) searchInfoText.text = "No search results";
        lock (_lock) { _hasPendingSearchInfoText = false; }
    }

    public void OnLogBoardClicked()
    {
        Debug.Log(board.ToMoveText());
    }

    public void OnThinkClicked()
    {
        if (_isThinking || _gameOver) return;
        StartThink();
    }

    public void OnThinkExtendedClicked()
    {
        if (_isThinking || _isExtendedThinking || _gameOver) return;
        StartThinkExtended();
    }

    public void OnStopExtendedClicked()
    {
        if (!_isExtendedThinking) return;
        _continuousSearch?.RequestStop();
        _isExtendedThinking = false;
        _status = "";
        RefreshUiText();
    }

    public void OnAcceptClicked()
    {
        if (_isThinking || _gameOver || _candidateMoves.Count == 0) return;

        // If extended think is still running, stop it - we're about to
        // change the board out from under its search clone.
        if (_isExtendedThinking)
        {
            _continuousSearch?.RequestStop();
            _isExtendedThinking = false;
        }

        Move best = _candidateMoves[0].move;
        int fromSq = best.From();
        int toSq = best.To();
        Debug.Log("Accepted: " + best);

        board.MakeMove(best);
        CheckGameOver();

        if (boardVisualizer != null)
            boardVisualizer.AnimateMoveThenSync(board, fromSq, toSq, moveAnimationDuration);

        ClearCandidateArrows();
        _candidateMoves.Clear();
        if (acceptButton != null) acceptButton.interactable = false;

        RefreshUiText();
    }

    // ---- Internals ----------------------------------------------------------

    private void TryPlayMove(string moveStr)
    {
        var legal = MoveGen.GenerateLegalMoves(board);
        var found = legal.FirstOrDefault(m => m.ToString() == moveStr);
        if (!found.IsNull)
        {
            int fromSq = found.From();
            int toSq = found.To();
            board.MakeMove(found);
            CheckGameOver();

            if (boardVisualizer != null)
                boardVisualizer.AnimateMoveThenSync(board, fromSq, toSq, moveAnimationDuration);

            ClearStaleCandidates();
        }
        else
        {
            Debug.Log("Illegal or unrecognized move. Legal moves: " + string.Join(" ", legal.Select(m => m.ToString())));
        }
    }

    void OnDestroy()
    {
        // Ask any in-flight Think Extended search to wind down as soon as
        // possible when this component goes away (Play Mode stopping, scene
        // unload, etc). This is a best-effort courtesy, not a guarantee: if
        // Unity aborts the background thread before it notices the flag,
        // you may still see a one-off ThreadAbortException in the console.
        // That's expected in that case and safe to ignore - both StartThink
        // and StartThinkExtended already swallow it rather than logging it
        // as a real error.
        _continuousSearch?.RequestStop();
    }

    void Update()
    {
        // Apply pending move from background search on main thread
        if (_hasPendingMove)
        {
            lock (_lock)
            {
                if (_hasPendingMove)
                {
                    if (_pendingMove.IsNull)
                    {
                        // The search itself found no legal moves - confirm via
                        // CheckGameOver so the status/UI correctly distinguish
                        // checkmate vs stalemate rather than a generic message.
                        CheckGameOver();
                    }
                    else
                    {
                        Debug.Log("Bot plays: " + _pendingMove);
                        // Score is relative to whichever side was to move when
                        // the search ran - convert to White's perspective
                        // BEFORE MakeMove flips SideToMove.
                        UpdateEvalBar(_pendingMoveScore, board.SideToMove);

                        int fromSq = _pendingMove.From();
                        int toSq = _pendingMove.To();
                        board.MakeMove(_pendingMove);
                        CheckGameOver();

                        if (boardVisualizer != null)
                            boardVisualizer.AnimateMoveThenSync(board, fromSq, toSq, moveAnimationDuration);

                        ClearStaleCandidates();
                    }
                    _hasPendingMove = false;
                    _isThinking = false;
                    if (!_gameOver) _status = "";
                    RefreshUiText();
                }
            }
        }

        // Apply pending candidate moves from a background Think on main thread
        if (_hasPendingCandidates)
        {
            lock (_lock)
            {
                if (_hasPendingCandidates)
                {
                    _candidateMoves = _pendingCandidates;
                    _pendingCandidates = null;
                    _hasPendingCandidates = false;
                    _isThinking = false;
                    if (!_gameOver) _status = "";

                    if (_candidateMoves.Count > 0)
                        UpdateEvalBar(_candidateMoves[0].score, board.SideToMove);

                    ClearCandidateArrows(); // remove the previous report's arrows before drawing the new ones
                    DrawCandidateArrows();

                    if (acceptButton != null)
                        acceptButton.interactable = _candidateMoves.Count > 0;

                    RefreshUiText();
                }
            }
        }

        // Apply pending search-info text from a background search on main thread
        if (_hasPendingSearchInfoText)
        {
            lock (_lock)
            {
                if (_hasPendingSearchInfoText)
                {
                    if (searchInfoText != null) searchInfoText.text = _pendingSearchInfoText;
                    _hasPendingSearchInfoText = false;
                }
            }
        }
    }

    /// <summary>
    /// Updates the eval slider/text from a score that's relative to
    /// `sideToMoveAtSearchRoot` (NegaMax convention: positive = good for
    /// whoever was to move when the search ran). Converts to White's
    /// perspective, then squashes centipawns into a 0..1 slider fill using a
    /// logistic curve (same idea as lichess/chess.com eval bars) so huge
    /// advantages saturate toward the ends instead of running off-scale.
    /// </summary>
    private void UpdateEvalBar(int scoreRelativeToMover, ChessBot.Color sideToMoveAtSearchRoot)
    {
        int whiteScore = sideToMoveAtSearchRoot == ChessBot.Color.White ? scoreRelativeToMover : -scoreRelativeToMover;

        if (evalSlider != null)
        {
            // Logistic curve: 0.5 at even, approaches 1/0 as |cp| grows.
            // evalSaturationCp controls how quickly it saturates.
            double clampedCp = Mathf.Clamp(whiteScore, -Search.MateScore, Search.MateScore);
            double winProbWhite = 1.0 / (1.0 + System.Math.Pow(10.0, -clampedCp / evalSaturationCp));
            evalSlider.value = (float)winProbWhite;
        }

        if (evalText != null)
        {
            bool isMate = System.Math.Abs(whiteScore) > Search.MateScore - Search.MaxPly;
            if (isMate)
            {
                int pliesToMate = Search.MateScore - System.Math.Abs(whiteScore);
                int movesToMate = Mathf.CeilToInt(pliesToMate / 2f);
                evalText.text = whiteScore > 0 ? $"M{movesToMate}" : $"-M{movesToMate}";
            }
            else
            {
                evalText.text = (whiteScore / 100f).ToString("+0.00;-0.00;0.00");
            }
        }
    }

    private void OnThinkTimeChanged(float value)
    {
        timeLimitMs = Mathf.RoundToInt(value);
        UpdateThinkTimeLabel(timeLimitMs);
    }

    private void UpdateThinkTimeLabel(int ms)
    {
        if (thinkTimeLabel != null) thinkTimeLabel.text = $"{(ms / 1000f):0.00}s";
    }

    private void RefreshUiText()
    {
        if (fenText != null) fenText.text = "FEN: " + board.ToFen();
        if (pgnText != null) pgnText.text = "PGN: " + board.ToPgn();
        if (statusText != null) statusText.text = "Status: " + _status;

        bool interactable = !_isThinking && !_isExtendedThinking && !_gameOver;
        if (playMoveButton != null) playMoveButton.interactable = interactable;
        if (goButton != null) goButton.interactable = interactable;
        if (goExploitButton != null) goExploitButton.interactable = interactable;
        if (goAdaptiveButton != null) goAdaptiveButton.interactable = interactable;
        if (goTrappyButton != null) goTrappyButton.interactable = interactable;
        if (goVsWeakButton != null) goVsWeakButton.interactable = interactable;
        if (playWeakOpponentMoveButton != null) playWeakOpponentMoveButton.interactable = interactable;
        if (loadFenButton != null) loadFenButton.interactable = !_isThinking && !_isExtendedThinking;
        if (resetButton != null) resetButton.interactable = !_isThinking && !_isExtendedThinking; // always allowed to start a new game, even after game over
        if (thinkButton != null) thinkButton.interactable = interactable;
        if (thinkExtendedButton != null) thinkExtendedButton.interactable = interactable;
        if (stopExtendedButton != null) stopExtendedButton.interactable = _isExtendedThinking;
        // logBoardButton stays interactable regardless - it's read-only.
        // acceptButton: only force it OFF while a one-shot Think/Go is
        // running, or the game has ended. During Think Extended it's
        // deliberately left alone here so the candidate-processing code can
        // enable it once real candidates exist - you're meant to be able to
        // Accept mid-analysis.
        if (acceptButton != null && (_isThinking || _gameOver)) acceptButton.interactable = false;
    }

    /// <summary>
    /// Checks the LIVE board (not the search's internal repetition-as-draw
    /// heuristic, which only affects move evaluation) for actual game-ending
    /// conditions - checkmate, stalemate, threefold repetition, or the
    /// 50-move rule - and updates status/UI accordingly. Call this after
    /// every point where a move is actually applied to `board` (manual play,
    /// bot move via Go, Accept).
    /// </summary>
    private void CheckGameOver()
    {
        var legal = MoveGen.GenerateLegalMoves(board);
        if (legal.Count == 0)
        {
            bool inCheck = board.InCheck(board.SideToMove);
            string winner = board.SideToMove == ChessBot.Color.White ? "Black" : "White";
            _status = inCheck ? $"Checkmate - {winner} wins" : "Stalemate - draw";
            _gameOver = true;
        }
        else if (board.IsRepetition())
        {
            _status = "Draw by threefold repetition";
            _gameOver = true;
        }
        else if (board.HalfmoveClock >= 100)
        {
            _status = "Draw by 50-move rule";
            _gameOver = true;
        }

        if (_gameOver)
        {
            Debug.Log("Game over: " + _status);
            if (_isExtendedThinking)
            {
                _continuousSearch?.RequestStop();
                _isExtendedThinking = false;
            }
        }
    }

    private void StartThinkExtended()
    {
        if (_isThinking || _isExtendedThinking || _gameOver) return;
        _isExtendedThinking = true;
        _status = "Thinking (extended)...";
        ClearCandidateArrows();
        _candidateMoves.Clear();
        if (acceptButton != null) acceptButton.interactable = false;
        if (stopExtendedButton != null) stopExtendedButton.interactable = true;
        RefreshUiText();

        // Same thread-safety reasoning as StartSearch()/StartThink(): search
        // mutates the board it's given, so it must run on a private clone,
        // never the live board the main thread reads every frame. This
        // clone lives for the entire duration of the continuous search
        // (until Stop or Accept), unlike the one-shot searches.
        Board searchBoard = board.Clone();
        var search = new Search(tt);
        _continuousSearch = search; // so Stop/Accept can call RequestStop() on it

        Task.Run(() =>
        {
            try
            {
                // Same single-PV search as Go, just run indefinitely (no
                // depth/time cap) until RequestStop() is called. Only ever
                // reports once a depth is FULLY completed - since there's
                // just one (move, score) result at a time rather than a
                // per-move dictionary, there's no way for a report to show a
                // partial mix of stale/fresh data, which is what caused the
                // old multi-PV version's "garbage moves at a new depth" bug.
                search.SearchContinuousSingle(searchBoard, (depth, nodes, elapsedMs, bestMove, score) =>
                {
                    var top = new List<(Move move, int score)> { (bestMove, score) };
                    LogSearchInfo("Extended", depth, nodes, elapsedMs, top);

                    // Reuses the exact same pending-candidates pipeline as
                    // one-shot Think - Update() already knows how to apply
                    // these (draw arrow, update eval bar, enable Accept). A
                    // single-entry list just means DrawCandidateArrows draws
                    // exactly one arrow, colored as bestMoveColor.
                    lock (_lock)
                    {
                        _pendingCandidates = top;
                        _hasPendingCandidates = true;
                    }
                });
            }
            catch (System.Threading.ThreadAbortException)
            {
                // Expected when Play Mode stops (or scripts reload) while
                // this background thread is still running - Unity aborts it
                // out from under us. Not a real error, nothing to log.
            }
            catch (System.Exception ex)
            {
                Debug.LogError("Think Extended error: " + ex);
            }
        });
    }

    private void StartThink()
    {
        if (_isThinking) return;
        _isThinking = true;
        _status = "Thinking...";
        ClearCandidateArrows();
        _candidateMoves.Clear();
        if (acceptButton != null) acceptButton.interactable = false;
        RefreshUiText();

        // Same thread-safety reasoning as StartSearch(): search mutates the
        // board it's given, so it must run on a private clone, never the
        // live board that the main thread reads every frame.
        Board searchBoard = board.Clone();
        Task.Run(() =>
        {
            try
            {
                var search = new Search(tt);
                var top = search.FindTopMoves(searchBoard, thinkDepth, timeLimitMs, topMovesCount);
                LogSearchInfo("Think", search.LastCompletedDepth, search.Nodes, search.ElapsedMs, top);
                lock (_lock)
                {
                    _pendingCandidates = top;
                    _hasPendingCandidates = true;
                }
            }
            catch (System.Threading.ThreadAbortException)
            {
                // Expected when Play Mode stops while this thread is running - not a real error.
            }
            catch (System.Exception ex)
            {
                Debug.LogError("Think error: " + ex);
                lock (_lock)
                {
                    _pendingCandidates = new List<(Move, int)>();
                    _hasPendingCandidates = true;
                }
            }
        });
    }

    /// <summary>
    /// UCI-style debug line for Think / Think Extended: depth reached, nodes
    /// searched, elapsed time, nodes-per-second, and the current top
    /// candidate moves with their scores. Called from the search's
    /// background thread.
    /// </summary>
    private void LogSearchInfo(string label, int depth, long nodes, long elapsedMs, List<(Move move, int score)> topMoves)
    {
        string moveList = string.Join(", ", topMoves.Select(t => $"{t.move} ({t.score})"));
        SetSearchInfo($"[{label}] depth {depth} nodes {nodes} time {elapsedMs}ms nps {NodesPerSecond(nodes, elapsedMs)} top: {moveList}");
        SetSearchInfo($"Search Info:\n Depth: {depth},\n Nodes: {nodes},\n Time: {elapsedMs}ms,\n NPS: {NodesPerSecond(nodes, elapsedMs)},\n Top Moves: {moveList}");

    }

    /// <summary>
    /// Logs a search-info line to the console (Debug.Log is safe to call off
    /// the main thread in Unity, it just gets queued) AND thread-safely
    /// queues it to be shown in searchInfoText on the main thread. Called
    /// from whatever background thread a search happens to be running on -
    /// never touches the UI component directly here, since Unity UI can only
    /// be touched from the main thread.
    /// </summary>
    private void SetSearchInfo(string line)
    {
        Debug.Log(line);
        lock (_lock)
        {
            _pendingSearchInfoText = line;
            _hasPendingSearchInfoText = true;
        }
    }

    private static long NodesPerSecond(long nodes, long elapsedMs)
    {
        if (elapsedMs <= 0) return 0;
        return nodes * 1000L / elapsedMs;
    }

    private void DrawCandidateArrows()
    {
        if (arrowDrawer == null || _candidateMoves.Count == 0) return;

        int count = _candidateMoves.Count;
        for (int i = 0; i < count; i++)
        {
            Move m = _candidateMoves[i].move;
            // i==0 (best) -> bestMoveColor; last (worst shown) -> worstMoveColor;
            // everything in between gradiented linearly.
            float t = count > 1 ? i / (float)(count - 1) : 0f;
            UnityEngine.Color color = UnityEngine.Color.Lerp(bestMoveColor, worstMoveColor, t);

            GameObject arrow = arrowDrawer.DrawArrow(m.From(), m.To(), color);
            if (arrow != null) _candidateArrows.Add(arrow);
        }
    }

    private void ClearCandidateArrows()
    {
        if (arrowDrawer == null) return;
        foreach (var arrow in _candidateArrows)
            arrowDrawer.RemoveArrow(arrow);
        _candidateArrows.Clear();
    }

    /// <summary>
    /// Wipes any leftover Think candidates/arrows whenever the position
    /// changes for a reason other than Accept (manual move, bot move via Go,
    /// Reset, Load FEN) - otherwise they'd be pointing at moves that are no
    /// longer legal in the new position.
    /// </summary>
    private void ClearStaleCandidates()
    {
        if (_isExtendedThinking)
        {
            _continuousSearch?.RequestStop();
            _isExtendedThinking = false;
        }
        if (_candidateMoves.Count == 0 && _candidateArrows.Count == 0) return;
        ClearCandidateArrows();
        _candidateMoves.Clear();
        if (acceptButton != null) acceptButton.interactable = false;
    }

    private void StartSearch()
    {
        if (_isThinking) return;
        _isThinking = true;
        _status = "Thinking...";
        RefreshUiText();

        // Run search on background thread to avoid freezing Unity main thread.
        // IMPORTANT: search mutates the board it's given (MakeMove/UnmakeMove) on
        // every node, so it must never be handed the live `board` object - that
        // object is read every frame (ToFen/ToPgn/etc) on the main thread via
        // RefreshUiText, and concurrent mutation + read of its internal move-
        // history lists causes ArgumentOutOfRangeException / corrupted state.
        // Give the search a private clone instead; only the resulting best
        // move ever gets applied to the live board, and that happens on the
        // main thread in Update().
        Board searchBoard = board.Clone();
        Task.Run(() =>
        {
            try
            {
                var search = new Search(tt);
                Move best = search.FindBestMove(searchBoard, thinkDepth, timeLimitMs);
                Debug.Log($"[Go] depth {search.LastCompletedDepth} nodes {search.Nodes} time {search.ElapsedMs}ms " +
                    $"nps {NodesPerSecond(search.Nodes, search.ElapsedMs)} bestmove {best} score {search.BestScoreRoot}");

                SetSearchInfo($"Search Info:\n Depth: {search.LastCompletedDepth},\n Nodes: {search.Nodes},\n Time: {search.ElapsedMs}ms,\n NPS: {NodesPerSecond(search.Nodes, search.ElapsedMs)},\n BestMove: {best},\n Score: {search.BestScoreRoot}");
                lock (_lock)
                {
                    _pendingMove = best;
                    _pendingMoveScore = search.BestScoreRoot;
                    _hasPendingMove = true;
                }
            }
            catch (System.Threading.ThreadAbortException)
            {
                // Expected when Play Mode stops while this thread is running - not a real error.
            }
            catch (System.Exception ex)
            {
                Debug.LogError("Search error: " + ex);
                lock (_lock)
                {
                    _pendingMove = default;
                    _hasPendingMove = true;
                }
            }
        });
    }

    /// <summary>
    /// Same as StartSearch(), but drives Search.FindBestMoveExploitative
    /// instead of FindBestMove - our own moves get exploitSelfDepth (same
    /// meaning as thinkDepth), the opponent's replies are capped at the much
    /// shallower exploitOppDepth. Everything else (background thread,
    /// board-clone safety, the _pendingMove/_lock hand-off to Update(), eval
    /// bar update, search-info logging) is identical to Go's pipeline, so
    /// this plugs straight into the existing UI/game-loop plumbing - no
    /// special-casing needed anywhere else for the Exploit button to "just
    /// work" the same way Go does.
    /// </summary>
    private void StartSearchExploitative()
    {
        if (_isThinking) return;
        _isThinking = true;
        _status = "Thinking (exploit)...";
        RefreshUiText();

        Board searchBoard = board.Clone();
        Task.Run(() =>
        {
            try
            {
                var search = new Search(tt);
                Move best = search.FindBestMoveExploitative(searchBoard, exploitSelfDepth, exploitOppDepth, exploitTimeLimitMs, exploitSelfMoveLimit);
                Debug.Log($"[Go-Exploit] depth {search.LastCompletedDepth} (opp cap {exploitOppDepth}, move limit {exploitSelfMoveLimit}) nodes {search.Nodes} time {search.ElapsedMs}ms " +
                    $"nps {NodesPerSecond(search.Nodes, search.ElapsedMs)} bestmove {best} score {search.BestScoreRoot}");

                SetSearchInfo($"Search Info (Exploit):\n Depth: {search.LastCompletedDepth},\n Opp Depth Cap: {exploitOppDepth},\n Nodes: {search.Nodes},\n Time: {search.ElapsedMs}ms,\n NPS: {NodesPerSecond(search.Nodes, search.ElapsedMs)},\n BestMove: {best},\n Score: {search.BestScoreRoot}");
                lock (_lock)
                {
                    _pendingMove = best;
                    _pendingMoveScore = search.BestScoreRoot;
                    _hasPendingMove = true;
                }
            }
            catch (System.Threading.ThreadAbortException)
            {
                // Expected when Play Mode stops while this thread is running - not a real error.
            }
            catch (System.Exception ex)
            {
                Debug.LogError("Exploitative search error: " + ex);
                lock (_lock)
                {
                    _pendingMove = default;
                    _hasPendingMove = true;
                }
            }
        });
    }

    /// <summary>
    /// Same background-thread/board-clone/lock pattern as StartSearch and
    /// StartSearchExploitative - drives Search.FindBestMoveAdaptive using
    /// the fixed opponentSkill setting configured in the Inspector.
    /// </summary>
    private void StartSearchAdaptive()
    {
        if (_isThinking) return;
        _isThinking = true;
        _status = "Thinking (adaptive)...";
        RefreshUiText();

        OpponentSkill skill = opponentSkill; // capture on main thread before handing off
        Board searchBoard = board.Clone();
        Task.Run(() =>
        {
            try
            {
                var search = new Search(tt);
                Move best = search.FindBestMoveAdaptive(searchBoard, thinkDepth, timeLimitMs, skill, adaptiveMultiPvCount);
                Debug.Log($"[Go-Adaptive] skill {skill} depth {search.LastCompletedDepth} nodes {search.Nodes} time {search.ElapsedMs}ms " +
                    $"nps {NodesPerSecond(search.Nodes, search.ElapsedMs)} bestmove {best} score {search.BestScoreRoot}");

                SetSearchInfo($"Search Info (Adaptive - {skill}):\n Depth: {search.LastCompletedDepth},\n Nodes: {search.Nodes},\n Time: {search.ElapsedMs}ms,\n NPS: {NodesPerSecond(search.Nodes, search.ElapsedMs)},\n BestMove: {best},\n Score: {search.BestScoreRoot}");
                lock (_lock)
                {
                    _pendingMove = best;
                    _pendingMoveScore = search.BestScoreRoot;
                    _hasPendingMove = true;
                }
            }
            catch (System.Threading.ThreadAbortException)
            {
                // Expected when Play Mode stops while this thread is running - not a real error.
            }
            catch (System.Exception ex)
            {
                Debug.LogError("Adaptive search error: " + ex);
                lock (_lock)
                {
                    _pendingMove = default;
                    _hasPendingMove = true;
                }
            }
        });
    }

    /// <summary>
    /// Same background-thread/board-clone/lock pattern as StartSearch -
    /// drives Search.FindBestMoveTrappy, which is a full standalone
    /// duplicate of FindBestMove that additionally activates
    /// Evaluation.SelfColor's opponent-exposure bias (see Evaluation.cs and
    /// Search.cs's FindBestMoveTrappy doc comment). FindBestMove itself is
    /// untouched by this - normal Go still behaves exactly as before.
    /// </summary>
    private void StartSearchTrappy()
    {
        if (_isThinking) return;
        _isThinking = true;
        _status = "Thinking (trappy)...";
        RefreshUiText();

        Board searchBoard = board.Clone();
        Task.Run(() =>
        {
            try
            {
                var search = new Search(tt);
                Move best = search.FindBestMoveTrappy(searchBoard, thinkDepth, timeLimitMs);
                Debug.Log($"[Go-Trappy] depth {search.LastCompletedDepth} nodes {search.Nodes} time {search.ElapsedMs}ms " +
                    $"nps {NodesPerSecond(search.Nodes, search.ElapsedMs)} bestmove {best} score {search.BestScoreRoot}");

                SetSearchInfo($"Search Info (Trappy):\n Depth: {search.LastCompletedDepth},\n Nodes: {search.Nodes},\n Time: {search.ElapsedMs}ms,\n NPS: {NodesPerSecond(search.Nodes, search.ElapsedMs)},\n BestMove: {best},\n Score: {search.BestScoreRoot}");
                lock (_lock)
                {
                    _pendingMove = best;
                    _pendingMoveScore = search.BestScoreRoot;
                    _hasPendingMove = true;
                }
            }
            catch (System.Threading.ThreadAbortException)
            {
                // Expected when Play Mode stops while this thread is running - not a real error.
            }
            catch (System.Exception ex)
            {
                Debug.LogError("Trappy search error: " + ex);
                lock (_lock)
                {
                    _pendingMove = default;
                    _hasPendingMove = true;
                }
            }
        });
    }

    /// <summary>
    /// Same background-thread/board-clone/lock pattern as StartSearch -
    /// drives Search.FindBestMoveVsWeakOpponent, which assumes a fully
    /// deterministic opponent playing a fixed depth-3 (+3 captures/checks)
    /// material-only policy (see Search.cs's doc comment on that method).
    /// </summary>
    private void StartSearchVsWeak()
    {
        if (_isThinking) return;
        _isThinking = true;
        _status = "Thinking (vs weak opponent)...";
        RefreshUiText();

        Board searchBoard = board.Clone();
        Task.Run(() =>
        {
            try
            {
                var search = new Search(tt);
                Move best = search.FindBestMoveVsWeakOpponent(searchBoard, thinkDepth, timeLimitMs, weakOppSearchDepth, weakOppExtensionPlies, weakOppSelfMoveLimit);
                Debug.Log($"[Go-VsWeak] depth {search.LastCompletedDepth} (opp depth {weakOppSearchDepth}+{weakOppExtensionPlies}) nodes {search.Nodes} time {search.ElapsedMs}ms " +
                    $"nps {NodesPerSecond(search.Nodes, search.ElapsedMs)} bestmove {best} score {search.BestScoreRoot}");

                SetSearchInfo($"Search Info (Vs Weak Opponent):\n Depth: {search.LastCompletedDepth},\n Opp Depth: {weakOppSearchDepth}+{weakOppExtensionPlies},\n Nodes: {search.Nodes},\n Time: {search.ElapsedMs}ms,\n NPS: {NodesPerSecond(search.Nodes, search.ElapsedMs)},\n BestMove: {best},\n Score: {search.BestScoreRoot}");
                lock (_lock)
                {
                    _pendingMove = best;
                    _pendingMoveScore = search.BestScoreRoot;
                    _hasPendingMove = true;
                }
            }
            catch (System.Threading.ThreadAbortException)
            {
                // Expected when Play Mode stops while this thread is running - not a real error.
            }
            catch (System.Exception ex)
            {
                Debug.LogError("Vs-weak-opponent search error: " + ex);
                lock (_lock)
                {
                    _pendingMove = default;
                    _hasPendingMove = true;
                }
            }
        });
    }

    /// <summary>
    /// Same background-thread/board-clone/lock pattern as the other Go
    /// variants - drives Search.FindWeakOpponentMove directly, letting
    /// whichever side is actually to move play according to the fixed
    /// deterministic weak-opponent policy (see Search.cs's doc comment on
    /// FindWeakOpponentMove and FindBestMoveVsWeakOpponent). Reuses the same
    /// weakOppSearchDepth/weakOppExtensionPlies Inspector settings as Go (Vs
    /// Weak), so both stay consistent with each other.
    /// </summary>
    private void StartWeakOpponentMove()
    {
        if (_isThinking) return;
        _isThinking = true;
        _status = "Thinking (weak opponent move)...";
        RefreshUiText();

        Board searchBoard = board.Clone();
        Task.Run(() =>
        {
            try
            {
                var search = new Search(tt);
                Move best = search.FindWeakOpponentMove(searchBoard, weakOppSearchDepth, weakOppExtensionPlies);
                Debug.Log($"[Weak-Opponent-Move] depth {weakOppSearchDepth}+{weakOppExtensionPlies} nodes {search.Nodes} time {search.ElapsedMs}ms " +
                    $"nps {NodesPerSecond(search.Nodes, search.ElapsedMs)} bestmove {best} score {search.BestScoreRoot}");

                SetSearchInfo($"Search Info (Weak Opponent Move):\n Opp Depth: {weakOppSearchDepth}+{weakOppExtensionPlies},\n Nodes: {search.Nodes},\n Time: {search.ElapsedMs}ms,\n NPS: {NodesPerSecond(search.Nodes, search.ElapsedMs)},\n BestMove: {best},\n Score: {search.BestScoreRoot}");
                lock (_lock)
                {
                    _pendingMove = best;
                    _pendingMoveScore = search.BestScoreRoot;
                    _hasPendingMove = true;
                }
            }
            catch (System.Threading.ThreadAbortException)
            {
                // Expected when Play Mode stops while this thread is running - not a real error.
            }
            catch (System.Exception ex)
            {
                Debug.LogError("Weak-opponent-move error: " + ex);
                lock (_lock)
                {
                    _pendingMove = default;
                    _hasPendingMove = true;
                }
            }
        });
    }
}