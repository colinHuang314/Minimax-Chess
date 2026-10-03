using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;

namespace ChessBot
{

/// <summary>
/// Assumed strength of the opponent, used by Search.FindBestMoveAdaptive to
/// decide how much (if at all) to prefer objectively-close-to-best moves
/// that are harder to defend over ones that are equally good but simple.
/// Never affects move LEGALITY or search CORRECTNESS - only which of the
/// already-strong candidate moves gets picked when several are close.
/// </summary>
public enum OpponentSkill
{
    Strong,
    Intermediate,
    Weak
}

public sealed class Search
{
    public const int Infinity = 1_000_000;
    public const int MateScore = 900_000;
    public const int MaxPly = 128;

    private readonly TranspositionTable _tt;
    private readonly Move[,] _killers = new Move[MaxPly, 2];
    private readonly int[,,] _history = new int[2, 64, 64];
    private Stopwatch _timer = new();
    private long _timeLimitMs;
    private bool _stop;
    private long _nodes;

    // Per-ply pooled buffers, reused across every node in the tree instead of
    // allocating a fresh List<Move>/int[] at every single call - this was the
    // dominant source of GC pressure in the hot search path (Mono's GC in
    // particular punishes this much harder than CoreCLR's does, which is why
    // it showed up more under Unity than under `dotnet run`). Safe to reuse
    // per-ply because the search is single-threaded DFS: only one frame at a
    // given ply is ever active at a time, and a frame at ply P never touches
    // buffers for any ply other than P (deeper recursion uses ply+1, ply+2,
    // etc). Sized with headroom beyond MaxPly since Quiescence's own
    // recursion can occasionally push ply slightly past the normal search's
    // MaxPly in long forced-capture sequences; BufIdx clamps into range for
    // that rare case rather than throwing.
    private const int MoveBufDepth = MaxPly + 64;
    private readonly List<Move>[] _pseudoBuf;
    private readonly List<Move>[] _moveBuf;
    private readonly int[][] _scoreBuf;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int BufIdx(int ply) => ply < MoveBufDepth ? ply : MoveBufDepth - 1;

    // Set via RequestStop() from another thread to cancel an in-progress
    // SearchContinuous run. Checked from TimeUp() (same place the normal
    // time-limit check lives) so it takes effect at the same granularity as
    // the existing time-based cutoff, without needing a check threaded
    // through every recursive call.
    private volatile bool _externalStop;

    // --- Exploitative ("Asymmetric Alpha-Beta") search ---
    //
    // Own moves are searched to a full depth; the opponent's replies are
    // searched to a much shallower depth, modeling them as a tactically
    // blind/shallow player. This deliberately walks into deferred tactics
    // and traps that a short search horizon on their side wouldn't see
    // coming, while our own side still gets a full-strength search.
    //
    // Uses its OWN TranspositionTable (not the shared _tt passed into the
    // constructor). Reason: a TT entry's (depth, score, bound) triple is
    // only valid under the assumption that whoever stored it searched that
    // subtree with a single consistent depth policy. The outer self-search
    // below is keyed purely by selfPliesLeft, but that's only a valid TT key
    // under a FIXED oppMaxDepth for the whole call - reusing the shared _tt
    // would let a result computed under one oppMaxDepth setting get read
    // back and misapplied under a different one (or by the normal
    // FindBestMove/FindTopMoves search entirely). Keeping a separate table
    // sidesteps that at the cost of a smaller/colder table for this mode.
    // (Only the OUTER self-search uses this table - the inner shallow
    // opponent-move search below intentionally uses no TT at all; see its
    // comment for why.)
    private readonly TranspositionTable _exploitTt = new TranspositionTable(64);

    // Dedicated TT for FindBestMoveVsWeakOpponent's outer self-search
    // (SelfSearchVsWeak). Kept separate from _exploitTt and the shared _tt
    // for the same reason as always: entries here reflect a value under a
    // SPECIFIC opponent-modeling assumption (deterministic depth-3,
    // material-only opponent), not directly comparable to entries computed
    // under FindBestMove's assumptions or FindBestMoveExploitative's
    // assumptions, even though the underlying position might be identical.
    private readonly TranspositionTable _weakSelfTt = new TranspositionTable(64);

    /// <summary>
    /// Hook for future "Trap bias" / "Aggression bias" evaluation tuning
    /// (see class-level TODO). Currently just forwards to the normal static
    /// eval. Centralizing exploitative-mode eval behind this one method
    /// means those enhancements only need to change in one place later,
    /// without touching the search tree logic at all.
    /// </summary>
    private static int EvaluateExploitative(Board b) => Evaluation.Evaluate(b);

    /// <summary>
    /// Hook for future "Opponent-blindness pruning" (skip quiet defensive
    /// moves when generating the opponent's shallow replies, on the theory
    /// that a shallow/weak player is unlikely to find them anyway). Returns
    /// false (prune nothing) for now - wire this up before enabling it, and
    /// be careful: over-pruning here can make the model overconfident and
    /// walk into real tactics the opponent actually would find.
    /// </summary>
    private static bool ShouldPruneAsOpponentBlind(Board b, Move m, int oppPliesLeft) => false;

    /// <summary>
    /// Hook for future "King-hunt extensions" (deepen search on checks,
    /// sacrifices, or moves that open lines toward the enemy king, on either
    /// side). Returns 0 (no extension) for now. Only wired into the
    /// opponent's shallow move-selection search below; extending the outer
    /// self-search would need a separate call site.
    /// </summary>
    private static int ExploitativeExtension(Board b, Move m, bool isSelfNode, bool givesCheck) => 0;

    public Move BestMoveRoot;
    public int BestScoreRoot;

    /// <summary>Nodes searched during the most recent FindBestMove/FindTopMoves/SearchContinuous call.</summary>
    public long Nodes => _nodes;

    /// <summary>Elapsed time (ms) since the most recent search call started.</summary>
    public long ElapsedMs => _timer.ElapsedMilliseconds;

    /// <summary>Deepest depth fully completed by the most recent FindTopMoves/SearchContinuous call.</summary>
    public int LastCompletedDepth { get; private set; }

    public Search(TranspositionTable tt)
    {
        _tt = tt;

        _pseudoBuf = new List<Move>[MoveBufDepth];
        _moveBuf = new List<Move>[MoveBufDepth];
        _scoreBuf = new int[MoveBufDepth][];
        for (int i = 0; i < MoveBufDepth; i++)
        {
            _pseudoBuf[i] = new List<Move>(64);
            _moveBuf[i] = new List<Move>(64);
            _scoreBuf[i] = new int[256]; // safe upper bound on legal moves in any reachable chess position
        }
    }

    /// <summary>
    /// Searches every move in `orderedMoves` at `depth`, sharing alpha across
    /// them like a normal single-PV root search (the same thing FindBestMove
    /// does) instead of giving each move its own independent full-window
    /// search. Moves are assumed to be ordered best-first (e.g. by the
    /// previous depth's scores).
    ///
    /// The first move gets a full-window search (-Infinity, Infinity), same
    /// as FindBestMove's root move. Every subsequent move first gets a cheap
    /// null-window probe against the current best score (alpha); since move
    /// ordering usually already has the best move first, most of these
    /// probes fail low almost immediately and get pruned - exactly as cheap
    /// as they'd be in a normal single-PV search. Only a move that actually
    /// beats alpha gets re-searched with a full window to find out how much
    /// better it really is.
    ///
    /// Tradeoff: scores for moves that don't beat alpha are only guaranteed
    /// upper bounds (their true score could be anywhere <= the returned
    /// value), not exact - fine for "roughly rank the top few candidates"
    /// UI purposes, not for anything needing precise centipawn accuracy.
    ///
    /// Updates `scores` in place (only overwriting entries for moves that
    /// were actually searched before a stop was requested). Returns false if
    /// the search was stopped (time limit / external stop) before finishing.
    /// </summary>
    private bool SearchRootDepthSharedAlpha(Board b, List<Move> orderedMoves, int depth, Dictionary<Move, int> scores, Action afterEachMove = null)
    {
        int alpha = -Infinity;
        const int beta = Infinity;
        bool first = true;

        foreach (var m in orderedMoves)
        {
            if (TimeUp()) { _stop = true; return false; }

            b.MakeMove(m);
            int score;
            if (first)
            {
                score = -NegaMax(b, depth - 1, 1, -beta, -alpha, false);
                first = false;
            }
            else
            {
                // Cheap probe: "is this move better than our current best?"
                score = -NegaMax(b, depth - 1, 1, -alpha - 1, -alpha, false);
                if (!_stop && score > alpha)
                {
                    // It actually beat the current best - worth knowing the real score.
                    score = -NegaMax(b, depth - 1, 1, -beta, -alpha, false);
                }
            }
            b.UnmakeMove(m);

            if (_stop) return false;

            scores[m] = score;
            if (score > alpha) alpha = score;

            afterEachMove?.Invoke();
        }

        return true;
    }

