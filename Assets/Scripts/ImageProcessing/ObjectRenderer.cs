using System;
using System.Collections.Generic;
using UnityEngine;
using Meta.XR;

public class ObjectRenderer : MonoBehaviour
{
    [Header("Camera & Raycast Settings")]
    [SerializeField] private float mergeThreshold = 0.2f;

    [Header("Marker Settings")]
    [SerializeField] private GameObject markerPrefab;

    [Header("Label Filtering")]
    [SerializeField] private YOLOv9Labels[] labelFilters;
    [SerializeField, Range(0f, 1f)] private float minConfidence = 0.5f;
    [Header("UI Instruction")]
    public GameObject searchUIObject; // Drag your Canvas or Text object here

    private Camera _mainCamera;
    private const float ModelInputSize = 640f;
    private PassthroughCameraAccess _cameraAccess;
    private EnvironmentRaycastManager _envRaycastManager;
    private readonly Dictionary<string, MarkerController> _activeMarkers = new();

    private void Awake()
    {
        _cameraAccess = GetComponent<PassthroughCameraAccess>() ?? FindAnyObjectByType<PassthroughCameraAccess>(FindObjectsInactive.Include);
        _envRaycastManager = GetComponent<EnvironmentRaycastManager>() ?? FindAnyObjectByType<EnvironmentRaycastManager>(FindObjectsInactive.Include);
        if (!_cameraAccess || !_envRaycastManager)
        {
            Debug.LogWarning("[Detection3DRenderer] Passthrough camera or Environment Raycast Manager is not ready.");
            return;
        }
        _mainCamera = Camera.main;
    }

