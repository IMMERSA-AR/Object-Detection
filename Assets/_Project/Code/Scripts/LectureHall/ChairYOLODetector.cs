using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Meta.XR;
using Meta.XR.MRUtilityKit;
using Unity.InferenceEngine;

/// <summary>
/// Detects real chairs using YOLOv8 + Quest 3S hardware depth.
///
/// KEY DESIGN — works even when chairs are touching or have zero gap:
///
///   1. Within each frame: NMS (Non-Maximum Suppression) in 2D image space.
///      YOLO produces separate bounding boxes per chair even when chairs touch.
///      NMS keeps all distinct boxes → one raycast per box → one 3D point per chair.
///
///   2. Across frames: small world-space radius (trackingRadius) matches the same
///      chair across inference passes. Because NMS already handled within-frame
///      separation, trackingRadius only needs to cover same-chair jitter (~0.05 m),
///      NOT the full chair spacing — so touching chairs are never merged.
///
/// Compatible with both yolov8n.onnx [1,84,8400] and yolov8n-seg.onnx [1,116,8400].
/// </summary>
public class ChairYOLODetector : MonoBehaviour
{
    [Header("YOLOv8 Model  (detection or seg variant — both work)")]
    [SerializeField] private ModelAsset sentisModel;
    [SerializeField] private BackendType backend = BackendType.CPU;
    [Tooltip("Spread inference across N layers per frame to avoid hitches")]
    [SerializeField] private int kLayersPerFrame = 20;

    [Header("Scan Settings")]
    [Tooltip("Total seconds to scan — aim at each chair for ~2 s then move to the next")]
    [SerializeField] private float scanDuration = 10f;

    [Tooltip("Run one YOLO pass every N seconds")]
    [SerializeField] private float inferenceInterval = 0.5f;

    [Tooltip("Minimum YOLO class confidence to consider a detection")]
    [SerializeField] private float confidenceThreshold = 0.40f;

    [Header("NMS — separates adjacent / touching chairs within each frame")]
    [Tooltip("IoU overlap threshold for Non-Maximum Suppression.\n" +
             "Two detections with IoU above this value are considered the same chair;\n" +
             "the lower-confidence one is discarded.\n" +
             "0.45 is the YOLO standard. Lower = more aggressive suppression.")]
    [SerializeField] private float iouThreshold = 0.45f;

    [Header("Seat Sampling")]
    [Tooltip("Vertical bias from bbox center toward seat (0 = center, 0.20 = 20% below).\n" +
             "Biases the raycast toward the seat surface rather than the chair back.")]
    [SerializeField] private float seatSampleBias = 0.20f;

    [Header("Cross-Frame Tracking")]
    [Tooltip("World-space XZ radius (metres) to match detections across frames to the same chair.\n" +
             "NMS already separates chairs within each frame, so this only needs to cover\n" +
             "same-chair hit jitter (typically 0.05–0.10 m).\n" +
             "Must be less than half the chair centre-to-centre distance.\n" +
             "Chair width ~0.45 m touching → centre gap 0.45 m → max safe radius 0.22 m.\n" +
             "Use 0.15 m for a comfortable safety margin.")]
    [SerializeField] private float trackingRadius = 0.15f;

    [Tooltip("Maximum chairs to track and return")]
    [SerializeField] private int maxChairs = 10;

    [Tooltip("Minimum hits a tracked chair must accumulate to count as a real chair.\n" +
             "With the 5-point stage-2 sampling most passes succeed, so 2 is enough.\n" +
             "Raise if you get phantom chairs; lower to 1 if real chairs are being missed.")]
    [SerializeField] private int minHitsPerChair = 2;

    [Header("Early Exit")]
    [Tooltip("Stop scanning early once the confirmed chair count has been stable for this many seconds.\n" +
             "Since all chairs are visible at once, this triggers after just a few passes.\n" +
             "Set to 0 to always run the full scanDuration.")]
    [SerializeField] private float stabilityDuration = 2f;

    [Header("Seat Height Window  (used by the downward stage-2 raycast)")]
    [Tooltip("Minimum height above floor for the downward ray to accept a surface as a seat.\n" +
             "Standard chair seat: ~0.40–0.45 m. Use 0.20 m for a generous lower bound.")]
    [SerializeField] private float seatMinHeight = 0.20f;

    [Tooltip("Maximum height above floor for the downward ray to accept a surface as a seat.\n" +
             "Standard chair seat: ~0.40–0.45 m. Use 0.70 m for a generous upper bound.")]
    [SerializeField] private float seatMaxHeight = 0.70f;

