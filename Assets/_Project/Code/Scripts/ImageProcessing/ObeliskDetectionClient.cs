using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using Meta.XR;

/// <summary>
/// Sends camera frames to the Python obelisk detection server and
/// spawns characters when the obelisk is confirmed detected.
///
/// Setup:
///  1. Run obelisk_server.py on your PC.
///  2. Set ServerIP to your PC's IPv4 address (run ipconfig in CMD).
///  3. Attach this script to a GameObject in the Obelisk scene.
///  4. Assign characterPrefab and characterSpawnRoot in Inspector.
///  5. Ensure a PassthroughCameraAccess component exists in the scene
///     (added automatically by the [BuildingBlock] Passthrough building block).
/// </summary>
public class ObeliskDetectionClient : MonoBehaviour
{
    [Header("Server")]
    [Tooltip("Your PC's IPv4 address — run 'ipconfig' in CMD to find it.")]
    public string serverIP = "192.168.1.5";
    public int serverPort = 5000;

    [Header("Detection")]
    [Tooltip("Send a frame every N seconds. Lower = more responsive but more CPU.")]
    public float detectionInterval = 1.0f;

    [Tooltip("How many consecutive detections needed before spawning characters.\n" +
             "Higher = more robust, less twitchy.")]
    public int confirmFrames = 3;

    [Tooltip("Minimum detection confidence — reject if bbox height < this fraction of frame.")]
    public float minObeliskHeightFraction = 0.3f;

    [Header("Character Spawning")]
    [Tooltip("How far from the camera the characters are placed (metres) when the obelisk is detected.")]
    public float spawnDistance = 3.0f;

    [Tooltip("Prefabs to spawn around the detected obelisk.")]
    public GameObject[] characterPrefabs;

    [Tooltip("Parent transform for spawned characters (optional).")]
    public Transform spawnRoot;

    [Header("UI Feedback")]
    [Tooltip("Optional UI shown while scanning.")]
    public GameObject scanningUI;

    [Tooltip("Optional UI shown when obelisk is confirmed detected.")]
    public GameObject detectedUI;

    // ── Callback ───────────────────────────────────────────────
    /// <summary>
    /// Optional callback fired once the obelisk is confirmed and characters
    /// are spawned. ObeliskManager assigns this before calling StartDetection().
    /// </summary>
    public System.Action OnObeliskConfirmed;

    // ── private ───────────────────────────────────────────────
    private string _serverUrl;
    private bool _running = false;
    private bool _spawned = false;
    private int _consecutiveHits = 0;

    // Last confirmed detection data
    private float _lastCX, _lastCY;   // normalized center (0-1)
    private float _lastHeight;         // normalized height

    // Reused capture buffer — allocated once to avoid per-frame GC pressure.
    private const int CapW = 640, CapH = 480;
    private Texture2D _captureTex;

    // Passthrough camera (Meta XR) — provides the real-world camera feed on Quest.
    // The virtual Camera.main render does NOT include passthrough (it's a compositor
    // overlay), so we need this to send frames the server can actually detect from.
    private PassthroughCameraAccess _cameraAccess;

    // ── Awake ──────────────────────────────────────────────────
    private void Awake()
    {
        _cameraAccess = FindAnyObjectByType<PassthroughCameraAccess>();
        if (_cameraAccess == null)
            Debug.LogWarning("[ObeliskClient] PassthroughCameraAccess not found in scene — " +
                             "will use virtual camera fallback (editor/testing only).");
    }

    // ── Start ──────────────────────────────────────────────────
    private void Start()
    {
        _serverUrl = $"http://{serverIP}:{serverPort}";
        _captureTex = new Texture2D(CapW, CapH, TextureFormat.RGB24, false);

        if (scanningUI != null) scanningUI.SetActive(false);
        if (detectedUI != null) detectedUI.SetActive(false);
    }

    private void OnDestroy()
    {
        if (_captureTex != null) Destroy(_captureTex);
    }

    // ── Public API ─────────────────────────────────────────────

