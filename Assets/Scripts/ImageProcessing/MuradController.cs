// using UnityEngine;

// public class MuradController : MonoBehaviour
// {
//     [Header("Components")]
//     public Animator animator;

//     [Header("Movement Settings")]
//     public float moveSpeed = 1.5f;
//     public float stoppingDistance = 0.6f;
//     public float rotationSpeed = 8f;

//     [Header("Sitting Adjustment")]
//     [Tooltip("Adjust height when seated — negative moves down")]
//     public float seatHeightOffset = 0f;

//     // --- NEW: Push him forward or backward to hit the cushion ---
//     [Tooltip("Adjust forward/backward when seated — positive pushes him forward, negative pushes backward")]
//     public float seatForwardOffset = 0f;

//     private Vector3 _targetPosition;
//     private bool _isWalkingToTarget = false;
//     private bool _isSitting = false;
//     private bool _rotationComplete = false;

//     void Start()
//     {
//         if (animator == null)
//             animator = GetComponent<Animator>();

//         animator.SetBool("IsStanding", true);
//         animator.SetBool("IsWalking", false);
//         animator.SetBool("IsSitting", false);
//     }

//     void Update()
//     {
//         if (_isSitting || !_isWalkingToTarget)
//             return;

//         // Calculate flat direction to chair
//         // ignoring Y axis
//         Vector3 flatTarget = new Vector3(
//             _targetPosition.x,
//             transform.position.y,
//             _targetPosition.z);

//         float distance = Vector3.Distance(
//             transform.position, flatTarget);

//         if (distance > stoppingDistance)
//         {
//             // ── Phase 1: Walk toward chair ──
//             animator.SetBool("IsStanding", false);
//             animator.SetBool("IsWalking", true);
//             animator.SetBool("IsSitting", false);

//             // Smooth rotation toward chair
//             Vector3 direction =
//                 (flatTarget - transform.position)
//                 .normalized;

//             if (direction != Vector3.zero)
//             {
//                 Quaternion targetRot =
//                     Quaternion.LookRotation(direction);
//                 transform.rotation = Quaternion.Slerp(
//                     transform.rotation,
//                     targetRot,
//                     rotationSpeed * Time.deltaTime);
//             }

//             // Move forward in facing direction
//             // not directly to target
//             // This prevents crabwalking
//             transform.position +=
//                 transform.forward *
//                 moveSpeed * Time.deltaTime;
//         }
//         else
//         {
//             // ── Phase 2: Reached chair ──
//             animator.SetBool("IsWalking", false);
//             animator.SetBool("IsStanding", false);
//             animator.SetBool("IsSitting", true);

//             // 1. --- FIXED: Force him to look directly at the User (Camera) before sitting! ---
//             if (Camera.main != null)
//             {
//                 Vector3 lookAtUser = Camera.main.transform.position - transform.position;
//                 lookAtUser.y = 0; // Keep it flat so he doesn't tilt backwards
//                 transform.rotation = Quaternion.LookRotation(lookAtUser);
//             }

//             // 2. Snap position to chair AND apply the height adjustment
//             Vector3 finalPos = new Vector3(
//                 _targetPosition.x,
//                 _targetPosition.y + seatHeightOffset,
//                 _targetPosition.z);

//             transform.position = finalPos;

//             // 3. Trigger sit animation
//             //animator.SetBool("IsSitting", true);

//             _isWalkingToTarget = false;
//             _isSitting = true;

//             Debug.Log("[Murad] Sitting down!");
//         }

//     }

//     public void WalkToChair(Vector3 chairLocation)
//     {
//         if (_isSitting) return;

//         _targetPosition = chairLocation;
//         _isWalkingToTarget = true;
//         _rotationComplete = false;

//         Debug.Log(
//             $"[Murad] Walking to chair " +
//             $"at {chairLocation}");
//     }

//     public void StandUp()
//     {
//         _isSitting = false;
//         _isWalkingToTarget = false;
//         animator.SetBool("IsSitting", false);
//         animator.SetBool("IsWalking", false);
//         animator.SetBool("IsStanding", true);
//         Debug.Log("[Murad] Standing up");
//     }

//     // public void UpdateChairLocation(Vector3 newChairPos)
//     // {
//     //     // Calculate how far the chair moved from its last known spot
//     //     float shiftDistance = Vector3.Distance(_targetPosition, newChairPos);

