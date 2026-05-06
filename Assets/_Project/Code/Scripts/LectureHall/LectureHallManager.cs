using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using Meta.XR;
using Meta.XR.MRUtilityKit;

/// <summary>
/// Spawns the 1918 lecture hall scene, plays the lecture audio,
/// then hands control to ExperienceManager for Murad's Q&A phase.
/// NPCs stay visible the entire time — even after Murad appears.
/// </summary>
public class LectureHallManager : MonoBehaviour
{
    [Header("Scene Layout")]
    [Header("Configuration")]
    public ExperienceConfig currentConfig;
    public float sceneDistance = 2.5f;
    public float seatSpacingX = 0.85f;
    public float seatSpacingZ = 0.9f;

    [Tooltip("How far to the user's right the doctor stands (metres). Negative = left side.\n" +
             "Only used by the GRID spawn path. Chair-based path uses doctorForwardOffset instead.")]
    public float doctorSideOffset = 1.2f;

    [Tooltip("Chair-based path: how far PAST the chair centroid the doctor stands (metres).\n" +
             "Used only when NO desk anchor is found via MRUK.\n" +
             "Doctor is placed in the direction user→chairs, beyond the chairs.")]
    public float doctorForwardOffset = 1.2f;

    [Tooltip("When a MRUK TABLE (desk) is found, how far behind the desk the doctor stands (metres).\n" +
             "'Behind' = the side of the desk that is farther from the students.")]
    public float doctorBehindDeskOffset = 0.5f;

    [Tooltip("When a SCREEN (whiteboard / projector screen) anchor is found, how far IN FRONT\n" +
             "of it the doctor stands (metres). 'In front' = toward the students.")]
    public float doctorInFrontOfScreenOffset = 0.8f;

    [Tooltip("Extra Y offset added to every student spawn position after the base Y is chosen.\n" +
             "Fine-tune this if the character floats above or sinks through the seat.")]
    public float sittingYOffset = 0f;

    [Header("Desk Detection — Grid Scan")]
    [Tooltip("Minimum height above floor to count as a desk surface (metres).\n" +
             "Just above the tallest chair seat (~0.65 m) so chairs are excluded.")]
    public float deskMinAboveFloor = 0.65f;

    [Tooltip("Maximum height above floor to count as a desk surface (metres).\n" +
             "Standard desk: ~0.75 m. 0.95 m covers high tables too.")]
    public float deskMaxAboveFloor = 0.95f;

    [Tooltip("Grid ray spacing for the desk scan (metres). 0.10 m matches the chair scan.")]
    public float deskGridStep = 0.10f;

    [Tooltip("Cluster radius for desk surface hits (metres).")]
    public float deskClusterRadius = 0.30f;

    [Tooltip("Minimum ray hits a desk cluster must have to be kept.\n" +
             "A desk is a larger surface than a chair so a higher value is fine.")]
    public int deskMinHits = 6;

    [Tooltip("How the student root Y is determined when using detected chair positions:\n" +
             "• OFF (default) — root placed at FLOOR level under the chair.\n" +
             "  Correct for standard Mixamo sitting animations whose root stays at Y=0\n" +
             "  while the hips rise ~0.45 m above it.\n" +
             "• ON — root placed at the detected SEAT SURFACE Y.\n" +
             "  Use this only if your animation's root is already at hip/seat height.")]
    public bool spawnAtSeatSurface = false;

    [Header("Audio")]
    public AudioSource lectureAudioSource;

    [Header("UI")]
    public GameObject lectureUI;

    // ── private ───────────────────────────────────────────────────
    private readonly List<GameObject> _spawnedNPCs = new List<GameObject>();
    private Action _onLectureComplete;
    private EnvironmentRaycastManager _envRaycast;

    // Set to true when students are spawned one-by-one via SpawnStudentAtChair,
    // so StartLectureWithChairs knows to skip the batch student-spawn step.
    private bool _studentsSpawnedProgressively = false;
    // Add this near your other private variables
    private GameObject _mainMuradInstance;

    // Shuffled queue of student prefab variants. Lazily built on first pick,
    // refilled (and re-shuffled) when exhausted. Reset by ClearScene.
    private List<StudentVariant> _shuffledStudentVariants;
    private int _variantCursor;

    private void Awake()
    {
        _envRaycast = FindAnyObjectByType<EnvironmentRaycastManager>();
    }

    // ── Public API ────────────────────────────────────────────────

    public void StartLecture(ExperienceConfig config, Action onComplete)
    {
        _onLectureComplete = onComplete;

        Vector3 forward = GetPlayerFlatForward();
        Vector3 anchor = ComputeSceneAnchor(forward);   // student area centre

        // ── Doctor beside the user, not in front of students ──────────
        Transform cam = Camera.main.transform;
        Vector3 camRight = new Vector3(cam.right.x, 0f, cam.right.z).normalized;
        Vector3 doctorPos = new Vector3(cam.position.x, 0f, cam.position.z)
                          + camRight * doctorSideOffset;
        doctorPos.y = FindFloorY(doctorPos, cam.position.y);

        // Students face the doctor; doctor faces the student anchor
        SpawnStudents(anchor, forward, config, doctorPos);
        SpawnDoctorAt(doctorPos, anchor, config);

        if (lectureUI != null)
            lectureUI.SetActive(true);

        StartCoroutine(RunLectureSequence(config));
        Debug.Log("[LectureHall] Scene spawned. Lecture starting.");
    }