    /// <summary>Call this to start scanning (e.g. when user enters obelisk scene).</summary>
    public void StartDetection()
    {
        if (_running) return;
        _running = true;
        _spawned = false;
        _consecutiveHits = 0;

        if (scanningUI != null) scanningUI.SetActive(true);
        if (detectedUI != null) detectedUI.SetActive(false);

        StartCoroutine(DetectionLoop());
        Debug.Log("[ObeliskClient] Detection started.");
    }

    /// <summary>Stops detection and clears spawned characters.</summary>
    public void StopDetection()
    {
        _running = false;
        StopAllCoroutines();

        if (scanningUI != null) scanningUI.SetActive(false);
        if (detectedUI != null) detectedUI.SetActive(false);

        // Destroy spawned characters
        if (spawnRoot != null)
            foreach (Transform child in spawnRoot)
                Destroy(child.gameObject);

        _spawned = false;
        _consecutiveHits = 0;
        Debug.Log("[ObeliskClient] Detection stopped.");
    }

    // ── Detection loop ─────────────────────────────────────────
    private IEnumerator DetectionLoop()
    {
        // First, ping the server to confirm it's reachable
        yield return StartCoroutine(PingServer());

        // Wait for the passthrough camera to produce its first frame
        if (_cameraAccess != null)
        {
            float waited = 0f;
            while (!_cameraAccess.IsPlaying && waited < 5f)
            {
                yield return new WaitForSeconds(0.2f);
                waited += 0.2f;
            }
            if (!_cameraAccess.IsPlaying)
                Debug.LogWarning("[ObeliskClient] Passthrough camera not ready after 5 s — falling back to virtual camera.");
            else
                Debug.Log("[ObeliskClient] Passthrough camera ready.");
        }

        while (_running && !_spawned)
        {
            yield return StartCoroutine(SendFrameAndDetect());
            yield return new WaitForSeconds(detectionInterval);
        }
    }

    private IEnumerator PingServer()
    {
        using var req = UnityWebRequest.Get($"{_serverUrl}/ping");
        req.timeout = 3;
        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
            Debug.Log("[ObeliskClient] Server reachable ✅");
        else
            Debug.LogWarning($"[ObeliskClient] Server not reachable ⚠️  " +
                             $"Make sure obelisk_server.py is running on {serverIP}:{serverPort}");
    }

    private IEnumerator SendFrameAndDetect()
    {
        // ── Capture camera frame ───────────────────────────────
        // Uses the Quest's passthrough camera if available,
        // otherwise captures from a RenderTexture snapshot.
        byte[] jpegBytes = CaptureFrameAsJpeg();
        if (jpegBytes == null)
        {
            Debug.LogWarning("[ObeliskClient] Frame capture returned null.");
            yield break;
        }

        // ── Send to server ─────────────────────────────────────
        using var req = new UnityWebRequest($"{_serverUrl}/detect", "POST");
        req.uploadHandler = new UploadHandlerRaw(jpegBytes);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/octet-stream");
        req.timeout = 10;

        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogWarning($"[ObeliskClient] Request failed: {req.error}");
            _consecutiveHits = 0;
            yield break;
        }

        // ── Parse JSON response ────────────────────────────────
        string json = req.downloadHandler.text;
        Debug.Log($"[ObeliskClient] Response: {json}");

        DetectionResult result = JsonUtility.FromJson<DetectionResult>(json);

        if (!result.detected)
        {
            Debug.Log($"[ObeliskClient] Not detected. Reason: {result.reason}");
            _consecutiveHits = 0;
            yield break;
        }

        // Reject if obelisk appears too small (user is too far or wrong detection)
        if (result.bbox.height < minObeliskHeightFraction)
        {
            Debug.Log($"[ObeliskClient] Detection too small " +
                      $"(height={result.bbox.height:F2} < {minObeliskHeightFraction}).");
            _consecutiveHits = 0;
            yield break;
        }

