using System;

namespace ChessBot
{
public static class Evaluation
{
    // Which color the engine is playing as, for the opponent-exposure bias
    // below. Set once per search call (see Search.cs's FindBestMove etc. -
    // each sets this before starting) - NOT flipped per-node during search,
    // since the whole point is that it identifies the SAME physical
    // opponent king throughout the entire tree, regardless of whose turn it
    // is at any given leaf. Defaults to White so a stray call before any
    // search has run doesn't crash, though the bias won't mean anything
    // useful until a real search sets it.
    //
    // NOTE: this is deliberately simple static mutable state, not thread-
    // local - fine as long as only one search runs at a time (which is how
    // ChessBotRunner already guards things via _isThinking), but would need
    // revisiting if you ever run searches concurrently on multiple threads.
    public static Color SelfColor = Color.White;

    // Gates ALL exploitative bias terms below (opponent-exposure, complexity
    // retention, piece proximity, pawn storm, material imbalance). Only
    // Search.cs's FindBestMoveTrappy sets this to true, and does so with a
    // try/finally so it's guaranteed to be reset to false afterward even on
    // a timeout or exception - normal FindBestMove/FindTopMoves/etc. never
    // touch this field, so they're provably unaffected by bias regardless of
    // what was called before them or what SelfColor happens to be set to.
    // (Before this flag existed, the bias applied unconditionally keyed only
    // on SelfColor, which meant a stale SelfColor from a previous Trappy
    // call could silently leak bias into a later normal search - this flag
    // is what actually prevents that, not SelfColor by itself.)
    public static bool BiasEnabled = false;

    // Tunable weights for the opponent-exposure bias in Evaluate() below.
    // Kept as public static fields so they're easy to tune from the
    // Inspector or in code without hunting through Evaluate()'s body.
    public static int UncastledPenalty = 18;
    public static int MissingShieldPawnPenalty = 10;
    public static int MobilityBiasDivisor = 8; // higher = weaker influence from our own mobility

    // --- Additional exploitative bias terms (all gated by BiasEnabled) ---
    public static int KeepPiecesWeight = 1;          // per non-pawn/non-king piece remaining on board (both sides combined) - was 4, too strong: outweighed real development and rewarded passive/non-committal moves that never risk provoking a trade
    public static int QueensOnBoardBonusPerQueen = 8; // per queen still on the board (both sides combined) - was 20, same issue
    public static int KnightProximityWeight = 3;     // per point of Chebyshev closeness (7-dist) of our knights to the opponent king
    public static int BishopProximityWeight = 2;
    public static int QueenProximityWeight = 4;
    public static int PawnStormWeight = 6;           // per rank advanced, for our pawns on/near the opponent king's file
    public static int MaterialImbalanceWeight = 3;   // per point of |white count - black count|, summed over pawn..queen kinds - was 8, too strong for the same reason as KeepPiecesWeight above

    // --- New tunable eval terms (bishop pair, rook files, tempo) ---
    // Standard, well-established heuristics. mg/eg pairs taper the same way
    // material and PST values already do, via the existing phase blend.
    public static int BishopPairMg = 30;
    public static int BishopPairEg = 50;
    public static int RookOpenFileMg = 25;
    public static int RookOpenFileEg = 10;
    public static int RookSemiOpenFileMg = 12;
    public static int RookSemiOpenFileEg = 5;
    public static int TempoBonus = 15; // flat bonus for the side to move, applied after the perspective flip

    // Material values: mg/eg, indexed by piece kind (pawn,knight,bishop,rook,queen,king)
    public static readonly int[] MgValue = { 82, 337, 365, 477, 1025, 0 };
    public static readonly int[] EgValue = { 94, 281, 297, 512, 936, 0 };

    // Game phase weight per piece kind, used for tapering (king/pawn excluded from phase calc)
    private static readonly int[] PhaseWeight = { 0, 1, 1, 2, 4, 0 };
    private const int TotalPhase = 24; // 4 knights+4 bishops... actually 2N+2B+2R+1Q per side*2 => 2*1*4+2*2+... computed below matches standard 24

