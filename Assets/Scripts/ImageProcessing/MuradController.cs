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

    // --- NEW: Push him forward or backward to hit the cushion ---
    [Tooltip("Adjust forward/backward when seated — positive pushes him forward, negative pushes backward")]
    public float seatForwardOffset = 0f;

    private Vector3 _targetPosition;
    private bool _isWalkingToTarget = false;
    private bool _isSitting = false;
    private bool _rotationComplete = false;

    void Start()
    {
        if (animator == null)
            animator = GetComponent<Animator>();

        animator.SetBool("IsWalking", false);
        animator.SetBool("IsSitting", false);
    }

    void Update()
    {
        if (_isSitting || !_isWalkingToTarget)
            return;

        // Calculate flat direction to chair
        // ignoring Y axis
        Vector3 flatTarget = new Vector3(
            _targetPosition.x,
            transform.position.y,
            _targetPosition.z);

        float distance = Vector3.Distance(
            transform.position, flatTarget);

        if (distance > stoppingDistance)
        {
            // ── Phase 1: Walk toward chair ──
            animator.SetBool("IsWalking", true);
            animator.SetBool("IsSitting", false);

            // Smooth rotation toward chair
            Vector3 direction =
                (flatTarget - transform.position)
                .normalized;

            if (direction != Vector3.zero)
            {
                Quaternion targetRot =
                    Quaternion.LookRotation(direction);
                transform.rotation = Quaternion.Slerp(
                    transform.rotation,
                    targetRot,
                    rotationSpeed * Time.deltaTime);
            }

            // Move forward in facing direction
            // not directly to target
            // This prevents crabwalking
            transform.position +=
                transform.forward *
                moveSpeed * Time.deltaTime;
        }
        else
        {
            // ── Phase 2: Reached chair ──
            animator.SetBool("IsWalking", false);

            // 1. --- FIXED: Force him to look directly at the User (Camera) before sitting! ---
            if (Camera.main != null)
            {
                Vector3 lookAtUser = Camera.main.transform.position - transform.position;
                lookAtUser.y = 0; // Keep it flat so he doesn't tilt backwards
                transform.rotation = Quaternion.LookRotation(lookAtUser);
            }

            // 2. Snap position to chair AND apply the height adjustment
            Vector3 finalPos = new Vector3(
                _targetPosition.x,
                _targetPosition.y + seatHeightOffset,
                _targetPosition.z);

            transform.position = finalPos;

            // 3. Trigger sit animation
            animator.SetBool("IsSitting", true);

            _isWalkingToTarget = false;
            _isSitting = true;

            Debug.Log("[Murad] Sitting down!");
        }

    }

    public void WalkToChair(Vector3 chairLocation)
    {
        if (_isSitting) return;

        _targetPosition = chairLocation;
        _isWalkingToTarget = true;
        _rotationComplete = false;

        Debug.Log(
            $"[Murad] Walking to chair " +
            $"at {chairLocation}");
    }

    public void StandUp()
    {
        _isSitting = false;
        _isWalkingToTarget = false;
        animator.SetBool("IsSitting", false);
        animator.SetBool("IsWalking", false);
        Debug.Log("[Murad] Standing up");
    }

    // public void UpdateChairLocation(Vector3 newChairPos)
    // {
    //     // Calculate how far the chair moved from its last known spot
    //     float shiftDistance = Vector3.Distance(_targetPosition, newChairPos);

    //     // Only react if the chair moved more than 0.5 meters 
    //     // (This prevents him from jittering if the AI bounding box wobbles slightly)
    //     if (shiftDistance > 0.5f)
    //     {
    //         Debug.Log($"[Murad] Chair moved by {shiftDistance}m! Getting up to follow it...");
    //         _targetPosition = newChairPos;

    //         // If he is currently sitting down, tell him to stand up and walk to the new spot!
    //         if (_isSitting)
    //         {
    //             StandUp();
    //             WalkToChair(newChairPos);
    //         }
    //     }
    // }
}