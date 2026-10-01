using System.Collections.Generic;
using UnityEngine;

namespace VortexVapor
{
    // Draws the wing vapor WingVapor computes: short-lived soft puffs over the condensing points of
    // every lifting part, all in one particle system riding the vessel's root part (local
    // simulation space), so there is no floating-origin correction. Puffs drift aft, drawing the
    // sheet streaming off the trailing edge. Stock shaders only; the puff texture is generated
    // here.
    public class VaporRenderer
    {
        // Puffs per second per square metre of fully condensing wing.
        public const float PuffsPerSquareMetre = 450f;
        // The craft's budget: past it, puffs are fewer and larger, covering the same area, so
        // particle count and overdraw stay bounded.
        public const float MaxPuffsPerSecond = 20000f;
        public const int MaxPuffs = 6000, MaxEmitPerFrame = 500;
        public const float MaxSizeBoost = 2f;
        public const float MinLife = 0.12f, MaxLife = 0.24f;
        // How far past the section's trailing edge a puff drifts over its life, in section chords.
        // Vapor evaporates as the pressure recovers behind the wing.
        public const float PastTrailingEdge = 0.3f;
        // Peak opacity of one puff in fully dense vapor. Many overlap, so this stays low.
        public const float PuffAlpha = 0.28f;
        // A puff's size over its life, as a multiple of its start size.
        public const float SizeStart = 0.7f, SizeEnd = 1.5f;

        public class Source
        {
            public Part part;
            public PartBox box;
            public VaporField field;
            public float[] weights = new float[0];
            public float sum, carry;
        }

        readonly Dictionary<long, Source> sources = new Dictionary<long, Source>();
        readonly List<Source> active = new List<Source>();
        readonly VaporVolume volume = new VaporVolume();
        GameObject go;
        ParticleSystem ps;
        Part root;
        static Material material;

        public int LiveParticles { get { return ps != null ? ps.particleCount : 0; } }

        // Emits this frame's puffs. `down` is the unit direction the air moves past the aircraft;
        // `light` 0..1 is how sunlit the air around the vessel is.
        public void Emit(Vessel vessel, Vector3 down, float light, float dt)
        {
            if (vessel == null || dt <= 0f || vessel.rootPart == null) return;
            // Nothing condensing (the usual case, and always above the vapor's altitude): no work,
            // and no particle system on the vessel.
            if (WingVapor.Fields.Count == 0) { if (go != null) Clear(); return; }
            // Player intensity (ModSettings): scales puff opacity, not count. Off at zero.
            float userScale = ModSettings.VaporScale;
            if (userScale <= 0.01f) { if (go != null) Clear(); return; }
            EnsureMaterial();
            EnsureSystem(vessel.rootPart);
            // WingtipVortex's night floor: 0.2 of daylight brightness in full shadow.
            Color tint = Color.Lerp(new Color(0.19f, 0.2f, 0.23f), new Color(0.97f, 0.97f, 1f), light);

            // What each part asks for this frame, and the craft's total.
            active.Clear();
            float wanted = 0f;
            foreach (var kv in WingVapor.Fields)
            {
                VaporField f = kv.Value;
                Source src;
                if (!sources.TryGetValue(kv.Key, out src))
                {
                    Part p = FindPart(vessel, kv.Key);
                    if (p == null) continue;
                    src = new Source { part = p };
                    sources[kv.Key] = src;
                }
                if (src.part == null || src.part.vessel != vessel) { sources.Remove(kv.Key); continue; }
                src.box = AeroState.GetBox(src.part);
                if (src.box == null || src.box.planform == null) continue;
                Planform pf = src.box.planform;
                if (pf.samples.Length != f.density.Length) continue;
                if (src.weights.Length != pf.samples.Length) src.weights = new float[pf.samples.Length];
                float sum = 0f;
                for (int j = 0; j < pf.samples.Length; j++)
                {
                    // Square root: thin vapor still gets (faint) puffs, so the sheet spreads over
                    // the wing instead of crowding the densest samples.
                    sum += Mathf.Sqrt(f.density[j]) * pf.sampleShare[j];
                    src.weights[j] = sum;
                }
                if (sum <= 0f) { src.carry = 0f; continue; }
                src.sum = sum;
                src.field = f;
                wanted += sum * (pf.area / pf.samples.Length) * PuffsPerSquareMetre;
                active.Add(src);
            }
            // With Waterfall installed the vapor is drawn as volumetric cloud, and no puffs.
            if (ModSettings.VaporVolumetric && VaporVolume.Available)
            {
                volume.Update(active, vessel, down, tint, userScale, ModSettings.VaporThickness, dt);
                return;
            }
            volume.Hide();
            if (active.Count == 0) return;

            float scale = wanted > MaxPuffsPerSecond ? MaxPuffsPerSecond / wanted : 1f;
            float sizeBoost = Mathf.Min(Mathf.Sqrt(1f / scale), MaxSizeBoost);
            Transform local = go.transform;
            int budget = MaxEmitPerFrame;

            foreach (Source src in active)
            {
                Planform pf = src.box.planform;
                VaporField f = src.field;
                float areaEach = pf.area / pf.samples.Length;
                src.carry += src.sum * areaEach * PuffsPerSquareMetre * scale * dt;
                int count = Mathf.Min((int)src.carry, budget);
                src.carry -= (int)src.carry;
                if (count <= 0) continue;
                budget -= count;

                PlaneFrame fr = src.box.Frame();
                float cell = Mathf.Sqrt(areaEach);
                for (int k = 0; k < count; k++)
                {
                    // One pick per equal slice of the part's vapor, not independent draws, so the
                    // sheet covers the wing evenly.
                    int j = Pick(src.weights, (k + Random.value) / count * src.sum);
                    Vector2 q = pf.samples[j] + new Vector2(Random.Range(-0.5f, 0.5f), Random.Range(-0.5f, 0.5f)) * cell;
                    float side = f.sampleSide[j];
                    float life = Random.Range(MinLife, MaxLife);
                    float drift = f.sectionChord[j] * (1f - f.chordFrac[j] + PastTrailingEdge);
                    // Thin vapor is faint and fine-grained: opacity and size both grow with
                    // density, so condensation reads as a haze along the leading edge and thickens
                    // smoothly.
                    float d = f.density[j];
                    Color c = tint;
                    c.a = Mathf.Clamp01(PuffAlpha * userScale * Mathf.Lerp(0.08f, 1f, d));
                    float grain = Mathf.Lerp(0.45f, 1f, d);
                    float size = Mathf.Clamp(cell * Random.Range(1.6f, 2.6f) * sizeBoost * grain, 0.15f, 3f * sizeBoost);
                    // Vapor forms on the suction (upper) surface only. A puff starts resting on the
                    // skin (centre one start-radius up) and rises at the rate its radius grows
                    // (SizeStart..SizeEnd), so its lower edge stays on the surface for its whole
                    // life.
                    float rise = 0.5f * size * (SizeEnd - SizeStart) / life;
                    Vector3 world = fr.ToWorld(q) + fr.n * (side * (0.5f * pf.thickness + 0.5f * size * SizeStart));
                    Vector3 vel = down * (drift / life) + fr.n * (side * rise);
                    var ep = new ParticleSystem.EmitParams
                    {
                        position = local.InverseTransformPoint(world),
                        velocity = local.InverseTransformDirection(vel),
                        startLifetime = life,
                        startSize = size,
                        rotation = Random.Range(0f, 360f),
                        startColor = c
                    };
                    ps.Emit(ep, 1);
                }
                if (budget <= 0) break;
            }
        }

