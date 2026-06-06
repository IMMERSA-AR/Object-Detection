using UnityEngine;

[DisallowMultipleComponent]
public class PanelCharacterInteraction : MonoBehaviour
{
    public float maxRayDistance = 10f;
    private PanelDetector _detector;
    private AudioClip _clip;
    private string _transcript;
    private Transform _rightController;
    private Transform _leftController;
    private LineRenderer _laserPointer;
    private bool _triggered;


    public void Init(PanelDetector detector, AudioClip narrationClip, string transcript, Transform rightController, Transform leftController, LineRenderer laserPointer)
    {
        _detector = detector;
        _clip = narrationClip;
        _transcript = transcript;
        _rightController = rightController;
        _leftController = leftController;
        _laserPointer = laserPointer;

        EnsureCollider();

        if (_rightController == null && _leftController == null)
            Debug.LogWarning("[PanelCharacterInteraction] Both controllers are not assigned");

        if (_laserPointer == null)
            Debug.LogWarning("[PanelCharacterInteraction] No LineRenderer found on character prefab");

        if (_laserPointer != null)
        {
            _laserPointer.positionCount = 2;
            _laserPointer.SetPosition(0, Vector3.zero);
            _laserPointer.SetPosition(1, Vector3.zero);
            _laserPointer.enabled = false;
        }
    }

    private void Update()
    {
        if (_triggered || _detector == null)
            return;
        if (_rightController == null && _leftController == null)
            return;

        bool hitMe = false;
        Vector3 laserEnd = Vector3.zero;
        Transform aimController = null;

        //Right Controller is the one accessed here  
        if (TryRaycast(_rightController, out Vector3 rEnd, out bool rHit))
        {
            aimController = _rightController;
            laserEnd = rEnd;
            if (rHit) hitMe = true;
        }

        // Left controller overrides if no controller assigned 
        if (!hitMe && TryRaycast(_leftController, out Vector3 lEnd, out bool lHit))
        {
            if (lHit || aimController == null)
            {
                aimController = _leftController;
                laserEnd = lEnd;
            }
            if (lHit) hitMe = true;
        }

        if (_laserPointer != null && aimController != null)
        {
            _laserPointer.enabled = true;
            _laserPointer.SetPosition(0, aimController.position);
            _laserPointer.SetPosition(1, laserEnd);
            _laserPointer.startColor = hitMe ? Color.green : Color.white;
            _laserPointer.endColor = hitMe ? Color.green : Color.white;
        }

        if (hitMe)
        {
            bool pressed = OVRInput.GetDown(OVRInput.RawButton.RIndexTrigger) || OVRInput.GetDown(OVRInput.RawButton.LIndexTrigger);
            if (pressed)
            {
                _triggered = true;
                if (_laserPointer != null)
                    _laserPointer.enabled = false;
                Debug.Log("[PanelCharacterInteraction] Character is hitted and the naration started");
                _detector.TriggerNarration(gameObject, _clip);
            }
        }
    }

    private void OnDisable()
    {
        if (_laserPointer != null)
            _laserPointer.enabled = false;
    }

    private bool TryRaycast(Transform controller, out Vector3 endPoint, out bool hitsMe)
    {
        endPoint = Vector3.zero;
        hitsMe = false;
        if (controller == null)
            return false;

        Ray ray = new Ray(controller.position, controller.forward);
        endPoint = controller.position + controller.forward * maxRayDistance;
        if (Physics.Raycast(ray, out RaycastHit hit, maxRayDistance))
        {
            endPoint = hit.point;
            if (hit.transform.IsChildOf(transform) || hit.transform == transform)
                hitsMe = true;
        }

        return true;
    }
    private void EnsureCollider()
    {
        if (GetComponentInChildren<Collider>() != null)
            return;

        var cap = gameObject.AddComponent<CapsuleCollider>();
        cap.center = new Vector3(0f, 1f, 0f);
        cap.height = 2f;
        cap.radius = 0.5f;
        Debug.Log("[PanelCharacterInteraction] No collider found");
    }
}
