// After aeronautic (github.com/Romainlg29/aeronautic @ ca535855f7), packages/wing-vapor/src/measure.ts.
// MIT License, (c) 2026 Romain Le Gall; see THIRD-PARTY-NOTICES.md.
using System;
using System.Collections.Generic;

namespace VortexVapor.Aeronautic
{
    // Measures the airframe from its lifting outlines seen from above.
    public static class PlanformMeasure
    {
        const int Resolution = 256;
        // Runs closer than this along the chord are one (the seams between parts).
        const double SeamM = 0.12;
        const double BodyLeap = 9.5, ResumeLeap = 3, ResumeChord = 0.4;
        const double OuterFrom = 0.5, OuterTo = 0.92;
        const double RootThickness = 1.5, SpanSmoothing = 0.06;
        const int PodStations = 4, MinSecondStations = 4, MinSecondCells = 3;
        const double MinThicknessRatio = 0.02, MaxThicknessRatio = 0.15;
        // An outline whose middle lies this far behind the outer wing's trailing edge, carried
        // inboard, is a tailplane even where it touches the wing (KSP stabilators often do).
        const double AftMarginM = 0.1;
        // A canard's trailing edge stands at least this far ahead of the wing's leading edge.
        const double CanardGapM = 0.15;

        public class Column
        {
            public double Leading, Trailing, Mid, Thickness;
            public bool HasSecond;
            public double SecondLeading, SecondTrailing, SecondMid;
            public double Chord { get { return Trailing - Leading; } }
        }

        public class Result
        {
            public WingShape Shape;
            public double Apex, TanSweep, RootChord, MidRoot, TanDihedral, Thickness;
            public int RootStation, InnerStation;
        }

        // The silhouette: per cell, whether an outline covers it, their mean height and the thickest.
        class View
        {
            public double OriginX, OriginZ, Cell;
            public int Width, Height;
            public int[] Count;
            public float[] YSum, Thick;
        }

