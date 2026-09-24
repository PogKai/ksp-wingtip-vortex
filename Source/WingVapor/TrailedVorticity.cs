using System.Collections.Generic;
using UnityEngine;

namespace VortexVapor
{
    // One lifting surface as the shedding model sees it. No KSP types, so the whole model can be
    // run offline.
    public struct Surface
    {
        public long id;             // the part's flight ID
        public string name;
        public Planform planform;
        public PlaneFrame frame;
        public Vector3 lift;        // N, world: stock's force on this part
        // N, world: the share of `lift` the vapor may condense from. Equal to `lift` except on a
        // roll control surface, where it is the mean over its symmetry group (AeroState.Surfaces).
        public Vector3 vaporLift;
        // 0..1: how much of the part is stalled. FAR reports it; stock has no stall model, so 0.
        public float stall;
    }

    // Answers "which parts' volumes contain this point", as flight IDs, excluding one part.
    public interface IBodyQuery
    {
        void BodiesAt(Vector3 world, long exclude, List<long> into);
    }

    // One trailing vortex line leaving the airframe this step.
    public struct TrailedLine
    {
        public long id;             // stable across steps: the smallest edge id it was built from
        public Vector3 position;    // world space, where the line leaves the airframe
        public float circulation;   // m^2/s, signed about the downstream direction
        public float coreRadius;    // m, at birth
    }

    // Turns per-part lift into the trailing vortex lines the airframe sheds. There is no notion
    // here of a wing, a side, a tip or a group, and nothing is anchored.
    //
    // THE MODEL. Every lifting part is cut into a few spanwise panels, and every panel is a
    // horseshoe vortex. By Kutta-Joukowski its bound circulation is Gamma = L / (rho V w), w being
    // its width across the flow. By Helmholtz a vortex line cannot end in the fluid, so at each of
    // the panel's two span edges that circulation turns and trails downstream: +Gamma at one edge,
    // -Gamma at the other. Where two panels meet, their trailing legs mostly cancel; where nothing
    // continues outboard, the whole bound circulation trails. That place is the wingtip, and no
    // code decides so.
    //
    // A WING IS MANY PARTS. The same wing built as one part, as ten, or as ten with some clipped on
    // top of each other must shed the same wake. Stock does not give it one: every part lifts for
    // its full area, so a clipped stack lifts several times over and the loading piles up wherever
    // the builder stacked parts. Three things undo that, none of which classifies parts:
    //   - Shared area. Each panel's stock circulation is scaled by the share of its planform that
    //     no other lifting part also covers (PlanformOverlap). Stacked duplicates add up to one.
    //   - Resolution. A long part is cut into up to MaxSubPanels panels, stock's lift spread over
    //     them by local chord, so one long part and several short ones resolve the span alike.
    //   - Spanwise induction. Stock has none; LiftingLine adds Prandtl's, so loading falls off
    //     toward the tips and smooths across joints. Each connected structure is then scaled back
    //     to its stock lift, which is what actually holds the aircraft up.
    //
    // Bookkeeping around the edges:
    //   - Welding. Edges at the same spanwise station are one line: panels meeting at a joint,
    //     or stacked along the chord (slat, panel, flap) and touching chordwise.
    //   - Carry-through. An edge that ends inside another part does not end in the fluid. When the
    //     edges of two lifting surfaces are buried in a part that is not one of their own surfaces,
    //     face each other and sit level with each other (a wing through a fuselage), the bound
    //     vortex passes through the body and the two are one line, which cancels in symmetric flight.
    //   - Folding. Lines too weak to matter are merged into their nearest same-sign neighbour,
    //     conserving circulation and its centroid, and the count is capped.
    //
    // Roll-up happens in WakeSimulation, from the lines' induction on each other.
    public static class TrailedVorticity
    {
        // Two edges closer than this fraction of the narrower local panel width are the same
        // station. Absorbs slop at attach points.
        public const float EdgeWeldFraction = 0.25f;
        // How far past an edge the solid/air test looks, m. A gap narrower than this is touching.
        public const float AttachProbe = 0.25f;
        // How often the solid/air test is re-run, s. It is geometry, so it barely changes.
        public const float AttachRefreshInterval = 1f;
        // |cos| between two outward directions for them to count as facing or parallel.
        public const float FacingDot = 0.7f;
        // Lines weaker than this (m^2/s), or than this fraction of the strongest line, are folded.
        public const float MinCirculation = 0.5f;
        public const float MinLineFraction = 0.05f;
        // Most lines shed per step. Wake cost is quadratic in it (see WakeSimulation).
        public const int MaxLines = 16;
        // Core radius at birth as a fraction of the local chord. ASSUMPTION: a near-tip core of a
        // few percent of chord. To be pinned down with the condensation model (Milestone 3).
        public const float CoreChordFraction = 0.05f;
        // ...but never smaller than this fraction of the local panel width. Each line stands for
        // the vortex sheet shed across its panels, and a sheet cut into point vortices only
        // behaves like a sheet if neighbouring cores reach toward each other (the vortex-blob
        // overlap condition). A third keeps neighbours distinct (their cores do not touch)
        // while lines much closer than their spacing, like a narrow control surface's two edges,
        // overlap and net out instead of forming a dipole that flies off at the speed clamp.
        // Without it a pointed tip got a core of ~0 m (5% of a zero chord). v0.3.0 flights.
        public const float BlobFraction = 0.35f;
        // A part is cut into about span / mean chord panels, at most this many.
        public const int MaxSubPanels = 4;
        // How often the lifting-line matrix is rebuilt, s. It depends on geometry and on the flow
        // direction in the vessel's frame, both slow; stock's loads change every step and are
        // back-substituted every step. Rebuilding is cubic in the panel count (2 ms a step at 0.1 s
        // on a 242-part craft offline), and a few degrees of angle-of-attack change between
        // rebuilds barely moves the kernel.
        public const float LiftingLineRefresh = 0.4f;

