// Ported from aeronautic (github.com/Romainlg29/aeronautic @ ca535855f7), packages/wing-vapor/src/wing-shape.ts.
// MIT License, (c) 2026 Romain Le Gall; see THIRD-PARTY-NOTICES.md.
using System;

namespace VortexVapor.Aeronautic
{
    public class SecondarySurface
    {
        public bool IsCanard;   // ahead of the wing and lifting; else a tailplane behind it, trimming
        public double SemispanM, TipLeadingM, TipChordM, TipHeightM, AreaM2;
        // the canard's root, for its own sheet.
        public double RootSpanM, RootLeadingM, RootChordM, RootHeightM;
    }

    // One wing, station by station from the centreline to the tip: station k is k / (Stations - 1)
    // of the semispan out. Arrays are floats, as upstream's Float32Arrays are.
    public class WingShape
    {
        public const int Stations = 64;
        const int LatticePanels = 24;

        public double SemispanM, RootSpanM;
        // where the lifting surface starts inboard of the root (a LERX
        // or a lifting body), and which of it is wing seen from above. Null cover: upstream's sheet.
        public double InnerSpanM;
        public WingCover Cover;
        public float[] LeadingM = new float[Stations], ChordM = new float[Stations], MidM = new float[Stations];
        public float[] Thickness = new float[Stations], Loading = new float[Stations];
        public SecondarySurface Secondary;
        // a canard measured apart from the wing (an outline wholly ahead of
        // the wing's leading edge with a gap to it), for its own sheet. Null: none.
        public SecondarySurface Canard;

        public struct Station
        {
            public double LeadingM, ChordM, MidM, Thickness, Loading;
        }

        // Shallow: the tables are shared.
        public WingShape Copy() { return (WingShape)MemberwiseClone(); }

        public Station At(double span)
        {
            double share = Math.Abs(span) / SemispanM;
            int along = (int)(Math.Min(share, 1) * (Stations - 1));
            int next = Math.Min(along + 1, Stations - 1);
            double t = Math.Min(Math.Max(share * (Stations - 1) - along, 0), 1);
            return new Station
            {
                LeadingM = LeadingM[along] + ((double)LeadingM[next] - LeadingM[along]) * t,
                ChordM = ChordM[along] + ((double)ChordM[next] - ChordM[along]) * t,
                MidM = MidM[along] + ((double)MidM[next] - MidM[along]) * t,
                Thickness = Thickness[along] + ((double)Thickness[next] - Thickness[along]) * t,
                Loading = Loading[along] + ((double)Loading[next] - Loading[along]) * t,
            };
        }

        // one or two stations whose leading or trailing edge stands SpikeM beyond both
        // neighbours (a canard's tip caught in the table) go back on the line between them.
        public void Despike(double spikeM)
        {
            var trailing = new double[Stations];
            for (int k = 0; k < Stations; k++) trailing[k] = (double)LeadingM[k] + ChordM[k];
            var leading = new double[Stations];
            for (int k = 0; k < Stations; k++) leading[k] = LeadingM[k];
            DespikeEdge(leading, spikeM);
            DespikeEdge(trailing, spikeM);
            for (int k = 0; k < Stations; k++)
            {
                LeadingM[k] = (float)leading[k];
                ChordM[k] = (float)Math.Max(trailing[k] - leading[k], 0.05);
            }
        }

        static void DespikeEdge(double[] edge, double spikeM)
        {
            for (int width = 1; width <= 2; width++)
                for (int k = 1; k + width < edge.Length; k++)
                {
                    double a = edge[k - 1], b = edge[k + width];
                    int sign = 0;
                    bool spike = true;
                    for (int j = 0; j < width && spike; j++)
                    {
                        double e = edge[k + j];
                        int s = e > Math.Max(a, b) + spikeM ? 1 : e < Math.Min(a, b) - spikeM ? -1 : 0;
                        if (s == 0 || (sign != 0 && s != sign)) spike = false;
                        sign = s;
                    }
                    if (!spike) continue;
                    for (int j = 0; j < width; j++) edge[k + j] = a + (b - a) * (j + 1) / (width + 1);
                }
        }

        public double StationSpan(int station)
        {
            return SemispanM * station / (Stations - 1);
        }

        public static WingShape Trapezoid(VaporAirframe a)
        {
            double semispan = Math.Max(a.SpanM / 2, 1e-3);
            double tanSweep = Math.Tan(a.LeadingEdgeSweepRad), tanDihedral = Math.Tan(a.DihedralRad);
            var shape = new WingShape { SemispanM = semispan, RootSpanM = Math.Min(Math.Max(a.RootSpanM, 0), semispan) };
            for (int k = 0; k < Stations; k++)
            {
                double span = shape.StationSpan(k);
                shape.LeadingM[k] = (float)(a.ApexM + span * tanSweep);
                shape.ChordM[k] = (float)(a.RootChordM + (a.TipChordM - a.RootChordM) * span / semispan);
                shape.MidM[k] = (float)(a.WingHeightM + span * tanDihedral);
                shape.Thickness[k] = (float)a.Thickness;
            }
            float[] loading = LatticeLoading(shape);
            Array.Copy(loading, shape.Loading, Stations);
            return shape;
        }

