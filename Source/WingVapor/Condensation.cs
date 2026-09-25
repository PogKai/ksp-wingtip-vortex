using UnityEngine;

namespace VortexVapor
{
    // When does the air over a wing turn to cloud? No KSP types, so it can be checked offline.
    //
    // Air flowing over the suction side of a wing drops in pressure, and it does so too fast to
    // exchange heat, so it expands adiabatically and cools: T_local = T (p_local / p)^((g-1)/g).
    // Its water vapour keeps the same share of the pressure (the mixing ratio is fixed), so the
    // vapour's partial pressure falls with p_local, but the saturation pressure falls much faster
    // with the temperature. Where the vapour ends up above saturation it condenses. That is the
    // whole effect: nothing here knows about g-load, Mach or aircraft type. A fighter in a hard pull
    // condenses because its loading, and so its suction, is huge; an airliner condenses only in
    // very humid air; a transonic wing condenses because its suction peak sharpens (Prandtl-Glauert).
    public static class Condensation
    {
        const float Kappa = 0.2857f;   // (gamma - 1) / gamma for air

        // Share of a wing's pressure difference carried by suction on the upper surface, rather
        // than overpressure underneath. About two thirds on a typical airfoil at moderate lift.
        public const float SuctionShare = 0.67f;

        // Visibility goes by how much water actually condenses, not by whether the air is just
        // past saturation: a trace of droplets is invisible, and the fog thickens with the
        // condensed mass. g of liquid water per kg of air at first sight, and at full density.
        // TUNED by eye against the reference, not measured.
        public const float OnsetWater = 0.4f, FullWater = 2.5f;

        // Saturation vapour pressure over liquid water, Pa (Magnus; also used supercooled).
        public static float SaturationPressure(float kelvin)
        {
            float c = kelvin - 273.15f;
            return 610.94f * Mathf.Exp(17.625f * c / (c + 243.04f));
        }

        // KSP has no humidity, so the air's moisture is a PLACEHOLDER, given as a dew-point spread
        // (how far the air must cool before it condenses) rather than a relative humidity. The
        // spread is what decides whether a wing fogs: over-wing condensation is seen when the
        // temperature is within a couple of degrees of the dew point, i.e. on a humid day, and
        // a fixed relative humidity turns into a muggy tropical day in KSP's hot lowlands (KSC
        // runs 305-308 K, where 66% is a 7 K spread). A fixed spread keeps the dew point
        // tracking the temperature, as it does through a real day.
        //
        // Ordinary day: 10 K in the humid boundary layer (air denser than 62% of the body's
        // sea-level density, WingtipVortex's humidDensityMin, so both mods agree on where the
        // air is moist), 16 K above it. Cooling a wing's air 10 K takes about an 11% pressure
        // drop, which only a hard-pulled or near-transonic wing produces.
        public static float SurfaceSpread = 12f, FreeAirSpread = 18f;   // K; settable for the offline checks
        public const float BoundaryLayerDensity = 0.62f;

        // Where the vapor can exist at all: a HARD limit, by density relative to the body's
        // sea-level density (so it means the same on every body). Wing vapor is a lower-troposphere
        // effect: the air aloft holds almost no water, and a wing in thin air makes only a thin
        // suction. In air thinner than this the mod does no work at all: no vapor, no part reads,
        // no particle system, no log. (v0.4.8 ran whenever the surface speed was 5 m/s or more,
        // which in orbit is always: a station of hundreds of parts was read every step and the log
        // was written every second, and vapor still formed at 55 km.) About 14.3 km on
        // Kerbin (measured from a flight log; the verbose log records each crossing).
        public const float CutoffRatio = 0.10f;
        // Coming back down, the mod resumes at the cutoff exactly; going up, it stops a little
        // past it, so a craft hovering on the limit does not clear and rebuild its vapor
        // measurements every few steps.
        public const float CutoffHysteresis = 0.9f;

        public static float RelativeHumidity(float rho, float rhoSeaLevel, float kelvin)
        {
            float depth = Mathf.Clamp01((rho / Mathf.Max(rhoSeaLevel, 1e-4f) - BoundaryLayerDensity)
                                        / (1f - BoundaryLayerDensity));
            float spread = Mathf.Lerp(FreeAirSpread, SurfaceSpread, depth);
            return SaturationPressure(kelvin - spread) / SaturationPressure(kelvin);
        }

