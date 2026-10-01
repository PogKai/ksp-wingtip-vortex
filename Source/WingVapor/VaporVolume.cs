using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace VortexVapor
{
    // Optional: the wing vapor as soft volumetric cloud, drawn with Waterfall's volumetric shader
    // and model when Waterfall is installed (found by reflection, so it is not a dependency).
    // One cloud per side of the craft, lying along the span: a tapered tube whose front edge
    // lies along where the vapor starts and whose width follows its chord, so the cloud takes
    // the wing's sweep and taper. Its top is rounded from root to tip, and where both sides
    // carry vapor the two cross-fade over the fuselage into one dome.
    public class VaporVolume
    {
        const string ShaderName = "Waterfall/Additive (Volumetric)";
        const string ModelPath = "Waterfall/FX/fx-volumetric-simple";
        const string NoisePath = "Waterfall/FX/fx-noise-1";

        // Span strips the vapor is measured in before a line is fitted through them.
        public const int Strips = 24;
        public const float MinStripWidth = 0.5f;    // m
        public const float Brightness = 1.8f;       // in fully dense vapor
        // How far the cloud stands off the skin in fully dense vapor, in section chords, and
        // its cap in metres.
        public const float CloudHeightChords = 0.35f, MaxCloudHeight = 3f;
        // The cloud's radius off the wing, at most, in its half-widths along the flow.
        public const float MaxRiseChords = 0.45f;
        // The cloud's half-width along the flow, in vapor half-chords. Its front is laid along
        // where the vapor starts (see Front), so the extra runs on past the trailing edge.
        public const float ChordScale = 1.15f;
        // Strips with less vapor than this share of the side's fullest do not place the front.
        public const float FrontShare = 0.05f;
        // A strip's vapor is followed aft from where it starts until a gap this long, m: a tail
        // well behind the wing is not part of the wing's cloud.
        public const float RunGap = 1.5f;
        // A strip belongs to the cloud when its vapor starts within this of the line the other
        // strips start on: metres, or this share of the mean chord if more.
        public const float LineMetres = 0.6f, LineChords = 0.15f;
        // Where both sides have vapor, each cloud starts this far past the centreline and fades
        // in over twice that, so the two add up to one cloud there.
        public const float RootOverlap = 1f;        // m
        public const float MaxRootGap = 2.5f;       // m of fuselage the cloud is carried across
        public const float SmoothTime = 0.15f;      // s for a cloud to follow its fit
        public const float FullArea = 1.5f;         // m^2 of dense vapor on a side for full strength
        // A new line replaces the last frame's only when this much more vapor starts on it.
        public const float LineSwitch = 1.1f;

        static bool tried;
        static Shader shader;
        static GameObject prefab;
        static Texture noise;

        public static bool Available
        {
            get
            {
                if (!tried) Find();
                return shader != null && prefab != null;
            }
        }

        static void Find()
        {
            tried = true;
            try
            {
                foreach (var a in AssemblyLoader.loadedAssemblies)
                {
                    if (a.assembly.GetName().Name != "Waterfall") continue;
                    Type loader = a.assembly.GetType("Waterfall.ShaderLoader");
                    MethodInfo get = loader != null ? loader.GetMethod("GetShader", BindingFlags.Public | BindingFlags.Static) : null;
                    if (get != null) shader = get.Invoke(null, new object[] { ShaderName }) as Shader;
                    break;
                }
                if (shader != null)
                {
                    prefab = GameDatabase.Instance.GetModelPrefab(ModelPath);
                    noise = GameDatabase.Instance.GetTexture(NoisePath, false);
                }
            }
            catch (Exception e) { Debug.Log("[VORTEX] wing vapor: could not read Waterfall: " + e.Message); }
            Debug.Log("[VORTEX] wing vapor: Waterfall volumetric cloud " + (shader != null && prefab != null ? "available" : "not available"));
        }

        // One side's cloud. Root and tip are the two ends of its axis: span station x, the
        // axis's position along the flow, the vapor's half-chord, and the wing's height there.
        // The f-values are this frame's fit, which the others follow.
        class Cloud
        {
            public GameObject go;
            public MeshRenderer mr;
            public Material mat;
            public bool live, fit, joined;
            public float rootX, tipX, rootMid, tipMid, rootHalf, tipHalf, rootZ, tipZ, rootRise, strength;
            public float fRootX, fTipX, fRootMid, fTipMid, fRootHalf, fTipHalf, fRootZ, fTipZ, fRootRise, fStrength;

            public void Show(bool on) { if (mr != null) mr.enabled = on; }
        }

        // Left and right.
        readonly Cloud[] clouds = { new Cloud(), new Cloud() };
        // Each side's line from the last frame, as where it crosses the centreline and its slope.
        readonly bool[] lastLine = new bool[2];
        readonly float[] lastAt = new float[2], lastSlope = new float[2];
        float stripWidth = MinStripWidth;

        // Per strip, weighted by vapor density and area.
        readonly float[] sw = new float[Strips], sz = new float[Strips], sd = new float[Strips], sChord = new float[Strips];
        readonly float[] sMin = new float[Strips], sMax = new float[Strips];
        readonly bool[] keep = new bool[Strips];
        const int Bins = 32;
        readonly bool[] filled = new bool[Strips * Bins];
        const int EdgePoints = 48;
        readonly float[] edgeX = new float[EdgePoints + 1], edgeA = new float[EdgePoints + 1];

        // Fits and draws this frame's clouds from the parts carrying vapor. `down` is the unit
        // direction the air moves past the aircraft.
        public void Update(List<VaporRenderer.Source> active, Vessel vessel, Vector3 down, Color tint,
                           float userScale, float thickness, float dt)
        {
            Vector3 rootPos = vessel.rootPart.transform.position;

            // The suction side's mean normal sets which way the clouds stand.
            Vector3 normal = Vector3.zero;
            foreach (VaporRenderer.Source src in active)
            {
                VaporField f = src.field;
                Planform pf = src.box.planform;
                float sum = 0f;
                for (int j = 0; j < f.density.Length; j++) sum += f.sampleSide[j] * f.density[j] * pf.sampleShare[j];
                normal += src.box.Frame().n * (sum * pf.area / pf.samples.Length);
            }
            Vector3 up = -down;
            Vector3 across = Vector3.Cross(up, normal);
            bool any = across.sqrMagnitude > 1e-8f;
            foreach (Cloud c in clouds) c.fit = false;

            Vector3 stand = Vector3.zero;
            if (any)
            {
                across.Normalize();
                stand = Vector3.Cross(across, up).normalized;
                Measure(active, rootPos, across, down, stand);
                Fit(clouds[0], -1f, thickness);
                Fit(clouds[1], 1f, thickness);
                // Where both sides carry vapor, each runs a little past the centreline, so the
                // cloud is unbroken over the fuselage.
                bool joined = clouds[0].fit && clouds[1].fit
                              && -clouds[0].fRootX < MaxRootGap && clouds[1].fRootX < MaxRootGap;
                clouds[0].joined = clouds[1].joined = joined;
                if (joined)
                {
                    clouds[0].fRootX = RootOverlap;
                    clouds[1].fRootX = -RootOverlap;
                }
                if (clouds[0].fit) Front(clouds[0], -1f);
                if (clouds[1].fit) Front(clouds[1], 1f);
            }

            float follow = 1f - Mathf.Exp(-dt / SmoothTime);
            foreach (Cloud c in clouds)
            {
                if (c.fit)
                {
                    float k = c.live ? follow : 1f;
                    c.rootX += (c.fRootX - c.rootX) * k; c.tipX += (c.fTipX - c.tipX) * k;
                    c.rootMid += (c.fRootMid - c.rootMid) * k; c.tipMid += (c.fTipMid - c.tipMid) * k;
                    c.rootHalf += (c.fRootHalf - c.rootHalf) * k; c.tipHalf += (c.fTipHalf - c.tipHalf) * k;
                    c.rootZ += (c.fRootZ - c.rootZ) * k; c.tipZ += (c.fTipZ - c.tipZ) * k;
                    c.rootRise += (c.fRootRise - c.rootRise) * k;
                    if (!c.live) c.strength = 0f;
                    c.live = true;
                }
                c.strength += ((c.fit ? c.fStrength : 0f) - c.strength) * follow;
                if (!c.live || c.strength < 0.005f || !any)
                {
                    if (!c.fit && c.strength < 0.005f) c.live = false;
                    c.Show(false);
                    continue;
                }
                if (!Ensure(c)) continue;

                // The axis runs root to tip, lifted off the wing by the cloud's own radius at
                // each end, so the underside stays on the wing. The bulge rounds the taper into
                // a dome; its extra radius mid-span lifts the axis too.
                float taper = Taper(c.rootHalf, c.tipHalf), bulge = Bulge(taper);
                float radius = ChordScale * c.rootHalf;
                float rise = Mathf.Min(c.rootRise, MaxRiseChords * radius);
                float lift = rise * (Radius(0.5f, taper, bulge) - 0.5f * (1f + taper));
                Vector3 p0 = rootPos + across * c.rootX + down * c.rootMid + stand * (c.rootZ + rise + lift);
                Vector3 p1 = rootPos + across * c.tipX + down * c.tipMid + stand * (c.tipZ + rise * taper + lift);
                Vector3 axis = p1 - p0;
                float length = axis.magnitude;
                if (length < 0.2f) { c.Show(false); continue; }
                axis /= length;
                Vector3 off = (stand - axis * Vector3.Dot(stand, axis)).normalized;
                Transform t = c.go.transform;
                t.position = p0;
                t.rotation = Quaternion.LookRotation(off, -axis);
                t.localScale = new Vector3(radius * Square(c.tipX - c.rootX, c.tipMid - c.rootMid), length, rise);

                Color col = tint;
                col.a = 1f;
                c.mat.SetColor("_StartTint", col);
                c.mat.SetColor("_EndTint", col);
                c.mat.SetFloat("_ExpandLinear", taper - 1f);
                c.mat.SetFloat("_ExpandSquare", bulge);
                c.mat.SetFloat("_FadeIn", c.joined ? Mathf.Clamp(2f * RootOverlap / length, 0.05f, 0.6f) : 0.15f);
                c.mat.SetFloat("_Brightness", Brightness * c.strength * userScale);
                c.Show(true);
            }
        }

        // Sorts the vapor into span strips: how much, where along the flow it starts and ends,
        // how high the wing is there.
        void Measure(List<VaporRenderer.Source> active, Vector3 rootPos, Vector3 across, Vector3 down, Vector3 stand)
        {
            // Strips wide enough to reach the widest part with vapor; they only ever widen.
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (VaporRenderer.Source src in active)
            {
                Vector3 centre = src.box.Frame().origin - rootPos;
                float radius = src.box.planform.radius;
                stripWidth = Mathf.Max(stripWidth, 2.1f * (Mathf.Abs(Vector3.Dot(centre, across)) + radius) / Strips);
                float along = Vector3.Dot(centre, down);
                lo = Mathf.Min(lo, along - radius); hi = Mathf.Max(hi, along + radius);
            }
            float bin = Mathf.Max((hi - lo) / Bins, 1e-3f);
            for (int i = 0; i < Strips; i++)
            {
                sw[i] = sz[i] = sd[i] = sChord[i] = 0f;
                sMin[i] = float.MaxValue; sMax[i] = float.MinValue;
            }
            Array.Clear(filled, 0, filled.Length);
            foreach (VaporRenderer.Source src in active)
            {
                VaporField f = src.field;
                Planform pf = src.box.planform;
                PlaneFrame fr = src.box.Frame();
                float areaEach = pf.area / pf.samples.Length;
                Vector3 rel = fr.origin - rootPos;
                float x0 = Vector3.Dot(rel, across), a0 = Vector3.Dot(rel, down), z0 = Vector3.Dot(rel, stand);
                float xu = Vector3.Dot(fr.u, across), xv = Vector3.Dot(fr.v, across);
                float au = Vector3.Dot(fr.u, down), av = Vector3.Dot(fr.v, down);
                float zu = Vector3.Dot(fr.u, stand), zv = Vector3.Dot(fr.v, stand);
                float zn = Vector3.Dot(fr.n, stand) * 0.5f * pf.thickness;
                for (int j = 0; j < f.density.Length; j++)
                {
                    float d = f.density[j];
                    if (d < 0.01f) continue;
                    Vector2 q = pf.samples[j];
                    float x = x0 + q.x * xu + q.y * xv, a = a0 + q.x * au + q.y * av;
                    float z = z0 + q.x * zu + q.y * zv + f.sampleSide[j] * zn;
                    float w = d * pf.sampleShare[j] * areaEach;
                    int i = Mathf.Clamp((int)(x / stripWidth + 0.5f * Strips), 0, Strips - 1);
                    sw[i] += w; sz[i] += w * z; sd[i] += w * d; sChord[i] += w * f.sectionChord[j];
                    if (a < sMin[i]) sMin[i] = a;
                    if (a > sMax[i]) sMax[i] = a;
                    filled[i * Bins + Mathf.Clamp((int)((a - lo) / bin), 0, Bins - 1)] = true;
                }
            }

            // Each strip's vapor ends at the first long gap behind its start.
            int gap = Mathf.Max(3, Mathf.CeilToInt(RunGap / bin));
            for (int i = 0; i < Strips; i++)
            {
                if (sw[i] <= 1e-4f) continue;
                sMax[i] = Mathf.Min(sMax[i], lo + bin * RunEnd(filled, i * Bins, Bins, gap));
            }
        }

        // The bin just past the first run of filled bins, a run ending at `gap` empty ones.
        static int RunEnd(bool[] filled, int start, int count, int gap)
        {
            int end = -1, empty = 0;
            for (int b = 0; b < count; b++)
            {
                if (filled[start + b]) { end = b + 1; empty = 0; }
                else if (end >= 0 && ++empty >= gap) break;
            }
            return end < 0 ? count : end;
        }

        // Marks one side's strips that belong to its cloud: those whose vapor starts on the
        // straight line that the most vapor starts on. A strip that starts well off it (a tail
        // behind the wing, a strake ahead of it) is left out of the fit. The last frame's line is
        // kept unless a new one is clearly better, so strips do not flip in and out.
        void Keep(float sign)
        {
            int first = sign < 0f ? 0 : Strips / 2, last = sign < 0f ? Strips / 2 - 1 : Strips - 1;
            int side = sign < 0f ? 0 : 1;
            float w = 0f, chord = 0f;
            for (int i = first; i <= last; i++)
            {
                keep[i] = sw[i] > 1e-4f;
                if (keep[i]) { w += sw[i]; chord += sw[i] * (sMax[i] - sMin[i]); }
            }
            if (w <= 0f) { lastLine[side] = false; return; }
            float within = Mathf.Max(LineMetres, LineChords * chord / w);

            float best = -1f, bestSlope = 0f, bestAt = 0f;
            for (int i = first; i <= last; i++)
            {
                if (!keep[i]) continue;
                for (int j = i; j <= last; j++)
                {
                    if (!keep[j]) continue;
                    // The line through strips i and j; level when they are the same strip.
                    float slope = j > i ? (sMin[j] - sMin[i]) / ((j - i) * stripWidth) : 0f;
                    float at = sMin[i] - slope * Station(i);
                    float on = OnLine(first, last, at, slope, within);
                    if (on > best) { best = on; bestSlope = slope; bestAt = at; }
                }
            }
            if (lastLine[side] && best < LineSwitch * OnLine(first, last, lastAt[side], lastSlope[side], within))
            {
                bestAt = lastAt[side]; bestSlope = lastSlope[side];
            }
            for (int k = first; k <= last; k++)
                if (keep[k] && Mathf.Abs(sMin[k] - (bestAt + bestSlope * Station(k))) > within) keep[k] = false;
            lastLine[side] = true;
            lastAt[side] = bestAt; lastSlope[side] = bestSlope;
        }

        // A strip's span station, m from the centreline.
        float Station(int i) { return (i + 0.5f - 0.5f * Strips) * stripWidth; }

        // How much of a side's vapor starts on the line that crosses the centreline at `at`.
        float OnLine(int first, int last, float at, float slope, float within)
        {
            float on = 0f;
            for (int k = first; k <= last; k++)
                if (keep[k] && Mathf.Abs(sMin[k] - (at + slope * Station(k))) <= within) on += sw[k];
            return on;
        }

        // Straight lines through one side's strips, for the vapor's mid-chord, half-chord and
        // height against span station, read off at the side's inner and outer ends.
        void Fit(Cloud c, float sign, float thickness)
        {
            int first = sign < 0f ? 0 : Strips / 2, last = sign < 0f ? Strips / 2 - 1 : Strips - 1;
            float w = 0f, wx = 0f, wxx = 0f, wMid = 0f, wxMid = 0f, wHalf = 0f, wxHalf = 0f, wZ = 0f, wxZ = 0f;
            float density = 0f, height = 0f, inner = float.MaxValue, outer = 0f;
            Keep(sign);
            for (int i = first; i <= last; i++)
            {
                if (!keep[i]) continue;
                float x = Station(i);
                float mid = 0.5f * (sMin[i] + sMax[i]), half = Mathf.Max(0.5f * (sMax[i] - sMin[i]), 0.25f);
                float d = sd[i] / sw[i];
                w += sw[i]; wx += sw[i] * x; wxx += sw[i] * x * x;
                wMid += sw[i] * mid; wxMid += sw[i] * x * mid;
                wHalf += sw[i] * half; wxHalf += sw[i] * x * half;
                wZ += sz[i]; wxZ += x * sz[i];
                density += sw[i] * d;
                height += sw[i] * Mathf.Min(CloudHeightChords * sChord[i] / sw[i] * Mathf.Sqrt(d), MaxCloudHeight);
                inner = Mathf.Min(inner, Mathf.Abs(x) - 0.5f * stripWidth);
                outer = Mathf.Max(outer, Mathf.Abs(x) + 0.5f * stripWidth);
            }
            if (w <= 1e-4f) return;

            float mean = wx / w, spread = wxx - wx * wx / w;
            bool sloped = spread > 1e-3f * w;
            float midSlope = sloped ? (wxMid - wx * wMid / w) / spread : 0f;
            float halfSlope = sloped ? (wxHalf - wx * wHalf / w) / spread : 0f;
            float zSlope = sloped ? (wxZ - wx * wZ / w) / spread : 0f;
            c.fRootX = sign * Mathf.Max(inner, 0f);
            c.fTipX = sign * outer;
            float atMean = wHalf / w;
            c.fRootMid = wMid / w + midSlope * (c.fRootX - mean);
            c.fTipMid = wMid / w + midSlope * (c.fTipX - mean);
            c.fRootHalf = Mathf.Max(atMean + halfSlope * (c.fRootX - mean), 0.25f);
            c.fTipHalf = Mathf.Max(atMean + halfSlope * (c.fTipX - mean), 0.1f);
            c.fRootZ = wZ / w + zSlope * (c.fRootX - mean);
            c.fTipZ = wZ / w + zSlope * (c.fTipX - mean);
            // Height goes with the chord: the mean height belongs to the mean half-chord.
            c.fRootRise = Mathf.Max(0.5f * height / w * thickness * c.fRootHalf / atMean, 0.05f);
            c.fStrength = Mathf.Lerp(0.08f, 1f, density / w) * Mathf.Clamp01(w / FullArea);
            c.fit = true;
        }

        // The shader's radius along the axis, as a multiple of the root's: s is 0 at the root
        // and 1 at the tip. _ExpandLinear is taper - 1 and _ExpandSquare is the bulge.
        static float Radius(float s, float taper, float bulge)
        {
            float line = 1f + (taper - 1f) * s;
            return Mathf.Sqrt(line * line + 10f * bulge * (s - s * s));
        }

        // Sections are cut square to the axis, so a swept axis needs them this much narrower to
        // keep their width along the flow. `run` is the axis's span, `sweep` its run along the flow.
        static float Square(float run, float sweep)
        {
            return Mathf.Max(Mathf.Abs(run) / Mathf.Max(Mathf.Sqrt(run * run + sweep * sweep), 1e-4f), 0.2f);
        }

        static float Taper(float rootHalf, float tipHalf) { return Mathf.Clamp(tipHalf / Mathf.Max(rootHalf, 0.05f), 0.1f, 2f); }

        // Rounds a tapering cloud's straight sides into a dome.
        static float Bulge(float taper) { return Mathf.Max(0f, (1f - taper) / 10f); }

        // Turns and slides the cloud's axis until its front edge, as drawn, lies along where the
        // vapor starts: the best straight fit over its strips. The front is a little curved, so
        // it ends up slightly ahead of the wing's leading edge mid-span and slightly behind it
        // at the root and tip.
        void Front(Cloud c, float sign)
        {
            int first = sign < 0f ? 0 : Strips / 2, last = sign < 0f ? Strips / 2 - 1 : Strips - 1;
            float most = 0f;
            for (int i = first; i <= last; i++) if (keep[i]) most = Mathf.Max(most, sw[i]);
            float run = c.fTipX - c.fRootX;
            if (most <= 0f || Mathf.Abs(run) < 0.1f) return;
            float taper = Taper(c.fRootHalf, c.fTipHalf), bulge = Bulge(taper);

            // Turning the axis moves the edge again, so a few passes.
            for (int pass = 0; pass < 3; pass++)
            {
                // The front edge as drawn, seen from above: each section is square to the axis,
                // so on a swept axis its front point lies off to one side of its span station.
                float sweep = c.fTipMid - c.fRootMid;
                float square = Square(run, sweep), along = Mathf.Sqrt(run * run + sweep * sweep);
                float nx = sweep / along, na = -run / along;
                if (na > 0f) { nx = -nx; na = -na; }
                for (int k = 0; k <= EdgePoints; k++)
                {
                    float s = (float)k / EdgePoints;
                    float radius = ChordScale * c.fRootHalf * square * Radius(s, taper, bulge);
                    edgeX[k] = c.fRootX + run * s + nx * radius;
                    edgeA[k] = c.fRootMid + sweep * s + na * radius;
                }

                // How far the vapor's start is behind the edge at each strip, fitted with a
                // straight line along the axis: s is 0 at the root end and 1 at the tip.
                float w = 0f, ws = 0f, wss = 0f, we = 0f, wse = 0f;
                for (int i = first; i <= last; i++)
                {
                    if (!keep[i] || sw[i] < FrontShare * most) continue;
                    float x = Station(i), front = float.MaxValue;
                    for (int k = 0; k < EdgePoints; k++)
                    {
                        float x0 = edgeX[k], x1 = edgeX[k + 1];
                        if ((x0 - x) * (x1 - x) > 0f || x0 == x1) continue;
                        front = Mathf.Min(front, edgeA[k] + (edgeA[k + 1] - edgeA[k]) * (x - x0) / (x1 - x0));
                    }
                    if (front == float.MaxValue) continue;
                    float s = Mathf.Clamp01((x - c.fRootX) / run), e = sMin[i] - front;
                    w += sw[i]; ws += sw[i] * s; wss += sw[i] * s * s; we += sw[i] * e; wse += sw[i] * s * e;
                }
                if (w <= 0f) break;
                float spread = wss - ws * ws / w;
                // Strips bunched at one end cannot say which way to turn it.
                float slope = spread > 0.01f * w ? Mathf.Clamp((wse - ws * we / w) / spread, -3f, 3f) : 0f;
                float atRoot = we / w - slope * ws / w;
                c.fRootMid += atRoot;
                c.fTipMid += atRoot + slope;
            }

            if (WingVaporAddon.Verbose && Time.frameCount % 100 == 0)
            {
                int kept = 0, all = 0;
                for (int i = first; i <= last; i++) { if (sw[i] > 1e-4f) all++; if (keep[i]) kept++; }
                Debug.Log($"[VORTEX] wing vapor: cloud {(sign < 0f ? "left" : "right")} span {c.fRootX:F1}..{c.fTipX:F1} m, "
                          + $"axis {c.fRootMid:F1}..{c.fTipMid:F1} m aft of root part, half-chord {c.fRootHalf:F1}..{c.fTipHalf:F1} m, "
                          + $"rise {c.fRootRise:F2} m, {kept} of {all} strips");
            }
        }

        bool Ensure(Cloud c)
        {
            if (c.go != null) return true;
            // The shader tests against the scene's depth itself.
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (cam != null) cam.depthTextureMode |= DepthTextureMode.Depth;

            GameObject inst = UnityEngine.Object.Instantiate(prefab);
            inst.SetActive(true);
            MeshRenderer mr = inst.GetComponentInChildren<MeshRenderer>(true);
            MeshFilter mf = mr != null ? mr.GetComponent<MeshFilter>() : null;
            if (mf == null) { UnityEngine.Object.Destroy(inst); return false; }
            // Only the mesh object is kept, unparented, so its transform is the shader's space.
            if (mr.transform != inst.transform)
            {
                mr.transform.SetParent(null, false);
                UnityEngine.Object.Destroy(inst);
            }
            c.go = mr.gameObject;
            c.go.name = "WingVaporCloud";
            c.go.SetActive(true);
            c.mr = mr;
            // The shader widens the mesh past its own bounds.
            mf.mesh.bounds = new Bounds(new Vector3(0f, -0.5f, 0f), new Vector3(6f, 3f, 6f));

            c.mat = new Material(shader);
            if (noise != null) c.mat.SetTexture("_MainTex", noise);
            c.mat.SetFloat("_FadeOut", 0.35f);
            c.mat.SetFloat("_Falloff", 0f);
            c.mat.SetFloat("_FalloffStart", 0f);
            c.mat.SetFloat("_Fresnel", 2f);
            c.mat.SetFloat("_FresnelFadeIn", 0f);
            c.mat.SetFloat("_FresnelInvert", 0f);
            c.mat.SetFloat("_TintFalloff", 0f);
            c.mat.SetFloat("_TintFresnel", 0f);
            c.mat.SetFloat("_LengthBrightness", 1f);
            c.mat.SetFloat("_ClipBrightness", 1f);
            c.mat.SetFloat("_Noise", 3.5f);
            c.mat.SetFloat("_NoiseFresnel", 2.5f);
            // The texture scrolls around the axis, which runs along the span: over the wing.
            c.mat.SetFloat("_TileX", 3f);
            c.mat.SetFloat("_TileY", 2f);
            c.mat.SetFloat("_SpeedX", 10f);
            c.mat.SetFloat("_SpeedY", 0f);
            c.mat.SetFloat("_Seed", UnityEngine.Random.Range(-10f, 10f));
            mr.sharedMaterial = c.mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return true;
        }

        public void Hide()
        {
            foreach (Cloud c in clouds)
            {
                c.live = false;
                c.strength = 0f;
                c.Show(false);
            }
        }

        public void Clear()
        {
            foreach (Cloud c in clouds)
            {
                if (c.go != null) UnityEngine.Object.Destroy(c.go);
                if (c.mat != null) UnityEngine.Object.Destroy(c.mat);
                c.go = null; c.mr = null; c.mat = null; c.live = false; c.strength = 0f;
            }
            stripWidth = MinStripWidth;
            lastLine[0] = lastLine[1] = false;
        }
    }
}