    /// <summary>
    /// Chair-based entry point: students spawn at real chair positions from Meta Scene Understanding.
    /// Doctor still spawns in front of the player using camera forward.
    /// Students face the doctor automatically.
    /// Falls back to grid-based spawn if chairPositions is empty.
    /// </summary>
    public void StartLectureWithChairs(List<Vector3> chairPositions, ExperienceConfig config, Action onComplete)
    {
        _onLectureComplete = onComplete;

        if (chairPositions == null || chairPositions.Count == 0)
        {
            Debug.LogWarning("[LectureHall] No chair positions provided — falling back to grid spawn.");
            StartLecture(config, onComplete);
            return;
        }

        Vector3 forward = GetPlayerFlatForward();
        Vector3 anchor = ComputeSceneAnchor(forward);   // student area centre

        // ── Filter: keep only chairs that are in FRONT of the user ───────
        Transform cam = Camera.main.transform;
        var frontChairs = FilterChairsInFront(chairPositions, cam.position, forward);
        if (frontChairs.Count == 0)
        {
            Debug.LogWarning("[LectureHall] No chairs in front of user — using all chairs.");
            frontChairs = chairPositions;
        }
        Debug.Log($"[LectureHall] Using {frontChairs.Count} chair(s) in front of user " +
                  $"(filtered from {chairPositions.Count} total).");

        // ── Compute chair centroid ────────────────────────────────────
        Vector3 chairCentroid = Vector3.zero;
        foreach (var p in frontChairs) chairCentroid += p;
        chairCentroid /= frontChairs.Count;

        Vector3 userXZ = new Vector3(cam.position.x, 0f, cam.position.z);
        Vector3 centroidXZ = new Vector3(chairCentroid.x, 0f, chairCentroid.z);
        Vector3 toChairsDir = (centroidXZ - userXZ);
        if (toChairsDir.sqrMagnitude < 0.001f) toChairsDir = new Vector3(forward.x, 0f, forward.z);
        toChairsDir.Normalize();

        // ── Locate front-of-room anchor for doctor placement ─────────────
        // Priority 1 — SCREEN anchor (whiteboard / projector screen): most
        //              unique landmark in a lecture hall. Doctor stands in
        //              front of it facing the students.
        // Priority 2 — Grid-scan desk: largest flat surface at desk height
        //              on the far side of the chairs.
        // Priority 3 — MRUK TABLE label: fallback if grid scan finds nothing.
        // Priority 4 — No landmark: doctor placed past chairs along user→chairs.

        Vector3 doctorPos;
        Vector3 studentFaceTarget;

        Vector3? screenPos = FindScreenFromMRUK();

        if (screenPos.HasValue)
        {
            // ── Screen found: doctor stands in front of it ────────────
            Vector3 screenXZ = new Vector3(screenPos.Value.x, 0f, screenPos.Value.z);
            Vector3 screenToChairs = (centroidXZ - screenXZ);
            Vector3 screenToChairsDir = screenToChairs.sqrMagnitude > 0.001f
                                         ? screenToChairs.normalized
                                         : toChairsDir;

            Vector3 doctorXZ = screenXZ + screenToChairsDir * doctorInFrontOfScreenOffset;
            float doctorY = FindFloorY(doctorXZ, cam.position.y);
            doctorPos = new Vector3(doctorXZ.x, doctorY, doctorXZ.z);
            studentFaceTarget = doctorPos;

            Debug.Log($"[LectureHall] Screen at {screenPos.Value}  " +
                      $"Doctor in front at {doctorPos}  Students face doctor.");
        }
        else
        {
            // ── No screen — try desk (grid scan → MRUK TABLE) ─────────
            float floorY = GetRoomFloorY();
            Vector3? deskAnchorPos = FindDeskByGridScan(chairCentroid, toChairsDir, floorY)
                                  ?? FindDeskFromMRUK(chairCentroid);

            if (deskAnchorPos.HasValue)
            {
                // Doctor stands behind the desk (away from chairs)
                Vector3 deskXZ = new Vector3(deskAnchorPos.Value.x, 0f, deskAnchorPos.Value.z);
                Vector3 deskToChairs = (centroidXZ - deskXZ);
                Vector3 deskToChairsDir = deskToChairs.sqrMagnitude > 0.001f
                                             ? deskToChairs.normalized
                                             : -toChairsDir;

                Vector3 doctorXZ = deskXZ - deskToChairsDir * doctorBehindDeskOffset;
                float doctorY = FindFloorY(doctorXZ, cam.position.y);
                doctorPos = new Vector3(doctorXZ.x, doctorY, doctorXZ.z);
                studentFaceTarget = new Vector3(deskXZ.x,
                                                FindFloorY(deskXZ, cam.position.y) + 1.0f,
                                                deskXZ.z);

                Debug.Log($"[LectureHall] Desk at {deskAnchorPos.Value}  " +
                          $"Doctor behind desk at {doctorPos}.");
            }
            else
            {
                // No landmark at all — place doctor past the chairs
                Vector3 doctorXZ = centroidXZ + toChairsDir * doctorForwardOffset;
                float doctorY = FindFloorY(doctorXZ, cam.position.y);
                doctorPos = new Vector3(doctorXZ.x, doctorY, doctorXZ.z);
                studentFaceTarget = doctorPos;

                Debug.Log($"[LectureHall] No screen or desk found — " +
                          $"doctor placed past chairs at {doctorPos}.");
            }
        }

        // Students were already placed one-by-one during the scan — skip batch spawn.
        if (!_studentsSpawnedProgressively)
        {
            SpawnStudentsAtChairs(frontChairs, studentFaceTarget);
        }
        else
        {
            Debug.Log("[LectureHall] Students already placed progressively — skipping batch spawn.");
            // Progressive spawn always used studentPrefab — promote the one
            // closest to the user to the talking-Murad prefab.
            PromoteClosestStudentToMainMurad(config);
        }

        SpawnDoctorAt(doctorPos, chairCentroid, config);               // doctor faces chairs

        if (lectureUI != null)
            lectureUI.SetActive(true);

        StartCoroutine(RunLectureSequence(config));
        Debug.Log($"[LectureHall] Chair-based scene spawned with {frontChairs.Count} student(s).");
    }

