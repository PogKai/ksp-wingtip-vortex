// Ported from aeronautic (github.com/Romainlg29/aeronautic @ ca535855f7), packages/wing-vapor/src/vapor-field.ts.
// MIT License, (c) 2026 Romain Le Gall; see THIRD-PARTY-NOTICES.md.
using System;
using System.Runtime.CompilerServices;

namespace VortexVapor.Aeronautic
{
    // Everything the shader needs; four additive deficits. Frame: x aft, y up, z out (folded).
    public class VaporField
    {
        public double Mach, SpeedMPerS, CosAlpha, SinAlpha, FlowZ, RollBias, SlipBias, VortexK, SaturationDeficit;
        public double SemispanM, RootSpanM, TipLeadingM, TipChordM, TipHeightM;
        // how far behind the root's trailing edge the sheet thins out over the body.
        public double BodyTrailM;
        // where the sheet starts, and its share over the fuselage.
        public double InnerSpanM, BodyCover;
        // a flat body's own cloud, its deficit at the surface, how high it
        // reaches and how far it can fog.
        public double BodyDeficitAtSurface, BodyReachM, BodyBoundM, BodyFloorBlendM;
        // a canard's own sheet, as a trapezoid from its root to its tip,
        // loaded and reaching as the wing does so the two read as one.
        public double CanardRootSpanM, CanardSemispanM, CanardRootLeadingM, CanardRootChordM, CanardTipLeadingM, CanardTipChordM,
            CanardRootHeightM, CanardTipHeightM, CanardSection, CanardBoundM;
        public double SectionLiftM, ReachChordM, Separation, Cos2Sweep, NormalMach;
        public double TipCirculation, TipCore2M2, TipStart2M2, TipRollupM, TipGrowthM, TipDescent, TipReachM, TipBoundM, TrailSpacingM;
        public double SecondSemispanM, SecondTipLeadingM, SecondTipChordM, SecondTipHeightM, SecondCirculation, SecondCore2M2,
            SecondStart2M2, SecondRollupM, SecondGrowthM, SecondDescent, SecondReachM, SecondBoundM, SecondSpacingM;
        public double EdgeApexM, EdgeGradient, EdgeLengthM, EdgeCore, EdgeHeight, EdgeBurstM, EdgeBoundM;
        // the cord's radius, its deficit and how far it runs from the apex (0: upstream's vortex).
        public double EdgeRopeM, EdgeRopeDeficit, EdgeRopeLengthM, EdgeRopeWalkM;
        // the slope the cord climbs to by the end of its strake, and how far aft of the edge the field reaches.
        public double EdgeRopeFlare, EdgeTrailM;
        public double RopeStartM, RopeSpanM, RopeHeightM, RopeSlopeY, RopeSlopeZ;
        public double StrakeApexM, StrakeEndM;
        public double ForeTopM, ForeStartM, ForeEndM, ForeEnabled;
        public double WingBoundM, EvaporationM;
        public double NoseM, BodyLengthM, BodyRadiusM, BodyHeightM, ConeStrength, ConeShock, ConeShockWidth, ConeFade, ConeBoundM;
        // Multiplier for the wing field's vertical reach.
        public double ReachScale = 1;
    }

    // The leading-edge vortex's path, Stations points evenly from where it starts to the end of the edge.
    public class EdgePath
    {
        public float[] SpanM = new float[WingShape.Stations], SurfaceM = new float[WingShape.Stations], ReachM = new float[WingShape.Stations];
    }

    public class VaporGeometry
    {
        public VaporField Field;
        public WingShape Shape;
        public EdgePath Path;
        public TrailPaths Trails, SecondTrails;
    }

    public class VaporState : VaporGeometry
    {
        public MoistAir Air;
        public Aerodynamics.FlightState Flight;
        public double[] BoundsMin = new double[3], BoundsMax = new double[3];
        public bool Visible;
    }

    // What the shader would otherwise work out per sample: what only the Mach number sets.
    public struct VaporConstants
    {
        public double WingScale, WingSonic, WingBeta, WingKt, WingCap, WingReach, WingTravel, WingSupersonic;
        public double ConeScale, ConeBeta, ConeKt, ConeCap, ConeReachM;
    }

    public static class VaporFieldModel
    {
        public const double CanardLoading = 1.2, TailLoading = 0.15;
        // a LERX whose apex stands at least this far ahead of the wing root's leading edge.
        const double LerxMinM = 0.3;
        const double MaxBias = 0.7;
        const double SideLoadingMin = 0.2, SideLoadingMax = 1.8;
        public const double BoundFloor = 0.003;
        const double MaxSecondShare = 0.4;
        public const double VortexEddyViscosity = 2e-4;
        public const double RollupCoefficient = 0.28;
        const double RollupSpansMin = 0.5, RollupSpansMax = 4;
        public const double TipStartCore = 0.05, TipStartShare = 0.4;
        public const double MaxTrailM = 400;
        public const double LeadingEdgeVortexHeight = 1;
        public const double EdgeSurface = 0.375;
        public const double BurstLength = 0.08, BurstSwell = 2;
        const double Degree = Math.PI / 180;
        public const double EvaporationS = 0.003;
        const double ReferenceDropletM = 1e-6;
        public const double MaxLocalMach = 1.4;
        public const double RootFade = 0.12, RootBlur = 1;
        // with the cover the sheet thins out over this far behind the trailing edge.
        public const double CoverTrailM = 1.2;
        public const double FrontRiseM = 0.1;
        // the cord runs RopeChords root chords from the apex, short of that by RopeBurstShare as
        // the burst moves forward with the angle of attack, then fades over RopeTrail of its length.
        public const double RopeChords = 0.35, RopeBurstShare = 0.4, RopeTrail = 0.2, RopeOut = 0.9;
        // The strake cord clears the skin, follows the edge, then breaks up just past it.
        public const double RopeStart = 0.02, RopeLift = 0.35, RopeClear = 0.7, RopeFlareDeg = 25, RopeFlareShare = 0.5, RopeBreak = 0.06;
        // under the cover the sheet stands FrontShare of its height at the leading edge, full by FrontGrowth chords.
        public const double FrontShare = 0.35, FrontGrowth = 0.45;

        // over a strake (LERX) the sheet and the body's cloud, deficit and height, build from nothing
        // StrakeQuiet of the way back from its apex to full where the wing starts: clear canopy.
        public const double StrakeQuiet = 0.3;
        public static double StrakeRamp(VaporField f, double x, double span)
        {
            double inside = RootInside(f, span);
            if (f.StrakeEndM <= f.StrakeApexM || inside <= 0) return 1;
            // the build starts further back toward the root, along the swept strake, not on one straight line.
            double share = Math.Min(Math.Max(span / Math.Max(f.RootSpanM, 1e-3), 0), 1);
            double length = f.StrakeEndM - f.StrakeApexM;
            double ramp = Smoothstep(f.StrakeApexM + (StrakeQuiet + StrakeSweepShare * share) * length, f.StrakeEndM, x);
            return 1 + (ramp - 1) * inside;
        }

        // Feather the root transition so it cannot form a straight wall.
        public const double RootBlendM = 0.6, StrakeSweepShare = 0.4;
        public static double RootInside(VaporField f, double span)
        {
            return 1 - Smoothstep(f.RootSpanM - RootBlendM, f.RootSpanM + RootBlendM, Math.Abs(span));
        }

        public static double FrontTaper(double along)
        {
            return FrontShare + (1 - FrontShare) * Smoothstep(0, FrontGrowth, along);
        }

        public static double ForeWeight(VaporField f, double x, double span)
        {
            if (f.ForeEnabled <= 0 || f.ForeEndM <= f.ForeStartM) return 0;
            return f.ForeEnabled * (1 - Smoothstep(f.ForeStartM, f.ForeEndM, x))
                * (1 - Smoothstep(f.RootSpanM, f.RootSpanM + 0.4, Math.Abs(span)));
        }

        public static double ForeMask(VaporField f, double x, double y, double span)
        {
            if (f.ForeEnabled <= 0) return 1;
            return 1 - ForeWeight(f, x, span) * Smoothstep(f.ForeTopM - 0.2, f.ForeTopM, y);
        }

        static double TrailFade(double xi, double chord, bool covered, VaporField f, double span)
        {
            return 1 - Smoothstep(1, 1 + (covered ? Trail(f, span) / chord : 0.06), xi);
        }

        // behind the wing the sheet thins out over CoverTrailM; over the body (inside the
        // root) it carries on to the body's tail, as an F-22's long root chord does on its own.
        static double Trail(VaporField f, double span)
        {
            return CoverTrailM + (f.BodyTrailM - CoverTrailM) * (1 - Smoothstep(f.RootSpanM, f.RootSpanM + 0.5, span));
        }
        public const double NoseRadius = 0.03, NoseReachChords = 0.3;
        // The reach builds up over a chord share, but over no more than these metres on a long root chord.
        public const double NoseBuildM = 0.6, FrontBuildM = 1;
        // Aft of the body the cloud's top slopes down: higher up it ends sooner.
        public const double BodyAftSlope = 3;
        public const double WingFieldHeight = 0.1;
        const double WingHeightMinBeta = 0.4;
        public const double ShockTravel = 0.85, ShockWidth = 0.015;
        public const double ConeStrengthFactor = 4, ConeReach = 0.25;
        public const double ConeShockSubsonic = 0.55, ConeShockSonic = 0.92, ConeShockSupersonic = 0.98;
        public const double SupersonicFadeLow = 1.03, SupersonicFadeHigh = 1.15;
        public const double ConeShockLean = 0.08, PocketGrowth = 0.03, ConeRecompression = 0.3, ConeShockThickness = 0.01, ConePocketLength = 0.45;
        static readonly double[] BoundChord = { 0, 0.02, 0.05, 0.1, 0.2, 0.35, 0.5, 0.7, 0.9 };

