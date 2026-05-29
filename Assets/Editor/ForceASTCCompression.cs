using UnityEditor;
using UnityEngine;

public static class ForceASTCCompression
{
    [MenuItem("Tools/IMMERSA/Apply ASTC to All Textures")]
    public static void ApplyAll()
    {
        string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { "Assets" });
        int changed = 0;
        int total = guids.Length;

        try
        {
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);

                EditorUtility.DisplayProgressBar(
                    "Applying ASTC",
                    $"({i + 1}/{total}) {System.IO.Path.GetFileName(path)}",
                    (float)i / total);

                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer == null) continue;

                var settings = importer.GetPlatformTextureSettings("Android");

                // Normal maps get ASTC 6x6, everything else 8x8
                var fmt = importer.textureType == TextureImporterType.NormalMap
                    ? TextureImporterFormat.ASTC_6x6
                    : TextureImporterFormat.ASTC_8x8;

                if (settings.overridden && settings.format == fmt) continue;

                settings.overridden     = true;
                settings.format         = fmt;
                settings.maxTextureSize = importer.maxTextureSize;

                importer.SetPlatformTextureSettings(settings);
                importer.SaveAndReimport();
                changed++;
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        Debug.Log($"[ASTC] Done — {changed}/{total} textures updated.");
        EditorUtility.DisplayDialog("ASTC Applied",
            $"Updated {changed} of {total} textures to ASTC compression.\nRebuild the app to see GPU memory drop.", "OK");
    }
}
