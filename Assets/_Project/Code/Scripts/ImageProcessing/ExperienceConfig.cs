using UnityEngine;

[CreateAssetMenu(fileName = "NewExperience", menuName = "MuradXR/Experience Config")]
public class ExperienceConfig : ScriptableObject
{

    [Header("Lecture Hall")]
    [Tooltip("Optional intro audio clip played the moment the user clicks Begin.\n" +
                 "Chair detection waits until this clip finishes before starting.\n" +
                 "Leave empty to start chair detection immediately.")]
    public AudioClip introAudioClip;

    [Tooltip("Audio that loops during the chair-detection phase (while students are being placed).\n" +
             "Stops automatically the moment the doctor appears and the lecture is ready.\n" +
             "Leave empty for silence during detection.")]
    public AudioClip chairDetectionAudioClip;

    [Tooltip("The pre-recorded lecture audio script")]
    public AudioClip lectureAudioClip;

    [Tooltip("Full transcript of the lecture audio clip.\n" +
             "Used by CustomLipSyncContext for text-guided lip sync.\n" +
             "Leave empty to fall back to raw MFCC (mouth still moves, but less accurate).")]
    [TextArea(3, 8)]
    public string lectureAudioTranscript;

    [Tooltip("Prefab for the MAIN Murad (who sits, then stands up to talk)")]
    public GameObject mainMuradPrefab;

    [Tooltip("Prefab for the generic student NPCs (must have HistoricalNPCController).\n" +
             "Used as a fallback when studentPrefabVariants is empty.")]
    public GameObject studentPrefab;

    [Tooltip("Optional pool of student variants. Each entry pairs a character prefab with\n" +
             "an optional sitting clip just for that character. If at least one entry has a\n" +
             "prefab, students are picked from this list (shuffled). The single studentPrefab\n" +
             "above is used only when this list is empty.\n\n" +
             "Per-character clip resolution order:\n" +
             "  1. Variant's own Sitting Clip (set here)\n" +
             "  2. The prefab's existing HistoricalNPCController.idleClip\n" +
             "  3. The shared Student Sitting Clip below")]
    public StudentVariant[] studentPrefabVariants;

    [Tooltip("Shared sitting clip used as a last-resort fallback when neither the variant nor\n" +
             "the prefab itself supplies one. Drag a Mixamo 'Sitting Idle' clip here.")]
    public AnimationClip studentSittingClip;

    [Tooltip("Sitting clip played on the MAIN Murad while he is seated during the lecture.\n" +
             "Drag the same Mixamo 'Sitting Idle' clip you use for students, or a separate one.\n" +
             "If left empty, falls back to studentSittingClip. If both are empty, the\n" +
             "Animator Controller's IsSitting bool is used instead (may look different).\n" +
             "IMPORTANT: the FBX must be imported as Humanoid (Rig tab → Animation Type → Humanoid).")]
    public AnimationClip muradSittingClip;

    [Tooltip("Prefab for the 1918 doctor/professor NPC (must have HistoricalNPCController)")]
    public GameObject doctorPrefab;

    [Header("Doctor Animation Clips")]
    [Tooltip("Standing idle clip played while the doctor is waiting / not lecturing.\n" +
             "If empty, falls back to whatever idleClip is set on the prefab itself.")]
    public AnimationClip doctorIdleClip;

    [Tooltip("Talking / lecturing clip played while the lecture audio is playing.\n" +
             "If empty, falls back to whatever talkingClip is set on the prefab itself.")]
    public AnimationClip doctorTalkingClip;

    [Tooltip("Standing idle clip played AFTER the lecture ends (doctor stops talking).\n" +
             "If empty, falls back to doctorIdleClip, then to the prefab's standingAfterLectureClip.")]
    public AnimationClip doctorStandingAfterLectureClip;

    [Tooltip("Number of student rows")]
    public int studentRows = 2;

    [Tooltip("Number of seats per row")]
    public int studentsPerRow = 3;

    [Header("Obelisk")]
    [Tooltip("Audio clip that loops while scanning for the obelisk.")]
    public AudioClip obeliskScanningAudioClip;

    [Tooltip("Audio clip played once when the obelisk is confirmed detected.")]
    public AudioClip obeliskDetectedAudioClip;

    [Tooltip("Guidance text shown on the scanning UI while looking for the obelisk.")]
    public string obeliskGuidanceText = "Point the camera at the obelisk…";
}

[System.Serializable]
public class StudentVariant
{
    [Tooltip("Character prefab to spawn for this seat.")]
    public GameObject prefab;

    [Tooltip("Optional sitting clip used ONLY for this character. " +
             "Empty = fall back to the prefab's own idleClip, then to the shared studentSittingClip.")]
    public AnimationClip sittingClip;
}
