using System.Collections;
using UnityEngine;
using TMPro;

public class ObeliskManager : MonoBehaviour
{
    [Header("Detection")]
    public ObeliskYOLODetector detectionClient;
    [Header("Audio")]
    public AudioSource audioSource;
    [Header("UI")]
    public GameObject scanningPanel;
    public TextMeshProUGUI guidanceText;
    public GameObject detectedPanel;

    private ExperienceConfig _config;
    private System.Action _onComplete;
    private bool _started;

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

    public void ClearScene()
    {
        StopAllCoroutines();

        if (detectionClient != null)
        {
            detectionClient.StopDetection();
        }

        StopAudio();
        SetScanningUI(false);
        SetDetectedUI(false);

        _started = false;
        _config = null;
        _onComplete = null;

        Debug.Log("[ObeliskManager] Scene cleared.");
    }


    private IEnumerator RunExperience()
    {
        if (_config != null && guidanceText != null)
        {
            guidanceText.text = _config.obeliskGuidanceText;
        }

        SetScanningUI(true);
        SetDetectedUI(false);

        if (_config != null && _config.obeliskScanningAudioClip != null)
        {
            PlayAudio(_config.obeliskScanningAudioClip, loop: true);
        }

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

    private void OnObeliskConfirmed()
    {
        Debug.Log("[ObeliskManager] Obelisk confirmed — updating UI and playing detected audio.");

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
