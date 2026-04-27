using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Add this script to your MenusCanvas GameObject.
///
/// It ensures the Canvas has everything needed for Quest controller
/// ray-pointer interaction at runtime, so you don't have to manually
/// add/configure components in the Editor.
///
/// WHAT IT DOES:
///   1. Confirms Canvas is World Space with the correct camera
///   2. Confirms a raycaster is present that Quest controllers can use
///   3. Logs clear errors if anything is missing
///
/// HOW CONTROLLER SELECTION WORKS ON QUEST:
///   The Quest controller emits a ray (laser pointer). When it hits a
///   UI Button inside a World Space Canvas, the GraphicRaycaster detects
///   the hit. Pressing the trigger fires the Button's onClick event —
///   exactly like a mouse click in the Editor.
///
///   Required on the Canvas: TrackedDeviceGraphicRaycaster (Meta XR SDK)
///   Required in the scene:  EventSystem (auto-created with any Canvas)
/// </summary>
[RequireComponent(typeof(Canvas))]
public class MenuCanvasSetup : MonoBehaviour
{
    private void Awake()
    {
        Canvas canvas = GetComponent<Canvas>();

        // 1. Confirm World Space mode
        if (canvas.renderMode != RenderMode.WorldSpace)
        {
            Debug.LogError("[MenuCanvasSetup] Canvas is NOT set to World Space! " +
                           "Change Render Mode to World Space in the Inspector.");
        }

        // 2. Confirm a camera is assigned (needed for World Space UI interaction)
        if (canvas.worldCamera == null)
        {
            // Auto-assign main camera as fallback
            if (Camera.main != null)
            {
                canvas.worldCamera = Camera.main;
                Debug.LogWarning("[MenuCanvasSetup] Canvas had no Event Camera — auto-assigned Camera.main. " +
                                 "For Quest, assign CenterEyeAnchor instead.");
            }
            else
            {
                Debug.LogError("[MenuCanvasSetup] No camera assigned to Canvas and no Camera.main found!");
            }
        }

        // 3. Check for a raycaster that Quest controllers can use
        // TrackedDeviceGraphicRaycaster is from the Meta XR SDK and handles controller rays.
        // Standard GraphicRaycaster only works with mouse/touch — NOT Quest controllers.
        var raycaster = GetComponent<UnityEngine.EventSystems.BaseRaycaster>();
        if (raycaster == null)
        {
            Debug.LogError("[MenuCanvasSetup] No Raycaster on Canvas! " +
                           "Add 'Tracked Device Graphic Raycaster' from the Meta XR SDK. " +
                           "Without it, Quest controller triggers will NOT click the buttons.");
        }
        else if (raycaster.GetType().Name == "GraphicRaycaster")
        {
            Debug.LogWarning("[MenuCanvasSetup] Canvas has a standard GraphicRaycaster. " +
                            "This works in the Editor but NOT with Quest controllers. " +
                            "Replace it with 'Tracked Device Graphic Raycaster' from Meta XR SDK.");
        }
        else
        {
            Debug.Log($"[MenuCanvasSetup] Raycaster OK: {raycaster.GetType().Name}");
        }

        // 4. Confirm EventSystem exists in scene
        if (FindAnyObjectByType<UnityEngine.EventSystems.EventSystem>() == null)
        {
            Debug.LogError("[MenuCanvasSetup] No EventSystem in scene! " +
                        "Add one via: Hierarchy → right-click → UI → Event System.");
        }
        else
        {
            Debug.Log("[MenuCanvasSetup] EventSystem found — UI interaction ready.");
        }
    }
}