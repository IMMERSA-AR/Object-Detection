// ============================================================
//  CustomLipSyncContext.cs
//  Drop-in replacement for OVRLipSyncContext.
//
//  How it works (pre-compute strategy):
//    1. VoiceAPIController calls FeedAudioClip(clip) right after
//       it creates each TTS AudioClip.
//    2. FeedAudioClip extracts MFCCs + runs batch Sentis inference
//       for the ENTIRE clip upfront (takes ~15-30 ms — done before
//       the clip starts playing).
//    3. The result is stored as a ClipTimeline: per-frame timestamps
//       and 15-viseme probability arrays.
//    4. Every Update(), we look up which clip is playing and read
//       the viseme frame that matches audioSource.timeSamples,
//       lerping smoothly between adjacent frames.
//    5. CustomLipSyncMorphTarget reads CurrentVisemes each LateUpdate.
//
//  Thread-safety: FeedAudioClip may be called from the main thread
//  only (AudioClip.GetData is main-thread only).  Sentis GPU
//  inference is also main-thread.  Both are fast enough in practice.
// ============================================================

using System;
using System.Collections.Generic;
using UnityEngine;


namespace LipSync
{
    [RequireComponent(typeof(AudioSource))]
    public class CustomLipSyncContext : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────
        [Header("Sentis Model")]
        [Tooltip("Assign viseme_model.onnx (from StreamingAssets/LipSync/)")]
        public Unity.InferenceEngine.ModelAsset modelAsset;

        [Tooltip("GPUCompute on device; CPU for editor fallback.")]
        public Unity.InferenceEngine.BackendType backendType = Unity.InferenceEngine.BackendType.GPUCompute;

        [Header("Smoothing")]
        [Range(0f, 0.95f)]
        [Tooltip("Exponential smoothing alpha.  0 = instant, 0.9 = very smooth.")]
        public float smoothing = 0.55f;

        [Header("PP Rescue (m↔n confusion fix)")]
        [Tooltip("When nn (nasal/lateral) fires above this threshold,\n" +
                 "bleed a fraction of its weight into PP (bilabial).\n" +
                 "Compensates for the model confusing /m/ with /n/.\n" +
                 "Set to 0 to disable. Recommended: 0.30–0.45.")]
        [Range(0f, 1f)]
        public float ppRescueBlend = 0.35f;

        [Tooltip("Minimum nn probability before PP rescue activates.\n" +
                 "Keep above 0.10 to avoid rescuing genuine /n/ sounds.")]
        [Range(0f, 0.5f)]
        public float ppRescueThreshold = 0.12f;

        [Header("Debug")]
        public bool logTimings = false;

        // ── Runtime state exposed to CustomLipSyncMorphTarget ─────────────
        /// <summary>15 viseme probability weights in [0,1].  Updated every frame.</summary>
        public float[] CurrentVisemes { get; private set; } = new float[VisemePredictor.NUM_VISEMES];

        // ── Private ───────────────────────────────────────────────────────
        private MFCCExtractor   _mfcc;
        private VisemePredictor _predictor;
        private AudioSource     _audioSource;

        private float _nextStatusLog = 0f;          // rate-limit periodic logs

        // Pre-computed per-clip data
        private class ClipTimeline
        {
            public readonly float[]   frameTimes; // clip-local seconds per MFCC frame
            public readonly float[][] visemes;    // [frame][15]

            public ClipTimeline(float[] times, float[][] v)
            { frameTimes = times; visemes = v; }
        }

        // Map from AudioClip instance → its timeline
        private readonly Dictionary<AudioClip, ClipTimeline> _timelines
            = new Dictionary<AudioClip, ClipTimeline>();

        private ClipTimeline _active;
        private AudioClip    _lastClip;

        // Smoothed output buffer
        private readonly float[] _smoothed = new float[VisemePredictor.NUM_VISEMES];

        // ── Unity lifecycle ───────────────────────────────────────────────

        void Awake()
        {
            _audioSource = GetComponent<AudioSource>();
            _mfcc        = new MFCCExtractor();

            // ── Initialise predictor here, NOT in Start() ──────────────────
            // When this component starts disabled (e.g. during the lecture phase),
            // Unity defers Start() until the component is first enabled.  If we
            // then call FeedAudioClip() on the same frame we re-enable it, Start()
            // hasn't run yet → _predictor is null → FeedAudioClip returns early →
            // no timeline is built → lips never move.
            // Awake() always runs at instantiation regardless of enabled state,
            // so initialising here guarantees _predictor is ready before the first
            // FeedAudioClip() call no matter when we re-enable the component.
            if (modelAsset != null)
            {
                try
                {
                    _predictor = new VisemePredictor(modelAsset, backendType);
                    Debug.Log($"[LipSync] ✓ Predictor ready (Awake). IsReady={_predictor.IsReady}  backend={backendType}");
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[LipSync] ✗ Predictor init failed in Awake: {ex.Message}\n{ex.StackTrace}");
                }
            }
            else
            {
                Debug.LogWarning("[LipSync] modelAsset is NULL in Awake — predictor will be skipped. " +
                                 "Drag viseme_model.onnx into the Inspector.");
            }
        }

