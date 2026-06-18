using System.Collections;
using UnityEngine;
using TMPro;

/// <summary>
/// Orchestrates the Obelisk AR experience.
/// Mirrors the LectureHallManager pattern so ExperienceManager can call it
/// the same way it calls LectureHallManager.
///
/// Setup in Inspector:
///  1. Create an empty GameObject in your scene named "ObeliskManager".
///  2. Add this component to it.
///  3. Also add ObeliskDetectionClient to it (or a child GameObject).
///  4. Assign all Inspector fields below.
///  5. Drag this GameObject into ExperienceManager → Obelisk Manager slot.
///
/// IMPORTANT: Set ObeliskYOLODetector.autoStart = false so detection
/// does not begin before Message 1 finishes playing.
/// </summary>
public class ObeliskManager : MonoBehaviour
{
    // ── Inspector references ──────────────────────────────────────────

    [Header("Detection")]
    [Tooltip("The ObeliskYOLODetector that runs on-device inference.")]
    public ObeliskYOLODetector detectionClient;

    [Header("Audio")]
    [Tooltip("AudioSource used for scanning and detected sounds.\n" +
             "Add an AudioSource component to this GameObject and drag it here.")]
    public AudioSource audioSource;

    [Header("UI")]
    [Tooltip("The world-space scanning UI panel (shown while looking for the obelisk).\n" +
             "Can be the same scanningUI used by ExperienceManager, or a separate one.")]
    public GameObject scanningPanel;

    [Tooltip("TextMeshPro text inside the scanning panel — shows guidance text from config.")]
    public TextMeshProUGUI guidanceText;

    [Tooltip("The world-space detected UI panel (shown once obelisk is confirmed).")]
    public GameObject detectedPanel;

    [Header("Guidance Panel")]
    [Tooltip("The styled dialog panel used for guidance messages (same prefab as Lecture Hall).")]
    public GameObject guidancePanel;
    public TextMeshProUGUI guidanceBodyText;
    public AudioSource guidanceAudioSource;
    public float guidanceDisplayDuration = 6f;

    [Header("Guidance Audio Clips")]
    public AudioClip guidanceObeliskScanClip;    // Message 1
    public AudioClip guidanceTimeMachineClip;    // Message 3
    public AudioClip guidanceHassanReadyClip;    // Message 4
    public AudioClip guidanceReadyToLeaveClip;   // Message 5

    [Header("Guidance Messages")]
    [TextArea(2, 4)]
    public string msgObeliskScan =
        "Point your gaze at the obelisk to activate the scene. " +
        "Hold it in the center of your view until the scan completes.";

    [TextArea(2, 4)]
    public string msgTimeMachine =
        "Walk through the Time Machine to travel back in time. " +
        "The story will begin automatically once you pass through.";

    [TextArea(2, 4)]
    public string msgHassanReady =
        "Hassan is ready to speak with you. Press the Right Trigger to start speaking. " +
        "Ask your question, then press the Right Trigger again to send it.";

    [TextArea(2, 4)]
    public string msgReadyToLeave =
        "Ready to leave? Press B to summon the Time Machine. " +
        "Walk through the portal to return to the present.";

    [Header("UI Placement")]
    public Transform uiDialogueRoot;
    public float uiDistanceFromUser = 1.5f;
    public float uiVerticalOffset = -0.1f;

    [Header("Hassan")]
    [Tooltip("Hassan's HassanApproach component — used to detect when he arrives " +
             "in front of the user so Message 4 can fire.")]
    public HassanApproach hassanApproach;

    [Tooltip("Hassan's VoiceAPIController — the 20-second inactivity timer " +
             "on this triggers Message 5.")]
    public VoiceAPIController hassanVoiceController;

    // ── Private state ─────────────────────────────────────────────────

    private ExperienceConfig _config;
    private System.Action _onComplete;
    private bool _started;
    private Coroutine _guidanceCoroutine;

    // ── Unity lifecycle ───────────────────────────────────────────────

    private void Awake()
    {
        // Hide the guidance panel immediately so it never shows Lorem-Ipsum
        // placeholder text before the script sets the real message.
        if (guidancePanel != null)
            guidancePanel.SetActive(false);
    }

    /// <summary>
    /// Mirrors PanelSceneManager.Start() — auto-starts the experience when
    /// the scene loads.  If you later wire this to ExperienceManager, remove
    /// this method and call StartExperience() from there instead.
    /// </summary>
    private void Start()
    {
        if (guidancePanel != null)
            guidancePanel.SetActive(false);

        StartExperience(null);
    }

    // ── Public API ────────────────────────────────────────────────────

    /// <summary>
    /// Called by ExperienceManager when the user picks the Obelisk experience.
    /// </summary>
    public void StartExperience(ExperienceConfig config, System.Action onComplete = null)
    {
        if (_started)
        {
            Debug.LogWarning("[ObeliskManager] Already running — call ClearScene() first.");
            return;
        }

        _config = config;
        _onComplete = onComplete;
        _started = true;

        if (guidancePanel != null)
            guidancePanel.SetActive(false);

        Debug.Log("[ObeliskManager] Starting obelisk experience.");

        StartCoroutine(RunExperience());
    }

