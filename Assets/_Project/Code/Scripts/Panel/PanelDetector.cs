using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Meta.XR;
using Meta.XR.MRUtilityKit;
using Unity.InferenceEngine;

/// <summary>
/// Detects one of 9 exhibition panels using a YOLOv8 model via the Quest passthrough
/// camera, spawns the matching character beside it, and plays narration with lip sync.
///
/// YOLOv8 output shape: [1, 13, 8400]  (4 bbox + 9 class scores)
///   Rows 0–3  : cx, cy, w, h  (normalised to 640-pixel input)
///   Row  4+c  : confidence for class c  (0–8)
///
/// Panel class mapping (must match your YOLO training labels):
///   0  College History Text
///   1  University Dome Building
///   2  Mechanical Engineering
///   3  Gate and Mining Cart
///   4  Hydraulics and Tile Work
///   5  Architecture and Ornament Studies
///   6  Survey Instrument and Classical Facades
///   7  Corinthian Capital and Industrial Machinery
///   8  Steam Engine and Polytechnique Relief
///
/// Configure panelEntries[0..8] in the Inspector — element index = class ID.
/// Assign characterPrefab and narrationClip for each panel.
/// </summary>
public class PanelDetector : MonoBehaviour
{
    // ── Model ─────────────────────────────────────────────────────────────────
    [Header("YOLOv8 Model")]
    [Tooltip("Drag your panel-detection .onnx model asset here.")]
    public ModelAsset modelAsset;

    [Tooltip("GPUCompute on device; CPU for Editor fallback.")]
    public BackendType backend = BackendType.CPU;

    [Tooltip("Minimum class confidence to accept a detection.")]
    [Range(0f, 1f)]
    public float confidenceThreshold = 0.50f;

    [Tooltip("IoU threshold for Non-Maximum Suppression within each frame.")]
    [Range(0f, 1f)]
    public float iouThreshold = 0.45f;

    [Tooltip("Spread inference across N engine layers per frame to avoid hitches.")]
    public int layersPerFrame = 20;

    // ── Per-panel configuration ───────────────────────────────────────────────
    [Header("Panel Entries  (index must match YOLO class ID 0–8)")]
    [Tooltip("One entry per panel class. Element 0 → class 0, element 1 → class 1, …\n" +
             "Assign the character prefab and narration clip for each panel.")]
    public PanelEntry[] panelEntries = new PanelEntry[NumClasses];

    // ── Scan ──────────────────────────────────────────────────────────────────
    [Header("Scan Settings")]
    [Tooltip("Start scanning automatically when the scene loads.")]
    public bool autoStart = true;

    [Tooltip("Maximum seconds to scan before giving up.")]
    public float scanDuration = 10f;

    [Tooltip("Run one YOLO inference pass every N seconds.")]
    public float inferenceInterval = 0.5f;

    [Tooltip("Minimum 3-D hit count for the winning class before it is confirmed.")]
    public int minHitsToConfirm = 2;

    [Tooltip("Stop scanning as soon as the winning class reaches minHitsToConfirm.")]
    public bool stopOnFirstConfirm = true;

    // ── Character spawn ───────────────────────────────────────────────────────
    [Header("Character Spawn")]
    [Tooltip("(Unused in 'in front' mode — kept for compatibility.)")]
    public float spawnSideOffset = 0.0f;

    [Tooltip("When enabled, every character spawns directly in front of the CAMERA\n" +
             "(at spawnForwardOffset metres ahead of the user's gaze), regardless of\n" +
             "where the detected panel is on the wall.  This guarantees all characters\n" +
             "always appear centred in the user's view — useful when panels are on\n" +
             "side walls where the 'in-front-of-panel' logic places characters to the\n" +
             "side. Disable if you prefer characters to appear at the actual panel location.")]
    public bool spawnInFrontOfCamera = false;

    [Tooltip("How far IN FRONT of the panel (toward the user) the character stands, in metres.\n" +
             "When spawnInFrontOfCamera is enabled, this is the distance ahead of the camera\n" +
             "instead.  Increase if it spawns inside the wall.")]
    public float spawnForwardOffset = 0.8f;

    [Tooltip("Vertical offset added on top of the detected floor Y.\n" +
             "Increase if the character spawns underground (pivot is above the feet).\n" +
             "E.g. set to 0.9 if the character's pivot is at its hip height.")]
    public float spawnYOffset = 0f;