        static Part FindPart(Vessel vessel, long flightID)
        {
            foreach (Part p in vessel.Parts) if (p.flightID == flightID) return p;
            return null;
        }

        static int Pick(float[] cumulative, float x)
        {
            int lo = 0, hi = cumulative.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (cumulative[mid] < x) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        void EnsureSystem(Part rootPart)
        {
            if (go != null && root == rootPart) return;
            Clear();
            root = rootPart;

            go = new GameObject("WingVapor");
            go.transform.SetParent(rootPart.transform, false);
            // Undo the root's scale, so puff sizes and speeds are in metres.
            Vector3 ls = rootPart.transform.lossyScale;
            go.transform.localScale = new Vector3(1f / Mathf.Max(ls.x, 1e-3f), 1f / Mathf.Max(ls.y, 1e-3f), 1f / Mathf.Max(ls.z, 1e-3f));
            go.layer = rootPart.gameObject.layer;

            ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = ps.main;
            main.playOnAwake = false;
            main.loop = true;
            main.duration = 1f;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.maxParticles = MaxPuffs;
            main.startSpeed = 0f;
            main.gravityModifier = 0f;
            var emission = ps.emission;
            emission.enabled = false;
            var shape = ps.shape;
            shape.enabled = false;

            var col = ps.colorOverLifetime;
            col.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.15f), new GradientAlphaKey(0.7f, 0.5f), new GradientAlphaKey(0f, 1f) });
            col.color = grad;
            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, SizeStart, 1f, SizeEnd));

            var r = go.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.sharedMaterial = material;
            r.maxParticleSize = 1f;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;

            ps.Play();
        }

        static void EnsureMaterial()
        {
            if (material != null) return;
            Shader sh = Shader.Find("KSP/Particles/Alpha Blended");
            if (sh == null) sh = Shader.Find("Legacy Shaders/Particles/Alpha Blended");
            if (sh == null) sh = Shader.Find("Sprites/Default");
            material = new Material(sh) { mainTexture = PuffTexture() };
            Debug.Log("[VORTEX] wing vapor: vapor shader: " + sh.name);
        }

        // A soft, slightly lumpy round puff, made here rather than shipped.
        static Texture2D PuffTexture()
        {
            const int n = 64;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
                    float r = Mathf.Sqrt(u * u + v * v);
                    float lump = 0.7f + 0.3f * Mathf.PerlinNoise(x * 0.12f + 3.1f, y * 0.12f + 7.7f);
                    float a = Mathf.Clamp01(1f - r);
                    a = a * a * (3f - 2f * a) * lump;
                    px[y * n + x] = new Color(1f, 1f, 1f, a);
                }
            tex.SetPixels(px);
            tex.Apply(true);
            return tex;
        }

        public void Clear()
        {
            if (go != null) Object.Destroy(go);
            go = null;
            ps = null;
            root = null;
            volume.Clear();
            sources.Clear();
        }
    }
}