    public void RenderDetections(Unity.InferenceEngine.Tensor<float> coords, Unity.InferenceEngine.Tensor<int> labelIDs, Unity.InferenceEngine.Tensor<float> confidences = null)
    {
        ObjectStamper stamper = GetComponent<ObjectStamper>();

        // If Murad is already sitting, ignore the AI data completely
        if (stamper != null && stamper.HasSpawned)
        {
            return;
        }

        // Basic safety checks
        if (coords == null || labelIDs == null) return;

        if (!_cameraAccess || !_envRaycastManager)
        {
            Debug.LogWarning("[Detection3DRenderer] Missing dependencies.");
        }

        var numDetections = coords.shape[0];
        ClearPreviousMarkers();

        // --- NEW: Variables to track the closest chair ---
        Vector3 closestChairPos = Vector3.zero;
        float minDistance = float.MaxValue; // Start at infinity
        bool foundChairThisFrame = false;
        int chairCountThisFrame = 0;

        var imageWidth = ModelInputSize;
        var imageHeight = ModelInputSize;
        var halfWidth = imageWidth * 0.5f;
        var halfHeight = imageHeight * 0.5f;



        for (var i = 0; i < numDetections; i++)
        {

            var detectedCenterX = coords[i, 0];
            var detectedCenterY = coords[i, 1];
            var detectedWidth = coords[i, 2];
            var detectedHeight = coords[i, 3];

            var adjustedCenterX = detectedCenterX - halfWidth;
            var adjustedCenterY = detectedCenterY - halfHeight;

            var perX = (adjustedCenterX + halfWidth) / imageWidth;
            var perY = (adjustedCenterY + halfHeight) / imageHeight;
            var centerRay = _cameraAccess.ViewportPointToRay(DetectionToViewport(perX, perY));

            // --- NEW RAYCAST LOGIC WITH FALLBACK ---
            // FORCE markers to appear 1.5 meters in front of the camera for testing
            // FORCE markers to appear 1.0 meters in front of the camera for testing
            Vector3 markerWorldPos = _mainCamera.transform.position + (_mainCamera.transform.forward * 1.0f);
            Vector3 surfaceNormal = -_mainCamera.transform.forward; // Face the camera

            if (_envRaycastManager.Raycast(centerRay, out var centerHit))
            {
                // We hit a physical wall/table!
                markerWorldPos = centerHit.point;
                surfaceNormal = SampleSurfaceNormal(markerWorldPos, centerHit.normal);
            }
            else
            {
                // We hit empty air. Float the marker 2 meters away instead of deleting it!
                markerWorldPos = centerRay.GetPoint(2.0f);
            }


            var u1 = (detectedCenterX - detectedWidth * 0.5f) / imageWidth;
            var v1 = (detectedCenterY - detectedHeight * 0.5f) / imageHeight;
            var u2 = (detectedCenterX + detectedWidth * 0.5f) / imageWidth;
            var v2 = (detectedCenterY + detectedHeight * 0.5f) / imageHeight;

            var tlRay = _cameraAccess.ViewportPointToRay(DetectionToViewport(u1, v1));
            var trRay = _cameraAccess.ViewportPointToRay(DetectionToViewport(u2, v1));
            var blRay = _cameraAccess.ViewportPointToRay(DetectionToViewport(u1, v2));
            var brRay = _cameraAccess.ViewportPointToRay(DetectionToViewport(u2, v2));

            var depth = Vector3.Distance(_mainCamera.transform.position, markerWorldPos);
            var worldTL = tlRay.GetPoint(depth);
            var worldTR = trRay.GetPoint(depth);
            var worldBL = blRay.GetPoint(depth);

            var markerWidth = Vector3.Distance(worldTR, worldTL);
            var markerHeight = Vector3.Distance(worldBL, worldTL);
            // var markerScale = new Vector3(markerWidth, markerHeight, 1f);
            //float multiplier = 0.002f; // Adjust this if boxes are too small/big
            // Force a visible size (10cm) so we can actually see the object
            var markerScale = new Vector3(0.1f, 0.1f, 0.1f);

            var detectedLabel = (YOLOv9Labels)labelIDs[i];
            if (labelFilters is { Length: > 0 } && !Array.Exists(labelFilters, label => label == detectedLabel))
            {
                continue;
            }
            // ... inside your detection loop ...
            string labelName = detectedLabel.ToString();

            if (labelName.ToLower().Contains("chair"))
            {
                chairCountThisFrame++; // Increase the chair count by 1

                // 1. FIRST: Check confidence (Move this up so we don't process "weak" chairs)
                var chairConfidence = GetConfidence(coords, confidences, i);
                if (chairConfidence < minConfidence) continue;

                // 2. SECOND: Aspect Ratio Check
                // A chair is usually tall or square. A table is wide.
                float aspectRatio = detectedWidth / detectedHeight;

                // If the width is 20% larger than the height (1.2), it's likely a table
                if (aspectRatio > 1.2f)
                {
                    // Debug.Log($"[Chair Tracker] Ignored wide object. AR: {aspectRatio:F2}");
                    continue;
                }

                // 3. THIRD: If it passed the tests, count it!
                chairCountThisFrame++;

                float distanceToChair = Vector3.Distance(_mainCamera.transform.position, markerWorldPos);

                if (distanceToChair < minDistance)
                {
                    minDistance = distanceToChair;
                    closestChairPos = markerWorldPos;
                    foundChairThisFrame = true;
                }

            }
            Vector3 directionToPlayer = _mainCamera.transform.position - markerWorldPos;
            var markerRotation = Quaternion.LookRotation(_mainCamera.transform.position - markerWorldPos, Vector3.up);

            var dictionaryKey = detectedLabel.ToString();
            var confidence = GetConfidence(coords, confidences, i);
            if (confidence >= 0f && confidence < minConfidence)
            {
                continue;
            }


            //var labelWithConfidence = confidence >= 0f ? $"{dictionaryKey} ({confidence * 100f:F0}%)": dictionaryKey;
            var labelWithConfidence = $"{dictionaryKey}";

            var lookupKey = dictionaryKey;
            if (_activeMarkers.TryGetValue(lookupKey, out MarkerController existingMarker))
            {
                if (Vector3.Distance(existingMarker.transform.position, markerWorldPos) < mergeThreshold)
                {
                    existingMarker.UpdateMarker(markerWorldPos, markerRotation, markerScale, labelWithConfidence);
                    continue;
                }
                lookupKey = $"{dictionaryKey}_{i}";
            }

            var markerGo = Instantiate(markerPrefab, transform);
            var marker = markerGo.GetComponent<MarkerController>();
            if (!marker)
            {
                Debug.LogWarning($"[Detection3DRenderer] Detection {i}: Marker prefab is missing a MarkerController component.");
                continue;
            }

            marker.UpdateMarker(markerWorldPos, markerRotation, markerScale, labelWithConfidence);
            _activeMarkers[lookupKey] = marker;


        }
        if (foundChairThisFrame)
        {
            //closestChairPos.y = 0f;
            Debug.Log($"[Chair Tracker Summary] Total chair detections: {chairCountThisFrame}. Closest is {minDistance:F2} meters away.");

            //ObjectStamper stamper = GetComponent<ObjectStamper>();

            // Only try to spawn if we found the stamper and Murad hasn't appeared yet
            if (stamper != null && !stamper.HasSpawned)
            {
                // --- NEW: SPAWN THE GOLD DOT ---
                // We create a basic Unity sphere to mark the chosen spot
                // GameObject goldDot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                // goldDot.transform.position = closestChairPos;
                // goldDot.transform.localScale = new Vector3(0.15f, 0.15f, 0.15f); // 15cm big (slightly bigger than your green dots)

                // // Give it a bright gold/yellow color so it stands out!
                // goldDot.GetComponent<Renderer>().material.color = new Color(1.0f, 0.6f, 0.0f);

                // Vector3 lookDirection = _mainCamera.transform.right;
                // lookDirection.y = 0; // Keep him sitting perfectly straight
                // Quaternion spawnRotation = Quaternion.identity;
                // if (lookDirection != Vector3.zero)
                // {
                //     spawnRotation = Quaternion.LookRotation(lookDirection);
                // }

                // // Spawn him at the closest chair, facing right
                // stamper.PlacePermanentCharacter(closestChairPos, spawnRotation);
                // Make Murad look at the player
                Vector3 directionToPlayer = _mainCamera.transform.position - closestChairPos;
                directionToPlayer.y = 0; // Keep him standing perfectly straight

                Quaternion lookAtPlayerRot = Quaternion.identity;
                if (directionToPlayer != Vector3.zero)
                {
                    lookAtPlayerRot = Quaternion.LookRotation(directionToPlayer);
                }

                // Spawn him at the closest chair, facing the player
                stamper.PlacePermanentCharacter(closestChairPos, lookAtPlayerRot);
            }
        }
    }

