using System;
using System.Collections.Generic;
using UnityEngine;
using Meta.XR.MRUtilityKit;

/// <summary>
/// Spawns and manages 1918-era virtual props in the lecture hall AR scene.
/// Attach this script to the same GameObject as LectureHallManager.
/// Call SpawnProps() after the doctor spawns, ClearProps() when scene resets.
/// </summary>
public class LectureHallProps : MonoBehaviour
{
    [Header("Prop Prefabs")]
    [Tooltip("Wooden podium/lectern placed in front of the doctor.")]
    public GameObject podiumPrefab;

    [Tooltip("Chalkboard placed behind the doctor (front-wall side).")]
    public GameObject chalkboardPrefab;

    [Tooltip("Chandelier or gas lamp hung above the centre of the student area.")]
    public GameObject chandelierPrefab;

    [Tooltip("Framed portrait placed on wall anchors. Up to 2 will be spawned.")]
    public GameObject portraitPrefab;

    [Tooltip("Antique wall clock placed on the wall closest to the doctor.")]
    public GameObject clockPrefab;

    [Header("Doctor-Relative Placement")]
    [Tooltip("How far in front of the doctor the podium is placed (metres).")]
    public float podiumForwardOffset = 0.6f;

    [Tooltip("Y offset of the podium relative to floor level.")]
    public float podiumYOffset = 0f;

    [Tooltip("How far behind the doctor the chalkboard is placed (metres).")]
    public float chalkboardBehindOffset = 0.5f;

    [Tooltip("How far to the doctor's RIGHT the chalkboard is placed (metres).\n" +
             "Negative = left side. Use this to put it beside the doctor instead of behind.")]
    public float chalkboardSideOffset = 0f;

    [Tooltip("Y offset of the chalkboard base relative to floor level.")]
    public float chalkboardYOffset = 0f;

    [Tooltip("Extra Y-axis rotation applied to the chalkboard (degrees).\n" +
             "Use this to fix a diagonal board — try 45, -45, 90, or -90\n" +
             "until the face of the board points squarely toward the students.")]
    public float chalkboardYRotationOffset = 0f;

    [Header("Chandelier")]
    [Tooltip("Height above the floor at which the chandelier hangs (metres). " +
             "Typical ceiling is ~2.4 m; 2.2 puts it just below most ceilings.")]
    public float chandelierHeight = 2.2f;

    [Tooltip("Extra rotation applied to the chandelier after placement (Euler angles).\n" +
             "If the chandelier appears horizontal, try X = 90 or X = -90.\n" +
             "If it's upside-down, try X = 180.")]
    public Vector3 chandelierRotationOffset = Vector3.zero;

    [Tooltip("Uniform scale applied to the chandelier. 1 = model's original size.")]
    public float chandelierScale = 1f;

    [Header("Prop Scales")]
    [Tooltip("Uniform scale for the chalkboard. Decrease if it appears too large (e.g. 0.3).")]
    public float chalkboardScale = 1f;

    [Tooltip("Uniform scale for the podium.")]
    public float podiumScale = 1f;

    [Tooltip("Uniform scale for wall portraits.")]
    public float portraitScale = 1f;

    [Tooltip("Uniform scale for the wall clock.")]
    public float clockScale = 1f;

    [Header("Wall Props (Portraits & Clock)")]
    [Tooltip("How far off the wall surface the portrait/clock sits (metres). " +
             "Prevents z-fighting with the physical wall.")]
    public float wallOffsetDistance = 0.05f;

    [Tooltip("Y height of portrait centres above the floor (metres).")]
    public float portraitHeight = 1.6f;

    [Tooltip("Y height of the clock centre above the floor (metres).")]
    public float clockHeight = 1.8f;

    [Tooltip("Maximum number of portraits to place on wall anchors.")]
    public int maxPortraits = 2;

    // ── runtime ──────────────────────────────────────────────────
    private readonly List<GameObject> _spawnedProps = new List<GameObject>();

    // ── Public API ───────────────────────────────────────────────

    /// <summary>
    /// Spawns all props.
    /// <param name="doctorPos">World position of the doctor.</param>
    /// <param name="doctorRot">World rotation of the doctor (facing students).</param>
    /// <param name="chairCentroid">Centre of the student chair area (for chandelier).</param>
    /// <param name="floorY">Floor Y so props sit on the ground.</param>
    /// </summary>
    public void SpawnProps(Vector3 doctorPos, Quaternion doctorRot,
                           Vector3 chairCentroid, float floorY)
    {
        SpawnDoctorRelativeProps(doctorPos, doctorRot, floorY);
        SpawnChandelier(chairCentroid, floorY);
        SpawnWallProps(doctorPos, floorY);

        Debug.Log($"[LectureHallProps] Spawned {_spawnedProps.Count} prop(s).");
    }

    /// <summary>Destroys all spawned props. Call from LectureHallManager.ClearScene().</summary>
    public void ClearProps()
    {
        foreach (var p in _spawnedProps)
            if (p != null) Destroy(p);
        _spawnedProps.Clear();
        Debug.Log("[LectureHallProps] Props cleared.");
    }

    // ── Private helpers ──────────────────────────────────────────

    private void SpawnDoctorRelativeProps(Vector3 doctorPos, Quaternion doctorRot, float floorY)
    {
        // Doctor's facing direction (toward students)
        Vector3 forward = doctorRot * Vector3.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude > 0.001f) forward.Normalize();

