using System.Collections;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

public enum NPCRole { Student, Doctor }

/// <summary>
/// Controls a single historical NPC.
/// Uses PlayableGraph + AnimationMixerPlayable to crossfade smoothly between clips —
/// no Animator Controller required.
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

    [Tooltip("Doctor only: standing idle clip played AFTER the lecture ends.\n" +
             "If left empty, falls back to idleClip.")]
    public AnimationClip standingAfterLectureClip;

    [Tooltip("Doctor only: walking/pacing clip")]
    public AnimationClip walkingClip;

    [Header("Sitting Variation (Students only)")]
    [Tooltip("Pool of sitting clips to cycle through randomly while the student is seated.\n" +
             "Include your main idleClip here too if you want it in the rotation.\n" +
             "Leave empty to loop only idleClip with no switching.")]
    public AnimationClip[] sittingClipVariants;

    [Tooltip("Seconds each sitting clip plays before randomly switching to another.\n" +
             "A random ±20% jitter is applied so all students don't switch in unison.")]
    [Range(3f, 30f)]
    public float sittingSwitchInterval = 8f;

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

    [Header("Crossfade")]
    [Tooltip("Duration in seconds to blend from the current animation to the next.\n" +
             "0 = instant snap (old behaviour).  0.25 is a good starting point.\n" +
             "Applied to idle↔talking (doctor) and sitting variant switches (students).")]
    [Range(0f, 1f)]
    public float crossfadeDuration = 0.25f;

    // ── private ───────────────────────────────────────────────────
    private Animator _animator;
    private bool _isLecturing;
    private Transform _playerCamera;
    private Vector3 _paceOrigin;
    private Vector3 _paceRight;
    private float _pacePhase;
    private Vector3 _doctorLookAtPoint;
#pragma warning disable 0414
    private bool _hasLookAtPoint;
