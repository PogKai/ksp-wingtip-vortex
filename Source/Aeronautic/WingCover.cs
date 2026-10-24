using System;
using System.Collections.Generic;

namespace VortexVapor.Aeronautic
{
    // the craft seen from above, folded: what is wing, the tops, and the mantle.
    public class WingCover
    {
        // Raster cells along the box's longest side.
        public const int Resolution = 128;
        // A lifting outline whose top is within this of the craft's top surface is that surface.
        const double TopToleranceM = 0.15;
        const int BlurPasses = 2;
        // Top: nothing there.
        public const float Empty = -1e4f;
        // The sheet is raised at most this far above the wing's surface (a fin's top is not the body's).
        public const double MaxRiseM = 1.2;
        // a leading or trailing edge this far beyond both neighbours at one or two stations is a spike.
        public const double SpikeM = 0.4;
        // With the whole cover on, the sheet's height is capped at this many mean chords, not one.
        public static double ReachGrowth = 1;
        // Median body rise under FlatCraftM: flat all over (F-22); past RoundCraftM only the flat parts.
        public static double FlatCraftM = 0.42, RoundCraftM = 0.47;
        // The sheet's height scale is capped at this share of the semispan as well as at the mean
        // chord: a wing of huge chord for its span (Have Blue) stands no taller than a fighter's.
        public static double ReachSpanShare = 0.6;
        // the sheet stands as tall on every craft at the same g and speed: its height is
        // scaled as if the wing's reach chord were this long (the F-22's, measured 3.3 m).
        public static double ReferenceChordM = 3.3;
        // deficit falls as exp(-(h/reach)^ProfilePower) under the cover.
        public static double ProfilePower = 2;
        // the sheet leans aft with height.
        public static double FlowLean = 0.8;
        // on the wing the sheet starts on what rises from it (a nacelle's hump); 0 leaves it on the wing.
        public static double RaiseOnWing = 1;

        public static double Profile(double height, double reach, bool covered)
        {
            double r = height / Math.Max(reach, 1e-6);
            return Math.Exp(covered ? -Math.Pow(Math.Max(r, 0), ProfilePower) : -r);
        }

        // How far up the profile falls from `ratio` times the deficit needed to the deficit needed, in reaches.
        public static double ProfileTop(double ratio, bool covered)
        {
            double l = Math.Log(Math.Max(ratio, 1));
            return covered ? Math.Pow(l, 1 / ProfilePower) : l;
        }

        // Anything narrower than twice this, seen from above (a fin, an intake or a grille on a
        // LERX, an antenna), is left out of the top surface: the air flows round it.
        const double NarrowM = 0.6;
        // ...and for deciding what is wing, only what is narrower than twice this (an intake or a
        // grille on a LERX): a small craft's round body is not much wider than a fin.
        const double GrilleM = 0.3;

        public double OriginX, CellM;
        public int Width, Height;   // cells along x, and along the folded span from z = 0
        public float[] Wing, Top;
        // The top with nothing cut off: what decides whether a body is flat or round (a canopy or a
        // round fuselage's crown is round, though narrow).
        public float[] RawTop;
        float[] sourceTriangles;
        Dictionary<int, List<int>> roofBins;
        public WingShape SourceShape;
        public bool[] SourceExposed;

        // Exact intersections retain obstructions discarded by the smoothed vapor cover.
        public double MeshRoof(double x, double z)
        {
            if (sourceTriangles == null) return SampleRawRoof(x, z);
            if (roofBins == null)
            {
                roofBins = new Dictionary<int, List<int>>();
                for (int t = 0; t + 8 < sourceTriangles.Length; t += 9)
                {
                    int lo = (int)Math.Floor((Math.Min(sourceTriangles[t + 2], Math.Min(sourceTriangles[t + 5], sourceTriangles[t + 8])) - 1e-6) / CellM);
                    int hi = (int)Math.Floor((Math.Max(sourceTriangles[t + 2], Math.Max(sourceTriangles[t + 5], sourceTriangles[t + 8])) + 1e-6) / CellM);
                    for (int bin = lo; bin <= hi; bin++)
                    {
                        List<int> entries;
                        if (!roofBins.TryGetValue(bin, out entries)) roofBins.Add(bin, entries = new List<int>());
                        entries.Add(t);
                    }
                }
            }
            List<int> candidates;
            if (!roofBins.TryGetValue((int)Math.Floor(z / CellM), out candidates)) return Empty;
            double roof = Empty;
            foreach (int i in candidates)
            {
                double ax = sourceTriangles[i], az = sourceTriangles[i + 2];
                double bx = sourceTriangles[i + 3] - ax, bz = sourceTriangles[i + 5] - az;
                double cx = sourceTriangles[i + 6] - ax, cz = sourceTriangles[i + 8] - az;
                double det = bx * cz - bz * cx;
                if (Math.Abs(det) < 1e-10) continue;
                double u = ((x - ax) * cz - (z - az) * cx) / det;
                double v = (bx * (z - az) - bz * (x - ax)) / det;
                if (u < -1e-7 || v < -1e-7 || u + v > 1.0000001) continue;
                roof = Math.Max(roof, sourceTriangles[i + 1] + u * (sourceTriangles[i + 4] - sourceTriangles[i + 1]) + v * (sourceTriangles[i + 7] - sourceTriangles[i + 1]));
            }
            return roof;
        }
        // the mesh's top without its thin parts (fins), spread and smoothed: what the sheet wraps.
        public float[] Mantle;
        const double MantleThinM = 0.2, MantleSpreadM = 0.35;
        const int MantleBlurPasses = 2;
        // A rise up to WrapFull sheet heights is wrapped, none past WrapNone; nor one taller than
        // WrapNoneM (an F-4's spine over its low wing), whatever the sheet.
        public const double WrapFull = 1, WrapNone = 1.5, WrapFullM = 0.6, WrapNoneM = 1.0;
        // A flat craft's body seen from above (BuildBody).
        public float[] Body;
        public double BodyMinX, BodyMaxX, BodyTopM, BodySpanM;
        public bool ForebodyJoined;
        // The body's cloud against the wing's at the root's suction peak, and how high it stands at
        // its edges against its middle: a dome over the middle, not a slab (the F-22 photo).
        public static double BodyStrength = 0.6, BodyDomeEdge = 0.35, BodyReach = 0.5;

        // How high the body's cloud reaches here, as a share of its reach at the middle.
        public double BodyDome(double span)
        {
            double t = Math.Min(Math.Abs(span) / Math.Max(BodySpanM, 1e-3), 1);
            return BodyDomeEdge + (1 - BodyDomeEdge) * (1 - t * t);
        }
        // Upstream's measure_body over the same view: the round body of the largest cross-section
        // inside the wing's root, and where its middle is. 0 radius: none found.
        public double BodyRadiusM, BodyHeightM;
        // 1 for a craft flat all over, 0 for one with a round body (FlatCraftM).
        public double CraftFlat;

