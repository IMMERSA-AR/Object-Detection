// ============================================================
//  MFCCExtractor.cs
//  Pure C# MFCC feature extractor — no native dependencies.
//
//  Pipeline per audio chunk:
//    resample → pre-emphasis → frame → Hamming → FFT
//    → power spectrum → Mel filterbank → log → DCT (13 MFCCs)
//    → delta + delta-delta  →  39-dim feature vector / frame
//
//  Parameters are deliberately matched to the Python training
//  script (LipSyncModel/train.py) so inference is consistent.
// ============================================================

using System;
using UnityEngine;

namespace LipSync
{
    public class MFCCExtractor
    {
        // ── Public constants (must match train.py) ────────────────────────
        public const int TARGET_SR   = 16000; // resample target Hz
        public const int FRAME_LEN   = 400;   // 25 ms @ 16 kHz
        public const int HOP_LEN     = 160;   // 10 ms @ 16 kHz
        public const int FFT_SIZE    = 512;   // next power-of-2 ≥ FRAME_LEN
        public const int NUM_MEL     = 26;    // mel filter bank size
        public const int NUM_MFCC    = 13;    // cepstral coefficients kept
        public const int DELTA_WIN   = 2;     // ±2 frame delta window
        public const int FEATURE_DIM = NUM_MFCC * 3; // 39 (mfcc+Δ+ΔΔ)

        // ── Pre-computed lookup tables ────────────────────────────────────
        private readonly float[]   _hamming;      // [FRAME_LEN]
        private readonly float[][] _melBank;      // [NUM_MEL][FFT_SIZE/2+1]
        private readonly float[][] _dct;          // [NUM_MFCC][NUM_MEL]

        // FFT scratch buffer — re-used across frames to avoid GC
        private readonly float[]   _fftBuf = new float[FFT_SIZE * 2];

        public MFCCExtractor()
        {
            _hamming = BuildHammingWindow(FRAME_LEN);
            _melBank = BuildMelFilterBank(NUM_MEL, FFT_SIZE, TARGET_SR, 300f, 8000f);
            _dct     = BuildDCTMatrix(NUM_MFCC, NUM_MEL);
        }

        // ── Main API ──────────────────────────────────────────────────────

        /// <summary>
        /// Extract MFCC feature vectors from raw PCM samples.
        /// </summary>
        /// <param name="samples">Mono float PCM in [-1,1].</param>
        /// <param name="sourceSampleRate">Original Hz (e.g. 44100).</param>
        /// <returns>[numFrames][FEATURE_DIM=39]</returns>
        public float[][] Extract(float[] samples, int sourceSampleRate)
        {
            // 1. Resample to TARGET_SR
            float[] s = sourceSampleRate == TARGET_SR
                ? samples
                : Resample(samples, sourceSampleRate, TARGET_SR);

            // 2. Pre-emphasis  y[n] = x[n] - 0.97·x[n-1]
            float[] e = PreEmphasis(s, 0.97f);

            // 3. Number of frames
            int numFrames = Math.Max(1, 1 + (e.Length - FRAME_LEN) / HOP_LEN);

            // 4. Per-frame processing
            int bins = FFT_SIZE / 2 + 1;
            float[][] raw = new float[numFrames][];

            for (int fi = 0; fi < numFrames; fi++)
            {
                int start = fi * HOP_LEN;

                // Clear FFT buffer and apply Hamming window (real part only)
                Array.Clear(_fftBuf, 0, _fftBuf.Length);
                int copyLen = Math.Min(FRAME_LEN, e.Length - start);
                for (int j = 0; j < copyLen; j++)
                    _fftBuf[j * 2] = e[start + j] * _hamming[j];

                // In-place radix-2 FFT
                FFT(_fftBuf, FFT_SIZE);

                // Power spectrum
                float[] power = new float[bins];
                for (int b = 0; b < bins; b++)
                {
                    float re = _fftBuf[b * 2];
                    float im = _fftBuf[b * 2 + 1];
                    power[b] = re * re + im * im;
                }

                // Mel filter bank energies (log-compressed)
                float[] logMel = new float[NUM_MEL];
                for (int m = 0; m < NUM_MEL; m++)
                {
                    float sum = 0f;
                    float[] filt = _melBank[m];
                    for (int b = 0; b < bins; b++)
                        sum += power[b] * filt[b];
                    logMel[m] = Mathf.Log(sum + 1e-10f);
                }

                // DCT → MFCCs
                float[] mfcc = new float[NUM_MFCC];
                for (int c = 0; c < NUM_MFCC; c++)
                {
                    float sum = 0f;
                    float[] row = _dct[c];
                    for (int m = 0; m < NUM_MEL; m++)
                        sum += row[m] * logMel[m];
                    mfcc[c] = sum;
                }

                raw[fi] = mfcc;
            }

            // 5. Append Δ and ΔΔ  →  39-dim output
            return AppendDeltas(raw);
        }

