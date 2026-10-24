using System;
using UnityEngine;

namespace VortexVapor
{
    // Daylight 0..1 from the sun's height over the craft's horizon.
    public static class Sunlight
    {
        // Sun elevation in degrees across which it fades from night to day.
        public const float TwilightLow = -8f, TwilightHigh = 2f;
        // An eclipse is only believed with the sun well clear of the horizon.
        const float EclipseMinElevation = 10f;

        public static float Fraction(Vessel v)
        {
            float elevation;
            return Fraction(v, out elevation);
        }

        public static float Fraction(Vessel v, out float elevation)
        {
            elevation = 90f;
            CelestialBody star = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            CelestialBody body = v != null ? v.mainBody : null;
            if (star == null || body == null || body == star) return 1f;

            Vector3d pos = v.CoMD;
            Vector3d up = (pos - body.position).normalized;
            Vector3d toSun = (star.position - pos).normalized;
            double elev = Math.Asin(Math.Max(-1.0, Math.Min(1.0, Vector3d.Dot(up, toSun)))) * (180.0 / Math.PI);
            // The horizon dips as the craft climbs, so a high wake stays lit after the ground has
            // gone dark.
            double r = Math.Max(body.Radius, 1.0), h = Math.Max(v.altitude, 0.0);
            double dip = Math.Acos(r / (r + h)) * (180.0 / Math.PI);
            elevation = (float)(elev + dip);

            float lit = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(TwilightLow, TwilightHigh, elevation));
            if (elevation > EclipseMinElevation && v.solarFlux <= 0.0) lit = 0f;   // another body in the way
            return lit;
        }

        // Optical depths of the air straight up, red, green, blue (Rayleigh and haze, roughly).
        static readonly Vector3 Depth = new Vector3(0.09f, 0.17f, 0.35f);

        // sunlight left at an elevation (air mass), dimmed with the sun's height below 45 degrees.
        public static Vector3 Tint(float elevationDeg)
        {
            float e = Mathf.Max(elevationDeg, 0f);
            float mass = 1f / (Mathf.Sin(e * Mathf.Deg2Rad) + 0.50572f * Mathf.Pow(e + 6.07995f, -1.6364f));
            float extra = mass - 1f;
            return new Vector3(Mathf.Exp(-Depth.x * extra), Mathf.Exp(-Depth.y * extra), Mathf.Exp(-Depth.z * extra)) * Slant(e);
        }

        // How far KSP's frame has darkened with the sun's height (1 from 45 degrees up); the sky's
        // light on the vapor follows it too.
        public static float Slant(float elevationDeg)
        {
            return Mathf.Sqrt(Mathf.Clamp(Mathf.Sin(Mathf.Max(elevationDeg, 0f) * Mathf.Deg2Rad) / 0.7071f, 0.02f, 1f));
        }
    }
}