        public static double Smoothstep(double low, double high, double value)
        {
            double t = Math.Min(Math.Max((value - low) / (high - low), 0), 1);
            return t * t * (3 - 2 * t);
        }

        static double VortexDeficit(VaporField f, double circulation, double radius2, double core2)
        {
            return f.VortexK * circulation * circulation / Math.Max(radius2 + core2, 1e-6);
        }

        // A roll or a sideslip loads one side harder: a multiplier on the loading at a signed span.
        public static double SideLoading(VaporField f, double span)
        {
            return Math.Min(Math.Max(1 + f.RollBias * span / Math.Max(f.SemispanM, 1e-3) + f.SlipBias * Math.Sign(span), SideLoadingMin), SideLoadingMax);
        }

        struct TipSource
        {
            public double LeadingM, ChordM, HeightM, SemispanM, Circulation, Core2M2, Start2M2, RollupM, GrowthM, Descent, ReachM;
            public double VortexK, CosAlpha, SinAlpha, FlowZ, Positive, Negative;
        }

        static TipSource TipSourceOf(VaporField f, bool second)
        {
            return second
                ? new TipSource
                {
                    LeadingM = f.SecondTipLeadingM, ChordM = f.SecondTipChordM, HeightM = f.SecondTipHeightM, SemispanM = f.SecondSemispanM,
                    Circulation = f.SecondCirculation, Core2M2 = f.SecondCore2M2, Start2M2 = f.SecondStart2M2, RollupM = f.SecondRollupM,
                    GrowthM = f.SecondGrowthM, Descent = f.SecondDescent, ReachM = f.SecondReachM,
                    VortexK = f.VortexK, CosAlpha = f.CosAlpha, SinAlpha = f.SinAlpha, FlowZ = f.FlowZ,
                    Positive = SideLoading(f, f.SecondSemispanM), Negative = SideLoading(f, -f.SecondSemispanM),
                }
                : new TipSource
                {
                    LeadingM = f.TipLeadingM, ChordM = f.TipChordM, HeightM = f.TipHeightM, SemispanM = f.SemispanM,
                    Circulation = f.TipCirculation, Core2M2 = f.TipCore2M2, Start2M2 = f.TipStart2M2, RollupM = f.TipRollupM,
                    GrowthM = f.TipGrowthM, Descent = f.TipDescent, ReachM = f.TipReachM,
                    VortexK = f.VortexK, CosAlpha = f.CosAlpha, SinAlpha = f.SinAlpha, FlowZ = f.FlowZ,
                    Positive = SideLoading(f, f.SemispanM), Negative = SideLoading(f, -f.SemispanM),
                };
        }

        static TrailLayout SourceLayout(TipSource tip)
        {
            return new TrailLayout
            {
                TipX = tip.LeadingM + tip.ChordM, TipY = tip.HeightM, SemispanM = tip.SemispanM,
                CosAlpha = tip.CosAlpha, SinAlpha = tip.SinAlpha, FlowZ = tip.FlowZ,
                Descent = tip.Descent, RollupM = tip.RollupM, LengthM = Math.Max(tip.ReachM, 1),
            };
        }

        public static TrailLayout TrailLayoutOf(VaporField f, bool second = false)
        {
            return SourceLayout(TipSourceOf(f, second));
        }

        public static double TipDeficit(VaporGeometry g, double x, double y, double z)
        {
            double deficit = 0;
            for (int which = 0; which < 2; which++)
            {
                TipSource tip = TipSourceOf(g.Field, which == 1);
                TrailPaths paths = which == 1 ? g.SecondTrails : g.Trails;
                if (tip.Circulation <= 0) continue;
                deficit += SideDeficit(tip, paths.Positive, paths.SpacingM, 1, x, y, z) + SideDeficit(tip, paths.Negative, paths.SpacingM, -1, x, y, z);
            }
            return deficit;
        }

        static double SideDeficit(TipSource tip, float[] points, double spacing, int side, double x, double y, double z)
        {
            double tipLe = tip.LeadingM, tipTe = tipLe + tip.ChordM;
            double along = Trails.NearestAlong(points, spacing, tip.CosAlpha, tip.SinAlpha, tip.FlowZ, x, y, z);
            double circulation = tip.Circulation * (side > 0 ? tip.Positive : tip.Negative);

            if (along < 0)
            {
                double formed = Math.Min(Math.Max((x - tipLe) / Math.Max(tip.ChordM, 1e-3), 0), 1);
                double ax = Math.Min(Math.Max(x, tipLe), tipTe);
                double r2a = (x - ax) * (x - ax) + (y - tip.HeightM) * (y - tip.HeightM) + (z - side * tip.SemispanM) * (z - side * tip.SemispanM);
                double c = circulation * TipStartShare * formed;
                return tip.VortexK * c * c / Math.Max(r2a + tip.Start2M2, 1e-6);
            }

            double px, py, pz;
            Trails.Point(points, spacing, along, out px, out py, out pz);
            double r2 = (x - px) * (x - px) + (y - py) * (y - py) + (z - pz) * (z - pz);
            double rolled = 1 - Math.Exp(-along / Math.Max(tip.RollupM, 1e-3));
            double cr = circulation * (TipStartShare + (1 - TipStartShare) * rolled);
            double core2 = tip.Start2M2 + (tip.Core2M2 - tip.Start2M2) * rolled + tip.GrowthM * along;
            return tip.VortexK * cr * cr / Math.Max(r2 + core2, 1e-6);
        }

        public static double EdgeDeficit(VaporGeometry g, double x, double y, double z)
        {
            VaporField f = g.Field;
            double along = x - f.EdgeApexM;
            if (along <= 0 || f.EdgeGradient <= 0) return 0;
            double onWing = Math.Min(along, f.EdgeLengthM);
            double past = along - onWing;
            double spanM, surfaceM, reachM;
            EdgeAt(g.Path, onWing / Math.Max(f.EdgeLengthM, 1e-3), out spanM, out surfaceM, out reachM);
            double vy = surfaceM + f.EdgeHeight * reachM + past * (f.SinAlpha / Math.Max(f.CosAlpha, 0.1));
            double dz = Math.Abs(z) - spanM;
            double r2 = (y - vy) * (y - vy) + dz * dz;
            double burst = Smoothstep(f.EdgeBurstM, f.EdgeBurstM + BurstLength * f.EdgeLengthM, along);
            double core = Math.Max(f.EdgeCore * reachM, 0.02) * (1 + BurstSwell * burst);
            double leaving = Math.Exp(-past / (0.3 * f.EdgeLengthM));
            if (f.EdgeRopeM > 0)
            {
                // The coherent core leaves its origin along one free airflow axis.
                double length = Math.Max(f.EdgeRopeLengthM, 0.1);
                double start = f.RopeStartM;
                double share = (along - start) / length;
                double distance = Math.Max(along - start, 0);
                double drift = f.RopeSlopeZ * distance;
                double sourceSide = z - drift >= 0 ? 1 : -1;
                spanM = sourceSide * f.RopeSpanM + drift;
                double rope = f.EdgeRopeM * (0.3 + 0.7 * Math.Min(share, 1));
                vy = f.RopeHeightM + f.RopeSlopeY * distance;
                dz = z - spanM;
                r2 = (y - vy) * (y - vy) + dz * dz;
                double flat = r2 / (rope * rope);
                return f.EdgeRopeDeficit * SideLoading(f, sourceSide * f.RopeSpanM) * Smoothstep(0, 0.2, share) * Math.Exp(-Math.Max(share - 1, 0) / (f.EdgeRopeWalkM > 0 ? RopeBreak : RopeTrail))
                    * Math.Exp(-flat);
            }
            return VortexDeficit(f, f.EdgeGradient * SideLoading(f, Math.Sign(z) * spanM) * onWing * (1 - 0.3 * burst) * leaving, r2, core * core);
        }

        // how far along its edge the rope follows it: to the end of the last stretch swept at least
        // RopeSweepDeg (strake, chine, LEVCON) in the inner half of the span; none on a plain wing.
        public const double RopeSweepDeg = 65, LerxJumpM = 0.4;
        static double RopeWalk(EdgePath path, double lengthM, double outM)
        {
            double step = lengthM / (WingShape.Stations - 1), walk = 0;
            for (int k = 1; k < WingShape.Stations && path.ReachM[k] <= 0.5 * outM; k++)
                if (Math.Atan2(step, Math.Max(path.ReachM[k] - path.ReachM[k - 1], 1e-6)) >= RopeSweepDeg * Math.PI / 180) walk = k * step;
            return walk;
        }

