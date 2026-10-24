using System;
using System.Collections.Generic;

namespace VortexVapor.Aeronautic
{
    // A lifting part's outline in the airframe's canonical frame (x aft, y up, z along a wing).
    public class SurfacePolygon
    {
        public double[] X, Y, Z;    // the outline's corners, in order
        public double ThicknessM;   // the part's extent along its normal
        public double NormalY;      // |y| of its unit normal: 1 for a flat wing, 0 for a fin
    }

    // The wing table and airframe for aeronautic's field, measured from the craft's lifting parts.
    public static class AirframeBuilder
    {
        // A part whose normal is closer to horizontal than this is a fin, not a wing.
        const double MinNormalY = 0.7;
        // The body's radius counts as its side only when narrower than this share of the semispan.
        const double BodyReachShare = 0.5;

        public static bool Build(List<SurfacePolygon> parts, double noseX, double tailX, double bodyRadiusM, double bodyHeightM,
                                 double massKg, out VaporAirframe airframe, out WingShape shape)
        {
            airframe = null;
            shape = null;
            var lifting = new List<SurfacePolygon>();
            double furthest = 0;
            foreach (SurfacePolygon p in parts)
            {
                if (p.NormalY < MinNormalY || p.X.Length < 3) continue;
                lifting.Add(p);
                furthest = Math.Max(furthest, Reach(p));
            }
            if (lifting.Count == 0) return false;
            // The body's side, when the body is narrow beside the wings.
            double side = bodyRadiusM < BodyReachShare * furthest ? bodyRadiusM : 0;
            PlanformMeasure.Result m = PlanformMeasure.Measure(lifting, side);
            if (m == null) return false;
            shape = m.Shape;

            int tip = WingShape.Stations - 1;
            airframe = VaporAirframe.Default();
            airframe.SpanM = 2 * shape.SemispanM;
            airframe.RootChordM = m.RootChord;
            airframe.TipChordM = shape.ChordM[tip];
            airframe.LeadingEdgeSweepRad = Math.Atan(m.TanSweep);
            airframe.ApexM = m.Apex;
            airframe.WingHeightM = m.MidRoot;
            airframe.DihedralRad = Math.Atan(m.TanDihedral);
            airframe.Thickness = m.Thickness;
            airframe.RootSpanM = shape.RootSpanM;
            airframe.NoseM = -noseX;
            airframe.FuselageLengthM = Math.Max(tailX - noseX, 0.5);
            airframe.FuselageRadiusM = Math.Max(bodyRadiusM, 0.1);
            airframe.FuselageHeightM = bodyHeightM;
            airframe.MassKg = Math.Max(massKg, 1);
            return true;
        }

        static double Reach(SurfacePolygon p)
        {
            double r = 0;
            foreach (double z in p.Z) r = Math.Max(r, Math.Abs(z));
            return r;
        }
    }
}