    [Tooltip("Uniform scale applied to every spawned panel character.\n" +
             "Increase above 1.0 if characters appear shorter than the real-world user.\n" +
             "Typical range: 1.0 (no change) → 1.2 (20% taller) → 1.5 (50% taller).\n" +
             "Start at 1.2 and adjust until characters match the user's height.")]
    [Range(0.5f, 2.0f)]
    public float characterScale = 1.0f;

    // ── Interaction ─────────────────────────────────────────────────────────────
    [Header("Interaction")]
    [Tooltip("If true, narration does NOT auto-play. Instead the spawned character waits\n" +
             "until the user POINTS at it with the right controller, then plays the panel audio.\n" +
             "If false, narration plays automatically on spawn (old behaviour).")]
    public bool narrationOnPoint = true;

    [Tooltip("Drag OVRCameraRig → TrackingSpace → RightControllerAnchor here.")]
    public Transform rightController;

    [Tooltip("Drag OVRCameraRig → TrackingSpace → LeftControllerAnchor here.\n" +
             "Either controller can aim at and trigger a panel character.")]
    public Transform leftController;

    [Tooltip("Fallback LineRenderer for the laser — only used if the spawned character\n" +
             "prefab does NOT already have a LineRenderer on it.\n" +
             "Leave empty if your character prefabs carry their own LineRenderer\n" +
             "(same setup as the Murad Q&A prefab in the lecture hall scene).")]
    public LineRenderer laserPointer;

    // ── Audio ─────────────────────────────────────────────────────────────────
    [Header("Audio")]
    [Tooltip("Delay in seconds between character spawn and narration start.")]
    public float narrationDelay = 0.5f;

    [Tooltip("AudioSource for the AUDIBLE narration output.\n" +
             "Add an AudioSource to this GameObject and drag it here.\n" +
             "The character's CustomLipSyncContext AudioSource runs at volume=0\n" +
             "just for timing; this source provides the sound the user hears.")]
    public AudioSource narrationAudioSource;

    // ── Callbacks ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Fired when a panel class is confirmed (before the character is spawned).
    /// Args: (classId, panelName).
    /// Wire PanelSceneManager.OnPanelDetected to this.
    /// </summary>
    public System.Action<int, string> OnPanelConfirmed;

    /// <summary>
    /// Fired when the narration audio finishes playing (after the character has spoken).
    /// Wire PanelSceneManager.OnNarrationDone to this.
    /// </summary>
    public System.Action OnNarrationFinished;

    /// <summary>
    /// Optional gate: if set, PlayNarration waits until this returns true before
    /// starting the panel audio.  PanelSceneManager wires this to block narration
    /// while the scene intro clip is still playing.
    /// Leave null to start narration immediately (no gate).
    /// </summary>
    public System.Func<bool> IsReadyForNarration;

    // ── Constants ─────────────────────────────────────────────────────────────
    private const int NumClasses = 9;    // class IDs 0–8
    private const int InputSize  = 640;  // YOLO input resolution
    private const int Elements   = 8400; // anchors per YOLOv8 640×640 head

    // ── Runtime ───────────────────────────────────────────────────────────────
    private PassthroughCameraAccess   _cameraAccess;
    private EnvironmentRaycastManager _envRaycast;
    private Model   _model;
    private Worker  _engine;
    private bool    _modelLoaded;
    private bool    _scanning;
    private Texture _cameraTexture;

    /// <summary>The most recently spawned panel character (for cleanup on rescan).</summary>
    public GameObject LastSpawnedCharacter { get; private set; }

    // One hit list per class; index = classId
    private readonly List<Vector3>[] _hitsByClass = new List<Vector3>[NumClasses];

    // Classes that have already been confirmed — skipped on future scans.
    private readonly HashSet<int> _confirmedClasses = new HashSet<int>();

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    private void Awake()
    {
        for (int i = 0; i < NumClasses; i++)
            _hitsByClass[i] = new List<Vector3>();

        _cameraAccess = FindAnyObjectByType<PassthroughCameraAccess>();
        _envRaycast   = FindAnyObjectByType<EnvironmentRaycastManager>();

        if (_cameraAccess == null)
            Debug.LogWarning("[PanelDetector] PassthroughCameraAccess not found in scene.");
        if (_envRaycast == null)
            Debug.LogWarning("[PanelDetector] EnvironmentRaycastManager not found in scene.");

        LoadModel();
    }

    private void Start()
    {
        if (autoStart)
            StartDetection();
    }