    // PeSTO-style piece-square tables, white's perspective, a1=index0..h8=index63 with rank8 first in source arrays (we'll flip)
    private static readonly int[] PawnMg = {
          0,   0,   0,   0,   0,   0,  0,   0,
         98, 134,  61,  95,  68, 126, 34, -11,
         -6,   7,  26,  31,  65,  56, 25, -20,
        -14,  13,   6,  21,  23,  12, 17, -23,
        -27,  -2,  -5,  12,  17,   6, 10, -25,
        -26,  -4,  -4, -10,   3,   3, 33, -12,
        -35,  -1, -20, -23, -15,  24, 38, -22,
          0,   0,   0,   0,   0,   0,  0,   0
    };
    private static readonly int[] PawnEg = {
          0,   0,   0,   0,   0,   0,   0,   0,
        178, 173, 158, 134, 147, 132, 165, 187,
         94, 100,  85,  67,  56,  53,  82,  84,
         32,  24,  13,   5,  -2,   4,  17,  17,
         13,   9,  -3,  -7,  -7,  -8,   3,  -1,
          4,   7,  -6,   1,   0,  -5,  -1,  -8,
         13,   8,   8,  10,  13,   0,   2,  -7,
          0,   0,   0,   0,   0,   0,   0,   0
    };
    private static readonly int[] KnightMg = {
        -167, -89, -34, -49,  61, -97, -15, -107,
         -73, -41,  72,  36,  23,  62,   7,  -17,
         -47,  60,  37,  65,  84, 129,  73,   44,
          -9,  17,  19,  53,  37,  69,  18,   22,
         -13,   4,  16,  13,  28,  19,  21,   -8,
         -23,  -9,  12,  10,  19,  17,  25,  -16,
         -29, -53, -12,  -3,  -1,  18, -14,  -19,
        -105, -21, -58, -33, -17, -28, -19,  -23
    };
    private static readonly int[] KnightEg = {
        -58, -38, -13, -28, -31, -27, -63, -99,
        -25,  -8, -25,  -2,  -9, -25, -24, -52,
        -24, -20,  10,   9,  -1,  -9, -19, -41,
        -17,   3,  22,  22,  22,  11,   8, -18,
        -18,  -6,  16,  25,  16,  17,   4, -18,
        -23,  -3,  -1,  15,  10,  -3, -20, -22,
        -42, -20, -10,  -5,  -2, -20, -23, -44,
        -29, -51, -23, -15, -22, -18, -50, -64
    };
    private static readonly int[] BishopMg = {
        -29,   4, -82, -37, -25, -42,   7,  -8,
        -26,  16, -18, -13,  30,  59,  18, -47,
        -16,  37,  43,  40,  35,  50,  37,  -2,
         -4,   5,  19,  50,  37,  37,   7,  -2,
         -6,  13,  13,  26,  34,  12,  10,   4,
          0,  15,  15,  15,  14,  27,  18,  10,
          4,  15,  16,   0,   7,  21,  33,   1,
        -33,  -3, -14, -21, -13, -12, -39, -21
    };
    private static readonly int[] BishopEg = {
        -14, -21, -11,  -8, -7,  -9, -17, -24,
         -8,  -4,   7, -12, -3, -13,  -4, -14,
          2,  -8,   0,  -1, -2,   6,   0,   4,
         -3,   9,  12,   9, 14,  10,   3,   2,
         -6,   3,  13,  19,  7,  10,  -3,  -9,
        -12,  -3,   8,  10, 13,   3,  -7, -15,
        -14, -18,  -7,  -1,  4,  -9, -15, -27,
        -23,  -9, -23,  -5, -9, -16,  -5, -17
    };
    private static readonly int[] RookMg = {
         32,  42,  32,  51, 63,  9,  31,  43,
         27,  32,  58,  62, 80, 67,  26,  44,
         -5,  19,  26,  36, 17, 45,  61,  16,
        -24, -11,   7,  26, 24, 35,  -8, -20,
        -36, -26, -12,  -1,  9, -7,   6, -23,
        -45, -25, -16, -17,  3,  0,  -5, -33,
        -44, -16, -20,  -9, -1, 11,  -6, -71,
        -19, -13,   1,  17, 16,  7, -37, -26
    };
    private static readonly int[] RookEg = {
        13, 10, 18, 15, 12,  12,   8,   5,
        11, 13, 13, 11, -3,   3,   8,   3,
         7,  7,  7,  5,  4,  -3,  -5,  -3,
         4,  3, 13,  1,  2,   1,  -1,   2,
         3,  5,  8,  4, -5,  -6,  -8, -11,
        -4,  0, -5, -1, -7, -12,  -8, -16,
        -6, -6,  0,  2, -9,  -9, -11,  -3,
        -9,  2,  3, -1, -5, -13,   4, -20
    };
    private static readonly int[] QueenMg = {
        -28,   0,  29,  12,  59,  44,  43,  45,
        -24, -39,  -5,   1, -16,  57,  28,  54,
        -13, -17,   7,   8,  29,  56,  47,  57,
        -27, -27, -16, -16,  -1,  17,  -2,   1,
         -9, -26,  -9, -10,  -2,  -4,   3,  -3,
        -14,   2, -11,  -2,  -5,   2,  14,   5,
        -35,  -8,  11,   2,   8,  15,  -3,   1,
         -1, -18,  -9,  10, -15, -25, -31, -50
    };
    private static readonly int[] QueenEg = {
         -9,  22,  22,  27,  27,  19,  10,  20,
        -17,  20,  32,  41,  58,  25,  30,   0,
        -20,   6,   9,  49,  47,  35,  19,   9,
          3,  22,  24,  45,  57,  40,  57,  36,
        -18,  28,  19,  47,  31,  34,  39,  23,
        -16, -27,  15,   6,   9,  17,  10,   5,
        -22, -23, -30, -16, -16, -23, -36, -32,
        -33, -28, -22, -43,  -5, -32, -20, -41
    };
    private static readonly int[] KingMg = {
        -65,  23,  16, -15, -56, -34,   2,  13,
         29,  -1, -20,  -7,  -8,  -4, -38, -29,
         -9,  24,   2, -16, -20,   6,  22, -22,
        -17, -20, -12, -27, -30, -25, -14, -36,
        -49,  -1, -27, -39, -46, -44, -33, -51,
        -14, -14, -22, -46, -44, -30, -15, -27,
          1,   7,  -8, -64, -43, -16,   9,   8,
        -15,  36,  12, -54,   8, -28,  24,  14
    };
    private static readonly int[] KingEg = {
        -74, -35, -18, -18, -11,  15,   4, -17,
        -12,  17,  14,  17,  17,  38,  23,  11,
         10,  17,  23,  15,  20,  45,  44,  13,
         -8,  22,  24,  27,  26,  33,  26,   3,
        -18,  -4,  21,  24,  27,  23,   9, -11,
        -19,  -3,  11,  21,  23,  16,   7,  -9,
        -27, -11,   4,  13,  14,   4,  -5, -17,
        -53, -34, -21, -11, -28, -14, -24, -43
    };

