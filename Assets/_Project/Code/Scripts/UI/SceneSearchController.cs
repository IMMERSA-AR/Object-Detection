using UnityEngine;
using TMPro;

public class SceneSearchController : MonoBehaviour
{
    [Header("UI Elements")]
    [SerializeField] private TMP_InputField searchInputField;

    [Header("Filtering Target")]
    [Tooltip("Drag the 'Content' GameObject from your ScrollView here.")]
    [SerializeField] private Transform cardsContentParent;

    private TouchScreenKeyboard overlayKeyboard;

    void Start()
    {
        // Listen for when the user clicks/pokes the search bar to bring up the keyboard
        searchInputField.onSelect.AddListener(OpenVirtualKeyboard);

        // Listen for live typing to filter the visual cards instantly
        searchInputField.onValueChanged.AddListener(FilterDestinationCards);
    }

    private void OpenVirtualKeyboard(string currentText)
    {
        if (TouchScreenKeyboard.isSupported)
        {
            overlayKeyboard = TouchScreenKeyboard.Open(searchInputField.text, TouchScreenKeyboardType.Default);
        }
    }

    void Update()
    {
        // Update the search bar text live from the Meta VR Keyboard
        if (overlayKeyboard != null && overlayKeyboard.status == TouchScreenKeyboard.Status.Visible)
        {
            searchInputField.text = overlayKeyboard.text;
        }
    }

    private void OnDisable()
    {
        if (searchInputField != null)
        {
            searchInputField.onSelect.RemoveListener(OpenVirtualKeyboard);
            searchInputField.onValueChanged.RemoveListener(FilterDestinationCards);
        }
    }

    // Core Filtering Logic
    private void FilterDestinationCards(string searchText)
    {
        if (cardsContentParent == null) return;

        // If the search bar is empty, show all cards
        bool isSearchEmpty = string.IsNullOrEmpty(searchText);

        // Loop through every child card inside the ScrollView Content container
        foreach (Transform card in cardsContentParent)
        {
            if (isSearchEmpty)
            {
                // Show everything if nothing is typed
                card.gameObject.SetActive(true);
            }
            else
            {
                // Check if the Card's name contains the typed letters (case-insensitive)
                bool isMatch = card.name.IndexOf(searchText, System.StringComparison.OrdinalIgnoreCase) >= 0;

                // Hide if it doesn't match; Show if it does!
                card.gameObject.SetActive(isMatch);
            }
        }
    }
}