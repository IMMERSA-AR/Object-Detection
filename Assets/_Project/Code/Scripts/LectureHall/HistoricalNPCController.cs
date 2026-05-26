using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

public enum NPCRole { Student, Doctor }

/// <summary>
/// Controls a single historical NPC.
/// Uses PlayableGraph to drive animation clips directly — no Animator Controller required.
/// Assign Mixamo animation clips in the Inspector.
/// </summary>
public class HistoricalNPCController : MonoBehaviour
{
    [Header("Role")]
    public NPCRole role;

    [Header("Animation Clips — drag from your Mixamo FBX")]
    [Tooltip("For students: sitting idle clip. For doctor: standing idle clip.")]
    public AnimationClip idleClip;

    [Tooltip("Doctor only: the talking/lecturing animation clip")]
    public AnimationClip talkingClip;

    [Tooltip("Doctor only: walking/pacing clip")]
    public AnimationClip walkingClip;

    [Header("Doctor Pacing")]
    public float paceDistance = 0.6f;
    public float paceSpeed = 0.5f;

    [Header("Look At Player")]
    public float lookSpeed = 1.2f;
    [Tooltip("Students always face player. Doctor uses this angle limit.")]
    public float maxLookAngle = 45f;

    [Header("Breathing (Students only)")]
    [Tooltip("Exact name of the spine/chest bone to drive breathing.\n" +
             "CC4 characters: CC_Base_Spine01\nMixamo characters: mixamorig:Spine")]
    public string spineBoneName = "mixamorig:Spine";

    [Tooltip("How much the spine rocks forward/back while breathing (degrees).")]
    [Range(0f, 3f)] public float breathDepth = 1.2f;

    [Tooltip("Breaths per minute — 12 is natural resting, 16 is slightly alert.")]
    [Range(8f, 20f)] public float breathRate = 13f;

    [Tooltip("Random offset so all students don't breathe in sync.")]
    [Range(0f, 6.28f)] public float breathPhaseOffset = 0f;

    [Header("Head Look-At (Students only)")]
    [Tooltip("Exact name of the head bone in the character rig.\n" +
             "CC4 characters: CC_Base_Head\nMixamo characters: mixamorig:Head")]
    public string headBoneName = "CC_Base_Head";

    [Tooltip("Exact name of the neck bone in the character rig.\n" +
             "CC4 characters: CC_Base_NeckTwist01\nMixamo characters: mixamorig:Neck")]
    public string neckBoneName = "CC_Base_NeckTwist01";

    [Tooltip("How strongly the head turns toward the doctor.\n0 = no turn, 1 = full rotation override.")]
    [Range(0f, 1f)]
    public float headLookWeight = 0.7f;

    [Tooltip("Maximum angle from the body's forward direction the head will turn (degrees).\n" +
             "Beyond this the head stays in its animated pose.")]
    public float headMaxAngle = 70f;

    [Tooltip("Speed at which the head smoothly tracks the target.")]
    public float headLookSpeed = 4f;

    // ── private ───────────────────────────────────────────────────
    private Animator _animator;
    private bool _isLecturing;
    private Transform _playerCamera;
    private Vector3 _paceOrigin;
    private Vector3 _paceRight;
    private float _pacePhase;
    private Vector3 _doctorLookAtPoint; // world-space center of student area
#pragma warning disable 0414          // assigned by Init but kept for future doctor look-at use
    private bool _hasLookAtPoint;
#pragma warning restore 0414

    private PlayableGraph _graph;
    private AnimationClipPlayable _activePlayable;
    private AnimationClip _activeClip;

    // Breathing
    private Transform _spineBone;

    // Head bone IK
    private Transform _headBone;
    private Transform _neckBone;
    private Vector3 _headLookTarget;
    private bool _hasHeadLookTarget;
    private Quaternion _headSmoothRot;
    private bool _headInitialized;