    private void ClearPreviousMarkers()
    {
        foreach (var marker in _activeMarkers.Values)
        {
            if (marker && marker.gameObject)
            {
                Destroy(marker.gameObject);
            }
        }
        _activeMarkers.Clear();
    }

    private Vector2 DetectionToViewport(float normalizedX, float normalizedY)
    {
        // Since the AI input is a perfect 640x640 square, we just need 
        // to ensure the Y axis is flipped for Unity Viewport space.
        return new Vector2(Mathf.Clamp01(normalizedX), Mathf.Clamp01(1f - normalizedY));
    }

    private static float GetConfidence(Unity.InferenceEngine.Tensor<float> coords, Unity.InferenceEngine.Tensor<float> confidenceTensor, int index)
    {
        var sampled = SampleConfidence(confidenceTensor, index);
        if (sampled >= 0f)
        {
            return Mathf.Clamp01(sampled);
        }

        if (coords == null || coords.shape.rank < 2)
        {
            return -1f;
        }

        var channels = coords.shape[coords.shape.rank - 1];
        if (channels <= 4)
        {
            return -1f;
        }

        try
        {
            return Mathf.Clamp01(coords[index, 4]);
        }
        catch (Exception)
        {
            return -1f;
        }
    }

    private static float SampleConfidence(Unity.InferenceEngine.Tensor<float> tensor, int index)
    {
        if (tensor == null)
        {
            return -1f;
        }

        var length = tensor.shape.length;
        if (index < 0 || index >= length)
        {
            return -1f;
        }

        try
        {
            return tensor[index];
        }
        catch
        {
            return -1f;
        }
    }

    private Vector3 SampleSurfaceNormal(Vector3 position, Vector3 fallbackNormal)
    {
        if (_envRaycastManager == null)
        {
            return fallbackNormal;
        }

        var origin = _mainCamera ? _mainCamera.transform.position : position - fallbackNormal * 0.1f;
        var direction = position - origin;
        if (direction.sqrMagnitude > 0.0001f)
        {
            if (_envRaycastManager.Raycast(new Ray(origin, direction.normalized), out var hit, direction.magnitude + 0.05f))
            {
                return hit.normal;
            }
        }

        var offsetOrigin = position + fallbackNormal.normalized * 0.05f;
        if (_envRaycastManager.Raycast(new Ray(offsetOrigin, -fallbackNormal.normalized), out var reverseHit, 0.2f))
        {
            return reverseHit.normal;
        }

        return fallbackNormal;
    }
    private void Update()
    {
        ObjectStamper stamper = GetComponent<ObjectStamper>();

        // This runs every single frame, making it 100% guaranteed to turn off
        if (stamper != null && searchUIObject != null)
        {
            if (stamper.HasSpawned)
            {
                // Murad is sitting -> Force text off
                if (searchUIObject.activeSelf)
                {
                    searchUIObject.SetActive(false);
                    ClearPreviousMarkers(); // Wipe any stuck green boxes
                }
            }
            else
            {
                // Murad is not here yet -> Force text on
                if (!searchUIObject.activeSelf)
                {
                    searchUIObject.SetActive(true);
                }
            }
        }
    }
}
