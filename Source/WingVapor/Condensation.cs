using UnityEngine;

namespace VortexVapor
{
    // When does the air over a wing turn to cloud? No KSP types, so it can be checked offline.
    // Air over the suction side expands adiabatically and cools: T_local = T (p_local /
    // p)^((g-1)/g). Its vapor keeps the same share of the pressure, but the saturation pressure
    // falls much faster with temperature, so air that ends up above saturation condenses. Nothing
    // here knows about g-load, Mach or aircraft type.
    public static class Condensation
    {
        const float Kappa = 0.2857f;   // (gamma - 1) / gamma for air

        // Share of a wing's pressure difference carried by suction on the upper surface, rather
        // than overpressure underneath. About two thirds on a typical airfoil at moderate lift.
        public const float SuctionShare = 0.67f;

        // Visibility goes by how much water actually condenses. Tuned by eye, not measured.
        public const float OnsetWater = 0.4f, FullWater = 2.5f;

        // Saturation vapour pressure over liquid water, Pa (Magnus; also used supercooled).
        public static float SaturationPressure(float kelvin)
        {
            float c = kelvin - 273.15f;
            return 610.94f * Mathf.Exp(17.625f * c / (c + 243.04f));
        }

        // KSP has no humidity, so the air's moisture is a placeholder, given as a dew-point spread
        // rather than a relative humidity, which would make KSC's hot lowlands (305-308 K) muggy. A
        // fixed spread keeps the dew point tracking the temperature. Humid boundary layer (air
        // denser than 62% of the body's sea-level density, WingtipVortex's humidDensityMin) uses
        // SurfaceSpread; above it FreeAirSpread.
        public static float SurfaceSpread = 12f, FreeAirSpread = 18f;   // K; settable for the offline checks
        public const float BoundaryLayerDensity = 0.62f;

        // Where the vapor can exist at all: a hard limit by density relative to the body's
        // sea-level density, so it means the same on every body (about 14.3 km on Kerbin). In
        // thinner air the mod does no work at all.
        public const float CutoffRatio = 0.10f;
        // Coming down it resumes at the cutoff; going up it stops a little past it, so a craft
        // hovering on the limit does not rebuild its measurements every few steps.
        public const float CutoffHysteresis = 0.9f;

        public static float RelativeHumidity(float rho, float rhoSeaLevel, float kelvin)
        {
            float depth = Mathf.Clamp01((rho / Mathf.Max(rhoSeaLevel, 1e-4f) - BoundaryLayerDensity)
                                        / (1f - BoundaryLayerDensity));
            float spread = Mathf.Lerp(FreeAirSpread, SurfaceSpread, depth);
            return SaturationPressure(kelvin - spread) / SaturationPressure(kelvin);
        }

        // Supersaturation e / e_sat of air that started at (T, p, RH) and was sucked down by
        // `suction` Pa, and the water that condenses out of it, g per kg of air. Latent heat is
        // neglected, which overstates it slightly.
        public static float Supersaturation(float kelvin, float pascal, float rh, float suction)
        {
            float e, es, pl;
            Expand(kelvin, pascal, rh, suction, out e, out es, out pl);
            return es > 0f ? e / es : 0f;
        }

        public static float CondensedWater(float kelvin, float pascal, float rh, float suction)
        {
            float e, es, pl;
            Expand(kelvin, pascal, rh, suction, out e, out es, out pl);
            return e > es && pl > 0f ? 622f * (e - es) / pl : 0f;
        }

        static void Expand(float kelvin, float pascal, float rh, float suction, out float e, out float es, out float pLocal)
        {
            e = es = pLocal = 0f;
            if (pascal <= 1f || kelvin <= 100f) return;
            float ratio = Mathf.Clamp(1f - suction / pascal, 0.3f, 1f);
            float tLocal = kelvin * Mathf.Pow(ratio, Kappa);
            pLocal = pascal * ratio;
            e = rh * SaturationPressure(kelvin) * ratio;
            es = SaturationPressure(tLocal);
        }