    private void SpawnStudentsAtChairs(List<Vector3> chairPositions, Vector3 lookTarget)
    {
        bool mainMuradSpawned = false;

        foreach (Vector3 pos in chairPositions)
        {
            Vector3 dirToTarget = lookTarget - pos;
            dirToTarget.y = 0;
            Quaternion rot = dirToTarget != Vector3.zero ? Quaternion.LookRotation(dirToTarget) : Quaternion.identity;

            GameObject prefabToSpawn;
            AnimationClip variantClip = null;
            bool isMainMurad = false;

            if (!mainMuradSpawned && currentConfig.mainMuradPrefab != null)
            {
                prefabToSpawn = currentConfig.mainMuradPrefab;
                mainMuradSpawned = true;
                isMainMurad = true;
            }
            else
            {
                StudentVariant variant = PickStudentVariant(currentConfig);
                if (variant == null) continue;
                prefabToSpawn = variant.prefab;
                variantClip   = variant.sittingClip;
            }

            if (prefabToSpawn == null) continue;

            GameObject spawned = Instantiate(prefabToSpawn, pos, rot);
            EnsureBlockerCollider(spawned);
            _spawnedNPCs.Add(spawned);

            if (isMainMurad)
            {
                _mainMuradInstance = spawned;

                // Disable his standard AI script temporarily so he doesn't wander off
                MonoBehaviour muradAI = spawned.GetComponent("MuradController") as MonoBehaviour;
                if (muradAI != null) muradAI.enabled = false;

                // Force him to sit initially
                Animator anim = spawned.GetComponentInChildren<Animator>();
                if (anim != null)
                {
                    anim.SetBool("IsStanding", false);
                    anim.SetBool("IsWalking", false);
                    anim.SetBool("IsSitting", true);
                }
            }
            else
            {
                // Normal historical NPC setup — auto-attaches controller and assigns
                // the per-variant clip (or shared fallback) so each character sits
                // with its own animation.
                InitSeatedStudent(spawned, currentConfig, lookTarget, variantClip);
            }
        }
    }

    /// <summary>
    /// When students are spawned progressively (one per chair as detected),
    /// they all use studentPrefab. Call this after detection finishes to
    /// replace the seated student closest to the user with mainMuradPrefab.
    /// </summary>
    private void PromoteClosestStudentToMainMurad(ExperienceConfig config)
    {
        if (config == null || config.mainMuradPrefab == null)
        {
            Debug.LogWarning("[LectureHall] PromoteClosestStudentToMainMurad: mainMuradPrefab not assigned.");
            return;
        }
        if (Camera.main == null || _spawnedNPCs.Count == 0) return;

        Vector3 camXZ = new Vector3(
            Camera.main.transform.position.x, 0f,
            Camera.main.transform.position.z);

        int bestIdx = -1;
        float bestD2 = float.MaxValue;
        for (int i = 0; i < _spawnedNPCs.Count; i++)
        {
            GameObject npc = _spawnedNPCs[i];
            if (npc == null) continue;
            Vector3 p = npc.transform.position; p.y = 0f;
            float d2 = (p - camXZ).sqrMagnitude;
            if (d2 < bestD2) { bestD2 = d2; bestIdx = i; }
        }
        if (bestIdx < 0) return;

        GameObject toReplace = _spawnedNPCs[bestIdx];
        Vector3 pos = toReplace.transform.position;
        Quaternion rot = toReplace.transform.rotation;

        _spawnedNPCs.RemoveAt(bestIdx);
        Destroy(toReplace);

        GameObject murad = Instantiate(config.mainMuradPrefab, pos, rot);
        murad.name = "MainMurad_Seated";
        EnsureBlockerCollider(murad);
        _spawnedNPCs.Add(murad);
        _mainMuradInstance = murad;

        // Keep him seated until the lecture audio ends.
        MonoBehaviour muradAI = murad.GetComponent("MuradController") as MonoBehaviour;
        if (muradAI != null) muradAI.enabled = false;

        Animator anim = murad.GetComponentInChildren<Animator>();
        if (anim != null)
        {
            anim.SetBool("IsStanding", false);
            anim.SetBool("IsWalking", false);
            anim.SetBool("IsSitting", true);
        }

        Debug.Log($"[LectureHall] Promoted student at {pos} to main Murad (seated).");
    }

