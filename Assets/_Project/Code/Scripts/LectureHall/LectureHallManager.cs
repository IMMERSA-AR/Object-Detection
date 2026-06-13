using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;
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

    [Header("Guidance Panel")]
    public GameObject guidancePanel;
    public TextMeshProUGUI guidanceBodyText;
    public AudioSource guidanceAudioSource;
    public float guidanceDisplayDuration = 6f;

    [Header("Guidance Audio Clips")]
    public AudioClip guidanceChairScanClip;
    public AudioClip guidanceLectureEndClip;
    public AudioClip guidanceQAClip;
    public AudioClip guidanceRestartClip;
    public AudioClip guidanceThankYouClip;

    [Header("Guidance Messages")]
    [TextArea(2, 4)]
    public string msgChairScan = "Look around the room at the chairs and hold steady. The chairs will be detected automatically, and the students will take their seats.";
    [TextArea(2, 4)]
    public string msgLectureEnd = "The lecture has ended. Would you like to experience it again?";
    [TextArea(2, 4)]
    public string msgQA = "Aim your controller at Murad and press button A to begin the Q&A session.";
    [TextArea(2, 4)]
    public string msgRestart = "Would you like to restart the entire experience from the beginning?";
    [TextArea(2, 4)]
    public string msgThankYou = "Thank you for attending the lecture.";

    [Header("Guidance Dialog")]
    public RepeatDialog repeatDialog;

    [Header("UI Placement")]
    public Transform uiDialogueRoot;
    public float uiDistanceFromUser = 1.5f;
    public float uiVerticalOffset = -0.1f;

    [Header("Audio")]
    public AudioSource lectureAudioSource;
    public AudioClip greetingAudioClip;
    [TextArea(3, 8)]
    public string greetingAudioTranscript;
    public AudioSource chairDetectionAudioSource;

    private Coroutine _guidanceCoroutine;

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

        if (guidancePanel != null)
            guidancePanel.SetActive(false);
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

    // ── Guidance helpers (mirrored from PanelSceneManager) ────────────────

    public IEnumerator ShowChairScanGuidanceAndWait()
    {
        _guidanceCoroutine = StartCoroutine(GuidanceRoutine(msgChairScan, guidanceChairScanClip));
        yield return _guidanceCoroutine;
    }

    public void OnQAComplete()
    {
        HideGuidance();
        if (repeatDialog != null)
        {
            // Murad is standing in front — raise dialog above his head
            // YES = restart everything from scratch, NO = end with thank-you
            ShowRepeatDialog(msgRestart, guidanceRestartClip,
                             onYes: RestartExperience, onNo: EndExperience,
                             raiseAboveCharacter: true);
        }
        else
        {
            EndExperience();
        }
    }

    internal void ShowRepeatDialog(string message, AudioClip clip, System.Action onYes, System.Action onNo,
                                   bool raiseAboveCharacter = false)
    {
        HideGuidance();
        if (raiseAboveCharacter)
            PositionUIAboveCharacter();
        else
            PositionUIInFrontOfUser();
        EnsureRepeatDialogParentsActive();
        PlayGuidanceVoice(clip);
        repeatDialog.Show(message, onYes: onYes, onNo: onNo);
    }

    // Positions the UI beside Murad (to his left from the user's perspective)
    internal void PositionUIAboveCharacter()
    {
        if (uiDialogueRoot == null) return;
        Transform cam = Camera.main != null ? Camera.main.transform : null;
        if (cam == null) return;

        Vector3 fwd = cam.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
        fwd.Normalize();

        // Place dialog beside Murad: same forward distance, offset to the right
        Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
        uiDialogueRoot.position = cam.position
                                 + fwd   * uiDistanceFromUser
                                 + right * 0.6f
                                 + Vector3.up * uiVerticalOffset;
        uiDialogueRoot.rotation = Quaternion.LookRotation(uiDialogueRoot.position - cam.position, Vector3.up);
    }

    private void EnsureRepeatDialogParentsActive()
    {
        if (repeatDialog == null) return;
        Transform t = repeatDialog.transform.parent;
        while (t != null)
        {
            if (!t.gameObject.activeSelf)
                t.gameObject.SetActive(true);
            t = t.parent;
        }

        // Force the dialog canvas to render in front of all scene geometry
        Canvas dialogCanvas = repeatDialog.GetComponentInParent<Canvas>();
        if (dialogCanvas != null)
        {
            dialogCanvas.overrideSorting = true;
            dialogCanvas.sortingOrder = 999;
        }
    }

    internal void ShowGuidance(string message, AudioClip clip)
    {
        if (guidancePanel == null)
        {
            Debug.LogWarning("[LectureHallManager] ShowGuidance: guidancePanel not assigned in Inspector.");
            return;
        }
        if (guidanceBodyText == null)
        {
            Debug.LogWarning("[LectureHallManager] ShowGuidance: guidanceBodyText not assigned in Inspector.");
            return;
        }

        if (_guidanceCoroutine != null)
            StopCoroutine(_guidanceCoroutine);

        _guidanceCoroutine = StartCoroutine(GuidanceRoutine(message, clip));
    }

    internal void HideGuidance()
    {
        if (_guidanceCoroutine != null)
        {
            StopCoroutine(_guidanceCoroutine);
            _guidanceCoroutine = null;
        }
        if (guidanceAudioSource != null) guidanceAudioSource.Stop();
        if (guidancePanel != null) guidancePanel.SetActive(false);
    }

    internal void PlayGuidanceVoice(AudioClip clip)
    {
        if (guidanceAudioSource == null || clip == null) return;
        guidanceAudioSource.Stop();
        guidanceAudioSource.PlayOneShot(clip);
    }

    private void PositionUIInFrontOfUser()
    {
        if (uiDialogueRoot == null) return;
        Transform cam = Camera.main != null ? Camera.main.transform : null;
        if (cam == null) return;

        Vector3 fwd = cam.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
        fwd.Normalize();

        uiDialogueRoot.position = cam.position + fwd * uiDistanceFromUser + Vector3.up * uiVerticalOffset;
        uiDialogueRoot.rotation = Quaternion.LookRotation(uiDialogueRoot.position - cam.position, Vector3.up);
    }

    private IEnumerator GuidanceRoutine(string message, AudioClip clip)
    {
        PositionUIInFrontOfUser();
        guidanceBodyText.text = message;
        guidancePanel.SetActive(true);

        // Render guidance panel on top of all scene geometry
        Canvas guidanceCanvas = guidancePanel.GetComponentInParent<Canvas>();
        if (guidanceCanvas != null)
        {
            guidanceCanvas.overrideSorting = true;
            guidanceCanvas.sortingOrder = 999;
        }

        if (guidanceAudioSource != null && clip != null)
        {
            guidanceAudioSource.Stop();
            guidanceAudioSource.PlayOneShot(clip);
            yield return new WaitForSeconds(clip.length + 0.5f);
        }
        else
        {
            yield return new WaitForSeconds(guidanceDisplayDuration);
        }

        guidancePanel.SetActive(false);
        _guidanceCoroutine = null;
    }

    private void RestartExperience()
    {
        Debug.Log("[LectureHallManager] Restarting the whole experience.");
        ClearScene();
        if (ExperienceManager.Instance != null)
            ExperienceManager.Instance.ReturnToMenu();
    }

    private void EndExperience()
    {
        Debug.Log("[LectureHallManager] Experience ended.");
        ShowGuidance(msgThankYou, guidanceThankYouClip);
    }

    public void ClearScene()
    {
        StopAllCoroutines();

        // Hide the repeat dialog so its Update loop can't fire callbacks after clear
        if (repeatDialog != null)
            repeatDialog.Hide();

        HideGuidance();

        // Disable Murad's voice controller before destroying to stop any pending callbacks
        if (_mainMuradInstance != null)
        {
            var qaVoice = _mainMuradInstance.GetComponent<VoiceAPIController>();
            if (qaVoice != null)
            {
                qaVoice.onInactivityTimeout = null;
                qaVoice.enabled = false;
            }
        }

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
        _mainMuradInstance = null;
        _shuffledStudentVariants = null;
        _variantCursor = 0;
        _lectureActive = false;
        _sittingDriver = null;
        Debug.Log("[LectureHall] Scene cleared.");
    }
}
