using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class NPCBlinking : MonoBehaviour
{
    [Header("Timing")]
    public float MinInterval = 2.5f;
    public float MaxInterval = 6.0f;
    public float BlinkDuration = 0.09f;

    [Header("Blend shape name")]
    public string LeftEyeBlendName = "";
    public string RightEyeBlendName = "";
    private struct MeshBlink { public SkinnedMeshRenderer smr; public int idxL; public int idxR; }
    private readonly List<MeshBlink> _targets = new List<MeshBlink>();
    private static readonly string[][] _possibleNames =
    {
        new[]{ "Eye_Blink_L","Eye_Blink_R"},
        new[]{ "Eye_Blink" }
    };


    private void Awake()
    {
        FindBlinkTargets();
        if (_targets.Count == 0)
        {
            Debug.LogWarning($"[NPCBlinking] '{name}': no blink targets found.");
            enabled = false;
            return;
        }
        StartCoroutine(BlinkLoop());
    }

    private void FindBlinkTargets()
    {
        var allSMR = GetComponentsInChildren<SkinnedMeshRenderer>(true);
        if (string.IsNullOrEmpty(LeftEyeBlendName))
        {
            foreach (var smr in allSMR)
            {
                if (smr == null || smr.sharedMesh == null)
                    continue;
                foreach (var group in _possibleNames)
                {
                    if (group.Length == 1)
                    {
                        if (smr.sharedMesh.GetBlendShapeIndex(group[0]) >= 0)
                        {
                            LeftEyeBlendName = group[0];
                            RightEyeBlendName = "";
                            goto nameFound;
                        }
                    }
                    else
                    {
                        if (smr.sharedMesh.GetBlendShapeIndex(group[0]) >= 0 && smr.sharedMesh.GetBlendShapeIndex(group[1]) >= 0)
                        {
                            LeftEyeBlendName = group[0];
                            RightEyeBlendName = group[1];
                            goto nameFound;
                        }
                    }
                }
            }
        }
    nameFound:

        if (string.IsNullOrEmpty(LeftEyeBlendName))
        {
            foreach (var smr in allSMR)
            {
                if (smr == null || smr.sharedMesh == null || smr.sharedMesh.blendShapeCount == 0)
                    continue;
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[NPCBlinking] '{name}' mesh '{smr.name}' shapes:");
                for (int i = 0; i < smr.sharedMesh.blendShapeCount; i++)
                    sb.AppendLine($" [{i}] {smr.sharedMesh.GetBlendShapeName(i)}");
                Debug.Log(sb.ToString());
            }
            return;
        }

        foreach (var smr in allSMR)
        {
            if (smr == null || smr.sharedMesh == null)
                continue;

            int idxL = smr.sharedMesh.GetBlendShapeIndex(LeftEyeBlendName);
            if (idxL < 0)
                continue;

            int idxR = string.IsNullOrEmpty(RightEyeBlendName) ? -1 : smr.sharedMesh.GetBlendShapeIndex(RightEyeBlendName);

            _targets.Add(new MeshBlink { smr = smr, idxL = idxL, idxR = idxR });
            Debug.Log($"[NPCBlinking] '{name}': registered mesh '{smr.name}' ");
        }
    }

    private IEnumerator BlinkLoop()
    {
        yield return new WaitForSeconds(Random.Range(0f, MaxInterval));
        while (true)
        {
            yield return new WaitForSeconds(Random.Range(MinInterval, MaxInterval));
            int count = Random.value < 0.10f ? 2 : 1;
            for (int b = 0; b < count; b++)
            {
                yield return StartCoroutine(BlinkExecution());
                if (count > 1)
                    yield return new WaitForSeconds(0.07f);
            }
        }
    }

    private IEnumerator BlinkExecution()
    {
        float half = BlinkDuration * 0.5f;

        for (float t = 0f; t < half; t += Time.deltaTime)
        {
            SetWeights(Mathf.Clamp01(t / half) * 100f);
            yield return null;
        }
        SetWeights(100f);
        yield return null;

        for (float t = 0f; t < half; t += Time.deltaTime)
        {
            SetWeights((1f - Mathf.Clamp01(t / half)) * 100f);
            yield return null;
        }
        SetWeights(0f);
    }

    private void SetWeights(float w)
    {
        foreach (var mb in _targets)
        {
            if (mb.smr == null)
                continue;
            mb.smr.SetBlendShapeWeight(mb.idxL, w);
            if (mb.idxR >= 0)
                mb.smr.SetBlendShapeWeight(mb.idxR, w);
        }
    }
    private void OnDisable()
    {
        Debug.Log($"[NPCBlinking] '{name}': diabling blink and reset all weights.");
        SetWeights(0f);
    }
}