        public static void PlaceRope(VaporField f, WingShape shape, EdgePath path)
        {
            double length = Math.Max(f.EdgeRopeLengthM, 0.1);
            f.RopeStartM = Math.Max(f.EdgeRopeWalkM - length, 0);
            double span, height, reach;
            EdgeAt(path, f.RopeStartM / Math.Max(f.EdgeLengthM, 1e-3), out span, out height, out reach);
            f.RopeSpanM = span + (RopeOut - Aerodynamics.LeadingEdgeVortexSpan) * reach;
            if (f.EdgeRopeWalkM <= 0) f.RopeSpanM = Math.Max(f.RopeSpanM, f.BodyRadiusM + f.EdgeRopeM);
            f.RopeSlopeY = f.EdgeRopeFlare;
            // Ropes flow straight back along the airframe, never sideways.
            f.RopeSlopeZ = 0;
            // where the smoothed cover overhangs the airframe, the rope rides on the real roof.
            double startRoof = shape.Cover != null ? shape.Cover.SampleRawRoof(f.EdgeApexM + f.RopeStartM, Math.Abs(f.RopeSpanM)) : WingCover.Empty;
            if (startRoof > -100) height = Math.Min(height, startRoof);
            f.RopeHeightM = height + Math.Max(RopeLift * f.EdgeHeight * reach, RopeClear * f.EdgeRopeM);
            if (shape.Cover == null) return;
            bool seated = SeatRope(f, shape);
            if (seated) length = Math.Max(f.EdgeRopeLengthM, 0.1);
            for (int i = 0; i <= 16; i++)
            {
                double distance = length * i / 16;
                double radius = f.EdgeRopeM * (0.3 + 0.7 * i / 16.0);
                for (int side = -1; side <= 1; side += 2)
                {
                    double at = Math.Abs(side * f.RopeSpanM + f.RopeSlopeZ * distance);
                    double x = f.EdgeApexM + f.RopeStartM + distance;
                    double roof = shape.Cover.SampleRawRoof(x, at);
                    if (!seated && shape.Cover.SourceShape != null)
                    {
                        double ignoredSpan, ignoredReach, surface;
                        EdgeAt(path, (f.RopeStartM + distance) / Math.Max(f.EdgeLengthM, 1e-3), out ignoredSpan, out surface, out ignoredReach);
                        roof = roof > -100 ? Math.Min(roof, surface) : surface;
                    }
                    f.RopeHeightM = Math.Max(f.RopeHeightM, roof + RopeClear * radius - f.RopeSlopeY * distance);
                }
            }
        }

        // A rope belongs on the wing: where the airframe's roof under it stands more than RopeStepM above
        // the wing's own surface (not the mantle's) (an intake beside the root), it moves aft and outboard to the first place
        // the roof is wing-high along its whole length.
        public const double RopeStepM = 0.45, RopeSeatReachM = 3, RopeFilletShare = 0.25, RopeSeatedChords = 0.6;
        static bool SeatRope(VaporField f, WingShape shape)
        {
            WingCover cover = shape.Cover;
            WingShape.Station st = shape.At(Math.Max(f.RopeSpanM, shape.RootSpanM));
            double length = Math.Max(f.EdgeRopeLengthM, Math.Max(RopeSeatedChords * st.ChordM, 0.1)), cell = Math.Max(cover.CellM, 0.1), limit = st.MidM + 0.5 * st.Thickness * st.ChordM + RopeStepM;
            double x0 = f.EdgeApexM + f.RopeStartM;
            // A swept stretch this short against the root chord is a fillet beside an intake, not a strake.
            if (f.EdgeRopeWalkM > RopeFilletShare * st.ChordM) return false;
            if (RopeOnWing(cover, x0, f.RopeSpanM, f.RopeSlopeZ, length, limit)) return false;
            double outer = Math.Max(f.RopeSpanM, 0.5 * (f.BodyRadiusM + Math.Abs(f.RopeSpanM) * 4));
            for (double dx = 0; dx <= RopeSeatReachM; dx += cell)
                for (double span = f.RopeSpanM; span <= outer; span += cell)
                    if (RopeOnWing(cover, x0 + dx, span, f.RopeSlopeZ, length, limit))
                    {
                        f.RopeStartM += dx;
                        f.RopeSpanM = span + 0.5 * f.EdgeRopeM;
                        f.EdgeRopeLengthM = length;
                        f.RopeHeightM = st.MidM + 0.5 * st.Thickness * st.ChordM + Math.Max(RopeClear * f.EdgeRopeM, 0.1);
                        return true;
                    }
            return false;
        }

        static bool RopeOnWing(WingCover cover, double x, double span, double slopeZ, double length, double limit)
        {
            for (int i = 0; i <= 8; i++)
            {
                double distance = length * i / 8;
                double roof = cover.SampleRawTop(x + distance, Math.Abs(span + slopeZ * distance));
                if (roof < -100 || roof > limit) return false;
            }
            return true;
        }

        public static void EdgeAt(EdgePath path, double share, out double spanM, out double surfaceM, out double reachM)
        {
            double position = Math.Min(Math.Max(share, 0), 1) * (WingShape.Stations - 1);
            int i = Math.Min((int)Math.Floor(position), WingShape.Stations - 2);
            double t = position - i;
            spanM = path.SpanM[i] + ((double)path.SpanM[i + 1] - path.SpanM[i]) * t;
            surfaceM = path.SurfaceM[i] + ((double)path.SurfaceM[i + 1] - path.SurfaceM[i]) * t;
            reachM = path.ReachM[i] + ((double)path.ReachM[i + 1] - path.ReachM[i]) * t;
        }

        class EdgeCache
        {
            public double ApexM, LengthM, RootM;
            public EdgePath Path;
        }

        static readonly ConditionalWeakTable<WingShape, EdgeCache> edgeCaches = new ConditionalWeakTable<WingShape, EdgeCache>();

        static EdgePath CachedEdgePath(WingShape shape, double apexM, double lengthM, double rootM)
        {
            EdgeCache held;
            if (edgeCaches.TryGetValue(shape, out held) && held.ApexM == apexM && held.LengthM == lengthM && held.RootM == rootM) return held.Path;
            EdgePath path = EdgePathOf(shape, apexM, lengthM, rootM);
            edgeCaches.Remove(shape);
            edgeCaches.Add(shape, new EdgeCache { ApexM = apexM, LengthM = lengthM, RootM = rootM, Path = path });
            return path;
        }

        // Leading-edge vortex path; `rootM` where the edge leaves the body, the root if negative.
        public static EdgePath EdgePathOf(WingShape shape, double apexM, double lengthM, double rootM = -1)
        {
            var path = new EdgePath();
            int n = WingShape.Stations;
            double root = rootM >= 0 ? rootM : shape.RootSpanM;
            double outM = Math.Max(shape.SemispanM - root, 1e-3);
            for (int index = 0; index < n; index++)
            {
                double share = (double)index / (n - 1);
                double x = apexM + share * lengthM;
                double reach = share * outM;
                if (lengthM > 1e-3)
                {
                    reach = outM;
                    for (int station = 1; station < n; station++)
                    {
                        double inner = shape.StationSpan(station - 1), outer = shape.StationSpan(station);
                        if (outer <= root) continue;
                        double from = Math.Max(inner, root);
                        double x0 = shape.At(from).LeadingM;
                        double x1 = shape.LeadingM[station];
                        if (x <= x1)
                        {
                            double t = x1 > x0 ? Math.Min(Math.Max((x - x0) / (x1 - x0), 0), 1) : 0;
                            reach = from + (outer - from) * t - root;
                            break;
                        }
                    }
                }
                double span = root + Aerodynamics.LeadingEdgeVortexSpan * reach;
                WingShape.Station st = shape.At(span);
                path.SpanM[index] = (float)span;
                double surface = st.MidM + EdgeSurface * st.Thickness * st.ChordM;
                if (shape.Cover != null)
                    surface = Math.Max(surface, Math.Min(shape.Cover.SampleMantle(x, root + RopeOut * reach), surface + WingCover.MaxRiseM));
                path.SurfaceM[index] = (float)surface;
                path.ReachM[index] = (float)Math.Max(reach, 0);
            }
            return path;
        }

