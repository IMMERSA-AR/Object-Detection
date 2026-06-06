using System.Collections;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

public enum NPCRole
{
    Student,
    Doctor
}

public class HistoricalNPCController : MonoBehaviour
{
    [Header("Role")]
    public NPCRole role;

    [Header("Animation Clips")]
    public AnimationClip idleClip;

    public AnimationClip talkingClip;

    public AnimationClip standingAfterLectureClip;

    public AnimationClip walkingClip;

    public AnimationClip[] sittingClipVariants;
    [Range(3f, 30f)]
    public float sittingSwitchInterval = 8f;

    [Header("Doctor Pacing")]
    public float paceDistance = 0.6f;
    public float paceSpeed = 0.5f;

    [Header("Look At Player")]
    public float lookSpeed = 1.2f;
    public float maxLookAngle = 45f;

    [Header("Handling breathing")]
    public string spineBoneName = "mixamorig:Spine";

    [Range(0f, 3f)] public float breathDepth = 1.2f;

    [Range(8f, 20f)] public float breathRate = 13f;

    [Range(0f, 6.28f)] public float breathPhaseOffset = 0f;

    [Header("Head Look At")]
    public string headBoneName = "CC_Base_Head";
    public string neckBoneName = "CC_Base_NeckTwist01";
    [Range(0f, 1f)]
    public float headLookWeight = 0.7f;
    public float headMaxAngle = 70f;
    public float headLookSpeed = 4f;

    [Header("Animation Switching")]
    [Range(0f, 1f)]
    public float crossfadeDuration = 0.25f;

    // Private Parameters
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

    private PlayableGraph _graph;
    private AnimationMixerPlayable _mixer;
    private AnimationClipPlayable _slotA;
    private AnimationClipPlayable _slotB;
    private AnimationClip _clipA;
    private AnimationClip _clipB;
    private Coroutine _transitionCorountine;
    private bool _graphReady;
    private Transform _spineBone;
    private Transform _headBone;
    private Transform _neckBone;
    private Vector3 _headLookTarget;
    private bool _hasHeadLookTarget;
    private Quaternion _headSmoothRot;
    private bool _headInitialized;
    private Coroutine _sittingSwitchCoroutine;

    public NPCRole Role => role;

    public void Init(NPCRole assignedRole, Vector3? facingTarget = null)
    {
        role = assignedRole;
        _playerCamera = Camera.main?.transform;
        _animator = GetComponent<Animator>() ?? GetComponentInChildren<Animator>();

        if (_animator != null && _animator.runtimeAnimatorController != null)
        {
            Debug.Log($"[NPC] {gameObject.name}: removing animator controller");
            _animator.runtimeAnimatorController = null;
        }

        _paceOrigin = transform.position;
        _paceRight = transform.right;

        if (role == NPCRole.Student)
        {
            Vector3 target = facingTarget ?? (_playerCamera != null ? _playerCamera.position : transform.position + Vector3.forward);
            Vector3 dir = target - transform.position;
            dir.y = 0;
            if (dir.sqrMagnitude > 0.001f)
                transform.rotation = Quaternion.LookRotation(dir);

            _spineBone = FindCharacterBones(transform, spineBoneName);
            _headBone = FindCharacterBones(transform, headBoneName);
            _neckBone = FindCharacterBones(transform, neckBoneName);

            breathPhaseOffset = Random.Range(0f, Mathf.PI * 2f);

            if (_headBone == null)
            {
                // var sb = new System.Text.StringBuilder();
                // sb.AppendLine($"[NPC] Head bone is not found");
                // foreach (Transform t in GetComponentsInChildren<Transform>())
                //     sb.AppendLine($"  {t.name}");
                // Debug.LogWarning(sb.ToString());
                Debug.LogWarning($"[NPC] Head bone not found.");
            }
            else
            {
                Debug.Log($"[NPC]  Head bone = '{_headBone.name}'  " + $"Neck bone = '{(_neckBone != null ? _neckBone.name : "Not Found")}'");
            }

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
                _doctorLookAtPoint = facingTarget.Value;
                _hasLookAtPoint = true;

                Vector3 dir = facingTarget.Value - transform.position;
                dir.y = 0;
                if (dir.sqrMagnitude > 0.001f)
                    transform.rotation = Quaternion.LookRotation(dir);
            }
        }

        if (GetComponent<NPCBlinking>() == null)
            gameObject.AddComponent<NPCBlinking>();

