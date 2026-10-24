using System;
using System.IO;
using UnityEngine;

namespace VortexVapor
{
    // Loads the platform's shader bundle once; a failed shader stays null for fallback handling.
    public static class VaporShaders
    {
        public const string RaymarchName = "WingtipVortex/WingVapor", CompositeName = "WingtipVortex/VaporComposite";

        static bool loaded;
        static Shader raymarch, composite;
        public static Shader Composite { get { Load(); return composite; } }

        public static Shader Raymarch { get { Load(); return raymarch; } }

        public static void Load()
        {
            if (loaded) return;
            loaded = true;
            string platform = Application.platform == RuntimePlatform.OSXPlayer ? "macos"
                            : Application.platform == RuntimePlatform.LinuxPlayer ? "linux" : "windows";
            try
            {
                string plugins = Path.GetDirectoryName(typeof(VaporShaders).Assembly.Location);
                string file = Path.GetFullPath(Path.Combine(Path.Combine(plugins, ".."), Path.Combine("Shaders", "wingvapor-" + platform + ".bundle")));
                if (!File.Exists(file)) { Debug.Log("[VORTEX] wing vapor: no shader bundle at " + file); return; }
                AssetBundle bundle = AssetBundle.LoadFromFile(file);
                if (bundle == null) { Debug.Log("[VORTEX] wing vapor: could not load " + file); return; }
                foreach (Shader shader in bundle.LoadAllAssets<Shader>())
                {
                    Debug.Log("[VORTEX] wing vapor: shader " + shader.name + (shader.isSupported ? " loaded" : " NOT supported on " + SystemInfo.graphicsDeviceType));
                    if (shader.name == RaymarchName && shader.isSupported) raymarch = shader;
                    if (shader.name == CompositeName && shader.isSupported) { composite = shader; VaporVolumePass.CompositeShader = shader; }
                }
                bundle.Unload(false);
            }
            catch (Exception e) { Debug.Log("[VORTEX] wing vapor: shader bundle failed: " + e.Message); }
        }
    }

    // Loads the bundle once at the main menu, so a failure shows in the log before any flight.
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class VaporShaderLoader : MonoBehaviour
    {
        void Start() { VaporShaders.Load(); }
    }
}