        // Set to true to fill `Dump` on the next step.
        public static bool DebugDump = false;

        // Diagnostic switches for the offline checks, which compare each layer's effect. Not
        // player settings; always on in game.
        public static bool UseSharedArea = true, UseLiftingLine = true;
        // False to stop after the loading, skipping the trailing lines (for when no wake is
        // simulated): their folding is the part of this that grows fastest with part count.
        public static bool BuildLines = true;
        public static readonly List<string> Dump = new List<string>();
        public static int LiftingLineFallbacks { get { return liftingLine.Fallbacks; } }

        struct Panel
        {
            public int surface;
            public Vector3 center, span;
            public float halfSpan, chord, share, gStock, g2D, gamma;
        }

        struct Edge
        {
            public long id;
            public int surface;
            public long body;           // the part it belongs to
            public Vector3 station;     // world, mid-chord at this span station
            public float aft;           // trailing edge here, as a coordinate along downstream
            public Vector3 outward;     // unit, along the span axis, out of the part at its ends
            public Vector3 chordDir;    // unit, the part's chord in its own plane
            public float span;          // the part's width across the flow, m
            public float width;         // the panels' width at this joint, m: the local line spacing
            public float chord;         // local chord, m
            public float core;          // core radius at birth, m
            public List<long> inside;   // parts the space just past this edge lies in (null: interior)
            public int plusPanel;       // panel whose +span end is here (sheds +Gamma), or -1
            public int minusPanel;      // panel whose -span end is here (sheds -Gamma), or -1
            public float gamma;
        }

        static readonly List<Panel> panels = new List<Panel>();
        static readonly List<Edge> edges = new List<Edge>();
        static readonly List<TrailedLine> lines = new List<TrailedLine>();
        static readonly Dictionary<long, List<long>> insideCache = new Dictionary<long, List<long>>();
        static float nextAttachRefresh = 0f, nextLiftingLine = 0f;
        static long attachSignature = -1, overlapSignature = -1, topologySignature = -1, weldSignature = -1;
        static float nextWeld = 0f;
        static readonly List<int> weldPairs = new List<int>();
        static readonly List<int> carryPairs = new List<int>();
        static bool recordCarry = false;
        static Vector3[] wStation = new Vector3[0], wOut = new Vector3[0], wChordDir = new Vector3[0];
        static float[] wWidth = new float[0], wChord = new float[0];
        static int[] wSurface = new int[0];
        static readonly LiftingLine liftingLine = new LiftingLine();
        static int[] surfFirstPanel = new int[0], surfPanelCount = new int[0];
        static readonly List<Planform> overlapForms = new List<Planform>();
        static readonly List<PlaneFrame> overlapFrames = new List<PlaneFrame>();
        static readonly HashSet<long> liftingIds = new HashSet<long>();

        // Per-edge scratch, indexed by edge; for a group, read at the group's root edge.
        static int[] parent = new int[0], rootOf = new int[0], best = new int[0], groupIndex = new int[0],
                     groupPanel = new int[0], compOf = new int[0],
                     firstMember = new int[0], nextMember = new int[0];
        static float[] bestDist = new float[0], gSum = new float[0], wSum = new float[0],
                       aftMax = new float[0], coreMax = new float[0], minSpan = new float[0], topG = new float[0];
        static Vector3[] wPos = new Vector3[0], outSum = new Vector3[0], topChord = new Vector3[0],
                         compStock = new Vector3[0], compCarried = new Vector3[0];
        static long[] minId = new long[0];
        static bool[] buried = new bool[0], hasEnd = new bool[0];

        // Lifting-line inputs, grown as needed.
        static Vector3[] llCenter = new Vector3[0], llSpan = new Vector3[0], llGPos = new Vector3[0];
        static float[] llHalf = new float[0], llChord = new float[0], llG2D = new float[0], llStock = new float[0],
                       llGamma = new float[0], llCore = new float[0], llSign = new float[0];
        static int[] llPanel = new int[0], llGroup = new int[0];

        public static void ClearCache()
        {
            insideCache.Clear();
            attachSignature = overlapSignature = topologySignature = weldSignature = -1;
        }

