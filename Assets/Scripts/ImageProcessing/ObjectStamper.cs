// using UnityEngine;

// public class ObjectStamper : MonoBehaviour
// {
//     [Header("Settings")]
//     public GameObject characterPrefab;
//     [SerializeField] private float sideOffset = 0.5f;

//     // --- NEW: Add a slider to manually adjust his height ---
//     [Header("Adjustments")]
//     [Tooltip("Use negative numbers to move him down (e.g., -0.5)")]
//     [SerializeField] private float heightOffset = 0.0f;

//     private bool _hasSpawnedGlobal = false;
//     public bool HasSpawned => _hasSpawnedGlobal;

//     public void PlacePermanentCharacter(Vector3 chairPos, Quaternion rotation)
//     {
//         if (_hasSpawnedGlobal) return;

//         float distanceToUser = Vector3.Distance(Camera.main.transform.position, chairPos);
//         if (distanceToUser > 3.0f) return;

//         // 1. --- CHANGED: Spawn Murad near the PLAYER'S RIGHT SIDE! ---
//         Vector3 camPos = Camera.main.transform.position;
//         Vector3 camRight = Camera.main.transform.right;
//         camRight.y = 0; // Keep the direction flat on the floor

//         // Spawn Murad exactly 0.5 meters to your right so he actually has to walk to the chair
//         Vector3 spawnPos = camPos + (camRight.normalized * 0.5f);

//         // 2. Optimized Raycast (Finds the floor height for his new starting position)
//         RaycastHit groundHit;
//         if (Physics.Raycast(spawnPos + Vector3.up * 2.0f, Vector3.down, out groundHit, 10.0f))
//         {
//             spawnPos.y = groundHit.point.y + heightOffset;
//         }
//         else
//         {
//             spawnPos.y = 0f + heightOffset;
//         }

//         // 3. Spawn Murad into the world
//         GameObject character = Instantiate(characterPrefab, spawnPos, Quaternion.identity);
//         character.name = "Murad";

//         // 4. --- NEW: Tell Murad to start walking! ---
//         MuradController muradScript = character.GetComponent<MuradController>();
//         if (muradScript != null)
//         {
//             // We pass the actual 3D chair position to his brain
//             muradScript.WalkToChair(chairPos);
//         }
//         else
//         {
//             Debug.LogError("Murad prefab is missing the MuradController script!");
//         }

//         _hasSpawnedGlobal = true;

//         // Turn off AI to save headset battery once he spawns
//         ObjectDetector detector = FindAnyObjectByType<ObjectDetector>();
//         if (detector != null)
//         {
//             detector.enabled = false;
//             Debug.Log("AI Inference stopped to prevent freezing.");
//         }
//     }
// }

// // using UnityEngine;

// // public class ObjectStamper : MonoBehaviour
// // {
// //     [Header("Settings")]
// //     [Tooltip("Drag the Mourad from your SCENE HIERARCHY into here, NOT the project window!")]
// //     public GameObject sceneMurad;
// //     [SerializeField] private float sideOffset = 0.5f;

// //     [Header("Adjustments")]
// //     [Tooltip("Use negative numbers to move him down (e.g., -0.5)")]
// //     [SerializeField] private float heightOffset = 0.0f;

// //     private bool _hasSpawnedGlobal = false;
// //     public bool HasSpawned => _hasSpawnedGlobal;

// //     public void PlacePermanentCharacter(Vector3 chairPos, Quaternion rotation)
// //     {
// //         if (_hasSpawnedGlobal) return;

// //         float distanceToUser = Vector3.Distance(Camera.main.transform.position, chairPos);
// //         if (distanceToUser > 3.0f) return;

// //         // 1. Calculate spawn position on player's right
// //         Vector3 camPos = Camera.main.transform.position;
// //         Vector3 camRight = Camera.main.transform.right;
// //         camRight.y = 0;
// //         Vector3 spawnPos = camPos + (camRight.normalized * sideOffset); // Used sideOffset here!

