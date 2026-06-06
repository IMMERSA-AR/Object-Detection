

using System;
using System.Collections.Generic;
using UnityEngine;

namespace LipSync
{
    [RequireComponent(typeof(AudioSource))]
    public class CustomLipSyncContext : MonoBehaviour
    {
        [Header("Model")]
        [Tooltip("Assign viseme_model.onnx")]
        public Unity.InferenceEngine.ModelAsset modelAsset;

        [Tooltip("GPUCompute on device, CPU for editor fallback.")]
        public Unity.InferenceEngine.BackendType backendType = Unity.InferenceEngine.BackendType.GPUCompute;

        [Header("Smoothing")]
        [Range(0f, 0.95f)]
        [Tooltip("Exponential smoothing. 0 = instant, 0.9 = very smooth.")]
        public float smoothing = 0.55f;

        [Header("PP Rescue (m/n confusion fix)")]
        [Tooltip("When nn (nasal/lateral) fires above this, bleed some of its weight into PP.\n" +
                 "It fights the model mixing up /m/ and /n/. Set to 0 to disable. Try 0.30-0.45.")]
        [Range(0f, 1f)]
        public float ppRescueBlend = 0.35f;

        [Tooltip("Minimum nn probability before the PP rescue kicks in (keep above 0.10).")]
        [Range(0f, 0.5f)]
        public float ppRescueThreshold = 0.12f;

        [Header("Text-Guided MFCC (Viterbi alignment)")]
        [Tooltip("ON  = force the model output to follow the viseme sequence from the text\n" +
                 "      (guarantees the M/P/B closures).\n" +
                 "OFF = raw MFCC free guess (the original behaviour).\n" +
                 "Press T (or controller B) at runtime to compare the two.")]
        public bool useTextGuidedMFCC = true;

        [TextArea(2, 4)]
        [Tooltip("Transcript for the TEST CLIP, must match what the audio says.\n" +
                 "For live TTS this comes in automatically from reply_text_done.")]
        public string testTranscript = "My mama bakes warm bread, and Papa pours more coffee for me.";

        [Range(0f, 1f)]
        [Tooltip("How hard to push the text-guided visemes.\n" +
                 "1 = pure one-hot (closures guaranteed but snappy/wide), 0 = raw model.\n" +
                 "around 0.6-0.8 keeps the closures while staying natural.")]
        public float textGuidedStrength = 0.7f;

        [Range(0, 8)]
        [Tooltip("Crossfade window (frames) for text-guided mode, ramps visemes in/out\n" +
                 "instead of snapping. 0 = hard steps, 3-5 looks about as smooth as raw. 1 frame = 10 ms.")]
        public int textGuidedSmoothWindow = 4;

        [Tooltip("Show an on-screen label of the active mode (text-guided vs raw).")]
        public bool showModeLabel = true;

        [Header("Test Clip (optional)")]
        [Tooltip("If set and 'Play Test Clip On Start' is ON, this clip auto-plays with the Test Transcript above.")]
        public AudioClip testClip;
        public bool playTestClipOnStart = false;

        [Range(0.1f, 1f)]
        [Tooltip("Slow-mo playback for inspecting which sounds fail. 1 = normal, 0.25 = quarter speed.\n" +
                 "Lip-sync stays in sync (the pitch drops but you're only watching the lips).")]
        public float playbackSpeed = 1f;

        [Header("Debug")]
        public bool logTimings = false;

        // 15 viseme weights in [0,1], read by CustomLipSyncMorphTarget every frame
        public float[] CurrentVisemes { get; private set; } = new float[VisemePredictor.NUM_VISEMES];

        private MFCCExtractor _mfcc;
        private VisemePredictor _predictor;
        private AudioSource _audioSource;
        private float _nextStatusLog = 0f;   // rate-limit the periodic logs

        // everything we pre-compute for one clip
        private class ClipTimeline
        {
            public readonly float[] frameTimes;  // clip-local seconds per frame
            public float[][] visemes;            // [frame][15], what actually gets played
            public readonly float[][] rawProbs;  // original model output [frame][15]
            public readonly int[] framePath;     // text-guided chosen viseme per frame (null = raw)

            public ClipTimeline(float[] times, float[][] raw, int[] path = null)
            { frameTimes = times; rawProbs = raw; framePath = path; visemes = raw; }
        }