//     //     // Only react if the chair moved more than 0.5 meters 
//     //     // (This prevents him from jittering if the AI bounding box wobbles slightly)
//     //     if (shiftDistance > 0.5f)
//     //     {
//     //         Debug.Log($"[Murad] Chair moved by {shiftDistance}m! Getting up to follow it...");
//     //         _targetPosition = newChairPos;

//     //         // If he is currently sitting down, tell him to stand up and walk to the new spot!
//     //         if (_isSitting)
//     //         {
//     //             StandUp();
//     //             WalkToChair(newChairPos);
//     //         }
//     //     }
//     // }
// }

using UnityEngine;

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

    // Set by ExperienceManager when user picks a scene.
    // If null (no menu used), defaults to original chair/sit behaviour.
    private ExperienceConfig _activeConfig;

    /// <summary>
    /// Called by ExperienceManager when the user picks an experience from the menu.
    /// Resets Murad so he can be re-spawned for the new scene.
    /// </summary>
    public void ResetForNewExperience(ExperienceConfig config)
    {
        _activeConfig = config;
        _hasSpawnedGlobal = false;

        // Destroy existing Murad if he is already in the scene
        GameObject existing = GameObject.Find("Murad");
        if (existing != null)
            Destroy(existing);
    }

    public void PlacePermanentCharacter(Vector3 anchorPos, Quaternion rotation)
    {
        if (_hasSpawnedGlobal) return;

        // Use config distance limit if available, otherwise keep original 3m limit
        float maxDist = _activeConfig != null ? _activeConfig.maxAnchorDistance : 3.0f;
        float distanceToUser = Vector3.Distance(Camera.main.transform.position, anchorPos);
        if (distanceToUser > maxDist) return;

        // 1. Spawn Murad near the player's right side (unchanged from your original)
        Vector3 camPos = Camera.main.transform.position;
        Vector3 camRight = Camera.main.transform.right;
        camRight.y = 0;
        Vector3 spawnPos = camPos + (camRight.normalized * sideOffset);

        // 2. Find floor height (unchanged from your original)
        RaycastHit groundHit;
        if (Physics.Raycast(spawnPos + Vector3.up * 2.0f, Vector3.down, out groundHit, 10.0f))
            spawnPos.y = groundHit.point.y + heightOffset;
        else
            spawnPos.y = 0f + heightOffset;

        // 3. Spawn Murad (unchanged)
        GameObject character = Instantiate(characterPrefab, spawnPos, Quaternion.identity);
        character.name = "Murad";

        MuradController muradScript = character.GetComponent<MuradController>();
        if (muradScript == null)
        {
            Debug.LogError("Murad prefab is missing the MuradController script!");
            return;
        }

        // 4. Pass config so Murad knows what behaviour to use
        if (_activeConfig != null)
            muradScript.ApplyConfig(_activeConfig);

        // 5. Tell Murad what to do based on the active config behaviour
        MuradBehaviour behaviour = _activeConfig != null
            ? _activeConfig.muradBehaviour
            : MuradBehaviour.SitOnAnchor; // default: original chair behaviour

        switch (behaviour)
        {
            case MuradBehaviour.SitOnAnchor:
                // Original behaviour — walk to anchor and sit (lecture room / chair)
                muradScript.WalkToChair(anchorPos);
                break;

            case MuradBehaviour.StandBesideAnchor:
                // New behaviour — walk beside the anchor and stand facing it (obelisk)
                float offset = _activeConfig != null ? _activeConfig.standBesideOffset : 0.5f;
                Vector3 toAnchor = (anchorPos - camPos);
                toAnchor.y = 0;
                toAnchor.Normalize();
                Vector3 besidePos = anchorPos + Quaternion.Euler(0, 90, 0) * toAnchor * offset;
                muradScript.WalkToAnchorAndStand(besidePos, anchorPos);
                break;
        }

        _hasSpawnedGlobal = true;

        // Turn off AI to save headset battery once he spawns (unchanged)
        ObjectDetector detector = FindAnyObjectByType<ObjectDetector>();
        if (detector != null)
        {
            detector.enabled = false;
            Debug.Log("AI Inference stopped to prevent freezing.");
        }

        if (ExperienceManager.Instance != null)
            ExperienceManager.Instance.OnMuradPlaced();
    }
}