    private static readonly int[][] Mg = { PawnMg, KnightMg, BishopMg, RookMg, QueenMg, KingMg };
    private static readonly int[][] Eg = { PawnEg, KnightEg, BishopEg, RookEg, QueenEg, KingEg };

    // Convert table index (defined rank8->rank1 top to bottom, a->h) to our square index (a1=0)
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static int FlipTableIndex(int sq)
    {
        int file = Sq.File(sq), rank = Sq.Rank(sq);
        int tableRankFromTop = 7 - rank;
        return tableRankFromTop * 8 + file;
    }

    /// <summary>
    /// Material + piece-square tables + mobility - deliberately simpler
    /// than Evaluate() above (no king safety, no bishop pair, no rook-file
    /// bonuses, no tempo, none of Evaluate()'s bias terms), but enough to
    /// produce reasonably natural-looking play: PST gives it basic
    /// development/centralization sense, mobility gives it basic "don't
    /// block your own pieces" awareness. Used exclusively by Search.cs's
    /// weak-opponent model (FindBestMoveVsWeakOpponent/
    /// FindWeakOpponentMove) to represent an opponent who mostly cares
    /// about material but still moves somewhat sensibly. Completely
    /// independent of SelfColor/BiasEnabled - this is a standalone
    /// evaluation, not a variant of the main one. No mg/eg taper (uses
    /// midgame tables only) - kept simple on purpose, this isn't meant to
    /// rival Evaluate()'s precision. Same negamax sign convention (positive
    /// = good for side to move) as Evaluate(), so it's a drop-in leaf eval
    /// for any negamax.
    /// </summary>
    public static int EvaluateWeakOpponent(Board b)
    {
        int materialMultiplier = 2; // midgame only, no tapering
        int score = 0;
        for (int kind = 0; kind < 6; kind++)
        {
            Piece wp = (Piece)kind;
            Piece bp = (Piece)(kind + 6);

            ulong wbb = b.PieceBB[(int)wp];
            while (wbb != 0)
            {
                int sq = Bits.PopLsb(ref wbb);
                int idx = FlipTableIndex(sq);
                score += MgValue[kind] * materialMultiplier + Mg[kind][idx];
            }

            ulong bbb = b.PieceBB[(int)bp];
            while (bbb != 0)
            {
                int sq = Bits.PopLsb(ref bbb);
                int mirroredSq = Sq.Make(Sq.File(sq), 7 - Sq.Rank(sq));
                int idx = FlipTableIndex(mirroredSq);
                score -= MgValue[kind] * materialMultiplier + Mg[kind][idx];
            }
        }

        // Same MobilityScore already used by Evaluate() - reused as-is here
        // rather than duplicated, so tuning it in one place affects both.
        score += MobilityScore(b, Color.White) - MobilityScore(b, Color.Black);

        return b.SideToMove == Color.White ? score : -score;
    }