        // side: the body's side from KSP (0 if unknown); the wing's root is no further in.
        public static Result Measure(List<SurfacePolygon> lifting, double side)
        {
            double semispan = 0;
            foreach (SurfacePolygon p in lifting) foreach (double z in p.Z) semispan = Math.Max(semispan, Math.Abs(z));
            if (semispan < 0.1) return null;
            int n = WingShape.Stations;
            double step = semispan / (n - 1);
            int outerStation = (int)Math.Floor(OuterFrom * (n - 1));
            int outerLast = (int)Math.Floor(OuterTo * (n - 1));
            View view = Draw(lifting);
            Column[] columns = Columns(view, semispan);
            var outer = new List<int>();
            for (int k = outerStation; k <= outerLast; k++) if (columns[k] != null) outer.Add(k);
            if (outer.Count < 2) return null;

            // Tailplanes touching the wing: measured apart, as upstream's second run.
            double te0, teSlope;
            FitLine(outer, k => step * k, k => columns[k].Trailing, out te0, out teSlope);
            var wingParts = new List<SurfacePolygon>();
            var aftParts = new List<SurfacePolygon>();
            foreach (SurfacePolygon p in lifting)
            {
                double x = 0, z = 0;
                for (int i = 0; i < p.X.Length; i++) { x += p.X[i]; z += Math.Abs(p.Z[i]); }
                x /= p.X.Length; z /= p.X.Length;
                (x > te0 + teSlope * z + AftMarginM ? aftParts : wingParts).Add(p);
            }
            // canards apart too: outlines lying ahead of the outer wing's leading-edge line,
            // their trailing edge clear of it (a LERX runs into the wing's edge, a canard does not).
            double le0, leSlope;
            FitLine(outer, k => step * k, k => columns[k].Leading, out le0, out leSlope);
            var foreParts = new List<SurfacePolygon>();
            for (int i = wingParts.Count - 1; i >= 0; i--)
            {
                SurfacePolygon p = wingParts[i];
                bool clear = true;
                for (int j = 0; j < p.X.Length && clear; j++)
                    clear = p.X[j] < le0 + leSlope * Math.Abs(p.Z[j]) - CanardGapM;
                if (clear) { foreParts.Add(p); wingParts.RemoveAt(i); }
            }
            Column[] fore = null;
            if (foreParts.Count > 0 && wingParts.Count > 0)
            {
                fore = Columns(Draw(foreParts), semispan);
                if (aftParts.Count == 0)
                {
                    view = Draw(wingParts);
                    columns = Columns(view, semispan);
                    outer.Clear();
                    for (int k = outerStation; k <= outerLast; k++) if (columns[k] != null) outer.Add(k);
                    if (outer.Count < 2) return null;
                }
            }
            if (aftParts.Count > 0 && wingParts.Count > 0)
            {
                view = Draw(wingParts);
                columns = Columns(view, semispan);
                Column[] aft = Columns(Draw(aftParts), semispan);
                for (int k = 0; k < n; k++)
                {
                    if (columns[k] == null || aft[k] == null) continue;
                    columns[k].HasSecond = true;
                    columns[k].SecondLeading = aft[k].Leading;
                    columns[k].SecondTrailing = aft[k].Trailing;
                    columns[k].SecondMid = aft[k].Mid;
                }
                outer.Clear();
                for (int k = outerStation; k <= outerLast; k++) if (columns[k] != null) outer.Add(k);
                if (outer.Count < 2) return null;
            }

            // The root: inward until the leading edge leaps forward to the nose.
            int wingRoot = 0;
            Column outboard = null;
            for (int station = outerStation; station >= 0; station--)
            {
                Column column = columns[station];
                if (column == null) continue;
                bool breaks = outboard != null && (outboard.Leading - column.Leading) / step > BodyLeap;
                if (!breaks) { outboard = column; continue; }
                int resumes = -1;
                for (int i = station - 1; i >= Math.Max(station - 2 * PodStations, 0); i--)
                {
                    Column next = columns[i];
                    if (next != null && Math.Abs(outboard.Leading - next.Leading) <= ResumeLeap * step * (station - i)
                        && Math.Abs(Math.Log(Math.Max(next.Chord, view.Cell) / Math.Max(outboard.Chord, view.Cell))) < ResumeChord)
                    {
                        resumes = i;
                        break;
                    }
                }
                if (resumes < 0) { wingRoot = station + 1; break; }
                station = resumes + 1;
            }
            // the table carries on inboard over lifting surface; stops where a canard alone remains.
            int inner = outerStation;
            while (inner > 0 && columns[inner - 1] != null && columns[inner] != null
                   && columns[inner - 1].Trailing > columns[inner].Leading && columns[inner - 1].Leading < columns[inner].Trailing)
                inner--;
            while (inner < n - 2 && columns[inner] == null) inner++;
            int root = Math.Max(wingRoot, inner);
            while (root < n - 2 && (step * root < side || columns[root] == null)) root++;

            // Nacelles and pods cut off the edges.
            var leading = new double[n];
            var trailing = new double[n];
            for (int k = 0; k < n; k++)
            {
                leading[k] = columns[k] != null ? columns[k].Leading : double.NaN;
                trailing[k] = columns[k] != null ? columns[k].Trailing : double.NaN;
            }
            leading = WithoutPods(leading, inner, -1);
            trailing = WithoutPods(trailing, inner, 1);
            for (int k = inner; k < n; k++)
                if (columns[k] != null) { columns[k].Leading = leading[k]; columns[k].Trailing = trailing[k]; }

            var wing = new List<int>();
            for (int k = root; k <= outerLast; k++) if (columns[k] != null) wing.Add(k);
            if (wing.Count < 2) wing = outer;

            double apex, tanSweep, trailing0, trailingSlope, mid0, tanDihedral;
            FitLine(outer, k => step * k, k => columns[k].Leading, out apex, out tanSweep);
            FitLine(outer, k => step * k, k => columns[k].Trailing, out trailing0, out trailingSlope);
            FitLine(wing, k => step * k, k => columns[k].Mid, out mid0, out tanDihedral);
            var ratios = new List<double>();
            foreach (int k in wing) ratios.Add(columns[k].Thickness / Math.Max(columns[k].Chord, view.Cell));
            double thickness = Math.Min(Math.Max(Median(ratios), MinThicknessRatio), MaxThicknessRatio);

            var shape = new WingShape { SemispanM = semispan, RootSpanM = step * root, InnerSpanM = step * inner };
            for (int k = 0; k < n; k++)
            {
                double span = step * k;
                Column column = columns[k];
                if (k < inner || column == null)
                {
                    shape.LeadingM[k] = (float)(apex + tanSweep * span);
                    shape.ChordM[k] = (float)Math.Max(trailing0 + trailingSlope * span - shape.LeadingM[k], view.Cell);
                    shape.MidM[k] = (float)(mid0 + tanDihedral * span);
                    shape.Thickness[k] = (float)thickness;
                }
                else
                {
                    double chord = Math.Max(column.Chord, view.Cell);
                    shape.LeadingM[k] = (float)column.Leading;
                    shape.ChordM[k] = (float)chord;
                    shape.MidM[k] = (float)column.Mid;
                    shape.Thickness[k] = (float)Math.Max(Math.Min(column.Thickness / chord, RootThickness * thickness), MinThicknessRatio);
                }
            }
            SmoothTable(shape, inner);
            Array.Copy(WingShape.LatticeLoading(shape), shape.Loading, n);
            shape.Secondary = Secondary(columns, root, semispan);
            if (fore != null) shape.Canard = Canard(fore, semispan, side);
            if (shape.Secondary == null && shape.Canard != null) shape.Secondary = shape.Canard;

            return new Result
            {
                Shape = shape, Apex = apex, TanSweep = tanSweep, RootChord = Math.Max(trailing0 - apex, view.Cell),
                MidRoot = mid0, TanDihedral = tanDihedral, Thickness = thickness, RootStation = root, InnerStation = inner,
            };
        }

