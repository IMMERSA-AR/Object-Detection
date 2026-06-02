using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Meta.XR.MRUtilityKit;

public class ExperienceManager : MonoBehaviour
{
    public static ExperienceManager Instance { get; private set; }

    [Header("Experiences")]
    [Tooltip("Drag your LectureHall ScriptableObject asset here")]
    public ExperienceConfig[] experiences;

    [Header("Lecture Hall")]
    [Tooltip("Drag the LectureHallManager GameObject here")]
    public LectureHallManager lectureHallManager;

    [Header("Chair Detection")]
    [Tooltip("How long (seconds) to wait for MRUK to load the room before giving up and\n" +
             "using the grid-based spawn fallback. Increase on slower devices.")]
    public float mrukWaitTimeout = 10f;

    [Tooltip("How long (seconds) to wait for MRUK to report the room floor Y.\n" +
             "MRUK is used only to get an accurate floor height — chair positions\n" +
             "are found by environment raycast, no anchor labels required.\n" +
             "If MRUK times out, floor Y falls back to cameraY − 1.7 m.")]
    public float mrukFloorWaitTimeout = 5f;

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

    // ── Room-model / MRUK-mesh hiding ────────────────────────────────────────

    private void DisableRoomModelBuildingBlock()
    {
        int found = 0;
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

                foreach (MeshRenderer mr in go.GetComponentsInChildren<MeshRenderer>(true))
                    mr.enabled = false;
            }
        }

        if (found == 0)
            Debug.Log("[ExperienceManager] RoomModel building block not found in scene — " +
                      "disable it manually in the Hierarchy if the blue overlay persists.");
    }

    private IEnumerator HideMRUKVisualizationWhenReady()
    {
        float timeout = 20f;
        float elapsed = 0f;
        while (elapsed < timeout)
        {
            yield return null;
            elapsed += Time.deltaTime;
            if (MRUK.Instance != null && MRUK.Instance.GetCurrentRoom() != null)
                break;
        }

        if (MRUK.Instance == null) yield break;

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

    private int DisableMRUKRenderers()
    {
        int count = 0;

        var mruk = MRUK.Instance;
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

#if UNITY_2023_1_OR_NEWER
        var allMR = FindObjectsByType<MeshRenderer>(
                        FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
        var allMR = FindObjectsOfType<MeshRenderer>(includeInactive: true);
#endif
        foreach (MeshRenderer mr in allMR)
        {
            if (!mr.enabled) continue;

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

    // ── Lecture hall sequence ─────────────────────────────────────────────────

    /// <summary>
    /// Main sequence:
    ///   1. Play optional intro audio.
    ///   2. Start chair-detection audio.
    ///   3. Get floor Y — wait briefly for MRUK FLOOR anchor; fall back to cameraY−1.7m.
    ///   4. Scan environment depth mesh for chair-height horizontal surfaces
    ///      (no MRUK anchor labels required — works from raw room scan alone).
    ///   5a. If chairs found → StartLectureWithChairs().
    ///   5b. If none found   → StartLecture() (grid fallback).
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
                src.loop = false;
                src.Stop();
                src.clip = config.introAudioClip;
                src.Play();
                Debug.Log($"[ExperienceManager] Playing intro audio: '{config.introAudioClip.name}' " +
                          $"({config.introAudioClip.length:F1}s).");

                yield return new WaitForSeconds(config.introAudioClip.length);

                src.Stop();
                Debug.Log("[ExperienceManager] Intro audio finished.");
            }
            else
            {
                Debug.LogWarning("[ExperienceManager] introAudioClip set but lectureAudioSource is null — skipping.");
            }
        }

        // ── 2. Chair-detection phase audio (looping) ────────────────
        lectureHallManager.PlayDetectionAudio(config.chairDetectionAudioClip);

        // ── 3. Get floor Y from MRUK FLOOR anchor ───────────────────
        // We wait briefly just for the FLOOR anchor — we do NOT need MRUK to
        // label any furniture. Chair positions come from the environment depth scan.
        Debug.Log($"[ExperienceManager] Waiting for MRUK FLOOR anchor (timeout={mrukFloorWaitTimeout:F0}s)…");

        float floorY = float.MinValue;
        float waited = 0f;
        while (waited < mrukFloorWaitTimeout)
        {
            if (MRUK.Instance != null && MRUK.Instance.GetCurrentRoom() != null)
            {
                MRUKRoom room = MRUK.Instance.GetCurrentRoom();
                foreach (MRUKAnchor anchor in room.Anchors)
                {
                    if (anchor.HasLabel("FLOOR"))
                    {
                        floorY = anchor.transform.position.y;
                        Debug.Log($"[ExperienceManager] MRUK FLOOR anchor found at Y={floorY:F2}.");
                        break;
                    }
                }
                if (floorY > float.MinValue) break;
            }
            yield return new WaitForSeconds(0.5f);
            waited += 0.5f;
        }

        if (floorY <= float.MinValue)
        {
            floorY = Camera.main != null ? Camera.main.transform.position.y - 1.7f : 0f;
            Debug.LogWarning($"[ExperienceManager] No MRUK FLOOR anchor — using camera fallback: Y={floorY:F2}.");
        }

        // ── 4. Scan environment depth mesh for chair-height surfaces ─
        // No labels needed — the scan fires downward rays and finds any
        // horizontal surface between chairScanMinHeight and chairScanMaxHeight
        // above the floor, exactly like the room scan the user already did.
        Debug.Log("[ExperienceManager] Scanning environment for chairs…");
        List<Vector3> chairPositions = lectureHallManager.FindChairsByEnvironmentScan(floorY);

        // ── 5. Launch lecture ────────────────────────────────────────
        if (chairPositions.Count > 0)
        {
            Debug.Log($"[ExperienceManager] {chairPositions.Count} chair(s) found — " +
                      "starting chair-based lecture spawn.");
            lectureHallManager.StartLectureWithChairs(chairPositions, config, OnLectureComplete);
        }
        else
        {
            Debug.LogWarning("[ExperienceManager] No chairs detected by environment scan — " +
                             "falling back to grid-based spawn.");
            lectureHallManager.StartLecture(config, OnLectureComplete);
        }
    }

    // ── Callbacks ─────────────────────────────────────────────────────────────

    private void OnLectureComplete()
    {
        Debug.Log("[ExperienceManager] Lecture complete.");
    }

    // ── Return to start ───────────────────────────────────────────────────────

    public void ReturnToMenu()
    {
        if (lectureHallManager != null) lectureHallManager.ClearScene();

        if (experiences != null && experiences.Length > 0 && experiences[0] != null)
            StartCoroutine(BeginLectureHallSequence(experiences[0]));
    }
}