    public static int Evaluate(Board b)
    {
        int mgScore = 0, egScore = 0, phase = 0;

        for (int kind = 0; kind < 6; kind++)
        {
            Piece wp = (Piece)kind;
            Piece bp = (Piece)(kind + 6);

            ulong wbb = b.PieceBB[(int)wp];
            while (wbb != 0)
            {
                int sq = Bits.PopLsb(ref wbb);
                int idx = FlipTableIndex(sq);
                mgScore += MgValue[kind] + Mg[kind][idx];
                egScore += EgValue[kind] + Eg[kind][idx];
                phase += PhaseWeight[kind];
            }

            ulong bbb = b.PieceBB[(int)bp];
            while (bbb != 0)
            {
                int sq = Bits.PopLsb(ref bbb);
                // mirror square vertically for black, then flip table index the same way
                int mirroredSq = Sq.Make(Sq.File(sq), 7 - Sq.Rank(sq));
                int idx = FlipTableIndex(mirroredSq);
                mgScore -= MgValue[kind] + Mg[kind][idx];
                egScore -= EgValue[kind] + Eg[kind][idx];
                phase += PhaseWeight[kind];
            }
        }

        // Mobility bonus (cheap approximation): count pseudo attack squares for knights/bishops/rooks/queens.
        // Computed once per color and reused below (both here and in the
        // exposure bias) instead of calling MobilityScore repeatedly.
        int whiteMobility = MobilityScore(b, Color.White);
        int blackMobility = MobilityScore(b, Color.Black);
        mgScore += whiteMobility - blackMobility;
        egScore += (whiteMobility - blackMobility) / 2;

        // Exploitative bias terms (opponent-exposure, complexity retention,
        // piece proximity, pawn storm, material imbalance) - ALL gated by
        // BiasEnabled, only ever true during Search.cs's FindBestMoveTrappy.
        // Everything inside this block is signed by SelfColor (fixed for
        // the whole search, NOT by whose turn it is at this leaf), so it
        // consistently favors "us" throughout the tree. See BiasEnabled's
        // doc comment above for why the gate matters (prevents stale bias
        // leaking into normal, unbiased searches). This intentionally does
        // NOT change search structure, pruning, or node count at all - it
        // only changes which positions the existing, unmodified search
        // considers good, and only when explicitly asked for.
        if (BiasEnabled)
        {
            Color oppColor = SelfColor == Color.White ? Color.Black : Color.White;
            int selfMobility = SelfColor == Color.White ? whiteMobility : blackMobility;

            int biasTotal = 0;
            biasTotal += OpponentExposureBonus(b, oppColor) + selfMobility / MobilityBiasDivisor;
            biasTotal += ComplexityRetentionBonus(b);
            biasTotal += PieceProximityBonus(b, SelfColor, oppColor);
            biasTotal += PawnStormBonus(b, SelfColor, oppColor);
            biasTotal += MaterialImbalanceBonus(b);

            mgScore += SelfColor == Color.White ? biasTotal : -biasTotal;
        }

        // Bishop pair: having both bishops is a well-established bonus
        // independent of material count (they cover complementary color
        // squares). Cheap - just a popcount on each side's bishop bitboard.
        int whiteBishops = Bits.PopCount(b.PieceBB[2]);
        int blackBishops = Bits.PopCount(b.PieceBB[8]);
        if (whiteBishops >= 2) { mgScore += BishopPairMg; egScore += BishopPairEg; }
        if (blackBishops >= 2) { mgScore -= BishopPairMg; egScore -= BishopPairEg; }

        // Rooks on open/semi-open files: another standard, cheap heuristic
        // not captured by the PST alone. Open = no pawns of either color on
        // the file; semi-open = no pawns of the rook's OWN color (enemy
        // pawns may still be present).
        ulong whitePawns = b.PieceBB[0];
        ulong blackPawns = b.PieceBB[6];
        mgScore += RookFileScoreMg(b.PieceBB[3], whitePawns, blackPawns) - RookFileScoreMg(b.PieceBB[9], blackPawns, whitePawns);
        egScore += RookFileScoreEg(b.PieceBB[3], whitePawns, blackPawns) - RookFileScoreEg(b.PieceBB[9], blackPawns, whitePawns);

        int clampedPhase = Math.Min(phase, TotalPhase);
        int mgWeight = clampedPhase;
        int egWeight = TotalPhase - clampedPhase;
        int score = (mgScore * mgWeight + egScore * egWeight) / TotalPhase;

        int result = b.SideToMove == Color.White ? score : -score;
        return result + TempoBonus; // flat bonus for whoever's turn it is - "it's your move" has real value
    }