        static Column[] Columns(View view, double semispan)
        {
            int n = WingShape.Stations;
            double step = semispan / (n - 1);
            var columns = new Column[n];
            for (int k = 0; k < n; k++)
            {
                // Half a cell in from the tip, or the tip's own column reads empty.
                double inside = Math.Min(step * k, semispan - view.Cell * 0.5);
                columns[k] = Average(Read(view, inside), Read(view, -inside));
            }
            return columns;
        }

        static View Draw(List<SurfacePolygon> lifting)
        {
            double minX = double.PositiveInfinity, maxX = double.NegativeInfinity, minZ = double.PositiveInfinity, maxZ = double.NegativeInfinity;
            foreach (SurfacePolygon p in lifting)
                for (int i = 0; i < p.X.Length; i++)
                {
                    minX = Math.Min(minX, p.X[i]); maxX = Math.Max(maxX, p.X[i]);
                    minZ = Math.Min(minZ, p.Z[i]); maxZ = Math.Max(maxZ, p.Z[i]);
                }
            double cell = Math.Max(Math.Max(maxX - minX, maxZ - minZ), 1e-6) / Resolution;
            var v = new View { OriginX = minX - cell, OriginZ = minZ - cell, Cell = cell };
            v.Width = (int)Math.Ceiling((maxX - minX) / cell) + 2;
            v.Height = (int)Math.Ceiling((maxZ - minZ) / cell) + 2;
            v.Count = new int[v.Width * v.Height];
            v.YSum = new float[v.Width * v.Height];
            v.Thick = new float[v.Width * v.Height];
            foreach (SurfacePolygon p in lifting)
            {
                var hit = new bool[v.Width * v.Height];
                for (int k = 1; k + 1 < p.X.Length; k++) Fill(v, p, 0, k, k + 1, hit);
            }
            return v;
        }

        // One triangle of an outline: cell centres inside take its height once per outline.
        static void Fill(View v, SurfacePolygon p, int a, int b, int c, bool[] hit)
        {
            double au = (p.X[a] - v.OriginX) / v.Cell, av = (p.Z[a] - v.OriginZ) / v.Cell;
            double bu = (p.X[b] - v.OriginX) / v.Cell, bv = (p.Z[b] - v.OriginZ) / v.Cell;
            double cu = (p.X[c] - v.OriginX) / v.Cell, cv = (p.Z[c] - v.OriginZ) / v.Cell;
            double area = (bu - au) * (cv - av) - (bv - av) * (cu - au);
            if (Math.Abs(area) < 1e-12) return;
            int loU = Math.Max((int)Math.Floor(Math.Min(au, Math.Min(bu, cu))), 0), hiU = Math.Min((int)Math.Ceiling(Math.Max(au, Math.Max(bu, cu))), v.Width - 1);
            int loV = Math.Max((int)Math.Floor(Math.Min(av, Math.Min(bv, cv))), 0), hiV = Math.Min((int)Math.Ceiling(Math.Max(av, Math.Max(bv, cv))), v.Height - 1);
            for (int pv = loV; pv <= hiV; pv++)
                for (int pu = loU; pu <= hiU; pu++)
                {
                    double su = pu + 0.5, sv = pv + 0.5;
                    double wa = ((bu - su) * (cv - sv) - (bv - sv) * (cu - su)) / area;
                    double wb = ((cu - su) * (av - sv) - (cv - sv) * (au - su)) / area;
                    double wc = 1 - wa - wb;
                    int i = pv * v.Width + pu;
                    if (wa < 0 || wb < 0 || wc < 0 || hit[i]) continue;
                    hit[i] = true;
                    v.Count[i]++;
                    v.YSum[i] += (float)(wa * p.Y[a] + wb * p.Y[b] + wc * p.Y[c]);
                    v.Thick[i] = Math.Max(v.Thick[i], (float)p.ThicknessM);
                }
        }

