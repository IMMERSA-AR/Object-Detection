using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

[DisallowMultipleComponent]
public class PanelCharacterAnimator : MonoBehaviour
{
    private Animator _animator;
    private AnimationClip _idleClip;
    private AnimationClip _talkingClip;

    private PlayableGraph _graph;
    private AnimationClipPlayable _activePlayable;
    private AnimationClip _activeClip;

    public void Init(AnimationClip idleClip, AnimationClip talkingClip)
    {
        _idleClip = idleClip;
        _talkingClip = talkingClip;
        _animator = GetComponent<Animator>() ?? GetComponentInChildren<Animator>();
        if (_animator == null)
        {
            Debug.LogWarning($"[PanelCharacterAnimator] No animator found on the prefab");
            return;
        }
        PlayIdle();
    }

    // Used to play the standing idle clip that will be played once the character spawnd and finish the story
    public void PlayIdle()
    {
        if (_idleClip == null)
            Debug.LogWarning($"[PanelCharacterAnimator] Idle clip is not assigned");
        PlayClip(_idleClip, "idle");
    }

    // Used to play the talking clip when the character start to narrate the story
    public void PlayTalking()
    {
        PlayClip(_talkingClip != null ? _talkingClip : _idleClip, "talking");
    }

    private void PlayClip(AnimationClip clip, string label)
    {
        if (_animator == null)
            return;
        if (clip == null)
        {
            Debug.LogWarning($"[PanelCharacterAnimator] No clip is assigned");
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
    }

    private void Update()
    {
        if (!_graph.IsValid() || _activeClip == null || _activeClip.length <= 0f)
            return;
        if ((float)_activePlayable.GetTime() >= _activeClip.length)
            _activePlayable.SetTime(0f);
    }

    private void OnDestroy()
    {
        if (_graph.IsValid())
            _graph.Destroy();
    }
}
