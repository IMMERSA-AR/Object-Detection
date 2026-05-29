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
                new BlendShapeData { shapeName = "V_Lip_Open",          targetWeight = 80f  },
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

    // Stores what the current target is for each facial muscle
    private Dictionary<int, float> targetWeights = new Dictionary<int, float>();

    void Start()
    {
        // Try to auto-find the mesh if you forgot to drag it in
        if (faceMesh == null) faceMesh = GetComponent<SkinnedMeshRenderer>();
    }

    void Update()
    {
        if (faceMesh == null || faceMesh.sharedMesh == null) return;

        // Every frame, smoothly move all facial muscles toward their target
        for (int i = 0; i < faceMesh.sharedMesh.blendShapeCount; i++)
        {
            float currentWeight = faceMesh.GetBlendShapeWeight(i);

            // If the muscle is part of the current emotion, use its target. Otherwise, relax it to 0.
            float target = targetWeights.ContainsKey(i) ? targetWeights[i] : 0f;

            // Only do the math if the muscle isn't already at its destination
            if (Mathf.Abs(currentWeight - target) > 0.1f)
            {
                float newWeight = Mathf.Lerp(currentWeight, target, Time.deltaTime * transitionSpeed);
                faceMesh.SetBlendShapeWeight(i, newWeight);
            }
        }
    }

    // Call this function from your other scripts to change his face!
    public void PlayEmotion(string emotionName)
    {
        if (emotionName == "Neutral")
        {
            targetWeights.Clear(); // Clears all targets, relaxing the whole face to 0
            return;
        }

        Emotion targetEmotion = emotions.Find(e => e.emotionName == emotionName);

        if (targetEmotion != null)
        {
            targetWeights.Clear(); // Reset face before applying new emotion

            foreach (var shape in targetEmotion.activeShapes)
            {
                int index = faceMesh.sharedMesh.GetBlendShapeIndex(shape.shapeName);
                if (index != -1)
                {
                    targetWeights[index] = shape.targetWeight;
                }
                else
                {
                    Debug.LogWarning("Cannot find a facial muscle named: " + shape.shapeName);
                }
            }
        }
        else
        {
            Debug.LogWarning("You tried to play an emotion that doesn't exist: " + emotionName);
        }
    }

    // --- QUICK TEST BUTTONS ---
    // You can right-click this script in the Inspector to trigger these instantly!
    [ContextMenu("Test Angry Emotion")]
    public void TestAngry() { PlayEmotion("Angry"); }

    [ContextMenu("Test Happy Emotion")]
    public void TestHappy() { PlayEmotion("Happy"); }

    [ContextMenu("Test Sad Emotion")]
    public void TestSad() { PlayEmotion("Sad"); }

    [ContextMenu("Test Disgust Emotion")]
    public void TestDisgust() { PlayEmotion("Disgust"); }

    [ContextMenu("Test Surprise Emotion")]
    public void TestSurprise() { PlayEmotion("Surprise"); }

    [ContextMenu("Test Neutral (Relax Face)")]
    public void TestNeutral() { PlayEmotion("Neutral"); }
}
