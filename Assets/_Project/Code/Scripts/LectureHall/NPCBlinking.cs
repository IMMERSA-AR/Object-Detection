using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Realistic randomised eye-blinking for any CC4 / Mixamo humanoid NPC.
///
/// CC4 characters spread their facial blend shapes across MULTIPLE meshes
/// (CC_Base_Body, CC_Base_EyeOcclusion, CC_Base_TearLine, Brows_*, Lash_*, …).
/// Every mesh that contains the blink shapes must be driven simultaneously or
/// the eyes won't visually close.  This component collects ALL matching
/// (mesh, index) pairs and writes to all of them every frame.
/// </summary>
public class NPCBlinking : MonoBehaviour
{
    [Header("Timing")]
    public float minInterval  = 2.5f;
    public float maxInterval  = 6.0f;
    [Tooltip("Total duration of one blink (close + open), seconds. Human avg ≈ 0.14 s.")]
    public float blinkDuration = 0.09f;

    [Header("Blend-shape name override (leave empty = auto-detect)")]
    [Tooltip("Left-eye blink blend shape name. Auto-filled on first run.")]
    public string blendNameLeft  = "";
    [Tooltip("Right-eye blink blend shape name. Leave empty for a single combined shape.")]
    public string blendNameRight = "";

    // ── internal ──────────────────────────────────────────────────────────────
    private struct MeshBlink { public SkinnedMeshRenderer smr; public int idxL; public int idxR; }
    private readonly List<MeshBlink> _targets = new List<MeshBlink>();

    // Candidate name groups — tried in order, first match wins for NAME selection,
    // but then ALL meshes are checked for that name pair.
    private static readonly string[][] _candidates =
    {
        new[]{ "Eye_Blink_L",    "Eye_Blink_R"    },   // CC4 / iClone
        new[]{ "Eye_Blink"                         },   // CC4 combined
        new[]{ "eyeBlinkLeft",   "eyeBlinkRight"   },   // ARKit / RPM
        new[]{ "Blink_Left",     "Blink_Right"     },
        new[]{ "blink_L",        "blink_R"         },
        new[]{ "blink"                              },
        new[]{ "v_closeEye_L",   "v_closeEye_R"   },
    };

    private void Awake()
    {
        FindAllBlinkTargets();

        if (_targets.Count == 0)
        {
            Debug.LogWarning($"[NPCBlinking] '{name}': no blink blend shapes found on any mesh. " +
                             "Check logcat for the full blend-shape list.");
            enabled = false;
            return;
        }

        Debug.Log($"[NPCBlinking] '{name}': driving {_targets.Count} mesh(es) — " +
                  $"L='{blendNameLeft}'  R='{blendNameRight}'");
        StartCoroutine(BlinkLoop());
    }

    // ── Discovery ─────────────────────────────────────────────────────────────

    private void FindAllBlinkTargets()
    {
        var allSMR = GetComponentsInChildren<SkinnedMeshRenderer>(true);

        // ── Step 1: pick the blend-shape NAME to use ──────────────────────────
        // If the Inspector override is set, use it directly.
        // Otherwise auto-detect from the first SMR that has a recognised name.
        if (string.IsNullOrEmpty(blendNameLeft))
        {
            foreach (var smr in allSMR)
            {
                if (smr == null || smr.sharedMesh == null) continue;
                foreach (var group in _candidates)
                {
                    if (group.Length == 1)
                    {
                        if (smr.sharedMesh.GetBlendShapeIndex(group[0]) >= 0)
                        {
                            blendNameLeft  = group[0];
                            blendNameRight = "";
                            goto nameFound;
                        }
                    }
                    else
                    {
                        if (smr.sharedMesh.GetBlendShapeIndex(group[0]) >= 0 &&
                            smr.sharedMesh.GetBlendShapeIndex(group[1]) >= 0)
                        {
                            blendNameLeft  = group[0];
                            blendNameRight = group[1];
                            goto nameFound;
                        }
                    }
                }
            }
        }
        nameFound:

        if (string.IsNullOrEmpty(blendNameLeft))
        {
            // Dump all names to help debug
            foreach (var smr in allSMR)
            {
                if (smr == null || smr.sharedMesh == null || smr.sharedMesh.blendShapeCount == 0) continue;
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[NPCBlinking] '{name}' mesh '{smr.name}' shapes:");
                for (int i = 0; i < smr.sharedMesh.blendShapeCount; i++)
                    sb.AppendLine($"  [{i}] {smr.sharedMesh.GetBlendShapeName(i)}");
                Debug.Log(sb.ToString());
            }
            return;
        }

        // ── Step 2: collect EVERY mesh that has this name ─────────────────────
        foreach (var smr in allSMR)
        {
            if (smr == null || smr.sharedMesh == null) continue;

            int idxL = smr.sharedMesh.GetBlendShapeIndex(blendNameLeft);
            if (idxL < 0) continue;   // this mesh doesn't have the shape — skip

            int idxR = string.IsNullOrEmpty(blendNameRight)
                       ? -1
                       : smr.sharedMesh.GetBlendShapeIndex(blendNameRight);

            _targets.Add(new MeshBlink { smr = smr, idxL = idxL, idxR = idxR });
            Debug.Log($"[NPCBlinking] '{name}': registered mesh '{smr.name}'  " +
                      $"idxL={idxL}  idxR={idxR}");
        }
    }

    // ── Blink loop ─────────────────────────────────────────────────────────────

    private IEnumerator BlinkLoop()
    {
        yield return new WaitForSeconds(Random.Range(0f, maxInterval));

        while (true)
        {
            yield return new WaitForSeconds(Random.Range(minInterval, maxInterval));

            int count = Random.value < 0.10f ? 2 : 1;
            for (int b = 0; b < count; b++)
            {
                yield return StartCoroutine(DoBlink());
                if (count > 1) yield return new WaitForSeconds(0.07f);
            }
        }
    }

    private IEnumerator DoBlink()
    {
        float half = blinkDuration * 0.5f;

        // Close
        for (float t = 0f; t < half; t += Time.deltaTime)
        {
            SetWeights(Mathf.Clamp01(t / half) * 100f);
            yield return null;
        }
        SetWeights(100f);
        yield return null;

        // Open
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
            if (mb.smr == null) continue;
            mb.smr.SetBlendShapeWeight(mb.idxL, w);
            if (mb.idxR >= 0) mb.smr.SetBlendShapeWeight(mb.idxR, w);
        }
    }

    private void OnDisable() => SetWeights(0f);
}