    /// <summary>
    /// Requests that an in-progress SearchContinuous (or any other search on
    /// this instance) stop as soon as possible. Safe to call from another
    /// thread. The search may take a moment to actually unwind - this just
    /// sets a flag that's checked periodically, same as the internal time
    /// limit check.
    /// </summary>
    public void RequestStop() => _externalStop = true;

    /// <summary>
    /// Runs iterative deepening indefinitely (no depth or time cap - only
    /// stops when RequestStop() is called), reporting the current top N
    /// candidate root moves via `onUpdate` roughly every `reportIntervalMs`
    /// milliseconds. Intended for a "Think Extended" / "infinite analysis"
    /// UI feature: start this on a background thread, let onUpdate feed a
    /// live-updating display (e.g. arrows + eval bar), and call
    /// RequestStop() when the user is done.
    ///
    /// `onUpdate` receives the depth reached so far, total nodes searched,
    /// elapsed time in ms, and the current top-N snapshot - everything
    /// needed for a UCI-style "info depth D nodes N time T" log line without
    /// the caller needing to separately poll search state.
    ///
    /// `onUpdate` is called from whatever thread this method runs on (NOT
    /// automatically marshaled to the main thread) - the caller is
    /// responsible for thread-safely handing the result off to Unity's main
    /// thread, same as FindBestMove/FindTopMoves's callers already do.
    /// </summary>
    public void SearchContinuous(Board b, int topN, Action<int, long, long, List<(Move move, int score)>> onUpdate, int reportIntervalMs = 300)
    {
        _timer = Stopwatch.StartNew();
        _timeLimitMs = long.MaxValue; // no time cap - only _externalStop ends this
        _stop = false;
        _externalStop = false;
        _nodes = 0;
        LastCompletedDepth = 0;

        var legalMoves = MoveGen.GenerateLegalMoves(b);
        if (legalMoves.Count == 0)
        {
            onUpdate(0, _nodes, _timer.ElapsedMilliseconds, new List<(Move, int)>());
            return;
        }

        var scores = new Dictionary<Move, int>();
        foreach (var m in legalMoves) scores[m] = -Infinity;

        // Reported snapshots always come from here, never from `scores`
        // directly. `scores` gets partially overwritten move-by-move as a
        // depth is searched (fresh for moves already re-searched this depth,
        // still holding the PREVIOUS depth's values for moves not yet
        // reached) - reporting that mid-depth mix is what caused the arrows
        // to look unstable/scrambled right as a new depth started. Instead,
        // `stableScores` only gets updated in one atomic step once an entire
        // depth finishes, so every report - whether periodic or end-of-depth
        // - always reflects one complete, internally-consistent depth pass.
        var stableScores = new Dictionary<Move, int>(scores);
        int stableDepth = 0;

        var reportTimer = Stopwatch.StartNew();

        for (int depth = 1; depth <= MaxPly; depth++)
        {
            var ordered = legalMoves.OrderByDescending(m => scores[m]).ToList();

            bool completed = SearchRootDepthSharedAlpha(b, ordered, depth, scores, () =>
            {
                if (reportTimer.ElapsedMilliseconds >= reportIntervalMs)
                {
                    onUpdate(stableDepth, _nodes, _timer.ElapsedMilliseconds, SnapshotTopMoves(stableScores, topN));
                    reportTimer.Restart();
                }
            });

            if (!completed) break;

            LastCompletedDepth = depth;
            stableDepth = depth;
            foreach (var kv in scores) stableScores[kv.Key] = kv.Value; // promote this depth's results atomically

            // Completed a full depth - always report, regardless of the
            // interval timer, so each depth increase is visible even if it
            // happened to finish just under reportIntervalMs.
            onUpdate(stableDepth, _nodes, _timer.ElapsedMilliseconds, SnapshotTopMoves(stableScores, topN));
            reportTimer.Restart();
        }
    }

