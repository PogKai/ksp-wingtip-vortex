using System;
using VortexVapor.Aeronautic;

namespace VortexVapor
{
    // KSP calibration for the upstream aerodynamic field model.
    public static class KspBaseline
    {
        public static double Humidity = 0.9, OffsetK = 5;
        // apps/docs/examples/wing-vapor.tsx: its hard pull's humidity, and +10 K.
        public const double ExampleHumidity = 0.9, ExampleOffsetK = 10;
        // The sheet's height against upstream's at Depth 100% (upstream's is a thin film on KSP-sized wings).
        public static double BaseReach = 2.9;
        // Increase droplet extinction while retaining a translucent sheet.
        public static double Density = 1.7;
        public static double FieldSpeed = 160, OnsetOffsetG = 2.2, LoadGain = 1.4;
        // Every craft read at this wing loading, so a g works the wing alike on all of them.
        public static double FieldWingLoading = 265;
        public static bool LatticeLoading = false;
        // How strongly the station's chord sets its loading (chord / mean chord, to this power): 1
        // starves a strongly tapered wing's outer panels (Su-47: 7 m root, 1.4 m tip).
        public static double ChordPower = 0.5;
        // How much of the measured sideslip and roll rate the field reads (1 is upstream's).
        public static double SlipShare = 1, RollShare = 1;
        public const double SpeedReachMin = 0.8, SpeedReachMax = 1.15;
        // Keep the high-alpha vortex visible through a low-speed cobra.
        public static double FadeLow = 18, FadeHigh = 42;
        public const double AirfoilThickness = 0.06;
        // the sheet spreads over the fuselage from CoverFromG, all of it by CoverFullG.
        public static double CoverFromG = 3, CoverFullG = 6.5;
        // a canard's sheet is held back below CanardFromG, at full strength from CanardFullG.
        public static double CanardFromG = 3, CanardFullG = 6.5;
        // the wing's sheet from SheetFromG, full by SheetFullG; the leading-edge vortex as a
        // cord from RopeFromG, RopeGain times the condensing deficit by RopeFullG.
        public static double SheetFromG = 3, SheetFullG = 6.5;
        public static double RopeFromG = 3, RopeFullG = 6.5, RopeGain = 10, RopeRadiusM = 0.14;

        // No vapor below GateFromG of effective load, all of it by GateFullG; the AoA floor
        // supports a bounded localized high-alpha vortex.
        public static double GateFromG = 1.8, GateFullG = 2.6;

        public static double LoadGate(double loadG)
        {
            return Step(GateFromG, GateFullG, Math.Max(loadG, 0));
        }

        // A thin film reads as a veil: the droplets' extinction runs from OpacityMin of its full value at
        // OpacityFromG of effective load to all of it at OpacityFullG.
        public static double OpacityMin = 0.40, OpacityFromG = 3, OpacityFullG = 6;

        public static double LoadOpacity(double loadG)
        {
            return OpacityMin + (1 - OpacityMin) * Step(OpacityFromG, OpacityFullG, Math.Abs(loadG));
        }

        public static MoistAir Air(double altitudeM)
        {
            return MoistAir.FromAltitude(altitudeM, Humidity, OffsetK);
        }

        // Loading rescaled by chord over mean chord, so wings fog from the root out.
        public static void Prepare(WingShape shape)
        {
            double area = 0, span = 0;
            for (int k = 1; k < WingShape.Stations; k++)
            {
                double ds = shape.StationSpan(k) - shape.StationSpan(k - 1);
                area += ((double)shape.ChordM[k - 1] + shape.ChordM[k]) / 2 * ds;
                span += ds;
            }
            double mean = span > 0 ? area / span : 1;
            for (int k = 0; k < WingShape.Stations; k++)
            {
                // stock lift per metre of chord is uniform; lattice loading only when LatticeLoading.
                double lattice = LatticeLoading ? shape.Loading[k] : 1;
                shape.Loading[k] = (float)(lattice * Math.Pow(shape.ChordM[k] / Math.Max(mean, 1e-3), ChordPower));
                // One airfoil thickness ratio (KSP wing parts are constant-thickness slabs).
                shape.Thickness[k] = (float)AirfoilThickness;
            }
        }

        // AoA augments localized vortices, never broad-sheet eligibility.
        public static double AoaFromDeg = 12, AoaFullDeg = 25, AoaGain = 1.15;
        // High alpha supplies a bounded vortex floor through a cobra, with a low-speed fade.
        public static double AoaFloorG = 6.5, FloorFromDeg = 18, FloorFullDeg = 30, SeparatedFromDeg = 100, SeparatedDeg = 120;
        public static double FloorSpeedFrom = 16, FloorSpeedFull = 40;

        // A stalled wing keeps a bounded veil under a strong leading-edge vortex, never the loaded sheet.
        // Zero below PostStallFromDeg and past SeparatedDeg, gear down, or under ~18 m/s.
        public static double PostStallShare = 0.4, PostStallFromDeg = 26, PostStallFullDeg = 38, PostStallSpeedFrom = 18, PostStallSpeedFull = 45;

        public static double PostStallSheet(double alphaRad, double speed, double landing = 0)
        {
            double a = alphaRad * 180 / Math.PI;
            if (a <= 0) return 0;
            double separated = 1 - Step(SeparatedFromDeg, SeparatedDeg, a);
            return PostStallShare * Step(PostStallFromDeg, PostStallFullDeg, a) * separated * Step(PostStallSpeedFrom, PostStallSpeedFull, speed) * (1 - Math.Min(Math.Max(landing, 0), 1));
        }

