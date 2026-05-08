using System.Collections;
using System.Collections.Generic;
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

    [Header("Lecture Hall")]
    [Tooltip("Drag the LectureHallManager GameObject here for lecture hall experiences")]
    public LectureHallManager lectureHallManager;

    [Tooltip("PRIMARY: Drag the ChairMeshDetector GameObject here.\n" +
             "Queries MRUK Scene Mesh for horizontal surfaces — no user interaction needed.")]
    public ChairMeshDetector chairMeshDetector;

    [Tooltip("FALLBACK: Drag the ChairYOLODetector GameObject here.\n" +
             "Used only when MRUK returns zero chairs (asks the user to look at chairs).")]
    public ChairYOLODetector chairYOLODetector;

    [Tooltip("Text shown on the scanningUI while chair scan is running")]
    public TMPro.TextMeshProUGUI chairScanLabel;

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

        // Hide the canvas immediately — ShowMenuDelayed will re-enable it
        // once head-tracking is stable (avoids the menu appearing at origin).
        if (menuCanvas != null)
            menuCanvas.gameObject.SetActive(false);

        if (!ValidateReferences()) return;

        // Hide MRUK room-mesh visualization (the blue overlay). The GlobalMeshAnchor
        // is created at runtime by MRUK — subscribe to the room-created event so we
        // can disable its MeshRenderer as soon as it appears.
        StartCoroutine(HideMRUKVisualizationWhenReady());

        StartCoroutine(ShowMenuDelayed());
    }

    /// <summary>
    /// Waits for MRUK to finish loading the room, then disables every MeshRenderer
    /// that belongs to MRUK-spawned anchors (GlobalMeshAnchor, room walls, etc.).
    /// This removes the blue room-mesh overlay without affecting scene understanding data.
    /// </summary>
    private IEnumerator HideMRUKVisualizationWhenReady()
    {
        // Wait until MRUK has a valid room (can take a few frames on-device).
        float timeout = 10f;
        float elapsed = 0f;
        while (elapsed < timeout)
        {
            yield return null;
            elapsed += Time.deltaTime;

            if (Meta.XR.MRUtilityKit.MRUK.Instance != null &&
                Meta.XR.MRUtilityKit.MRUK.Instance.GetCurrentRoom() != null)
                break;
        }

        if (Meta.XR.MRUtilityKit.MRUK.Instance == null) yield break;

        // Disable MeshRenderers on the MRUK root and all its runtime children.
        // This covers GlobalMeshAnchor, EffectMesh, and any room-wall visualizers.
        Meta.XR.MRUtilityKit.MRUK mruk = Meta.XR.MRUtilityKit.MRUK.Instance;
        foreach (MeshRenderer mr in mruk.GetComponentsInChildren<MeshRenderer>(includeInactive: true))
        {
            mr.enabled = false;
            Debug.Log($"[ExperienceManager] Hid MRUK mesh renderer: {mr.gameObject.name}");
        }

        Debug.Log("[ExperienceManager] MRUK visualization hidden.");
    }

    /// <summary>
    /// Waits until the camera has a valid tracked position before showing the menu.
    /// On Meta Quest the first frame may still be at the default (0,0,0) origin.
    /// </summary>
    private IEnumerator ShowMenuDelayed()
    {
        // Wait for camera tracking to produce a non-origin position.
        // Timeout after 3 s so the menu always appears even if tracking is slow.
        float timeout = 3f;
        float elapsed = 0f;

        while (elapsed < timeout)
        {
            yield return null;   // wait one frame
            elapsed += Time.deltaTime;

            if (Camera.main != null && Camera.main.transform.position.sqrMagnitude > 0.01f)
                break;   // tracking has kicked in
        }

        // One extra frame for stability
        yield return null;

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

    // private void PositionMenuInFrontOfPlayer()
    // {
    //     if (Camera.main == null)
    //     {
    //         Debug.LogWarning("[ExperienceManager] No main camera found.");
    //         return;
    //     }

    //     Transform cam = Camera.main.transform;

    //     // Flatten forward so menu doesn't tilt with head pitch
    //     Vector3 flatForward = new Vector3(cam.forward.x, 0f, cam.forward.z);
    //     if (flatForward.sqrMagnitude < 0.001f)
    //         flatForward = Vector3.forward;
    //     flatForward.Normalize();

    //     // Place in front of player at eye level
    //     Vector3 menuPos = cam.position
    //         + flatForward * menuDistance
    //         + Vector3.up * menuHeightOffset;

    //     menuCanvas.transform.position = menuPos;
    // }

    private void PositionMenuInFrontOfPlayer()
    {
        if (Camera.main == null)
        {
            Debug.LogWarning("[ExperienceManager] No main camera found.");
            return;
        }

        Transform cam = Camera.main.transform;

        // Flatten camera forward — ignore head tilt up/down
        Vector3 flatForward = new Vector3(cam.forward.x, 0f, cam.forward.z);
        if (flatForward.sqrMagnitude < 0.001f)
            flatForward = Vector3.forward;
        flatForward.Normalize();

        // Position the menu in front of the player.
        // X and Z: directly in front along the flat forward direction.
        // Y: locked to camera eye height + menuHeightOffset.
        //    menuHeightOffset = 0   → menu centre at exact eye level
        //    menuHeightOffset = -0.2 → menu centre 20 cm below eye level (comfortable gaze angle)
        Vector3 menuPos = new Vector3(
            cam.position.x + flatForward.x * menuDistance,
            cam.position.y + menuHeightOffset,
            cam.position.z + flatForward.z * menuDistance
        );

        menuCanvas.transform.position = menuPos;

        // Canvas rotation: local +Z must match the camera's flat forward direction.
        // This places the canvas's readable face (-Z side) toward the user.
        // DO NOT add 180° — that flips the canvas away, making text appear mirrored.
        float yaw = Mathf.Atan2(flatForward.x, flatForward.z) * Mathf.Rad2Deg;
        menuCanvas.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
    }
    // ── Called by ExperienceCard button ─────────────────────────────

    public void SelectExperience(ExperienceConfig config)
    {
        ActiveConfig = config;
        Debug.Log($"[ExperienceManager] Experience selected: '{config.experienceName}'");

        menuCanvas.gameObject.SetActive(false);

        // ── Lecture Hall path ────────────────────────────────────────
        if (config.experienceType == ExperienceType.LectureHall)
        {
            if (lectureHallManager == null)
            {
                Debug.LogError("[ExperienceManager] lectureHallManager not assigned!");
                return;
            }

            StartCoroutine(BeginLectureHallSequence(config));
            return;
        }

        // ── Standard character-placement path ────────────────────────
        StartMuradDetectionPhase(config);
    }

    /// <summary>
    /// Plays the optional intro audio (if assigned), waits for it to finish,
    /// then kicks off the chair-detection pipeline. Reuses the
    /// LectureHallManager's existing AudioSource so no extra setup is needed.
    /// </summary>
    private IEnumerator BeginLectureHallSequence(ExperienceConfig config)
    {
        // ── 1. Intro audio ──────────────────────────────────────────
        AudioSource src = lectureHallManager != null ? lectureHallManager.lectureAudioSource : null;
        if (config.introAudioClip != null)
        {
            if (src != null)
            {
                src.loop = false;   // safety — never loop the intro clip
                src.Stop();
                src.clip = config.introAudioClip;
                src.Play();
                Debug.Log($"[ExperienceManager] Playing intro audio: '{config.introAudioClip.name}' " +
                          $"({config.introAudioClip.length:F1}s) — chair detection will start when it ends.");

                // Wait exactly the clip's duration — safe even if the AudioSource
                // has Loop accidentally enabled in the Inspector.
                yield return new WaitForSeconds(config.introAudioClip.length);

                src.Stop();   // ensure it's stopped before detection starts
                Debug.Log("[ExperienceManager] Intro audio finished.");
            }
            else
            {
                Debug.LogWarning("[ExperienceManager] introAudioClip set but lectureHallManager.lectureAudioSource is null — skipping.");
            }
        }

        // ── 2. Chair detection pipeline ─────────────────────────────
        if (chairMeshDetector != null)
        {
            if (scanningUI != null) scanningUI.SetActive(true);
            if (chairScanLabel != null)
                chairScanLabel.text = "Reading room data…";

            Debug.Log("[ExperienceManager] Starting MRUK scene-mesh chair detection (primary).");
            chairMeshDetector.DetectChairs(
                chairs => OnMeshChairsReady(chairs, config),
                status => { if (chairScanLabel != null) chairScanLabel.text = status; },
                pos    => lectureHallManager.SpawnStudentAtChair(pos, config));
        }
        else if (chairYOLODetector != null)
        {
            if (scanningUI != null) scanningUI.SetActive(true);
            if (chairScanLabel != null)
                chairScanLabel.text = "Look at the chairs and hold still for a few seconds…";

            Debug.Log("[ExperienceManager] chairMeshDetector not assigned — using YOLO directly.");
            chairYOLODetector.DetectChairs(chairs => OnChairsReady(chairs, config));
        }
        else
        {
            Debug.LogWarning("[ExperienceManager] No chair detector assigned — using grid spawn.");
            lectureHallManager.StartLecture(config, OnLectureComplete);
        }
    }

    /// <summary>
    /// Called when the PRIMARY MRUK mesh detector finishes.
    /// If it found chairs → start lecture immediately.
    /// If it found nothing → try YOLO fallback (asks user to look at chairs).
    /// If YOLO is also absent → grid spawn.
    /// </summary>
    private void OnMeshChairsReady(List<Vector3> chairs, ExperienceConfig config)
    {
        if (chairs != null && chairs.Count > 0)
        {
            // SUCCESS — hide UI and start straight away.
            if (scanningUI != null) scanningUI.SetActive(false);
            if (chairScanLabel != null) chairScanLabel.text = string.Empty;

            Debug.Log($"[ExperienceManager] MRUK mesh found {chairs.Count} chair(s) — starting lecture.");
            lectureHallManager.StartLectureWithChairs(chairs, config, OnLectureComplete);
            return;
        }

        // MRUK returned nothing — try YOLO fallback.
        Debug.LogWarning("[ExperienceManager] MRUK mesh returned 0 chairs — trying YOLO fallback.");

        if (chairYOLODetector != null)
        {
            if (chairScanLabel != null)
                chairScanLabel.text = "Look at the chairs and hold still for a few seconds…";

            Debug.Log("[ExperienceManager] Starting YOLO chair scan (fallback).");
            chairYOLODetector.DetectChairs(yoloChairs => OnChairsReady(yoloChairs, config));
        }
        else
        {
            // Neither detector found chairs — hide UI and use grid.
            if (scanningUI != null) scanningUI.SetActive(false);
            if (chairScanLabel != null) chairScanLabel.text = string.Empty;

            Debug.LogWarning("[ExperienceManager] No YOLO detector assigned either — using grid spawn.");
            lectureHallManager.StartLecture(config, OnLectureComplete);
        }
    }

    /// <summary>
    /// Called when the YOLO fallback detector finishes.
    /// </summary>
    private void OnChairsReady(List<Vector3> chairs, ExperienceConfig config)
    {
        // Hide scanning UI regardless of result
        if (scanningUI != null) scanningUI.SetActive(false);
        if (chairScanLabel != null) chairScanLabel.text = string.Empty;

        if (chairs != null && chairs.Count > 0)
        {
            Debug.Log($"[ExperienceManager] YOLO found {chairs.Count} chair(s) — starting lecture.");
            lectureHallManager.StartLectureWithChairs(chairs, config, OnLectureComplete);
        }
        else
        {
            Debug.LogWarning("[ExperienceManager] YOLO also found nothing — falling back to grid spawn.");
            lectureHallManager.StartLecture(config, OnLectureComplete);
        }
    }

    // Called when the lecture audio finishes — spawns Murad beside the player for Q&A.
    private void OnLectureComplete()
    {
        Debug.Log("[ExperienceManager] Lecture complete — spawning Murad for Q&A.");

        if (objectStamper != null)
        {
            objectStamper.ResetForNewExperience(ActiveConfig);
            objectStamper.SpawnMuradAtPlayerSide(ActiveConfig);
        }
        else
        {
            Debug.LogWarning("[ExperienceManager] objectStamper not assigned — Murad won't appear.");
        }
    }

    private void StartMuradDetectionPhase(ExperienceConfig config)
    {
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
        if (lectureHallManager != null) lectureHallManager.ClearScene();
        ShowMenu();
    }

}