#pragma warning restore 0414

    // ── PlayableGraph with 2-slot mixer ──────────────────────────────────────
    // Slot 0 (weight 1) = currently active clip.
    // Slot 1 (weight 0) = incoming clip during a crossfade.
    // The graph is built once and reused across all clip transitions.
    private PlayableGraph          _graph;
    private AnimationMixerPlayable _mixer;
    private AnimationClipPlayable  _slotA;     // currently active
    private AnimationClipPlayable  _slotB;     // incoming (used during crossfade)
    private AnimationClip          _clipA;
    private AnimationClip          _clipB;
    private Coroutine              _fadeCoroutine;
    private bool                   _graphReady;

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
        _animator = GetComponent<Animator>() ?? GetComponentInChildren<Animator>();

        if (_animator != null && _animator.runtimeAnimatorController != null)
        {
            Debug.Log($"[NPC] {gameObject.name}: removing Animator Controller '{_animator.runtimeAnimatorController.name}' — PlayableGraph takes over.");
            _animator.runtimeAnimatorController = null;
        }

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

            _spineBone = FindDeepChild(transform, spineBoneName);
            _headBone  = FindDeepChild(transform, headBoneName);
            _neckBone  = FindDeepChild(transform, neckBoneName);

            breathPhaseOffset = Random.Range(0f, Mathf.PI * 2f);

            if (_headBone == null)
            {
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

            if (facingTarget.HasValue)
            {
                _headLookTarget    = facingTarget.Value;
                _hasHeadLookTarget = true;
            }
        }
        else if (role == NPCRole.Doctor)
        {
            if (facingTarget.HasValue)
            {
                _doctorLookAtPoint = facingTarget.Value;
                _hasLookAtPoint    = true;

                Vector3 dir = facingTarget.Value - transform.position;
                dir.y = 0;
                if (dir.sqrMagnitude > 0.001f)
                    transform.rotation = Quaternion.LookRotation(dir);
            }
        }

        if (GetComponent<NPCBlinking>() == null)
            gameObject.AddComponent<NPCBlinking>();

        CrossfadeTo(idleClip, "idle");
        Debug.Log($"[NPC] {gameObject.name} initialized as {role}");
    }

    // ── Update ────────────────────────────────────────────────────

    private void Update()
    {
        if (_graphReady)
        {
            LoopClipPlayable(ref _slotA, _clipA);

            // Only loop slot B while a crossfade is actually in progress.
            if (_mixer.IsValid() && _mixer.GetInputWeight(1) > 0f)
                LoopClipPlayable(ref _slotB, _clipB);
        }
    }

    private static void LoopClipPlayable(ref AnimationClipPlayable playable, AnimationClip clip)
    {
        if (!playable.IsValid() || clip == null || clip.length <= 0f) return;
        if ((float)playable.GetTime() >= clip.length)
            playable.SetTime(0f);
    }

    private void OnDestroy()
    {
        StopSittingVariation();
        if (_fadeCoroutine != null) StopCoroutine(_fadeCoroutine);
        if (_graph.IsValid()) _graph.Destroy();
    }

    // ── Doctor behaviour ──────────────────────────────────────────

    public void SetLecturing(bool lecturing)
    {
        if (role != NPCRole.Doctor) return;
        _isLecturing = lecturing;

        if (lecturing)
        {
            AnimationClip clip = talkingClip != null ? talkingClip : idleClip;
            CrossfadeTo(clip, "talking");
            Debug.Log($"[NPC] Doctor lecturing — crossfading to: {clip?.name}");
        }
        else
        {
            AnimationClip standClip = standingAfterLectureClip != null ? standingAfterLectureClip : idleClip;

            if (standingAfterLectureClip == null)
                Debug.LogWarning($"[NPC] {gameObject.name}: 'Standing After Lecture Clip' is not assigned — " +
                                 "falling back to Idle Clip.");

            CrossfadeTo(standClip, "standing idle");
            Debug.Log($"[NPC] Doctor lecture ended — crossfading to: {standClip?.name}");
        }
    }

    private void UpdateDoctorPacing()
    {
        _pacePhase += Time.deltaTime * paceSpeed;
        float xOffset = Mathf.Sin(_pacePhase) * paceDistance;
        Vector3 newPos = _paceOrigin + _paceRight * xOffset;
        transform.position = new Vector3(newPos.x, _paceOrigin.y, newPos.z);
    }

    // ── Head look-at (runs AFTER animation to override bone rotation) ──

    private void LateUpdate()
    {
        if (role != NPCRole.Student) return;
        ApplyBreathing();
        if (!_hasHeadLookTarget || _headBone == null) return;
        if (!_headInitialized)
        {
            _headSmoothRot   = _headBone.rotation;
            _headInitialized = true;
        }
        ApplyHeadLookAt();
    }

    private void ApplyBreathing()
    {
        if (_spineBone == null) return;
        float cyclesPerSecond = breathRate / 60f;
        float breath = Mathf.Sin(Time.time * cyclesPerSecond * Mathf.PI * 2f + breathPhaseOffset);
        float tiltX = breath * breathDepth;
        _spineBone.localRotation *= Quaternion.Euler(tiltX, 0f, 0f);
    }

    private void ApplyHeadLookAt()
    {
        Vector3 eyeLevelTarget = new Vector3(
            _headLookTarget.x,
            _headBone.position.y,
            _headLookTarget.z);

        Vector3 toTarget = eyeLevelTarget - _headBone.position;
        if (toTarget.sqrMagnitude < 0.001f) return;

        Vector3 toTargetFlat = new Vector3(toTarget.x, 0f, toTarget.z).normalized;
        Vector3 bodyFwd = new Vector3(transform.forward.x, 0f, transform.forward.z).normalized;
        float angleY = Vector3.SignedAngle(bodyFwd, toTargetFlat, Vector3.up);
        float clampedAngle = Mathf.Clamp(angleY, -headMaxAngle, headMaxAngle);

        Quaternion animRot = _headBone.rotation;
        Quaternion desired = Quaternion.AngleAxis(clampedAngle, Vector3.up) * animRot;
        _headSmoothRot = Quaternion.Slerp(_headSmoothRot, desired, Time.deltaTime * headLookSpeed);
        _headBone.rotation = Quaternion.Slerp(animRot, _headSmoothRot, headLookWeight);

        if (_neckBone != null)
            _neckBone.rotation = Quaternion.Slerp(_neckBone.rotation,
                                                   _headSmoothRot,
                                                   headLookWeight * 0.4f);
    }

    // ── Sitting variation ─────────────────────────────────────────

    private Coroutine _sittingSwitchCoroutine;

    /// <summary>
    /// Start randomly cycling between sitting poses every
    /// <see cref="sittingSwitchInterval"/> seconds (±20% jitter).
    ///
    /// Pool = idleClip  +  sittingClipVariants (duplicates removed).
    /// Requires at least 2 distinct clips in total — if only idleClip is set and
    /// sittingClipVariants is empty/null, nothing switches (same as before).
    ///
    /// Having just ONE entry in sittingClipVariants is enough: the character
    /// will alternate between idleClip and that variant.
    /// </summary>
    public void StartSittingVariation()
    {
        // Build the full pool: base idle clip + unique non-null variants.
        var pool = new System.Collections.Generic.List<AnimationClip>();
        if (idleClip != null) pool.Add(idleClip);
        if (sittingClipVariants != null)
            foreach (var c in sittingClipVariants)
                if (c != null && !pool.Contains(c)) pool.Add(c);

        if (pool.Count < 2)
        {
            Debug.Log($"[NPC] '{name}': sitting pool has < 2 distinct clips — no switching.");
            return;
        }

        if (_sittingSwitchCoroutine != null) StopCoroutine(_sittingSwitchCoroutine);
        _sittingSwitchCoroutine = StartCoroutine(SittingVariationLoop(pool));
        Debug.Log($"[NPC] '{name}': sitting variation started " +
                  $"({pool.Count} clips, every ~{sittingSwitchInterval:F0}s).");
    }

    /// <summary>Stop cycling and return to whatever clip is currently playing.</summary>
    public void StopSittingVariation()
    {
        if (_sittingSwitchCoroutine == null) return;
        StopCoroutine(_sittingSwitchCoroutine);
        _sittingSwitchCoroutine = null;
        Debug.Log($"[NPC] '{name}': sitting variation stopped.");
    }

    private IEnumerator SittingVariationLoop(System.Collections.Generic.List<AnimationClip> pool)
    {
        int lastIndex = 0;   // start at idleClip (index 0)

        while (true)
        {
            float jitter = sittingSwitchInterval * 0.2f;
            float wait   = sittingSwitchInterval + Random.Range(-jitter, jitter);
            yield return new WaitForSeconds(wait);

            int next;
            do { next = Random.Range(0, pool.Count); }
            while (next == lastIndex && pool.Count > 1);

            lastIndex = next;
            AnimationClip clip = pool[next];
            if (clip != null)
                CrossfadeTo(clip, $"sitting variant '{clip.name}'");
        }
    }

    // ── Graph construction ────────────────────────────────────────────────────

    /// <summary>
    /// Builds the persistent graph with a 2-input mixer.
    /// Slot 0 (weight 1) = initial clip.  Slot 1 (weight 0) = placeholder.
    /// Called once; subsequent clip changes go through CrossfadeTo().
    /// </summary>
    private void BuildGraph(AnimationClip initialClip)
    {
        if (_graph.IsValid()) _graph.Destroy();
        _graphReady = false;

        if (_animator == null || initialClip == null) return;

        _graph = PlayableGraph.Create($"NPC_{gameObject.name}");
        _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);

        _mixer = AnimationMixerPlayable.Create(_graph, 2);

        _slotA = MakePlayable(initialClip);
        _slotB = MakePlayable(initialClip);   // placeholder — weight 0
        _clipA = initialClip;
        _clipB = initialClip;

        _graph.Connect(_slotA, 0, _mixer, 0);
        _graph.Connect(_slotB, 0, _mixer, 1);
        _mixer.SetInputWeight(0, 1f);
        _mixer.SetInputWeight(1, 0f);

        var output = AnimationPlayableOutput.Create(_graph, "Animation", _animator);
        output.SetSourcePlayable(_mixer);

        _graph.Play();
        _graphReady = true;
        Debug.Log($"[NPC] {gameObject.name}: PlayableGraph built — initial clip '{initialClip.name}'.");
    }

    // ── Crossfade ─────────────────────────────────────────────────────────────

    private void CrossfadeTo(AnimationClip targetClip, string label)
    {
        if (_animator == null) return;
        if (targetClip == null)
        {
            Debug.LogWarning($"[NPC] {gameObject.name}: CrossfadeTo called with null clip — ignoring.");
            return;
        }

        // Build graph lazily on first call.
        if (!_graphReady)
        {
            BuildGraph(targetClip);
            Debug.Log($"[NPC] {gameObject.name} playing {label}: '{targetClip.name}'");
            return;
        }

        // Already playing this clip at full weight — nothing to do.
        if (targetClip == _clipA && _mixer.GetInputWeight(0) >= 0.99f)
            return;

        // Snap any in-progress fade to completion before starting a new one.
        if (_fadeCoroutine != null)
        {
            StopCoroutine(_fadeCoroutine);
            _fadeCoroutine = null;
            PromoteSlotB();
        }

        // Load the target clip into slot B.
        _graph.Disconnect(_mixer, 1);
        _slotB = MakePlayable(targetClip);
        _clipB = targetClip;
        _graph.Connect(_slotB, 0, _mixer, 1);
        _mixer.SetInputWeight(1, 0f);

        Debug.Log($"[NPC] {gameObject.name} crossfading → {label}: '{targetClip.name}'  " +
                  $"duration={crossfadeDuration:F2}s");

        if (crossfadeDuration <= 0f)
            PromoteSlotB();
        else
            _fadeCoroutine = StartCoroutine(FadeCoroutine());
    }

    private IEnumerator FadeCoroutine()
    {
        float elapsed = 0f;
        while (elapsed < crossfadeDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / crossfadeDuration);
            _mixer.SetInputWeight(0, 1f - t);
            _mixer.SetInputWeight(1, t);
            yield return null;
        }
        PromoteSlotB();
        _fadeCoroutine = null;
    }

    /// <summary>Swap slot B → slot A so the next crossfade always fades FROM A.</summary>
    private void PromoteSlotB()
    {
        _graph.Disconnect(_mixer, 0);
        _graph.Disconnect(_mixer, 1);

        _slotA = _slotB;
        _clipA = _clipB;

        _slotB = MakePlayable(_clipB);   // fresh placeholder

        _graph.Connect(_slotA, 0, _mixer, 0);
        _graph.Connect(_slotB, 0, _mixer, 1);
        _mixer.SetInputWeight(0, 1f);
        _mixer.SetInputWeight(1, 0f);
    }

    private AnimationClipPlayable MakePlayable(AnimationClip clip)
    {
        var p = AnimationClipPlayable.Create(_graph, clip);
        p.SetApplyFootIK(false);
        p.SetTime(0);
        p.Play();
        return p;
    }

    // ── Bone search helper ────────────────────────────────────────

    private Transform FindDeepChild(Transform parent, string boneName)
    {
        foreach (Transform child in parent.GetComponentsInChildren<Transform>())
        {
            if (string.Equals(child.name, boneName, System.StringComparison.OrdinalIgnoreCase))
                return child;
        }
        return null;
    }

    // ── Public graph control (used by LectureHallManager for Murad) ──────────

    /// <summary>
    /// Sets up the head look-at system WITHOUT modifying transform.rotation.
    /// Use this when the caller has already set the correct world rotation.
    /// If <paramref name="sittingClip"/> is supplied it is played immediately.
    /// </summary>
    public void InitHeadLookOnly(Vector3 initialLookTarget, AnimationClip sittingClip = null)
    {
        role          = NPCRole.Student;
        _playerCamera = Camera.main?.transform;
        _animator     = GetComponent<Animator>() ?? GetComponentInChildren<Animator>();

        _spineBone = FindDeepChild(transform, spineBoneName);
        _headBone  = FindDeepChild(transform, headBoneName);
        _neckBone  = FindDeepChild(transform, neckBoneName);

        breathPhaseOffset = Random.Range(0f, Mathf.PI * 2f);

        if (_headBone == null)
            Debug.LogWarning($"[NPC] {gameObject.name}: head bone '{headBoneName}' not found — head look-at disabled.");
        else
            Debug.Log($"[NPC] {gameObject.name} (head-look-only): Head='{_headBone.name}'  " +
                      $"Neck='{(_neckBone != null ? _neckBone.name : "NOT FOUND")}'");

        _headLookTarget    = initialLookTarget;
        _hasHeadLookTarget = true;
        _headInitialized   = false;

        if (sittingClip != null)
        {
            idleClip = sittingClip;
            CrossfadeTo(sittingClip, "sitting (head-look-only)");
        }
    }

    /// <summary>
    /// Crossfades to a different clip immediately.
    /// Pass null to stop the PlayableGraph (Animator Controller takes back over).
    /// </summary>
    public void SwitchToClip(AnimationClip clip)
    {
        if (clip == null) { StopPlayableGraph(); return; }
        CrossfadeTo(clip, $"SwitchToClip '{clip.name}'");
    }

    /// <summary>
    /// Stops and destroys the PlayableGraph so the Animator Controller can take over again.
    /// (Used for Murad's stand-up/walk phase.)
    /// </summary>
    public void StopPlayableGraph()
    {
        StopSittingVariation();
        if (_fadeCoroutine != null)
        {
            StopCoroutine(_fadeCoroutine);
            _fadeCoroutine = null;
        }
        if (_graph.IsValid())
        {
            _graph.Destroy();
            _graphReady = false;
            Debug.Log($"[NPC] {gameObject.name}: PlayableGraph stopped — Animator Controller takes over.");
        }
    }

    /// <summary>
    /// Freezes the active clip at a normalised time (0 = start, 1 = end).
    /// Any in-progress crossfade is snapped to completion first so the correct
    /// clip is frozen.  Used by LectureHallManager to hold Murad in his seated pose.
    /// </summary>
    public void FreezePlayableGraph(float holdNormalised = 0f)
    {
        if (!_graphReady || _clipA == null) return;

        // Snap any in-progress fade so we freeze the right clip.
        if (_fadeCoroutine != null)
        {
            StopCoroutine(_fadeCoroutine);
            _fadeCoroutine = null;
            PromoteSlotB();
        }

        float holdTime = holdNormalised * _clipA.length;
        _slotA.SetTime(holdTime);
        _slotA.SetSpeed(0f);
        Debug.Log($"[NPC] {gameObject.name}: PlayableGraph frozen at {holdNormalised:P0} ({holdTime:F2}s).");
    }

    /// <summary>
    /// Resumes a frozen PlayableGraph at normal speed so the stand-up portion plays.
    /// </summary>
    public void ResumePlayableGraph()
    {
        if (!_graphReady) return;
        _slotA.SetSpeed(1f);
        Debug.Log($"[NPC] {gameObject.name}: PlayableGraph resumed — stand-up animation playing.");
    }

    /// <summary>
    /// Returns how many seconds remain in the active clip after the frozen hold point.
    /// Used by LectureHallManager to wait exactly long enough for the stand-up to finish.
    /// </summary>
    public float GetRemainingPlayableTime(float holdNormalised)
    {
        if (!_graphReady || _clipA == null) return 0f;
        float elapsed   = holdNormalised * _clipA.length;
        float remaining = _clipA.length - elapsed;
        return Mathf.Max(0f, remaining);
    }

    public void SetHeadLookTarget(Vector3 worldPos)
    {
        _headLookTarget  = worldPos;
        _hasHeadLookTarget = true;
        _headInitialized = false;
    }

    /// <summary>Disables the head look-at override so the bone returns to its animated pose.</summary>
    public void ClearHeadLookTarget()
    {
        _hasHeadLookTarget = false;
        _headInitialized   = false;
    }
}