    // ── YOLO constants ────────────────────────────────────────────
    private const int InputSize  = 640;
    private const int Elements   = 8400;
    private const int ChairClass = 56;
    private const int SofaClass  = 57;

    // ── Cross-frame chair tracker ─────────────────────────────────

    private class TrackedChair
    {
        public Vector3 WorldCentroid;        // running mean of 3D hit positions
        public readonly List<Vector3> Hits = new List<Vector3>();

        public TrackedChair(Vector3 firstHit)
        {
            WorldCentroid = firstHit;
            Hits.Add(firstHit);
        }

        public void AddHit(Vector3 pos)
        {
            Hits.Add(pos);
            // Update running mean
            WorldCentroid = Vector3.zero;
            foreach (var h in Hits) WorldCentroid += h;
            WorldCentroid /= Hits.Count;
        }

        public float XZDistanceTo(Vector3 pos)
            => Vector2.Distance(new Vector2(WorldCentroid.x, WorldCentroid.z),
                                new Vector2(pos.x,           pos.z));
    }

    // ── private fields ────────────────────────────────────────────
    private PassthroughCameraAccess   _cameraAccess;
    private EnvironmentRaycastManager _envRaycast;
    private Model  _model;
    private Worker _engine;
    private bool   _modelLoaded;
    private bool   _scanning;
    private Texture _cameraTexture;

    // ── Unity lifecycle ───────────────────────────────────────────

    private void Awake()
    {
        _cameraAccess = FindAnyObjectByType<PassthroughCameraAccess>();
        _envRaycast   = FindAnyObjectByType<EnvironmentRaycastManager>();

        if (_cameraAccess == null)
            Debug.LogWarning("[ChairDepth] PassthroughCameraAccess not found in scene.");
        if (_envRaycast == null)
            Debug.LogWarning("[ChairDepth] EnvironmentRaycastManager not found in scene.");

        LoadModel();
    }

    private void OnDestroy() => _engine?.Dispose();

    // ── Public API ────────────────────────────────────────────────

    public void DetectChairs(Action<List<Vector3>> onComplete)
    {
        if (_scanning)
        {
            Debug.LogWarning("[ChairDepth] Already scanning — ignoring duplicate call.");
            return;
        }
        if (!_modelLoaded)
        {
            Debug.LogError("[ChairDepth] Model not loaded — check sentisModel assignment.");
            onComplete?.Invoke(new List<Vector3>());
            return;
        }
        _scanning = true;
        StartCoroutine(ScanCoroutine(onComplete));
    }

    // ── Scan loop ─────────────────────────────────────────────────

    private IEnumerator ScanCoroutine(Action<List<Vector3>> onComplete)
    {
        // Wait for passthrough camera
        float camWait = 0f;
        while (!TryEnsureCameraTexture())
        {
            yield return new WaitForSeconds(0.2f);
            camWait += 0.2f;
            if (camWait > 5f)
            {
                Debug.LogWarning("[ChairDepth] Camera unavailable — aborting.");
                _scanning = false;
                onComplete?.Invoke(new List<Vector3>());
                yield break;
            }
        }

        float floorY = DetectFloorY();
        Debug.Log($"[ChairDepth] Floor Y = {floorY:F3} m | " +
                  $"Seat window [{floorY + seatMinHeight:F2}, {floorY + seatMaxHeight:F2}] m");

        Debug.Log($"[ChairDepth] Scanning up to {scanDuration}s — look at all the chairs.");

        var tracked    = new List<TrackedChair>();
        float elapsed  = 0f;
        float stableSec = 0f;
        int lastConfirmed = 0;

        while (elapsed < scanDuration)
        {
            yield return new WaitForSeconds(inferenceInterval);
            elapsed += inferenceInterval;

            // RunInference returns one 3D point per distinct chair detected this frame (post-NMS)
            var frameHits = new List<Vector3>();
            yield return StartCoroutine(RunInference(frameHits, floorY));

            foreach (var hit in frameHits)
                TrackHit(tracked, hit);

            int confirmed = CountConfirmed(tracked);

            Debug.Log($"[ChairDepth] {elapsed:F1}/{scanDuration}s — " +
                      $"{frameHits.Count} in frame | {confirmed} confirmed.");

            if (confirmed != lastConfirmed)
            {
                lastConfirmed = confirmed;
                stableSec     = 0f;
                if (confirmed > 0)
                    Debug.Log($"[ChairDepth] ★ {confirmed} chair(s) confirmed!");
            }
            else if (confirmed > 0)
            {
                stableSec += inferenceInterval;
                if (stabilityDuration > 0f && stableSec >= stabilityDuration)
                {
                    Debug.Log($"[ChairDepth] Count stable at {confirmed} for {stableSec:F1}s — finishing early.");
                    break;
                }
            }
        }

        // Build final result from confirmed tracked chairs
        var chairs = new List<Vector3>();
        foreach (var c in tracked)
        {
            if (c.Hits.Count >= minHitsPerChair && chairs.Count < maxChairs)
            {
                chairs.Add(c.WorldCentroid);
                Debug.Log($"[ChairDepth] ✓ Chair {chairs.Count}: " +
                          $"{c.Hits.Count} hits → {c.WorldCentroid}");
            }
            else if (c.Hits.Count < minHitsPerChair)
            {
                Debug.Log($"[ChairDepth]   Discarded: only {c.Hits.Count} hits " +
                          $"(need {minHitsPerChair}) — centroid={c.WorldCentroid}");
            }
        }

        Debug.Log($"[ChairDepth] Scan complete → {chairs.Count} chair(s) returned.");
        _scanning = false;
        onComplete?.Invoke(chairs);
    }

