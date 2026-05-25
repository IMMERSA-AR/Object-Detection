using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// World-space status HUD for the obelisk experience.
///
/// Self-builds its UI hierarchy at runtime — no scene setup required.
/// Add this component to an empty GameObject and drag that GameObject
/// into the ObeliskYOLODetector's "Status HUD" slot.
///
/// Visual:
///   ┌─────────────────────────────────────────┐
///   │█  DETECTING OBELISK     ● ● ● ○ ○       │   <- 3/5 hits, blue pips
///   └─────────────────────────────────────────┘
///   ┌─────────────────────────────────────────┐
///   │█  OBELISK FOUND         ● ● ● ● ●       │   <- all green, panel bounces
///   └─────────────────────────────────────────┘
///
/// API:
///   ShowScanning(int totalHits) — start scanning with N empty pips.
///   SetProgress(int current, int total) — fill the first `current` pips.
///   ShowFound() — turn all pips green, bounce.
///   Hide() — fade out.
/// </summary>
public class ObeliskStatusHUD : MonoBehaviour
{
    // ── Placement ─────────────────────────────────────────────────
    [Header("Placement")]
    [Tooltip("Metres in front of the camera the HUD floats.")]
    [SerializeField] private float distance = 1.6f;

    [Tooltip("Vertical offset from camera position (negative = below eye level).")]
    [SerializeField] private float verticalOffset = -0.15f;

    [Tooltip("0 = HUD instantly snaps to head. ~0.85 feels natural.")]
    [Range(0f, 0.99f)]
    [SerializeField] private float followLag = 0.85f;

    // ── Look ──────────────────────────────────────────────────────
    [Header("Look")]
    [SerializeField] private Vector2 panelSize       = new Vector2(0.70f, 0.14f);
    [SerializeField] private Color   backgroundColor = new Color(0.04f, 0.06f, 0.10f, 0.88f);
    [SerializeField] private Color   scanningAccent  = new Color(0.30f, 0.65f, 1.00f);
    [SerializeField] private Color   foundAccent     = new Color(0.35f, 0.90f, 0.55f);
    [SerializeField] private Color   pipEmptyColor   = new Color(1f, 1f, 1f, 0.18f);

    // ── Messages ──────────────────────────────────────────────────
    [Header("Messages")]
    [SerializeField] private string scanningMessage = "DETECTING OBELISK";
    [SerializeField] private string foundMessage    = "OBELISK FOUND";

    // ── State ─────────────────────────────────────────────────────
    private enum State { Hidden, Scanning, Found }
    private State _state = State.Hidden;
    private int   _totalHits = 5;

    // ── Generated UI refs ─────────────────────────────────────────
    private Camera        _cam;
    private CanvasGroup   _group;
    private RectTransform _panel;
    private Image         _panelBg;
    private Image         _accentBar;
    private TMP_Text      _label;
    private RectTransform _pipsContainer;
    private readonly List<Image> _pips = new List<Image>();

    private Coroutine _stateRoutine;

    // ── Lifecycle ─────────────────────────────────────────────────

    private void Awake()
    {
        BuildHud();
        _group.alpha = 0f;
    }

