using System.Collections.Generic;
using UnityEngine;

namespace LipSync
{

    public static class TextGuidedAlignment
    {

        public static int[] TextToVisemeSequence(string text)
        {
            var seq = new List<int>();
            seq.Add(0); // leading silence

            string lower = text.ToLowerInvariant();
            for (int i = 0; i < lower.Length; i++)
            {
                char c = lower[i];
                char next = (i + 1 < lower.Length) ? lower[i + 1] : '\0';

                // anything that isn't a letter is a word boundary -> sil anchor
                if (!char.IsLetter(c))
                {
                    if (seq[seq.Count - 1] != 0) seq.Add(0);
                    continue;
                }

                int v;
                // handle a few common digraphs first
                if (c == 's' && next == 'h') { v = 6; i++; }       // sh -> CH
                else if (c == 'c' && next == 'h') { v = 6; i++; }  // ch -> CH
                else if (c == 't' && next == 'h') { v = 3; i++; }  // th -> TH
                else if (c == 'p' && next == 'h') { v = 2; i++; }  // ph -> FF
                else if (c == 'c' && next == 'k') { v = 5; i++; }  // ck -> kk
                else
                {
                    switch (c)
                    {
                        case 'm': case 'p': case 'b': v = 1; break;  // PP (bilabial)
                        case 'f': case 'v': v = 2; break;            // FF
                        case 'd': case 't': v = 4; break;            // DD
                        case 'k': case 'c': case 'g': case 'q': case 'x': v = 5; break; // kk
                        case 'j': v = 6; break;                      // CH
                        case 's': case 'z': v = 7; break;            // SS
                        case 'n': case 'l': v = 8; break;            // nn
                        case 'r': v = 9; break;                      // RR
                        case 'a': v = 10; break;                     // aa
                        case 'e': v = 11; break;                     // E
                        case 'i': case 'y': v = 12; break;           // ih
                        case 'o': v = 13; break;                     // oh
                        case 'u': case 'w': v = 14; break;           // ou
                        case 'h': v = -1; break;                     // skip, no strong viseme
                        default: v = -1; break;
                    }
                }

                if (v < 0) continue;
                if (seq[seq.Count - 1] != v) seq.Add(v);  // collapse repeats
            }

            if (seq[seq.Count - 1] != 0) seq.Add(0); // trailing silence
            return seq.ToArray();
        }


        public static int[] ViterbiAlign(float[][] probs, int[] seq, int startPos, bool forceEnd, out int endPos)
        {
            int T = probs.Length;
            int K = seq.Length;
            const float NEG = -1e9f;
            startPos = Mathf.Clamp(startPos, 0, K - 1);

            // score[t][j] over sequence positions j from startPos..K-1
            float[][] score = new float[T][];
            int[][] back = new int[T][];   // 0 = stayed on j, 1 = advanced from j-1
            for (int t = 0; t < T; t++) { score[t] = new float[K]; back[t] = new int[K]; }

            float LP(int t, int j) => Mathf.Log(Mathf.Max(probs[t][seq[j]], 1e-8f));

            // frame 0 has to sit on the cursor position
            for (int j = startPos; j < K; j++) score[0][j] = NEG;
            score[0][startPos] = LP(0, startPos);

            for (int t = 1; t < T; t++)
            {
                for (int j = startPos; j < K; j++)
                {
                    float stay = score[t - 1][j];
                    float adv = (j > startPos) ? score[t - 1][j - 1] : NEG;
                    if (adv > stay) { score[t][j] = adv + LP(t, j); back[t][j] = 1; }
                    else { score[t][j] = stay + LP(t, j); back[t][j] = 0; }
                }
            }

            // pick where to end
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

            // walk back to read the per-frame viseme
            int[] frameViseme = new int[T];
            for (int t = T - 1; t >= 0; t--)
            {
                frameViseme[t] = seq[cur];
                if (t > 0 && back[t][cur] == 1) cur--;
            }
            return frameViseme;
        }


        public static float[][] BuildGuidedVisemes(float[][] rawProbs, int[] framePath,
                                                   float strength, int smoothWindow)
        {
            float s = Mathf.Clamp01(strength);
            int T = rawProbs.Length;
            int V = VisemePredictor.NUM_VISEMES;
            float[][] outVis = new float[T][];

            for (int t = 0; t < T; t++)
            {
                outVis[t] = new float[V];
                int chosen = framePath[t];
                for (int v = 0; v < V; v++)
                {
                    float raw = rawProbs[t][v];
                    float oneHot = (v == chosen) ? 1f : 0f;
                    outVis[t][v] = raw + s * (oneHot - raw);  // lerp(raw, oneHot, s)
                }
            }

            if (smoothWindow > 0)
                outVis = TemporalSmooth(outVis, smoothWindow);
            return outVis;
        }

        // triangular moving average over time, per viseme channel.
        // turns the hard one-hot edges into smooth crossfades.
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
                    float weight = (w + 1) - Mathf.Abs(d);   // triangular, peak in the middle
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
    }
}
