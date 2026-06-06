using System.Collections;
using UnityEngine;
using UnityEngine.Events;

public class HassanApproach : MonoBehaviour
{
    [Header("Animator")]
    public Animator animator;
    public string walkBool = "IsWalking";
    [Header("Movement")]
    public float moveSpeed = 1.5f;
    public float talkDistance = 1.2f;
    public float rotationSpeed = 8f;
    public float facingYawOffset = 0f;
    [Header("Events")]
    public UnityEvent onArrived;
    private bool _approaching;
    private Transform _user;
    public void Approach()
    {
        if (_approaching) return;

        _user = Camera.main != null ? Camera.main.transform : null;
        if (_user == null)
        {
            Debug.LogWarning("[HassanApproach] no main camera found, cannot approach the user");
            return;
        }

        if (animator == null) animator = GetComponentInChildren<Animator>();

        _approaching = true;
        StartCoroutine(ApproachRoutine());
    }

    private IEnumerator ApproachRoutine()
    {
        var pao = GetComponent<PlayAnimationOnStart>();
        if (pao != null) 
        {
            Destroy(pao);
        }

        bool prevRoot = animator != null && animator.applyRootMotion;
        if (animator != null) 
        {
            animator.applyRootMotion = false;
        }

        SetWalk(true);

        while (true)
        {
            Vector3 userFlat = new Vector3(_user.position.x, transform.position.y, _user.position.z);
            Vector3 toUser = userFlat - transform.position;
            float distance = toUser.magnitude;

            if (distance <= talkDistance) 
            {
                break;
            }

            Vector3 dir = toUser / Mathf.Max(distance, 0.0001f);
            Quaternion look = Quaternion.LookRotation(dir) * Quaternion.Euler(0f, facingYawOffset, 0f);
            transform.rotation = Quaternion.Slerp(transform.rotation, look, rotationSpeed * Time.deltaTime);
            transform.position += dir * moveSpeed * Time.deltaTime;
            yield return null;
        }

        SetWalk(false);
        if (animator != null) animator.applyRootMotion = prevRoot;

        Vector3 faceFlat = new Vector3(_user.position.x, transform.position.y, _user.position.z);
        Vector3 faceDir  = faceFlat - transform.position;
        if (faceDir.sqrMagnitude > 0.0001f)
        {
            Quaternion finalRot = Quaternion.LookRotation(faceDir) * Quaternion.Euler(0f, facingYawOffset, 0f);
            float t = 0f;
            while (t < 1f)
            {
                t += Time.deltaTime * rotationSpeed;
                transform.rotation = Quaternion.Slerp(transform.rotation, finalRot, t);
                yield return null;
            }
            transform.rotation = finalRot;
        }
        onArrived?.Invoke();
    }

    private void SetWalk(bool value)
    {
        if (animator == null || string.IsNullOrEmpty(walkBool)) 
        {
            return;
        }
        foreach (var p in animator.parameters)
        {
            if (p.type == AnimatorControllerParameterType.Bool && p.name == walkBool)
            {
                animator.SetBool(walkBool, value);
                return;
            }
        }
    }
}