    /// <summary>Sums RookOpenFileMg/RookSemiOpenFileMg over every rook in `rooks` belonging to the side whose pawns are `ownPawns` (the other side's pawns are `enemyPawns`). Skips rooks still on their home square - see IsRookHomeSquare for why.</summary>
    private static int RookFileScoreMg(ulong rooks, ulong ownPawns, ulong enemyPawns)
    {
        int total = 0;
        ulong r = rooks;
        while (r != 0)
        {
            int sq = Bits.PopLsb(ref r);
            if (IsRookHomeSquare(sq)) continue;
            int file = Sq.File(sq);
            bool ownOnFile = HasPawnOnFile(ownPawns, file);
            bool enemyOnFile = HasPawnOnFile(enemyPawns, file);
            if (!ownOnFile && !enemyOnFile) total += RookOpenFileMg;
            else if (!ownOnFile) total += RookSemiOpenFileMg;
        }
        return total;
    }

    /// <summary>Endgame-weight counterpart to RookFileScoreMg - see that method for the shared logic.</summary>
    private static int RookFileScoreEg(ulong rooks, ulong ownPawns, ulong enemyPawns)
    {
        int total = 0;
        ulong r = rooks;
        while (r != 0)
        {
            int sq = Bits.PopLsb(ref r);
            if (IsRookHomeSquare(sq)) continue;
            int file = Sq.File(sq);
            bool ownOnFile = HasPawnOnFile(ownPawns, file);
            bool enemyOnFile = HasPawnOnFile(enemyPawns, file);
            if (!ownOnFile && !enemyOnFile) total += RookOpenFileEg;
            else if (!ownOnFile) total += RookSemiOpenFileEg;
        }
        return total;
    }

