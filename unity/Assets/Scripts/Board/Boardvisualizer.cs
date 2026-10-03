using UnityEngine;
using ChessBot;

/// <summary>
/// Bridges the logical ChessBot.Board (bitboards, Piece enum, square indices 0..63,
/// a1=0) to a 3D scene: converts squares <-> world positions, and keeps one 3D
/// GameObject per occupied square in sync with the board state.
///
/// Usage:
///   1. Drop this on an empty GameObject placed at the board's a1 corner
///      (or anywhere - set `origin` to the transform whose position IS a1's
///      corner, e.g. the corner of the a1 square, not its center).
///   2. Assign the 12 prefabs in `piecePrefabs` in enum order:
///      WPawn, WKnight, WBishop, WRook, WQueen, WKing,
///      BPawn, BKnight, BBishop, BRook, BQueen, BKing
///   3. Call Sync(board) any time the board changes (e.g. right after
///      board.MakeMove(...) or board.SetFen(...)) to update visuals.
/// </summary>
public class BoardVisualizer : MonoBehaviour
{
    [Header("Layout")]
    [Tooltip("Transform at the a1 corner of the board (not the center of the a1 square). " +
             "Board extends +X across files a->h and +Z across ranks 1->8.")]
    public Transform origin;

    [Tooltip("Size of one square's side, in world units.")]
    public float squareSize = 1f;

    [Tooltip("Height above the square at which pieces are placed (useful if your prefabs' pivots aren't at their base).")]
    public float pieceYOffset = 0f;

    [Header("Piece Prefabs (order must match ChessBot.Piece enum)")]
    [Tooltip("Index 0-5 = White Pawn,Knight,Bishop,Rook,Queen,King. Index 6-11 = same for Black.")]
    public GameObject[] piecePrefabs = new GameObject[12];

    [Header("Optional")]
    [Tooltip("Parent transform for spawned piece instances. Defaults to this GameObject's transform.")]
    public Transform pieceContainer;

    [Tooltip("Height above a straight line the piece arcs upward mid-move, in world units. 0 = flat slide.")]
    public float moveArcHeight = 1f;

    [Header("Piece Move Animation (optional)")]
    [Tooltip("If your piece prefabs have an Animator with a Trigger parameter for a 'being moved' animation, name it here. Left blank = no animator trigger is fired (Animation-component fallback below still applies).")]
    public string moveAnimatorTrigger = "";

    // One entry per square (0..63); null if empty.
    private readonly GameObject[] _squareObjects = new GameObject[64];
    // Tracks which Piece kind is currently instantiated on each square, so we
    // only destroy/recreate when a square's piece type actually changes.
    private readonly Piece[] _squareKinds = new Piece[64];
    // Tracks the per-square Y offset applied during instantiation.
    private readonly float[] _squareYOffset = new float[64];

    void Awake()
    {
        if (pieceContainer == null) pieceContainer = transform;
        for (int i = 0; i < 64; i++)
        {
            _squareKinds[i] = Piece.None;
            _squareYOffset[i] = 0f;
        }
    }

    // ---- Coordinate conversion -------------------------------------------------

    /// <summary>World-space center of the given square (0..63, a1=0).</summary>
    public Vector3 SquareToWorld(int sq)
    {
        int file = Sq.File(sq); // 0=a .. 7=h
        int rank = Sq.Rank(sq); // 0=rank1 .. 7=rank8

        Vector3 local = new Vector3(
            (file + 0.5f) * squareSize,
            pieceYOffset,
            (rank + 0.5f) * squareSize);

        Transform o = origin != null ? origin : transform;
        return o.TransformPoint(local);
    }

    /// <summary>Inverse of SquareToWorld: nearest square index for a world position, or -1 if off-board.</summary>
    public int WorldToSquare(Vector3 worldPos)
    {
        Transform o = origin != null ? origin : transform;
        Vector3 local = o.InverseTransformPoint(worldPos);

        int file = Mathf.FloorToInt(local.x / squareSize);
        int rank = Mathf.FloorToInt(local.z / squareSize);

        if (file < 0 || file > 7 || rank < 0 || rank > 7) return -1;
        return Sq.Make(file, rank);
    }