        // streaming utterance state for the live (text-guided) case.
        private int[] _utteranceSeq;   // full viseme sequence for the current reply
        private int _seqCursor;        // how far into the sequence we have consumed

        private readonly Dictionary<AudioClip, ClipTimeline> _timelines = new Dictionary<AudioClip, ClipTimeline>();

        private ClipTimeline _active;
        private AudioClip _lastClip;
        private bool _lastTextGuided = true;   // to rebuild live when the toggle changes
        private float _lastStrength = 0.7f;    // to rebuild live when strength changes
        private int _lastSmoothWindow = 4;     // to rebuild live when the window changes

        private readonly float[] _smoothed = new float[VisemePredictor.NUM_VISEMES];

        void Awake()
        {
            _audioSource = GetComponent<AudioSource>();
            _mfcc = new MFCCExtractor();
        }

        void Start()
        {
            if (modelAsset == null)
            {
                Debug.LogWarning("[LipSync] modelAsset is NULL, MFCC mode disabled. Drag viseme_model.onnx in.");
            }
            else
            {
                Debug.Log($"[LipSync] modelAsset assigned: {modelAsset.name}  backend: {backendType}");
                try
                {
                    _predictor = new VisemePredictor(modelAsset, backendType);
                    Debug.Log("[LipSync] Predictor ready. IsReady=" + _predictor.IsReady);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[LipSync] Predictor init failed: {ex.Message}\n{ex.StackTrace}");
                }
            }

            // optionally auto-play a fixed test clip (uses the Test Transcript)
            if (playTestClipOnStart && testClip != null)
            {
                _audioSource.clip = testClip;
                FeedAudioClip(testClip, testTranscript);
                _audioSource.Play();
                Debug.Log($"[LipSync] Auto-playing test clip '{testClip.name}'. Press T to toggle text-guidance.");
            }
        }

        void OnDestroy()
        {
            _predictor?.Dispose();
        }

        // on-screen label so i know which mode is active (editor and headset)
        void OnGUI()
        {
            if (!showModeLabel) return;

            string label = useTextGuidedMFCC ? "LIPSYNC: MFCC + TEXT-GUIDED (Viterbi)" : "LIPSYNC: MFCC RAW (free guess)";
            Color color = useTextGuidedMFCC ? Color.yellow : Color.cyan;

            var style = new GUIStyle
            {
                fontSize = 30,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperLeft,
            };
            style.normal.textColor = color;

            // black copy behind it so it's readable over the scene
            var shadow = new GUIStyle(style);
            shadow.normal.textColor = Color.black;
            GUI.Label(new Rect(22, 22, 1000, 60), label, shadow);
            GUI.Label(new Rect(20, 20, 1000, 60), label, style);

            var hint = new GUIStyle { fontSize = 16 };
            hint.normal.textColor = Color.white;
            GUI.Label(new Rect(22, 60, 1000, 40), "T (or controller B) = toggle text-guidance on/off", hint);
        }