        void Start()
        {
            // Predictor is already initialised in Awake().
            // Start() is a no-op except for a guard-log so missing modelAsset
            // is still surfaced if Awake somehow ran without one.
            if (_predictor != null)
            {
                Debug.Log($"[LipSync] Start(): predictor already ready (initialised in Awake). IsReady={_predictor.IsReady}");
                return;
            }

            if (modelAsset == null)
            {
                Debug.LogError("[LipSync] ✗ modelAsset is NULL — drag viseme_model.onnx into the Inspector.");
                return;
            }

            // Fallback: init here if Awake somehow missed it (shouldn't happen).
            Debug.LogWarning("[LipSync] Predictor was not ready in Awake — initialising in Start (fallback).");
            try
            {
                _predictor = new VisemePredictor(modelAsset, backendType);
                Debug.Log($"[LipSync] ✓ Predictor ready (Start fallback). IsReady={_predictor.IsReady}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LipSync] ✗ Predictor init failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        void OnDestroy()
        {
            _predictor?.Dispose();
        }

        // ── Public API (called by VoiceAPIController / LectureHallManager) ──────

        /// <summary>
        /// Guarantee that the predictor and AudioSource reference are ready, even
        /// if this component lives on a child GameObject that was inactive at
        /// instantiation time (which prevents Awake() from running).
        /// Safe to call multiple times — only initialises once.
        /// Call this BEFORE FeedAudioClip() whenever the component may have started
        /// on an inactive GameObject.
        /// </summary>
        public void EnsureInitialized()
        {
            // Bootstrap the fields that Awake() would have set
            if (_audioSource == null)
                _audioSource = GetComponent<AudioSource>();
            if (_mfcc == null)
                _mfcc = new MFCCExtractor();

            if (_predictor != null) return;   // already ready

            if (modelAsset == null)
            {
                Debug.LogError("[LipSync] EnsureInitialized: modelAsset is NULL — " +
                               "assign viseme_model.onnx in the Inspector.");
                return;
            }

            try
            {
                _predictor = new VisemePredictor(modelAsset, backendType);
                Debug.Log($"[LipSync] ✓ EnsureInitialized — predictor ready. " +
                          $"IsReady={_predictor.IsReady}  backend={backendType}");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LipSync] ✗ EnsureInitialized failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Pre-compute the viseme timeline for an incoming TTS AudioClip.
        /// Call this BEFORE the clip is enqueued for playback.
        /// Runs MFCC extraction + batch inference synchronously on the main thread.
        /// </summary>
        public void FeedAudioClip(AudioClip clip)
        {
            if (clip == null) { Debug.LogWarning("[LipSync] FeedAudioClip: clip is NULL"); return; }
            Debug.Log($"[LipSync] FeedAudioClip called — '{clip.name}'  {clip.length:F2}s  {clip.frequency}Hz");

            if (_predictor == null || !_predictor.IsReady)
            {
                Debug.LogError("[LipSync] ✗ Predictor not ready — skipping. Check Backend type (use CPU in editor).");
                return;
            }
            if (_timelines.ContainsKey(clip)) return; // already processed

            float t0 = logTimings ? Time.realtimeSinceStartup : 0f;

            // 1. Pull raw PCM from clip  (main thread only)
            float[] samples = new float[clip.samples * clip.channels];
            clip.GetData(samples, 0);

            // Downmix to mono if stereo
            if (clip.channels > 1)
                samples = DownmixToMono(samples, clip.channels);

            // 2. Extract MFCC frames  [T][39]
            float[][] mfccFrames = _mfcc.Extract(samples, clip.frequency);

            if (mfccFrames.Length == 0)
            {
                Debug.LogWarning("[LipSync] No MFCC frames extracted — clip may be too short.");
                return;
            }

            // 3. Batch Sentis inference  [T][15]
            float[][] visemes = _predictor.PredictBatch(mfccFrames);

            // 4. Build timeline with per-frame timestamps
            float   hopSec     = MFCCExtractor.HOP_LEN / (float)MFCCExtractor.TARGET_SR; // 0.01 s
            float[] frameTimes = new float[mfccFrames.Length];
            for (int i = 0; i < frameTimes.Length; i++)
                frameTimes[i] = i * hopSec;

            lock (_timelines)
                _timelines[clip] = new ClipTimeline(frameTimes, visemes);

            Debug.Log($"[LipSync] ✓ Timeline built — {mfccFrames.Length} frames, " +
                      $"viseme[0] sample: [{string.Join(", ", System.Linq.Enumerable.Select(visemes[0], v => v.ToString("F2")))}]");

            if (logTimings)
                Debug.Log($"[LipSync] {clip.length:F2}s clip → {mfccFrames.Length} frames " +
                          $"processed in {(Time.realtimeSinceStartup - t0) * 1000f:F1} ms");
        }

        /// <summary>
        /// Remove a clip's timeline once you know it will never play again.
        /// Called automatically on clip change; can also be called manually.
        /// </summary>
        public void ReleaseClip(AudioClip clip)
        {
            if (clip == null) return;
            lock (_timelines)
                _timelines.Remove(clip);
        }

        // ── Update: serve CurrentVisemes each frame ───────────────────────

        void Update()
        {
            AudioClip currentClip = _audioSource.clip;

            // Detect clip change
            if (currentClip != _lastClip)
            {
                // Release the old clip's timeline (memory cleanup)
                if (_lastClip != null)
                    ReleaseClip(_lastClip);

                _lastClip = currentClip;
                _active   = null;

                if (currentClip != null)
                {
                    lock (_timelines)
                        _timelines.TryGetValue(currentClip, out _active);
                }
            }

            // Silence if nothing is playing
            if (!_audioSource.isPlaying)
            {
                Smooth(null);
                return;
            }

            // Audio is playing — check we have a timeline
            if (_active == null)
            {
                // Log once per second so it's easy to spot without flooding
                if (Time.realtimeSinceStartup > _nextStatusLog)
                {
                    _nextStatusLog = Time.realtimeSinceStartup + 1f;
                    Debug.LogWarning("[LipSync] ⚠ Audio is playing but NO timeline found for " +
                        $"'{_audioSource.clip?.name}'. " +
                        "Check: (1) FeedAudioClip was called before playback, " +
                        "(2) Predictor is ready (use CPU backend in editor), " +
                        "(3) customLipSyncContext is assigned in VoiceAPIController.");
                }
                Smooth(null);
                return;
            }

            // Convert timeSamples → clip-local time
            float clipTime = (float)_audioSource.timeSamples / _audioSource.clip.frequency;
            float[] visemes = SampleTimeline(_active, clipTime);

            if (logTimings && Time.realtimeSinceStartup > _nextStatusLog)
            {
                _nextStatusLog = Time.realtimeSinceStartup + 0.5f;
                int top = 0;
                for (int i = 1; i < VisemePredictor.NUM_VISEMES; i++)
                    if (visemes[i] > visemes[top]) top = i;
                Debug.Log($"[LipSync] t={clipTime:F2}s  top-viseme={top}  prob={visemes[top]:F2}");
            }

            Smooth(visemes);
        }

        // ── Internal helpers ──────────────────────────────────────────────

        // Binary-search for the right frame, then lerp between neighbours
        static float[] SampleTimeline(ClipTimeline tl, float t)
        {
            float[] times = tl.frameTimes;
            int     T     = times.Length;
            if (T == 0) return null;

            // Fast boundary checks
            if (t <= times[0])  return tl.visemes[0];
            if (t >= times[T-1]) return tl.visemes[T-1];

            // Binary search
            int lo = 0, hi = T - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (times[mid] <= t) lo = mid;
                else                 hi = mid;
            }

            // Lerp between lo and hi
            float span  = times[hi] - times[lo];
            float alpha = span > 0f ? (t - times[lo]) / span : 0f;

            float[] lerped = new float[VisemePredictor.NUM_VISEMES];
            float[] vLo = tl.visemes[lo];
            float[] vHi = tl.visemes[hi];
            for (int i = 0; i < VisemePredictor.NUM_VISEMES; i++)
                lerped[i] = vLo[i] + (vHi[i] - vLo[i]) * alpha;
            return lerped;
        }

        // Exponential smoothing:  s = α·s + (1-α)·target
        void Smooth(float[] target)
        {
            float a = smoothing;
            float b = 1f - a;
            for (int i = 0; i < VisemePredictor.NUM_VISEMES; i++)
            {
                float goal   = target != null ? target[i] : 0f;
                _smoothed[i] = a * _smoothed[i] + b * goal;
            }

            // ── PP Rescue ─────────────────────────────────────────────────
            // The BiLSTM confuses bilabial /m/ with alveolar /n/ because
            // their MFCCs are nearly identical (both nasals).  When nn fires
            // strongly we bleed a fraction of that energy into PP so that
            // M/P/B words still produce visible lip closure.
            if (ppRescueBlend > 0f && _smoothed[8] > ppRescueThreshold)
            {
                float rescued = _smoothed[8] * ppRescueBlend;
                _smoothed[1]  = Mathf.Max(_smoothed[1], rescued);
            }

            Array.Copy(_smoothed, CurrentVisemes, VisemePredictor.NUM_VISEMES);
        }

        static float[] DownmixToMono(float[] interleaved, int channels)
        {
            int     monoLen = interleaved.Length / channels;
            float[] mono    = new float[monoLen];
            float   inv     = 1f / channels;
            for (int i = 0; i < monoLen; i++)
            {
                float sum = 0f;
                for (int c = 0; c < channels; c++)
                    sum += interleaved[i * channels + c];
                mono[i] = sum * inv;
            }
            return mono;
        }
    }
}
