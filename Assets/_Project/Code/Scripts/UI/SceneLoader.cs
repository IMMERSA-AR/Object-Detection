using UnityEngine;
using UnityEngine.SceneManagement;

public class SceneLoader : MonoBehaviour
{
    // This allows you to type the exact target scene name in the Inspector for each button
    [Header("Scene Configuration")]
    [SerializeField] private string sceneToLoad;

    // This public method can be called by your button's OnClick event
    public void LoadTargetScene(bool isOn)
    {
        // Only load the scene when the toggle is being switched to TRUE (clicked on)
        if (isOn && !string.IsNullOrEmpty(sceneToLoad))
        {
            Debug.Log($"Loading Scene: {sceneToLoad}");
            SceneManager.LoadScene(sceneToLoad);
        }
        else
        {
            Debug.LogWarning($"No scene name assigned on {gameObject.name}!");
        }
    }
}