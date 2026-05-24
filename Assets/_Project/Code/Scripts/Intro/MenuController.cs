using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;

/// <summary>
/// Controls the main menu scene.
/// Fades in from white (continuing from the intro white flash),
/// shows two floating scene cards, and loads the chosen scene with a fade.
///
/// Hierarchy expected:
///   MenuController GO
///   FadeCanvas / FadeImage          — full screen overlay
///   MenuRoot                        — parent of all world-space cards
///     Card_LectureHall
///     Card_Obelisk
///   TitleCanvas                     — world-space canvas with title text
/// </summary>
public class MenuController : MonoBehaviour
{
    // ── Scene Names ───────────────────────────────────────────────
    [Header("Scene Names")]
    [Tooltip("Exact scene name for the Lecture Hall experience.")]
    public string lectureHallSceneName = "SampleScene";

    [Tooltip("Exact scene name for the Obelisk experience.")]
    public string obeliskSceneName = "SampleScene";   // change when ready

    // ── Fade Overlay ──────────────────────────────────────────────
    [Header("Fade")]
    [Tooltip("Full-screen Image used for fade in / fade out.")]
    public Image fadeImage;

    [Tooltip("How long the fade-in from white takes on scene load.")]
    public float fadeInDuration = 1.2f;

    [Tooltip("How long the fade-out to black takes before loading a scene.")]
    public float fadeOutDuration = 0.8f;

    // ── Menu Cards ────────────────────────────────────────────────
    [Header("Menu Cards")]
    [Tooltip("Root that holds both cards — used to position/show them together.")]
    public GameObject menuRoot;

    [Tooltip("The Lecture Hall card GameObject.")]
    public GameObject cardLectureHall;

    [Tooltip("The Obelisk card GameObject.")]
    public GameObject cardObelisk;

    // ── Title ─────────────────────────────────────────────────────
    [Header("Title")]
    [Tooltip("World-space TextMeshPro showing the app title above the cards.")]
    public TextMeshProUGUI titleText;

    [Tooltip("Title string shown above the menu cards.")]
    public string titleString = "اختر تجربتك\nChoose Your Experience";

    // ── Positioning ───────────────────────────────────────────────
    [Header("Card Positioning")]
    [Tooltip("Distance from the camera to place the cards.")]
    public float cardDistance = 1.4f;

    [Tooltip("Height offset from camera position for the cards.")]
    public float cardHeightOffset = -0.1f;

    [Tooltip("Horizontal separation between the two cards (metres).")]
    public float cardSpacing = 0.55f;

    // ── Private ───────────────────────────────────────────────────
    private bool _inputLocked = true;   // prevents clicks during fade-in
    private Camera _cam;

    // ─────────────────────────────────────────────────────────────

    private void Start()
    {
        _cam = Camera.main;

        // Start from white (continuing from intro's white-flash transition)
        SetFade(1f, Color.white);

        if (menuRoot != null) menuRoot.SetActive(false);

        if (titleText != null)
        {
            titleText.text = titleString;
            titleText.gameObject.SetActive(false);
        }

        StartCoroutine(MenuEnterSequence());
    }

    // ── Enter Sequence ────────────────────────────────────────────

    private IEnumerator MenuEnterSequence()
    {
        // Position cards in front of the camera before revealing them
        PositionCardsInFrontOfCamera();

        yield return new WaitForSeconds(0.1f);

        // Fade from white to passthrough
        yield return StartCoroutine(FadeTo(0f, Color.white, fadeInDuration));

        // Show title then cards
        if (titleText != null)
        {
            titleText.gameObject.SetActive(true);
            yield return StartCoroutine(FadeGraphicAlpha(titleText, 0f, 1f, 0.5f));
        }

        if (menuRoot != null)
        {
            menuRoot.SetActive(true);
            yield return StartCoroutine(FadeCanvasGroupAlpha(
                menuRoot.GetComponent<CanvasGroup>() ?? menuRoot.AddComponent<CanvasGroup>(),
                0f, 1f, 0.6f));
        }

        _inputLocked = false;
        Debug.Log("[Menu] Ready — waiting for user selection.");
    }

