using System;
using System.Collections;
using System.Threading.Tasks;
using UnityEngine;
using Meta.XR;
using Meta.XR.MRUtilityKit;
using Unity.InferenceEngine;

public class ObeliskYOLODetector : MonoBehaviour
{
    [Header("YOLO Model")]
    [SerializeField] private ModelAsset sentisModel;
#if UNITY_EDITOR
    [SerializeField] private BackendType backend = BackendType.CPU;
#else
    [SerializeField] private BackendType backend = BackendType.GPUCompute;
#endif
    [SerializeField] private int layersPerFrame = 50;
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
    [SerializeField] private float timeMachineForwardOffset = 2.5f;
    [SerializeField] private float timeMachineHeightOffset = 0f;
    [Header("Hassan")]
    [SerializeField] private GameObject hassan;
    [SerializeField] private float hassanRightOffset = 1.0f;
    [SerializeField] private float hassanYaw = -90f;
    [Header("UI")]
    [SerializeField] private GameObject scanningUI;
    [SerializeField] private GameObject detectedUI;
    [SerializeField] private YOLODiagnosticUI diagnosticUI;
    [SerializeField] private ObeliskStatusHUD statusHUD;
    [Header("Debug")]
    [SerializeField] private bool dumpInputFrames = false;
    [SerializeField] private int dumpEveryNFrames = 120;

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

    private void Awake()
    {
        if (timeMachine != null)
        {
            timeMachine.SetActive(false);
        }

        if (hassan != null)
        {
            hassan.SetActive(false);
        }

        if (characterRoot != null)
        {
            characterRoot.gameObject.SetActive(false);
        }

        _cameraAccess = FindAnyObjectByType<PassthroughCameraAccess>();
        _envRaycast = FindAnyObjectByType<EnvironmentRaycastManager>();

        if (_cameraAccess == null)
        {
            Debug.LogWarning("[ObeliskYOLO] PassthroughCameraAccess missing.");
        }

        if (_envRaycast == null)
        {
            Debug.LogWarning("[ObeliskYOLO] EnvironmentRaycastManager missing.");
        }
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

    public void StartDetection()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        _spawned = false;
        _consecutiveHits = 0;

        if (scanningUI != null)
        {
            scanningUI.SetActive(true);
        }

        if (detectedUI != null)
        {
            detectedUI.SetActive(false);
        }

        if (statusHUD != null) 
        {
            statusHUD.ShowScanning(confirmFrames);
        }

        StartCoroutine(DetectionLoop());

        Debug.Log("[ObeliskYOLO] Detection started.");
    }

    public void StopDetection()
    {
        _running = false;

        StopAllCoroutines();

        if (scanningUI != null)
        {
            scanningUI.SetActive(false);
        }

        if (detectedUI != null)
        {
            detectedUI.SetActive(false);
        }

        if (diagnosticUI != null)
        {
            diagnosticUI.ClearDiagnostics();
        }

        if (characterRoot != null)
        {
            characterRoot.gameObject.SetActive(false);
        }

        if (statusHUD != null) 
        {
            statusHUD.Hide();
        }

        _consecutiveHits = 0;
        _spawned = false;

        Debug.Log("[ObeliskYOLO] Detection stopped.");
    }

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

