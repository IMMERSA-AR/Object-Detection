using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;

/// <summary>
/// Orchestrates the Museum Panel AR experience.
/// Handles the scanning → confirmed → narration → rescan lifecycle and
/// manages all UI/audio feedback.
///
/// ── Scene Setup (do this once in Unity) ────────────────────────────────
///
///  HIERARCHY:
///  ├─ PanelSceneManager         (this component + 2× AudioSource)
///  │   └─ PanelDetector         (PanelDetector component — fill its Inspector fields)
///  ├─ OVRCameraRig              (standard Meta camera rig)
///  ├─ MRUK                      (MR Utility Kit)
///  └─ GuidancePanel  [Canvas]   (world-space dialog, e.g. ContentRoot)
///      └─ BodyText              (TextMeshProUGUI — staged guidance messages)
///
///  INSPECTOR WIRING:
///   Panel Detector        → drag PanelDetector child GameObject here
///   Audio Source          → main AudioSource on THIS GameObject (intro/scanning/detected)
///   Intro/Scanning/Detected Clip → optional audio
///   Guidance Panel        → ContentRoot (the floating dialog root)
///   Guidance Body Text    → BodyText inside the dialog
///   Guidance Audio Source → a SECOND AudioSource (guidance voice-overs only)
///   Guidance Scan/Detected/Next Panel Clip → the three staged voice-overs
/// </summary>
public class PanelSceneManager : MonoBehaviour
{
    // ── Inspector ──────────────────────────────────────────────────────

    [Header("Detection")]
    [Tooltip("The PanelDetector component (usually on a child GameObject).")]
    public PanelDetector panelDetector;

    [Header("Audio")]
    [Tooltip("AudioSource on this GameObject — used for the intro, scanning and detected sounds.")]
    public AudioSource audioSource;

    [Tooltip("Intro narration played ONCE the moment the scene starts, regardless of detection.\n" +
             "Assign Assets/_Project/Audio/Panels Audio/intro.mp3 here.\n" +
             "Detection runs in parallel and never interrupts this clip.")]
    public AudioClip introAudioClip;

    [Tooltip("Loops while scanning for a panel (used on rescans). Leave empty for silence.")]
    public AudioClip scanningAudioClip;

    [Tooltip("Plays once when a panel is confirmed — only if the intro has already finished.\n" +
             "Leave empty for silence.")]
    public AudioClip detectedAudioClip;

    [Header("Rescan")]
    [Tooltip("Optional rescan button — manually restarts the full experience (destroys all characters).\n" +
             "Wire its OnClick → PanelSceneManager.RescanButtonPressed().")]
    public GameObject rescanButton;

    [Tooltip("Seconds to wait after narration ends before automatically scanning for the next panel.\n" +
             "Spawned characters stay in the world. Default: 2 seconds.")]
    public float autoRescanDelay = 2f;

    [Header("Detection Limits")]
    [Tooltip("Maximum number of DIFFERENT panels that can be detected and given a character.\n" +
             "Once this many panels have spawned their characters, detection stops permanently.\n" +
             "Default: 3.")]
    public int maxPanels = 3;

    [Header("Guidance Panel")]
    [Tooltip("Root GameObject of the guidance dialog (e.g. ContentRoot). " +
             "It is shown/hidden automatically at each stage.")]
    public GameObject guidancePanel;

    [Tooltip("The BodyText TextMeshProUGUI inside the guidance dialog. " +
             "Drag Dialog1Button_TextOnly → BodyText here.")]
    public TextMeshProUGUI guidanceBodyText;

    [Tooltip("Separate AudioSource used exclusively for guidance voice-overs " +
             "so they never conflict with scanning/detected sounds.")]
    public AudioSource guidanceAudioSource;

    [Tooltip("Guidance clip played after the intro finishes (scan instruction).")]
    public AudioClip guidanceScanClip;

    [Tooltip("Guidance clip played once a panel is confirmed (interaction instruction).")]
    public AudioClip guidanceDetectedClip;

    [Tooltip("Guidance clip played after narration ends (next-panel instruction).")]
    public AudioClip guidanceNextPanelClip;

    [Tooltip("Seconds the guidance panel stays visible before fading out " +
             "(ignored when an audio clip is supplied — panel stays until the clip ends).")]
    public float guidanceDisplayDuration = 6f;

    [Header("UI Placement")]
    [Tooltip("Root transform of the whole dialog UI (e.g. ContentRoot). When a guidance\n" +
             "message or repeat dialog is shown, this is moved in front of the user and\n" +
             "rotated to face them. Leave empty to keep the UI at its fixed scene position.")]
    public Transform uiRoot;

    [Tooltip("Distance in metres the UI is placed in front of the user when shown.")]
    public float uiDistance = 1.5f;

    [Tooltip("Vertical offset from eye level for the UI (negative = lower).")]
    public float uiVerticalOffset = -0.1f;

