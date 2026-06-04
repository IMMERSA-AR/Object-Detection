using System;
using TMPro;
using UnityEngine;

/// <summary>
/// A reusable two-button (Yes / No) confirmation dialog for the panel scene.
///
/// The user clicks a button the SAME way they start a character's story: aim the
/// LEFT or RIGHT controller laser at the button and pull the index trigger.
/// (No hand-poke / ISDK required — this uses a physics raycast against a collider
///  auto-added to each button, exactly like PanelCharacterInteraction.)
///
/// PanelSceneManager calls Show(message, onYes, onNo) to display it and Hide() to
/// close it. The dialog hides itself automatically the moment a button is chosen.
///
/// ── Prefab setup ───────────────────────────────────────────────────────────
///  • Put this component on the 2-button dialog root (the GameObject you toggle).
///  • Drag the message TMP text, the YES button GameObject and the NO button
///    GameObject into the matching fields.
///  • Drag the LEFT/RIGHT controller anchors (same ones used by PanelDetector).
///  • Colliders are added to the buttons automatically if they don't have one.
/// </summary>
[DisallowMultipleComponent]
public class RepeatDialog : MonoBehaviour
{
    [Header("Dialog content")]
    [Tooltip("Optional root to show/hide. If empty, this GameObject is toggled.")]
    public GameObject dialogRoot;

    [Tooltip("TextMeshPro that displays the question (e.g. 'Repeat this story?').")]
    public TMP_Text messageText;

    [Header("Buttons")]
    [Tooltip("The YES button GameObject (its RectTransform defines the clickable area).")]
    public GameObject yesButton;

    [Tooltip("The NO button GameObject.")]
    public GameObject noButton;

    [Header("Controllers (same as PanelDetector)")]
    public Transform rightController;
    public Transform leftController;

    [Tooltip("Optional laser line drawn while aiming. Leave empty to use no visible laser.")]
    public LineRenderer laserPointer;

    [Tooltip("Maximum ray distance in metres.")]
    public float maxPointDistance = 10f;

    [Header("Highlight")]
    public Color normalColor    = new Color(1f, 1f, 1f, 0.20f);
    public Color highlightColor = new Color(0.26f, 0.72f, 0.51f, 0.9f);

    // ── Runtime ────────────────────────────────────────────────────────────
    private Action _onYes;
    private Action _onNo;
    private bool   _active;
    private Collider _yesCol;
    private Collider _noCol;
    private UnityEngine.UI.Image _yesImg;
    private UnityEngine.UI.Image _noImg;

