using UnityEngine;
using ChessBot;

/// <summary>
/// Quick manual test for ArrowDrawer: draws one arrow on Start() using square
/// names typed in the Inspector (e.g. "e2" -> "e4"). Not meant to stick
/// around long-term - once you're wiring arrows up for real (best move
/// hints, last-move indicator, etc.) you can delete this and call
/// ArrowDrawer.DrawArrow(...) directly from whatever triggers it.
/// </summary>
public class ArrowDrawerTester : MonoBehaviour
{
    [Header("References")]
    public ArrowDrawer arrowDrawer;

    [Header("Test Arrow")]
    [Tooltip("Square name, e.g. \"e2\".")]
    public string fromSquare = "e2";
    [Tooltip("Square name, e.g. \"e4\".")]
    public string toSquare = "e4";
    public UnityEngine.Color color = UnityEngine.Color.red;
    public float width = 0.2f;

    void Start()
    {
        if (arrowDrawer == null)
        {
            Debug.LogError("ArrowDrawerTester: arrowDrawer is not assigned.");
            return;
        }

        int fromSq = Sq.Parse(fromSquare.Trim().ToLower());
        int toSq = Sq.Parse(toSquare.Trim().ToLower());

        arrowDrawer.DrawArrow(fromSq, toSq, color, width);
    }
}