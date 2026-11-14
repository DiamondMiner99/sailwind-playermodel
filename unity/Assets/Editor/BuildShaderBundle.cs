using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Builds the asset bundle SailwindPlayerModel embeds (its shaders), for the game's own Unity version and platform.
/// Run from the command line:
///   Unity.exe -batchmode -nographics -quit -projectPath unity -executeMethod BuildShaderBundle.Build -logFile build.log
/// The bundle lands in src/SailwindPlayerModel/Resources/playermodel.shaders.
/// </summary>
public static class BuildShaderBundle
{
    private const string BundleName = "playermodel.shaders";

    public static void Build()
    {
        // Sailwind renders in LINEAR color space. Surface shaders are compiled for the project's color space (the
        // dielectric reflectance, and the conversion of ambient light, are baked in), and a project left at Unity's
        // default, gamma, built shaders that lit every color brighter than the game's own character shader.
        if (PlayerSettings.colorSpace != ColorSpace.Linear)
        {
            PlayerSettings.colorSpace = ColorSpace.Linear;
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
        }
        if (PlayerSettings.colorSpace != ColorSpace.Linear)
        {
            Debug.LogError("BuildShaderBundle: the project is not in linear color space");
            EditorApplication.Exit(1);
            return;
        }

        // A shader with an error still builds, as a pass-less shell. In batch mode the error is only in the log, as
        // "Shader error in"; check the log, and the bundle's passes, after every build. This check catches what the
        // editor does report.
        foreach (var path in new[] { "Assets/Shaders/SeatedBodyFade.shader", "Assets/Shaders/PatternedBody.shader" })
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (shader == null) { Debug.LogError("BuildShaderBundle: cannot load " + path); EditorApplication.Exit(1); return; }
            var messages = ShaderUtil.GetShaderMessages(shader);
            foreach (var m in messages)
                Debug.Log("BuildShaderBundle: " + path + ": " + m.severity + " line " + m.line + ": " + m.message + " " + m.messageDetails);
            if (ShaderUtil.ShaderHasError(shader)) { Debug.LogError("BuildShaderBundle: " + path + " has errors"); EditorApplication.Exit(1); return; }
        }

        string outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Build"));
        Directory.CreateDirectory(outDir);
        var builds = new[]
        {
            new AssetBundleBuild
            {
                assetBundleName = BundleName,
                assetNames = new[] { "Assets/Shaders/SeatedBodyFade.shader", "Assets/Shaders/PatternedBody.shader" },
            },
        };
        var manifest = BuildPipeline.BuildAssetBundles(outDir, builds,
            BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode | BuildAssetBundleOptions.ForceRebuildAssetBundle,
            BuildTarget.StandaloneWindows64);
        if (manifest == null)
        {
            Debug.LogError("BuildShaderBundle: the bundle did not build");
            EditorApplication.Exit(1);
            return;
        }

        string dest = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "src", "SailwindPlayerModel", "Resources"));
        Directory.CreateDirectory(dest);
        File.Copy(Path.Combine(outDir, BundleName), Path.Combine(dest, BundleName), true);
        Debug.Log("BuildShaderBundle: wrote " + Path.Combine(dest, BundleName));
    }
}