    // ── Cross-frame tracking ──────────────────────────────────────

    private int CountConfirmed(List<TrackedChair> tracked)
    {
        int n = 0;
        foreach (var c in tracked)
            if (c.Hits.Count >= minHitsPerChair) n++;
        return n;
    }

    private void TrackHit(List<TrackedChair> tracked, Vector3 worldPos)
    {
        // Find the closest existing tracked chair (XZ distance)
        TrackedChair closest = null;
        float closestDist = trackingRadius;

        foreach (var c in tracked)
        {
            float d = c.XZDistanceTo(worldPos);
            if (d < closestDist) { closest = c; closestDist = d; }
        }

        if (closest != null)
            closest.AddHit(worldPos);
        else if (tracked.Count < maxChairs)
            tracked.Add(new TrackedChair(worldPos));
    }

    // ── YOLO inference + NMS + depth raycast ──────────────────────

    /// <summary>
    /// Runs one YOLO inference pass and returns one 3D world position per distinct
    /// chair detected in this frame (after NMS removes duplicates within the frame).
    /// </summary>
    private IEnumerator RunInference(List<Vector3> frameHits, float floorY)
    {
        if (!TryEnsureCameraTexture()) yield break;

        // Prepare input tensor
        RenderTexture rt = RenderTexture.GetTemporary(InputSize, InputSize, 0, RenderTextureFormat.ARGB32);
        Graphics.Blit(_cameraTexture, rt);
        var inputTensor = new Tensor<float>(new TensorShape(1, 3, InputSize, InputSize));
        TextureConverter.ToTensor(rt, inputTensor);
        RenderTexture.ReleaseTemporary(rt);

        // Run model spread across frames
        var schedule = _engine.ScheduleIterable(inputTensor);
        if (schedule == null)
        {
            _engine.Schedule(inputTensor);
        }
        else
        {
            int n = 0;
            while (schedule.MoveNext())
                if (++n % kLayersPerFrame == 0) yield return null;
        }

        // Read output0 only (works for both detection and seg models)
        Tensor<float> out0 = _engine.PeekOutput(0) as Tensor<float>;
        if (out0 == null)
        {
            Debug.LogError("[ChairDepth] output0 is null — wrong model?");
            inputTensor.Dispose();
            yield break;
        }

        out0.ReadbackRequest();
        while (!out0.IsReadbackRequestDone()) yield return null;

        Tensor<float> cpu0 = out0.ReadbackAndClone() as Tensor<float>;

        // Collect raw detections above confidence threshold
        var rawDets = new List<RawDetection>();
        for (int i = 0; i < Elements; i++)
        {
            float chairConf = cpu0[(ChairClass + 4) * Elements + i];
            float sofaConf  = cpu0[(SofaClass  + 4) * Elements + i];
            float bestConf  = Mathf.Max(chairConf, sofaConf);
            if (bestConf < confidenceThreshold) continue;

            rawDets.Add(new RawDetection
            {
                cx   = cpu0[0 * Elements + i],
                cy   = cpu0[1 * Elements + i],
                w    = cpu0[2 * Elements + i],
                h    = cpu0[3 * Elements + i],
                conf = bestConf
            });
        }

        // Apply NMS — keeps one detection per distinct chair in this frame
        List<RawDetection> nmsResult = ApplyNMS(rawDets);
        Debug.Log($"[ChairDepth]   {rawDets.Count} raw → {nmsResult.Count} after NMS.");

        float seatYMin = floorY + seatMinHeight;
        float seatYMax = floorY + seatMaxHeight;

        // ── Two-stage raycast per detection ──────────────────────────
        //
        // WHY two stages:
        //   The user looks at chairs HORIZONTALLY. The viewport ray hits the CHAIR
        //   BACK first (a vertical surface, upDot ≈ 0) before reaching the seat.
        //   Checking upDot on this hit always fails → 0 chairs detected.
        //
        // Stage 1 — viewport ray → locate the chair's XZ position.
        //   We don't care if we hit the chair back, wall, or seat — we just want
        //   the approximate XZ of where the chair is in the world.
        //
        // Stage 2 — downward ray from above seat height at that XZ → find the seat.
        //   Casting straight DOWN bypasses the chair back (vertical, no collision
        //   with a downward ray unless it is in the ray's path) and hits the
        //   horizontal seat surface directly. The height window [seatYMin, seatYMax]
        //   ensures we only accept genuine seat surfaces.
        // ─────────────────────────────────────────────────────────────

        foreach (var det in nmsResult)
        {
            if (_envRaycast == null || Camera.main == null) continue;

            // ── Stage 1: viewport ray → chair XZ ──────────────────
            float sampleX = det.cx;
            float sampleY = det.cy + det.h * seatSampleBias;
            float vx = Mathf.Clamp01(sampleX / InputSize);
            float vy = Mathf.Clamp01(1f - sampleY / InputSize); // image Y flipped

            Ray viewRay = Camera.main.ViewportPointToRay(new Vector3(vx, vy, 0f));
            if (!_envRaycast.Raycast(viewRay, out var viewHit, 8f))
            {
                Debug.Log($"[ChairDepth]   conf={det.conf:F2} — stage 1: no depth hit.");
                continue;
            }

            // ── Stage 2: downward ray → seat surface ───────────────
            // Cast straight DOWN from above the seat-height window.
            // WHY multi-point: stage 1's XZ can land on a chair leg (thin vertical
            // piece) or a mesh artifact → downward ray hits it with upDot≈0 and misses
            // the seat. Trying 5 nearby XZ offsets (±7 cm) guarantees at least one
            // cast lands on the flat seat surface rather than a leg or edge.
            float castFromY = seatYMax + 0.15f;
            float castDistY = (seatYMax - seatYMin) + 0.30f;

            // 5-point pattern: centre + 4 cardinal offsets
            float[] dxArr = { 0f,  0.07f, -0.07f,  0f,    0f   };
            float[] dzArr = { 0f,  0f,    0f,      0.07f, -0.07f };

            bool    seatFound = false;
            Vector3 seatPos   = Vector3.zero;

            for (int s = 0; s < dxArr.Length && !seatFound; s++)
            {
                Vector3 downOrigin = new Vector3(
                    viewHit.point.x + dxArr[s],
                    castFromY,
                    viewHit.point.z + dzArr[s]);

                if (!_envRaycast.Raycast(new Ray(downOrigin, Vector3.down), out var seatHit, castDistY))
                    continue;

                float upDot = Vector3.Dot(seatHit.normal, Vector3.up);
                if (upDot < 0.5f) continue;                         // not a horizontal surface

                float seatY = seatHit.point.y;
                if (seatY < seatYMin || seatY > seatYMax) continue; // outside seat window

                // Use the viewHit XZ (chair centre) with the accurate Y from downcast
                seatPos   = new Vector3(viewHit.point.x, seatY, viewHit.point.z);
                seatFound = true;

                Debug.Log($"[ChairDepth]   ✓ conf={det.conf:F2} | sample#{s} | " +
                          $"seat Y={seatY:F2} | pos={seatPos} | upDot={upDot:F2}");
            }

            if (!seatFound)
            {
                Debug.Log($"[ChairDepth]   conf={det.conf:F2} — stage 2: no horizontal seat found " +
                          $"at any of 5 XZ offsets around ({viewHit.point.x:F2},{viewHit.point.z:F2}).");
                continue;
            }

            frameHits.Add(seatPos);
        }

        inputTensor.Dispose();
        out0.Dispose();
        cpu0.Dispose();
    }