// //         // 2. Find the floor
// //         RaycastHit groundHit;
// //         if (Physics.Raycast(spawnPos + Vector3.up * 2.0f, Vector3.down, out groundHit, 10.0f))
// //         {
// //             spawnPos.y = groundHit.point.y + heightOffset;
// //         }
// //         else
// //         {
// //             spawnPos.y = 0f + heightOffset;
// //         }

// //         // 3. --- THE MAGIC FIX: Teleport and Wake Up! ---
// //         if (sceneMurad != null)
// //         {
// //             sceneMurad.transform.position = spawnPos;
// //             sceneMurad.transform.rotation = Quaternion.identity;
// //             sceneMurad.SetActive(true); // Wake him up!

// //             // 4. Tell Mourad to walk
// //             MuradController muradScript = sceneMurad.GetComponent<MuradController>();
// //             if (muradScript != null)
// //             {
// //                 muradScript.WalkToChair(chairPos);
// //             }
// //             else
// //             {
// //                 Debug.LogError("Mourad is missing the MuradController script!");
// //             }
// //         }
// //         else
// //         {
// //             Debug.LogError("You forgot to drag Mourad into the SceneMurad slot on the Manager!");
// //         }

// //         _hasSpawnedGlobal = true;

// //         // 5. Turn off AI Inference
// //         ObjectDetector detector = FindAnyObjectByType<ObjectDetector>();
// //         if (detector != null)
// //         {
// //             detector.enabled = false;
// //             Debug.Log("AI Inference stopped to prevent freezing.");
// //         }
// //     }
// // }



using UnityEngine;

public class MuradController : MonoBehaviour
{
    [Header("Components")]
    public Animator animator;

    [Header("Movement Settings")]
    public float moveSpeed = 1.5f;
    public float stoppingDistance = 0.6f;
    public float rotationSpeed = 8f;

    [Header("Sitting Adjustment")]
    [Tooltip("Adjust height when seated — negative moves down")]
    public float seatHeightOffset = 0f;

    [Tooltip("Adjust forward/backward when seated — positive pushes him forward, negative pushes backward")]
    public float seatForwardOffset = 0f;

    private Vector3 _targetPosition;
    private bool _isWalkingToTarget = false;
    private bool _isSitting = false;
    private bool _rotationComplete = false;

    // ── Config-driven behaviour ──────────────────────────────────────
    // Set by ObjectStamper via ApplyConfig() before walking starts.
    // Defaults to SitOnAnchor so existing behaviour is fully preserved.
    private MuradBehaviour _behaviour = MuradBehaviour.SitOnAnchor;
    private Vector3 _facingTarget;      // Only used by StandBesideAnchor
    private bool _hasFacingTarget = false;
    private bool _isStandingAtAnchor = false; // True when standing is locked
    private Vector3 _lockedStandPosition;
    private Quaternion _lockedStandRotation;

    void Start()
    {
        if (animator == null)
            animator = GetComponent<Animator>();

        animator.SetBool("IsStanding", true);
        animator.SetBool("IsWalking", false);
        animator.SetBool("IsSitting", false);
    }

    void Update()
    {
        // ── Lock standing position every frame (prevents Quest drift) ──
        if (_isStandingAtAnchor)
        {
            transform.position = _lockedStandPosition;
            transform.rotation = _lockedStandRotation;
            return;
        }

        if (_isSitting || !_isWalkingToTarget)
            return;

        // Calculate flat direction to target — ignoring Y axis (unchanged)
        Vector3 flatTarget = new Vector3(
            _targetPosition.x,
            transform.position.y,
            _targetPosition.z);

        float distance = Vector3.Distance(transform.position, flatTarget);

        if (distance > stoppingDistance)
        {
            // ── Phase 1: Walk toward target (unchanged) ──
            animator.SetBool("IsStanding", false);
            animator.SetBool("IsWalking", true);
            animator.SetBool("IsSitting", false);

            Vector3 direction = (flatTarget - transform.position).normalized;

            if (direction != Vector3.zero)
            {
                Quaternion targetRot = Quaternion.LookRotation(direction);
                transform.rotation = Quaternion.Slerp(
                    transform.rotation, targetRot, rotationSpeed * Time.deltaTime);
            }

            transform.position += transform.forward * moveSpeed * Time.deltaTime;
        }
        else
        {
            // ── Phase 2: Arrived — execute the configured behaviour ──
            animator.SetBool("IsWalking", false);

            if (_behaviour == MuradBehaviour.StandBesideAnchor)
            {
                ArriveAndStand();
            }
            else
            {
                ArriveAndSit(); // original sit behaviour, unchanged
            }
        }
    }