    private Vector3 SquareToWorldWithYOffset(int sq, float extraYOffset)
    {
        Vector3 position = SquareToWorld(sq);
        position.y += extraYOffset;
        return position;
    }

    private float GetArcHeight(Vector3 start, Vector3 target, Piece pieceKind = Piece.None)
    {
        if (pieceKind == Piece.WKnight || pieceKind == Piece.BKnight)
            return moveArcHeight;

        float horizontalDistance = Vector3.Distance(
            new Vector3(start.x, 0f, start.z),
            new Vector3(target.x, 0f, target.z));
        float maxDistance = squareSize * 7f;
        float scale = Mathf.Clamp01(horizontalDistance / maxDistance);
        return moveArcHeight * scale;
    }

    private Vector3 GetCaptureThrowTarget(GameObject obj)
    {
        if (obj == null) return Vector3.zero;

        Transform o = origin != null ? origin : transform;
        Vector3 current = obj.transform.position;
        Vector3 localCurrent = o.InverseTransformPoint(current);
        Vector3 boardCenterLocal = new Vector3(4f * squareSize, 0f, 4f * squareSize);

        Vector3 direction = localCurrent - boardCenterLocal;
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.01f)
        {
            float angle = Random.Range(0f, Mathf.PI * 2f);
            direction = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
        }
        else
        {
            direction.Normalize();
        }

        float boardMin = 0f;
        float boardMax = 8f * squareSize;
        float t = float.MaxValue;
        if (direction.x > 0f) t = Mathf.Min(t, (boardMax - localCurrent.x) / direction.x);
        if (direction.x < 0f) t = Mathf.Min(t, (boardMin - localCurrent.x) / direction.x);
        if (direction.z > 0f) t = Mathf.Min(t, (boardMax - localCurrent.z) / direction.z);
        if (direction.z < 0f) t = Mathf.Min(t, (boardMin - localCurrent.z) / direction.z);

        float minThrow = squareSize * 3f;
        float margin = squareSize * 1.5f;
        float throwDistance = Mathf.Max(minThrow, t + margin);