    /// <summary>
    /// Guarantees a freshly-spawned student plays the sitting animation, even if
    /// the prefab is a bare character (no HistoricalNPCController, no idle clip).
    /// Resolution order for the clip:
    ///   1. <paramref name="overrideClip"/> (per-character variant clip)
    ///   2. The prefab's existing HistoricalNPCController.idleClip
    ///   3. config.studentSittingClip (shared fallback)
    /// </summary>
    private void InitSeatedStudent(GameObject npc, ExperienceConfig config, Vector3 lookTarget,
                                   AnimationClip overrideClip = null)
    {
        if (npc == null) return;

        HistoricalNPCController ctrl = npc.GetComponent<HistoricalNPCController>();
        if (ctrl == null)
            ctrl = npc.AddComponent<HistoricalNPCController>();

        if (overrideClip != null)
            ctrl.idleClip = overrideClip;
        else if (ctrl.idleClip == null && config != null && config.studentSittingClip != null)
            ctrl.idleClip = config.studentSittingClip;

        if (ctrl.idleClip == null)
            Debug.LogWarning($"[LectureHall] {npc.name}: no per-variant clip, no prefab idleClip, " +
                             "and no shared studentSittingClip — character will T-pose.");

        ctrl.Init(NPCRole.Student, lookTarget);
    }

    /// <summary>
    /// Returns the next student variant (prefab + per-character sitting clip).
    /// If config.studentPrefabVariants has any entry with a non-null prefab,
    /// picks from that list using a Fisher-Yates shuffle; each entry is used
    /// exactly once before the pool is reshuffled.
    /// Falls back to a synthetic variant wrapping config.studentPrefab when the
    /// list is empty/null. Returns null if neither is set.
    /// </summary>
    private StudentVariant PickStudentVariant(ExperienceConfig config)
    {
        if (config == null) return null;

        StudentVariant[] variants = config.studentPrefabVariants;
        bool hasVariants = false;
        if (variants != null)
        {
            for (int i = 0; i < variants.Length; i++)
                if (variants[i] != null && variants[i].prefab != null) { hasVariants = true; break; }
        }

        if (!hasVariants)
        {
            if (config.studentPrefab == null) return null;
            return new StudentVariant { prefab = config.studentPrefab, sittingClip = null };
        }

        // (Re)build the shuffled queue if empty or exhausted.
        if (_shuffledStudentVariants == null || _variantCursor >= _shuffledStudentVariants.Count)
        {
            _shuffledStudentVariants = new List<StudentVariant>();
            foreach (var v in variants)
                if (v != null && v.prefab != null) _shuffledStudentVariants.Add(v);

            for (int i = _shuffledStudentVariants.Count - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                (_shuffledStudentVariants[i], _shuffledStudentVariants[j]) =
                    (_shuffledStudentVariants[j], _shuffledStudentVariants[i]);
            }
            _variantCursor = 0;
        }

        return _shuffledStudentVariants[_variantCursor++];
    }

    /// <summary>
    /// Adds a simple capsule collider to a spawned NPC so the walking Murad's
    /// CharacterController has something to collide against. The Reallusion
    /// character prefabs ship with bone-only colliders that are too small/sparse
    /// to act as a body blocker — this gives every NPC a clean 0.3 m capsule
    /// at body centre. No-op if the root already has a CapsuleCollider.
    /// </summary>
    private void EnsureBlockerCollider(GameObject npc)
    {
        if (npc == null) return;
        if (npc.GetComponent<CapsuleCollider>() != null) return;

        var cap = npc.AddComponent<CapsuleCollider>();
        cap.center    = new Vector3(0f, 0.9f, 0f);
        cap.radius    = 0.30f;
        cap.height    = 1.75f;
        cap.direction = 1; // Y-axis
    }

