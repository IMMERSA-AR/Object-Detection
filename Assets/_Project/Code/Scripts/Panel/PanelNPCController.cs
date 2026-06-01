using System.Collections;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

/// <summary>
/// Lightweight NPC controller for panel-scene characters (e.g. Mourad).
/// Add this component directly to the character PREFAB in the Inspector.
///
/// Two animations only:
///   • Standing idle  — plays automatically on spawn, and after narration ends.
///   • Talking        — played by PanelDetector while narration audio is active.
///
/// Uses PlayableGraph + AnimationMixerPlayable to crossfade smoothly between clips —
/// no Animator Controller asset required.
///
/// In LateUpdate, the head and eye bones are rotated toward the user's eye level
/// (camera position) on top of whatever the animation is doing, giving the impression
/// that the character is making eye contact.
///
/// ── Prefab Setup ──────────────────────────────────────────────────────────
///  1. Add this component to the character prefab root.
///  2. Assign Idle Clip  / Talking Clip in the Inspector (or leave empty and inject
///     them at runtime via Init() if you reuse the same prefab across scenes).
///  3. Set the correct bone names for Head, Neck, Left Eye, Right Eye
///     (defaults match CC4 rigs; see tooltips for Mixamo alternatives).
///  4. The Animator component needs a Humanoid Avatar assigned.
/// </summary>
[DisallowMultipleComponent]
public class PanelNPCController : MonoBehaviour
{
    [Header("Animation Clips")]
    [Tooltip("Looping standing-idle clip — plays on spawn and after narration ends.")]
    public AnimationClip idleClip;

    [Tooltip("Talking clip — plays while narration audio is active.\n" +
             "Falls back to idle clip if left empty.")]
    public AnimationClip talkingClip;

    [Header("Crossfade")]
    [Tooltip("Duration in seconds to blend from the current animation to the next.\n" +
             "0 = instant snap.  0.25 is a good starting point.")]
    [Range(0f, 1f)]
    public float crossfadeDuration = 0.25f;

    // ── Look-At ───────────────────────────────────────────────────────────────
    [Header("Head & Eye Look-At")]
    [Tooltip("Uncheck to disable all look-at overrides (useful for debugging).")]
    public bool enableLookAt = true;

    [Tooltip("Head bone name.\n" +
             "CC4 rigs  : CC_Base_Head\n" +
             "Mixamo rigs: mixamorig:Head")]
    public string headBoneName = "CC_Base_Head";

    [Tooltip("Neck bone name — gets a partial share of the head turn for realism.\n" +
             "CC4 rigs  : CC_Base_NeckTwist01\n" +
             "Mixamo rigs: mixamorig:Neck")]
    public string neckBoneName = "CC_Base_NeckTwist01";

    [Tooltip("Left eye bone name.\n" +
             "CC4 rigs  : CC_Base_L_Eye\n" +
             "Mixamo rigs: mixamorig:LeftEye")]
    public string leftEyeBoneName = "CC_Base_L_Eye";

    [Tooltip("Right eye bone name.\n" +
             "CC4 rigs  : CC_Base_R_Eye\n" +
             "Mixamo rigs: mixamorig:RightEye")]
    public string rightEyeBoneName = "CC_Base_R_Eye";

    [Space]
    [Tooltip("How strongly the head turns toward the user.\n" +
             "0 = no head turn, 1 = full rotation to face the user.\n" +
             "0.5 is a subtle, natural-feeling turn.")]
    [Range(0f, 1f)]
    public float headLookWeight = 0.5f;

    [Tooltip("Maximum head turn in degrees before clamping.\n" +
             "Keep below 60 or the neck looks strained.")]
    [Range(0f, 90f)]
    public float maxHeadLookAngle = 50f;

    [Tooltip("How quickly the head rotation smoothly tracks the user.")]
    [Range(1f, 15f)]
    public float headLookSpeed = 4f;

    [Space]
    [Tooltip("How strongly the eyes rotate toward the user.\n" +
             "1 = eyes always point directly at the camera (full tracking).")]
    [Range(0f, 1f)]
    public float eyeLookWeight = 1f;

    [Tooltip("Maximum eye rotation from the animated forward direction, in degrees.\n" +
             "Keep at or below 30 — beyond that the eyeball visibly slides off the face.")]
    [Range(0f, 40f)]
    public float maxEyeLookAngle = 25f;

