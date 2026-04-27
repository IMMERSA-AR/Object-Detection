using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using System.Collections.Generic;

/// <summary>
/// Quest World Space UI ray pointer.
/// Bypasses GraphicRaycaster entirely — directly tests the ray against
/// every Button's RectTransform in the canvas. Works with runtime-spawned
/// prefab cards, negative canvas scale, and Quest stereo rendering.
/// </summary>
public class ControllerRayPointer : MonoBehaviour
{
    [Header("Ray Settings")]
    public float rayLength = 10f;
    public Color rayColor = new Color(1f, 0.85f, 0.3f, 1f);
    public float rayWidth = 0.003f;

    [Header("References — drag in Inspector")]
    public Canvas targetCanvas;
    public Transform trackingSpace;

    private LineRenderer _line;
    private EventSystem _eventSystem;
    private GameObject _currentHovered;
    private Vector3 _lastHitPoint = Vector3.zero;

    private float _lastLogTime = -999f;
    private void TLog(string msg)
    {
        if (Time.time - _lastLogTime > 1f) { Debug.Log(msg); _lastLogTime = Time.time; }
    }

    // ── Start ──────────────────────────────────────────────────────
    void Start()
    {
        Debug.Log("[RayPointer] Start()");

        _line = gameObject.AddComponent<LineRenderer>();
        _line.positionCount = 2;
        _line.startWidth = rayWidth;
        _line.endWidth = rayWidth * 0.2f;
        _line.useWorldSpace = true;
        _line.material = new Material(Shader.Find("Unlit/Color"));
        _line.material.color = rayColor;
        _line.enabled = false;

        _eventSystem = EventSystem.current;

        if (trackingSpace == null)
        {
            GameObject ts = GameObject.Find("TrackingSpace");
            if (ts != null) trackingSpace = ts.transform;
        }

        if (targetCanvas == null)
        {
            GameObject mc = GameObject.Find("MenusCanvas");
            if (mc != null) targetCanvas = mc.GetComponent<Canvas>();
        }

        Debug.Log($"[RayPointer] Canvas={targetCanvas?.name ?? "NULL"} " +
                  $"TrackingSpace={trackingSpace?.name ?? "NULL"}");
    }

    // ── Update ─────────────────────────────────────────────────────
    void Update()
    {
        if (!OVRInput.GetControllerPositionTracked(OVRInput.Controller.RTouch))
        {
            _line.enabled = false;
            ClearHover();
            return;
        }

        // Build world-space ray from right controller
        Vector3 lp = OVRInput.GetLocalControllerPosition(OVRInput.Controller.RTouch);
        Quaternion lr = OVRInput.GetLocalControllerRotation(OVRInput.Controller.RTouch);
        Vector3 wp = trackingSpace != null ? trackingSpace.TransformPoint(lp) : lp;
        Quaternion wr = trackingSpace != null ? trackingSpace.rotation * lr : lr;

        Vector3 dir = wr * Vector3.forward;
        Ray ray = new Ray(wp, dir);

        _line.enabled = true;
        _line.SetPosition(0, wp);
        _line.SetPosition(1, wp + dir * rayLength);

        // Direct hit test — no GraphicRaycaster, no screen coords
        Button hitButton = FindHitButton(ray);
        GameObject hitObj = hitButton != null ? hitButton.gameObject : null;

        // Hover
        if (hitObj != _currentHovered)
        {
            if (_currentHovered != null)
            {
                _currentHovered.GetComponent<Button>()?.targetGraphic
                    ?.CrossFadeColor(Color.white, 0.1f, true, true);
                Debug.Log($"[RayPointer] Exit: {_currentHovered.name}");
            }
            if (hitObj != null)
            {
                // Highlight the button visually
                hitButton.targetGraphic?.CrossFadeColor(
                    hitButton.colors.highlightedColor, 0.1f, true, true);
                Debug.Log($"[RayPointer] Hover: {hitObj.name}");
            }
            _currentHovered = hitObj;
        }

        if (hitObj != null && _lastHitPoint != Vector3.zero)
            _line.SetPosition(1, _lastHitPoint);

        // Trigger = click
        if (OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch))
        {
            Debug.Log($"[RayPointer] TRIGGER. Button={hitButton?.name ?? "NONE"}");

            if (hitButton != null && hitButton.interactable)
            {
                Debug.Log($"[RayPointer] CLICKING: {hitButton.name}");
                hitButton.onClick.Invoke();
            }
        }
    }

    // ── Direct button hit test ─────────────────────────────────────
    /// <summary>
    /// Intersects the ray with the canvas plane, then checks every
    /// Button's RectTransform to see if the hit point is inside it.
    /// No GraphicRaycaster, no screen-space conversion needed.
    /// </summary>
    private Button FindHitButton(Ray ray)
    {
        if (targetCanvas == null) return null;

        RectTransform canvasRect = targetCanvas.GetComponent<RectTransform>();

        // Step 1: intersect ray with canvas plane (try both normals)
        bool found = false;
        Vector3 hitWorld = Vector3.zero;

        foreach (var normal in new[] { -targetCanvas.transform.forward, targetCanvas.transform.forward })
        {
            Plane plane = new Plane(normal, targetCanvas.transform.position);
            float enter;
            if (!plane.Raycast(ray, out enter) || enter < 0.01f) continue;

            Vector3 hp = ray.GetPoint(enter);
            Vector3 lp3 = canvasRect.InverseTransformPoint(hp);

            if (canvasRect.rect.Contains(new Vector2(lp3.x, lp3.y)))
            {
                hitWorld = hp;
                found = true;
                break;
            }
        }

        if (!found)
        {
            TLog("[RayPointer] Ray misses canvas.");
            return null;
        }

        _lastHitPoint = hitWorld;

        // Step 2: check every Button in the canvas
        Button[] buttons = targetCanvas.GetComponentsInChildren<Button>(false);
        TLog($"[RayPointer] Canvas hit. Checking {buttons.Length} buttons.");

        Button bestButton = null;
        float bestDistance = float.MaxValue;

        foreach (Button btn in buttons)
        {
            if (!btn.gameObject.activeInHierarchy) continue;
            if (!btn.interactable) continue;

            RectTransform rt = btn.GetComponent<RectTransform>();
            if (rt == null) continue;

            // Convert world hit point to this button's local space
            Vector3 localPt3 = rt.InverseTransformPoint(hitWorld);
            Vector2 localPt = new Vector2(localPt3.x, localPt3.y);

            // Check if inside (rect is in local space, unaffected by scale)
            if (rt.rect.Contains(localPt))
            {
                // Use distance along ray to pick the front-most button
                float dist = Vector3.Distance(ray.origin, hitWorld);
                if (dist < bestDistance)
                {
                    bestDistance = dist;
                    bestButton = btn;
                }
                Debug.Log($"[RayPointer] HIT button: {btn.name} localPt={localPt} rect={rt.rect}");
            }
            else
            {
                TLog($"[RayPointer] Miss button: {btn.name} localPt={localPt} rect={rt.rect}");
            }
        }

        return bestButton;
    }

    private void ClearHover()
    {
        if (_currentHovered == null) return;
        _currentHovered.GetComponent<Button>()?.targetGraphic
            ?.CrossFadeColor(Color.white, 0.1f, true, true);
        _currentHovered = null;
    }
}