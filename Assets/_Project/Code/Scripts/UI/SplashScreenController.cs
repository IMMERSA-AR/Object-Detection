using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Meta Quest splash screen using OVROverlay (compositor layers).
/// Compositor layers render at native display resolution — pixel-perfect,
/// zero aliasing, same quality as Meta's own system UI.
///
/// ── SCENE HIERARCHY ──────────────────────────────────────────────────
///
///  [OVRCameraRig]
///    └── TrackingSpace
///          └── CenterEyeAnchor          ← drag into Inspector as eyeAnchor
///
///  [SplashScreen]  ← this script lives here
///    ├── BackgroundOverlay              ← OVROverlay component (see below)
///    └── LogoOverlay                   ← OVROverlay component (see below)
///
/// ── OVROverlay SETTINGS ───────────────────────────────────────────────
///
///  BackgroundOverlay
///    Overlay Type  : Overlay
///    Overlay Shape : Quad
///    Textures[0]   : a 4×4 solid-black Texture2D (create in Unity:
///                    right-click > Create > Texture, paint black)
///    Transform     : position (0, 0, 0.5)  scale (4, 4, 1)
///                    (make it a child of CenterEyeAnchor so it stays head-locked)
///
///  LogoOverlay
///    Overlay Type  : Overlay
///    Overlay Shape : Quad
///    Textures[0]   : your logo PNG (import as Sprite/Texture2D, RGBA32)
///    Transform     : position (0, 0, 0.49)  scale (0.6, 0.6, 1)
///                    (child of CenterEyeAnchor — slightly closer than background)
///
/// Both GameObjects should start INACTIVE in the scene; the script enables them.
/// </summary>
public class SplashScreenController : MonoBehaviour
{
    // ── Inspector ─────────────────────────────────────────────────────

    [Header("Meta XR — Compositor Layers")]
    [Tooltip("OVROverlay that covers the whole FOV with a solid dark background.")]
    public OVROverlay backgroundOverlay;

    [Tooltip("OVROverlay that shows your game logo.")]
    public OVROverlay logoOverlay;

    [Header("Anchor")]
    [Tooltip("Drag CenterEyeAnchor here so overlays are head-locked.")]
    public Transform eyeAnchor;

    [Header("Timing (seconds)")]
    public float fadeInDuration  = 0.8f;
    public float holdDuration    = 2.5f;
    public float fadeOutDuration = 0.9f;

    [Header("Logo Scale Animation")]
    [Tooltip("Logo starts at this scale fraction and grows to 1.")]
    [Range(0.3f, 0.9f)]
    public float logoStartScale = 0.55f;

    [Tooltip("Slight overshoot for a premium elastic pop.")]
    [Range(1f, 1.25f)]
    public float logoOvershoot = 1.07f;

    public float scaleInDuration = 0.55f;

    [Header("Background Color")]
    [Tooltip("Tint applied to the background overlay. Keep near-black for Meta Quest style.")]
    public Color backgroundTint = new Color(0.05f, 0.05f, 0.06f, 1f);

    [Header("Scene Transition")]
    public bool loadNextScene = false;
    [Tooltip("Build Settings name of the scene to load after the splash.")]
    public string nextSceneName = "StartScene";

    // Fires when the splash finishes — connect to ExperienceManager.ShowMenu() etc.
    public System.Action OnSplashComplete;

    // ── Private ───────────────────────────────────────────────────────

    private Vector3 _logoBaseScale;

    // ── Lifecycle ─────────────────────────────────────────────────────

    private void Awake()
    {
        // Both overlays start invisible and inactive
        if (backgroundOverlay != null)
        {
            backgroundOverlay.gameObject.SetActive(false);
            SetOverlayAlpha(backgroundOverlay, 0f);
            // Apply the dark background tint
            backgroundOverlay.colorScale = new Vector4(
                backgroundTint.r, backgroundTint.g, backgroundTint.b, 0f);
        }

        if (logoOverlay != null)
        {
            _logoBaseScale = logoOverlay.transform.localScale;
            logoOverlay.transform.localScale = _logoBaseScale * logoStartScale;
            logoOverlay.gameObject.SetActive(false);
            SetOverlayAlpha(logoOverlay, 0f);
        }
    }

