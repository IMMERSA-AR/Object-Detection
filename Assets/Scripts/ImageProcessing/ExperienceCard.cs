using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Attached to the ExperienceCard prefab ROOT.
/// ExperienceManager calls Setup() on each spawned card to fill it with data.
///
/// PREFAB STRUCTURE REQUIRED:
///   ExperienceCard (root)
///   ├── Image component          ← card background
///   ├── Button component         ← makes whole card clickable  
///   ├── ExperienceCard script    ← this script
///   ├── TitleText                ← TextMeshProUGUI
///   ├── DescriptionText          ← TextMeshProUGUI
///   └── SelectButton             ← Button (optional separate button)
///       └── Text (TMP)           ← says "Select" or "Begin"
/// </summary>
public class ExperienceCard : MonoBehaviour
{
    [Header("Wire these to children in the Prefab")]
    public TextMeshProUGUI titleText;
    public TextMeshProUGUI descriptionText;

    [Tooltip("The clickable button. Can be the card root Button or a child SelectButton.")]
    public Button selectButton;

    private ExperienceConfig _config;
    private ExperienceManager _manager;

    /// <summary>
    /// Called by ExperienceManager right after Instantiating this card.
    /// Fills the UI with the config's data and wires the button.
    /// </summary>
    public void Setup(ExperienceConfig config, ExperienceManager manager)
    {
        _config = config;
        _manager = manager;

        // Fill title
        if (titleText != null)
            titleText.text = config.experienceName;
        else
            Debug.LogWarning($"[ExperienceCard] titleText is not assigned on the prefab! Card for '{config.experienceName}' won't show a title.");

        // Fill description
        if (descriptionText != null)
            descriptionText.text = config.description;
        else
            Debug.LogWarning($"[ExperienceCard] descriptionText is not assigned on the prefab!");

        // Wire button — try assigned button first, fall back to Button on this root GameObject
        if (selectButton == null)
            selectButton = GetComponent<Button>();

        if (selectButton != null)
        {
            selectButton.onClick.RemoveAllListeners();
            selectButton.onClick.AddListener(OnCardSelected);
            Debug.Log($"[ExperienceCard] Button wired for '{config.experienceName}'");
        }
        else
        {
            Debug.LogError($"[ExperienceCard] No Button found on card for '{config.experienceName}'! " +
                           "Add a Button component to the prefab root or assign selectButton in the Inspector.");
        }
    }

    private void OnCardSelected()
    {
        Debug.Log($"[ExperienceCard] '{_config.experienceName}' selected!");
        _manager.SelectExperience(_config);
    }
}