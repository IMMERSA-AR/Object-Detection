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

        [Header("Text-Guided MFCC (Viterbi alignment)")]
        [Tooltip("ON  = constrain the MFCC model's output to follow the viseme\n" +
                 "      sequence from the transcript (guarantees M/P/B closures).\n" +
                 "OFF = raw MFCC free-guess (the original behaviour).\n" +
                 "Press T (or controller B) at runtime to compare raw vs text-guided.")]
        public bool useTextGuidedMFCC = true;

        [TextArea(2, 4)]
        [Tooltip("Transcript for the TEST CLIP. Must match what the audio says.\n" +
                 "For live TTS this is supplied automatically from reply_text_done.")]
        public string testTranscript = "My mama bakes warm bread, and Papa pours more coffee for me.";

        [Range(0f, 1f)]
        [Tooltip("How hard to push the text-guided visemes.\n" +
                 "1 = pure one-hot (guaranteed closures but snappy/wide).\n" +
                 "0 = raw model (natural but weak closures).\n" +
                 "~0.6–0.8 keeps closures while staying natural.")]
        public float textGuidedStrength = 0.7f;

        [Range(0, 8)]
        [Tooltip("Crossfade window (frames) for text-guided mode.\n" +
                 "Ramps each viseme in/out instead of snapping between them.\n" +
                 "0 = hard steps, 3–5 ≈ as smooth as the raw model. 1 frame = 10 ms.")]
        public int textGuidedSmoothWindow = 4;

        [Tooltip("Show an on-screen label of the active mode (text-guided vs raw).")]
        public bool showModeLabel = true;

        [Header("Test Clip (optional)")]
        [Tooltip("If set and 'Play Test Clip On Start' is ON, this clip auto-plays " +
                 "using the Test Transcript above.")]
        public AudioClip testClip;
        public bool playTestClipOnStart = false;

        [Range(0.1f, 1f)]
        [Tooltip("Slow-motion playback for inspecting which sounds fail.\n" +
                 "1 = normal, 0.25 = quarter speed. Lip-sync stays in sync.\n" +
                 "(Audio pitch drops — that's fine, you're watching the lips.)")]
        public float playbackSpeed = 1f;

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
            public float[][]          visemes;    // [frame][15] — what is actually played
            public readonly float[][] rawProbs;   // original model output [frame][15]
            public readonly int[]     framePath;  // text-guided chosen viseme per frame (null = raw)

            public ClipTimeline(float[] times, float[][] raw, int[] path = null)
            { frameTimes = times; rawProbs = raw; framePath = path; visemes = raw; }
        }

        // ── Streaming utterance state (live, text-guided) ─────────────────
        // Set when reply_text_done arrives; consumed chunk-by-chunk by FeedAudioClip.
        private int[] _utteranceSeq;   // full viseme sequence for the current reply
        private int   _seqCursor;      // how far into the sequence we've consumed

        // Map from AudioClip instance → its timeline
        private readonly Dictionary<AudioClip, ClipTimeline> _timelines
            = new Dictionary<AudioClip, ClipTimeline>();

        private ClipTimeline _active;
        private AudioClip    _lastClip;
        private bool         _lastTextGuided = true;  // tracks useTextGuidedMFCC for live rebuild
        private float        _lastStrength   = 0.7f;  // tracks textGuidedStrength for live rebuild
        private int          _lastSmoothWindow = 4;   // tracks textGuidedSmoothWindow for live rebuild

        // Smoothed output buffer
        private readonly float[] _smoothed = new float[VisemePredictor.NUM_VISEMES];

        // ── Unity lifecycle ───────────────────────────────────────────────

        void Awake()
        {
            _audioSource = GetComponent<AudioSource>();
            _mfcc        = new MFCCExtractor();
        }

        void Start()
        {
            if (modelAsset == null)
            {
                Debug.LogWarning("[LipSync] modelAsset is NULL — MFCC mode disabled. " +
                                 "(Fine if you only want to test forced-alignment mode.)");
            }
            else
            {
                Debug.Log($"[LipSync] modelAsset assigned: {modelAsset.name}  backend: {backendType}");
                try
                {
                    _predictor = new VisemePredictor(modelAsset, backendType);
                    Debug.Log("[LipSync] ✓ Predictor ready. IsReady=" + _predictor.IsReady);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[LipSync] ✗ Predictor init failed: {ex.Message}\n{ex.StackTrace}");
                }
            }

            // Optionally auto-play a fixed test clip (uses the Test Transcript)
            if (playTestClipOnStart && testClip != null)
            {
                _audioSource.clip = testClip;
                FeedAudioClip(testClip, testTranscript);   // builds the text-guided MFCC timeline
                _audioSource.Play();
                Debug.Log($"[LipSync] ▶ Auto-playing test clip '{testClip.name}'. " +
                          "Press T to toggle text-guidance on/off.");
            }
        }

        void OnDestroy()
        {
            _predictor?.Dispose();
        }

        // On-screen label showing the active mode (visible in editor Game view + headset)
        void OnGUI()
        {
            if (!showModeLabel) return;

            string label = useTextGuidedMFCC
                ? "LIPSYNC: MFCC + TEXT-GUIDED (Viterbi)"
                : "LIPSYNC: MFCC RAW (free guess)";
            Color color = useTextGuidedMFCC ? Color.yellow : Color.cyan;

            var style = new GUIStyle
            {
                fontSize  = 30,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperLeft,
            };
            style.normal.textColor = color;

            // Drop-shadow for readability over the scene
            var shadow = new GUIStyle(style);
            shadow.normal.textColor = Color.black;
            GUI.Label(new Rect(22, 22, 1000, 60), label, shadow);
            GUI.Label(new Rect(20, 20, 1000, 60), label, style);

            var hint = new GUIStyle { fontSize = 16 };
            hint.normal.textColor = Color.white;
            GUI.Label(new Rect(22, 60, 1000, 40),
                      "T (or controller B) = toggle text-guidance on/off", hint);
        }

        // ── Public API (called by VoiceAPIController) ─────────────────────

        /// <summary>
        /// Pre-compute the viseme timeline for an incoming TTS AudioClip.
        /// Call this BEFORE the clip is enqueued for playback.
        /// Runs MFCC extraction + batch inference synchronously on the main thread.
        /// </summary>
        public void FeedAudioClip(AudioClip clip, string transcript = null)
        {
            if (clip == null) { Debug.LogWarning("[LipSync] FeedAudioClip: clip is NULL"); return; }
            Debug.Log($"[LipSync] FeedAudioClip called — '{clip.name}'  {clip.length:F2}s  {clip.frequency}Hz" +
                      (string.IsNullOrEmpty(transcript) ? "" : $"  | transcript: \"{transcript}\""));

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

            // 3. Batch Sentis inference  [T][15]  (the model's raw per-frame probabilities)
            float[][] rawProbs = _predictor.PredictBatch(mfccFrames);

            // 4. Build timeline with per-frame timestamps
            float   hopSec     = MFCCExtractor.HOP_LEN / (float)MFCCExtractor.TARGET_SR; // 0.01 s
            float[] frameTimes = new float[mfccFrames.Length];
            for (int i = 0; i < frameTimes.Length; i++)
                frameTimes[i] = i * hopSec;

            // ── Decide the per-frame viseme path (text-guided) ───────────────
            // Two ways to get text guidance:
            //   (a) full transcript passed in  → align the whole clip (test clip)
            //   (b) a live utterance is active → align THIS chunk to the next slice
            //       of the utterance sequence, advancing a shared cursor.
            int[]  framePath = null;
            string mode      = "raw MFCC";

            if (useTextGuidedMFCC && !string.IsNullOrEmpty(transcript))
            {
                int[] seq = TextToVisemeSequence(transcript);
                if (seq.Length > 0 && rawProbs.Length >= seq.Length)
                {
                    framePath = ViterbiAlign(rawProbs, seq, 0, true, out _);
                    mode = $"TEXT-GUIDED (full, {seq.Length} visemes)";
                }
            }
            else if (useTextGuidedMFCC && _utteranceSeq != null
                     && (_utteranceSeq.Length - _seqCursor) >= 3)   // enough sequence left to align
            {
                framePath = ViterbiAlign(rawProbs, _utteranceSeq, _seqCursor, false, out int endPos);
                mode = $"TEXT-GUIDED (stream, seq {_seqCursor}->{endPos}/{_utteranceSeq.Length})";
                _seqCursor = endPos;   // advance cursor for the next chunk
            }
            else if (useTextGuidedMFCC && _utteranceSeq != null)
            {
                // Sequence exhausted/stale for this audio → raw MFCC, don't park on a viseme.
                mode = $"raw MFCC (sequence exhausted at {_seqCursor}/{_utteranceSeq.Length})";
            }

            var timeline = new ClipTimeline(frameTimes, rawProbs, framePath);
            RebuildTimelineVisemes(timeline);   // builds blended + smoothed visemes

            lock (_timelines)
                _timelines[clip] = timeline;

            Debug.Log($"[LipSync] ✓ Timeline built — {mfccFrames.Length} frames  ({mode})");

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

        /// <summary>
        /// Begin a new spoken utterance with its full transcript (called when
        /// reply_text_done arrives). Builds the viseme sequence and resets the
        /// streaming cursor so the following audio chunks get text-guided.
        /// </summary>
        public void BeginUtterance(string transcript)
        {
            if (string.IsNullOrEmpty(transcript))
            {
                _utteranceSeq = null;
                _seqCursor    = 0;
                return;
            }
            _utteranceSeq = TextToVisemeSequence(transcript);
            _seqCursor    = 0;
            Debug.Log($"[LipSync] BeginUtterance — {_utteranceSeq.Length} visemes from: \"{transcript}\"");
        }

        /// <summary>Clear the streaming utterance (call on tts_done; optional).</summary>
        public void EndUtterance()
        {
            _utteranceSeq = null;
            _seqCursor    = 0;
        }

        // ── Update: serve CurrentVisemes each frame ───────────────────────

        void Update()
        {
            // ── Toggle text-guided MFCC on/off (T key on PC, B button in headset) ──
            bool tgKey = false;
#if ENABLE_INPUT_SYSTEM
            if (UnityEngine.InputSystem.Keyboard.current != null)
                tgKey = UnityEngine.InputSystem.Keyboard.current.tKey.wasPressedThisFrame;
#else
            tgKey = Input.GetKeyDown(KeyCode.T);
#endif
            bool tgBtn = false;
            try { tgBtn = OVRInput.GetDown(OVRInput.Button.Two); } catch { }
            if (tgKey || tgBtn)
            {
                useTextGuidedMFCC = !useTextGuidedMFCC;
                Debug.Log($"[LipSync] === TEXT-GUIDED MFCC → {(useTextGuidedMFCC ? "ON" : "OFF")} ===");
            }

            // If any text-guided parameter changed, rebuild the active timeline live
            if (useTextGuidedMFCC != _lastTextGuided
                || !Mathf.Approximately(textGuidedStrength, _lastStrength)
                || textGuidedSmoothWindow != _lastSmoothWindow)
            {
                _lastTextGuided   = useTextGuidedMFCC;
                _lastStrength     = textGuidedStrength;
                _lastSmoothWindow = textGuidedSmoothWindow;
                if (_active != null) RebuildTimelineVisemes(_active);
            }

            // Slow-motion playback (lip-sync timeline follows timeSamples, stays in sync)
            if (_audioSource != null && !Mathf.Approximately(_audioSource.pitch, playbackSpeed))
                _audioSource.pitch = playbackSpeed;

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

            // Audio is playing — check we have a timeline for this clip
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
            // Only needed in RAW mode — text-guided already guarantees PP from the text.
            if (!useTextGuidedMFCC && ppRescueBlend > 0f && _smoothed[8] > ppRescueThreshold)
            {
                float rescued = _smoothed[8] * ppRescueBlend;
                _smoothed[1]  = Mathf.Max(_smoothed[1], rescued);
            }

            Array.Copy(_smoothed, CurrentVisemes, VisemePredictor.NUM_VISEMES);
        }

        // ── Text-guided Viterbi alignment ─────────────────────────────────

        // Rebuild a timeline's played visemes from its raw probs + chosen path:
        //   text-guided ON + has framePath → blended (raw↔one-hot) + smoothed
        //   otherwise                      → raw model probabilities
        void RebuildTimelineVisemes(ClipTimeline tl)
        {
            if (tl == null) return;

            if (!useTextGuidedMFCC || tl.framePath == null)
            {
                tl.visemes = tl.rawProbs;   // raw free-guess
                return;
            }

            int[] path = tl.framePath;

            // Blend the chosen viseme with the raw probabilities so the result keeps
            // the model's natural softness/amplitude while guaranteeing the closures.
            //   weight = lerp(rawProb, oneHot, strength)
            float s = Mathf.Clamp01(textGuidedStrength);
            int T = tl.rawProbs.Length;
            int V = VisemePredictor.NUM_VISEMES;
            float[][] outVis = new float[T][];
            for (int t = 0; t < T; t++)
            {
                outVis[t] = new float[V];
                int chosen = path[t];
                for (int v = 0; v < V; v++)
                {
                    float raw    = tl.rawProbs[t][v];
                    float oneHot = (v == chosen) ? 1f : 0f;
                    outVis[t][v] = raw + s * (oneHot - raw);   // lerp(raw, oneHot, s)
                }
            }

            // Crossfade the hard Viterbi segment boundaries into gradual ramps,
            // so transitions look as smooth as the raw model (triangular blur).
            if (textGuidedSmoothWindow > 0)
                outVis = TemporalSmooth(outVis, textGuidedSmoothWindow);

            tl.visemes = outVis;
        }

        // Triangular moving-average over time, applied per viseme channel.
        // Turns the hard one-hot segment edges into smooth crossfades.
        static float[][] TemporalSmooth(float[][] vis, int w)
        {
            int T = vis.Length;
            if (T == 0) return vis;
            int V = vis[0].Length;
            float[][] outv = new float[T][];

            for (int t = 0; t < T; t++)
            {
                outv[t] = new float[V];
                float wsum = 0f;
                for (int d = -w; d <= w; d++)
                {
                    int tt = t + d;
                    if (tt < 0 || tt >= T) continue;
                    float weight = (w + 1) - Mathf.Abs(d);   // triangular: peak at centre
                    wsum += weight;
                    for (int v = 0; v < V; v++)
                        outv[t][v] += vis[tt][v] * weight;
                }
                if (wsum > 0f)
                    for (int v = 0; v < V; v++)
                        outv[t][v] /= wsum;
            }
            return outv;
        }

        // Convert a transcript to the ordered list of visemes the mouth passes
        // through. Letter-based (bilabials m/p/b → PP exactly, which is the
        // whole point). Consecutive duplicates are collapsed; sil bookends added.
        static int[] TextToVisemeSequence(string text)
        {
            var seq = new List<int>();
            seq.Add(0); // leading silence

            string lower = text.ToLowerInvariant();
            for (int i = 0; i < lower.Length; i++)
            {
                char c = lower[i];
                char next = (i + 1 < lower.Length) ? lower[i + 1] : '\0';

                // word boundary → silence anchor
                if (!char.IsLetter(c))
                {
                    if (seq[seq.Count - 1] != 0) seq.Add(0);
                    continue;
                }

                int v;
                // a few common digraphs first
                if (c == 's' && next == 'h') { v = 6; i++; }           // sh → CH
                else if (c == 'c' && next == 'h') { v = 6; i++; }      // ch → CH
                else if (c == 't' && next == 'h') { v = 3; i++; }      // th → TH
                else if (c == 'p' && next == 'h') { v = 2; i++; }      // ph → FF
                else if (c == 'c' && next == 'k') { v = 5; i++; }      // ck → kk
                else
                {
                    switch (c)
                    {
                        case 'm': case 'p': case 'b':            v = 1;  break; // PP (bilabial)
                        case 'f': case 'v':                      v = 2;  break; // FF
                        case 'd': case 't':                      v = 4;  break; // DD
                        case 'k': case 'c': case 'g': case 'q': case 'x': v = 5; break; // kk
                        case 'j':                                v = 6;  break; // CH
                        case 's': case 'z':                      v = 7;  break; // SS
                        case 'n': case 'l':                      v = 8;  break; // nn
                        case 'r':                                v = 9;  break; // RR
                        case 'a':                                v = 10; break; // aa
                        case 'e':                                v = 11; break; // E
                        case 'i': case 'y':                      v = 12; break; // ih
                        case 'o':                                v = 13; break; // oh
                        case 'u': case 'w':                      v = 14; break; // ou
                        case 'h':                                v = -1; break; // skip (no strong viseme)
                        default:                                 v = -1; break;
                    }
                }

                if (v < 0) continue;
                if (seq[seq.Count - 1] != v) seq.Add(v);  // collapse repeats
            }

            if (seq[seq.Count - 1] != 0) seq.Add(0); // trailing silence
            return seq.ToArray();
        }

        // Viterbi forced alignment: assign each of T frames to a position in the
        // viseme sequence (monotonic, stay-or-advance), maximising total log-prob.
        //
        //   startPos  : first sequence index this clip may use (the streaming cursor)
        //   forceEnd  : true  → must finish at the last sequence symbol (whole-clip)
        //               false → may finish anywhere ≥ startPos (one streamed chunk)
        //   endPos    : (out) the sequence index the last frame landed on — becomes
        //               the next chunk's startPos in streaming mode.
        //
        // Returns the chosen VISEME ID for each frame (length T).
        static int[] ViterbiAlign(float[][] probs, int[] seq, int startPos, bool forceEnd, out int endPos)
        {
            int T = probs.Length;
            int K = seq.Length;
            const float NEG = -1e9f;
            startPos = Mathf.Clamp(startPos, 0, K - 1);

            // score[t][j] over sequence positions j in [startPos, K-1]
            float[][] score = new float[T][];
            int[][]   back  = new int[T][];   // 0 = stayed (j), 1 = advanced (j-1)
            for (int t = 0; t < T; t++) { score[t] = new float[K]; back[t] = new int[K]; }

            float LP(int t, int j) => Mathf.Log(Mathf.Max(probs[t][seq[j]], 1e-8f));

            // Init: frame 0 must be at the cursor position
            for (int j = startPos; j < K; j++) score[0][j] = NEG;
            score[0][startPos] = LP(0, startPos);

            for (int t = 1; t < T; t++)
            {
                for (int j = startPos; j < K; j++)
                {
                    float stay = score[t - 1][j];
                    float adv  = (j > startPos) ? score[t - 1][j - 1] : NEG;
                    if (adv > stay) { score[t][j] = adv  + LP(t, j); back[t][j] = 1; }
                    else            { score[t][j] = stay + LP(t, j); back[t][j] = 0; }
                }
            }

            // Choose the ending sequence position
            int cur;
            if (forceEnd)
            {
                cur = K - 1;
            }
            else
            {
                cur = startPos;
                float best = score[T - 1][startPos];
                for (int j = startPos + 1; j < K; j++)
                    if (score[T - 1][j] > best) { best = score[T - 1][j]; cur = j; }
            }
            endPos = cur;

            // Backtrack
            int[] frameViseme = new int[T];
            for (int t = T - 1; t >= 0; t--)
            {
                frameViseme[t] = seq[cur];
                if (t > 0 && back[t][cur] == 1) cur--;
            }
            return frameViseme;
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
