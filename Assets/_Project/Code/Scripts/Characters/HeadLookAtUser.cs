using UnityEngine;

public class HeadLookAtUser : MonoBehaviour
{
    [Header("Target")]
    public Transform target;
    [Header("Bones (exact names; searched in children)")]
    public string headBoneName = "CC_Base_Head";
    public string neckBoneName = "CC_Base_NeckTwist01";
    [Header("Look Settings")]
    [Range(0f, 1f)]
    public float headWeight = 0.7f;
    [Range(0f, 1f)]
    public float neckWeight = 0.4f;
    public float maxAngle = 70f;
    public float trackSpeed = 5f;
    public float bodyYawOffset = 0f;
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
        if (!active || _head == null) 
        {
            return;
        }

        Transform t = target != null ? target : (Camera.main != null ? Camera.main.transform : null);

        if (t == null)
        {
            return;
        }

        if (!_initialised) 
        { 
            _headSmooth = _head.rotation; _initialised = true; 
        }

        Vector3 eyeLevel = new Vector3(t.position.x, _head.position.y, t.position.z);
        Vector3 toTarget = eyeLevel - _head.position;
        if (toTarget.sqrMagnitude < 0.0001f) 
        {
            return;
        }

        Vector3 toTargetFlat = new Vector3(toTarget.x, 0f, toTarget.z).normalized;
        Vector3 bodyFwd = Quaternion.Euler(0f, bodyYawOffset, 0f) * transform.forward;
        bodyFwd.y = 0f;
        bodyFwd.Normalize();

        float angleY    = Vector3.SignedAngle(bodyFwd, toTargetFlat, Vector3.up);
        float clamped   = Mathf.Clamp(angleY, -maxAngle, maxAngle);

        Quaternion animRot = _head.rotation;
        Quaternion desired = Quaternion.AngleAxis(clamped, Vector3.up) * animRot;

        _headSmooth = Quaternion.Slerp(_headSmooth, desired, Time.deltaTime * trackSpeed);
        _head.rotation = Quaternion.Slerp(animRot, _headSmooth, headWeight);

        if (_neck != null)
        {
            _neck.rotation = Quaternion.Slerp(_neck.rotation, _headSmooth, headWeight * neckWeight);
        }
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
