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
    public float seatHeightOffset = 0f;

    private Vector3 _targetPosition;
    private bool _movingToTarget = false;
    private bool _isSittingOnChair = false;

    void Start()
    {
        if (animator == null)   // This condition try to find animator in the children 
        {
            animator = GetComponentInChildren<Animator>();
        }

        if (animator == null)
        {
            Debug.LogWarning("[MuradController] No animator found on the object or its children");
            enabled = false;
            return;
        }

        if (!enabled)
            return;

        animator.SetBool("IsStanding", true);
        animator.SetBool("IsWalking", false);
        animator.SetBool("IsSitting", false);
    }

    void Update()
    {
        if (animator == null)
        {
            Debug.LogWarning("[MuradController] Animator is not assigned");
            return;
        }
        if (_isSittingOnChair || !_movingToTarget)
        {
            return;
        }

        Vector3 flatTarget = new Vector3(_targetPosition.x, transform.position.y, _targetPosition.z);
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
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, rotationSpeed * Time.deltaTime);
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
        transform.position = new Vector3(_targetPosition.x, _targetPosition.y + seatHeightOffset, _targetPosition.z);
        _movingToTarget = false;
        _isSittingOnChair = true;
        Debug.Log("Character [Murad] is sitting down");
    }

    public void WalkToChair(Vector3 chairLocation)
    {
        if (_isSittingOnChair)
        {
            return;
        }
        _targetPosition = chairLocation;
        _movingToTarget = true;
        Debug.Log("Character [Murad] is walking to the chair");
    }

    public void StandUp()
    {
        _isSittingOnChair = false;
        _movingToTarget = false;
        animator.SetBool("IsSitting", false);
        animator.SetBool("IsWalking", false);
        animator.SetBool("IsStanding", true);
        Debug.Log("Character [Murad] is standing up");
    }
}
