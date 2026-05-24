// ============================================================
//  CustomLipSyncMorphTarget.cs
//  Reads viseme weights from CustomLipSyncContext and drives
//  Reallusion CC character blend shapes.
// ============================================================

using System.Collections.Generic;
using UnityEngine;

namespace LipSync
{
    [RequireComponent(typeof(CustomLipSyncContext))]
    public class CustomLipSyncMorphTarget : MonoBehaviour
    {
        // ── Inspector ─────────────────────────────────────────────────────
        [Header("References")]
        [Tooltip("The CC face SkinnedMeshRenderer — usually CC_Base_Body.")]
        public SkinnedMeshRenderer faceMesh;

        [Header("Strength")]
        [Range(0f, 3f)]
        [Tooltip("Global mouth amplification applied to all visemes.")]
        public float mouthExaggeration = 1.5f;

        [Header("Per-Viseme Multipliers")]
        [Tooltip("Fine-tune individual visemes without changing the global scale.\n" +
                 "Index: 0=sil 1=PP 2=FF 3=TH 4=DD 5=kk 6=CH 7=SS 8=nn 9=RR\n" +
                 "       10=aa 11=E 12=ih 13=oh 14=ou\n" +
                 "Raise PP(1) to make M/P/B lips close more visibly.\n" +
                 "Raise oh(13) to make O more rounded.")]
        public float[] perVisemeMultiplier = new float[15]
        {
            1.0f,  // 0  sil
            2.0f,  // 1  PP  — M/P/B need stronger closure to be visible
            1.5f,  // 2  FF  — F/V dental
            1.0f,  // 3  TH
            1.0f,  // 4  DD
            1.0f,  // 5  kk
            1.2f,  // 6  CH
            1.0f,  // 7  SS
            1.0f,  // 8  nn
            1.0f,  // 9  RR
            1.2f,  // 10 aa
            1.0f,  // 11 E
            1.0f,  // 12 ih
            1.8f,  // 13 oh — O needs stronger rounding to stand out from V_Lip_Open
            1.5f,  // 14 ou
        };

        [Header("Smoothing Override")]
        [Tooltip("When PP (lip closure) probability exceeds this threshold,\n" +
                 "use the fast smoothing value instead — makes M/P/B snappier.")]
        [Range(0f, 1f)]
        public float ppClosureThreshold = 0.25f;

        [Tooltip("Smoothing used when PP fires above threshold (0 = instant).")]
        [Range(0f, 0.95f)]
        public float ppFastSmoothing = 0.15f;

        [Header("OVR Viseme → CC Blend Shape (index 0–14)")]
        [Tooltip("CC blend shape names driven by each OVR viseme slot.")]
        public string[] visemeBlendShapeNames = new string[15]
        {
            "V_None",            // 0  sil
            "V_Explosive",       // 1  PP    — p, b, m
            "V_Dental_Lip",      // 2  FF    — f, v
            "V_Lip_Open",        // 3  TH    — th, dh
            "V_Open",            // 4  DD    — d, t
            "Merged_Open_Mouth", // 5  kk    — k, g
            "V_Affricate",       // 6  CH    — ch, sh
            "V_Lip_Open",        // 7  SS    — s, z
            "V_Lip_Open",        // 8  nn    — n, l
            "V_Lip_Open",        // 9  RR    — r
            "Merged_Open_Mouth", // 10 aa    — ah, aw, ae
            "Mouth_Drop_Lower",  // 11 E     — eh, ey
            "Mouth_Drop_Lower",  // 12 ih    — ih, iy
            "V_Tight_O",         // 13 oh    — ao, ow
            "V_Tight",           // 14 ou    — uh, uw, w
        };

        [Header("Debug")]
        [Tooltip("Show full bar-chart overlay in Game view (Editor only).")]
        public bool showDebugOverlay = true;

        // ── Private ───────────────────────────────────────────────────────
        private CustomLipSyncContext _context;
        private int[]                _bsIndices;
        private HashSet<int>         _managedIndices = new HashSet<int>();
        private Dictionary<int, float> _frameWeights = new Dictionary<int, float>();

        // ── Unity lifecycle ───────────────────────────────────────────────

        void Start()
        {
            _context = GetComponent<CustomLipSyncContext>();

            if (faceMesh == null)
                faceMesh = GetComponentInChildren<SkinnedMeshRenderer>();

            if (faceMesh == null)
            {
                Debug.LogError("[LipSync] CustomLipSyncMorphTarget: no faceMesh assigned.");
                enabled = false;
                return;
            }

            // Ensure multiplier array is always the right length
            if (perVisemeMultiplier == null || perVisemeMultiplier.Length != 15)
            {
                perVisemeMultiplier = new float[15];
                for (int i = 0; i < 15; i++) perVisemeMultiplier[i] = 1f;
            }

            CacheBlendShapeIndices();
        }

