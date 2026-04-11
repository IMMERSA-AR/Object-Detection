using UnityEngine;

public class ObjectStamper : MonoBehaviour
{
    [Header("Settings")]
    public GameObject characterPrefab; // Drag 'Murad' prefab here in Inspector
    [SerializeField] private float sideOffset = 0.5f; // Murad stands 0.5m to the side

    private bool _hasSpawnedGlobal = false;
    public bool HasSpawned => _hasSpawnedGlobal;

    public void PlacePermanentCharacter(Vector3 chairPos, Quaternion rotation)
    {
        if (_hasSpawnedGlobal) return;

        // 1. Move him further out to ensure he doesn't hit the chair's collider
        float widerOffset = 0.9f;
        Vector3 spawnPos = chairPos + (rotation * Vector3.right * widerOffset);

        // 2. INCREASE RAYCAST HEIGHT
        RaycastHit groundHit;
        // Start the ray 2 meters ABOVE the chair to ensure it captures the floor properly
        // We shoot down a long distance (10m) to be safe
        if (Physics.Raycast(spawnPos + Vector3.up * 2.0f, Vector3.down, out groundHit, 10.0f))
        {
            // Set height to exactly where the ray hit the ground
            spawnPos.y = groundHit.point.y;
        }
        else
        {
            // If the Raycast fails to find anything, snap him to y = 0 
            // In most Meta Quest Passthrough scenes, 0 is the floor level
            spawnPos.y = 0f;
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
        Debug.Log($"Murad placed at height: {spawnPos.y}");
    }
}