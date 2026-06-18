using UnityEngine;

[CreateAssetMenu(fileName = "NewExperience", menuName = "MuradXR/Experience Config")]
public class ExperienceConfig : ScriptableObject
{

    [Header("Lecture Hall")]
    public AudioClip introAudioClip;
    public AudioClip chairDetectionAudioClip;
    public AudioClip lectureAudioClip;
    [TextArea(3, 8)]
    public string lectureAudioTranscript;
    public GameObject mainMuradPrefab;
    public GameObject studentPrefab;    // used as fallback if the student variants are empty
    public StudentVariant[] studentPrefabVariants;
    public AnimationClip studentSittingClip;
    public AnimationClip muradSittingClip;
    public GameObject doctorPrefab;
    public AnimationClip doctorIdleClip;
    public AnimationClip doctorTalkingClip;
    public AnimationClip doctorStandingAfterLectureClip;
    public int studentRows = 2;

    public int studentsPerRow = 3;

    [Header("Obelisk")]
    public AudioClip obeliskScanningAudioClip;
    public AudioClip obeliskDetectedAudioClip;
    public string obeliskGuidanceText = "Point the camera at the obelisk…";
}

[System.Serializable]
public class StudentVariant
{
    public GameObject prefab;
    public AnimationClip sittingClip;
}
