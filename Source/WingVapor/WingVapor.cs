using System.Collections.Generic;
using UnityEngine;

namespace VortexVapor
{
    // How dense the vapor is at each sample point of one lifting part's planform.
    public class VaporField
    {
        public long id;
        public float[] density;     // 0..1 per planform sample, smoothed in time
        public float[] chordFrac;   // 0 leading edge .. 1 trailing edge, per sample
        public float[] camberShape, peakShape;   // Condensation's chordwise shapes at chordFrac
        public float[] sampleSide;  // +1 or -1 per sample: which side of the plane is sucked there
        public byte[] buried;       // per sample: bit 1 the +normal face, bit 2 the -normal face lies inside a body
        public float[] sectionChord;   // per sample: the chord of the wing section it sits on, m
        public int[] stripOf;       // per sample: which span strip of the part it lies in

        // The wing section through each span strip: every lifting part the flow line through the
        // strip crosses (members stripStart[b] .. stripStart[b + 1] - 1). Every sample of a strip
        // shares them, so the search and the per-step sums are done per strip, not per sample.
        public int[] stripStart = new int[WingVapor.Strips + 1];
        public int[] memberIdx = new int[0];       // the member's index in the surface list (valid until the part set changes)
        public float[] memberFrac = new float[0];  // where the strip sits across that part's span range, 0..1
        public float[] memberSign = new float[0];  // +1 or -1: the member's span axis against this part's
        public bool[] memberLeads = new bool[0];   // the member starts at the section's leading edge (else it is a flap behind it)

        public Vector2 cachedSpan;  // in-plane span axis the strips were measured along
        public float chord;         // m, mean chord: how far the vapor streams
        public float peakWater;     // most condensed water this step, g/kg, for the log
        public float[] stripCamberMax = new float[WingVapor.Strips], stripPeakMax = new float[WingVapor.Strips];   // largest chordwise shapes in each strip
        public float effArea;       // m^2 of this part's own area (clipped duplicates share); constant between part-set changes
        public long areaSignature = -1;
        public bool hasDensity;     // some sample still carries vapor
        public bool measured;       // the strips hold a real measurement (a new part waits its turn)
        public bool buriedMeasured; // `buried` holds a real measurement
        public bool seen;
    }

    // Where the wing vapor forms. No KSP types, so it can be checked offline.
    //
    // No detection and no anchors, the same rule as the wake: every point of every lifting part
    // gets the local loading the shedding model already settled on (stock lift, shared area for
    // clipped stacks, spanwise lifting line), spreads it along the chord the way an airfoil does,
    // and asks Condensation whether the air there condenses. So vapor shows on the middle of the
    // wings, where the loading is highest, first at the leading-edge suction peak and spreading
    // aft as the pull tightens; it thins toward the tips, where the loading falls away; a
    // tailplane pushing down condenses under itself; and in a bank each wing gets its own.
    public static class WingVapor
    {
        // Span strips per part. Samples are on a 12 x 12 grid, and a part is cut into at most four
        // panels, so twelve strips resolve everything the loading can vary over.
        public const int Strips = 12;
        // Seconds for the vapor to follow a change in loading. Stock's per-part lift jitters
        // step to step with every control twitch; real condensation also takes a moment to form.
        public const float SmoothTime = 0.12f;
        // How far the in-plane span axis may swing (as a cosine) before the strips are measured
        // again. 0.85 is 32 degrees: a chord lengthens by 18% over that, which does not show, and
        // a hard turn swings the flow across every deflected control surface by more than the old
        // 6 degrees on nearly every step (the v0.4.7 F-22 spent 10 ms a step in it).
        const float SpanRecheck = 0.85f;
        // Parts whose strips are re-measured per step at most, and parts whose buried faces are
        // measured per step at most (once per part, when the part set changes). A change swings
        // many parts at once, so the work is spread over a few steps instead of one hitch.
        const int MeasuresPerStep = 1, BuriedPerStep = 2;
        // A section lifting against the aircraft's net lift is trimming it: a tailplane pushing
        // down, a canard set against the wing. KSP puts far more load on these than any real
        // aircraft does (a flown A300's tail at rotation pushed down at a lift coefficient of
        // about 1.5, past where a real tail stalls, and condensed while the wing stayed clear),
        // because stock has no downwash and craft are often trimmed with the centre of mass well
        // forward. So by default they make no vapor. A DELIBERATE STYLE RULE, not physics: real
        // fighter tailplanes can fog in a hard pull. Surfaces lifting sideways (a fin, cos of the
        // angle to the net lift above -AgainstCos) are not affected.
        public static bool CondenseAgainstLift = false;
        const float AgainstCos = 0.3f;

