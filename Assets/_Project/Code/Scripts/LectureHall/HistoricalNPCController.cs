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

    // ── private ───────────────────────────────────────────────────
    private Animator _animator;
    private bool _isLecturing;
    private Transform _playerCamera;
    private Vector3 _paceOrigin;
    private Vector3 _paceRight;
    private float _pacePhase;
    private Vector3 _doctorLookAtPoint; // world-space center of student area
    private bool _hasLookAtPoint;

    private PlayableGraph _graph;
    private AnimationClipPlayable _activePlayable;
    private AnimationClip _activeClip;

    public NPCRole Role => role;

    // ── Init ──────────────────────────────────────────────────────

    /// <param name="facingTarget">Optional world position to face at spawn.
    /// If null, students face the camera. Pass the doctor's position for chair-based spawning.</param>
    public void Init(NPCRole assignedRole, Vector3? facingTarget = null)
    {
        role = assignedRole;
        _playerCamera = Camera.main?.transform;
        _animator = GetComponent<Animator>();
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


    private void OnDestroy()
    {
        if (_graph.IsValid())
            _graph.Destroy();
    }
}
