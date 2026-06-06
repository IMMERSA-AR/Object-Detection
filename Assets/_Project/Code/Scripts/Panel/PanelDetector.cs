using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Meta.XR;
using Meta.XR.MRUtilityKit;
using Unity.InferenceEngine;

public class PanelDetector : MonoBehaviour
{
    [Header("YOLO Model")]
    public ModelAsset yoloModel;
    public BackendType backend = BackendType.CPU;
    [Range(0f, 1f)]
    public float confidenceThreshold = 0.50f;
    [Range(0f, 1f)]
    public float iouThreshold = 0.45f;
    public int layersPerFrame = 20;

    [Header("Panel Entries")]
    public PanelEntry[] panelEntries = new PanelEntry[NumClasses];

    [Header("Scan Settings")]
    public bool autoStart = true;
    public float scanDuration = 10f;
    public float inferenceInterval = 0.5f;
    public int minHitsToConfirm = 2;
    public bool stopOnFirstConfirm = true;

    //Paramters reponsible for spawning characters
    [Header("Character Spawn")]
    public float spawnSideOffset = 0.0f;
    public bool spawnInFrontOfCamera = false;
    public float spawnForwardOffset = 0.8f;
    public float spawnYOffset = 0f;
    [Range(0.5f, 2.0f)]
    public float characterScale = 1.0f;
    public float minCharacterSeparation = 1.2f;
    public float separationStep = 0.4f;

    // Parameters responsilbe for interaction of characters 
    [Header("Interaction")]
    public bool narrationOnPoint = true;
    public Transform rightController;
    public Transform leftController;
    public LineRenderer laserPointer;
    public LineRenderer detectionBox;
    public Color detectionBoxColor = new Color(0.1f, 0.8f, 1f, 1f);
    [Range(0.3f, 1.3f)]
    public float detectionBoxScale = 0.8f;

    // Parameters responsible for story that will be played 
    [Header("Audio")]
    public float narrationDelay = 0.5f;
    public AudioSource narrationAudioSource;

    public System.Action<int, string> OnPanelConfirmed;
    public System.Action OnNarrationStarted;
    public System.Action OnNarrationFinished;
    public System.Action OnScanCompletedNoResult;
    public int ConfirmedCount => _confirmedClasses.Count;
    public bool IsScanning => _scanning;
    public System.Func<bool> IsReadyForNarration;

    private const int NumClasses = 9;
    private const int InputSize = 640;
    private const int Elements = 8400;

    private PassthroughCameraAccess _cameraAccess;
    private EnvironmentRaycastManager _envRaycast;
    private Model _model;
    private Worker _engine;
    private bool _modelLoaded;
    private bool _scanning;
    private bool _abortScan;
    private Texture _cameraTexture;

    public GameObject LastSpawnedCharacter { get; private set; }
    private readonly List<Vector3>[] _hitsByClass = new List<Vector3>[NumClasses];
    private readonly HashSet<int> _confirmedClasses = new HashSet<int>();
    private readonly List<GameObject> _spawnedCharacters = new List<GameObject>();
    private GameObject _lastNarrationCharacter;
    private AudioClip _lastNarrationClip;
    private string _lastNarrationTranscript;

    private void Awake()
    {
        for (int i = 0; i < NumClasses; i++)
            _hitsByClass[i] = new List<Vector3>();

        _cameraAccess = FindAnyObjectByType<PassthroughCameraAccess>();
        _envRaycast = FindAnyObjectByType<EnvironmentRaycastManager>();

        if (_cameraAccess == null)
            Debug.LogWarning("[PanelDetector] PassthroughCameraAccess can't be accessed");
        if (_envRaycast == null)
            Debug.LogWarning("[PanelDetector] EnvironmentRaycastManager can't be accessed");

        LoadModel();
    }

    private void Start()
    {
        if (autoStart)
            StartDetection();
    }

    private void OnDestroy()
    {
        _engine?.Dispose();
    }

    public void StartDetection()
    {
        if (_scanning)
        {
            Debug.LogWarning("[PanelDetector] Scan started.");
            return;
        }
        if (!_modelLoaded)
        {
            Debug.LogError("[PanelDetector] Model did not load");
            return;
        }
        _scanning = true;
        for (int i = 0; i < NumClasses; i++)
            _hitsByClass[i].Clear();
        StartCoroutine(ScanCoroutine());
    }

