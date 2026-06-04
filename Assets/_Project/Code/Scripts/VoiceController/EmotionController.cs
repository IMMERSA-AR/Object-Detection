using UnityEngine;
using System.Collections.Generic;

[System.Serializable]
public class BlendShapeData
{
    [Tooltip("The EXACT name of the blendshape from the Inspector (e.g., Brow_Drop)")]
    public string shapeName;
    [Range(0, 100)] public float targetWeight = 100f;
}

[System.Serializable]
public class Emotion
{
    public string emotionName; // e.g., "Angry", "Sad", "Smile"
    public List<BlendShapeData> activeShapes;
}

public class EmotionController : MonoBehaviour
{
    [Header("Setup")]
    public SkinnedMeshRenderer faceMesh; // Drag CC_Base_Body here
    public float transitionSpeed = 5f;   // How fast the face changes

    [Header("Your Emotions")]
    public List<Emotion> emotions = new List<Emotion>
    {
        new Emotion
        {
            emotionName = "Happy",
            activeShapes = new List<BlendShapeData>
            {
                new BlendShapeData { shapeName = "Brow_Raise_Inner_L", targetWeight = 10f },
                new BlendShapeData { shapeName = "Brow_Raise_Inner_R", targetWeight = 10f },
                new BlendShapeData { shapeName = "Eye_Wide_L",         targetWeight = 10f },
                new BlendShapeData { shapeName = "Eye_Wide_R",         targetWeight = 10f },
                new BlendShapeData { shapeName = "Cheek_Raise_L",      targetWeight = 40f },
                new BlendShapeData { shapeName = "Cheek_Raise_R",      targetWeight = 40f },
                new BlendShapeData { shapeName = "Mouth_Smile_L",      targetWeight = 60f },
                new BlendShapeData { shapeName = "Mouth_Smile_R",      targetWeight = 60f },
            }
        },
        new Emotion
        {
            emotionName = "Angry",
            activeShapes = new List<BlendShapeData>
            {
                new BlendShapeData { shapeName = "Brow_Drop_L",    targetWeight = 100f },
                new BlendShapeData { shapeName = "Brow_Drop_R",    targetWeight = 100f },
                new BlendShapeData { shapeName = "Eye_Wide_L",     targetWeight = 28f  },
                new BlendShapeData { shapeName = "Eye_Wide_R",     targetWeight = 28f  },
                new BlendShapeData { shapeName = "Mouth_Frown_L",  targetWeight = 18f  },
                new BlendShapeData { shapeName = "Mouth_Frown_R",  targetWeight = 18f  },
            }
        },
        new Emotion
        {
            emotionName = "Disgust",
            activeShapes = new List<BlendShapeData>
            {
                new BlendShapeData { shapeName = "Brow_Raise_Outer_L",    targetWeight = 40f  },
                new BlendShapeData { shapeName = "Brow_Raise_Outer_R",    targetWeight = 40f  },
                new BlendShapeData { shapeName = "Eye_Squint_L",          targetWeight = 50f  },
                new BlendShapeData { shapeName = "Eye_Squint_R",          targetWeight = 50f  },
                new BlendShapeData { shapeName = "Nose_Sneer_L",          targetWeight = 100f },
                new BlendShapeData { shapeName = "Nose_Sneer_R",          targetWeight = 100f },
                new BlendShapeData { shapeName = "Cheek_Raise_L",         targetWeight = 80f  },
                new BlendShapeData { shapeName = "Cheek_Raise_R",         targetWeight = 80f  },
                new BlendShapeData { shapeName = "Cheek_Puff_L",          targetWeight = 20f  },
                new BlendShapeData { shapeName = "Cheek_Puff_R",          targetWeight = 20f  },
                new BlendShapeData { shapeName = "Mouth_Frown_L",         targetWeight = 20f  },
                new BlendShapeData { shapeName = "Mouth_Frown_R",         targetWeight = 20f  },
                new BlendShapeData { shapeName = "Mouth_Pucker_Up_L",     targetWeight = 30f  },
                new BlendShapeData { shapeName = "Mouth_Pucker_Up_R",     targetWeight = 30f  },
                new BlendShapeData { shapeName = "Mouth_Pucker_Down_L",   targetWeight = 30f  },
                new BlendShapeData { shapeName = "Mouth_Pucker_Down_R",   targetWeight = 30f  },
                new BlendShapeData { shapeName = "Mouth_Roll_In_Upper_L", targetWeight = 20f  },
                new BlendShapeData { shapeName = "Mouth_Roll_In_Upper_R", targetWeight = 20f  },
                new BlendShapeData { shapeName = "Mouth_Up",              targetWeight = 70f  },
            }
        },
        new Emotion
        {
            emotionName = "Sad",
            activeShapes = new List<BlendShapeData>
            {
                new BlendShapeData { shapeName = "Brow_Raise_Inner_L",  targetWeight = 80f },
                new BlendShapeData { shapeName = "Brow_Raise_Inner_R",  targetWeight = 80f },
                new BlendShapeData { shapeName = "Eye_Wide_L",          targetWeight = 20f },
                new BlendShapeData { shapeName = "Eye_Wide_R",          targetWeight = 20f },
                new BlendShapeData { shapeName = "Mouth_Frown_L",       targetWeight = 20f },
                new BlendShapeData { shapeName = "Mouth_Frown_R",       targetWeight = 20f },
                new BlendShapeData { shapeName = "Mouth_Pucker_Up_L",   targetWeight = 10f },
                new BlendShapeData { shapeName = "Mouth_Pucker_Up_R",   targetWeight = 10f },
                new BlendShapeData { shapeName = "Mouth_Pucker_Down_L", targetWeight = 10f },
                new BlendShapeData { shapeName = "Mouth_Pucker_Down_R", targetWeight = 10f },
                new BlendShapeData { shapeName = "Mouth_Down",          targetWeight = 20f },
            }
        },
        new Emotion
        {
            emotionName = "Surprise",
            activeShapes = new List<BlendShapeData>
            {
                new BlendShapeData { shapeName = "Brow_Raise_Inner_L",  targetWeight = 100f },
                new BlendShapeData { shapeName = "Brow_Raise_Inner_R",  targetWeight = 100f },
                new BlendShapeData { shapeName = "Brow_Raise_Outer_L",  targetWeight = 100f },
                new BlendShapeData { shapeName = "Brow_Raise_Outer_R",  targetWeight = 100f },
                new BlendShapeData { shapeName = "Eye_Wide_L",          targetWeight = 100f },
                new BlendShapeData { shapeName = "Eye_Wide_R",          targetWeight = 100f },
                new BlendShapeData { shapeName = "Mouth_Pucker_Up_L",   targetWeight = 100f },
                new BlendShapeData { shapeName = "Mouth_Pucker_Up_R",   targetWeight = 100f },
                new BlendShapeData { shapeName = "Mouth_Pucker_Down_L", targetWeight = 100f },
                new BlendShapeData { shapeName = "Mouth_Pucker_Down_R", targetWeight = 100f },
                new BlendShapeData { shapeName = "Jaw_Open",            targetWeight = 80f  },
                // V_Lip_Open removed — it's a viseme shape (index 8) used by lip sync
            }
        },
        new Emotion
        {
            emotionName = "Surprise2",
            activeShapes = new List<BlendShapeData>
            {
                new BlendShapeData { shapeName = "Brow_Raise_Inner_L",  targetWeight = 100f },
                new BlendShapeData { shapeName = "Brow_Raise_Inner_R",  targetWeight = 100f },
                new BlendShapeData { shapeName = "Brow_Raise_Outer_L",  targetWeight = 100f },
                new BlendShapeData { shapeName = "Brow_Raise_Outer_R",  targetWeight = 100f },
                new BlendShapeData { shapeName = "Eye_Squint_L",        targetWeight = 30f  },
                new BlendShapeData { shapeName = "Eye_Squint_R",        targetWeight = 30f  },
                new BlendShapeData { shapeName = "Eye_Wide_L",          targetWeight = 90f  },
                new BlendShapeData { shapeName = "Eye_Wide_R",          targetWeight = 90f  },
                new BlendShapeData { shapeName = "Cheek_Raise_L",       targetWeight = 60f  },
                new BlendShapeData { shapeName = "Cheek_Raise_R",       targetWeight = 60f  },
                new BlendShapeData { shapeName = "Mouth_Pucker_Up_L",   targetWeight = 90f  },
                new BlendShapeData { shapeName = "Mouth_Pucker_Up_R",   targetWeight = 90f  },
                new BlendShapeData { shapeName = "Mouth_Pucker_Down_L", targetWeight = 90f  },
                new BlendShapeData { shapeName = "Mouth_Pucker_Down_R", targetWeight = 90f  },
                new BlendShapeData { shapeName = "Merged_Open_Mouth",   targetWeight = 80f  },
                new BlendShapeData { shapeName = "Mouth_Close",         targetWeight = 30f  },
            }
        },
    };