        // The body as upstream measures it, in place of the lifting-part outlines' estimate.
        public static void SizeBody(WingCover cover, VaporAirframe airframe)
        {
            if (cover == null || cover.BodyRadiusM <= 0) return;
            airframe.FuselageRadiusM = Math.Max(cover.BodyRadiusM, 0.1);
            airframe.FuselageHeightM = cover.BodyHeightM;
        }

        // Bilinear between cell centres; 0 (Empty for the top) outside.
        public double Sample(double x, double span) { return Sample(Wing, 0, x, span); }
        public double SampleBody(double x, double span) { return Body == null ? 0 : Sample(Body, 0, x, span); }
        public double SampleTop(double x, double span) { return SampleHeight(Top, x, span, 0.5); }
        public double SampleRawTop(double x, double span) { return SampleHeight(RawTop, x, span, 0.5); }
        public double SampleRawRoof(double x, double span)
        {
            return SampleHeight(RawTop, x, span, 1e-6);
        }

        double SampleHeight(float[] grid, double x, double span, double minimumWeight)
        {
            if (grid == null) return Empty;
            double u = (x - OriginX) / CellM - 0.5, v = Math.Abs(span) / CellM - 0.5;
            if (u < -0.5 || v > Height - 0.5 || u > Width - 0.5) return Empty;
            int u0 = (int)Math.Floor(u), v0 = (int)Math.Floor(v);
            double tu = u - u0, tv = v - v0, total = 0, weight = 0;
            for (int a = 0; a <= 1; a++)
                for (int b = 0; b <= 1; b++)
                {
                    double value = At(grid, Empty, u0 + a, v0 + b);
                    double w = (a == 0 ? 1 - tu : tu) * (b == 0 ? 1 - tv : tv);
                    if (value <= Empty || w <= 0) continue;
                    total += w * value; weight += w;
                }
            return weight >= minimumWeight ? total / weight : Empty;
        }
        public double SampleMantle(double x, double span) { return SampleHeight(Mantle, x, span, 0.5); }
        public double BodyBase(double x, double span)
        {
            double top = SampleTop(x, span);
            return Math.Max(top, Math.Min(SampleMantle(x, span), top + MaxRiseM));
        }

        // the sheet's base, on the mantle where it rises less than the sheet is tall.
        public double Base(double surface, double wing, double sheet, double x, double span, double maxRise = MaxRiseM)
        {
            double rise = Math.Min(Math.Max(SampleMantle(x, span) - surface, 0), maxRise);
            sheet = Math.Max(sheet, 1e-4);
            // the sheet rises with the surface up to what it wraps, then stays there (hidden inside a
            // taller part) instead of dropping back, which left a ridge round every tall part's blurred edge.
            return surface + Math.Max(Math.Min(rise, Math.Min(WrapFull * sheet, WrapFullM)), rise * wing * RaiseOnWing);
        }

        double Sample(float[] grid, float outside, double x, double span)
        {
            double u = (x - OriginX) / CellM - 0.5, v = Math.Abs(span) / CellM - 0.5;
            if (u < -0.5 || v > Height - 0.5 || u > Width - 0.5) return outside;
            int u0 = (int)Math.Floor(u), v0 = (int)Math.Floor(v);
            double tu = u - u0, tv = v - v0;
            double a = At(grid, outside, u0, v0), b = At(grid, outside, u0 + 1, v0), c = At(grid, outside, u0, v0 + 1), d = At(grid, outside, u0 + 1, v0 + 1);
            return (a + (b - a) * tu) + ((c + (d - c) * tu) - (a + (b - a) * tu)) * tv;
        }

        float At(int u, int v) { return At(Wing, 0, u, v); }

        float At(float[] grid, float outside, int u, int v)
        {
            if (v < 0) v = -v - 1;
            if (u < 0 || u >= Width || v >= Height) return outside;
            return grid[v * Width + u];
        }

