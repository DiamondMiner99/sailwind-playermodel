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
        string outDir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Build"));
        Directory.CreateDirectory(outDir);
        var builds = new[]
        {
            new AssetBundleBuild
            {
                assetBundleName = BundleName,
                assetNames = new[] { "Assets/Shaders/SeatedBodyFade.shader" },
            },
        };
        var manifest = BuildPipeline.BuildAssetBundles(outDir, builds,
            BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode,
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
