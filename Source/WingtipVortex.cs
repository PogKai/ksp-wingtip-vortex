using UnityEngine;
using KSP;
using System.Linq;
using System.Collections.Generic;

[KSPAddon(KSPAddon.Startup.Flight, false)]
public class WingtipVortexLoader : MonoBehaviour
{
    private Dictionary<Part, ParticleSystem> effects = new Dictionary<Part, ParticleSystem>();
    private Dictionary<Part, Vector3> wingtipLocal = new Dictionary<Part, Vector3>();
    private Dictionary<Part, float> emitAccumulator = new Dictionary<Part, float>();

    private Vessel cachedVessel = null;

    void Update()
    {
        if (FlightGlobals.ActiveVessel == null) return;

        Vessel vessel = FlightGlobals.ActiveVessel;

        if (cachedVessel != vessel)
        {
            Cleanup();
            cachedVessel = vessel;
        }

        var wings = vessel.parts
            .Where(p => p.Modules.Contains("ModuleLiftingSurface"))
            .ToList();

        if (wings.Count == 0) return;

        Vector3 com = vessel.CoM;
        Vector3 right = vessel.transform.right;

        Part left = null;
        Part rightWing = null;

        float minDot = float.MaxValue;
        float maxDot = float.MinValue;

        foreach (var wing in wings)
        {
            float dot = Vector3.Dot(wing.transform.position - com, right);

            if (dot < minDot) { minDot = dot; left = wing; }
            if (dot > maxDot) { maxDot = dot; rightWing = wing; }
        }

        ProcessWing(left, vessel, -1);
        ProcessWing(rightWing, vessel, 1);
    }

    void ProcessWing(Part wing, Vessel vessel, int sideSign)
    {
        if (wing == null) return;

        EnsureEffect(wing);

        if (!wingtipLocal.ContainsKey(wing))
            wingtipLocal[wing] = FindWingtipLocal(wing, vessel.CoM);

        Vector3 tip = wing.transform.TransformPoint(wingtipLocal[wing]);

        Emit(wing, vessel, tip, sideSign);
    }

    void EnsureEffect(Part wing)
    {
        if (effects.ContainsKey(wing)) return;

        GameObject obj = new GameObject("Vortex");

        var ps = obj.AddComponent<ParticleSystem>();

        var main = ps.main;
        main.loop = false;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startLifetime = 0.3f;
        main.startSpeed = 0f;
        main.startSize = 0.045f;
        main.startColor = Color.white;
        main.maxParticles = 50000;

        var emission = ps.emission;
        emission.enabled = false;

        var renderer = ps.GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.material = new Material(Shader.Find("Legacy Shaders/Particles/Alpha Blended"));

        var col = ps.colorOverLifetime;
        col.enabled = true;

        Gradient grad = new Gradient();
        grad.SetKeys(
            new GradientColorKey[] {
                new GradientColorKey(new Color(0.92f,0.92f,0.92f), 0f),
                new GradientColorKey(new Color(0.7f,0.7f,0.7f), 0.5f),
                new GradientColorKey(new Color(0.5f,0.5f,0.5f), 1f)
            },
            new GradientAlphaKey[] {
                new GradientAlphaKey(0.95f, 0f),
                new GradientAlphaKey(0.5f, 0.5f),
                new GradientAlphaKey(0f, 1f)
            }
        );

        col.color = new ParticleSystem.MinMaxGradient(grad);

        effects[wing] = ps;
        emitAccumulator[wing] = 0f;
    }

    void Emit(Part wing, Vessel vessel, Vector3 tip, int sideSign)
    {
        if (vessel.srf_velocity.sqrMagnitude < 0.01f) return;

        float speed = (float)vessel.srfSpeed;
        float g = (float)vessel.geeForce;
        float density = (float)vessel.atmDensity;
        float altitude = (float)vessel.altitude;

        Vector3 airflow = -(Vector3)vessel.srf_velocity.normalized;

        bool highG = speed >= 60f && g >= 3f;
        bool highAlt = speed >= 250f && altitude >= 20000f;

        if (!(highG || highAlt)) return;
        if (density < 0.15f) return;

        float intensity = Mathf.Clamp01((g - 1f) * 0.3f + (speed - 250f) / 200f);

        var ps = effects[wing];

        float particlesPerMeter = Mathf.Lerp(30f, 100f, intensity);
        float rate = particlesPerMeter * speed;

        emitAccumulator[wing] += rate * Time.deltaTime;

        int count = Mathf.FloorToInt(emitAccumulator[wing]);
        if (count <= 0) return;

        emitAccumulator[wing] -= count;

        float flowSpeed = speed * Mathf.Lerp(0.9f, 1.1f, intensity);
        Vector3 baseVelocity = airflow * flowSpeed;

        Vector3 side = Vector3.Cross(vessel.transform.up, airflow).normalized;
        Vector3 ortho = Vector3.Cross(airflow, side).normalized;

        float radius = Mathf.Lerp(0.003f, 0.01f, intensity);

        for (int i = 0; i < count; i++)
        {
            ParticleSystem.EmitParams ep = new ParticleSystem.EmitParams();

            // SMALL OFFSET ONLY (no ring spawning)
            Vector3 offset =
                side * Random.Range(-radius, radius) +
                ortho * Random.Range(-radius, radius);

            ep.position = tip + offset;

            // delayed swirl (fixes cone)

            // swirl starts weak near wing, grows naturally downstream
            float swirlStrength = Mathf.Lerp(0.2f, 8f, intensity);

            // initial swirl dampening (this is the delay effect)
            swirlStrength *= 0.15f;

            // radial direction
            Vector3 radial = offset.sqrMagnitude > 0.0001f
                ? offset.normalized
                : side;

            // tangential rotation
            Vector3 tangential = Vector3.Cross(airflow, radial).normalized;

            // centripetal pull (keeps vortex tight)
            float coreTightness = Mathf.Lerp(3f, 12f, intensity);
            Vector3 centripetal = -radial * coreTightness;

            ep.velocity =
                baseVelocity +
                tangential * swirlStrength * sideSign +
                centripetal;

            ep.startLifetime = Mathf.Lerp(0.2f, 0.35f, intensity);
            ep.startSize = Mathf.Lerp(0.04f, 0.065f, intensity);

            ep.startColor = Color.Lerp(
                new Color(0.6f, 0.6f, 0.6f),
                Color.white,
                Mathf.InverseLerp(3f, 7f, g)
            );

            ps.Emit(ep, 1);
        }
    }

    Vector3 FindWingtipLocal(Part wing, Vector3 com)
    {
        Vector3 best = Vector3.zero;
        float bestDist = 0f;

        foreach (var mf in wing.FindModelComponents<MeshFilter>())
        {
            if (mf.sharedMesh == null) continue;

            foreach (var v in mf.sharedMesh.vertices)
            {
                Vector3 wp = mf.transform.TransformPoint(v);
                float d = (wp - com).sqrMagnitude;

                if (d > bestDist)
                {
                    bestDist = d;
                    best = wing.transform.InverseTransformPoint(wp);
                }
            }
        }

        return best;
    }

    void Cleanup()
    {
        foreach (var ps in effects.Values)
            if (ps != null) Destroy(ps.gameObject);

        effects.Clear();
        wingtipLocal.Clear();
        emitAccumulator.Clear();
    }

    void OnDestroy()
    {
        Cleanup();
    }
}