    private void OnDestroy() => _engine?.Dispose();

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Call this to begin a panel scan (or set autoStart = true).</summary>
    public void StartDetection()
    {
        if (_scanning)
        {
            Debug.LogWarning("[PanelDetector] Already scanning — ignoring duplicate call.");
            return;
        }
        if (!_modelLoaded)
        {
            Debug.LogError("[PanelDetector] Model not loaded — check modelAsset assignment.");
            return;
        }
        _scanning = true;
        for (int i = 0; i < NumClasses; i++)
            _hitsByClass[i].Clear();
        StartCoroutine(ScanCoroutine());
    }

    /// <summary>
    /// Clears the confirmed-panel history so all panels can be detected again.
    /// Does NOT start a new scan — call ResetDetection() or StartDetection() after this.
    /// </summary>
    public void ResetConfirmedPanels()
    {
        _confirmedClasses.Clear();
        Debug.Log("[PanelDetector] Confirmed-panel history cleared — all panels eligible again.");
    }

    /// <summary>
    /// Clears all accumulated hit data and starts a fresh scan.
    /// Already-confirmed panels are still excluded; call ResetConfirmedPanels() first
    /// if you want to allow re-detection of all panels.
    /// </summary>
    public void ResetDetection()
    {
        if (_scanning)
        {
            Debug.LogWarning("[PanelDetector] Still scanning — wait for the current scan to finish.");
            return;
        }
        if (!_modelLoaded)
        {
            Debug.LogError("[PanelDetector] Model not loaded — check modelAsset assignment.");
            return;
        }
        for (int i = 0; i < NumClasses; i++)
            _hitsByClass[i].Clear();
        _scanning = true;
        StartCoroutine(ScanCoroutine());
        Debug.Log("[PanelDetector] Detection reset — scanning for next panel.");
    }

    // ── Scan loop ─────────────────────────────────────────────────────────────

    private IEnumerator ScanCoroutine()
    {
        Debug.Log("[PanelDetector] Scan started — look at the panel.");

        // Wait for passthrough camera
        float camWait = 0f;
        while (!TryEnsureCameraTexture())
        {
            yield return new WaitForSeconds(0.2f);
            camWait += 0.2f;
            if (camWait > 5f)
            {
                Debug.LogWarning("[PanelDetector] Camera unavailable after 5 s — aborting.");
                _scanning = false;
                yield break;
            }
        }

        float elapsed = 0f;

        while (elapsed < scanDuration)
        {
            yield return new WaitForSeconds(inferenceInterval);
            elapsed += inferenceInterval;

            var frameHits = new List<(Vector3 pos, int classId)>();
            yield return StartCoroutine(RunInference(frameHits));

            foreach (var (pos, cls) in frameHits)
                _hitsByClass[cls].Add(pos);

            int bestClass = BestClass();
            int bestCount = bestClass >= 0 ? _hitsByClass[bestClass].Count : 0;

            Debug.Log($"[PanelDetector] {elapsed:F1}/{scanDuration}s — " +
                      $"frame hits: {frameHits.Count}  " +
                      $"best class: {bestClass} ({bestCount}/{minHitsToConfirm} hits)");

            if (stopOnFirstConfirm && bestCount >= minHitsToConfirm)
            {
                Debug.Log($"[PanelDetector] Class {bestClass} confirmed with {bestCount} hits — stopping early.");
                break;
            }
        }

        _scanning = false;

        int confirmedClass = BestClass();
        if (confirmedClass < 0 || _hitsByClass[confirmedClass].Count == 0)
        {
            Debug.LogWarning("[PanelDetector] No panel detected — " +
                             "check model, confidence threshold, and that panelEntries are assigned.");
            yield break;
        }

        // Average 3-D positions for a stable panel location
        var hits     = _hitsByClass[confirmedClass];
        var panelPos = Vector3.zero;
        foreach (var h in hits) panelPos += h;
        panelPos /= hits.Count;

        string label = GetPanelLabel(confirmedClass);
        _confirmedClasses.Add(confirmedClass);   // exclude from all future scans
        Debug.Log($"[PanelDetector] Confirmed: '{label}'  pos={panelPos:F2}  ({hits.Count} hits)  " +
                  $"[{_confirmedClasses.Count} panel(s) done]");

        // Notify PanelSceneManager (or any listener) before spawning the character
        OnPanelConfirmed?.Invoke(confirmedClass, label);

        SpawnCharacterBesidePanel(panelPos, confirmedClass);
    }

