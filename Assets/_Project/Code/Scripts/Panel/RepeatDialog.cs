using System;
using TMPro;
using UnityEngine;

[DisallowMultipleComponent]
public class RepeatDialog : MonoBehaviour
{
    [Header("Dialog content")]
    public GameObject dialogRoot;
    public TMP_Text messageText;    // Message that will appear on dialogue

    [Header("Buttons")]
    public GameObject yesButton;
    public GameObject noButton;

    [Header("Controllers")]
    public Transform rightController;
    public Transform leftController;
    public LineRenderer laserPointer;
    public float maxPointDistance = 10f;
    private Action _onYes;
    private Action _onNo;
    private bool _active;
    private Collider _yesCol;
    private Collider _noCol;

    private void Awake()
    {
        if (dialogRoot == null)
        {
            Debug.LogWarning("[RepeatDialog] Dialog Root is not assigned");
            dialogRoot = gameObject;
        }
        HideImmediate();
    }

    public void Show(string message, Action onYes, Action onNo)
    {
        _onYes = onYes;
        _onNo = onNo;

        if (messageText != null)
        {
            messageText.text = message;
        }
        else
        {
            Debug.LogWarning("[RepeatDialog] Message text is not assigned");
        }

        dialogRoot.SetActive(true);
        _active = true;
        RefreshColliders();

        if (rightController == null && leftController == null)
            Debug.LogWarning("[RepeatDialog] Controllers are not assigned");
        if (_yesCol == null || _noCol == null)
            Debug.LogWarning("[RepeatDialog] Yes/No buttons are not assigned");

        if (laserPointer != null)
        {
            laserPointer.positionCount = 2;
            laserPointer.enabled = false;
        }
    }

    private void RefreshColliders()
    {
        var rt = dialogRoot.GetComponent<RectTransform>();
        if (rt != null)
            UnityEngine.UI.LayoutRebuilder.ForceRebuildLayoutImmediate(rt);

        _yesCol = EnsureCollider(yesButton);
        _noCol = EnsureCollider(noButton);
    }

    public void Hide()
    {
        _active = false;
        if (laserPointer != null)
        {
            laserPointer.enabled = false;
        }
        dialogRoot.SetActive(false);
    }

    private void HideImmediate()
    {
        _active = false;
        if (dialogRoot != null)
            dialogRoot.SetActive(false);
    }

    private void Update()
    {
        if (!_active)
            return;
        if (rightController == null && leftController == null)
            return;

        bool overYes = false;
        bool overNo = false;
        Vector3 laserEnd = Vector3.zero;
        Transform aim = null;
        //Check right controller first. 
        if (TryRaycast(rightController, out Vector3 rEnd, out Transform rHit))
        {
            aim = rightController; laserEnd = rEnd;
            if (IsPartOf(rHit, yesButton))
                overYes = true;
            else if (IsPartOf(rHit, noButton))
                overNo = true;
        }
        //check left controller 
        if (!overYes && !overNo && TryRaycast(leftController, out Vector3 lEnd, out Transform lHit))
        {
            bool hitsButton = IsPartOf(lHit, yesButton) || IsPartOf(lHit, noButton);
            if (hitsButton || aim == null)
            {
                aim = leftController;
                laserEnd = lEnd;
            }
            if (IsPartOf(lHit, yesButton))
                overYes = true;
            else if (IsPartOf(lHit, noButton))
                overNo = true;
        }

        if (laserPointer != null && aim != null)
        {
            laserPointer.enabled = true;
            laserPointer.SetPosition(0, aim.position);
            laserPointer.SetPosition(1, laserEnd);
            Color c = (overYes || overNo) ? Color.green : Color.white;
            laserPointer.startColor = c;
            laserPointer.endColor = c;
        }

        if (overYes || overNo)
        {
            bool pressed = OVRInput.GetDown(OVRInput.RawButton.RIndexTrigger) || OVRInput.GetDown(OVRInput.RawButton.LIndexTrigger);
            if (pressed)
            {
                Debug.Log($"[RepeatDialog] {(overYes ? "YES" : "NO")} button clicked.");
                Action chosen = overYes ? _onYes : _onNo;
                Hide();
                chosen?.Invoke();
            }
        }
    }

    private bool TryRaycast(Transform controller, out Vector3 endPoint, out Transform hitT)
    {
        endPoint = Vector3.zero;
        hitT = null;
        if (controller == null)
            return false;

        endPoint = controller.position + controller.forward * maxPointDistance;

        // Use RaycastAll so Murad's collider doesn't block the buttons behind him
        RaycastHit[] hits = Physics.RaycastAll(controller.position, controller.forward, maxPointDistance);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        // First pass: prefer a button hit even if something is in front
        foreach (var h in hits)
        {
            if (IsPartOf(h.transform, yesButton) || IsPartOf(h.transform, noButton))
            {
                endPoint = h.point;
                hitT = h.transform;
                return true;
            }
        }

        // Fall back to closest hit
        if (hits.Length > 0)
        {
            endPoint = hits[0].point;
            hitT = hits[0].transform;
        }

        return true;
    }

    private static bool IsPartOf(Transform hit, GameObject root)
    {
        if (hit == null || root == null)
        {
            return false;
        }
        return hit == root.transform || hit.IsChildOf(root.transform);
    }

    private static Collider EnsureCollider(GameObject button)
    {
        if (button == null)
            return null;

        var box = button.GetComponent<BoxCollider>();
        if (box == null)
        {
            box = button.AddComponent<BoxCollider>();
        }

        var rt = button.GetComponent<RectTransform>();
        if (rt != null)
        {
            Rect r = rt.rect;
            float w = Mathf.Max(r.width, 1f);
            float h = Mathf.Max(r.height, 1f);
            float boxH = h * 2.5f;
            float boxZ = Mathf.Max(w, h) * 1.0f;
            box.size = new Vector3(w, boxH, boxZ);
            box.center = new Vector3((0.5f - rt.pivot.x) * w, (0.5f - rt.pivot.y) * h, 0f);
        }
        return box;
    }
}
