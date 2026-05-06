using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Meta.XR;
using Meta.XR.MRUtilityKit;
using TMPro;

/// <summary>
/// PRIMARY chair detection. Three-level pipeline tried in order:
///
///   1. MRUK anchor labels  (COUCH → OTHER → TABLE)
///      Instant. Works when Space Setup labelled seating correctly.
///
///   2. EnvironmentRaycastManager grid scan
///      Fires a dense grid of downward rays at seat height across the area in
///      front of the user. Keeps upward-facing hits, clusters them → chairs.
///      Works on every Quest 3/3S without any extra configuration.
///
///   3. MRUK GlobalMesh triangles
///      Works when MRUK is configured with a GlobalMesh prefab.
///      Currently optional / bonus — levels 1–2 cover most rooms.
///
///   → Returns empty list if all levels fail.
///     ExperienceManager then falls back to ChairYOLODetector → grid spawn.
///
/// Public API matches ChairYOLODetector: DetectChairs(Action<List<Vector3>>)
/// </summary>
public class ChairMeshDetector : MonoBehaviour
{
    // ── Inspector ─────────────────────────────────────────────────

    [Header("Seat Height Filter  (relative to detected floor)")]
    [Tooltip("Minimum distance above floor for a surface to count as a seat.")]
    public float seatMinAboveFloor = 0.25f;

    [Tooltip("Maximum distance above floor for a surface to count as a seat.\n" +
             "Standard chair seat: ~0.43 m. Set to 0.50 to exclude beds/sofas (~0.55 m+).")]
    public float seatMaxAboveFloor = 0.50f;

    [Header("Level 2 — Raycast Grid Scan")]
    [Tooltip("How far forward from the user the grid scan starts (metres).\n" +
             "Keep ≥ 0.6 so the user's own body and nearby objects are excluded.")]
    public float gridStartForward = 0.6f;

    [Tooltip("How far forward the grid scan extends (metres).")]
    public float gridEndForward = 4.0f;

    [Tooltip("Half-width of the grid to either side of the user's forward direction (metres).\n" +
             "3.0 m covers chairs even when the user is not facing them directly.")]
    public float gridHalfWidth = 3.0f;

    [Tooltip("Spacing between adjacent rays in the grid (metres).\n" +
             "0.10 m gives ~16 hits on a 0.40×0.40 m chair seat.")]
    public float gridStep = 0.10f;

    [Tooltip("Minimum dot(normal, up) for a raycast hit to count as a horizontal surface.")]
    [Range(0.5f, 1f)]
    public float minUpwardDot = 0.70f;

    [Tooltip("Minimum XZ distance from the user a cluster must be to count as a chair.\n" +
             "Prevents nearby furniture (shelves, desks right next to you) from being detected.")]
    public float minDistanceFromUser = 0.6f;

    [Header("AR Debug Markers")]
    [Tooltip("Spawn a visible disc + label in AR at each detected chair position.")]
    public bool showDebugMarkers = true;

    [Tooltip("Color of the chair marker disc.")]
    public Color markerColor = Color.yellow;

    [Tooltip("Seconds the markers stay visible. 0 = permanent until scene reloads.")]
    public float markerDuration = 30f;

    [Header("Clustering  (shared by all levels)")]
    [Tooltip("XZ radius (metres) within which two hits are considered the same chair.\n" +
             "Keep below half the chair centre-to-centre distance (≈ 0.21 m for 0.42 m spacing).")]
    public float clusterRadius = 0.20f;

    [Tooltip("Minimum number of grid hits a cluster must have to be kept as a real chair.\n" +
             "A 0.40×0.40 m seat at 0.10 m step gives ~8-16 hits. 6 filters noise safely.")]
    public int minHitsPerCluster = 6;

    [Tooltip("Maximum chairs to return.")]
    public int maxChairs = 12;

    [Tooltip("After clustering, merge any two cluster centres closer than this distance.\n" +
             "Fixes split detections when the two halves of one chair seat form separate\n" +
             "clusters. Default 0.45 m = typical chair width. Keep below min chair spacing (~0.50 m).")]
    public float chairDiameter = 0.45f;

    [Header("Multi-Scan Loop")]
    [Tooltip("Stop scanning as soon as this many chairs have been accumulated.\n" +
             "Set to 0 to always run all maxScanAttempts passes.")]
    public int targetChairCount = 3;