    // Tracks the current displayed weight for each blend shape (our own — never read from mesh)
    private Dictionary<int, float> currentWeights = new Dictionary<int, float>();
    // Tracks where we want to get to
    private Dictionary<int, float> targetWeights  = new Dictionary<int, float>();

    void Start()
    {
        if (faceMesh == null) faceMesh = GetComponent<SkinnedMeshRenderer>();
    }

    /// <summary>
    /// Change Mourad's facial expression with a smooth transition.
    /// Pass "Neutral" to relax the face back to default.
    /// </summary>
    public void PlayEmotion(string emotionName)
    {
        if (faceMesh == null || string.IsNullOrEmpty(emotionName)) return;

        string normalised = char.ToUpper(emotionName[0]) + emotionName.Substring(1).ToLower();
        Debug.Log($"[Emotion] Playing: {normalised}");

        // Build new target weights
        Dictionary<int, float> newTargets = new Dictionary<int, float>();

        if (normalised != "Neutral")
        {
            Emotion e = emotions.Find(x => x.emotionName == normalised);
            if (e == null) { Debug.LogWarning($"[Emotion] Unknown emotion: '{normalised}'"); return; }

            foreach (var shape in e.activeShapes)
            {
                int idx = faceMesh.sharedMesh.GetBlendShapeIndex(shape.shapeName);
                if (idx >= 0) newTargets[idx] = shape.targetWeight;
                else Debug.LogWarning($"[Emotion] Blendshape not found: '{shape.shapeName}'");
            }
        }
        // "Neutral" leaves newTargets empty → all blend to 0

        targetWeights = newTargets;

        // Restart the transition coroutine
        StopAllCoroutines();
        StartCoroutine(TransitionCoroutine());
    }

