using System;
using UnityEngine;

namespace VortexVapor
{
    // How lit the air around a craft is, 0 (night) to 1 (day). Shared by the vortices and the wing
    // vapor, which both dim on the night side: the vortices because their additive material glows
    // like neon against a dark sky, the vapor because an unlit cloud is not white.
    //
    // It is keyed to the sun's height above the craft's own horizon, not to vessel.solarFlux as it
    // was. Stock's solarFlux includes the atmosphere's absorption along the sun's path, so it falls
    // steeply as the sun gets low: an afternoon at KSC read as 20-45% lit while the sky was still
    // bright daylight, and both effects were drawn at a half to a third of their daytime strength,
    // exactly when a bright sky washes out an additive trail. Night is about the sky going dark,
    // which follows the sun's elevation.
    public static class Sunlight
    {
        // Elevation of the sun above the craft's horizon, degrees, across which it fades from night
        // to day: from deep in twilight to just above the horizon.
        public const float TwilightLow = -8f, TwilightHigh = 2f;
        // An eclipse is only believed with the sun well clear of the horizon, where no amount of
        // atmosphere or terrain explains stock reporting no sunlight at all.
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
            // The horizon dips as the craft climbs, so a high wake is still lit after the ground
            // below it has gone dark, which is when real contrails are at their most striking.
            double r = Math.Max(body.Radius, 1.0), h = Math.Max(v.altitude, 0.0);
            double dip = Math.Acos(r / (r + h)) * (180.0 / Math.PI);
            elevation = (float)(elev + dip);

            float lit = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(TwilightLow, TwilightHigh, elevation));
            if (elevation > EclipseMinElevation && v.solarFlux <= 0.0) lit = 0f;   // another body in the way
            return lit;
        }
    }
}