    // ── Original sit logic — untouched ──────────────────────────────
    private void ArriveAndSit()
    {
        animator.SetBool("IsStanding", false);
        animator.SetBool("IsSitting", true);

        if (Camera.main != null)
        {
            Vector3 lookAtUser = Camera.main.transform.position - transform.position;
            lookAtUser.y = 0;
            transform.rotation = Quaternion.LookRotation(lookAtUser);
        }

        Vector3 finalPos = new Vector3(
            _targetPosition.x,
            _targetPosition.y + seatHeightOffset,
            _targetPosition.z);

        transform.position = finalPos;

        _isWalkingToTarget = false;
        _isSitting = true;

        Debug.Log("[Murad] Sitting down!");
    }

    // ── New stand-beside logic ───────────────────────────────────────
    private void ArriveAndStand()
    {
        animator.SetBool("IsStanding", true);
        animator.SetBool("IsWalking", false);
        animator.SetBool("IsSitting", false);

        // Face the anchor object (e.g. the obelisk)
        Quaternion standRot = transform.rotation;
        if (_hasFacingTarget)
        {
            Vector3 toAnchor = _facingTarget - transform.position;
            toAnchor.y = 0;
            if (toAnchor.sqrMagnitude > 0.001f)
                standRot = Quaternion.LookRotation(toAnchor);
        }

        // Lock position and rotation so Quest drift can't move him
        _lockedStandPosition = new Vector3(
            _targetPosition.x,
            _targetPosition.y,
            _targetPosition.z);
        _lockedStandRotation = standRot;

        transform.position = _lockedStandPosition;
        transform.rotation = _lockedStandRotation;

        _isWalkingToTarget = false;
        _isStandingAtAnchor = true;

        Debug.Log("[Murad] Standing beside anchor.");
    }

    // ── Public API ───────────────────────────────────────────────────

    /// <summary>
    /// Called by ObjectStamper after reading the ExperienceConfig.
    /// Safe to skip — defaults to original sit behaviour if never called.
    /// </summary>
    public void ApplyConfig(ExperienceConfig config)
    {
        _behaviour = config.muradBehaviour;
        seatHeightOffset = config.heightOffset;
        seatForwardOffset = config.forwardOffset;
    }

    /// <summary>
    /// Original method — walk to chair and sit. Kept exactly as-is.
    /// </summary>
    public void WalkToChair(Vector3 chairLocation)
    {
        if (_isSitting) return;

        _behaviour = MuradBehaviour.SitOnAnchor;
        _targetPosition = chairLocation;
        _isWalkingToTarget = true;
        _rotationComplete = false;

        Debug.Log($"[Murad] Walking to chair at {chairLocation}");
    }

    /// <summary>
    /// New method for StandBesideAnchor — walk to walkTarget, then face facingTarget.
    /// </summary>
    public void WalkToAnchorAndStand(Vector3 walkTarget, Vector3 facingTarget)
    {
        _behaviour = MuradBehaviour.StandBesideAnchor;
        _targetPosition = walkTarget;
        _facingTarget = facingTarget;
        _hasFacingTarget = true;
        _isWalkingToTarget = true;
        _isStandingAtAnchor = false;

        Debug.Log($"[Murad] Walking to stand beside anchor at {walkTarget}");
    }

    public void StandUp()
    {
        _isSitting = false;
        _isStandingAtAnchor = false;
        _isWalkingToTarget = false;
        animator.SetBool("IsSitting", false);
        animator.SetBool("IsWalking", false);
        animator.SetBool("IsStanding", true);
        Debug.Log("[Murad] Standing up");
    }
}
