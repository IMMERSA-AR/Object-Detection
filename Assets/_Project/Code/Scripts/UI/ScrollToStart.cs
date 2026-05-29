using UnityEngine;
using UnityEngine.UI;

public class ScrollToStart : MonoBehaviour
{
    void Awake()
    {
        Debug.Log("AWAKE CALLED");
        GetComponent<ScrollRect>().horizontalNormalizedPosition = 0f;
    }
}