        // triangles: nine floats each, every visible mesh in the canonical frame. parts: the lifting outlines.
        public static WingCover Build(float[] triangles, List<SurfacePolygon> parts, double rootSpanM, double minNormalY = 0.7)
        {
            double minX = double.PositiveInfinity, maxX = double.NegativeInfinity, minZ = double.PositiveInfinity, maxZ = double.NegativeInfinity, minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
            for (int i = 0; i < triangles.Length; i += 3)
            {
                minX = Math.Min(minX, triangles[i]); maxX = Math.Max(maxX, triangles[i]);
                minY = Math.Min(minY, triangles[i + 1]); maxY = Math.Max(maxY, triangles[i + 1]);
                minZ = Math.Min(minZ, triangles[i + 2]); maxZ = Math.Max(maxZ, triangles[i + 2]);
            }
            if (!(maxX > minX)) return null;
            double longest = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
            double cell = Math.Max(longest, 1e-6) / Resolution;
            double originX = minX - cell, originZ = minZ - cell;
            int width = (int)Math.Ceiling((maxX - minX) / cell) + 2;
            int across = (int)Math.Ceiling((maxZ - minZ) / cell) + 2;

            var top = new float[width * across];
            var lift = new float[width * across];
            var under = new float[width * across];   // minus the lowest surface
            for (int i = 0; i < top.Length; i++) { top[i] = float.NegativeInfinity; lift[i] = float.NegativeInfinity; under[i] = float.NegativeInfinity; }
            for (int t = 0; t + 8 < triangles.Length; t += 9)
            {
                Raster(top, width, across, originX, originZ, cell,
                       triangles[t], triangles[t + 1], triangles[t + 2], triangles[t + 3], triangles[t + 4], triangles[t + 5],
                       triangles[t + 6], triangles[t + 7], triangles[t + 8]);
                Raster(under, width, across, originX, originZ, cell,
                       triangles[t], -triangles[t + 1], triangles[t + 2], triangles[t + 3], -triangles[t + 4], triangles[t + 5],
                       triangles[t + 6], -triangles[t + 7], triangles[t + 8]);
            }
            foreach (SurfacePolygon p in parts)
            {
                if (p.NormalY < minNormalY || p.X.Length < 3) continue;
                double up = p.ThicknessM / 2;
                for (int k = 1; k + 1 < p.X.Length; k++)
                    Raster(lift, width, across, originX, originZ, cell,
                           p.X[0], p.Y[0] + up, p.Z[0], p.X[k], p.Y[k] + up, p.Z[k], p.X[k + 1], p.Y[k + 1] + up, p.Z[k + 1]);
            }

            // The wing's plane (the median top of what is wing), and how far the body stands over it, before
            // its narrow parts are cut off (a canopy or a spine is what makes a body round).
            var wingTops = new List<double>();
            var rises = new List<double>();
            double band = Math.Max(rootSpanM, cell);
            for (int i = 0; i < top.Length; i++)
                if (!float.IsNegativeInfinity(lift[i]) && !float.IsNegativeInfinity(top[i]) && top[i] <= lift[i] + TopToleranceM) wingTops.Add(top[i]);
            double plane = Median(wingTops);
            for (int row = 0; row < across; row++)
            {
                if (Math.Abs(originZ + (row + 0.5) * cell) > band) continue;
                for (int u = 0; u < width; u++)
                {
                    int i = row * width + u;
                    if (float.IsNegativeInfinity(top[i]) || (!float.IsNegativeInfinity(lift[i]) && top[i] <= lift[i] + TopToleranceM)) continue;
                    rises.Add(top[i] - plane);
                }
            }
            double craftT = rises.Count == 0 ? 0 : Math.Min(Math.Max((Median(rises) - FlatCraftM) / (RoundCraftM - FlatCraftM), 0), 1);
            double craftFlat = rises.Count == 0 ? 1 : 1 - craftT * craftT * (3 - 2 * craftT);

            int narrow = Math.Max(1, (int)Math.Round(NarrowM / cell));
            var rawTop = (float[])top.Clone();
            var mantleTop = (float[])top.Clone();
            Open(mantleTop, width, across, Math.Max(1, (int)Math.Round(MantleThinM / cell)));
            Dilate(mantleTop, width, across, Math.Max(1, (int)Math.Round(MantleSpreadM / cell)));
            var maskTop = (float[])top.Clone();
            Open(maskTop, width, across, Math.Max(1, (int)Math.Round(GrilleM / cell)));
            Open(top, width, across, narrow);
            Open(under, width, across, narrow);

            double largest = 0, bodyHeight = 0;
            for (int u = 0; u < width; u++)
            {
                double area = 0, moment = 0;
                for (int row = 0; row < across; row++)
                {
                    if (Math.Abs(originZ + (row + 0.5) * cell) > band) continue;
                    int i = row * width + u;
                    if (float.IsNegativeInfinity(top[i]) || float.IsNegativeInfinity(under[i])) continue;
                    double depth = Math.Max(top[i] + under[i], 0);
                    area += depth * cell;
                    moment += depth * cell * (top[i] - under[i]) / 2;
                }
                if (area > largest) { largest = area; bodyHeight = moment / area; }
            }

            // Folded: each cell from z = 0 out, the two sides averaged.
            int height = (int)Math.Ceiling(Math.Max(-minZ, maxZ) / cell) + 1;
            var wing = new float[width * height];
            var highest = new float[width * height];
            var highestRaw = new float[width * height];
            var mantle = new float[width * height];
            for (int v = 0; v < height; v++)
            {
                double span = (v + 0.5) * cell;
                for (int u = 0; u < width; u++)
                {
                    float sum = 0, high = Empty, raw = Empty, coat = Empty;
                    for (int side = -1; side <= 1; side += 2)
                    {
                        int row = (int)Math.Floor((side * span - originZ) / cell);
                        if (row < 0 || row >= across) continue;
                        int i = row * width + u;
                        if (!float.IsNegativeInfinity(lift[i]) && maskTop[i] <= lift[i] + WrapFullM) sum += 0.5f;
                        if (!float.IsNegativeInfinity(top[i])) high = Math.Max(high, top[i]);
                        if (!float.IsNegativeInfinity(rawTop[i])) raw = Math.Max(raw, rawTop[i]);
                        if (!float.IsNegativeInfinity(mantleTop[i])) coat = Math.Max(coat, mantleTop[i]);
                    }
                    wing[v * width + u] = sum;
                    highest[v * width + u] = high;
                    highestRaw[v * width + u] = raw;
                    mantle[v * width + u] = coat;
                }
            }
            FillHoles(wing, width, height, cell);
            var cover = new WingCover { Parts = parts, OriginX = originX, CellM = cell, Width = width, Height = height, Wing = wing, Top = highest, RawTop = highestRaw, Mantle = mantle,
                                     BodyRadiusM = Math.Sqrt(largest / Math.PI), BodyHeightM = bodyHeight, CraftFlat = craftFlat, sourceTriangles = triangles };
            for (int pass = 0; pass < BlurPasses; pass++) cover.Blur();
            float[] mantleFloor = (float[])cover.Mantle.Clone();
            for (int pass = 0; pass < MantleBlurPasses; pass++) cover.Mantle = cover.Smooth(cover.Mantle);
            for (int i = 0; i < cover.Mantle.Length; i++) cover.Mantle[i] = Math.Max(cover.Mantle[i], mantleFloor[i]);
            return cover;
        }

        static void Dilate(float[] grid, int width, int height, int r)
        {
            var scratch = new float[grid.Length];
            Pass(grid, scratch, width, height, r, true, false);
            Pass(scratch, grid, width, height, r, false, false);
        }

        // 3x3 box over the cells that have a top, mirrored across the centreline.
        float[] Smooth(float[] grid)
        {
            var next = new float[grid.Length];
            for (int v = 0; v < Height; v++)
                for (int u = 0; u < Width; u++)
                {
                    float sum = 0;
                    int n = 0;
                    for (int dv = -1; dv <= 1; dv++)
                        for (int du = -1; du <= 1; du++)
                        {
                            float t = At(grid, Empty, u + du, v + dv);
                            if (t > Empty) { sum += t; n++; }
                        }
                    next[v * Width + u] = grid[v * Width + u] > Empty ? sum / n : Empty;
                }
            return next;
        }

        // A min then a max over a square r cells either way: the top with its narrow features cut off.
        // Dilate then erode; past the grid's edge counts as neither, so parts at the edge keep their cells.
        static void Close(float[] grid, int width, int height, int r)
        {
            var scratch = new float[grid.Length];
            Pass(grid, scratch, width, height, r, true, false);
            Pass(scratch, grid, width, height, r, false, false);
            for (int pass = 0; pass < 2; pass++)
            {
                bool alongU = pass == 0;
                float[] from = alongU ? grid : scratch, to = alongU ? scratch : grid;
                for (int v = 0; v < height; v++)
                    for (int u = 0; u < width; u++)
                    {
                        float best = float.PositiveInfinity;
                        for (int d = -r; d <= r; d++)
                        {
                            int uu = alongU ? u + d : u, vv = alongU ? v : v + d;
                            if (uu >= 0 && uu < width && vv >= 0 && vv < height) best = Math.Min(best, from[vv * width + uu]);
                        }
                        to[v * width + u] = best;
                    }
            }
        }

