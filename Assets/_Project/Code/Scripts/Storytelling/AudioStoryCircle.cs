using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;
using TMPro;

public class AudioStoryCircle : MonoBehaviour
{
    [System.Serializable]
    public class StoryCue
    {

        public float time;
        public Sprite image;
        public VideoClip videoClip;
        public float videoSpeed = 1f;
        public float fadeIn = 0.6f;
        public string year;
    }

    [Header("Audio")]
    [SerializeField] private AudioSource narration;
    [SerializeField] private AudioClip storyClip;
    [SerializeField] private bool autoPlay = false;
    [SerializeField] private float startupDelay = 0.5f;

    [Header("Story")]
    [SerializeField] private StoryCue[] cues;

    [Header("Circle Layout")]
    [SerializeField] private float radius = 2.0f;
    [SerializeField] private float heightOffset = 0f;
    [SerializeField] private float startAngle = 0f;
    [SerializeField] private float arcSpan = 360f;
    [SerializeField] private bool centerForwardBetweenFirstTwo = false;

    [Header("Frame Appearance")]
    [SerializeField] private float frameHeight = 0.8f;
    [SerializeField] private Vector2 aspectClamp = new Vector2(0.3f, 3.2f);
    [SerializeField] private float cornerRadius = 26f;
    [SerializeField] private float borderThickness = 0.03f;
    [SerializeField] private Color borderColor = Color.white;
    [SerializeField] private bool faceUser = true;

    [Header("Year Caption")]
    [Tooltip("TMP font asset for the year caption. Assign 'Cinzel-Regular SDF' (or Bold/" +
             "Medium) from Assets/_Project/Art/UI/Fonts/Cinzel/Font Assets. " +
             "Leave empty to use TMP's default font.")]
    [SerializeField] private TMP_FontAsset captionFont;
    [Tooltip("Font size of the year caption (world-canvas units; a frame of height " +
             "0.8 m ≈ 80 units, so ~16 reads well).")]
    [SerializeField] private float captionFontSize = 16f;
    [Tooltip("Colour of the year caption text.")]
    [SerializeField] private Color captionColor = Color.white;
    [Tooltip("Gap between the bottom edge of the frame and the caption (metres). " +
             "Keep this above the Glow Size so the text clears the frame's glow halo.")]
    [SerializeField] private float captionGap = 0.14f;

    [Header("Glow Halo")]
    [Tooltip("Soft blurred halo behind each frame so the pictures look spotlit " +
             "against the dimmed room.")]
    [SerializeField] private bool glow = true;
    [Tooltip("How far the glow extends past the frame edges (metres).")]
    [SerializeField] private float glowSize = 0.12f;
    [Tooltip("Glow colour & strength. A soft warm white reads as a spotlight.")]
    [SerializeField] private Color glowColor = new Color(1f, 0.97f, 0.9f, 0.5f);

    [Header("Background Dim (Spotlight)")]
    [Tooltip("Darken EVERYTHING in view (room, characters, time machine, passthrough) " +
             "while the story plays, so the lit pictures are the focus. The images " +
             "themselves stay bright.")]
    [SerializeField] private bool dimBackground = true;
    [Tooltip("Dim tint & strength. Alpha = how dark it gets (0.85 ≈ moonlit room). " +
             "A dark blue tint reads as moonlight.")]
    [SerializeField] private Color dimColor = new Color(0.02f, 0.04f, 0.10f, 0.85f);
    [Tooltip("Fade duration for the dim in/out (seconds).")]
    [SerializeField] private float dimFade = 1.2f;

    // ── runtime ──
    private const float UnitsPerMetre = 100f;   // 1 world metre = 100 canvas units

    private Transform _cam;
    private Vector3 _circleCenter;
    private List<StoryCue> _ordered;            // cues sorted by time
    private readonly List<GameObject> _frames = new List<GameObject>();
    private readonly List<CanvasGroup> _groups = new List<CanvasGroup>();
    // Aligned 1:1 with _frames. Null entry = static image frame; non-null = video frame.
    private readonly List<VideoPlayer> _videoPlayers = new List<VideoPlayer>();
    private GameObject _dimOverlay;
    private CanvasGroup _dimGroup;
    private bool _started;
    private bool _prewarmed;

    /// <summary>Fired once when the narration finishes and all cues have been shown.</summary>
    public System.Action OnStoryComplete;

    /// <summary>Hides all story picture frames immediately.</summary>
    public void HideFrames()
    {
        foreach (var f in _frames)
            if (f != null) f.SetActive(false);
    }