        // ── Static helpers ────────────────────────────────────────────────

        // Linear interpolation resampler (sufficient for speech)
        static float[] Resample(float[] input, int srcRate, int dstRate)
        {
            if (srcRate == dstRate) return input;
            double ratio  = (double)dstRate / srcRate;
            int    outLen = (int)Math.Floor(input.Length * ratio);
            float[] output = new float[outLen];
            for (int i = 0; i < outLen; i++)
            {
                double srcIdx = i / ratio;
                int    lo     = (int)srcIdx;
                int    hi     = Math.Min(lo + 1, input.Length - 1);
                float  t      = (float)(srcIdx - lo);
                output[i] = input[lo] * (1f - t) + input[hi] * t;
            }
            return output;
        }

        static float[] PreEmphasis(float[] s, float coeff)
        {
            float[] o = new float[s.Length];
            o[0] = s[0];
            for (int i = 1; i < s.Length; i++)
                o[i] = s[i] - coeff * s[i - 1];
            return o;
        }

        // Hamming window  w[n] = 0.54 - 0.46·cos(2π·n/(N-1))
        static float[] BuildHammingWindow(int n)
        {
            float[] w = new float[n];
            float   k = 2f * Mathf.PI / (n - 1);
            for (int i = 0; i < n; i++)
                w[i] = 0.54f - 0.46f * Mathf.Cos(k * i);
            return w;
        }

        // Triangular Mel filter bank
        static float[][] BuildMelFilterBank(int numFilters, int fftSize,
                                             int sr, float fLow, float fHigh)
        {
            int   bins    = fftSize / 2 + 1;
            float melLow  = HzToMel(fLow);
            float melHigh = HzToMel(fHigh);

            // numFilters + 2 equally-spaced mel break-points
            float[] melPts = new float[numFilters + 2];
            for (int i = 0; i < melPts.Length; i++)
                melPts[i] = melLow + (melHigh - melLow) * i / (numFilters + 1);

            // Convert break-points to FFT bin indices
            int[] binIdx = new int[numFilters + 2];
            for (int i = 0; i < melPts.Length; i++)
                binIdx[i] = (int)Math.Floor((fftSize + 1) * MelToHz(melPts[i]) / sr);

            float[][] bank = new float[numFilters][];
            for (int m = 1; m <= numFilters; m++)
            {
                bank[m - 1] = new float[bins];
                int lo = binIdx[m - 1], ctr = binIdx[m], hi = binIdx[m + 1];

                for (int b = lo; b < ctr && b < bins; b++)
                    bank[m - 1][b] = (b - lo + 1e-6f) / (ctr - lo + 1e-6f);

                for (int b = ctr; b <= hi && b < bins; b++)
                    bank[m - 1][b] = (hi - b + 1e-6f) / (hi - ctr + 1e-6f);
            }
            return bank;
        }

