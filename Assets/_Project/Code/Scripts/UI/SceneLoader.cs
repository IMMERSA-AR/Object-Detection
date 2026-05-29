using UnityEngine;
using UnityEngine.SceneManagement;

public class SceneLoader : MonoBehaviour
{
    // 1. Define your exact available scenes here
    public enum TargetScene
    {
        ObeliskScene,
        LectureHallScene
    }

    [Header("Scene Selection")]
    [Tooltip("Pick your destination from the dropdown list.")]
    [SerializeField] private TargetScene sceneToLoad;

    public void LoadTargetScene(bool isOn)
    {
        if (!isOn) return;

        // Convert the chosen enum value directly into a text string
        string finalSceneName = sceneToLoad.ToString();

        Debug.Log($"Loading Scene: {finalSceneName}");
        SceneManager.LoadScene(finalSceneName);
    }
}