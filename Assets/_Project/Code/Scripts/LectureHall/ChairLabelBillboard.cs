using UnityEngine;

/// <summary>
/// Keeps a world-space label facing the main camera every frame.
/// Attached automatically by ChairMeshDetector to each chair label.
/// </summary>
public class ChairLabelBillboard : MonoBehaviour
{
    private void LateUpdate()
    {
        if (Camera.main == null) return;
        transform.forward = Camera.main.transform.forward;
    }
}
