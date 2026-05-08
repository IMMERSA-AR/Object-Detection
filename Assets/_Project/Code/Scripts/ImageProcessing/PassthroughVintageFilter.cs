using UnityEngine;

/// <summary>
/// Applies a "1918" vintage colour grade to the Meta XR passthrough feed.
/// Combines two Meta APIs that compose on top of each other:
///   1. <see cref="OVRPassthroughLayer.SetBrightnessContrastSaturation"/>
///      — dim, harden, desaturate the live camera feed.
///   2. <see cref="OVRPassthroughLayer.colorScale"/>
///      — per-channel warm tint that turns the now-grey image sepia.
///
/// Attach this component to the SAME GameObject that already has
/// OVRPassthroughLayer in your scene.  All values can be tweaked in the
/// Inspector while the app is running — changes apply on the next frame.
/// </summary>
[RequireComponent(typeof(OVRPassthroughLayer))]
public class PassthroughVintageFilter : MonoBehaviour
{
    [Header("Tone — desaturation / brightness / contrast")]

    [Tooltip("-1 = pure grayscale  •  0 = original colours.\n" +
             "-0.75 is a strong 'old photograph' starting point. Slide back toward 0 if it feels too washed out.")]
    [Range(-1f, 1f)] public float saturation = -0.90f;

    [Tooltip("Brightness offset.  -0.10 dims the room a little, as if it were lit by gas lamps.")]
    [Range(-1f, 1f)] public float brightness = -0.20f;

    [Tooltip("Contrast.  Slight positive (~0.10) gives the high-contrast feel of old film.")]
    [Range(-1f, 1f)] public float contrast = 0.20f;

    [Header("Warm Sepia Tint (multiplied after the tone pass)")]

    [Tooltip("Red channel multiplier — push above 1.0 for warmth.")]
    [Range(0f, 2f)] public float redScale = 1.15f;

    [Tooltip("Green channel multiplier.")]
    [Range(0f, 2f)] public float greenScale = 0.95f;

    [Tooltip("Blue channel multiplier — drop below 1.0 for sepia.")]
    [Range(0f, 2f)] public float blueScale = 0.65f;

    [Header("Runtime")]
    [Tooltip("If on, any Inspector change is pushed to the passthrough layer next frame. Turn off if you want to lock the look.")]
    public bool liveTweak = true;

    // ── private ──────────────────────────────────────────────────
    private OVRPassthroughLayer _layer;
    private float _lastSat, _lastBri, _lastCon;
    private float _lastR, _lastG, _lastB;

    private void Awake()
    {
        _layer = GetComponent<OVRPassthroughLayer>();
    }

    private void Start()
    {
        Apply();
    }

    private void Update()
    {
        if (!liveTweak) return;

        if (_lastSat != saturation ||
            _lastBri != brightness ||
            _lastCon != contrast ||
            _lastR != redScale ||
            _lastG != greenScale ||
            _lastB != blueScale)
        {
            Apply();
        }
    }

    /// <summary>
    /// Pushes the current Inspector values to the passthrough layer.
    /// Right-click the component in the Inspector to invoke manually.
    /// </summary>
    [ContextMenu("Apply Now")]
    public void Apply()
    {
        if (_layer == null) _layer = GetComponent<OVRPassthroughLayer>();
        if (_layer == null) return;

        // 1. Desaturation + brightness + contrast (single Meta call).
        //    This sets colorMapEditorType to ColorAdjustment internally.
        _layer.SetBrightnessContrastSaturation(brightness, contrast, saturation);

        // 2. Per-channel warm sepia tint, applied multiplicatively on top
        //    of the desaturated image.
        _layer.colorScale = new Vector4(redScale, greenScale, blueScale, 1f);
        _layer.colorOffset = Vector4.zero;

        _lastSat = saturation;
        _lastBri = brightness;
        _lastCon = contrast;
        _lastR = redScale;
        _lastG = greenScale;
        _lastB = blueScale;
    }

    /// <summary>
    /// Restores the unmodified passthrough feed (real colours).
    /// Right-click the component in the Inspector to invoke.
    /// </summary>
    [ContextMenu("Reset to Original Passthrough")]
    public void ResetPassthrough()
    {
        if (_layer == null) _layer = GetComponent<OVRPassthroughLayer>();
        if (_layer == null) return;
        _layer.DisableColorMap();
        _layer.colorScale = Vector4.one;
        _layer.colorOffset = Vector4.zero;
    }
}
