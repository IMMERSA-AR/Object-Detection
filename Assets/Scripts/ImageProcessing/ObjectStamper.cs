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

        // 1. --- CHANGED: Spawn Murad near the PLAYER'S RIGHT SIDE! ---
        Vector3 camPos = Camera.main.transform.position;
        Vector3 camRight = Camera.main.transform.right;
        camRight.y = 0; // Keep the direction flat on the floor

        // Spawn Murad exactly 0.5 meters to your right so he actually has to walk to the chair
        Vector3 spawnPos = camPos + (camRight.normalized * 0.5f);

        // 2. Optimized Raycast (Finds the floor height for his new starting position)
        RaycastHit groundHit;
        if (Physics.Raycast(spawnPos + Vector3.up * 2.0f, Vector3.down, out groundHit, 10.0f))
        {
            spawnPos.y = groundHit.point.y + heightOffset;
        }
        else
        {
            spawnPos.y = 0f + heightOffset;
        }

        // 3. Spawn Murad into the world
        GameObject character = Instantiate(characterPrefab, spawnPos, Quaternion.identity);
        character.name = "Murad_Character";

        // 4. --- NEW: Tell Murad to start walking! ---
        MuradController muradScript = character.GetComponent<MuradController>();
        if (muradScript != null)
        {
            // We pass the actual 3D chair position to his brain
            muradScript.WalkToChair(chairPos);
        }
        else
        {
            Debug.LogError("Murad prefab is missing the MuradController script!");
        }

        _hasSpawnedGlobal = true;

        // Turn off AI to save headset battery once he spawns
        ObjectDetector detector = FindAnyObjectByType<ObjectDetector>();
        if (detector != null)
        {
            detector.enabled = false;
            Debug.Log("AI Inference stopped to prevent freezing.");
        }
    }
}