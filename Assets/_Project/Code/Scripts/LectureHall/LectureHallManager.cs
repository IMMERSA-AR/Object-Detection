using System;
using System.Collections.Generic;
using UnityEngine;
using Meta.XR;

public partial class LectureHallManager : MonoBehaviour
{
    [Header("Configuration")]
    public ExperienceConfig currentConfig;
    public float sceneDistance = 2.5f;
    public float seatSpacingX = 0.85f;
    public float seatSpacingZ = 0.9f;
    public float doctorSideOffset = 1.2f;     //Doctor's poistion referred to the user's right
    public float doctorForwardOffset = 1.2f;    //used when no desk detected
    public float doctorBehindDeskOffset = 0.5f;
    public float doctorInFrontOfScreenOffset = 0.8f;
    public float sittingYOffset = 0f;

    [Header("Chair Detection — Environment Scan")]
    public float chairMinHeight = 0.30f;
    public float chairMaxHeight = 0.70f;
    public float chairScanGridStep = 0.08f;
    public float chairScanClusterRadius = 0.35f;
    public int minHitsForChair = 4;
    public float chairScanForwardRange = 6.0f;
    public float chairScanSideRange = 4.0f;

    [Header("Desk Detection — Grid Scan")]
    public float deskMinHeight = 0.65f;
    public float deskMaxHeight = 0.95f;
    public float deskGridStep = 0.10f;
    public float deskClusterRadius = 0.30f;
    public int minHitsForDesk = 6;
    public bool spawnAtSeatSurface = false;

    [Header("Chair Orientation")]
    public bool flipChairForward = false;
    public bool flipMuradFacing = false;
    public Vector3 muradSeatOffset = new Vector3(0f, 0f, -0.1f);
    public float chairAnchorMatchRadius = 0.5f;
    public float chairBackrestProbeHeight = 0.75f;

    [Header("Audio")]
    public AudioSource lectureAudioSource;
    public AudioClip greetingAudioClip;
    [TextArea(3, 8)]
    public string greetingAudioTranscript;
    public AudioSource chairDetectionAudioSource;

    private readonly List<GameObject> _spawnedNPCs = new List<GameObject>();
    private Action _onLectureComplete;
    private EnvironmentRaycastManager _envRaycast;
    private GameObject _mainMuradInstance;
    private MuradSittingPoseDriver _sittingDriver;
    private List<StudentVariant> _shuffledStudentVariants;
    private int _variantCursor;
    private bool _lectureActive = false;

    private void Awake()
    {
        _envRaycast = FindAnyObjectByType<EnvironmentRaycastManager>();
    }

    //Fallback function if no chairs found
    public void StartLecture(ExperienceConfig config, Action onComplete)
    {
        if (config != null)
            currentConfig = config;

        Vector3 forward = GetPlayerFlatForward();
        Vector3 anchor = ComputeSceneAnchor(forward);

        Transform cam = Camera.main.transform;
        Vector3 camRight = new Vector3(cam.right.x, 0f, cam.right.z).normalized;
        Vector3 doctorPos = new Vector3(cam.position.x, 0f, cam.position.z) + camRight * doctorSideOffset;
        doctorPos.y = FindFloorY(doctorPos, cam.position.y);

        SpawnStudents(anchor, forward, config, doctorPos);
        SpawnDoctorAt(doctorPos, anchor, config);

        StartCoroutine(RunLectureSequence(config));
        Debug.Log("[LectureHall] Characters and doctor are spawned and lecture started");
    }

