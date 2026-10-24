// Ported from aeronautic (github.com/Romainlg29/aeronautic @ ca535855f7), packages/wing-vapor/src/types.ts.
// MIT License, (c) 2026 Romain Le Gall; see THIRD-PARTY-NOTICES.md.
namespace VortexVapor.Aeronautic
{
    // The airframe as the field reads it, in its canonical frame: x aft, y up, z out along the
    // right wing, metres from its origin.
    public class VaporAirframe
    {
        public double SpanM, RootChordM, TipChordM, LeadingEdgeSweepRad, ApexM, WingHeightM, DihedralRad, Thickness, RootSpanM;
        public double NoseM, FuselageLengthM, FuselageRadiusM, FuselageHeightM;
        public double MassKg, LeadingEdgeSharpness;
        public double TipCoreRadius, TipCoreShare, LeadingEdgeCoreRadius;

        // aeronautic's docs fighter.
        public static VaporAirframe Default()
        {
            return new VaporAirframe
            {
                SpanM = 14, RootChordM = 9.2, TipChordM = 1.6, LeadingEdgeSweepRad = 55 * System.Math.PI / 180, ApexM = -5.66,
                WingHeightM = 0.08, DihedralRad = -3 * System.Math.PI / 180, Thickness = 0.06, RootSpanM = 1.6,
                NoseM = 9.9, FuselageLengthM = 18.5, FuselageRadiusM = 1.4, FuselageHeightM = 0,
                MassKg = 20000, LeadingEdgeSharpness = 1,
                TipCoreRadius = 0.02, TipCoreShare = 0.35, LeadingEdgeCoreRadius = 0.02,
            };
        }

        public VaporAirframe Clone() { return (VaporAirframe)MemberwiseClone(); }
    }

    public struct VaporFlight
    {
        public double AirspeedMPerS, AngleOfAttackRad, SideslipRad, RollRateRadPerS;
        // how much of the sheet stands over the fuselage, 0 to 1.
        public double BodyCover;
        // how much of the canard sheet's lift is held back, 0 to 1.
        public double CanardCut;
        // how much of the wing's sheet is held back (0 to 1); the leading-edge vortex drawn
        // as a cord of RopeRadiusM, RopeGain times the condensing deficit (0: upstream's vortex).
        public double SheetCut, RopeGain, RopeRadiusM;
    }

    public struct VaporEffects
    {
        public bool TipVortices, LeadingEdgeVortices, Wing, Cone, SelfShadow;

        public static VaporEffects Default()
        {
            return new VaporEffects { TipVortices = true, LeadingEdgeVortices = true, Wing = true, Cone = true, SelfShadow = true };
        }
    }

    public class VaporLook
    {
        public double SunIntensity = 3, SkyIntensity = 1, Exposure = 1;
        public double DropletRadiusM = 1e-6, HumiditySpread = 0.06, EddyM = 0.6, ShutterS = 1.0 / 60, Anisotropy = 0.75;
        // Multiplier for the wing field's vertical reach.
        public double ReachScale = 1;
    }
}
