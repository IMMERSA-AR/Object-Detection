using System;
using UnityEngine;
using Unity.InferenceEngine;

namespace LipSync
{
    // Runs the trained viseme ONNX model through Unity's Inference Engine.
    public class VisemePredictor : IDisposable
    {
        // Constants zay train.py
        public const int NUM_VISEMES = 15;//visemes from CC
        public const int CONTEXT_FRAMES = 5;    // 2 frames of context on each side
        public const int INPUT_DIM = MFCCExtractor.FEATURE_DIM * CONTEXT_FRAMES; // 200
        // Model input shape is [1, T, INPUT_DIM] (batch=1, sequence=T, features=200)
        private Model _model;
        private Worker _worker;
        private bool _disposed;

        public bool IsReady => _worker != null && !_disposed;

        //ModelAsset: viseme_model.onnx assigned in the Inspector
        // backend: GPUCompute on the Quest, CPU for editor testing but use on CPU.
        public VisemePredictor(ModelAsset modelAsset,
                               BackendType backend = BackendType.GPUCompute)
        {
            if (modelAsset == null)//no model in inspector
                throw new ArgumentNullException(nameof(modelAsset));

            _model = ModelLoader.Load(modelAsset);
            _worker = new Worker(_model, backend);
        }

        public float[][] PredictBatch(float[][] mfccFrames)
        {
            if (mfccFrames == null || mfccFrames.Length == 0)
                return Array.Empty<float[]>();

            int T = mfccFrames.Length;

            // Build the context-windowed flat array [T * INPUT_DIM]
            float[] inputData = BuildContextFeatures(mfccFrames);

            using var inputTensor = new Tensor<float>(new TensorShape(1, T, INPUT_DIM), inputData);
            _worker.Schedule(inputTensor);

            // PeekOutput gives a reference, DownloadToArray copies it from GPU to CPU.
            // Output is [1, T, 15], so the flat array has T*15 values (batch dim collapses).
            var rawOut = _worker.PeekOutput() as Tensor<float>;
            float[] flat = rawOut.DownloadToArray();  // blocks until the GPU is done

            float[][] result = new float[T][];
            for (int t = 0; t < T; t++)
            {
                result[t] = new float[NUM_VISEMES];
                Array.Copy(flat, t * NUM_VISEMES, result[t], 0, NUM_VISEMES);
            }
            return result;
        }

        // For each frame t, glue together frames [t-2, t-1, t, t+1, t+2].
        static float[] BuildContextFeatures(float[][] frames)
        {
            int T = frames.Length;
            int D = MFCCExtractor.FEATURE_DIM; // 40(mfcc+log energy)
            int half = CONTEXT_FRAMES / 2;

            float[] data = new float[T * INPUT_DIM];
            for (int t = 0; t < T; t++)
            {
                int outBase = t * INPUT_DIM;
                for (int c = -half; c <= half; c++)//loop on context frames
                {
                    int srcFrame = Math.Clamp(t + c, 0, T - 1);
                    Array.Copy(frames[srcFrame], 0,
                               data, outBase + (c + half) * D,
                               D);
                }
            }
            return data;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _worker?.Dispose();
            _disposed = true;
        }
    }
}