    [Tooltip("Seconds to wait between scan passes.\n" +
             "Move your head between passes to let the depth sensor cover more area.")]
    public float scanInterval = 5.0f;

    [Tooltip("Maximum number of scan passes before giving up.\n" +
             "Total max wait = scanInterval × (maxScanAttempts - 1)  e.g. 5 s × 5 = 25 s.")]
    public int maxScanAttempts = 6;

    [Header("Timing")]
    [Tooltip("Seconds to wait for MRUK room before giving up.")]
    public float sceneLoadTimeout = 8f;
    public float pollInterval = 0.4f;

    // ── Private ───────────────────────────────────────────────────

    private EnvironmentRaycastManager _envRaycast;
    private readonly List<GameObject> _activeMarkers = new List<GameObject>();

    private void Awake()
    {
        _envRaycast = FindAnyObjectByType<EnvironmentRaycastManager>();
        if (_envRaycast == null)
            Debug.LogWarning("[ChairMeshDetector] EnvironmentRaycastManager not found — " +
                             "grid scan (level 2) will be skipped.");
    }

    // ── Public API ────────────────────────────────────────────────

    /// <param name="onComplete">Called with the final list of chair world positions.</param>
    /// <param name="onStatus">Optional — called each scan pass with a human-readable status
    ///                        string, e.g. to update a scanning UI label.</param>
    /// <param name="onChairFound">Optional — called IMMEDIATELY each time a new unique chair
    ///                            is added to the accumulator, so callers can spawn a student
    ///                            on that chair right away without waiting for all passes.</param>
    public void DetectChairs(Action<List<Vector3>> onComplete,
                             Action<string>        onStatus    = null,
                             Action<Vector3>       onChairFound = null)
    {
        StopAllCoroutines();
        StartCoroutine(DetectCoroutine(onComplete, onStatus, onChairFound));
    }

    // ── Main coroutine ────────────────────────────────────────────