        // Call this BEFORE the clip is queued for playback. transcript is optional:
        // pass it for the test clip, live chunks use the streaming cursor instead.
        public void FeedAudioClip(AudioClip clip, string transcript = null)
        {
            if (clip == null) { Debug.LogWarning("[LipSync] FeedAudioClip: clip is NULL"); return; }
            Debug.Log($"[LipSync] FeedAudioClip: '{clip.name}'  {clip.length:F2}s  {clip.frequency}Hz" +
                      (string.IsNullOrEmpty(transcript) ? "" : $"  | transcript: \"{transcript}\""));

            if (_predictor == null || !_predictor.IsReady)
            {
                Debug.LogError("[LipSync] Predictor not ready, skipping. (use CPU backend in editor)");
                return;
            }
            if (_timelines.ContainsKey(clip)) return; // already done

            float t0 = logTimings ? Time.realtimeSinceStartup : 0f;

            // pull the raw PCM (main thread only)
            float[] samples = new float[clip.samples * clip.channels];
            clip.GetData(samples, 0);
            if (clip.channels > 1)
                samples = DownmixToMono(samples, clip.channels);

            // MFCC frames [T][40]
            float[][] mfccFrames = _mfcc.Extract(samples, clip.frequency);
            if (mfccFrames.Length == 0)
            {
                Debug.LogWarning("[LipSync] No MFCC frames extracted, clip might be too short.");
                return;
            }

            // run the model on the whole clip -> raw per-frame probs [T][15]
            float[][] rawProbs = _predictor.PredictBatch(mfccFrames);

            // per-frame timestamps (10 ms hop)
            float hopSec = MFCCExtractor.HOP_LEN / (float)MFCCExtractor.TARGET_SR;
            float[] frameTimes = new float[mfccFrames.Length];
            for (int i = 0; i < frameTimes.Length; i++)
                frameTimes[i] = i * hopSec;

            // decide the per-frame viseme path (text-guided).
            // two cases: a full transcript was passed (test clip) -> align the whole clip,
            // or a live utterance is active -> align this chunk to the next slice of the
            // sequence and move the shared cursor forward.
            int[] framePath = null;
            string mode = "raw MFCC";

            if (useTextGuidedMFCC && !string.IsNullOrEmpty(transcript))
            {
                int[] seq = TextGuidedAlignment.TextToVisemeSequence(transcript);
                if (seq.Length > 0 && rawProbs.Length >= seq.Length)
                {
                    framePath = TextGuidedAlignment.ViterbiAlign(rawProbs, seq, 0, true, out _);
                    mode = $"TEXT-GUIDED (full, {seq.Length} visemes)";
                }
            }
            else if (useTextGuidedMFCC && _utteranceSeq != null
                     && (_utteranceSeq.Length - _seqCursor) >= 3)   // enough sequence left to align
            {
                framePath = TextGuidedAlignment.ViterbiAlign(rawProbs, _utteranceSeq, _seqCursor, false, out int endPos);
                mode = $"TEXT-GUIDED (stream, seq {_seqCursor}->{endPos}/{_utteranceSeq.Length})";
                _seqCursor = endPos;   // move the cursor for the next chunk
            }
            else if (useTextGuidedMFCC && _utteranceSeq != null)
            {
                // sequence is used up / stale for this audio -> fall back to raw so we
                // don't park on the last viseme
                mode = $"raw MFCC (sequence exhausted at {_seqCursor}/{_utteranceSeq.Length})";
            }

            var timeline = new ClipTimeline(frameTimes, rawProbs, framePath);
            RebuildTimelineVisemes(timeline);

            lock (_timelines)
                _timelines[clip] = timeline;

            Debug.Log($"[LipSync] Timeline built, {mfccFrames.Length} frames  ({mode})");

            if (logTimings)
                Debug.Log($"[LipSync] {clip.length:F2}s clip -> {mfccFrames.Length} frames in " +
                          $"{(Time.realtimeSinceStartup - t0) * 1000f:F1} ms");
        }

        // drop a clip's timeline once it will never play again
        public void ReleaseClip(AudioClip clip)
        {
            if (clip == null) return;
            lock (_timelines)
                _timelines.Remove(clip);
        }

        // Start a new utterance with its full transcript (called on reply_text_done).
        // Builds the viseme sequence and resets the streaming cursor so the audio chunks
        // that follow get text-guided.
        public void BeginUtterance(string transcript)
        {
            if (string.IsNullOrEmpty(transcript))
            {
                _utteranceSeq = null;
                _seqCursor = 0;
                return;
            }
            _utteranceSeq = TextGuidedAlignment.TextToVisemeSequence(transcript);
            _seqCursor = 0;
            Debug.Log($"[LipSync] BeginUtterance, {_utteranceSeq.Length} visemes from: \"{transcript}\"");
        }

        // clear the streaming utterance (call on tts_done)
        public void EndUtterance()
        {
            _utteranceSeq = null;
            _seqCursor = 0;
        }

        void Update()
        {
            // toggle text-guided on/off (T on the keyboard, B on the controller)
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
                Debug.Log($"[LipSync] TEXT-GUIDED MFCC -> {(useTextGuidedMFCC ? "ON" : "OFF")}");
            }

            // if any text-guided slider changed, rebuild the active timeline live
            if (useTextGuidedMFCC != _lastTextGuided
                || !Mathf.Approximately(textGuidedStrength, _lastStrength)
                || textGuidedSmoothWindow != _lastSmoothWindow)
            {
                _lastTextGuided = useTextGuidedMFCC;
                _lastStrength = textGuidedStrength;
                _lastSmoothWindow = textGuidedSmoothWindow;
                if (_active != null) RebuildTimelineVisemes(_active);
            }