        public static List<TrailedLine> Compute(List<Surface> surfaces, IBodyQuery bodies,
            Vector3 vesselVelocity, float airDensity, float airspeed, float time)
        {
            lines.Clear();
            edges.Clear();
            panels.Clear();
            if (surfFirstPanel.Length < surfaces.Count)
            {
                surfFirstPanel = new int[2 * surfaces.Count];
                surfPanelCount = new int[2 * surfaces.Count];
            }
            for (int i = 0; i < surfaces.Count; i++) surfPanelCount[i] = 0;
            if (DebugDump) Dump.Clear();
            if (surfaces.Count == 0 || airspeed < 1f || airDensity < 1e-4f) return lines;

            // Downstream: the way the air moves past the aircraft, and the way every trailing leg runs.
            Vector3 down = -vesselVelocity / airspeed;
            Vector3 vInf = down * airspeed;
            float rhoV2 = airDensity * airspeed * airspeed;

            long signature = surfaces.Count;
            foreach (var s in surfaces) signature = signature * 31 + s.id;
            if (signature != overlapSignature)
            {
                // Which area is shared depends only on the airframe, so it is worked out once per
                // change of the part set, not per step.
                overlapSignature = signature;
                overlapForms.Clear(); overlapFrames.Clear(); liftingIds.Clear();
                foreach (var s in surfaces) { overlapForms.Add(s.planform); overlapFrames.Add(s.frame); liftingIds.Add(s.id); }
                PlanformOverlap.Compute(overlapForms, overlapFrames);
            }
            bool refresh = time >= nextAttachRefresh || signature != attachSignature;
            if (refresh)
            {
                nextAttachRefresh = time + AttachRefreshInterval;
                attachSignature = signature;
            }

            for (int i = 0; i < surfaces.Count; i++) BuildPanels(surfaces[i], i, bodies, down, vInf, rhoV2, refresh);
            int m = edges.Count;
            if (m == 0) return lines;
            EnsureScratch(m);

            for (int i = 0; i < m; i++) parent[i] = i;
            bool rewelded = Weld(m, time);
            // Carry-through depends only on the airframe (which edges weld, which parts they end
            // in), so like the welds it is worked out when those are, and replayed in between.
            // Its pairwise search is the costliest thing here on a big craft.
            if (rewelded || refresh)
            {
                SummariseGroups(m, true);
                carryPairs.Clear();
                recordCarry = true;
                CarryThrough(m);
                PoolInsideBodies(m);
                recordCarry = false;
            }
            else
                for (int k = 0; k + 1 < carryPairs.Count; k += 2) Union(carryPairs[k], carryPairs[k + 1]);
            SummariseGroups(m, false);   // again: carry-through merged groups

            SolveLoading(m, down, time);
            // The panels' loading is all the wing vapor needs; the lines are only for the wake.
            if (!BuildLines)
            {
                if (DebugDump) { DumpSurfaces(surfaces); DebugDump = false; }
                return lines;
            }

            for (int i = 0; i < m; i++)
            {
                Edge e = edges[i];
                e.gamma = (e.plusPanel >= 0 ? panels[e.plusPanel].gamma : 0f)
                        - (e.minusPanel >= 0 ? panels[e.minusPanel].gamma : 0f);
                edges[i] = e;
            }
            SummariseGroups(m, false);   // and again: the positions are weighted by what each edge sheds
            MatchStockLift(m, down);

            float strongest = 0f;
            for (int r = 0; r < m; r++)
            {
                if (parent[r] != r) continue;
                // Cross-flow position from the edges, weighted by how much each one sheds; along
                // the flow, the trailing edge, since that is where the line leaves the airframe.
                Vector3 p = wPos[r] / wSum[r];
                p += down * (aftMax[r] - Vector3.Dot(p, down));
                lines.Add(new TrailedLine
                {
                    id = minId[r],
                    position = p,
                    circulation = gSum[r],
                    coreRadius = coreMax[r]
                });
                strongest = Mathf.Max(strongest, Mathf.Abs(gSum[r]));
            }

            if (DebugDump) DumpSurfaces(surfaces);
            Fold(down, Mathf.Max(MinCirculation, MinLineFraction * strongest));
            if (DebugDump)
            {
                DebugDump = false;
                DumpLines(surfaces, down);
            }
            return lines;
        }

