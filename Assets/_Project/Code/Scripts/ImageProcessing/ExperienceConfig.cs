using UnityEngine;

[CreateAssetMenu(fileName = "NewExperience", menuName = "MuradXR/Experience Config")]
public class ExperienceConfig : ScriptableObject
{
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
}

public enum MuradBehaviour
{
    SitOnAnchor,        // Walk to anchor and sit — e.g. lecture room chair
    StandBesideAnchor   // Walk beside anchor and stand — e.g. obelisk
}