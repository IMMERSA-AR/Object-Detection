using System;
using System.Collections;
using System.Threading.Tasks;
using UnityEngine;
using Meta.XR;
using Meta.XR.MRUtilityKit;
using Unity.InferenceEngine;

/// <summary>
/// YOLO26 Obelisk Detector for Meta Quest 3S
/// CORRECTED VERSION
/// - Fixed: bestConf zeroed silently due to pixel-vs-normalized space confusion
/// - Fixed: size check now uses normalized coords consistently
/// - Added: per-frame final state diagnostics
/// - Added: debug input dump for preprocessing verification
/// </summary>
public class ObeliskYOLODetector : MonoBehaviour
{
    // ─────────────────────────────────────────────────────────
    // Inspector
    // ─────────────────────────────────────────────────────────

    [Header("YOLO Model")]
    [SerializeField] private ModelAsset sentisModel;
#if UNITY_EDITOR
    [SerializeField] private BackendType backend = BackendType.CPU;
#else
    [SerializeField] private BackendType backend = BackendType.GPUCompute;
#endif
    [SerializeField] private int layersPerFrame = 50;  // raise in Inspector if it was 10

    [Header("Detection")]
    [SerializeField] private bool autoStart = true;
    [SerializeField] private float detectionInterval = 1.0f;
    [SerializeField] private int confirmFrames = 5;

    [Range(0f, 1f)]
    [SerializeField] private float confidenceThreshold = 0.15f;

    [Header("Spatial Validation (normalized 0-1)")]
    [SerializeField] private float minBoxWidth = 0.02f;
    [SerializeField] private float minBoxHeight = 0.02f;

    [Header("Characters")]
    [SerializeField] private Transform characterRoot;

    [Header("Time Machine")]
    [SerializeField] private GameObject timeMachine;
    [Tooltip("Distance (metres) IN FRONT of the user the time machine spawns.")]
    [SerializeField] private float timeMachineForwardOffset = 2.5f;
    [Tooltip("Vertical offset (metres) added on top of the floor height. " +
             "Set to 0 if the prefab pivot is at its base; increase if the pivot " +
             "is centred and the machine sinks into the floor.")]
    [SerializeField] private float timeMachineHeightOffset = 0f;

    [Header("Hassan")]
    [Tooltip("Hassan character — placed to the RIGHT of the time machine (same " +
             "camera-right axis used to place the time machine).")]
    [SerializeField] private GameObject hassan;
    [Tooltip("Distance (metres) further RIGHT of the time machine Hassan stands.")]
    [SerializeField] private float hassanRightOffset = 1.0f;
    [Tooltip("Yaw rotation (degrees) applied to Hassan when he spawns.")]
    [SerializeField] private float hassanYaw = -90f;

    [Header("UI")]
    [SerializeField] private GameObject scanningUI;
    [SerializeField] private GameObject detectedUI;
    [SerializeField] private YOLODiagnosticUI diagnosticUI;

    [Tooltip("Optional polished status HUD that floats in front of the user. " +
             "Drag in the GameObject that has the ObeliskStatusHUD component.")]
    [SerializeField] private ObeliskStatusHUD statusHUD;

    [Header("Debug")]
    [SerializeField] private bool dumpInputFrames = false;
    [SerializeField] private int dumpEveryNFrames = 120;

    // ─────────────────────────────────────────────────────────
    // Runtime
    // ─────────────────────────────────────────────────────────

    private const int InputSize = 640;

    private Model _model;
    private Worker _engine;

    private bool _modelLoaded;
    private bool _running;
    private bool _spawned;

    private int _consecutiveHits;

    private float _lastCX;
    private float _lastCY;

    private Vector3 _obeliskBase;

    private Tensor<float> _inputTensor;

    private PassthroughCameraAccess _cameraAccess;
    private EnvironmentRaycastManager _envRaycast;

    public Action OnObeliskConfirmed;