    private static List<(Move move, int score)> SnapshotTopMoves(Dictionary<Move, int> scores, int topN)
    {
        return scores
            .OrderByDescending(kv => kv.Value)
            .Take(topN)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    /// <summary>
    /// Like FindBestMove, but returns the top N candidate root moves with
    /// their scores instead of just the single best move - useful for UI
    /// features like drawing "here are the top options" arrows. Shares alpha
    /// across root moves (see SearchRootDepthSharedAlpha) so this runs at
    /// close to FindBestMove's speed rather than ~N times slower - the
    /// tradeoff is that scores for anything other than the actual best move
    /// are approximate upper bounds, not exact. Moves are re-ordered by
    /// their previous iteration's score each depth, so the most promising
    /// moves get the freshest/deepest scores if time runs out mid-iteration.
    /// </summary>
    public List<(Move move, int score)> FindTopMoves(Board b, int maxDepth, long timeLimitMs, int topN)
    {
        _timer = Stopwatch.StartNew();
        _timeLimitMs = timeLimitMs;
        _stop = false;
        _nodes = 0;
        LastCompletedDepth = 0;

        var legalMoves = MoveGen.GenerateLegalMoves(b);
        if (legalMoves.Count == 0) return new List<(Move, int)>();

        var scores = new Dictionary<Move, int>();
        foreach (var m in legalMoves) scores[m] = -Infinity;

        for (int depth = 1; depth <= maxDepth; depth++)
        {
            // Search the currently most-promising moves first, so if we run
            // out of time mid-depth, the best-looking moves still got a
            // score at this depth rather than being cut off early.
            var ordered = legalMoves.OrderByDescending(m => scores[m]).ToList();

            bool completed = SearchRootDepthSharedAlpha(b, ordered, depth, scores);
            if (!completed) break;

            LastCompletedDepth = depth;

            if (_timer.ElapsedMilliseconds > _timeLimitMs) break;
        }

        return scores
            .OrderByDescending(kv => kv.Value)
            .Take(topN)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }

    /// <summary>
    /// Skill-adaptive move selection. Runs the normal, fast, well-pruned
    /// FindTopMoves exactly as-is (no changes to the main search at all -
    /// still gets your usual depth/nodes), then, depending on `skill`,
    /// optionally re-ranks the top few candidates by how "trappy" they are:
    /// how much worse an opponent's position gets if they play anything
    /// other than their single best defensive reply. This never overrides
    /// an objectively better move with a worse one outside the configured
    /// margin - it only breaks ties among moves the real search already
    /// considers close to equally good, so genuine forced wins/mates are
    /// never passed up for the sake of trickiness.
    ///
    ///   Strong:       returns the top move from FindTopMoves, unchanged.
    ///   Intermediate: considers candidates within ~75cp of best, measures
    ///                 trickiness with a moderate depth-7 look at the
    ///                 opponent's replies (catches multi-move "only one
    ///                 correct defense" patterns, not just one-movers).
    ///   Weak:         considers candidates within ~150cp of best (per your
    ///                 calibration), measures trickiness with a cheap
    ///                 depth-2 look (catches immediate blunders/hung pieces/
    ///                 short mate threats - this is what naturally produces
    ///                 "early aggressive/sharp" behavior without having to
    ///                 special-case specific moves like early queen sorties;
    ///                 it falls out of the measurement whenever such a move
    ///                 happens to set up a cheap one/two-move threat).
    ///
    /// Uses a small SEPARATE time budget for the trickiness pass (not the
    /// caller's timeLimitMs again) so total time is bounded and predictable
    /// even though this runs after FindTopMoves already used its own budget.
    /// If FindTopMoves itself ran out of time, the trickiness pass is
    /// skipped entirely and the top move is returned as-is, to avoid
    /// stacking extra latency on top of an already-slow search.
    /// </summary>
    public Move FindBestMoveAdaptive(Board b, int maxDepth, long timeLimitMs, OpponentSkill skill, int multiPvCount = 6)
    {
        var top = FindTopMoves(b, maxDepth, timeLimitMs, multiPvCount);
        if (top.Count == 0) { BestScoreRoot = 0; return Move.None; }

        if (skill == OpponentSkill.Strong || _stop)
        {
            BestScoreRoot = top[0].score;
            return top[0].move;
        }

        int bestScore = top[0].score;
        int margin = skill == OpponentSkill.Weak ? 150 : 75;
        int trickDepth = skill == OpponentSkill.Weak ? 2 : 7;
        int replyCap = skill == OpponentSkill.Weak ? 24 : 10;
        double trickWeight = skill == OpponentSkill.Weak ? 1.0 : 0.4;
        int blunderBonusPerChance = skill == OpponentSkill.Weak ? 15 : 10;
        int blunderThreshold = 100; // cp gap from opponent's best defense to count as "a chance to mess up"

        var shortlist = top.Where(t => bestScore - t.score <= margin).ToList();

        // Separate, bounded time budget for this pass - deliberately not
        // reusing timeLimitMs again, so total time stays predictable even
        // though FindTopMoves already spent its own budget above.
        _timer = Stopwatch.StartNew();
        _timeLimitMs = Math.Min(500, Math.Max(150, timeLimitMs / 4));
        _stop = false;

        Move bestMove = top[0].move;
        int bestMoveObjectiveScore = top[0].score; // BestScoreRoot reports the REAL cp score, not the blended trickiness value
        double bestFinal = double.NegativeInfinity;

        foreach (var (move, score) in shortlist)
        {
            var (perfectScore, avgGap, blunderCount) = EvaluateTrickiness(b, move, trickDepth, replyCap, blunderThreshold);
            if (_stop) { bestMove = top[0].move; bestMoveObjectiveScore = top[0].score; break; } // ran out of budget mid-pass - fall back to the safe top move rather than an incomplete comparison

            double final = perfectScore + trickWeight * avgGap + blunderBonusPerChance * Math.Min(blunderCount, 5);
            if (final > bestFinal)
            {
                bestFinal = final;
                bestMove = move;
                bestMoveObjectiveScore = score;
            }
        }

        BestScoreRoot = bestMoveObjectiveScore;
        return bestMove;
    }

    /// <summary>
    /// For the position after playing `m`, looks at the opponent's actual
    /// legal replies (ordered and capped to `replyCap` to bound cost) and,
    /// for each, evaluates the resulting position `depth` plies further
    /// using the normal NegaMax (well-pruned, same engine strength as
    /// everywhere else - no separate weak sub-engine here, this is
    /// genuinely asking "how good is this position for us").
    ///
    /// Returns:
    ///   perfectScore - our score assuming the opponent finds their best
    ///                  defense (the minimum across their replies, since
    ///                  they're trying to minimize our score).
    ///   avgGap       - how much better we do on average if they play
    ///                  something other than their best defense. Larger =
    ///                  more of their options are costly mistakes.
    ///   blunderCount - how many of their replies are worse than their best
    ///                  defense by more than `blunderThreshold` - a direct
    ///                  count of "chances to mess up," not just an average.
    /// </summary>
    private (int perfectScore, double avgGap, int blunderCount) EvaluateTrickiness(Board b, Move m, int depth, int replyCap, int blunderThreshold)
    {
        b.MakeMove(m);

        var oppMoves = MoveGen.GenerateLegalMoves(b);
        if (oppMoves.Count == 0)
        {
            bool oppInCheck = b.InCheck(b.SideToMove);
            b.UnmakeMove(m);
            // Immediate checkmate/stalemate right after our move - nothing
            // to measure trickiness over, just report the outcome directly.
            return (oppInCheck ? MateScore : 0, 0.0, 0);
        }

        OrderMoves(b, oppMoves, Move.None, 0);
        var capped = oppMoves.Count > replyCap ? oppMoves.Take(replyCap).ToList() : oppMoves;

        var vals = new List<int>(capped.Count);
        foreach (var r in capped)
        {
            b.MakeMove(r);
            // Side to move after both our move and their reply is us again,
            // so this is already in OUR perspective - no negation needed.
            int val = NegaMax(b, Math.Max(depth - 1, 0), 0, -Infinity, Infinity, false);
            b.UnmakeMove(r);

            vals.Add(val);
            if (_stop) break;
        }

        b.UnmakeMove(m);

        if (vals.Count == 0)
            return (0, 0.0, 0);

        int perfectScore = vals.Min(); // opponent plays to minimize our score
        double avg = vals.Average();
        double avgGap = avg - perfectScore;
        int blunderCount = vals.Count(v => v - perfectScore > blunderThreshold);

        return (perfectScore, avgGap, blunderCount);
    }

    /// <summary>
    /// "Think Extended" done the simple, robust way: runs the EXACT same
    /// single-PV search as FindBestMove/Go (full alpha-beta pruning across
    /// the whole tree, one best move, no multi-PV bookkeeping), just with no
    /// depth or time cap - only RequestStop() ends it. Reports the best move
    /// and score via `onUpdate` once every time a depth is FULLY completed
    /// (never mid-depth), which is what fixes the "garbage moves when a new
    /// depth starts" problem the old multi-PV-based SearchContinuous had:
    /// there's no per-move dictionary that can be a partial mix of this
    /// depth's fresh values and the previous depth's stale ones, because
    /// there's only ever one (move, score) result at a time, and it's only
    /// ever replaced once a full depth's search legitimately finishes.
    ///
    /// Since this shares alpha-beta pruning across the whole tree exactly
    /// like Go does, it runs at Go's speed per depth - the tradeoff (as
    /// requested) is that only the single best move is available, not a
    /// ranked list of runner-up candidates.
    ///
    /// `onUpdate` is called from whatever thread this method runs on (NOT
    /// automatically marshaled to the main thread) - same threading contract
    /// as SearchContinuous/FindTopMoves.
    /// </summary>
    public void SearchContinuousSingle(Board b, Action<int, long, long, Move, int> onUpdate)
    {
        _timer = Stopwatch.StartNew();
        _timeLimitMs = long.MaxValue; // no time cap - only _externalStop (via TimeUp()) ends this
        _stop = false;
        _externalStop = false;
        _nodes = 0;
        LastCompletedDepth = 0;

        Move bestMove = Move.None;
        int bestScore = 0;

        for (int depth = 1; depth <= MaxPly; depth++)
        {
            int score = NegaMax(b, depth, 0, -Infinity, Infinity, true);
            if (_stop && depth > 1) break;

            if (BestMoveRoot != Move.None)
            {
                bestMove = BestMoveRoot;
                bestScore = score;
            }

            LastCompletedDepth = depth;
            BestScoreRoot = bestScore;

            onUpdate(depth, _nodes, _timer.ElapsedMilliseconds, bestMove, bestScore);

            if (_stop) break;
        }
    }

    public Move FindBestMove(Board b, int maxDepth, long timeLimitMs)
    {
        _timer = Stopwatch.StartNew();
        _timeLimitMs = timeLimitMs;
        _stop = false;
        _nodes = 0;
        LastCompletedDepth = 0;

        Move bestMove = Move.None;
        int bestScore = 0;

        for (int depth = 1; depth <= maxDepth; depth++)
        {
            int score = NegaMax(b, depth, 0, -Infinity, Infinity, true);
            if (_stop && depth > 1) break;

            if (BestMoveRoot != Move.None)
            {
                bestMove = BestMoveRoot;
                bestScore = score;
            }

            LastCompletedDepth = depth;

            Console.WriteLine($"info depth {depth} score cp {score} nodes {_nodes} time {_timer.ElapsedMilliseconds} pv {bestMove}");

            if (_timer.ElapsedMilliseconds > _timeLimitMs) break;
        }

        BestScoreRoot = bestScore;
        return bestMove;
    }

    /// <summary>
    /// Identical to FindBestMove in every way EXCEPT for one line: it sets
    /// Evaluation.SelfColor before searching, which activates the
    /// opponent-exposure bias in Evaluation.Evaluate() (see Evaluation.cs -
    /// favors positions where the opponent's king is uncastled/has a
    /// weakened pawn shield, throughout the whole tree regardless of whose
    /// turn it is at any given leaf).
    ///
    /// This is a deliberate full duplicate of FindBestMove rather than a
    /// modification of it or a shared code path, so that FindBestMove itself
    /// stays completely untouched and behaves exactly as it always has - use
    /// this method instead whenever you want the eval bias active, and keep
    /// using FindBestMove for plain, unbiased play. Both call the exact same
    /// underlying NegaMax/Quiescence (also untouched), so search behavior
    /// (pruning, node counts, depth reached) is identical between the two -
    /// only which positions get ranked as "good" differs.
    /// </summary>
    public Move FindBestMoveTrappy(Board b, int maxDepth, long timeLimitMs)
    {
        Evaluation.SelfColor = b.SideToMove; // identifies which king is "opponent" for every bias term in Evaluate()
        Evaluation.BiasEnabled = true;       // gate is reset in finally below, guaranteed even on timeout/exception -
                                              // see BiasEnabled's doc comment in Evaluation.cs for why this matters
        try
        {
            _timer = Stopwatch.StartNew();
            _timeLimitMs = timeLimitMs;
            _stop = false;
            _nodes = 0;
            LastCompletedDepth = 0;

            Move bestMove = Move.None;
            int bestScore = 0;

            for (int depth = 1; depth <= maxDepth; depth++)
            {
                int score = NegaMax(b, depth, 0, -Infinity, Infinity, true);
                if (_stop && depth > 1) break;

                if (BestMoveRoot != Move.None)
                {
                    bestMove = BestMoveRoot;
                    bestScore = score;
                }

                LastCompletedDepth = depth;

                Console.WriteLine($"info depth {depth} (trappy) score cp {score} nodes {_nodes} time {_timer.ElapsedMilliseconds} pv {bestMove}");

                if (_timer.ElapsedMilliseconds > _timeLimitMs) break;
            }

            BestScoreRoot = bestScore;
            return bestMove;
        }
        finally
        {
            Evaluation.BiasEnabled = false;
        }
    }

    /// <summary>
    /// Assumes a fully deterministic opponent who plays exactly like this:
    /// a plain depth-3 negamax, evaluated with Evaluation.EvaluateWeakOpponent
    /// (material + piece-square tables + mobility, but no king safety, no
    /// bishop pair, no rook-file bonuses, no tempo - simpler than the main
    /// engine's eval, but enough for reasonably natural-looking play),
    /// extended up to 3
    /// further plies at the horizon but ONLY considering captures and
    /// checks (a bounded tactical lookahead, not open-ended quiescence).
    /// Since that opponent model is deterministic, our own side never needs
    /// to consider alternatives for their move at all: for any position
    /// after one of our candidate moves, we run this fixed opponent policy
    /// exactly once, take whatever move it returns, apply it, and continue
    /// OUR OWN full-strength, uncapped iterative-deepening search from the
    /// result - same two-tier structure as FindBestMoveExploitative, just
    /// with a much simpler, cheaper, and fully deterministic opponent model
    /// instead of a search-for-their-best-shallow-move step.
    ///
    /// Our own side uses the normal Evaluation.Evaluate (full positional
    /// understanding, no bias) via the existing Quiescence - this method
    /// does not touch Evaluation.SelfColor/BiasEnabled at all, it's
    /// unrelated to the Trappy bias system.
    ///
    /// `oppSearchDepth`: the opponent's fixed plain-negamax depth (3 per
    /// your spec). `oppExtensionPlies`: how many further plies of
    /// captures/checks-only lookahead the opponent gets once oppSearchDepth
    /// is exhausted (3 per your spec - "+3 captures and checks").
    /// `selfMoveLimit`: same safety valve as FindBestMoveExploitative - only
    /// the top N ordered self-candidates get the full opponent-reply
    /// treatment. Defaults very high here since the opponent search itself
    /// is now cheap (material-only eval, fixed small depth), so it's much
    /// less likely to be needed than in the exploitative mode, but it's
    /// still available if a particular position turns out to be slow.
    /// </summary>
    public Move FindBestMoveVsWeakOpponent(Board b, int selfMaxDepth, long timeLimitMs, int oppSearchDepth = 3, int oppExtensionPlies = 3, int selfMoveLimit = 9999)
    {
        _timer = Stopwatch.StartNew();
        _timeLimitMs = timeLimitMs;
        _stop = false;
        _nodes = 0;
        LastCompletedDepth = 0;

        Move bestMove = Move.None;
        int bestScore = 0;

        for (int depth = 1; depth <= selfMaxDepth; depth++)
        {
            int score = SelfSearchVsWeak(b, depth, oppSearchDepth, oppExtensionPlies, 0, -Infinity, Infinity, isRoot: true, selfMoveLimit);
            if (_stop && depth > 1) break;

            if (BestMoveRoot != Move.None)
            {
                bestMove = BestMoveRoot;
                bestScore = score;
            }

            LastCompletedDepth = depth;

            Console.WriteLine($"info depth {depth} (vs weak opp: depth {oppSearchDepth}+{oppExtensionPlies} tactical, material-only) score cp {score} nodes {_nodes} time {_timer.ElapsedMilliseconds} pv {bestMove}");

            if (_timer.ElapsedMilliseconds > _timeLimitMs) break;
        }

        BestScoreRoot = bestScore;
        return bestMove;
    }

    /// <summary>
    /// Mirrors FindBestMoveExploitative's SelfSearch exactly (same
    /// non-negating convention - see that method's original doc comment for
    /// the full reasoning), but resolves the opponent's move via the fixed,
    /// deterministic WeakOpponentBestMove instead of a search-for-their-
    /// best-shallow-move step, and uses its own dedicated _weakSelfTt.
    /// </summary>
    private int SelfSearchVsWeak(Board b, int selfPliesLeft, int oppSearchDepth, int oppExtensionPlies, int ply, int alpha, int beta, bool isRoot, int selfMoveLimit)
    {
        _nodes++;

        if (!isRoot && TimeUp()) { _stop = true; return 0; }
        if (_stop) return 0;
        if (!isRoot && (b.HalfmoveClock >= 100 || b.IsRepetition())) return 0;

        ulong key = b.Hash;
        Move ttMove = Move.None;
        if (_weakSelfTt.TryGet(key, out var entry))
        {
            ttMove = entry.BestMove;
            if (!isRoot && entry.Depth >= selfPliesLeft)
            {
                if (entry.Flag == TTFlag.Exact) return entry.Score;
                if (entry.Flag == TTFlag.LowerBound && entry.Score > alpha) alpha = entry.Score;
                else if (entry.Flag == TTFlag.UpperBound && entry.Score < beta) beta = entry.Score;
                if (alpha >= beta) return entry.Score;
            }
        }

        bool inCheck = b.InCheck(b.SideToMove);

        if (selfPliesLeft <= 0)
            return Quiescence(b, alpha, beta, ply);

        var pseudo = _pseudoBuf[BufIdx(ply)];
        var moves = _moveBuf[BufIdx(ply)];
        MoveGen.GenerateLegalMoves(b, pseudo, moves);

        if (moves.Count == 0)
            return inCheck ? -MateScore + ply : 0; // we are checkmated/stalemated

        OrderMoves(b, moves, ttMove, ply);

        int origAlpha = alpha;
        Move bestMove = moves[0];
        int bestScore = -Infinity;

        for (int i = 0; i < moves.Count; i++)
        {
            var m = moves[i];
            b.MakeMove(m);

            var oppPseudo = _pseudoBuf[BufIdx(ply + 1)];
            var oppMoves = _moveBuf[BufIdx(ply + 1)];
            MoveGen.GenerateLegalMoves(b, oppPseudo, oppMoves);

            int score;
            if (oppMoves.Count == 0)
            {
                bool oppInCheck = b.InCheck(b.SideToMove);
                score = oppInCheck ? (MateScore - (ply + 1)) : 0;
            }
            else if (i < selfMoveLimit)
            {
                Move oppReply = WeakOpponentBestMove(b, oppMoves, oppSearchDepth, oppExtensionPlies, ply + 1, out _);
                b.MakeMove(oppReply);
                score = SelfSearchVsWeak(b, selfPliesLeft - 1, oppSearchDepth, oppExtensionPlies, ply + 2, alpha, beta, false, selfMoveLimit);
                b.UnmakeMove(oppReply);
            }
            else
            {
                score = -Evaluation.Evaluate(b);
            }

            b.UnmakeMove(m);

            if (_stop) return 0;

            if (score > bestScore)
            {
                bestScore = score;
                bestMove = m;
                if (isRoot) { BestMoveRoot = m; }
            }

            if (score > alpha)
            {
                alpha = score;
            }

            if (alpha >= beta)
            {
                if (!m.Flag().IsCapture())
                {
                    _killers[ply, 1] = _killers[ply, 0];
                    _killers[ply, 0] = m;
                    _history[(int)b.SideToMove, m.From(), m.To()] += selfPliesLeft * selfPliesLeft;
                }
                break;
            }
        }

        TTFlag flag = bestScore <= origAlpha ? TTFlag.UpperBound :
                      bestScore >= beta ? TTFlag.LowerBound : TTFlag.Exact;
        _weakSelfTt.Store(key, selfPliesLeft, bestScore, flag, bestMove);

        return bestScore;
    }

    /// <summary>
    /// Picks the deterministic weak opponent's move: plain negamax to
    /// oppSearchDepth, using Evaluation.EvaluateWeakOpponent at the horizon
    /// (extended via WeakOpponentNegaMax's capture/check logic). No TT here
    /// deliberately - depth is small and fixed, and the eval is cheap
    /// (material popcounts only, no PST/mobility/attack generation), so the
    /// raw cost stays small without needing the added complexity of another
    /// dedicated table. `moves` is the caller's already-generated legal move
    /// list for this position.
    /// </summary>
    /// <summary>
    /// Captures-first ordering for the weak-opponent search, deliberately
    /// NOT using the shared OrderMoves/_killers/_history - see
    /// WeakOpponentNegaMax's doc comment for why. This is a pure function of
    /// `moves` alone: same input list always produces the same output,
    /// completely independent of anything happening elsewhere in whatever
    /// outer search this is running inside of. Only uses m.Flag().IsCapture()
    /// - no other move-ordering heuristics - simple on purpose, this is
    /// about guaranteeing determinism, not about search speed.
    /// </summary>
    private static void OrderMovesDeterministic(List<Move> moves)
    {
        moves.Sort((a, c) => (c.Flag().IsCapture() ? 1 : 0) - (a.Flag().IsCapture() ? 1 : 0));
    }

    private Move WeakOpponentBestMove(Board b, List<Move> moves, int oppSearchDepth, int oppExtensionPlies, int ply, out int bestScore)
    {
        OrderMovesDeterministic(moves);

        Move best = moves[0];
        int bestVal = -Infinity;
        int alpha = -Infinity, beta = Infinity;

        foreach (var m in moves)
        {
            b.MakeMove(m);
            int val = -WeakOpponentNegaMax(b, oppSearchDepth - 1, oppExtensionPlies, ply + 1, -beta, -alpha);
            b.UnmakeMove(m);

            if (val > bestVal) { bestVal = val; best = m; }
            if (val > alpha) alpha = val;
            if (alpha >= beta) break;
        }

        bestScore = bestVal;
        return best;
    }

    /// <summary>
    /// Direct entry point to actually PLAY a move using the deterministic
    /// weak-opponent policy (depth-`oppSearchDepth` plain negamax + up to
    /// `oppExtensionPlies` further captures/checks-only lookahead, all
    /// scored with Evaluation.EvaluateWeakOpponent) - the exact same policy
    /// FindBestMoveVsWeakOpponent simulates internally, but callable
    /// directly for whichever side is actually to move on `b`. Useful for
    /// letting a person play against this policy directly, or for having it
    /// play both sides to sanity-check what it does on its own.
    ///
    /// `timeLimitMs` is a safety net, not a normal budget - this search is
    /// small and fixed-depth by design, so it should finish well under any
    /// reasonable limit; the parameter exists only to guard against a
    /// pathological worst-case position rather than to shape normal
    /// behavior the way it does in the other Find* methods.
    /// </summary>
    public Move FindWeakOpponentMove(Board b, int oppSearchDepth = 3, int oppExtensionPlies = 3, long timeLimitMs = 5000)
    {
        _timer = Stopwatch.StartNew();
        _timeLimitMs = timeLimitMs;
        _stop = false;
        _nodes = 0;

        var pseudo = _pseudoBuf[BufIdx(0)];
        var moves = _moveBuf[BufIdx(0)];
        MoveGen.GenerateLegalMoves(b, pseudo, moves);

        if (moves.Count == 0)
        {
            BestScoreRoot = 0;
            return Move.None; // checkmate/stalemate - nothing to play
        }

        Move best = WeakOpponentBestMove(b, moves, oppSearchDepth, oppExtensionPlies, 0, out int bestScore);

        LastCompletedDepth = oppSearchDepth;
        BestScoreRoot = bestScore;
        Console.WriteLine($"info (weak opponent policy) depth {oppSearchDepth}+{oppExtensionPlies} score cp {bestScore} nodes {_nodes} time {_timer.ElapsedMilliseconds} bestmove {best}");

        return best;
    }

    /// <summary>
    /// The weak opponent's own thinking: plain negamax down to depth 0,
    /// then (if extensionsLeft > 0) extends further but restricted to ONLY
    /// captures and checking moves - a hard-capped tactical lookahead, not
    /// open-ended quiescence. Mate/stalemate is always checked against the
    /// FULL legal move list first, even during the extension phase, so a
    /// real forced mate is never misread as "nothing tactical left, return
    /// material eval".
    ///
    /// Deliberately has NO time-based early exit (no TimeUp()/_stop checks)
    /// unlike every other recursive search in this file. This is on
    /// purpose: this recursion is already structurally guaranteed to
    /// terminate (every path strictly decreases `depth` or `extensionsLeft`
    /// until it hits a leaf), so it doesn't need a time safety net the way
    /// an iterative-deepening search does. Adding one anyway would be
    /// actively harmful here specifically: _timer/_timeLimitMs/_stop are
    /// shared instance fields with whatever OUTER search is running this as
    /// a sub-computation (e.g. FindBestMoveVsWeakOpponent's own iterative
    /// deepening), so as the outer search's budget runs low, this "opponent
    /// simulation" could get silently cut short and return a placeholder
    /// score of 0 - meaning the opponent move the bot PLANS against could
    /// subtly differ from what a fresh, separate call (e.g. the "Play Weak
    /// Opponent Move" button, which always gets its own full budget) would
    /// actually produce for the same position. Since the entire point of
    /// this search mode is "assume the opponent follows this exact,
    /// deterministic policy," any such mismatch directly undermines that
    /// assumption. Removing the time dependence here guarantees this method
    /// always computes the same result for the same position, regardless of
    /// what else is going on in the surrounding search.
    ///
    /// For the same reason, this method (and WeakOpponentBestMove) also use
    /// OrderMovesDeterministic instead of the shared OrderMoves/_killers/
    /// _history - those tables get filled up by whatever else the outer
    /// search has done at that ply slot across potentially thousands of
    /// unrelated positions during a long iterative-deepening run, which
    /// could make a tied-score tie-break here pick a different move
    /// depending on that unrelated history, rather than depending only on
    /// the actual position. OrderMovesDeterministic has no such dependency.
    /// </summary>
    private int WeakOpponentNegaMax(Board b, int depth, int extensionsLeft, int ply, int alpha, int beta)
    {
        _nodes++;

        if (b.HalfmoveClock >= 100 || b.IsRepetition()) return 0;

        bool inCheck = b.InCheck(b.SideToMove);

        var pseudo = _pseudoBuf[BufIdx(ply)];
        var allMoves = _moveBuf[BufIdx(ply)];
        MoveGen.GenerateLegalMoves(b, pseudo, allMoves);

        if (allMoves.Count == 0)
            return inCheck ? -MateScore + ply : 0; // real mate/stalemate - checked regardless of depth/extension phase

        if (depth > 0)
        {
            OrderMovesDeterministic(allMoves);
            int best = -Infinity;
            foreach (var m in allMoves)
            {
                b.MakeMove(m);
                int val = -WeakOpponentNegaMax(b, depth - 1, extensionsLeft, ply + 1, -beta, -alpha);
                b.UnmakeMove(m);

                if (val > best) best = val;
                if (val > alpha) alpha = val;
                if (alpha >= beta) break;
            }
            return best;
        }

        // Depth exhausted - extend via captures/checks only, up to a hard
        // cap of `extensionsLeft` further plies.
        if (extensionsLeft <= 0)
            return Evaluation.EvaluateWeakOpponent(b);

        var tactical = new List<Move>();
        foreach (var m in allMoves)
        {
            if (m.Flag().IsCapture())
            {
                tactical.Add(m);
                continue;
            }
            b.MakeMove(m);
            bool givesCheck = b.InCheck(b.SideToMove);
            b.UnmakeMove(m);
            if (givesCheck) tactical.Add(m);
        }

        if (tactical.Count == 0)
            return Evaluation.EvaluateWeakOpponent(b); // quiet - nothing tactical left, stop extending

        OrderMovesDeterministic(tactical);
        int bestTactical = -Infinity;
        foreach (var m in tactical)
        {
            b.MakeMove(m);
            int val = -WeakOpponentNegaMax(b, 0, extensionsLeft - 1, ply + 1, -beta, -alpha);
            b.UnmakeMove(m);

            if (val > bestTactical) bestTactical = val;
            if (val > alpha) alpha = val;
            if (alpha >= beta) break;
        }
        return bestTactical;
    }


    /// <summary>
    /// Asymmetric alpha-beta ("exploitative") search: our own moves get a
    /// genuine, uncapped iterative-deepening negamax up to `selfMaxDepth`
    /// (same as FindBestMove) - but whenever it's the OPPONENT's turn, we do
    /// NOT continue that same recursion. Instead we run a small, independent,
    /// bounded search (see ShallowOpponentBestMove/ShallowNegaMax below)
    /// purely to decide what a depth-`oppMaxDepth` player would play there,
    /// apply that single move, and hand control straight back to our own
    /// full-strength search from the resulting position.
    ///
    /// This two-tier structure is deliberate and fixes an earlier (buggy)
    /// version of this method that tried to model the asymmetry as a single
    /// pair of counters alternating down one shared negamax recursion. That
    /// doesn't work: self and opponent plies strictly alternate 1:1 in a real
    /// game, so there's no way for one side's counter to run out "faster"
    /// than the other's within that same alternation without either (a)
    /// silently capping the WHOLE tree's depth at whichever counter is
    /// smaller, forever, no matter how far you iteratively deepen the other
    /// side, or (b) - if you try to patch that by resetting the smaller
    /// counter - making it never expire at all. Splitting the opponent's
    /// decision out into its own separate, bounded sub-search sidesteps the
    /// problem entirely: it has its own depth budget that's simply small and
    /// fixed, not entangled with our own depth counter's arithmetic. It also
    /// means the opponent's contribution to the total node count per self-ply
    /// is a small, roughly constant cost (~branchingFactor^oppMaxDepth)
    /// instead of scaling with how deep we search - which is what actually
    /// makes this cheaper than a symmetric search to a comparable real depth,
    /// not just "shallower-looking" while secretly costing the same or more.
    ///
    /// Root is assumed to be a self node (b.SideToMove is us). If you need
    /// this callable when it's actually the opponent's turn (e.g. analyzing
    /// "what should the shallow opponent do here"), that's a different query
    /// and isn't what this method is for.
    /// </summary>
    public Move FindBestMoveExploitative(Board b, int selfMaxDepth, int oppMaxDepth, long timeLimitMs, int selfMoveLimit = 10)
    {
        _timer = Stopwatch.StartNew();
        _timeLimitMs = timeLimitMs;
        _stop = false;
        _nodes = 0;
        LastCompletedDepth = 0;

        Move bestMove = Move.None;
        int bestScore = 0;

        for (int depth = 1; depth <= selfMaxDepth; depth++)
        {
            int score = SelfSearch(b, depth, oppMaxDepth, 0, -Infinity, Infinity, isRoot: true, selfMoveLimit);
            if (_stop && depth > 1) break;

            if (BestMoveRoot != Move.None)
            {
                bestMove = BestMoveRoot;
                bestScore = score;
            }

            LastCompletedDepth = depth;

            Console.WriteLine($"info depth {depth} (opp window {oppMaxDepth}, self move limit {selfMoveLimit}) score cp {score} nodes {_nodes} time {_timer.ElapsedMilliseconds} pv {bestMove}");

            if (_timer.ElapsedMilliseconds > _timeLimitMs) break;
        }

        BestScoreRoot = bestScore;
        return bestMove;
    }

    /// <summary>
    /// Represents OUR turn only - the opponent's turn never gets its own
    /// call in this recursion. `selfPliesLeft` counts down purely our own
    /// remaining move budget; `oppMaxDepth` is passed through unchanged
    /// (it's a fixed setting for the whole call, not something that gets
    /// consumed). Because there's no side-flip between successive
    /// SelfSearch calls (we've already folded the opponent's single move in
    /// between), scores and alpha/beta bounds are passed straight through
    /// WITHOUT negation - this is intentionally not a standard negamax
    /// alternation, it's closer to a plain maximizing search over our own
    /// moves with a fixed (non-searched-by-us) step interleaved.
    ///
    /// `selfMoveLimit`: only the first `selfMoveLimit` moves in ORDERED
    /// order (i.e. the ones move-ordering already ranks as most promising -
    /// TT move, captures, killers, history) get the full, expensive
    /// treatment of a real opponent-reply search followed by a recursive
    /// SelfSearch continuation. This is the main lever for controlling total
    /// cost: that full treatment is what actually dominates the node count
    /// (every self-move pays a whole opponent sub-search, with no early
    /// cutoff available at the root of that sub-search). Moves ranked past
    /// the limit get a much cheaper approximate score instead (see below) -
    /// they were already judged less promising by ordering, so losing some
    /// precision on them costs little in practice, similar in spirit to
    /// standard forward-pruning/late-move-reduction techniques.
    /// </summary>
    private int SelfSearch(Board b, int selfPliesLeft, int oppMaxDepth, int ply, int alpha, int beta, bool isRoot, int selfMoveLimit)
    {
        _nodes++;

        if (!isRoot && TimeUp()) { _stop = true; return 0; }
        if (_stop) return 0;

        // Draw detection: 50-move rule and threefold repetition.
        if (!isRoot && (b.HalfmoveClock >= 100 || b.IsRepetition())) return 0;

        ulong key = b.Hash;
        Move ttMove = Move.None;
        if (_exploitTt.TryGet(key, out var entry))
        {
            ttMove = entry.BestMove;
            if (!isRoot && entry.Depth >= selfPliesLeft)
            {
                if (entry.Flag == TTFlag.Exact) return entry.Score;
                if (entry.Flag == TTFlag.LowerBound && entry.Score > alpha) alpha = entry.Score;
                else if (entry.Flag == TTFlag.UpperBound && entry.Score < beta) beta = entry.Score;
                if (alpha >= beta) return entry.Score;
            }
        }

        bool inCheck = b.InCheck(b.SideToMove);

        if (selfPliesLeft <= 0)
            return Quiescence(b, alpha, beta, ply);

        var pseudo = _pseudoBuf[BufIdx(ply)];
        var moves = _moveBuf[BufIdx(ply)];
        MoveGen.GenerateLegalMoves(b, pseudo, moves);

        if (moves.Count == 0)
            return inCheck ? -MateScore + ply : 0; // we are checkmated/stalemated

        OrderMoves(b, moves, ttMove, ply);

        int origAlpha = alpha;
        Move bestMove = moves[0];
        int bestScore = -Infinity;

        for (int i = 0; i < moves.Count; i++)
        {
            var m = moves[i];
            b.MakeMove(m); // our move

            // Check what the opponent's options are. We need this ourselves
            // (rather than only inside the shallow sub-search) so we can
            // correctly score checkmate/stalemate delivered BY our move,
            // rather than misreading a TT/search miss as one of those cases.
            var oppPseudo = _pseudoBuf[BufIdx(ply + 1)];
            var oppMoves = _moveBuf[BufIdx(ply + 1)];
            MoveGen.GenerateLegalMoves(b, oppPseudo, oppMoves);

            int score;
            if (oppMoves.Count == 0)
            {
                bool oppInCheck = b.InCheck(b.SideToMove);
                // Opponent has no legal reply. If they're in check, WE just
                // delivered checkmate - about as good an outcome as exists,
                // prefer the fastest mate. Otherwise it's stalemate (a draw).
                score = oppInCheck ? (MateScore - (ply + 1)) : 0;
            }
            else if (i < selfMoveLimit)
            {
                Move oppReply = ShallowOpponentBestMove(b, oppMoves, oppMaxDepth, ply + 1);
                b.MakeMove(oppReply);
                // No negation: SelfSearch always returns a value in OUR
                // perspective (see class doc above), and the opponent's turn
                // has already been resolved as a fixed, already-decided step
                // rather than something we're maximizing/minimizing over
                // here - so alpha/beta pass straight through unchanged too.
                score = SelfSearch(b, selfPliesLeft - 1, oppMaxDepth, ply + 2, alpha, beta, false, selfMoveLimit);
                b.UnmakeMove(oppReply);
            }
            else
            {
                // Past the move-ordering-ranked cutoff: skip the full,
                // expensive opponent-reply search entirely (that per-move
                // sub-search is what dominates total cost - see class doc)
                // and fall back to a cheap static evaluation of the position
                // right after our own move, with no opponent modeling at
                // all. These moves were already ranked as our least
                // promising candidates by move ordering, so approximating
                // them cheaply costs little in practice - they were unlikely
                // to be selected as best regardless.
                //
                // Sign: Evaluation.Evaluate is from the side-to-move's
                // perspective, and it's the OPPONENT to move right here
                // (right after our MakeMove(m)) - so negate to convert back
                // into OUR perspective, matching every other `score` in this
                // loop.
                score = -EvaluateExploitative(b);
            }

            b.UnmakeMove(m);

            if (_stop) return 0;

            if (score > bestScore)
            {
                bestScore = score;
                bestMove = m;
                if (isRoot) { BestMoveRoot = m; }
            }

            if (score > alpha)
            {
                alpha = score;
            }

            if (alpha >= beta)
            {
                if (!m.Flag().IsCapture())
                {
                    _killers[ply, 1] = _killers[ply, 0];
                    _killers[ply, 0] = m;
                    _history[(int)b.SideToMove, m.From(), m.To()] += selfPliesLeft * selfPliesLeft;
                }
                break;
            }
        }

        TTFlag flag = bestScore <= origAlpha ? TTFlag.UpperBound :
                      bestScore >= beta ? TTFlag.LowerBound : TTFlag.Exact;
        _exploitTt.Store(key, selfPliesLeft, bestScore, flag, bestMove);

        return bestScore;
    }

    /// <summary>
    /// Picks the opponent's move using a small, independent, fixed-depth
    /// search from THEIR perspective (standard negamax sign convention
    /// within this sub-search only - it does not interact with SelfSearch's
    /// non-negating convention at all, it's a self-contained calculation
    /// whose only output is "which move do they play").
    ///
    /// Uses the SHARED main `_tt` (not a separate table). Unlike the outer
    /// SelfSearch's entries (which only mean anything under an exploitative
    /// interpretation), a result computed here is just an ordinary, valid,
    /// ordinary-strength fixed-depth negamax result - identical in kind to
    /// what FindBestMove would compute at that position and depth. It's
    /// completely safe to read/write the shared table, and doing so matters
    /// a lot in practice: every one of our own candidate root moves triggers
    /// its own call into this shallow opponent search, and many of those
    /// branches transpose into overlapping or identical positions (this
    /// happens constantly in chess). Without sharing, each one repeats that
    /// work from scratch; with it, later calls reuse what earlier ones
    /// already computed - this was the actual cause of node counts being
    /// far higher than expected even at self-depth 1.
    ///
    /// `moves` is the caller's already-generated legal move list for this
    /// position (avoids regenerating it here).
    /// </summary>
    private Move ShallowOpponentBestMove(Board b, List<Move> moves, int oppMaxDepth, int ply)
    {
        Move ttMove = Move.None;
        if (_tt.TryGet(b.Hash, out var rootEntry))
            ttMove = rootEntry.BestMove;

        OrderMoves(b, moves, ttMove, ply);

        // oppMaxDepth <= 0: a genuine zero-cost "instinct" move - whatever
        // the ordering heuristic (captures/killers/history/TT move) already
        // ranks first, with NO search and NO quiescence at all. This used
        // to be silently floored to depth 1 via Math.Max(oppMaxDepth, 1),
        // which meant setting oppMaxDepth to 0 (or 1) still paid the cost of
        // evaluating every single opponent move plus a quiescence search on
        // each one - that per-move quiescence cost, not the depth number
        // itself, was the actual floor on how cheap this could get. This
        // path exists specifically so there IS a setting with no floor.
        if (oppMaxDepth <= 0)
            return moves[0];

        int depth = oppMaxDepth;

        Move best = moves[0];
        int bestVal = -Infinity;
        int alpha = -Infinity, beta = Infinity;
        int origAlpha = alpha;

        for (int i = 0; i < moves.Count; i++)
        {
            var m = moves[i];

            // Opponent-blindness pruning hook (off by default - see
            // ShouldPruneAsOpponentBlind). Applies only to the opponent's
            // own candidate replies here, never to our own moves.
            if (ShouldPruneAsOpponentBlind(b, m, depth))
                continue;

            b.MakeMove(m);
            bool givesCheck = b.InCheck(b.SideToMove);
            int childDepth = Math.Max(depth - 1, 0) + ExploitativeExtension(b, m, isSelfNode: false, givesCheck: givesCheck);
            int val = -ShallowNegaMax(b, childDepth, ply + 1, -beta, -alpha);
            b.UnmakeMove(m);

            if (_stop) break;

            if (val > bestVal) { bestVal = val; best = m; }
            if (val > alpha) alpha = val;
            if (alpha >= beta) break;
        }

        TTFlag flag = bestVal <= origAlpha ? TTFlag.UpperBound :
                      bestVal >= beta ? TTFlag.LowerBound : TTFlag.Exact;
        _tt.Store(b.Hash, depth, bestVal, flag, best);

        return best;
    }

    /// <summary>
    /// Fixed-depth negamax used only to evaluate the opponent's candidate
    /// replies in ShallowOpponentBestMove. No LMR (depth is already small
    /// here, and simplicity/predictability matter more than shaving a bit
    /// more off an already-cheap search) - but DOES use the shared `_tt`,
    /// same reasoning as ShallowOpponentBestMove above: these are ordinary
    /// valid results, and reusing them avoids repeating identical work when
    /// different root-move branches transpose into the same position.
    /// </summary>
    private int ShallowNegaMax(Board b, int depth, int ply, int alpha, int beta)
    {
        _nodes++;

        if (TimeUp()) { _stop = true; return 0; }
        if (_stop) return 0;
        if (b.HalfmoveClock >= 100 || b.IsRepetition()) return 0;

        ulong key = b.Hash;
        Move ttMove = Move.None;
        if (_tt.TryGet(key, out var entry))
        {
            ttMove = entry.BestMove;
            if (entry.Depth >= depth)
            {
                if (entry.Flag == TTFlag.Exact) return entry.Score;
                if (entry.Flag == TTFlag.LowerBound && entry.Score > alpha) alpha = entry.Score;
                else if (entry.Flag == TTFlag.UpperBound && entry.Score < beta) beta = entry.Score;
                if (alpha >= beta) return entry.Score;
            }
        }

        bool inCheck = b.InCheck(b.SideToMove);

        if (depth <= 0)
            return Quiescence(b, alpha, beta, ply);

        var pseudo = _pseudoBuf[BufIdx(ply)];
        var moves = _moveBuf[BufIdx(ply)];
        MoveGen.GenerateLegalMoves(b, pseudo, moves);

        if (moves.Count == 0)
            return inCheck ? -MateScore + ply : 0;

        OrderMoves(b, moves, ttMove, ply);

        int origAlpha = alpha;
        Move bestMove = moves[0];
        int best = -Infinity;
        for (int i = 0; i < moves.Count; i++)
        {
            var m = moves[i];
            b.MakeMove(m);
            int val = -ShallowNegaMax(b, depth - 1, ply + 1, -beta, -alpha);
            b.UnmakeMove(m);

            if (_stop) return 0;

            if (val > best) { best = val; bestMove = m; }
            if (val > alpha) alpha = val;
            if (alpha >= beta) break;
        }

        TTFlag flag2 = best <= origAlpha ? TTFlag.UpperBound :
                       best >= beta ? TTFlag.LowerBound : TTFlag.Exact;
        _tt.Store(key, depth, best, flag2, bestMove);

        return best;
    }


    private bool TimeUp() => _externalStop || ((_nodes & 2047) == 0 && _timer.ElapsedMilliseconds > _timeLimitMs);

    private int NegaMax(Board b, int depth, int ply, int alpha, int beta, bool isRoot)
    {
        _nodes++;

        if (!isRoot && TimeUp()) { _stop = true; return 0; }
        if (_stop) return 0;

        // Draw detection: 50-move rule and threefold repetition.
        if (!isRoot && (b.HalfmoveClock >= 100 || b.IsRepetition())) return 0;

        ulong key = b.Hash;
        Move ttMove = Move.None;
        if (_tt.TryGet(key, out var entry))
        {
            ttMove = entry.BestMove;
            if (!isRoot && entry.Depth >= depth)
            {
                if (entry.Flag == TTFlag.Exact) return entry.Score;
                if (entry.Flag == TTFlag.LowerBound && entry.Score > alpha) alpha = entry.Score;
                else if (entry.Flag == TTFlag.UpperBound && entry.Score < beta) beta = entry.Score;
                if (alpha >= beta) return entry.Score;
            }
        }

        bool inCheck = b.InCheck(b.SideToMove);

        if (depth <= 0)
            return Quiescence(b, alpha, beta, ply);

        var pseudo = _pseudoBuf[BufIdx(ply)];
        var moves = _moveBuf[BufIdx(ply)];
        MoveGen.GenerateLegalMoves(b, pseudo, moves);

        if (moves.Count == 0)
            return inCheck ? -MateScore + ply : 0; // checkmate or stalemate

        OrderMoves(b, moves, ttMove, ply);

        int origAlpha = alpha;
        Move bestMove = moves[0];
        int bestScore = -Infinity;

        for (int i = 0; i < moves.Count; i++)
        {
            var m = moves[i];
            b.MakeMove(m);
            int score;

            if (i == 0)
            {
                score = -NegaMax(b, depth - 1, ply + 1, -beta, -alpha, false);
            }
            else
            {
                // Late move reduction for quiet, later moves
                int reduction = (depth >= 3 && i >= 4 && !m.Flag().IsCapture() && !inCheck) ? 1 : 0;
                score = -NegaMax(b, depth - 1 - reduction, ply + 1, -alpha - 1, -alpha, false);
                if (score > alpha && (reduction > 0 || score < beta))
                    score = -NegaMax(b, depth - 1, ply + 1, -beta, -alpha, false);
            }

            b.UnmakeMove(m);

            if (_stop) return 0;

            if (score > bestScore)
            {
                bestScore = score;
                bestMove = m;
                if (isRoot) { BestMoveRoot = m; }
            }

            if (score > alpha)
            {
                alpha = score;
            }

            if (alpha >= beta)
            {
                if (!m.Flag().IsCapture())
                {
                    _killers[ply, 1] = _killers[ply, 0];
                    _killers[ply, 0] = m;
                    _history[(int)b.SideToMove, m.From(), m.To()] += depth * depth;
                }
                break;
            }
        }

        TTFlag flag = bestScore <= origAlpha ? TTFlag.UpperBound :
                      bestScore >= beta ? TTFlag.LowerBound : TTFlag.Exact;
        _tt.Store(key, depth, bestScore, flag, bestMove);

        return bestScore;
    }

    private int Quiescence(Board b, int alpha, int beta, int ply)
    {
        _nodes++;
        if (TimeUp()) { _stop = true; return 0; }

        int standPat = Evaluation.Evaluate(b);
        if (standPat >= beta) return beta;
        if (standPat > alpha) alpha = standPat;

        var pseudo = _pseudoBuf[BufIdx(ply)];
        var captures = _moveBuf[BufIdx(ply)];
        MoveGen.GenerateLegalCaptures(b, pseudo, captures);
        OrderCaptures(b, captures, ply);

        foreach (var m in captures)
        {
            b.MakeMove(m);
            int score = -Quiescence(b, -beta, -alpha, ply + 1);
            b.UnmakeMove(m);

            if (_stop) return 0;

            if (score >= beta) return beta;
            if (score > alpha) alpha = score;
        }

        return alpha;
    }

    private static int PieceValue(Piece p) => p == Piece.None ? 0 : Evaluation.MgValue[p.Kind()];

    private void OrderMoves(Board b, List<Move> moves, Move ttMove, int ply)
    {
        var scores = _scoreBuf[BufIdx(ply)];
        for (int i = 0; i < moves.Count; i++)
        {
            var m = moves[i];
            if (m == ttMove) scores[i] = 1_000_000;
            else if (m.Flag().IsCapture())
                scores[i] = 100_000 + PieceValue(m.Captured()) * 10 - PieceValue(m.Moved());
            else if (m == _killers[ply, 0]) scores[i] = 90_000;
            else if (m == _killers[ply, 1]) scores[i] = 80_000;
            else scores[i] = _history[(int)b.SideToMove, m.From(), m.To()];
        }
        SortByScoreDesc(moves, scores);
    }

    private void OrderCaptures(Board b, List<Move> moves, int ply)
    {
        var scores = _scoreBuf[BufIdx(ply)];
        for (int i = 0; i < moves.Count; i++)
        {
            var m = moves[i];
            scores[i] = PieceValue(m.Captured()) * 10 - PieceValue(m.Moved());
        }
        SortByScoreDesc(moves, scores);
    }

    private static void SortByScoreDesc(List<Move> moves, int[] scores)
    {
        // simple insertion sort - move lists are short, and this avoids allocations
        for (int i = 1; i < moves.Count; i++)
        {
            int key = scores[i];
            Move keyMove = moves[i];
            int j = i - 1;
            while (j >= 0 && scores[j] < key)
            {
                scores[j + 1] = scores[j];
                moves[j + 1] = moves[j];
                j--;
            }
            scores[j + 1] = key;
            moves[j + 1] = keyMove;
        }
    }
}

}