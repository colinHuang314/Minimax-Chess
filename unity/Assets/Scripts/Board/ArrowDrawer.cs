using System.Collections.Generic;
using UnityEngine;
using ChessBot;

/// <summary>
/// Draws an arrow on the 3D board from one square to another - e.g. to show
/// the engine's suggested move, the last move played, a hint, etc.
///
/// Not wired up to anything yet; call DrawArrow(...) from wherever you want
/// (e.g. after the bot moves, or on hover). Multiple arrows can be shown at
/// once; each call returns a handle you can pass to RemoveArrow, or call
/// ClearAllArrows() to wipe them all.
///
/// Two rendering modes:
///   1. PROCEDURAL (default, no setup required): generates a flat arrow mesh
///      (rectangular shaft + triangular head) lying just above the board.
///   2. PREFAB: if you assign `arrowPrefab`, that's instantiated/scaled/
///      rotated to span the two squares instead. See the tooltip on
///      `arrowPrefab` for the modeling convention it needs to follow.
/// </summary>
/// 


public class ArrowDrawer : MonoBehaviour
{
    [Header("Board Reference")]
    [Tooltip("Used to convert square indices to world positions. Must be assigned.")]
    public BoardVisualizer boardVisualizer;

    [Header("Appearance Defaults")]
    [Tooltip("Default arrow color if not specified per-call.")]
    public UnityEngine.Color defaultColor = new UnityEngine.Color(1f, 0.85f, 0f, 0.85f); // translucent gold, chess.com-ish

    [Tooltip("Default shaft width (world units) if not specified per-call.")]
    public float defaultWidth = 0.18f;

    [Tooltip("Height above the board surface the arrow is drawn at, to avoid z-fighting with the board mesh.")]
    public float yOffset = 0.02f;

    [Header("Procedural Arrow Shape (used when arrowPrefab is not assigned)")]
    [Tooltip("Arrowhead length as a fraction of the shaft width (the head widens/lengthens relative to shaft width, not total arrow length).")]
    public float headLengthMultiplier = 2.5f;
    [Tooltip("Arrowhead width as a multiple of the shaft width.")]
    public float headWidthMultiplier = 2.2f;

    [Header("Optional Prefab Mode")]
    [Tooltip("Optional. If assigned, this prefab is used instead of the procedural mesh. " +
             "MODELING CONVENTION: model it centered at the local origin, elongated along " +
             "local +Z, exactly 1 unit long (tail at z=-0.5, tip at z=+0.5). It will be scaled " +
             "on Z to match the distance between squares, positioned at the midpoint, and " +
             "rotated so +Z points from the start square to the end square.")]
    public GameObject arrowPrefab;

    // Cached at runtime so every arrow doesn't need its own material instance.
    private Material _proceduralMaterialTemplate;

    private readonly List<GameObject> _activeArrows = new List<GameObject>();

    /// <summary>
    /// Draws an arrow from fromSq to toSq (square indices 0..63, a1=0).
    /// Returns the spawned GameObject so you can reposition/remove it later
    /// (e.g. RemoveArrow(handle)), or ignore the return value and just use
    /// ClearAllArrows() when you're done with all of them.
    /// </summary>
    public GameObject DrawArrow(int fromSq, int toSq, UnityEngine.Color? color = null, float? width = null)
    {
        if (boardVisualizer == null)
        {
            Debug.LogError("ArrowDrawer: boardVisualizer is not assigned.");
            return null;
        }
        if (fromSq == toSq)
        {
            Debug.LogWarning("ArrowDrawer: fromSq and toSq are the same square, skipping.");
            return null;
        }

        Vector3 start = boardVisualizer.SquareToWorld(fromSq) + Vector3.up * yOffset;
        Vector3 end = boardVisualizer.SquareToWorld(toSq) + Vector3.up * yOffset;
        UnityEngine.Color c = color ?? defaultColor;
        float w = width ?? defaultWidth;

        GameObject arrow = arrowPrefab != null
            ? BuildFromPrefab(start, end, c)
            : BuildProcedural(start, end, c, w);

        arrow.name = $"Arrow_{Sq.Name(fromSq)}_{Sq.Name(toSq)}";
        _activeArrows.Add(arrow);
        return arrow;
    }