    public void StopDetection()
    {
        if (_scanning)
            Debug.Log("[PanelDetector] Detection stopped");
        _abortScan = true;
    }

    // Used to delete the detected panels to be able to detect them again if user asks to repeat the experience
    public void ResetConfirmedPanels()
    {
        _confirmedClasses.Clear();
        Debug.Log("[PanelDetector] Old panel detetion is cleared");
    }

    // Clear all the spawned charaters, to spawn them again in the case of repeating experience
    public void ClearAllSpawnedCharacters()
    {
        foreach (var go in _spawnedCharacters)
            if (go != null) Destroy(go);
        _spawnedCharacters.Clear();
        LastSpawnedCharacter = null;
        Debug.Log("[PanelDetector] All spawned characters are cleared");
    }

    public void ResetDetection()
    {
        if (_scanning)
        {
            Debug.LogWarning("[PanelDetector] Scan started.");
            return;
        }
        if (!_modelLoaded)
        {
            Debug.LogError("[PanelDetector] Model did not load");
            return;
        }
        for (int i = 0; i < NumClasses; i++)
            _hitsByClass[i].Clear();
        _scanning = true;
        StartCoroutine(ScanCoroutine());
        Debug.Log($"[PanelDetector] Detection reset, scanning for next panel ");
    }

    private IEnumerator ScanCoroutine()
    {
        Debug.Log("[PanelDetector] Scan started. Please look at the panel");
        _abortScan = false;
        float camWait = 0f;
        while (!TryEnsureCameraTexture())
        {
            if (_abortScan)
            {
                _scanning = false;
                yield break;
            }
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
            if (_abortScan)
            {
                _scanning = false;
                HideDetectionBox();
                Debug.Log("[PanelDetector] Scan aborted before a panel was confirmed.");
                yield break;
            }

            yield return new WaitForSeconds(inferenceInterval);
            elapsed += inferenceInterval;
            var frameHits = new List<(Vector3 pos, int classId)>();
            yield return StartCoroutine(RunInference(frameHits));
            foreach (var (pos, cls) in frameHits)
                _hitsByClass[cls].Add(pos);

            int bestClass = BestClass();
            int bestCount = bestClass >= 0 ? _hitsByClass[bestClass].Count : 0;

            if (stopOnFirstConfirm && bestCount >= minHitsToConfirm)
            {
                Debug.Log($"[PanelDetector] Class {bestClass} confirmed with {bestCount} hits");
                break;
            }
        }

        _scanning = false;
        HideDetectionBox();

        if (_abortScan)
        {
            Debug.Log("[PanelDetector] Scan aborted at end of pass, no confirmation.");
            yield break;
        }

        int confirmedClass = BestClass();
        if (confirmedClass < 0 || _hitsByClass[confirmedClass].Count == 0)
        {
            Debug.Log("[PanelDetector] No panel detected this pass");
            OnScanCompletedNoResult?.Invoke();
            yield break;
        }

        var hits = _hitsByClass[confirmedClass];
        var panelPos = Vector3.zero;
        foreach (var h in hits) panelPos += h;
        panelPos /= hits.Count;

        string label = GetPanelLabel(confirmedClass);
        _confirmedClasses.Add(confirmedClass);
        OnPanelConfirmed?.Invoke(confirmedClass, label);
        SpawnCharacterBesidePanel(panelPos, confirmedClass);
    }

    private int BestClass()
    {
        int best = -1, bestCount = 0;
        for (int i = 0; i < NumClasses; i++)
        {
            if (_confirmedClasses.Contains(i))
            {
                continue;
            }
            if (_hitsByClass[i].Count > bestCount)
            {
                bestCount = _hitsByClass[i].Count;
                best = i;
            }
        }
        return best;
    }

    private string GetPanelLabel(int classId)
    {
        if (classId >= 0 && classId < panelEntries.Length && panelEntries[classId] != null && !string.IsNullOrEmpty(panelEntries[classId].panelName))
            return panelEntries[classId].panelName;

        return $"Panel class {classId}";
    }

