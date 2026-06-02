using UnityEngine;

/// <summary>
/// Attached automatically to a spawned panel character by PanelDetector.
///
/// Either the LEFT or RIGHT controller can aim at and trigger this character:
///   • A laser line is drawn from whichever controller is currently aimed at the character.
///   • Line turns GREEN when aimed at this character.
///   • Press the INDEX TRIGGER on either controller while aimed → narration starts once.
///
/// The LineRenderer is taken directly from the spawned character prefab (same setup as
/// the Murad Q&A prefab in the lecture hall scene). A fallback LineRenderer on
/// PanelDetector is used if the prefab has none.
/// </summary>
[DisallowMultipleComponent]
public class PanelCharacterInteraction : MonoBehaviour
{
    [Tooltip("Maximum ray distance in metres.")]
    public float maxPointDistance = 10f;

    // Filled by PanelDetector via Init()
    private PanelDetector _detector;
    private AudioClip     _clip;
    private string        _transcript;
    private Transform     _rightController;
    private Transform     _leftController;
    private LineRenderer  _laserPointer;
    private bool          _triggered;

    // ── Public init ───────────────────────────────────────────────────────────

    /// <summary>Called by PanelDetector right after spawning the character.</summary>
    public void Init(PanelDetector detector, AudioClip narrationClip, string transcript,
                     Transform rightController, Transform leftController,
                     LineRenderer laserPointer)
    {
        _detector         = detector;
        _clip             = narrationClip;
        _transcript       = transcript;
        _rightController  = rightController;
        _leftController   = leftController;
        _laserPointer     = laserPointer;

        EnsureCollider();

        if (_rightController == null && _leftController == null)
            Debug.LogWarning("[PanelCharacterInteraction] Neither rightController nor leftController " +
                             "is assigned on PanelDetector — drag RightControllerAnchor and/or " +
                             "LeftControllerAnchor from OVRCameraRig → TrackingSpace.");

        if (_laserPointer == null)
            Debug.LogWarning("[PanelCharacterInteraction] No LineRenderer found on character prefab " +
                             "and no fallback laserPointer assigned on PanelDetector. " +
                             "Add a LineRenderer component to the character prefab.");

        // Initialise laser to hidden
        if (_laserPointer != null)
        {
            _laserPointer.positionCount = 2;
            _laserPointer.SetPosition(0, Vector3.zero);
            _laserPointer.SetPosition(1, Vector3.zero);
            _laserPointer.enabled = false;
        }
    }

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    private void Update()
    {
        if (_triggered || _detector == null) return;
        if (_rightController == null && _leftController == null) return;

        // ── Test both controllers; pick the one that hits this character ──────
        bool     hitMe          = false;
        Vector3  laserEnd       = Vector3.zero;
        Transform aimController = null;

        if (TryRaycast(_rightController, out Vector3 rEnd, out bool rHit))
        {
            aimController = _rightController;
            laserEnd      = rEnd;
            if (rHit) hitMe = true;
        }

        // Left controller overrides if it hits (or is the only controller assigned)
        if (!hitMe && TryRaycast(_leftController, out Vector3 lEnd, out bool lHit))
        {
            if (lHit || aimController == null)
            {
                aimController = _leftController;
                laserEnd      = lEnd;
            }
            if (lHit) hitMe = true;
        }

        // ── Update laser visual ───────────────────────────────────────────────
        if (_laserPointer != null && aimController != null)
        {
            _laserPointer.enabled = true;
            _laserPointer.SetPosition(0, aimController.position);
            _laserPointer.SetPosition(1, laserEnd);
            _laserPointer.startColor = hitMe ? Color.green : Color.white;
            _laserPointer.endColor   = hitMe ? Color.green : Color.white;
        }

        // ── Trigger on either index trigger while aimed ───────────────────────
        if (hitMe)
        {
            bool pressed = OVRInput.GetDown(OVRInput.RawButton.RIndexTrigger)
                        || OVRInput.GetDown(OVRInput.RawButton.LIndexTrigger);
            if (pressed)
            {
                _triggered = true;
                if (_laserPointer != null) _laserPointer.enabled = false;
                Debug.Log("[PanelCharacterInteraction] Trigger pressed on character — starting narration.");
                _detector.TriggerNarration(gameObject, _clip);
            }
        }
    }

    private void OnDisable()
    {
        if (_laserPointer != null) _laserPointer.enabled = false;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Casts a ray from <paramref name="controller"/> forward.
    /// Returns false if controller is null.
    /// <paramref name="endPoint"/> is the world hit point (or max-range endpoint).
    /// <paramref name="hitsMe"/> is true when the ray hits this character's collider.
    /// </summary>
    private bool TryRaycast(Transform controller, out Vector3 endPoint, out bool hitsMe)
    {
        endPoint = Vector3.zero;
        hitsMe   = false;

        if (controller == null) return false;

        Ray ray = new Ray(controller.position, controller.forward);
        endPoint = controller.position + controller.forward * maxPointDistance;

        if (Physics.Raycast(ray, out RaycastHit hit, maxPointDistance))
        {
            endPoint = hit.point;
            if (hit.transform.IsChildOf(transform) || hit.transform == transform)
                hitsMe = true;
        }

        return true;
    }

    /// <summary>
    /// Ensures the character has at least one collider so raycasts can hit it.
    /// If the prefab already has any collider, nothing is added.
    /// </summary>
    private void EnsureCollider()
    {
        if (GetComponentInChildren<Collider>() != null) return;

        var cap    = gameObject.AddComponent<CapsuleCollider>();
        cap.center = new Vector3(0f, 1f, 0f);
        cap.height = 2f;
        cap.radius = 0.5f;
        Debug.Log("[PanelCharacterInteraction] No collider found — added CapsuleCollider.");
    }
}