    private void Awake()
    {
        if (dialogRoot == null) dialogRoot = gameObject;
        if (yesButton != null) _yesImg = yesButton.GetComponent<UnityEngine.UI.Image>();
        if (noButton  != null) _noImg  = noButton.GetComponent<UnityEngine.UI.Image>();
        HideImmediate();
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>Shows the dialog with a question and the two callbacks.</summary>
    public void Show(string message, Action onYes, Action onNo)
    {
        _onYes = onYes;
        _onNo  = onNo;

        if (messageText != null) messageText.text = message;

        dialogRoot.SetActive(true);
        _active = true;

        // Size the click colliders NOW (dialog is active, so a Horizontal/Vertical
        // Layout Group has finalised each button's rect). Doing this in Awake() while
        // the dialog was inactive produced zero-size colliders → buttons unclickable.
        RefreshColliders();

        if (rightController == null && leftController == null)
            Debug.LogWarning("[RepeatDialog] No controllers assigned — buttons cannot be clicked. " +
                             "Assign Right/Left Controller in the Inspector.");
        if (_yesCol == null || _noCol == null)
            Debug.LogWarning("[RepeatDialog] Yes/No button colliders missing — assign Yes Button / No Button.");

        Debug.Log($"[RepeatDialog] Shown: \"{message.Replace("\n", " ")}\"");

        if (laserPointer != null)
        {
            laserPointer.positionCount = 2;
            laserPointer.enabled = false;
        }
        SetButtonColor(_yesImg, normalColor);
        SetButtonColor(_noImg,  normalColor);
    }

    /// <summary>Forces layout to settle, then (re)builds correctly sized button colliders.</summary>
    private void RefreshColliders()
    {
        var rt = dialogRoot.GetComponent<RectTransform>();
        if (rt != null)
            UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(rt);

        _yesCol = EnsureCollider(yesButton);
        _noCol  = EnsureCollider(noButton);
    }

    /// <summary>Hides the dialog without invoking any callback.</summary>
    public void Hide()
    {
        _active = false;
        if (laserPointer != null) laserPointer.enabled = false;
        dialogRoot.SetActive(false);
    }

    private void HideImmediate()
    {
        _active = false;
        if (dialogRoot != null) dialogRoot.SetActive(false);
    }

    // ── Update: laser aim + trigger ────────────────────────────────────────────

    private void Update()
    {
        if (!_active) return;
        if (rightController == null && leftController == null) return;

        bool      overYes = false;
        bool      overNo  = false;
        Vector3   laserEnd = Vector3.zero;
        Transform aim      = null;

        // Aim with the right controller first, then the left. A hit on the Yes/No
        // button (or any of its children) selects that button.
        if (TryRaycast(rightController, out Vector3 rEnd, out Transform rHit))
        {
            aim = rightController; laserEnd = rEnd;
            if      (IsPartOf(rHit, yesButton)) overYes = true;
            else if (IsPartOf(rHit, noButton))  overNo  = true;
        }
        if (!overYes && !overNo && TryRaycast(leftController, out Vector3 lEnd, out Transform lHit))
        {
            bool hitsButton = IsPartOf(lHit, yesButton) || IsPartOf(lHit, noButton);
            if (hitsButton || aim == null) { aim = leftController; laserEnd = lEnd; }
            if      (IsPartOf(lHit, yesButton)) overYes = true;
            else if (IsPartOf(lHit, noButton))  overNo  = true;
        }

        // Laser visual
        if (laserPointer != null && aim != null)
        {
            laserPointer.enabled = true;
            laserPointer.SetPosition(0, aim.position);
            laserPointer.SetPosition(1, laserEnd);
            Color c = (overYes || overNo) ? Color.green : Color.white;
            laserPointer.startColor = c;
            laserPointer.endColor   = c;
        }

        // Button highlight
        SetButtonColor(_yesImg, overYes ? highlightColor : normalColor);
        SetButtonColor(_noImg,  overNo  ? highlightColor : normalColor);

        // Trigger
        if (overYes || overNo)
        {
            bool pressed = OVRInput.GetDown(OVRInput.RawButton.RIndexTrigger)
                        || OVRInput.GetDown(OVRInput.RawButton.LIndexTrigger);
            if (pressed)
            {
                Debug.Log($"[RepeatDialog] {(overYes ? "YES" : "NO")} button clicked.");
                Action chosen = overYes ? _onYes : _onNo;
                Hide();                 // close first so callbacks can re-open it
                chosen?.Invoke();
            }
        }
    }

    private Transform _rightOrLeft(bool right) => right ? rightController : leftController;

    // ── Helpers ────────────────────────────────────────────────────────────────

    private bool TryRaycast(Transform controller, out Vector3 endPoint, out Transform hitT)
    {
        endPoint = Vector3.zero;
        hitT     = null;
        if (controller == null) return false;

        endPoint = controller.position + controller.forward * maxPointDistance;
        if (Physics.Raycast(controller.position, controller.forward, out RaycastHit hit, maxPointDistance))
        {
            endPoint = hit.point;
            hitT     = hit.transform;
        }
        return true;
    }

    /// <summary>True if the ray hit is the object itself or any of its children.</summary>
    private static bool IsPartOf(Transform hit, GameObject root)
    {
        if (hit == null || root == null) return false;
        return hit == root.transform || hit.IsChildOf(root.transform);
    }

    private static void SetButtonColor(UnityEngine.UI.Image img, Color c)
    {
        if (img != null) img.color = c;
    }

    /// <summary>Adds (or re-sizes) a BoxCollider to match the button's current RectTransform.</summary>
    private static Collider EnsureCollider(GameObject button)
    {
        if (button == null) return null;

        var box = button.GetComponent<BoxCollider>();
        if (box == null) box = button.AddComponent<BoxCollider>();

        var rt = button.GetComponent<RectTransform>();
        if (rt != null)
        {
            Rect r = rt.rect;
            // Guard against a zero-size rect (layout not yet built).
            float w = Mathf.Max(r.width,  1f);
            float h = Mathf.Max(r.height, 1f);

            // These buttons are thin (~40px tall), which makes a flat collider almost
            // impossible to hit with a laser. Keep the WIDTH at the button width (so the
            // Yes/No colliders stay horizontally separate → no overlap/swap), but make
            // the collider much TALLER and give it real DEPTH so the ray reliably hits it.
            float boxH = h * 2.5f;                 // taller → easy to aim at
            float boxZ = Mathf.Max(w, h) * 1.0f;   // thick slab → ray always intersects
            box.size   = new Vector3(w, boxH, boxZ);
            box.center = new Vector3((0.5f - rt.pivot.x) * w,
                                     (0.5f - rt.pivot.y) * h,
                                     0f);
        }
        return box;
    }
}
