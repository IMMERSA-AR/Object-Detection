using UnityEngine;

/// <summary>
/// Attached automatically to a spawned panel character by PanelDetector.
///
/// Mirrors the lecture-hall (StreamVoiceAPIController) interaction pattern:
///   • A laser line is drawn from the right controller each frame.
///   • Line turns GREEN when aimed at this character.
///   • Press the RIGHT INDEX TRIGGER while aimed → narration starts once.
///
/// ── Scene Setup ─────────────────────────────────────────────────────────────
///  The PanelDetector needs two scene references so it can pass them here:
///    Right Controller  → drag OVRCameraRig/TrackingSpace/RightControllerAnchor
///    Laser Pointer     → drag a child GameObject of the controller that has
///                        a LineRenderer component on it
///  Assign both on the PanelDetector Inspector (see PanelDetector.rightController
///  and PanelDetector.laserPointer).
/// </summary>
[DisallowMultipleComponent]
public class PanelCharacterInteraction : MonoBehaviour
{
    [Tooltip("Maximum ray distance in metres.")]
    public float maxPointDistance = 10f;

    // Filled by PanelDetector.Init() from its own Inspector fields
    private PanelDetector _detector;
    private AudioClip     _clip;
    private Transform     _rightController;
    private LineRenderer  _laserPointer;
    private bool          _triggered;

    // ── Public init ───────────────────────────────────────────────────────────

    /// <summary>Called by PanelDetector right after spawning the character.</summary>
    public void Init(PanelDetector detector, AudioClip narrationClip,
                     Transform rightController, LineRenderer laserPointer)
    {
        _detector        = detector;
        _clip            = narrationClip;
        _rightController = rightController;
        _laserPointer    = laserPointer;

        EnsureCollider();

        if (_rightController == null)
            Debug.LogWarning("[PanelCharacterInteraction] rightController is not assigned on " +
                             "PanelDetector — drag RightControllerAnchor from OVRCameraRig.");
        if (_laserPointer == null)
            Debug.LogWarning("[PanelCharacterInteraction] laserPointer is not assigned on " +
                             "PanelDetector — add a LineRenderer child to the controller and drag it.");
    }

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    private void Update()
    {
        if (_triggered || _rightController == null || _detector == null) return;

        // ── Ray from controller (identical to StreamVoiceAPIController) ────────
        Ray ray = new Ray(_rightController.position, _rightController.forward);
        Vector3 endPos = _rightController.position + _rightController.forward * maxPointDistance;
        bool hitMe = false;

        if (Physics.Raycast(ray, out RaycastHit hit, maxPointDistance))
        {
            endPos = hit.point;
            if (hit.transform.IsChildOf(transform) || hit.transform == transform)
                hitMe = true;
        }

        // ── Laser visual ───────────────────────────────────────────────────────
        if (_laserPointer != null)
        {
            _laserPointer.enabled = true;
            _laserPointer.SetPosition(0, _rightController.position);
            _laserPointer.SetPosition(1, endPos);
            _laserPointer.startColor = hitMe ? Color.green : Color.white;
            _laserPointer.endColor   = hitMe ? Color.green : Color.white;
        }

        // ── Trigger on index press while aimed ────────────────────────────────
        if (hitMe && OVRInput.GetDown(OVRInput.RawButton.RIndexTrigger))
        {
            _triggered = true;
            if (_laserPointer != null) _laserPointer.enabled = false;
            Debug.Log("[PanelCharacterInteraction] Trigger pressed on character — starting narration.");
            _detector.TriggerNarration(gameObject, _clip);
        }
    }

    private void OnDisable()
    {
        if (_laserPointer != null) _laserPointer.enabled = false;
    }

    // ── Collider ──────────────────────────────────────────────────────────────

    private void EnsureCollider()
    {
        if (GetComponentInChildren<Collider>() != null) return;

        var cap    = gameObject.AddComponent<CapsuleCollider>();
        cap.center = new Vector3(0f, 1f, 0f);
        cap.height = 2f;
        cap.radius = 0.5f;
        Debug.Log("[PanelCharacterInteraction] No collider on prefab — added CapsuleCollider.");
    }
}