    /// <summary>
    /// Spawns a single student immediately at the detected chair position.
    /// The student faces AWAY from the user (direction user → chair), independent
    /// of where the doctor will eventually stand.
    /// Called progressively during the chair scan — one call per new chair found.
    /// </summary>
    public void SpawnStudentAtChair(Vector3 chairPos, ExperienceConfig config)
    {
        if (config == null)
        {
            Debug.LogWarning("[LectureHall] SpawnStudentAtChair: config is null.");
            return;
        }

        StudentVariant variant = PickStudentVariant(config);
        if (variant == null || variant.prefab == null)
        {
            Debug.LogWarning("[LectureHall] SpawnStudentAtChair: no studentPrefab or studentPrefabVariants assigned.");
            return;
        }

        _studentsSpawnedProgressively = true;

        // ── Vertical position ─────────────────────────────────────
        float camY = Camera.main != null ? Camera.main.transform.position.y : 1.7f;
        Vector3 spawnPos = chairPos;
        spawnPos.y = spawnAtSeatSurface
            ? chairPos.y + sittingYOffset
            : FindFloorY(chairPos, camY) + sittingYOffset;

        // ── Facing: away from user, toward the chair ──────────────
        // This is independent of the doctor position — students simply
        // face the natural "forward" direction of the room (away from viewer).
        Vector3 camXZ = Camera.main != null
            ? new Vector3(Camera.main.transform.position.x, 0f, Camera.main.transform.position.z)
            : Vector3.zero;
        Vector3 chairXZ = new Vector3(chairPos.x, 0f, chairPos.z);
        Vector3 faceDir = (camXZ - chairXZ);
        if (faceDir.sqrMagnitude < 0.001f) faceDir = Vector3.forward;
        faceDir.Normalize();

        // lookAt target slightly in front of the student (used by NPC controller)
        Vector3 lookTarget = spawnPos + faceDir * 2f;

        GameObject npc = Instantiate(variant.prefab, spawnPos, Quaternion.LookRotation(faceDir));
        npc.name = $"Student_{_spawnedNPCs.Count}";

        InitSeatedStudent(npc, config, lookTarget, variant.sittingClip);

        EnsureBlockerCollider(npc);

        _spawnedNPCs.Add(npc);
        Debug.Log($"[LectureHall] Student spawned at chair {spawnPos}  facing {faceDir}.");
    }

    /// <param name="facingTarget">World position the doctor should face — pass actual chair centroid.</param>
    private void SpawnDoctorAt(Vector3 pos, Vector3 facingTarget, ExperienceConfig config)
    {
        if (config.doctorPrefab == null)
        {
            Debug.LogWarning("[LectureHall] doctorPrefab not assigned.");
            return;
        }

        // Face toward the students immediately at spawn
        Vector3 dir = facingTarget - pos;
        dir.y = 0f;
        Quaternion rot = dir.sqrMagnitude > 0.001f
            ? Quaternion.LookRotation(dir)
            : Quaternion.identity;

        GameObject doctor = Instantiate(config.doctorPrefab, pos, rot);
        doctor.name = "Doctor_1918";

        HistoricalNPCController ctrl = doctor.GetComponent<HistoricalNPCController>();
        if (ctrl != null) ctrl.Init(NPCRole.Doctor, facingTarget);

        EnsureBlockerCollider(doctor);

        _spawnedNPCs.Add(doctor);
        Debug.Log($"[LectureHall] Doctor spawned at {pos}, facing {facingTarget}.");
    }

    public void ClearScene()
    {
        // This log includes a stack trace — use it to find what is calling ClearScene unexpectedly.
        Debug.Log("[LectureHall] ClearScene called.\n" + System.Environment.StackTrace);

        StopAllCoroutines();

        if (lectureAudioSource != null)
            lectureAudioSource.Stop();

        if (lectureUI != null)
            lectureUI.SetActive(false);

        foreach (var npc in _spawnedNPCs)
            if (npc != null) Destroy(npc);

        _spawnedNPCs.Clear();
        _studentsSpawnedProgressively = false;
        _shuffledStudentVariants = null;
        _variantCursor = 0;
        Debug.Log("[LectureHall] Scene cleared.");
    }

    // ── Spawning ──────────────────────────────────────────────────

