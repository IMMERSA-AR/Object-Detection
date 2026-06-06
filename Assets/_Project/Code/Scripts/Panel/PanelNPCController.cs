using System.Collections;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

[DisallowMultipleComponent]
public class PanelNPCController : MonoBehaviour
{
    [Header("Animation Clips")]
    public AnimationClip standingIdleClip;
    public AnimationClip talkingClip;

    [Header("Crossfade")]
    [Range(0f, 1f)]
    public float crossfadeDuration = 0.25f;

    [Header("Head & Eye Look-At")]
    public bool enableLookAt = true;
    public string headBoneName = "CC_Base_Head";
    public string neckBoneName = "CC_Base_NeckTwist01";
    public string leftEyeBoneName = "CC_Base_L_Eye";
    public string rightEyeBoneName = "CC_Base_R_Eye";

    [Space]
    [Range(0f, 1f)]
    public float headLookWeight = 0.5f;
    [Range(0f, 90f)]
    public float maxHeadLookAngle = 50f;
    [Range(1f, 15f)]
    public float headLookSpeed = 4f;

    [Space]
    [Range(0f, 1f)]
    public float eyeLookWeight = 1f;

    [Range(0f, 40f)]
    public float maxEyeLookAngle = 25f;
    [Range(1f, 20f)]
    public float eyeLookSpeed = 10f;

    private Animator _animator;

    private PlayableGraph _graph;
    private AnimationMixerPlayable _mixer;
    private AnimationClipPlayable _slotA;
    private AnimationClipPlayable _slotB;
    private AnimationClip _clipA;
    private AnimationClip _clipB;
    private Coroutine _fadeCoroutine;
    private bool _graphReady;

    private Transform _camera;
    private Transform _headBone;
    private Transform _neckBone;
    private Transform _leftEyeBone;
    private Transform _rightEyeBone;
    private Quaternion _headSmoothRot;
    private Quaternion _eyeSmoothRot;


    private void Awake()
    {
        _animator = GetComponent<Animator>() ?? GetComponentInChildren<Animator>();

        if (_animator == null)
            Debug.LogWarning($"PanelNPCController: no animator assigned");
    }

