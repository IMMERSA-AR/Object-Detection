using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

/// <summary>
/// Drives a spawned panel character's animation using a PlayableGraph — no
/// Animator Controller asset required (only an <see cref="Animator"/> component
/// with a valid Avatar on the rig).
///
/// This removes the default T-pose: the character plays an IDLE clip the moment
/// it is spawned, then switches to a TALKING clip while the narration audio plays,
/// and returns to idle when narration finishes.
///
/// PanelDetector adds this component automatically and calls
/// <see cref="Init"/> / <see cref="PlayTalking"/> / <see cref="PlayIdle"/>.
/// </summary>
[DisallowMultipleComponent]
public class PanelCharacterAnimator : MonoBehaviour
{
    private Animator _animator;
    private AnimationClip _idleClip;
    private AnimationClip _talkingClip;

    private PlayableGraph _graph;
    private AnimationClipPlayable _activePlayable;
    private AnimationClip _activeClip;

    /// <summary>
    /// Caches the rig's Animator and starts the idle animation immediately.
    /// </summary>
    /// <param name="idleClip">Looping idle clip — plays on spawn and after narration.</param>
    /// <param name="talkingClip">Talking clip — plays while narration audio is active.
    /// Falls back to idle if null.</param>
    public void Init(AnimationClip idleClip, AnimationClip talkingClip)
    {
        _idleClip    = idleClip;
        _talkingClip = talkingClip;

        // Mixamo/CC4 rigs usually have the Animator on the root; some nest it.
        _animator = GetComponent<Animator>() ?? GetComponentInChildren<Animator>();

        if (_animator == null)
        {
            Debug.LogWarning($"[PanelCharacterAnimator] {name}: no Animator component on the prefab — " +
                             "cannot drive idle/talking animation (character will stay in T-pose). " +
                             "Add an Animator with the character's Avatar to the prefab.");
            return;
        }

        PlayIdle();
    }

    /// <summary>Play the idle clip (called on spawn and when narration ends).</summary>
    public void PlayIdle()
    {
        if (_idleClip == null)
            Debug.LogWarning($"[PanelCharacterAnimator] {name}: Idle clip is NOT assigned on this " +
                             "panel's PanelEntry — character will stay in T-pose. " +
                             "Open the PanelDetector Inspector, find this panel's entry and drag an " +
                             "idle AnimationClip (from the character's own FBX) into the Idle Clip field.");
        PlayClip(_idleClip, "idle");
    }

    /// <summary>Play the talking clip (called when narration audio starts).</summary>
    public void PlayTalking() => PlayClip(_talkingClip != null ? _talkingClip : _idleClip, "talking");

    private void PlayClip(AnimationClip clip, string label)
    {
        if (_animator == null) return;
        if (clip == null)
        {
            Debug.LogWarning($"[PanelCharacterAnimator] {name}: {label} clip is null — " +
                             "assign Idle/Talking clips on the PanelEntry in the Inspector.");
            return;
        }

        if (_graph.IsValid())
            _graph.Destroy();

        _graph = PlayableGraph.Create($"PanelChar_{name}");
        _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);

        _activePlayable = AnimationClipPlayable.Create(_graph, clip);
        _activePlayable.SetApplyFootIK(false);
        _activeClip = clip;

        var output = AnimationPlayableOutput.Create(_graph, "Animation", _animator);
        output.SetSourcePlayable(_activePlayable);

        _graph.Play();
        Debug.Log($"[PanelCharacterAnimator] {name} playing {label} clip: '{clip.name}'.");
    }

    private void Update()
    {
        // PlayableGraph plays a clip once by default — loop it manually.
        if (!_graph.IsValid() || _activeClip == null || _activeClip.length <= 0f) return;

        if ((float)_activePlayable.GetTime() >= _activeClip.length)
            _activePlayable.SetTime(0f);
    }

    private void OnDestroy()
    {
        if (_graph.IsValid())
            _graph.Destroy();
    }
}