            // slow-mo. the timeline reads timeSamples so it stays in sync with the pitch.
            if (_audioSource != null && !Mathf.Approximately(_audioSource.pitch, playbackSpeed))
                _audioSource.pitch = playbackSpeed;

            AudioClip currentClip = _audioSource.clip;

            // detect a clip change
            if (currentClip != _lastClip)
            {
                if (_lastClip != null)
                    ReleaseClip(_lastClip);

                _lastClip = currentClip;
                _active = null;

                if (currentClip != null)
                {
                    lock (_timelines)
                        _timelines.TryGetValue(currentClip, out _active);
                }
            }

            // nothing playing -> relax the face to zero
            if (!_audioSource.isPlaying)
            {
                Smooth(null);
                return;
            }

            // playing but no timeline for this clip
            if (_active == null)
            {
                // warn once a second so it doesn't flood the console
                if (Time.realtimeSinceStartup > _nextStatusLog)
                {
                    _nextStatusLog = Time.realtimeSinceStartup + 1f;
                    Debug.LogWarning("[LipSync] Audio is playing but no timeline for " +
                        $"'{_audioSource.clip?.name}'. Check FeedAudioClip ran before playback " +
                        "and the predictor is ready.");
                }
                Smooth(null);
                return;
            }

            // timeSamples -> clip-local time, then sample the timeline
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

        // binary search for the right frame, then lerp between the two neighbours
        static float[] SampleTimeline(ClipTimeline tl, float t)
        {
            float[] times = tl.frameTimes;
            int T = times.Length;
            if (T == 0) return null;

            if (t <= times[0]) return tl.visemes[0];
            if (t >= times[T - 1]) return tl.visemes[T - 1];

            int lo = 0, hi = T - 1;
            while (hi - lo > 1)
            {
                int mid = (lo + hi) >> 1;
                if (times[mid] <= t) lo = mid;
                else hi = mid;
            }

            float span = times[hi] - times[lo];
            float alpha = span > 0f ? (t - times[lo]) / span : 0f;

            float[] lerped = new float[VisemePredictor.NUM_VISEMES];
            float[] vLo = tl.visemes[lo];
            float[] vHi = tl.visemes[hi];
            for (int i = 0; i < VisemePredictor.NUM_VISEMES; i++)
                lerped[i] = vLo[i] + (vHi[i] - vLo[i]) * alpha;
            return lerped;
        }

        // exponential smoothing: s = a*s + (1-a)*target
        void Smooth(float[] target)
        {
            float a = smoothing;
            float b = 1f - a;
            for (int i = 0; i < VisemePredictor.NUM_VISEMES; i++)
            {
                float goal = target != null ? target[i] : 0f;
                _smoothed[i] = a * _smoothed[i] + b * goal;
            }

            // PP rescue: the model mixes up bilabial /m/ with /n/ because thier MFCCs
            // look almost the same. when nn fires strongly we bleed some of it into PP so
            // M/P/B still show a lip closure. not needed in text-guided mode, that already
            // puts PP from the text.
            if (!useTextGuidedMFCC && ppRescueBlend > 0f && _smoothed[8] > ppRescueThreshold)
            {
                float rescued = _smoothed[8] * ppRescueBlend;
                _smoothed[1] = Mathf.Max(_smoothed[1], rescued);
            }

            Array.Copy(_smoothed, CurrentVisemes, VisemePredictor.NUM_VISEMES);
        }

        // rebuild what gets played from the raw probs + the chosen path.
        // text-guided on + we have a path -> blend + smooth (see TextGuidedAlignment),
        // otherwise just play the raw model probs.
        void RebuildTimelineVisemes(ClipTimeline tl)
        {
            if (tl == null) return;

            if (!useTextGuidedMFCC || tl.framePath == null)
            {
                tl.visemes = tl.rawProbs;
                return;
            }

            tl.visemes = TextGuidedAlignment.BuildGuidedVisemes(
                tl.rawProbs, tl.framePath, textGuidedStrength, textGuidedSmoothWindow);
        }

        static float[] DownmixToMono(float[] interleaved, int channels)
        {
            int monoLen = interleaved.Length / channels;
            float[] mono = new float[monoLen];
            float inv = 1f / channels;
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
