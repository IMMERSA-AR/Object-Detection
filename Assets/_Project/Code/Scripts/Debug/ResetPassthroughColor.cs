using UnityEngine;

public class ResetPassthroughColor : MonoBehaviour
{
    private void Start()
    {
        var layer = FindAnyObjectByType<OVRPassthroughLayer>();
        if (layer == null) { Debug.LogWarning("[PTReset] No OVRPassthroughLayer found."); return; }

        layer.DisableColorMap();
        layer.colorScale  = Vector4.one;
        layer.colorOffset = Vector4.zero;

        Debug.Log("[PTReset] Passthrough colour reset to normal.");
        Destroy(this);
    }
}
