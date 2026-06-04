using System.Collections;
using UnityEngine;

public class TimePortalLogic : MonoBehaviour
{
    [Header("Obelisk Detection")]
    [Tooltip("Drag the ObeliskDetector GameObject here.")]
    public ObeliskYOLODetector obeliskDetector;

    [Header("Audio Storytelling")]
    [Tooltip("Optional. Drag the AudioStoryCircle here to start the picture story " +
             "when the user passes through the time machine.")]
    public AudioStoryCircle audioStory;

    [Header("Time Machine")]
    [Tooltip("Drag the time machine GameObject here so it can be hidden during the story.")]
    public GameObject timeMachine;

    [Header("Hassan")]
    [Tooltip("Optional. Drag Hassan's HassanApproach component here. After the intro " +
             "story ends, Hassan walks up to the user so they can talk to him.")]
    public HassanApproach hassan;

    [Header("Leave")]
    [Tooltip("Controller button the user presses to bring the time machine BACK when they " +
             "want to leave. Default: B / Y (Button.Two).")]
    public OVRInput.Button leaveButton = OVRInput.Button.Two;

    private bool _storyStarted;    // intro story has been kicked off (runs only once)
    private bool _storyComplete;   // intro story finished — entries now toggle the cast
    private bool _awaitingLeave;   // story done, machine hidden, waiting for the leave button

    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag("Player")) return;

        // ── First entry: play the intro story ONCE ──
        if (!_storyStarted)
        {
            _storyStarted = true;

            if (audioStory != null)
            {
                // Hide the time machine (visually) while the intro images/audio play.
                SetTimeMachineVisible(false);

                // When the story finishes: reveal the cast and send Hassan over, but KEEP
                // the time machine hidden. The user brings it back with the leave button
                // when they're ready to go.
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
                // No intro story assigned — reveal the characters immediately.
                _storyComplete = true;
                RevealCharacters();
            }
            return;
        }

        // ── Subsequent entries (after the story): toggle the cast in/out, like before ──
        if (_storyComplete)
            RevealCharacters();   // ObeliskYOLODetector.ToggleCharacters()
    }

    private void Update()
    {
        // After the story, watch for the leave button to bring the time machine back.
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

    /// <summary>
    /// Shows/hides the time machine by toggling its Renderers + Colliders, WITHOUT
    /// deactivating its GameObject. This keeps this trigger (which may live under the
    /// machine) running so it can still listen for the leave button while hidden.
    /// </summary>
    private void SetTimeMachineVisible(bool visible)
    {
        if (timeMachine == null) return;

        // Visuals + physics.
        foreach (var r in timeMachine.GetComponentsInChildren<Renderer>(true))
            r.enabled = visible;
        foreach (var c in timeMachine.GetComponentsInChildren<Collider>(true))
            c.enabled = visible;
        foreach (var l in timeMachine.GetComponentsInChildren<Light>(true))
            l.enabled = visible;
        foreach (var a in timeMachine.GetComponentsInChildren<Animator>(true))
            a.enabled = visible;

        // Sound — actually stop the portal/hum while hidden, resume on show.
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

        // Particle / vortex effects — stop + clear while hidden, restart on show.
        foreach (var ps in timeMachine.GetComponentsInChildren<ParticleSystem>(true))
        {
            if (visible) ps.Play(true);
            else         ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        }
    }

    /// <summary>
    /// Post-story flow: hide the story pictures, reveal the characters, then walk Hassan
    /// up to the user once his GameObject is active. (Time machine stays hidden.)
    /// </summary>
    private IEnumerator PostStorySequence()
    {
        if (audioStory != null) audioStory.HideFrames();

        RevealCharacters();

        if (hassan != null)
        {
            // ToggleCharacters() activates the cast across several frames (StaggeredReveal),
            // so wait until Hassan's hierarchy is actually live before sending him over —
            // a coroutine can't start on an inactive GameObject.
            while (!hassan.gameObject.activeInHierarchy)
                yield return null;

            hassan.Approach();
        }
    }

    /// <summary>Reveals the obelisk characters (Hassan etc.) via the detector.</summary>
    private void RevealCharacters()
    {
        if (obeliskDetector != null)
            obeliskDetector.ToggleCharacters();
        else
            Debug.LogWarning("[TimePortal] obeliskDetector not assigned in Inspector.");
    }
}