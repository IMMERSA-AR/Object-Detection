using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;

public class PanelSceneManager : MonoBehaviour
{

    [Header("Detection")]
    public PanelDetector panelDetector;

    [Header("Audio")]
    public AudioSource audioSource;
    public AudioClip introAudioClip;
    public AudioClip scanningAudioClip;
    public AudioClip detectedAudioClip;

    [Header("Rescan")]
    public GameObject rescanButton;
    public float autoRescanDelay = 2f;

    [Header("Detection Limits")]
    public int maxDetectedPanels = 3;

    [Header("Guidance Panel")]
    public GameObject guidancePanel;
    public TextMeshProUGUI guidanceBodyText;
    public AudioSource guidanceAudioSource;
    public AudioClip guidanceScanClip;
    public AudioClip guidanceDetectedClip;
    public AudioClip guidanceNextPanelClip;
    public float guidanceDisplayDuration = 6f;

    [Header("UI Placement")]
    public Transform uiDialogueRoot;
    public float uiDistanceFromUser = 1.5f;
    public float uiVerticalOffset = -0.1f;
    [Tooltip("How fast the UI follows the user while visible. 0 = fixed, 2 = gentle lag, 8 = near-instant.")]
    public float uiFollowSpeed = 2f;

    [Header("Repeat Dialog ")]
    public RepeatDialog repeatDialog;
    [TextArea(2, 4)]
    public string repeatStoryQuestion = "The story has ended. Do you want to hear it again?";
    public AudioClip repeatStoryClip;
    [TextArea(2, 4)]
    public string restartExperienceQuestion = "Do you want to restart the whole experience?";
    public AudioClip restartExperienceClip;
    public AudioClip thankYouClip;

    private bool _narrationDone;
    private int _lastConfirmedClass = -1;
    private string _lastConfirmedName = "";
    private Coroutine _guidanceCoroutine;
    private bool _introPlaying;
    private bool _narrationPlaying;
    private bool _dialogOpen;
    private Coroutine _flowCoroutine;
    private readonly List<GameObject> _spawnedCharacters = new List<GameObject>();

    private void Awake()
    {
        if (panelDetector == null)
            panelDetector = GetComponentInChildren<PanelDetector>();

        if (panelDetector == null)
        {
            Debug.LogError("[PanelSceneManager] PanelDetector not found");
            return;
        }

        panelDetector.OnPanelConfirmed = OnPanelDetected;
        panelDetector.OnNarrationStarted = OnNarrationStarted;
        panelDetector.OnNarrationFinished = OnNarrationDone;
        panelDetector.OnScanCompletedNoResult = OnScanFoundNothing;

        // Gate: hold panel narration until the scene intro clip finishes.
        panelDetector.IsReadyForNarration = () => !_introPlaying;
    }

    private void Start()
    {
        SetRescanButton(false);
        if (guidancePanel != null) 
            guidancePanel.SetActive(false);
        if (repeatDialog != null) 
            repeatDialog.Hide();

        StartCoroutine(IntroThenFirstScan());
    }

    private void Update()
    {
        if (uiDialogueRoot == null || Camera.main == null) return;

        bool anyVisible = (guidancePanel != null && guidancePanel.activeSelf) || _dialogOpen;
        if (!anyVisible) return;

        Transform cam = Camera.main.transform;
        Vector3 fwd = cam.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
        fwd.Normalize();

        Vector3 targetPos = cam.position + fwd * uiDistanceFromUser + Vector3.up * uiVerticalOffset;
        Quaternion targetRot = Quaternion.LookRotation(targetPos - cam.position, Vector3.up);

        uiDialogueRoot.position = Vector3.Lerp(uiDialogueRoot.position, targetPos, Time.deltaTime * uiFollowSpeed);
        uiDialogueRoot.rotation = Quaternion.Slerp(uiDialogueRoot.rotation, targetRot, Time.deltaTime * uiFollowSpeed);
    }

