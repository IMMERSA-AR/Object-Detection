using UnityEngine;

/// <summary>
/// Eliminates the per-vertex light probe flicker that occurs when a character moves
/// through an AR scene. All renderers on this character share a single smoothed
/// probe anchor at torso height, so the lighting never jumps between probe regions
/// on a per-vertex or per-mesh-part basis.
/// </summary>
public class CharacterLightingStabilizer : MonoBehaviour
{
    [Tooltip("How fast the probe anchor tracks the character's actual position. " +
             "Lower values are smoother but lag slightly behind on fast movement.")]
    [Range(1f, 30f)]
    public float smoothSpeed = 8f;

    [Tooltip("Height above the root transform where the shared probe anchor sits. " +
             "0.9 m places it at torso height for a standing adult.")]
    public float anchorHeight = 0.9f;

    private Transform _anchor;
    private Renderer[] _renderers;

    void Start()
    {
        var anchorGO = new GameObject("_LightProbeAnchor") { hideFlags = HideFlags.HideInHierarchy };
        anchorGO.transform.SetParent(transform.parent);
        anchorGO.transform.position = transform.position + Vector3.up * anchorHeight;
        _anchor = anchorGO.transform;

        _renderers = GetComponentsInChildren<Renderer>(includeInactive: true);
        foreach (var r in _renderers)
            r.probeAnchor = _anchor;
    }

    void LateUpdate()
    {
        Vector3 target = transform.position + Vector3.up * anchorHeight;
        _anchor.position = Vector3.Lerp(_anchor.position, target, smoothSpeed * Time.deltaTime);
    }

    void OnDestroy()
    {
        if (_anchor != null)
            Destroy(_anchor.gameObject);
    }
}
