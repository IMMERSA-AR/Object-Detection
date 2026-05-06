using UnityEngine;
using Meta.XR;

public class ObjectStamper : MonoBehaviour
{
    [Header("Settings")]
    public GameObject characterPrefab;
    [SerializeField] private float sideOffset = 0.5f;

    [Header("Adjustments")]
    [Tooltip("Use negative numbers to move him down (e.g., -0.5)")]
    [SerializeField] private float heightOffset = 0.0f;

    private bool _hasSpawnedGlobal = false;
    public bool HasSpawned => _hasSpawnedGlobal;

    // Set by ExperienceManager when user picks a scene
    private ExperienceConfig _activeConfig;

    private EnvironmentRaycastManager _envRaycast;

    private void Awake()
    {
        _envRaycast = FindAnyObjectByType<EnvironmentRaycastManager>();
    }

    /// <summary>
    /// Called by ExperienceManager when a new experience is selected.
    /// Resets Murad so he can be re-spawned.
    /// </summary>
    public void ResetForNewExperience(ExperienceConfig config)
    {
        _activeConfig = config;
        _hasSpawnedGlobal = false;

        GameObject existing = GameObject.Find("Murad");
        if (existing != null)
            Destroy(existing);

        Debug.Log($"[ObjectStamper] Reset for: {(config != null ? config.experienceName : "null")}");
    }

    /// <summary>
    /// Called after the lecture ends — spawns Murad beside the player automatically,
    /// no YOLO chair detection needed.
    /// </summary>
    public void SpawnMuradAtPlayerSide(ExperienceConfig config)
    {
        if (_hasSpawnedGlobal) return;

        _activeConfig = config;

        if (characterPrefab == null)
        {
            Debug.LogError("[ObjectStamper] characterPrefab is not assigned — drag Murad prefab here.");
            return;
        }

        Transform cam = Camera.main.transform;

        // Spawn 1.2 m to the player's right, on the floor
        Vector3 flatRight = new Vector3(cam.right.x, 0f, cam.right.z).normalized;
        Vector3 spawnPos = cam.position + flatRight * 1.2f;
        spawnPos.y = FindRealFloorY(spawnPos, cam.position.y);

        // Face Murad toward the player
        Vector3 toPlayer = cam.position - spawnPos;
        toPlayer.y = 0f;
        Quaternion rot = toPlayer.sqrMagnitude > 0.001f
            ? Quaternion.LookRotation(toPlayer)
            : Quaternion.identity;

        GameObject character = Instantiate(characterPrefab, spawnPos, rot);
        character.name = "Murad";

        MuradController muradScript = character.GetComponent<MuradController>();
        if (muradScript == null)
        {
            Debug.LogError("[ObjectStamper] Murad prefab is missing MuradController! Add it to the prefab.");
            return;
        }

        if (config != null)
            muradScript.ApplyConfig(config);

        muradScript.StandUp();

        _hasSpawnedGlobal = true;

        if (ExperienceManager.Instance != null)
            ExperienceManager.Instance.OnMuradPlaced();

        Debug.Log($"[ObjectStamper] Murad spawned beside player at {spawnPos}");
    }

    public void PlacePermanentCharacter(Vector3 anchorPos, Quaternion rotation)
    {
        if (_hasSpawnedGlobal) return;

        float maxDist = _activeConfig != null ? _activeConfig.maxAnchorDistance : 3.0f;
        float distanceToUser = Vector3.Distance(Camera.main.transform.position, anchorPos);
        if (distanceToUser > maxDist)
        {
            Debug.Log($"[ObjectStamper] Anchor too far: {distanceToUser:F2}m");
            return;
        }

        // 1. Spawn position: player's right side
        Vector3 camPos = Camera.main.transform.position;
        Vector3 camRight = Camera.main.transform.right;
        camRight.y = 0;
        Vector3 spawnPos = camPos + (camRight.normalized * sideOffset);

        // 2. Find floor height using EnvironmentRaycastManager (real world mesh)
        float floorY = FindRealFloorY(spawnPos, camPos.y);
        spawnPos.y = floorY + heightOffset;

        // 3. Spawn Murad
        GameObject character = Instantiate(characterPrefab, spawnPos, Quaternion.identity);
        character.name = "Murad";

        Debug.Log($"[ObjectStamper] Murad spawned at {spawnPos}");

        MuradController muradScript = character.GetComponent<MuradController>();
        if (muradScript == null)
        {
            Debug.LogError("[ObjectStamper] Murad prefab is missing MuradController!");
            return;
        }

        // 4. Apply config and send him to anchor
        if (_activeConfig != null)
            muradScript.ApplyConfig(_activeConfig);

        MuradBehaviour behaviour = _activeConfig != null
            ? _activeConfig.muradBehaviour
            : MuradBehaviour.SitOnAnchor;

        switch (behaviour)
        {
            case MuradBehaviour.SitOnAnchor:
                muradScript.WalkToChair(anchorPos);
                break;

            case MuradBehaviour.StandBesideAnchor:
                float offset = _activeConfig != null ? _activeConfig.standBesideOffset : 0.5f;
                Vector3 toAnchor = (anchorPos - camPos);
                toAnchor.y = 0;
                toAnchor.Normalize();
                Vector3 besidePos = anchorPos + Quaternion.Euler(0, 90, 0) * toAnchor * offset;
                muradScript.WalkToAnchorAndStand(besidePos, anchorPos);
                break;
        }

        _hasSpawnedGlobal = true;

        // Notify manager
        if (ExperienceManager.Instance != null)
            ExperienceManager.Instance.OnMuradPlaced();

        // Stop AI to save battery
        ObjectDetector detector = FindAnyObjectByType<ObjectDetector>();
        if (detector != null)
        {
            detector.enabled = false;
            Debug.Log("[ObjectStamper] AI stopped after placement.");
        }
    }

    private float FindRealFloorY(Vector3 xzPos, float cameraY)
    {
        if (_envRaycast == null)
            return cameraY - 1.7f;

        Vector3 rayOrigin = new Vector3(xzPos.x, cameraY + 0.5f, xzPos.z);

        if (TryEnvRayDown(rayOrigin, out float y)) return y;

        float[] offsets = { 0.1f, -0.1f };
        foreach (float d in offsets)
        {
            if (TryEnvRayDown(rayOrigin + new Vector3(d, 0, 0), out y)) return y;
            if (TryEnvRayDown(rayOrigin + new Vector3(0, 0, d), out y)) return y;
        }

        return cameraY - 1.7f;
    }

    private bool TryEnvRayDown(Vector3 origin, out float hitY)
    {
        if (_envRaycast.Raycast(new Ray(origin, Vector3.down), out var hit, 5.0f))
        {
            if (Vector3.Dot(hit.normal, Vector3.up) > 0.5f)
            {
                hitY = hit.point.y;
                return true;
            }
        }
        hitY = 0f;
        return false;
    }
}