        // 1. Cut a surface into panels across the flow, and put an edge at every joint.
        static void BuildPanels(Surface sf, int si, IBodyQuery bodies, Vector3 down, Vector3 vInf, float rhoV2, bool refresh)
        {
            Planform pf = sf.planform;
            if (pf == null || pf.hull.Length < 3) return;
            PlaneFrame fr = sf.frame;

            // Span axis: in the part's own plane, across the flow. It moves with sideslip but not
            // with angle of attack, since the flow's component along the normal drops out.
            Vector3 s = Vector3.Cross(fr.n, down);
            if (s.sqrMagnitude < 0.01f) return;           // flow square onto the surface: no span
            s.Normalize();
            Vector2 s2 = fr.InPlane(s).normalized;
            Vector2 c2 = new Vector2(-s2.y, s2.x);
            Vector3 chordDir = (fr.u * c2.x + fr.v * c2.y).normalized;

            float lo, hi;
            pf.SpanRange(s2, out lo, out hi);
            float w = hi - lo;
            if (w < 0.1f) return;
            float meanChord = pf.area / w;
            int n = Mathf.Clamp(Mathf.RoundToInt(w / Mathf.Max(meanChord, 0.01f)), 1, MaxSubPanels);

            // Kutta-Joukowski in vector form, rho * w * (Vinf x Gamma) = L, solves to
            // Gamma = (L x Vinf) / (rho V^2 w). Its component along s is the part's bound
            // circulation; the sign comes from the lift, so a part carrying download is negative.
            float gPart = Vector3.Dot(Vector3.Cross(sf.lift, vInf), s) / (rhoV2 * w);

            int firstPanel = panels.Count;
            surfFirstPanel[si] = firstPanel;
            surfPanelCount[si] = n;
            for (int j = 0; j < n; j++)
            {
                float a = lo + w * j / n, b = lo + w * (j + 1) / n, mid = 0.5f * (a + b);
                float c0, c1;
                if (!pf.ChordAt(s2, mid, out c0, out c1)) { c0 = c1 = 0f; }
                float chord = Mathf.Max(c1 - c0, 0.01f);
                float share = UseSharedArea ? pf.Share(s2, a, b) : 1f;
                // Stock lifts the whole part at one angle of attack, so its lift is spread over the
                // panels by area: Gamma is proportional to local chord.
                float gStock = gPart * chord / Mathf.Max(meanChord, 0.01f);
                panels.Add(new Panel
                {
                    surface = si,
                    center = fr.ToWorld(s2 * mid + c2 * (0.5f * (c0 + c1))),
                    span = s,
                    halfSpan = 0.5f * (b - a),
                    chord = chord,
                    share = share,
                    gStock = gStock,
                    g2D = gStock * share
                });
            }

            for (int j = 0; j <= n; j++)
            {
                float sigma = lo + w * j / n;
                // Just inside the end, so a streamwise tip gives its tip chord, not a point.
                float probe = Mathf.Clamp(sigma, lo + 0.01f * w, hi - 0.01f * w);
                float c0, c1;
                if (!pf.ChordAt(s2, probe, out c0, out c1)) { c0 = c1 = 0f; }
                Vector3 front = fr.ToWorld(s2 * sigma + c2 * c0), back = fr.ToWorld(s2 * sigma + c2 * c1);
                bool end = j == 0 || j == n;
                var e = new Edge
                {
                    id = (sf.id << 4) | (long)j,
                    surface = si,
                    body = sf.id,
                    station = 0.5f * (front + back),
                    aft = Mathf.Max(Vector3.Dot(front, down), Vector3.Dot(back, down)),
                    outward = j == 0 ? -s : s,
                    chordDir = chordDir,
                    span = w,
                    width = w / n,
                    chord = Mathf.Max(c1 - c0, 0.01f),
                    core = Mathf.Max(CoreChordFraction * (c1 - c0), BlobFraction * w / n),
                    plusPanel = j > 0 ? firstPanel + j - 1 : -1,
                    minusPanel = j < n ? firstPanel + j : -1
                };
                e.inside = end ? Inside(bodies, e, refresh) : null;
                edges.Add(e);
            }
        }

        static List<long> Inside(IBodyQuery bodies, Edge e, bool refresh)
        {
            List<long> list;
            if (!insideCache.TryGetValue(e.id, out list))
            {
                list = new List<long>(2);
                insideCache[e.id] = list;
                refresh = true;
            }
            if (refresh && bodies != null) bodies.BodiesAt(e.station + e.outward * AttachProbe, e.body, list);
            return list;
        }

        // 2. Edges at the same station are one line: side by side along the span (a joint, the
        //    two edges face each other) or stacked along the chord (parallel edges, parts touching
        //    chordwise). The chordwise offset is ignored when measuring, since two lines at the
        //    same cross-flow position are the same line downstream.
        //
        //    Which edges weld depends only on the airframe and on sideslip, and the all-pairs test
        //    is the most expensive thing in this file (about two thirds of it at 66 parts), so the
        //    pairs are found once per AttachRefreshInterval, or when the edge set changes, and
        //    replayed every step in between.
        static bool Weld(int m, float time)
        {
            long signature = m;
            for (int i = 0; i < m; i++) signature = signature * 31 + edges[i].id;
            if (signature == weldSignature && time < nextWeld)
            {
                for (int k = 0; k + 1 < weldPairs.Count; k += 2) Union(weldPairs[k], weldPairs[k + 1]);
                return false;
            }
            weldSignature = signature;
            nextWeld = time + AttachRefreshInterval;
            weldPairs.Clear();

            if (wStation.Length < m)
            {
                int n = Mathf.Max(m, 2 * wStation.Length);
                wStation = new Vector3[n]; wOut = new Vector3[n]; wChordDir = new Vector3[n];
                wWidth = new float[n]; wChord = new float[n]; wSurface = new int[n];
            }
            for (int i = 0; i < m; i++)
            {
                Edge e = edges[i];
                wStation[i] = e.station; wOut[i] = e.outward; wChordDir[i] = e.chordDir;
                wWidth[i] = e.width; wChord[i] = e.chord; wSurface[i] = e.surface;
            }
            for (int i = 0; i < m; i++)
            {
                for (int j = i + 1; j < m; j++)
                {
                    if (wSurface[i] == wSurface[j]) continue;
                    // Relative to the local panel width, not the part's span: with parts cut into
                    // panels, a part-span tolerance chained a stack's panel joints into its root.
                    float tol = EdgeWeldFraction * Mathf.Min(wWidth[i], wWidth[j]);
                    float reach = tol + 0.5f * (wChord[i] + wChord[j]) + tol;
                    Vector3 d = wStation[j] - wStation[i];
                    if (d.sqrMagnitude > reach * reach) continue;   // too far apart for either test
                    if (Mathf.Abs(Vector3.Dot(wOut[i], wOut[j])) < FacingDot) continue;
                    float alongChord = Vector3.Dot(d, wChordDir[i]);
                    Vector3 cross = d - wChordDir[i] * alongChord;
                    if (cross.sqrMagnitude < tol * tol && Mathf.Abs(alongChord) < 0.5f * (wChord[i] + wChord[j]) + tol)
                    {
                        Union(i, j);
                        weldPairs.Add(i); weldPairs.Add(j);
                    }
                }
            }
            return true;
        }

