using UnityEngine;

/// <summary>
/// Gently pulls the character's wrists toward a lap resting position
/// after the animation has played. Fully automatic — no target GameObjects
/// needed in the scene or prefab. The script locates the hip bone at runtime
/// and creates its own lap targets as children.
///
/// Attach to the same GameObject as HistoricalNPCController.
/// Only needs bone names set correctly in the Inspector.
/// </summary>
public class NPCHandRest : MonoBehaviour
{
    [Header("Bone Names — Mixamo defaults")]
    [Tooltip("Mixamo: mixamorig:LeftForeArm   CC4: CC_Base_L_ForearmTwist01")]
    public string leftForeArmBone  = "mixamorig:LeftForeArm";

    [Tooltip("Mixamo: mixamorig:RightForeArm  CC4: CC_Base_R_ForearmTwist01")]
    public string rightForeArmBone = "mixamorig:RightForeArm";

    [Tooltip("Mixamo: mixamorig:LeftHand   CC4: CC_Base_L_Hand")]
    public string leftHandBone  = "mixamorig:LeftHand";

    [Tooltip("Mixamo: mixamorig:RightHand  CC4: CC_Base_R_Hand")]
    public string rightHandBone = "mixamorig:RightHand";

    [Tooltip("Mixamo: mixamorig:Hips   CC4: CC_Base_Hip")]
    public string hipBoneName = "mixamorig:Hips";

    [Header("Lap Offset (relative to hip bone)")]
    [Tooltip("How far forward from the hip the hands rest (metres).\n" +
             "Positive = in front of the character.")]
    public float lapForward = 0.05f;

    [Tooltip("How far down from the hip the hands rest (metres).\n" +
             "Negative = below the hip.")]
    public float lapDown = -0.15f;

    [Tooltip("How far left/right from centre each hand rests (metres).")]
    public float lapSideOffset = 0.12f;

    [Header("Blend")]
    [Tooltip("0 = full animation, 1 = hands locked to lap.\n" +
             "0.45 gives a natural settled look without overriding the animation.")]
    [Range(0f, 1f)] public float restWeight = 0.45f;

    [Tooltip("Speed at which hands move toward rest position.")]
    [Range(1f, 20f)] public float smoothSpeed = 5f;

    // ── private ───────────────────────────────────────────────────
    private Transform _leftForeArm;
    private Transform _rightForeArm;
    private Transform _leftHand;
    private Transform _rightHand;
    private Transform _hipBone;

    // Auto-created lap target transforms (children of this GameObject)
    private Transform _leftTarget;
    private Transform _rightTarget;

    // Per-hand smooth rotations
    private Quaternion _leftSmooth;
    private Quaternion _rightSmooth;
    private bool _initialized;

    private void Start()
    {
        // Find bones
        foreach (Transform t in GetComponentsInChildren<Transform>())
        {
            string n = t.name;
            if (n == leftForeArmBone)  _leftForeArm  = t;
            if (n == rightForeArmBone) _rightForeArm = t;
            if (n == leftHandBone)     _leftHand      = t;
            if (n == rightHandBone)    _rightHand     = t;
            if (n == hipBoneName)      _hipBone       = t;
        }

        if (_hipBone == null)
        {
            Debug.LogWarning($"[NPCHandRest] {gameObject.name}: hip bone '{hipBoneName}' not found. " +
                             "Hand rest disabled. Check bone name in Inspector.");
            enabled = false;
            return;
        }

        // Create lap target transforms as children — they follow the hip bone
        _leftTarget  = CreateLapTarget("_LeftLapTarget",  -lapSideOffset);
        _rightTarget = CreateLapTarget("_RightLapTarget",  lapSideOffset);
    }

    /// <summary>
    /// Creates an empty child transform positioned at lap height
    /// relative to the hip bone.
    /// </summary>
    private Transform CreateLapTarget(string targetName, float sideOffset)
    {
        GameObject go = new GameObject(targetName);

        // Parent to hip bone so the target follows the seated character
        go.transform.SetParent(_hipBone, worldPositionStays: false);

        // Local position: forward, down, and to the side
        go.transform.localPosition = new Vector3(sideOffset, lapDown, lapForward);
        go.transform.localRotation = Quaternion.identity;

        return go.transform;
    }

    private void LateUpdate()
    {
        if (!_initialized)
        {
            if (_leftForeArm  != null) _leftSmooth  = _leftForeArm.rotation;
            if (_rightForeArm != null) _rightSmooth = _rightForeArm.rotation;
            _initialized = true;
        }

        PullArm(_leftForeArm,  _leftHand,  _leftTarget,  ref _leftSmooth);
        PullArm(_rightForeArm, _rightHand, _rightTarget, ref _rightSmooth);
    }

    /// <summary>
    /// Rotates the forearm so the wrist moves toward the lap target,
    /// then blends with the animated rotation using restWeight.
    /// </summary>
    private void PullArm(Transform foreArm, Transform hand,
                         Transform target, ref Quaternion smoothRot)
    {
        if (foreArm == null || hand == null || target == null) return;

        Vector3 elbowToWrist  = (hand.position   - foreArm.position).normalized;
        Vector3 elbowToTarget = (target.position  - foreArm.position).normalized;

        if (elbowToTarget.sqrMagnitude < 0.001f) return;

        Quaternion animRot    = foreArm.rotation;
        Quaternion desiredRot = Quaternion.FromToRotation(elbowToWrist, elbowToTarget) * animRot;

        // Smooth toward desired
        smoothRot = Quaternion.Slerp(smoothRot, desiredRot, Time.deltaTime * smoothSpeed);

        // Blend between animation and smoothed target
        foreArm.rotation = Quaternion.Slerp(animRot, smoothRot, restWeight);
    }
}