        // Supersaturation e / e_sat of air that started at (T, p, RH) and has been sucked down by
        // `suction` Pa; and the water that condenses out of it, g per kg of air. The vapour's share
        // of the pressure is fixed, so what exceeds saturation at the new pressure and temperature
        // condenses (the latent heat it releases is neglected, which overstates it slightly).
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
        //
        // Thin-airfoil theory splits a section's lift in two. Up to its ideal lift coefficient
        // the camber carries it, spread smoothly along the chord, peaking mid-chord. Everything
        // above that comes from angle of attack and piles into a suction peak at the leading
        // edge, as sqrt((1 - x) / x) (the offset rounds off the singularity, as a real nose radius
        // does). So the peak grows faster than the lift: a wing working near its limit, slow and
        // hard-pulled, has a far sharper peak than the same load carried fast at a low lift
        // coefficient. That is why vapor, like WingtipVortex's trails, comes most easily on a
        // slow, high-alpha wing and needs more g the faster the aircraft goes. Compressibility
        // (Prandtl-Glauert) sharpens the peak further toward Mach 1 without changing the lift,
        // which stock's figure already includes, so it scales only the peak's excess over its
        // mean.
        public const float IdealCL = 0.4f;    // typical light camber; KSP's stock wings are near flat
        // The suction peak cannot grow without limit: past a peak pressure coefficient of a few,
        // the boundary layer behind it separates and the wing stalls, and the peak collapses
        // rather than deepening. KSP lets a wing carry far more lift than a real one (5 g at
        // 70 m/s in flight logs), so without this a slow, hard-pulled wing got an impossible
        // suction peak. Real airfoils reach Cp_min of about -3 to -6 at maximum lift; the
        // incompressible limit is scaled by Prandtl-Glauert like the rest of the peak.
        public static float MaxPeakCp = 3.5f;
        // The second ceiling, and the one that binds at speed. The air over the suction peak
        // speeds up as the pressure falls, and past a local Mach of about 1.3-1.4 it ends in a
        // shock strong enough to separate the boundary layer, which collapses the peak. So the
        // deepest suction is the isentropic pressure drop from flight Mach to that local Mach:
        //   Cp_max = (1 - ((1 + 0.2 M^2) / (1 + 0.2 Ml^2))^3.5) / (0.7 M^2)
        // It FALLS with Mach (5.8 at M 0.4, 3.4 at M 0.5, 2.3 at M 0.6), where the stall cap
        // scaled by Prandtl-Glauert rises (3.8, 4.0, 4.4). Using only that one let a 5 g pull at
        // M 0.6 reach a pressure drop larger than the whole ambient pressure and condense nearly
        // all the water in the air (20 g/kg in a flight log). Slow flight keeps the stall cap.
        public static float MaxLocalMach = 1.35f;

        // Deepest suction coefficient the section can carry at this flight Mach: the lower of the
        // stall ceiling and the shock ceiling. Computed once per step.
        public static float PeakCpCap(float mach)
        {
            float stallCap = MaxPeakCp * PrandtlGlauert(mach);
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
            return Suction(CamberShape(x), PeakShape(x), loading, 0f, q, PrandtlGlauert(mach), PeakCpCap(mach));
        }

        // With part of the loading carried by a deflected flap, aileron or elevator behind the
        // leading edge. Thin-airfoil theory treats a flap deflection as a change of camber: its
        // lift spreads along the chord (peaking at the hinge) rather than feeding the
        // leading-edge suction peak the way angle of attack does. So a hard-deflected control
        // surface loads its section without sharpening the peak where vapor forms first.
        // Loadings are signed toward the suction side of the whole section; `flapLoading` may be
        // negative (a flap working against the section).
        public static float Suction(float camberShape, float peakShape, float leadLoading, float flapLoading, float q, float pg, float cpCap)
        {
            float cl = leadLoading / Mathf.Max(q, 1f), clFlap = flapLoading / Mathf.Max(q, 1f);
            float camber = Mathf.Clamp(cl, 0f, IdealCL), alpha = Mathf.Max(0f, cl - IdealCL);
            float peak = Mathf.Max(0f, 1f + (peakShape - 1f) * pg);
            float cp = Mathf.Max(0f, SuctionShare * ((camber + clFlap) * camberShape + alpha * peak));
            return q * Mathf.Min(cp, cpCap);
        }

        // The per-point shapes depend only on geometry, so they are computed once per part and
        // the per-step work is a few multiplies.
        // The same physics, split in two so the per-sample work is a few multiplies. A section's
        // loading fixes two terms for the whole strip; each sample then adds only its own shapes:
        //   cp = camberTerm * camberShape + alphaTerm * peakAdjust
        // where peakAdjust = max(0, 1 + (peakShape - 1) * pg) is Prandtl-Glauert's sharpening of
        // the peak's excess over its mean. Identical to Suction(...) above.
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

    // CondensedWater as a function of suction alone, for one ambient state: the ambient air is the
    // same for every point of the airframe in a step, so the exponentials are worked out on a
    // table once per step rather than once per point.
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