    private void Start()
    {
        // Build everything up-front (texture, canvases, images) while the scene is
        // already loaded, so passing through the portal causes no allocation hitch.
        Prewarm();
        if (autoPlay) Play();
    }

    private void Prewarm()
    {
        if (_prewarmed) return;
        _prewarmed = true;

        _ordered = new List<StoryCue>(cues);
        _ordered.Sort((a, b) => a.time.CompareTo(b.time));

        for (int i = 0; i < _ordered.Count; i++)
            BuildFrame(_ordered[i], i);

        // Prime the audio system so first playback doesn't stall.
        if (storyClip != null) storyClip.LoadAudioData();
    }

    /// <summary>Starts the story. Safe to call more than once — only runs the first time.</summary>
    public void Play()
    {
        if (_started) return;
        _started = true;
        Prewarm();                  // no-op if already done
        StartCoroutine(Run());
    }

    private IEnumerator Run()
    {
        // Anchor the circle on the user's current position/facing.
        if (Camera.main == null)
        {
            Debug.LogError("[AudioStoryCircle] Camera.main is null — cannot place frames.");
            yield break;
        }
        if (narration == null)
        {
            Debug.LogError("[AudioStoryCircle] No AudioSource assigned.");
            yield break;
        }

        // Small wait only if tracking may not be ready yet (e.g. autoPlay at boot).
        if (startupDelay > 0f) yield return new WaitForSeconds(startupDelay);

        _cam = Camera.main.transform;
        _circleCenter = _cam.position + Vector3.up * heightOffset;

        // Position the pre-built frames around the user (cheap: just transforms).
        for (int i = 0; i < _frames.Count; i++)
            PositionFrame(_frames[i], i, _frames.Count);

        // Spotlight dim: create (transparent) and fade the surround in.
        if (dimBackground)
        {
            CreateDimOverlay();
            StartCoroutine(FadeDim(dimColor.a, dimFade));
        }

        // Start the narration + reveal pictures on their cue times.
        narration.clip = storyClip != null ? storyClip : narration.clip;
        narration.Play();
        Debug.Log($"[AudioStoryCircle] Story started ({_ordered.Count} pictures).");

        // Safety timeout so the experience can NEVER get stuck on the black dim overlay
        // if the narration AudioSource loops, has no clip, or a cue time is set past the
        // clip's end. After this it force-completes (reveals all, lifts the dim, spawns).
        float storyStart   = Time.time;
        float storyTimeout = (narration.clip != null ? narration.clip.length : 30f) + 3f;

        int idx = 0;
        while (idx < _ordered.Count)
        {
            bool timedOut = (Time.time - storyStart) >= storyTimeout;
            // Drive off the audio clock, not Time.time, so visuals stay in sync.
            if (narration.time >= _ordered[idx].time || !narration.isPlaying || timedOut)
            {
                if (idx < _frames.Count) _frames[idx].SetActive(true);
                if (idx < _groups.Count) StartCoroutine(FadeIn(_groups[idx], _ordered[idx].fadeIn));

                // If this frame is a video, start playback (decoder already prepared).
                if (idx < _videoPlayers.Count && _videoPlayers[idx] != null)
                    _videoPlayers[idx].Play();

                idx++;
            }
            yield return null;
        }

        // Wait for narration to end — but never past the timeout (covers looping / stalled audio).
        while (narration.isPlaying && (Time.time - storyStart) < storyTimeout)
            yield return null;

        if (dimBackground && _dimGroup != null)
            yield return FadeDim(0f, dimFade);

        // Notify listeners (e.g. TimePortalLogic) that the story is done.
        OnStoryComplete?.Invoke();
    }

    // ──────────────────────────────────────────────────────────
    // Background dim (full-view spotlight overlay — UI Image based)
    // ──────────────────────────────────────────────────────────