    public void Init(AnimationClip overrideIdle = null, AnimationClip overrideTalking = null)
    {
        if (overrideIdle != null) standingIdleClip = overrideIdle;
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

    private void LateUpdate()
    {
        if (!enableLookAt)
            return;

        if (_camera == null)
        {
            Debug.LogWarning($"PanelNPCController: camera is null");
            _camera = Camera.main?.transform;
        }
        if (_camera == null || _headBone == null)
        {
            Debug.LogWarning($"PanelNPCController: camera or head bone is null, please check them");
            return;
        }
        Vector3 target = _camera.position;
        ApplyHeadLookAt(target);
        ApplyEyeLookAt(target);
    }

    private void OnDestroy()
    {
        if (_fadeCoroutine != null)
            StopCoroutine(_fadeCoroutine);
        if (_graph.IsValid())
            _graph.Destroy();
    }
    public void PlayIdle()
    {

        if (standingIdleClip == null)
        {
            Debug.LogWarning($"PanelNPCController: standing idle clip is not defined");
            return;
        }
        CrossfadeTo(standingIdleClip, "idle");
    }

    public void PlayTalking()
    {
        AnimationClip clip = talkingClip != null ? talkingClip : standingIdleClip;
        if (clip == null)
        {
            Debug.LogWarning($"PanelNPCController: no talking clip and no standing idle clip");
            return;
        }
        CrossfadeTo(clip, "talking");

    }

    private void SetupLookAt()
    {
        _camera = Camera.main?.transform;
        _headBone = FindDeepChild(transform, headBoneName);
        _neckBone = FindDeepChild(transform, neckBoneName);
        _leftEyeBone = FindDeepChild(transform, leftEyeBoneName);
        _rightEyeBone = FindDeepChild(transform, rightEyeBoneName);

        if (_headBone == null)
        {
            Debug.LogWarning($"PanelNPCController: no head bone");
        }
        else
        {
            Debug.Log($"PanelNPCController: '{name}' look-at bones — " + $"Head: '{_headBone.name}'  " + $"Neck: '{(_neckBone != null ? _neckBone.name : "NOT FOUND")}'  " + $"L.Eye: '{(_leftEyeBone != null ? _leftEyeBone.name : "NOT FOUND")}'  " + $"R.Eye: '{(_rightEyeBone != null ? _rightEyeBone.name : "NOT FOUND")}'");
        }
    }
    //look at 
    private void ApplyHeadLookAt(Vector3 target)
    {
        Vector3 eyeTarget = new Vector3(target.x, _headBone.position.y, target.z);

        Vector3 toTarget = eyeTarget - _headBone.position;
        if (toTarget.sqrMagnitude < 0.001f) return;

        Vector3 bodyFwd = new Vector3(transform.forward.x, 0f, transform.forward.z).normalized;
        Vector3 targetFlat = new Vector3(toTarget.x, 0f, toTarget.z).normalized;
        float angleY = Vector3.SignedAngle(bodyFwd, targetFlat, Vector3.up);

        // Clamp so the head doesn't turn past a natural range.
        float clampedAngle = Mathf.Clamp(angleY, -maxHeadLookAngle, maxHeadLookAngle);

        Quaternion animRot = _headBone.rotation;
        Quaternion desired = Quaternion.AngleAxis(clampedAngle, Vector3.up) * animRot;

        // Smooth toward the desired rotation, dont rotate in odd way 
        _headSmoothRot = Quaternion.Slerp(_headSmoothRot, desired, Time.deltaTime * headLookSpeed);

        // Blend between pure animation and the look-at rotation
        _headBone.rotation = Quaternion.Slerp(animRot, _headSmoothRot, headLookWeight);

        // give it a reasonabel angle to make it look natural 
        if (_neckBone != null)
            _neckBone.rotation = Quaternion.Slerp(_neckBone.rotation, _headSmoothRot, headLookWeight * 0.35f);
    }

    // look at eyes
    private void ApplyEyeLookAt(Vector3 target)
    {
        if (_leftEyeBone == null && _rightEyeBone == null)
        {
            Debug.LogWarning($"PanelNPCController: no left and right eye bones");
            return;
        }

        Vector3 eyeCenter = Vector3.zero;
        int count = 0;
        if (_leftEyeBone != null)
        {
            eyeCenter += _leftEyeBone.position;
            count++;
        }
        if (_rightEyeBone != null)
        {
            eyeCenter += _rightEyeBone.position;
            count++;
        }
        eyeCenter = eyeCenter / count;

        Vector3 toTarget = (target - eyeCenter).normalized;
        Quaternion desired = Quaternion.LookRotation(toTarget, Vector3.up);
        _eyeSmoothRot = Quaternion.Slerp(_eyeSmoothRot, desired, Time.deltaTime * eyeLookSpeed);

        RotateEyeBone(_leftEyeBone, _eyeSmoothRot);
        RotateEyeBone(_rightEyeBone, _eyeSmoothRot);
    }

    private void RotateEyeBone(Transform eyeBone, Quaternion smoothDesired)
    {
        if (eyeBone == null)
        {
            Debug.LogWarning($"PanelNPCController: no eye bones");
            return;
        }
        Quaternion animRot = eyeBone.rotation;

        float angle = Quaternion.Angle(animRot, smoothDesired);
        Quaternion clamped = angle > maxEyeLookAngle ? Quaternion.Slerp(animRot, smoothDesired, maxEyeLookAngle / angle) : smoothDesired;

        eyeBone.rotation = Quaternion.Slerp(animRot, clamped, eyeLookWeight);
    }

    private void BuildGraph()
    {
        if (_graph.IsValid())
            _graph.Destroy();
        _graphReady = false;

        if (_animator == null)
        {
            Debug.LogWarning($"PanelNPCController: no animator");
            return;
        }
        _graph = PlayableGraph.Create($"PanelNPC_{name}");
        _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);

        _mixer = AnimationMixerPlayable.Create(_graph, 2);

        AnimationClip placeholder = standingIdleClip != null ? standingIdleClip : talkingClip != null ? talkingClip : null;

        if (placeholder == null)
        {
            Debug.LogWarning($"PanelNPCController: No clips assigned ");
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

    private void CrossfadeTo(AnimationClip targetClip, string label)
    {
        if (_animator == null)
            return;

        if (!_graphReady)
            BuildGraph();
        if (!_graphReady)
            return;

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
            float clipTime = Mathf.Clamp01(elapsed / crossfadeDuration);
            _mixer.SetInputWeight(0, 1f - clipTime);
            _mixer.SetInputWeight(1, clipTime);
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

    private Transform FindDeepChild(Transform root, string boneName)
    {
        foreach (Transform t in root.GetComponentsInChildren<Transform>())
            if (string.Equals(t.name, boneName, System.StringComparison.OrdinalIgnoreCase))
                return t;
        return null;
    }
}
