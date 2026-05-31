// ============================================================
//  VisemePredictor.cs
//  Runs the trained ONNX lipsync model via Unity Inference Engine
//  (com.unity.ai.inference / formerly Sentis 2.x).
//
//  Input tensor  : [batch, CONTEXT_FRAMES * FEATURE_DIM] = [B, 200]
//  Output tensor : [batch, NUM_VISEMES]  = [B, 15]  (softmax probs)
// ============================================================

using System;
using UnityEngine;
using Unity.InferenceEngine;

namespace LipSync
{
    public class VisemePredictor : IDisposable
    {
        // ── Constants (must match train.py) ───────────────────────────────
        public const int NUM_VISEMES    = 15;
        public const int CONTEXT_FRAMES = 5;    // ±2 frame context window
        public const int INPUT_DIM      = MFCCExtractor.FEATURE_DIM * CONTEXT_FRAMES; // 200

        // Model input shape: [1, T, INPUT_DIM]  (batch=1, sequence=T, features=195)
        // Works for both BiLSTM and MLP exports — export_onnx.py wraps MLP to match.

        // ── Inference Engine state ────────────────────────────────────────
        private Model  _model;
        private Worker _worker;
        private bool   _disposed;

        public bool IsReady => _worker != null && !_disposed;

        /// <param name="modelAsset">viseme_model.onnx asset assigned in Inspector</param>
        /// <param name="backend">GPUCompute on Quest; CPU for editor testing.</param>
        public VisemePredictor(ModelAsset modelAsset,
                               BackendType backend = BackendType.GPUCompute)
        {
            if (modelAsset == null)
                throw new ArgumentNullException(nameof(modelAsset));

            _model  = ModelLoader.Load(modelAsset);
            _worker = new Worker(_model, backend);
        }

        // ── Public API ────────────────────────────────────────────────────

        /// <summary>
        /// Predict viseme probability distributions for every MFCC frame.
        /// All frames are batched into a single GPU dispatch — very fast.
        /// </summary>
        /// <param name="mfccFrames">[T][40] feature vectors from MFCCExtractor</param>
        /// <returns>[T][15] softmax probability arrays</returns>
        public float[][] PredictBatch(float[][] mfccFrames)
        {
            if (mfccFrames == null || mfccFrames.Length == 0)
                return Array.Empty<float[]>();

            int T = mfccFrames.Length;

            // Build context-windowed flat array  [T * INPUT_DIM]
            float[] inputData = BuildContextFeatures(mfccFrames);

            // Create input tensor [1, T, 195]  (batch=1, sequence, features)
            // BiLSTM expects 3-D input; MLP export is wrapped to match.
            using var inputTensor = new Tensor<float>(new TensorShape(1, T, INPUT_DIM), inputData);
            _worker.Schedule(inputTensor);

            // PeekOutput returns a reference — DownloadToArray() syncs GPU → CPU
            // Output shape is [1, T, 15] → flat array has T*15 elements (batch dim=1 collapses)
            var rawOut = _worker.PeekOutput() as Tensor<float>;
            float[] flat = rawOut.DownloadToArray();  // blocks until GPU done

            // Reshape flat [T*15] → [T][15]
            float[][] result = new float[T][];
            for (int t = 0; t < T; t++)
            {
                result[t] = new float[NUM_VISEMES];
                Array.Copy(flat, t * NUM_VISEMES, result[t], 0, NUM_VISEMES);
            }
            return result;
        }

        // ── Internal helpers ──────────────────────────────────────────────

        /// <summary>
        /// For each frame t, concatenate frames [t-2, t-1, t, t+1, t+2].
        /// Edge frames are padded by clamping to the boundary.
        /// Produces a flat float[] of length T * INPUT_DIM.
        /// </summary>
        static float[] BuildContextFeatures(float[][] frames)
        {
            int T    = frames.Length;
            int D    = MFCCExtractor.FEATURE_DIM; // 39
            int half = CONTEXT_FRAMES / 2;         // 2

            float[] data = new float[T * INPUT_DIM];
            for (int t = 0; t < T; t++)
            {
                int outBase = t * INPUT_DIM;
                for (int c = -half; c <= half; c++)
                {
                    int srcFrame = Math.Clamp(t + c, 0, T - 1);
                    Array.Copy(frames[srcFrame], 0,
                               data, outBase + (c + half) * D,
                               D);
                }
            }
            return data;
        }

        // ── IDisposable ───────────────────────────────────────────────────
        public void Dispose()
        {
            if (_disposed) return;
            _worker?.Dispose();
            _disposed = true;
        }
    }
}