        // 3. Carry-through. A group is buried when every one of its edges ends inside a part that
        //    is not itself one of the group's surfaces (so a joint between two panels, where each
        //    edge ends inside its neighbour, does not count). Two buried groups facing each other
        //    across the body, level with each other, are the same bound vortex passing through
        //    it. Mutual nearest only, so a wing root pairs with the other wing root and not with
        //    the canard root on the far side.
        // 3b. Everything buried in the same solid body is one line through that body. Vorticity
        //     that trails from inside a fuselage cannot leave it there; the body carries it. So
        //     every buried group whose ends lie inside the same non-lifting part is pooled, and in
        //     symmetric flight the pool cancels. This catches what the facing rule above cannot:
        //     the joint between two parts clipped into a fuselage has no one outward direction,
        //     and a KSP fighter can have a dozen of them (v0.3.0 Su-33). A wingtip buried in a tip
        //     pod still trails from the tip, because nothing else shares the pod, and a wing does
        //     not count as a body here: its own bound vortex is already in the model.
        static void PoolInsideBodies(int m)
        {
            // rootOf, buried and the member lists are from before carry-through; Union copes with
            // groups that carry-through has already joined.
            for (int g = 0; g < m; g++)
            {
                if (rootOf[g] != g || !buried[g]) continue;
                for (int h = g + 1; h < m; h++)
                {
                    if (rootOf[h] != h || !buried[h]) continue;
                    if (ShareBody(g, h)) Union(g, h);
                }
            }
        }

        static bool ShareBody(int g, int h)
        {
            for (int i = firstMember[g]; i >= 0; i = nextMember[i])
            {
                List<long> a = edges[i].inside;
                if (a == null) continue;
                for (int k = 0; k < a.Count; k++)
                {
                    if (liftingIds.Contains(a[k])) continue;
                    for (int j = firstMember[h]; j >= 0; j = nextMember[j])
                    {
                        List<long> b = edges[j].inside;
                        if (b != null && b.Contains(a[k])) return true;
                    }
                }
            }
            return false;
        }

        static void CarryThrough(int m)
        {
            for (int r = 0; r < m; r++) { best[r] = -1; bestDist[r] = float.MaxValue; }

            for (int g = 0; g < m; g++)
            {
                if (parent[g] != g || !buried[g]) continue;
                for (int h = g + 1; h < m; h++)
                {
                    if (parent[h] != h || !buried[h]) continue;
                    if (Vector3.Dot(outSum[g], outSum[h]) > -FacingDot) continue;

                    // `along` is the gap between them: positive across a fuselage, negative (down to
                    // minus a panel's span) where two panels overlap. Far more negative means the
                    // two point away from each other, like a pair of wingtip pods.
                    float narrower = Mathf.Min(minSpan[g], minSpan[h]);
                    float tol = EdgeWeldFraction * narrower;
                    Vector3 d = wPos[h] / wSum[h] - wPos[g] / wSum[g];
                    Vector3 cross = d - topChord[g] * Vector3.Dot(d, topChord[g]);
                    float along = Vector3.Dot(cross, outSum[g]);
                    float lateral = (cross - outSum[g] * along).magnitude;
                    if (along < -narrower || lateral > tol) continue;

                    float dist = d.magnitude;
                    if (dist < bestDist[g]) { bestDist[g] = dist; best[g] = h; }
                    if (dist < bestDist[h]) { bestDist[h] = dist; best[h] = g; }
                }
            }
            for (int g = 0; g < m; g++)
                if (best[g] > g && best[best[g]] == g) Union(g, best[g]);
        }