        // ── Confirmed detection ────────────────────────────────
        _consecutiveHits++;
        _lastCX = result.bbox.cx;
        _lastCY = result.bbox.cy;
        _lastHeight = result.bbox.height;

        Debug.Log($"[ObeliskClient] Detection hit {_consecutiveHits}/{confirmFrames}  " +
                  $"center=({_lastCX:F2},{_lastCY:F2})  height={_lastHeight:F2}");

        if (_consecutiveHits >= confirmFrames)
        {
            Debug.Log("[ObeliskClient] Obelisk CONFIRMED — spawning characters.");
            SpawnCharacters();
            OnObeliskConfirmed?.Invoke();
        }
    }

    // ── Capture frame ──────────────────────────────────────────

    private byte[] CaptureFrameAsJpeg()
    {
        RenderTexture rt = null;
        try
        {
            if (_cameraAccess != null && _cameraAccess.IsPlaying)
            {
                Texture camTex = _cameraAccess.GetTexture();
                if (camTex != null)
                {
                    rt = RenderTexture.GetTemporary(CapW, CapH, 0, RenderTextureFormat.ARGB32);
                    Graphics.Blit(camTex, rt);
                }
            }

            if (rt == null)
            {
                Camera cam = Camera.main;
                if (cam == null)
                {
                    Debug.LogWarning("[ObeliskClient] No passthrough camera and no main camera found.");
                    return null;
                }
                rt = RenderTexture.GetTemporary(CapW, CapH, 0);
                cam.targetTexture = rt;
                cam.Render();
                cam.targetTexture = null;
            }

            RenderTexture.active = rt;
            _captureTex.ReadPixels(new Rect(0, 0, CapW, CapH), 0, 0);
            _captureTex.Apply();
            RenderTexture.active = null;
            return _captureTex.EncodeToJPG(75);
        }
        finally
        {
            if (rt != null) RenderTexture.ReleaseTemporary(rt);
        }
    }

    // ── Spawn characters around the obelisk ───────────────────

    private void SpawnCharacters()
    {
        _spawned = true;
        _running = false;

        if (scanningUI != null) scanningUI.SetActive(false);
        if (detectedUI != null) detectedUI.SetActive(true);

        if (characterPrefabs == null || characterPrefabs.Length == 0)
        {
            Debug.LogWarning("[ObeliskClient] No character prefabs assigned.");
            return;
        }

        // Estimate 3D obelisk position:
        // Cast a ray from camera through the detected center pixel
        Camera cam = Camera.main;
        if (cam == null) return;

        // Convert normalized image coords to viewport coords
        Vector3 viewport = new Vector3(_lastCX, 1f - _lastCY, spawnDistance);
        Vector3 worldCenter = cam.ViewportToWorldPoint(viewport);
        worldCenter.y = 0f;   // snap to floor

        Debug.Log($"[ObeliskClient] Spawning at world position {worldCenter}");

        // Spawn characters in a circle around the obelisk base
        int count = characterPrefabs.Length;
        for (int i = 0; i < count; i++)
        {
            if (characterPrefabs[i] == null) continue;

            float angle = i * (360f / count);
            float radius = 1.5f;
            Vector3 offset = Quaternion.Euler(0, angle, 0) * Vector3.forward * radius;
            Vector3 spawnPos = worldCenter + offset;

            // Face toward obelisk center
            Quaternion rot = Quaternion.LookRotation(worldCenter - spawnPos);
            rot = Quaternion.Euler(0, rot.eulerAngles.y, 0);

            Transform parent = spawnRoot != null ? spawnRoot : transform;
            GameObject go = Instantiate(characterPrefabs[i], spawnPos, rot, parent);
            go.name = $"ObeliskCharacter_{i}";
            Debug.Log($"[ObeliskClient] Spawned {go.name} at {spawnPos}");
        }
    }

    // ── JSON response structure ────────────────────────────────

    [Serializable]
    private class DetectionResult
    {
        public bool detected;
        public string reason;
        public BBox bbox;
    }

    [Serializable]
    private class BBox
    {
        public float x, y, width, height, cx, cy;
    }
}