    /// <summary>
    /// True for a1/h1/a8/h8 - a rook's starting squares. Without this check,
    /// a rook still sitting untouched on its home square would get full
    /// open/semi-open-file credit the INSTANT the adjacent flank pawn moved
    /// - e.g. pushing the a-pawn one square immediately "opens" the a8/a1
    /// rook's file even though the rook hasn't developed at all and can't
    /// use the file yet. That made pushing a flank pawn purely to farm this
    /// bonus attractive very early in the game, with no actual development
    /// behind it - this is what was causing early a4/a5-style pawn pushes.
    /// Requiring the rook to have actually moved fixes that at the source.
    /// </summary>
    private static bool IsRookHomeSquare(int sq)
    {
        int f = Sq.File(sq), r = Sq.Rank(sq);
        return (f == 0 || f == 7) && (r == 0 || r == 7);
    }

    /// <summary>Checks whether `pawnBB` has any pawn on the given file. O(8) bit tests - cheap, and only called per-rook (at most a handful per position).</summary>
    private static bool HasPawnOnFile(ulong pawnBB, int file)
    {
        for (int rank = 0; rank < 8; rank++)
        {
            int sq = Sq.Make(file, rank);
            if (((pawnBB >> sq) & 1UL) != 0) return true;
        }
        return false;
    }

    /// <summary>
    /// Cheap proxy for how exposed `kingColor`'s king currently is: a flat
    /// penalty if the king is still sitting on its starting square
    /// (uncastled), plus a penalty per missing pawn in the 3-file shield
    /// directly in front of it. Deliberately simple/cheap - this runs at
    /// EVERY leaf of the search, so it only uses a handful of bit tests, no
    /// attack-generation calls (those are comparatively expensive and would
    /// meaningfully slow down a depth-9+ search if run per-leaf).
    ///
    /// This is a real approximation, not a full king-safety evaluation: it
    /// doesn't check whether the king actually castled to relative safety
    /// vs. just moved off the start square some other way, and the "shield
    /// rank" check is most meaningful for a castled king, less so for one
    /// still wandering in the center (though the uncastled penalty already
    /// covers that case separately). Good enough as a directional bias, not
    /// meant to be a precise king-safety score.
    /// </summary>
    private static int OpponentExposureBonus(Board b, Color kingColor)
    {
        int kingPieceIdx = kingColor == Color.White ? 5 : 11; // kind 5 = king, +6 offset for black
        ulong kingBB = b.PieceBB[kingPieceIdx];
        if (kingBB == 0) return 0; // shouldn't happen in a legal position, but stay safe

        int kingSq = Bits.PopLsb(ref kingBB); // local copy, doesn't mutate b.PieceBB
        int kingFile = Sq.File(kingSq);
        int kingRank = Sq.Rank(kingSq);

        int homeRank = kingColor == Color.White ? 0 : 7;
        const int startFile = 4; // e-file
        bool stillOnStartSquare = kingFile == startFile && kingRank == homeRank;

        int pawnPieceIdx = kingColor == Color.White ? 0 : 6; // kind 0 = pawn
        ulong pawns = b.PieceBB[pawnPieceIdx];

        int shieldRank = kingColor == Color.White ? homeRank + 1 : homeRank - 1;
        int missingShield = 0;
        for (int df = -1; df <= 1; df++)
        {
            int f = kingFile + df;
            if (f < 0 || f > 7) continue;
            int sq = Sq.Make(f, shieldRank);
            bool hasPawn = ((pawns >> sq) & 1UL) != 0;
            if (!hasPawn) missingShield++;
        }

        int bonus = 0;
        if (stillOnStartSquare) bonus += UncastledPenalty;
        bonus += missingShield * MissingShieldPawnPenalty;

        return bonus;
    }

