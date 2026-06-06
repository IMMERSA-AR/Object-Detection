using UnityEngine;

public class AmbientMouthChatter : MonoBehaviour
{
    [Header("Targets")]
    public SkinnedMeshRenderer faceMesh;
    public string[] mouthBlendShapeNames =
        { "Jaw_Open", "Merged_Open_Mouth", "V_Open", "Mouth_Open", "A25_Jaw_Open", "jawOpen" };
    public string[] jawBoneNames =
        { "CC_Base_JawRoot", "CC_Base_Jaw", "JawRoot", "Jaw", "mixamorig:Jaw" };
    [Header("Motion")]
    [Range(0f, 1f)]
    public float intensity = 0.7f;
    public float chatterSpeed = 9f;
    public float smoothing = 12f;
    public Vector3 jawOpenEuler = new Vector3(12f, 0f, 0f);
    [Header("Rhythm")]
    public Vector2 talkBurst = new Vector2(1.2f, 3f);
    public Vector2 pause = new Vector2(0.4f, 1.5f);

    private int        _bsIndex = -1;
    private Transform  _jaw;
    private Quaternion _jawRest;
    private float      _value, _target;
    private bool       _talking;
    private float      _phaseTimer;
    private float      _seed;

    void Start()
    {
        _seed = Random.value * 100f;

        if (faceMesh != null) _bsIndex = FindMouthBlendShape(faceMesh.sharedMesh);
        if (_bsIndex < 0)
        {
            foreach (var smr in GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null) continue;
                int idx = FindMouthBlendShape(smr.sharedMesh);
                if (idx >= 0) { faceMesh = smr; _bsIndex = idx; break; }
            }
        }

        if (_bsIndex < 0)
        {
            _jaw = FindJaw();
            if (_jaw != null) _jawRest = _jaw.localRotation;
        }

        if (_bsIndex < 0 && _jaw == null)
        {
            Debug.LogWarning($"[AmbientMouthChatter] {name}: no mouth blendshape or jaw bone found, disabled");
            enabled = false;
            return;
        }

        _talking = true;
        ResetPhase();
    }

    void LateUpdate()
    {
        _phaseTimer -= Time.deltaTime;
        if (_phaseTimer <= 0f) { _talking = !_talking; ResetPhase(); }

        if (_talking)
        {
            float t = Time.time * chatterSpeed + _seed;
            float n = Mathf.PerlinNoise(t, 0f) * 0.6f + Mathf.PerlinNoise(t * 1.7f, 5f) * 0.4f;
            _target = Mathf.Clamp01(n) * intensity;
        }
        else
        {
            _target = 0f;
        }

        _value = Mathf.Lerp(_value, _target, Time.deltaTime * smoothing);

        if (_bsIndex >= 0 && faceMesh != null)
            faceMesh.SetBlendShapeWeight(_bsIndex, _value * 100f);
        else if (_jaw != null)
            _jaw.localRotation = _jawRest * Quaternion.Euler(jawOpenEuler * _value);
    }

    private void ResetPhase()
    {
        _phaseTimer = _talking
            ? Random.Range(talkBurst.x, talkBurst.y)
            : Random.Range(pause.x, pause.y);
    }

    private int FindMouthBlendShape(Mesh m)
    {
        if (m == null) return -1;
        foreach (var n in mouthBlendShapeNames)
        {
            int i = m.GetBlendShapeIndex(n);
            if (i >= 0) return i;
        }
        return -1;
    }

    private Transform FindJaw()
    {
        foreach (var tr in GetComponentsInChildren<Transform>(true))
            foreach (var n in jawBoneNames)
                if (string.Equals(tr.name, n, System.StringComparison.OrdinalIgnoreCase))
                    return tr;
        return null;
    }
}
