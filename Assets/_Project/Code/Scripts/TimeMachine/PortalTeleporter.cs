using UnityEngine;

public class TimePortalLogic : MonoBehaviour
{
    [Header("World Settings")]
    public GameObject historicalWorld; // Drag a folder/empty containing your old characters here
    public bool startInPast = false;

    private void Start()
    {
        // Set the initial state of the world
        historicalWorld.SetActive(startInPast);
    }

    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Player"))
        {
            // Toggle the historical world on/off
            bool currentState = historicalWorld.activeSelf;
            historicalWorld.SetActive(!currentState);

            Debug.Log("Time Travel Successful! Historical World is now: " + !currentState);

            // Optional: You could trigger a screen flash or sound effect here
        }
    }
}