    /// <summary>
    /// Stops detection and destroys all spawned characters.
    /// Call this from ExperienceManager.ReturnToMenu().
    /// </summary>
    public void ClearScene()
    {
        StopAllCoroutines();

        if (detectionClient != null)
            detectionClient.StopDetection();

        HideGuidance();
        StopAudio();
        SetScanningUI(false);
        SetDetectedUI(false);

        if (hassanApproach != null)
            hassanApproach.onArrived.RemoveListener(OnHassanArrived);

        if (hassanVoiceController != null)
        {
            hassanVoiceController.onInactivityTimeout = null;
            hassanVoiceController.enabled = false;
        }

        _started = false;
        _config = null;
        _onComplete = null;

        Debug.Log("[ObeliskManager] Scene cleared.");
    }

    // ── Internal flow ─────────────────────────────────────────────────

    private IEnumerator RunExperience()
    {
        // ── 1. Message 1: obelisk scan instruction — blocks detection ─
        if (guidancePanel != null && guidanceBodyText != null)
        {
            _guidanceCoroutine = StartCoroutine(GuidanceRoutine(msgObeliskScan, guidanceObeliskScanClip));
            yield return _guidanceCoroutine;
        }

        // ── 2. Show scanning UI ───────────────────────────────────────
        if (_config != null && guidanceText != null)
            guidanceText.text = _config.obeliskGuidanceText;

        SetScanningUI(true);
        SetDetectedUI(false);

        // ── 3. Play scanning audio (looping) ─────────────────────────
        if (_config != null && _config.obeliskScanningAudioClip != null)
            PlayAudio(_config.obeliskScanningAudioClip, loop: true);

        // ── 4. Start detection ────────────────────────────────────────
        if (detectionClient == null)
        {
            Debug.LogError("[ObeliskManager] detectionClient not assigned! " +
                           "Drag ObeliskDetectionClient here in the Inspector.");
            yield break;
        }

        detectionClient.OnObeliskConfirmed = OnObeliskConfirmed;
        detectionClient.StartDetection();

        yield break;
    }

    /// <summary>
    /// Called by ObeliskDetectionClient when the obelisk is confirmed detected
    /// and characters have been spawned.
    /// </summary>
    private void OnObeliskConfirmed()
    {
        Debug.Log("[ObeliskManager] Obelisk confirmed — updating UI and playing detected audio.");

        StopAudio();
        SetScanningUI(false);
        SetDetectedUI(true);

        if (_config != null && _config.obeliskDetectedAudioClip != null)
            PlayAudio(_config.obeliskDetectedAudioClip, loop: false);

        // Message 3: instruct user to walk through the time machine
        if (guidancePanel != null && guidanceBodyText != null)
            StartCoroutine(ShowGuidanceAndWait(msgTimeMachine, guidanceTimeMachineClip));

        // Subscribe to Hassan's onArrived so Message 4 fires when he reaches the user
        if (hassanApproach != null)
            hassanApproach.onArrived.AddListener(OnHassanArrived);

        _onComplete?.Invoke();
    }

    private IEnumerator ShowGuidanceAndWait(string message, AudioClip clip)
    {
        _guidanceCoroutine = StartCoroutine(GuidanceRoutine(message, clip));
        yield return _guidanceCoroutine;
    }

    /// <summary>
    /// Fired by HassanApproach.onArrived once Hassan walks up to the user.
    /// Shows Message 4 and starts the 20-second inactivity timer for Message 5.
    /// </summary>
    private void OnHassanArrived()
    {
        if (hassanApproach != null)
            hassanApproach.onArrived.RemoveListener(OnHassanArrived);

        HideGuidance();
        StartCoroutine(ShowGuidanceAndWait(msgHassanReady, guidanceHassanReadyClip));

        if (hassanVoiceController != null)
        {
            hassanVoiceController.onInactivityTimeout = OnHassanInactivityTimeout;
            hassanVoiceController.enabled = true;
            Debug.Log("[ObeliskManager] Hassan voice controller enabled with inactivity timer.");
        }
    }

    /// <summary>
    /// Fires when Hassan's voice controller has been inactive for 20 seconds.
    /// Shows Message 5 (ready to leave).
    /// </summary>
    private void OnHassanInactivityTimeout()
    {
        HideGuidance();
        if (guidancePanel != null && guidanceBodyText != null)
            StartCoroutine(ShowGuidanceAndWait(msgReadyToLeave, guidanceReadyToLeaveClip));
    }

    // ── UI helpers ────────────────────────────────────────────────────

    private void SetScanningUI(bool visible)
    {
        if (scanningPanel != null)
            scanningPanel.SetActive(visible);
    }

    private void SetDetectedUI(bool visible)
    {
        if (detectedPanel != null)
            detectedPanel.SetActive(visible);
    }

    // ── Audio helpers ─────────────────────────────────────────────────

    private void PlayAudio(AudioClip clip, bool loop)
    {
        if (audioSource == null || clip == null) return;
        audioSource.Stop();
        audioSource.clip = clip;
        audioSource.loop = loop;
        audioSource.Play();
    }

    private void StopAudio()
    {
        if (audioSource != null)
            audioSource.Stop();
    }

    // ── Guidance helpers ──────────────────────────────────────────────

    private void HideGuidance()
    {
        if (_guidanceCoroutine != null)
        {
            StopCoroutine(_guidanceCoroutine);
            _guidanceCoroutine = null;
        }
        if (guidanceAudioSource != null) guidanceAudioSource.Stop();
        if (guidancePanel != null) guidancePanel.SetActive(false);
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
}