    public void RescanButtonPressed()
    {
        Debug.Log("[PanelSceneManager] Rescan requested.");
        ClearSpawnedCharacters();
        panelDetector?.ResetConfirmedPanels();
        StartScanSequence(playNextPanelFirst: false);
    }

    private IEnumerator IntroThenFirstScan()
    {
        if (introAudioClip != null)
        {
            _introPlaying = true;
            PlayAudio(introAudioClip, loop: false);
            yield return new WaitForSeconds(introAudioClip.length);
            _introPlaying = false;
        }
        StartScanSequence(playNextPanelFirst: false);
    }

    /// <summary>
    /// Runs the audio/text lead-in, then starts a fresh scan.
    ///   playNextPanelFirst = true  → "move to another panel" voice first, then scan guidance.
    ///   playNextPanelFirst = false → straight to the scan guidance (first scan / restart).
    /// </summary>
    private void StartScanSequence(bool playNextPanelFirst)
    {
        if (_flowCoroutine != null) StopCoroutine(_flowCoroutine);
        _flowCoroutine = StartCoroutine(ScanSequence(playNextPanelFirst));
    }

    private IEnumerator ScanSequence(bool playNextPanelFirst)
    {
        // 1. (Optional) "move to another panel" instruction.
        if (playNextPanelFirst)
        {
            ShowGuidance(
                "Move to another exhibition panel to continue your exploration.",
                guidanceNextPanelClip);
            yield return WaitGuidance(guidanceNextPanelClip);
        }

        // 2. Scan guidance ("aim your device at a panel").
        ShowGuidance(
            "Direct your device toward one of the exhibition panels and hold steady.\n" +
            "Panel detection will begin automatically.",
            guidanceScanClip);
        yield return WaitGuidance(guidanceScanClip);
        HideGuidance();

        // 3. Begin scanning (with optional ambient loop).
        if (scanningAudioClip != null)
            PlayAudio(scanningAudioClip, loop: true);

        _flowCoroutine = null;
        StartFreshScan();
    }

    /// <summary>Waits for a guidance clip to finish, or guidanceDisplayDuration if none.</summary>
    private IEnumerator WaitGuidance(AudioClip clip)
    {
        if (clip != null) yield return new WaitForSeconds(clip.length + 0.3f);
        else yield return new WaitForSeconds(guidanceDisplayDuration);
    }

    /// <summary>Starts a fresh scan pass (already-confirmed panels stay excluded).</summary>
    private void StartFreshScan()
    {
        if (panelDetector == null) return;
        if (panelDetector.IsScanning) return;
        if (panelDetector.ConfirmedCount >= maxDetectedPanels) return;
        panelDetector.ResetDetection();
    }

    /// <summary>Called by PanelDetector.OnPanelConfirmed.</summary>
    private void OnPanelDetected(int classId, string panelName)
    {
        _lastConfirmedClass = classId;
        _lastConfirmedName = panelName;

        Debug.Log($"[PanelSceneManager] Panel confirmed: [{classId}] '{panelName}'");

        // Detection has stopped (the scan coroutine ends on confirm). We do NOT
        // auto-resume — the user must click the character to hear the story.
        // Stop the looping scanning ambience.
        if (audioSource != null && audioSource.isPlaying && audioSource.loop)
            audioSource.Stop();

        // "Panel recognized — point at the character" guidance + voice (auto-hides).
        ShowGuidance(
            "Panel recognized.\n" +
            "Point your controller at the character and pull the trigger to begin the story.",
            guidanceDetectedClip);
    }

    /// <summary>Called by PanelDetector.OnNarrationStarted — pause detection while audio plays.</summary>
    private void OnNarrationStarted()
    {
        _narrationPlaying = true;
        Debug.Log("[PanelSceneManager] Narration started — pausing detection and stopping scan audio.");
        panelDetector?.StopDetection();

        // Stop the looping scanning ambience so it doesn't play UNDER the story
        // (both on first play and on replay). Never cut a non-looping clip (e.g. intro).
        if (audioSource != null && audioSource.isPlaying && audioSource.loop)
            audioSource.Stop();
    }