        // Gear limits the high-alpha vortex floor without banning humid approaches.
        public static double LandingFloorG = 3.4, GearBlendS = 1.5;

        // Local vortex eligibility; the broad sheet uses measured load separately.
        public static double EquivalentLoad(double loadG, double alphaRad, double landing = 0, double speed = 0)
        {
            double a = alphaRad * 180 / Math.PI;
            if (loadG < 0) return 0;
            if (a <= 0) return loadG;
            double boosted = loadG * (1 + (AoaGain - 1) * Step(AoaFromDeg, AoaFullDeg, a));
            double floor = AoaFloorG * Step(FloorFromDeg, FloorFullDeg, a) * (1 - Step(SeparatedFromDeg, SeparatedDeg, a));
            floor += (Math.Min(floor, LandingFloorG) - floor) * Math.Min(Math.Max(landing, 0), 1);
            floor *= speed > 0 ? Step(FloorSpeedFrom, FloorSpeedFull, speed) : 1;
            return Math.Max(boosted, floor);
        }

        // `landing` eased toward the gear's state over GearBlendS.
        public static double EaseLanding(double landing, bool gearDown, double dt)
        {
            return landing + ((gearDown ? 1 : 0) - landing) * (1 - Math.Exp(-Math.Max(dt, 0) / GearBlendS));
        }

        static double Step(double from, double to, double x)
        {
            double t = Math.Min(Math.Max((x - from) / (to - from), 0), 1);
            return t * t * (3 - 2 * t);
        }

        // The load the field is read at, from the measured one (g along the craft's up axis).
        public static double FieldLoad(double loadG)
        {
            return Math.Sign(loadG) * Math.Max(Math.Abs(loadG) - OnsetOffsetG, 0) * LoadGain;
        }

        public static VaporFlight Flight(VaporAirframe airframe, MoistAir air, double loadG, double beta, double roll)
        {
            return new VaporFlight
            {
                AirspeedMPerS = FieldSpeed,
                AngleOfAttackRad = AngleForLoad(airframe, FieldLoad(loadG) * FieldWingLoading * Aerodynamics.PlanformOf(airframe).AreaM2 / Math.Max(airframe.MassKg, 1), FieldSpeed, air),
                SideslipRad = beta * SlipShare,
                RollRateRadPerS = roll * RollShare,
            };
        }

        public static double SheetSpeed(double speed) { return Step(80, 140, speed); }

        public static double SheetStrength(double measuredLoadG, double speed)
        {
            return Step(SheetFromG, SheetFullG, Math.Max(measuredLoadG, 0)) * SheetSpeed(speed);
        }

        public static double SheetCut(double loadG)
        {
            double t = Math.Min(Math.Max((Math.Abs(loadG) - SheetFromG) / (SheetFullG - SheetFromG), 0), 1);
            return 1 - t * t * (3 - 2 * t);
        }

        public static double RopeStrength(double loadG)
        {
            // Positive g only: a push loads the wing's underside.
            double t = Math.Min(Math.Max((loadG - RopeFromG) / (RopeFullG - RopeFromG), 0), 1);
            return RopeGain * t * t * (3 - 2 * t);
        }

        public static double CanardCut(double loadG)
        {
            double t = Math.Min(Math.Max((Math.Abs(loadG) - CanardFromG) / (CanardFullG - CanardFromG), 0), 1);
            return 1 - t * t * (3 - 2 * t);
        }

        public static double BodyCover(double loadG)
        {
            double t = Math.Min(Math.Max((Math.Abs(loadG) - CoverFromG) / (CoverFullG - CoverFromG), 0), 1);
            return t * t * (3 - 2 * t);
        }

        // How high the sheet reaches for the speed, against FieldSpeed's.
        public static double SpeedReach(double speed)
        {
            return Math.Min(Math.Max(Math.Sqrt(Math.Max(speed, 0) / FieldSpeed), SpeedReachMin), SpeedReachMax);
        }

        public static double Fade(double speed)
        {
            double t = Math.Min(Math.Max((speed - FadeLow) / (FadeHigh - FadeLow), 0), 1);
            return t * t * (3 - 2 * t);
        }

        // Aerodynamics.AngleOfAttackForLoad without its per-call delegate (it runs every frame).
        public static double AngleForLoad(VaporAirframe a, double loadFactor, double airspeed, MoistAir air)
        {
            double wanted = Math.Abs(loadFactor);
            if (wanted < 1e-6) return 0;
            double top = 0, best = 0;
            for (int degrees = 1; degrees <= 50; degrees++)
            {
                double alpha = degrees * Math.PI / 180;
                double load = Load(a, alpha, airspeed, air);
                if (load > best) { best = load; top = alpha; }
            }
            double sign = loadFactor < 0 ? -1 : 1;
            if (wanted >= best) return sign * top;
            double low = 0, high = top;
            for (int i = 0; i < 30; i++)
            {
                double middle = (low + high) / 2;
                if (Load(a, middle, airspeed, air) < wanted) low = middle; else high = middle;
            }
            return sign * (low + high) / 2;
        }

        static double Load(VaporAirframe a, double alpha, double airspeed, MoistAir air)
        {
            return Aerodynamics.State(a, new VaporFlight { AirspeedMPerS = airspeed, AngleOfAttackRad = alpha }, air).LoadFactor;
        }
    }
}
