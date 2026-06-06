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
        if (isHovered && toggleComponent != null && toggleComponent.interactable)
        {
            if (OVRInput.GetDown(OVRInput.Button.One))
            {
                ExecuteSceneLoad();
            }
        }
    }

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

    public void OnPointerEnter(PointerEventData eventData)
    {
        isHovered = true;
    }

    public void OnPointerExit(PointerEventData eventData)
    {
        isHovered = false;
    }
}