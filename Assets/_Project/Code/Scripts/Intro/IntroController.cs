using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

/// <summary>
/// Drives the Archive Room intro sequence:
///   1. Screen starts black (FadeImage alpha = 1)
///   2. Fade to passthrough
///   3. Archive table materialises
///   4. Documents appear one by one
///   5. Voiceover plays
///   6. White flash → load MenuScene
///
/// Assign all references in the Inspector.
/// </summary>
public class IntroController : MonoBehaviour
{
    // ── Scene to load after intro ─────────────────────────────────
    [Header("Scene Transition")]
    [Tooltip("Exact name of the Menu scene to load after the intro.")]
    public string menuSceneName = "MenuScene";

    // ── Fade overlay ──────────────────────────────────────────────
    [Header("Fade Canvas")]
    [Tooltip("The full-screen Image used for fade in / fade out.")]
    public Image fadeImage;

    // ── Archive table & documents ─────────────────────────────────
    [Header("Archive Table")]
    [Tooltip("Root GameObject of the table (and all its children).")]
    public GameObject archiveTable;

    [Tooltip("Document quads in the order you want them to appear.")]
    public List<GameObject> documents = new List<GameObject>();

    // ── Lighting ──────────────────────────────────────────────────
    [Header("Warm Light")]
    [Tooltip("The warm point light above the table.")]
    public Light warmLight;

    [Tooltip("Target intensity for the warm light (set in Inspector).")]
    public float warmLightTargetIntensity = 1.5f;

    // ── Audio ─────────────────────────────────────────────────────
    [Header("Audio")]
    [Tooltip("AudioSource that will play the voiceover clip.")]
    public AudioSource voiceoverSource;

    [Tooltip("Optional ambient background sound (old Cairo street, etc.).")]
    public AudioSource ambientSource;

    // ── Timing ────────────────────────────────────────────────────
    [Header("Timing (seconds)")]
    public float initialBlackDuration    = 0.5f;   // hold black before fading in
    public float fadeInDuration          = 1.5f;   // black → passthrough
    public float tableAppearDuration     = 1.0f;   // table fade-in
    public float docStaggerDelay        = 0.4f;   // gap between each document
    public float docFadeDuration        = 0.5f;   // each document fade-in
    public float holdAfterVoiceover     = 1.0f;   // pause after VO ends
    public float fadeOutDuration        = 1.0f;   // passthrough → white flash

    // ── Private ───────────────────────────────────────────────────
    // We store per-document renderers and their original materials
    // so we can fade their alpha independently.
    private List<Renderer> _docRenderers = new List<Renderer>();
    private List<Material> _docMaterials = new List<Material>();

    private Renderer _tableRenderer;
    private Material _tableMaterial;

    // ─────────────────────────────────────────────────────────────

    private void Awake()
    {
        // Start screen fully black
        SetFadeAlpha(1f, Color.black);

        // Hide table and all documents immediately
        if (archiveTable != null)
            archiveTable.SetActive(false);

        if (warmLight != null)
            warmLight.intensity = 0f;

        // Cache document renderers and prepare transparent materials
        foreach (var doc in documents)
        {
            if (doc == null) continue;
            doc.SetActive(false);

            Renderer r = doc.GetComponent<Renderer>();
            if (r == null) continue;

            // Duplicate the material so we can change alpha independently
            Material mat = new Material(r.sharedMaterial);
            SetMaterialTransparent(mat);
            r.material = mat;

            _docRenderers.Add(r);
            _docMaterials.Add(mat);
        }

        // Cache table renderer
        if (archiveTable != null)
        {
            _tableRenderer = archiveTable.GetComponent<Renderer>();
            if (_tableRenderer != null)
            {
                _tableMaterial = new Material(_tableRenderer.sharedMaterial);
                SetMaterialTransparent(_tableMaterial);
                _tableRenderer.material = _tableMaterial;
            }
        }
    }

    private void Start()
    {
        StartCoroutine(RunIntroSequence());
    }

    // ── Main Sequence ─────────────────────────────────────────────

