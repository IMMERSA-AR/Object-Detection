using UnityEngine;

public class ObjectStamper : MonoBehaviour
{
    [Header("Settings")]
    public GameObject cubePrefab;
    [SerializeField] private float sideOffset = 0.2f; // Distance from the chair (Nearer)

    private bool _hasSpawnedGlobal = false;

    public bool HasSpawned => _hasSpawnedGlobal;

    public void PlacePermanentCube(Vector3 chairPos, Quaternion rotation)
    {
        // 1. GLOBAL LOCK: If we already spawned one, stop immediately
        if (_hasSpawnedGlobal) return;

        // 2. Calculate offset (0.4 meters to the right instead of 1.0)
        // 'rotation * Vector3.right' ensures it stays relative to the chair's orientation
        Vector3 spawnPos = chairPos + (rotation * Vector3.right * sideOffset);

        // 3. Spawn the cube
        GameObject newCube = Instantiate(cubePrefab, spawnPos, Quaternion.identity);

        // 4. Set visual properties
        newCube.transform.localScale = new Vector3(0.2f, 0.2f, 0.2f); // 20cm cube
        newCube.name = "Permanent_Chair_Cube";

        // 5. Mark as spawned
        _hasSpawnedGlobal = true;

        Debug.Log($"[Stamper] Single cube spawned beside chair at: {spawnPos}");
    }

    // Optional: Call this if you want to allow spawning another cube later
    public void ResetStamper()
    {
        _hasSpawnedGlobal = false;
    }
}