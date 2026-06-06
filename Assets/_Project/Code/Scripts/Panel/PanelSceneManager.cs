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


    //start new scan
    private void StartScanSequence(bool playNextPanelFirst)
    {
        if (_flowCoroutine != null)
            StopCoroutine(_flowCoroutine);
        _flowCoroutine = StartCoroutine(ScanSequence(playNextPanelFirst));
    }

    private IEnumerator ScanSequence(bool playNextPanelFirst)
    {
        if (playNextPanelFirst)
        {
            ShowGuidance("Move to another exhibition panel to continue your exploration.", guidanceNextPanelClip);
            yield return WaitGuidance(guidanceNextPanelClip);
        }
        ShowGuidance("Direct your device toward one of the exhibition panels and hold steady.\n" + "Panel detection will begin automatically.", guidanceScanClip);
        yield return WaitGuidance(guidanceScanClip);
        HideGuidance();
        if (scanningAudioClip != null)
            PlayAudio(scanningAudioClip, loop: true);

        _flowCoroutine = null;
        StartFreshScan();
    }

    // for waiting until guidance audio finishes
    private IEnumerator WaitGuidance(AudioClip clip)
    {
        if (clip != null)
            yield return new WaitForSeconds(clip.length + 0.3f);
        else
            yield return new WaitForSeconds(guidanceDisplayDuration);
    }

    private void StartFreshScan()
    {
        if (panelDetector == null)
            return;
        if (panelDetector.IsScanning)
            return;
        if (panelDetector.ConfirmedCount >= maxDetectedPanels)
            return;
        panelDetector.ResetDetection();
    }

    //called onec pannel is confirmed
    private void OnPanelDetected(int classId, string panelName)
    {
        _lastConfirmedClass = classId;
        _lastConfirmedName = panelName;

        Debug.Log($"[PanelSceneManager] Panel confirmed'");
        if (audioSource != null && audioSource.isPlaying && audioSource.loop)
            audioSource.Stop();

        ShowGuidance("Panel recognized.\n" + "Point your controller at the character and pull the trigger to begin the story.", guidanceDetectedClip);
    }

    private void OnNarrationStarted()
    {
        _narrationPlaying = true;
        Debug.Log("[PanelSceneManager] Narration started — pausing detection and stopping scan audio.");
        panelDetector?.StopDetection();

        if (audioSource != null && audioSource.isPlaying && audioSource.loop)
            audioSource.Stop();
    }
    private void OnNarrationDone()
    {
        _narrationPlaying = false;
        _narrationDone = true;
        Debug.Log("[PanelSceneManager] Narration finished.");

        // offer to repeat story again 
        if (repeatDialog != null)
        {
            HideGuidance();
            _dialogOpen = true;
            PlayGuidanceVoice(repeatStoryClip);
            repeatDialog.Show(repeatStoryQuestion, onYes: ReplayStory, onNo: DeclineRepeat);
        }
        else
        {
            DeclineRepeat();
        }
    }
    private void ReplayStory()
    {
        _dialogOpen = false;
        Debug.Log("[PanelSceneManager] yes button clicked, repeating story");
        if (panelDetector == null || !panelDetector.ReplayLastNarration())
            DeclineRepeat();
    }
    private void DeclineRepeat()
    {
        _dialogOpen = false;
        Debug.Log("[PanelSceneManager] No button clicked");
        if (panelDetector != null && panelDetector.ConfirmedCount >= maxDetectedPanels)
        {
            OfferRestartExperience();
            return;
        }
        StartScanSequence(playNextPanelFirst: true);
    }

    // for restarting the whole experience
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

    private void RestartExperience()
    {
        _dialogOpen = false;
        Debug.Log("[PanelSceneManager] button yes clicked, restarting the whole experience");
        panelDetector?.ClearAllSpawnedCharacters();
        ClearSpawnedCharacters();                    //clear all characters 
        panelDetector?.ResetConfirmedPanels();
        StartScanSequence(playNextPanelFirst: false);
    }
    private void EndExperience()
    {
        _dialogOpen = false;
        Debug.Log("[PanelSceneManager] button yes clicked");
        ShowGuidance("Thank you for visiting the exhibition.", thankYouClip);
    }


    private void OnScanFoundNothing()
    {
        if (panelDetector == null)
            return;
        if (panelDetector.ConfirmedCount >= maxDetectedPanels)
            return;
        if (_narrationPlaying || _dialogOpen)
            return;
        if (panelDetector.IsScanning)
            return;
        panelDetector.ResetDetection();
    }

    // spawning characters

    private void ClearSpawnedCharacters()
    {
        if (panelDetector != null && panelDetector.LastSpawnedCharacter != null)
            Destroy(panelDetector.LastSpawnedCharacter);

        foreach (var go in _spawnedCharacters)
            if (go != null) Destroy(go);
        _spawnedCharacters.Clear();
    }

    private void SetRescanButton(bool visible)
    {
        if (rescanButton != null)
            rescanButton.SetActive(visible);
    }

    private void PlayAudio(AudioClip clip, bool loop)
    {
        if (audioSource == null || clip == null)
            return;
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

    private void ShowGuidance(string message, AudioClip clip)
    {
        if (guidancePanel == null)
        {
            Debug.LogWarning("[PanelSceneManager] guidance panel is not assigned so it does not appear");
            return;
        }
        if (guidanceBodyText == null)
        {
            Debug.LogWarning("[PanelSceneManager] text is not assigned");
            return;
        }
        if (_guidanceCoroutine != null)
            StopCoroutine(_guidanceCoroutine);

        _guidanceCoroutine = StartCoroutine(GuidanceRoutine(message, clip));
    }

    // make dialoge appear in front of the user
    private void PositionUIInFrontOfUser()
    {
        if (uiDialogueRoot == null)
            return;
        Transform cam = Camera.main != null ? Camera.main.transform : null;
        if (cam == null)
            return;

        Vector3 fwd = cam.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.001f)
            fwd = Vector3.forward;
        fwd.Normalize();

        uiDialogueRoot.position = cam.position + fwd * uiDistanceFromUser + Vector3.up * uiVerticalOffset;
        uiDialogueRoot.rotation = Quaternion.LookRotation(uiDialogueRoot.position - cam.position, Vector3.up);
    }
    private void PlayGuidanceVoice(AudioClip clip)
    {
        if (guidanceAudioSource == null || clip == null) return;
        guidanceAudioSource.Stop();
        guidanceAudioSource.PlayOneShot(clip);
    }

    //responsible for hiding guidance dialoge
    private void HideGuidance()
    {
        if (_guidanceCoroutine != null)
        {
            StopCoroutine(_guidanceCoroutine);
            _guidanceCoroutine = null;
        }
        if (guidanceAudioSource != null)
            guidanceAudioSource.Stop();
        if (guidancePanel != null)
            guidancePanel.SetActive(false);
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