        static void Open(float[] grid, int width, int height, int r)
        {
            var scratch = new float[grid.Length];
            Pass(grid, scratch, width, height, r, true, true);
            Pass(scratch, grid, width, height, r, false, true);
            Pass(grid, scratch, width, height, r, true, false);
            Pass(scratch, grid, width, height, r, false, false);
        }

        static void Pass(float[] from, float[] to, int width, int height, int r, bool alongU, bool min)
        {
            for (int v = 0; v < height; v++)
                for (int u = 0; u < width; u++)
                {
                    float best = min ? float.PositiveInfinity : float.NegativeInfinity;
                    for (int d = -r; d <= r; d++)
                    {
                        int uu = alongU ? u + d : u, vv = alongU ? v : v + d;
                        float x = uu < 0 || uu >= width || vv < 0 || vv >= height ? float.NegativeInfinity : from[vv * width + uu];
                        best = min ? Math.Min(best, x) : Math.Max(best, x);
                    }
                    to[v * width + u] = best;
                }
        }

        const int BodyBlurPasses = 3;

        // small not-wing holes enclosed by wing are wing.
        const double HoleMaxM2 = 2;

        static void FillHoles(float[] wing, int width, int height, double cell)
        {
            var seen = new bool[wing.Length];
            var region = new List<int>();
            var queue = new Queue<int>();
            for (int start = 0; start < wing.Length; start++)
            {
                if (seen[start] || wing[start] >= 0.5f) continue;
                region.Clear();
                bool open = false;
                seen[start] = true;
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    int i = queue.Dequeue();
                    region.Add(i);
                    int u0 = i % width, v0 = i / width;
                    if (u0 == 0 || u0 == width - 1 || v0 == height - 1) open = true;
                    for (int k = 0; k < 4; k++)
                    {
                        int u = u0 + (k == 0 ? 1 : k == 1 ? -1 : 0), v = v0 + (k == 2 ? 1 : k == 3 ? -1 : 0);
                        if (u < 0 || u >= width || v < 0 || v >= height) continue;
                        int j = v * width + u;
                        if (seen[j] || wing[j] >= 0.5f) continue;
                        seen[j] = true;
                        queue.Enqueue(j);
                    }
                }
                if (!open && region.Count * cell * cell <= HoleMaxM2)
                    foreach (int i in region) wing[i] = 1;
            }
        }

        // level non-lifting surface ahead of the wing (LERX, chine) counts as wing.
        public double FrontM;
        const double LevelM = 0.15, LevelStepM = 0.08;