    // ── NMS ───────────────────────────────────────────────────────

    private struct RawDetection
    {
        public float cx, cy, w, h, conf;
    }

    /// <summary>
    /// Standard greedy NMS: sort by confidence, keep a detection only if it does
    /// not overlap (IoU > iouThreshold) with any already-kept detection.
    /// Separates adjacent/touching chairs even when they have zero physical gap,
    /// because their bounding boxes have low IoU (they barely overlap in the image).
    /// </summary>
    private List<RawDetection> ApplyNMS(List<RawDetection> dets)
    {
        // Sort descending by confidence
        dets.Sort((a, b) => b.conf.CompareTo(a.conf));

        var kept      = new List<RawDetection>();
        var suppressed = new bool[dets.Count];

        for (int i = 0; i < dets.Count; i++)
        {
            if (suppressed[i]) continue;

            kept.Add(dets[i]);

            for (int j = i + 1; j < dets.Count; j++)
            {
                if (suppressed[j]) continue;
                if (ComputeIoU(dets[i], dets[j]) > iouThreshold)
                    suppressed[j] = true;
            }
        }

        return kept;
    }

    private static float ComputeIoU(RawDetection a, RawDetection b)
    {
        float ax1 = a.cx - a.w * 0.5f, ay1 = a.cy - a.h * 0.5f;
        float ax2 = a.cx + a.w * 0.5f, ay2 = a.cy + a.h * 0.5f;
        float bx1 = b.cx - b.w * 0.5f, by1 = b.cy - b.h * 0.5f;
        float bx2 = b.cx + b.w * 0.5f, by2 = b.cy + b.h * 0.5f;

        float ix1 = Mathf.Max(ax1, bx1), iy1 = Mathf.Max(ay1, by1);
        float ix2 = Mathf.Min(ax2, bx2), iy2 = Mathf.Min(ay2, by2);

        if (ix2 <= ix1 || iy2 <= iy1) return 0f;

        float inter = (ix2 - ix1) * (iy2 - iy1);
        float aArea = a.w * a.h;
        float bArea = b.w * b.h;
        return inter / (aArea + bArea - inter);
    }