        // Orthonormal DCT-II matrix  (matches scipy.fft.dct norm='ortho')
        static float[][] BuildDCTMatrix(int numCeps, int numFilters)
        {
            float[][] d    = new float[numCeps][];
            float     n0   = Mathf.Sqrt(1f / numFilters);
            float     nRest = Mathf.Sqrt(2f / numFilters);

            for (int c = 0; c < numCeps; c++)
            {
                d[c] = new float[numFilters];
                float norm = (c == 0) ? n0 : nRest;
                for (int m = 0; m < numFilters; m++)
                    d[c][m] = norm * Mathf.Cos(Mathf.PI * c * (2 * m + 1) / (2f * numFilters));
            }
            return d;
        }

        // Add delta and delta-delta features  →  [T][39]
        static float[][] AppendDeltas(float[][] raw)
        {
            int        T      = raw.Length;
            float[][]  d1     = Delta(raw, DELTA_WIN);
            float[][]  d2     = Delta(d1,  DELTA_WIN);
            float[][]  result = new float[T][];
            for (int t = 0; t < T; t++)
            {
                result[t] = new float[FEATURE_DIM];
                Array.Copy(raw[t], 0, result[t], 0,             NUM_MFCC);
                Array.Copy(d1[t],  0, result[t], NUM_MFCC,      NUM_MFCC);
                Array.Copy(d2[t],  0, result[t], NUM_MFCC * 2,  NUM_MFCC);
            }
            return result;
        }

        // Numerical derivative via regression  Δx[t] = Σ n·(x[t+n]-x[t-n]) / 2Σn²
        static float[][] Delta(float[][] x, int win)
        {
            int T    = x.Length;
            int D    = x[0].Length;
            // Σ n² for n=1..win
            float denom = 0f;
            for (int n = 1; n <= win; n++) denom += n * n;
            denom *= 2f;

            float[][] d = new float[T][];
            for (int t = 0; t < T; t++)
            {
                d[t] = new float[D];
                for (int n = 1; n <= win; n++)
                {
                    int ahead  = Math.Min(t + n, T - 1);
                    int behind = Math.Max(t - n, 0);
                    for (int j = 0; j < D; j++)
                        d[t][j] += n * (x[ahead][j] - x[behind][j]);
                }
                for (int j = 0; j < D; j++)
                    d[t][j] /= denom;
            }
            return d;
        }

        static float HzToMel(float hz) => 2595f * Mathf.Log10(1f + hz / 700f);
        static float MelToHz(float mel) => 700f * (Mathf.Pow(10f, mel / 2595f) - 1f);

        // ── Cooley-Tukey radix-2 iterative FFT ───────────────────────────
        // buf: interleaved  [re₀, im₀, re₁, im₁, …]  length = n*2
        static void FFT(float[] buf, int n)
        {
            // Bit-reversal permutation
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j)
                {
                    Swap(ref buf[i * 2],     ref buf[j * 2]);
                    Swap(ref buf[i * 2 + 1], ref buf[j * 2 + 1]);
                }
            }

            // Butterfly passes
            for (int len = 2; len <= n; len <<= 1)
            {
                float ang = -2f * Mathf.PI / len;
                float wRe = Mathf.Cos(ang);
                float wIm = Mathf.Sin(ang);

                for (int i = 0; i < n; i += len)
                {
                    float curRe = 1f, curIm = 0f;
                    int   half  = len >> 1;

                    for (int j = 0; j < half; j++)
                    {
                        int u = (i + j) * 2;
                        int v = (i + j + half) * 2;

                        float uRe = buf[u],     uIm = buf[u + 1];
                        float vRe = buf[v] * curRe - buf[v + 1] * curIm;
                        float vIm = buf[v] * curIm + buf[v + 1] * curRe;

                        buf[u]     = uRe + vRe;
                        buf[u + 1] = uIm + vIm;
                        buf[v]     = uRe - vRe;
                        buf[v + 1] = uIm - vIm;

                        float nextRe = curRe * wRe - curIm * wIm;
                        curIm        = curRe * wIm + curIm * wRe;
                        curRe        = nextRe;
                    }
                }
            }
        }

        static void Swap(ref float a, ref float b) { float t = a; a = b; b = t; }
    }
}