    [Tooltip("How quickly the eye rotation smoothly tracks the user.\n" +
             "Eyes should respond faster than the head (8–12 feels natural).")]
    [Range(1f, 20f)]
    public float eyeLookSpeed = 10f;

    // ── Private — animation ───────────────────────────────────────────────────
    private Animator _animator;

    private PlayableGraph            _graph;
    private AnimationMixerPlayable   _mixer;
    private AnimationClipPlayable    _slotA;
    private AnimationClipPlayable    _slotB;
    private AnimationClip            _clipA;
    private AnimationClip            _clipB;
    private Coroutine                _fadeCoroutine;
    private bool                     _graphReady;

    // ── Private — look-at ────────────────────────────────────────────────────
    private Transform  _camera;
    private Transform  _headBone;
    private Transform  _neckBone;
    private Transform  _leftEyeBone;
    private Transform  _rightEyeBone;
    private Quaternion _headSmoothRot;
    private Quaternion _eyeSmoothRot;
    private bool       _lookAtInitialized;

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    private void Awake()
    {
        _animator = GetComponent<Animator>() ?? GetComponentInChildren<Animator>();

        if (_animator == null)
            Debug.LogWarning($"[PanelNPCController] '{name}': No Animator found. " +
                             "Add an Animator component with a Humanoid Avatar to the prefab.");
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Called by PanelDetector right after spawning.
    /// Strips any Animator Controller, plays the idle clip, and sets up look-at bones.
    /// </summary>
    public void Init(AnimationClip overrideIdle = null, AnimationClip overrideTalking = null)
    {
        if (overrideIdle    != null) idleClip    = overrideIdle;
        if (overrideTalking != null) talkingClip = overrideTalking;

        if (_animator != null)
            _animator.runtimeAnimatorController = null;

        BuildGraph();
        PlayIdle();
        SetupLookAt();
    }

    private void Update()
    {
        if (!_graphReady) return;
        LoopClipPlayable(ref _slotA, _clipA);
        if (_mixer.GetInputWeight(1) > 0f)
            LoopClipPlayable(ref _slotB, _clipB);
    }

    /// <summary>
    /// LateUpdate runs AFTER the PlayableGraph updates all bone positions,
    /// so our rotation overrides always sit on top of the animation.
    /// </summary>
    private void LateUpdate()
    {
        if (!enableLookAt) return;

        // Re-acquire camera if lost (scene reload, etc.)
        if (_camera == null) _camera = Camera.main?.transform;
        if (_camera == null || _headBone == null) return;

        // Initialise smooth rotations on the very first frame so there is no
        // pop from identity to the animated pose.
        if (!_lookAtInitialized)
        {
            _headSmoothRot     = _headBone.rotation;
            _eyeSmoothRot      = _leftEyeBone != null ? _leftEyeBone.rotation
                                                      : _headBone.rotation;
            _lookAtInitialized = true;
        }

        // Target = camera position = user's eye level.
        Vector3 target = _camera.position;

        ApplyHeadLookAt(target);
        ApplyEyeLookAt(target);
    }

    private void OnDestroy()
    {
        if (_fadeCoroutine != null) StopCoroutine(_fadeCoroutine);
        if (_graph.IsValid()) _graph.Destroy();
    }

    /// <summary>Switch to the standing idle animation (with crossfade).</summary>
    public void PlayIdle()
    {
        if (idleClip == null)
        {
            Debug.LogWarning($"[PanelNPCController] '{name}': Idle Clip is not assigned.");
            return;
        }
        CrossfadeTo(idleClip, "idle");
    }

    /// <summary>Switch to the talking animation (with crossfade).</summary>
    public void PlayTalking()
    {
        AnimationClip clip = talkingClip != null ? talkingClip : idleClip;
        if (clip == null)
        {
            Debug.LogWarning($"[PanelNPCController] '{name}': Neither Talking Clip nor Idle Clip is assigned.");
            return;
        }
        CrossfadeTo(clip, "talking");
    }

    // ── Look-At setup ─────────────────────────────────────────────────────────

    private void SetupLookAt()
    {
        _camera       = Camera.main?.transform;
        _headBone     = FindDeepChild(transform, headBoneName);
        _neckBone     = FindDeepChild(transform, neckBoneName);
        _leftEyeBone  = FindDeepChild(transform, leftEyeBoneName);
        _rightEyeBone = FindDeepChild(transform, rightEyeBoneName);

        // Log what was found so the user can correct bone names in the Inspector.
        if (_headBone == null)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[PanelNPCController] '{name}': Head bone '{headBoneName}' not found — " +
                          "look-at disabled.  Available bones:");
            foreach (Transform t in GetComponentsInChildren<Transform>())
                sb.AppendLine($"  {t.name}");
            Debug.LogWarning(sb.ToString());
        }
        else
        {
            Debug.Log($"[PanelNPCController] '{name}' look-at bones — " +
                      $"Head: '{_headBone.name}'  " +
                      $"Neck: '{(_neckBone     != null ? _neckBone.name     : "NOT FOUND")}'  " +
                      $"L.Eye: '{(_leftEyeBone  != null ? _leftEyeBone.name  : "NOT FOUND")}'  " +
                      $"R.Eye: '{(_rightEyeBone != null ? _rightEyeBone.name : "NOT FOUND")}'");
        }
    }

    // ── Look-At — head ────────────────────────────────────────────────────────

    /// <summary>
    /// Turns the head (and neck partially) toward <paramref name="target"/>,
    /// on top of the animation's bone rotation.
    /// Uses a horizontal signed angle so the head never flips when the user is
    /// behind or to the side.
    /// </summary>
    private void ApplyHeadLookAt(Vector3 target)
    {
        // Look at the user's eye level — use camera Y so we don't look down or up.
        Vector3 eyeTarget = new Vector3(target.x, _headBone.position.y, target.z);

        Vector3 toTarget = eyeTarget - _headBone.position;
        if (toTarget.sqrMagnitude < 0.001f) return;

        // Signed horizontal angle between body forward and target direction.
        Vector3 bodyFwd    = new Vector3(transform.forward.x, 0f, transform.forward.z).normalized;
        Vector3 targetFlat = new Vector3(toTarget.x,          0f, toTarget.z).normalized;
        float   angleY     = Vector3.SignedAngle(bodyFwd, targetFlat, Vector3.up);

        // Clamp so the head doesn't turn past a natural range.
        float clampedAngle = Mathf.Clamp(angleY, -maxHeadLookAngle, maxHeadLookAngle);

        // Apply the horizontal rotation ON TOP OF the animation's bone rotation.
        Quaternion animRot  = _headBone.rotation;
        Quaternion desired  = Quaternion.AngleAxis(clampedAngle, Vector3.up) * animRot;

        // Smooth toward the desired rotation.
        _headSmoothRot  = Quaternion.Slerp(_headSmoothRot, desired,
                                           Time.deltaTime * headLookSpeed);

        // Blend between pure animation and the look-at rotation.
        _headBone.rotation = Quaternion.Slerp(animRot, _headSmoothRot, headLookWeight);

        // Give the neck ~35% of the turn for a natural two-joint movement.
        if (_neckBone != null)
            _neckBone.rotation = Quaternion.Slerp(_neckBone.rotation,
                                                   _headSmoothRot,
                                                   headLookWeight * 0.35f);
    }

    // ── Look-At — eyes ───────────────────────────────────────────────────────

    /// <summary>
    /// Rotates the eye bones so the pupils point directly at the camera (user's eye),
    /// clamped to <see cref="maxEyeLookAngle"/> to prevent the eyeballs sliding off-mesh.
    /// A single shared smooth rotation is used for both eyes (parallax is negligible
    /// at talking distance).
    /// </summary>
    private void ApplyEyeLookAt(Vector3 target)
    {
        if (_leftEyeBone == null && _rightEyeBone == null) return;

        // Reference eye position: midpoint of whichever eyes are present.
        Vector3 eyeCenter = Vector3.zero;
        int     count     = 0;
        if (_leftEyeBone  != null) { eyeCenter += _leftEyeBone.position;  count++; }
        if (_rightEyeBone != null) { eyeCenter += _rightEyeBone.position; count++; }
        eyeCenter /= count;

        Vector3 toTarget = (target - eyeCenter).normalized;

        // Build the desired world-space rotation: forward = toward user.
        // Use Vector3.up as the "up" reference so eyes don't roll sideways.
        Quaternion desired = Quaternion.LookRotation(toTarget, Vector3.up);

        // Smooth toward desired — eyes react faster than the head.
        _eyeSmoothRot = Quaternion.Slerp(_eyeSmoothRot, desired,
                                         Time.deltaTime * eyeLookSpeed);

        // Apply to each eye, clamped against the bone's CURRENT animated forward
        // to avoid exceeding maxEyeLookAngle even if the head is mid-turn.
        RotateEyeBone(_leftEyeBone,  _eyeSmoothRot);
        RotateEyeBone(_rightEyeBone, _eyeSmoothRot);
    }

    private void RotateEyeBone(Transform eyeBone, Quaternion smoothDesired)
    {
        if (eyeBone == null) return;

        // Animated rotation for this frame (set by the PlayableGraph just before LateUpdate).
        Quaternion animRot = eyeBone.rotation;

        // Clamp: if the angle from animated forward to desired is too large, dial it back.
        float angle = Quaternion.Angle(animRot, smoothDesired);
        Quaternion clamped = angle > maxEyeLookAngle
            ? Quaternion.Slerp(animRot, smoothDesired, maxEyeLookAngle / angle)
            : smoothDesired;

        // Blend between pure animation and the clamped look-at.
        eyeBone.rotation = Quaternion.Slerp(animRot, clamped, eyeLookWeight);
    }

    // ── Graph construction ────────────────────────────────────────────────────

    private void BuildGraph()
    {
        if (_graph.IsValid()) _graph.Destroy();
        _graphReady = false;

        if (_animator == null) return;

        _graph = PlayableGraph.Create($"PanelNPC_{name}");
        _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);

        _mixer = AnimationMixerPlayable.Create(_graph, 2);

        AnimationClip placeholder = idleClip    != null ? idleClip
                                  : talkingClip != null ? talkingClip : null;

        if (placeholder == null)
        {
            Debug.LogWarning($"[PanelNPCController] '{name}': No clips assigned — graph not built.");
            _graph.Destroy();
            return;
        }

        _slotA = MakePlayable(placeholder);
        _slotB = MakePlayable(placeholder);
        _clipA = placeholder;
        _clipB = placeholder;

        _graph.Connect(_slotA, 0, _mixer, 0);
        _graph.Connect(_slotB, 0, _mixer, 1);
        _mixer.SetInputWeight(0, 1f);
        _mixer.SetInputWeight(1, 0f);

        var output = AnimationPlayableOutput.Create(_graph, "Animation", _animator);
        output.SetSourcePlayable(_mixer);

        _graph.Play();
        _graphReady = true;
    }

    // ── Crossfade ─────────────────────────────────────────────────────────────

    private void CrossfadeTo(AnimationClip targetClip, string label)
    {
        if (_animator == null) return;

        if (!_graphReady) BuildGraph();
        if (!_graphReady) return;

        if (targetClip == _clipA && _mixer.GetInputWeight(0) >= 0.99f)
            return;

        if (_fadeCoroutine != null)
        {
            StopCoroutine(_fadeCoroutine);
            _fadeCoroutine = null;
            PromoteSlotB();
        }

        _graph.Disconnect(_mixer, 1);
        _slotB = MakePlayable(targetClip);
        _clipB = targetClip;
        _graph.Connect(_slotB, 0, _mixer, 1);
        _mixer.SetInputWeight(1, 0f);

        Debug.Log($"[PanelNPCController] '{name}' crossfading → {label}: '{targetClip.name}'  " +
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

    private void PromoteSlotB()
    {
        _graph.Disconnect(_mixer, 0);
        _graph.Disconnect(_mixer, 1);
        _slotA = _slotB;
        _clipA = _clipB;
        _slotB = MakePlayable(_clipB);
        _graph.Connect(_slotA, 0, _mixer, 0);
        _graph.Connect(_slotB, 0, _mixer, 1);
        _mixer.SetInputWeight(0, 1f);
        _mixer.SetInputWeight(1, 0f);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private AnimationClipPlayable MakePlayable(AnimationClip clip)
    {
        var p = AnimationClipPlayable.Create(_graph, clip);
        p.SetApplyFootIK(false);
        p.SetTime(0);
        p.Play();
        return p;
    }

    private static void LoopClipPlayable(ref AnimationClipPlayable playable, AnimationClip clip)
    {
        if (!playable.IsValid() || clip == null || clip.length <= 0f) return;
        if ((float)playable.GetTime() >= clip.length)
            playable.SetTime(0f);
    }

    /// <summary>
    /// Searches the entire child hierarchy for a Transform whose name matches
    /// <paramref name="boneName"/> (case-insensitive exact match).
    /// </summary>
    private Transform FindDeepChild(Transform root, string boneName)
    {
        foreach (Transform t in root.GetComponentsInChildren<Transform>())
            if (string.Equals(t.name, boneName, System.StringComparison.OrdinalIgnoreCase))
                return t;
        return null;
    }
}
