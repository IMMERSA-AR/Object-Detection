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

    // ── Private state ─────────────────────────────────────────────────

    private ExperienceConfig _config;
    private System.Action _onComplete;
    private bool _started;

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

        StopAudio();
        SetScanningUI(false);
        SetDetectedUI(false);

        _started = false;
        _config = null;
        _onComplete = null;

        Debug.Log("[ObeliskManager] Scene cleared.");
    }

    // ── Internal flow ─────────────────────────────────────────────────

    private IEnumerator RunExperience()
    {
        // ── 1. Show guidance UI ──────────────────────────────────────
        if (_config != null && guidanceText != null)
            guidanceText.text = _config.obeliskGuidanceText;

        SetScanningUI(true);
        SetDetectedUI(false);

        // ── 2. Play scanning audio (looping) ─────────────────────────
        if (_config != null && _config.obeliskScanningAudioClip != null)
            PlayAudio(_config.obeliskScanningAudioClip, loop: true);

        // ── 3. Start detection ────────────────────────────────────────
        if (detectionClient == null)
        {
            Debug.LogError("[ObeliskManager] detectionClient not assigned! " +
                           "Drag ObeliskDetectionClient here in the Inspector.");
            yield break;
        }

        // Hook into the detection client's callback before starting
        detectionClient.OnObeliskConfirmed = OnObeliskConfirmed;
        detectionClient.StartDetection();

        // ── 4. Wait until detection completes (callback will fire) ────
        // Nothing to do here — OnObeliskConfirmed() is called by the client.
        yield break;
    }

    /// <summary>
    /// Called by ObeliskDetectionClient when the obelisk is confirmed detected
    /// and characters have been spawned.
    /// </summary>
    private void OnObeliskConfirmed()
    {
        Debug.Log("[ObeliskManager] Obelisk confirmed — updating UI and playing detected audio.");

        // Stop scanning loop audio
        StopAudio();
        SetScanningUI(false);

        // Show detected panel
        SetDetectedUI(true);

        // Play one-shot detected audio
        if (_config != null && _config.obeliskDetectedAudioClip != null)
            PlayAudio(_config.obeliskDetectedAudioClip, loop: false);

        // Notify ExperienceManager that the experience is "complete"
        _onComplete?.Invoke();
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
}