        void ExtendLevel(WingShape shape)
        {
            FrontM = 0;
            if (RawTop == null) return;
            int n = Wing.Length;
            var level = new bool[n];
            var wingLevel = new double[n];
            var queue = new Queue<int>();
            // Lifting parts ahead of the wing are the canard's, or the wing's root extension without one.
            var lifting = new bool[n];
            for (int i = 0; i < n; i++)
            {
                double span = (i / Width + 0.5) * CellM, x = OriginX + (i % Width + 0.5) * CellM;
                lifting[i] = Wing[i] >= 0.5;
                if (span < shape.SemispanM && x < shape.At(span).LeadingM - CellM)
                {
                    if (shape.Canard != null || !lifting[i] || RawTop[i] <= Empty) { Wing[i] = 0; continue; }
                    FrontM = Math.Max(FrontM, shape.At(span).LeadingM - x);
                }
                if (lifting[i] && RawTop[i] > Empty) { level[i] = true; queue.Enqueue(i); }
            }
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                int u0 = i % Width, v0 = i / Width;
                for (int k = 0; k < 4; k++)
                {
                    int u = u0 + (k == 0 ? 1 : k == 1 ? -1 : 0), v = v0 + (k == 2 ? 1 : k == 3 ? -1 : 0);
                    if (u < 0 || u >= Width || v < 0 || v >= Height) continue;
                    int j = v * Width + u;
                    if (level[j] || lifting[j] || RawTop[j] <= Empty || Math.Abs(RawTop[j] - RawTop[i]) > LevelStepM) continue;
                    double span = (v + 0.5) * CellM;
                    if (span >= shape.SemispanM) continue;
                    WingShape.Station st = shape.At(span);
                    double x = OriginX + (u + 0.5) * CellM;
                    if (x >= st.LeadingM || RawTop[j] - st.MidM > LevelM) continue;
                    level[j] = true;
                    FrontM = Math.Max(FrontM, st.LeadingM - x);
                    queue.Enqueue(j);
                }
            }
            if (FrontM <= 0) return;
            var ext = new float[n];
            for (int i = 0; i < n; i++) ext[i] = level[i] ? 1 : 0;
            float[] wing = Wing;
            Wing = ext;
            for (int pass = 0; pass < BlurPasses; pass++) BlurWing();
            for (int i = 0; i < n; i++) wing[i] = Math.Max(wing[i], Wing[i]);
            Wing = wing;
        }

        // The lifting outlines the cover was built from.
        public List<SurfacePolygon> Parts;
        // Pieces this close along x are one surface (a panel's edge on the next one's).
        const double TouchM = 0.05;
        // A station's measured leading edge may sit this far from the parts' own.
        const double FitSlackM = 0.3;
        // Fore surfaces with no canard sheet of their own (LEVCONs) join the wing across this much body.
        const double BridgeM = 2;

        // leading edges put exactly on the lifting parts' front edges.
        void ExactLeadingEdges(WingShape shape)
        {
            if (Parts == null) return;
            var lo = new List<double>();
            var hi = new List<double>();
            var found = new bool[WingShape.Stations];
            for (int k = 0; k < WingShape.Stations; k++)
            {
                double span = shape.StationSpan(k);
                if (span >= shape.SemispanM) continue;
                lo.Clear(); hi.Clear();
                foreach (SurfacePolygon part in Parts)
                {
                    if (part.NormalY < 0.5 || part.X.Length < 3) continue;
                    for (int side = -1; side <= 1; side += 2)
                    {
                        double a, b;
                        if (Across(part, side * span, out a, out b)) { lo.Add(a); hi.Add(b); }
                    }
                }
                double leading = shape.LeadingM[k], trailing = leading + shape.ChordM[k];
                // With a canard sheet, only what lies wholly behind the canard's trailing edge
                // (the panels between canard, wing and body) joins the wing.
                SecondarySurface c = shape.Canard;
                double floor = CanardFloor(shape, span);
                double front = double.PositiveInfinity, back = double.NegativeInfinity;
                var taken = new bool[lo.Count];
                for (int m = 0; m < lo.Count; m++)
                    if (hi[m] >= leading - FitSlackM && lo[m] <= trailing && lo[m] >= floor)
                    {
                        taken[m] = true; front = Math.Min(front, lo[m]); back = Math.Max(back, hi[m]);
                    }
                // Without a canard sheet, panels joined to the wing reach its edge however far ahead (a Su-57's LEVCONs).
                if (double.IsInfinity(front) && c == null)
                {
                    int last = -1;
                    for (int m = 0; m < lo.Count; m++) if (lo[m] <= trailing && (last < 0 || hi[m] > hi[last])) last = m;
                    if (last >= 0) { taken[last] = true; front = lo[last]; back = hi[last]; }
                }
                if (double.IsInfinity(front)) continue;
                found[k] = true;
                double reach = c == null && shape.Secondary != null && shape.Secondary.IsCanard ? BridgeM : TouchM;
                for (bool grew = true; grew;)
                {
                    grew = false;
                    for (int m = 0; m < lo.Count; m++)
                        if (!taken[m] && lo[m] >= floor && hi[m] >= front - reach && lo[m] <= back + TouchM)
                        {
                            taken[m] = true; grew = true; front = Math.Min(front, lo[m]); back = Math.Max(back, hi[m]);
                        }
                }
                shape.LeadingM[k] = (float)front;
                shape.ChordM[k] = (float)Math.Max(trailing - front, 0.05);
            }
            JoinBody(shape, found);
            FillNotches(shape);
        }

        // a leading edge set back over a span narrower than NotchM, with edges ahead on both sides, is filled.
        const double NotchM = 0.6;

        static void FillNotches(WingShape shape)
        {
            int n = WingShape.Stations;
            var lead = new double[n];
            for (int k = 0; k < n; k++) lead[k] = shape.LeadingM[k];
            for (int k = 0; k < n; k++)
            {
                double span = shape.StationSpan(k), left = double.PositiveInfinity, right = double.PositiveInfinity;
                for (int j = k - 1; j >= 0 && shape.StationSpan(j) >= span - NotchM; j--) left = Math.Min(left, lead[j]);
                for (int j = k + 1; j < n && shape.StationSpan(j) <= span + NotchM; j++) right = Math.Min(right, lead[j]);
                if (k == 0) left = right;
                double fill = Math.Max(left, right);
                if (fill >= lead[k]) continue;
                double trailing = lead[k] + shape.ChordM[k];
                shape.LeadingM[k] = (float)fill;
                shape.ChordM[k] = (float)(trailing - fill);
            }
        }

        // Inside the root's gap the stations take the root's.
        const double JoinM = 0.6;

        static void JoinBody(WingShape shape, bool[] found)
        {
            if (shape.InnerSpanM <= 1e-3) return;
            int inner = -1;
            for (int k = 0; k < WingShape.Stations; k++) if (shape.StationSpan(k) >= shape.InnerSpanM) { inner = k; break; }
            if (inner <= 0) return;
            double join = shape.LeadingM[inner];
            for (int k = inner; k < WingShape.Stations && shape.StationSpan(k) <= shape.InnerSpanM + JoinM; k++) join = Math.Min(join, shape.LeadingM[k]);
            double trailing = shape.LeadingM[inner] + shape.ChordM[inner];
            for (int k = 0; k < inner; k++)
            {
                double lead = found[k] && shape.Canard == null ? Math.Min(shape.LeadingM[k], join) : join;
                shape.LeadingM[k] = (float)lead;
                shape.ChordM[k] = (float)Math.Max(trailing - lead, 0.05);
                shape.MidM[k] = shape.MidM[inner];
                shape.Thickness[k] = shape.Thickness[inner];
                shape.Loading[k] = shape.Loading[inner];
            }
        }

        // Where the canard's sheet fades at its tip (VaporField's 0.85), the wing's own edge takes over.
        static double CanardFloor(WingShape shape, double span)
        {
            SecondarySurface c = shape.Canard;
            bool underCanard = c != null && (span - c.RootSpanM) / Math.Max(c.SemispanM - c.RootSpanM, 1e-3) < 0.85;
            return underCanard ? CanardTrailing(c, span) - TouchM : double.NegativeInfinity;
        }

        double SkinFloor(WingShape shape, double span)
        {
            return CraftFlat > 0.5 ? double.NegativeInfinity : CanardFloor(shape, span);
        }

        static double CanardTrailing(SecondarySurface c, double span)
        {
            double t = Math.Min(Math.Max((span - c.RootSpanM) / Math.Max(c.SemispanM - c.RootSpanM, 1e-3), 0), 1);
            return c.RootLeadingM + c.RootChordM + (c.TipLeadingM + c.TipChordM - c.RootLeadingM - c.RootChordM) * t;
        }

        // Where the line z = z0 crosses the outline (seen from above): x from a to b.
        static bool Across(SurfacePolygon part, double z0, out double a, out double b)
        {
            a = double.PositiveInfinity; b = double.NegativeInfinity;
            int n = part.X.Length;
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                double za = part.Z[i], zb = part.Z[j];
                if ((za - z0) * (zb - z0) > 0 || za == zb) continue;
                double x = part.X[i] + (part.X[j] - part.X[i]) * (z0 - za) / (zb - za);
                a = Math.Min(a, x); b = Math.Max(b, x);
            }
            return b > a;
        }

        // A lumpy skin (a fuselage built of gear parts) leaves valleys between the stations that took the
        // trim forward: each station reaches as far forward as a neighbour within SkinBridgeM, less a slope.
        const double SkinBridgeM = 1, SkinBridgeSlope = 1, SkinValleyM = 0.8;

        void BridgeSkin(WingShape shape)
        {
            int n = WingShape.Stations;
            var front = new double[n];
            for (int k = 0; k < n; k++) front[k] = shape.LeadingM[k];
            for (int k = 0; k < n; k++)
            {
                double span = shape.StationSpan(k);
                if (span >= shape.SemispanM) continue;
                double best = front[k];
                for (int m = 0; m < n; m++)
                {
                    double d = Math.Abs(shape.StationSpan(m) - span);
                    if (d > SkinBridgeM || shape.StationSpan(m) >= shape.SemispanM) continue;
                    best = Math.Min(best, front[m] + SkinBridgeSlope * d);
                }
                best = Math.Max(best, SkinFloor(shape, span));
                if (best >= front[k]) continue;
                // Only over a body not too tall: the roof there is skin height, or a valley below it.
                double roof = Math.Max(MeshRoof(best + 0.1, span), MeshRoof(best + 0.1, -span));
                if (!(roof > Empty && roof <= shape.MidM[k] + WrapFullM && roof >= shape.MidM[k] - SkinValleyM)) continue;
                double trailing = shape.LeadingM[k] + shape.ChordM[k];
                shape.LeadingM[k] = (float)best;
                shape.ChordM[k] = (float)(trailing - best);
            }
        }

        // Shallow visible trim joins the lifting skin without turning fins or pods into wings.
        // on a round body, never ahead of a canard's trailing edge (blended bodies join in JoinForebody).
        void ExtendWingSkin(WingShape shape)
        {
            if (RawTop == null) return;
            for (int k = 0; k < WingShape.Stations; k++)
            {
                double span = shape.StationSpan(k);
                if (span >= shape.SemispanM || span < 0.45 * BodyRadiusM) continue;
                double leading = shape.LeadingM[k], trailing = leading + shape.ChordM[k], gap = 0, front = leading;
                double step = 0.04, limit = Math.Min(Math.Min(1.2, shape.ChordM[k] * 0.35), leading - SkinFloor(shape, span));
                for (double x = leading + Math.Min(0.12, shape.ChordM[k] * 0.1); x >= leading - limit; x -= step)
                {
                    double roof = Math.Max(MeshRoof(x, span), MeshRoof(x, -span));
                    bool skin = roof > Empty && roof >= shape.MidM[k] - 0.2 && roof <= shape.MidM[k] + WrapFullM;
                    gap = skin ? 0 : gap + step;
                    if (gap > 0.12) break;
                    if (skin) front = Math.Min(front, x);
                }
                shape.LeadingM[k] = (float)front;
                shape.ChordM[k] = (float)(trailing - front);
            }
            BridgeSkin(shape);
            if (Mantle == null) return;
            for (int v = 0; v < Height; v++)
                for (int u = 0; u < Width; u++)
                {
                    double span = (v + 0.5) * CellM, x = OriginX + (u + 0.5) * CellM;
                    if (span < 0.45 * BodyRadiusM || span >= shape.SemispanM) continue;
                    WingShape.Station st = shape.At(span);
                    int i = v * Width + u;
                    if (x >= st.LeadingM && x <= st.LeadingM + st.ChordM && RawTop[i] > st.MidM && RawTop[i] <= st.MidM + WrapFullM)
                        Mantle[i] = Math.Max(Mantle[i], RawTop[i]);
                }
        }

        void ExtendShoulders(WingShape shape)
        {
            if (CraftFlat > 0.5 || BodyRadiusM <= 0 || shape.Canard != null) return;
            for (int k = 0; k < WingShape.Stations; k++)
            {
                double span = shape.StationSpan(k);
                if (span < BodyRadiusM * 0.45 || span > shape.RootSpanM + 0.4) continue;
                int row = Math.Min(Height - 1, Math.Max(0, (int)(span / CellM)));
                double leading = shape.LeadingM[k], trailing = leading + shape.ChordM[k], gap = 0, front = leading;
                int start = Math.Min(Width - 1, (int)((leading - OriginX) / CellM));
                for (int u = start; u >= 0; u--)
                {
                    double x = OriginX + (u + 0.5) * CellM;
                    if (leading - x > Math.Min(2, shape.ChordM[k] * 0.35)) break;
                    double roof = Top[row * Width + u];
                    bool shoulder = roof > Empty && roof >= shape.MidM[k] - 0.15 && roof <= shape.MidM[k] + 0.6;
                    gap = shoulder ? 0 : gap + CellM;
                    if (gap > 2 * CellM) break;
                    if (!shoulder) continue;
                    front = Math.Min(front, x - CellM * 0.5);
                    Wing[row * Width + u] = 1;
                }
                if (front >= leading) continue;
                FrontM = Math.Max(FrontM, leading - front);
                shape.LeadingM[k] = (float)front;
                shape.ChordM[k] = (float)(trailing - front);
            }
        }

        // Suppress a raised intake or spine while retaining a flat blended lifting body.
        public double RaisedRootM;
        public const double RaisedReach = 0.12, RaisedFrom = 0.25, RaisedTo = 0.9, RaisedRootMaxM = 1.5;
        void SuppressRaised(WingShape shape)
        {
            RaisedRootM = 0;
            if (RawTop == null || CraftFlat > 0.5 || shape.Canard != null || FrontM >= 0.3) return;
            bool edge = true, standing = true;
            double rootEdge = shape.RootSpanM;
            for (int v = 0; v < Height; v++)
            {
                double span = (v + 0.5) * CellM;
                if (span >= shape.SemispanM) break;
                WingShape.Station st = shape.At(span);
                double top = st.MidM + 0.5 * st.Thickness * st.ChordM, reach = Math.Max(RaisedReach * st.ChordM, 0.2);
                int tall = 0, roofs = 0;
                for (int u = 0; u < Width; u++)
                {
                    int i = v * Width + u;
                    if (RawTop[i] <= Empty) continue;
                    double x = OriginX + (u + 0.5) * CellM;
                    double t = Math.Min(Math.Max((RawTop[i] - top - RaisedFrom * reach) / ((RaisedTo - RaisedFrom) * reach), 0), 1);
                    if (x >= st.LeadingM && x <= st.LeadingM + 0.5 * st.ChordM) { roofs++; if (t >= 0.5) tall++; }
                    if (Wing[i] > 0 && t > 0) Wing[i] *= (float)(1 - t * t * (3 - 2 * t));
                }
                if (standing && span <= shape.RootSpanM + RaisedRootMaxM && span >= shape.RootSpanM - CellM)
                {
                    if (roofs > 0 && tall * 2 >= roofs) rootEdge = Math.Max(rootEdge, span + 0.5 * CellM);
                    else if (span > shape.RootSpanM) standing = false;
                }
            }
            if (edge && rootEdge > shape.RootSpanM + CellM) RaisedRootM = rootEdge;
        }

        // where the leading edge jumps aft by StepM between two stations, the stations inboard of
        // it (outside the body's radius) that reach ahead mostly over open air (the gap beside the
        // F-22's intakes) take the outboard wing's line, so the sheet does not start in the air.
        public const double StepM = 0.5;
        void SeatStep(WingShape shape)
        {
            int n = WingShape.Stations;
            for (int k = 1; k + 2 < n; k++)
            {
                if (shape.LeadingM[k + 1] - shape.LeadingM[k] < StepM) continue;
                double slope = shape.LeadingM[k + 2] - shape.LeadingM[k + 1];
                for (int j = k; j >= 0; j--)
                {
                    double span = shape.StationSpan(j), line = shape.LeadingM[k + 1] - slope * (k + 1 - j);
                    if (span <= BodyRadiusM || shape.LeadingM[j] >= line) break;
                    int open = 0;
                    for (int q = 1; q <= 4; q++) if (MeshRoof(shape.LeadingM[j] + (line - shape.LeadingM[j]) * q / 5, span) < -100) open++;
                    if (open < 3) break;
                    double trailing = (double)shape.LeadingM[j] + shape.ChordM[j];
                    shape.LeadingM[j] = (float)line;
                    shape.ChordM[j] = (float)Math.Max(trailing - line, 0.05);
                }
            }
        }

        public void BuildBody(WingShape shape)
        {
            BuildBodyCore(shape);
            SmoothSkinFront(shape);
            SuppressRaised(shape);
            shape.Despike(SpikeM);
            SeatStep(shape);
            if (SourceShape != null)
            {
                SourceExposed = new bool[WingShape.Stations];
                for (int k = 0; k < WingShape.Stations; k++)
                {
                    double span = shape.StationSpan(k);
                    SourceExposed[k] = ExposedLeading(SourceShape.LeadingM[k], SourceShape.MidM[k], span, SourceShape.ChordM[k], SourceShape.Thickness[k]);
                }
            }
        }

        // Smooth the cloud envelope without moving physical vortex sources.
        internal void SmoothSkinFront(WingShape shape)
        {
            if (RawTop == null || BodyRadiusM <= 0 || shape.RootSpanM <= 0) return;
            double sigma = Math.Min(0.7, Math.Max(0.15, BodyRadiusM * 0.5));
            double outer = shape.RootSpanM + 2 * sigma;
            var front = (float[])shape.LeadingM.Clone();
            for (int k = 0; k < WingShape.Stations; k++)
            {
                double span = shape.StationSpan(k);
                if (span >= outer) break;
                double sum = 0, weight = 0;
                for (int j = 0; j < WingShape.Stations; j++)
                {
                    double at = shape.StationSpan(j), d = (at - span) / sigma;
                    if (Math.Abs(d) > 3) continue;
                    double w = Math.Exp(-0.5 * d * d) + Math.Exp(-0.5 * Math.Pow((at + span) / sigma, 2));
                    sum += front[j] * w;
                    weight += w;
                }
                if (weight <= 0) continue;
                double blend = Math.Min(Math.Max((outer - span) / (2 * sigma), 0), 1);
                blend = blend * blend * (3 - 2 * blend);
                double leading = Math.Max(front[k] + (sum / weight - front[k]) * blend, Math.Min(front[k], SkinFloor(shape, span)));
                double roof = SampleTop(leading + 0.1, span);
                // the blurred raster reaches a cell past an edge; the sheet must not start over open air.
                bool open = Math.Max(MeshRoof(leading + 0.1, span), MeshRoof(leading + 0.1, -span)) <= Empty;
                if (leading < front[k] && (roof <= Empty || open || roof > shape.MidM[k] + WrapNoneM)) continue;
                double trailing = front[k] + shape.ChordM[k];
                shape.LeadingM[k] = (float)leading;
                shape.ChordM[k] = (float)Math.Max(0.05, trailing - leading);
            }
        }

        // A source needs a supported upper surface and an unobstructed upstream separation edge.
        public bool ExposedLeading(double x, double y, double span, double chord, double thickness)
        {
            double inset = Math.Min(0.1, chord * 0.1), top = y + 0.5 * thickness * chord;
            for (int side = -1; side <= 1; side += 2)
            {
                double roof = MeshRoof(x + inset, side * span);
                if (roof <= Empty || roof > top + TopToleranceM) return false;
                if (MeshRoof(x - 0.15, side * span) >= top - TopToleranceM) return false;
                for (int k = 1; k <= 4; k++)
                    for (int lateral = -1; lateral <= 1; lateral++)
                        if (MeshRoof(x + Math.Min(1, chord * 0.25) * k / 4, side * (span + lateral * 0.15)) > top + TopToleranceM) return false;
            }
            return true;
        }

        void BuildBodyCore(WingShape shape)
        {
            ExactLeadingEdges(shape);
            ExtendWingSkin(shape);
            SourceShape = shape.Copy();
            SourceShape.LeadingM = (float[])shape.LeadingM.Clone();
            SourceShape.ChordM = (float[])shape.ChordM.Clone();
            SourceShape.Cover = null;
            JoinForebody(shape);
            ExtendLevel(shape);
            ExtendShoulders(shape);
            Body = null;
            if (CraftFlat <= 0) return;
            var body = new float[Wing.Length];
            double half = shape.RootSpanM, narrow = Math.Min(BodyRadiusM, shape.RootSpanM);
            BodyMinX = double.PositiveInfinity; BodyMaxX = double.NegativeInfinity; BodyTopM = double.NegativeInfinity;
            for (int v = 0; v < Height; v++)
            {
                double span = (v + 0.5) * CellM;
                if (span > half) break;
                WingShape.Station st = shape.At(span);
                for (int u = 0; u < Width; u++)
                {
                    int i = v * Width + u;
                    if (Top[i] <= Empty) continue;
                    double x = OriginX + (u + 0.5) * CellM;
                    bool shoulder = shape.Canard == null && span >= 0.6 * narrow && x >= st.LeadingM - Math.Min(3, 0.4 * st.ChordM);
                    if ((x < st.LeadingM && !shoulder) || (x > st.LeadingM + st.ChordM && span > narrow)) continue;
                    body[i] = 1;
                    BodyMinX = Math.Min(BodyMinX, x);
                    BodyMaxX = Math.Max(BodyMaxX, x);
                    BodyTopM = Math.Max(BodyTopM, Top[i]);
                }
            }
            if (double.IsInfinity(BodyMinX)) return;
            BodySpanM = half;
            float[] wing = Wing;
            Wing = body;
            for (int pass = 0; pass < BodyBlurPasses; pass++) BlurWing();
            Body = Wing;
            Wing = wing;
            // the blur fades the body's cloud inside the airframe (gaps between parts closed), never out past its edge.
            var outline = new float[Body.Length];
            for (int i = 0; i < outline.Length; i++) outline[i] = RawTop[i] > Empty ? 0f : float.NegativeInfinity;
            Close(outline, Width, Height, BodyBlurPasses);
            for (int i = 0; i < Body.Length; i++) if (float.IsNegativeInfinity(outline[i])) Body[i] = 0;
            if (shape.Canard == null)
                for (int k = 0; k < WingShape.Stations; k++)
                {
                    double span = shape.StationSpan(k);
                    if (span < 0.6 * narrow || span >= half) continue;
                    int row = Math.Min((int)(span / CellM), Height - 1);
                    double trailing = shape.LeadingM[k] + shape.ChordM[k];
                    for (int u = 0; u < Width; u++)
                        if (Body[row * Width + u] >= 0.5f)
                        {
                            double front = OriginX + (u + 0.5) * CellM;
                            if (front < shape.LeadingM[k])
                            {
                                shape.LeadingM[k] = (float)front;
                                shape.ChordM[k] = (float)(trailing - front);
                            }
                            break;
                        }
                }
        }

        // Join paired foreplanes across supported low skin, without moving their vortex sources.
        internal void JoinForebody(WingShape shape)
        {
            ForebodyJoined = false;
            var fore = shape.Canard;
            if (CraftFlat <= 0.5 || fore == null || fore.RootSpanM > shape.RootSpanM + 0.5) return;
            double outer = Math.Min(fore.RootSpanM, shape.RootSpanM);
            if (outer <= 0) return;
            double edge = Math.Min(fore.RootLeadingM, shape.At(outer).LeadingM);
            double mid = Math.Max(fore.RootHeightM, shape.At(outer).MidM);
            double roofAtJoin = SampleTop(edge, 0);
            if (roofAtJoin <= Empty) return;
            double rise = Math.Max(0, roofAtJoin - mid);
            double thin = Math.Min(Math.Max((WrapNoneM - rise) / (WrapNoneM - WrapFullM), 0), 1);
            double bow = Math.Min(outer, 2) * thin;
            double clearance = Math.Min(WrapNoneM, Math.Max(WrapFullM, outer * 0.5));
            for (int k = 0; k < WingShape.Stations; k++)
            {
                double span = shape.StationSpan(k);
                if (span >= outer) break;
                double leading = shape.LeadingM[k], front = leading, trailing = leading + shape.ChordM[k];
                double t = span / outer;
                double arch = edge - bow * (1 - t * t);
                double limit = Math.Max(arch, leading - Math.Min(2, shape.ChordM[k] * 0.35));
                for (double x = leading; x >= limit; x -= 0.04)
                {
                    double roof = ForebodyRoof(x, span, outer, mid, clearance, fore.RootHeightM);
                    if (roof <= Empty || roof < shape.MidM[k] - 0.2 || roof > mid + clearance) break;
                    front = x;
                }
                shape.LeadingM[k] = (float)front;
                shape.ChordM[k] = (float)(trailing - front);
                if (front < leading - 0.05) ForebodyJoined = true;
            }
            if (!ForebodyJoined) return;
            for (int v = 0; v < Height && (v + 0.5) * CellM < outer; v++)
                for (int u = 0; u < Width; u++)
                {
                    double span = (v + 0.5) * CellM, x = OriginX + (u + 0.5) * CellM;
                    if (x < shape.At(span).LeadingM - BodyBlurPasses * CellM || x > edge + fore.RootChordM + BodyBlurPasses * CellM) continue;
                    int i = v * Width + u;
                    if (Top[i] >= mid - 0.2) continue;
                    double roof = ForebodyRoof(x, span, outer, mid, clearance, fore.RootHeightM);
                    if (roof <= Empty) continue;
                    Top[i] = (float)roof;
                    if (Mantle != null) Mantle[i] = Math.Max(Mantle[i], (float)roof);
                }
        }

        // Condensation can bridge filtered trim gaps between paired roots above a shallow body.
        double ForebodyRoof(double x, double span, double halfWidth, double mid, double clearance, double edgeHeight)
        {
            double roof = SampleTop(x, span);
            if (roof >= mid - 0.2) return roof;
            roof = SampleRawRoof(x, span);
            if (roof >= mid - 0.2) return roof <= mid + clearance ? roof : Empty;
            double center = SampleTop(x, 0);
            if (center < mid - 0.2 || center > mid + clearance) return Empty;
            double t = Math.Min(span / halfWidth, 1);
            return center + (edgeHeight - center) * t * t;
        }

        void BlurWing()
        {
            var next = new float[Wing.Length];
            for (int v = 0; v < Height; v++)
                for (int u = 0; u < Width; u++)
                {
                    float sum = 0;
                    for (int dv = -1; dv <= 1; dv++)
                        for (int du = -1; du <= 1; du++) sum += At(u + du, v + dv);
                    next[v * Width + u] = sum / 9;
                }
            Wing = next;
        }

        static double Median(List<double> values)
        {
            if (values.Count == 0) return 0;
            values.Sort();
            int m = values.Count / 2;
            return values.Count % 2 == 1 ? values[m] : (values[m - 1] + values[m]) / 2;
        }

        // 3x3 box, mirrored across the centreline; the top over the cells that have one.
        void Blur()
        {
            var next = new float[Wing.Length];
            var high = new float[Top.Length];
            for (int v = 0; v < Height; v++)
                for (int u = 0; u < Width; u++)
                {
                    float sum = 0, topSum = 0;
                    int tops = 0;
                    for (int dv = -1; dv <= 1; dv++)
                        for (int du = -1; du <= 1; du++)
                        {
                            sum += At(u + du, v + dv);
                            float t = At(Top, Empty, u + du, v + dv);
                            if (t > Empty) { topSum += t; tops++; }
                        }
                    next[v * Width + u] = sum / 9;
                    high[v * Width + u] = Top[v * Width + u] > Empty ? topSum / tops : Empty;
                }
            Wing = next;
            Top = high;
        }

        // The highest y at every cell centre inside the triangle (upstream's rasterise, top only).
        static void Raster(float[] buf, int width, int height, double originX, double originZ, double cell,
                           double ax, double ay, double az, double bx, double by, double bz, double cx, double cy, double cz)
        {
            double au = (ax - originX) / cell, av = (az - originZ) / cell;
            double bu = (bx - originX) / cell, bv = (bz - originZ) / cell;
            double cu = (cx - originX) / cell, cv = (cz - originZ) / cell;
            double area = (bu - au) * (cv - av) - (bv - av) * (cu - au);
            if (Math.Abs(area) < 1e-12) return;
            int loU = Math.Max((int)Math.Floor(Math.Min(au, Math.Min(bu, cu))), 0);
            int hiU = Math.Min((int)Math.Ceiling(Math.Max(au, Math.Max(bu, cu))), width - 1);
            int loV = Math.Max((int)Math.Floor(Math.Min(av, Math.Min(bv, cv))), 0);
            int hiV = Math.Min((int)Math.Ceiling(Math.Max(av, Math.Max(bv, cv))), height - 1);
            for (int pv = loV; pv <= hiV; pv++)
            {
                double sv = pv + 0.5;
                for (int pu = loU; pu <= hiU; pu++)
                {
                    double su = pu + 0.5;
                    double wa = ((bu - su) * (cv - sv) - (bv - sv) * (cu - su)) / area;
                    double wb = ((cu - su) * (av - sv) - (cv - sv) * (au - su)) / area;
                    double wc = 1 - wa - wb;
                    if (wa < 0 || wb < 0 || wc < 0) continue;
                    float y = (float)(wa * ay + wb * by + wc * cy);
                    int i = pv * width + pu;
                    if (y > buf[i]) buf[i] = y;
                }
            }
        }
    }
}