    private void LateUpdate()
    {
        if (_state == State.Hidden) return;
        if (_cam == null) _cam = Camera.main;
        if (_cam == null) return;

        Vector3 target = _cam.transform.position
                       + _cam.transform.forward * distance
                       + _cam.transform.up      * verticalOffset;

        transform.position = Vector3.Lerp(transform.position, target, 1f - followLag);

        // Billboard
        Vector3 lookDir = transform.position - _cam.transform.position;
        if (lookDir.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.LookRotation(lookDir);
    }

    // ── Public API ────────────────────────────────────────────────

    /// <summary>Start the scanning state with `totalHits` empty pips.</summary>
    public void ShowScanning(int totalHits)
    {
        _totalHits = Mathf.Max(1, totalHits);
        EnsurePips(_totalHits);
        PaintPips(filledCount: 0, filledColor: scanningAccent);
        GoTo(State.Scanning);
    }

    /// <summary>Update the pip count. Call every time `_consecutiveHits` changes.</summary>
    public void SetProgress(int currentHits, int totalHits)
    {
        bool totalChanged = (totalHits != _totalHits);
        _totalHits = Mathf.Max(1, totalHits);
        int clamped = Mathf.Clamp(currentHits, 0, _totalHits);

        if (totalChanged) EnsurePips(_totalHits);
        PaintPips(filledCount: clamped, filledColor: scanningAccent);

        // Pop animation on the newly-filled pip
        if (clamped > 0 && clamped <= _pips.Count)
            StartCoroutine(PopPip(_pips[clamped - 1]));
    }

    /// <summary>All pips green + label switches to "OBELISK FOUND" + panel bounce.</summary>
    public void ShowFound()
    {
        EnsurePips(_totalHits);
        PaintPips(filledCount: _totalHits, filledColor: foundAccent);
        GoTo(State.Found);
    }

    public void Hide() => GoTo(State.Hidden);

    // ── State transitions ─────────────────────────────────────────

    private void GoTo(State newState)
    {
        if (_state == newState) return;
        _state = newState;

        if (_stateRoutine != null) StopCoroutine(_stateRoutine);
        _stateRoutine = StartCoroutine(TransitionTo(newState));
    }

    private IEnumerator TransitionTo(State target)
    {
        // Snap into place if appearing from hidden
        if (_group.alpha < 0.01f && target != State.Hidden && Camera.main != null)
        {
            Camera c = Camera.main;
            transform.position = c.transform.position
                               + c.transform.forward * distance
                               + c.transform.up      * verticalOffset;
        }

        switch (target)
        {
            case State.Scanning:
                _label.text      = scanningMessage;
                _accentBar.color = scanningAccent;
                break;
            case State.Found:
                _label.text      = foundMessage;
                _accentBar.color = foundAccent;
                StartCoroutine(FoundBounce());
                break;
        }

        // Fade
        float startAlpha = _group.alpha;
        float endAlpha   = (target == State.Hidden) ? 0f : 1f;
        const float duration = 0.30f;
        float t = 0f;
        while (t < duration)
        {
            t += Time.unscaledDeltaTime;
            _group.alpha = Mathf.SmoothStep(startAlpha, endAlpha, t / duration);
            yield return null;
        }
        _group.alpha = endAlpha;
    }

    // ── Animations ────────────────────────────────────────────────

    private IEnumerator FoundBounce()
    {
        const float duration = 0.40f;
        float t = 0f;
        while (t < duration)
        {
            t += Time.unscaledDeltaTime;
            float p = t / duration;
            float scale = 1f + 0.12f * Mathf.Sin(p * Mathf.PI);
            if (_panel != null) _panel.localScale = Vector3.one * scale;
            yield return null;
        }
        if (_panel != null) _panel.localScale = Vector3.one;
    }

    private IEnumerator PopPip(Image pip)
    {
        if (pip == null) yield break;
        RectTransform rect = pip.rectTransform;
        const float duration = 0.25f;
        float t = 0f;
        while (t < duration)
        {
            t += Time.unscaledDeltaTime;
            float p = t / duration;
            float scale = 1f + 0.55f * Mathf.Sin(p * Mathf.PI);
            rect.localScale = Vector3.one * scale;
            yield return null;
        }
        rect.localScale = Vector3.one;
    }

    // ── Pips ──────────────────────────────────────────────────────

    private void PaintPips(int filledCount, Color filledColor)
    {
        for (int i = 0; i < _pips.Count; i++)
            _pips[i].color = (i < filledCount) ? filledColor : pipEmptyColor;
    }

    private void EnsurePips(int count)
    {
        // Remove excess
        while (_pips.Count > count)
        {
            int last = _pips.Count - 1;
            if (_pips[last] != null) Destroy(_pips[last].gameObject);
            _pips.RemoveAt(last);
        }
        // Add missing
        while (_pips.Count < count)
            _pips.Add(CreatePip(_pips.Count));

        LayoutPips();
    }

    private Image CreatePip(int index)
    {
        var pipGo = new GameObject($"Pip{index}", typeof(RectTransform));
        pipGo.transform.SetParent(_pipsContainer, false);
        var img = pipGo.AddComponent<Image>();
        img.color         = pipEmptyColor;
        img.raycastTarget = false;
        return img;
    }

    private void LayoutPips()
    {
        if (_pips.Count == 0) return;

        float pipSize    = panelSize.y * 0.32f;
        float gap        = pipSize * 0.45f;
        float totalWidth = _pips.Count * pipSize + (_pips.Count - 1) * gap;
        float startX     = -totalWidth * 0.5f + pipSize * 0.5f;

        for (int i = 0; i < _pips.Count; i++)
        {
            var rt = _pips[i].rectTransform;
            rt.anchorMin        = new Vector2(0.5f, 0.5f);
            rt.anchorMax        = new Vector2(0.5f, 0.5f);
            rt.pivot            = new Vector2(0.5f, 0.5f);
            rt.sizeDelta        = new Vector2(pipSize, pipSize);
            rt.anchoredPosition = new Vector2(startX + i * (pipSize + gap), 0f);
            rt.localScale       = Vector3.one;
        }
    }

    // ── UI construction ───────────────────────────────────────────

    private T GetOrAdd<T>() where T : Component
    {
        T c = gameObject.GetComponent<T>();
        return c != null ? c : gameObject.AddComponent<T>();
    }

    private void BuildHud()
    {
        // Canvas (world space)
        Canvas canvas = GetOrAdd<Canvas>();
        canvas.renderMode   = RenderMode.WorldSpace;
        canvas.sortingOrder = 1000;
        GetOrAdd<CanvasScaler>();
        GetOrAdd<GraphicRaycaster>();
        _group = GetOrAdd<CanvasGroup>();
        _group.interactable  = false;
        _group.blocksRaycasts = false;

        RectTransform rootRect = (RectTransform)transform;
        rootRect.sizeDelta  = panelSize;
        rootRect.localScale = Vector3.one;

        // Background panel
        var panelGo = new GameObject("Panel", typeof(RectTransform));
        panelGo.transform.SetParent(transform, false);
        _panel = (RectTransform)panelGo.transform;
        _panel.anchorMin = Vector2.zero;
        _panel.anchorMax = Vector2.one;
        _panel.offsetMin = Vector2.zero;
        _panel.offsetMax = Vector2.zero;
        _panelBg = panelGo.AddComponent<Image>();
        _panelBg.color         = backgroundColor;
        _panelBg.raycastTarget = false;

        // Accent bar (left edge)
        var accentGo = new GameObject("AccentBar", typeof(RectTransform));
        accentGo.transform.SetParent(_panel, false);
        var accentRect = (RectTransform)accentGo.transform;
        accentRect.anchorMin        = new Vector2(0f, 0f);
        accentRect.anchorMax        = new Vector2(0f, 1f);
        accentRect.pivot            = new Vector2(0f, 0.5f);
        accentRect.sizeDelta        = new Vector2(panelSize.x * 0.022f, 0f);
        accentRect.anchoredPosition = Vector2.zero;
        _accentBar = accentGo.AddComponent<Image>();
        _accentBar.color         = scanningAccent;
        _accentBar.raycastTarget = false;

        // Label (left ~60% of the panel)
        var labelGo = new GameObject("Label", typeof(RectTransform));
        labelGo.transform.SetParent(_panel, false);
        var labelRect = (RectTransform)labelGo.transform;
        labelRect.anchorMin = new Vector2(0f, 0f);
        labelRect.anchorMax = new Vector2(0.62f, 1f);
        labelRect.offsetMin = new Vector2(panelSize.y * 0.45f, panelSize.y * 0.10f);
        labelRect.offsetMax = new Vector2(0f, -panelSize.y * 0.10f);
        _label = labelGo.AddComponent<TextMeshProUGUI>();
        _label.text             = scanningMessage;
        _label.color            = Color.white;
        _label.alignment        = TextAlignmentOptions.MidlineLeft;
        _label.fontStyle        = FontStyles.Bold;
        _label.characterSpacing = 8f;
        _label.enableAutoSizing = true;
        _label.fontSizeMin      = 10;
        _label.fontSizeMax      = 200;
        _label.raycastTarget    = false;

        // Pips container (right ~38% of the panel)
        var pipsGo = new GameObject("PipsContainer", typeof(RectTransform));
        pipsGo.transform.SetParent(_panel, false);
        _pipsContainer = (RectTransform)pipsGo.transform;
        _pipsContainer.anchorMin = new Vector2(0.62f, 0f);
        _pipsContainer.anchorMax = new Vector2(1f, 1f);
        _pipsContainer.offsetMin = Vector2.zero;
        _pipsContainer.offsetMax = new Vector2(-panelSize.y * 0.25f, 0f);

        // Pre-create default pip count
        EnsurePips(_totalHits);
    }
}
