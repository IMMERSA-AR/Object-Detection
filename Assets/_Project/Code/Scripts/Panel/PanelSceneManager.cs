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
///  ├─ PanelSceneManager         (this component + AudioSource)
///  │   └─ PanelDetector         (PanelDetector component — fill its Inspector fields)
///  ├─ OVRCameraRig              (standard Meta camera rig)
///  ├─ MRUK                      (MR Utility Kit)
///  ├─ ScanningUI  [Canvas]      (world-space canvas, always-facing or HUD)
///  │   ├─ Background            (Image)
///  │   ├─ GuidanceText          (TextMeshProUGUI)
///  │   └─ ScanningSpinner       (optional animated icon)
///  └─ DetectedUI  [Canvas]      (world-space canvas)
///      ├─ Background            (Image)
///      ├─ PanelNameText         (TextMeshProUGUI — shows panel name)
///      ├─ RescanButton          (Button — calls PanelSceneManager.RescanButtonPressed)
///      └─ RescanButtonLabel     (TextMeshProUGUI inside button, e.g. "Scan Next Panel")
///
///  INSPECTOR WIRING:
///   Panel Detector   → drag PanelDetector child GameObject here
///   Audio Source     → drag the AudioSource on THIS GameObject here
///   Scanning Panel   → drag ScanningUI canvas GameObject
///   Guidance Text    → drag GuidanceText inside ScanningUI
///   Detected Panel   → drag DetectedUI canvas GameObject
///   Detected Label   → drag PanelNameText inside DetectedUI
///   Scanning Clip    → optional looping ambient scan audio
///   Detected Clip    → optional one-shot "panel found" sound
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

    [Header("Scanning UI")]
    [Tooltip("Root canvas/panel shown while scanning.")]
    public GameObject scanningPanel;

    [Tooltip("TextMeshPro inside scanningPanel — shows guidance text.")]
    public TextMeshProUGUI guidanceText;

    [Tooltip("Text displayed while scanning.")]
    public string scanningGuidanceText = "Point the camera at a panel…";

    [Header("Detected UI")]
    [Tooltip("Root canvas/panel shown after a panel is confirmed.")]
    public GameObject detectedPanel;

    [Tooltip("TextMeshPro inside detectedPanel — shows the confirmed panel name.")]
    public TextMeshProUGUI detectedPanelNameText;

    [Header("Rescan")]
    [Tooltip("Optional rescan button — manually restarts the full experience (destroys all characters).\n" +
             "Wire its OnClick → PanelSceneManager.RescanButtonPressed().")]
    public GameObject rescanButton;

    [Tooltip("Seconds to wait after narration ends before automatically scanning for the next panel.\n" +
             "Spawned characters stay in the world. Default: 2 seconds.")]
    public float autoRescanDelay = 2f;

    // ── Private state ──────────────────────────────────────────────────

    private bool _narrationDone;
    private bool _firstScan = true;
    private int _lastConfirmedClass = -1;
    private string _lastConfirmedName = "";

    /// <summary>True while the scene intro clip is still playing.</summary>
    private bool _introPlaying;

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
        panelDetector.OnPanelConfirmed    = OnPanelDetected;
        panelDetector.OnNarrationFinished = OnNarrationDone;

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
        SetScanningUI(true);
        SetDetectedUI(false);

        if (guidanceText != null)
            guidanceText.text = scanningGuidanceText;

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

        // Switch from scanning UI to detected UI
        SetScanningUI(false);

        // Stop ONLY the looping scanning ambience — never cut the one-shot intro,
        // which must keep playing "regardless of the banners".
        if (audioSource != null && audioSource.isPlaying && audioSource.loop)
            audioSource.Stop();

        if (detectedPanelNameText != null)
            detectedPanelNameText.text = panelName;
        SetDetectedUI(true);

        // Detected sound only if nothing is currently playing (so it never cuts the intro).
        if (detectedAudioClip != null && (audioSource == null || !audioSource.isPlaying))
            PlayAudio(detectedAudioClip, loop: false);

        // Rescan button stays hidden — scanning resumes automatically after narration.
    }

    /// <summary>Called by PanelDetector.OnNarrationFinished.</summary>
    private void OnNarrationDone()
    {
        _narrationDone = true;
        Debug.Log("[PanelSceneManager] Narration finished — resuming scan for next panel.");
        StartCoroutine(AutoRescanAfterDelay(autoRescanDelay));
    }

    /// <summary>
    /// Waits, then starts scanning for the next panel.
    /// Spawned characters are kept in the world — only RescanButtonPressed() destroys them.
    /// </summary>
    private IEnumerator AutoRescanAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        Debug.Log("[PanelSceneManager] Auto-rescan starting.");
        BeginScanning();   // keeps existing characters alive; only resets UI + detection
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
}