        static readonly Dictionary<long, VaporField> fields = new Dictionary<long, VaporField>();
        static readonly List<long> stale = new List<long>();
        static long partSignature = -1;
        static readonly CondensationTable table = new CondensationTable();
        static int[] gammaStart = new int[0], gammaCount = new int[0];
        static float[] gammaOf = new float[0], chordOf = new float[0];
        static readonly float[] stripGamma = new float[Strips], stripFlap = new float[Strips], stripChordSum = new float[Strips];
        static readonly bool[] stripQuiet = new bool[Strips], stripDead = new bool[Strips];
        static readonly float[] stripCamberTerm = new float[Strips], stripAlphaTerm = new float[Strips], stripSideOf = new float[Strips];
        static readonly int[] stripBit = new int[Strips];

        public static Dictionary<long, VaporField> Fields { get { return fields; } }

        // Area-weighted fraction of the lifting surfaces carrying vapor, and the most water
        // condensed anywhere (g/kg), from the last Compute. For the log.
        public static float Coverage { get; private set; }
        public static float PeakWater { get; private set; }

        public static void Clear() { fields.Clear(); Coverage = PeakWater = 0f; }

        // Call right after TrailedVorticity.Compute on the same surfaces.
        // `bodies` answers which parts contain a point, for the faces buried inside a fuselage or
        // nacelle; null skips that test.
        public static void Compute(List<Surface> surfaces, Vector3 down, float rho, float speed,
            float kelvin, float pascal, float humidity, float mach, float dt, IBodyQuery bodies = null)
        {
            foreach (var f in fields.Values) f.seen = false;
            // The chord is measured across every part, so a change of the part set re-measures all.
            long signature = surfaces.Count;
            foreach (var sf in surfaces) signature = signature * 31 + sf.id;
            if (signature != partSignature)
            {
                partSignature = signature;
                // The members are stored as indices into this list, so a changed part set makes
                // every measurement stale, and each part waits its turn to be measured again.
                foreach (var f in fields.Values) { f.cachedSpan = Vector2.zero; f.measured = false; f.buriedMeasured = false; }
            }
            float follow = 1f - Mathf.Exp(-dt / SmoothTime);
            table.Build(kelvin, pascal, humidity);
            float q = 0.5f * rho * speed * speed;
            float pg = Condensation.PrandtlGlauert(mach);
            float covered = 0f, total = 0f, peak = 0f;
            Vector3 netLift = Vector3.zero;
            foreach (var sf in surfaces) netLift += sf.lift;
            Vector3 liftDir = netLift.sqrMagnitude > 1e-6f ? netLift.normalized : Vector3.zero;
            int measures = 0, buriedDone = 0, nSurf = surfaces.Count;

            if (signature != liftingSignature)
            {
                liftingSignature = signature;
                liftingIds.Clear();
                foreach (var sf in surfaces) liftingIds.Add(sf.id);
            }

            // Every panel's bound circulation this step, by part, for the sections to sum.
            if (gammaStart.Length < surfaces.Count) { gammaStart = new int[2 * surfaces.Count]; gammaCount = new int[2 * surfaces.Count]; }
            int totalPanels = 0;
            for (int si = 0; si < surfaces.Count; si++)
            {
                gammaStart[si] = totalPanels;
                gammaCount[si] = TrailedVorticity.PanelCount(si);
                totalPanels += gammaCount[si];
            }
            if (gammaOf.Length < totalPanels) { gammaOf = new float[2 * totalPanels]; chordOf = new float[2 * totalPanels]; }
            for (int si = 0; si < surfaces.Count; si++)
            {
                // The share of this part's loading the vapor sees (Surface.vaporLift): 1 on
                // everything but a roll control surface, where it is how much of its load along
                // the net lift its symmetry group carries together. Zero in a pure roll. A part
                // lifting mostly sideways (a twin fin's rudder) keeps its whole load: its component
                // along the net lift is small and would make the ratio noise.
                float own = Vector3.Dot(surfaces[si].lift, liftDir);
                float vaporShare = Mathf.Abs(own) > AgainstCos * surfaces[si].lift.magnitude + 1e-3f
                    ? Mathf.Clamp01(Vector3.Dot(surfaces[si].vaporLift, liftDir) / own) : 1f;
                for (int k = 0; k < gammaCount[si]; k++)
                {
                    float g, c, share;
                    TrailedVorticity.PanelLoading(si, k, out g, out c, out share);
                    gammaOf[gammaStart[si] + k] = g * vaporShare;
                    chordOf[gammaStart[si] + k] = c * Mathf.Clamp01(share);
                }
            }

            for (int si = 0; si < surfaces.Count; si++)
            {
                Surface sf = surfaces[si];
                Planform pf = sf.planform;
                if (pf == null || pf.hull.Length < 3) continue;
                PlaneFrame fr = sf.frame;
                int n = pf.samples.Length;

                VaporField f;
                if (!fields.TryGetValue(sf.id, out f) || f.density.Length != n)
                {
                    f = new VaporField
                    {
                        id = sf.id, density = new float[n], chordFrac = new float[n],
                        camberShape = new float[n], peakShape = new float[n], sampleSide = new float[n],
                        sectionChord = new float[n], buried = new byte[n], stripOf = new int[n]
                    };
                    fields[sf.id] = f;
                }
                f.seen = true;
                f.peakWater = 0f;

                Vector3 s = Vector3.Cross(fr.n, down);
                bool flowing = s.sqrMagnitude > 0.01f && speed > 1f;
                if (flowing)
                {
                    Vector2 s2 = fr.InPlane(s.normalized).normalized;
                    if (Vector2.Dot(s2, f.cachedSpan) < SpanRecheck && measures < MeasuresPerStep)
                    {
                        MeasureChord(surfaces, si, s2, down, f);
                        measures++;
                    }
                }
                if (!f.buriedMeasured && buriedDone < BuriedPerStep)
                {
                    MeasureBuried(sf, bodies, f);
                    buriedDone++;
                }

                // The mean pressure difference across the wing section at each strip, Pa: rho V
                // times the section's bound circulation (summed over every part in the chordwise
                // row, signed against this part's span axis) over the section's chord. Stacked
                // copies each carry their share of the circulation, so they add up to one wing.
                // Summing is what keeps a control surface in proportion: an aileron deflected hard
                // in a roll carries a large lift for its own small chord, but as part of the
                // section it only shifts the whole section's loading by its share. By Kutta-
                // Joukowski the lift points along +normal where the section's circulation is
                // positive, so that side is the suction side.
                if (f.areaSignature != signature)
                {
                    f.areaSignature = signature;
                    float sum = 0f;
                    for (int j = 0; j < n; j++) sum += pf.area / n * pf.sampleShare[j];
                    f.effArea = sum;
                }

                bool live = flowing && f.measured && f.buriedMeasured;
                bool allQuiet = true;
                // Whether this part lifts against the aircraft's net lift is a property of the
                // part, not of the sample.
                float partAgainst = Vector3.Dot(fr.n, liftDir);
                if (live)
                {
                    for (int b = 0; b < Strips; b++)
                    {
                        float gamma = 0f, flapGamma = 0f, chord = 0f, leadStall = 0f;
                        for (int k = f.stripStart[b]; k < f.stripStart[b + 1]; k++)
                        {
                            int mi = f.memberIdx[k];
                            if (mi >= nSurf || gammaCount[mi] == 0) continue;
                            int idx = Mathf.Min((int)(f.memberFrac[k] * gammaCount[mi]), gammaCount[mi] - 1);
                            float g = f.memberSign[k] * gammaOf[gammaStart[mi] + idx];
                            gamma += g;
                            if (!f.memberLeads[k]) flapGamma += g;
                            else leadStall = Mathf.Max(leadStall, surfaces[mi].stall);
                            chord += chordOf[gammaStart[mi] + idx];
                        }
                        // The section's loading over the members' own panel chords (each counted
                        // by its share), not the length of the flow line through the point: a
                        // panel's circulation belongs to its whole width, and near a pointed or
                        // slanted edge the local line shrinks to nothing while the circulation does
                        // not (v0.4.4: fog at 0.7 g from a few points at the corners of the A300's
                        // parts). What the parts behind the leading edge carry (flaps, ailerons,
                        // elevators) is a camber change, spread along the chord, not an angle of
                        // attack that piles into the leading-edge peak (Condensation.Suction).
                        float side = gamma >= 0f ? 1f : -1f;
                        float perGamma = rho * speed / Mathf.Max(chord, 0.1f);
                        float loading = perGamma * gamma * side, flapLoading = perGamma * flapGamma * side;
                        float ct, at;
                        Condensation.StripTerms(loading - flapLoading, flapLoading, q, out ct, out at);
                        // A stalled section has no leading-edge suction peak: the flow has
                        // separated from the nose, and what lift remains is spread over a flat,
                        // shallow pressure field. So stall removes the angle-of-attack term, the
                        // peak, and leaves the camber term. FAR reports stall per part (the
                        // leading member of the section decides it); under stock it is always 0,
                        // and Condensation.MaxPeakCp stands in for the stall stock never models.
                        at *= 1f - leadStall;
                        stripCamberTerm[b] = ct; stripAlphaTerm[b] = at;
                        stripSideOf[b] = side;
                        stripBit[b] = side > 0f ? 1 : 2;   // the face the vapor would be on: a face inside a body has no flow over it
                        stripDead[b] = !CondenseAgainstLift && partAgainst * side < -AgainstCos;
                        // Can any sample of the strip condense at all? The most its shapes allow,
                        // through the same physics: if that is below the visible onset, the strip
                        // is quiet and its samples only need to fade out. Almost every strip of
                        // every part is quiet almost all the time.
                        float bound = stripDead[b] ? 0f
                            : Condensation.SuctionFromTerms(Mathf.Max(ct, 0f), at, f.stripCamberMax[b], Condensation.PeakAdjust(f.stripPeakMax[b], pg), q, pg);
                        stripQuiet[b] = table.Density(bound) <= 0f;
                        if (!stripQuiet[b]) allQuiet = false;
                    }
                }

                float areaEach = pf.area / n;
                if (allQuiet && !f.hasDensity)
                {
                    total += f.effArea;
                    continue;
                }
                bool any = false;
                float maxSuction = 0f;
                for (int j = 0; j < n; j++)
                {
                    int b = f.stripOf[j];
                    float d = f.density[j];
                    if (live && !stripQuiet[b])
                    {
                        f.sampleSide[j] = stripSideOf[b];
                        float target = 0f;
                        if (!stripDead[b] && (f.buried[j] & stripBit[b]) == 0)
                        {
                            float suction = Condensation.SuctionFromTerms(stripCamberTerm[b], stripAlphaTerm[b], f.camberShape[j],
                                                                 Condensation.PeakAdjust(f.peakShape[j], pg), q, pg);
                            if (suction > maxSuction) maxSuction = suction;
                            target = table.Density(suction);
                        }
                        d += (target - d) * follow;
                        f.density[j] = d;
                    }
                    else if (d != 0f)
                    {
                        d -= d * follow;
                        if (d < 1e-4f) d = 0f;
                        f.density[j] = d;
                    }
                    if (d > 0f)
                    {
                        any = true;
                        covered += d * areaEach * pf.sampleShare[j];
                    }
                    total += areaEach * pf.sampleShare[j];
                }
                f.hasDensity = any;
                if (maxSuction > 0f) f.peakWater = table.Water(maxSuction);
                peak = Mathf.Max(peak, f.peakWater);
            }

            stale.Clear();
            foreach (var kv in fields) if (!kv.Value.seen) stale.Add(kv.Key);
            foreach (long id in stale) fields.Remove(id);
            Coverage = total > 0f ? covered / total : 0f;
            PeakWater = peak;
        }

