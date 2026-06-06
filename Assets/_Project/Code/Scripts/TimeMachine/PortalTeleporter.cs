using System.Collections;
using UnityEngine;

public class TimePortalLogic : MonoBehaviour
{
    [Header("Obelisk Detection")]
    public ObeliskYOLODetector obeliskDetector;
    [Header("Audio Storytelling")]
    public AudioStoryCircle audioStory;
    [Header("Time Machine")]
    public GameObject timeMachine;
    [Header("Hassan")]
    public HassanApproach hassan;
    [Header("Leave")]
    public OVRInput.Button leaveButton = OVRInput.Button.Two;

    private bool _storyStarted;    
    private bool _storyComplete;  
    private bool _awaitingLeave; 

    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag("Player")) return;

        if (!_storyStarted)
        {
            _storyStarted = true;

            if (audioStory != null)
            {
                
                SetTimeMachineVisible(false);
                audioStory.OnStoryComplete = () =>
                {
                    _storyComplete = true;
                    _awaitingLeave = true;
                    StartCoroutine(PostStorySequence());
                };

                audioStory.Play();
                Debug.Log("[TimePortal] Intro story started — machine stays hidden until the leave button.");
            }
            else
            {
                _storyComplete = true;
                RevealCharacters();
            }
            return;
        }

        if (_storyComplete)
            RevealCharacters();   
    }

    private void Update()
    {
        if (!_awaitingLeave) return;

        bool pressed = false;
        try { pressed = OVRInput.GetDown(leaveButton); } catch { }

        if (pressed)
        {
            SetTimeMachineVisible(true);
            _awaitingLeave = false;
            Debug.Log("[TimePortal] Leave button pressed — time machine summoned.");
        }
    }

    private void SetTimeMachineVisible(bool visible)
    {
        if (timeMachine == null) return;

        foreach (var r in timeMachine.GetComponentsInChildren<Renderer>(true))
            r.enabled = visible;
        foreach (var c in timeMachine.GetComponentsInChildren<Collider>(true))
            c.enabled = visible;
        foreach (var l in timeMachine.GetComponentsInChildren<Light>(true))
            l.enabled = visible;
        foreach (var a in timeMachine.GetComponentsInChildren<Animator>(true))
            a.enabled = visible;
        foreach (var src in timeMachine.GetComponentsInChildren<AudioSource>(true))
        {
            if (visible)
            {
                src.enabled = true;
                if (src.playOnAwake || src.loop) src.Play();
            }
            else
            {
                src.Stop();
                src.enabled = false;
            }
        }

        foreach (var ps in timeMachine.GetComponentsInChildren<ParticleSystem>(true))
        {
            if (visible) ps.Play(true);
            else ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }
    }

    private IEnumerator PostStorySequence()
    {
        if (audioStory != null) audioStory.HideFrames();

        RevealCharacters();

        if (hassan != null)
        {
                yield return null;

            hassan.Approach();
        }
    }

    private void RevealCharacters()
    {
        if (obeliskDetector != null)
            obeliskDetector.ToggleCharacters();
        else
            Debug.LogWarning("[TimePortal] obeliskDetector not assigned in Inspector.");
    }
}