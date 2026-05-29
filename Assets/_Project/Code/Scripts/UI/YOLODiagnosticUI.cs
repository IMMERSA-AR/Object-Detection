using UnityEngine;

[RequireComponent(typeof(LineRenderer))]
public class YOLODiagnosticUI : MonoBehaviour
{
    private LineRenderer lineRenderer;
    private bool isDetecting = false;

    void Awake()
    {
        lineRenderer = GetComponent<LineRenderer>();

        // Setup clean, sharp laser lines in world coordinates
        lineRenderer.positionCount = 5;
        lineRenderer.startWidth = 0.03f; // Slightly thicker so it's clear in pass-through
        lineRenderer.endWidth = 0.03f;
        lineRenderer.loop = true;
        lineRenderer.useWorldSpace = true;

        // Force lines to render over passthrough background color textures
        lineRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lineRenderer.receiveShadows = false;
    }

    /// <summary>
    /// Draws the literal bounding box calculated from the YOLO system
    /// </summary>
    public void DisplayTestMetrics(Vector3 center, float width, float height, string className, float confidence)
    {
        isDetecting = true;
        lineRenderer.enabled = true;

        // Calculate the 4 exact spatial coordinates of your target box bounds
        Vector3 topLeft = center + new Vector3(-width / 2, height / 2, 0);
        Vector3 topRight = center + new Vector3(width / 2, height / 2, 0);
        Vector3 bottomRight = center + new Vector3(width / 2, -height / 2, 0);
        Vector3 bottomLeft = center + new Vector3(-width / 2, -height / 2, 0);

        // Map the corners directly into world space locations
        lineRenderer.SetPosition(0, topLeft);
        lineRenderer.SetPosition(1, topRight);
        lineRenderer.SetPosition(2, bottomRight);
        lineRenderer.SetPosition(3, bottomLeft);
        lineRenderer.SetPosition(4, topLeft); // Close loop

        // Green if certain, Red if uncertain
        Color boxColor = confidence >= 0.75f ? Color.green : Color.red;
        lineRenderer.startColor = boxColor;
        lineRenderer.endColor = boxColor;
    }

    public void ClearDiagnostics()
    {
        isDetecting = false;
        if (lineRenderer != null) lineRenderer.enabled = false;
    }
}