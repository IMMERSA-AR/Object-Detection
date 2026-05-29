using UnityEngine;

public class TimePortalLogic : MonoBehaviour
{
    [Header("Obelisk Detection")]
    [Tooltip("Drag the ObeliskDetector GameObject here.")]
    public ObeliskYOLODetector obeliskDetector;

    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag("Player")) return;

        if (obeliskDetector != null)
            obeliskDetector.ToggleCharacters();
        else
            Debug.LogWarning("[TimePortal] obeliskDetector not assigned in Inspector.");
    }
}