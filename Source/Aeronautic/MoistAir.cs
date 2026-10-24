// Ported from aeronautic (github.com/Romainlg29/aeronautic @ ca535855f7), packages/core/src/atmosphere.ts.
// MIT License, (c) 2026 Romain Le Gall; see THIRD-PARTY-NOTICES.md.
using System;

namespace VortexVapor.Aeronautic
{
    // The air and the water it carries. Plain doubles, no Unity or KSP types, so it can be checked
    // offline against the TypeScript.
    public static class Atmosphere
    {
        const double Hydrostatic = 0.0341632;
        public const double SeaLevelK = 288.15;
        public const double SeaLevelPa = 101325;
        public const double AirGamma = 1.4;
        public const double AirGasConstant = 287.05;
        public const double AirCp = 1004.6;
        public const double VapourRatio = 0.622;
        public const double LatentHeat = 2.501e6;

        static readonly double[,] Layers =
        {
            { 0, -0.0065 }, { 11000, 0 }, { 20000, 0.001 }, { 32000, 0.0028 },
            { 47000, 0 }, { 51000, -0.0028 }, { 71000, -0.002 }, { 86000, 0 },
        };
        const double CeilingM = 120000;

        // ISA temperature (K) and pressure (Pa) at an altitude.
        public static void Standard(double altitudeM, out double temperatureK, out double pressurePa)
        {
            double height = Math.Max(0, Math.Min(altitudeM, CeilingM));
            double t = SeaLevelK, pressure = 1;
            int n = Layers.GetLength(0);
            for (int i = 0; i < n; i++)
            {
                double baseM = Layers[i, 0], lapse = Layers[i, 1];
                double top = i + 1 < n ? Layers[i + 1, 0] : double.PositiveInfinity;
                double rise = Math.Min(height, top) - baseM;
                if (rise <= 0) break;
                double next = t + lapse * rise;
                pressure *= lapse == 0 ? Math.Exp(-Hydrostatic * rise / t) : Math.Pow(next / t, -Hydrostatic / lapse);
                t = next;
            }
            temperatureK = t;
            pressurePa = pressure * SeaLevelPa;
        }

        public static double SpeedOfSound(double temperatureK)
        {
            return Math.Sqrt(AirGamma * AirGasConstant * Math.Max(temperatureK, 1));
        }

        // Over liquid water, also supercooled (Alduchov and Eskridge's Magnus fit), Pa.
        public static double SaturationPressure(double temperatureK)
        {
            double c = temperatureK - 273.15;
            return 610.94 * Math.Exp(17.625 * c / (c + 243.04));
        }

        public static double DewPoint(double vapourPa)
        {
            double ln = Math.Log(Math.Max(vapourPa, 1e-6) / 610.94);
            return 273.15 + 243.04 * ln / (17.625 - ln);
        }

        public static double MixingRatio(double vapourPa, double pressurePa)
        {
            return VapourRatio * vapourPa / Math.Max(pressurePa - vapourPa, 1);
        }
    }

    public class MoistAir
    {
        public double TemperatureK, PressurePa, DensityKgPerM3, SoundMPerS, VapourPa, MixingRatio, DewPointK, RelativeHumidity;

        // The standard atmosphere at an altitude, warmed by an offset, at a relative humidity
        // (aeronautic's moist_air).
        public static MoistAir FromAltitude(double altitudeM, double relativeHumidity, double temperatureOffsetK = 0)
        {
            double t, p;
            Atmosphere.Standard(altitudeM, out t, out p);
            return FromState(Math.Max(t + temperatureOffsetK, 150), p, relativeHumidity);
        }

        // Air at a given temperature and pressure, as KSP reports them.
        public static MoistAir FromState(double temperatureK, double pressurePa, double relativeHumidity)
        {
            double humidity = Math.Min(Math.Max(relativeHumidity, 0), 1);
            double vapour = Math.Min(humidity * Atmosphere.SaturationPressure(temperatureK), pressurePa * 0.5);
            return new MoistAir
            {
                TemperatureK = temperatureK,
                PressurePa = pressurePa,
                DensityKgPerM3 = (pressurePa - 0.378 * vapour) / (Atmosphere.AirGasConstant * temperatureK),
                SoundMPerS = Atmosphere.SpeedOfSound(temperatureK),
                VapourPa = vapour,
                MixingRatio = Atmosphere.MixingRatio(vapour, pressurePa),
                DewPointK = Atmosphere.DewPoint(vapour),
                RelativeHumidity = humidity,
            };
        }
    }
}