    /// <summary>Called by PanelDetector.OnNarrationFinished.</summary>
    private void OnNarrationDone()
    {
        _narrationPlaying = false;
        _narrationDone = true;
        Debug.Log("[PanelSceneManager] Narration finished.");

        // Offer to repeat THIS panel's story. Detection stays paused until the
        // user answers (handled by ReplayStory / DeclineRepeat).
        if (repeatDialog != null)
        {
            HideGuidance();
            _dialogOpen = true;
            PositionUIInFrontOfUser();   // snap before showing so dialog appears right in front
            EnsureCanvasOnTop(repeatDialog.GetComponentInParent<Canvas>());
            PlayGuidanceVoice(repeatStoryClip);
            // noButton is the one the user physically aims at as "YES" — callbacks swapped to match
            repeatDialog.Show(repeatStoryQuestion, onYes: DeclineRepeat, onNo: ReplayStory);
        }
        else
        {
            // No dialog wired — fall back to the old behaviour.
            DeclineRepeat();
        }
    }

    /// <summary>"Repeat → Yes": replay the same story. The repeat dialog reappears
    /// automatically when the replay finishes (via OnNarrationDone again).</summary>
    private void ReplayStory()
    {
        _dialogOpen = false;
        Debug.Log("[PanelSceneManager] Repeat requested — replaying the story.");
        if (panelDetector == null || !panelDetector.ReplayLastNarration())
            DeclineRepeat();   // nothing to replay → behave as if user declined
    }

    /// <summary>"Repeat → No": either move on to the next panel, or—if every panel
    /// has been explored—offer to restart the whole experience.</summary>
    private void DeclineRepeat()
    {
        _dialogOpen = false;
        Debug.Log("[PanelSceneManager] Repeat declined — moving on.");

        // If every panel is done, offer to restart the whole experience.
        if (panelDetector != null && panelDetector.ConfirmedCount >= maxDetectedPanels)
        {
            OfferRestartExperience();
            return;
        }

        // Otherwise: "move to another panel" voice → scan guidance voice → scan.
        StartScanSequence(playNextPanelFirst: true);
    }

    /// <summary>After all panels are done, ask whether to restart everything.</summary>
    private void OfferRestartExperience()
    {
        if (repeatDialog != null)
        {
            HideGuidance();
            _dialogOpen = true;
            PositionUIInFrontOfUser();   // snap before showing so dialog appears right in front
            EnsureCanvasOnTop(repeatDialog.GetComponentInParent<Canvas>());
            PlayGuidanceVoice(restartExperienceClip);
            repeatDialog.Show(restartExperienceQuestion, onYes: RestartExperience, onNo: EndExperience);
        }
        else
        {
            EndExperience();
        }
    }

    /// <summary>"Restart → Yes": destroy all characters, clear history, scan from scratch.</summary>
    private void RestartExperience()
    {
        _dialogOpen = false;
        Debug.Log("[PanelSceneManager] Restarting the whole experience.");
        panelDetector?.ClearAllSpawnedCharacters();   // destroy ALL characters
        ClearSpawnedCharacters();                     // also clear manager-tracked list
        panelDetector?.ResetConfirmedPanels();        // all panels eligible again

        // Straight back into the scan sequence (scan guidance → scan).
        StartScanSequence(playNextPanelFirst: false);
    }

    /// <summary>"Restart → No": end the experience. Characters remain interactive,
    /// so the user can still point at any of them to replay its story.</summary>
    private void EndExperience()
    {
        _dialogOpen = false;
        Debug.Log("[PanelSceneManager] Experience ended by user.");
        ShowGuidance("Thank you for visiting the exhibition.", thankYouClip);
    }

