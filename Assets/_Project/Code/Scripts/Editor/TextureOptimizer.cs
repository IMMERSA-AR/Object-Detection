#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// One-click VRAM/bandwidth optimizer for the heavy CC4 character textures.
///
/// The Obelisk scene's ~10 Reallusion characters ship 4K maps; uncompressed/oversized
/// in VRAM that's what removes the D3D11 device (device-removed crash) and stalls loads.
/// This caps their size, forces compression, and turns on mip STREAMING (so only the mip
/// levels actually on screen are resident — the single biggest VRAM saver).
///
/// Run from the menu, then commit. Re-running skips already-optimised textures.
/// Tools ▸ Optimization ▸ Optimize Character Textures
/// </summary>
public static class TextureOptimizer
{
    // Folders whose textures get capped + streamed.
    private static readonly string[] TargetFolders =
    {
        "Assets/_Project/Art/Models/Characters",
        "Assets/_Project/Props",
    };

    private const int DefaultMax = 2048;   // Editor / Standalone (PC)
    private const int AndroidMax = 1024;   // Meta Quest

    [MenuItem("Tools/Optimization/Optimize Character Textures (cap + streaming)")]
    public static void Optimize()
    {
        if (!EditorUtility.DisplayDialog(
                "Optimize Character Textures",
                $"Cap textures in:\n  • {string.Join("\n  • ", TargetFolders)}\n\n" +
                $"PC max {DefaultMax}, Quest max {AndroidMax}, compressed, mip-streaming ON.\n\n" +
                "This reimports the affected textures (can take a few minutes). Continue?",
                "Optimize", "Cancel"))
            return;

        string[] guids = AssetDatabase.FindAssets("t:Texture2D", TargetFolders);
        int changed = 0;

        try
        {
            AssetDatabase.StartAssetEditing();
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                if (EditorUtility.DisplayCancelableProgressBar(
                        "Optimizing textures", path, (float)i / Mathf.Max(1, guids.Length)))
                    break;

                var imp = AssetImporter.GetAtPath(path) as TextureImporter;
                if (imp == null) continue;

                bool dirty = false;

                // Mipmaps + streaming — the big VRAM win (only resident mips load).
                if (!imp.mipmapEnabled)    { imp.mipmapEnabled = true;    dirty = true; }
                if (!imp.streamingMipmaps) { imp.streamingMipmaps = true; dirty = true; }

                // Default (PC/editor) platform: cap size + force compression.
                var def = imp.GetDefaultPlatformTextureSettings();
                if (def.maxTextureSize > DefaultMax ||
                    def.textureCompression == TextureImporterCompression.Uncompressed)
                {
                    def.maxTextureSize     = Mathf.Min(def.maxTextureSize, DefaultMax);
                    def.textureCompression = TextureImporterCompression.Compressed;
                    imp.SetPlatformTextureSettings(def);
                    dirty = true;
                }

                // Android (Quest) override: cap harder + compress.
                var and = imp.GetPlatformTextureSettings("Android");
                if (!and.overridden ||
                    and.maxTextureSize != AndroidMax ||
                    and.textureCompression != TextureImporterCompression.Compressed)
                {
                    and.overridden         = true;
                    and.maxTextureSize     = AndroidMax;
                    and.textureCompression = TextureImporterCompression.Compressed;
                    and.format             = TextureImporterFormat.Automatic;
                    imp.SetPlatformTextureSettings(and);
                    dirty = true;
                }

                if (dirty)
                {
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
                    changed++;
                }
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            AssetDatabase.StopAssetEditing();
            AssetDatabase.Refresh();
        }

        Debug.Log($"[TextureOptimizer] Scanned {guids.Length} textures, updated {changed}.  " +
                  $"PC cap {DefaultMax} (compressed), Quest cap {AndroidMax}, mip-streaming ON.  " +
                  "NOW enable Project Settings ▸ Quality ▸ Texture Streaming for every level.");
    }
}
#endif
