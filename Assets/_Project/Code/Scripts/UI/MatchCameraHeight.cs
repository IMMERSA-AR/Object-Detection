using UnityEngine;

/// <summary>
/// Locks this object's position to the camera (headset) on the chosen axes, so a
/// world-space panel like the Scene Selector stays at the user's eye height and/or
/// horizontally centred on the user. Z is left untouched by default.
/// </summary>
public class MatchCameraHeight : MonoBehaviour
{
    [Tooltip("Camera to match. Leave empty to use Camera.main (the CenterEyeAnchor).")]
    [SerializeField] private Transform cameraTransform;

    [Header("Axes to match")]
    [Tooltip("Match the camera's X position (left/right).")]
    [SerializeField] private bool matchX = true;
    [Tooltip("Match the camera's Y position (height / eye level).")]
    [SerializeField] private bool matchY = true;

    [Header("Offsets (metres)")]
    [Tooltip("Offset added on top of the camera's X.")]
    [SerializeField] private float xOffset = 0f;
    [Tooltip("Offset added on top of the camera's Y. " +
             "0 = exactly eye level, negative = below eye level.")]
    [SerializeField] private float yOffset = 0f;

    [Header("Behaviour")]
    [Tooltip("ON  = follow the camera every frame (panel tracks the user).\n" +
             "OFF = set position only once at start.")]
    [SerializeField] private bool continuous = true;

    [Tooltip("Smoothing time (seconds). 0 = snap instantly. Higher = gentler easing.")]
    [SerializeField] private float smoothTime = 0.15f;

    private Vector3 _velocity;
    private bool _snapped;

    private void Start()
    {
        ResolveCamera();

        if (cameraTransform != null)
        {
            ApplyPosition(instant: true);
            _snapped = true;
        }

        if (!continuous && _snapped) enabled = false;   // one-shot done
    }

    private void LateUpdate()
    {
        if (cameraTransform == null)
        {
            ResolveCamera();
            if (cameraTransform == null) return;        // headset not ready yet — wait
        }

        // First valid frame: snap (in case Start ran before the rig was tracking).
        bool instant = smoothTime <= 0f || !_snapped;
        ApplyPosition(instant);
        _snapped = true;

        if (!continuous) enabled = false;               // one-shot: stop after first good placement
    }

    /// <summary>
    /// Find the headset camera even when Camera.main is null (the Quest
    /// CenterEyeAnchor is frequently NOT tagged "MainCamera").
    /// </summary>
    private void ResolveCamera()
    {
        if (cameraTransform != null) return;

        // 1) MainCamera tag, if present.
        if (Camera.main != null) { cameraTransform = Camera.main.transform; return; }

        // 2) Look for the OVR/center-eye camera by name.
        foreach (var cam in Camera.allCameras)
        {
            string n = cam.name.ToLowerInvariant();
            if (n.Contains("centereye") || n.Contains("center eye") || n.Contains("eye"))
            {
                cameraTransform = cam.transform;
                return;
            }
        }

        // 3) Fallback: any enabled camera in the scene.
        if (Camera.allCamerasCount > 0)
            cameraTransform = Camera.allCameras[0].transform;
    }

    private void ApplyPosition(bool instant)
    {
        Vector3 current = transform.position;
        Vector3 cam = cameraTransform.position;

        // Build the target: matched axes follow the camera, others keep current value.
        Vector3 target = current;
        if (matchX) target.x = cam.x + xOffset;
        if (matchY) target.y = cam.y + yOffset;

        transform.position = instant
            ? target
            : Vector3.SmoothDamp(current, target, ref _velocity, smoothTime);
    }
}
