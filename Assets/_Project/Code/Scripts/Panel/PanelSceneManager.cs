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

    [Header("Repeat Dialog (Yes/No)")]
    [Tooltip("The reusable two-button confirmation dialog (RepeatDialog component).\n" +
             "Used both for 'Repeat this story?' after each panel and for\n" +
             "'Restart the whole experience?' after all panels are done.\n" +
             "Leave empty to disable repeat prompts entirely.")]
    public RepeatDialog repeatDialog;

    [Tooltip("Question shown after a story ends (per-panel repeat).")]
    [TextArea(2, 4)]
    public string repeatStoryQuestion =
        "The story has ended.\nWould you like to hear it again?";

    [Tooltip("Voice-over for the 'repeat this story?' question. Optional.")]
    public AudioClip repeatStoryClip;

    [Tooltip("Question shown after every panel has been explored (whole-experience restart).")]
    [TextArea(2, 4)]
    public string restartExperienceQuestion =
        "You have explored all the panels.\nWould you like to restart the whole experience?";

    [Tooltip("Voice-over for the 'restart the whole experience?' question. Optional.")]
    public AudioClip restartExperienceClip;

    [Tooltip("Voice-over for the final 'thank you for visiting' message. Optional.")]
    public AudioClip thankYouClip;

    // ── Private state ──────────────────────────────────────────────────

    private bool _narrationDone;
    private bool _firstScan = true;
    private int _lastConfirmedClass = -1;
    private string _lastConfirmedName = "";
    private Coroutine _guidanceCoroutine;

    /// <summary>True while the scene intro clip is still playing.</summary>
    private bool _introPlaying;

    /// <summary>True while a character's narration audio is playing (detection paused).</summary>
    private bool _narrationPlaying;

    /// <summary>True while a Yes/No repeat dialog is open (detection paused).</summary>
    private bool _dialogOpen;

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
        // Hide rescan button until narration finishes
        SetRescanButton(false);
        BeginScanning();
    }

    // ── Public API (called by buttons or external scripts) ─────────────

    /// <summary>Destroy all spawned characters and restart a fresh scan.</summary>
    public void RescanButtonPressed()
    {
        Debug.Log("[PanelSceneManager] Rescan requested.");
        ClearSpawnedCharacters();
        BeginScanning();
    }

    // ── Internal flow ──────────────────────────────────────────────────

    private void BeginScanning()
    {
        _narrationDone = false;
        _lastConfirmedClass = -1;
        _lastConfirmedName = "";

        SetRescanButton(false);

        // Start or restart detection
        if (panelDetector == null) return;

        if (_firstScan)
        {
            _firstScan = false;

            if (introAudioClip != null)
            {
                // Play intro first — detection starts only after the intro finishes.
                _introPlaying = true;
                PlayAudio(introAudioClip, loop: false);
                StartCoroutine(StartDetectionAfterIntro(introAudioClip.length));
            }
            else
            {
                // No intro — start scanning immediately with optional ambient loop.
                if (scanningAudioClip != null)
                    PlayAudio(scanningAudioClip, loop: true);

                panelDetector.StartDetection();
            }
        }
        else
        {
            // Rescans: ambient scanning loop (intro is a one-time thing only).
            if (scanningAudioClip != null)
                PlayAudio(scanningAudioClip, loop: true);

            panelDetector.ResetDetection();   // clear hits and rescan
        }
    }

    /// <summary>Called by PanelDetector.OnPanelConfirmed.</summary>
    private void OnPanelDetected(int classId, string panelName)
    {
        _lastConfirmedClass = classId;
        _lastConfirmedName = panelName;

        Debug.Log($"[PanelSceneManager] Panel confirmed: [{classId}] '{panelName}'");

        // Stop ONLY the looping scanning ambience — never cut the one-shot intro,
        // which must keep playing "regardless of the banners".
        if (audioSource != null && audioSource.isPlaying && audioSource.loop)
            audioSource.Stop();

        // Detected sound only if nothing is currently playing (so it never cuts the intro).
        if (detectedAudioClip != null && (audioSource == null || !audioSource.isPlaying))
            PlayAudio(detectedAudioClip, loop: false);

        ShowGuidance(
            "Panel recognized.\n" +
            "Point your controller at the character and press the trigger to begin the story.",
            guidanceDetectedClip);

        // Either keep scanning for the next panel (continuous) or stop if we've
        // reached the maximum number of panels. The user can still click the
        // spawned character to hear its story — that pauses detection automatically.
        if (panelDetector.ConfirmedCount >= maxPanels)
        {
            Debug.Log($"[PanelSceneManager] Max panels ({maxPanels}) reached — detection complete.");
        }
        else
        {
            StartCoroutine(ResumeScanAfterDelay(autoRescanDelay));
        }
    }

    /// <summary>Called by PanelDetector.OnNarrationStarted — pause detection while audio plays.</summary>
    private void OnNarrationStarted()
    {
        _narrationPlaying = true;
        Debug.Log("[PanelSceneManager] Narration started — pausing detection until it ends.");
        panelDetector?.StopDetection();
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
        if (panelDetector != null && panelDetector.ConfirmedCount >= maxPanels)
        {
            OfferRestartExperience();
            return;
        }

        ShowGuidance(
            "Move to another exhibition panel to continue your exploration.",
            guidanceNextPanelClip);

        ResumeScanIfAllowed();
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
        ClearSpawnedCharacters();
        panelDetector?.ResetConfirmedPanels();   // all panels eligible again

        ShowGuidance(
            "Restarting the experience.\n" +
            "Direct your device toward one of the exhibition panels.",
            guidanceScanClip);

        ResumeScanIfAllowed();
    }

    /// <summary>"Restart → No": end the experience. Characters remain interactive,
    /// so the user can still point at any of them to replay its story.</summary>
    private void EndExperience()
    {
        _dialogOpen = false;
        Debug.Log("[PanelSceneManager] Experience ended by user.");
        ShowGuidance("Thank you for visiting the exhibition.", thankYouClip);
    }

    /// <summary>Called by PanelDetector.OnScanCompletedNoResult — keep scanning continuously.</summary>
    private void OnScanFoundNothing()
    {
        // A scan pass ended without finding a panel. Keep scanning unless we are
        // paused for narration or have hit the panel limit.
        ResumeScanIfAllowed();
    }

    /// <summary>
    /// Resumes scanning only when it is allowed:
    ///   • not already scanning,
    ///   • no narration audio currently playing,
    ///   • fewer than maxPanels confirmed.
    /// </summary>
    private void ResumeScanIfAllowed()
    {
        if (panelDetector == null) return;

        if (panelDetector.ConfirmedCount >= maxPanels)
        {
            Debug.Log($"[PanelSceneManager] Max panels ({maxPanels}) reached — detection stays off.");
            return;
        }
        if (_narrationPlaying)
        {
            Debug.Log("[PanelSceneManager] Narration in progress — detection resumes when it ends.");
            return;
        }
        if (_dialogOpen)
        {
            Debug.Log("[PanelSceneManager] Repeat dialog open — detection resumes after the user answers.");
            return;
        }
        if (panelDetector.IsScanning)
            return;   // a scan is already running — nothing to do

        BeginScanning();   // keeps existing characters alive; only resets UI + detection
    }

    /// <summary>Waits, then resumes scanning for the next panel (if allowed).</summary>
    private IEnumerator ResumeScanAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        ResumeScanIfAllowed();
    }

    /// <summary>
    /// Waits for the intro clip to finish, then:
    ///  1. Clears _introPlaying so the narration gate opens.
    ///  2. Starts panel detection.
    ///  3. Optionally switches to the scanning ambient loop.
    /// </summary>
    private IEnumerator StartDetectionAfterIntro(float introDuration)
    {
        yield return new WaitForSeconds(introDuration);

        _introPlaying = false;
        Debug.Log("[PanelSceneManager] Intro finished — starting panel detection.");

        ShowGuidance(
            "Direct your device toward one of the exhibition panels and hold steady.\n" +
            "Panel detection will begin automatically.",
            guidanceScanClip);

        if (scanningAudioClip != null)
            PlayAudio(scanningAudioClip, loop: true);

        if (panelDetector != null)
            panelDetector.StartDetection();
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
        if (guidancePanel == null || guidanceBodyText == null) return;

        if (_guidanceCoroutine != null)
            StopCoroutine(_guidanceCoroutine);

        _guidanceCoroutine = StartCoroutine(GuidanceRoutine(message, clip));
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
