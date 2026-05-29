using UnityEngine;

public class SpawnRelativeToUser : MonoBehaviour
{
    [Tooltip("Metres forward (+) or back (-) from the headset.")]
    public float forwardOffset = 0f;

    [Tooltip("Metres right (+) or left (-) from the headset.")]
    public float rightOffset = 0f;

    [Tooltip("Metres up (+) or down (-) from the headset.")]
    public float verticalOffset = -1.7f;

    private void Start()
    {
        StartCoroutine(PlaceAfterTracking());
    }

    private System.Collections.IEnumerator PlaceAfterTracking()
    {
        yield return new WaitForSeconds(3f);

        if (Camera.main == null)
        {
            Debug.LogError($"[SpawnRelativeToUser] Camera.main is null — cannot place {gameObject.name}.");
            yield break;
        }

        Transform cam = Camera.main.transform;

        Vector3 forward = cam.forward;
        forward.y = 0f;
        forward.Normalize();

        Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;

        Vector3 pos = cam.position
            + forward * forwardOffset
            + right   * rightOffset
            + Vector3.up * verticalOffset;

        transform.position = pos;

        Debug.Log($"[SpawnRelativeToUser] {gameObject.name} placed at {pos}.");
    }
}
