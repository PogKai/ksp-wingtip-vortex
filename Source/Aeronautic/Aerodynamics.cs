// Ported from aeronautic (github.com/Romainlg29/aeronautic @ ca535855f7), packages/wing-vapor/src/aerodynamics.ts.
// MIT License, (c) 2026 Romain Le Gall; see THIRD-PARTY-NOTICES.md.
using System;

namespace VortexVapor.Aeronautic
{
    // The lift from the planform (DATCOM's slope, Polhamus's vortex lift), its circulation, and
    // where the leading-edge vortices burst.
    public static class Aerodynamics
    {
        const double G0 = 9.80665;
        const double SectionEfficiency = 0.95;
        const double LiftSlopeMaxMach = 0.9;
        public const double VortexSpacing = Math.PI / 4;
        public const double LeadingEdgeVortexSpan = 0.7;
        public const double MinBeta = 0.15;

        public struct Planform
        {
            public double AreaM2, AspectRatio, Taper, MeanChordM;
        }

        public static Planform PlanformOf(VaporAirframe a)
        {
            double area = a.SpanM * (a.RootChordM + a.TipChordM) / 2;
            return new Planform
            {
                AreaM2 = area,
                AspectRatio = a.SpanM * a.SpanM / Math.Max(area, 1e-6),
                Taper = a.TipChordM / Math.Max(a.RootChordM, 1e-6),
                MeanChordM = area / Math.Max(a.SpanM, 1e-6),
            };
        }

        public static double SweepAt(VaporAirframe a, double fraction)
        {
            Planform p = PlanformOf(a);
            return Math.Atan(Math.Tan(a.LeadingEdgeSweepRad) - 4 * fraction * (1 - p.Taper) / (p.AspectRatio * (1 + p.Taper)));
        }

        public static double LiftSlope(VaporAirframe a, double mach)
        {
            double ar = PlanformOf(a).AspectRatio;
            double m = Math.Min(Math.Max(mach, 0), LiftSlopeMaxMach);
            double beta = Math.Sqrt(1 - m * m);
            double halfChord = Math.Tan(SweepAt(a, 0.5));
            return 2 * Math.PI * ar
                / (2 + Math.Sqrt(ar * ar * beta * beta / (SectionEfficiency * SectionEfficiency) * (1 + halfChord * halfChord / (beta * beta)) + 4));
        }

        public static void Polhamus(VaporAirframe a, double mach, out double potential, out double vortex)
        {
            double ar = PlanformOf(a).AspectRatio;
            potential = LiftSlope(a, mach);
            vortex = Math.Max(potential - potential * potential / (Math.PI * ar), 0)
                / Math.Max(Math.Cos(a.LeadingEdgeSweepRad), 0.05)
                * Math.Min(Math.Max(a.LeadingEdgeSharpness, 0), 1);
        }

        public static void BreakdownAngles(double sweepRad, out double trailingEdge, out double apex)
        {
            double sweepDeg = sweepRad * 180 / Math.PI;
            double te = Math.Max(1.3 * (sweepDeg - 47), 3);
            trailingEdge = te * Math.PI / 180;
            apex = (te + 15) * Math.PI / 180;
        }

        public static double BreakdownFraction(VaporAirframe a, double angleOfAttackRad)
        {
            double te, apex;
            BreakdownAngles(a.LeadingEdgeSweepRad, out te, out apex);
            return Math.Min(Math.Max((apex - angleOfAttackRad) / (apex - te), 0), 1);
        }

        public static void LiftCoefficient(VaporAirframe a, double alpha, double mach, out double potential, out double vortex)
        {
            double kp, kv;
            Polhamus(a, mach, out kp, out kv);
            double sin = Math.Sin(alpha), cos = Math.Cos(alpha);
            potential = kp * sin * cos * cos;
            double whole = 0.4 + 0.6 * BreakdownFraction(a, Math.Abs(alpha));
            vortex = kv * sin * Math.Abs(sin) * cos * whole;
        }

        public struct FlightState
        {
            public double Mach, DynamicPressurePa;
            public double LiftCoefficient, PotentialLiftCoefficient, VortexLiftCoefficient;
            public double LiftN, LoadFactor, TipCirculation, LeadingEdgeCirculation, Breakdown;
        }

        public static FlightState State(VaporAirframe a, VaporFlight flight, MoistAir air)
        {
            double speed = Math.Max(flight.AirspeedMPerS, 0);
            double mach = speed / air.SoundMPerS;
            double q = 0.5 * air.DensityKgPerM3 * speed * speed;
            double area = PlanformOf(a).AreaM2;
            double potential, vortex;
            LiftCoefficient(a, flight.AngleOfAttackRad, mach, out potential, out vortex);
            double total = potential + vortex;
            double lift = q * area * total;
            double span = Math.Max(a.SpanM, 1e-3);
            return new FlightState
            {
                Mach = mach,
                DynamicPressurePa = q,
                LiftCoefficient = total,
                PotentialLiftCoefficient = potential,
                VortexLiftCoefficient = vortex,
                LiftN = lift,
                LoadFactor = lift / (Math.Max(a.MassKg, 1) * G0),
                TipCirculation = speed > 0 ? Math.Abs(lift) / (air.DensityKgPerM3 * speed * VortexSpacing * span) : 0,
                LeadingEdgeCirculation = speed * area * Math.Max(vortex, 0) / (2 * LeadingEdgeVortexSpan * span),
                Breakdown = BreakdownFraction(a, Math.Abs(flight.AngleOfAttackRad)),
            };
        }

        // The angle of attack that pulls a load factor; past the stall, the stall's angle.
        public static double AngleOfAttackForLoad(VaporAirframe a, double loadFactor, double airspeed, MoistAir air)
        {
            Func<double, double> liftOf = alpha => State(a, new VaporFlight { AirspeedMPerS = airspeed, AngleOfAttackRad = alpha }, air).LoadFactor;
            double sign = Math.Sign(loadFactor);
            if (sign == 0) sign = 1;
            double wanted = Math.Abs(loadFactor);
            double top = 0, best = 0;
            for (int degrees = 1; degrees <= 50; degrees++)
            {
                double alpha = degrees * Math.PI / 180;
                double load = liftOf(alpha);
                if (load > best) { best = load; top = alpha; }
            }
            if (wanted >= best) return sign * top;
            double low = 0, high = top;
            for (int i = 0; i < 40; i++)
            {
                double middle = (low + high) / 2;
                if (liftOf(middle) < wanted) low = middle; else high = middle;
            }
            return sign * (low + high) / 2;
        }

        public static double KarmanTsien(double incompressible, double mach)
        {
            double beta = CompressibilityBeta(mach);
            double denominator = beta + mach * mach / (1 + beta) * (incompressible / 2);
            return incompressible / Math.Max(denominator, beta * 0.5);
        }

        public static double CompressibilityBeta(double mach)
        {
            return Math.Max(Math.Sqrt(Math.Abs(1 - mach * mach)), MinBeta);
        }

        public static double CriticalPressure(double mach)
        {
            double m2 = Math.Max(mach * mach, 1e-6);
            double g = Atmosphere.AirGamma;
            return 2 / (g * m2) * (Math.Pow((2 + (g - 1) * m2) / (g + 1), g / (g - 1)) - 1);
        }

        public static double PressureRatio(double coefficient, double mach)
        {
            return 1 + Atmosphere.AirGamma / 2 * mach * mach * coefficient;
        }
    }
}