        public const double RaiseFrontShare = 0.12;
        // The sheet over the body and strake root strip is cut by this share, so the main wing carries the tallest vapor.
        public const double ForwardBodyCut = 0.6;
        public static double WingDeficit(VaporGeometry g, double x, double y, double z)
        {
            VaporField f = g.Field;
            double span = Math.Abs(z);
            if (span >= f.SemispanM) return 0;
            // with the cover the sheet is one layer from tip to tip: inside the root it goes on
            // with the root's station, and the body's own surface (drawn over it) decides what shows.
            bool covered = g.Shape.Cover != null;
            double floorWeight = 1;
            double spanAt = span;
            double eta = spanAt / f.SemispanM;
            WingShape.Station st = g.Shape.At(spanAt);
            double chord = Math.Max(st.ChordM, 0.05);
            double xi = (x - st.LeadingM) / chord;
            double along = Math.Min(Math.Max(xi, 0), 1);
            double surface = st.MidM + 2 * st.Thickness * chord * along * (1 - along);
            // the sheet starts on the leading edge itself, over its first few centimetres.
            double frontRise = FrontRiseM + ForeWeight(f, x, span) * (0.6 - FrontRiseM);
            double front = covered ? Smoothstep(0, frontRise / chord, xi) : Smoothstep(-0.04, 0, xi), wing = 1;
            double section = f.SectionLiftM * st.Loading * SideLoading(f, z) / chord;
            double mach = f.NormalMach;
            double peak = NormalCoefficient(0.1, section, st.Thickness, f, mach);
            double frontScale = FrontSheetScale(f, x, span);
            if (covered)
                frontScale *= 1 - 0.55 * (1 - g.Shape.Cover.CraftFlat) * (1 - Smoothstep(f.RootSpanM, f.RootSpanM + 0.8, span)) * (1 - Smoothstep(0.15, 0.55, along));
            double full = frontScale * WingFieldHeight * f.ReachScale * Math.Min(chord, f.ReachChordM) / Math.Max(Aerodynamics.CompressibilityBeta(mach), WingHeightMinBeta);
            if (covered) full = RoundedRootReach(f, g.Shape.Cover.CraftFlat, span, along, full, g.Shape.Cover.BodyRadiusM);
            double height = y - surface;
            if (height < 0) return 0;
            if (covered)
            {
                wing = g.Shape.Cover.Sample(x, span);
                double shoulder = RoundedShoulder(g.Shape.Cover.CraftFlat, g.Shape.Cover.BodyRadiusM, f.RootSpanM, span, along);
                wing = Math.Max(wing, shoulder);
                if (g.Shape.Cover.CraftFlat > 0.5 && span < f.RootSpanM)
                {
                    floorWeight = BodyFloorWeight(g, x, y, span);
                    if (floorWeight <= 0) return 0;
                }
                double sheet = full * WingCover.ProfileTop(CappedDeficit(-(Atmosphere.AirGamma / 2) * mach * mach * peak, mach) / Math.Max(f.SaturationDeficit, 1e-9), true);
                // filled up to the base, measured from it.
                double basis = g.Shape.Cover.Base(surface, wing, sheet, x, span, WingCover.MaxRiseM * frontScale);
                // the wrap comes in over the chord's first RaiseFrontShare; at the leading edge the sheet hugs
                // the wing, so it does not stand up beside a tall intake as a horn (F-22).
                basis = surface + (basis - surface) * Smoothstep(0, RaiseFrontShare, along);
                double mantle = Math.Max(surface, Math.Min(g.Shape.Cover.SampleMantle(x, span), surface + WingCover.MaxRiseM));
                basis += (mantle - basis) * shoulder;
                double roundRoot = (1 - g.Shape.Cover.CraftFlat) * (1 - Smoothstep(f.RootSpanM - 0.15, f.RootSpanM + 0.25, span));
                double localRoof = Math.Max(surface, g.Shape.Cover.SampleTop(x, span)) + 0.1;
                basis += (Math.Min(basis, localRoof) - basis) * roundRoot;
                if (shoulder > 0.5 && y < basis) return 0;
                // below its base the sheet fills down only over a part the cover's top holds, not under a thin
                // overhang it dropped (a canard's tip), where the fill stood as a slab.
                if (y < basis && g.Shape.Cover.SampleTop(x, span) < -100) return 0;
                height = Math.Max(y - basis, 0);
                // read upstream by FlowLean times the height.
                double lean = WingCover.FlowLean + ForeWeight(f, x, span) * Math.Max(1.6 - WingCover.FlowLean, 0);
                xi = (x - lean * height - st.LeadingM) / chord;
                along = Math.Min(Math.Max(xi, 0), 1);
                front = Smoothstep(0, frontRise / chord, xi);
            }

            double coefficient = NormalCoefficient(along, section, st.Thickness, f, mach);
            double sonic = Aerodynamics.CriticalPressure(mach);
            if (peak < sonic)
            {
                double shock = ShockStation(f, section, sonic, mach);
                double ahead = Math.Max(1 - Smoothstep(shock - ShockWidth, shock + ShockWidth, along),
                                        Math.Exp(-Math.Max(along - shock, 0) * chord / Math.Max(f.EvaporationM, 1e-3)));
                coefficient = Math.Min(coefficient, peak) * ahead + Math.Max(coefficient, sonic) * (1 - ahead);
            }

            double ramp = StrakeRamp(f, x, span);
            if (covered) ramp += (1 - ramp) * RoundedShoulder(g.Shape.Cover.CraftFlat, g.Shape.Cover.BodyRadiusM, f.RootSpanM, span, along);
            double noseReachChords = Math.Min(NoseReachChords, NoseBuildM / chord), frontGrowth = Math.Min(FrontGrowth, FrontBuildM / chord);
            double reach = full * Math.Min((along + NoseRadius) / noseReachChords, 1) * (covered ? FrontShare + (1 - FrontShare) * Smoothstep(0, frontGrowth, along) : 1) * ramp;
            reach *= 1 - ForwardBodyCut * (1 - Smoothstep(f.RootSpanM, f.RootSpanM + 1.0, span));
            // Without the cover the body's edge is unknown and the sheet fades in over an eighth of
            // the semispan from the root.
            double blur = RootBlur * height / f.SemispanM;
            double fade = (covered ? 1 : Smoothstep(f.InnerSpanM / f.SemispanM - blur, f.InnerSpanM / f.SemispanM + RootFade + blur, eta))
                * (1 - Smoothstep(0.9, 1, eta)) * front * TrailFade(xi, chord, covered, f, span);
            if (covered)
            {
                fade *= 1 - (1 - wing) * (1 - g.Shape.Cover.CraftFlat) * (1 - Smoothstep(f.RootSpanM - 0.3, f.RootSpanM + 0.3, span));
                fade *= 1 - ForeWeight(f, x, span) * (1 - Smoothstep(0, 0.8, wing));
            }

            return CappedDeficit(-(Atmosphere.AirGamma / 2) * mach * mach * coefficient, mach) * WingCover.Profile(height, reach, covered) * fade * ramp * ForeMask(f, x, y, span) * floorWeight;
        }

        // The section's pressure coefficient at a chordwise station, compressible, normal to the sweep.
        static double NormalCoefficient(double at, double section, double thickness, VaporField f, double mach)
        {
            return Aerodynamics.KarmanTsien((-(section / Math.PI) * LiftShape(at, f.Separation) - 1.3 * thickness * Math.Sin(Math.PI * at)) / f.Cos2Sweep, mach);
        }

        public static double NoseReach(double xi)
        {
            return Math.Min((xi + NoseRadius) / NoseReachChords, 1);
        }

        public static double FrontSheetScale(VaporField f, double x, double span)
        {
            double inside = RootInside(f, span);
            if (f.StrakeEndM <= f.StrakeApexM || inside <= 0) return 1;
            return 1 - 0.45 * (1 - Smoothstep(f.StrakeApexM, f.StrakeEndM, x)) * inside;
        }

        public static double RoundedRootReach(VaporField f, double flat, double span, double along, double reach, double measuredBodyRadius = double.NaN)
        {
            double front = (1 - flat) * (1 - Smoothstep(f.RootSpanM, f.RootSpanM + 0.8, span)) * (1 - Smoothstep(0.3, 0.7, along));
            front = Math.Max(front, (1 - flat) * (1 - Smoothstep(f.RootSpanM - 0.15, f.RootSpanM + 0.25, span)));
            double shoulder = Math.Max(f.RootSpanM - (double.IsNaN(measuredBodyRadius) ? f.BodyRadiusM : measuredBodyRadius), 0.3);
            return reach + (Math.Min(reach, 0.9 * shoulder) - reach) * front;
        }

        public static double RoundedShoulder(double flat, double bodyRadius, double root, double span, double along)
        {
            if (bodyRadius <= 0) return 0;
            return (1 - flat) * Smoothstep(bodyRadius * 0.35, bodyRadius * 0.75, span)
                * (1 - Smoothstep(root + 0.3, root + 0.8, span)) * (1 - Smoothstep(0.45, 0.75, along));
        }

        // Eased into what an isentropic expansion to MaxLocalMach makes.
        public static double CappedDeficit(double deficit, double mach)
        {
            double cap = MostDeficit(mach);
            return deficit > 0 ? cap * (1 - Math.Exp(-deficit / cap)) : deficit;
        }

        public static double MostDeficit(double mach)
        {
            double g = Atmosphere.AirGamma, half = (g - 1) / 2;
            return 1 - Math.Pow((1 + half * mach * mach) / (1 + half * MaxLocalMach * MaxLocalMach), g / (g - 1));
        }

        public static double LiftShape(double xi, double separation)
        {
            return Math.Sqrt((1 - xi) / (xi + NoseRadius)) * (1 - separation) + Math.PI * (1 - xi) * separation;
        }

        public static double ShockStation(VaporField f, double section, double sonic, double mach)
        {
            if (mach >= 1 || sonic >= 0) return 1;
            double beta = Aerodynamics.CompressibilityBeta(mach);
            double k = mach * mach / (2 * (1 + beta));
            double target = sonic * beta / (1 - k * sonic) * f.Cos2Sweep;
            double q = -target * Math.PI / Math.Max(section, 1e-6);
            double q2 = q * q;
            double attached = (1 - q2 * NoseRadius) / (1 + q2);
            double separated = 1 - q / Math.PI;
            double station = Math.Min(Math.Max(attached + (separated - attached) * f.Separation, 0), 1);
            return station + (1 - station) * ShockTravel * Smoothstep(0.75, 1, mach);
        }

        public static double BodyFloorWeight(VaporGeometry g, double x, double y, double span)
        {
            WingCover cover = g.Shape.Cover;
            double top = cover.SampleTop(x, span), basis = cover.BodyBase(x, span);
            if (top <= WingCover.Empty) return 0;
            double blend = top <= g.Shape.At(span).MidM + WingCover.WrapNoneM ? g.Field.BodyFloorBlendM : 0;
            double floor = basis - blend;
            if (y < top || y < floor) return 0;
            return blend > 1e-5 ? Smoothstep(floor, basis, y) : 1;
        }

        public static double BodyDeficit(VaporGeometry g, double x, double y, double z)
        {
            VaporField f = g.Field;
            WingCover cover = g.Shape.Cover;
            if (cover == null || f.BodyDeficitAtSurface <= 0) return 0;
            double span = Math.Abs(z);
            double b = cover.SampleBody(x, span);
            if (b <= 0) return 0;
            double floorWeight = BodyFloorWeight(g, x, y, span);
            if (floorWeight <= 0) return 0;
            double height = Math.Max(y - cover.BodyBase(x, span), 0);
            b = Math.Min(b, cover.SampleBody(x + BodyAftSlope * height, span));
            double ramp = StrakeRamp(f, x, span);
            // the cloud is as low as it is thin at the body's edge, a shoulder rather than a wall (F-22 horns).
            return f.BodyDeficitAtSurface * b * WingCover.Profile(height, f.BodyReachM * cover.BodyDome(span) * ramp * b, true) * ramp * ForeMask(f, x, y, span) * floorWeight;
        }