    private IEnumerator DetectCoroutine(Action<List<Vector3>> onComplete,
                                        Action<string>       onStatus,
                                        Action<Vector3>      onChairFound)
    {
        // ── Wait for MRUK room ───────────────────────────────────
        float elapsed = 0f;
        while (elapsed < sceneLoadTimeout)
        {
            if (MRUK.Instance != null && MRUK.Instance.GetCurrentRoom() != null) break;
            yield return new WaitForSeconds(pollInterval);
            elapsed += pollInterval;
            Debug.Log($"[ChairMeshDetector] Waiting for MRUK room… {elapsed:F1}s");
        }

        MRUKRoom room = MRUK.Instance?.GetCurrentRoom();
        if (room == null)
        {
            Debug.LogWarning("[ChairMeshDetector] MRUK room unavailable — returning empty.");
            onComplete?.Invoke(new List<Vector3>());
            yield break;
        }

        float floorY   = GetFloorY(room);
        float seatYMin = floorY + seatMinAboveFloor;
        float seatYMax = floorY + seatMaxAboveFloor;
        Debug.Log($"[ChairMeshDetector] Floor={floorY:F3} m  seat window [{seatYMin:F2}, {seatYMax:F2}] m");

        // ── Level 1: MRUK anchor labels (COUCH / OTHER only) ─────
        List<Vector3> anchorChairs = CollectFromAnchors(room, seatYMin, seatYMax);
        Debug.Log($"[ChairMeshDetector] Level 1 (anchors) → {anchorChairs.Count} labelled seat(s).");

        // ── Level 2: multi-pass grid scan ─────────────────────────
        // Each pass fires a fresh grid of depth rays.  Results are merged
        // into a running accumulator; a chair found in any earlier pass is
        // not double-counted in later passes (dedup by chairDiameter).
        // Scanning stops as soon as targetChairCount chairs are accumulated,
        // or after maxScanAttempts passes, whichever comes first.

        var accumulated = new List<Vector3>();
        int passes = Mathf.Max(1, maxScanAttempts);
        int target = targetChairCount > 0 ? targetChairCount : int.MaxValue;

        for (int attempt = 1; attempt <= passes; attempt++)
        {
            string statusMsg = accumulated.Count >= target
                ? $"Found {accumulated.Count} chair(s) ✓"
                : $"Scanning… found {accumulated.Count}/{target} chair(s)  (pass {attempt}/{passes})";
            onStatus?.Invoke(statusMsg);
            Debug.Log($"[ChairMeshDetector] --- Pass {attempt}/{passes} ---");

            yield return null;   // one frame before raycasting
            List<Vector3> scanResult = GridScan(seatYMin, seatYMax);

            // Merge new cluster centres into accumulator — skip duplicates
            int added = 0;
            foreach (var pos in scanResult)
            {
                bool dup = false;
                foreach (var existing in accumulated)
                {
                    float dx = pos.x - existing.x, dz = pos.z - existing.z;
                    if (dx * dx + dz * dz < chairDiameter * chairDiameter)
                    { dup = true; break; }
                }
                if (!dup)
                {
                    accumulated.Add(pos);
                    added++;
                    onChairFound?.Invoke(pos);   // ← student can sit down immediately
                }
            }

            Debug.Log($"[ChairMeshDetector] Pass {attempt}: +{added} new → " +
                      $"{accumulated.Count} total chair(s) accumulated.");

            if (accumulated.Count >= target)
            {
                Debug.Log($"[ChairMeshDetector] Target {target} reached after {attempt} pass(es).");
                break;
            }

            if (attempt < passes)
            {
                onStatus?.Invoke(
                    $"Found {accumulated.Count}/{target} chair(s) — " +
                    $"rescanning in {scanInterval:F0} s…  (look around to help)");
                yield return new WaitForSeconds(scanInterval);
            }
        }

        Debug.Log($"[ChairMeshDetector] Level 2 (multi-scan) → {accumulated.Count} physical seat(s).");

        // ── Merge L1 + L2, de-duplicate within clusterRadius ─────
        List<Vector3> merged = MergeResults(anchorChairs, accumulated);

        if (merged.Count > 0)
        {
            Debug.Log($"[ChairMeshDetector] ══════════════════════════════");
            Debug.Log($"[ChairMeshDetector]  DETECTED: {merged.Count} CHAIR(S)");
            Debug.Log($"[ChairMeshDetector] ══════════════════════════════");
            SpawnChairMarkers(merged);
            onComplete?.Invoke(merged);
            yield break;
        }

        // ── Level 3: MRUK GlobalMesh triangles (optional) ────────
        List<Vector3> meshChairs = CollectFromSceneMesh(room, seatYMin, seatYMax);
        if (meshChairs.Count > 0)
        {
            Debug.Log($"[ChairMeshDetector] ══════════════════════════════");
            Debug.Log($"[ChairMeshDetector]  DETECTED: {meshChairs.Count} CHAIR(S) (mesh)");
            Debug.Log($"[ChairMeshDetector] ══════════════════════════════");
            SpawnChairMarkers(meshChairs);
            onComplete?.Invoke(meshChairs);
            yield break;
        }

        Debug.LogWarning("[ChairMeshDetector] ✗ 0 chairs detected — falling back to grid spawn.");
        onComplete?.Invoke(new List<Vector3>());
    }

    // ── Level 1: Anchor labels ────────────────────────────────────

    private List<Vector3> CollectFromAnchors(MRUKRoom room, float seatYMin, float seatYMax)
    {
        var result = new List<Vector3>();
        // TABLE deliberately excluded — it marks desks, not chair seats.
        string[] labels = { "COUCH", "OTHER" };

        foreach (string label in labels)
        {
            foreach (MRUKAnchor anchor in room.Anchors)
            {
                if (!anchor.HasLabel(label)) continue;
                Vector3 pos = anchor.transform.position;
                if (pos.y < seatYMin || pos.y > seatYMax)
                {
                    Debug.Log($"[ChairMeshDetector] L1 skip '{anchor.gameObject.name}' " +
                              $"[{label}] Y={pos.y:F2} (outside [{seatYMin:F2},{seatYMax:F2}])");
                    continue;
                }
                result.Add(pos);
                Debug.Log($"[ChairMeshDetector] L1 ✓ '{anchor.gameObject.name}' [{label}] at {pos}");
                if (result.Count >= maxChairs) break;
            }
            if (result.Count > 0) break;
        }
        return result;
    }

    // ── Level 2: EnvironmentRaycastManager grid scan ──────────────
    //
    // Fires a dense grid of downward rays slightly above seatYMax and
    // keeps hits that are horizontal and within the seat height window.
    // Handles chairs of any label (or no label) at any spacing.