        public void PlanformOf(out double areaM2, out double spanM, out double sweepRad, out double rootChordM, out double tipChordM)
        {
            double area = 0;
            for (int k = 1; k < Stations; k++)
            {
                double step = SemispanM / (Stations - 1);
                area += ((double)ChordM[k - 1] + ChordM[k]) / 2 * step;
            }
            Station root = At(RootSpanM);
            int tip = Stations - 1;
            double outM = SemispanM - RootSpanM;
            areaM2 = area * 2;
            spanM = SemispanM * 2;
            sweepRad = outM > 1e-3 ? Math.Atan((LeadingM[tip] - root.LeadingM) / outM) : 0;
            rootChordM = ChordM[0];
            tipChordM = ChordM[tip];
        }

        // Weissinger's lifting line: the spanwise loading's shape, over its mean.
        public static float[] LatticeLoading(WingShape shape)
        {
            int panels = LatticePanels * 2;
            double semispan = shape.SemispanM;
            Func<int, double> edge = i => -semispan * Math.Cos(Math.PI * i / panels);
            Func<double, double> quarter = span => { Station s = shape.At(span); return s.LeadingM + 0.25 * s.ChordM; };

            var controlX = new double[panels];
            var controlZ = new double[panels];
            for (int i = 0; i < panels; i++)
            {
                double span = (edge(i) + edge(i + 1)) / 2;
                Station s = shape.At(span);
                controlX[i] = s.LeadingM + 0.75 * s.ChordM;
                controlZ[i] = span;
            }

            double far = semispan * 1e3;
            var matrix = new double[panels][];
            for (int i = 0; i < panels; i++)
            {
                matrix[i] = new double[panels];
                double px = controlX[i], pz = controlZ[i];
                for (int j = 0; j < panels; j++)
                {
                    double za = edge(j), zb = edge(j + 1);
                    double xa = quarter(za), xb = quarter(zb);
                    matrix[i][j] = Segment(px, pz, far, za, xa, za) + Segment(px, pz, xa, za, xb, zb) + Segment(px, pz, xb, zb, far, zb);
                }
            }
            var right = new double[panels];
            for (int i = 0; i < panels; i++) right[i] = 1;
            double[] circulation = Solve(matrix, right);

            var loading = new float[Stations];
            for (int k = 0; k < Stations; k++)
            {
                double span = shape.StationSpan(k);
                loading[k] = (float)((Interpolate(controlZ, circulation, span) + Interpolate(controlZ, circulation, -span)) / 2);
            }
            loading[Stations - 1] = 0;

            double mean = 0;
            for (int k = 1; k < Stations; k++) mean += ((double)loading[k - 1] + loading[k]) / 2;
            mean /= Stations - 1;
            for (int k = 0; k < Stations; k++) loading[k] = (float)Math.Max(loading[k] / (mean != 0 ? mean : 1), 0);
            return loading;
        }

        static double Segment(double px, double pz, double ax, double az, double bx, double bz)
        {
            double r1x = px - ax, r1z = pz - az, r2x = px - bx, r2z = pz - bz;
            double cross = r1x * r2z - r1z * r2x;
            if (Math.Abs(cross) < 1e-10) return 0;
            double r1 = Hypot(r1x, r1z), r2 = Hypot(r2x, r2z);
            double r0x = bx - ax, r0z = bz - az;
            double along = r0x * (r1x / r1 - r2x / r2) + r0z * (r1z / r1 - r2z / r2);
            return along / cross / (4 * Math.PI);
        }

        static double[] Solve(double[][] matrix, double[] right)
        {
            int size = right.Length;
            for (int column = 0; column < size; column++)
            {
                int pivot = column;
                for (int row = column + 1; row < size; row++)
                    if (Math.Abs(matrix[row][column]) > Math.Abs(matrix[pivot][column])) pivot = row;
                double[] swapRow = matrix[column]; matrix[column] = matrix[pivot]; matrix[pivot] = swapRow;
                double swap = right[column]; right[column] = right[pivot]; right[pivot] = swap;
                double lead = matrix[column][column];
                if (lead == 0) lead = 1e-12;
                for (int row = column + 1; row < size; row++)
                {
                    double factor = matrix[row][column] / lead;
                    if (factor == 0) continue;
                    for (int k = column; k < size; k++) matrix[row][k] -= factor * matrix[column][k];
                    right[row] -= factor * right[column];
                }
            }
            var solution = new double[size];
            for (int row = size - 1; row >= 0; row--)
            {
                double sum = right[row];
                for (int k = row + 1; k < size; k++) sum -= matrix[row][k] * solution[k];
                double d = matrix[row][row];
                solution[row] = sum / (d != 0 ? d : 1e-12);
            }
            return solution;
        }

        static double Interpolate(double[] at, double[] values, double x)
        {
            if (x <= at[0]) return values[0];
            if (x >= at[at.Length - 1]) return values[values.Length - 1];
            int i = 1;
            while (at[i] < x) i++;
            double t = (x - at[i - 1]) / (at[i] - at[i - 1]);
            return values[i - 1] + (values[i] - values[i - 1]) * t;
        }

        // Math.hypot's result for finite inputs; the checker's tolerance covers its last bit.
        internal static double Hypot(double x, double y) { return Math.Sqrt(x * x + y * y); }
    }
}
