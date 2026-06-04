using System.Collections;
using UnityEngine;

/// <summary>
/// Hides a UI panel on startup and only shows it once passthrough is confirmed
/// running. This prevents the panel from flickering or disappearing during the
/// passthrough compositor initialization on Meta Quest.
///
/// Setup:
///  1. Add this script to any GameObject in the UISet scene (e.g. SceneSelector).
///  2. Drag the SceneSelector (or whichever panel to gate) into the Panel field.
/// </summary>
public class PassthroughReadyGate : MonoBehaviour
{
    [Tooltip("The UI panel to hide until passthrough is ready.")]
    public GameObject panel;

    [Tooltip("Maximum seconds to wait before showing the panel anyway.")]
    public float timeout = 5f;

    private void Start()
    {
        if (panel != null)
            panel.SetActive(false);

        StartCoroutine(WaitForPassthrough());
    }

    private IEnumerator WaitForPassthrough()
    {
        float elapsed = 0f;

        // Wait until the OVRPassthroughLayer reports it is running,
        // OR until the timeout expires — so the UI always appears eventually.
        OVRPassthroughLayer passthroughLayer = FindAnyObjectByType<OVRPassthroughLayer>();

        while (elapsed < timeout)
        {
            yield return null;
            elapsed += Time.deltaTime;

            // Passthrough is ready when the layer exists and is enabled
            if (passthroughLayer != null && passthroughLayer.isActiveAndEnabled)
                break;

            // Also exit early if passthrough cannot be found — no point waiting
            if (passthroughLayer == null && elapsed > 1f)
                break;
        }

        // One extra frame for the compositor to settle
        yield return null;
        yield return null;

        if (panel != null)
            panel.SetActive(true);
    }
}