    private List<Vector3> GridScan(float seatYMin, float seatYMax)
    {
        if (_envRaycast == null)
        {
            Debug.LogWarning("[ChairMeshDetector] L2 skipped — no EnvironmentRaycastManager.");
            return new List<Vector3>();
        }

        Transform cam       = Camera.main?.transform;
        if (cam == null) return new List<Vector3>();

        // Flat (horizontal) axes in the direction the user faces
        Vector3 fwd   = new Vector3(cam.forward.x, 0f, cam.forward.z).normalized;
        Vector3 right = new Vector3(cam.right.x,   0f, cam.right.z  ).normalized;
        Vector3 camXZ = new Vector3(cam.position.x, 0f, cam.position.z);

        float castFromY  = seatYMax + 0.20f;   // start just above the highest valid seat
        float castDistY  = (seatYMax - seatYMin) + 0.40f;

        var rawHits = new List<Vector3>();
        int totalRays = 0;

        for (float f = gridStartForward; f <= gridEndForward; f += gridStep)
        {
            for (float s = -gridHalfWidth; s <= gridHalfWidth; s += gridStep)
            {
                Vector3 xzPos  = camXZ + fwd * f + right * s;
                Vector3 origin = new Vector3(xzPos.x, castFromY, xzPos.z);

                totalRays++;
                if (!_envRaycast.Raycast(new Ray(origin, Vector3.down), out var hit, castDistY))
                    continue;

                if (Vector3.Dot(hit.normal, Vector3.up) < minUpwardDot) continue;
                if (hit.point.y < seatYMin || hit.point.y > seatYMax)   continue;

                rawHits.Add(hit.point);
            }
        }

        Debug.Log($"[ChairMeshDetector] L2 grid: {totalRays} rays → {rawHits.Count} seat hits.");

        if (rawHits.Count == 0) return new List<Vector3>();

        var clusters = ClusterPoints(rawHits, minHitsPerCluster);

        // ── Merge split detections (two halves of the same seat) ─────
        if (clusters.Count > 1)
        {
            int before = clusters.Count;
            clusters = MergeNearbyClusters(clusters);
            if (clusters.Count < before)
                Debug.Log($"[ChairMeshDetector] Split-seat merge: {before} clusters → {clusters.Count}.");
        }

        // ── Filter: remove clusters too close to the user ─────────
        var filtered = new List<Vector3>();
        Vector3 userXZ = new Vector3(cam.position.x, 0f, cam.position.z);
        foreach (var c in clusters)
        {
            Vector3 cXZ = new Vector3(c.x, 0f, c.z);
            float dist = Vector3.Distance(userXZ, cXZ);
            if (dist < minDistanceFromUser)
            {
                Debug.Log($"[ChairMeshDetector]   Rejected cluster at {c} — " +
                          $"only {dist:F2} m from user (min {minDistanceFromUser} m).");
                continue;
            }
            Debug.Log($"[ChairMeshDetector]   ✓ Chair cluster at XZ=({c.x:F2}, {c.z:F2})  " +
                      $"Y={c.y:F2}  dist={dist:F2} m from user.");
            filtered.Add(c);
        }

        Debug.Log($"[ChairMeshDetector] L2 after distance filter: {filtered.Count} chair(s).");
        return filtered;
    }

    // ── Level 3: MRUK GlobalMesh ──────────────────────────────────

    private List<Vector3> CollectFromSceneMesh(MRUKRoom room, float seatYMin, float seatYMax)
    {
        MRUKAnchor meshAnchor = null;
        try { meshAnchor = room.GlobalMeshAnchor; }
        catch (Exception ex)
        { Debug.LogWarning($"[ChairMeshDetector] L3 GlobalMeshAnchor error: {ex.Message}"); }

        if (meshAnchor == null)
        {
            Debug.LogWarning("[ChairMeshDetector] L3: No GlobalMeshAnchor — " +
                             "assign a GlobalMesh prefab in the MRUK component to enable this path.");
            return new List<Vector3>();
        }

        MeshFilter mf = meshAnchor.GetComponent<MeshFilter>();
        if (mf == null || mf.sharedMesh == null)
        {
            Debug.LogWarning("[ChairMeshDetector] L3: GlobalMeshAnchor has no MeshFilter. " +
                             "In the MRUK Inspector assign a prefab (with MeshFilter) to the " +
                             "Global Mesh Prefab field to enable this path.");
            return new List<Vector3>();
        }

        return ScanMesh(mf.sharedMesh, meshAnchor.transform, seatYMin, seatYMax);
    }