    // ─────────────────────────────────────────────────────────
    // Unity Events
    // ─────────────────────────────────────────────────────────

    private void Awake()
    {
        if (timeMachine != null)
            timeMachine.SetActive(false);

        if (hassan != null)
            hassan.SetActive(false);

        if (characterRoot != null)
            characterRoot.gameObject.SetActive(false);

        _cameraAccess = FindAnyObjectByType<PassthroughCameraAccess>();
        _envRaycast = FindAnyObjectByType<EnvironmentRaycastManager>();

        if (_cameraAccess == null)
            Debug.LogWarning("[ObeliskYOLO] PassthroughCameraAccess missing.");

        if (_envRaycast == null)
            Debug.LogWarning("[ObeliskYOLO] EnvironmentRaycastManager missing.");
    }

    private void Start()
    {
        StartCoroutine(LoadModelAsync());
    }

    private void OnDestroy()
    {
        _engine?.Dispose();
        _inputTensor?.Dispose();
    }

    // ─────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────

    public void StartDetection()
    {
        if (_running)
            return;

        _running = true;
        _spawned = false;
        _consecutiveHits = 0;

        if (scanningUI != null)
            scanningUI.SetActive(true);

        if (detectedUI != null)
            detectedUI.SetActive(false);

        if (statusHUD != null) statusHUD.ShowScanning(confirmFrames);

        StartCoroutine(DetectionLoop());

        Debug.Log("[ObeliskYOLO] Detection started.");
    }

    public void StopDetection()
    {
        _running = false;

        StopAllCoroutines();

        if (scanningUI != null)
            scanningUI.SetActive(false);

        if (detectedUI != null)
            detectedUI.SetActive(false);

        if (diagnosticUI != null)
            diagnosticUI.ClearDiagnostics();

        if (characterRoot != null)
            characterRoot.gameObject.SetActive(false);

        if (statusHUD != null) statusHUD.Hide();

        _consecutiveHits = 0;
        _spawned = false;

        Debug.Log("[ObeliskYOLO] Detection stopped.");
    }

    // ─────────────────────────────────────────────────────────
    // Detection Loop
    // ─────────────────────────────────────────────────────────

    private IEnumerator DetectionLoop()
    {
        if (_cameraAccess != null)
        {
            float waited = 0f;

            while (!_cameraAccess.IsPlaying && waited < 5f)
            {
                yield return new WaitForSeconds(0.2f);
                waited += 0.2f;
            }

            Debug.Log(_cameraAccess.IsPlaying
                ? "[ObeliskYOLO] Passthrough ready."
                : "[ObeliskYOLO] Passthrough failed.");
        }

        while (_running && !_spawned)
        {
            yield return StartCoroutine(RunInference());

            yield return new WaitForSeconds(detectionInterval);
        }
    }

    // ─────────────────────────────────────────────────────────
    // Inference Engine
    // ─────────────────────────────────────────────────────────