    public NPCRole Role => role;

    // ── Init ──────────────────────────────────────────────────────

    /// <param name="facingTarget">Optional world position to face at spawn.
    /// If null, students face the camera. Pass the doctor's position for chair-based spawning.</param>
    public void Init(NPCRole assignedRole, Vector3? facingTarget = null)
    {
        role = assignedRole;
        _playerCamera = Camera.main?.transform;
        // Search root first, then children — Mixamo/CC4 characters put Animator on the root,
        // but some rigs nest it one level down (e.g. an armature child object).
        _animator = GetComponent<Animator>() ?? GetComponentInChildren<Animator>();
        _paceOrigin = transform.position;
        _paceRight = transform.right;

        if (role == NPCRole.Student)
        {
            Vector3 target = facingTarget
                ?? (_playerCamera != null ? _playerCamera.position : transform.position + Vector3.forward);
            Vector3 dir = target - transform.position;
            dir.y = 0;
            if (dir.sqrMagnitude > 0.001f)
                transform.rotation = Quaternion.LookRotation(dir);

            // Cache spine, head & neck bones for LateUpdate breathing + look-at
            _spineBone = FindDeepChild(transform, spineBoneName);
            _headBone  = FindDeepChild(transform, headBoneName);
            _neckBone  = FindDeepChild(transform, neckBoneName);

            // Randomise breath phase so all students don't breathe in sync
            breathPhaseOffset = Random.Range(0f, Mathf.PI * 2f);

            if (_headBone == null)
            {
                // Print every bone name so you can find the correct one
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[NPC] {gameObject.name}: 'Head' bone not found. All bones in rig:");
                foreach (Transform t in GetComponentsInChildren<Transform>())
                    sb.AppendLine($"  {t.name}");
                Debug.LogWarning(sb.ToString());
            }
            else
            {
                Debug.Log($"[NPC] {gameObject.name}: Head bone = '{_headBone.name}'  " +
                          $"Neck bone = '{(_neckBone != null ? _neckBone.name : "NOT FOUND")}'");
            }

            // Store the doctor position as the look-at target
            if (facingTarget.HasValue)
            {
                _headLookTarget = facingTarget.Value;
                _hasHeadLookTarget = true;
            }
        }
        else if (role == NPCRole.Doctor)
        {
            if (facingTarget.HasValue)
            {
                // Store the student area center — doctor always looks at this point
                _doctorLookAtPoint = facingTarget.Value;
                _hasLookAtPoint = true;

                // Snap rotation immediately toward students at spawn
                Vector3 dir = facingTarget.Value - transform.position;
                dir.y = 0;
                if (dir.sqrMagnitude > 0.001f)
                    transform.rotation = Quaternion.LookRotation(dir);
            }
        }

        PlayClip(idleClip);
        Debug.Log($"[NPC] {gameObject.name} initialized as {role}");
    }

    // ── Animation via PlayableGraph ───────────────────────────────
    // This works without any Animator Controller in the Inspector.
    // The clip plays directly on the character's rig via its Avatar.

    private void PlayClip(AnimationClip clip)
    {
        if (_animator == null)
        {
            Debug.LogWarning($"[NPC] {gameObject.name}: no Animator component found.");
            return;
        }
        if (clip == null)
        {
            Debug.LogWarning($"[NPC] {gameObject.name}: clip is null — assign it in the Inspector.");
            return;
        }

        if (_graph.IsValid())
            _graph.Destroy();

        _graph = PlayableGraph.Create($"NPC_{gameObject.name}");
        _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);

        _activePlayable = AnimationClipPlayable.Create(_graph, clip);
        _activePlayable.SetApplyFootIK(false);
        _activeClip = clip;

        var output = AnimationPlayableOutput.Create(_graph, "Animation", _animator);
        output.SetSourcePlayable(_activePlayable);

