using UnityEngine;

/// <summary>
/// Applies a vintage colour grade to the Meta XR passthrough feed.
/// Attach to the same GameObject that has OVRPassthroughLayer.
/// Uncheck the component in the Inspector to restore original passthrough colours.
/// </summary>
[RequireComponent(typeof(OVRPassthroughLayer))]
public class PassthroughVintageFilter : MonoBehaviour
{
    [Header("Tone")]
    [Range(-1f, 1f)] public float saturation = 0f;
    [Range(-1f, 1f)] public float brightness = 0f;
    [Range(-1f, 1f)] public float contrast = 0f;

    [Header("Color Tint")]
    [Range(0f, 2f)] public float redScale = 1f;
    [Range(0f, 2f)] public float greenScale = 1f;
    [Range(0f, 2f)] public float blueScale = 1f;

    private OVRPassthroughLayer _layer;

    private void Awake() => _layer = GetComponent<OVRPassthroughLayer>();
    private void Start() => Apply();
    private void OnDisable() => ResetPassthrough();

    private void Update() => Apply();

    [ContextMenu("Apply Now")]
    public void Apply()
    {
        if (_layer == null) _layer = GetComponent<OVRPassthroughLayer>();
        if (_layer == null) return;
        _layer.SetBrightnessContrastSaturation(brightness, contrast, saturation);
        _layer.colorScale = new Vector4(redScale, greenScale, blueScale, 1f);
        _layer.colorOffset = Vector4.zero;
    }

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
