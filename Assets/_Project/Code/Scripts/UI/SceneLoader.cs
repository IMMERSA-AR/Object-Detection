using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.EventSystems; // Required to check if the controller ray is hovering

public class SceneLoader : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public enum TargetScene
    {
        ObeliskScene,
        LectureHallScene,
        GraduationGalleryScene
    }

    [Header("Scene Selection")]
    [SerializeField] private TargetScene sceneToLoad;

    private Toggle toggleComponent;
    private bool isHovered = false;

    private void Awake()
    {
        toggleComponent = GetComponent<Toggle>();
    }

    private void Update()
    {
        // QUEST APPS BEHAVIOR: 
        // If the player's pointer ray is actively hovering over this card
        // AND they press the physical 'A' button on the Right Controller
        if (isHovered && toggleComponent != null && toggleComponent.interactable)
        {
            if (OVRInput.GetDown(OVRInput.Button.One))
            {
                ExecuteSceneLoad();
            }
        }
    }

    // This catches Meta's default Pointer Ray Click (Trigger button)
    public void LoadTargetScene(bool isOn)
    {
        if (isOn)
        {
            ExecuteSceneLoad();
        }
    }

    private void ExecuteSceneLoad()
    {
        string finalSceneName = sceneToLoad.ToString();
        Debug.Log($"Loading Scene: {finalSceneName}");
        SceneManager.LoadScene(finalSceneName);
    }

    // Track when the player's controller ray points at the button
    public void OnPointerEnter(PointerEventData eventData)
    {
        isHovered = true;
    }

    // Track when the player's controller ray points away
    public void OnPointerExit(PointerEventData eventData)
    {
        isHovered = false;
    }
}