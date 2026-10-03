using UnityEngine;
using ChessBot;

/// <summary>
/// Spawns 64 square prefabs (alternating light/dark) to build the visual
/// chessboard. Uses the same coordinate convention as BoardVisualizer: place
/// this on an empty GameObject at the a1 *corner* of the board (not the
/// center of the a1 square), with the board extending +X across files a->h
/// and +Z across ranks 1->8.
///
/// a1 is a dark square in standard chess, so square (file, rank) is dark
/// when (file + rank) is even.
/// </summary>
public class BoardSquareSpawner : MonoBehaviour
{
    [Header("Prefabs")]
    public GameObject lightSquarePrefab;
    public GameObject darkSquarePrefab;

    [Header("Layout")]
    [Tooltip("Transform at the a1 corner of the board. Defaults to this GameObject's transform if left empty.")]
    public Transform origin;

    [Tooltip("Size of one square's side, in world units. Should match BoardVisualizer.squareSize.")]
    public float squareSize = 1f;

    [Header("Optional")]
    [Tooltip("Parent transform for spawned square instances. Defaults to this GameObject's transform.")]
    public Transform squareContainer;

    private readonly GameObject[] _squares = new GameObject[64];

    void Start()
    {
        SpawnBoard();
    }

    /// <summary>Destroys any previously spawned squares and instantiates a fresh 8x8 board.</summary>
    public void SpawnBoard()
    {
        ClearBoard();

        if (lightSquarePrefab == null || darkSquarePrefab == null)
        {
            Debug.LogError("BoardSquareSpawner: assign both lightSquarePrefab and darkSquarePrefab before spawning.");
            return;
        }

        Transform o = origin != null ? origin : transform;
        Transform container = squareContainer != null ? squareContainer : transform;

        for (int file = 0; file < 8; file++)
        {
            for (int rank = 0; rank < 8; rank++)
            {
                int sq = Sq.Make(file, rank);
                bool isDark = (file + rank) % 2 == 0; // a1 (file0,rank0) is dark

                GameObject prefab = isDark ? darkSquarePrefab : lightSquarePrefab;

                Vector3 localPos = new Vector3(
                    (file + 0.5f) * squareSize,
                    0f,
                    (rank + 0.5f) * squareSize);
                Vector3 worldPos = o.TransformPoint(localPos);

                GameObject instance = Instantiate(prefab, worldPos, o.rotation, container);
                instance.name = $"Square_{Sq.Name(sq)}_{(isDark ? "Dark" : "Light")}";
                _squares[sq] = instance;
            }
        }
    }

    /// <summary>Destroys all currently spawned square instances.</summary>
    public void ClearBoard()
    {
        for (int i = 0; i < 64; i++)
        {
            if (_squares[i] != null)
            {
                if (Application.isPlaying) Destroy(_squares[i]);
                else DestroyImmediate(_squares[i]);
                _squares[i] = null;
            }
        }
    }
}