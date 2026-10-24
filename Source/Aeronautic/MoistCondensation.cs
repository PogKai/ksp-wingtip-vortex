// Ported from aeronautic (github.com/Romainlg29/aeronautic @ ca535855f7), packages/wing-vapor/src/condensation.ts.
// MIT License, (c) 2026 Romain Le Gall; see THIRD-PARTY-NOTICES.md.
using System;

namespace VortexVapor.Aeronautic
{
    // Expansion on the moist adiabat: latent heat limits how much condenses.
    public static class MoistCondensation
    {
        public const double MinRatio = 0.3;
        public const int Texels = 128;
        public const double DefaultHumiditySpread = 0.06;
        public const double MoistestHumidity = 0.995;

        public struct Parcel
        {
            public double TemperatureK, Liquid, WaterKgPerM3;
        }

        public static Parcel Condensate(double pressureRatio, MoistAir air, double humidityScale = 1)
        {
            double ratio = Math.Min(Math.Max(pressureRatio, 1e-3), 1);
            double pressure = air.PressurePa * ratio;
            double dry = air.TemperatureK * Math.Pow(ratio, Atmosphere.AirGasConstant / Atmosphere.AirCp);
            double water = air.MixingRatio * Math.Max(humidityScale, 0);

            if (water <= Saturated(dry, pressure))
                return new Parcel { TemperatureK = dry, Liquid = 0, WaterKgPerM3 = 0 };

            double low = dry, high = Math.Max(air.TemperatureK, dry + 1e-3);
            for (int i = 0; i < 40; i++)
            {
                double middle = (low + high) / 2;
                double balance = Atmosphere.AirCp * (middle - dry) - Atmosphere.LatentHeat * (water - Saturated(middle, pressure));
                if (balance > 0) high = middle; else low = middle;
            }
            double t = (low + high) / 2;
            double liquid = Math.Max(water - Saturated(t, pressure), 0);
            return new Parcel { TemperatureK = t, Liquid = liquid, WaterKgPerM3 = liquid * pressure / (Atmosphere.AirGasConstant * t) };
        }

        static double Saturated(double temperatureK, double pressurePa)
        {
            return Atmosphere.MixingRatio(Atmosphere.SaturationPressure(temperatureK), pressurePa);
        }

        // How much moister than the day's average its moistest patches are, never past saturation.
        public static double Moistest(MoistAir air, double spread)
        {
            return Math.Max(Math.Min(1 + Math.Max(spread, 0), MoistestHumidity / Math.Max(air.RelativeHumidity, 1e-3)), 1);
        }

        public static double RatioAt(int texel)
        {
            return MinRatio + (1 - MinRatio) * texel / (Texels - 1);
        }

        // Texels x 4 floats: g/m^3 of liquid for the drier, the day's and the moister parcel, and
        // the day's parcel's temperature drop, K.
        public static float[] Table(MoistAir air, double spread = DefaultHumiditySpread, float[] target = null)
        {
            if (target == null) target = new float[Texels * 4];
            double moist = Moistest(air, spread);
            for (int texel = 0; texel < Texels; texel++)
            {
                double ratio = RatioAt(texel);
                Parcel day = Condensate(ratio, air, 1);
                target[texel * 4] = (float)(Condensate(ratio, air, 1 - spread).WaterKgPerM3 * 1000);
                target[texel * 4 + 1] = (float)(day.WaterKgPerM3 * 1000);
                target[texel * 4 + 2] = (float)(Condensate(ratio, air, moist).WaterKgPerM3 * 1000);
                target[texel * 4 + 3] = (float)(air.TemperatureK - day.TemperatureK);
            }
            return target;
        }

        // Per metre, for liquid water in kg/m^3 as droplets of this radius.
        public static double CloudExtinction(double waterKgPerM3, double dropletRadiusM)
        {
            return 3 * waterKgPerM3 / (2 * 1000 * Math.Max(dropletRadiusM, 1e-8));
        }

        // The pressure ratio at which the free air just saturates, or 0 if none short of the table's floor.
        public static double SaturationRatio(MoistAir air)
        {
            double low = MinRatio, high = 1;
            if (Condensate(low, air).Liquid <= 0) return 0;
            for (int i = 0; i < 40; i++)
            {
                double middle = (low + high) / 2;
                if (Condensate(middle, air).Liquid > 0) low = middle; else high = middle;
            }
            return low;
        }

        // The deficit (1 - pressure ratio) at which the free air, made a little moister, just
        // condenses; 1 if nothing in reach does.
        public static double CondensingDeficit(MoistAir air, double humidityScale)
        {
            // The same day asks the same question every frame.
            if (air.TemperatureK == memoT && air.PressurePa == memoP && air.MixingRatio == memoMix && humidityScale == memoScale) return memoDeficit;
            double deficit = SolveCondensingDeficit(air, humidityScale);
            memoT = air.TemperatureK; memoP = air.PressurePa; memoMix = air.MixingRatio; memoScale = humidityScale; memoDeficit = deficit;
            return deficit;
        }

        static double memoT = double.NaN, memoP, memoMix, memoScale, memoDeficit;

        static double SolveCondensingDeficit(MoistAir air, double humidityScale)
        {
            double low = MinRatio, high = 1;
            if (Condensate(low, air, humidityScale).Liquid <= 0) return 1;
            for (int i = 0; i < 32; i++)
            {
                double middle = (low + high) / 2;
                if (Condensate(middle, air, humidityScale).Liquid > 0) low = middle; else high = middle;
            }
            return 1 - low;
        }
    }
}
