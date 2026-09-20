using System.Collections.Generic;
using UnityEngine;

namespace VortexVapor
{
    // Where a planform sits in the world right now: an origin and three unit axes, u and v in the
    // plane and n its normal.
    public struct PlaneFrame
    {
        public Vector3 origin, u, v, n;

        public Vector3 ToWorld(Vector2 p) { return origin + u * p.x + v * p.y; }

        public Vector2 ToPlane(Vector3 world, out float height)
        {
            Vector3 d = world - origin;
            height = Vector3.Dot(d, n);
            return new Vector2(Vector3.Dot(d, u), Vector3.Dot(d, v));
        }

        public Vector2 InPlane(Vector3 dir) { return new Vector2(Vector3.Dot(dir, u), Vector3.Dot(dir, v)); }
    }

    // A lifting part's outline in its own plane: the convex hull of its mesh, in metres, about the
    // centre of its bounds. Measured once per part. Pure geometry, so it can be checked offline.
    //
    // It exists because a wing in KSP is usually many parts, often clipped into each other, and
    // stock gives every one of them lift for its full area. Two panels stacked in the same place are
    // one piece of wing, not two, and the samples below are how the model finds out which area is
    // shared (see PlanformOverlap).
    public class Planform
    {
        public Vector2[] hull;          // counter-clockwise
        public Vector2[] samples;       // interior points on a uniform grid, so each stands for equal area
        public float[] sampleShare;     // 1 / (number of lifting planforms covering the sample)
        public float area;              // m^2
        public float thickness;         // m, extent along the normal
        public float radius;            // m, bounding radius about the origin

        const int SampleGrid = 12;

        public static Planform Build(List<Vector2> points, float thickness)
        {
            var pf = new Planform { thickness = thickness };
            pf.hull = ConvexHull(points);
            pf.area = Mathf.Abs(SignedArea(pf.hull));
            foreach (var h in pf.hull) pf.radius = Mathf.Max(pf.radius, h.magnitude);

            Vector2 min = new Vector2(float.MaxValue, float.MaxValue), max = -min;
            foreach (var h in pf.hull) { min = Vector2.Min(min, h); max = Vector2.Max(max, h); }
            var inside = new List<Vector2>();
            for (int i = 0; i < SampleGrid; i++)
                for (int j = 0; j < SampleGrid; j++)
                {
                    var q = new Vector2(Mathf.Lerp(min.x, max.x, (i + 0.5f) / SampleGrid),
                                        Mathf.Lerp(min.y, max.y, (j + 0.5f) / SampleGrid));
                    if (pf.Contains(q)) inside.Add(q);
                }
            if (inside.Count == 0) inside.Add(Centroid(pf.hull));
            pf.samples = inside.ToArray();
            pf.sampleShare = new float[pf.samples.Length];
            for (int i = 0; i < pf.sampleShare.Length; i++) pf.sampleShare[i] = 1f;
            return pf;
        }

        public bool Contains(Vector2 p)
        {
            if (hull.Length < 3) return false;
            for (int i = 0; i < hull.Length; i++)
            {
                Vector2 a = hull[i], b = hull[(i + 1) % hull.Length];
                if ((b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x) < -1e-5f) return false;
            }
            return true;
        }

        // Extent along an in-plane span direction s (unit).
        public void SpanRange(Vector2 s, out float lo, out float hi)
        {
            lo = float.MaxValue; hi = float.MinValue;
            foreach (var h in hull)
            {
                float d = Vector2.Dot(h, s);
                lo = Mathf.Min(lo, d); hi = Mathf.Max(hi, d);
            }
        }