    // ── Floor Y detection ─────────────────────────────────────────

    private float DetectFloorY()
    {
        Vector3 cam = Camera.main != null ? Camera.main.transform.position : Vector3.up * 1.7f;

        // 1. MRUK FLOOR anchor
        try
        {
            if (MRUK.Instance != null)
            {
                MRUKRoom room = MRUK.Instance.GetCurrentRoom();
                if (room != null)
                {
                    foreach (MRUKAnchor anchor in room.Anchors)
                    {
                        if (anchor.HasLabel("FLOOR"))
                        {
                            float fy = anchor.transform.position.y;
                            Debug.Log($"[ChairDepth] Floor Y from MRUK: {fy:F3} m");
                            return fy;
                        }
                    }
                }
            }
        }
        catch (Exception e) { Debug.LogWarning("[ChairDepth] MRUK floor: " + e.Message); }

        // 2. Low-origin raycast (avoids hitting desk/table surfaces)
        if (_envRaycast != null)
        {
            float estimatedFloor = cam.y - 1.7f;
            float castY = estimatedFloor + 0.6f;
            float[] xOff = { 0f,  0.4f, -0.4f,  0f,    0f   };
            float[] zOff = { 0f,  0f,    0f,    0.4f, -0.4f };
            float lowest = float.MaxValue;
            bool  found  = false;

            for (int n = 0; n < xOff.Length; n++)
            {
                Vector3 origin = new Vector3(cam.x + xOff[n], castY, cam.z + zOff[n]);
                if (_envRaycast.Raycast(new Ray(origin, Vector3.down), out var hit, 1.5f) &&
                    Vector3.Dot(hit.normal, Vector3.up) > 0.7f)
                {
                    if (hit.point.y < lowest) { lowest = hit.point.y; found = true; }
                }
            }
            if (found)
            {
                Debug.Log($"[ChairDepth] Floor Y from raycast: {lowest:F3} m");
                return lowest;
            }
        }

        // 3. Fallback
        float fallback = cam.y - 1.7f;
        Debug.LogWarning($"[ChairDepth] Floor Y fallback: {fallback:F3} m");
        return fallback;
    }

    // ── Helpers ───────────────────────────────────────────────────

    private void LoadModel()
    {
        if (sentisModel == null)
        {
            Debug.LogError("[ChairDepth] sentisModel not assigned.");
            return;
        }
        try
        {
            _model       = ModelLoader.Load(sentisModel);
            _engine      = new Worker(_model, backend);
            _modelLoaded = true;
            Debug.Log($"[ChairDepth] Model loaded ({backend}).");
        }
        catch (Exception e)
        {
            Debug.LogError("[ChairDepth] Failed to load model: " + e.Message);
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