        // The wing section through each span strip. A wing in KSP is often several parts in a row
        // along the chord (slat, clipped panels, flap, aileron), and measured on its own each would
        // put a leading-edge suction peak in the middle of the real wing and carry its own lift as
        // if it were a whole airfoil. So the section through a strip is the run of lifting
        // surfaces the flow line through it crosses, in the same plane (PlanformOverlap's slab)
        // and touching end to end, the same way for every part: its chord, and its member parts.
        // Each sample then takes its place along that chord (0 at the leading edge, whichever end
        // faces upstream) from where it lies along the flow line.
        const float ChordGap = 0.3f;   // m: parts closer than this along the flow are one airfoil
        struct Crossing { public float t0, t1, frac, sign; public int idx; }
        // A member starting within this fraction of the chord from the section's leading edge
        // leads it (the wing itself, a slat, a clipped copy); one further back is a flap.
        const float LeadingFraction = 0.25f;
        static readonly List<bool> mLeads = new List<bool>();
        static readonly List<Crossing> crossings = new List<Crossing>();
        static readonly List<int> near = new List<int>();
        static readonly List<int> mIdx = new List<int>();
        static readonly List<float> mFrac = new List<float>(), mSign = new List<float>();
        static readonly float[] stripSum = new float[Strips], stripLe = new float[Strips], stripLen = new float[Strips];
        static readonly int[] stripCount = new int[Strips];
        static readonly bool[] stripFallback = new bool[Strips];
        static readonly Vector2[] stripPoint = new Vector2[Strips];

