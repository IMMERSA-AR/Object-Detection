using UnityEngine;

[ExecuteInEditMode]
[RequireComponent(typeof(Camera))]
public class VintageCameraEffect : MonoBehaviour
{
    [Header("Vintage Settings")]
    [Tooltip("The material containing the Vintage Shader")]
    public Material vintageMaterial;

    [Range(0f, 1f)]
    public float sepiaIntensity = 1.0f;

    [Range(0f, 1f)]
    public float vignetteIntensity = 1.5f;

    void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        if (vintageMaterial != null)
        {
            // Pass variables to the shader
            vintageMaterial.SetFloat("_SepiaIntensity", sepiaIntensity);
            vintageMaterial.SetFloat("_VignetteIntensity", vignetteIntensity);

            // Apply the effect
            Graphics.Blit(source, destination, vintageMaterial);
        }
        else
        {
            // If no material is assigned, just render normally
            Graphics.Blit(source, destination);
        }
    }
}