        // 4. The loading: shared-area stock circulation, redistributed by the lifting line over the
        //    trailing groups found above, and scaled back to stock lift per connected structure.
        static void SolveLoading(int m, Vector3 down, float time)
        {
            int np = panels.Count;
            int ng = 0;
            long topology = np;
            for (int i = 0; i < m; i++)
            {
                if (parent[i] == i) groupIndex[i] = ng++;
                topology = topology * 31 + rootOf[i];
            }
            int nc = 0;
            for (int i = 0; i < m; i++) nc += (edges[i].plusPanel >= 0 ? 1 : 0) + (edges[i].minusPanel >= 0 ? 1 : 0);
            EnsureLiftingLine(np, ng, nc);

            for (int i = 0; i < np; i++)
            {
                Panel p = panels[i];
                llCenter[i] = p.center; llSpan[i] = p.span; llHalf[i] = p.halfSpan;
                // A stack's panels share the one real chord. Zero chord switches the induction off
                // and leaves only the rescale to stock lift.
                llChord[i] = UseLiftingLine ? p.chord * p.share : 0f;
                llG2D[i] = p.g2D; llStock[i] = p.gStock;
            }
            for (int r = 0; r < m; r++)
            {
                if (parent[r] != r) continue;
                llGPos[groupIndex[r]] = wPos[r] / wSum[r];
                llCore[groupIndex[r]] = coreMax[r];
            }
            int k = 0;
            for (int i = 0; i < m; i++)
            {
                int g = groupIndex[rootOf[i]];
                if (edges[i].plusPanel >= 0) { llPanel[k] = edges[i].plusPanel; llGroup[k] = g; llSign[k] = 1f; k++; }
                if (edges[i].minusPanel >= 0) { llPanel[k] = edges[i].minusPanel; llGroup[k] = g; llSign[k] = -1f; k++; }
            }

            if (time >= nextLiftingLine || topology != topologySignature || !liftingLine.Factored)
            {
                nextLiftingLine = time + LiftingLineRefresh;
                topologySignature = topology;
                liftingLine.Factor(np, llCenter, llSpan, llHalf, llChord, ng, llGPos, llCore, nc, llPanel, llGroup, llSign, down);
            }
            liftingLine.Solve(np, llG2D, llStock, llHalf, llSpan, down, llGamma);
            for (int i = 0; i < np; i++)
            {
                Panel p = panels[i];
                p.gamma = llGamma[i];
                panels[i] = p;
            }
        }

        // 5. The wake must carry exactly the lift that holds the aircraft up, which is stock's. The
        //    lifting line already scales each structure's panels to it, but carry-through then
        //    adds lift of its own: the bound vortex spans the fuselage, which stock never gives any
        //    lift (19% of the total on the offline airliner check). So each structure is scaled
        //    once more, by the lift its trailing lines actually carry, rho V (t x sum gamma r).
        static void MatchStockLift(int m, Vector3 down)
        {
            int np = panels.Count;
            if (compOf.Length < np)
            {
                int n = Mathf.Max(np, 2 * compOf.Length);
                compOf = new int[n]; compStock = new Vector3[n]; compCarried = new Vector3[n];
            }
            for (int i = 0; i < np; i++) { compOf[i] = i; compStock[i] = Vector3.zero; compCarried[i] = Vector3.zero; }
            // Structures: panels joined through any shared trailing group.
            for (int r = 0; r < m; r++) groupPanel[r] = -1;
            for (int i = 0; i < m; i++)
            {
                int r = rootOf[i];
                for (int side = 0; side < 2; side++)
                {
                    int p = side == 0 ? edges[i].plusPanel : edges[i].minusPanel;
                    if (p < 0) continue;
                    if (groupPanel[r] < 0) { groupPanel[r] = p; continue; }
                    int a = FindComp(groupPanel[r]), b = FindComp(p);
                    if (a != b) compOf[b] = a;
                }
            }
            for (int i = 0; i < np; i++)
                compStock[FindComp(i)] += Vector3.Cross(down, panels[i].span) * (2f * panels[i].halfSpan * panels[i].gStock);
            for (int r = 0; r < m; r++)
                if (parent[r] == r && groupPanel[r] >= 0)
                    compCarried[FindComp(groupPanel[r])] += Vector3.Cross(down, wPos[r] / wSum[r]) * gSum[r];
            for (int r = 0; r < m; r++)
            {
                if (parent[r] != r || groupPanel[r] < 0) continue;
                int c = FindComp(groupPanel[r]);
                Vector3 carried = compCarried[c];
                if (carried.sqrMagnitude < 1e-6f) continue;
                float s = Vector3.Dot(compStock[c], carried) / carried.sqrMagnitude;
                if (s >= 0.5f && s <= 2f) gSum[r] *= s;   // outside that, leave the structure as solved
            }
        }

        static int FindComp(int i)
        {
            while (compOf[i] != i) { compOf[i] = compOf[compOf[i]]; i = compOf[i]; }
            return i;
        }