    private List<Vector3> ScanMesh(Mesh mesh, Transform tf, float seatYMin, float seatYMax)
    {
        Vector3[] verts = mesh.vertices;
        int[]     tris  = mesh.triangles;
        var       pts   = new List<Vector3>();

        for (int i = 0; i < tris.Length; i += 3)
        {
            Vector3 v0 = tf.TransformPoint(verts[tris[i    ]]);
            Vector3 v1 = tf.TransformPoint(verts[tris[i + 1]]);
            Vector3 v2 = tf.TransformPoint(verts[tris[i + 2]]);

            Vector3 n = Vector3.Cross(v1 - v0, v2 - v0);
            if (n.sqrMagnitude < 1e-8f) continue;
            if (Vector3.Dot(n.normalized, Vector3.up) < minUpwardDot) continue;

            Vector3 c = (v0 + v1 + v2) * 0.3333f;
            if (c.y < seatYMin || c.y > seatYMax) continue;
            pts.Add(c);
        }

        Debug.Log($"[ChairMeshDetector] L3 mesh: {tris.Length / 3} tris → {pts.Count} seat points.");
        return pts.Count == 0 ? new List<Vector3>() : ClusterPoints(pts, 1);
    }

    // ── AR debug markers ──────────────────────────────────────────

    private void SpawnChairMarkers(List<Vector3> chairs)
    {
        if (!showDebugMarkers) return;

        // Destroy any previous markers
        foreach (var old in _activeMarkers)
            if (old != null) Destroy(old);
        _activeMarkers.Clear();

        for (int i = 0; i < chairs.Count; i++)
        {
            Vector3 seatPos = chairs[i];

            // ── Root ──────────────────────────────────────────────
            var root = new GameObject($"ChairMarker_{i + 1}");
            root.transform.position = seatPos;
            _activeMarkers.Add(root);

            // ── Shared property block (avoids runtime shader lookup) ──
            var mpb = new MaterialPropertyBlock();
            mpb.SetColor("_BaseColor", markerColor);   // URP lit/unlit
            mpb.SetColor("_Color",     markerColor);   // Built-in fallback

            // ── Flat disc at seat surface ─────────────────────────
            var disc = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            disc.name = "Disc";
            disc.transform.SetParent(root.transform, false);
            disc.transform.localPosition = new Vector3(0f, 0.01f, 0f);
            disc.transform.localScale    = new Vector3(0.35f, 0.01f, 0.35f);
            Destroy(disc.GetComponent<Collider>());
            disc.GetComponent<Renderer>().SetPropertyBlock(mpb);

            // ── Thin vertical pole ────────────────────────────────
            var pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pole.name = "Pole";
            pole.transform.SetParent(root.transform, false);
            pole.transform.localPosition = new Vector3(0f, 0.25f, 0f);
            pole.transform.localScale    = new Vector3(0.015f, 0.25f, 0.015f);
            Destroy(pole.GetComponent<Collider>());
            pole.GetComponent<Renderer>().SetPropertyBlock(mpb);

            // ── Floating "Chair N" label ──────────────────────────
            var labelGO = new GameObject("Label");
            labelGO.transform.SetParent(root.transform, false);
            labelGO.transform.localPosition = new Vector3(0f, 0.60f, 0f);
            labelGO.transform.localScale    = Vector3.one * 0.004f;

            var tmp = labelGO.AddComponent<TextMeshPro>();
            tmp.text      = $"Chair {i + 1}";
            tmp.fontSize  = 48f;
            tmp.color     = Color.white;
            tmp.outlineWidth = 0.25f;
            tmp.outlineColor = new Color32(0, 0, 0, 200);
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.enableWordWrapping = false;

            labelGO.AddComponent<ChairLabelBillboard>();

            // ── Auto-destroy ──────────────────────────────────────
            if (markerDuration > 0f)
                Destroy(root, markerDuration);

            Debug.Log($"[ChairMeshDetector]   Marker {i + 1}: " +
                      $"XZ=({seatPos.x:F2}, {seatPos.z:F2})  Y={seatPos.y:F2}");
        }
    }