        public static double ConeDeficit(VaporGeometry g, double x, double y, double z)
        {
            VaporField f = g.Field;
            if (f.ConeStrength <= 0) return 0;
            double xi = (x + f.NoseM) / f.BodyLengthM;
            if (xi <= 0 || xi >= 1) return 0;
            double r = WingShape.Hypot(y - f.BodyHeightM, z);
            double radius = BodyRadius(f, xi);
            double shock = f.ConeShock + ConeShockLean * r / f.BodyLengthM;
            double ahead = Math.Max(1 - Smoothstep(shock - ConeShockThickness, shock + f.ConeShockWidth, xi),
                                    Math.Exp(-Math.Max(xi - shock, 0) * f.BodyLengthM / Math.Max(f.EvaporationM, 1e-3)));
            double pocket = ConePocket(xi, shock);
            double beta = Aerodynamics.CompressibilityBeta(f.Mach);
            double reach = ConeReach * f.BodyLengthM / beta * (0.2 + 0.8 * pocket);
            double lateral = Math.Log(1 + reach / Math.Max(r, radius)) / Math.Log(1 + reach / Math.Max(radius, 1e-3));
            double coefficient = Aerodynamics.KarmanTsien(-f.ConeStrength * pocket, f.Mach);
            return CappedDeficit(-(Atmosphere.AirGamma / 2) * f.Mach * f.Mach * coefficient, f.Mach) * lateral * lateral * ahead * f.ConeFade;
        }

        public static double ConePocket(double xi, double shock)
        {
            return Smoothstep(shock - ConePocketLength, shock, xi);
        }

        public static double SlenderSuction(double strength, double ratio, double mach)
        {
            return strength * (1 + Math.Log(1 / Aerodynamics.CompressibilityBeta(mach)) / Math.Max(Math.Log(1 / Math.Max(ratio, 1e-3)), 1));
        }

        public static double BodyCriticalMach(double strength, double ratio)
        {
            if (strength <= 0) return 1;
            double low = 0.3, high = 1;
            for (int i = 0; i < 24; i++)
            {
                double middle = (low + high) / 2;
                if (-SlenderSuction(strength, ratio, middle) > Aerodynamics.CriticalPressure(middle)) low = middle; else high = middle;
            }
            return (low + high) / 2;
        }

        public static double ConeStrengthAt(double strength, double ratio, double mach, double growth)
        {
            if (strength <= 0) return 0;
            double slender = -SlenderSuction(strength, ratio, mach);
            double transonic = Aerodynamics.KarmanTsien(-strength, mach);
            double wanted = slender + (transonic - slender) * growth;
            double beta = Aerodynamics.CompressibilityBeta(mach);
            double k = mach * mach / (2 * (1 + beta));
            return -(wanted * beta) / (1 - k * wanted);
        }

        // A Sears-Haack body's radius.
        static double BodyRadius(VaporField f, double xi)
        {
            return f.BodyRadiusM * Math.Pow(Math.Max(4 * xi * (1 - xi), 0), 0.75);
        }

        public static double Deficit(VaporGeometry g, double x, double y, double z)
        {
            return TipDeficit(g, x, y, z) + EdgeDeficit(g, x, y, z) + WingDeficit(g, x, y, z) + ConeDeficit(g, x, y, z) + BodyDeficit(g, x, y, z)
                + CanardDeficit(g, x, y, z);
        }

        // the canard's sheet, the wing's mean section lift per chord on every station and the
        // wing's height rule (its chord, up to the wing's reach chord), so the two match.
        public static double CanardDeficit(VaporGeometry g, double x, double y, double z)
        {
            VaporField f = g.Field;
            if (f.CanardSection <= 0) return 0;
            double t = (Math.Abs(z) - f.CanardRootSpanM) / (f.CanardSemispanM - f.CanardRootSpanM);
            double rawSpan = Math.Abs(z);
            // inside its root the canard's sheet runs on over the body with the root's
            // section; the body's own surface hides it where the body stands taller.
            t = Math.Min(Math.Max(t, 0), 1);
            double leading = f.CanardRootLeadingM + (f.CanardTipLeadingM - f.CanardRootLeadingM) * t;
            double chord = Math.Max(f.CanardRootChordM + (f.CanardTipChordM - f.CanardRootChordM) * t, 0.05);
            double xi = (x - leading) / chord;
            double along = Math.Min(Math.Max(xi, 0), 1);
            double surface = f.CanardRootHeightM + (f.CanardTipHeightM - f.CanardRootHeightM) * t + 2 * KspCanardThickness * chord * along * (1 - along);
            double height = y - surface;
            if (height < 0) return 0;
            double mach = f.NormalMach;
            double section = f.CanardSection * SideLoading(f, z);
            double baseFade = Smoothstep(0, 0.04, height);
            double full = 0.7 * WingFieldHeight * f.ReachScale * Math.Min(chord, f.ReachChordM) / Math.Max(Aerodynamics.CompressibilityBeta(mach), WingHeightMinBeta);
            if (g.Shape.Cover != null)
            {
                double peak = NormalCoefficient(0.1, section, KspCanardThickness, f, mach);
                double sheet = full * WingCover.ProfileTop(CappedDeficit(-(Atmosphere.AirGamma / 2) * mach * mach * peak, mach) / Math.Max(f.SaturationDeficit, 1e-9), true);
                double basis = g.Shape.Cover.Base(surface, 0, sheet, x, Math.Abs(z));
                baseFade = Smoothstep(0, Math.Max(basis - surface, 0.04), height);
                height = Math.Max(y - basis, 0);
            }
            xi = (x - WingCover.FlowLean * height - leading) / chord;
            along = Math.Min(Math.Max(xi, 0), 1);
            double coefficient = NormalCoefficient(Math.Min(along, 0.85), section, KspCanardThickness, f, mach);
            double reach = full * NoseReach(along) * FrontTaper(along);
            // a canard's sheet runs on over the gap to the wing's leading edge before it thins
            // out, so the two meet as one sheet (a close-coupled canard's flow lands on the wing).
            double gap = Math.Max(g.Shape.At(Math.Abs(z)).LeadingM - (leading + chord), 0);
            double spread = CanardWakeSpread(x - leading - chord, height);
            double rootFade = Smoothstep(0, Math.Max(f.CanardRootSpanM, 0.15), rawSpan);
            double fade = rootFade * baseFade * (1 - Smoothstep(f.CanardSemispanM - 0.15, f.CanardSemispanM + spread, rawSpan)) * Smoothstep(0, 0.45 / chord, xi) * (1 - Smoothstep(1 + gap / chord, 1 + (gap + CoverTrailM) / chord, xi));
            return CappedDeficit(-(Atmosphere.AirGamma / 2) * mach * mach * coefficient, mach) * WingCover.Profile(height, reach, true) * fade;
        }

        const double KspCanardThickness = 0.06;
        public const double CanardWakeMarginM = 1.2;
        public static double CanardWakeSpread(double aft, double height)
        {
            return Math.Min(CanardWakeMarginM, 0.15 + 0.12 * Math.Max(aft, 0) + 0.15 * Math.Max(height, 0));
        }

