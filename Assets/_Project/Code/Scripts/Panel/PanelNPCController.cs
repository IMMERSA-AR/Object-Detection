using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

/// <summary>
/// Lightweight NPC controller for panel-scene characters (e.g. Mourad).
/// Add this component directly to the character PREFAB in the Inspector.
///
/// Two animations only:
///   • Standing idle  — plays automatically on spawn, and after narration ends.
///   • Talking        — played by PanelDetector while narration audio is active.
///
/// Uses PlayableGraph to drive clips directly on the Animator — no Animator
/// Controller asset required (same proven approach as HistoricalNPCController).
///
/// ── Prefab Setup ──────────────────────────────────────────────────────────
///  1. Add this component to the Mourad prefab root (or whichever panel character).
///  2. Assign Idle Clip   → drag a standing-idle AnimationClip from the character's FBX.
///  3. Assign Talking Clip → drag a talking AnimationClip from the character's FBX.
///  4. Make sure the Animator component on the prefab has the correct Avatar assigned
///     and Rig type is set to Humanoid in the FBX import settings.
/// </summary>
[DisallowMultipleComponent]
public class PanelNPCController : MonoBehaviour
{
    [Header("Animation Clips")]
    [Tooltip("Looping standing-idle clip — plays on spawn and after narration ends.")]
    public AnimationClip idleClip;

    [Tooltip("Talking clip — plays while narration audio is active.\n" +
             "Falls back to idle clip if left empty.")]
    public AnimationClip talkingClip;

    // ── Private ───────────────────────────────────────────────────────────────
    private Animator              _animator;
    private PlayableGraph         _graph;
    private AnimationClipPlayable _activePlayable;
    private AnimationClip         _currentClip;

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    private void Awake()
    {
        _animator = GetComponent<Animator>() ?? GetComponentInChildren<Animator>();

        if (_animator == null)
            Debug.LogWarning($"[PanelNPCController] '{name}': No Animator found. " +
                             "Add an Animator component with a Humanoid Avatar to the prefab.");
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Call this from PanelDetector immediately after spawning the character.
    /// Strips any Animator Controller (prevents it fighting the PlayableGraph)
    /// then plays the idle clip.
    /// </summary>
    public void Init()
    {
        if (_animator != null)
        {
            // Remove the Controller so it can never override our PlayableGraph.
            _animator.runtimeAnimatorController = null;
        }
        PlayIdle();
    }

    private void Update()
    {
        // PlayableGraph plays a clip once by default — loop it manually.
        if (!_graph.IsValid() || _currentClip == null || _currentClip.length <= 0f) return;

        if ((float)_activePlayable.GetTime() >= _currentClip.length)
            _activePlayable.SetTime(0f);
    }

    private void OnDestroy()
    {
        if (_graph.IsValid())
            _graph.Destroy();
    }

    /// <summary>Switch to the standing idle animation.</summary>
    public void PlayIdle()
    {
        if (idleClip == null)
        {
            Debug.LogWarning($"[PanelNPCController] '{name}': Idle Clip is not assigned. " +
                             "Drag a standing-idle clip into the Idle Clip field on this component.");
            return;
        }
        PlayClip(idleClip, "idle");
    }

    /// <summary>Switch to the talking animation (called when narration starts).</summary>
    public void PlayTalking()
    {
        AnimationClip clip = talkingClip != null ? talkingClip : idleClip;
        if (clip == null)
        {
            Debug.LogWarning($"[PanelNPCController] '{name}': Neither Talking Clip nor Idle Clip " +
                             "is assigned — animation unchanged.");
            return;
        }
        PlayClip(clip, "talking");
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void PlayClip(AnimationClip clip, string label)
    {
        if (_animator == null) return;

        // Destroy previous graph before creating a new one.
        if (_graph.IsValid())
            _graph.Destroy();

        _graph = PlayableGraph.Create($"PanelNPC_{name}");
        _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);

        _activePlayable = AnimationClipPlayable.Create(_graph, clip);
        _activePlayable.SetApplyFootIK(false);
        _currentClip = clip;

        var output = AnimationPlayableOutput.Create(_graph, "Animation", _animator);
        output.SetSourcePlayable(_activePlayable);

        _graph.Play();
        Debug.Log($"[PanelNPCController] '{name}' playing {label}: '{clip.name}'.");
    }
}
