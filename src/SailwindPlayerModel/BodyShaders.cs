using UnityEngine;

namespace SailwindPlayerModel
{
    /// <summary>
    /// The mod's own shaders, built with the game's Unity version (2019.1.10f1) by the project in /unity and embedded
    /// in this DLL as an asset bundle. Loaded once, on first use. A shader the graphics card cannot run, or a bundle that
    /// fails to load, leaves the feature that wanted it switched off with a line in the log.
    /// </summary>
    internal static class BodyShaders
    {
        private const string ResourceName = "SailwindPlayerModel.playermodel.shaders";
        private static bool _tried;
        private static Shader _seatedFade;

        /// <summary>SailwindPlayerModel/SeatedBodyFade, or null when it could not be loaded.</summary>
        public static Shader SeatedFade
        {
            get
            {
                if (!_tried) Load();
                return _seatedFade;
            }
        }

        private static void Load()
        {
            _tried = true;
            try
            {
                byte[] bytes;
                using (var stream = typeof(BodyShaders).Assembly.GetManifestResourceStream(ResourceName))
                {
                    if (stream == null) { Plugin.Log.LogWarning("[Shaders] the shader bundle is missing from the DLL"); return; }
                    bytes = new byte[stream.Length];
                    int read = 0;
                    while (read < bytes.Length)
                    {
                        int n = stream.Read(bytes, read, bytes.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                }
                var bundle = AssetBundle.LoadFromMemory(bytes);
                if (bundle == null) { Plugin.Log.LogWarning("[Shaders] the shader bundle did not load"); return; }
                foreach (var shader in bundle.LoadAllAssets<Shader>())
                {
                    if (shader.name == "SailwindPlayerModel/SeatedBodyFade") _seatedFade = shader;
                }
                // The shaders stay loaded; the bundle's own bookkeeping can go.
                bundle.Unload(false);
                if (_seatedFade == null) Plugin.Log.LogWarning("[Shaders] SeatedBodyFade is not in the bundle");
                else if (!_seatedFade.isSupported) { Plugin.Log.LogWarning("[Shaders] SeatedBodyFade is not supported on this graphics card"); _seatedFade = null; }
                else Plugin.Log.LogInfo("[Shaders] loaded SeatedBodyFade");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("[Shaders] " + e.Message);
                _seatedFade = null;
            }
        }
    }
}
