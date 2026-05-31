using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class ExperienceManager : MonoBehaviour
{
    public static ExperienceManager Instance { get; private set; }

    [Header("Experiences")]
    [Tooltip("Drag your LectureHall ScriptableObject asset here")]
    public ExperienceConfig[] experiences;

    [Header("Lecture Hall")]
    [Tooltip("Drag the LectureHallManager GameObject here")]
    public LectureHallManager lectureHallManager;

    public ExperienceConfig ActiveConfig { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Start()
    {
        // Disable the [BuildingBlock] RoomModel GameObject immediately —
        // it renders the blue room-mesh overlay and is not needed at runtime.
        DisableRoomModelBuildingBlock();

        // Also hide any MRUK-spawned MeshRenderers (GlobalMeshAnchor etc.)
        // via a polling coroutine, since those spawn asynchronously.
        StartCoroutine(HideMRUKVisualizationWhenReady());

        if (experiences == null || experiences.Length == 0 || experiences[0] == null)
        {
            Debug.LogError("[ExperienceManager] No ExperienceConfig assigned! Drag a config asset here.");
            return;
        }

        if (lectureHallManager == null)
        {
            Debug.LogError("[ExperienceManager] lectureHallManager not assigned!");
            return;
        }

        StartCoroutine(BeginLectureHallSequence(experiences[0]));
    }

    /// <summary>
    /// Immediately disables the [BuildingBlock] RoomModel GameObject (and any similarly
    /// named siblings) that Meta's Building Block system places in the scene to
    /// visualise the room mesh as a blue overlay.
    /// </summary>
    private void DisableRoomModelBuildingBlock()
    {
        int found = 0;
        // FindObjectsByType searches all root and non-root GameObjects including inactive ones.
#if UNITY_2023_1_OR_NEWER
        foreach (GameObject go in FindObjectsByType<GameObject>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
#else
        foreach (GameObject go in FindObjectsOfType<GameObject>(includeInactive: true))
#endif
        {
            string n = go.name;
            if (n.Contains("RoomModel") || n.Contains("EffectMesh") ||
                n.Contains("SceneMesh") || n.Contains("GlobalMesh"))
            {
                if (go.activeSelf)
                {
                    go.SetActive(false);
                    Debug.Log($"[ExperienceManager] Disabled room-mesh GO: '{go.name}'.");
                    found++;
                }

                // Also disable all MeshRenderers on it (in case SetActive is undone)
                foreach (MeshRenderer mr in go.GetComponentsInChildren<MeshRenderer>(true))
                    mr.enabled = false;
            }
        }

        if (found == 0)
            Debug.Log("[ExperienceManager] RoomModel building block not found in scene — " +
                      "disable it manually in the Hierarchy if the blue overlay persists.");
    }

    /// <summary>
    /// Polls every 0.5 s for up to 30 s after the room is ready, disabling every
    /// MeshRenderer that belongs to MRUK-spawned objects (GlobalMeshAnchor, room
    /// walls, EffectMesh, etc.). The GlobalMeshAnchor is built asynchronously and
    /// arrives several seconds after the room object — a one-shot check is too early.
    /// </summary>
    private IEnumerator HideMRUKVisualizationWhenReady()
    {
        // ── Wait for room to exist ────────────────────────────────────
        float timeout = 20f;
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

        // ── Poll every 0.5 s for 30 s ────────────────────────────────
        // GlobalMeshAnchor spawns asynchronously (often 5-10 s after the room).
        // We keep sweeping until we've had at least one successful hide, then
        // continue for a short grace period in case more objects appear.
        int totalHidden = 0;
        float pollEnd = Time.time + 30f;

        while (Time.time < pollEnd)
        {
            int n = DisableMRUKRenderers();
            if (n > 0)
            {
                totalHidden += n;
                Debug.Log($"[ExperienceManager] MRUK hide sweep: {n} new renderer(s) disabled " +
                          $"(total={totalHidden}).");
            }
            yield return new WaitForSeconds(0.5f);
        }

        Debug.Log($"[ExperienceManager] MRUK visualization polling done. " +
                  $"Total renderers disabled: {totalHidden}.");
    }

    /// <summary>
    /// Single-pass sweep that disables all currently-enabled MeshRenderers that
    /// belong to MRUK / OVRScene objects. Returns the count newly disabled.
    /// </summary>
    private int DisableMRUKRenderers()
    {
        int count = 0;

        // ── Pass 1: children of MRUK.Instance ────────────────────────
        var mruk = Meta.XR.MRUtilityKit.MRUK.Instance;
        if (mruk != null)
        {
            foreach (MeshRenderer mr in
                     mruk.GetComponentsInChildren<MeshRenderer>(includeInactive: true))
            {
                if (!mr.enabled) continue;
                mr.enabled = false;
                count++;
            }
        }

        // ── Pass 2: GlobalMeshAnchor via room API ─────────────────────
        var room = mruk?.GetCurrentRoom();
        if (room?.GlobalMeshAnchor != null)
        {
            foreach (MeshRenderer mr in
                     room.GlobalMeshAnchor
                         .GetComponentsInChildren<MeshRenderer>(includeInactive: true))
            {
                if (!mr.enabled) continue;
                mr.enabled = false;
                count++;
            }
        }

        // ── Pass 3: scene-wide hierarchy search ───────────────────────
        // Walk every enabled MeshRenderer's ancestor chain; disable the renderer
        // if any ancestor name contains an MRUK / OVRScene / meta keyword.
#if UNITY_2023_1_OR_NEWER
        var allMR = FindObjectsByType<MeshRenderer>(
                        FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
        var allMR = FindObjectsOfType<MeshRenderer>(includeInactive: true);
#endif
        foreach (MeshRenderer mr in allMR)
        {
            if (!mr.enabled) continue;

            // Walk up the transform hierarchy
            Transform t = mr.transform;
            bool isMRUK = false;
            while (t != null && !isMRUK)
            {
                string n = t.gameObject.name;
                if (n.Contains("MRUK") || n.Contains("GlobalMesh") ||
                    n.Contains("EffectMesh") || n.Contains("SceneMesh") ||
                    n.Contains("RoomMesh") || n.Contains("OVRScene") ||
                    n.Contains("OVRGlobalMesh") || n.Contains("SceneCapture") ||
                    n.Contains("MRUKRoom") || n.Contains("RoomModel"))
                    isMRUK = true;
                t = t.parent;
            }
            if (!isMRUK) continue;

            mr.enabled = false;
            count++;
        }

        return count;
    }

    /// <summary>
    /// Plays the optional intro audio (if assigned), waits for it to finish,
    /// then kicks off the lecture hall sequence via LectureHallManager.
    /// </summary>
    private IEnumerator BeginLectureHallSequence(ExperienceConfig config)
    {
        ActiveConfig = config;

        // ── 1. Intro audio ──────────────────────────────────────────
        AudioSource src = lectureHallManager.lectureAudioSource;
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

                yield return new WaitForSeconds(config.introAudioClip.length);

                src.Stop();
                Debug.Log("[ExperienceManager] Intro audio finished.");
            }
            else
            {
                Debug.LogWarning("[ExperienceManager] introAudioClip set but lectureHallManager.lectureAudioSource is null — skipping.");
            }
        }

        // ── 2. Detection audio + start lecture ──────────────────────
        // LectureHallManager handles MRUK chair detection internally.
        lectureHallManager.PlayDetectionAudio(config.chairDetectionAudioClip);
        lectureHallManager.StartLecture(config, OnLectureComplete);
    }

    // Called when the lecture sequence finishes.
    private void OnLectureComplete()
    {
        Debug.Log("[ExperienceManager] Lecture complete.");
    }

    // ── Return to start (wire to a restart button if needed) ─────────

    public void ReturnToMenu()
    {
        if (lectureHallManager != null) lectureHallManager.ClearScene();

        if (experiences != null && experiences.Length > 0 && experiences[0] != null)
            StartCoroutine(BeginLectureHallSequence(experiences[0]));
    }
}
