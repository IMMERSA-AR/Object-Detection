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

    private Vector3 _targetPosition;
    private bool _isWalkingToTarget = false;
    private bool _isSitting = false;

    void Start()
    {
        // Search root and all children — Animator may not be on the root object.
        if (animator == null)
            animator = GetComponentInChildren<Animator>();

        if (animator == null)
        {
            Debug.LogWarning("[MuradController] No Animator found on '" + gameObject.name +
                             "' or its children — MuradController disabled on this object. " +
                             "If this is a Manager/LectureHallManager, remove the MuradController component from it in the Inspector.");
            enabled = false;
            return;
        }

        // Only drive the standing idle state when this component is actively running
        // (i.e. NOT when LectureHallManager has disabled it for the seated phase).
        // Calling SetBool here was overriding the IsSitting = true that
        // LectureHallManager sets right after Instantiate, making Murad T-pose.
        if (!enabled) return;

        animator.SetBool("IsStanding", true);
        animator.SetBool("IsWalking", false);
        animator.SetBool("IsSitting", false);
    }

    void Update()
    {
        if (animator == null) return;
        if (_isSitting || !_isWalkingToTarget) return;

        Vector3 flatTarget = new Vector3(
            _targetPosition.x,
            transform.position.y,
            _targetPosition.z);

        float distance = Vector3.Distance(transform.position, flatTarget);

        if (distance > stoppingDistance)
        {
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
            animator.SetBool("IsWalking", false);
            ArriveAndSit();
        }
    }

    private void ArriveAndSit()
    {
        animator.SetBool("IsStanding", false);
        animator.SetBool("IsSitting", true);

        // Do NOT rotate toward the user here — the chair orientation is already
        // set by LectureHallManager at spawn time and must be preserved.
        // Murad faces the front of the room (away from the user) while seated,
        // exactly like the other students. He turns to face the user only when
        // he stands up and walks over after the lecture ends.

        transform.position = new Vector3(
            _targetPosition.x,
            _targetPosition.y + seatHeightOffset,
            _targetPosition.z);

        _isWalkingToTarget = false;
        _isSitting = true;
        Debug.Log("[Murad] Sitting down!");
    }

    public void WalkToChair(Vector3 chairLocation)
    {
        if (_isSitting) return;
        _targetPosition = chairLocation;
        _isWalkingToTarget = true;
        Debug.Log($"[Murad] Walking to chair at {chairLocation}");
    }

    public void StandUp()
    {
        _isSitting = false;
        _isWalkingToTarget = false;
        animator.SetBool("IsSitting", false);
        animator.SetBool("IsWalking", false);
        animator.SetBool("IsStanding", true);
        Debug.Log("[Murad] Standing up");
    }
}