        // Between the two rows of cells round the span, as the field reads its tables.
        static Column Read(View v, double span)
        {
            double at = (span - v.OriginZ) / v.Cell - 0.5;
            int r0 = (int)Math.Floor(at);
            double t = at - r0;
            Column a = ReadRow(v, r0), b = ReadRow(v, r0 + 1);
            if (a == null || b == null) return t < 0.5 ? a : b;
            var c = new Column
            {
                Leading = a.Leading + (b.Leading - a.Leading) * t,
                Trailing = a.Trailing + (b.Trailing - a.Trailing) * t,
                Mid = a.Mid + (b.Mid - a.Mid) * t,
                Thickness = a.Thickness + (b.Thickness - a.Thickness) * t,
            };
            if (a.HasSecond && b.HasSecond)
            {
                c.HasSecond = true;
                c.SecondLeading = a.SecondLeading + (b.SecondLeading - a.SecondLeading) * t;
                c.SecondTrailing = a.SecondTrailing + (b.SecondTrailing - a.SecondTrailing) * t;
                c.SecondMid = a.SecondMid + (b.SecondMid - a.SecondMid) * t;
            }
            else if (a.HasSecond || b.HasSecond)
            {
                Column s = (t < 0.5 ? a : b).HasSecond ? (t < 0.5 ? a : b) : null;
                if (s != null) { c.HasSecond = true; c.SecondLeading = s.SecondLeading; c.SecondTrailing = s.SecondTrailing; c.SecondMid = s.SecondMid; }
            }
            return c;
        }

        // upstream's read_column on one row of the silhouette.
        static Column ReadRow(View v, int row)
        {
            if (row < 0 || row >= v.Height) return null;
            double gap = Math.Max(1.5 * v.Cell, SeamM);
            int bestStart = -1, bestEnd = -1, nextStart = -1, nextEnd = -1;
            int start = -1, last = -1;
            for (int u = 0; u <= v.Width; u++)
            {
                bool filled = u < v.Width && v.Count[row * v.Width + u] > 0;
                if (filled)
                {
                    if (start >= 0 && (u - last) * v.Cell > gap) { Close(start, last, ref bestStart, ref bestEnd, ref nextStart, ref nextEnd); start = -1; }
                    if (start < 0) start = u;
                    last = u;
                }
                else if (u == v.Width && start >= 0) Close(start, last, ref bestStart, ref bestEnd, ref nextStart, ref nextEnd);
            }
            if (bestStart < 0) return null;
            var column = new Column
            {
                Leading = v.OriginX + bestStart * v.Cell,
                Trailing = v.OriginX + (bestEnd + 1) * v.Cell,
                Mid = MedianMid(v, row, bestStart, bestEnd),
                Thickness = MedianThick(v, row, bestStart, bestEnd),
            };
            if (nextStart >= 0 && nextEnd - nextStart + 1 >= MinSecondCells)
            {
                column.HasSecond = true;
                column.SecondLeading = v.OriginX + nextStart * v.Cell;
                column.SecondTrailing = v.OriginX + (nextEnd + 1) * v.Cell;
                column.SecondMid = MedianMid(v, row, nextStart, nextEnd);
            }
            return column;
        }

        static void Close(int start, int end, ref int bestStart, ref int bestEnd, ref int nextStart, ref int nextEnd)
        {
            int length = end - start;
            if (bestStart < 0 || length > bestEnd - bestStart)
            {
                nextStart = bestStart; nextEnd = bestEnd;
                bestStart = start; bestEnd = end;
            }
            else if (nextStart < 0 || length > nextEnd - nextStart)
            {
                nextStart = start; nextEnd = end;
            }
        }

        static double MedianMid(View v, int row, int from, int to)
        {
            var values = new List<double>();
            for (int u = from; u <= to; u++) { int i = row * v.Width + u; if (v.Count[i] > 0) values.Add(v.YSum[i] / v.Count[i]); }
            return Median(values);
        }