    // Runs once per PlayEmotion call, drives all blend shapes to their targets
    // then stops. Uses yield return null so it runs in the normal Update phase
    // which is guaranteed to execute (no LateUpdate dependency).
    private System.Collections.IEnumerator TransitionCoroutine()
    {
        if (faceMesh == null) yield break;

        // Snapshot where every active blend shape currently is
        var allIndices = new System.Collections.Generic.HashSet<int>(targetWeights.Keys);
        foreach (int i in currentWeights.Keys) allIndices.Add(i);

        var startWeights = new Dictionary<int, float>();
        foreach (int i in allIndices)
            startWeights[i] = currentWeights.ContainsKey(i) ? currentWeights[i] : 0f;

        float elapsed = 0f;
        float duration = 1f / Mathf.Max(transitionSpeed, 0.1f);

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);

            foreach (int i in allIndices)
            {
                float from = startWeights.ContainsKey(i) ? startWeights[i] : 0f;
                float to   = targetWeights.ContainsKey(i)  ? targetWeights[i]  : 0f;
                float val  = Mathf.Lerp(from, to, t);
                currentWeights[i] = val;
                faceMesh.SetBlendShapeWeight(i, val);
            }

            yield return null;
        }

        // Snap to final values
        foreach (int i in allIndices)
        {
            float final = targetWeights.ContainsKey(i) ? targetWeights[i] : 0f;
            currentWeights[i] = final;
            faceMesh.SetBlendShapeWeight(i, final);
        }
    }

    // --- QUICK TEST BUTTONS (right-click this script in Inspector) ---
    [ContextMenu("Test Angry")]   public void TestAngry()   { PlayEmotion("Angry");   }
    [ContextMenu("Test Happy")]   public void TestHappy()   { PlayEmotion("Happy");   }
    [ContextMenu("Test Sad")]     public void TestSad()     { PlayEmotion("Sad");     }
    [ContextMenu("Test Disgust")] public void TestDisgust() { PlayEmotion("Disgust"); }
    [ContextMenu("Test Surprise")]public void TestSurprise(){ PlayEmotion("Surprise");}
    [ContextMenu("Test Neutral")] public void TestNeutral() { PlayEmotion("Neutral"); }

    [ContextMenu(">>> DUMP All Blend Shape Names <<<")]
    public void DumpBlendShapeNames()
    {
        if (faceMesh == null || faceMesh.sharedMesh == null)
        {
            Debug.LogError("[Emotion] faceMesh is not assigned! Drag the SkinnedMeshRenderer into the faceMesh field.");
            return;
        }
        int count = faceMesh.sharedMesh.blendShapeCount;
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.AppendLine($"[Emotion] {count} blend shapes on '{faceMesh.name}':");
        for (int i = 0; i < count; i++)
            sb.AppendLine($"  [{i}] {faceMesh.sharedMesh.GetBlendShapeName(i)}");
        Debug.Log(sb.ToString());
    }
}