    // Returns the class with the most hits (at least 1); -1 if no hits at all.
    // Already-confirmed classes are excluded so they are never re-detected.
    private int BestClass()
    {
        int best = -1, bestCount = 0;
        for (int i = 0; i < NumClasses; i++)
        {
            if (_confirmedClasses.Contains(i)) continue;   // skip already visited panels
            if (_hitsByClass[i].Count > bestCount)
            {
                bestCount = _hitsByClass[i].Count;
                best      = i;
            }
        }
        return best;
    }

    private string GetPanelLabel(int classId)
    {
        if (classId >= 0 && classId < panelEntries.Length &&
            panelEntries[classId] != null &&
            !string.IsNullOrEmpty(panelEntries[classId].panelName))
            return panelEntries[classId].panelName;

        return $"Panel class {classId}";
    }

    // ── Character spawn ───────────────────────────────────────────────────────

    private void SpawnCharacterBesidePanel(Vector3 panelWorldPos, int classId)
    {
        if (classId < 0 || classId >= panelEntries.Length || panelEntries[classId] == null)
        {
            Debug.LogError($"[PanelDetector] No PanelEntry configured for class {classId}. " +
                           "Expand panelEntries array in the Inspector.");
            return;
        }

        PanelEntry entry = panelEntries[classId];

        if (entry.characterPrefab == null)
        {
            Debug.LogError($"[PanelDetector] characterPrefab not set for '{GetPanelLabel(classId)}'.");
            return;
        }

        Transform cam = Camera.main != null ? Camera.main.transform : null;

        // Direction from panel toward the user (flat)
        Vector3 toUser = cam != null
            ? new Vector3(cam.position.x - panelWorldPos.x, 0f, cam.position.z - panelWorldPos.z)
            : Vector3.forward;
        if (toUser.sqrMagnitude > 0.001f) toUser.Normalize();
        else toUser = Vector3.forward;

        float camY = cam != null ? cam.position.y : 1.7f;

        // ── Choose spawn XZ and facing direction ─────────────────────────────
        // Mode A (spawnInFrontOfCamera = true):
        //   Always place the character directly in front of the camera at
        //   spawnForwardOffset metres.  Panel XZ position is ignored, so the
        //   character always appears centred in the user's view regardless of
        //   which wall the panel is on.  Character faces the user.
        //
        // Mode B (default):
        //   Place the character between the panel and the user (existing behaviour).
        //   Good when the panel is directly in front of the user.
        Vector3    spawnXZ;
        Quaternion spawnRot;

        if (spawnInFrontOfCamera && cam != null)
        {
            Vector3 camFwdFlat = new Vector3(cam.forward.x, 0f, cam.forward.z);
            if (camFwdFlat.sqrMagnitude > 0.001f) camFwdFlat.Normalize();
            else camFwdFlat = toUser;   // fall back to panel direction

            spawnXZ  = new Vector3(cam.position.x + camFwdFlat.x * spawnForwardOffset,
                                   0f,
                                   cam.position.z + camFwdFlat.z * spawnForwardOffset);
            spawnRot = Quaternion.LookRotation(-camFwdFlat);   // face toward user
        }
        else
        {
            // Original logic: move from the panel toward the user by spawnForwardOffset.
            // Compute the OPEN-SPACE XZ spawn position first, then find the floor Y there.
            // Finding floor at panelWorldPos (on the wall) causes the downward raycast to
            // hit wall geometry instead of the floor.
            spawnXZ  = new Vector3(panelWorldPos.x + toUser.x * spawnForwardOffset,
                                   0f,
                                   panelWorldPos.z + toUser.z * spawnForwardOffset);
            spawnRot = Quaternion.LookRotation(toUser);
        }

        float floorY  = GetFloorY(spawnXZ, camY);
        Vector3 spawnPos = new Vector3(spawnXZ.x, floorY + spawnYOffset, spawnXZ.z);

        GameObject character = Instantiate(entry.characterPrefab, spawnPos, spawnRot);
        character.name = $"PanelCharacter_{classId}_{GetPanelLabel(classId)}";
        LastSpawnedCharacter = character;

        // Apply scale multiplier so characters match real-world user height.
        if (!Mathf.Approximately(characterScale, 1f))
            character.transform.localScale = Vector3.one * characterScale;

        Debug.Log($"[PanelDetector] Spawned '{GetPanelLabel(classId)}' at {spawnPos:F2}  scale={characterScale:F2}.");

        // ── Disable AI/voice components BEFORE their Start() runs ────────────
        // MuradController.Start() re-assigns the Animator Controller, which destroys
        // the PlayableGraph that PanelNPCController.Init() is about to create → T-pose.
        // VoiceAPIController.Start() may also drive the Animator or start Q&A listening early.
        // Disabling them here (same frame as Instantiate, before Start() fires) prevents
        // both Start() and all Update/LateUpdate from running on these components.
        // This mirrors exactly what LectureHallManager does for the seated Murad.
        var muradAI = character.GetComponent<MuradController>();
        if (muradAI != null)
        {
            muradAI.enabled = false;
            Debug.Log("[PanelDetector] MuradController disabled — PanelNPCController owns animation.");
        }

        var voiceCtrl = character.GetComponent<VoiceAPIController>();
        if (voiceCtrl != null)
        {
            voiceCtrl.enabled = false;
            Debug.Log("[PanelDetector] VoiceAPIController disabled on panel character.");
        }

        // Also disable CustomLipSyncContext at spawn — it will be re-enabled by
        // PlayNarration() only for the duration of the narration clip.
        var lipSync = character.GetComponentInChildren<LipSync.CustomLipSyncContext>(includeInactive: true);
        if (lipSync != null)
        {
            lipSync.enabled = false;
            Debug.Log("[PanelDetector] CustomLipSyncContext disabled at spawn — re-enabled during narration.");
        }

        // Hide any subtitle/prompt canvases baked into the prefab (driven by VoiceAPIController).
        foreach (var tmp in character.GetComponentsInChildren<TMPro.TMP_Text>(includeInactive: true))
            tmp.text = "";
        foreach (Canvas c in character.GetComponentsInChildren<Canvas>(includeInactive: true))
            c.enabled = false;

        // ── Idle animation on spawn (removes T-pose) ─────────────────────────
        // Call Init() explicitly — same timing as the lecture hall's HistoricalNPCController.
        // Now safe: MuradController is disabled so it cannot re-assign the AnimatorController
        // and destroy our PlayableGraph.
        // Pass entry.idleClip / entry.talkingClip so the SAME prefab can be reused across
        // scenes: the PanelEntry (configured per-panel in the Inspector) provides the clips
        // and they are injected at runtime — no clips need to be baked into the prefab.
        var panelNPCCtrl = character.GetComponent<PanelNPCController>();
        if (panelNPCCtrl != null)
        {
            panelNPCCtrl.Init(entry.idleClip, entry.talkingClip);
            // Tell the controller where the panel is so the pointing gesture knows
            // which direction to aim the arm and redirect the head/eyes.
            panelNPCCtrl.SetPanelPosition(panelWorldPos);
        }
        else
        {
            var panelAnim = character.AddComponent<PanelCharacterAnimator>();
            panelAnim.Init(entry.idleClip, entry.talkingClip);
        }

        // ── Auto-blink ───────────────────────────────────────────────────────
        // Add NPCBlinking so the character blinks naturally.
        // NPCBlinking auto-detects eye blend shapes on Start() — no extra setup needed.
        if (character.GetComponentInChildren<NPCBlinking>() == null)
        {
            character.AddComponent<NPCBlinking>();
            Debug.Log("[PanelDetector] NPCBlinking added to panel character.");
        }

        if (entry.narrationClip == null)
        {
            Debug.LogWarning($"[PanelDetector] narrationClip not set for class {classId} — skipping audio.");
            return;
        }

        if (narrationOnPoint)
        {
            // Auto-detect LineRenderer from the spawned character first.
            // Characters that carry their own LineRenderer (same as the Murad Q&A prefab
            // in the lecture hall scene) don't need the fallback laserPointer on PanelDetector.
            LineRenderer lr = character.GetComponentInChildren<LineRenderer>(includeInactive: true);
            if (lr == null) lr = laserPointer;   // fall back to PanelDetector's assigned field

            // Defer narration: the character waits until the user points the controller at it.
            var interaction = character.AddComponent<PanelCharacterInteraction>();
            interaction.Init(this, entry.narrationClip, rightController, leftController, lr);
            Debug.Log("[PanelDetector] Narration deferred — aim either controller at character and pull trigger.");
        }
        else
        {
            StartCoroutine(PlayNarration(character, entry.narrationClip));
        }
    }

