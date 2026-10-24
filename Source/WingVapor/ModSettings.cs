using System;
using System.Globalization;
using System.IO;
using KSP.UI.Screens;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace VortexVapor
{
    // Player toggles and intensity dials, saved to PluginData/settings.cfg.
    public static class ModSettings
    {
        public const float MaxIntensity = 2f;

        static bool loaded;
        static bool vortexOn = true, vaporOn = true, vaporSheet = true;
        static float vortexIntensity = 1f, vaporIntensity = 1f, sheetDepth = 1f;

        // Set by the setters; the window saves shortly after the last change.
        public static bool Dirty;
        public static float ChangedAt;

        // What the effects multiply their strength by: 0 when off.
        public static float VortexScale { get { Ensure(); return vortexOn ? vortexIntensity : 0f; } }
        public static float VaporScale { get { Ensure(); return vaporOn ? vaporIntensity : 0f; } }

        public static bool VortexOn { get { Ensure(); return vortexOn; } set { Ensure(); if (vortexOn != value) { vortexOn = value; Touch(); } } }
        public static bool VaporOn { get { Ensure(); return vaporOn; } set { Ensure(); if (vaporOn != value) { vaporOn = value; Touch(); } } }
        public static float VortexIntensity { get { Ensure(); return vortexIntensity; } set { Ensure(); value = Clamp(value); if (vortexIntensity != value) { vortexIntensity = value; Touch(); } } }
        public static float VaporIntensity { get { Ensure(); return vaporIntensity; } set { Ensure(); value = Clamp(value); if (vaporIntensity != value) { vaporIntensity = value; Touch(); } } }

        // Wing vapor raymarched (true) or drawn as the legacy particle puffs (false), and how high the
        // raymarched vapor stands off the wing, as a share of the default.
        public static bool VaporSheet { get { Ensure(); return vaporSheet; } set { Ensure(); if (vaporSheet != value) { vaporSheet = value; Touch(); } } }
        public static float SheetDepth { get { Ensure(); return sheetDepth; } set { Ensure(); value = Clamp(value); if (sheetDepth != value) { sheetDepth = value; Touch(); } } }

        static float Clamp(float v) { return Mathf.Clamp(v, 0f, MaxIntensity); }
        static void Touch() { Dirty = true; ChangedAt = Time.unscaledTime; }

        public static void ResetToDefaults()
        {
            Ensure();
            vortexOn = vaporOn = vaporSheet = true;
            vortexIntensity = vaporIntensity = sheetDepth = 1f;
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
                vaporSheet = ReadBool(node, "vaporSheet", true);
                sheetDepth = Clamp(ReadFloat(node, "sheetDepth", 1f));
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
                node.AddValue("vaporSheet", vaporSheet.ToString());
                node.AddValue("sheetDepth", sheetDepth.ToString("F2", CultureInfo.InvariantCulture));
                node.Save(path);
            }
            catch (Exception e) { Debug.Log("[VORTEX] settings: could not save: " + e.Message); }
        }

        // Next to the Plugins folder, like verbose.txt.
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

    // Settings window in flight: app-launcher button or Alt+V.
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
                // Through the button, so it stays lit with the window.
                if (button != null) { if (open) button.SetFalse(true); else button.SetTrue(true); }
                else { open = !open; if (!open && ModSettings.Dirty) ModSettings.Save(); }
            }

            // One write after a slider drag, not hundreds.
            if (ModSettings.Dirty && Time.unscaledTime - ModSettings.ChangedAt > SaveDelay) ModSettings.Save();
        }

        void OnGUI()
        {
            if (!open || uiHidden) return;
            GUI.skin = HighLogic.Skin;
            rect = GUILayout.Window(WindowId, rect, Draw, "Wingtip Vortex " + global::WingtipVortex.ModVersion, GUILayout.Width(300f));
            rect.x = Mathf.Clamp(rect.x, 0f, Screen.width - 60f);
            rect.y = Mathf.Clamp(rect.y, 0f, Screen.height - 40f);
        }

        void Draw(int id)
        {
            if (heading == null) heading = new GUIStyle(GUI.skin.toggle) { richText = true };
            if (note == null) note = new GUIStyle(GUI.skin.label) { fontSize = 11, wordWrap = true };

            GUILayout.Space(4f);
            ModSettings.VortexOn = GUILayout.Toggle(ModSettings.VortexOn, " <b>Wingtip vortices</b>", heading);
            ModSettings.VortexIntensity = IntensityRow("Strength", ModSettings.VortexIntensity, ModSettings.VortexOn);

            GUILayout.Space(8f);
            ModSettings.VaporOn = GUILayout.Toggle(ModSettings.VaporOn, " <b>Wing vapor</b>", heading);
            ModSettings.VaporIntensity = IntensityRow("Density", ModSettings.VaporIntensity, ModSettings.VaporOn);
            bool raymarched = !Legacy;
            ModSettings.SheetDepth = IntensityRow("Height", ModSettings.SheetDepth, ModSettings.VaporOn && raymarched);
            bool wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && ModSettings.VaporOn;
            ModSettings.VaporSheet = !GUILayout.Toggle(Legacy, " Legacy particle vapor");
            GUI.enabled = wasEnabled;
            if (Legacy && ModSettings.VaporOn) GUILayout.Label("The older puff effect, in place of the raymarched vapor.", note);

            GUILayout.Space(10f);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Defaults")) ModSettings.ResetToDefaults();
            if (GUILayout.Button("Close"))
            {
                if (button != null) button.SetFalse(true); else OnClose();
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("Alt+V opens and closes this window. 100% is the default look.", note);
            GUI.DragWindow();
        }

        GUIStyle heading, note;
        static bool Legacy { get { return !ModSettings.VaporSheet; } }

        // 0 to MaxIntensity in 5% steps, snapping to 100%; greyed out while its effect is off.
        static float IntensityRow(string label, float value, bool enabled)
        {
            bool wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && enabled;
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(64f));
            float raw = GUILayout.HorizontalSlider(value, 0f, ModSettings.MaxIntensity);
            GUILayout.Label(Mathf.RoundToInt(value * 100f) + "%", GUILayout.Width(44f));
            GUILayout.EndHorizontal();
            GUI.enabled = wasEnabled;
            // Unchanged until dragged, so a saved 0.97 is not rounded by drawing the window.
            if (raw == value) return value;
            float v = Mathf.Round(raw * 20f) / 20f;
            return Mathf.Abs(v - 1f) < 0.06f ? 1f : v;
        }

        // A wing seen from ahead with its tip vortex rolling up off the tip, white, 38x38 like the
        // stock launcher icons; drawn here (4x4 samples a pixel), no asset to ship.
        static Texture2D MakeIcon()
        {
            const int n = 38, sub = 4, coil = 240;
            // The wing, root at the left edge tapering to the tip; KSP's textures run bottom-up.
            var wing = new[] { new Vector2(1f, 21f), new Vector2(15f, 19.7f), new Vector2(15f, 18.3f), new Vector2(1f, 16f) };
            // The vortex: a spiral out from its core to the tip, 2.4 turns.
            var spiral = new Vector2[coil];
            for (int k = 0; k < coil; k++)
            {
                float f = 1f - k / (coil - 1f);
                float r = 0.6f + 9.9f * f, a = Mathf.PI + f * 2.4f * 2f * Mathf.PI;
                spiral[k] = new Vector2(25.5f + r * Mathf.Cos(a), 19f - r * Mathf.Sin(a));
            }
            var tex = new Texture2D(n, n, TextureFormat.ARGB32, false);
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float sum = 0f;
                    for (int sy = 0; sy < sub; sy++)
                        for (int sx = 0; sx < sub; sx++)
                        {
                            var q = new Vector2(x + (sx + 0.5f) / sub, y + (sy + 0.5f) / sub);
                            float near = float.MaxValue;
                            for (int k = 0; k + 1 < coil; k++) near = Mathf.Min(near, SegmentDistance(q, spiral[k], spiral[k + 1]));
                            sum += Inside(q, wing) ? 1f : Mathf.Clamp01(1.5f - near);
                        }
                    px[y * n + x] = new Color(1f, 1f, 1f, sum / (sub * sub));
                }
            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }

        static bool Inside(Vector2 p, Vector2[] poly)
        {
            bool c = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
                if ((poly[i].y > p.y) != (poly[j].y > p.y) && p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x) c = !c;
            return c;
        }

        static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
            return (p - (a + t * ab)).magnitude;
        }
    }
}
