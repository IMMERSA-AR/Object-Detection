using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

/// <summary>
/// Attached to the ExperienceCard prefab ROOT.
///
/// PREFAB STRUCTURE:
///   ExperienceCard  (Image + Button + ExperienceCard script)
///   ├── TitleText        (TextMeshProUGUI)
///   ├── DescriptionText  (TextMeshProUGUI)
///   └── SelectButton     (Button)
///       └── ButtonLabel  (TextMeshProUGUI) ← shows config.buttonLabel
/// </summary>
public class ExperienceCard : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    [Header("Wire these in the Prefab Inspector")]
    public TextMeshProUGUI titleText;
    public TextMeshProUGUI descriptionText;
    public Button selectButton;

    [Tooltip("The TMP text inside the SelectButton that shows the action label")]
    public TextMeshProUGUI buttonLabelText;

    [Header("Hover Style")]
    public Color normalColor = new Color(0.05f, 0.11f, 0.18f, 0.9f);
    public Color hoveredColor = new Color(0.20f, 0.15f, 0.05f, 0.95f);

    private Image _cardImage;
    private ExperienceConfig _config;
    private ExperienceManager _manager;

    private void Awake()
    {
        _cardImage = GetComponent<Image>();
    }

    /// <summary>
    /// Called by ExperienceManager right after Instantiating this card.
    /// </summary>
    public void Setup(ExperienceConfig config, ExperienceManager manager)
    {
        _config = config;
        _manager = manager;

        // ── Title ──────────────────────────────────────────────────
        if (titleText != null)
            titleText.text = config.experienceName;
        else
            Debug.LogWarning($"[ExperienceCard] titleText not assigned for '{config.experienceName}'");

        // ── Description ────────────────────────────────────────────
        if (descriptionText != null)
            descriptionText.text = config.description;
        else
            Debug.LogWarning($"[ExperienceCard] descriptionText not assigned for '{config.experienceName}'");

        // ── Button label (immersive text from config) ──────────────
        if (buttonLabelText != null)
            buttonLabelText.text = config.buttonLabel;   // e.g. "Enter the Hall"
        else
        {
            // Fallback: try to find TMP inside the button automatically
            if (selectButton != null)
            {
                var tmp = selectButton.GetComponentInChildren<TextMeshProUGUI>();
                if (tmp != null) tmp.text = config.buttonLabel;
            }
        }

        // ── Wire button ────────────────────────────────────────────
        // Prefer assigned selectButton, fall back to Button on this root object
        if (selectButton == null)
            selectButton = GetComponent<Button>();

        if (selectButton != null)
        {
            selectButton.onClick.RemoveAllListeners();
            selectButton.onClick.AddListener(OnCardSelected);
            Debug.Log($"[ExperienceCard] Button '{config.buttonLabel}' wired for '{config.experienceName}'");
        }
        else
        {
            Debug.LogError($"[ExperienceCard] No Button found for '{config.experienceName}'! " +
                           "Add a Button component to the prefab root or assign selectButton.");
        }

        // Reset card color
        if (_cardImage != null)
            _cardImage.color = normalColor;
    }

    // ── Button click ──────────────────────────────────────────────

    public void OnCardSelected()
    {
        Debug.Log($"[ExperienceCard] '{_config.experienceName}' selected — starting experience.");
        _manager.SelectExperience(_config);
    }

    // ── Hover highlight (works with Quest controller pointer) ─────

    public void OnPointerEnter(PointerEventData eventData)
    {
        if (_cardImage != null)
            _cardImage.color = hoveredColor;
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        if (_cardImage != null)
            _cardImage.color = normalColor;
    }
}