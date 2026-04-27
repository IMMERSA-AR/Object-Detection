using UnityEngine;
using TMPro;
using UnityEngine.UI; // Added for Image/UI support

public class MarkerController : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] private TextMeshProUGUI _textMesh;
    [SerializeField] private RectTransform _frame; // Reference to your Green Box/Frame

    public float lastUpdateTime;

    private void Awake()
    {
        // Fallback: If you forgot to drag them in the Inspector, try to find them
        if (_textMesh == null) _textMesh = GetComponentInChildren<TextMeshProUGUI>();

        if (_textMesh == null)
        {
            Debug.LogError("No TextMeshProUGUI found on marker prefab!");
        }
    }

    /// <summary>
    /// Updates the marker’s transform and text, and records the update time.
    /// </summary>
    public void UpdateMarker(Vector3 position, Quaternion rotation, Vector3 scale, string text)
    {
        // Position and Rotation are set by the ObjectRenderer's raycast logic
        transform.SetPositionAndRotation(position, rotation);

        // Scale is determined by the AI detection's width/height in meters
        transform.localScale = scale;

        if (_textMesh)
        {
            _textMesh.text = text;
        }

        lastUpdateTime = Time.time;

        if (!gameObject.activeSelf)
        {
            gameObject.SetActive(true);
        }
    }
    private void Update()
    {
        // Check if a cube has already been placed globally
        ObjectStamper stamper = FindAnyObjectByType<ObjectStamper>();
        bool hasPlacedCube = stamper != null && stamper.HasSpawned;

        // If we haven't placed a cube yet, keep taking frames and stay visible.
        // If we HAVE placed the cube, we can resume the 2-second auto-hide logic.
        if (!hasPlacedCube)
        {
            // Stay active to ensure the ObjectStamper can "see" this marker
            return;
        }

        // Standard auto-hide logic resumes only AFTER the chair task is done
        if (gameObject.activeSelf && Time.time - lastUpdateTime > 0f)
        {
            gameObject.SetActive(false);
        }
    }
}