        // The field and its bounds for one moment (vapor_state); `into` reuses last frame's state.
        public static VaporState Compute(VaporAirframe airframe, VaporFlight flight, MoistAir air, VaporLook look, WingShape shape = null, bool tipBounds = true,
                                         VaporState into = null, TrailHistory history = null, bool flowGiven = false, double flowAlphaRad = 0, double flowBetaRad = 0)
        {
            if (shape == null) shape = WingShape.Trapezoid(airframe);
            Aerodynamics.FlightState state = Aerodynamics.State(airframe, flight, air);

            double speed = Math.Max(flight.AirspeedMPerS, 1e-3);
            double alpha = flight.AngleOfAttackRad;
            double sideslip = flight.SideslipRad;
            double semispan = shape.SemispanM;
            double span = semispan * 2;
            int tip = WingShape.Stations - 1;

            double effectiveCos = Math.Sqrt(Math.Cos(Aerodynamics.SweepAt(airframe, 0.5)));
            double area = Aerodynamics.PlanformOf(airframe).AreaM2;
            double vortexK = air.DensityKgPerM3 / (8 * Math.PI * Math.PI * air.PressurePa);
            double saturationDeficit = MoistCondensation.CondensingDeficit(air, MoistCondensation.Moistest(air, look.HumiditySpread));

            SecondarySurface secondary = shape.Secondary;
            double share = secondary != null
                ? Math.Min(secondary.AreaM2 / Math.Max(area, 1e-3) * (secondary.IsCanard ? CanardLoading : TailLoading), MaxSecondShare)
                : 0;
            double wingShare = secondary != null && secondary.IsCanard ? 1 - share : 1;
            double wingCirculation = state.TipCirculation * wingShare;
            double secondSpan = secondary != null ? secondary.SemispanM * 2 : 1;
            double secondCirculation = secondary != null ? state.TipCirculation * share * span / secondSpan : 0;

            double tipCore = airframe.TipCoreRadius * span;
            double tipCirculation = airframe.TipCoreShare * wingCirculation;
            double tipGrowth = 4 * VortexEddyViscosity * wingCirculation / speed;
            double tipDescent = wingCirculation / (2 * Math.PI * Aerodynamics.VortexSpacing * span) / speed;
            double secondCore = airframe.TipCoreRadius * secondSpan;
            double rollupSpans = Math.Min(Math.Max(RollupCoefficient * (span * span) / Math.Max(area, 1e-3) / Math.Max(Math.Abs(state.LiftCoefficient), 1e-3),
                                                   RollupSpansMin), RollupSpansMax);

            // on a LERX the leading-edge vortex starts at its apex.
            double edgeRoot = shape.RootSpanM;
            if (shape.Cover != null && shape.InnerSpanM < shape.RootSpanM)
            {
                double shoulder = Math.Min(shape.RootSpanM, Math.Max(shape.InnerSpanM, shape.Cover.BodyRadiusM));
                double rootLeading = shape.At(shape.RootSpanM).LeadingM, front = rootLeading;
                for (int k = 0; k < WingShape.Stations; k++)
                {
                    double sk = shape.StationSpan(k);
                    if (sk < shape.InnerSpanM || sk > shape.RootSpanM) continue;
                    if (sk < shoulder)
                    {
                        double roof = shape.Cover.SampleRawRoof(shape.LeadingM[k] + 0.2, sk);
                        if (sk < 0.75 * shoulder || shape.Canard != null || shape.Cover.CraftFlat < 0.5 || roof <= WingCover.Empty || roof > shape.MidM[k] + RopeStepM) continue;
                    }
                    if (shape.LeadingM[k] < front) { front = shape.LeadingM[k]; edgeRoot = sk; }
                }
                if (rootLeading - front < LerxMinM) edgeRoot = shape.RootSpanM;
            }
            double edgeApex = shape.At(edgeRoot).LeadingM;
            double strakeEnd = edgeRoot < shape.RootSpanM ? shape.At(shape.RootSpanM).LeadingM : edgeApex;
            double foreApex = edgeApex;
            WingShape sourceShape = shape.Cover != null && shape.Cover.SourceShape != null ? shape.Cover.SourceShape : shape;
            bool exposedSource = true;
            double lerxRoot = edgeRoot, lerxApex = edgeApex;
            if (shape.Cover != null && shape.Cover.SourceExposed != null)
            {
                exposedSource = false;
                double front = double.PositiveInfinity;
                for (int k = 0; k < WingShape.Stations - 1; k++)
                {
                    double sk = sourceShape.StationSpan(k);
                    // the apex is inboard; a forward-swept wing's tip is its frontmost point but no apex.
                    if (sk < shape.InnerSpanM || sk > 0.5 * sourceShape.SemispanM || !shape.Cover.SourceExposed[k]) continue;
                    if (sourceShape.LeadingM[k] < front) { front = sourceShape.LeadingM[k]; edgeRoot = sk; exposedSource = true; }
                }
                // an exposed start far outboard of a LERX found in the body (Su-33) is the LERX's end, not its apex.
                if (exposedSource && lerxRoot < shape.RootSpanM && edgeRoot > shape.RootSpanM + LerxJumpM) { edgeRoot = lerxRoot; edgeApex = lerxApex; }
                else if (exposedSource) edgeApex = front;
            }
            double edgeLength = Math.Max(sourceShape.LeadingM[tip] - edgeApex, 0);
            EdgePath path = CachedEdgePath(sourceShape, edgeApex, edgeLength, edgeRoot);

            double tipChordShape = Math.Max(shape.ChordM[tip], 1e-3);
            double secondTipChord = secondary != null ? secondary.TipChordM : 0;
            VaporField f = into != null && into.Field != null ? into.Field : new VaporField();
            f.Mach = state.Mach;
            f.SpeedMPerS = speed;
            double flowAlpha = flowGiven ? flowAlphaRad : alpha, flowBeta = flowGiven ? flowBetaRad : sideslip;
            f.CosAlpha = Math.Cos(flowAlpha) * Math.Cos(flowBeta);
            f.SinAlpha = Math.Sin(flowAlpha) * Math.Cos(flowBeta);
            f.FlowZ = -Math.Sin(flowBeta);
            f.RollBias = Math.Min(Math.Max(flight.RollRateRadPerS * semispan / (speed * Math.Max(Math.Abs(alpha), 2 * Degree)), -MaxBias), MaxBias);
            f.SlipBias = Math.Min(Math.Max(2 * sideslip * Math.Tan(airframe.LeadingEdgeSweepRad), -MaxBias), MaxBias);
            f.VortexK = vortexK;
            f.SaturationDeficit = saturationDeficit;

            f.SemispanM = semispan;
            f.RootSpanM = shape.Cover != null ? Math.Max(shape.RootSpanM, shape.Cover.RaisedRootM) : shape.RootSpanM;
            f.InnerSpanM = shape.Cover != null ? Math.Min(shape.InnerSpanM, shape.RootSpanM) : shape.RootSpanM;
            f.BodyCover = Math.Min(Math.Max(flight.BodyCover, 0), 1);
            f.TipLeadingM = shape.LeadingM[tip];
            f.TipChordM = tipChordShape;
            f.TipHeightM = shape.MidM[tip];

            f.Separation = Math.Min(Math.Max(airframe.LeadingEdgeSharpness, 0), 1) * Smoothstep(3 * Degree, 8 * Degree, Math.Abs(alpha));
            f.SectionLiftM = Math.Max(state.PotentialLiftCoefficient, 0) * (area / span) * (1 - Math.Min(Math.Max(flight.SheetCut, 0), 1));
            f.ReachChordM = Math.Max(area / span, 0.05);
            // in the hardest pulls the cloud stands tallest over the long chords at the middle.
            double reachNorm = 1;
            if (shape.Cover != null)
            {
                f.ReachChordM = Math.Min(f.ReachChordM, WingCover.ReachSpanShare * semispan);
                reachNorm = WingCover.ReferenceChordM / f.ReachChordM;
                f.ReachChordM *= 1 + (WingCover.ReachGrowth - 1) * f.BodyCover;
            }
            f.Cos2Sweep = Math.Max(effectiveCos, 0.05);
            f.NormalMach = state.Mach * Math.Sqrt(effectiveCos);

            f.TipCirculation = tipCirculation;
            f.TipCore2M2 = tipCore * tipCore;
            f.TipStart2M2 = Math.Min(Math.Pow(TipStartCore * tipChordShape, 2), tipCore * tipCore);
            f.TipRollupM = rollupSpans * span;
            f.TipGrowthM = tipGrowth;
            f.TipDescent = tipDescent;
            f.TipReachM = 0;
            f.TipBoundM = 0;
            f.TrailSpacingM = 1;

            f.SecondSemispanM = secondary != null ? secondary.SemispanM : 1;
            f.SecondTipLeadingM = secondary != null ? secondary.TipLeadingM : 0;
            f.SecondTipChordM = secondary != null ? secondary.TipChordM : 1;
            f.SecondTipHeightM = secondary != null ? secondary.TipHeightM : 0;
            f.SecondCirculation = airframe.TipCoreShare * secondCirculation;
            f.SecondCore2M2 = secondCore * secondCore;
            f.SecondStart2M2 = Math.Min(Math.Pow(TipStartCore * secondTipChord, 2), secondCore * secondCore);
            f.SecondRollupM = rollupSpans * secondSpan;
            f.SecondGrowthM = 4 * VortexEddyViscosity * secondCirculation / speed;
            f.SecondDescent = secondCirculation / (2 * Math.PI * Aerodynamics.VortexSpacing * secondSpan) / speed;
            f.SecondReachM = 0;
            f.SecondBoundM = 0;
            f.SecondSpacingM = 1;

            f.EdgeApexM = edgeApex;
            f.StrakeApexM = foreApex;
            f.StrakeEndM = strakeEnd;
            f.ForeEnabled = 0;
            if (shape.Canard == null && shape.Cover != null && shape.Cover.RawTop != null && strakeEnd - foreApex > LerxMinM)
            {
                double roof = WingCover.Empty;
                double start = foreApex - (strakeEnd - foreApex) * 0.5;
                for (int k = 0; k <= 24; k++) roof = Math.Max(roof, shape.Cover.SampleRawRoof(start + (strakeEnd - start) * k / 24, 0));
                double surface = shape.At(shape.RootSpanM).MidM;
                if (roof > surface + 0.3)
                {
                    f.ForeEnabled = 1;
                    f.ForeStartM = strakeEnd;
                    f.ForeTopM = roof - 0.12;
                    f.ForeEndM = strakeEnd + Math.Max(0.5, shape.At(shape.RootSpanM).ChordM * 0.35);
                }
            }
            if (shape.Cover != null && shape.Cover.Body != null && (shape.Canard == null || shape.Cover.ForebodyJoined) && f.BodyCover > 0)
            {
                double growth = Math.Max(0.5, 2 * shape.Cover.CellM);
                f.StrakeApexM = shape.Cover.BodyMinX - growth * StrakeQuiet / (1 - StrakeQuiet);
                f.StrakeEndM = shape.Cover.BodyMinX + growth;
            }
            f.EdgeGradient = exposedSource && alpha > 0 && edgeLength > 1e-3 ? state.LeadingEdgeCirculation / edgeLength : 0;
            f.EdgeLengthM = edgeLength;
            f.EdgeCore = airframe.LeadingEdgeCoreRadius;
            // how high the vortex rides is the air's real angle, not the angle standing for the load.
            f.EdgeHeight = LeadingEdgeVortexHeight * Math.Max(Math.Sin(flowAlpha), 0);
            f.EdgeBurstM = state.Breakdown * edgeLength;
            f.EdgeBoundM = 0;
            f.EdgeRopeM = exposedSource && flight.RopeGain > 0 && flight.RopeRadiusM > 0 ? flight.RopeRadiusM : 0;
            f.EdgeRopeDeficit = f.EdgeRopeM > 0 ? flight.RopeGain * saturationDeficit : 0;
            f.EdgeRopeWalkM = RopeWalk(path, edgeLength, shape.SemispanM - edgeRoot);
            double bursting = Smoothstep(15 * Math.PI / 180, 35 * Math.PI / 180, flowAlpha);
            // over a strake, the whole of it from near its apex.
            f.EdgeRopeLengthM = f.EdgeRopeM <= 0 ? 0 : f.EdgeRopeWalkM > 0 ? (1 - RopeStart) * f.EdgeRopeWalkM
                : RopeChords * shape.At(shape.RootSpanM).ChordM * (1 - RopeBurstShare * bursting);
            // the core leaves the surface at about half the angle of attack, not the whole of it.
            f.EdgeRopeFlare = Math.Tan(Math.Min(RopeFlareShare * Math.Max(flowAlpha, 0), RopeFlareDeg * Math.PI / 180));
            f.EdgeTrailM = Math.Max(edgeLength * 0.6, f.EdgeRopeM > 0 ? f.EdgeRopeWalkM + 4 * (f.EdgeRopeWalkM > 0 ? RopeBreak : RopeTrail) * f.EdgeRopeLengthM - edgeLength : 0);

            f.WingBoundM = 0;
            f.EvaporationM = speed * EvaporationS * Math.Pow(look.DropletRadiusM / ReferenceDropletM, 2);

            f.NoseM = airframe.NoseM;
            {
                WingShape.Station rootSt = shape.At(shape.RootSpanM);
                double tail = airframe.NoseM + airframe.FuselageLengthM;
                f.BodyTrailM = shape.Cover != null ? Math.Min(Math.Max(tail - (rootSt.LeadingM + rootSt.ChordM), CoverTrailM), 8) : CoverTrailM;
            }
            f.BodyLengthM = Math.Max(airframe.FuselageLengthM, 1e-3);
            f.BodyRadiusM = airframe.FuselageRadiusM;
            f.BodyHeightM = airframe.FuselageHeightM;
            f.ConeStrength = 0;
            f.ConeShock = 0;
            f.ConeShockWidth = ConeShockThickness;
            f.ConeFade = 1 - Smoothstep(SupersonicFadeLow, SupersonicFadeHigh, state.Mach);
            f.ConeBoundM = 0;
            if (f.EdgeRopeM > 0) PlaceRope(f, shape, path);
            f.ReachScale = look.ReachScale * reachNorm;
            // the canard's sheet.
            f.CanardSection = 0;
            f.CanardBoundM = 0;
            SecondarySurface canard = shape.Canard ?? (secondary != null && secondary.IsCanard ? secondary : null);
            if (shape.Cover != null && canard != null && canard.SemispanM > canard.RootSpanM + 1e-3)
            {
                f.CanardRootSpanM = canard.RootSpanM;
                f.CanardSemispanM = canard.SemispanM;
                f.CanardRootLeadingM = canard.RootLeadingM;
                f.CanardRootChordM = canard.RootChordM;
                f.CanardTipLeadingM = canard.TipLeadingM;
                f.CanardTipChordM = canard.TipChordM;
                f.CanardRootHeightM = canard.RootHeightM;
                f.CanardTipHeightM = canard.TipHeightM;
                f.CanardSection = Math.Max(state.PotentialLiftCoefficient, 0) * (1 - Math.Min(Math.Max(flight.CanardCut, 0), 1));
            }

            VaporState result = into ?? new VaporState();
            result.Air = air;
            result.Flight = state;
            result.Field = f;
            result.Shape = shape;
            result.Path = path;
            result.Trails = Trails.Straight(TrailLayoutOf(f), result.Trails);
            result.SecondTrails = Trails.Straight(TrailLayoutOf(f, true), result.SecondTrails);

            double wingFront = double.PositiveInfinity, wingBack = double.NegativeInfinity;
            double wingLow = double.PositiveInfinity, wingHigh = double.NegativeInfinity, deepest = 0;
            for (int station = 0; station < WingShape.Stations; station++)
            {
                if (shape.StationSpan(station + 1) < f.InnerSpanM) continue;
                double chord = shape.ChordM[station];
                double half = shape.Thickness[station] * chord * 0.5;
                wingFront = Math.Min(wingFront, shape.LeadingM[station]);
                wingBack = Math.Max(wingBack, shape.LeadingM[station] + chord);
                wingLow = Math.Min(wingLow, shape.MidM[station] - half);
                wingHigh = Math.Max(wingHigh, shape.MidM[station] + half);
                deepest = Math.Max(deepest, chord);
            }

            double needed = Math.Max(saturationDeficit, BoundFloor);
            bool any = false;
            double[] bmin = result.BoundsMin, bmax = result.BoundsMax;

            for (int which = 0; which < (tipBounds ? 2 : 0); which++)
            {
                bool second = which == 1;
                TipSource ts = TipSourceOf(f, second);
                double strongest = ts.Circulation * Math.Max(ts.Positive, ts.Negative);
                double well = vortexK * strongest * strongest;
                double widest = well / needed - ts.Core2M2;
                double first = well * TipStartShare * TipStartShare / needed - ts.Start2M2;
                if (!(needed < 1 && (widest > 0 || first > 0) && ts.Circulation > 0)) continue;

                double bound = Math.Sqrt(Math.Max(well / needed - ts.Start2M2, 0)) * 1.15 + 0.1;
                double reach = Math.Min(widest > 0 ? widest / Math.Max(ts.GrowthM, 1e-9) : ts.RollupM, MaxTrailM);
                if (!second) { f.TipBoundM = bound; f.TipReachM = reach; }
                else { f.SecondBoundM = bound; f.SecondReachM = reach; }

                TrailLayout layout = TrailLayoutOf(f, second);
                TrailPaths target = second ? result.SecondTrails : result.Trails;
                TrailPaths trails = history != null ? history.Paths(layout, target) : Trails.Straight(layout, target);

                double[] low = tipLow, high = tipHigh;
                low[0] = ts.LeadingM - bound; low[1] = ts.HeightM - bound; low[2] = -ts.SemispanM - bound;
                high[0] = ts.LeadingM + ts.ChordM + bound; high[1] = ts.HeightM + bound; high[2] = ts.SemispanM + bound;
                for (int side = 0; side < 2; side++)
                {
                    float[] points = side == 0 ? trails.Positive : trails.Negative;
                    for (int i = 0; i < points.Length; i += 3)
                        for (int axis = 0; axis < 3; axis++)
                        {
                            low[axis] = Math.Min(low[axis], points[i + axis] - bound);
                            high[axis] = Math.Max(high[axis], points[i + axis] + bound);
                        }
                }
                Union(ref any, bmin, bmax, low[0], low[1], low[2], high[0], high[1], high[2]);
            }

            {
                double steepest = 0;
                for (int index = 1; index < WingShape.Stations; index++)
                {
                    double along = (double)index / (WingShape.Stations - 1) * edgeLength;
                    steepest = Math.Max(steepest, along / Math.Max(path.ReachM[index], 1e-3));
                }
                double heaviest = Math.Max(SideLoading(f, semispan), SideLoading(f, -semispan));
                double ratio = f.EdgeGradient * heaviest * steepest / Math.Max(f.EdgeCore, 1e-3);
                double well = vortexK * ratio * ratio;
                if (f.EdgeRopeM > 0)
                {
                    // the cord's own bound, wherever its deficit passes the condensing one.
                    double top = f.EdgeRopeDeficit * heaviest / Math.Max(needed, 1e-9);
                    well = top > 1 ? needed * 2 : 0;
                }
                if (needed < 1 && well > needed && f.EdgeGradient > 0)
                {
                    double endReach = path.ReachM[tip];
                    double endCore = f.EdgeCore * endReach;
                    double circulation = f.EdgeGradient * heaviest * edgeLength;
                    double widest = Math.Sqrt(Math.Max(vortexK * circulation * circulation / needed - endCore * endCore, 0));
                    f.EdgeBoundM = Math.Max(widest, (1 + BurstSwell) * endCore) * 1.15 + 0.1;
                    if (f.EdgeRopeM > 0)
                        f.EdgeBoundM = f.EdgeRopeM * Math.Pow(Math.Log(Math.Max(f.EdgeRopeDeficit * heaviest / needed, 1)), 0.25) * 1.15 + 0.1;
                    double b = f.EdgeBoundM;
                    double trailing = f.EdgeTrailM;
                    double maxSurface = double.NegativeInfinity;
                    for (int i = 0; i < WingShape.Stations; i++) maxSurface = Math.Max(maxSurface, path.SurfaceM[i]);
                    double top = maxSurface + f.EdgeHeight * endReach + trailing * (f.SinAlpha / Math.Max(f.CosAlpha, 0.1)) + f.EdgeRopeFlare * f.EdgeRopeLengthM / 2;
                    double outboard = semispan;
                    if (f.EdgeRopeM > 0)
                    {
                        top = Math.Max(top, f.RopeHeightM + Math.Max(f.RopeSlopeY, 0) * (f.EdgeRopeLengthM + trailing));
                        outboard = Math.Max(outboard, f.RopeSpanM + Math.Abs(f.RopeSlopeZ) * (f.EdgeRopeLengthM + trailing));
                    }
                    Union(ref any, bmin, bmax, edgeApex - b, wingLow - b, -outboard - b, edgeApex + edgeLength + trailing + b, top + b, outboard + b);
                }
            }

            {
                double strongest = 0;
                foreach (double xi in BoundChord)
                {
                    for (int j = 0; j <= 6; j++)
                    {
                        double spanAt = f.InnerSpanM + (semispan - f.InnerSpanM) * j / 6;
                        WingShape.Station st = shape.At(spanAt);
                        double x = st.LeadingM + st.ChordM * xi;
                        double y = st.MidM + 2 * st.Thickness * st.ChordM * xi * (1 - xi) + 1e-3;
                        for (int side = 1; side >= -1; side -= 2)
                            strongest = Math.Max(strongest, WingDeficit(result, x, y, side * Math.Min(spanAt, semispan * 0.999)));
                    }
                }
                if (needed < 1 && strongest > needed && !double.IsInfinity(wingFront) && !double.IsNaN(wingFront))
                {
                    double reach = WingFieldHeight * f.ReachScale * Math.Min(deepest, f.ReachChordM) / Math.Max(Aerodynamics.CompressibilityBeta(f.NormalMach), WingHeightMinBeta);
                    f.WingBoundM = reach * WingCover.ProfileTop(strongest / needed, shape.Cover != null) * 1.1 + 0.05;
                    Union(ref any, bmin, bmax, wingFront - 0.05 * deepest - (shape.Cover != null ? shape.Cover.FrontM : 0), wingLow - 0.05, -semispan, wingBack + 0.1 * f.TipChordM + (shape.Cover != null ? Math.Max(CoverTrailM, f.BodyTrailM) + WingCover.FlowLean * (f.WingBoundM + WingCover.MaxRiseM) : 0), wingHigh + f.WingBoundM + (shape.Cover != null ? WingCover.MaxRiseM : 0), semispan);
                }
            }

            if (f.CanardSection > 0)
            {
                double side = Math.Max(SideLoading(f, f.CanardSemispanM), SideLoading(f, -f.CanardSemispanM));
                double peak = NormalCoefficient(0.1, f.CanardSection * side, KspCanardThickness, f, f.NormalMach);
                double strongest = CappedDeficit(-(Atmosphere.AirGamma / 2) * f.NormalMach * f.NormalMach * peak, f.NormalMach);
                if (needed < 1 && strongest > needed)
                {
                    double reach = 0.7 * WingFieldHeight * f.ReachScale * Math.Min(Math.Max(f.CanardRootChordM, f.CanardTipChordM), f.ReachChordM) / Math.Max(Aerodynamics.CompressibilityBeta(f.NormalMach), WingHeightMinBeta);
                    f.CanardBoundM = reach * WingCover.ProfileTop(strongest / needed, true) * 1.1 + 0.05;
                    double front = Math.Min(f.CanardRootLeadingM, f.CanardTipLeadingM), back = Math.Max(f.CanardRootLeadingM + f.CanardRootChordM, f.CanardTipLeadingM + f.CanardTipChordM);
                    for (int k = 0; k < WingShape.Stations; k++)
                        if (shape.StationSpan(k) <= f.CanardSemispanM + CanardWakeMarginM) back = Math.Max(back, shape.LeadingM[k]);
                    double rise = shape.Cover != null ? WingCover.MaxRiseM : 0;
                    double low = Math.Min(f.CanardRootHeightM, f.CanardTipHeightM) - 0.1, high = Math.Max(f.CanardRootHeightM, f.CanardTipHeightM) + 0.2 * f.CanardRootChordM;
                    Union(ref any, bmin, bmax, front - 0.05, low, -f.CanardSemispanM - CanardWakeMarginM, back + 0.1 + CoverTrailM + WingCover.FlowLean * (f.CanardBoundM + rise), high + f.CanardBoundM + rise, f.CanardSemispanM + CanardWakeMarginM);
                }
            }

            // a flat body's own cloud, as strong at its surface as the wing's at the root's
            // suction peak, and reaching as high.
            f.BodyDeficitAtSurface = 0;
            f.BodyBoundM = 0;
            WingCover body = shape.Cover;
            f.BodyFloorBlendM = body != null && body.CraftFlat > 0.5 ? 0.3 : 0;
            if (body != null && body.Body != null && f.BodyCover > 0)
            {
                WingShape.Station rootStation = shape.At(shape.RootSpanM);
                double rx = rootStation.LeadingM + 0.1 * rootStation.ChordM;
                double ry = rootStation.MidM + 2 * rootStation.Thickness * rootStation.ChordM * 0.09 + 1e-3;
                double meanChord = f.ReachChordM / (1 + (WingCover.ReachGrowth - 1) * f.BodyCover);
                f.BodyReachM = WingCover.BodyReach * WingFieldHeight * f.ReachScale * Math.Min(rootStation.ChordM, meanChord) / Math.Max(Aerodynamics.CompressibilityBeta(f.NormalMach), WingHeightMinBeta);
                f.BodyDeficitAtSurface = WingCover.BodyStrength * WingDeficit(result, rx, ry, shape.RootSpanM) * f.BodyCover * body.CraftFlat;
                if (needed < 1 && f.BodyDeficitAtSurface > needed)
                {
                    f.BodyBoundM = f.BodyReachM * WingCover.ProfileTop(f.BodyDeficitAtSurface / needed, true) * 1.1 + 0.05;
                    Union(ref any, bmin, bmax, body.BodyMinX - body.CellM * 3, body.BodyTopM - WingCover.MaxRiseM - 1, -body.BodySpanM - body.CellM * 3,
                          body.BodyMaxX + body.CellM * 3, body.BodyTopM + f.BodyBoundM + WingCover.MaxRiseM, body.BodySpanM + body.CellM * 3);
                }
            }

            {
                double ratio = airframe.FuselageRadiusM / f.BodyLengthM;
                double strength = ratio > 0 ? ConeStrengthFactor * ratio * ratio * Math.Log(1 / ratio) : 0;
                double critical = BodyCriticalMach(strength, ratio);
                double coneMach = state.Mach;
                double growth = Smoothstep(critical, critical + PocketGrowth, coneMach);
                f.ConeStrength = ConeStrengthAt(strength, ratio, coneMach, growth);
                f.ConeShockWidth = ConeRecompression + (ConeShockThickness - ConeRecompression) * growth;
                f.ConeShock = ConeShockSubsonic
                    + (ConeShockSonic - ConeShockSubsonic) * Smoothstep(critical, 1.01, coneMach)
                    + (ConeShockSupersonic - ConeShockSonic) * Smoothstep(1, SupersonicFadeLow + 0.03, coneMach);

                double strongest = 0;
                for (int i = 1; i < 40; i++)
                {
                    double xi = i / 40.0;
                    if (xi < f.ConeShock)
                        strongest = Math.Max(strongest, ConeDeficit(result, xi * f.BodyLengthM - f.NoseM, f.BodyHeightM + BodyRadius(f, xi), 0));
                }

                if (needed < 1 && strongest > needed && f.ConeFade > 0)
                {
                    double xi = f.ConeShock - 0.02;
                    double x = xi * f.BodyLengthM - f.NoseM;
                    double low = BodyRadius(f, xi), high = 80;
                    if (ConeDeficit(result, x, f.BodyHeightM + high, 0) > needed) low = high;
                    for (int i = 0; i < 32 && low < high; i++)
                    {
                        double middle = (low + high) / 2;
                        if (ConeDeficit(result, x, f.BodyHeightM + middle, 0) > needed) low = middle; else high = middle;
                    }
                    f.ConeBoundM = low * 1.15 + 0.2;
                    double b = f.ConeBoundM;
                    double shockX = (f.ConeShock + f.ConeShockWidth + ConeShockLean * b / f.BodyLengthM + 0.02) * f.BodyLengthM - f.NoseM;
                    Union(ref any, bmin, bmax, -f.NoseM, f.BodyHeightM - b, -b, shockX, f.BodyHeightM + b, b);
                }
                else f.ConeStrength = 0;
            }

            result.Visible = any;
            if (!any)
                for (int axis = 0; axis < 3; axis++) { bmin[axis] = 0; bmax[axis] = 0; }
            f.TrailSpacingM = result.Trails.SpacingM;
            f.SecondSpacingM = result.SecondTrails.SpacingM;
            return result;
        }