    private IEnumerator RunInference()
    {
        if (!_modelLoaded)
        {
            yield break;
        }

        if (_cameraAccess == null || !_cameraAccess.IsPlaying)
        {
            yield break;
        }

        Texture camTex = _cameraAccess.GetTexture();

        if (camTex == null)
        {
            Debug.LogWarning("[ObeliskYOLO] Camera texture null.");
            yield break;
        }

        RenderTexture rt = RenderTexture.GetTemporary(InputSize, InputSize, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(camTex, rt);

        if (dumpInputFrames && Time.frameCount % dumpEveryNFrames == 0)
        {
            DumpRenderTexture(rt, Time.frameCount);
        }

        TextureTransform texTransform = new TextureTransform().SetDimensions(InputSize, InputSize).SetTensorLayout(TensorLayout.NCHW);

        TextureConverter.ToTensor(rt, _inputTensor, texTransform);

        RenderTexture.ReleaseTemporary(rt);

        var schedule = _engine.ScheduleIterable(_inputTensor);

        if (schedule == null)
        {
            Debug.LogError("[ObeliskYOLO] ScheduleIterable returned null — skipping this frame to avoid main-thread stall.");
            yield break;
        }

        int counter = 0;
        while (schedule.MoveNext())
        {
            counter++;
            if (counter % layersPerFrame == 0)
            {
                yield return null;
            }
        }

        Tensor<float> output = _engine.PeekOutput(_model.outputs[0].name) as Tensor<float>;

        if (output == null)
        {
            Debug.LogError("[ObeliskYOLO] Output tensor null.");
            yield break;
        }

        output.ReadbackRequest();

        while (!output.IsReadbackRequestDone()) 
        {
            yield return null;
        }

        Tensor<float> cpu = output.ReadbackAndClone() as Tensor<float>;

        if (cpu == null)
        {
            Debug.LogError("[ObeliskYOLO] CPU tensor null.");
            yield break;
        }

        int numBoxes = cpu.shape[1];

        float bestConfPx = 0f;
        float bestX1Px = 0f;
        float bestY1Px = 0f;
        float bestX2Px = 0f;
        float bestY2Px = 0f;

        for (int i = 0; i < numBoxes; i++)
        {
            float conf = cpu[0, i, 4];

            if (float.IsNaN(conf) || float.IsInfinity(conf) || conf < confidenceThreshold || conf <= bestConfPx)
            {
                continue;
            }

            bestConfPx = conf;
            bestX1Px   = cpu[0, i, 0];
            bestY1Px   = cpu[0, i, 1];
            bestX2Px   = cpu[0, i, 2];
            bestY2Px   = cpu[0, i, 3];
        }

        cpu.Dispose();

        float wPx = bestX2Px - bestX1Px;
        float hPx = bestY2Px - bestY1Px;

        float bestCw = wPx / InputSize;
        float bestCh = hPx / InputSize;
        float bestCx = (bestX1Px + (wPx * 0.5f)) / InputSize;
        float bestCy = (bestY1Px + (hPx * 0.5f)) / InputSize;

        bestCx = Mathf.Clamp01(bestCx);
        bestCy = Mathf.Clamp01(bestCy);
        bestCw = Mathf.Clamp01(bestCw);
        bestCh = Mathf.Clamp01(bestCh);

        float bestConf = bestConfPx;

        if (bestConf >= confidenceThreshold)
        {
            if (bestCw < minBoxWidth || bestCh < minBoxHeight)
            {
                Debug.LogWarning($"[ObeliskYOLO] Rejected tiny box. " + $"W={bestCw:F3} H={bestCh:F3} (min W={minBoxWidth} H={minBoxHeight})");
                bestConf = 0f;
            }
        }

        if (diagnosticUI != null)
        {
            if (bestConf > 0f)
            {
                diagnosticUI.DisplayTestMetrics(new Vector3(bestCx, bestCy, 0f), bestCw, bestCh, "obelisk", bestConf);
            }
            else
            {
                diagnosticUI.ClearDiagnostics();
            }
        }

        if (bestConf < confidenceThreshold)
        {
            _consecutiveHits = 0;
            if (statusHUD != null){
                statusHUD.SetProgress(0, confirmFrames);
            }
            Debug.Log($"[ObeliskYOLO] No valid detection this frame.");
            yield break;
        }

        _consecutiveHits++;
        if (statusHUD != null){
            statusHUD.SetProgress(_consecutiveHits, confirmFrames);
        }

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

    private void PlaceCharacters()
    {
        _spawned = true;
        _running = false;

        if (scanningUI != null)
        {
            scanningUI.SetActive(false);
        }

        if (detectedUI != null)
        {
            detectedUI.SetActive(true);
        }

        if (statusHUD != null)
        {
            statusHUD.ShowFound();
        }

        _obeliskBase = FindObeliskBase();

        Debug.Log($"[ObeliskYOLO] Calculated Ground Position: {_obeliskBase}");

        Vector3 timeMachinePos = GetTimeMachinePosition(_obeliskBase);

        if (timeMachine != null)
        {
            timeMachine.transform.position = timeMachinePos;
            timeMachine.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
            timeMachine.SetActive(true);
        }

        if (hassan != null)
        {
            Vector3 hassanPos = timeMachinePos + GetCameraRight() * hassanRightOffset;
            hassanPos.y = _obeliskBase.y;
            hassan.transform.position = hassanPos;
            hassan.transform.rotation = Quaternion.Euler(0f, hassanYaw, 0f);
            hassan.SetActive(true);
        }

        if (characterRoot != null)
        {
            characterRoot.gameObject.SetActive(false);
        }
    }

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
        {
            return;
        }

        bool show = !characterRoot.gameObject.activeSelf;

        if (show)
        {
            float floorY = GetFloorY(_obeliskBase);

            Vector3 pos = new Vector3(_obeliskBase.x, floorY, _obeliskBase.z);

            characterRoot.position = pos;
            characterRoot.rotation = Quaternion.identity;

            if (_revealRoutine != null) StopCoroutine(_revealRoutine);
            _revealRoutine = StartCoroutine(StaggeredReveal());

            Debug.Log($"[ObeliskYOLO] Characters revealing at world coordinate: {pos}");
        }
        else
        {
            if (_revealRoutine != null) 
            {
                 StopCoroutine(_revealRoutine); _revealRoutine = null;
            }
            characterRoot.gameObject.SetActive(false);
            Debug.Log("[ObeliskYOLO] Characters hidden.");
        }
    }

    private Coroutine _revealRoutine;

    private IEnumerator StaggeredReveal()
    {
        int n = characterRoot.childCount;
        var children = new Transform[n];
        var wanted   = new bool[n];

        for (int i = 0; i < n; i++)
        {
            children[i] = characterRoot.GetChild(i);
            wanted[i] = children[i].gameObject.activeSelf;
            children[i].gameObject.SetActive(false);
        }

        characterRoot.gameObject.SetActive(true);

        for (int i = 0; i < n; i++)
        {
            if (wanted[i])
            {
                children[i].gameObject.SetActive(true);
            }
            yield return null;
        }

        _revealRoutine = null;
    }

    private Vector3 FindObeliskBase()
    {
        if (Camera.main == null)
        {
            return Vector3.zero;
        }

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

        return new Vector3(hitPoint.x, GetFloorY(hitPoint), hitPoint.z);
    }

    private Vector3 GetTimeMachinePosition(Vector3 obeliskBase)
    {
        if (Camera.main == null)
        {
            return obeliskBase;
        }

        Vector3 camPos = Camera.main.transform.position;
        Vector3 camForward = Camera.main.transform.forward;

        camForward.y = 0f;
        camForward.Normalize();

        return new Vector3(camPos.x + camForward.x * timeMachineForwardOffset, obeliskBase.y + timeMachineHeightOffset, camPos.z + camForward.z * timeMachineForwardOffset);
    }

    [Header("Floor")]
    [SerializeField] private bool useFloorLevelOrigin = true;
    [SerializeField] private float floorY = 0f;

    private float GetFloorY(Vector3 xzPos)
    {
        if (useFloorLevelOrigin)
            return floorY;
        try
        {
            if (MRUK.Instance != null)
            {
                MRUKRoom room = MRUK.Instance.GetCurrentRoom();
                if (room != null)
                {
                    Vector3 origin = new Vector3(xzPos.x, xzPos.y + 2f, xzPos.z);
                    if (room.Raycast(new Ray(origin, Vector3.down), 5f, out RaycastHit rayHit, out MRUKAnchor hitAnchor) && hitAnchor != null && hitAnchor.HasLabel("FLOOR"))
                    {
                        Debug.Log($"[ObeliskYOLO] Floor Y from MRUK raycast: {rayHit.point.y:F3} m");
                        return rayHit.point.y;
                    }

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

        if (_envRaycast != null)
        {
            Vector3 camPos = Camera.main != null ? Camera.main.transform.position : Vector3.up * 1.7f;
            Vector3 origin = new Vector3(xzPos.x, camPos.y, xzPos.z);
            if (_envRaycast.Raycast(new Ray(origin, Vector3.down), out var hit, 3f) && Vector3.Dot(hit.normal, Vector3.up) > 0.7f)
            {
                Debug.Log($"[ObeliskYOLO] Floor Y from depth raycast: {hit.point.y:F3} m");
                return hit.point.y;
            }
        }

        float camYFallback = Camera.main != null ? Camera.main.transform.position.y : 1.7f;
        float fallback = camYFallback - 1.7f;
        Debug.LogWarning($"[ObeliskYOLO] Floor Y fallback (cam - 1.7): {fallback:F3} m");
        return fallback;
    }

    private IEnumerator LoadModelAsync()
    {
        if (sentisModel == null)
        {
            Debug.LogError("[ObeliskYOLO] No model assigned.");
            yield break;
        }

        Model loadedModel = null;
        Exception loadError = null;

        var task = Task.Run(() =>
        {
            try { loadedModel = ModelLoader.Load(sentisModel); }
            catch (Exception e) { loadError = e; }
        });

        while (!task.IsCompleted)
        {
            yield return null;
        }

        if (loadError != null)
        {
            Debug.LogError($"[ObeliskYOLO] Critical failure loading NN model:\n{loadError}");
            yield break;
        }

        _model = loadedModel;
        _engine = new Worker(_model, backend);
        _inputTensor = new Tensor<float>(new TensorShape(1, 3, InputSize, InputSize));
        _modelLoaded = true;

        Debug.Log($"[ObeliskYOLO] Model loaded via {backend}.");

        if (autoStart)
            StartDetection();
    }
}