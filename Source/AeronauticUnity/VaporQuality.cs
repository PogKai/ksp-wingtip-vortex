// Upstream's quality presets (VAPOR_QUALITY): most steps per pixel.
// After aeronautic (github.com/Romainlg29/aeronautic @ ca535855f7), packages/wing-vapor/src/types.ts.
// MIT License, (c) 2026 Romain Le Gall; see THIRD-PARTY-NOTICES.md.
namespace VortexVapor
{
    public static class VaporQuality
    {
        public const int Low = 0, Medium = 1, High = 2, Ultra = 3, Default = High;

        static readonly int[] steps = { 64, 112, 160, 256 };
        static readonly string[] names = { "Low", "Medium", "High", "Ultra" };

        public static int Clamp(int preset) { return preset < Low ? Low : preset > Ultra ? Ultra : preset; }
        public static int MaxSteps(int preset) { return steps[Clamp(preset)]; }
        public static string Name(int preset) { return names[Clamp(preset)]; }
    }
}