        Vector3 targetLocal = localCurrent + direction * throwDistance;
        Vector3 target = o.TransformPoint(targetLocal);
        target.y = current.y + Mathf.Max(0.5f, squareSize * 0.2f);
        return target;
    }

    private System.Collections.IEnumerator ThrowCaptureAndDestroy(GameObject obj, float duration, Vector3 target)
    {
        if (obj == null) yield break;

        float throwArc = Mathf.Max(moveArcHeight * 0.5f, 0.5f);
        yield return TweenArc(obj, obj.transform.position, target, duration, throwArc);
        if (obj != null) Destroy(obj);
    }

    private bool TryGetCastlingRookSquares(int kingFromSq, int kingToSq, out int rookFromSq, out int rookToSq)
    {
        rookFromSq = -1;
        rookToSq = -1;

        int delta = kingToSq - kingFromSq;
        if (Mathf.Abs(delta) != 2)
            return false;

        int rank = kingFromSq / 8;
        if (delta == 2)
        {
            rookFromSq = rank * 8 + 7;
            rookToSq = rank * 8 + 5;
            return true;
        }

        if (delta == -2)
        {
            rookFromSq = rank * 8 + 0;
            rookToSq = rank * 8 + 3;
            return true;
        }

        return false;
    }

    // ---- Board sync -------------------------------------------------------------

    /// <summary>
    /// Rebuilds visuals to match the current board state. Cheap to call after
    /// every move: only squares whose occupant changed get destroyed/instantiated;
    /// unchanged pieces are just repositioned (handles animation hook-in easily -
    /// see MovePieceVisual below if you want to tween instead of snap).
    /// </summary>
    public void Sync(Board board)
    {
        for (int sq = 0; sq < 64; sq++)
        {
            Piece piece = board.SquareToPiece[sq];

            if (piece == _squareKinds[sq])
            {
                // Same occupant (including "both empty") - just make sure position is correct.
                if (_squareObjects[sq] != null)
                    _squareObjects[sq].transform.position = SquareToWorldWithYOffset(sq, _squareYOffset[sq]);
                continue;
            }

            // Occupant changed: clear old, spawn new.
            if (_squareObjects[sq] != null)
            {
                GameObject captured = _squareObjects[sq];
                _squareObjects[sq] = null;
                _squareYOffset[sq] = 0f;
                if (piece != Piece.None)
                {
                    StartCoroutine(ThrowCaptureAndDestroy(captured, 0.4f, GetCaptureThrowTarget(captured)));
                }
                else
                {
                    Destroy(captured);
                }
            }

            if (piece != Piece.None)
            {
                GameObject prefab = piecePrefabs[(int)piece];
                if (prefab == null)
                {
                    Debug.LogWarning($"BoardVisualizer: no prefab assigned for {piece} (index {(int)piece}).");
                }
                else
                {
                    float prefabYOffset = prefab.transform.position.y;
                    Vector3 pos = SquareToWorldWithYOffset(sq, prefabYOffset);
                    GameObject instance = Instantiate(prefab, pos, prefab.transform.rotation, pieceContainer);
                    instance.name = $"{piece}_{Sq.Name(sq)}";
                    _squareObjects[sq] = instance;
                    _squareYOffset[sq] = prefabYOffset;
                }
            }

            _squareKinds[sq] = piece;
        }
    }

    /// <summary>
    /// Smoothly moves the visual piece currently on `fromSq` to `toSq` over
    /// `duration` seconds, without touching board logic. Call this yourself
    /// right before board.MakeMove(...), then call Sync(board) right after
    /// MakeMove to reconcile captures/promotions/castling once the tween ends
    /// (or just call Sync immediately if you don't need animation).
    /// </summary>
    /// <summary>
    /// Convenience wrapper: animates the piece from fromSq to toSq, then once
    /// the tween finishes, calls Sync(board) to reconcile bookkeeping
    /// (captures, castling rook, en passant, promotion, etc). Call this
    /// AFTER you've already applied the move to board logic (board.MakeMove),
    /// but the visual Sync is deliberately delayed so the animation isn't
    /// interrupted. See usage note on AnimateMove for why order matters.
    /// </summary>
    public void AnimateMoveThenSync(Board board, int fromSq, int toSq, float duration = 0.25f)
    {
        StartCoroutine(AnimateThenSyncCoroutine(board, fromSq, toSq, duration));
    }

    private System.Collections.IEnumerator AnimateThenSyncCoroutine(Board board, int fromSq, int toSq, float duration)
    {
        GameObject obj = _squareObjects[fromSq];
        GameObject rookObj = null;
        int rookToSq = -1;
        Piece rookKind = Piece.None;
        float rookYOffset = 0f;

        if (obj != null)
        {
            // Register at the destination BEFORE animating (not after), so
            // Sync() below sees the square as already correct instead of
            // destroying+recreating this object - which was the cause of
            // animations "restarting on arrival": recreating via Instantiate()
            // retriggers any Animator/Animation with Play Automatically on.
            Piece movedKind = _squareKinds[fromSq];
            float movedYOffset = _squareYOffset[fromSq];
            _squareObjects[fromSq] = null;
            _squareKinds[fromSq] = Piece.None;
            _squareYOffset[fromSq] = 0f;

            if (TryGetCastlingRookSquares(fromSq, toSq, out int rookFromSq, out rookToSq))
            {
                if (_squareObjects[rookFromSq] != null &&
                    (_squareKinds[rookFromSq] == Piece.WRook || _squareKinds[rookFromSq] == Piece.BRook))
                {
                    rookObj = _squareObjects[rookFromSq];
                    rookKind = _squareKinds[rookFromSq];
                    rookYOffset = _squareYOffset[rookFromSq];
                    _squareObjects[rookFromSq] = null;
                    _squareKinds[rookFromSq] = Piece.None;
                    _squareYOffset[rookFromSq] = 0f;
                    _squareObjects[rookToSq] = rookObj;
                    _squareKinds[rookToSq] = rookKind;
                    _squareYOffset[rookToSq] = rookYOffset;
                }
            }

            // If a captured piece's object is still sitting at the destination,
            // throw it off the board rather than destroying it instantly.
            if (_squareObjects[toSq] != null && _squareObjects[toSq] != obj)
            {
                GameObject captured = _squareObjects[toSq];
                _squareObjects[toSq] = null;
                _squareKinds[toSq] = Piece.None;
                _squareYOffset[toSq] = 0f;
                StartCoroutine(ThrowCaptureAndDestroy(captured, 0.4f, GetCaptureThrowTarget(captured)));
            }

            _squareObjects[toSq] = obj;
            _squareKinds[toSq] = movedKind;
            _squareYOffset[toSq] = movedYOffset;

            Vector3 target = SquareToWorldWithYOffset(toSq, movedYOffset);
            var kingMove = TweenArc(obj, obj.transform.position, target, duration, GetArcHeight(obj.transform.position, target, movedKind));
            if (rookObj != null)
            {
                Vector3 rookTarget = SquareToWorldWithYOffset(rookToSq, rookYOffset);
                StartCoroutine(TweenArc(rookObj, rookObj.transform.position, rookTarget, duration, GetArcHeight(rookObj.transform.position, rookTarget, rookKind)));
            }

            yield return kingMove;
        }
        // Reconcile bookkeeping now that the tween is done - handles captures,
        // castling rook, en passant pawn removal, and promotions, none of
        // which the simple from->to lerp above accounts for.
        Sync(board);
    }

    public void AnimateMove(int fromSq, int toSq, float duration = 0.25f)
    {
        GameObject obj = _squareObjects[fromSq];
        if (obj == null) return;
        float movedYOffset = _squareYOffset[fromSq];
        Piece movedKind = _squareKinds[fromSq];
        Vector3 target = SquareToWorldWithYOffset(toSq, movedYOffset);
        StartCoroutine(TweenArc(obj, obj.transform.position, target, duration, GetArcHeight(obj.transform.position, target, movedKind)));

        if (movedKind == Piece.WKing || movedKind == Piece.BKing)
        {
            if (TryGetCastlingRookSquares(fromSq, toSq, out int rookFromSq, out int rookToSq))
            {
                GameObject rookObj = _squareObjects[rookFromSq];
                if (rookObj != null && (_squareKinds[rookFromSq] == Piece.WRook || _squareKinds[rookFromSq] == Piece.BRook))
                {
                    float rookYOffset = _squareYOffset[rookFromSq];
                    Piece rookKind = _squareKinds[rookFromSq];
                    _squareObjects[rookFromSq] = null;
                    _squareKinds[rookFromSq] = Piece.None;
                    _squareYOffset[rookFromSq] = 0f;
                    _squareObjects[rookToSq] = rookObj;
                    _squareKinds[rookToSq] = rookKind;
                    _squareYOffset[rookToSq] = rookYOffset;
                    Vector3 rookTarget = SquareToWorldWithYOffset(rookToSq, rookYOffset);
                    StartCoroutine(TweenArc(rookObj, rookObj.transform.position, rookTarget, duration, GetArcHeight(rookObj.transform.position, rookTarget, rookKind)));
                }
            }
        }
    }

    /// <summary>
    /// Moves obj from `start` to `target` over `duration` seconds. Horizontal
    /// (XZ) motion is a straight lerp; vertical (Y) motion is that same lerp
    /// PLUS a sine hump peaking at `arcHeight` at the midpoint, so the piece
    /// rises and falls like a hand physically picking it up and setting it
    /// back down, rather than sliding flat along the board.
    /// </summary>
    private System.Collections.IEnumerator TweenArc(GameObject obj, Vector3 start, Vector3 target, float duration, float arcHeight)
    {
        if (obj != null) TriggerMoveAnimation(obj);

        if (duration <= 0f)
        {
            if (obj != null) obj.transform.position = target;
            yield break;
        }

        float t = 0f;
        while (t < duration)
        {
            if (obj == null) yield break; // destroyed mid-animation (e.g. captured)
            t += Time.deltaTime;
            float frac = Mathf.Clamp01(t / duration);

            Vector3 pos = Vector3.Lerp(start, target, frac);
            // sin(0)=0, sin(pi/2)=1, sin(pi)=0 - zero at both ends, peak at the midpoint.
            pos.y += Mathf.Sin(frac * Mathf.PI) * arcHeight;

            obj.transform.position = pos;
            yield return null;
        }

        if (obj != null) obj.transform.position = target;
    }

    /// <summary>
    /// Explicitly starts the piece's "being moved" animation right as a tween
    /// begins, so it plays DURING the move rather than being incidentally
    /// retriggered by Instantiate()'s play-on-awake behavior after the fact.
    /// Supports either an Animator (via a named Trigger parameter, set
    /// `moveAnimatorTrigger`) or a legacy Animation component (plays its
    /// default clip). Safe no-op if the object has neither, or if
    /// `moveAnimatorTrigger` is left blank and only an Animator is present.
    /// </summary>
    private void TriggerMoveAnimation(GameObject obj)
    {
        var animator = obj.GetComponentInChildren<Animator>();
        if (animator != null)
        {
            if (!string.IsNullOrEmpty(moveAnimatorTrigger))
            {
                animator.SetTrigger(moveAnimatorTrigger);
            }
            else
            {
                // No trigger configured - just restart whatever state is
                // currently assigned (e.g. a single "play automatically"
                // clip with no parameters) from frame 0.
                var state = animator.GetCurrentAnimatorStateInfo(0);
                animator.Play(state.fullPathHash, 0, 0f);
            }
            return;
        }

        var legacyAnimation = obj.GetComponentInChildren<Animation>();
        if (legacyAnimation != null && legacyAnimation.clip != null)
        {
            legacyAnimation.Play();
        }
    }

    /// <summary>
    /// Transitions the ENTIRE board to a new arrangement (e.g. after Reset or
    /// SetFen) with every piece animating to its new square, instead of
    /// snapping instantly like Sync(). Since a full reposition doesn't come
    /// with per-piece "this moved from A to B" info, pieces are matched to
    /// their destination by nearest-same-kind: e.g. each white rook flies to
    /// whichever empty white-rook destination square is closest to it. This
    /// gives a natural-looking "everyone flies back to their starting
    /// squares" transition rather than an arbitrary/crossed-paths one.
    ///
    /// Any leftover pieces with no destination of the same kind (e.g. a piece
    /// that no longer exists in the new position) fade out in place. Any
    /// newly-needed pieces with no old counterpart to reuse (e.g. going from
    /// a position with fewer queens to one with more) simply fade in at their
    /// destination, since there's no sensible "from" square for them.
    /// </summary>
    public void AnimateToBoard(Board newBoard, float duration = 0.4f)
    {
        StartCoroutine(AnimateToBoardCoroutine(newBoard, duration));
    }

    private System.Collections.IEnumerator AnimateToBoardCoroutine(Board newBoard, float duration)
    {
        // Bucket current occupied squares and target occupied squares by piece kind.
        var oldSquaresByKind = new System.Collections.Generic.List<int>[12];
        var newSquaresByKind = new System.Collections.Generic.List<int>[12];
        for (int k = 0; k < 12; k++)
        {
            oldSquaresByKind[k] = new System.Collections.Generic.List<int>();
            newSquaresByKind[k] = new System.Collections.Generic.List<int>();
        }
        for (int sq = 0; sq < 64; sq++)
        {
            if (_squareKinds[sq] != Piece.None) oldSquaresByKind[(int)_squareKinds[sq]].Add(sq);
            Piece newPiece = newBoard.SquareToPiece[sq];
            if (newPiece != Piece.None) newSquaresByKind[(int)newPiece].Add(sq);
        }

        var movingObjects = new System.Collections.Generic.List<GameObject>();
        var moveTargets = new System.Collections.Generic.List<Vector3>();
        var moveKinds = new System.Collections.Generic.List<Piece>();
        var fadeOutObjects = new System.Collections.Generic.List<GameObject>();
        var fadeInSquares = new System.Collections.Generic.List<int>();

        for (int k = 0; k < 12; k++)
        {
            var oldSqs = oldSquaresByKind[k];
            var newSqs = newSquaresByKind[k];

            // Greedy nearest-match: for each destination, grab whichever
            // remaining old piece of this kind is geometrically closest.
            foreach (int newSq in newSqs)
            {
                if (oldSqs.Count == 0)
                {
                    fadeInSquares.Add(newSq); // no old piece left to reuse - just appear
                    continue;
                }

                Vector3 target = SquareToWorld(newSq);
                int bestIdx = 0;
                float bestDist = float.MaxValue;
                for (int i = 0; i < oldSqs.Count; i++)
                {
                    float d = (SquareToWorld(oldSqs[i]) - target).sqrMagnitude;
                    if (d < bestDist) { bestDist = d; bestIdx = i; }
                }

                int matchedOldSq = oldSqs[bestIdx];
                float oldYOffset = _squareYOffset[matchedOldSq];
                oldSqs.RemoveAt(bestIdx);

                GameObject obj = _squareObjects[matchedOldSq];
                if (obj != null)
                {
                    movingObjects.Add(obj);
                    moveTargets.Add(SquareToWorldWithYOffset(newSq, oldYOffset));
                    moveKinds.Add((Piece)k);
                    // Detach from the old square's bookkeeping slot so the
                    // leftover-cleanup loop below doesn't also destroy it...
                    _squareObjects[matchedOldSq] = null;
                    _squareKinds[matchedOldSq] = Piece.None;
                    _squareYOffset[matchedOldSq] = 0f;
                    // ...and immediately register it at its NEW square so the
                    // Sync() call at the end of this coroutine sees the square
                    // as already correct, instead of thinking it's still
                    // empty and instantiating a duplicate on top of it.
                    _squareObjects[newSq] = obj;
                    _squareKinds[newSq] = (Piece)k;
                    _squareYOffset[newSq] = oldYOffset;
                }
                else
                {
                    fadeInSquares.Add(newSq);
                }
            }

            // Any old squares of this kind left unmatched have no home in the
            // new position (captured/removed) - throw them off the board.
            foreach (int leftoverOldSq in oldSqs)
            {
                if (_squareObjects[leftoverOldSq] != null)
                {
                    fadeOutObjects.Add(_squareObjects[leftoverOldSq]);
                    _squareObjects[leftoverOldSq] = null;
                    _squareKinds[leftoverOldSq] = Piece.None;
                    _squareYOffset[leftoverOldSq] = 0f;
                }
            }
        }

        // Kick off all animations in parallel.
        var running = new System.Collections.Generic.List<Coroutine>();
        for (int i = 0; i < movingObjects.Count; i++)
            running.Add(StartCoroutine(TweenArc(movingObjects[i], movingObjects[i].transform.position, moveTargets[i], duration, GetArcHeight(movingObjects[i].transform.position, moveTargets[i], moveKinds[i]))));
        foreach (var obj in fadeOutObjects)
            running.Add(StartCoroutine(ThrowCaptureAndDestroy(obj, duration, GetCaptureThrowTarget(obj))));

        // Wait for the tweens to finish (all share the same duration).
        yield return new WaitForSeconds(duration);

        // Now that motion is done, spawn any "appear" pieces (ones with no
        // old counterpart to reuse) and rebuild bookkeeping to exactly match
        // newBoard via Sync - this also robustly cleans up anything the
        // matching above missed.
        Sync(newBoard);
    }

    private System.Collections.IEnumerator FadeOutAndDestroy(GameObject obj, float duration)
    {
        if (obj == null) yield break;

        var renderers = obj.GetComponentsInChildren<Renderer>();
        var originalColors = new System.Collections.Generic.List<UnityEngine.Color>();
        var mats = new System.Collections.Generic.List<Material>();
        foreach (var r in renderers)
        {
            foreach (var m in r.materials)
            {
                if (m.HasProperty("_Color"))
                {
                    mats.Add(m);
                    originalColors.Add(m.color);
                }
            }
        }

        float t = 0f;
        while (t < duration)
        {
            if (obj == null) yield break;
            t += Time.deltaTime;
            float frac = Mathf.Clamp01(t / duration);
            for (int i = 0; i < mats.Count; i++)
            {
                UnityEngine.Color c = originalColors[i];
                c.a = 1f - frac;
                mats[i].color = c;
            }
            // Small hop while fading, consistent with the "picked up" feel.
            yield return null;
        }

        if (obj != null) Destroy(obj);
    }

    /// <summary>Clears all spawned piece visuals (e.g. before loading a new FEN).</summary>
    public void ClearAll()
    {
        for (int sq = 0; sq < 64; sq++)
        {
            if (_squareObjects[sq] != null) Destroy(_squareObjects[sq]);
            _squareObjects[sq] = null;
            _squareKinds[sq] = Piece.None;
            _squareYOffset[sq] = 0f;
        }
    }
}