    // ── Merge two chair lists, removing duplicates ────────────────
    // Grid results are kept as base; anchor results are added only if
    // they are farther than clusterRadius from every existing point.

    private List<Vector3> MergeResults(List<Vector3> anchors, List<Vector3> grid)
    {
        var merged = new List<Vector3>(grid);

        foreach (Vector3 a in anchors)
        {
            bool duplicate = false;
            foreach (Vector3 g in merged)
            {
                float dx = a.x - g.x, dz = a.z - g.z;
                if (dx * dx + dz * dz < clusterRadius * clusterRadius)
                { duplicate = true; break; }
            }
            if (!duplicate) merged.Add(a);
        }

        return merged;
    }

    // ── Post-cluster merge: join centres closer than chairDiameter ──
    // Runs on cluster CENTRES (not raw points), so it only fires when the
    // greedy scan split one physical seat into two nearby clusters.

    private List<Vector3> MergeNearbyClusters(List<Vector3> clusters)
    {
        var result = new List<Vector3>(clusters);
        float r2 = chairDiameter * chairDiameter;
        bool anyMerged = true;

        while (anyMerged)
        {
            anyMerged = false;
            for (int i = 0; i < result.Count && !anyMerged; i++)
            {
                for (int j = i + 1; j < result.Count; j++)
                {
                    float dx = result[i].x - result[j].x;
                    float dz = result[i].z - result[j].z;
                    if (dx * dx + dz * dz < r2)
                    {
                        Vector3 avg = (result[i] + result[j]) * 0.5f;
                        Debug.Log($"[ChairMeshDetector] Merge ({result[i].x:F2},{result[i].z:F2}) + " +
                                  $"({result[j].x:F2},{result[j].z:F2}) → ({avg.x:F2},{avg.z:F2})");
                        result[i] = avg;
                        result.RemoveAt(j);
                        anyMerged = true;
                        break;
                    }
                }
            }
        }
        return result;
    }

    // ── Greedy XZ clustering ──────────────────────────────────────

    private List<Vector3> ClusterPoints(List<Vector3> points, int minHits)
    {
        var sums   = new List<Vector3>();
        var counts = new List<int>();

        foreach (Vector3 pt in points)
        {
            int   best  = -1;
            float bestD = float.MaxValue;

            for (int i = 0; i < sums.Count; i++)
            {
                Vector3 cc = sums[i] / counts[i];
                float dx = cc.x - pt.x, dz = cc.z - pt.z;
                float d2 = dx * dx + dz * dz;
                if (d2 < bestD) { bestD = d2; best = i; }
            }

            if (best >= 0 && bestD < clusterRadius * clusterRadius)
            { sums[best] += pt; counts[best]++; }
            else
            { sums.Add(pt); counts.Add(1); }
        }

        // Sort by hit count descending (most-hit cluster = most-solid chair)
        var idx = new List<int>();
        for (int i = 0; i < sums.Count; i++) idx.Add(i);
        idx.Sort((a, b) => counts[b].CompareTo(counts[a]));

        var result = new List<Vector3>();
        foreach (int i in idx)
        {
            if (counts[i] < minHits)
            {
                Debug.Log($"[ChairMeshDetector] Cluster {i} rejected — " +
                          $"only {counts[i]} hit(s) < minHitsPerCluster ({minHits}).");
                continue;
            }
            result.Add(sums[i] / counts[i]);
            if (result.Count >= maxChairs) break;
        }

        Debug.Log($"[ChairMeshDetector] Clustering: {points.Count} pts → " +
                  $"{sums.Count} raw clusters → {result.Count} kept (minHits={minHits}).");
        return result;
    }

    // ── Floor Y ───────────────────────────────────────────────────

    private float GetFloorY(MRUKRoom room)
    {
        foreach (MRUKAnchor a in room.Anchors)
        {
            if (a.HasLabel("FLOOR"))
            {
                Debug.Log($"[ChairMeshDetector] Floor Y from MRUK FLOOR anchor: {a.transform.position.y:F3}");
                return a.transform.position.y;
            }
        }
        float fb = Camera.main != null ? Camera.main.transform.position.y - 1.7f : 0f;
        Debug.LogWarning($"[ChairMeshDetector] No FLOOR anchor — fallback floor Y: {fb:F3}");
        return fb;
    }
}