        // 0..1: how dense the vapor looks with this much condensed water, g/kg.
        public static float Density(float water)
        {
            return Mathf.SmoothStep(0f, 1f, (water - OnsetWater) / (FullWater - OnsetWater));
        }

        // Suction on the lifting side at chord fraction x (0 leading edge, 1 trailing edge), Pa,
        // for a section carrying a mean pressure difference `loading` at dynamic pressure q.
        // Thin-airfoil theory: up to its ideal lift coefficient the camber carries the lift, spread
        // along the chord; the rest comes from angle of attack and piles into a leading-edge peak,
        // sqrt((1 - x) / x). So a slow, high-alpha wing has a sharper peak than the same load
        // carried fast. Prandtl-Glauert sharpens only the peak's excess over its mean.
        public const float IdealCL = 0.4f;    // typical light camber; KSP's stock wings are near flat
        // The peak cannot grow without limit: past a peak pressure coefficient of a few the
        // boundary layer separates and the peak collapses. KSP wings carry far more lift than real
        // ones, so this caps it (real airfoils reach Cp_min of -3 to -6). Scaled by Prandtl-Glauert
        // like the rest of the peak.
        public static float MaxPeakCp = 3.5f;
        // The second ceiling, which binds at speed: past a local Mach of about 1.3-1.4 a shock
        // separates the flow, so the deepest suction is the isentropic drop from flight Mach to
        // that local Mach:
        // Cp_max = (1 - ((1 + 0.2 M^2) / (1 + 0.2 Ml^2))^3.5) / (0.7 M^2)
        // It falls with Mach (5.8 at M 0.4, 3.4 at 0.5, 2.3 at 0.6). Slow flight keeps the stall
        // cap.
        public static float MaxLocalMach = 1.35f;

        // Deepest suction coefficient the section can carry at this flight Mach, computed once per
        // step.
        // Stock: the stall ceiling alone, scaled by Prandtl-Glauert; stock lift is not tied to a
        // real angle of attack, and the onset was tuned against this cap.
        // FAR: the lower of that and the shock ceiling, since FAR flies real angles of attack.
        public static float PeakCpCap(float mach, bool far)
        {
            return far ? PeakCpCapFAR(mach) : PeakCpCapStock(mach);
        }

        public static float PeakCpCapStock(float mach)
        {
            return MaxPeakCp * PrandtlGlauert(mach);
        }

        public static float PeakCpCapFAR(float mach)
        {
            float stallCap = PeakCpCapStock(mach);
            if (mach < 0.05f) return stallCap;
            float m2 = mach * mach;
            float ratio = Mathf.Pow((1f + 0.2f * m2) / (1f + 0.2f * MaxLocalMach * MaxLocalMach), 3.5f);
            float shockCap = (1f - ratio) / (0.7f * m2);
            return Mathf.Max(0f, Mathf.Min(stallCap, shockCap));
        }
        const float NoseOffset = 0.05f;
        static float peakMean = -1f;

        public static float Suction(float x, float loading, float q, float mach)
        {
            return Suction(CamberShape(x), PeakShape(x), loading, 0f, q, PrandtlGlauert(mach), PeakCpCapStock(mach));
        }

        // With part of the loading carried by a deflected flap, aileron or elevator behind the
        // leading edge: a flap deflection is a camber change, spread along the chord, not an angle
        // of attack feeding the leading-edge peak. Loadings are signed toward the suction side of
        // the whole section; `flapLoading` may be negative.
        public static float Suction(float camberShape, float peakShape, float leadLoading, float flapLoading, float q, float pg, float cpCap)
        {
            float cl = leadLoading / Mathf.Max(q, 1f), clFlap = flapLoading / Mathf.Max(q, 1f);
            float camber = Mathf.Clamp(cl, 0f, IdealCL), alpha = Mathf.Max(0f, cl - IdealCL);
            float peak = Mathf.Max(0f, 1f + (peakShape - 1f) * pg);
            float cp = Mathf.Max(0f, SuctionShare * ((camber + clFlap) * camberShape + alpha * peak));
            return q * Mathf.Min(cp, cpCap);
        }

