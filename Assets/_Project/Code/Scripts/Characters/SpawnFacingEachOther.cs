using UnityEngine;

public class SpawnFacingEachOther : MonoBehaviour
{
    [Header("Characters")]
    [SerializeField] private GameObject characterA;
    [SerializeField] private GameObject characterB;

    [Header("Placement")]
    [Tooltip("How many metres in front of the user to place the pair.")]
    [SerializeField] private float forwardDistance = 2f;

    [Tooltip("How far apart the two characters are from each other (half on each side).")]
    [SerializeField] private float separationDistance = 1f;

    [Tooltip("Offset from the headset to floor level.")]
    [SerializeField] private float verticalOffset = -1.7f;

    private void Start()
    {
        StartCoroutine(PlaceAfterTracking());
    }

    private System.Collections.IEnumerator PlaceAfterTracking()
    {
        yield return new WaitForSeconds(3f);

        if (Camera.main == null)
        {
            Debug.LogError("[SpawnFacingEachOther] Camera.main is null.");
            yield break;
        }

        if (characterA == null || characterB == null)
        {
            Debug.LogError("[SpawnFacingEachOther] One or both characters are not assigned.");
            yield break;
        }

        Transform cam = Camera.main.transform;

        Vector3 forward = cam.forward;
        forward.y = 0f;
        forward.Normalize();

        Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;

        float floorY = cam.position.y + verticalOffset;

        // Place characters in front of user, offset left and right of centre
        Vector3 centre = cam.position + forward * forwardDistance;

        Vector3 posA = new Vector3(
            (centre - right * (separationDistance * 0.5f)).x,
            floorY,
            (centre - right * (separationDistance * 0.5f)).z);

        Vector3 posB = new Vector3(
            (centre + right * (separationDistance * 0.5f)).x,
            floorY,
            (centre + right * (separationDistance * 0.5f)).z);

        characterA.transform.position = posA;
        characterB.transform.position = posB;

        // Make them face each other
        characterA.transform.rotation = Quaternion.LookRotation(posB - posA);
        characterB.transform.rotation = Quaternion.LookRotation(posA - posB);

        Debug.Log($"[SpawnFacingEachOther] CharA at {posA}, CharB at {posB}.");
    }
}