        public static int MeasureCount;
        static void MeasureChord(List<Surface> surfaces, int si, Vector2 s2, Vector3 down, VaporField f)
        {
            Surface me = surfaces[si];
            Planform pf = me.planform;
            f.cachedSpan = s2; MeasureCount++;
            f.measured = true;
            float lo, hi;
            pf.SpanRange(s2, out lo, out hi);
            // Mean chord, but never more than the part's own length along the flow: a part nearly
            // edge-on to the span (a strake along the fuselage) has a tiny span and area/span
            // would be many metres.
            Vector2 c2 = new Vector2(-s2.y, s2.x);
            float clo, chi;
            pf.SpanRange(c2, out clo, out chi);
            f.chord = Mathf.Min(pf.area / Mathf.Max(hi - lo, 0.01f), chi - clo);
            float width = Mathf.Max(hi - lo, 1e-3f);
            Vector3 sMe = Vector3.Cross(me.frame.n, down).normalized;
            Vector2 dMe = me.frame.InPlane(down);
            bool meAlong = dMe.sqrMagnitude >= 0.25f;
            if (meAlong) dMe.Normalize();

            // Only parts whose plane and outline can reach this one's are worth testing: one
            // bounding-sphere test each, up front.
            near.Clear();
            for (int m = 0; m < surfaces.Count; m++)
            {
                Planform po = surfaces[m].planform;
                if (po == null || po.hull.Length < 3) continue;
                float slab = Mathf.Max(PlanformOverlap.MinSlab, 0.5f * (pf.thickness + po.thickness));
                float reach = pf.radius + po.radius + slab + 0.1f;
                if ((surfaces[m].frame.origin - me.frame.origin).sqrMagnitude <= reach * reach) near.Add(m);
            }

            // Which strip each sample is in, and where the strip's flow line runs: through the
            // mean span station of its samples, at mid-chord.
            for (int b = 0; b < Strips; b++) { stripSum[b] = 0f; stripCount[b] = 0; }
            for (int j = 0; j < pf.samples.Length; j++)
            {
                float sigma = Vector2.Dot(pf.samples[j], s2);
                int b = Mathf.Clamp((int)((sigma - lo) / width * Strips), 0, Strips - 1);
                f.stripOf[j] = b;
                stripSum[b] += sigma;
                stripCount[b]++;
            }

            mIdx.Clear(); mFrac.Clear(); mSign.Clear(); mLeads.Clear();
            for (int b = 0; b < Strips; b++)
            {
                f.stripStart[b] = mIdx.Count;
                stripFallback[b] = true;
                stripLen[b] = f.chord;
                if (stripCount[b] == 0) continue;
                float sigma = stripSum[b] / stripCount[b];
                float c0, c1;
                float cMid = pf.ChordAt(s2, sigma, out c0, out c1) ? 0.5f * (c0 + c1) : 0.5f * (clo + chi);
                Vector2 qb = s2 * sigma + c2 * cMid;
                stripPoint[b] = qb;
                Vector3 x = me.frame.ToWorld(qb);

                crossings.Clear();
                for (int nk = 0; nk < near.Count; nk++)
                {
                    int m = near[nk];
                    Surface o = surfaces[m];
                    Planform po = o.planform;
                    float slab = Mathf.Max(PlanformOverlap.MinSlab, 0.5f * (pf.thickness + po.thickness));
                    float h;
                    Vector2 q = o.frame.ToPlane(x, out h);
                    if (Mathf.Abs(h) > slab) continue;
                    Vector2 d = o.frame.InPlane(down);
                    if (d.sqrMagnitude < 0.25f) continue;   // this surface is not along the flow here
                    d.Normalize();
                    if (Mathf.Abs(q.x * d.y - q.y * d.x) > po.radius) continue;   // the flow line misses it
                    float t0, t1;
                    if (!Clip(po.hull, q, d, out t0, out t1)) continue;
                    // Where the strip sits across this part's span, as its panels are cut.
                    Vector3 sO = Vector3.Cross(o.frame.n, down);
                    if (sO.sqrMagnitude < 0.01f) continue;
                    sO.Normalize();
                    Vector2 so2 = o.frame.InPlane(sO).normalized;
                    float olo, ohi;
                    po.SpanRange(so2, out olo, out ohi);
                    crossings.Add(new Crossing
                    {
                        t0 = t0, t1 = t1, idx = m,
                        frac = Mathf.Clamp01((Vector2.Dot(q, so2) - olo) / Mathf.Max(ohi - olo, 1e-3f)),
                        sign = Vector3.Dot(sO, sMe) >= 0f ? 1f : -1f
                    });
                }
                // Merge the crossings that touch into runs, and take the one holding the strip's
                // own point (t = 0).
                crossings.Sort((a, c) => a.t0.CompareTo(c.t0));
                float le = 0f, te = 0f;
                int first = -1, last = -1;
                for (int k = 0; k < crossings.Count && first < 0; k++)
                {
                    int start = k;
                    float end = crossings[k].t1;
                    while (k + 1 < crossings.Count && crossings[k + 1].t0 <= end + ChordGap) { k++; end = Mathf.Max(end, crossings[k].t1); }
                    if (crossings[start].t0 <= 1e-3f && end >= -1e-3f) { le = crossings[start].t0; te = end; first = start; last = k; }
                }
                if (first >= 0 && te - le > 1e-3f && meAlong)
                {
                    stripFallback[b] = false;
                    stripLe[b] = le;
                    stripLen[b] = te - le;
                    for (int k = first; k <= last; k++)
                    {
                        mIdx.Add(crossings[k].idx); mFrac.Add(crossings[k].frac); mSign.Add(crossings[k].sign);
                        mLeads.Add(crossings[k].t0 - le <= LeadingFraction * (te - le));
                    }
                }
                else
                {
                    // Not found along the flow (edge-on to it): the part on its own.
                    mIdx.Add(si);
                    mFrac.Add(Mathf.Clamp01((sigma - lo) / width));
                    mSign.Add(1f);
                    mLeads.Add(true);
                }
            }
            f.stripStart[Strips] = mIdx.Count;
            f.memberIdx = mIdx.ToArray();
            f.memberFrac = mFrac.ToArray();
            f.memberSign = mSign.ToArray();
            f.memberLeads = mLeads.ToArray();

            for (int b = 0; b < Strips; b++) { f.stripCamberMax[b] = 0f; f.stripPeakMax[b] = 0f; }
            for (int j = 0; j < pf.samples.Length; j++)
            {
                int b = f.stripOf[j];
                f.sectionChord[j] = stripLen[b];
                if (stripFallback[b]) f.chordFrac[j] = 0.5f;
                else
                {
                    float delta = Vector2.Dot(pf.samples[j] - stripPoint[b], dMe);   // metres downstream of the strip's point
                    f.chordFrac[j] = Mathf.Clamp01((delta - stripLe[b]) / stripLen[b]);
                }
                f.camberShape[j] = Condensation.CamberShape(f.chordFrac[j]);
                f.peakShape[j] = Condensation.PeakShape(f.chordFrac[j]);
                f.stripCamberMax[b] = Mathf.Max(f.stripCamberMax[b], f.camberShape[j]);
                f.stripPeakMax[b] = Mathf.Max(f.stripPeakMax[b], f.peakShape[j]);
            }
        }