        void LateUpdate()
        {
            if (faceMesh == null || _context == null || _bsIndices == null) return;

            float[] visemes = _context.CurrentVisemes;

            // ── PP fast-smoothing override ────────────────────────────────
            // If the model is firing PP strongly right now, temporarily swap
            // the context's smoothing to make the lip closure snappier.
            // We read the RAW timeline value (before smoothing) via the
            // public API already available.
            float rawPP = _context.CurrentVisemes[1]; // smoothed PP
            if (rawPP >= ppClosureThreshold)
                _context.smoothing = Mathf.Lerp(_context.smoothing, ppFastSmoothing, 0.3f);

            // ── Accumulate blend-shape contributions (SUM strategy) ───────
            _frameWeights.Clear();
            for (int v = 0; v < _bsIndices.Length; v++)
            {
                int bsIdx = _bsIndices[v];
                if (bsIdx < 0) continue;

                float mult         = (v < perVisemeMultiplier.Length) ? perVisemeMultiplier[v] : 1f;
                float contribution = visemes[v] * 100f * mouthExaggeration * mult;

                if (_frameWeights.TryGetValue(bsIdx, out float current))
                    _frameWeights[bsIdx] = current + contribution;
                else
                    _frameWeights[bsIdx] = contribution;
            }

            // ── Apply to mesh ─────────────────────────────────────────────
            foreach (int bsIdx in _managedIndices)
            {
                float w = _frameWeights.TryGetValue(bsIdx, out float val)
                    ? Mathf.Clamp(val, 0f, 100f)
                    : 0f;
                faceMesh.SetBlendShapeWeight(bsIdx, w);
            }
        }

        // ── Internal helpers ──────────────────────────────────────────────

        void CacheBlendShapeIndices()
        {
            var mesh = faceMesh.sharedMesh;
            _bsIndices = new int[visemeBlendShapeNames.Length];
            _managedIndices.Clear();

            for (int i = 0; i < visemeBlendShapeNames.Length; i++)
            {
                string name = visemeBlendShapeNames[i];
                int    idx  = mesh.GetBlendShapeIndex(name);
                _bsIndices[i] = idx;

                if (idx >= 0)
                    _managedIndices.Add(idx);
                else
                    Debug.LogWarning($"[LipSync] Blend shape '{name}' not found on " +
                                     $"'{mesh.name}'. Viseme slot {i} will be silent.");
            }

            Debug.Log($"[LipSync] Cached {_managedIndices.Count} unique blend shapes on '{mesh.name}'.");
        }

        // ── Editor debug overlay ──────────────────────────────────────────
#if UNITY_EDITOR
        static readonly string[] _names =
            { "sil","PP ","FF ","TH ","DD ","kk ","CH ","SS ","nn ","RR ","aa ","E  ","ih ","oh ","ou " };

        // Colours: neutral, PP highlighted orange, oh highlighted cyan
        static readonly Color[] _barColors = {
            Color.gray,                      // 0 sil
            new Color(1f,   0.5f, 0f),       // 1 PP  — orange
            Color.yellow,                    // 2 FF
            Color.yellow,                    // 3 TH
            Color.yellow,                    // 4 DD
            Color.yellow,                    // 5 kk
            Color.yellow,                    // 6 CH
            Color.yellow,                    // 7 SS
            Color.yellow,                    // 8 nn
            Color.yellow,                    // 9 RR
            new Color(0.4f, 1f,   0.4f),     // 10 aa — green
            new Color(0.4f, 1f,   0.4f),     // 11 E
            new Color(0.4f, 1f,   0.4f),     // 12 ih
            new Color(0.4f, 0.9f, 1f),       // 13 oh — cyan
            new Color(0.4f, 0.9f, 1f),       // 14 ou
        };

        void OnGUI()
        {
            if (!Application.isPlaying || !showDebugOverlay || _context == null) return;

            float[] v = _context.CurrentVisemes;
            if (v == null) return;

            // ── Panel background ──────────────────────────────────────────
            const float X      = 10f;
            const float Y      = 90f;   // below LipSyncTester buttons
            const float ROW_H  = 18f;
            const float LABEL_W = 36f;
            const float BAR_MAX = 160f;
            const float PANEL_W = LABEL_W + BAR_MAX + 50f;
            float panelH = 15 * ROW_H + 28f;

            // Semi-transparent background
            GUI.color = new Color(0, 0, 0, 0.55f);
            GUI.DrawTexture(new Rect(X - 4, Y - 4, PANEL_W + 8, panelH), Texture2D.whiteTexture);
            GUI.color = Color.white;

            // Title
            GUI.color = Color.white;
            GUI.Label(new Rect(X, Y, PANEL_W, 20f), "<b>[LipSync Visemes]</b>");

            // ── One row per viseme ────────────────────────────────────────
            for (int i = 0; i < 15; i++)
            {
                float rowY   = Y + 20f + i * ROW_H;
                float weight = v[i];                     // 0..1 smoothed
                float barW   = weight * BAR_MAX;

                // Label
                GUI.color = weight > 0.1f ? _barColors[i] : new Color(0.6f, 0.6f, 0.6f);
                GUI.Label(new Rect(X, rowY, LABEL_W, ROW_H - 1), _names[i]);

                // Bar
                if (barW > 0f)
                {
                    GUI.color = _barColors[i];
                    GUI.DrawTexture(
                        new Rect(X + LABEL_W, rowY + 2f, barW, ROW_H - 5f),
                        Texture2D.whiteTexture);
                }

                // Numeric value
                GUI.color = Color.white;
                GUI.Label(
                    new Rect(X + LABEL_W + BAR_MAX + 2f, rowY, 44f, ROW_H),
                    weight.ToString("F2"));
            }

            GUI.color = Color.white;
        }
#endif
    }
}