    private IEnumerator RunIntroSequence()
    {
        // 1. Hold black
        yield return new WaitForSeconds(initialBlackDuration);

        // 2. Start ambient sound
        if (ambientSource != null)
        {
            ambientSource.loop   = true;
            ambientSource.volume = 0f;
            ambientSource.Play();
            StartCoroutine(FadeAudioIn(ambientSource, 0.4f, fadeInDuration));
        }

        // 3. Fade from black to passthrough
        yield return StartCoroutine(FadeTo(0f, Color.black, fadeInDuration));

        yield return new WaitForSeconds(0.3f);

        // 4. Materialise the archive table
        if (archiveTable != null)
        {
            archiveTable.SetActive(true);
            if (_tableMaterial != null)
                yield return StartCoroutine(FadeMaterialAlpha(_tableMaterial, 0f, 1f, tableAppearDuration));
        }

        // 5. Fade the warm light in
        if (warmLight != null)
            yield return StartCoroutine(FadeLightIntensity(warmLight, 0f, warmLightTargetIntensity, 0.5f));

        // 6. Documents appear one by one
        for (int i = 0; i < _docRenderers.Count; i++)
        {
            if (documents[i] != null)
                documents[i].SetActive(true);

            yield return StartCoroutine(FadeMaterialAlpha(_docMaterials[i], 0f, 1f, docFadeDuration));
            yield return new WaitForSeconds(docStaggerDelay);
        }

        yield return new WaitForSeconds(0.5f);

        // 7. Play voiceover
        if (voiceoverSource != null && voiceoverSource.clip != null)
        {
            voiceoverSource.Play();
            yield return new WaitForSeconds(voiceoverSource.clip.length);
        }
        else
        {
            // No clip assigned — wait a fixed duration so the intro still works
            Debug.LogWarning("[Intro] No voiceover clip assigned — waiting 5s placeholder.");
            yield return new WaitForSeconds(5f);
        }

        yield return new WaitForSeconds(holdAfterVoiceover);

        // 8. Fade ambient out
        if (ambientSource != null)
            StartCoroutine(FadeAudioOut(ambientSource, fadeOutDuration));

        // 9. White flash → load menu
        yield return StartCoroutine(FadeTo(1f, Color.white, fadeOutDuration));

        SceneManager.LoadScene(menuSceneName);
    }

    // ── Fade Helpers ──────────────────────────────────────────────

    /// Fades the full-screen overlay to targetAlpha over duration seconds.
    private IEnumerator FadeTo(float targetAlpha, Color color, float duration)
    {
        if (fadeImage == null) yield break;

        Color startColor = fadeImage.color;
        color.a = startColor.a;           // keep current alpha as start
        float startAlpha = startColor.a;

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float a = Mathf.Lerp(startAlpha, targetAlpha, elapsed / duration);
            color.a = a;
            fadeImage.color = color;
            yield return null;
        }

        color.a = targetAlpha;
        fadeImage.color = color;
    }

    /// Sets the overlay to an exact alpha + colour immediately (no animation).
    private void SetFadeAlpha(float alpha, Color color)
    {
        if (fadeImage == null) return;
        color.a = alpha;
        fadeImage.color = color;
    }

    /// Fades a material's alpha from startA to endA over duration seconds.
    private IEnumerator FadeMaterialAlpha(Material mat, float startA, float endA, float duration)
    {
        if (mat == null) yield break;

        Color c = mat.color;
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            c.a = Mathf.Lerp(startA, endA, elapsed / duration);
            mat.color = c;
            yield return null;
        }
        c.a = endA;
        mat.color = c;
    }

    /// Fades a Light's intensity from startI to endI over duration seconds.
    private IEnumerator FadeLightIntensity(Light light, float startI, float endI, float duration)
    {
        if (light == null) yield break;

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            light.intensity = Mathf.Lerp(startI, endI, elapsed / duration);
            yield return null;
        }
        light.intensity = endI;
    }

    /// Fades an AudioSource volume from 0 to targetVolume.
    private IEnumerator FadeAudioIn(AudioSource source, float targetVolume, float duration)
    {
        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            source.volume = Mathf.Lerp(0f, targetVolume, elapsed / duration);
            yield return null;
        }
        source.volume = targetVolume;
    }

    /// Fades an AudioSource volume to 0 then stops it.
    private IEnumerator FadeAudioOut(AudioSource source, float duration)
    {
        float startVol = source.volume;
        float elapsed  = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            source.volume = Mathf.Lerp(startVol, 0f, elapsed / duration);
            yield return null;
        }
        source.Stop();
    }

    // ── Material Helper ───────────────────────────────────────────

    /// Switches a URP Lit material to Transparent surface type so alpha fades work.
    /// If you are using the Built-in pipeline, switch "Rendering Mode" to Transparent instead.
    private void SetMaterialTransparent(Material mat)
    {
        if (mat == null) return;

        // URP Lit shader
        if (mat.shader.name.Contains("Universal Render Pipeline") ||
            mat.shader.name.Contains("Lit"))
        {
            mat.SetFloat("_Surface", 1f);          // 0 = Opaque, 1 = Transparent
            mat.SetFloat("_Blend", 0f);             // Alpha blend
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }
        // Built-in Standard shader
        else if (mat.shader.name == "Standard")
        {
            mat.SetFloat("_Mode", 3f);              // Transparent mode
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }

        // Set initial alpha to 0
        Color c = mat.color;
        c.a = 0f;
        mat.color = c;
    }
}