    private void CreateDimOverlay()
    {
        if (_dimOverlay != null || _cam == null) return;

        // A dark UI Image on a world-space canvas parented to the camera. This uses
        // the SAME UI rendering path as the story frames (known to render in URP), so
        // it reliably darkens the whole view. sortingOrder is low so the bright story
        // frames (higher order) always draw on top and stay lit.
        _dimOverlay = new GameObject("StoryDimOverlay");
        _dimOverlay.transform.SetParent(_cam, false);

        var canvas = _dimOverlay.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = -100;            // behind the story frames

        var rt = _dimOverlay.GetComponent<RectTransform>();
        // Big sheet at 0.5 m in front of the eyes — oversized to cover the whole FOV.
        const float dist = 0.5f;
        _dimOverlay.transform.localPosition = new Vector3(0f, 0f, dist);
        _dimOverlay.transform.localRotation = Quaternion.identity;
        rt.sizeDelta = new Vector2(600f, 600f);          // canvas units
        _dimOverlay.transform.localScale = Vector3.one / UnitsPerMetre;  // → 6 m sheet

        _dimGroup = _dimOverlay.AddComponent<CanvasGroup>();
        _dimGroup.alpha = 0f;
        _dimGroup.interactable = false;
        _dimGroup.blocksRaycasts = false;       // never intercept UI/ray input

        // The dark sheet itself (alpha comes from the CanvasGroup fade).
        var img = NewImage("DimSheet", rt, null, new Color(dimColor.r, dimColor.g, dimColor.b, 1f));
        Stretch(img.rectTransform);
        img.raycastTarget = false;
    }

    private IEnumerator FadeDim(float targetAlpha, float duration)
    {
        if (_dimGroup == null) yield break;

        float startA = _dimGroup.alpha;
        float t = 0f;
        while (t < duration)
        {
            t += Time.deltaTime;
            _dimGroup.alpha = Mathf.Lerp(startA, targetAlpha, Mathf.Clamp01(t / duration));
            yield return null;
        }
        _dimGroup.alpha = targetAlpha;
    }

    // ──────────────────────────────────────────────────────────
    // Frame construction (done up-front in Prewarm)
    // ──────────────────────────────────────────────────────────