    /// <summary>Called by PanelDetector.OnScanCompletedNoResult — a scan pass found
    /// nothing. Keep scanning (silently, ambience already playing) until a panel
    /// appears, unless we're paused for narration / a dialog / the panel limit.</summary>
    private void OnScanFoundNothing()
    {
        if (panelDetector == null) return;
        if (panelDetector.ConfirmedCount >= maxDetectedPanels) return;
        if (_narrationPlaying || _dialogOpen) return;
        if (panelDetector.IsScanning) return;

        // Re-scan immediately — do NOT replay the scan-guidance voice each pass.
        panelDetector.ResetDetection();
    }

    // ── Character tracking ─────────────────────────────────────────────

    private void ClearSpawnedCharacters()
    {
        // PanelDetector owns the spawn; destroy the most recent one before rescanning.
        if (panelDetector != null && panelDetector.LastSpawnedCharacter != null)
            Destroy(panelDetector.LastSpawnedCharacter);

        foreach (var go in _spawnedCharacters)
            if (go != null) Destroy(go);
        _spawnedCharacters.Clear();
    }

    // ── UI helpers ─────────────────────────────────────────────────────

    private void SetRescanButton(bool visible)
    {
        if (rescanButton != null)
            rescanButton.SetActive(visible);
    }

    // ── Audio helpers ──────────────────────────────────────────────────

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

    // ── Guidance panel helpers ─────────────────────────────────────────

    /// <summary>
    /// Shows the guidance panel with <paramref name="message"/>, plays the optional
    /// <paramref name="clip"/> through guidanceAudioSource, then hides the panel once
    /// the clip finishes (or after guidanceDisplayDuration seconds when no clip is set).
    /// Any previously queued guidance is cancelled first so messages never stack.
    /// </summary>
    private void ShowGuidance(string message, AudioClip clip)
    {
        if (guidancePanel == null)
        {
            Debug.LogWarning("[PanelSceneManager] ShowGuidance: 'Guidance Panel' is NOT assigned in the " +
                             "Inspector — nothing will appear. Drag the guidance dialog GameObject here.");
            return;
        }
        if (guidanceBodyText == null)
        {
            Debug.LogWarning("[PanelSceneManager] ShowGuidance: 'Guidance Body Text' is NOT assigned in the " +
                             "Inspector — nothing will appear. Drag the BodyText (TextMeshProUGUI) here.");
            return;
        }

        Debug.Log($"[PanelSceneManager] ShowGuidance → \"{message.Replace("\n", " ")}\"  " +
                  $"(panel '{guidancePanel.name}', clip={(clip != null ? clip.name : "none")})");

        if (_guidanceCoroutine != null)
            StopCoroutine(_guidanceCoroutine);

        _guidanceCoroutine = StartCoroutine(GuidanceRoutine(message, clip));
    }

    /// <summary>Moves the whole dialog UI in front of the user and faces it toward them.</summary>
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
        // Face the user: the canvas front (+Z) points away from the camera so text is readable.
        uiDialogueRoot.rotation = Quaternion.LookRotation(uiDialogueRoot.position - cam.position, Vector3.up);
    }

    /// <summary>Plays a one-shot voice-over through the guidance AudioSource (no-op if null).</summary>
    private void PlayGuidanceVoice(AudioClip clip)
    {
        if (guidanceAudioSource == null || clip == null) return;
        guidanceAudioSource.Stop();
        guidanceAudioSource.PlayOneShot(clip);
    }

    /// <summary>Immediately hides the guidance panel (used before showing the repeat dialog).</summary>
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

    private IEnumerator GuidanceRoutine(string message, AudioClip clip)
    {
        PositionUIInFrontOfUser();   // snap immediately so Update's lerp starts from the right place
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

    private void EnsureCanvasOnTop(Canvas canvas)
    {
        if (canvas == null) return;
        canvas.overrideSorting = true;
        canvas.sortingOrder = 999;
    }
}
