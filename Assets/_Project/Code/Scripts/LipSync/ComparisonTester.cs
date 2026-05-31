// ============================================================
//  ComparisonTester.cs
//  Plays the same AudioClip through two characters at exactly
//  the same time so you can compare lipsync systems side by side.
//
//  Character A  →  Our BiLSTM (CustomLipSyncContext)
//  Character B  →  Oculus OVRLipSync (OVRLipSyncContext)
//
//  Setup:
//    1. Create an empty GameObject called "ComparisonController".
//    2. Add this script to it.
//    3. Assign BiLSTM_Root  → your existing Mourad (has CustomLipSyncContext)
//    4. Assign OVR_Root     → the duplicate Mourad (has OVRLipSyncContext)
//    5. Assign TestClip     → your WAV
//    6. Hit Play, press Space (or click the on-screen button).
// ============================================================

using System.Collections;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace LipSync
{
    public class ComparisonTester : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────
        [Header("Characters")]
        [Tooltip("Mourad with CustomLipSyncContext + CustomLipSyncMorphTarget")]
        public GameObject biLSTM_Root;

        [Tooltip("Duplicate Mourad with OVRLipSyncContext + OVRLipSyncContextMorphTarget")]
        public GameObject ovr_Root;

        [Header("Clip")]
        public AudioClip testClip;
        public bool playOnStart = false;

        [Header("Trigger Key")]
#if ENABLE_INPUT_SYSTEM
        public Key triggerKey = Key.Space;
#else
        public KeyCode triggerKey = KeyCode.Space;
#endif

        // ── Private ───────────────────────────────────────────────────────
        private CustomLipSyncContext _biLSTMContext;
        private AudioSource          _biLSTMAudio;
        private AudioSource          _ovrAudio;
        private bool                 _ready = false;

        // ── Unity lifecycle ───────────────────────────────────────────────
        IEnumerator Start()
        {
            yield return null;    // let all Start() calls on other scripts run
            yield return null;

            // Grab BiLSTM components
            if (biLSTM_Root != null)
            {
                _biLSTMContext = biLSTM_Root.GetComponentInChildren<CustomLipSyncContext>();
                _biLSTMAudio   = biLSTM_Root.GetComponentInChildren<AudioSource>();
            }

            // Grab OVR AudioSource
            if (ovr_Root != null)
                _ovrAudio = ovr_Root.GetComponentInChildren<AudioSource>();

            bool ok = true;
            if (_biLSTMContext == null)
            { Debug.LogError("[Comparison] BiLSTM_Root has no CustomLipSyncContext."); ok = false; }
            if (_biLSTMAudio == null)
            { Debug.LogError("[Comparison] BiLSTM_Root has no AudioSource."); ok = false; }
            if (_ovrAudio == null)
            { Debug.LogError("[Comparison] OVR_Root has no AudioSource."); ok = false; }

            if (ok)
            {
                _ready = true;
                Debug.Log("[Comparison] Ready — both characters found. Press Space to play.");
            }

            if (playOnStart && testClip != null && _ready)
                PlayBoth();
        }

        void Update()
        {
#if ENABLE_INPUT_SYSTEM
            bool pressed = Keyboard.current != null &&
                           Keyboard.current[triggerKey].wasPressedThisFrame;
#else
            bool pressed = Input.GetKeyDown(triggerKey);
#endif
            if (_ready && pressed) PlayBoth();
        }

        // ── Public API ────────────────────────────────────────────────────

        public void PlayBoth()
        {
            if (!_ready || testClip == null) return;

            Debug.Log($"[Comparison] ▶ Playing '{testClip.name}' on BOTH characters simultaneously");

            // ── 1. BiLSTM: pre-compute timeline then play ─────────────────
            _biLSTMContext.FeedAudioClip(testClip);

            _biLSTMAudio.Stop();
            _biLSTMAudio.clip   = testClip;
            _biLSTMAudio.loop   = false;
            _biLSTMAudio.volume = 1f;
            _biLSTMAudio.Play();

            // ── 2. OVR: just play — OVRLipSyncContext hooks OnAudioFilterRead
            //    and processes audio in real-time as it plays ────────────────
            _ovrAudio.Stop();
            _ovrAudio.clip   = testClip;
            _ovrAudio.loop   = false;
            _ovrAudio.volume = 1f;
            _ovrAudio.Play();

            // Both Play() calls happen in the same frame → perfectly in sync
        }

        public void StopBoth()
        {
            _biLSTMAudio?.Stop();
            _ovrAudio?.Stop();
        }

        // ── On-screen UI ──────────────────────────────────────────────────
        void OnGUI()
        {
            if (!Application.isPlaying) return;

            float W = 300, H = 38, pad = 12;
            bool playing = _biLSTMAudio != null && _biLSTMAudio.isPlaying;

            // ── Play button ───────────────────────────────────────────────
            GUI.backgroundColor = playing ? Color.grey : new Color(0.2f, 0.8f, 0.2f);
            string clipLabel = testClip != null ? testClip.name : "no clip";
            if (GUI.Button(new Rect(pad, pad, W, H), $"▶  Play both:  {clipLabel}"))
                PlayBoth();

            // ── Stop button ───────────────────────────────────────────────
            GUI.backgroundColor = new Color(0.9f, 0.2f, 0.2f);
            if (GUI.Button(new Rect(pad, pad + H + 6, W, H), "■  Stop both"))
                StopBoth();

            // ── Status labels ─────────────────────────────────────────────
            GUI.backgroundColor = Color.clear;

            float labelY = pad + (H + 6) * 2 + 8;
            float time   = _biLSTMAudio != null ? _biLSTMAudio.time : 0f;
            float dur    = testClip     != null ? testClip.length   : 0f;

            GUI.color = new Color(0.4f, 0.9f, 1f);
            GUI.Label(new Rect(pad, labelY,      W * 2, 22),
                playing ? $"[BiLSTM]  {time:F1}s / {dur:F1}s  — pre-computed ML"
                        : (_ready ? "[BiLSTM]  ready" : "[BiLSTM]  initialising..."));

            GUI.color = new Color(1f, 0.85f, 0.3f);
            GUI.Label(new Rect(pad, labelY + 24, W * 2, 22),
                playing ? $"[OVR]     {time:F1}s / {dur:F1}s  — real-time rule-based"
                        : (_ready ? "[OVR]     ready" : "[OVR]     waiting..."));

            GUI.color = Color.white;
        }
    }
}
