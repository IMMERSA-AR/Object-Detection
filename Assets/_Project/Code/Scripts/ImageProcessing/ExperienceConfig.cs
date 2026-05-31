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

    [Tooltip("Standing idle clip played while Murad stands up and during Q&A after the lecture.\n" +
             "Download 'Standing Idle' or 'Breathing Idle' from Mixamo, set Rig → Humanoid, drag here.\n" +
             "If empty, the Animator Controller (IsStanding bool) handles standing — requires the\n" +
             "Animator Controller to have a proper Standing Idle state.")]
    public AnimationClip muradStandingClip;

    [Tooltip("Walking clip played while Murad walks toward the user after the lecture.\n" +
             "Download 'Walking' from Mixamo, set Rig → Humanoid, drag here.\n" +
             "If empty AND muradStandingClip is set, Murad glides (standing idle while moving).\n" +
             "If both are empty, the Animator Controller (IsWalking bool) handles walking.")]
    public AnimationClip muradWalkingClip;

    [Tooltip("Prefab for the 1918 doctor/professor NPC (must have HistoricalNPCController)")]
    public GameObject doctorPrefab;

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
