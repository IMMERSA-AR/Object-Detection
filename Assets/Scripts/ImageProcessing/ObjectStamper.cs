using UnityEngine;

public class ObjectStamper : MonoBehaviour
{
    [Header("Settings")]
    public GameObject characterPrefab;
    [SerializeField] private float sideOffset = 0.5f;

    // --- NEW: Add a slider to manually adjust his height ---
    [Header("Adjustments")]
    [Tooltip("Use negative numbers to move him down (e.g., -0.5)")]
    [SerializeField] private float heightOffset = 0.0f;

    private bool _hasSpawnedGlobal = false;
    public bool HasSpawned => _hasSpawnedGlobal;

    public void PlacePermanentCharacter(Vector3 chairPos, Quaternion rotation)
    {
        if (_hasSpawnedGlobal) return;

        float distanceToUser = Vector3.Distance(Camera.main.transform.position, chairPos);
        if (distanceToUser > 3.0f) return;

        float widerOffset = 0.0f;
        Vector3 spawnPos = chairPos + (rotation * Vector3.right * widerOffset);

        // 2. Optimized Raycast
        RaycastHit groundHit;

        // We shoot the ray slightly further away to avoid hitting the chair's collider
        if (Physics.Raycast(spawnPos + Vector3.up * 2.0f, Vector3.down, out groundHit, 10.0f))
        {
            // Apply the offset to the raycast hit
            spawnPos.y = groundHit.point.y + heightOffset;
        }
        else
        {
            // Apply the offset to the default zero height
            spawnPos.y = 0f + heightOffset;
        }

        // 3. Spawn
        GameObject character = Instantiate(characterPrefab, spawnPos, Quaternion.identity);
        character.name = "Murad_Character";

        // 4. Face User
        if (Camera.main != null)
        {
            Vector3 lookDir = Camera.main.transform.position - character.transform.position;
            lookDir.y = 0;
            character.transform.rotation = Quaternion.LookRotation(lookDir);
        }

        _hasSpawnedGlobal = true;

        ObjectDetector detector = FindAnyObjectByType<ObjectDetector>();
        if (detector != null)
        {
            detector.enabled = false;
            Debug.Log("AI Inference stopped to prevent freezing.");
        }

        Debug.Log($"Murad placed at height: {spawnPos.y}");
    }
}