    private void SpawnStudents(Vector3 anchor, Vector3 forward, ExperienceConfig config, Vector3 lookAtTarget)
    {
        int rows = Mathf.Max(1, config.studentRows);
        int cols = Mathf.Max(1, config.studentsPerRow);
        Vector3 right = Vector3.Cross(Vector3.up, forward);
        float halfX = (cols - 1) * seatSpacingX * 0.5f;
        float halfZ = (rows - 1) * seatSpacingZ * 0.5f;

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                StudentVariant variant = PickStudentVariant(config);
                if (variant == null || variant.prefab == null)
                {
                    Debug.LogWarning("[LectureHall] No studentPrefab or studentPrefabVariants assigned.");
                    return;
                }

                Vector3 offset = right * (-halfX + c * seatSpacingX)
                               + forward * (-halfZ + r * seatSpacingZ);

                Vector3 pos = new Vector3(anchor.x + offset.x, anchor.y + sittingYOffset, anchor.z + offset.z);

                GameObject npc = Instantiate(variant.prefab, pos, Quaternion.identity);
                npc.name = $"Student_{r}_{c}";

                // Students face back toward the user & doctor (lookAtTarget = camera position)
                InitSeatedStudent(npc, config, lookAtTarget, variant.sittingClip);

                _spawnedNPCs.Add(npc);
            }
        }

        Debug.Log($"[LectureHall] Spawned {rows * cols} students facing the user/doctor.");
    }

    // ── Lecture sequence ──────────────────────────────────────────

    // ── Lecture sequence ──────────────────────────────────────────
    private IEnumerator RunLectureSequence(ExperienceConfig config)
    {
        // Wait a brief moment before starting
        yield return new WaitForSeconds(1.0f);

        if (lectureAudioSource != null && config.lectureAudioClip != null)
        {
            lectureAudioSource.clip = config.lectureAudioClip;
            lectureAudioSource.Play();
            Debug.Log($"[LectureHall] Playing lecture audio: {config.lectureAudioClip.name}");

            // Wait for the audio clip to finish
            yield return new WaitForSeconds(config.lectureAudioClip.length);
        }

        Debug.Log("[LectureHall] Lecture audio finished.");

        // Stop doctor talking animation if you have one
        if (lectureUI != null) lectureUI.SetActive(false);

        // TRIGGER MAIN MURAD TO STAND UP AND WALK TO USER
        if (_mainMuradInstance != null)
        {
            StartCoroutine(MainMuradApproachUser());
        }
        else
        {
            // Fallback just in case Murad didn't spawn
            if (ExperienceManager.Instance != null)
            {
                // If you have a method to trigger Q&A phase in ExperienceManager, call it here
                // For example: ExperienceManager.Instance.OnLectureFinished();
                Debug.Log("[LectureHall] Lecture finished, handing control to ExperienceManager.");
            }
        }
    }

    // The Murad model's visible "face" is the +Z axis (standard Mixamo/Reallusion).
    // To face a target, just point +Z at it via Quaternion.LookRotation(target-from).
    private static Quaternion FaceTowards(Vector3 fromPos, Vector3 lookAt)
    {
        Vector3 dir = lookAt - fromPos;   // toward target → +Z
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f) return Quaternion.identity;
        return Quaternion.LookRotation(dir.normalized);
    }

    [Tooltip("Distance from the user where Murad stops after walking up (metres).")]
    public float muradFinalDistance = 1.2f;

    private IEnumerator MainMuradApproachUser()
    {
        Animator anim = _mainMuradInstance.GetComponentInChildren<Animator>();

        if (anim != null)
        {
            // Don't let Mixamo / Reallusion root motion drive the transform —
            // we drive it ourselves via CharacterController. Mixing the two
            // produces the "twitching" the user described.
            anim.applyRootMotion = false;

            // 1. Stand up
            anim.SetBool("IsSitting", false);
            anim.SetBool("IsStanding", true);
        }

        Transform camTransform = Camera.main.transform;
        float rotSpeed = 6f;

        // ── Phase 1: stand up AND smoothly turn to face the user ─────
        // Previously he just stood for 2 s with his back to the user. Now
        // we rotate during those 2 s so he greets the user instead of
        // moonwalking out of his chair.
        float standTimer = 0f;
        while (standTimer < 2.0f)
        {
            standTimer += Time.deltaTime;
            Quaternion target = FaceTowards(_mainMuradInstance.transform.position, camTransform.position);
            _mainMuradInstance.transform.rotation = Quaternion.Slerp(
                _mainMuradInstance.transform.rotation, target, rotSpeed * Time.deltaTime);
            yield return null;
        }

        if (anim != null) anim.SetBool("IsWalking", true);

        // ── Snap him to the real floor Y BEFORE walking ─────────────
        // He may have been seated at floor-level OR at the chair seat surface
        // depending on spawnAtSeatSurface. Either way, when he stands up he
        // should be on the floor — not floating, not sunk.
        float camY = camTransform != null ? camTransform.position.y : 1.7f;
        float floorY = FindFloorY(_mainMuradInstance.transform.position, camY);

        Vector3 startPos = _mainMuradInstance.transform.position;
        startPos.y = floorY;
        _mainMuradInstance.transform.position = startPos;

        // 2. Pick a stop point a "proper distance" in front of the user.
        Vector3 camForward = camTransform.forward;
        camForward.y = 0;
        camForward = camForward.sqrMagnitude > 0.001f ? camForward.normalized : Vector3.forward;
        Vector3 targetPos = camTransform.position + camForward * Mathf.Max(0.6f, muradFinalDistance);
        targetPos.y = floorY;

        // Disable his sitting capsule, then attach a CharacterController so the
        // walk respects scene/wall colliders and other students' capsules.
        var sittingCol = _mainMuradInstance.GetComponent<CapsuleCollider>();
        if (sittingCol != null) sittingCol.enabled = false;

        CharacterController cc = _mainMuradInstance.GetComponent<CharacterController>();
        if (cc == null)
        {
            cc = _mainMuradInstance.AddComponent<CharacterController>();
            cc.center     = new Vector3(0f, 0.9f, 0f);
            cc.radius     = 0.28f;
            cc.height     = 1.75f;
            cc.skinWidth  = 0.04f;
            cc.stepOffset = 0.25f;
        }

        float walkSpeed = 1.2f;

        // 3. Walk towards the user (collision-aware via CharacterController.Move)
        // No gravity simulation — MRUK floor colliders are optional, so we lock
        // his Y to the known floor Y for the entire walk.
        const float ARRIVE_DIST = 0.2f;
        float stuckTimer = 0f;
        Vector3 lastPos = _mainMuradInstance.transform.position;

        while (Vector3.Distance(_mainMuradInstance.transform.position, targetPos) > ARRIVE_DIST)
        {
            Vector3 toTarget = targetPos - _mainMuradInstance.transform.position;
            toTarget.y = 0f;
            Vector3 dir = toTarget.sqrMagnitude > 0.0001f ? toTarget.normalized : Vector3.zero;

            // Face the direction he's walking (model -Z = visible face).
            if (dir != Vector3.zero)
            {
                Quaternion lookRot = FaceTowards(_mainMuradInstance.transform.position,
                                                 _mainMuradInstance.transform.position + dir);
                _mainMuradInstance.transform.rotation = Quaternion.Slerp(
                    _mainMuradInstance.transform.rotation, lookRot, rotSpeed * Time.deltaTime);
            }

            // Pure horizontal motion. CharacterController still respects wall /
            // student capsule colliders for obstacle blocking.
            cc.Move(dir * walkSpeed * Time.deltaTime);

            // If a chair/student/wall blocks the straight path, sidestep it.
            if ((cc.collisionFlags & CollisionFlags.Sides) != 0)
            {
                Vector3 sideDir = Vector3.Cross(Vector3.up, dir).normalized;
                Vector3 toUser = (camTransform.position - _mainMuradInstance.transform.position);
                toUser.y = 0f;
                if (Vector3.Dot(sideDir, toUser) < 0f) sideDir = -sideDir;
                cc.Move(sideDir * walkSpeed * 0.6f * Time.deltaTime);
            }

            // Y clamp — use the proper teleport pattern (disable cc, set position,
            // re-enable) instead of writing transform.position while cc is active.
            // This eliminates the per-frame fight that was producing the twitching.
            Vector3 p = _mainMuradInstance.transform.position;
            if (Mathf.Abs(p.y - floorY) > 0.001f)
            {
                cc.enabled = false;
                p.y = floorY;
                _mainMuradInstance.transform.position = p;
                cc.enabled = true;
            }

            // Anti-stuck: if he hasn't moved in 1.5 s, give up.
            if ((_mainMuradInstance.transform.position - lastPos).sqrMagnitude < 0.0004f)
            {
                stuckTimer += Time.deltaTime;
                if (stuckTimer > 1.5f)
                {
                    Debug.LogWarning("[LectureHall] Murad stuck while walking to user — stopping early.");
                    break;
                }
            }
            else
            {
                stuckTimer = 0f;
                lastPos = _mainMuradInstance.transform.position;
            }

            yield return null;
        }

        // 4. Arrived! Stop walking, face the user squarely.
        if (anim != null) anim.SetBool("IsWalking", false);

        float faceTimer = 0f;
        while (faceTimer < 1f)
        {
            faceTimer += Time.deltaTime;
            Quaternion target = FaceTowards(_mainMuradInstance.transform.position, camTransform.position);
            _mainMuradInstance.transform.rotation = Quaternion.Slerp(
                _mainMuradInstance.transform.rotation, target, rotSpeed * Time.deltaTime);
            yield return null;
        }

        Debug.Log("[LectureHallManager] Main Murad is ready for Q&A.");

        // 5. Hand control back to Experience Manager for the Q&A / Voice phase
        if (ExperienceManager.Instance != null)
        {
            // Call your method here if you have one!
            Debug.Log("[LectureHallManager] Handing over to ExperienceManager.");
        }
    }

    private void NotifyDoctorLecturing(bool lecturing)
    {
        foreach (var npc in _spawnedNPCs)
        {
            if (npc == null) continue;
            var ctrl = npc.GetComponent<HistoricalNPCController>();
            if (ctrl != null && ctrl.Role == NPCRole.Doctor)
                ctrl.SetLecturing(lecturing);
        }
    }

    // ── Spatial helpers ───────────────────────────────────────────

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

    /// <summary>
    /// Returns only chairs that are in front of the user (positive dot product with forward).
    /// Uses a wide 120° cone so chairs slightly off to the sides are still included.
    /// </summary>
    private List<Vector3> FilterChairsInFront(List<Vector3> chairs, Vector3 camPos, Vector3 forward)
    {
        var result = new List<Vector3>();
        foreach (var pos in chairs)
        {
            Vector3 toChair = pos - camPos;
            toChair.y = 0f;
            // dot > -0.5 means within 120° cone in front (allows wide lateral spread)
            if (Vector3.Dot(toChair.normalized, forward) > -0.5f)
                result.Add(pos);
        }
        return result;
    }

    // ── Screen / desk detection helpers ──────────────────────────

    /// <summary>
    /// Finds a SCREEN (whiteboard / projector screen) or WALL_ART anchor in the MRUK room.
    /// Returns the world position of the first one found, or null if none exist.
    /// Label your whiteboard/screen as "Screen" once in Meta Quest Space Setup.
    /// </summary>
    private Vector3? FindScreenFromMRUK()
    {
        try
        {
            if (MRUK.Instance == null) return null;
            MRUKRoom room = MRUK.Instance.GetCurrentRoom();
            if (room == null) return null;

            // Check SCREEN first, then WALL_ART as secondary
            // (some users label whiteboards as Wall Art in Space Setup)
            string[] labels = { "SCREEN", "WALL_ART" };

            foreach (string label in labels)
            {
                foreach (MRUKAnchor anchor in room.Anchors)
                {
                    if (!anchor.HasLabel(label)) continue;

                    Vector3 pos = anchor.transform.position;
                    Debug.Log($"[LectureHall] Screen anchor [{label}] found at {pos}.");
                    return pos;
                }
            }

            Debug.Log("[LectureHall] No SCREEN or WALL_ART anchor in MRUK room — trying desk.");
            return null;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[LectureHall] FindScreenFromMRUK error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Returns the MRUK FLOOR anchor Y, or a camera-height fallback.
    /// </summary>
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
        catch (Exception) { }
        return Camera.main != null ? Camera.main.transform.position.y - 1.7f : 0f;
    }

    /// <summary>
    /// Greedy XZ point-clustering used for the desk surface scan.
    /// Returns cluster centroids that have at least <paramref name="minHits"/> members.
    /// </summary>
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
            if (best >= 0 && bestD < r2) { sums[best] += pt; counts[best]++; }
            else { sums.Add(pt); counts.Add(1); }
        }

        var result = new List<Vector3>();
        for (int i = 0; i < sums.Count; i++)
            if (counts[i] >= minHits)
                result.Add(sums[i] / counts[i]);
        return result;
    }

    /// <summary>
    /// PRIMARY desk finder: fires a downward ray grid at desk height and picks
    /// the horizontal surface cluster that lies furthest along the user→chairs
    /// direction (i.e. on the far side of the chairs, near the front wall).
    /// Returns null if no qualifying surface is found.
    /// </summary>
    private Vector3? FindDeskByGridScan(Vector3 chairCentroid, Vector3 toChairsDir, float floorY)
    {
        if (_envRaycast == null)
        {
            Debug.LogWarning("[LectureHall] FindDeskByGridScan: no EnvironmentRaycastManager.");
            return null;
        }

        Transform cam = Camera.main?.transform;
        if (cam == null) return null;

        float yMin = floorY + deskMinAboveFloor;
        float yMax = floorY + deskMaxAboveFloor;
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
                if (Vector3.Dot(hit.normal, Vector3.up) < 0.70f) continue;
                if (hit.point.y < yMin || hit.point.y > yMax) continue;

                rawHits.Add(hit.point);
            }
        }

        Debug.Log($"[LectureHall] Desk scan: {rawHits.Count} desk-height hit(s).");

        if (rawHits.Count == 0) return null;

        var clusters = ClusterSurfaces(rawHits, deskClusterRadius, deskMinHits);
        if (clusters.Count == 0)
        {
            Debug.Log("[LectureHall] Desk scan: no cluster passed minHits — no desk found.");
            return null;
        }

        // Pick the cluster that is furthest along the user→chairs direction
        // beyond the chair centroid. This is the front-of-room desk, not a
        // random table behind the user.
        Vector3 centroidXZ = new Vector3(chairCentroid.x, 0f, chairCentroid.z);
        Vector3? best = null;
        float bestScore = float.MinValue;

        foreach (var c in clusters)
        {
            Vector3 cXZ = new Vector3(c.x, 0f, c.z);
            // Score = how far this cluster is past the chair centroid in the
            // user→chairs direction. Positive = beyond the chairs (correct side).
            float score = Vector3.Dot(cXZ - centroidXZ, toChairsDir);
            if (score > bestScore) { bestScore = score; best = c; }
        }

        if (best.HasValue)
            Debug.Log($"[LectureHall] Desk found by grid scan at {best.Value}  " +
                      $"(score={bestScore:F2} m past chair centroid).");
        else
            Debug.Log("[LectureHall] Desk scan: no cluster on far side of chairs.");

        return best;
    }

    /// <summary>
    /// Finds the best TABLE anchor from MRUK to use as the lecture desk.
    /// Picks the TABLE closest to the line user→chairCentroid (i.e. in front of the chairs).
    /// Returns null if MRUK has no TABLE anchors.
    /// </summary>
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
                // Pick the TABLE that is closest to the chair centroid in XZ
                float d = Vector2.Distance(
                    new Vector2(pos.x, pos.z),
                    new Vector2(chairCentroid.x, chairCentroid.z));

                if (d < bestDist)
                {
                    bestDist = d;
                    best = pos;
                }
            }

            if (best.HasValue)
                Debug.Log($"[LectureHall] Desk anchor found at {best.Value} " +
                          $"({bestDist:F2} m from chair centroid).");
            else
                Debug.Log("[LectureHall] No TABLE anchor in MRUK room — desk detection skipped.");

            return best;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[LectureHall] FindDeskFromMRUK error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Returns real floor Y under xzPos.
    /// Priority: MRUK FLOOR anchor → low-origin raycast → cam-1.7 fallback.
    ///
    /// IMPORTANT: do NOT cast from camera height — the first upward-facing hit is
    /// the chair-seat surface, not the floor.  Casting from ~0.3 m above estimated
    /// floor finds the actual floor without being blocked by chair seats.
    /// </summary>
    private float FindFloorY(Vector3 xzPos, float cameraY)
    {
        // ── 1. MRUK FLOOR anchor ─────────────────────────────────
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
        catch (Exception) { }

        // ── 2. Raycast from just above estimated floor (avoids chair seats) ──
        if (_envRaycast != null)
        {
            float estimatedFloor = cameraY - 1.7f;
            Vector3 origin = new Vector3(xzPos.x, estimatedFloor + 0.3f, xzPos.z);
            if (_envRaycast.Raycast(new Ray(origin, Vector3.down), out var hit, 0.8f) &&
                Vector3.Dot(hit.normal, Vector3.up) > 0.7f)
                return hit.point.y;
        }

        // ── 3. Hard fallback ─────────────────────────────────────
        return cameraY - 1.7f;
    }
}