        // Positions, strengths and extents per group, read at each group's root edge. The buried
        // test is only needed before carry-through, so later passes skip it.
        static void SummariseGroups(int m, bool withBuried)
        {
            for (int i = 0; i < m; i++)
            {
                rootOf[i] = Find(i);
                gSum[i] = 0f; wSum[i] = 0f; wPos[i] = Vector3.zero; outSum[i] = Vector3.zero;
                aftMax[i] = float.MinValue; coreMax[i] = 0f; minSpan[i] = float.MaxValue;
                topG[i] = -1f; minId[i] = long.MaxValue; buried[i] = withBuried; hasEnd[i] = false;
                firstMember[i] = -1;
            }
            // Member lists per group, so the buried test walks a group's own few edges.
            for (int i = m - 1; i >= 0; i--) { nextMember[i] = firstMember[rootOf[i]]; firstMember[rootOf[i]] = i; }
            for (int i = 0; i < m; i++)
            {
                int r = rootOf[i];
                Edge e = edges[i];
                float g = Mathf.Abs(e.gamma);
                float w = g + 1e-3f;   // so a group of zero-lift edges (and every group before the solve) still has a position
                gSum[r] += e.gamma;
                wSum[r] += w;
                wPos[r] += e.station * w;
                outSum[r] += e.outward * w;
                aftMax[r] = Mathf.Max(aftMax[r], e.aft);
                coreMax[r] = Mathf.Max(coreMax[r], e.core);
                minSpan[r] = Mathf.Min(minSpan[r], e.span);
                if (g > topG[r]) { topG[r] = g; topChord[r] = e.chordDir; }
                if (e.id < minId[r]) minId[r] = e.id;
                // A joint between two panels of one part is neither air nor solid: it does not
                // end anything. Only the part ends decide, and a group needs at least one.
                if (e.inside != null)
                {
                    hasEnd[r] = true;
                    if (buried[r] && !EndsInForeignPart(i, r)) buried[r] = false;
                }
            }
            for (int r = 0; r < m; r++) buried[r] = buried[r] && hasEnd[r];
            for (int r = 0; r < m; r++)
                if (parent[r] == r && outSum[r].sqrMagnitude > 1e-12f) outSum[r].Normalize();
        }

        static bool EndsInForeignPart(int i, int root)
        {
            List<long> inside = edges[i].inside;
            for (int k = 0; k < inside.Count; k++)
            {
                bool member = false;
                for (int j = firstMember[root]; j >= 0 && !member; j = nextMember[j])
                    if (edges[j].body == inside[k]) member = true;
                if (!member) return true;
            }
            return false;
        }

        // 6. First, lines whose cores overlap in the cross-flow plane are one line, exactly as the
        //    wake would merge them a step later (same sign: circulation-weighted centroid; opposite
        //    sign: the net, carried by the stronger). Then fold the weakest line into its nearest
        //    same-sign neighbour until every line clears the threshold and the count is within
        //    MaxLines. Circulation and its cross-flow centroid are conserved; a line with no
        //    same-sign neighbour is dropped.
        static void Fold(Vector3 down, float threshold)
        {
            // One sweep per pass (a merge moves line i a little, so it is re-checked against the
            // rest in the same sweep); passes repeat only while something merged.
            bool merged = true;
            while (merged)
            {
                merged = false;
                for (int i = 0; i < lines.Count; i++)
                    for (int j = i + 1; j < lines.Count; j++)
                    {
                        TrailedLine a = lines[i], b = lines[j];
                        Vector3 d = b.position - a.position;
                        d -= down * Vector3.Dot(d, down);
                        float reach = a.coreRadius + b.coreRadius;
                        if (d.sqrMagnitude >= reach * reach) continue;
                        float ga = Mathf.Abs(a.circulation), gb = Mathf.Abs(b.circulation);
                        TrailedLine keep = ga >= gb ? a : b;
                        if (a.circulation * b.circulation > 0f)
                            keep.position = a.position + d * (gb / (ga + gb));
                        keep.circulation = a.circulation + b.circulation;
                        keep.coreRadius = Mathf.Max(a.coreRadius, b.coreRadius);
                        lines[i] = keep;
                        lines.RemoveAt(j);
                        j--;
                        merged = true;
                    }
            }

            while (lines.Count > 0)
            {
                int weakest = 0;
                for (int k = 1; k < lines.Count; k++)
                    if (Mathf.Abs(lines[k].circulation) < Mathf.Abs(lines[weakest].circulation)) weakest = k;
                float gw = Mathf.Abs(lines[weakest].circulation);
                if (gw >= threshold && lines.Count <= MaxLines) break;

                TrailedLine w = lines[weakest];
                int target = -1;
                float targetDist = float.MaxValue;
                for (int k = 0; k < lines.Count; k++)
                {
                    if (k == weakest || lines[k].circulation * w.circulation <= 0f) continue;
                    Vector3 d = lines[k].position - w.position;
                    float dist = (d - down * Vector3.Dot(d, down)).sqrMagnitude;
                    if (dist < targetDist) { targetDist = dist; target = k; }
                }
                if (target >= 0)
                {
                    TrailedLine t = lines[target];
                    float gt = Mathf.Abs(t.circulation);
                    Vector3 d = w.position - t.position;
                    t.position += (d - down * Vector3.Dot(d, down)) * (gw / (gt + gw));
                    t.circulation += w.circulation;
                    lines[target] = t;
                }
                lines.RemoveAt(weakest);
            }
        }