    /// <summary>
    /// Rewards keeping non-pawn material on the board (both sides combined,
    /// unsigned magnitude only - the caller applies the SelfColor sign),
    /// plus an extra per-queen bonus since queens create disproportionately
    /// more tactical complexity than any other piece. The idea: more pieces
    /// on the board means more forcing lines and more ways for a weaker
    /// opponent to miscalculate, so this discourages simplifying into a
    /// "safe" reduced position even when that would be objectively fine for
    /// a strong opponent to allow. Cheap - just a handful of popcounts.
    /// </summary>
    private static int ComplexityRetentionBonus(Board b)
    {
        int nonPawnPieces = 0;
        for (int kind = 1; kind <= 3; kind++) // knight, bishop, rook
        {
            nonPawnPieces += Bits.PopCount(b.PieceBB[kind]) + Bits.PopCount(b.PieceBB[kind + 6]);
        }
        int queenCount = Bits.PopCount(b.PieceBB[4]) + Bits.PopCount(b.PieceBB[10]);

        return nonPawnPieces * KeepPiecesWeight + queenCount * QueensOnBoardBonusPerQueen;
    }

    /// <summary>
    /// Rewards our knights/bishops/queen being physically close to the
    /// OPPONENT's king (fixed by `oppColor`, not by whose turn it is at this
    /// leaf) - a cheap stand-in for "how much pressure are we applying"
    /// that avoids real attack-square generation (which MobilityScore
    /// already shows is comparatively expensive, and this runs at every
    /// leaf). Distance is plain Chebyshev distance (max file/rank
    /// difference) between each piece and the opponent king's square - no
    /// bitboard attack lookups at all, just arithmetic on square indices.
    /// </summary>
    private static int PieceProximityBonus(Board b, Color selfColor, Color oppColor)
    {
        int oppKingPieceIdx = oppColor == Color.White ? 5 : 11;
        ulong oppKingBB = b.PieceBB[oppKingPieceIdx];
        if (oppKingBB == 0) return 0;
        int oppKingSq = Bits.PopLsb(ref oppKingBB);
        int kf = Sq.File(oppKingSq), kr = Sq.Rank(oppKingSq);

        int baseIdx = selfColor == Color.White ? 0 : 6;
        int bonus = 0;
        bonus += ProximitySum(b.PieceBB[baseIdx + 1], kf, kr, KnightProximityWeight); // knights
        bonus += ProximitySum(b.PieceBB[baseIdx + 2], kf, kr, BishopProximityWeight); // bishops
        bonus += ProximitySum(b.PieceBB[baseIdx + 4], kf, kr, QueenProximityWeight);  // queen(s)
        return bonus;
    }

    private static int ProximitySum(ulong pieces, int kf, int kr, int weightPerCloseness)
    {
        int total = 0;
        ulong p = pieces;
        while (p != 0)
        {
            int sq = Bits.PopLsb(ref p);
            int f = Sq.File(sq), r = Sq.Rank(sq);
            int dist = Math.Max(Math.Abs(f - kf), Math.Abs(r - kr)); // Chebyshev distance, 1..7
            total += (7 - dist) * weightPerCloseness;
        }
        return total;
    }

