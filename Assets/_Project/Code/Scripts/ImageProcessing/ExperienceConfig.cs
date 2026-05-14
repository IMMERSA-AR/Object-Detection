using UnityEngine;

[CreateAssetMenu(fileName = "NewExperience", menuName = "MuradXR/Experience Config")]
public class ExperienceConfig : ScriptableObject
{
    [Header("Experience Type")]
    [Tooltip("CharacterPlacement = YOLO detects anchor then spawns Murad.\nLectureHall = constant scene + lecture audio + then Murad Q&A.")]
    public ExperienceType experienceType = ExperienceType.CharacterPlacement;

    [Header("Display")]
    [Tooltip("Name shown as the card title")]
    public string experienceName = "New Experience";

    [Tooltip("Short description shown on the card")]
    [TextArea(2, 3)]
    public string description = "Description here.";

    [Tooltip("Text on the button — make it immersive! e.g. 'Enter the Hall'")]
    public string buttonLabel = "Begin";

    [Header("Anchor Detection")]
    [Tooltip("Which YOLO label to detect as the anchor object")]
    public YOLOv9Labels anchorLabel = YOLOv9Labels.chair;

    [Tooltip("Minimum YOLO confidence to accept this anchor (0-1)")]
    [Range(0f, 1f)]
    public float minConfidence = 0.5f;

    [Tooltip("Maximum distance from player to accept the anchor (meters)")]
    public float maxAnchorDistance = 3.0f;

    [Header("Murad Behaviour")]
    public MuradBehaviour muradBehaviour = MuradBehaviour.SitOnAnchor;

    [Tooltip("How far beside the anchor Murad stands (StandBesideAnchor only)")]
    public float standBesideOffset = 0.5f;

    [Header("Position Fine-Tuning")]
    [Tooltip("Height offset relative to detected anchor surface")]
    public float heightOffset = 0f;

    [Tooltip("Forward/backward offset from anchor center")]
    public float forwardOffset = 0f;

[Header("Lecture Hall (ExperienceType = LectureHall only)")]
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
             "Animator Controller's IsSitting bool is used instead (may look different).")]
    public AnimationClip muradSittingClip;

    [Tooltip("Prefab for the 1918 doctor/professor NPC (must have HistoricalNPCController)")]
    public GameObject doctorPrefab;

    [Tooltip("Number of student rows")]
    public int studentRows = 2;

    [Tooltip("Number of seats per row")]
    public int studentsPerRow = 3;

    [Header("Obelisk (ExperienceType = Obelisk only)")]
    [Tooltip("Audio clip played on loop while scanning for the obelisk.\n" +
             "Leave empty for silence during detection.")]
    public AudioClip obeliskScanningAudioClip;

    [Tooltip("Audio clip played once when the obelisk is confirmed detected.")]
    public AudioClip obeliskDetectedAudioClip;

    [Tooltip("Text shown on the scanning UI while looking for the obelisk.\n" +
             "e.g. 'Point the camera at the obelisk…'")]
    public string obeliskGuidanceText = "Point the camera at the obelisk…";
}

public enum MuradBehaviour
{
    SitOnAnchor,        // Walk to anchor and sit — e.g. lecture room chair
    StandBesideAnchor   // Walk beside anchor and stand — e.g. obelisk
}

public enum ExperienceType
{
    CharacterPlacement, // YOLO detects anchor → Murad walks to it
    LectureHall,        // Constant 1918 scene → lecture audio → Murad Q&A
    Obelisk             // Classical IP detects obelisk → characters spawn around it
}

/// <summary>
/// One entry in ExperienceConfig.studentPrefabVariants — pairs a character
/// prefab with an optional per-character sitting clip.
/// </summary>
[System.Serializable]
public class StudentVariant
{
    [Tooltip("Character prefab to spawn for this seat.")]
    public GameObject prefab;

    [Tooltip("Optional sitting clip used ONLY for this character. " +
             "Empty = fall back to the prefab's own idleClip, then to the shared studentSittingClip.")]
    public AnimationClip sittingClip;
}