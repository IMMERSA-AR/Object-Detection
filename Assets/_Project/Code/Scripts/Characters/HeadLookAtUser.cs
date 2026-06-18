using UnityEngine;

/// <summary>
/// Turns a character's head (and a bit of neck) to look at the user during conversation.
/// Runs in LateUpdate so it overrides the animated head pose. Mirrors the head look-at
/// the lecture-hall students use toward the doctor.
///
/// Drop on Hassan. By default it tracks Camera.main (the user's head). Works alongside
/// HassanApproach — he walks up, then keeps his gaze on you while talking.
/// </summary>
public class HeadLookAtUser : MonoBehaviour
{
    [Header("Target")]
    [Tooltip("What to look at. Leave empty to track the user (Camera.main).")]
    public Transform target;

    [Header("Bones (exact names; searched in children)")]
    [Tooltip("CC4: CC_Base_Head   •   Mixamo: mixamorig:Head")]
    public string headBoneName = "CC_Base_Head";
    [Tooltip("CC4: CC_Base_NeckTwist01   •   Mixamo: mixamorig:Neck   (optional)")]
    public string neckBoneName = "CC_Base_NeckTwist01";

    [Header("Look Settings")]
    [Range(0f, 1f)]
    [Tooltip("How strongly the head turns to the target (0 = none, 1 = full override).")]
    public float headWeight = 0.7f;

    [Range(0f, 1f)]
    [Tooltip("Fraction of the head turn the neck also takes, for a natural two-joint look.")]
    public float neckWeight = 0.4f;

    [Tooltip("Max angle from the body's forward the head will turn (degrees). Beyond this " +
             "it stays in the animated pose so he never cranes unnaturally.")]
    public float maxAngle = 70f;

    [Tooltip("How smoothly the head tracks (higher = snappier).")]
    public float trackSpeed = 5f;

    [Tooltip("Yaw offset (degrees) for the BODY forward reference. If the head turns the " +
             "wrong way, set this to match HassanApproach's Facing Yaw Offset (often 180).")]
    public float bodyYawOffset = 0f;

    [Tooltip("Master on/off — disable to release the head back to pure animation.")]
    public bool active = true;

    private Transform  _head, _neck;
    private Quaternion _headSmooth;
    private bool       _initialised;

    void Start()
    {
        _head = FindDeepChild(transform, headBoneName);
        _neck = FindDeepChild(transform, neckBoneName);

        if (_head == null)
            Debug.LogWarning($"[HeadLookAtUser] {name}: head bone '{headBoneName}' not found — " +
                             "set the correct bone name (CC4 = CC_Base_Head, Mixamo = mixamorig:Head).");
    }

    void LateUpdate()
    {
        if (!active || _head == null) return;

        Transform t = target != null
            ? target
            : (Camera.main != null ? Camera.main.transform : null);
        if (t == null) return;

        if (!_initialised) { _headSmooth = _head.rotation; _initialised = true; }

        // Aim at the target's height level so he doesn't look down/up at the floor pivot.
        Vector3 eyeLevel = new Vector3(t.position.x, _head.position.y, t.position.z);
        Vector3 toTarget = eyeLevel - _head.position;
        if (toTarget.sqrMagnitude < 0.0001f) return;

        Vector3 toTargetFlat = new Vector3(toTarget.x, 0f, toTarget.z).normalized;
        Vector3 bodyFwd = Quaternion.Euler(0f, bodyYawOffset, 0f) * transform.forward;
        bodyFwd.y = 0f;
        bodyFwd.Normalize();

        // Signed horizontal angle from body forward to the target, clamped.
        float angleY    = Vector3.SignedAngle(bodyFwd, toTargetFlat, Vector3.up);
        float clamped   = Mathf.Clamp(angleY, -maxAngle, maxAngle);

        Quaternion animRot = _head.rotation;
        Quaternion desired = Quaternion.AngleAxis(clamped, Vector3.up) * animRot;

        _headSmooth = Quaternion.Slerp(_headSmooth, desired, Time.deltaTime * trackSpeed);
        _head.rotation = Quaternion.Slerp(animRot, _headSmooth, headWeight);

        if (_neck != null)
            _neck.rotation = Quaternion.Slerp(_neck.rotation, _headSmooth, headWeight * neckWeight);
    }

    private static Transform FindDeepChild(Transform parent, string boneName)
    {
        if (string.IsNullOrEmpty(boneName)) return null;
        foreach (Transform child in parent.GetComponentsInChildren<Transform>(true))
            if (string.Equals(child.name, boneName, System.StringComparison.OrdinalIgnoreCase))
                return child;
        return null;
    }
}
