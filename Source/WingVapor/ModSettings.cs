using System;
using System.Globalization;
using System.IO;
using KSP.UI.Screens;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace VortexVapor
{
    // The player's own switches and intensity dials for the two effects, shared by both addons in
    // this assembly, kept in GameData/<this mod>/PluginData/settings.cfg and read once per session.
    //
    // The dials are MULTIPLIERS on what the physics already decided, applied at the last step
    // before drawing, never inputs to the physics: a wake at 50% is the same wake, fainter and
    // thinner, not a different aircraft. That keeps every threshold, onset and fade the mod was
    // tuned with exactly where it was at 100%, and 100% is the default.
    //
    // Switching an effect off sets its scale to zero rather than tearing anything down, so what is
    // already in the air ages out on its own instead of vanishing in one frame.
    public static class ModSettings
    {
        public const float MaxIntensity = 2f;

        static bool loaded;
        static bool vortexOn = true, vaporOn = true;
        static float vortexIntensity = 1f, vaporIntensity = 1f;

        // Set by the setters; the window saves a moment after the player stops dragging a slider.
        public static bool Dirty;
        public static float ChangedAt;

        // What the effects multiply their strength by: 0 when off.
        public static float VortexScale { get { Ensure(); return vortexOn ? vortexIntensity : 0f; } }
        public static float VaporScale { get { Ensure(); return vaporOn ? vaporIntensity : 0f; } }

        public static bool VortexOn { get { Ensure(); return vortexOn; } set { Ensure(); if (vortexOn != value) { vortexOn = value; Touch(); } } }
        public static bool VaporOn { get { Ensure(); return vaporOn; } set { Ensure(); if (vaporOn != value) { vaporOn = value; Touch(); } } }
        public static float VortexIntensity { get { Ensure(); return vortexIntensity; } set { Ensure(); value = Clamp(value); if (vortexIntensity != value) { vortexIntensity = value; Touch(); } } }
        public static float VaporIntensity { get { Ensure(); return vaporIntensity; } set { Ensure(); value = Clamp(value); if (vaporIntensity != value) { vaporIntensity = value; Touch(); } } }

        static float Clamp(float v) { return Mathf.Clamp(v, 0f, MaxIntensity); }
        static void Touch() { Dirty = true; ChangedAt = Time.unscaledTime; }

        public static void ResetToDefaults()
        {
            Ensure();
            vortexOn = vaporOn = true;
            vortexIntensity = vaporIntensity = 1f;
            Touch();
        }

        static void Ensure()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                string path = FilePath();
                if (path == null || !File.Exists(path)) return;
                ConfigNode node = ConfigNode.Load(path);
                if (node == null) return;
                vortexOn = ReadBool(node, "vortexEnabled", true);
                vaporOn = ReadBool(node, "vaporEnabled", true);
                vortexIntensity = Clamp(ReadFloat(node, "vortexIntensity", 1f));
                vaporIntensity = Clamp(ReadFloat(node, "vaporIntensity", 1f));
            }
            catch (Exception e) { Debug.Log("[VORTEX] settings: could not read, using defaults: " + e.Message); }
        }

        public static void Save()
        {
            Dirty = false;
            try
            {
                string path = FilePath();
                if (path == null) return;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var node = new ConfigNode("WingtipVortexSettings");
                node.AddValue("vortexEnabled", vortexOn.ToString());
                node.AddValue("vaporEnabled", vaporOn.ToString());
                node.AddValue("vortexIntensity", vortexIntensity.ToString("F2", CultureInfo.InvariantCulture));
                node.AddValue("vaporIntensity", vaporIntensity.ToString("F2", CultureInfo.InvariantCulture));
                node.Save(path);
            }
            catch (Exception e) { Debug.Log("[VORTEX] settings: could not save: " + e.Message); }
        }

        // GameData/<this mod>/PluginData/settings.cfg, next to the Plugins folder (as verbose.txt).
        static string FilePath()
        {
            string plugins = Path.GetDirectoryName(typeof(ModSettings).Assembly.Location);
            string root = plugins != null ? Path.GetDirectoryName(plugins) : null;
            return root != null ? Path.Combine(Path.Combine(root, "PluginData"), "settings.cfg") : null;
        }

        static bool ReadBool(ConfigNode node, string key, bool fallback)
        {
            bool v;
            return bool.TryParse(node.GetValue(key), out v) ? v : fallback;
        }

        static float ReadFloat(ConfigNode node, string key, float fallback)
        {
            float v;
            return float.TryParse(node.GetValue(key), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }
    }

    // The settings window in flight: a button on the stock app launcher, or Alt+V.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class ModSettingsWindow : MonoBehaviour
    {
        const int WindowId = 0x57545601;
        const float SaveDelay = 1f;   // s after the last change

        Rect rect = new Rect(140f, 140f, 290f, 10f);
        bool open, uiHidden;
        ApplicationLauncherButton button;
        Texture2D icon;

        void Start()
        {
            GameEvents.onGUIApplicationLauncherReady.Add(AddButton);
            GameEvents.onGUIApplicationLauncherDestroyed.Add(RemoveButton);
            GameEvents.onHideUI.Add(HideUI);
            GameEvents.onShowUI.Add(ShowUI);
            if (ApplicationLauncher.Ready) AddButton();
        }

        void OnDestroy()
        {
            GameEvents.onGUIApplicationLauncherReady.Remove(AddButton);
            GameEvents.onGUIApplicationLauncherDestroyed.Remove(RemoveButton);
            GameEvents.onHideUI.Remove(HideUI);
            GameEvents.onShowUI.Remove(ShowUI);
            RemoveButton();
            if (icon != null) Destroy(icon);
            if (ModSettings.Dirty) ModSettings.Save();
        }

        void HideUI() { uiHidden = true; }
        void ShowUI() { uiHidden = false; }

        void AddButton()
        {
            if (button != null || ApplicationLauncher.Instance == null) return;
            if (icon == null) icon = MakeIcon();
            button = ApplicationLauncher.Instance.AddModApplication(
                OnOpen, OnClose, null, null, null, null, ApplicationLauncher.AppScenes.FLIGHT, icon);
        }

        void RemoveButton()
        {
            if (button != null && ApplicationLauncher.Instance != null)
                ApplicationLauncher.Instance.RemoveModApplication(button);
            button = null;
        }

        void OnOpen() { open = true; }

        void OnClose()
        {
            open = false;
            if (ModSettings.Dirty) ModSettings.Save();
        }

        void Update()
        {
            bool alt = Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt);
            if (alt && Input.GetKeyDown(KeyCode.V))
            {
                // Through the button where there is one, so it stays lit in step with the window.
                if (button != null) { if (open) button.SetFalse(true); else button.SetTrue(true); }
                else { open = !open; if (!open && ModSettings.Dirty) ModSettings.Save(); }
            }

            // Saved shortly after the last change, so dragging a slider is one write, not hundreds.
            if (ModSettings.Dirty && Time.unscaledTime - ModSettings.ChangedAt > SaveDelay) ModSettings.Save();
        }

        void OnGUI()
        {
            if (!open || uiHidden) return;
            GUI.skin = HighLogic.Skin;
            rect = GUILayout.Window(WindowId, rect, Draw, "Wingtip Vortex " + global::WingtipVortex.ModVersion, GUILayout.Width(290f));
            rect.x = Mathf.Clamp(rect.x, 0f, Screen.width - 60f);
            rect.y = Mathf.Clamp(rect.y, 0f, Screen.height - 40f);
        }

        void Draw(int id)
        {
            GUILayout.Space(4f);
            ModSettings.VortexOn = GUILayout.Toggle(ModSettings.VortexOn, " Wingtip vortices");
            ModSettings.VortexIntensity = IntensityRow(ModSettings.VortexIntensity, ModSettings.VortexOn);
            GUILayout.Space(6f);
            ModSettings.VaporOn = GUILayout.Toggle(ModSettings.VaporOn, " Wing vapor");
            ModSettings.VaporIntensity = IntensityRow(ModSettings.VaporIntensity, ModSettings.VaporOn);
            GUILayout.Space(8f);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Reset")) ModSettings.ResetToDefaults();
            if (GUILayout.Button("Close"))
            {
                if (button != null) button.SetFalse(true); else OnClose();
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("Alt+V toggles this window. 100% is the default look.");
            GUI.DragWindow();
        }

        // A slider from 0 to MaxIntensity in 5% steps, with a pull toward 100% so the default is
        // easy to land on. Greyed out while its effect is off, but still readable.
        static float IntensityRow(float value, bool enabled)
        {
            bool wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && enabled;
            GUILayout.BeginHorizontal();
            GUILayout.Label("Intensity", GUILayout.Width(64f));
            float raw = GUILayout.HorizontalSlider(value, 0f, ModSettings.MaxIntensity);
            GUILayout.Label(Mathf.RoundToInt(value * 100f) + "%", GUILayout.Width(44f));
            GUILayout.EndHorizontal();
            GUI.enabled = wasEnabled;
            // The slider hands back exactly what it was given until it is dragged, so a setting
            // read from the file as 0.97 is not nudged just by drawing the window.
            if (raw == value) return value;
            float v = Mathf.Round(raw * 20f) / 20f;
            return Mathf.Abs(v - 1f) < 0.06f ? 1f : v;
        }

        // A white vortex spiral on transparent, 38x38 like the stock launcher icons. Drawn here, so
        // there is no image asset to ship.
        static Texture2D MakeIcon()
        {
            const int n = 38;
            var tex = new Texture2D(n, n, TextureFormat.ARGB32, false);
            var px = new Color[n * n];
            float c = (n - 1) * 0.5f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = x - c, dy = y - c;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float turn = Mathf.Atan2(dy, dx) / Mathf.PI + r / 9f;   // two arms
                    float t = turn - Mathf.Floor(turn);
                    float arm = 1f - Mathf.SmoothStep(0f, 0.2f, Mathf.Min(t, 1f - t));
                    float edge = 1f - Mathf.SmoothStep(13f, 17f, r);
                    px[y * n + x] = new Color(1f, 1f, 1f, arm * edge);
                }
            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }
    }
}
