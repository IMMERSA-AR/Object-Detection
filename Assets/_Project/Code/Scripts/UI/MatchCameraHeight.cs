using UnityEngine;

public class MatchCameraHeight : MonoBehaviour
{
    [SerializeField] private Transform cameraTransform;

    [Header("Axes to match")]
    [SerializeField] private bool matchX = true;
    [SerializeField] private bool matchY = true;
    [Header("Offsets (metres)")]
    [SerializeField] private float xOffset = 0f;
    [SerializeField] private float yOffset = 0f;
    [Header("Behaviour")]
    [SerializeField] private bool continuous = true;
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

        if (!continuous && _snapped) enabled = false; 
    }

    private void LateUpdate()
    {
        if (cameraTransform == null)
        {
            ResolveCamera();
            if (cameraTransform == null) return;  
        }

        bool instant = smoothTime <= 0f || !_snapped;
        ApplyPosition(instant);
        _snapped = true;

        if (!continuous) enabled = false;  
    }

    private void ResolveCamera()
    {
        if (cameraTransform != null) return;

        if (Camera.main != null) { cameraTransform = Camera.main.transform; return; }

        foreach (var cam in Camera.allCameras)
        {
            string n = cam.name.ToLowerInvariant();
            if (n.Contains("centereye") || n.Contains("center eye") || n.Contains("eye"))
            {
                cameraTransform = cam.transform;
                return;
            }
        }

        if (Camera.allCamerasCount > 0)
            cameraTransform = Camera.allCameras[0].transform;
    }

    private void ApplyPosition(bool instant)
    {
        Vector3 current = transform.position;
        Vector3 cam = cameraTransform.position;

        Vector3 target = current;
        if (matchX) target.x = cam.x + xOffset;
        if (matchY) target.y = cam.y + yOffset;

        transform.position = instant
            ? target
            : Vector3.SmoothDamp(current, target, ref _velocity, smoothTime);
    }
}
