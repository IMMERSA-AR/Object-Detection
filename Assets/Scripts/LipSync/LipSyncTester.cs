// ============================================================
//  LipSyncTester.cs
//  Standalone lipsync test — no WebSocket, no Quest required.
//
//  Setup:
//    1. Add this script to the same GameObject as
//       CustomLipSyncContext + CustomLipSyncMorphTarget.
//    2. Assign any AudioClip in the Inspector (drag a WAV in).
//    3. Make sure CustomLipSyncContext has:
//         • Backend Type = CPU   (required in Editor)
//         • Model Asset assigned (viseme_model.onnx)
//    4. Hit Play.  Press SPACE (or click the on-screen button)
//       to trigger playback.  Watch the mouth move.
//
//  You can also call PlayClip() from another script or Unity Event.
// ============================================================

using System.Collections;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace LipSync
{
    [RequireComponent(typeof(AudioSource))]
    [RequireComponent(typeof(CustomLipSyncContext))]
    public class LipSyncTester : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────
        [Header("Test Clip")]
        [Tooltip("Drag any WAV / AudioClip here.")]
        public AudioClip testClip;

        [Tooltip("Play automatically when the scene starts.")]
        public bool playOnStart = false;

        [Tooltip("Keyboard shortcut to trigger playback in Play mode.")]
#if ENABLE_INPUT_SYSTEM
        public Key triggerKey = Key.Space;
#else
        public KeyCode triggerKey = KeyCode.Space;
#endif

        [Header("Optional second source (for spatial audio test)")]
        [Tooltip("Leave empty to play through the main AudioSource only.")]
        public AudioSource speakerSource;

        // ── Private ───────────────────────────────────────────────────────
        private CustomLipSyncContext _lipSync;
        private AudioSource          _audioSource;
        private bool                 _ready = false;

        // ── Unity lifecycle ───────────────────────────────────────────────
        void Awake()
        {
            _audioSource = GetComponent<AudioSource>();
            _lipSync     = GetComponent<CustomLipSyncContext>();
        }

        IEnumerator Start()
        {
            // Wait one frame so CustomLipSyncContext.Start() has run
            // (it initialises the predictor there)
            yield return null;

            if (_lipSync == null)
            {
                Debug.LogError("[LipSyncTester] CustomLipSyncContext not found on this GameObject.");
                yield break;
            }

            // Give the predictor one more frame to finish init
            yield return null;

            _ready = true;
            Debug.Log($"[LipSyncTester] Ready.  Clip='{(testClip ? testClip.name : "NONE")}'  " +
                      $"Press [{triggerKey}] or click the on-screen button to play.");

            if (playOnStart && testClip != null)
                PlayClip();
        }

        void Update()
        {
#if ENABLE_INPUT_SYSTEM
            bool keyPressed = Keyboard.current != null &&
                              Keyboard.current[triggerKey].wasPressedThisFrame;
#else
            bool keyPressed = Input.GetKeyDown(triggerKey);
#endif
            if (_ready && keyPressed)
                PlayClip();
        }

        // ── Public API ────────────────────────────────────────────────────

        /// <summary>
        /// Pre-compute the viseme timeline and play the clip.
        /// Safe to call from a UI Button's OnClick event.
        /// </summary>
        public void PlayClip()
        {
            if (testClip == null)
            {
                Debug.LogWarning("[LipSyncTester] No testClip assigned — drag a WAV into the Inspector.");
                return;
            }
            PlayClip(testClip);
        }

        /// <summary>Play an arbitrary clip through the lipsync pipeline.</summary>
        public void PlayClip(AudioClip clip)
        {
            if (!_ready)
            {
                Debug.LogWarning("[LipSyncTester] Not ready yet — still initialising predictor.");
                return;
            }

            if (clip == null) return;

            Debug.Log($"[LipSyncTester] ▶ Playing '{clip.name}'  ({clip.length:F2}s)");

            // 1. Pre-compute viseme timeline (synchronous, ~15-30ms)
            _lipSync.FeedAudioClip(clip);

            // 2. Play on the main AudioSource (drives lipsync)
            _audioSource.Stop();
            _audioSource.clip   = clip;
            _audioSource.loop   = false;
            _audioSource.mute   = false;
            _audioSource.volume = 1f;
            _audioSource.Play();

            // 3. Optionally mirror to a spatial source
            if (speakerSource != null && speakerSource != _audioSource)
            {
                speakerSource.Stop();
                speakerSource.clip = clip;
                speakerSource.Play();
            }
        }

        /// <summary>Stop playback and reset visemes to silence.</summary>
        public void StopClip()
        {
            _audioSource.Stop();
            if (speakerSource != null) speakerSource.Stop();
            Debug.Log("[LipSyncTester] ■ Stopped.");
        }

        // ── Editor UI (in-game button + status overlay) ───────────────────
#if UNITY_EDITOR
        void OnGUI()
        {
            if (!Application.isPlaying) return;

            float W = 260, H = 36, pad = 12;
            Rect btnPlay = new Rect(pad, pad, W, H);
            Rect btnStop = new Rect(pad, pad + H + 6, W, H);
            Rect status  = new Rect(pad, pad + (H + 6) * 2, W * 2, H);

            string clipName = testClip != null ? testClip.name : "(no clip assigned)";
            bool playing    = _audioSource != null && _audioSource.isPlaying;

            // Play button
            GUI.backgroundColor = playing ? Color.grey : Color.green;
            if (GUI.Button(btnPlay, $"▶  Play: {clipName}"))
                PlayClip();

            // Stop button
            GUI.backgroundColor = Color.red;
            if (GUI.Button(btnStop, "■  Stop"))
                StopClip();

            // Status line
            GUI.backgroundColor = Color.clear;
            GUI.color = playing ? Color.cyan : Color.white;
            string state = playing
                ? $"Playing — {_audioSource.time:F1}s / {_audioSource.clip?.length:F1}s"
                : (_ready ? $"Ready  |  [{triggerKey}] to play" : "Initialising...");
            GUI.Label(status, $"[LipSyncTester]  {state}");
            GUI.color = Color.white;
        }
#endif
    }
}