        // For the log: the parts condensing most, strongest first, as "name water g/kg, loading
        // Pa, side, with/against" where `side` is the face the vapor is on (the part's +normal
        // or -normal) and with/against says whether the part lifts with the aircraft's net lift
        // or against it (a tail in trim pushes down).
        static readonly List<int> order = new List<int>();
        public static string Report(List<Surface> surfaces, int top)
        {
            Vector3 net = Vector3.zero;
            foreach (var sf in surfaces) net += sf.lift;
            order.Clear();
            for (int i = 0; i < surfaces.Count; i++)
            {
                VaporField f;
                if (fields.TryGetValue(surfaces[i].id, out f) && f.peakWater >= Condensation.OnsetWater) order.Add(i);
            }
            if (order.Count == 0) return null;
            order.Sort((a, b) => fields[surfaces[b].id].peakWater.CompareTo(fields[surfaces[a].id].peakWater));
            var sb = new System.Text.StringBuilder();
            for (int k = 0; k < order.Count && k < top; k++)
            {
                Surface sf = surfaces[order[k]];
                VaporField f = fields[sf.id];
                int j = 0;
                for (int i = 1; i < f.density.Length; i++) if (f.density[i] > f.density[j]) j = i;
                if (k > 0) sb.Append("; ");
                sb.Append($"'{sf.name}' {f.peakWater:F1} g/kg, stock lift/area {sf.lift.magnitude / Mathf.Max(sf.planform.area, 0.01f):F0} Pa, ");
                sb.Append($"vapor on {(f.sampleSide[j] > 0f ? "+" : "-")}normal side at x={f.chordFrac[j]:F2} of {f.sectionChord[j]:F1} m, ");
                sb.Append(Vector3.Dot(sf.lift, net) >= 0f ? "lifting with the aircraft" : "lifting AGAINST the aircraft");
            }
            if (order.Count > top) sb.Append($"; +{order.Count - top} more");
            return sb.ToString();
        }

