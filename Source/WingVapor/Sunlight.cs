using System;
using UnityEngine;

namespace VortexVapor
{
    // How lit the air around a craft is, 0 (night) to 1 (day), shared by the vortices and the wing
    // vapor, which both dim at night. Keyed to the sun's height above the craft's own horizon, not
    // vessel.solarFlux, whose atmospheric absorption reads an afternoon as 20-45% lit under a
    // bright sky.
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
    }
}
