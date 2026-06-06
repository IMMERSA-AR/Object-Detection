using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MuradSittingPoseDriver : MonoBehaviour
{
    public AnimationClip clip;
    public AnimationClip[] variantClips;
    public float switchInterval = 15f;
    private Coroutine _variationCoroutine;
    private AnimationClip _baseClip;



    // for multiple sitting animations 
    public void StartSittingVariation()
    {
        _baseClip = clip;
        var pool = new List<AnimationClip>();
        if (_baseClip != null)
            pool.Add(_baseClip);
        if (variantClips != null)
            foreach (var oneclip in variantClips)
                if (oneclip != null && !pool.Contains(oneclip))
                    pool.Add(oneclip);

        if (pool.Count < 2)
        {
            Debug.Log("MuradSittingPoseDriver: since no of variations < 2, it is not true. Can not find anything to switch with ");
            return;
        }

        if (_variationCoroutine != null) StopCoroutine(_variationCoroutine);
        _variationCoroutine = StartCoroutine(SittingVariationLoop(pool));
        Debug.Log($"MuradSittingPoseDriver: variation sitting clips started.");
    }

    private IEnumerator SittingVariationLoop(List<AnimationClip> pool)
    {
        int lastIdx = 0;

        while (true)
        {
            float jitter = switchInterval * 0.2f;
            float wait = switchInterval + Random.Range(-jitter, jitter);
            yield return new WaitForSeconds(wait);

            int next;
            do
            {
                next = Random.Range(0, pool.Count);
            }
            while (next == lastIdx && pool.Count > 1);

            lastIdx = next;
            clip = pool[next];
            _clipTime = 0f;

            Debug.Log($"MuradSittingPoseDriver: Switched to another sitting clip");
        }
    }

    private float _clipTime = 0f;
    public Vector3 headLookTarget;
    public bool hasHeadLookTarget = false;

    [Range(0f, 1f)] public float headLookWeight = 0.65f;
    public float headMaxAngle = 60f;
    public float headLookSpeed = 4f;

    private Transform _headBone;
    private Transform _neckBone;
    private Quaternion _headSmoothRot;
    private bool _headInitialized;

    private SkinnedMeshRenderer _faceMesh;
    private float[] _savedWeights;
    private Transform _jawBone;

    void Awake()
    {
        var skinmeshrenders = GetComponentsInChildren<SkinnedMeshRenderer>(true);
        int maxBS = 0;
        foreach (var singleskinmeshrender in skinmeshrenders)
        {
            if (singleskinmeshrender.sharedMesh == null)
                continue;
            int count = singleskinmeshrender.sharedMesh.blendShapeCount;
            if (count > maxBS)
            {
                maxBS = count;
                _faceMesh = singleskinmeshrender;
            }
        }
        if (_faceMesh != null)
        {
            _savedWeights = new float[maxBS];
        }
        else
        {
            Debug.LogWarning("MuradSittingPoseDriver:no facemesh");
        }

        var anim = GetComponentInChildren<Animator>();
        if (anim != null && anim.isHuman)
        {
            _jawBone = anim.GetBoneTransform(HumanBodyBones.Jaw);
            if (_jawBone != null)
                Debug.Log($"MuradSittingPoseDriver: find jaw bone");
            else
                Debug.Log("MuradSittingPoseDriver: can not findjawbone");

            _headBone = anim.GetBoneTransform(HumanBodyBones.Head);
            _neckBone = anim.GetBoneTransform(HumanBodyBones.Neck);
            if (_headBone != null)
                Debug.Log($"MuradSittingPoseDriver: find headbone , neck = '{(_neckBone != null ? _neckBone.name : "not found")}'");
            else
                Debug.LogWarning("MuradSittingPoseDriver: can not find headbone.");
        }
    }

    void OnDestroy()
    {
        // kill the variation loop so it doesn't run after scene finished
        if (_variationCoroutine != null)
        {
            StopCoroutine(_variationCoroutine);
            _variationCoroutine = null;
        }
    }

    private void ApplyHeadLookAt()
    {
        // keep the charcter at head height 
        Vector3 eyeLevelTarget = new Vector3(headLookTarget.x, _headBone.position.y, headLookTarget.z);

        Vector3 toTarget = eyeLevelTarget - _headBone.position;
        if (toTarget.sqrMagnitude < 0.001f)
            return;

        Vector3 toFlat = new Vector3(toTarget.x, 0f, toTarget.z).normalized;
        Vector3 bodyFwd = new Vector3(transform.forward.x, 0f, transform.forward.z).normalized;
        float angleY = Vector3.SignedAngle(bodyFwd, toFlat, Vector3.up);
        float clamped = Mathf.Clamp(angleY, -headMaxAngle, headMaxAngle);

        if (!_headInitialized)
        {
            _headSmoothRot = _headBone.rotation;
            _headInitialized = true;
        }

        // rotate it in a smooth way to not make it feel odd
        Quaternion animRot = _headBone.rotation;
        Quaternion desired = Quaternion.AngleAxis(clamped, Vector3.up) * animRot;
        _headSmoothRot = Quaternion.Slerp(_headSmoothRot, desired, Time.deltaTime * headLookSpeed);

        _headBone.rotation = Quaternion.Slerp(animRot, _headSmoothRot, headLookWeight);

        // make neck look natural by rotating a little bit
        if (_neckBone != null)
            _neckBone.rotation = Quaternion.Slerp(_neckBone.rotation, _headSmoothRot, headLookWeight * 0.4f);
    }

    void LateUpdate()
    {
        if (clip == null)
        {
            Debug.LogWarning("MuradSittingPoseDriver: no clip assigned.");
            return;
        }

        Vector3 savedPos = transform.position;
        Quaternion savedRot = transform.rotation;


        if (_faceMesh != null && _savedWeights != null)
            for (int i = 0; i < _savedWeights.Length; i++)
                _savedWeights[i] = _faceMesh.GetBlendShapeWeight(i);

        Quaternion jawBefore = _jawBone != null ? _jawBone.localRotation : Quaternion.identity;
        clip.SampleAnimation(gameObject, _clipTime);

        // put him back where he was
        transform.position = savedPos;
        transform.rotation = savedRot;

        // restore the face so the lip-sync keeps control of the mouth
        if (_faceMesh != null && _savedWeights != null)
            for (int i = 0; i < _savedWeights.Length; i++)
                _faceMesh.SetBlendShapeWeight(i, _savedWeights[i]);

        if (_jawBone != null)
            _jawBone.localRotation = jawBefore;

        _clipTime = _clipTime + Time.deltaTime;
        if (_clipTime > clip.length)
            _clipTime = _clipTime % clip.length;

        // turn the head toward the doctor
        if (hasHeadLookTarget && _headBone != null)
            ApplyHeadLookAt();
    }
}