        // Which faces of each sample lie inside another, non-lifting part. KSP builders clip wing
        // roots, control surfaces and strakes into fuselages and engine nacelles, and stock still
        // gives those parts lift for their whole area; but a face inside a solid body has no flow
        // over it, so it cannot condense (v0.4.4: stray puffs at 1-1.5 g at an A300's wing roots
        // and nacelles). Lifting parts do not count as bodies here: overlapping wing panels are
        // the same wing (PlanformOverlap).
        static readonly List<long> inside = new List<long>();
        static readonly HashSet<long> liftingIds = new HashSet<long>();
        static long liftingSignature = -1;
        const float FaceProbe = 0.1f;   // m past the part's surface

        static void MeasureBuried(Surface sf, IBodyQuery bodies, VaporField f)
        {
            f.buriedMeasured = true;
            if (bodies == null) { System.Array.Clear(f.buried, 0, f.buried.Length); return; }
            Planform pf = sf.planform;
            Vector3 up = sf.frame.n * (0.5f * pf.thickness + FaceProbe);
            for (int j = 0; j < pf.samples.Length; j++)
            {
                Vector3 x = sf.frame.ToWorld(pf.samples[j]);
                byte b = 0;
                if (InBody(bodies, x + up, sf.id)) b |= 1;
                if (InBody(bodies, x - up, sf.id)) b |= 2;
                f.buried[j] = b;
            }
        }

        static bool InBody(IBodyQuery bodies, Vector3 p, long self)
        {
            bodies.BodiesAt(p, self, inside);
            for (int k = 0; k < inside.Count; k++) if (!liftingIds.Contains(inside[k])) return true;
            return false;
        }

        // Where the line q + t d crosses a convex counter-clockwise outline, as [t0, t1].
        static bool Clip(Vector2[] hull, Vector2 q, Vector2 d, out float t0, out float t1)
        {
            t0 = float.MinValue; t1 = float.MaxValue;
            for (int i = 0; i < hull.Length; i++)
            {
                Vector2 a = hull[i], e = hull[(i + 1) % hull.Length] - a;
                float f0 = e.x * (q.y - a.y) - e.y * (q.x - a.x);   // inside where >= 0
                float fd = e.x * d.y - e.y * d.x;
                if (Mathf.Abs(fd) < 1e-9f) { if (f0 < 0f) return false; continue; }
                float t = -f0 / fd;
                if (fd > 0f) t0 = Mathf.Max(t0, t); else t1 = Mathf.Min(t1, t);
            }
            return t0 <= t1 && t0 > float.MinValue && t1 < float.MaxValue;
        }
    }
}
