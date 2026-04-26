using UnityEngine;
using TMPro;
using UnityEngine.UI;

/// <summary>
/// Manages the floating experience selection menu.
/// Spawns one ExperienceCard per entry in the `experiences` array.
/// </summary>
public class ExperienceManager : MonoBehaviour
{
    public static ExperienceManager Instance { get; private set; }

    [Header("Experiences")]
    [Tooltip("Drag your LectureRoom and Obelisk ScriptableObject assets here")]
    public ExperienceConfig[] experiences;

    [Header("Menu UI")]
    [Tooltip("The world-space Canvas (MenusCanvas in your hierarchy)")]
    public Canvas menuCanvas;

    [Tooltip("The CardContainer RectTransform inside MenusCanvas")]
    public RectTransform cardContainer;

    [Tooltip("The ExperienceCard prefab from your Project window")]
    public GameObject cardPrefab;

    [Tooltip("How far in front of the player the menu floats (meters)")]
    public float menuDistance = 1.5f;

    [Tooltip("Height offset from camera level (negative = lower)")]
    public float menuHeightOffset = -0.1f;

    [Header("Scene References")]
    [Tooltip("The GameObject that has ObjectDetector on it")]
    public ObjectDetector objectDetector;

    [Tooltip("The GameObject that has ObjectStamper on it")]
    public ObjectStamper objectStamper;

    [Tooltip("Optional scanning UI shown while AI hunts for the anchor")]
    public GameObject scanningUI;

    public ExperienceConfig ActiveConfig { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Start()
    {
        if (objectDetector != null)
            objectDetector.enabled = false;
        else
            Debug.LogWarning("[ExperienceManager] objectDetector not assigned in Inspector.");

        if (scanningUI != null)
            scanningUI.SetActive(false);

        if (!ValidateReferences()) return;

        ShowMenu();
    }

    // ── Validate all Inspector references ───────────────────────────

    private bool ValidateReferences()
    {
        bool ok = true;

        if (menuCanvas == null)
        {
            Debug.LogError("[ExperienceManager] menuCanvas is NULL. Drag MenusCanvas here.");
            ok = false;
        }
        if (cardContainer == null)
        {
            Debug.LogError("[ExperienceManager] cardContainer is NULL. Drag the CardContainer RectTransform here.");
            ok = false;
        }
        if (cardPrefab == null)
        {
            Debug.LogError("[ExperienceManager] cardPrefab is NULL. Drag the ExperienceCard prefab here.");
            ok = false;
        }
        if (experiences == null || experiences.Length == 0)
        {
            Debug.LogError("[ExperienceManager] experiences array is EMPTY. Drag LectureRoom and Obelisk config assets here.");
            ok = false;
        }

        if (!ok)
            Debug.LogError("[ExperienceManager] Fix the above missing references. Menu will not appear until all are assigned.");

        return ok;
    }

    // ── Show Menu ────────────────────────────────────────────────────

    public void ShowMenu()
    {
        // Clear any previously spawned cards
        foreach (Transform child in cardContainer)
            Destroy(child.gameObject);

        Debug.Log($"[ExperienceManager] Building menu with {experiences.Length} experience(s)...");

        foreach (var config in experiences)
        {
            if (config == null)
            {
                Debug.LogWarning("[ExperienceManager] One entry in experiences array is null — skipping.");
                continue;
            }

            GameObject cardGO = Instantiate(cardPrefab, cardContainer);
            ExperienceCard card = cardGO.GetComponent<ExperienceCard>();

            if (card == null)
            {
                Debug.LogError("[ExperienceManager] cardPrefab has no ExperienceCard component! Add the ExperienceCard script to the prefab root.");
                Destroy(cardGO);
                continue;
            }

            card.Setup(config, this);
            Debug.Log($"[ExperienceManager] Card spawned for '{config.experienceName}'");
        }

        PositionMenuInFrontOfPlayer();
        menuCanvas.gameObject.SetActive(true);
        Debug.Log("[ExperienceManager] Menu visible.");
    }

    private void PositionMenuInFrontOfPlayer()
    {
        if (Camera.main == null)
        {
            Debug.LogWarning("[ExperienceManager] No main camera found.");
            return;
        }

        Transform cam = Camera.main.transform;

        // Flatten forward so menu doesn't tilt with head pitch
        Vector3 flatForward = new Vector3(cam.forward.x, 0f, cam.forward.z);
        if (flatForward.sqrMagnitude < 0.001f)
            flatForward = Vector3.forward;
        flatForward.Normalize();

        // Place in front of player at eye level
        Vector3 menuPos = cam.position
            + flatForward * menuDistance
            + Vector3.up * menuHeightOffset;

        menuCanvas.transform.position = menuPos;

        // Face the canvas TOWARD the camera so it's always head-on
        // We point the canvas Z-axis back at the player
        Vector3 dirToCamera = cam.position - menuPos;
        dirToCamera.y = 0f;
        if (dirToCamera.sqrMagnitude > 0.001f)
            menuCanvas.transform.rotation = Quaternion.LookRotation(dirToCamera.normalized);
        else
            menuCanvas.transform.rotation = Quaternion.LookRotation(-flatForward);
    }

    // ── Called by ExperienceCard button ─────────────────────────────

    public void SelectExperience(ExperienceConfig config)
    {
        ActiveConfig = config;
        Debug.Log($"[ExperienceManager] Experience selected: '{config.experienceName}'");

        menuCanvas.gameObject.SetActive(false);

        if (scanningUI != null)
            scanningUI.SetActive(true);

        if (objectStamper != null)
            objectStamper.ResetForNewExperience(config);
        else
            Debug.LogWarning("[ExperienceManager] objectStamper not assigned — Murad won't know what to do.");

        if (objectDetector != null)
            objectDetector.enabled = true;
        else
            Debug.LogWarning("[ExperienceManager] objectDetector not assigned — AI won't start.");
    }

    // ── Called by ObjectStamper after Murad is placed ────────────────

    public void OnMuradPlaced()
    {
        if (scanningUI != null)
            scanningUI.SetActive(false);

        Debug.Log("[ExperienceManager] Murad successfully placed.");
    }

    // ── Return to menu (wire to a restart button if needed) ──────────

    public void ReturnToMenu()
    {
        if (objectDetector != null) objectDetector.enabled = false;
        if (objectStamper != null) objectStamper.ResetForNewExperience(null);
        if (scanningUI != null) scanningUI.SetActive(false);
        ShowMenu();
    }
}