    private IEnumerator RunInference()
    {
        if (!_modelLoaded)
            yield break;

        if (_cameraAccess == null || !_cameraAccess.IsPlaying)
            yield break;

        Texture camTex = _cameraAccess.GetTexture();

        if (camTex == null)
        {
            Debug.LogWarning("[ObeliskYOLO] Camera texture null.");
            yield break;
        }

        // Blit Passthrough texture into a standard RT
        RenderTexture rt = RenderTexture.GetTemporary(InputSize, InputSize, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(camTex, rt);

        // Optional: dump input for preprocessing verification
        if (dumpInputFrames && Time.frameCount % dumpEveryNFrames == 0)
        {
            DumpRenderTexture(rt, Time.frameCount);
        }

        TextureTransform texTransform = new TextureTransform()
            .SetDimensions(InputSize, InputSize)
            .SetTensorLayout(TensorLayout.NCHW);

        TextureConverter.ToTensor(rt, _inputTensor, texTransform);

        RenderTexture.ReleaseTemporary(rt);

        // Schedule the engine
        var schedule = _engine.ScheduleIterable(_inputTensor);

        if (schedule == null)
        {
            // Don't fall back to the blocking _engine.Schedule(_inputTensor) —
            // that runs the whole model on the main thread and causes Android ANR.
            // Skip this frame; the next tick will try again.
            Debug.LogError("[ObeliskYOLO] ScheduleIterable returned null — skipping this frame to avoid main-thread stall.");
            yield break;
        }

        int counter = 0;
        while (schedule.MoveNext())
        {
            counter++;
            if (counter % layersPerFrame == 0)
                yield return null;
        }

        Tensor<float> output = _engine.PeekOutput(_model.outputs[0].name) as Tensor<float>;

        if (output == null)
        {
            Debug.LogError("[ObeliskYOLO] Output tensor null.");
            yield break;
        }

        // Non-blocking readback: queue the GPU→CPU copy, then yield each frame
        // until it completes. This keeps Unity rendering during inference
        // instead of freezing the main thread (the cause of the ANR dialog).
        output.ReadbackRequest();
        while (!output.IsReadbackRequestDone()) yield return null;

        Tensor<float> cpu = output.ReadbackAndClone() as Tensor<float>;

        if (cpu == null)
        {
            Debug.LogError("[ObeliskYOLO] CPU tensor null.");
            yield break;
        }

        // ─────────────────────────────────────────────────────
        // YOLO26 End-to-End Parser
        // Output format: [1, 300, 6] with rows [x1, y1, x2, y2, conf, cls]
        // Coordinates are in PIXEL space (0-640), not normalized.
        // ─────────────────────────────────────────────────────

        int numBoxes = cpu.shape[1];

        // All work below is in PIXEL space until the very end.
        float bestConfPx = 0f;
        float bestX1Px = 0f;
        float bestY1Px = 0f;
        float bestX2Px = 0f;
        float bestY2Px = 0f;

        for (int i = 0; i < numBoxes; i++)
        {
            float conf = cpu[0, i, 4];

            if (float.IsNaN(conf) || float.IsInfinity(conf) || conf < confidenceThreshold || conf <= bestConfPx)
                continue;

            bestConfPx = conf;
            bestX1Px   = cpu[0, i, 0];
            bestY1Px   = cpu[0, i, 1];
            bestX2Px   = cpu[0, i, 2];
            bestY2Px   = cpu[0, i, 3];
        }

        cpu.Dispose();

        // ─────────────────────────────────────────────────────
        // Normalize to 0-1 range ONCE, at the end
        // ─────────────────────────────────────────────────────

        float wPx = bestX2Px - bestX1Px;
        float hPx = bestY2Px - bestY1Px;

        float bestCw = wPx / InputSize;
        float bestCh = hPx / InputSize;
        float bestCx = (bestX1Px + (wPx * 0.5f)) / InputSize;
        float bestCy = (bestY1Px + (hPx * 0.5f)) / InputSize;

        // Clamp normalized values to handle edge-of-frame detections
        bestCx = Mathf.Clamp01(bestCx);
        bestCy = Mathf.Clamp01(bestCy);
        bestCw = Mathf.Clamp01(bestCw);
        bestCh = Mathf.Clamp01(bestCh);

        float bestConf = bestConfPx;

        // ─────────────────────────────────────────────────────
        // Spatial Validation (normalized space)
        // ─────────────────────────────────────────────────────

        if (bestConf >= confidenceThreshold)
        {
            if (bestCw < minBoxWidth || bestCh < minBoxHeight)
            {
                Debug.LogWarning(
                    $"[ObeliskYOLO] Rejected tiny box. " +
                    $"W={bestCw:F3} H={bestCh:F3} (min W={minBoxWidth} H={minBoxHeight})");
                bestConf = 0f;
            }
        }

        // ─────────────────────────────────────────────────────
        // Final state diagnostic
        // ─────────────────────────────────────────────────────

        // ─────────────────────────────────────────────────────
        // UI Debug Overlay
        // ─────────────────────────────────────────────────────

        if (diagnosticUI != null)
        {
            if (bestConf > 0f)
            {
                diagnosticUI.DisplayTestMetrics(
                    new Vector3(bestCx, bestCy, 0f),
                    bestCw, bestCh,
                    "obelisk",
                    bestConf);
            }
            else
            {
                diagnosticUI.ClearDiagnostics();
            }
        }

        // ─────────────────────────────────────────────────────
        // Process Detection Result
        // ─────────────────────────────────────────────────────

        if (bestConf < confidenceThreshold)
        {
            _consecutiveHits = 0;
            if (statusHUD != null) statusHUD.SetProgress(0, confirmFrames);
            Debug.Log($"[ObeliskYOLO] No valid detection this frame.");
            yield break;
        }

        _consecutiveHits++;
        if (statusHUD != null) statusHUD.SetProgress(_consecutiveHits, confirmFrames);

        _lastCX = (_consecutiveHits == 1) ? bestCx : Mathf.Lerp(_lastCX, bestCx, 0.3f);
        _lastCY = (_consecutiveHits == 1) ? bestCy : Mathf.Lerp(_lastCY, bestCy, 0.3f);

        Debug.Log($"[ObeliskYOLO] VALID DETECTION! CONF={bestConf:F3} | HITS={_consecutiveHits}/{confirmFrames}");

        if (_consecutiveHits >= confirmFrames)
        {
            Debug.Log("[ObeliskYOLO] TARGET OBELISK CONFIRMED.");
            PlaceCharacters();
            OnObeliskConfirmed?.Invoke();
        }
    }

    // ─────────────────────────────────────────────────────────
    // Debug Helpers
    // ─────────────────────────────────────────────────────────

    private void DumpRenderTexture(RenderTexture rt, int frameNum)
    {
        try
        {
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;

            Texture2D tex = new Texture2D(InputSize, InputSize, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, InputSize, InputSize), 0, 0);
            tex.Apply();

            string path = $"{Application.persistentDataPath}/yolo_input_{frameNum}.png";
            System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());

            RenderTexture.active = prev;
            UnityEngine.Object.Destroy(tex);

            Debug.Log($"[ObeliskYOLO] Input dumped to {path}");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ObeliskYOLO] Failed to dump input: {e.Message}");
        }
    }

    // ─────────────────────────────────────────────────────────
    // Spatial Placement & Raycasting
    // ─────────────────────────────────────────────────────────

    private void PlaceCharacters()
    {
        _spawned = true;
        _running = false;

        if (scanningUI != null)
            scanningUI.SetActive(false);

        if (detectedUI != null)
            detectedUI.SetActive(true);

        if (statusHUD != null) statusHUD.ShowFound();

        _obeliskBase = FindObeliskBase();

        Debug.Log($"[ObeliskYOLO] Calculated Ground Position: {_obeliskBase}");

        Vector3 timeMachinePos = GetTimeMachinePosition(_obeliskBase);

        if (timeMachine != null)
        {
            timeMachine.transform.position = timeMachinePos;
            timeMachine.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
            timeMachine.SetActive(true);
        }

        // Hassan stands `hassanRightOffset` metres to the RIGHT of the time machine
        // (computed from the time machine position, so they stay together). His feet
        // sit on the floor (he doesn't inherit the machine's vertical offset).
        if (hassan != null)
        {
            Vector3 hassanPos = timeMachinePos + GetCameraRight() * hassanRightOffset;
            hassanPos.y = _obeliskBase.y;
            hassan.transform.position = hassanPos;
            hassan.transform.rotation = Quaternion.Euler(0f, hassanYaw, 0f);
            hassan.SetActive(true);
        }

        if (characterRoot != null)
            characterRoot.gameObject.SetActive(false);
    }

    /// <summary>Flattened camera-right vector (XZ plane) — the "right of the user" axis.</summary>
    private Vector3 GetCameraRight()
    {
        if (Camera.main == null) return Vector3.right;
        Vector3 r = Camera.main.transform.right;
        r.y = 0f;
        return r.normalized;
    }

    public void ToggleCharacters()
    {
        if (characterRoot == null)
            return;

        bool show = !characterRoot.gameObject.activeSelf;

        if (show)
        {
            float floorY = GetFloorY(_obeliskBase);

            Vector3 pos = new Vector3(
                _obeliskBase.x,
                floorY,
                _obeliskBase.z);

            characterRoot.position = pos;
            characterRoot.rotation = Quaternion.identity;

            // Reveal characters ONE PER FRAME instead of all 8 in a single frame.
            // Activating 8 skinned characters at once causes a big hitch (animator
            // init + GPU upload + first-render shader work all land together).
            // Spreading it over a few frames turns one giant spike into tiny ones.
            if (_revealRoutine != null) StopCoroutine(_revealRoutine);
            _revealRoutine = StartCoroutine(StaggeredReveal());

            Debug.Log($"[ObeliskYOLO] Characters revealing at world coordinate: {pos}");
        }
        else
        {
            if (_revealRoutine != null) { StopCoroutine(_revealRoutine); _revealRoutine = null; }
            characterRoot.gameObject.SetActive(false);
            Debug.Log("[ObeliskYOLO] Characters hidden.");
        }
    }

    private Coroutine _revealRoutine;

    // Activates the character root with its children hidden, then switches each
    // character on across consecutive frames to spread the activation cost.
    private IEnumerator StaggeredReveal()
    {
        int n = characterRoot.childCount;
        var children = new Transform[n];
        var wanted   = new bool[n];

        // Remember which children were meant to be visible, then hide them all.
        for (int i = 0; i < n; i++)
        {
            children[i] = characterRoot.GetChild(i);
            wanted[i]   = children[i].gameObject.activeSelf;
            children[i].gameObject.SetActive(false);
        }

        characterRoot.gameObject.SetActive(true);

        // Turn on one character per frame.
        for (int i = 0; i < n; i++)
        {
            if (wanted[i]) children[i].gameObject.SetActive(true);
            yield return null;
        }

        _revealRoutine = null;
    }

    private Vector3 FindObeliskBase()
    {
        if (Camera.main == null)
            return Vector3.zero;

        float vx = Mathf.Clamp01(_lastCX);
        float vy = Mathf.Clamp01(1f - _lastCY);

        Ray ray = Camera.main.ViewportPointToRay(new Vector3(vx, vy, 0f));

        Vector3 hitPoint;

        if (_envRaycast != null && _envRaycast.Raycast(ray, out var hit, 20f))
        {
            hitPoint = hit.point;
        }
        else
        {
            hitPoint = ray.GetPoint(5f);
        }

        return new Vector3(
            hitPoint.x,
            GetFloorY(hitPoint),
            hitPoint.z);
    }

    private Vector3 GetTimeMachinePosition(Vector3 obeliskBase)
    {
        if (Camera.main == null)
            return obeliskBase;

        Vector3 camPos     = Camera.main.transform.position;
        Vector3 camForward = Camera.main.transform.forward;
        camForward.y = 0f;
        camForward.Normalize();

        return new Vector3(
            camPos.x + camForward.x * timeMachineForwardOffset,
            obeliskBase.y + timeMachineHeightOffset,
            camPos.z + camForward.z * timeMachineForwardOffset);
    }

    [Header("Floor")]
    [Tooltip("Trust the FloorLevel tracking origin and place the obelisk base / time machine / " +
             "characters on the floor at 'Floor Y' (the floor's world Y, 0 with FloorLevel " +
             "tracking + rig at origin) instead of the MRUK/depth/cam-1.7 guessing that was " +
             "sinking everything to mid-height.")]
    [SerializeField] private bool useFloorLevelOrigin = true;
    [Tooltip("World Y of the real floor. Leave 0 for FloorLevel tracking; nudge if your rig isn't at world origin.")]
    [SerializeField] private float floorY = 0f;

    private float GetFloorY(Vector3 xzPos)
    {
        // FloorLevel tracking origin puts the real floor at a known plane, so trust that
        // instead of the MRUK/depth/cam-1.7 chain that was sinking everything to mid-height.
        if (useFloorLevelOrigin)
            return floorY;

        // 1. MRUK — the real, scanned floor.
        try
        {
            if (MRUK.Instance != null)
            {
                MRUKRoom room = MRUK.Instance.GetCurrentRoom();
                if (room != null)
                {
                    // 1a. Cast straight down at the obelisk's XZ and let MRUK
                    //     return the exact floor surface height there.
                    Vector3 origin = new Vector3(xzPos.x, xzPos.y + 2f, xzPos.z);
                    if (room.Raycast(new Ray(origin, Vector3.down), 5f, out RaycastHit rayHit, out MRUKAnchor hitAnchor)
                        && hitAnchor != null && hitAnchor.HasLabel("FLOOR"))
                    {
                        Debug.Log($"[ObeliskYOLO] Floor Y from MRUK raycast: {rayHit.point.y:F3} m");
                        return rayHit.point.y;
                    }

                    // 1b. Fall back to the room's floor anchor height (floor is flat).
                    if (room.FloorAnchors != null && room.FloorAnchors.Count > 0)
                    {
                        float fy = room.FloorAnchors[0].transform.position.y;
                        Debug.Log($"[ObeliskYOLO] Floor Y from MRUK floor anchor: {fy:F3} m");
                        return fy;
                    }
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[ObeliskYOLO] MRUK floor lookup failed: " + e.Message);
        }

        // 2. Environment depth raycast straight down.
        if (_envRaycast != null)
        {
            Vector3 camPos = Camera.main != null ? Camera.main.transform.position : Vector3.up * 1.7f;
            Vector3 origin = new Vector3(xzPos.x, camPos.y, xzPos.z);
            if (_envRaycast.Raycast(new Ray(origin, Vector3.down), out var hit, 3f)
                && Vector3.Dot(hit.normal, Vector3.up) > 0.7f)
            {
                Debug.Log($"[ObeliskYOLO] Floor Y from depth raycast: {hit.point.y:F3} m");
                return hit.point.y;
            }
        }

        // 3. Heuristic fallback: assume eyes ~1.7 m above the floor.
        float camYFallback = Camera.main != null ? Camera.main.transform.position.y : 1.7f;
        float fallback = camYFallback - 1.7f;
        Debug.LogWarning($"[ObeliskYOLO] Floor Y fallback (cam - 1.7): {fallback:F3} m");
        return fallback;
    }

    // ─────────────────────────────────────────────────────────
    // Model Loading
    // ─────────────────────────────────────────────────────────

    private IEnumerator LoadModelAsync()
    {
        if (sentisModel == null)
        {
            Debug.LogError("[ObeliskYOLO] No model assigned.");
            yield break;
        }

        // Load model bytes on a background thread so the main thread stays responsive
        Model loadedModel = null;
        Exception loadError = null;
        var task = Task.Run(() =>
        {
            try { loadedModel = ModelLoader.Load(sentisModel); }
            catch (Exception e) { loadError = e; }
        });

        while (!task.IsCompleted)
            yield return null;

        if (loadError != null)
        {
            Debug.LogError($"[ObeliskYOLO] Critical failure loading NN model:\n{loadError}");
            yield break;
        }

        // Worker must be created on the main thread
        _model = loadedModel;
        _engine = new Worker(_model, backend);
        // Pre-allocate the input tensor once so RunInference never allocates mid-frame
        _inputTensor = new Tensor<float>(new TensorShape(1, 3, InputSize, InputSize));
        _modelLoaded = true;

        Debug.Log($"[ObeliskYOLO] Model loaded via {backend}.");

        if (autoStart)
            StartDetection();
    }
}