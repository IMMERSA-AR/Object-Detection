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

    [Header("Chair Orientation")]
    [Tooltip("Flip the estimated chair-forward direction. Enable this if students consistently end up sitting backwards on their chairs.")]
    public bool flipChairForward = false;

    [Tooltip("Apply a 180° facing correction to the main Murad prefab.\n" +
             "Enable this if Murad sits with his back to the chair back while all other students face correctly.\n" +
             "Caused by the Murad prefab model facing the opposite axis (-Z) to the student prefabs (+Z).")]
    public bool flipMuradFacing = false;

    [Tooltip("Local-space position offset applied to Murad when he is seated.\n" +
             "X = left/right,  Y = up/down,  Z = forward/backward in Murad's facing direction.\n" +
             "Negative Z moves him BACKWARD toward the chair backrest.\n" +
             "Start with Z = -0.1 and adjust until his back rests against the chair.")]
    public Vector3 muradSeatOffset = new Vector3(0f, 0f, -0.1f);

    [Tooltip("Only used when muradSittingClip contains BOTH a sit-down AND a stand-up section.\n" +
             "Set this to the normalised time (0–1) where Murad is fully seated.\n" +
             "The clip will freeze here during the lecture and resume when it ends.\n" +
             "Set to 0 when using a dedicated looping sit clip (default).")]
    [Range(0f, 1f)]
    public float muradSitHoldNormalised = 0f;

    [Tooltip("How far (m) a detected chair position may be from an MRUK COUCH/OTHER anchor and still be matched to it for orientation.")]
    public float chairAnchorMatchRadius = 0.5f;

    [Tooltip("Vertical offset above the floor at which the backrest-detection raycast scan is performed (m). 0.75 m hits typical chair backrests.")]
    public float chairBackrestProbeHeight = 0.75f;

    [Header("Lighting")]
    [Tooltip("Parent GameObject that holds all 1918 lamp Point Lights.\n" +
             "Create an empty GameObject called 'LectureLights', parent your Point Lights\n" +
             "under it, then drag it here. Lights turn on when the scene spawns and\n" +
             "turn off when ClearScene() is called.")]
    public GameObject lectureLightsRoot;

    [Header("Audio")]
    public AudioSource lectureAudioSource;

    [Tooltip("Clip played through Murad once he walks up to the user after the lecture. " +
             "Assign basic_audio_murad.mp3 here.")]
    public AudioClip greetingAudioClip;

    [Tooltip("AudioSource used for the chair-detection phase audio.\n" +
             "Add a second AudioSource component to this GameObject, set Loop = ON and\n" +
             "Play On Awake = OFF, then drag it here. The clip is assigned at runtime\n" +
             "from ExperienceConfig.chairDetectionAudioClip.")]
    public AudioSource detectionAudioSource;

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

    // Drives the correct Humanoid sitting pose via SampleAnimation each LateUpdate,
    // overriding any Generic-clip T-pose the Animator Controller might produce.
    // Created by RunLectureSequence, destroyed by MainMuradApproachUser.
    private MuradSittingPoseDriver _sittingDriver;

    // Shuffled queue of student prefab variants. Lazily built on first pick,
    // refilled (and re-shuffled) when exhausted. Reset by ClearScene.
    private List<StudentVariant> _shuffledStudentVariants;
    private int _variantCursor;

    // When the variant pool is exhausted during progressive spawning and
    // mainMuradPrefab is set, we reserve that chair position for Murad
    // instead of reshuffling (which would create a duplicate character).
    private Vector3? _reservedMuradChairPos;
    private Quaternion _reservedMuradChairRot;

    private void Awake()
    {
        _envRaycast = FindAnyObjectByType<EnvironmentRaycastManager>();

        // Lights start OFF — turned on by StartLecture / StartLectureWithChairs
        if (lectureLightsRoot != null)
            lectureLightsRoot.SetActive(false);
    }

    // ── Public API ────────────────────────────────────────────────

    public void StartLecture(ExperienceConfig config, Action onComplete)
    {
        _onLectureComplete = onComplete;
        // Sync currentConfig so the batch spawn path always uses the same
        // config that ExperienceManager selected — prevents Inspector/runtime mismatch.
        if (config != null) currentConfig = config;
        if (lectureLightsRoot != null) lectureLightsRoot.SetActive(true);

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
    // Safety net — prevents duplicate starts if ExperienceManager fires the callback
    // multiple times due to duplicate event subscriptions.
    private bool _lectureActive = false;

    public void StartLectureWithChairs(List<Vector3> chairPositions, ExperienceConfig config, Action onComplete)
    {
        if (_lectureActive)
        {
            Debug.LogWarning("[LectureHall] StartLectureWithChairs called again — ignored (lecture already active).");
            return;
        }
        _lectureActive = true;

        _onLectureComplete = onComplete;
        // Sync currentConfig so SpawnStudentsAtChairs (batch path) and
        // PromoteClosestStudentToMainMurad both see the same config.
        if (config != null) currentConfig = config;
        if (lectureLightsRoot != null) lectureLightsRoot.SetActive(true);

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

        // Orientation is corrected INSIDE SpawnDoctorAt once the doctor position is
        // known — that gives us a reliable ground-truth direction instead of consensus.
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
            // Per-chair facing — rotate to match the real chair, not toward a shared point.
            Vector3 chairForward = EstimateChairForward(pos);
            Quaternion rot = Quaternion.LookRotation(chairForward);
            Vector3 perChairLookTarget = pos + chairForward * 2f;

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

            // Main Murad prefab may have its model root facing the opposite
            // axis to the student prefabs — flip 180° around Y to correct.
            Quaternion spawnRot = (isMainMurad && flipMuradFacing)
                ? rot * Quaternion.Euler(0f, 180f, 0f)
                : rot;

            GameObject spawned = Instantiate(prefabToSpawn, pos, spawnRot);
            EnsureBlockerCollider(spawned);
            _spawnedNPCs.Add(spawned);

            if (isMainMurad)
            {
                _mainMuradInstance = spawned;
                spawned.AddComponent<CharacterLightingStabilizer>();

                // Disable his wandering AI so he stays seated during the lecture.
                // Use the concrete type — string-based GetComponent can silently return null.
                MuradController muradAI = spawned.GetComponent<MuradController>();
                if (muradAI != null) muradAI.enabled = false;

                // Disable VoiceAPIController — if enabled at spawn it drives the Animator
                // (fighting IsSitting=true → T-pose) and starts Q&A voice listening early.
                var batchVoiceCtrl = spawned.GetComponent<VoiceAPIController>();
                if (batchVoiceCtrl != null)
                {
                    batchVoiceCtrl.enabled = false;
                    Debug.Log("[LectureHall] VoiceAPIController disabled on Murad (batch path) — re-enabled at Q&A.");
                }
                // Disable CustomLipSyncContext — it runs independently of VoiceAPIController
                // and opens Murad's mouth during the lecture whenever any audio plays.
                // Re-enabled in MainMuradApproachUser just before Q&A starts.
                var batchLipSync = spawned.GetComponentInChildren<LipSync.CustomLipSyncContext>(includeInactive: true);
                if (batchLipSync != null)
                {
                    batchLipSync.enabled = false;
                    Debug.Log("[LectureHall] CustomLipSyncContext disabled on Murad (batch path) — re-enabled at Q&A.");
                }
                // Clear any default status text and hide UI canvases on the prefab.
                // While VoiceAPIController is disabled, Start() is deferred, so the prefab's
                // default TMP_Text value stays visible. Clear it now.
                foreach (var tmp in spawned.GetComponentsInChildren<TMPro.TMP_Text>(includeInactive: true))
                    tmp.text = "";
                foreach (Canvas c in spawned.GetComponentsInChildren<Canvas>(includeInactive: true))
                    c.enabled = false;

                // Drive animation via Animator Controller booleans
                Animator anim = spawned.GetComponentInChildren<Animator>();
                if (anim != null)
                {
                    anim.applyRootMotion = false;
                    anim.SetBool("IsStanding", false);
                    anim.SetBool("IsWalking", false);
                    anim.SetBool("IsSitting", true);
                }

                // Murad animation is driven purely by the Animator Controller (MuradController.controller).
                // IsSitting=true (set above) keeps the Animator in its default Sitting Idle state.
                // RunLectureSequence() will inject the Humanoid muradSittingClip via
                // AnimatorOverrideController to fix T-pose from the Generic clip in the asset.
                // SpawnDoctorAt() will call SetHeadLookTarget(doctorPos) on him
                Debug.Log($"[LectureHall] Murad seated at {pos}  euler={spawnRot.eulerAngles}  " +
                          $"world_fwd={spawned.transform.forward:F2}  " +
                          $"(flipMuradFacing={flipMuradFacing}).");
            }
            else
            {
                // Normal historical NPC setup — auto-attaches controller and assigns
                // the per-variant clip (or shared fallback) so each character sits
                // with its own animation. Look target is per-chair so seated head
                // tracking matches the chair's facing direction, not a shared point.
                InitSeatedStudent(spawned, currentConfig, perChairLookTarget, variantClip);
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

        // ── Fast path: reserved chair available ───────────────────
        // When the variant pool was exhausted during progressive spawning,
        // a chair position was reserved specifically for Murad.
        // Spawn him there directly — no existing student needs to be destroyed,
        // so all 4 seats are filled with distinct characters.
        if (_reservedMuradChairPos.HasValue)
        {
            Vector3 rPos = _reservedMuradChairPos.Value;
            Quaternion rRot = flipMuradFacing
                ? _reservedMuradChairRot * Quaternion.Euler(0f, 180f, 0f)
                : _reservedMuradChairRot;

            Vector3 rLookTarget = rPos + rRot * Vector3.forward * 2f;
            rPos += rRot * muradSeatOffset;

            GameObject rMurad = Instantiate(config.mainMuradPrefab, rPos, rRot);
            rMurad.name = "MainMurad_Seated";

            MuradController rMuradAI = rMurad.GetComponent<MuradController>();
            if (rMuradAI != null) rMuradAI.enabled = false;

            // Disable VoiceAPIController during seated phase.
            // If enabled at spawn it immediately drives the Animator (T-pose) and
            // starts listening for Q&A voice input before the lecture even begins.
            // It is re-enabled in MainMuradApproachUser when Q&A is actually ready.
            var rVoiceCtrl = rMurad.GetComponent<VoiceAPIController>();
            if (rVoiceCtrl != null)
            {
                rVoiceCtrl.enabled = false;
                Debug.Log("[LectureHall] VoiceAPIController disabled on Murad — will re-enable at Q&A time.");
            }
            // Disable CustomLipSyncContext — runs independently, opens Murad's mouth during lecture.
            var rLipSync = rMurad.GetComponentInChildren<LipSync.CustomLipSyncContext>(includeInactive: true);
            if (rLipSync != null)
            {
                rLipSync.enabled = false;
                Debug.Log("[LectureHall] CustomLipSyncContext disabled on Murad (reserved-chair path).");
            }
            // Clear any status text shown by the Murad prefab before VoiceAPIController.Start()
            // could run. While the component is disabled Start() is deferred, so the prefab's
            // default TMP_Text value stays visible throughout the lecture. Clear it now.
            foreach (var tmp in rMurad.GetComponentsInChildren<TMPro.TMP_Text>(includeInactive: true))
                tmp.text = "";
            // Also hide any Canvas (subtitle / prompt panels) baked into the Murad prefab.
            foreach (Canvas c in rMurad.GetComponentsInChildren<Canvas>(includeInactive: true))
                c.enabled = false;

            EnsureBlockerCollider(rMurad);
            _spawnedNPCs.Add(rMurad);
            _mainMuradInstance = rMurad;

            Animator rAnim = rMurad.GetComponentInChildren<Animator>();
            if (rAnim != null)
            {
                rAnim.applyRootMotion = false;
                rAnim.SetBool("IsStanding", false);
                rAnim.SetBool("IsWalking",  false);
                rAnim.SetBool("IsSitting",  true);
            }

            // Murad animation is driven purely by the Animator Controller.
            // IsSitting=true (set above) keeps the Animator in Sitting Idle.
            // RunLectureSequence() injects muradSittingClip via AnimatorOverrideController.

            _reservedMuradChairPos = null;

            // ── Force-enable ALL renderers (in case prefab has any disabled) ─
            // This is the most common cause of "character spawns but is invisible".
            int rendererCount = 0;
            foreach (Renderer r in rMurad.GetComponentsInChildren<Renderer>(includeInactive: true))
            {
                r.enabled = true;
                rendererCount++;
            }
            // Also make sure every child GameObject is active.
            foreach (Transform child in rMurad.GetComponentsInChildren<Transform>(includeInactive: true))
                child.gameObject.SetActive(true);

            // ── Visibility diagnostics (split into separate Debug.Log calls  ─
            // so Android logcat line-length limits do NOT truncate the output) ─
            Debug.Log($"[LectureHall] *** MURAD SPAWN *** pos={rPos}  rot={rRot.eulerAngles}  prefab={config.mainMuradPrefab.name}");
            Debug.Log($"[LectureHall] MURAD: Animator={rAnim != null}  SitAnim=AnimatorController(IsSitting=true)  Renderers enabled={rendererCount}");

            SkinnedMeshRenderer[] smrs = rMurad.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive: true);
            if (smrs.Length == 0)
            {
                // Also check for regular MeshRenderer (some imported characters use these)
                MeshRenderer[] mrs = rMurad.GetComponentsInChildren<MeshRenderer>(includeInactive: true);
                Debug.LogWarning($"[LectureHall] MURAD: 0 SkinnedMeshRenderers! MeshRenderers={mrs.Length}  " +
                                 $"-- Check the Murad prefab has a character mesh attached.");
            }
            else
            {
                foreach (var smr in smrs)
                {
                    smr.updateWhenOffscreen = true;   // prevent hands/feet culling during sitting animation
                    Debug.Log($"[LectureHall] MURAD SMR: '{smr.name}' enabled={smr.enabled}  " +
                              $"bounds.center={smr.bounds.center}  go.active={smr.gameObject.activeSelf}");
                }
            }
            return;
        }

        if (_spawnedNPCs.Count == 0) return;

        // Compute the centroid of all spawned students (XZ only).
        // Murad is promoted to the student CLOSEST TO THE CENTROID so he appears
        // at the natural "lead" position in the group — not right in the user's face.
        Vector3 centroidXZ = Vector3.zero;
        int validCount = 0;
        for (int i = 0; i < _spawnedNPCs.Count; i++)
        {
            if (_spawnedNPCs[i] == null) continue;
            Vector3 p = _spawnedNPCs[i].transform.position; p.y = 0f;
            centroidXZ += p;
            validCount++;
        }
        if (validCount == 0) return;
        centroidXZ /= validCount;

        int bestIdx = -1;
        float bestD2 = float.MaxValue;
        for (int i = 0; i < _spawnedNPCs.Count; i++)
        {
            GameObject npc = _spawnedNPCs[i];
            if (npc == null) continue;
            Vector3 p = npc.transform.position; p.y = 0f;
            float d2 = (p - centroidXZ).sqrMagnitude;
            if (d2 < bestD2) { bestD2 = d2; bestIdx = i; }
        }
        if (bestIdx < 0) return;

        GameObject toReplace = _spawnedNPCs[bestIdx];
        Vector3 pos = toReplace.transform.position;
        Quaternion rot = toReplace.transform.rotation;   // inherit the chair's detected rotation

        _spawnedNPCs.RemoveAt(bestIdx);
        Destroy(toReplace);

        // Use the replaced student's chair rotation.
        // CorrectStudentOrientations() (called by StartLectureWithChairs right after)
        // will flip Murad 180° if his chair was mis-detected — same safety net used
        // for all other students.
        Quaternion muradRot = flipMuradFacing
            ? rot * Quaternion.Euler(0f, 180f, 0f)
            : rot;

        // Apply seat offset in Murad's local space so his back reaches the chair backrest.
        // muradSeatOffset.z < 0 shifts him backward (toward the backrest).
        Vector3 spawnPos = pos + muradRot * muradSeatOffset;

        GameObject murad = Instantiate(config.mainMuradPrefab, spawnPos, muradRot);
        murad.name = "MainMurad_Seated";
        murad.AddComponent<CharacterLightingStabilizer>();

        // Disable AI immediately (same frame as Instantiate, before Start() fires).
        MuradController muradAI = murad.GetComponent<MuradController>();
        if (muradAI != null) muradAI.enabled = false;

        // Disable VoiceAPIController during seated phase (same reason as reserved-chair path).
        var voiceCtrlSeat = murad.GetComponent<VoiceAPIController>();
        if (voiceCtrlSeat != null)
        {
            voiceCtrlSeat.enabled = false;
            Debug.Log("[LectureHall] VoiceAPIController disabled on Murad (promote path) — re-enabled at Q&A.");
        }
        // Disable CustomLipSyncContext — runs independently, opens Murad's mouth during lecture.
        var promoteLipSync = murad.GetComponentInChildren<LipSync.CustomLipSyncContext>(includeInactive: true);
        if (promoteLipSync != null)
        {
            promoteLipSync.enabled = false;
            Debug.Log("[LectureHall] CustomLipSyncContext disabled on Murad (promote path).");
        }
        // Clear any default text / hide canvas panels on the Murad prefab (same as reserved-chair path).
        foreach (var tmp in murad.GetComponentsInChildren<TMPro.TMP_Text>(includeInactive: true))
            tmp.text = "";
        foreach (Canvas c in murad.GetComponentsInChildren<Canvas>(includeInactive: true))
            c.enabled = false;

        EnsureBlockerCollider(murad);
        _spawnedNPCs.Add(murad);
        _mainMuradInstance = murad;

        Animator anim = murad.GetComponentInChildren<Animator>();
        if (anim != null)
        {
            anim.applyRootMotion = false;
            anim.SetBool("IsStanding", false);
            anim.SetBool("IsWalking", false);
            anim.SetBool("IsSitting", true);
        }

        // Murad animation is driven purely by the Animator Controller.
        // IsSitting=true (set above) keeps the Animator in Sitting Idle.
        // RunLectureSequence() injects muradSittingClip via AnimatorOverrideController.
        // SpawnDoctorAt() runs next and will redirect his look target to the doctor

        // Force-enable ALL renderers (same fix as the reserved-chair path).
        foreach (Renderer r in murad.GetComponentsInChildren<Renderer>(includeInactive: true))
            r.enabled = true;
        foreach (Transform child in murad.GetComponentsInChildren<Transform>(includeInactive: true))
            child.gameObject.SetActive(true);
        // Prevent hand/foot culling during sitting animation (bounds computed from T-pose at import).
        foreach (var smr in murad.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive: true))
            smr.updateWhenOffscreen = true;

        // Log Murad's world-space forward so orientation can be verified in logcat.
        // "Murad world fwd" should point toward the front of the room (doctor side).
        // If it points the wrong way, enable flipMuradFacing in the Inspector.
        Debug.Log($"[LectureHall] Murad seated at {spawnPos}  euler={muradRot.eulerAngles}  " +
                  $"world_fwd={murad.transform.forward:F2}  " +
                  $"(flipMuradFacing={flipMuradFacing}).");
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

        // updateWhenOffscreen: sitting animations move the hands/feet outside the
        // SkinnedMeshRenderer's import-time bounds (computed from the T-pose).
        // Without this flag Unity culls the mesh as "off-screen" → hands appear cut off.
        var studentSMRs = npc.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive: true);
        foreach (var smr in studentSMRs)
        {
            smr.updateWhenOffscreen = true;
            Debug.Log($"[LectureHall] STUDENT SMR '{npc.name}/{smr.name}' " +
                      $"enabled={smr.enabled}  updateWhenOffscreen=true  " +
                      $"bounds.center={smr.bounds.center}  go.active={smr.gameObject.activeSelf}");
        }
        if (studentSMRs.Length == 0)
            Debug.LogWarning($"[LectureHall] STUDENT '{npc.name}' has NO SkinnedMeshRenderers — check prefab setup.");

        // ── NPCHandRest is intentionally NOT added here ───────────────────────
        // NPCHandRest pulls forearms toward hip-relative "lap targets" in LateUpdate.
        // For CC4 characters in a sitting pose those targets land inside the thigh mesh,
        // so the hands disappear into the geometry.  The sitting animation clip already
        // poses the hands correctly — adding the script makes things worse, not better.
        // (updateWhenOffscreen above handles the only real culling issue.)
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

        // Build the candidate list — always exclude mainMuradPrefab so it can
        // never appear as a regular student AND as the promoted main character.
        var candidates = new List<StudentVariant>();
        if (variants != null)
        {
            foreach (var v in variants)
            {
                if (v == null || v.prefab == null) continue;
                if (config.mainMuradPrefab != null && v.prefab == config.mainMuradPrefab)
                {
                    Debug.LogWarning($"[LectureHall] PickStudentVariant: skipping '{v.prefab.name}' " +
                                     "because it matches mainMuradPrefab — would cause duplicates.");
                    continue;
                }
                candidates.Add(v);
            }
        }

        // No variants configured — fall back to the single studentPrefab.
        if (candidates.Count == 0)
        {
            if (config.studentPrefab == null) return null;
            if (config.mainMuradPrefab != null && config.studentPrefab == config.mainMuradPrefab)
            {
                Debug.LogWarning("[LectureHall] studentPrefab == mainMuradPrefab — cannot spawn a distinct student.");
                return null;
            }
            return new StudentVariant { prefab = config.studentPrefab, sittingClip = null };
        }

        // Build the pool on first call or if not yet built.
        if (_shuffledStudentVariants == null || _shuffledStudentVariants.Count == 0)
        {
            _shuffledStudentVariants = new List<StudentVariant>(candidates);

            // Fisher-Yates shuffle so order is random each session.
            for (int i = _shuffledStudentVariants.Count - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                (_shuffledStudentVariants[i], _shuffledStudentVariants[j]) =
                    (_shuffledStudentVariants[j], _shuffledStudentVariants[i]);
            }
            _variantCursor = 0;
        }

        // Pool exhausted — decide whether to reshuffle or stop.
        if (_variantCursor >= _shuffledStudentVariants.Count)
        {
            // If mainMuradPrefab will fill a chair via promotion, returning null
            // tells the caller to reserve that chair for Murad instead of spawning
            // a duplicate student. This guarantees all seated characters are distinct.
            if (config.mainMuradPrefab != null)
            {
                Debug.Log("[LectureHall] PickStudentVariant: pool exhausted — " +
                          "returning null so caller can reserve chair for Murad.");
                return null;
            }

            // No Murad promotion — reshuffle and continue (repeats are acceptable).
            _shuffledStudentVariants = new List<StudentVariant>(candidates);
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
            // Pool exhausted and mainMuradPrefab is set — reserve this chair for Murad.
            // PromoteClosestStudentToMainMurad will spawn Murad here directly
            // without destroying any existing student, keeping all 4 seats distinct.
            if (config.mainMuradPrefab != null && _reservedMuradChairPos == null)
            {
                Vector3 chairForwardReserved = EstimateChairForward(chairPos);
                float camYReserved = Camera.main != null ? Camera.main.transform.position.y : 1.7f;
                Vector3 reservedPos = chairPos;
                reservedPos.y = spawnAtSeatSurface
                    ? chairPos.y + sittingYOffset
                    : FindFloorY(chairPos, camYReserved) + sittingYOffset;

                _reservedMuradChairPos = reservedPos;
                _reservedMuradChairRot = Quaternion.LookRotation(chairForwardReserved);
                _studentsSpawnedProgressively = true;
                Debug.Log($"[LectureHall] Chair at {reservedPos} reserved for Murad (pool exhausted).");
            }
            else
            {
                Debug.LogWarning("[LectureHall] SpawnStudentAtChair: no studentPrefab or variants assigned.");
            }
            return;
        }

        _studentsSpawnedProgressively = true;

        // ── Vertical position ─────────────────────────────────────
        float camY = Camera.main != null ? Camera.main.transform.position.y : 1.7f;
        Vector3 spawnPos = chairPos;
        spawnPos.y = spawnAtSeatSurface
            ? chairPos.y + sittingYOffset
            : FindFloorY(chairPos, camY) + sittingYOffset;

        // ── Facing: derive from the real chair's orientation ──────
        // Resolution order: nearest MRUK anchor → backrest raycast scan
        // → fallback (away from user). See EstimateChairForward.
        Vector3 chairForward = EstimateChairForward(chairPos);

        // lookAt target slightly in front of the student (used by NPC controller)
        Vector3 lookTarget = spawnPos + chairForward * 2f;

        GameObject npc = Instantiate(variant.prefab, spawnPos, Quaternion.LookRotation(chairForward));
        npc.name = $"Student_{_spawnedNPCs.Count}";

        InitSeatedStudent(npc, config, lookTarget, variant.sittingClip);

        EnsureBlockerCollider(npc);

        _spawnedNPCs.Add(npc);
        Debug.Log($"[LectureHall] Student spawned at chair {spawnPos}  facing {chairForward}.");
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

        // Prevent arm/hand culling during the lecture animation.
        // The doctor's talking clip moves arms outside the T-pose import bounds;
        // without this flag Unity culls the SMR as "off-screen" → hands disappear.
        foreach (var smr in doctor.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive: true))
            smr.updateWhenOffscreen = true;

        EnsureBlockerCollider(doctor);

        _spawnedNPCs.Add(doctor);
        Debug.Log($"[LectureHall] Doctor spawned at {pos}, facing {facingTarget}.");

        // Spawn era props now that both doctor position and chair centroid are known.
        float floorY = FindFloorY(pos, Camera.main != null ? Camera.main.transform.position.y : 1.7f);
        var props = GetComponent<LectureHallProps>();
        if (props != null)
            props.SpawnProps(pos, rot, facingTarget, floorY);

        // Now that the doctor position is known:
        //   1. Correct body orientation — any student facing more than 90° away
        //      from the doctor gets flipped 180°.  Using the doctor as ground truth
        //      is far more reliable than the MRUK anchor data or consensus heuristics.
        //   2. Redirect head look-at target to the doctor.
        int flipped = 0;
        foreach (var npc in _spawnedNPCs)
        {
            if (npc == null || npc == doctor) continue;
            var studentCtrl = npc.GetComponent<HistoricalNPCController>();
            if (studentCtrl == null || studentCtrl.Role != NPCRole.Student) continue;

            // ── Body orientation ──────────────────────────────────
            Vector3 toDoc = pos - npc.transform.position; toDoc.y = 0f;
            if (toDoc.sqrMagnitude > 0.001f)
            {
                Vector3 fwd = npc.transform.forward; fwd.y = 0f;
                if (Vector3.Dot(fwd.normalized, toDoc.normalized) < 0f)
                {
                    npc.transform.rotation *= Quaternion.Euler(0f, 180f, 0f);
                    flipped++;
                    Debug.Log($"[LectureHall] Orientation fix: flipped '{npc.name}' to face doctor.");
                }
            }

            // ── Head look-at ──────────────────────────────────────
            studentCtrl.SetHeadLookTarget(pos);
        }
        // ── Murad orientation fix ─────────────────────────────────
        // Murad has no HistoricalNPCController so the student loop above
        // skips him. Apply the same body-flip logic here so he faces the
        // doctor instead of facing away from the front of the room.
        if (_mainMuradInstance != null)
        {
            Vector3 muradToDoc = pos - _mainMuradInstance.transform.position;
            muradToDoc.y = 0f;
            if (muradToDoc.sqrMagnitude > 0.001f)
            {
                Vector3 muradFwd = _mainMuradInstance.transform.forward;
                muradFwd.y = 0f;
                if (Vector3.Dot(muradFwd.normalized, muradToDoc.normalized) < 0f)
                {
                    _mainMuradInstance.transform.rotation *= Quaternion.Euler(0f, 180f, 0f);
                    flipped++;
                    Debug.Log("[LectureHall] Orientation fix: flipped Murad to face doctor.");
                }
            }
        }

        Debug.Log($"[LectureHall] SpawnDoctorAt: {flipped} student(s)/Murad flipped to face doctor. " +
                  $"All {_spawnedNPCs.Count - 1} student(s) now track doctor.");
    }

    public void ClearScene()
    {
        // This log includes a stack trace — use it to find what is calling ClearScene unexpectedly.
        Debug.Log("[LectureHall] ClearScene called.\n" + System.Environment.StackTrace);

        StopAllCoroutines();

        if (lectureAudioSource != null)
            lectureAudioSource.Stop();

        if (detectionAudioSource != null)
            detectionAudioSource.Stop();

        // Stop doctor's own AudioSource (used for lip-sync-compatible audio playback).
        foreach (var npc in _spawnedNPCs)
        {
            if (npc == null) continue;
            var npcCtrl = npc.GetComponent<HistoricalNPCController>();
            if (npcCtrl == null || npcCtrl.Role != NPCRole.Doctor) continue;
            AudioSource docAudio = npc.GetComponent<AudioSource>();
            if (docAudio != null) docAudio.Stop();
        }

        if (lectureLightsRoot != null)
            lectureLightsRoot.SetActive(false);

        if (lectureUI != null)
            lectureUI.SetActive(false);

        foreach (var npc in _spawnedNPCs)
            if (npc != null) Destroy(npc);

        _spawnedNPCs.Clear();
        _studentsSpawnedProgressively = false;
        _shuffledStudentVariants = null;
        _variantCursor = 0;
        _reservedMuradChairPos = null;
        _lectureActive = false;
        _sittingDriver = null;   // GameObject is destroyed above; null the ref so GC can collect it

        var props = GetComponent<LectureHallProps>();
        if (props != null) props.ClearProps();

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

    // ── Detection audio ───────────────────────────────────────────

    /// <summary>
    /// Starts the chair-detection phase audio (looping).
    /// Called by ExperienceManager as soon as chair scanning begins.
    /// </summary>
    public void PlayDetectionAudio(AudioClip clip)
    {
        if (detectionAudioSource == null || clip == null) return;
        detectionAudioSource.clip  = clip;
        detectionAudioSource.loop  = true;
        detectionAudioSource.Play();
        Debug.Log($"[LectureHall] Detection audio started: {clip.name}");
    }

    // ── Lecture sequence ──────────────────────────────────────────
    private IEnumerator RunLectureSequence(ExperienceConfig config)
    {
        // Doctor has just appeared — stop the detection audio before lecture begins.
        if (detectionAudioSource != null && detectionAudioSource.isPlaying)
        {
            detectionAudioSource.Stop();
            Debug.Log("[LectureHall] Detection audio stopped — lecture starting.");
        }

        // ── Fix Murad T-pose during seated phase ─────────────────────────────
        // MuradSittingPoseDriver (below) calls SampleAnimation(muradSittingClip)
        // every LateUpdate, writing the correct Humanoid bone transforms after the
        // Animator runs. This is sufficient — no AnimatorOverrideController needed.
        //
        // WHY AnimatorOverrideController was removed:
        //   muradAnim.runtimeAnimatorController = overrideCtrl resets ALL Animator
        //   parameters to their defaults and corrupts the state machine on Quest at
        //   runtime: subsequent CrossFade() and IsName() calls are silently ignored
        //   (stateHash stays constant, normTime stays 0.00 forever) → stand-up and
        //   walk animations never play. The SittingPoseDriver alone handles the T-pose
        //   fix without touching the Animator's state machine.
        yield return null;

        // ── SittingPoseDriver: correct Humanoid sitting pose via SampleAnimation ──
        // this component calls SampleAnimation(muradSittingClip) in LateUpdate —
        // AFTER the Animator has run — directly writing the correct Humanoid bone
        // transforms onto Murad's skeleton. No name matching required.
        if (_mainMuradInstance != null && config?.muradSittingClip != null)
        {
            _sittingDriver = _mainMuradInstance.AddComponent<MuradSittingPoseDriver>();
            _sittingDriver.clip = config.muradSittingClip;
            Debug.Log($"[LectureHall] ✓ MuradSittingPoseDriver started — " +
                      $"clip='{config.muradSittingClip.name}'  length={config.muradSittingClip.length:F2}s  " +
                      $"isHumanMotion={config.muradSittingClip.humanMotion}");
        }
        else if (config?.muradSittingClip == null)
        {
            Debug.LogError("[LectureHall] ✗ config.muradSittingClip is NULL — Murad will T-pose during lecture. " +
                           "Assign a Humanoid sitting animation clip to ExperienceConfig.muradSittingClip in the Inspector.");
        }

        // Wait a brief moment before starting
        yield return new WaitForSeconds(1.0f);

        if (config.lectureAudioClip == null)
        {
            Debug.LogError("[LectureHall] *** lectureAudioClip is NULL in ExperienceConfig! ***\n" +
                           "Assign the audio file (e.g. prof_roger.mp3) to ExperienceConfig.lectureAudioClip " +
                           "in the Inspector. Skipping audio — lecture will finish instantly.");
        }

        if (config.lectureAudioClip != null)
        {
            // ── Step 1: Play audible audio through lectureAudioSource ─────────
            // lectureAudioSource lives on LectureHallManager — no OVRLipSyncContext
            // on that GameObject, so the audio buffer reaches the headset speaker
            // intact. This is the confirmed-working approach from the obelisk commit.
            if (lectureAudioSource != null)
            {
                lectureAudioSource.outputAudioMixerGroup = null;  // bypass any muted mixer group
                lectureAudioSource.spatialBlend          = 0f;    // 2D — always heard at full volume
                lectureAudioSource.volume                = 1f;
                lectureAudioSource.mute                  = false;
                lectureAudioSource.loop                  = false;
                lectureAudioSource.clip                  = config.lectureAudioClip;
                lectureAudioSource.Play();
                Debug.Log($"[LectureHall] Playing lecture audio: {config.lectureAudioClip.name}  " +
                          $"length={config.lectureAudioClip.length:F1}s");
            }
            else
            {
                Debug.LogError("[LectureHall] lectureAudioSource is NULL — assign an AudioSource component " +
                               "to the LectureHallManager GameObject and drag it into the 'Lecture Audio Source' field.");
            }

            // ── Step 2: Drive lip sync via doctor's own OVRLipSyncContext AudioSource ──
            // This is the same technique used by Murad's VoiceAPIController during Q&A:
            //
            //   • OVRLipSyncContext is [RequireComponent(AudioSource)], so the doctor's
            //     prefab already has an AudioSource on the same GO as the context.
            //   • We assign the lecture clip to that AudioSource and play it at volume=1.
            //     Unity's DSP calls OVRLipSyncContext.OnAudioFilterRead for every buffer
            //     → PreprocessAudioSamples (gain) → ProcessAudioSamplesRaw (viseme FFT)
            //     → PostprocessAudioSamples (ZEROES buffer because audioLoopback=false).
            //   • Because PostprocessAudioSamples zeroes the buffer, the user does NOT
            //     hear the doctor's AudioSource — they hear lectureAudioSource instead
            //     (which has no OVRLipSyncContext and therefore no zeroing).
            //   • OVRLipSyncContextMorphTarget reads the viseme frame each Update()
            //     and drives the mouth blend-shapes → lips move in sync.
            //
            // volume MUST be > 0: Unity multiplies samples by volume BEFORE calling
            // OnAudioFilterRead, so volume=0 → all-zero PCM → no visemes detected.
            // mute MUST be false: Unity skips OnAudioFilterRead entirely for muted sources.
            GameObject doctorNPC = null;
            AudioSource doctorLipSyncAudio = null;

            foreach (var npc in _spawnedNPCs)
            {
                if (npc == null) continue;
                var npcCtrl = npc.GetComponent<HistoricalNPCController>();
                if (npcCtrl != null && npcCtrl.Role == NPCRole.Doctor) { doctorNPC = npc; break; }
            }

            if (doctorNPC != null)
            {
                OVRLipSyncContext lipCtx = doctorNPC.GetComponentInChildren<OVRLipSyncContext>();
                if (lipCtx != null)
                {
                    doctorLipSyncAudio = lipCtx.GetComponent<AudioSource>();
                    if (doctorLipSyncAudio != null)
                    {
                        doctorLipSyncAudio.outputAudioMixerGroup = null;
                        doctorLipSyncAudio.clip         = config.lectureAudioClip;
                        doctorLipSyncAudio.loop         = false;
                        doctorLipSyncAudio.mute         = false; // must NOT be muted
                        doctorLipSyncAudio.volume       = 1f;    // must be > 0 for PCM to reach OnAudioFilterRead
                        doctorLipSyncAudio.spatialBlend = 0f;
                        doctorLipSyncAudio.Play();
                        // PostprocessAudioSamples zeros this buffer → inaudible to user.
                        // lectureAudioSource (no OVRLipSyncContext) provides the heard audio.

                        var morphTarget = doctorNPC.GetComponentInChildren<OVRLipSyncContextMorphTarget>();
                        if (morphTarget == null)
                            Debug.LogWarning("[LectureHall] OVRLipSyncContextMorphTarget not found on doctor — " +
                                             "add it to the Amin prefab and assign the face SkinnedMeshRenderer.");
                        else
                            Debug.Log($"[LectureHall] Doctor lip sync active: context='{lipCtx.gameObject.name}' " +
                                      $"mesh='{(morphTarget.skinnedMeshRenderer != null ? morphTarget.skinnedMeshRenderer.name : "NULL")}'");
                    }
                    else
                    {
                        Debug.LogWarning("[LectureHall] OVRLipSyncContext found but no AudioSource on same GO — lip sync skipped.");
                    }
                }
                else
                {
                    Debug.LogWarning("[LectureHall] No OVRLipSyncContext found on doctor — " +
                                     "lips will not move. Add OVRLipSyncContext to the Amin prefab.");
                }
            }
            else
            {
                Debug.LogWarning("[LectureHall] Doctor NPC not found in _spawnedNPCs — lip sync skipped.");
            }

            // ── Step 3: Wait for lecture to finish ────────────────────────────
            if (lectureAudioSource != null)
                yield return new WaitForSeconds(config.lectureAudioClip.length);

            // ── Step 4: Stop doctor's lip-sync source — lips return to neutral ─
            if (doctorLipSyncAudio != null) doctorLipSyncAudio.Stop();
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
        // ── Stop the sitting-pose override BEFORE triggering stand-up ────────
        if (_sittingDriver != null)
        {
            Destroy(_sittingDriver);
            _sittingDriver = null;
            Debug.Log("[LectureHall] MuradSittingPoseDriver destroyed — Animator takes over for stand-up.");
        }

        // ── DIAGNOSTICS: decode known state name hashes ──────────────────────
        Debug.Log($"[LectureHall] Expected hashes — " +
                  $"SittingIdle={Animator.StringToHash("Sitting Idle")}  " +
                  $"StandingIdle={Animator.StringToHash("Standing Idle")}  " +
                  $"Walk={Animator.StringToHash("Amin_Motion_Imported_Walk Relaxed_2Loop")}  " +
                  $"Talk={Animator.StringToHash("Amin_Motion_Imported_Talk Serious")}");

        // ── DIAGNOSTICS: log every Animator on Murad so we know which one is used ──
        Animator[] allAnims = _mainMuradInstance.GetComponentsInChildren<Animator>(true);
        Debug.Log($"[LectureHall] Murad Animator count = {allAnims.Length}");
        for (int i = 0; i < allAnims.Length; i++)
        {
            var a = allAnims[i];
            Debug.Log($"[LectureHall]   anim[{i}] on '{a.gameObject.name}'  " +
                      $"enabled={a.enabled}  ctrl={(a.runtimeAnimatorController != null ? a.runtimeAnimatorController.name : "NULL")}  " +
                      $"paramCount={a.parameterCount}");
        }

        // Use the Animator that has parameters (is connected to MuradController.controller).
        Animator anim = null;
        foreach (var a in allAnims)
        {
            if (a.runtimeAnimatorController != null && a.parameterCount > 0)
            { anim = a; break; }
        }
        if (anim == null && allAnims.Length > 0) anim = allAnims[0]; // fallback

        Debug.Log($"[LectureHall] Selected Animator: {(anim != null ? anim.gameObject.name : "NULL")}  " +
                  $"ctrl={(anim?.runtimeAnimatorController != null ? anim.runtimeAnimatorController.name : "NULL")}");

        // ── DIAGNOSTICS: log all animator parameters ──────────────────────────
        if (anim != null)
        {
            string pList = "";
            for (int i = 0; i < anim.parameterCount; i++)
                pList += anim.parameters[i].name + " ";
            Debug.Log($"[LectureHall] Animator parameters: [{pList.Trim()}]");
        }

        // ── Stop any PlayableGraph (HistoricalNPCController head-look) ──────
        // IMPORTANT: must be done BEFORE any SetBool/Play calls, because an active
        // PlayableGraph overrides the Animator Controller's output AND silently
        // causes anim.Play() to be ignored on Quest.
        var muradHNPC = _mainMuradInstance.GetComponent<HistoricalNPCController>();
        if (muradHNPC != null)
        {
            Debug.Log($"[LectureHall] Found HistoricalNPCController on Murad — stopping PlayableGraph.");
            muradHNPC.ClearHeadLookTarget();
            muradHNPC.StopPlayableGraph();
        }
        else
        {
            Debug.Log("[LectureHall] No HistoricalNPCController on Murad root — checking children...");
            var muradHNPCChild = _mainMuradInstance.GetComponentInChildren<HistoricalNPCController>(true);
            if (muradHNPCChild != null)
            {
                Debug.Log($"[LectureHall] Found HistoricalNPCController on child '{muradHNPCChild.gameObject.name}' — stopping.");
                muradHNPCChild.ClearHeadLookTarget();
                muradHNPCChild.StopPlayableGraph();
            }
            else
            {
                Debug.Log("[LectureHall] No HistoricalNPCController found anywhere on Murad.");
            }
        }

        // Wait TWO frames so Destroy(sittingDriver) fully completes and any
        // pending PlayableGraph output is flushed before we touch the Animator.
        yield return null;
        yield return null;

        if (anim != null)
        {
            anim.enabled = true;
            anim.applyRootMotion = false;

            // ── CRITICAL: Always Animate prevents Quest from freezing bones when
            // Murad is considered "off-screen" (culled) during the walk.
            // "Cull Update Transforms" (the prefab default) stops bone updates the
            // moment the character is outside the frustum — causing the sitting-pose
            // freeze even when the Animator state machine has moved to Walk.
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            // Log state BEFORE we do anything
            var before = anim.GetCurrentAnimatorStateInfo(0);
            Debug.Log($"[LectureHall] State BEFORE stand-up: hash={before.shortNameHash}  time={before.normalizedTime:F2}  " +
                      $"IsSitting={anim.GetBool("IsSitting")}  IsStanding={anim.GetBool("IsStanding")}  IsWalking={anim.GetBool("IsWalking")}");

            // Force Standing Idle directly — bypass transition conditions entirely.
            anim.Play("Standing Idle", 0, 0f);

            anim.SetBool("IsSitting",  false);
            anim.SetBool("IsStanding", true);
            anim.SetBool("IsWalking",  false);
            Debug.Log("[LectureHall] Murad: forced Standing Idle + IsSitting=false IsStanding=true.  cullingMode=AlwaysAnimate");
        }

        Transform camTransform = Camera.main.transform;
        float rotSpeed = 6f;

        // ── Phase 1: stand-up phase (2 s) — rotate Murad to face the user ────
        float standTimer = 0f;
        while (standTimer < 2.0f)
        {
            standTimer += Time.deltaTime;
            if (_mainMuradInstance != null && camTransform != null)
            {
                Quaternion target = FaceTowards(
                    _mainMuradInstance.transform.position, camTransform.position);
                _mainMuradInstance.transform.rotation = Quaternion.Slerp(
                    _mainMuradInstance.transform.rotation, target, rotSpeed * Time.deltaTime);
            }
            yield return null;
        }

        // ── Phase 2: start walking ─────────────────────────────────────────────
        if (anim != null)
        {
            // Log state after stand-up phase
            var mid = anim.GetCurrentAnimatorStateInfo(0);
            Debug.Log($"[LectureHall] State after stand-up phase: hash={mid.shortNameHash}  time={mid.normalizedTime:F2}  " +
                      $"IsSitting={anim.GetBool("IsSitting")}  IsStanding={anim.GetBool("IsStanding")}  IsWalking={anim.GetBool("IsWalking")}");

            // Force Walk state directly — no transition conditions needed.
            anim.SetBool("IsStanding", false);
            anim.SetBool("IsWalking",  true);
            anim.Play("Amin_Motion_Imported_Walk Relaxed_2Loop", 0, 0f);

            yield return null;  // one frame — let Play() take effect
            if (anim != null)
            {
                var wi = anim.GetCurrentAnimatorStateInfo(0);
                // IsName needs the FULL layer-prefixed path, e.g. "Base Layer.StateName".
                // shortNameHash == StringToHash("StateName") is the reliable way to check.
                int expectedWalkHash = Animator.StringToHash("Amin_Motion_Imported_Walk Relaxed_2Loop");
                bool isWalkByHash   = wi.shortNameHash == expectedWalkHash;
                bool isWalkByName   = wi.IsName("Base Layer.Amin_Motion_Imported_Walk Relaxed_2Loop");
                Debug.Log($"[LectureHall] Walk state: hash={wi.shortNameHash}  expectedWalkHash={expectedWalkHash}  " +
                          $"IsWalkByHash={isWalkByHash}  IsWalkByName={isWalkByName}  time={wi.normalizedTime:F2}  " +
                          $"IsSitting={anim.GetBool("IsSitting")}  IsStanding={anim.GetBool("IsStanding")}  IsWalking={anim.GetBool("IsWalking")}");
            }
        }

        // ── Snap to floor Y before walking ────────────────────────────────────
        float camY = camTransform != null ? camTransform.position.y : 1.7f;
        float floorY = FindFloorY(_mainMuradInstance.transform.position, camY);
        Vector3 startPos = _mainMuradInstance.transform.position;
        startPos.y = floorY;
        _mainMuradInstance.transform.position = startPos;

        // Compute the stop-point in front of the user.
        Vector3 camFwdInit = camTransform.forward;
        camFwdInit.y = 0f;
        if (camFwdInit.sqrMagnitude > 0.001f) camFwdInit.Normalize(); else camFwdInit = Vector3.forward;
        Vector3 targetPos = camTransform.position
                          + camFwdInit * Mathf.Max(0.6f, muradFinalDistance);
        targetPos.y = floorY;

        // Disable sitting blocker collider; attach CharacterController for
        // obstacle-aware movement (matches the working "add obelisk code" approach).
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

        // ── Walk loop ──────────────────────────────────────────────────────────
        const float ARRIVE_TOLERANCE = 0.2f;

        Debug.Log($"[LectureHall] Murad starting walk from {_mainMuradInstance.transform.position} " +
                  $"toward user at {camTransform.position}  goal={muradFinalDistance:F1}m away");

        while (true)
        {
            // ── Recompute target every frame so Murad follows a moving user ──
            Vector3 camFwd = camTransform.forward;
            camFwd.y = 0f;
            camFwd = camFwd.sqrMagnitude > 0.001f ? camFwd.normalized : Vector3.forward;

            // Target = exactly muradFinalDistance metres in front of the camera (on floor).
            targetPos = camTransform.position + camFwd * muradFinalDistance;
            targetPos.y = floorY;

            // Horizontal distance from Murad to that target point.
            float dx = _mainMuradInstance.transform.position.x - targetPos.x;
            float dz = _mainMuradInstance.transform.position.z - targetPos.z;
            float distToTarget = Mathf.Sqrt(dx * dx + dz * dz);

            if (distToTarget <= ARRIVE_TOLERANCE) break;   // arrived!

            // Direction toward target (horizontal only).
            Vector3 dir = new Vector3(-dx, 0f, -dz) / distToTarget;

            // Smoothly rotate toward walking direction.
            _mainMuradInstance.transform.rotation = Quaternion.Slerp(
                _mainMuradInstance.transform.rotation,
                Quaternion.LookRotation(dir),
                rotSpeed * Time.deltaTime);

            // Advance — clamp step so he doesn't overshoot on the last frame.
            float step = Mathf.Min(walkSpeed * Time.deltaTime, distToTarget);
            Vector3 newPos = _mainMuradInstance.transform.position + dir * step;
            newPos.y = floorY;
            _mainMuradInstance.transform.position = newPos;

            yield return null;
        }

        Debug.Log($"[LectureHall] Murad arrived at {_mainMuradInstance.transform.position}  " +
                  $"(camera={camTransform.position})");

        // 4. Arrived! Clear IsWalking → Animator Controller transitions to Standing Idle.
        if (anim != null)
            anim.SetBool("IsWalking", false);

        float faceTimer = 0f;
        while (faceTimer < 1f)
        {
            faceTimer += Time.deltaTime;
            Quaternion target = FaceTowards(_mainMuradInstance.transform.position, camTransform.position);
            _mainMuradInstance.transform.rotation = Quaternion.Slerp(
                _mainMuradInstance.transform.rotation, target, rotSpeed * Time.deltaTime);
            yield return null;
        }

        // ── Q&A phase: Animator Controller is already in Standing Idle ──────
        // VoiceAPIController.Update() will set IsTalking=true when audio plays
        // and IsTalking=false when done — the Animator Controller handles the
        // Talk Serious ↔ Standing Idle transitions automatically.
        // Re-enable MuradController now that its Start() can run safely
        // (MuradController.Start() sets IsStanding=true which matches current state).
        MuradController muradQAAI = _mainMuradInstance?.GetComponent<MuradController>();
        if (muradQAAI != null) muradQAAI.enabled = true;

        // ── Re-enable VoiceAPIController + CustomLipSyncContext for Q&A ────────
        // Both were disabled at spawn to keep Murad silent and still during lecture.
        // Re-enabling here so lips move correctly from the very first greeting word.
        if (_mainMuradInstance != null)
        {
            // ── Locate the CustomLipSyncContext ───────────────────────────────
            // GetComponentInChildren(includeInactive:true) searches inactive child GOs
            // too.  If the component's GO was inactive at instantiation, Awake() never
            // ran and _predictor is null.  We fix this by:
            //   1. Activating the child GO so Unity's lifecycle can proceed.
            //   2. Calling EnsureInitialized() to bootstrap predictor + AudioSource
            //      without relying on Awake/Start timing.
            var qaLipSync = _mainMuradInstance.GetComponentInChildren<LipSync.CustomLipSyncContext>(includeInactive: true);
            if (qaLipSync != null)
            {
                // Ensure the GO the component lives on is active.
                if (!qaLipSync.gameObject.activeSelf)
                {
                    qaLipSync.gameObject.SetActive(true);
                    Debug.Log("[LectureHall] CustomLipSyncContext GO was inactive — activated for Q&A.");
                }

                qaLipSync.enabled = true;

                // Bootstrap predictor in case Awake() never ran (inactive GO at spawn).
                qaLipSync.EnsureInitialized();

                Debug.Log("[LectureHall] CustomLipSyncContext re-enabled + initialized for Q&A.");
            }
            else
            {
                Debug.LogWarning("[LectureHall] CustomLipSyncContext NOT FOUND on Murad — lip sync will be silent.");
            }

            VoiceAPIController qaVoice = _mainMuradInstance.GetComponent<VoiceAPIController>();
            if (qaVoice != null)
            {
                // Re-enable Canvas panels BEFORE enabling VoiceAPIController so that
                // Start() (which clears the status text) runs with the UI already visible.
                foreach (Canvas c in _mainMuradInstance.GetComponentsInChildren<Canvas>(includeInactive: true))
                    c.enabled = true;

                qaVoice.enabled = true;
                Debug.Log("[LectureHall] VoiceAPIController re-enabled — Q&A is now active.");
            }
        }

        Debug.Log("[LectureHallManager] Main Murad is ready for Q&A.");

        // 5. Play Murad's greeting audio.
        // StreamVoiceAPIController uses TWO AudioSources:
        //   audioSource   → lip-sync only (OVRLipSyncContext watches this)
        //   speakerSource → child AudioSource that actually reaches the headset
        // We must play on BOTH so lips move AND audio is heard.
        if (greetingAudioClip != null && _mainMuradInstance != null)
        {
            VoiceAPIController voiceCtrl = _mainMuradInstance.GetComponent<VoiceAPIController>();

            if (voiceCtrl != null)
            {
                // Pre-compute the viseme timeline for the greeting clip BEFORE playback.
                // CustomLipSyncContext.Update() looks up the timeline by AudioClip instance;
                // if FeedAudioClip is not called first, it finds nothing → lips never move.
                //
                // voiceCtrl.customLipSyncContext is an Inspector-wired field; if it is null
                // (not assigned), fall back to finding the component ourselves.
                var lipCtx = voiceCtrl.customLipSyncContext
                          ?? _mainMuradInstance.GetComponentInChildren<LipSync.CustomLipSyncContext>(includeInactive: true);

                if (lipCtx != null)
                {
                    lipCtx.EnsureInitialized();       // idempotent — safe if already ready
                    lipCtx.FeedAudioClip(greetingAudioClip);
                    Debug.Log("[LectureHall] Greeting: FeedAudioClip called on CustomLipSyncContext.");
                }
                else
                {
                    Debug.LogWarning("[LectureHall] Greeting: No CustomLipSyncContext found — " +
                                     "lip sync will not play for greeting.");
                }

                // --- Lip source (moves the mouth) ---
                if (voiceCtrl.audioSource != null)
                {
                    voiceCtrl.audioSource.Stop();
                    voiceCtrl.audioSource.clip        = greetingAudioClip;
                    voiceCtrl.audioSource.loop        = false;
                    voiceCtrl.audioSource.mute        = false;
                    voiceCtrl.audioSource.volume      = 1f;
                    voiceCtrl.audioSource.spatialBlend = 1f;
                    voiceCtrl.audioSource.Play();
                }

                // --- Speaker source (actually heard in the headset) ---
                if (voiceCtrl.speakerSource != null)
                {
                    voiceCtrl.speakerSource.Stop();
                    voiceCtrl.speakerSource.clip        = greetingAudioClip;
                    voiceCtrl.speakerSource.loop        = false;
                    voiceCtrl.speakerSource.mute        = false;
                    voiceCtrl.speakerSource.volume      = 1f;
                    voiceCtrl.speakerSource.spatialBlend = 1f;
                    voiceCtrl.speakerSource.Play();
                }
                else
                {
                    // No speaker child — unmute the lip source as fallback
                    if (voiceCtrl.audioSource != null)
                        voiceCtrl.audioSource.mute = false;
                }

                Debug.Log("[LectureHallManager] Playing Murad greeting audio via VoiceAPIController sources.");
            }
            else
            {
                // Fallback: VoiceAPIController not found, just use the first AudioSource
                AudioSource muradAudio = _mainMuradInstance.GetComponent<AudioSource>();
                if (muradAudio == null)
                    muradAudio = _mainMuradInstance.AddComponent<AudioSource>();

                muradAudio.spatialBlend = 1f;
                muradAudio.loop         = false;
                muradAudio.mute         = false;
                muradAudio.volume       = 1f;
                muradAudio.clip         = greetingAudioClip;
                muradAudio.Play();
                Debug.Log("[LectureHallManager] Playing Murad greeting audio via fallback AudioSource.");
            }
        }

        // 6. Hand control back to Experience Manager for the Q&A / Voice phase
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
    /// Estimates which way a chair faces, so the seated student can be rotated to
    /// match the real chair instead of always pointing at the user.
    /// Resolution order:
    ///   1. Closest MRUK COUCH/OTHER anchor within chairAnchorMatchRadius — uses anchor.transform.forward.
    ///   2. Backrest detection — fires horizontal rays around the chair at backrestProbe height
    ///      and returns the OPPOSITE of the nearest near-vertical hit (the back of the chair).
    ///   3. Fallback — the direction from user to chair (chair faces away from viewer).
    /// Returned vector is unit-length and flat (Y = 0).
    /// </summary>
    private Vector3 EstimateChairForward(Vector3 chairPos)
    {
        // ── Fallback: chair faces away from the user ─────────────
        Vector3 fallback;
        if (Camera.main != null)
        {
            Vector3 camXZ   = new Vector3(Camera.main.transform.position.x, 0f, Camera.main.transform.position.z);
            Vector3 chairXZ = new Vector3(chairPos.x, 0f, chairPos.z);
            Vector3 awayFromUser = chairXZ - camXZ;
            fallback = awayFromUser.sqrMagnitude > 0.0001f ? awayFromUser.normalized : Vector3.forward;
        }
        else fallback = Vector3.forward;

        // ── Level 1: nearest MRUK COUCH/OTHER anchor ─────────────
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
                        if (!anchor.HasLabel("COUCH") && !anchor.HasLabel("OTHER")) continue;
                        Vector3 ap = anchor.transform.position;
                        float dx = ap.x - chairPos.x, dz = ap.z - chairPos.z;
                        float d  = Mathf.Sqrt(dx * dx + dz * dz);
                        if (d < bestDist) { bestDist = d; best = anchor; }
                    }
                    if (best != null)
                    {
                        Vector3 fwd = best.transform.forward;
                        fwd.y = 0f;
                        if (fwd.sqrMagnitude > 0.0001f)
                        {
                            Vector3 r = fwd.normalized;
                            if (flipChairForward) r = -r;
                            Debug.Log($"[LectureHall] Chair @ ({chairPos.x:F2},{chairPos.z:F2}): forward from MRUK anchor '{best.gameObject.name}' = {r}");
                            return r;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[LectureHall] EstimateChairForward MRUK error: {ex.Message}");
        }

        // ── Level 2: backrest detection via raycast scan ─────────
        if (_envRaycast != null)
        {
            float camY   = Camera.main != null ? Camera.main.transform.position.y : 1.7f;
            float floorY = FindFloorY(chairPos, camY);
            float probeY = floorY + chairBackrestProbeHeight;
            Vector3 origin = new Vector3(chairPos.x, probeY, chairPos.z);

            const int   numRays      = 16;
            const float searchRadius = 0.40f;   // chair backrest ≤ ~0.4 m from seat centre

            float closest = searchRadius;
            Vector3 backDir = Vector3.zero;

            for (int i = 0; i < numRays; i++)
            {
                float angle = (i / (float)numRays) * 2f * Mathf.PI;
                Vector3 dir = new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle));

                if (_envRaycast.Raycast(new Ray(origin, dir), out var hit, searchRadius))
                {
                    // EnvironmentRaycastHit doesn't expose distance — derive it.
                    float dist = Vector3.Distance(origin, hit.point);
                    // Want a near-vertical surface (a backrest, not the floor or ceiling).
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
                if (flipChairForward) r = -r;
                Debug.Log($"[LectureHall] Chair @ ({chairPos.x:F2},{chairPos.z:F2}): forward from backrest scan = {r} (back at {closest:F2}m)");
                return r;
            }
        }

        // ── Level 3: fallback ────────────────────────────────────
        Vector3 fb = flipChairForward ? -fallback : fallback;
        Debug.Log($"[LectureHall] Chair @ ({chairPos.x:F2},{chairPos.z:F2}): no orientation info — fallback (away from user) = {fb}");
        return fb;
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

    // ── Murad sitting pose driver ─────────────────────────────────────────────
    /// <summary>
    /// Temporary MonoBehaviour added to Murad's root GameObject during the
    /// seated lecture phase.
    ///
    /// WHY IT EXISTS:
    ///   MuradController.controller's "Sitting Idle" state references an FBX-embedded
    ///   Generic animation (import type = Generic). Playing a Generic clip on Murad's
    ///   Humanoid CC4 avatar retargets to nothing → T-pose.
    ///
    ///   This component calls <see cref="AnimationClip.SampleAnimation"/> every
    ///   LateUpdate — AFTER the Animator has written its Generic-clip pose — to
    ///   overwrite the bone transforms with the correct Humanoid sitting pose.
    ///   No clip-name matching or AnimatorOverrideController slot lookup needed.
    ///
    /// LIFETIME:
    ///   Added  → end of RunLectureSequence (right after the Animator override attempt)
    ///   Removed → start of MainMuradApproachUser (before IsSitting=false fires)
    /// </summary>
    private class MuradSittingPoseDriver : MonoBehaviour
    {
        /// <summary>
        /// Humanoid sitting animation clip (e.g. a Mixamo "Sitting Idle" .anim
        /// imported with Rig = Humanoid). Must NOT be null.
        /// </summary>
        public AnimationClip clip;

        private float _t = 0f;

        // ── Facial protection (blend shapes + jaw bone) ──────────────────────
        // clip.SampleAnimation() on a Humanoid clip writes BOTH:
        //   (a) blend-shape tracks baked into the clip  → CC4 viseme morphs
        //   (b) the Humanoid Jaw muscle → the physical CC4_Base_JawRoot bone
        // Both paths can make Murad's mouth appear open during the lecture.
        // Fix: snapshot blend shapes AND the jaw bone before SampleAnimation,
        // then restore them after, so the sitting-idle clip drives the body pose
        // only and can never touch the face.
        private SkinnedMeshRenderer _faceMesh;
        private float[]             _savedWeights;
        private Transform           _jawBone;
        private Quaternion          _savedJawRot;

        void Awake()
        {
            // ── Blend shapes: find mesh with most blend shapes (= CC4 face/body) ──
            var smrs = GetComponentsInChildren<SkinnedMeshRenderer>(true);
            int maxBS = 0;
            foreach (var smr in smrs)
            {
                if (smr.sharedMesh == null) continue;
                int count = smr.sharedMesh.blendShapeCount;
                if (count > maxBS) { maxBS = count; _faceMesh = smr; }
            }
            if (_faceMesh != null)
            {
                _savedWeights = new float[maxBS];
                Debug.Log($"[MuradSittingPoseDriver] Face mesh = '{_faceMesh.name}'  " +
                          $"blend shapes to protect = {maxBS}");
            }
            else
            {
                Debug.LogWarning("[MuradSittingPoseDriver] No SkinnedMeshRenderer with " +
                                 "blend shapes found — blend-shape mouth protection inactive.");
            }

            // ── Jaw bone: Humanoid rig maps jaw via HumanBodyBones.Jaw muscle ──
            var anim = GetComponentInChildren<Animator>();
            if (anim != null && anim.isHuman)
            {
                _jawBone = anim.GetBoneTransform(HumanBodyBones.Jaw);
                if (_jawBone != null)
                {
                    _savedJawRot = _jawBone.localRotation;   // rest pose = closed mouth
                    Debug.Log($"[MuradSittingPoseDriver] Jaw bone = '{_jawBone.name}'  " +
                              $"restRot={_savedJawRot.eulerAngles}");
                }
                else
                {
                    Debug.Log("[MuradSittingPoseDriver] HumanBodyBones.Jaw not mapped in avatar — " +
                              "jaw-bone mouth protection inactive (blend-shape protection still active).");
                }
            }
        }

        void LateUpdate()
        {
            if (clip == null) return;

            // Save root transform BEFORE SampleAnimation.
            // For Humanoid clips SampleAnimation applies root-motion, which would slide
            // Murad off his chair every frame.  We want body bone poses only, not root.
            Vector3    savedPos = transform.position;
            Quaternion savedRot = transform.rotation;

            // Save facial state so the sitting clip cannot open the mouth.
            if (_faceMesh != null && _savedWeights != null)
                for (int i = 0; i < _savedWeights.Length; i++)
                    _savedWeights[i] = _faceMesh.GetBlendShapeWeight(i);

            // Save jaw bone rotation (Humanoid muscle path).
            // We save it every frame so other systems (e.g. CustomLipSyncMorphTarget)
            // can update it — we just prevent the sitting-idle clip from overriding it.
            Quaternion jawBefore = _jawBone != null ? _jawBone.localRotation : Quaternion.identity;

            // SampleAnimation writes directly to the skeleton using Humanoid retargeting.
            clip.SampleAnimation(gameObject, _t);

            // Restore root position/rotation.
            transform.position = savedPos;
            transform.rotation = savedRot;

            // Restore blend shapes — undoes any jaw/mouth blend-shape curves in the clip.
            if (_faceMesh != null && _savedWeights != null)
                for (int i = 0; i < _savedWeights.Length; i++)
                    _faceMesh.SetBlendShapeWeight(i, _savedWeights[i]);

            // Restore jaw bone — undoes the Humanoid Jaw muscle applied by the clip.
            if (_jawBone != null)
                _jawBone.localRotation = jawBefore;

            // Advance time and loop.
            _t += Time.deltaTime;
            if (_t > clip.length) _t %= clip.length;
        }
    }

    // ── Lip-sync audio bridge ─────────────────────────────────────────────────
    /// <summary>
    /// Temporary component added to LectureHallManager's GameObject while the
    /// lecture audio plays.  Because it lives on the SAME GameObject as
    /// lectureAudioSource, Unity calls its OnAudioFilterRead for every DSP buffer
    /// produced by that source.  We forward those raw samples directly to the
    /// doctor's OVRLipSyncContext.ProcessAudioSamplesRaw — which feeds the FFT
    /// analyser and updates the viseme frame WITHOUT touching the audio buffer
    /// (no zeroing, no loopback concern).  The doctor's
    /// OVRLipSyncContextMorphTarget reads the viseme frame in its own Update()
    /// and drives the mouth blend-shapes.
    ///
    /// Destroyed by LectureHallManager as soon as the lecture clip ends.
    /// </summary>
    private class LipSyncBridge : MonoBehaviour
    {
        /// <summary>The doctor's OVRLipSyncContext to forward samples into.</summary>
        public OVRLipSyncContext targetCtx;

        // Called on the audio DSP thread for every buffer produced by the child
        // AudioSource on this GameObject.  IMPORTANT: this runs on the AUDIO DSP
        // THREAD, not the main thread.  Do NOT access any Unity API properties
        // here (e.g. isActiveAndEnabled, gameObject.activeSelf, transform) — they
        // throw UnityException on non-main threads.  Only plain C# field reads and
        // ProcessAudioSamplesRaw (which uses lock(this) internally) are safe.
        private void OnAudioFilterRead(float[] data, int channels)
        {
            // targetCtx is a plain reference — null check is thread-safe.
            // ProcessAudioSamplesRaw guards itself: returns early if
            // OVRLipSync is not initialised or Context == 0.
            if (targetCtx == null) return;
            targetCtx.ProcessAudioSamplesRaw(data, channels);
        }
    }
}