    private void Start()
    {
        StartCoroutine(RunSplash());
    }

    // ── Main sequence ─────────────────────────────────────────────────

    private IEnumerator RunSplash()
    {
        // Wait for Quest head-tracking to give us a real position,
        // so overlays appear correctly anchored to the user's view.
        yield return StartCoroutine(WaitForTracking());

        // Parent overlays to the eye anchor so they stay head-locked
        if (eyeAnchor != null)
        {
            if (backgroundOverlay != null)
                backgroundOverlay.transform.SetParent(eyeAnchor, worldPositionStays: false);
            if (logoOverlay != null)
                logoOverlay.transform.SetParent(eyeAnchor, worldPositionStays: false);
        }

        backgroundOverlay?.gameObject.SetActive(true);
        logoOverlay?.gameObject.SetActive(true);

        // Run scale animation in parallel with the alpha fade-in
        StartCoroutine(AnimateLogoScale());
        yield return StartCoroutine(FadeOverlays(0f, 1f, fadeInDuration));

        yield return new WaitForSeconds(holdDuration);

        yield return StartCoroutine(FadeOverlays(1f, 0f, fadeOutDuration));

        backgroundOverlay?.gameObject.SetActive(false);
        logoOverlay?.gameObject.SetActive(false);

        OnSplashComplete?.Invoke();

        if (loadNextScene)
            SceneManager.LoadScene(nextSceneName);
    }

    // ── Head-tracking wait (matches ExperienceManager pattern) ────────

    private IEnumerator WaitForTracking()
    {
        float timeout = 3f, elapsed = 0f;

        while (elapsed < timeout)
        {
            yield return null;
            elapsed += Time.deltaTime;

            if (Camera.main != null && Camera.main.transform.position.sqrMagnitude > 0.01f)
                break;
        }

        yield return null; // one extra frame for stability
    }

    // ── Alpha fade (both overlays together) ───────────────────────────

    private IEnumerator FadeOverlays(float from, float to, float duration)
    {
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            // Smooth step for a more polished feel than linear
            float t = elapsed / duration;
            t = t * t * (3f - 2f * t);
            float alpha = Mathf.Lerp(from, to, t);

            SetOverlayAlpha(backgroundOverlay, alpha);
            SetOverlayAlpha(logoOverlay, alpha);
            yield return null;
        }

        SetOverlayAlpha(backgroundOverlay, to);
        SetOverlayAlpha(logoOverlay, to);
    }

    // ── Logo scale: ease-out cubic with a subtle overshoot ────────────

    private IEnumerator AnimateLogoScale()
    {
        if (logoOverlay == null) yield break;

        float elapsed = 0f;

        // Phase 1 — grow from start → overshoot (80 % of total time)
        float phase1 = scaleInDuration * 0.8f;
        while (elapsed < phase1)
        {
            elapsed += Time.deltaTime;
            float t = 1f - Mathf.Pow(1f - Mathf.Clamp01(elapsed / phase1), 3f);
            float s = Mathf.Lerp(logoStartScale, logoOvershoot, t);
            logoOverlay.transform.localScale = _logoBaseScale * s;
            yield return null;
        }

        // Phase 2 — settle back to 1.0 (remaining 20 %)
        elapsed = 0f;
        float phase2 = scaleInDuration * 0.2f;
        while (elapsed < phase2)
        {
            elapsed += Time.deltaTime;
            float t = elapsed / phase2;
            float s = Mathf.Lerp(logoOvershoot, 1f, t);
            logoOverlay.transform.localScale = _logoBaseScale * s;
            yield return null;
        }

        logoOverlay.transform.localScale = _logoBaseScale;
    }

    // ── Helper: set a compositor layer's alpha via colorScale ─────────

    private void SetOverlayAlpha(OVROverlay overlay, float alpha)
    {
        if (overlay == null) return;
        Vector4 c = overlay.colorScale;
        c.w = alpha;
        overlay.colorScale = c;
    }
}