        static double MedianThick(View v, int row, int from, int to)
        {
            var values = new List<double>();
            for (int u = from; u <= to; u++) { int i = row * v.Width + u; if (v.Count[i] > 0) values.Add(v.Thick[i]); }
            return Median(values);
        }

        static Column Average(Column right, Column left)
        {
            if (right == null || left == null) return right ?? left;
            var c = new Column
            {
                Leading = (right.Leading + left.Leading) / 2,
                Trailing = (right.Trailing + left.Trailing) / 2,
                Thickness = (right.Thickness + left.Thickness) / 2,
                Mid = (right.Mid + left.Mid) / 2,
            };
            if (right.HasSecond && left.HasSecond)
            {
                c.HasSecond = true;
                c.SecondLeading = (right.SecondLeading + left.SecondLeading) / 2;
                c.SecondTrailing = (right.SecondTrailing + left.SecondTrailing) / 2;
                c.SecondMid = (right.SecondMid + left.SecondMid) / 2;
            }
            else if (right.HasSecond || left.HasSecond)
            {
                Column s = right.HasSecond ? right : left;
                c.HasSecond = true;
                c.SecondLeading = s.SecondLeading; c.SecondTrailing = s.SecondTrailing; c.SecondMid = s.SecondMid;
            }
            return c;
        }

        // upstream's without_pods, ends padded by extending the edge straight.
        static double[] WithoutPods(double[] edge, int from, int outward)
        {
            int last = edge.Length - 1;
            Func<double[], int, double> at = (e, s) =>
            {
                if (s >= from && s <= last) return e[s];
                int end = s < from ? from : last, inside = s < from ? Math.Min(from + 1, last) : Math.Max(last - 1, from);
                double slope = double.IsNaN(e[inside]) || inside == end ? 0 : (e[end] - e[inside]) / (end - inside);
                return e[end] + slope * (s - end);
            };
            var flipped = new double[edge.Length];
            for (int i = 0; i < edge.Length; i++) flipped[i] = edge[i] * outward;
            double[] eroded = Sweep(flipped, from, at, true);
            double[] opened = Sweep(eroded, from, at, false, flipped);
            var result = new double[edge.Length];
            for (int i = 0; i < edge.Length; i++)
                result[i] = i < from || double.IsNaN(edge[i]) ? edge[i] : opened[i] * outward;
            return result;
        }

        static double[] Sweep(double[] read, int from, Func<double[], int, double> at, bool min, double[] original = null)
        {
            double[] source = original ?? read;
            var outp = new double[read.Length];
            for (int s = 0; s < read.Length; s++)
            {
                if (s < from || double.IsNaN(source[s])) { outp[s] = source[s]; continue; }
                double picked = at(read, s);
                for (int o = -PodStations; o <= PodStations; o++)
                {
                    double other = at(read, s + o);
                    if (!double.IsNaN(other)) picked = min ? Math.Min(picked, other) : Math.Max(picked, other);
                }
                outp[s] = picked;
            }
            return outp;
        }

        // upstream's smooth_table: a Gaussian-weighted straight line through the stations round each.
        static void SmoothTable(WingShape shape, int from)
        {
            int n = WingShape.Stations;
            double sigma = SpanSmoothing * (n - 1);
            int reach = (int)Math.Ceiling(3 * sigma);
            var rows = new float[4][];
            rows[0] = (float[])shape.LeadingM.Clone();
            rows[1] = new float[n];
            for (int k = 0; k < n; k++) rows[1][k] = shape.LeadingM[k] + shape.ChordM[k];
            rows[2] = (float[])shape.MidM.Clone();
            rows[3] = (float[])shape.Thickness.Clone();
            var xs = new List<int>();
            var fitted = new double[4];
            for (int station = from; station < n; station++)
            {
                xs.Clear();
                for (int other = Math.Max(station - reach, from); other <= Math.Min(station + reach, n - 1); other++) xs.Add(other - station);
                for (int r = 0; r < 4; r++)
                {
                    double intercept, slope;
                    float[] row = rows[r];
                    int at = station;
                    FitLine(xs, x => x, x => row[at + x], out intercept, out slope, x => Math.Exp(-(x * x) / (2 * sigma * sigma)));
                    fitted[r] = intercept;
                }
                shape.LeadingM[station] = (float)fitted[0];
                shape.ChordM[station] = (float)Math.Max(fitted[1] - fitted[0], 1e-3);
                shape.MidM[station] = (float)fitted[2];
                shape.Thickness[station] = (float)fitted[3];
            }
        }