    private void SpawnCharacterBesidePanel(Vector3 panelWorldPos, int classId)
    {
        if (classId < 0 || classId >= panelEntries.Length || panelEntries[classId] == null)
        {
            Debug.LogError($"[PanelDetector] No PanelEntry configured for class");
            return;
        }

        PanelEntry entry = panelEntries[classId];
        if (entry.characterPrefab == null)
        {
            Debug.LogError($"[PanelDetector] characterPrefab is not assigned");
            return;
        }

        Transform cam = Camera.main != null ? Camera.main.transform : null;
        Vector3 toUser = cam != null ? new Vector3(cam.position.x - panelWorldPos.x, 0f, cam.position.z - panelWorldPos.z) : Vector3.forward;
        if (toUser.sqrMagnitude > 0.001f)
            toUser.Normalize();
        else
            toUser = Vector3.forward;

        float camY = cam != null ? cam.position.y : 1.7f;
        Vector3 spawnXZ;
        Quaternion spawnRot;

        if (spawnInFrontOfCamera && cam != null)
        {
            Vector3 camFwdFlat = new Vector3(cam.forward.x, 0f, cam.forward.z);
            if (camFwdFlat.sqrMagnitude > 0.001f) camFwdFlat.Normalize();
            else camFwdFlat = toUser;

            spawnXZ = new Vector3(cam.position.x + camFwdFlat.x * spawnForwardOffset, 0f, cam.position.z + camFwdFlat.z * spawnForwardOffset);
            spawnRot = Quaternion.LookRotation(-camFwdFlat);
        }
        else
        {
            spawnXZ = new Vector3(panelWorldPos.x + toUser.x * spawnForwardOffset, 0f, panelWorldPos.z + toUser.z * spawnForwardOffset);
            spawnRot = Quaternion.LookRotation(toUser);
        }

        float floorY = GetFloorY(spawnXZ, camY);
        Vector3 spawnPos = new Vector3(spawnXZ.x, floorY + spawnYOffset, spawnXZ.z);

        GameObject character = Instantiate(entry.characterPrefab, spawnPos, spawnRot);
        character.name = $"PanelCharacter_{classId}_{GetPanelLabel(classId)}";
        LastSpawnedCharacter = character;
        _spawnedCharacters.Add(character);

        if (!Mathf.Approximately(characterScale, 1f))
            character.transform.localScale = Vector3.one * characterScale;

        var muradAI = character.GetComponent<MuradController>();
        if (muradAI != null)
        {
            muradAI.enabled = false;
            Debug.Log("[PanelDetector] MuradController disabled");
        }

        var voiceCtrl = character.GetComponent<VoiceAPIController>();
        if (voiceCtrl != null)
        {
            voiceCtrl.enabled = false;
            Debug.Log("[PanelDetector] VoiceAPIController disabled");
        }

        var lipSync = character.GetComponentInChildren<LipSync.CustomLipSyncContext>(includeInactive: true);
        if (lipSync != null)
        {
            lipSync.enabled = false;
            Debug.Log("[PanelDetector] CustomLipSyncContext disabled at spawning characters");
        }

        // Delete text that are attached to characters like hello, thinking
        foreach (var tmp in character.GetComponentsInChildren<TMPro.TMP_Text>(includeInactive: true))
            tmp.text = "";
        foreach (Canvas c in character.GetComponentsInChildren<Canvas>(includeInactive: true))
            c.enabled = false;

        var panelNPCCtrl = character.GetComponent<PanelNPCController>();
        if (panelNPCCtrl != null)
        {
            panelNPCCtrl.Init(entry.idleClip, entry.talkingClip);
            panelNPCCtrl.SetPanelPosition(panelWorldPos);
        }
        else
        {
            var panelAnim = character.AddComponent<PanelCharacterAnimator>();
            panelAnim.Init(entry.idleClip, entry.talkingClip);
        }

        if (character.GetComponentInChildren<NPCBlinking>() == null)
        {
            character.AddComponent<NPCBlinking>();
            Debug.Log("[PanelDetector] NPCBlinking added to characters");
        }

        if (entry.narrationClip == null)
        {
            Debug.LogWarning($"[PanelDetector] narrationClip not set for class {classId}.");
            return;
        }

        if (narrationOnPoint)
        {
            LineRenderer lr = character.GetComponentInChildren<LineRenderer>(includeInactive: true);
            if (lr == null)
            {
                lr = laserPointer;
            }

            var interaction = character.AddComponent<PanelCharacterInteraction>();
            interaction.Init(this, entry.narrationClip, entry.narrationTranscript, rightController, leftController, lr);
            Debug.Log("[PanelDetector] Aim the controller at the character to trigger narration.");
        }
        else
        {
            StartCoroutine(PlayNarration(character, entry.narrationClip, entry.narrationTranscript));
        }
    }