    public void StartLectureWithChairs(List<Vector3> chairPositions, ExperienceConfig config, Action onComplete)
    {
        if (_lectureActive)
        {
            Debug.LogWarning("[LectureHall] Lecture already started");
            return;
        }
        _lectureActive = true;

        if (config != null)
            currentConfig = config;

        if (chairPositions == null || chairPositions.Count == 0)
        {
            Debug.LogWarning("[LectureHall] No chairs found");
            StartLecture(config, onComplete);
            return;
        }

        Vector3 forward = GetPlayerFlatForward();
        Vector3 anchor = ComputeSceneAnchor(forward);

        Transform cam = Camera.main.transform;
        var frontChairs = FilterChairsInFront(chairPositions, cam.position, forward);
        if (frontChairs.Count == 0)
        {
            Debug.LogWarning("[LectureHall] No chairs in front of user");
            frontChairs = chairPositions;
        }

        Vector3 chairCentroid = Vector3.zero;
        int chairCount = frontChairs.Count;
        foreach (var p in frontChairs)
            chairCentroid += p;
        chairCentroid = chairCentroid / chairCount;

        Vector3 userXZ = new Vector3(cam.position.x, 0f, cam.position.z);
        Vector3 centroidXZ = new Vector3(chairCentroid.x, 0f, chairCentroid.z);
        Vector3 toChairsDir = (centroidXZ - userXZ);
        if (toChairsDir.sqrMagnitude < 0.001f)
            toChairsDir = new Vector3(forward.x, 0f, forward.z);
        toChairsDir.Normalize();

        Vector3 doctorPos;
        Vector3 studentFaceTarget;
        Vector3? screenPos = FindScreenFromMRUK();

        if (screenPos.HasValue)
        {
            Vector3 screenXZ = new Vector3(screenPos.Value.x, 0f, screenPos.Value.z);
            Vector3 screenToChairs = (centroidXZ - screenXZ);
            Vector3 screenToChairsDir = screenToChairs.sqrMagnitude > 0.001f ? screenToChairs.normalized : toChairsDir;

            Vector3 doctorXZ = screenXZ + screenToChairsDir * doctorInFrontOfScreenOffset;
            float doctorY = FindFloorY(doctorXZ, cam.position.y);
            doctorPos = new Vector3(doctorXZ.x, doctorY, doctorXZ.z);
            studentFaceTarget = doctorPos;

            Debug.Log($"[LectureHall] Screen found and doctor stand in front of screen");
        }
        else
        {
            Debug.Log($"[LectureHall] No screen found, try disk instead of it");
            float floorY = GetRoomFloorY();
            Vector3? deskAnchorPos = FindDeskByGridScan(chairCentroid, toChairsDir, floorY) ?? FindDeskFromMRUK(chairCentroid);

            if (deskAnchorPos.HasValue)
            {
                Vector3 deskXZ = new Vector3(deskAnchorPos.Value.x, 0f, deskAnchorPos.Value.z);
                Vector3 deskToChairs = (centroidXZ - deskXZ);
                Vector3 deskToChairsDir = deskToChairs.sqrMagnitude > 0.001f ? deskToChairs.normalized : -toChairsDir;

                Vector3 doctorXZ = deskXZ - deskToChairsDir * doctorBehindDeskOffset;
                float doctorY = FindFloorY(doctorXZ, cam.position.y);
                doctorPos = new Vector3(doctorXZ.x, doctorY, doctorXZ.z);
                studentFaceTarget = new Vector3(deskXZ.x, FindFloorY(deskXZ, cam.position.y) + 1.0f, deskXZ.z);

                Debug.Log($"[LectureHall] Desk found and doctor stands behind the desk");
            }
            else
            {
                Vector3 doctorXZ = centroidXZ + toChairsDir * doctorForwardOffset;
                float doctorY = FindFloorY(doctorXZ, cam.position.y);
                doctorPos = new Vector3(doctorXZ.x, doctorY, doctorXZ.z);
                studentFaceTarget = doctorPos;
                Debug.Log($"[LectureHall] No screen or desk found ");
            }
        }

        SpawnStudentsAtChairs(frontChairs, studentFaceTarget);
        Debug.Log("[LectureHall] Spawning students on chairs");
        SpawnDoctorAt(doctorPos, chairCentroid, config);
        StartCoroutine(RunLectureSequence(config));
    }

    public void ClearScene()
    {
        StopAllCoroutines();
        if (lectureAudioSource != null)
            lectureAudioSource.Stop();

        if (chairDetectionAudioSource != null)
            chairDetectionAudioSource.Stop();
        foreach (var npc in _spawnedNPCs)
        {
            if (npc == null)
                continue;
            var npcCtrl = npc.GetComponent<HistoricalNPCController>();
            if (npcCtrl == null || npcCtrl.Role != NPCRole.Doctor)
                continue;
            AudioSource docAudio = npc.GetComponent<AudioSource>();
            if (docAudio != null)
                docAudio.Stop();
        }

        foreach (var npc in _spawnedNPCs)
            if (npc != null) Destroy(npc);

        _spawnedNPCs.Clear();
        _shuffledStudentVariants = null;
        _variantCursor = 0;
        _lectureActive = false;
        _sittingDriver = null;
        Debug.Log("[LectureHall] Scene cleared.");
    }
}