        // the canard from its own silhouette, from the body's side to its tip. One that does
        // not reach 0.3 of the wing's semispan, or past the body's side, is a chine or strake.
        static SecondarySurface Canard(Column[] fore, double semispan, double side)
        {
            int n = WingShape.Stations;
            double step = semispan / (n - 1);
            int first = -1, tip = -1;
            double area = 0;
            int from = Math.Min((int)Math.Ceiling(side / step), n - 1);
            for (int s = from; s < n; s++)
            {
                if (fore[s] == null) { if (first >= 0) break; continue; }
                if (first < 0) first = s;
                tip = s;
                area += fore[s].Chord * step;
            }
            if (first < 0 || tip < first + MinSecondStations - 1 || (tip + 0.5) * step < Math.Max(0.3 * semispan, side + CanardGapM)) return null;
            Column a = fore[first], outer = fore[tip], inner = fore[Math.Max(tip - 1, first)];
            // A canard tapers or is straight; an outline widening outboard is a chine running into the wing.
            if ((outer.Chord + inner.Chord) / 2 > 1.2 * a.Chord) return null;
            return new SecondarySurface
            {
                IsCanard = true,
                RootSpanM = first * step,
                RootLeadingM = a.Leading,
                RootChordM = Math.Max(a.Chord, step),
                RootHeightM = a.Mid,
                SemispanM = (tip + 0.5) * step,
                TipLeadingM = (outer.Leading + inner.Leading) / 2,
                TipChordM = Math.Max((outer.Chord + inner.Chord) / 2, step),
                TipHeightM = (outer.Mid + inner.Mid) / 2,
                AreaM2 = area * 2,
            };
        }

        // upstream's measure_secondary.
        static SecondarySurface Secondary(Column[] columns, int root, double semispan)
        {
            int n = WingShape.Stations;
            double step = semispan / (n - 1);
            int tip = -1, ahead = 0;
            double area = 0;
            for (int s = root; s < n; s++)
            {
                Column c = columns[s];
                if (c == null || !c.HasSecond) break;
                tip = s;
                area += (c.SecondTrailing - c.SecondLeading) * step;
                ahead += c.SecondLeading < c.Leading ? 1 : -1;
            }
            if (tip < root + MinSecondStations - 1) return null;
            Column outer = columns[tip], innerColumn = columns[Math.Max(tip - 1, root)], rootColumn = columns[root];
            area += (rootColumn.SecondTrailing - rootColumn.SecondLeading) * root * step;
            // Where it starts: inboard of the wing's root while the silhouette still sees it.
            int first = root;
            while (first > 0 && columns[first - 1] != null && columns[first - 1].HasSecond) first--;
            Column firstColumn = columns[first];
            return new SecondarySurface
            {
                RootSpanM = first * step,
                RootLeadingM = firstColumn.SecondLeading,
                RootChordM = Math.Max(firstColumn.SecondTrailing - firstColumn.SecondLeading, step),
                RootHeightM = firstColumn.SecondMid,
                IsCanard = ahead > 0,
                SemispanM = (tip + 0.5) * step,
                TipLeadingM = (outer.SecondLeading + innerColumn.SecondLeading) / 2,
                TipChordM = Math.Max((outer.SecondTrailing - outer.SecondLeading + innerColumn.SecondTrailing - innerColumn.SecondLeading) / 2, step),
                TipHeightM = (outer.SecondMid + innerColumn.SecondMid) / 2,
                AreaM2 = area * 2,
            };
        }

        static void FitLine<T>(List<T> items, Func<T, double> x, Func<T, double> y, out double intercept, out double slope, Func<T, double> weight = null)
        {
            double total = 0, mx = 0, my = 0;
            foreach (T i in items) { double w = weight != null ? weight(i) : 1; total += w; mx += w * x(i); my += w * y(i); }
            mx /= total; my /= total;
            double cov = 0, var = 0;
            foreach (T i in items) { double w = weight != null ? weight(i) : 1; cov += w * (x(i) - mx) * (y(i) - my); var += w * (x(i) - mx) * (x(i) - mx); }
            slope = var > 0 ? cov / var : 0;
            intercept = my - slope * mx;
        }

        static double Median(List<double> values)
        {
            if (values.Count == 0) return 0;
            values.Sort();
            int m = values.Count / 2;
            return values.Count % 2 == 1 ? values[m] : (values[m - 1] + values[m]) / 2;
        }
    }
}