    public void TriggerNarration(GameObject character, AudioClip clip, string transcript = null)
    {
        if (character == null || clip == null)
        {
            Debug.LogWarning("[PanelDetector] One of the charactersor clips are not assigned");
            return;
        }
        StartCoroutine(PlayNarration(character, clip, transcript));
    }

    public bool ReplayLastNarration()
    {
        if (_lastNarrationCharacter == null || _lastNarrationClip == null)
        {
            Debug.LogWarning("[PanelDetector] Nothing to replay yet.");
            return false;
        }
        StartCoroutine(PlayNarration(_lastNarrationCharacter, _lastNarrationClip, _lastNarrationTranscript));
        return true;
    }

    private IEnumerator PlayNarration(GameObject character, AudioClip clip, string transcript = null)
    {
        _lastNarrationCharacter = character;
        _lastNarrationClip = clip;
        _lastNarrationTranscript = transcript;

        yield return new WaitForSeconds(narrationDelay);

        if (IsReadyForNarration != null)
        {
            while (!IsReadyForNarration())
            {
                Debug.Log("[PanelDetector] Waiting for intro to finish before starting panel narration");
                yield return new WaitForSeconds(0.25f);
            }
        }

        // switching from standing animation to talking once the narration starts
        var panelNPC = character != null ? character.GetComponent<PanelNPCController>() : null;
        var charAnim = character != null ? character.GetComponent<PanelCharacterAnimator>() : null;
        if (panelNPC != null)
            panelNPC.PlayTalking();
        else if (charAnim != null)
            charAnim.PlayTalking();

        var lipSync = character.GetComponentInChildren<LipSync.CustomLipSyncContext>(true);
        AudioSource lipSyncAudio = null;

        if (lipSync != null)
        {
            lipSync.enabled = true;
            lipSync.FeedAudioClip(clip, transcript);
            lipSyncAudio = lipSync.GetComponent<AudioSource>();
            if (lipSyncAudio != null)
            {
                lipSyncAudio.outputAudioMixerGroup = null;
                lipSyncAudio.clip = clip;
                lipSyncAudio.loop = false;
                lipSyncAudio.mute = false;
                lipSyncAudio.volume = 0f;
                lipSyncAudio.spatialBlend = 0f;
                lipSyncAudio.Play();
                Debug.Log("[PanelDetector] CustomLipSyncContext playing");
            }
        }
        else
        {
            Debug.LogWarning("[PanelDetector] No CustomLipSyncContext found on character");
        }

        if (narrationAudioSource == null)
        {
            narrationAudioSource = gameObject.AddComponent<AudioSource>();
            Debug.LogWarning("[PanelDetector] narrationAudioSource not assigned");
        }

        narrationAudioSource.outputAudioMixerGroup = null;
        narrationAudioSource.spatialBlend = 0f;
        narrationAudioSource.volume = 1f;
        narrationAudioSource.loop = false;
        narrationAudioSource.clip = clip;
        narrationAudioSource.Play();

        OnNarrationStarted?.Invoke();
        Debug.Log($"[PanelDetector] Narration started: '{clip.name}'  {clip.length:F1}s");
        yield return new WaitForSeconds(clip.length);

        if (lipSyncAudio != null)
            lipSyncAudio.Stop();
        narrationAudioSource.Stop();
        Debug.Log("[PanelDetector] Narration finished.");

        if (lipSync != null)
        {
            lipSync.ResetVisemes();
            lipSync.enabled = false;
            Debug.Log("[PanelDetector] CustomLipSyncContext disabled.");
        }

        //Return to standing after narration 
        if (panelNPC != null)
            panelNPC.PlayIdle();
        else if (charAnim != null)
            charAnim.PlayIdle();

        OnNarrationFinished?.Invoke();
    }

