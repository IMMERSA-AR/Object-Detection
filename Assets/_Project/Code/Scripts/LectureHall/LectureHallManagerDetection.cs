using System;
using System.Collections.Generic;
using UnityEngine;
using Meta.XR;
using Meta.XR.MRUtilityKit;

public partial class LectureHallManager
{
    private Vector3 ComputeSceneAnchor(Vector3 forward)
    {
        Transform cam = Camera.main.transform;
        Vector3 xz = cam.position + forward * sceneDistance;
        float y = FindFloorY(xz, cam.position.y);
        return new Vector3(xz.x, y, xz.z);
    }

    private Vector3 GetPlayerFlatForward()
    {
        if (Camera.main == null) return Vector3.forward;
        Vector3 fwd = Camera.main.transform.forward;
        fwd.y = 0f;
        return fwd.sqrMagnitude > 0.001f ? fwd.normalized : Vector3.forward;
    }

    private Vector3 EstimateChairForward(Vector3 chairPos)
    {
        Vector3 fallback;
        if (Camera.main != null)
        {
            Vector3 camXZ = new Vector3(Camera.main.transform.position.x, 0f, Camera.main.transform.position.z);
            Vector3 chairXZ = new Vector3(chairPos.x, 0f, chairPos.z);
            Vector3 awayFromUser = chairXZ - camXZ;
            fallback = awayFromUser.sqrMagnitude > 0.0001f ? awayFromUser.normalized : Vector3.forward;
        }
        else fallback = Vector3.forward;
        try
        {
            if (MRUK.Instance != null)
            {
                MRUKRoom room = MRUK.Instance.GetCurrentRoom();
                if (room != null)
                {
                    MRUKAnchor best = null;
                    float bestDist = chairAnchorMatchRadius;
                    foreach (MRUKAnchor anchor in room.Anchors)
                    {
                        if (!anchor.HasLabel("COUCH") && !anchor.HasLabel("OTHER"))
                            continue;
                        Vector3 ap = anchor.transform.position;
                        float dx = ap.x - chairPos.x, dz = ap.z - chairPos.z;
                        float d = Mathf.Sqrt(dx * dx + dz * dz);
                        if (d < bestDist)
                        {
                            bestDist = d;
                            best = anchor;
                        }
                    }
                    if (best != null)
                    {
                        Vector3 fwd = best.transform.forward;
                        fwd.y = 0f;
                        if (fwd.sqrMagnitude > 0.0001f)
                        {
                            Vector3 r = fwd.normalized;
                            if (flipChairForward) r = -r;
                            Debug.Log($"Lecture Hall:  Chair found");
                            return r;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Lecture Hall: Error: {ex.Message}");
        }

        if (_envRaycast != null)
        {
            float camY = Camera.main != null ? Camera.main.transform.position.y : 1.7f;
            float floorY = FindFloorY(chairPos, camY);
            float probeY = floorY + chairBackrestProbeHeight;
            Vector3 origin = new Vector3(chairPos.x, probeY, chairPos.z);

            const int numRays = 16;
            const float searchRadius = 0.40f;
            float closest = searchRadius;
            Vector3 backDir = Vector3.zero;

            for (int i = 0; i < numRays; i++)
            {
                float angle = (i / (float)numRays) * 2f * Mathf.PI;
                Vector3 dir = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));

                if (_envRaycast.Raycast(new Ray(origin, dir), out var hit, searchRadius))
                {
                    float dist = Vector3.Distance(origin, hit.point);
                    if (Mathf.Abs(hit.normal.y) < 0.4f && dist < closest)
                    {
                        closest = dist;
                        backDir = dir;
                    }
                }
            }

            if (backDir != Vector3.zero)
            {
                Vector3 r = (-backDir).normalized;
                if (flipChairForward)
                    r = -r;
                return r;
            }
        }

        Vector3 fb = flipChairForward ? -fallback : fallback;
        return fb;
    }

    private List<Vector3> FilterChairsInFront(List<Vector3> chairs, Vector3 camPos, Vector3 forward)
    {
        var result = new List<Vector3>();
        foreach (var pos in chairs)
        {
            Vector3 toChair = pos - camPos;
            toChair.y = 0f;
            if (Vector3.Dot(toChair.normalized, forward) > -0.5f)
                result.Add(pos);
        }
        return result;
    }

    private Vector3? FindScreenFromMRUK()
    {
        try
        {
            if (MRUK.Instance == null)
                return null;
            MRUKRoom room = MRUK.Instance.GetCurrentRoom();
            if (room == null)
                return null;

            string[] labels = { "SCREEN", "WALL_ART" };

            foreach (string label in labels)
            {
                foreach (MRUKAnchor anchor in room.Anchors)
                {
                    if (!anchor.HasLabel(label))
                        continue;

                    Vector3 pos = anchor.transform.position;
                    Debug.Log($"Lecture Hall: Screen anchor found");
                    return pos;
                }
            }

            Debug.Log("Lecture Hall: No screen or wall art anchor found");
            return null;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Lecture Hall: Error: {ex.Message}");
            return null;
        }
    }

    private float GetRoomFloorY()
    {
        try
        {
            if (MRUK.Instance != null)
            {
                MRUKRoom room = MRUK.Instance.GetCurrentRoom();
                if (room != null)
                    foreach (MRUKAnchor anchor in room.Anchors)
                        if (anchor.HasLabel("FLOOR"))
                            return anchor.transform.position.y;
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Lecture Hall: Error: {ex.Message}");
        }
        return Camera.main != null ? Camera.main.transform.position.y - 1.7f : 0f;
    }


    private List<Vector3> ClusterSurfaces(List<Vector3> points, float radius, int minHits)
    {
        var sums = new List<Vector3>();
        var counts = new List<int>();
        float r2 = radius * radius;

        foreach (var pt in points)
        {
            int best = -1;
            float bestD = float.MaxValue;
            for (int i = 0; i < sums.Count; i++)
            {
                Vector3 cc = sums[i] / counts[i];
                float dx = cc.x - pt.x, dz = cc.z - pt.z;
                float d2 = dx * dx + dz * dz;
                if (d2 < bestD) { bestD = d2; best = i; }
            }
            if (best >= 0 && bestD < r2)
            {
                sums[best] += pt;
                counts[best]++;
            }
            else { sums.Add(pt); counts.Add(1); }
        }

        var result = new List<Vector3>();
        for (int i = 0; i < sums.Count; i++)
            if (counts[i] >= minHits)
                result.Add(sums[i] / counts[i]);
        return result;
    }

    public List<Vector3> FindChairsByEnvironmentScan(float floorY)
    {
        var positions = new List<Vector3>();

        if (_envRaycast == null)
        {
            Debug.LogWarning("Lecture Hall: no EnvironmentRaycastManager");
            return positions;
        }

        Transform cam = Camera.main?.transform;
        if (cam == null) return positions;

        float yMin = floorY + chairMinHeight;
        float yMax = floorY + chairMaxHeight;
        float castFromY = yMax + 0.30f;
        float castDist = (yMax - yMin) + 0.50f;

        Vector3 fwd = new Vector3(cam.forward.x, 0f, cam.forward.z);
        if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
        fwd.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
        Vector3 camXZ = new Vector3(cam.position.x, 0f, cam.position.z);

        var rawHits = new List<Vector3>();

        for (float f = 0.3f; f <= chairScanForwardRange; f += chairScanGridStep)
        {
            for (float s = -chairScanSideRange; s <= chairScanSideRange; s += chairScanGridStep)
            {
                Vector3 xzPos = camXZ + fwd * f + right * s;
                Vector3 origin = new Vector3(xzPos.x, castFromY, xzPos.z);

                if (!_envRaycast.Raycast(new Ray(origin, Vector3.down), out var hit, castDist))
                    continue;

                // Reject non horizontal objects
                if (Vector3.Dot(hit.normal, Vector3.up) < 0.70f)
                    continue;

                // Reject objects that not the same height as chair
                if (hit.point.y < yMin || hit.point.y > yMax)
                    continue;

                rawHits.Add(hit.point);
            }
        }

        if (rawHits.Count == 0)
            return positions;

        var clusters = ClusterSurfaces(rawHits, chairScanClusterRadius, minHitsForChair);
        foreach (var c in clusters)
            positions.Add(c);

        return positions;
    }
    //Try to find disk by firing a grid of downward rays at disk height
    private Vector3? FindDeskByGridScan(Vector3 chairCentroid, Vector3 toChairsDir, float floorY)
    {
        if (_envRaycast == null)
        {
            Debug.LogWarning("LectureHall: no EnvironmentRaycastManager.");
            return null;
        }

        Transform cam = Camera.main?.transform;
        if (cam == null) return null;

        float yMin = floorY + deskMinHeight;
        float yMax = floorY + deskMaxHeight;
        float castFromY = yMax + 0.20f;
        float castDist = (yMax - yMin) + 0.40f;

        Vector3 fwd = new Vector3(cam.forward.x, 0f, cam.forward.z).normalized;
        Vector3 right = new Vector3(cam.right.x, 0f, cam.right.z).normalized;
        Vector3 camXZ = new Vector3(cam.position.x, 0f, cam.position.z);

        var rawHits = new List<Vector3>();

        for (float f = 0.5f; f <= 6.0f; f += deskGridStep)
        {
            for (float s = -3.0f; s <= 3.0f; s += deskGridStep)
            {
                Vector3 xzPos = camXZ + fwd * f + right * s;
                Vector3 origin = new Vector3(xzPos.x, castFromY, xzPos.z);

                if (!_envRaycast.Raycast(new Ray(origin, Vector3.down), out var hit, castDist))
                    continue;
                if (Vector3.Dot(hit.normal, Vector3.up) < 0.70f)
                    continue;
                if (hit.point.y < yMin || hit.point.y > yMax)
                    continue;

                rawHits.Add(hit.point);
            }
        }

        Debug.Log($"Lecture Hall: desk found.");

        if (rawHits.Count == 0)
            return null;

        var clusters = ClusterSurfaces(rawHits, deskClusterRadius, minHitsForDesk);
        if (clusters.Count == 0)
        {
            Debug.Log("Lecture Hall: no desk found");
            return null;
        }

        Vector3 centroidXZ = new Vector3(chairCentroid.x, 0f, chairCentroid.z);
        Vector3? best = null;
        float bestScore = float.MinValue;

        foreach (var c in clusters)
        {
            Vector3 cXZ = new Vector3(c.x, 0f, c.z);
            float score = Vector3.Dot(cXZ - centroidXZ, toChairsDir);
            if (score > bestScore) { bestScore = score; best = c; }
        }

        if (best.HasValue)
            Debug.Log($"Lecture Hall:  desk found by grid scan");
        else
            Debug.Log("Lecture Hall:  no desk found");

        return best;
    }
    //Reads MRUK anchors and try to find something that is labeled by Table
    private Vector3? FindDeskFromMRUK(Vector3 chairCentroid)
    {
        try
        {
            if (MRUK.Instance == null) return null;
            MRUKRoom room = MRUK.Instance.GetCurrentRoom();
            if (room == null) return null;

            Vector3? best = null;
            float bestDist = float.MaxValue;

            foreach (MRUKAnchor anchor in room.Anchors)
            {
                if (!anchor.HasLabel("TABLE")) continue;

                Vector3 pos = anchor.transform.position;
                float d = Vector2.Distance(new Vector2(pos.x, pos.z), new Vector2(chairCentroid.x, chairCentroid.z));

                if (d < bestDist)
                {
                    bestDist = d;
                    best = pos;
                }
            }

            if (best.HasValue)
                Debug.Log($"Lecture Hall:  desk found ");
            else
                Debug.Log("Lecture Hall:  no table anchor found so there is no detected desk");

            return best;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Lecture Hall:error: {ex.Message}");
            return null;
        }
    }
    //find floor height by MRUK,by searching for label floor
    private float FindFloorY(Vector3 xzPos, float cameraY)
    {
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
                            return anchor.transform.position.y;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Lecture Hall:error: {ex.Message}");
        }
        if (_envRaycast != null)
        {
            float estimatedFloor = cameraY - 1.7f;
            Vector3 origin = new Vector3(xzPos.x, estimatedFloor + 0.3f, xzPos.z);
            if (_envRaycast.Raycast(new Ray(origin, Vector3.down), out var hit, 0.8f) &&Vector3.Dot(hit.normal, Vector3.up) > 0.7f)
                return hit.point.y;
        }
        return cameraY - 1.7f;
    }
}