        AnimationTransition(idleClip, "idle");
        Debug.Log($"[NPC] {gameObject.name} is defined as {role}");
    }

    private void Update()
    {
        if (_graphReady)
        {
            LoopClipPlayable(ref _slotA, _clipA);
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
        if (_transitionCorountine != null)
        {
            StopCoroutine(_transitionCorountine);
        }
        if (_graph.IsValid()) _graph.Destroy();
    }

    // Handle doctor actions 

    public void SetLecturing(bool lecturing)
    {
        if (role != NPCRole.Doctor)
            return;
        _isLecturing = lecturing;

        if (lecturing)
        {
            AnimationClip clip = talkingClip != null ? talkingClip : idleClip;
            AnimationTransition(clip, "talking");
            Debug.Log($"[NPC] Doctor is giving a lecture.");
        }
        else
        {
            AnimationClip standClip = standingAfterLectureClip != null ? standingAfterLectureClip : idleClip;

            if (standingAfterLectureClip == null)
                Debug.LogWarning($"[NPC] {gameObject.name}: Can not find standing after lecture clip.");

            AnimationTransition(standClip, "standing idle");
            Debug.Log($"[NPC] Lecture finished. Now doctor is standing.");
        }
    }

    private void UpdateDoctorPacing()
    {
        _pacePhase += Time.deltaTime * paceSpeed;
        float xOffset = Mathf.Sin(_pacePhase) * paceDistance;
        Vector3 newPos = _paceOrigin + _paceRight * xOffset;
        transform.position = new Vector3(newPos.x, _paceOrigin.y, newPos.z);
    }
    private void LateUpdate()
    {
        if (role != NPCRole.Student)
            return;
        ApplyBreathing();
        if (!_hasHeadLookTarget || _headBone == null)
            return;
        if (!_headInitialized)
        {
            _headSmoothRot = _headBone.rotation;
            _headInitialized = true;
        }
        ApplyHeadLookAt();
    }

    private void ApplyBreathing()
    {
        if (_spineBone == null)
            return;
        float cyclesPerSecond = breathRate / 60f;
        float breath = Mathf.Sin(Time.time * cyclesPerSecond * Mathf.PI * 2f + breathPhaseOffset);
        float tiltX = breath * breathDepth;
        _spineBone.localRotation *= Quaternion.Euler(tiltX, 0f, 0f);
    }

    private void ApplyHeadLookAt()
    {
        Vector3 eyeLevelTarget = new Vector3(_headLookTarget.x, _headBone.position.y, _headLookTarget.z);
        Vector3 toTarget = eyeLevelTarget - _headBone.position;
        if (toTarget.sqrMagnitude < 0.001f)
            return;
        Vector3 toTargetFlat = new Vector3(toTarget.x, 0f, toTarget.z).normalized;
        Vector3 bodyFwd = new Vector3(transform.forward.x, 0f, transform.forward.z).normalized;
        float angleY = Vector3.SignedAngle(bodyFwd, toTargetFlat, Vector3.up);
        float clampedAngle = Mathf.Clamp(angleY, -headMaxAngle, headMaxAngle);

        Quaternion animRot = _headBone.rotation;
        Quaternion desired = Quaternion.AngleAxis(clampedAngle, Vector3.up) * animRot;
        _headSmoothRot = Quaternion.Slerp(_headSmoothRot, desired, Time.deltaTime * headLookSpeed);
        _headBone.rotation = Quaternion.Slerp(animRot, _headSmoothRot, headLookWeight);

        if (_neckBone != null)
            _neckBone.rotation = Quaternion.Slerp(_neckBone.rotation, _headSmoothRot, headLookWeight * 0.4f);
    }

    // Allow multiple sitting options 

    public void StartSittingVariation()
    {
        var pool = new System.Collections.Generic.List<AnimationClip>();
        if (idleClip != null)
            pool.Add(idleClip);
        if (sittingClipVariants != null)
            foreach (var c in sittingClipVariants)
                if (c != null && !pool.Contains(c))
                    pool.Add(c);

        if (pool.Count < 2)
        {
            Debug.Log($"[NPC] '{name}': The sitting options are less than 2 so there is no switching.");
            return;
        }

        if (_sittingSwitchCoroutine != null)
        {
            StopCoroutine(_sittingSwitchCoroutine);
        }
        _sittingSwitchCoroutine = StartCoroutine(SittingVariationLoop(pool));
    }

    public void StopSittingVariation()
    {
        if (_sittingSwitchCoroutine == null)
            return;
        StopCoroutine(_sittingSwitchCoroutine);
        _sittingSwitchCoroutine = null;
        Debug.Log($"[NPC] '{name}': sitting variation stopped.");
    }

    private IEnumerator SittingVariationLoop(System.Collections.Generic.List<AnimationClip> pool)
    {
        int idleClipIndex = 0;
        while (true)
        {
            float jitter = sittingSwitchInterval * 0.2f;
            float wait = sittingSwitchInterval + Random.Range(-jitter, jitter);
            yield return new WaitForSeconds(wait);

            int next;
            do
            {
                next = Random.Range(0, pool.Count);
            }
            while (next == idleClipIndex && pool.Count > 1);

            idleClipIndex = next;
            AnimationClip clip = pool[next];
            if (clip != null)
                AnimationTransition(clip, $"Sitting Clip '{clip.name}'");
        }
    }

    private void BuildGraph(AnimationClip initialClip)
    {
        if (_graph.IsValid())
            _graph.Destroy();
        _graphReady = false;

        if (_animator == null || initialClip == null)
            return;

        _graph = PlayableGraph.Create($"NPC_{gameObject.name}");
        _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
        _mixer = AnimationMixerPlayable.Create(_graph, 2);
        _slotA = MakePlayable(initialClip);
        _slotB = MakePlayable(initialClip);
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
        Debug.Log($"[NPC] {gameObject.name}: PlayableGraph, the starting clip is:'{initialClip.name}'.");
    }

    // Transferring Animations 

    private void AnimationTransition(AnimationClip targetClip, string label)
    {
        if (_animator == null)
        {
            Debug.LogWarning($"NPC '{gameObject.name}': Can't find the animator");
            return;
        }
        if (targetClip == null)
        {
            Debug.LogWarning($"[NPC] {gameObject.name}: Can't find the clip");
            return;
        }

        if (!_graphReady)
        {
            BuildGraph(targetClip);
            Debug.Log($"[NPC] {gameObject.name} playing {label}: '{targetClip.name}'");
            return;
        }

        if (targetClip == _clipA && _mixer.GetInputWeight(0) >= 0.99f)
            return;

        if (_transitionCorountine != null)
        {
            StopCoroutine(_transitionCorountine);
            _transitionCorountine = null;
            PromoteSlotB();
        }

        _graph.Disconnect(_mixer, 1);
        _slotB = MakePlayable(targetClip);
        _clipB = targetClip;
        _graph.Connect(_slotB, 0, _mixer, 1);
        _mixer.SetInputWeight(1, 0f);

        if (crossfadeDuration <= 0f)
            PromoteSlotB();
        else
            _transitionCorountine = StartCoroutine(FadeCoroutine());
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
        _transitionCorountine = null;
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

    private AnimationClipPlayable MakePlayable(AnimationClip clip)
    {
        var p = AnimationClipPlayable.Create(_graph, clip);
        p.SetApplyFootIK(false);
        p.SetTime(0);
        p.Play();
        return p;
    }

    // Used to try to find head,neck and spine bones by name

    private Transform FindCharacterBones(Transform parent, string boneName)
    {
        foreach (Transform child in parent.GetComponentsInChildren<Transform>())
        {
            if (string.Equals(child.name, boneName, System.StringComparison.OrdinalIgnoreCase))
                return child;
        }
        return null;
    }

    public void InitHeadLookOnly(Vector3 initialLookTarget, AnimationClip sittingClip = null)
    {
        role = NPCRole.Student;
        _playerCamera = Camera.main?.transform;
        _animator = GetComponent<Animator>() ?? GetComponentInChildren<Animator>();

        _spineBone = FindCharacterBones(transform, spineBoneName);
        _headBone = FindCharacterBones(transform, headBoneName);
        _neckBone = FindCharacterBones(transform, neckBoneName);

        breathPhaseOffset = Random.Range(0f, Mathf.PI * 2f);

        if (_headBone == null)
            Debug.LogWarning($"[NPC] {gameObject.name}: can't find head bone");
        else
            Debug.Log($"[NPC] {gameObject.name} head bone is found");

        _headLookTarget = initialLookTarget;
        _hasHeadLookTarget = true;
        _headInitialized = false;

        if (sittingClip != null)
        {
            idleClip = sittingClip;
            AnimationTransition(sittingClip, "sitting");
        }
    }

    public void SwitchToClip(AnimationClip clip)
    {
        if (clip == null)
        {
            StopPlayableGraph();
            return;
        }
        AnimationTransition(clip, $"SwitchToClip '{clip.name}'");
    }

    public void StopPlayableGraph()
    {
        StopSittingVariation();
        if (_transitionCorountine != null)
        {
            StopCoroutine(_transitionCorountine);
            _transitionCorountine = null;
        }
        if (_graph.IsValid())
        {
            _graph.Destroy();
            _graphReady = false;
            Debug.Log($"[NPC] {gameObject.name}: PlayableGraph stopped");
        }
    }

    public void FreezePlayableGraph(float holdNormalised = 0f)
    {
        if (!_graphReady || _clipA == null)
            return;

        if (_transitionCorountine != null)
        {
            StopCoroutine(_transitionCorountine);
            _transitionCorountine = null;
            PromoteSlotB();
        }

        float holdTime = holdNormalised * _clipA.length;
        _slotA.SetTime(holdTime);
        _slotA.SetSpeed(0f);
    }

    public void ResumePlayableGraph()
    {
        if (!_graphReady) return;
        _slotA.SetSpeed(1f);
        Debug.Log($"[NPC] {gameObject.name}: PlayableGraph resumed");
    }

    public float GetRemainingPlayableTime(float holdNormalised)
    {
        if (!_graphReady || _clipA == null)
            return 0f;
        float elapsed = holdNormalised * _clipA.length;
        float remaining = _clipA.length - elapsed;
        return Mathf.Max(0f, remaining);
    }

    // Set look target and Ckear it
    public void SetHeadLookTarget(Vector3 worldPos)
    {
        _headLookTarget = worldPos;
        _hasHeadLookTarget = true;
        _headInitialized = false;
    }

    public void ClearHeadLookTarget()
    {
        _hasHeadLookTarget = false;
        _headInitialized = false;
    }
}