        // The loading the last Compute settled on at a point of surface `surface` (its index in the
        // list passed to Compute): the bound circulation and chord of the panel spanning that
        // point, and the share of that chord which is this part's own rather than a clipped
        // neighbour's. False if the surface got no panels. Used by the wing vapor, so the vapor
        // sees the same shared-area and lifting-line loading as the wake.
        public static bool PanelAt(int surface, Vector3 world, out float gamma, out float chord, out float share)
        {
            gamma = 0f; chord = 0f; share = 1f;
            if (surface < 0 || surface >= surfPanelCount.Length || surfPanelCount[surface] == 0) return false;
            int first = surfFirstPanel[surface], n = surfPanelCount[surface];
            if (first + n > panels.Count) return false;
            int found = first;
            float bestOut = float.MaxValue;
            for (int j = first; j < first + n; j++)
            {
                Panel p = panels[j];
                float outside = Mathf.Abs(Vector3.Dot(world - p.center, p.span)) - p.halfSpan;
                if (outside < bestOut) { bestOut = outside; found = j; }
            }
            gamma = panels[found].gamma;
            chord = panels[found].chord;
            share = panels[found].share;
            return true;
        }

        // The same, by the panel's index along the span (0 at the low end of the part's span range,
        // as BuildPanels cuts it). Count is 0 if the surface got no panels.
        public static int PanelCount(int surface)
        {
            if (surface < 0 || surface >= surfPanelCount.Length) return 0;
            int n = surfPanelCount[surface];
            return surfFirstPanel[surface] + n <= panels.Count ? n : 0;
        }

        public static void PanelLoading(int surface, int k, out float gamma, out float chord, out float share)
        {
            Panel p = panels[surfFirstPanel[surface] + k];
            gamma = p.gamma; chord = p.chord; share = p.share;
        }

        static int Find(int i)
        {
            while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; }
            return i;
        }

        static void Union(int a, int b)
        {
            if (recordCarry) { carryPairs.Add(a); carryPairs.Add(b); }
            a = Find(a); b = Find(b);
            if (a != b) parent[b] = a;
        }

        static void EnsureScratch(int m)
        {
            if (parent.Length >= m) return;
            int n = Mathf.Max(m, 2 * parent.Length);
            parent = new int[n]; rootOf = new int[n]; best = new int[n]; groupIndex = new int[n]; groupPanel = new int[n];
            firstMember = new int[n]; nextMember = new int[n];
            bestDist = new float[n]; gSum = new float[n]; wSum = new float[n];
            aftMax = new float[n]; coreMax = new float[n]; minSpan = new float[n]; topG = new float[n];
            wPos = new Vector3[n]; outSum = new Vector3[n]; topChord = new Vector3[n];
            minId = new long[n]; buried = new bool[n]; hasEnd = new bool[n];
        }

        static void EnsureLiftingLine(int np, int ng, int nc)
        {
            if (llCenter.Length < np)
            {
                int n = Mathf.Max(np, 2 * llCenter.Length);
                llCenter = new Vector3[n]; llSpan = new Vector3[n];
                llHalf = new float[n]; llChord = new float[n]; llG2D = new float[n]; llStock = new float[n]; llGamma = new float[n];
            }
            if (llGPos.Length < ng) { int n = Mathf.Max(ng, 2 * llGPos.Length); llGPos = new Vector3[n]; llCore = new float[n]; }
            if (llPanel.Length < nc) { int n = Mathf.Max(nc, 2 * llPanel.Length); llPanel = new int[n]; llGroup = new int[n]; llSign = new float[n]; }
        }

        static void DumpSurfaces(List<Surface> surfaces)
        {
            Dump.Add($"lifting line: {liftingLine.Fallbacks} structure(s) fell back to shared area or stock loading");
            for (int si = 0; si < surfaces.Count; si++)
            {
                var sb = new System.Text.StringBuilder();
                sb.Append($"surface {surfaces[si].id} '{surfaces[si].name}' area={surfaces[si].planform.area:F2} m^2 panels:");
                for (int i = 0; i < panels.Count; i++)
                {
                    if (panels[i].surface != si) continue;
                    Panel p = panels[i];
                    sb.Append($" [w={2f * p.halfSpan:F2} c={p.chord:F2} share={p.share:F2} stock={p.gStock:F1} -> {p.gamma:F1}]");
                }
                Dump.Add(sb.ToString());
            }
        }

        // Lines in a frame across and along the total lift, relative to the lift-weighted centre.
        static void DumpLines(List<Surface> surfaces, Vector3 down)
        {
            Vector3 lift = Vector3.zero, centre = Vector3.zero;
            float wt = 0f;
            foreach (var s in surfaces)
            {
                lift += s.lift;
                float m = s.lift.magnitude;
                centre += s.frame.origin * m; wt += m;
            }
            if (wt > 0f) centre /= wt;
            Vector3 up = lift.sqrMagnitude > 1e-6f ? lift.normalized : Vector3.up;
            Vector3 right = Vector3.Cross(up, down).normalized;
            up = Vector3.Cross(down, right).normalized;
            foreach (var l in lines)
            {
                Vector3 rel = l.position - centre;
                Dump.Add($"line {l.id} gamma={l.circulation:F1} m^2/s lateral={Vector3.Dot(rel, right):F2} "
                         + $"vertical={Vector3.Dot(rel, up):F2} m core={l.coreRadius:F2} m");
            }
        }
    }
}
