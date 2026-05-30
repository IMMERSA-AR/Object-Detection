using UnityEngine;

/// <summary>
/// Gently pulls the character's wrists toward a lap resting position
/// after the animation has played.
///
/// Bone detection order:
///   1. Humanoid Avatar via Animator.GetBoneTransform  (works on any rig)
///   2. Mixamo name search  (mixamorig:LeftForeArm …)
///   3. CC4 name search     (CC_Base_L_ForearmTwist01 …)
///   4. Generic name search (LeftForeArm, RightForeArm …)
///
/// If no bones are found at all the component disables itself and logs
/// every bone name in the rig so you can identify the correct names.
/// </summary>
public class NPCHandRest : MonoBehaviour
{
    [Header("Lap Offset (relative to hip bone)")]
    [Tooltip("How far forward (+) or backward (-) from the hip the hands rest.")]
    public float lapForward    = 0.05f;

    [Tooltip("How far down from the hip the hands rest. Negative = below hip.")]
    public float lapDown       = -0.15f;

    [Tooltip("How far left / right from centre each hand rests.")]
    public float lapSideOffset = 0.12f;

    [Header("Blend")]
    [Tooltip("0 = pure animation pose.  1 = hands fully locked to lap target.\n" +
             "0.6 overrides a badly-posed animation without looking robotic.")]
    [Range(0f, 1f)]
    public float restWeight  = 0.6f;

    [Tooltip("Speed at which hands move toward the rest position.")]
    [Range(1f, 20f)]
    public float smoothSpeed = 6f;

    // ── private ───────────────────────────────────────────────────
    private Transform _leftForeArm;
    private Transform _rightForeArm;
    private Transform _leftHand;
    private Transform _rightHand;
    private Transform _hipBone;

    private Transform _leftTarget;
    private Transform _rightTarget;

    private Quaternion _leftSmooth;
    private Quaternion _rightSmooth;
    private bool _initialized;

    // ── Bone name fallback lists ──────────────────────────────────
    private static readonly string[] HipNames = {
        "mixamorig:Hips", "CC_Base_Hip", "Hips", "Hip", "pelvis", "Pelvis"
    };
    private static readonly string[] LeftForeArmNames = {
        "mixamorig:LeftForeArm", "CC_Base_L_ForearmTwist01",
        "LeftForeArm", "Left ForeArm", "ForeArmL", "lForeArm"
    };
    private static readonly string[] RightForeArmNames = {
        "mixamorig:RightForeArm", "CC_Base_R_ForearmTwist01",
        "RightForeArm", "Right ForeArm", "ForeArmR", "rForeArm"
    };
    private static readonly string[] LeftHandNames = {
        "mixamorig:LeftHand", "CC_Base_L_Hand",
        "LeftHand", "Left Hand", "HandL", "lHand"
    };
    private static readonly string[] RightHandNames = {
        "mixamorig:RightHand", "CC_Base_R_Hand",
        "RightHand", "Right Hand", "HandR", "rHand"
    };

    // ─────────────────────────────────────────────────────────────

    private void Start()
    {
        FindBones();

        if (_hipBone == null)
        {
            // Print every bone in the rig so the user can identify correct names
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[NPCHandRest] {gameObject.name}: could not find hip bone. All bones:");
            foreach (Transform t in GetComponentsInChildren<Transform>())
                sb.AppendLine($"  '{t.name}'");
            Debug.LogWarning(sb.ToString());

            enabled = false;
            return;
        }

        // Log which bones were found for debugging
        Debug.Log($"[NPCHandRest] {gameObject.name}: " +
                  $"Hip={_hipBone?.name}  " +
                  $"LForeArm={_leftForeArm?.name}  RForeArm={_rightForeArm?.name}  " +
                  $"LHand={_leftHand?.name}  RHand={_rightHand?.name}");

        _leftTarget  = CreateLapTarget("_LeftLapTarget",  -lapSideOffset);
        _rightTarget = CreateLapTarget("_RightLapTarget",  lapSideOffset);
    }

    // ── Bone Detection ────────────────────────────────────────────

    private void FindBones()
    {
        // ── Method 1: Humanoid Avatar (most reliable, rig-agnostic) ──
        Animator anim = GetComponentInChildren<Animator>();
        if (anim != null && anim.isHuman)
        {
            _leftForeArm  = anim.GetBoneTransform(HumanBodyBones.LeftLowerArm);
            _rightForeArm = anim.GetBoneTransform(HumanBodyBones.RightLowerArm);
            _leftHand     = anim.GetBoneTransform(HumanBodyBones.LeftHand);
            _rightHand    = anim.GetBoneTransform(HumanBodyBones.RightHand);
            _hipBone      = anim.GetBoneTransform(HumanBodyBones.Hips);

            if (_hipBone != null)
            {
                Debug.Log($"[NPCHandRest] {gameObject.name}: bones found via Humanoid Avatar.");
                return;
            }
        }

        // ── Method 2: Name-based search (Mixamo / CC4 / generic) ──
        Transform[] all = GetComponentsInChildren<Transform>();

        _hipBone      = FindByNames(all, HipNames);
        _leftForeArm  = FindByNames(all, LeftForeArmNames);
        _rightForeArm = FindByNames(all, RightForeArmNames);
        _leftHand     = FindByNames(all, LeftHandNames);
        _rightHand    = FindByNames(all, RightHandNames);

        if (_hipBone != null)
            Debug.Log($"[NPCHandRest] {gameObject.name}: bones found via name search.");
    }

    /// Returns the first Transform whose name matches any entry in <paramref name="names"/>.
    private static Transform FindByNames(Transform[] all, string[] names)
    {
        foreach (string candidate in names)
            foreach (Transform t in all)
                if (string.Equals(t.name, candidate, System.StringComparison.OrdinalIgnoreCase))
                    return t;
        return null;
    }

    // ── Lap Target Creation ───────────────────────────────────────

    private Transform CreateLapTarget(string targetName, float sideOffset)
    {
        GameObject go = new GameObject(targetName);
        go.transform.SetParent(_hipBone, worldPositionStays: false);
        go.transform.localPosition = new Vector3(sideOffset, lapDown, lapForward);
        go.transform.localRotation = Quaternion.identity;
        return go.transform;
    }

    // ── LateUpdate ────────────────────────────────────────────────

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

    private void PullArm(Transform foreArm, Transform hand,
                         Transform target,  ref Quaternion smoothRot)
    {
        if (foreArm == null || hand == null || target == null) return;

        Vector3 elbowToWrist  = (hand.position   - foreArm.position).normalized;
        Vector3 elbowToTarget = (target.position  - foreArm.position).normalized;

        if (elbowToTarget.sqrMagnitude < 0.001f) return;

        Quaternion animRot    = foreArm.rotation;
        Quaternion desiredRot = Quaternion.FromToRotation(elbowToWrist, elbowToTarget) * animRot;

        smoothRot        = Quaternion.Slerp(smoothRot, desiredRot, Time.deltaTime * smoothSpeed);
        foreArm.rotation = Quaternion.Slerp(animRot,  smoothRot,  restWeight);
    }
}
