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
        if (animator == null)
            animator = GetComponentInChildren<Animator>();

        if (animator == null)
        {
            Debug.LogError("[MuradController] No animator found on the object or its children");
            enabled = false;
            return;
        }

        Debug.Log($"[MuradController] Animator found: {animator.gameObject.name}");

        // Log all animator parameters
        string paramList = "";
        for (int p = 0; p < animator.parameterCount; p++)
            paramList += animator.parameters[p].name + " ";
        Debug.Log($"[MuradController] Animator parameters: [{paramList.Trim()}]");

        // Auto-assign animator to VoiceAPIController on the same GameObject
        VoiceAPIController voiceCtrl = GetComponent<VoiceAPIController>();
        if (voiceCtrl != null)
        {
            if (voiceCtrl.animator == null)
            {
                voiceCtrl.animator = animator;
                Debug.Log("[MuradController] Auto-assigned Animator to VoiceAPIController.");
            }
            else
            {
                Debug.Log($"[MuradController] VoiceAPIController already has animator: {voiceCtrl.animator.gameObject.name}");
            }
        }
        else
        {
            Debug.LogWarning("[MuradController] VoiceAPIController not found on same GameObject.");
        }

        if (!enabled)
            return;

        Debug.Log("[MuradController] Setting IsStanding=true, IsWalking=false, IsSitting=false");
        animator.SetBool("IsStanding", true);
        animator.SetBool("IsWalking", false);
        animator.SetBool("IsSitting", false);

        // Log current animator state
        var state = animator.GetCurrentAnimatorStateInfo(0);
        Debug.Log($"[MuradController] Current state hash: {state.shortNameHash} | normalizedTime: {state.normalizedTime}");
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
