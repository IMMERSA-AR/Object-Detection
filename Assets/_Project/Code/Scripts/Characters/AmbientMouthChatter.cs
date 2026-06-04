using UnityEngine;

/// <summary>
/// Fakes background "talking" for ambient NPCs whose body chat animation doesn't move
/// the face. Opens/closes the mouth on a randomized loop (irregular bursts of jabber
/// with natural pauses), so a group of characters looks like they're chatting.
///
/// Auto-detects what to drive:
///   1. a mouth BLENDSHAPE (V_Open / Merged_Open_Mouth / Jaw_Open …) if the mesh has one, else
///   2. the JAW BONE (CC_Base_JawRoot / Jaw …) — works even with no facial blendshapes.
///
/// Applied in LateUpdate so it sits on top of the body animation. Each character seeds
/// itself randomly so they don't chatter in sync. Drop it on each NPC (or their prefab).
/// </summary>
public class AmbientMouthChatter : MonoBehaviour
{
    [Header("Targets (auto-detected if left empty)")]
    [Tooltip("Face mesh with the mouth blendshape. Auto-found if empty.")]
    public SkinnedMeshRenderer faceMesh;

    [Tooltip("Mouth-open blendshape names to look for, in priority order.")]
    public string[] mouthBlendShapeNames =
        { "Jaw_Open", "Merged_Open_Mouth", "V_Open", "Mouth_Open", "A25_Jaw_Open", "jawOpen" };

    [Tooltip("Jaw bone names to look for if no mouth blendshape exists.")]
    public string[] jawBoneNames =
        { "CC_Base_JawRoot", "CC_Base_Jaw", "JawRoot", "Jaw", "mixamorig:Jaw" };

    [Header("Motion")]
    [Range(0f, 1f)]
    [Tooltip("Maximum mouth opening (1 = full).")]
    public float intensity = 0.7f;

    [Tooltip("How fast the mouth shape changes while talking.")]
    public float chatterSpeed = 9f;

    [Tooltip("How quickly the mouth eases toward its target (higher = snappier).")]
    public float smoothing = 12f;

    [Tooltip("Jaw rotation applied at full open when driving the JAW BONE (degrees). " +
             "If the mouth opens the wrong way, flip the sign or change the axis.")]
    public Vector3 jawOpenEuler = new Vector3(12f, 0f, 0f);

    [Header("Rhythm (seconds)")]
    [Tooltip("Length of a talking burst (random in this range).")]
    public Vector2 talkBurst = new Vector2(1.2f, 3f);

    [Tooltip("Length of a quiet pause between bursts (random in this range).")]
    public Vector2 pause = new Vector2(0.4f, 1.5f);

    // ── runtime ──
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

        // 1) Mouth blendshape on the assigned mesh, then any child mesh.
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

        // 2) Fall back to a jaw bone.
        if (_bsIndex < 0)
        {
            _jaw = FindJaw();
            if (_jaw != null) _jawRest = _jaw.localRotation;
        }

        if (_bsIndex < 0 && _jaw == null)
        {
            Debug.LogWarning($"[AmbientMouthChatter] {name}: no mouth blendshape or jaw bone found — disabled.");
            enabled = false;
            return;
        }

        _talking = true;
        ResetPhase();
    }

    void LateUpdate()
    {
        // Talk / pause rhythm.
        _phaseTimer -= Time.deltaTime;
        if (_phaseTimer <= 0f) { _talking = !_talking; ResetPhase(); }

        if (_talking)
        {
            // Layered Perlin noise → irregular, organic jabber.
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