    [Header("Repeat Dialog (Yes/No)")]
    [Tooltip("The reusable two-button confirmation dialog (RepeatDialog component).\n" +
             "Used both for 'Repeat this story?' after each panel and for\n" +
             "'Restart the whole experience?' after all panels are done.\n" +
             "Leave empty to disable repeat prompts entirely.")]
    public RepeatDialog repeatDialog;

    [Tooltip("Question shown after a story ends (per-panel repeat).")]
    [TextArea(2, 4)]
    public string repeatStoryQuestion =
        "The story has ended. Do you want to hear it again?";

    [Tooltip("Voice-over for the 'repeat this story?' question. Optional.")]
    public AudioClip repeatStoryClip;

    [Tooltip("Question shown after every panel has been explored (whole-experience restart).")]
    [TextArea(2, 4)]
    public string restartExperienceQuestion =
        "Do you want to restart the whole experience?";

    [Tooltip("Voice-over for the 'restart the whole experience?' question. Optional.")]
    public AudioClip restartExperienceClip;

    [Tooltip("Voice-over for the final 'thank you for visiting' message. Optional.")]
    public AudioClip thankYouClip;

    // ── Private state ──────────────────────────────────────────────────

    private bool _narrationDone;
    private int _lastConfirmedClass = -1;
    private string _lastConfirmedName = "";
    private Coroutine _guidanceCoroutine;

    /// <summary>True while the scene intro clip is still playing.</summary>
    private bool _introPlaying;

    /// <summary>True while a character's narration audio is playing (detection paused).</summary>
    private bool _narrationPlaying;

    /// <summary>True while a Yes/No repeat dialog is open (detection paused).</summary>
    private bool _dialogOpen;

    /// <summary>The running audio/text lead-in coroutine before a scan (so we can cancel it).</summary>
    private Coroutine _flowCoroutine;

    // Track spawned characters so we can destroy them before the next scan
    private readonly List<GameObject> _spawnedCharacters = new List<GameObject>();

    // ── Unity lifecycle ────────────────────────────────────────────────

    private void Awake()
    {
        if (panelDetector == null)
            panelDetector = GetComponentInChildren<PanelDetector>();

        if (panelDetector == null)
        {
            Debug.LogError("[PanelSceneManager] PanelDetector not found! " +
                           "Add a child GameObject with PanelDetector and assign it here.");
            return;
        }

        // Wire callbacks
        panelDetector.OnPanelConfirmed = OnPanelDetected;
        panelDetector.OnNarrationStarted = OnNarrationStarted;
        panelDetector.OnNarrationFinished = OnNarrationDone;
        panelDetector.OnScanCompletedNoResult = OnScanFoundNothing;

        // Gate: hold panel narration until the scene intro clip finishes.
        // Returns true immediately if no intro is configured.
        panelDetector.IsReadyForNarration = () => !_introPlaying;
    }

    private void Start()
    {
        SetRescanButton(false);

        // Hide both dialogs at startup so nothing (e.g. the placeholder text) shows
        // during the intro. The first real guidance appears only after the intro ends.
        if (guidancePanel != null) guidancePanel.SetActive(false);
        if (repeatDialog != null) repeatDialog.Hide();

        StartCoroutine(IntroThenFirstScan());
    }

    // ── Public API (called by buttons or external scripts) ─────────────

    /// <summary>Destroy all spawned characters and restart the whole experience.</summary>
    public void RescanButtonPressed()
    {
        Debug.Log("[PanelSceneManager] Rescan requested.");
        ClearSpawnedCharacters();
        panelDetector?.ResetConfirmedPanels();
        StartScanSequence(playNextPanelFirst: false);
    }

    // ── Internal flow ──────────────────────────────────────────────────
    //
    // Sequential, one panel at a time:
    //   intro → [scan-guidance audio+text → SCAN] → panel found (box shown) →
    //   detected audio+text → user clicks character → story →
    //   "repeat?" dialog → Yes: replay story / No: "next panel" audio →
    //   scan-guidance audio → SCAN again …  After maxPanels: "restart?" dialog.

    /// <summary>Plays the intro once (if set), then runs the first scan sequence.</summary>
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
        if (panelDetector.ConfirmedCount >= maxPanels) return;
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
            PlayGuidanceVoice(repeatStoryClip);
            repeatDialog.Show(repeatStoryQuestion, onYes: ReplayStory, onNo: DeclineRepeat);
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
        if (panelDetector != null && panelDetector.ConfirmedCount >= maxPanels)
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
        if (panelDetector.ConfirmedCount >= maxPanels) return;
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
        if (uiRoot == null) return;
        Transform cam = Camera.main != null ? Camera.main.transform : null;
        if (cam == null) return;

        Vector3 fwd = cam.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
        fwd.Normalize();

        uiRoot.position = cam.position + fwd * uiDistance + Vector3.up * uiVerticalOffset;
        // Face the user: the canvas front (+Z) points away from the camera so text is readable.
        uiRoot.rotation = Quaternion.LookRotation(uiRoot.position - cam.position, Vector3.up);
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
        guidanceBodyText.text = message;
        guidancePanel.SetActive(true);

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