    /// <summary>Removes a single previously-drawn arrow (the GameObject returned by DrawArrow).</summary>
    public void RemoveArrow(GameObject arrow)
    {
        if (arrow == null) return;
        _activeArrows.Remove(arrow);
        Destroy(arrow);
    }

    /// <summary>Removes every arrow currently drawn by this ArrowDrawer.</summary>
    public void ClearAllArrows()
    {
        foreach (var arrow in _activeArrows)
        {
            if (arrow != null) Destroy(arrow);
        }
        _activeArrows.Clear();
    }

    // ---- Prefab-based arrow -------------------------------------------------

    private GameObject BuildFromPrefab(Vector3 start, Vector3 end, UnityEngine.Color color)
    {
        Vector3 mid = (start + end) * 0.5f;
        Vector3 dir = end - start;
        float length = dir.magnitude;

        GameObject instance = Instantiate(arrowPrefab, mid, Quaternion.LookRotation(dir.normalized, Vector3.up), transform);
        Vector3 scale = instance.transform.localScale;
        scale.z = length; // prefab convention: 1 unit long along local Z at scale 1
        instance.transform.localScale = scale;

        // Best-effort recolor: tint any renderer materials that expose _Color.
        foreach (var r in instance.GetComponentsInChildren<Renderer>())
        {
            foreach (var m in r.materials)
            {
                if (m.HasProperty("_Color")) m.color = color;
            }
        }

        return instance;
    }

    // ---- Procedural arrow (shaft rectangle + triangular head) ---------------

    private GameObject BuildProcedural(Vector3 start, Vector3 end, UnityEngine.Color color, float shaftWidth)
    {
        Vector3 full = end - start;
        float totalLength = full.magnitude;
        Vector3 dir = full / totalLength;
        Vector3 perp = Vector3.Cross(Vector3.up, dir); // flat on the XZ plane

        float headLength = Mathf.Min(shaftWidth * headLengthMultiplier, totalLength * 0.6f);
        float headWidth = shaftWidth * headWidthMultiplier;
        float shaftEndDist = Mathf.Max(0f, totalLength - headLength);

        Vector3 shaftEndCenter = start + dir * shaftEndDist;

        // Local-space vertices (object itself sits at world origin - the mesh
        // stores absolute world positions directly, so no transform juggling).
        Vector3 s0 = start - perp * (shaftWidth * 0.5f);
        Vector3 s1 = start + perp * (shaftWidth * 0.5f);
        Vector3 s2 = shaftEndCenter + perp * (shaftWidth * 0.5f);
        Vector3 s3 = shaftEndCenter - perp * (shaftWidth * 0.5f);

        Vector3 h0 = shaftEndCenter - perp * (headWidth * 0.5f);
        Vector3 h1 = shaftEndCenter + perp * (headWidth * 0.5f);
        Vector3 h2 = end;

        var mesh = new Mesh { name = "ArrowMesh" };
        mesh.vertices = new[] { s0, s1, s2, s3, h0, h1, h2 };
        mesh.triangles = new[]
        {
            // Shaft (two triangles forming a rectangle), wound for an upward-facing normal
            0, 2, 1,
            0, 3, 2,
            // Head (single triangle)
            4, 6, 5
        };
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        var go = new GameObject("ArrowMesh");
        go.transform.SetParent(transform, worldPositionStays: true);
        var mf = go.AddComponent<MeshFilter>();
        mf.mesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.material = GetProceduralMaterial(color);
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;

        return go;
    }

    private Material GetProceduralMaterial(UnityEngine.Color color)
    {
        if (_proceduralMaterialTemplate == null)
        {
            // "Unlit/Color" ships with every Unity install and needs no setup;
            // swap this out for a URP/HDRP unlit shader if your project uses
            // one of those pipelines and the built-in shader isn't available.
            Shader shader = Shader.Find("Unlit/Color");
            if (shader == null) shader = Shader.Find("Sprites/Default"); // fallback, supports vertex alpha
            _proceduralMaterialTemplate = new Material(shader);
        }

        var mat = new Material(_proceduralMaterialTemplate) { color = color };
        return mat;
    }
}