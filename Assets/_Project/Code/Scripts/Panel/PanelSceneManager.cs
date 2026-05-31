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
    [Tooltip("AudioSource on this GameObject — used for scanning and detected sounds.")]
    public AudioSource audioSource;

    [Tooltip("Loops while scanning for a panel. Leave empty for silence.")]
    public AudioClip scanningAudioClip;

    [Tooltip("Plays once when a panel is confirmed. Leave empty for silence.")]
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
    [Tooltip("If true, the Rescan button appears only after narration finishes.\n" +
             "If false, it appears immediately after confirmation.")]
    public bool showRescanButtonAfterNarration = true;

    [Tooltip("Optional rescan button — shown when ready to scan the next panel.\n" +
             "Wire its OnClick → PanelSceneManager.RescanButtonPressed().")]
    public GameObject rescanButton;

    [Tooltip("Seconds to wait after narration ends before auto-rescanning.\n" +
             "Set to 0 to require the user to press the Rescan button instead.")]
    public float autoRescanDelay = 0f;

    // ── Private state ──────────────────────────────────────────────────

    private bool   _narrationDone;
    private bool   _firstScan           = true;
    private int    _lastConfirmedClass  = -1;
    private string _lastConfirmedName   = "";

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
        panelDetector.OnPanelConfirmed  = OnPanelDetected;
        panelDetector.OnNarrationFinished = OnNarrationDone;
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
        _narrationDone        = false;
        _lastConfirmedClass   = -1;
        _lastConfirmedName    = "";

        SetRescanButton(false);
        SetScanningUI(true);
        SetDetectedUI(false);

        if (guidanceText != null)
            guidanceText.text = scanningGuidanceText;

        PlayAudio(scanningAudioClip, loop: true);

        // Start or restart detection
        if (panelDetector == null) return;

        if (_firstScan)
        {
            _firstScan = false;
            panelDetector.StartDetection();   // first run — model already loaded in Awake
        }
        else
        {
            panelDetector.ResetDetection();   // subsequent runs — clear hits and rescan
        }
    }

    /// <summary>Called by PanelDetector.OnPanelConfirmed.</summary>
    private void OnPanelDetected(int classId, string panelName)
    {
        _lastConfirmedClass = classId;
        _lastConfirmedName  = panelName;

        Debug.Log($"[PanelSceneManager] Panel confirmed: [{classId}] '{panelName}'");

        // Switch from scanning UI to detected UI
        StopAudio();
        SetScanningUI(false);

        if (detectedPanelNameText != null)
            detectedPanelNameText.text = panelName;
        SetDetectedUI(true);

        PlayAudio(detectedAudioClip, loop: false);

        // Show rescan button now if we're not waiting for narration
        if (!showRescanButtonAfterNarration)
            SetRescanButton(true);
    }

    /// <summary>Called by PanelDetector.OnNarrationFinished.</summary>
    private void OnNarrationDone()
    {
        _narrationDone = true;
        Debug.Log("[PanelSceneManager] Narration finished.");

        if (autoRescanDelay > 0f)
            StartCoroutine(AutoRescanAfterDelay(autoRescanDelay));
        else
            SetRescanButton(true);   // let the user decide when to scan next
    }

    private IEnumerator AutoRescanAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        RescanButtonPressed();
    }

    // ── Character tracking ─────────────────────────────────────────────

    private void ClearSpawnedCharacters()
    {
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
        audioSource.clip  = clip;
        audioSource.loop  = loop;
        audioSource.Play();
    }

    private void StopAudio()
    {
        if (audioSource != null)
            audioSource.Stop();
    }
}