    // Run yolo
    private IEnumerator RunInference(List<(Vector3 pos, int classId)> frameHits)
    {
        if (!TryEnsureCameraTexture())
            yield break;

        RenderTexture rt = RenderTexture.GetTemporary(InputSize, InputSize, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(_cameraTexture, rt);
        var inputTensor = new Tensor<float>(new TensorShape(1, 3, InputSize, InputSize));
        TextureConverter.ToTensor(rt, inputTensor, new TextureTransform().SetDimensions(InputSize, InputSize).SetTensorLayout(TensorLayout.NCHW));
        RenderTexture.ReleaseTemporary(rt);

        var schedule = _engine.ScheduleIterable(inputTensor);
        if (schedule == null)
        {
            Debug.LogError("[PanelDetector] ScheduleIterable returned null");
            inputTensor.Dispose();
            yield break;
        }

        int n = 0;
        while (schedule.MoveNext())
            if (++n % layersPerFrame == 0)
                yield return null;

        // Read output tensor 
        Tensor<float> out0 = _engine.PeekOutput(0) as Tensor<float>;
        if (out0 == null)
        {
            Debug.LogError("[PanelDetector] Output tensor is null");
            inputTensor.Dispose();
            yield break;
        }

        out0.ReadbackRequest();
        while (!out0.IsReadbackRequestDone()) yield return null;
        Tensor<float> cpu0 = out0.ReadbackAndClone() as Tensor<float>;

        var rawDets = new List<RawDetection>();

        for (int i = 0; i < Elements; i++)
        {
            int bestCls = -1;
            float bestConf = confidenceThreshold;
            for (int c = 0; c < NumClasses; c++)
            {
                float conf = cpu0[(4 + c) * Elements + i];
                if (conf > bestConf)
                {
                    bestConf = conf;
                    bestCls = c;
                }
            }

            if (bestCls < 0)
            {
                continue;
            }
            if (_confirmedClasses.Contains(bestCls))
            {
                continue;
            }

            rawDets.Add(new RawDetection { cx = cpu0[0 * Elements + i], cy = cpu0[1 * Elements + i], w = cpu0[2 * Elements + i], h = cpu0[3 * Elements + i], conf = bestConf, classId = bestCls });
        }

        List<RawDetection> kept = ApplyNonMaximumSuppression(rawDets);

        foreach (var det in kept)
        {
            if (_envRaycast == null || Camera.main == null)
                continue;

            float vx = Mathf.Clamp01(det.cx / InputSize);
            float vy = Mathf.Clamp01(1f - det.cy / InputSize);
            Ray ray = Camera.main.ViewportPointToRay(new Vector3(vx, vy, 0f));
            if (!_envRaycast.Raycast(ray, out var hit, 10f))
            {
                continue;
            }
            frameHits.Add((hit.point, det.classId));
        }

        //Draw box around the detected panel 
        if (kept.Count > 0)
            DrawDetectionBox(kept[0]);
        else
            HideDetectionBox();

        inputTensor.Dispose();
        out0.Dispose();
        cpu0.Dispose();
    }

    private void DrawDetectionBox(RawDetection det)
    {
        if (detectionBox == null || _envRaycast == null || Camera.main == null)
        {
            Debug.LogError("[PanelDetector] No box, no raycast, no camera");
            return;
        }

        float halfW = det.w * 0.5f * detectionBoxScale;
        float halfH = det.h * 0.5f * detectionBoxScale;

        Vector2[] corners = { new Vector2(det.cx - halfW, det.cy - halfH), new Vector2(det.cx + halfW, det.cy - halfH), new Vector2(det.cx + halfW, det.cy + halfH), new Vector2(det.cx - halfW, det.cy + halfH), };

        var world = new Vector3[4];
        for (int i = 0; i < 4; i++)
        {
            float vx = Mathf.Clamp01(corners[i].x / InputSize);
            float vy = Mathf.Clamp01(1f - corners[i].y / InputSize);
            Ray ray = Camera.main.ViewportPointToRay(new Vector3(vx, vy, 0f));
            if (!_envRaycast.Raycast(ray, out var hit, 10f))
            {
                HideDetectionBox();
                return;
            }
            world[i] = hit.point;
        }

        detectionBox.useWorldSpace = true;
        detectionBox.loop = true;
        detectionBox.positionCount = 4;
        detectionBox.SetPositions(world);
        detectionBox.startColor = detectionBoxColor;
        detectionBox.endColor = detectionBoxColor;
        detectionBox.enabled = true;
    }

    private void HideDetectionBox()
    {
        if (detectionBox != null) detectionBox.enabled = false;
    }
    private struct RawDetection
    {
        public float cx, cy, w, h, conf;
        public int classId;
    }

    private List<RawDetection> ApplyNonMaximumSuppression(List<RawDetection> dets)
    {
        dets.Sort((a, b) => b.conf.CompareTo(a.conf));

        var kept = new List<RawDetection>();
        var suppressed = new bool[dets.Count];
        for (int i = 0; i < dets.Count; i++)
        {
            if (suppressed[i])
            {
                continue;
            }
            kept.Add(dets[i]);
            for (int j = i + 1; j < dets.Count; j++)
            {
                if (suppressed[j])
                    continue;

                if (dets[j].classId == dets[i].classId && ComputeIoU(dets[i], dets[j]) > iouThreshold)
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

        if (ix2 <= ix1 || iy2 <= iy1)
        {
            return 0f;
        }

        float inter = (ix2 - ix1) * (iy2 - iy1);
        float union = a.w * a.h + b.w * b.h - inter;
        float result = inter / union;
        return result;
    }

    private Vector3 ResolveSeparation(Vector3 desiredXZ, Vector3 toUser)
    {
        if (minCharacterSeparation <= 0f)
            return desiredXZ;

        Vector3 lateral = Vector3.Cross(Vector3.up, toUser);
        if (lateral.sqrMagnitude < 0.0001f)
            lateral = Vector3.right;
        lateral.Normalize();

        float step = Mathf.Max(0.05f, separationStep);
        const int maxSteps = 16;
        for (int k = 0; k <= maxSteps; k++)
        {
            for (int sign = (k == 0 ? 0 : 1); sign >= (k == 0 ? 0 : -1); sign -= 2)
            {
                Vector3 candidate = desiredXZ + lateral * (step * k * sign);
                if (IsClearOfSpawned(candidate))
                    return candidate;
                if (k == 0)
                    break;
            }
        }
        return desiredXZ;
    }
    private bool IsClearOfSpawned(Vector3 xz)
    {
        float minSqr = minCharacterSeparation * minCharacterSeparation;
        for (int i = _spawnedCharacters.Count - 1; i >= 0; i--)
        {
            GameObject go = _spawnedCharacters[i];
            if (go == null)
            {
                _spawnedCharacters.RemoveAt(i);
                continue;
            }

            Vector3 p = go.transform.position;
            float dx = p.x - xz.x;
            float dz = p.z - xz.z;
            if (dx * dx + dz * dz < minSqr)
                return false;
        }
        return true;
    }

    //Used MRUK to find floor 
    private float GetFloorY(Vector3 nearPos, float cameraY)
    {
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
        catch (Exception e)
        {
            Debug.LogError("[PanelDetector] MRUK not present: " + e.Message);
        }
        if (_envRaycast != null)
        {
            float estimatedFloor = cameraY - 1.7f;
            Vector3 origin = new Vector3(nearPos.x, estimatedFloor + 0.3f, nearPos.z);
            if (_envRaycast.Raycast(new Ray(origin, Vector3.down), out var hit, 0.8f) &&
                Vector3.Dot(hit.normal, Vector3.up) > 0.7f)
                return hit.point.y;
        }
        return cameraY - 1.7f;
    }

    private void LoadModel()
    {
        if (yoloModel == null)
        {
            Debug.LogError("[PanelDetector] Model is not assigned");
            return;
        }
        try
        {
            _model = ModelLoader.Load(yoloModel);
            _engine = new Worker(_model, backend);
            _modelLoaded = true;
            Debug.Log($"[PanelDetector] Model loaded");
        }
        catch (Exception e)
        {
            Debug.LogError("[PanelDetector] Failed to load model: " + e.Message);
        }
    }

    private bool TryEnsureCameraTexture()
    {
        if (_cameraAccess == null || !_cameraAccess.IsPlaying)
        {
            return false;
        }
        if (_cameraTexture != null)
            return true;
        _cameraTexture = _cameraAccess.GetTexture();
        return _cameraTexture != null;
    }
}


[System.Serializable]
public class PanelEntry
{
    public string panelName;
    public GameObject characterPrefab;
    public AudioClip narrationClip;
    [TextArea(3, 8)]
    public string narrationTranscript;
    public AnimationClip idleClip;
    public AnimationClip talkingClip;
}