    /// <summary>
    /// Public entry point used by PanelCharacterInteraction to start narration once
    /// the user points the controller at the spawned character.
    /// </summary>
    public void TriggerNarration(GameObject character, AudioClip clip)
    {
        if (character == null || clip == null) return;
        StartCoroutine(PlayNarration(character, clip));
    }

    // ── Narration + lip sync ──────────────────────────────────────────────────

    private IEnumerator PlayNarration(GameObject character, AudioClip clip)
    {
        yield return new WaitForSeconds(narrationDelay);

        // If PanelSceneManager set a gate (e.g. waiting for intro to finish),
        // hold here until the gate opens.  Poll every 0.25 s to avoid busy-spin.
        if (IsReadyForNarration != null)
        {
            while (!IsReadyForNarration())
            {
                Debug.Log("[PanelDetector] Waiting for intro to finish before starting panel narration…");
                yield return new WaitForSeconds(0.25f);
            }
        }

        // Switch from idle → talking for the duration of the narration.
        var panelNPC  = character != null ? character.GetComponent<PanelNPCController>()      : null;
        var charAnim  = character != null ? character.GetComponent<PanelCharacterAnimator>()  : null;
        if      (panelNPC != null) panelNPC.PlayTalking();
        else if (charAnim != null) charAnim.PlayTalking();

        // ── 1. CustomLipSyncContext: runs silently for timeSamples-based lip sync ──
        var lipSync = character.GetComponentInChildren<LipSync.CustomLipSyncContext>(true);
        AudioSource lipSyncAudio = null;

        if (lipSync != null)
        {
            lipSync.enabled = true;
            lipSync.EnsureInitialized();
            lipSync.FeedAudioClip(clip);

            lipSyncAudio = lipSync.GetComponent<AudioSource>();
            if (lipSyncAudio != null)
            {
                lipSyncAudio.outputAudioMixerGroup = null;
                lipSyncAudio.clip         = clip;
                lipSyncAudio.loop         = false;
                lipSyncAudio.mute         = false;
                lipSyncAudio.volume       = 0f;   // inaudible — narrationAudioSource carries the sound
                lipSyncAudio.spatialBlend = 0f;
                lipSyncAudio.Play();
                Debug.Log("[PanelDetector] CustomLipSyncContext playing (silent) for lip sync timing.");
            }
        }
        else
        {
            Debug.LogWarning("[PanelDetector] No CustomLipSyncContext found on character. " +
                             "Add CustomLipSyncContext + CustomLipSyncMorphTarget to the prefab.");
        }

        // ── 2. Audible narration ──────────────────────────────────────────────
        if (narrationAudioSource == null)
        {
            narrationAudioSource = gameObject.AddComponent<AudioSource>();
            Debug.LogWarning("[PanelDetector] narrationAudioSource not assigned — created at runtime.");
        }

        narrationAudioSource.outputAudioMixerGroup = null;
        narrationAudioSource.spatialBlend = 0f;
        narrationAudioSource.volume       = 1f;
        narrationAudioSource.loop         = false;
        narrationAudioSource.clip         = clip;
        narrationAudioSource.Play();

        Debug.Log($"[PanelDetector] Narration started: '{clip.name}'  {clip.length:F1}s");

        yield return new WaitForSeconds(clip.length);

        if (lipSyncAudio != null) lipSyncAudio.Stop();
        narrationAudioSource.Stop();
        Debug.Log("[PanelDetector] Narration finished.");

        // Zero viseme weights first so the morph targets snap to neutral (mouth closed),
        // then disable the component so CustomLipSyncMorphTarget stops reading weights.
        if (lipSync != null)
        {
            lipSync.ResetVisemes();
            lipSync.enabled = false;
            Debug.Log("[PanelDetector] CustomLipSyncContext disabled — mouth reset to neutral.");
        }

        // Return to idle now that the character has finished speaking.
        if      (panelNPC != null) panelNPC.PlayIdle();
        else if (charAnim != null) charAnim.PlayIdle();

        // Notify PanelSceneManager that narration is done (safe to rescan)
        OnNarrationFinished?.Invoke();
    }

