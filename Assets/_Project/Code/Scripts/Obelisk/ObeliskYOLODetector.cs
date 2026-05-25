using System;
using System.Collections;
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
    [SerializeField] private BackendType backend = BackendType.CPU;
    [SerializeField] private int layersPerFrame = 10;

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
    [SerializeField] private float timeMachineSideOffset = 2.0f;

    [Header("UI")]
    [SerializeField] private GameObject scanningUI;
    [SerializeField] private GameObject detectedUI;
    [SerializeField] private YOLODiagnosticUI diagnosticUI;

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

        if (characterRoot != null)
            characterRoot.gameObject.SetActive(false);

        _cameraAccess = FindAnyObjectByType<PassthroughCameraAccess>();
        _envRaycast = FindAnyObjectByType<EnvironmentRaycastManager>();

        if (_cameraAccess == null)
            Debug.LogWarning("[ObeliskYOLO] PassthroughCameraAccess missing.");

        if (_envRaycast == null)
            Debug.LogWarning("[ObeliskYOLO] EnvironmentRaycastManager missing.");

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

        _inputTensor ??= new Tensor<float>(new TensorShape(1, 3, InputSize, InputSize));

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
            float x1, y1, x2, y2, conf, cls;

            try
            {
                x1   = cpu[0, i, 0];
                y1   = cpu[0, i, 1];
                x2   = cpu[0, i, 2];
                y2   = cpu[0, i, 3];
                conf = cpu[0, i, 4];
                cls  = cpu[0, i, 5];
            }
            catch (Exception e)
            {
                Debug.LogError($"[ObeliskYOLO] Tensor indexing failed at box {i}\n{e}");
                break;
            }

            if (float.IsNaN(conf) || float.IsInfinity(conf))
                continue;

            conf = Mathf.Clamp01(conf);

            if (i < 5)
            {
                Debug.Log(
                    $"DET {i:D2} | CONF={conf:F3} | CLS={cls:F0} | " +
                    $"XYXY=({x1:F1},{y1:F1}) -> ({x2:F1},{y2:F1})");
            }

            if (conf < confidenceThreshold)
                continue;

            if (conf <= bestConfPx)
                continue;

            bestConfPx = conf;
            bestX1Px = x1;
            bestY1Px = y1;
            bestX2Px = x2;
            bestY2Px = y2;

            Debug.Log(
                $"[ObeliskYOLO] NEW BEST -> CONF={bestConfPx:F3} " +
                $"BOX_PX=({x1:F1},{y1:F1},{x2:F1},{y2:F1})");
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

        Debug.Log(
            $"[ObeliskYOLO] FINAL STATE: " +
            $"conf={bestConf:F4} " +
            $"center=({bestCx:F3},{bestCy:F3}) " +
            $"size=({bestCw:F3}x{bestCh:F3}) " +
            $"threshold={confidenceThreshold:F3}");

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
            Debug.Log($"[ObeliskYOLO] No valid detection this frame.");
            yield break;
        }

        _consecutiveHits++;

        _lastCX = (_consecutiveHits == 1) ? bestCx : Mathf.Lerp(_lastCX, bestCx, 0.3f);
        _lastCY = (_consecutiveHits == 1) ? bestCy : Mathf.Lerp(_lastCY, bestCy, 0.3f);

        Debug.Log(
            $"[ObeliskYOLO] VALID DETECTION! " +
            $"CONF={bestConf:F3} | HITS={_consecutiveHits}/{confirmFrames}");

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

        _obeliskBase = FindObeliskBase();

        Debug.Log($"[ObeliskYOLO] Calculated Ground Position: {_obeliskBase}");

        if (timeMachine != null)
        {
            timeMachine.transform.position = GetTimeMachinePosition(_obeliskBase);
            timeMachine.SetActive(true);
        }

        if (characterRoot != null)
            characterRoot.gameObject.SetActive(false);
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
            characterRoot.gameObject.SetActive(true);

            Debug.Log($"[ObeliskYOLO] Characters active at world coordinate: {pos}");
        }
        else
        {
            characterRoot.gameObject.SetActive(false);
            Debug.Log("[ObeliskYOLO] Characters hidden.");
        }
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

        // Direction from camera to obelisk, flattened to XZ plane
        Vector3 toObelisk = obeliskBase - Camera.main.transform.position;
        toObelisk.y = 0f;

        // Perpendicular direction (to the right of the camera-to-obelisk vector)
        Vector3 sideDir = Vector3.Cross(Vector3.up, toObelisk.normalized);

        return new Vector3(
            obeliskBase.x + sideDir.x * timeMachineSideOffset,
            obeliskBase.y,
            obeliskBase.z + sideDir.z * timeMachineSideOffset);
    }

    private float GetFloorY(Vector3 xzPos)
    {
        float camY = Camera.main != null
            ? Camera.main.transform.position.y
            : 1.7f;

        return camY - 1.7f;
    }

    // ─────────────────────────────────────────────────────────
    // Model Loading
    // ─────────────────────────────────────────────────────────

    private void LoadModel()
    {
        if (sentisModel == null)
        {
            Debug.LogError("[ObeliskYOLO] No model assigned.");
            return;
        }

        try
        {
            _model = ModelLoader.Load(sentisModel);
            _engine = new Worker(_model, backend);
            _modelLoaded = true;

            Debug.Log($"[ObeliskYOLO] Model engine initialized successfully via {backend}.");

            // Diagnostic: log model I/O shapes
            foreach (var input in _model.inputs)
                Debug.Log($"[ObeliskYOLO] Model IN: {input.name} shape={input.shape}");

            foreach (var outputDesc in _model.outputs)
                Debug.Log($"[ObeliskYOLO] Model OUT: {outputDesc.name}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[ObeliskYOLO] Critical failure loading NN model:\n{e}");
        }
    }
}