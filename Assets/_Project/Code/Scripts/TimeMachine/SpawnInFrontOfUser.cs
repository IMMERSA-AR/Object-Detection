using UnityEngine;

public class SpawnInFrontOfUser : MonoBehaviour
{
    private void Start()
    {
        StartCoroutine(PlaceAfterTracking());
    }

    private System.Collections.IEnumerator PlaceAfterTracking()
    {
        yield return new WaitForSeconds(3f);

        if (Camera.main == null)
        {
            Debug.LogError("[SpawnInFront] Camera.main is null after 3s — cannot place time machine.");
            yield break;
        }

        Transform cam = Camera.main.transform;

        Vector3 forward = cam.forward;
        forward.y = 0f;
        forward.Normalize();

        Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;

        Vector3 pos = new Vector3(
            cam.position.x,
            cam.position.y - 3f,
            cam.position.z) + right * 1f;

        transform.position = pos;
        transform.rotation = Quaternion.Euler(0f, -90f, 0f);

        Debug.Log($"[SpawnInFront] Time machine placed at {pos}.");
    }
}
