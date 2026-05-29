using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Meta-Quest-style status HUD for the obelisk experience.
///
/// Self-builds its UI hierarchy at runtime using procedurally generated
/// rounded-rect / circle sprites — no scene setup or assets required.
///
/// Visual: a dark card with soft drop shadow, circular accent icon on the
/// left, title + subtitle in the middle, and a smooth animated progress
/// bar at the bottom. Mirrors Meta's own system-UI toast style.
///
/// Auto-hides a configurable number of seconds after detection completes.
///
/// API (called by ObeliskYOLODetector):
///   ShowScanning(int totalHits)
///   SetProgress(int currentHits, int totalHits)
///   ShowFound()      -> auto-hides after `foundHideDelay` seconds
///   Hide()
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
    [SerializeField] private Vector2 panelSize = new Vector2(0.80f, 0.26f);

    [Tooltip("Dark card colour. Default matches Meta Quest system panels.")]
    [SerializeField] private Color panelColor = new Color(0.106f, 0.118f, 0.149f, 0.96f);

    [Tooltip("Primary accent while scanning. Default = Meta blue.")]
    [SerializeField] private Color scanningAccent = new Color(0.094f, 0.467f, 0.949f);

    [Tooltip("Primary accent on successful detection.")]
    [SerializeField] private Color foundAccent = new Color(0.259f, 0.722f, 0.514f);

    [SerializeField] private Color titleColor    = Color.white;
    [SerializeField] private Color subtitleColor = new Color(0.718f, 0.741f, 0.776f);

    [Tooltip("Track behind the progress bar (the empty part).")]
    [SerializeField] private Color trackColor = new Color(1f, 1f, 1f, 0.10f);

    // ── Shadow ────────────────────────────────────────────────────
    [Header("Shadow")]
    [Range(0f, 1f)]
    [SerializeField] private float shadowStrength = 0.35f;
    [Tooltip("How far the soft shadow extends, in metres.")]
    [SerializeField] private float shadowSpread = 0.045f;
    [Tooltip("Vertical drop of the shadow, in metres.")]
    [SerializeField] private float shadowYOffset = 0.012f;

    // ── Messages ──────────────────────────────────────────────────
    [Header("Messages")]
    [SerializeField] private string scanningMessage  = "DETECTING OBELISK";
    [SerializeField] private string foundMessage     = "OBELISK FOUND";
    [Tooltip("{0} = current hits, {1} = total.")]
    [SerializeField] private string scanningSubtitle = "{0} of {1} confirmed";
    [SerializeField] private string foundSubtitle    = "Experience starting";

    // ── Auto-hide ─────────────────────────────────────────────────
    [Header("Behaviour")]
    [Tooltip("Seconds to keep the 'Found' state visible before fading out. " +
             "Set to 0 to disable auto-hide.")]
    [SerializeField] private float foundHideDelay = 2.0f;

    // ── Animation ─────────────────────────────────────────────────
    [Header("Animation")]
    [Tooltip("How quickly the progress bar catches up to its target value.")]
    [SerializeField] private float fillResponseSpeed = 8f;

    // ── State ─────────────────────────────────────────────────────
    private enum State { Hidden, Scanning, Found }
    private State _state     = State.Hidden;
    private int   _totalHits = 5;
    private int   _currentHits;
    private float _currentFillRatio;
    private float _targetFillRatio;

    // ── Generated UI refs ─────────────────────────────────────────
    private Camera        _cam;
    private CanvasGroup   _group;
    private RectTransform _panel;
    private Image         _panelBg;
    private Image[]       _shadowLayers;
    private RectTransform _iconRoot;
    private Image         _iconBg;
    private TMP_Text      _iconText;       // shows "●" or "✓"
    private TMP_Text      _title;
    private TMP_Text      _subtitle;
    private RectTransform _progressTrack;
    private RectTransform _progressFill;
    private Image         _progressFillImg;

    // ── Procedural sprites ────────────────────────────────────────
    private Sprite _roundedRectSprite;
    private Sprite _pillSprite;
    private Sprite _circleSprite;

    // ── Coroutines ────────────────────────────────────────────────
    private Coroutine _stateRoutine;
    private Coroutine _autoHideRoutine;

    // ── Lifecycle ─────────────────────────────────────────────────

    private void Awake()
    {
        _roundedRectSprite = CreateRoundedRectSprite(64, 22);
        _pillSprite        = CreateRoundedRectSprite(32, 14);
        _circleSprite      = CreateCircleSprite(64);

        BuildHud();
        _group.alpha = 0f;
    }

    private void OnDestroy()
    {
        DestroySpriteAndTexture(_roundedRectSprite);
        DestroySpriteAndTexture(_pillSprite);
        DestroySpriteAndTexture(_circleSprite);
    }

    private void LateUpdate()
    {
        // Follow the camera
        if (_state != State.Hidden)
        {
            if (_cam == null) _cam = Camera.main;
            if (_cam != null)
            {
                Vector3 target = _cam.transform.position
                               + _cam.transform.forward * distance
                               + _cam.transform.up      * verticalOffset;

                transform.position = Vector3.Lerp(transform.position, target, 1f - followLag);

                Vector3 lookDir = transform.position - _cam.transform.position;
                if (lookDir.sqrMagnitude > 0.0001f)
                    transform.rotation = Quaternion.LookRotation(lookDir);
            }
        }

        // Animate the progress bar smoothly
        if (_progressTrack != null && Mathf.Abs(_currentFillRatio - _targetFillRatio) > 0.0005f)
        {
            _currentFillRatio = Mathf.Lerp(
                _currentFillRatio, _targetFillRatio,
                Time.unscaledDeltaTime * fillResponseSpeed);
            ApplyFillWidth();
        }
    }

    // ── Public API ────────────────────────────────────────────────

    public void ShowScanning(int totalHits)
    {
        _totalHits = Mathf.Max(1, totalHits);
        _currentHits = 0;
        _targetFillRatio = 0f;
        _currentFillRatio = 0f;
        ApplyFillWidth();
        UpdateSubtitleForScanning();

        CancelAutoHide();
        GoTo(State.Scanning);
    }

    public void SetProgress(int currentHits, int totalHits)
    {
        _totalHits  = Mathf.Max(1, totalHits);
        _currentHits = Mathf.Clamp(currentHits, 0, _totalHits);
        _targetFillRatio = (float)_currentHits / _totalHits;

        if (_state == State.Scanning)
            UpdateSubtitleForScanning();
    }

    public void ShowFound()
    {
        _currentHits = _totalHits;
        _targetFillRatio = 1f;
        if (_subtitle != null) _subtitle.text = foundSubtitle;

        GoTo(State.Found);
        ScheduleAutoHide();
    }

    public void Hide()
    {
        CancelAutoHide();
        GoTo(State.Hidden);
    }

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
        // Snap to camera if appearing from hidden
        if (_group.alpha < 0.01f && target != State.Hidden && Camera.main != null)
        {
            Camera c = Camera.main;
            transform.position = c.transform.position
                               + c.transform.forward * distance
                               + c.transform.up      * verticalOffset;
        }

        Color accent = (target == State.Found) ? foundAccent : scanningAccent;

        switch (target)
        {
            case State.Scanning:
                _title.text     = scanningMessage;
                _iconBg.color   = accent;
                _iconText.text  = "●";
                _iconText.color = Color.white;
                _progressFillImg.color = accent;
                ColorShadows(scanningAccent, isFound: false);
                break;
            case State.Found:
                _title.text     = foundMessage;
                _iconBg.color   = foundAccent;
                _iconText.text  = "✓";
                _iconText.color = Color.white;
                _progressFillImg.color = foundAccent;
                ColorShadows(foundAccent, isFound: true);
                StartCoroutine(FoundBounce());
                break;
        }

        // Spring-ease fade
        float startAlpha = _group.alpha;
        float endAlpha   = (target == State.Hidden) ? 0f : 1f;
        const float duration = 0.40f;
        float t = 0f;
        while (t < duration)
        {
            t += Time.unscaledDeltaTime;
            float p = Mathf.Clamp01(t / duration);
            // smootherstep
            float e = p * p * p * (p * (p * 6 - 15) + 10);
            _group.alpha = Mathf.Lerp(startAlpha, endAlpha, e);
            yield return null;
        }
        _group.alpha = endAlpha;

        if (target == State.Hidden)
        {
            // Reset for next time
            _currentFillRatio = 0f;
            _targetFillRatio  = 0f;
            ApplyFillWidth();
        }
    }

    // ── Subtitle helpers ──────────────────────────────────────────

    private void UpdateSubtitleForScanning()
    {
        if (_subtitle == null) return;
        _subtitle.text = string.Format(scanningSubtitle, _currentHits, _totalHits);
    }

    // ── Auto-hide ─────────────────────────────────────────────────

    private void ScheduleAutoHide()
    {
        CancelAutoHide();
        if (foundHideDelay > 0f)
            _autoHideRoutine = StartCoroutine(AutoHideAfter(foundHideDelay));
    }

    private void CancelAutoHide()
    {
        if (_autoHideRoutine != null)
        {
            StopCoroutine(_autoHideRoutine);
            _autoHideRoutine = null;
        }
    }

    private IEnumerator AutoHideAfter(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        _autoHideRoutine = null;
        GoTo(State.Hidden);
    }

    // ── Animations ────────────────────────────────────────────────

    private IEnumerator FoundBounce()
    {
        const float duration = 0.50f;
        float t = 0f;
        while (t < duration)
        {
            t += Time.unscaledDeltaTime;
            float p = t / duration;
            float ease = Mathf.Sin(p * Mathf.PI);
            float scale = 1f + 0.08f * ease;
            if (_panel != null) _panel.localScale = Vector3.one * scale;
            yield return null;
        }
        if (_panel != null) _panel.localScale = Vector3.one;
    }

    private void ApplyFillWidth()
    {
        if (_progressFill == null || _progressTrack == null) return;
        float trackWidth = _progressTrack.rect.width;
        float fillWidth  = trackWidth * Mathf.Clamp01(_currentFillRatio);
        _progressFill.sizeDelta = new Vector2(fillWidth, _progressFill.sizeDelta.y);
    }

    private void ColorShadows(Color baseAccent, bool isFound)
    {
        if (_shadowLayers == null) return;
        // Subtle tint: ~10% mix of accent into black so the shadow picks up
        // a hint of the state colour without looking colourised.
        Color shadowBase = Color.Lerp(Color.black, baseAccent, isFound ? 0.18f : 0.10f);
        for (int i = 0; i < _shadowLayers.Length; i++)
        {
            float alpha = _shadowLayers[i].color.a;
            Color c = shadowBase;
            c.a = alpha;
            _shadowLayers[i].color = c;
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
        // ── Canvas ────────────────────────────────────────────────
        Canvas canvas = GetOrAdd<Canvas>();
        canvas.renderMode   = RenderMode.WorldSpace;
        canvas.sortingOrder = 1000;
        GetOrAdd<CanvasScaler>();
        GetOrAdd<GraphicRaycaster>();
        _group = GetOrAdd<CanvasGroup>();
        _group.interactable   = false;
        _group.blocksRaycasts = false;

        RectTransform rootRect = (RectTransform)transform;
        Vector2 rootSize = panelSize + new Vector2(shadowSpread * 2.5f, shadowSpread * 2.5f);
        rootRect.sizeDelta  = rootSize;
        rootRect.localScale = Vector3.one;

        // ── Drop shadow (3 stacked layers) ────────────────────────
        _shadowLayers = new Image[3];
        float[] sizes   = { 0.012f, 0.025f, shadowSpread };
        float[] alphas  = { 0.30f * shadowStrength,
                            0.18f * shadowStrength,
                            0.08f * shadowStrength };

        for (int i = 0; i < 3; i++)
        {
            var shadowGo = new GameObject($"Shadow{i + 1}", typeof(RectTransform));
            shadowGo.transform.SetParent(transform, false);
            var sRect = (RectTransform)shadowGo.transform;
            sRect.anchorMin = new Vector2(0.5f, 0.5f);
            sRect.anchorMax = new Vector2(0.5f, 0.5f);
            sRect.pivot     = new Vector2(0.5f, 0.5f);
            sRect.sizeDelta = panelSize + new Vector2(sizes[i] * 2f, sizes[i] * 2f);
            sRect.anchoredPosition = new Vector2(0f, -shadowYOffset);
            var img = shadowGo.AddComponent<Image>();
            img.sprite        = _roundedRectSprite;
            img.type          = Image.Type.Sliced;
            img.pixelsPerUnitMultiplier = 1f;
            img.color         = new Color(0f, 0f, 0f, alphas[i]);
            img.raycastTarget = false;
            _shadowLayers[i] = img;
        }

        // ── Background panel ──────────────────────────────────────
        var panelGo = new GameObject("Panel", typeof(RectTransform));
        panelGo.transform.SetParent(transform, false);
        _panel = (RectTransform)panelGo.transform;
        _panel.anchorMin = new Vector2(0.5f, 0.5f);
        _panel.anchorMax = new Vector2(0.5f, 0.5f);
        _panel.pivot     = new Vector2(0.5f, 0.5f);
        _panel.sizeDelta = panelSize;
        _panel.anchoredPosition = Vector2.zero;
        _panelBg = panelGo.AddComponent<Image>();
        _panelBg.sprite                = _roundedRectSprite;
        _panelBg.type                  = Image.Type.Sliced;
        _panelBg.pixelsPerUnitMultiplier = 1f;
        _panelBg.color                 = panelColor;
        _panelBg.raycastTarget         = false;

        // ── Layout dimensions ─────────────────────────────────────
        const float paddingFrac = 0.10f;          // fraction of panel height used as padding
        float padding     = panelSize.y * paddingFrac;
        float iconSize    = panelSize.y * 0.45f;
        float textX       = padding + iconSize + padding * 0.6f;
        float progressY   = panelSize.y * 0.18f;  // height of the progress bar area from bottom

        // ── Icon ──────────────────────────────────────────────────
        var iconRootGo = new GameObject("Icon", typeof(RectTransform));
        iconRootGo.transform.SetParent(_panel, false);
        _iconRoot = (RectTransform)iconRootGo.transform;
        _iconRoot.anchorMin = new Vector2(0f, 1f);
        _iconRoot.anchorMax = new Vector2(0f, 1f);
        _iconRoot.pivot     = new Vector2(0f, 1f);
        _iconRoot.sizeDelta = new Vector2(iconSize, iconSize);
        _iconRoot.anchoredPosition = new Vector2(padding, -padding);

        _iconBg = iconRootGo.AddComponent<Image>();
        _iconBg.sprite        = _circleSprite;
        _iconBg.color         = scanningAccent;
        _iconBg.raycastTarget = false;

        var iconTextGo = new GameObject("IconText", typeof(RectTransform));
        iconTextGo.transform.SetParent(_iconRoot, false);
        var itRect = (RectTransform)iconTextGo.transform;
        itRect.anchorMin = Vector2.zero;
        itRect.anchorMax = Vector2.one;
        itRect.offsetMin = Vector2.zero;
        itRect.offsetMax = Vector2.zero;
        _iconText = iconTextGo.AddComponent<TextMeshProUGUI>();
        _iconText.text             = "●";
        _iconText.color            = Color.white;
        _iconText.alignment        = TextAlignmentOptions.Center;
        _iconText.enableAutoSizing = true;
        _iconText.fontSizeMin      = 10;
        _iconText.fontSizeMax      = 400;
        _iconText.raycastTarget    = false;

        // ── Title ─────────────────────────────────────────────────
        var titleGo = new GameObject("Title", typeof(RectTransform));
        titleGo.transform.SetParent(_panel, false);
        var titleRect = (RectTransform)titleGo.transform;
        titleRect.anchorMin = new Vector2(0f, 1f);
        titleRect.anchorMax = new Vector2(1f, 1f);
        titleRect.pivot     = new Vector2(0f, 1f);
        titleRect.sizeDelta = new Vector2(panelSize.x - textX - padding, iconSize * 0.55f);
        titleRect.anchoredPosition = new Vector2(textX, -padding);
        _title = titleGo.AddComponent<TextMeshProUGUI>();
        _title.text             = scanningMessage;
        _title.color            = titleColor;
        _title.alignment        = TextAlignmentOptions.BottomLeft;
        _title.fontStyle        = FontStyles.Bold;
        _title.characterSpacing = 8f;
        _title.enableAutoSizing = true;
        _title.fontSizeMin      = 10;
        _title.fontSizeMax      = 200;
        _title.raycastTarget    = false;

        // ── Subtitle ──────────────────────────────────────────────
        var subGo = new GameObject("Subtitle", typeof(RectTransform));
        subGo.transform.SetParent(_panel, false);
        var subRect = (RectTransform)subGo.transform;
        subRect.anchorMin = new Vector2(0f, 1f);
        subRect.anchorMax = new Vector2(1f, 1f);
        subRect.pivot     = new Vector2(0f, 1f);
        subRect.sizeDelta = new Vector2(panelSize.x - textX - padding, iconSize * 0.38f);
        subRect.anchoredPosition = new Vector2(textX, -padding - iconSize * 0.58f);
        _subtitle = subGo.AddComponent<TextMeshProUGUI>();
        _subtitle.text             = string.Format(scanningSubtitle, 0, _totalHits);
        _subtitle.color            = subtitleColor;
        _subtitle.alignment        = TextAlignmentOptions.TopLeft;
        _subtitle.fontStyle        = FontStyles.Normal;
        _subtitle.characterSpacing = 2f;
        _subtitle.enableAutoSizing = true;
        _subtitle.fontSizeMin      = 8;
        _subtitle.fontSizeMax      = 100;
        _subtitle.raycastTarget    = false;

        // ── Progress track ────────────────────────────────────────
        float trackHeight = panelSize.y * 0.05f;
        float trackWidth  = panelSize.x - padding * 2f;

        var trackGo = new GameObject("ProgressTrack", typeof(RectTransform));
        trackGo.transform.SetParent(_panel, false);
        _progressTrack = (RectTransform)trackGo.transform;
        _progressTrack.anchorMin = new Vector2(0.5f, 0f);
        _progressTrack.anchorMax = new Vector2(0.5f, 0f);
        _progressTrack.pivot     = new Vector2(0.5f, 0f);
        _progressTrack.sizeDelta = new Vector2(trackWidth, trackHeight);
        _progressTrack.anchoredPosition = new Vector2(0f, progressY * 0.6f);
        var trackImg = trackGo.AddComponent<Image>();
        trackImg.sprite        = _pillSprite;
        trackImg.type          = Image.Type.Sliced;
        trackImg.pixelsPerUnitMultiplier = 1f;
        trackImg.color         = trackColor;
        trackImg.raycastTarget = false;

        // ── Progress fill ─────────────────────────────────────────
        var fillGo = new GameObject("ProgressFill", typeof(RectTransform));
        fillGo.transform.SetParent(_progressTrack, false);
        _progressFill = (RectTransform)fillGo.transform;
        _progressFill.anchorMin = new Vector2(0f, 0f);
        _progressFill.anchorMax = new Vector2(0f, 1f);
        _progressFill.pivot     = new Vector2(0f, 0.5f);
        _progressFill.sizeDelta = new Vector2(0f, 0f);
        _progressFill.anchoredPosition = new Vector2(0f, 0f);
        _progressFillImg = fillGo.AddComponent<Image>();
        _progressFillImg.sprite        = _pillSprite;
        _progressFillImg.type          = Image.Type.Sliced;
        _progressFillImg.pixelsPerUnitMultiplier = 1f;
        _progressFillImg.color         = scanningAccent;
        _progressFillImg.raycastTarget = false;
    }

    // ── Procedural sprite generation ──────────────────────────────

    private static void DestroySpriteAndTexture(Sprite s)
    {
        if (s == null) return;
        if (s.texture != null) Destroy(s.texture);
        Destroy(s);
    }

    /// <summary>
    /// Anti-aliased rounded rectangle with 9-slice borders so it scales
    /// cleanly to any size.
    /// </summary>
    private static Sprite CreateRoundedRectSprite(int size, int radius)
    {
        radius = Mathf.Clamp(radius, 1, size / 2 - 1);
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode   = TextureWrapMode.Clamp
        };

        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                byte alpha = 255;
                int dx = 0, dy = 0;
                bool inCorner = false;

                if (x < radius && y < radius)
                { dx = radius - x; dy = radius - y; inCorner = true; }
                else if (x >= size - radius && y < radius)
                { dx = x - (size - radius - 1); dy = radius - y; inCorner = true; }
                else if (x < radius && y >= size - radius)
                { dx = radius - x; dy = y - (size - radius - 1); inCorner = true; }
                else if (x >= size - radius && y >= size - radius)
                { dx = x - (size - radius - 1); dy = y - (size - radius - 1); inCorner = true; }

                if (inCorner)
                {
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    float a    = Mathf.Clamp01(radius + 0.5f - dist);
                    alpha = (byte)(a * 255);
                }

                pixels[y * size + x] = new Color32(255, 255, 255, alpha);
            }
        }

        tex.SetPixels32(pixels);
        tex.Apply();

        Vector4 border = new Vector4(radius, radius, radius, radius);
        return Sprite.Create(
            tex,
            new Rect(0, 0, size, size),
            new Vector2(0.5f, 0.5f),
            100f, 0,
            SpriteMeshType.FullRect,
            border);
    }

    /// <summary>Anti-aliased filled circle.</summary>
    private static Sprite CreateCircleSprite(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode   = TextureWrapMode.Clamp
        };

        var pixels = new Color32[size * size];
        float cx     = size * 0.5f;
        float cy     = size * 0.5f;
        float radius = size * 0.5f - 1f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx   = x - cx + 0.5f;
                float dy   = y - cy + 0.5f;
                float dist = Mathf.Sqrt(dx * dx + dy * dy);
                float a    = Mathf.Clamp01(radius + 0.5f - dist);
                pixels[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
        }

        tex.SetPixels32(pixels);
        tex.Apply();

        return Sprite.Create(
            tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
    }
}