    // ── YOLOv8 inference ─────────────────────────────────────────────────────

    private IEnumerator RunInference(List<(Vector3 pos, int classId)> frameHits)
    {
        if (!TryEnsureCameraTexture()) yield break;

        // ── Prepare 640×640 input tensor ─────────────────────────────────────
        RenderTexture rt = RenderTexture.GetTemporary(InputSize, InputSize, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(_cameraTexture, rt);
        var inputTensor = new Tensor<float>(new TensorShape(1, 3, InputSize, InputSize));
        TextureConverter.ToTensor(rt, inputTensor,
            new TextureTransform()
                .SetDimensions(InputSize, InputSize)
                .SetTensorLayout(TensorLayout.NCHW));
        RenderTexture.ReleaseTemporary(rt);

        // ── Run model, spread across multiple frames to avoid GPU stalls ──────
        var schedule = _engine.ScheduleIterable(inputTensor);
        if (schedule == null)
        {
            // ScheduleIterable unavailable — skip this frame rather than blocking
            // the main thread with a synchronous Schedule() call (causes ANR on device).
            Debug.LogError("[PanelDetector] ScheduleIterable returned null — skipping frame.");
            inputTensor.Dispose();
            yield break;
        }

        int n = 0;
        while (schedule.MoveNext())
            if (++n % layersPerFrame == 0) yield return null;

        // ── Read output tensor [1, 13, 8400] ──────────────────────────────────
        Tensor<float> out0 = _engine.PeekOutput(0) as Tensor<float>;
        if (out0 == null)
        {
            Debug.LogError("[PanelDetector] Output tensor is null — check model format.");
            inputTensor.Dispose();
            yield break;
        }

        out0.ReadbackRequest();
        while (!out0.IsReadbackRequestDone()) yield return null;
        Tensor<float> cpu0 = out0.ReadbackAndClone() as Tensor<float>;

        // ── Build raw detections: best class per anchor ───────────────────────
        // For each of the 8400 anchors, pick the class with the highest confidence.
        // Only keep anchors where the best confidence exceeds the threshold.
        var rawDets = new List<RawDetection>();

        for (int i = 0; i < Elements; i++)
        {
            int   bestCls  = -1;
            float bestConf = confidenceThreshold;   // treat threshold as the floor

            for (int c = 0; c < NumClasses; c++)
            {
                float conf = cpu0[(4 + c) * Elements + i];
                if (conf > bestConf)
                {
                    bestConf = conf;
                    bestCls  = c;
                }
            }

            if (bestCls < 0) continue;

            rawDets.Add(new RawDetection
            {
                cx      = cpu0[0 * Elements + i],
                cy      = cpu0[1 * Elements + i],
                w       = cpu0[2 * Elements + i],
                h       = cpu0[3 * Elements + i],
                conf    = bestConf,
                classId = bestCls
            });
        }

        // ── Class-agnostic NMS (high-confidence box suppresses lower ones nearby)
        List<RawDetection> kept = ApplyNMS(rawDets);
        Debug.Log($"[PanelDetector]   {rawDets.Count} raw → {kept.Count} after NMS.");

        // ── Raycast each surviving detection centre to 3-D world space ────────
        foreach (var det in kept)
        {
            if (_envRaycast == null || Camera.main == null) continue;

            float vx = Mathf.Clamp01(det.cx / InputSize);
            float vy = Mathf.Clamp01(1f - det.cy / InputSize);   // Y-flip: image top = viewport top

            Ray ray = Camera.main.ViewportPointToRay(new Vector3(vx, vy, 0f));

            if (!_envRaycast.Raycast(ray, out var hit, 10f))
            {
                Debug.Log($"[PanelDetector]   class={det.classId} conf={det.conf:F2} — no depth hit.");
                continue;
            }

            frameHits.Add((hit.point, det.classId));
            Debug.Log($"[PanelDetector]   ✓ class={det.classId} ('{GetPanelLabel(det.classId)}') " +
                      $"conf={det.conf:F2}  pos={hit.point:F2}");
        }

        inputTensor.Dispose();
        out0.Dispose();
        cpu0.Dispose();
    }

    // ── NMS ───────────────────────────────────────────────────────────────────

    private struct RawDetection
    {
        public float cx, cy, w, h, conf;
        public int   classId;
    }

    // Class-agnostic NMS: sort by confidence, suppress overlapping boxes regardless of class.
    private List<RawDetection> ApplyNMS(List<RawDetection> dets)
    {
        dets.Sort((a, b) => b.conf.CompareTo(a.conf));

        var kept       = new List<RawDetection>();
        var suppressed = new bool[dets.Count];

        for (int i = 0; i < dets.Count; i++)
        {
            if (suppressed[i]) continue;
            kept.Add(dets[i]);
            for (int j = i + 1; j < dets.Count; j++)
            {
                if (!suppressed[j] && ComputeIoU(dets[i], dets[j]) > iouThreshold)
                    suppressed[j] = true;
            }
        }
        return kept;
    }

    private static float ComputeIoU(RawDetection a, RawDetection b)
    {
        float ax1 = a.cx - a.w * .5f, ay1 = a.cy - a.h * .5f;
        float ax2 = a.cx + a.w * .5f, ay2 = a.cy + a.h * .5f;
        float bx1 = b.cx - b.w * .5f, by1 = b.cy - b.h * .5f;
        float bx2 = b.cx + b.w * .5f, by2 = b.cy + b.h * .5f;

        float ix1 = Mathf.Max(ax1, bx1), iy1 = Mathf.Max(ay1, by1);
        float ix2 = Mathf.Min(ax2, bx2), iy2 = Mathf.Min(ay2, by2);

        if (ix2 <= ix1 || iy2 <= iy1) return 0f;

        float inter = (ix2 - ix1) * (iy2 - iy1);
        return inter / (a.w * a.h + b.w * b.h - inter);
    }

    // ── Floor Y detection ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns the world-space floor Y at the given XZ position.
    /// Uses the same three-tier logic as LectureHallManager.FindFloorY:
    ///   1. MRUK FLOOR anchor (most reliable on-device).
    ///   2. EnvironmentRaycastManager downcast from just above estimated floor
    ///      (origin = estimatedFloor + 0.3 m, max range 0.8 m).
    ///      Starting close to the floor avoids hitting wall/furniture geometry.
    ///   3. Hard fallback: camera Y - 1.7 m.
    /// </summary>
    private float GetFloorY(Vector3 nearPos, float cameraY)
    {
        // ── 1. MRUK FLOOR anchor ─────────────────────────────────
        try
        {
            var mruk = Meta.XR.MRUtilityKit.MRUK.Instance;
            if (mruk != null)
            {
                var room = mruk.GetCurrentRoom();
                if (room != null)
                    foreach (var anchor in room.Anchors)
                        if (anchor.HasLabel("FLOOR"))
                            return anchor.transform.position.y;
            }
        }
        catch { /* MRUK not present */ }

        // ── 2. Tight downward raycast from just above estimated floor ─────────
        // Origin is only 0.3 m above the estimated floor so the ray hits the
        // actual floor surface rather than furniture, baseboards, or wall faces.
        // Max distance 0.8 m prevents it from shooting through the floor.
        if (_envRaycast != null)
        {
            float estimatedFloor = cameraY - 1.7f;
            Vector3 origin = new Vector3(nearPos.x, estimatedFloor + 0.3f, nearPos.z);
            if (_envRaycast.Raycast(new Ray(origin, Vector3.down), out var hit, 0.8f) &&
                Vector3.Dot(hit.normal, Vector3.up) > 0.7f)
                return hit.point.y;
        }

        // ── 3. Hard fallback ─────────────────────────────────────
        return cameraY - 1.7f;
    }

    // ── Model loading ─────────────────────────────────────────────────────────

    private void LoadModel()
    {
        if (modelAsset == null)
        {
            Debug.LogError("[PanelDetector] modelAsset not assigned — drag the .onnx into the Inspector.");
            return;
        }
        try
        {
            _model       = ModelLoader.Load(modelAsset);
            _engine      = new Worker(_model, backend);
            _modelLoaded = true;
            Debug.Log($"[PanelDetector] Model loaded ({backend}). " +
                      $"Expects [1, {4 + NumClasses}, {Elements}] output.");
        }
        catch (Exception e)
        {
            Debug.LogError("[PanelDetector] Failed to load model: " + e.Message);
        }
    }

    private bool TryEnsureCameraTexture()
    {
        if (_cameraAccess == null || !_cameraAccess.IsPlaying) return false;
        if (_cameraTexture != null) return true;
        _cameraTexture = _cameraAccess.GetTexture();
        return _cameraTexture != null;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// PanelEntry — one element per YOLO class in the Inspector
// ─────────────────────────────────────────────────────────────────────────────

[System.Serializable]
public class PanelEntry
{
    [Tooltip("Display name shown in debug logs (informational only).")]
    public string panelName;

    [Tooltip("Character prefab to spawn beside this panel.\n" +
             "Must have CustomLipSyncContext + CustomLipSyncMorphTarget for lip sync.")]
    public GameObject characterPrefab;

    [Tooltip("Narration audio clip for this panel.")]
    public AudioClip narrationClip;

    [Tooltip("Idle animation clip — plays on spawn and after narration ends.\n" +
             "Drag a Mixamo/CC4 idle clip here. Removes the default T-pose.\n" +
             "Requires an Animator component (with Avatar) on the character prefab.")]
    public AnimationClip idleClip;

    [Tooltip("Talking animation clip — plays while the narration audio is playing.\n" +
             "Falls back to the idle clip if left empty.")]
    public AnimationClip talkingClip;
}