    // ── Card Positioning ──────────────────────────────────────────

    private void PositionCardsInFrontOfCamera()
    {
        if (_cam == null) return;

        // Flatten camera forward so cards appear level even if user looks up/down
        Vector3 forward = _cam.transform.forward;
        forward.y = 0f;
        forward.Normalize();

        Vector3 right   = Vector3.Cross(Vector3.up, forward) * -1f; // camera right
        Vector3 center  = _cam.transform.position
                        + forward * cardDistance
                        + Vector3.up * cardHeightOffset;

        // Face cards toward the camera
        Quaternion faceCam = Quaternion.LookRotation(-forward);

        if (cardLectureHall != null)
        {
            cardLectureHall.transform.position = center - right * cardSpacing;
            cardLectureHall.transform.rotation = faceCam;
        }

        if (cardObelisk != null)
        {
            cardObelisk.transform.position = center + right * cardSpacing;
            cardObelisk.transform.rotation = faceCam;
        }

        // Title sits above the center point
        if (titleText != null)
        {
            titleText.transform.position = center + Vector3.up * 0.38f;
            titleText.transform.rotation = faceCam;
        }
    }

    // ── Public Button Callbacks ───────────────────────────────────
    // Wire these to the Button component on each card via OnClick() in the Inspector.

    public void OnLectureHallSelected()
    {
        if (_inputLocked) return;
        StartCoroutine(LoadScene(lectureHallSceneName));
    }

    public void OnObeliskSelected()
    {
        if (_inputLocked) return;
        StartCoroutine(LoadScene(obeliskSceneName));
    }

    // ── Scene Load ────────────────────────────────────────────────

    private IEnumerator LoadScene(string sceneName)
    {
        _inputLocked = true;

        Debug.Log($"[Menu] Loading scene: {sceneName}");

        // Fade cards and title out first
        if (menuRoot != null)
        {
            CanvasGroup cg = menuRoot.GetComponent<CanvasGroup>();
            if (cg != null)
                yield return StartCoroutine(FadeCanvasGroupAlpha(cg, 1f, 0f, 0.3f));
        }

        if (titleText != null)
            yield return StartCoroutine(FadeGraphicAlpha(titleText, 1f, 0f, 0.3f));

        // Fade to black then load
        yield return StartCoroutine(FadeTo(1f, Color.black, fadeOutDuration));

        SceneManager.LoadScene(sceneName);
    }

    // ── Fade Helpers ──────────────────────────────────────────────

    private void SetFade(float alpha, Color color)
    {
        if (fadeImage == null) return;
        color.a = alpha;
        fadeImage.color = color;
    }

    private IEnumerator FadeTo(float targetAlpha, Color color, float duration)
    {
        if (fadeImage == null) yield break;

        float startAlpha = fadeImage.color.a;
        float elapsed    = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            color.a  = Mathf.Lerp(startAlpha, targetAlpha, elapsed / duration);
            fadeImage.color = color;
            yield return null;
        }

        color.a = targetAlpha;
        fadeImage.color = color;
    }

    private IEnumerator FadeCanvasGroupAlpha(CanvasGroup cg, float from, float to, float duration)
    {
        if (cg == null) yield break;
        cg.alpha = from;
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed  += Time.deltaTime;
            cg.alpha  = Mathf.Lerp(from, to, elapsed / duration);
            yield return null;
        }
        cg.alpha = to;
        // Block/unblock raycasts based on visibility
        cg.interactable   = to > 0.5f;
        cg.blocksRaycasts = to > 0.5f;
    }

    private IEnumerator FadeGraphicAlpha(Graphic graphic, float from, float to, float duration)
    {
        if (graphic == null) yield break;
        Color c  = graphic.color;
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            c.a      = Mathf.Lerp(from, to, elapsed / duration);
            graphic.color = c;
            yield return null;
        }
        c.a = to;
        graphic.color = c;
    }
}