        // ── Podium: in front of the doctor, facing doctor ─────────
        if (podiumPrefab != null)
        {
            Vector3 pos = doctorPos + forward * podiumForwardOffset;
            pos.y = floorY + podiumYOffset;

            // Podium faces the doctor (rotated 180° from doctor's forward)
            Quaternion rot = Quaternion.LookRotation(-forward);
            var obj = Instantiate(podiumPrefab, pos, rot);
            obj.name = "Prop_Podium";
            if (podiumScale != 1f)
                obj.transform.localScale = Vector3.one * podiumScale;
            _spawnedProps.Add(obj);
            Debug.Log($"[LectureHallProps] Podium placed at {pos} scale={podiumScale}.");
        }

        // ── Chalkboard: beside/behind the doctor, facing students ──
        if (chalkboardPrefab != null)
        {
            // right = doctor's local right axis (flat)
            Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;

            Vector3 pos = doctorPos
                        - forward * chalkboardBehindOffset
                        + right   * chalkboardSideOffset;
            pos.y = floorY + chalkboardYOffset;

            // Face toward students, then apply the user-tunable Y rotation offset
            // so a diagonal board can be snapped to the correct wall orientation.
            Quaternion rot = Quaternion.LookRotation(forward)
                           * Quaternion.Euler(0f, chalkboardYRotationOffset, 0f);

            var obj = Instantiate(chalkboardPrefab, pos, rot);
            obj.name = "Prop_Chalkboard";
            if (chalkboardScale != 1f)
                obj.transform.localScale = Vector3.one * chalkboardScale;
            _spawnedProps.Add(obj);
            Debug.Log($"[LectureHallProps] Chalkboard placed at {pos} " +
                      $"sideOffset={chalkboardSideOffset} yRot={chalkboardYRotationOffset} " +
                      $"scale={chalkboardScale}.");
        }
    }

    private void SpawnChandelier(Vector3 chairCentroid, float floorY)
    {
        if (chandelierPrefab == null) return;

        Vector3 pos = new Vector3(chairCentroid.x, floorY + chandelierHeight, chairCentroid.z);

        // Apply the rotation offset on top of Quaternion.identity so the model's
        // "hanging" axis points downward regardless of how it was exported.
        Quaternion rot = Quaternion.Euler(chandelierRotationOffset);

        var obj = Instantiate(chandelierPrefab, pos, rot);
        obj.name = "Prop_Chandelier";
        if (chandelierScale != 1f)
            obj.transform.localScale = Vector3.one * chandelierScale;
        _spawnedProps.Add(obj);
        Debug.Log($"[LectureHallProps] Chandelier placed at {pos} " +
                  $"rotOffset={chandelierRotationOffset} scale={chandelierScale}.");
    }

    private void SpawnWallProps(Vector3 doctorPos, float floorY)
    {
        if (MRUK.Instance == null)
        {
            Debug.LogWarning("[LectureHallProps] MRUK not available — wall props skipped.");
            return;
        }

        MRUKRoom room = MRUK.Instance.GetCurrentRoom();
        if (room == null) return;

        // Collect all wall anchors
        var wallAnchors = new List<MRUKAnchor>();
        foreach (MRUKAnchor anchor in room.Anchors)
        {
            if (anchor.HasLabel("WALL_FACE") || anchor.HasLabel("WALL_ART"))
                wallAnchors.Add(anchor);
        }

        if (wallAnchors.Count == 0)
        {
            Debug.LogWarning("[LectureHallProps] No wall anchors found — portraits & clock skipped.");
            return;
        }

        // Sort walls by distance from doctor (closest first)
        wallAnchors.Sort((a, b) =>
        {
            float da = Vector3.Distance(a.transform.position, doctorPos);
            float db = Vector3.Distance(b.transform.position, doctorPos);
            return da.CompareTo(db);
        });

        // ── Clock: on the wall closest to the doctor ──────────────
        if (clockPrefab != null && wallAnchors.Count > 0)
        {
            PlaceOnWall(clockPrefab, wallAnchors[0], floorY + clockHeight, "Prop_Clock", clockScale);
            // Remove this anchor so portraits don't overlap with the clock
            wallAnchors.RemoveAt(0);
        }

        // ── Portraits: next available walls ───────────────────────
        if (portraitPrefab != null)
        {
            int placed = 0;
            foreach (var anchor in wallAnchors)
            {
                if (placed >= maxPortraits) break;
                PlaceOnWall(portraitPrefab, anchor, floorY + portraitHeight,
                            $"Prop_Portrait_{placed + 1}", portraitScale);
                placed++;
            }
        }
    }

    /// <summary>
    /// Places a prop flat against a MRUK wall anchor.
    /// The prop faces into the room (away from the wall surface).
    /// </summary>
    private void PlaceOnWall(GameObject prefab, MRUKAnchor anchor, float worldY,
                             string propName, float scale = 1f)
    {
        // MRUK wall anchor: transform.forward points INTO the room (away from wall surface)
        Vector3 wallNormal = anchor.transform.forward;
        wallNormal.y = 0f;
        if (wallNormal.sqrMagnitude < 0.001f) return;
        wallNormal.Normalize();

        // Position: anchor XZ, chosen Y, offset slightly away from wall to avoid z-fighting
        Vector3 pos = new Vector3(
            anchor.transform.position.x,
            worldY,
            anchor.transform.position.z);
        pos += wallNormal * wallOffsetDistance;

        // Rotation: prop faces into the room (same as wall normal)
        Quaternion rot = Quaternion.LookRotation(wallNormal);

        var obj = Instantiate(prefab, pos, rot);
        obj.name = propName;
        if (scale != 1f)
            obj.transform.localScale = Vector3.one * scale;
        _spawnedProps.Add(obj);
        Debug.Log($"[LectureHallProps] {propName} placed at {pos} facing {wallNormal} scale={scale}.");
    }
}