        _graph.Play();
        Debug.Log($"[NPC] {gameObject.name} playing: {clip.name}");
    }

    // ── Update ────────────────────────────────────────────────────

    private void Update()
    {
        LoopActiveClip();

        if (role == NPCRole.Doctor && _isLecturing)
        {
            UpdateDoctorPacing();
        }
        // Doctor rotation is locked at spawn — never updated toward the user
    }

    // Keep current animation looping — PlayableGraph plays once by default
    private void LoopActiveClip()
    {
        if (!_graph.IsValid() || _activeClip == null) return;
        if (_activeClip.length <= 0f) return;

        float t = (float)_activePlayable.GetTime();
        if (t >= _activeClip.length)
            _activePlayable.SetTime(0f);
    }

    // ── Doctor behaviour ──────────────────────────────────────────

    public void SetLecturing(bool lecturing)
    {
        if (role != NPCRole.Doctor) return;
        _isLecturing = lecturing;

        if (lecturing)
        {
            AnimationClip clip = talkingClip != null ? talkingClip : idleClip;
            PlayClip(clip);
            Debug.Log($"[NPC] Doctor lecturing — playing: {clip?.name}");
        }
        else
        {
            transform.position = _paceOrigin;
            PlayClip(idleClip);
        }
    }

    private void UpdateDoctorPacing()
    {
        _pacePhase += Time.deltaTime * paceSpeed;
        float xOffset = Mathf.Sin(_pacePhase) * paceDistance;

        // Only move position left/right — rotation stays locked at spawn direction
        Vector3 newPos = _paceOrigin + _paceRight * xOffset;
        transform.position = new Vector3(newPos.x, _paceOrigin.y, newPos.z);
    }


    // ── Head look-at (runs AFTER animation to override bone rotation) ──

    private void LateUpdate()
    {
        if (role != NPCRole.Student) return;

        // ── Breathing ─────────────────────────────────────────────
        ApplyBreathing();

        // ── Head look-at ──────────────────────────────────────────
        if (!_hasHeadLookTarget || _headBone == null) return;

        if (!_headInitialized)
        {
            _headSmoothRot = _headBone.rotation;
            _headInitialized = true;
        }

        ApplyHeadLookAt();
    }

    private void ApplyBreathing()
    {
        if (_spineBone == null) return;

        // Sine wave: breathRate breaths/min → cycles per second = breathRate/60
        float cyclesPerSecond = breathRate / 60f;
        float breath = Mathf.Sin(Time.time * cyclesPerSecond * Mathf.PI * 2f + breathPhaseOffset);

        // Tilt spine slightly forward on inhale, back on exhale
        float tiltX = breath * breathDepth;

        // Apply on top of the animation's current bone rotation
        _spineBone.localRotation *= Quaternion.Euler(tiltX, 0f, 0f);
    }

    private void ApplyHeadLookAt()
    {
        // Look at doctor's eye level (same Y as head) — avoids looking down
        // because the doctor's stored position is at floor height.
        Vector3 eyeLevelTarget = new Vector3(
            _headLookTarget.x,
            _headBone.position.y,
            _headLookTarget.z);

        Vector3 toTarget = eyeLevelTarget - _headBone.position;
        if (toTarget.sqrMagnitude < 0.001f) return;

        Vector3 toTargetFlat = new Vector3(toTarget.x, 0f, toTarget.z).normalized;
        Vector3 bodyFwd = new Vector3(transform.forward.x, 0f, transform.forward.z).normalized;

        // Signed horizontal angle from body forward to target direction.
        // SignedAngle returns [-180, 180] — safe for any orientation, no flip risk.
        float angleY = Vector3.SignedAngle(bodyFwd, toTargetFlat, Vector3.up);

        // Clamp to headMaxAngle so students facing sideways/away only turn
        // their head as far as is natural, never snapping backwards.
        float clampedAngle = Mathf.Clamp(angleY, -headMaxAngle, headMaxAngle);

        // Capture the animation's bone rotation this frame (before our override).
        Quaternion animRot = _headBone.rotation;

        // Apply a pure world-Y rotation on top of the animated rotation.
        // AngleAxis(angle, up) never produces flips — it is just a horizontal pivot.
        Quaternion desired = Quaternion.AngleAxis(clampedAngle, Vector3.up) * animRot;

        // Smooth toward desired over time
        _headSmoothRot = Quaternion.Slerp(_headSmoothRot, desired,
                                          Time.deltaTime * headLookSpeed);

        // Blend between pure animation and look-at
        _headBone.rotation = Quaternion.Slerp(animRot, _headSmoothRot, headLookWeight);

        // Neck gets 30% of the turn for a natural two-joint look
        if (_neckBone != null)
            _neckBone.rotation = Quaternion.Slerp(_neckBone.rotation,
                                                   _headSmoothRot,
                                                   headLookWeight * 0.4f);
    }

    // ── Bone search helper ────────────────────────────────────────

    /// <summary>
    /// Searches the entire child hierarchy for a Transform whose name
    /// contains <paramref name="boneName"/> (case-insensitive).
    /// Mixamo rigs typically name bones "mixamorig:Head", "mixamorig:Neck", etc.
    /// </summary>
    private Transform FindDeepChild(Transform parent, string boneName)
    {
        foreach (Transform child in parent.GetComponentsInChildren<Transform>())
        {
            // Exact match first (handles CC4 names like CC_Base_NeckTwist01)
            if (string.Equals(child.name, boneName, System.StringComparison.OrdinalIgnoreCase))
                return child;
        }
        return null;
    }

    /// <summary>
    /// Updates the head look-at target after spawn.
    /// Call this once the doctor's world position is known so students
    /// turn their heads toward the doctor rather than the user.
    /// </summary>
    /// <summary>
    /// Sets up the head look-at system WITHOUT modifying transform.rotation.
    /// Use this when the caller has already set the correct world rotation
    /// (e.g. the main Murad prefab, whose flip is applied at Instantiate time).
    /// <para>
    /// If <paramref name="sittingClip"/> is supplied, it is played immediately via
    /// PlayableGraph — giving Murad the same animation as the other students while
    /// his Animator Controller is kept intact for the stand-up / walk phase later.
    /// When <paramref name="sittingClip"/> is null the Animator Controller drives
    /// the animation (IsSitting bool must be set on it by the caller).
    /// </para>
    /// </summary>
    public void InitHeadLookOnly(Vector3 initialLookTarget, AnimationClip sittingClip = null)
    {
        role = NPCRole.Student;          // enables LateUpdate head look-at
        _playerCamera = Camera.main?.transform;
        // Search root first, then children (same approach as Init).
        _animator = GetComponent<Animator>() ?? GetComponentInChildren<Animator>();

        _spineBone = FindDeepChild(transform, spineBoneName);
        _headBone  = FindDeepChild(transform, headBoneName);
        _neckBone  = FindDeepChild(transform, neckBoneName);

        breathPhaseOffset = Random.Range(0f, Mathf.PI * 2f);

        if (_headBone == null)
            Debug.LogWarning($"[NPC] {gameObject.name}: head bone '{headBoneName}' not found — head look-at disabled.");
        else
            Debug.Log($"[NPC] {gameObject.name} (head-look-only): Head='{_headBone.name}'  " +
                      $"Neck='{(_neckBone != null ? _neckBone.name : "NOT FOUND")}'");

        _headLookTarget = initialLookTarget;
        _hasHeadLookTarget = true;
        _headInitialized = false;

        // If a sitting clip was provided, drive it via PlayableGraph so Murad
        // looks identical to the other students while seated.
        // When no clip is provided, the Animator Controller handles animation.
        if (sittingClip != null)
        {
            idleClip = sittingClip;
            PlayClip(sittingClip);
            Debug.Log($"[NPC] {gameObject.name} (head-look-only): playing sitting clip '{sittingClip.name}' via PlayableGraph.");
        }
    }

    /// <summary>
    /// Switches the active PlayableGraph to play a different clip immediately.
    /// Destroys the current graph (if any) and creates a new one with <paramref name="clip"/>.
    /// Use this to transition Murad between sitting / standing / walking clips without
    /// touching the Animator Controller.
    /// Pass null to stop the current graph and release the Animator back to its controller.
    /// </summary>
    public void SwitchToClip(AnimationClip clip)
    {
        if (clip == null) { StopPlayableGraph(); return; }
        PlayClip(clip);   // PlayClip already destroys the previous graph first
    }

    /// <summary>
    /// Stops and destroys the PlayableGraph so the Animator Controller can take
    /// over again. Call this before re-enabling the Animator Controller on Murad
    /// (e.g. when he stands up and the MuradController / walk animation begins).
    /// No-op if no graph is currently running.
    /// </summary>
    public void StopPlayableGraph()
    {
        if (_graph.IsValid())
        {
            _graph.Destroy();
            Debug.Log($"[NPC] {gameObject.name}: PlayableGraph stopped — Animator Controller takes over.");
        }
    }

    /// <summary>
    /// Freezes the currently playing clip at the given normalised time (0–1).
    /// Use this to hold Murad in his fully-seated pose during the lecture when
    /// the clip contains both a sit-down AND a stand-up section.
    /// Call ResumePlayableGraph() when the lecture ends to continue the animation.
    /// </summary>
    /// <param name="holdNormalised">0 = clip start, 1 = clip end.
    /// 0.5 = halfway through. Set to the frame where the character is fully seated.</param>
    public void FreezePlayableGraph(float holdNormalised = 0f)
    {
        if (!_graph.IsValid() || _activeClip == null) return;
        float holdTime = holdNormalised * _activeClip.length;
        _activePlayable.SetTime(holdTime);
        _activePlayable.SetSpeed(0f);   // freeze — no further playback
        Debug.Log($"[NPC] {gameObject.name}: PlayableGraph frozen at {holdNormalised:P0} ({holdTime:F2}s).");
    }

    /// <summary>
    /// Resumes a frozen PlayableGraph at normal speed so the stand-up portion plays.
    /// Call this when the lecture ends and Murad should begin standing up.
    /// </summary>
    public void ResumePlayableGraph()
    {
        if (!_graph.IsValid()) return;
        _activePlayable.SetSpeed(1f);
        Debug.Log($"[NPC] {gameObject.name}: PlayableGraph resumed — stand-up animation playing.");
    }

    /// <summary>
    /// Returns how many seconds remain in the active clip after the frozen hold point.
    /// Used by LectureHallManager to wait exactly long enough for the stand-up to finish.
    /// Returns 0 if no graph is running or clip is unknown.
    /// </summary>
    public float GetRemainingPlayableTime(float holdNormalised)
    {
        if (!_graph.IsValid() || _activeClip == null) return 0f;
        float elapsed  = holdNormalised * _activeClip.length;
        float remaining = _activeClip.length - elapsed;
        return Mathf.Max(0f, remaining);
    }

    public void SetHeadLookTarget(Vector3 worldPos)
    {
        _headLookTarget = worldPos;
        _hasHeadLookTarget = true;
        // Reset smooth rotation so it re-initialises toward the new target
        _headInitialized = false;
    }

    /// <summary>
    /// Disables the head look-at override so the bone returns to its animated pose.
    /// Call this when the character stands up or no longer needs to track a target.
    /// </summary>
    public void ClearHeadLookTarget()
    {
        _hasHeadLookTarget = false;
        _headInitialized = false;
    }

    private void OnDestroy()
    {
        if (_graph.IsValid())
            _graph.Destroy();
    }
}