    private void BuildFrame(StoryCue cue, int index)
    {
        // ── Derive this frame's size from the picture's / video's real aspect ratio ──
        float aspect = 1f;
        if (cue.videoClip != null && cue.videoClip.height > 0)
            aspect = (float)cue.videoClip.width / cue.videoClip.height;
        else if (cue.image != null && cue.image.rect.height > 0f)
            aspect = cue.image.rect.width / cue.image.rect.height;
        aspect = Mathf.Clamp(aspect, aspectClamp.x, aspectClamp.y);

        float h = frameHeight;
        float w = frameHeight * aspect;
        Vector2 sizeMetres = new Vector2(w, h);

        // A rounded sprite generated at THIS frame's aspect → corners stay uniform
        // and the matte border is the same thickness on every side.
        int texH = 256;
        int texW = Mathf.Clamp(Mathf.RoundToInt(256 * aspect), 16, 1024);
        Sprite rounded = BuildRoundedSprite(texW, texH, cornerRadius);

        var frame = new GameObject($"StoryFrame_{index}");
        frame.transform.SetParent(transform, false);

        var canvas = frame.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;

        var rt = frame.GetComponent<RectTransform>();
        rt.sizeDelta = sizeMetres * UnitsPerMetre;
        frame.transform.localScale = Vector3.one / UnitsPerMetre;

        var group = frame.AddComponent<CanvasGroup>();
        group.alpha = 0f;

        // ── Soft glow halo (rendered first = behind everything) ──
        if (glow)
        {
            Sprite glowSprite = BuildBlurredSprite(texW, texH, cornerRadius);
            var halo = NewImage("Glow", rt, glowSprite, glowColor);
            Stretch(halo.rectTransform);
            float g = glowSize * UnitsPerMetre;
            halo.rectTransform.offsetMin = new Vector2(-g, -g);
            halo.rectTransform.offsetMax = new Vector2(g, g);
        }

        // ── Border / matte (rounded box behind the picture) ──
        var border = NewImage("Border", rt, rounded, borderColor);
        Stretch(border.rectTransform);

        // ── Masked picture (rounded, inset by a uniform border thickness) ──
        var maskGO = new GameObject("PictureMask");
        var maskRT = maskGO.AddComponent<RectTransform>();
        maskRT.SetParent(rt, false);
        Stretch(maskRT);
        float inset = borderThickness * UnitsPerMetre;
        maskRT.offsetMin = new Vector2(inset, inset);
        maskRT.offsetMax = new Vector2(-inset, -inset);

        var maskImg = maskGO.AddComponent<Image>();
        maskImg.sprite = rounded;
        maskImg.type = Image.Type.Simple;
        var mask = maskGO.AddComponent<Mask>();
        mask.showMaskGraphic = false;

        // Either a video frame (RawImage fed by a VideoPlayer) or a static picture.
        VideoPlayer vp = null;
        if (cue.videoClip != null)
        {
            vp = SetupVideoFrame(cue.videoClip, maskRT);
            if (vp != null) vp.playbackSpeed = cue.videoSpeed;
        }
        else
        {
            // Frame matches the image aspect, so the picture fills the matte exactly →
            // a clean, even border on all sides with no empty gaps.
            var picture = NewImage("Picture", maskRT, cue.image, Color.white);
            picture.preserveAspect = true;
            Stretch(picture.rectTransform);
        }

        frame.SetActive(false);                 // hidden until its cue fires

        // Register the frame BEFORE building the optional caption, so a caption failure
        // can never desync _frames from _ordered (which would crash the reveal loop and
        // leave the black dim overlay stuck up).
        _frames.Add(frame);
        _groups.Add(group);
        _videoPlayers.Add(vp);

        // ── Year caption below the picture ──────────────────────────────
        // Parented to the frame so it hangs below the matte, billboards with the frame
        // and fades in via the same CanvasGroup.
        if (!string.IsNullOrEmpty(cue.year))
        {
            try { BuildCaption(cue.year, rt, sizeMetres.x); }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[AudioStoryCircle] Year caption failed for '{cue.year}': {e.Message}");
            }
        }
    }

    /// <summary>
    /// Builds a RawImage fed by a VideoPlayer rendering into a RenderTexture, and
    /// pre-prepares the decoder so playback starts instantly with no hitch.
    /// </summary>
    private VideoPlayer SetupVideoFrame(VideoClip clip, RectTransform parent)
    {
        int vw = Mathf.Clamp((int)clip.width, 16, 1920);
        int vh = Mathf.Clamp((int)clip.height, 16, 1080);

        var renderTex = new RenderTexture(vw, vh, 0);
        renderTex.Create();

        var go = new GameObject("Video");
        var rrt = go.AddComponent<RectTransform>();
        rrt.SetParent(parent, false);
        Stretch(rrt);

        var raw = go.AddComponent<RawImage>();
        raw.texture = renderTex;

        var vp = go.AddComponent<VideoPlayer>();
        vp.source            = VideoSource.VideoClip;
        vp.clip              = clip;
        vp.renderMode        = VideoRenderMode.RenderTexture;
        vp.targetTexture     = renderTex;
        vp.playOnAwake       = false;
        vp.isLooping         = false;
        vp.audioOutputMode   = VideoAudioOutputMode.None;  // narration handles audio
        vp.skipOnDrop        = true;

        // Warm up the hardware decoder now (during prewarm/scanning) so Play() is instant.
        vp.Prepare();

        return vp;
    }

    /// <summary>Places a pre-built frame around the user (transform-only, no allocation).</summary>
    private void PositionFrame(GameObject frame, int index, int total)
    {
        float denom = (arcSpan >= 360f) ? total : Mathf.Max(1, total - 1);

        // Optionally rotate the whole ring anticlockwise by half the per-frame spacing,
        // so the user's forward bisects frames 0 and 1 (both visible at the start).
        float spacing = (total > 1) ? (arcSpan / denom) : 0f;
        float bias    = centerForwardBetweenFirstTwo ? -spacing * 0.5f : 0f;

        float angleDeg = startAngle + bias + (total <= 1 ? 0f : (index / denom) * arcSpan);

        Vector3 fwd = _cam.forward; fwd.y = 0f; fwd.Normalize();
        Vector3 dir = Quaternion.AngleAxis(angleDeg, Vector3.up) * fwd;
        Vector3 pos = _circleCenter + dir * radius;

        frame.transform.position = pos;
        if (faceUser)
            frame.transform.rotation = Quaternion.LookRotation(pos - _circleCenter, Vector3.up);
    }

    /// <summary>
    /// Builds a TextMeshPro year caption hanging just below the frame.
    /// </summary>
    private void BuildCaption(string text, RectTransform frameRT, float frameWidthMetres)
    {
        var capGO = new GameObject("YearCaption");
        var capRT = capGO.AddComponent<RectTransform>();
        capRT.SetParent(frameRT, false);

        // Anchor to the bottom-centre of the frame; pivot at the TOP so the
        // label hangs below the frame edge by captionGap.
        capRT.anchorMin = new Vector2(0.5f, 0f);
        capRT.anchorMax = new Vector2(0.5f, 0f);
        capRT.pivot     = new Vector2(0.5f, 1f);
        capRT.sizeDelta = new Vector2(frameWidthMetres * UnitsPerMetre, captionFontSize * 1.6f);

        // Hang the caption below the frame. Always clear the glow halo (which extends
        // glowSize past the frame edge) plus padding, so it never overlaps regardless
        // of the captionGap value set in the Inspector.
        float gap = captionGap;
        if (glow) gap = Mathf.Max(gap, glowSize + 0.06f);
        capRT.anchoredPosition = new Vector2(0f, -gap * UnitsPerMetre);

        var txt = capGO.AddComponent<TextMeshProUGUI>();
        if (captionFont != null) txt.font = captionFont;   // Cinzel
        txt.text          = text;
        txt.fontSize      = captionFontSize;
        txt.color         = captionColor;
        txt.alignment     = TextAlignmentOptions.Top;
        txt.raycastTarget = false;
    }

    private static Image NewImage(string name, RectTransform parent, Sprite sprite, Color color)
    {
        var go = new GameObject(name);
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        var img = go.AddComponent<Image>();
        img.sprite = sprite;
        img.color = color;
        if (sprite != null && sprite.border != Vector4.zero)
            img.type = Image.Type.Sliced;
        return img;
    }

    private static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    private IEnumerator FadeIn(CanvasGroup group, float duration)
    {
        float t = 0f;
        while (t < duration)
        {
            t += Time.deltaTime;
            group.alpha = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t / duration));
            yield return null;
        }
        group.alpha = 1f;
    }

    // ──────────────────────────────────────────────────────────
    // Procedural rounded-rectangle sprite (rendered Simple/stretched)
    // ──────────────────────────────────────────────────────────

    private static Sprite BuildRoundedSprite(int w, int h, float radius)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[w * h];
        radius = Mathf.Clamp(radius, 0, Mathf.Min(w, h) * 0.5f);

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float a = RoundedRectAlpha(x + 0.5f, y + 0.5f, w, h, radius);
                px[y * w + x] = new Color32(255, 255, 255, (byte)(a * 255f));
            }
        }
        tex.SetPixels32(px);
        tex.Apply();

        // Plain sprite (no 9-slice border) -> drawn as Image.Type.Simple, stretched
        // to the frame. The texture is generated at the frame's aspect ratio, so the
        // corners stay rounded instead of collapsing into a circle.
        return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f);
    }

    private static float RoundedRectAlpha(float x, float y, int w, int h, float r)
    {
        // Distance into the corner region on each axis (0 when not in a corner).
        float dx = Mathf.Max(r - x, x - (w - r), 0f);
        float dy = Mathf.Max(r - y, y - (h - r), 0f);

        // Not in a corner band -> fully inside.
        if (dx <= 0f || dy <= 0f) return 1f;

        // Inside a corner: alpha by distance from the corner's arc centre,
        // with a 1px anti-aliased edge.
        float dist = Mathf.Sqrt(dx * dx + dy * dy);
        return Mathf.Clamp01(r - dist + 0.5f);
    }

    // A soft, blurred rounded rectangle — used as the glow halo behind each frame.
    private static Sprite BuildBlurredSprite(int w, int h, float radius)
    {
        radius = Mathf.Clamp(radius, 0, Mathf.Min(w, h) * 0.5f);

        // 1) Build the sharp rounded-rect alpha mask.
        float[] a = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                a[y * w + x] = RoundedRectAlpha(x + 0.5f, y + 0.5f, w, h, radius);

        // 2) Box-blur the alpha a few passes for a soft falloff.
        int blur = Mathf.Max(4, Mathf.RoundToInt(Mathf.Min(w, h) * 0.12f));
        a = BoxBlur(a, w, h, blur);
        a = BoxBlur(a, w, h, blur);

        // 3) Pack into a white sprite (colour comes from the Image tint).
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[w * h];
        for (int i = 0; i < px.Length; i++)
            px[i] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(a[i]) * 255f));
        tex.SetPixels32(px);
        tex.Apply();

        return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f), 100f);
    }

    // Simple separable box blur on a single-channel buffer.
    private static float[] BoxBlur(float[] src, int w, int h, int r)
    {
        float[] tmp = new float[src.Length];
        float inv = 1f / (2 * r + 1);

        // Horizontal
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float sum = 0f;
                for (int k = -r; k <= r; k++)
                    sum += src[y * w + Mathf.Clamp(x + k, 0, w - 1)];
                tmp[y * w + x] = sum * inv;
            }
        }

        // Vertical
        float[] dst = new float[src.Length];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float sum = 0f;
                for (int k = -r; k <= r; k++)
                    sum += tmp[Mathf.Clamp(y + k, 0, h - 1) * w + x];
                dst[y * w + x] = sum * inv;
            }
        }
        return dst;
    }
}