    /// <summary>
    /// Rewards advanced pawns of ours on/near the OPPONENT king's file (a
    /// simple pawn-storm proxy) - concrete, advancing pawn threats near the
    /// enemy king tend to demand precise, calculation-heavy responses that a
    /// weaker player is more likely to get wrong than a slower positional
    /// squeeze elsewhere on the board. Only counts pawns within 1 file of
    /// the opponent king's file (same "shield window" as
    /// OpponentExposureBonus), weighted by how far advanced each one is.
    ///
    /// IMPORTANT: only activates once the opponent king is on a wing (file
    /// a-c or f-h) - NOT while it's still on the center d/e files. The
    /// opponent king starts on the e-file and stays there until it actually
    /// castles, so without this check the "storm window" (kingFile +-1)
    /// covered the d/e/f files during the entire opening - exactly the
    /// files normal opening theory pushes anyway (e4, d4, etc). That meant
    /// this bonus was firing on completely ordinary central development in
    /// nearly every line, not on an actual flank pawn storm, which is what
    /// caused pawns to get pushed aggressively even at a weight of 1. A real
    /// storm only makes sense once the opponent has committed to a side.
    /// </summary>
    private static int PawnStormBonus(Board b, Color selfColor, Color oppColor)
    {
        int oppKingPieceIdx = oppColor == Color.White ? 5 : 11;
        ulong oppKingBB = b.PieceBB[oppKingPieceIdx];
        if (oppKingBB == 0) return 0;
        int oppKingSq = Bits.PopLsb(ref oppKingBB);
        int kf = Sq.File(oppKingSq);

        if (kf >= 3 && kf <= 4) return 0; // king still on d/e file (central/uncommitted) - not a storm target yet

        int pawnPieceIdx = selfColor == Color.White ? 0 : 6;
        ulong ourPawns = b.PieceBB[pawnPieceIdx];
        int homeRank = selfColor == Color.White ? 1 : 6; // starting pawn rank

        int bonus = 0;
        ulong p = ourPawns;
        while (p != 0)
        {
            int sq = Bits.PopLsb(ref p);
            int f = Sq.File(sq), r = Sq.Rank(sq);
            if (Math.Abs(f - kf) > 1) continue; // only pawns on/near the opponent king's file count as a "storm"
            int advancement = selfColor == Color.White ? (r - homeRank) : (homeRank - r);
            advancement = Math.Min(advancement, 5); // safety cap - don't let a single far-advanced pawn dominate the eval
            if (advancement > 0) bonus += advancement * PawnStormWeight;
        }
        return bonus;
    }

    /// <summary>
    /// Rewards material imbalance existing at all (bishop-vs-knight count
    /// differences, unequal pawn counts, etc.), independent of which side
    /// it favors - imbalanced positions are generally harder to navigate
    /// correctly on general principles than clean, balanced ones, which is
    /// exactly the kind of position a weaker player is more likely to
    /// misjudge. Deliberately unsigned/symmetric in what it measures (the
    /// caller applies the SelfColor sign uniformly, same as every other
    /// term here) - it doesn't care which side the imbalance favors, only
    /// that the position isn't materially "clean."
    /// </summary>
    private static int MaterialImbalanceBonus(Board b)
    {
        int imbalance = 0;
        for (int kind = 0; kind <= 4; kind++) // pawn..queen
        {
            int whiteCount = Bits.PopCount(b.PieceBB[kind]);
            int blackCount = Bits.PopCount(b.PieceBB[kind + 6]);
            imbalance += Math.Abs(whiteCount - blackCount);
        }
        return imbalance * MaterialImbalanceWeight;
    }

    private static int MobilityScore(Board b, Color side)
    {
        ulong occ = b.Occupancy;
        ulong own = b.ColorBB[(int)side];
        int baseIdx = side == Color.White ? 0 : 6;
        int score = 0;

        ulong knights = b.PieceBB[baseIdx + 1];
        while (knights != 0)
        {
            int sq = Bits.PopLsb(ref knights);
            score += Bits.PopCount(Attacks.Knight[sq] & ~own) * 4;
        }
        ulong bishops = b.PieceBB[baseIdx + 2];
        while (bishops != 0)
        {
            int sq = Bits.PopLsb(ref bishops);
            score += Bits.PopCount(Attacks.Bishop(sq, occ) & ~own) * 3;
        }
        ulong rooks = b.PieceBB[baseIdx + 3];
        while (rooks != 0)
        {
            int sq = Bits.PopLsb(ref rooks);
            score += Bits.PopCount(Attacks.Rook(sq, occ) & ~own) * 2;
        }
        ulong queens = b.PieceBB[baseIdx + 4];
        while (queens != 0)
        {
            int sq = Bits.PopLsb(ref queens);
            score += Bits.PopCount(Attacks.Queen(sq, occ) & ~own) * 1;
        }
        return score;
    }
}

}