        // The per-point shapes depend only on geometry, so they are computed once per part. A
        // section's loading fixes two terms for the strip, and each sample adds its own shapes: cp
        // = camberTerm * camberShape + alphaTerm * peakAdjust, where peakAdjust = max(0, 1 +
        // (peakShape - 1) * pg). Identical to Suction() above.
        public static void StripTerms(float leadLoading, float flapLoading, float q, out float camberTerm, out float alphaTerm)
        {
            float cl = leadLoading / Mathf.Max(q, 1f), clFlap = flapLoading / Mathf.Max(q, 1f);
            camberTerm = SuctionShare * (Mathf.Clamp(cl, 0f, IdealCL) + clFlap);
            alphaTerm = SuctionShare * Mathf.Max(0f, cl - IdealCL);
        }

        public static float PeakAdjust(float peakShape, float pg)
        {
            return Mathf.Max(0f, 1f + (peakShape - 1f) * pg);
        }

        public static float SuctionFromTerms(float camberTerm, float alphaTerm, float camberShape, float peakAdjust, float q, float cpCap)
        {
            float cp = camberTerm * camberShape + alphaTerm * peakAdjust;
            return cp <= 0f ? 0f : q * Mathf.Min(cp, cpCap);
        }

        public static float CamberShape(float x)
        {
            x = Mathf.Clamp01(x);
            return (8f / Mathf.PI) * Mathf.Sqrt(x * (1f - x));   // mean 1
        }

        public static float PeakShape(float x)
        {
            if (peakMean < 0f)
            {
                double sum = 0; const int n = 2000;
                for (int i = 0; i < n; i++) { double xi = (i + 0.5) / n; sum += System.Math.Sqrt((1 - xi) / (xi + NoseOffset)); }
                peakMean = (float)(sum / n);
            }
            x = Mathf.Clamp01(x);
            return Mathf.Sqrt((1f - x) / (x + NoseOffset)) / peakMean;   // mean 1
        }

        // 1 / sqrt(1 - M^2), held finite near Mach 1 and dropped supersonically (the flow over a
        // supersonic wing no longer has a subsonic suction peak).
        public static float PrandtlGlauert(float mach)
        {
            if (mach >= 1.05f) return 1f;
            float m = Mathf.Min(mach, 0.95f);
            float pg = 1f / Mathf.Sqrt(1f - m * m);
            return mach > 0.95f ? Mathf.Lerp(pg, 1f, (mach - 0.95f) / 0.1f) : pg;
        }
    }

    // CondensedWater as a function of suction alone, for one ambient state, worked out on a table
    // once per step.
    public class CondensationTable
    {
        const int N = 256;
        readonly float[] water = new float[N + 1], dens = new float[N + 1];
        float kelvin = -1f, pascal = -1f, rh = -1f, perSuction;

        public void Build(float kelvin, float pascal, float rh)
        {
            if (Mathf.Abs(kelvin - this.kelvin) < 0.01f && Mathf.Abs(pascal - this.pascal) < 1f && Mathf.Abs(rh - this.rh) < 1e-4f) return;
            this.kelvin = kelvin; this.pascal = pascal; this.rh = rh;
            float max = 0.7f * Mathf.Max(pascal, 1f);   // Condensation clamps expansion at 0.3 p
            perSuction = N / max;
            for (int i = 0; i <= N; i++)
            {
                water[i] = Condensation.CondensedWater(kelvin, pascal, rh, max * i / N);
                dens[i] = Condensation.Density(water[i]);
            }
        }

        public float Water(float suction) { return Lookup(water, suction); }

        // How dense the vapor looks at this suction: Condensation.Density of the condensed water.
        public float Density(float suction) { return Lookup(dens, suction); }

        float Lookup(float[] t, float suction)
        {
            float x = suction * perSuction;
            if (x <= 0f) return t[0];
            if (x >= N) return t[N];
            int i = (int)x;
            return t[i] + (t[i + 1] - t[i]) * (x - i);
        }
    }
}
