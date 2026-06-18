using System.Collections;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// Walks Hassan up to the user so they can talk to him — using the SAME simple model
/// as MuradController: play the Animator Controller's in-place walk animation
/// (via the <see cref="walkBool"/> parameter) while moving the transform forward at
/// <see cref="moveSpeed"/>. No root motion, so no special clip import settings are needed.
///
/// Triggered AFTER the intro story (TimePortalLogic calls <see cref="Approach"/>).
/// Before walking it stops any PlayableGraph holding the Animator (e.g.
/// PlayAnimationOnStart's Think loop) so the controller is in control.
/// </summary>
public class HassanApproach : MonoBehaviour
{
    [Header("Animator")]
    [Tooltip("Hassan's Animator. Auto-found in children if left empty.")]
    public Animator animator;

    [Tooltip("Bool parameter on the Animator Controller that plays the WALK state " +
             "(MuradController uses 'IsWalking').")]
    public string walkBool = "IsWalking";

    [Header("Movement")]
    [Tooltip("Walk speed (metres / second). Tune to match the walk animation's stride " +
             "so the feet don't slide — same idea as MuradController's Move Speed.")]
    public float moveSpeed = 1.5f;

    [Tooltip("How far in front of the user Hassan stops (metres).")]
    public float talkDistance = 1.2f;

    [Tooltip("How fast he turns toward the user (higher = snappier).")]
    public float rotationSpeed = 8f;

    [Tooltip("Yaw offset (degrees) applied to his facing. If Hassan ends up facing AWAY from " +
             "the user, set this to 180; if he faces sideways, try +90 or -90. Leave 0 if his " +
             "model's front is already +Z (transform.forward).")]
    public float facingYawOffset = 0f;

    [Header("Events")]
    [Tooltip("Fired once Hassan arrives in front of the user and faces them.")]
    public UnityEvent onArrived;

    private bool _approaching;
    private Transform _user;

    /// <summary>Starts Hassan walking toward the user. Runs once.</summary>
    public void Approach()
    {
        if (_approaching) return;

        _user = Camera.main != null ? Camera.main.transform : null;
        if (_user == null)
        {
            Debug.LogWarning("[HassanApproach] No Camera.main found — cannot approach the user.");
            return;
        }

        if (animator == null) animator = GetComponentInChildren<Animator>();

        _approaching = true;
        StartCoroutine(ApproachRoutine());
    }

    private IEnumerator ApproachRoutine()
    {
        // Stop any PlayableGraph holding the Animator (e.g. PlayAnimationOnStart's Think
        // loop) so the Animator Controller — and its Walk state — takes over.
        var pao = GetComponent<PlayAnimationOnStart>();
        if (pao != null) Destroy(pao);   // its OnDestroy() destroys its graph

        // Move exactly like MuradController: in-place walk animation + transform slide.
        // Root motion OFF so it never fights our translation (no clip import needed).
        bool prevRoot = animator != null && animator.applyRootMotion;
        if (animator != null) animator.applyRootMotion = false;

        SetWalk(true);
        Debug.Log("[HassanApproach] Hassan walking toward the user...");

        // ── Walk until within talkDistance of the user (XZ only) ──
        while (true)
        {
            Vector3 userFlat = new Vector3(_user.position.x, transform.position.y, _user.position.z);
            Vector3 toUser   = userFlat - transform.position;
            float distance   = toUser.magnitude;

            if (distance <= talkDistance) break;

            Vector3 dir = toUser / Mathf.Max(distance, 0.0001f);
            Quaternion look = Quaternion.LookRotation(dir) * Quaternion.Euler(0f, facingYawOffset, 0f);
            transform.rotation = Quaternion.Slerp(
                transform.rotation, look, rotationSpeed * Time.deltaTime);
            transform.position += dir * moveSpeed * Time.deltaTime;
            yield return null;
        }

        // ── Arrived: stop walking; controller returns to idle/talk ──
        SetWalk(false);
        if (animator != null) animator.applyRootMotion = prevRoot;

        // Square up to face the user.
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

        Debug.Log("[HassanApproach] Hassan arrived in front of the user — ready to talk.");
        onArrived?.Invoke();
    }

    // Sets the walk bool only if the controller actually has that parameter (no warnings).
    private void SetWalk(bool value)
    {
        if (animator == null || string.IsNullOrEmpty(walkBool)) return;
        foreach (var p in animator.parameters)
        {
            if (p.type == AnimatorControllerParameterType.Bool && p.name == walkBool)
            {
                animator.SetBool(walkBool, value);
                return;
            }
        }
        Debug.LogWarning($"[HassanApproach] Animator has no bool parameter '{walkBool}' — " +
                         "Hassan will move but not play a walk animation.");
    }
}