        // The chord at span station sigma: where the line {p . s = sigma} crosses the outline,
        // as coordinates along the chord direction perp(s) = (-s.y, s.x).
        public bool ChordAt(Vector2 s, float sigma, out float c0, out float c1)
        {
            Vector2 c = new Vector2(-s.y, s.x);
            c0 = float.MaxValue; c1 = float.MinValue;
            for (int i = 0; i < hull.Length; i++)
            {
                Vector2 a = hull[i], b = hull[(i + 1) % hull.Length];
                float pa = Vector2.Dot(a, s) - sigma, pb = Vector2.Dot(b, s) - sigma;
                if (pa * pb > 0f) continue;
                if (Mathf.Abs(pa - pb) < 1e-6f)
                {
                    float ca = Vector2.Dot(a, c), cb = Vector2.Dot(b, c);
                    c0 = Mathf.Min(c0, Mathf.Min(ca, cb)); c1 = Mathf.Max(c1, Mathf.Max(ca, cb));
                    continue;
                }
                Vector2 x = a + (b - a) * (pa / (pa - pb));
                float cx = Vector2.Dot(x, c);
                c0 = Mathf.Min(c0, cx); c1 = Mathf.Max(c1, cx);
            }
            return c1 >= c0;
        }

        // The mean share of the samples between two span stations: the fraction of this strip's
        // area that belongs to this part rather than to a part stacked on top of it.
        public float Share(Vector2 s, float lo, float hi)
        {
            float sum = 0f; int n = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                float d = Vector2.Dot(samples[i], s);
                if (d < lo || d > hi) continue;
                sum += sampleShare[i]; n++;
            }
            return n > 0 ? sum / n : AverageShare();
        }

        public float AverageShare()
        {
            float sum = 0f;
            foreach (float w in sampleShare) sum += w;
            return sampleShare.Length > 0 ? sum / sampleShare.Length : 1f;
        }

        static Vector2[] ConvexHull(List<Vector2> input)
        {
            var pts = new List<Vector2>(input);
            pts.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            if (pts.Count < 3) return pts.ToArray();
            var h = new List<Vector2>();
            for (int pass = 0; pass < 2; pass++)
            {
                int start = h.Count;
                for (int i = 0; i < pts.Count; i++)
                {
                    Vector2 p = pts[pass == 0 ? i : pts.Count - 1 - i];
                    while (h.Count - start >= 2 && Cross(h[h.Count - 2], h[h.Count - 1], p) <= 1e-7f)
                        h.RemoveAt(h.Count - 1);
                    h.Add(p);
                }
                h.RemoveAt(h.Count - 1);
            }
            return h.ToArray();
        }

        static float Cross(Vector2 o, Vector2 a, Vector2 b)
        {
            return (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
        }

        static float SignedArea(Vector2[] poly)
        {
            float a = 0f;
            for (int i = 0; i < poly.Length; i++)
            {
                Vector2 p = poly[i], q = poly[(i + 1) % poly.Length];
                a += p.x * q.y - q.x * p.y;
            }
            return 0.5f * a;
        }

        static Vector2 Centroid(Vector2[] poly)
        {
            Vector2 c = Vector2.zero;
            foreach (var p in poly) c += p;
            return poly.Length > 0 ? c / poly.Length : c;
        }
    }

    // Which area of the airframe's lifting surfaces is shared. Every sample of every planform is
    // tested against every other planform; a sample covered by m planforms in all gives each of
    // them 1/m of its area. Stacked duplicates therefore add up to one surface, a part partly
    // overlapping another gives up only the overlapped area, and a flap butted against a wing
    // (touching, not overlapping) keeps all of its own.
    public static class PlanformOverlap
    {
        // Two surfaces closer than this along their normals, or than their own thickness, are the
        // same piece of wing. Further apart they are a biplane and both lift.
        public const float MinSlab = 0.3f;

        public static void Compute(List<Planform> forms, List<PlaneFrame> frames)
        {
            for (int k = 0; k < forms.Count; k++)
            {
                Planform pk = forms[k];
                for (int i = 0; i < pk.samples.Length; i++)
                {
                    Vector3 x = frames[k].ToWorld(pk.samples[i]);
                    int covered = 1;
                    for (int m = 0; m < forms.Count; m++)
                    {
                        if (m == k) continue;
                        Planform pm = forms[m];
                        float slab = Mathf.Max(MinSlab, 0.5f * (pk.thickness + pm.thickness));
                        if ((x - frames[m].origin).sqrMagnitude > (pm.radius + slab) * (pm.radius + slab)) continue;
                        float h;
                        Vector2 q = frames[m].ToPlane(x, out h);
                        if (Mathf.Abs(h) <= slab && pm.Contains(q)) covered++;
                    }
                    pk.sampleShare[i] = 1f / covered;
                }
            }
        }
    }
}