        static readonly double[] tipLow = new double[3], tipHigh = new double[3];

        // Grows the box to hold another (the first sets it).
        static void Union(ref bool any, double[] bmin, double[] bmax, double x0, double y0, double z0, double x1, double y1, double z1)
        {
            if (!any)
            {
                bmin[0] = x0; bmin[1] = y0; bmin[2] = z0;
                bmax[0] = x1; bmax[1] = y1; bmax[2] = z1;
                any = true;
                return;
            }
            bmin[0] = Math.Min(bmin[0], x0); bmin[1] = Math.Min(bmin[1], y0); bmin[2] = Math.Min(bmin[2], z0);
            bmax[0] = Math.Max(bmax[0], x1); bmax[1] = Math.Max(bmax[1], y1); bmax[2] = Math.Max(bmax[2], z1);
        }

        public static VaporConstants Constants(VaporField f)
        {
            double wingMach = f.NormalMach;
            double wingBeta = Aerodynamics.CompressibilityBeta(wingMach);
            double coneMach = f.Mach;
            double coneBeta = Aerodynamics.CompressibilityBeta(coneMach);
            return new VaporConstants
            {
                WingScale = Atmosphere.AirGamma / 2 * wingMach * wingMach,
                WingSonic = Aerodynamics.CriticalPressure(wingMach),
                WingBeta = wingBeta,
                WingKt = wingMach * wingMach / (2 * (1 + wingBeta)),
                WingCap = MostDeficit(wingMach),
                WingReach = WingFieldHeight * f.ReachScale / Math.Max(wingBeta, WingHeightMinBeta),
                WingTravel = ShockTravel * Smoothstep(0.75, 1, wingMach),
                WingSupersonic = wingMach >= 1 ? 1 : 0,
                ConeScale = Atmosphere.AirGamma / 2 * coneMach * coneMach,
                ConeBeta = coneBeta,
                ConeKt = coneMach * coneMach / (2 * (1 + coneBeta)),
                ConeCap = MostDeficit(coneMach),
                ConeReachM = ConeReach * f.BodyLengthM / coneBeta,
            };
        }
    }
}
