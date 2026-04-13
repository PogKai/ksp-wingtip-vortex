// Baseline with Curved High-Speed LineRenderer (trail-like behavior)

using UnityEngine;
using KSP;
using System.Collections;
using System.Collections.Generic;

[KSPAddon(KSPAddon.Startup.Flight, false)]
public class WingtipVortex : MonoBehaviour
{
    private Vessel vessel;

    private List<TrailRenderer> trails = new List<TrailRenderer>();
    private List<LineRenderer> lines = new List<LineRenderer>();
    private List<GameObject> trailObjs = new List<GameObject>();
    private List<Transform> anchors = new List<Transform>();
    private List<float> strengths = new List<float>();
    private List<float> areas = new List<float>();

    private List<Queue<Vector3>> lineHistory = new List<Queue<Vector3>>();
    private int historyLength = 8;

    private List<bool> trailWasActive = new List<bool>();
    private List<bool> lineWasActive = new List<bool>();

    private bool useLineMode = false;
    private float currentIntensity = 0f;

    float buildSpeed = 2f;
    float decaySpeed = 0.8f;

    IEnumerator Start()
    {
        while (FlightGlobals.ActiveVessel == null || !FlightGlobals.ActiveVessel.loaded)
            yield return null;

        vessel = FlightGlobals.ActiveVessel;
        FindVortexSources();
    }

    void FindVortexSources()
    {
        List<PartScore> allParts = new List<PartScore>();

        foreach (Part p in vessel.parts)
        {
            if (!(p.Modules.Contains("ModuleLiftingSurface") ||
                  p.Modules.Contains("ModuleControlSurface"))) continue;

            Renderer r = p.GetComponentInChildren<Renderer>();
            if (r == null) continue;

            Vector3 local = vessel.transform.InverseTransformPoint(p.transform.position);
            if (Mathf.Abs(local.x) < 0.3f) continue;

            float liftScore = r.bounds.size.x * r.bounds.size.z;
            allParts.Add(new PartScore(p, Mathf.Abs(local.x), local.z, liftScore));
        }

        allParts.Sort((a, b) => b.lift.CompareTo(a.lift));

        Part leftRear = null;
        Part rightRear = null;

        foreach (var p in allParts)
        {
            Vector3 local = vessel.transform.InverseTransformPoint(p.part.transform.position);
            if (local.x < 0f && leftRear == null) leftRear = p.part;
            if (local.x > 0f && rightRear == null) rightRear = p.part;
            if (leftRear != null && rightRear != null) break;
        }

        if (leftRear != null) AddWingVortex(leftRear, false, 1f);
        if (rightRear != null) AddWingVortex(rightRear, true, 1f);

        allParts.Sort((a, b) => b.forward.CompareTo(a.forward));

        Part leftFront = null;
        Part rightFront = null;

        foreach (var p in allParts)
        {
            Vector3 local = vessel.transform.InverseTransformPoint(p.part.transform.position);
            if (local.x < 0f && leftFront == null && p.part != leftRear) leftFront = p.part;
            if (local.x > 0f && rightFront == null && p.part != rightRear) rightFront = p.part;
            if (leftFront != null && rightFront != null) break;
        }

        if (leftFront != null) AddWingVortex(leftFront, false, 0.4f);
        if (rightFront != null) AddWingVortex(rightFront, true, 0.4f);
    }

    void AddWingVortex(Part wing, bool isRight, float strength)
    {
        Transform anchor = CreateWingtipAnchor(wing, isRight);
        if (anchor == null) return;

        Renderer r = wing.GetComponentInChildren<Renderer>();
        float area = (r != null) ? r.bounds.size.x * r.bounds.size.z : 1f;

        GameObject obj = new GameObject("Vortex");

        TrailRenderer tr = obj.AddComponent<TrailRenderer>();
        tr.time = 1.5f;
        tr.startWidth = 0.15f;
        tr.endWidth = 0.03f;
        tr.material = new Material(Shader.Find("Legacy Shaders/Particles/Additive"));
        tr.enabled = false;

        LineRenderer lr = obj.AddComponent<LineRenderer>();
        lr.positionCount = historyLength;
        lr.startWidth = 0.1f;
        lr.endWidth = 0.02f;
        lr.material = new Material(Shader.Find("Legacy Shaders/Particles/Additive"));
        lr.enabled = false;

        trailObjs.Add(obj);
        trails.Add(tr);
        lines.Add(lr);
        anchors.Add(anchor);
        strengths.Add(strength);
        areas.Add(area);

        lineHistory.Add(new Queue<Vector3>());
        trailWasActive.Add(false);
        lineWasActive.Add(false);
    }

    Transform CreateWingtipAnchor(Part wing, bool isRight)
    {
        MeshFilter mf = wing.GetComponentInChildren<MeshFilter>();
        if (mf == null) return null;

        Vector3 best = mf.mesh.vertices[0];
        float scoreBest = float.MinValue;

        foreach (var v in mf.mesh.vertices)
        {
            Vector3 world = mf.transform.TransformPoint(v);
            float score = vessel.transform.InverseTransformPoint(world).x * (isRight ? 1f : -1f);
            if (score > scoreBest)
            {
                scoreBest = score;
                best = v;
            }
        }

        GameObject anchor = new GameObject("Anchor");
        anchor.transform.position = mf.transform.TransformPoint(best);
        anchor.transform.parent = wing.transform;
        return anchor.transform;
    }

    void Update()
    {
        if (vessel == null || !vessel.loaded) return;

        float speed = vessel.GetSrfVelocity().magnitude;
        float altitude = (float)vessel.altitude;
        float g = Mathf.Min((float)vessel.geeForce, 15f);

        Vector3 flow = vessel.GetSrfVelocity().normalized;

        float targetIntensity = Mathf.Clamp01((g - 3f) / 10f);

        if (targetIntensity > currentIntensity)
            currentIntensity = Mathf.MoveTowards(currentIntensity, targetIntensity, buildSpeed * Time.deltaTime);
        else
            currentIntensity = Mathf.MoveTowards(currentIntensity, targetIntensity, decaySpeed * Time.deltaTime);

        float intensity = currentIntensity;

        bool shouldEnterLineMode = speed >= 180f || altitude >= 8000f;
        bool shouldExitLineMode = speed <= 165f && altitude <= 4000f && g <= 3.1f;

        if (!useLineMode && shouldEnterLineMode)
            useLineMode = true;
        else if (useLineMode && shouldExitLineMode)
            useLineMode = false;

        float trailFade = useLineMode ? 0f : 1f;
        float lineFade = useLineMode ? 1f : 0f;

        for (int i = 0; i < trails.Count; i++)
        {
            var tr = trails[i];
            var lr = lines[i];
            var obj = trailObjs[i];
            var anchor = anchors[i];
            var history = lineHistory[i];

            float visible = intensity * strengths[i];
            if (strengths[i] < 1f && g < 4.2f) visible *= Mathf.Clamp01((g - 3.5f) / 0.7f);

            // FORCE visibility gradually at high altitude (no G or speed required)
            if (altitude >= 8000f)
            {
                float altBlend = Mathf.Clamp01((altitude - 8000f) / 4000f); // ramps from 8k → 12k
                float forcedVis = Mathf.Lerp(0.1f, 0.5f, altBlend);
                visible = Mathf.Max(visible, forcedVis);
            }

            Vector3 currentPoint = anchor.position;

            history.Enqueue(currentPoint);
            if (history.Count > historyLength) history.Dequeue();

            float cappedSpeed = Mathf.Min(speed, 180f);

            if (Vector3.Distance(obj.transform.position, anchor.position) > 1.5f)
            {
                obj.transform.position = anchor.position - flow * 0.5f;
            }
            else
            {
                Vector3 targetPos = anchor.position - flow * 0.5f;
                obj.transform.position = Vector3.Lerp(obj.transform.position, targetPos, Time.deltaTime * 4f);
            }

            bool shouldUseLine = useLineMode && visible > 0.01f;
            bool shouldUseTrail = !useLineMode && visible > 0.01f;

            if (shouldUseTrail)
            {
                lr.enabled = false;

                if (lineWasActive[i])
                {
                    tr.Clear();
                    history.Clear();
                    obj.transform.position = anchor.position - flow * 0.5f;
                }

                if (!trailWasActive[i])
                {
                    tr.Clear();
                    obj.transform.position = anchor.position - flow * 0.5f;
                }

                tr.enabled = true;
                tr.emitting = true;

                tr.startWidth = 0.2f * visible * trailFade;
                tr.time = Mathf.Lerp(0.8f, 3.0f, visible);

                trailWasActive[i] = true;
                lineWasActive[i] = false;
            }
            else
            {
                tr.emitting = false;
                tr.enabled = true;
                trailWasActive[i] = false;
            }

            if (shouldUseLine)
            {
                lr.enabled = true;

                if (trailWasActive[i])
                {
                    tr.Clear();
                    tr.emitting = false;
                    tr.enabled = false;
                    history.Clear();
                }

                int dynamicLength = historyLength;
                if (lr.positionCount != dynamicLength)
                    lr.positionCount = dynamicLength;

                int idx = 0;
                Vector3 perp = Vector3.Cross(flow, vessel.upAxis).normalized;
                float sideSign = Mathf.Sign(vessel.transform.InverseTransformPoint(anchor.position).x);
                float cruiseFactor = Mathf.Clamp01((altitude - 8000f) / 8000f);
                float lengthScale = Mathf.Lerp(1.0f, 4.0f, Mathf.Clamp01(speed / 300f));

                foreach (var point in history)
                {
                    Vector3 offset = -flow * (idx * 1.5f * lengthScale);

                    float curveStrength = 0.3f * visible;
                    float curve = curveStrength * idx * idx * 0.15f;
                    Vector3 curvedOffset = perp * curve * sideSign;

                    float warpAmp = Mathf.Lerp(0.0f, 1.2f, cruiseFactor) * visible;
                    float warpFreq = Mathf.Lerp(0.5f, 1.5f, cruiseFactor);
                    float warp = Mathf.Sin(idx * 0.4f + Time.time * warpFreq) * warpAmp;
                    Vector3 warpOffset = perp * warp;

                    float seed = (i + 1) * 13.37f + idx * 7.91f;
                    float phaseOffset = seed * 0.37f;
                    float ampJitter = 0.75f + 0.5f * Mathf.PerlinNoise(seed, Time.time * 0.2f);
                    float freqJitter = 0.85f + 0.3f * Mathf.PerlinNoise(seed * 0.5f, Time.time * 0.15f);

                    float helixRadius = Mathf.Lerp(0.05f, 0.25f, cruiseFactor) * visible * ampJitter;
                    float helixSpeed = Mathf.Lerp(1.0f, 2.5f, cruiseFactor) * freqJitter;
                    float angle = Time.time * helixSpeed + idx * 0.6f + phaseOffset;

                    Vector3 helixOffset = perp * Mathf.Sin(angle) * helixRadius
                                         + vessel.upAxis * Mathf.Cos(angle) * helixRadius * 0.6f;

                    // ensure visible motion at cruise
                    float falloff = Mathf.Clamp01(idx / (float)historyLength);

                    // boost motion so it is always noticeable
                    float motionBoost = Mathf.Lerp(0.3f, 1.2f, cruiseFactor);

                    Vector3 motion = (warpOffset + helixOffset) * falloff * motionBoost;

                    lr.SetPosition(idx, point + offset + curvedOffset + motion);
                    idx++;
                }

                float strengthFactor = strengths[i];
                lr.startWidth = 0.15f * visible * lineFade * strengthFactor;
                lr.endWidth = 0.03f * visible * lineFade * strengthFactor;

                // smooth fade-out along line (prevents ribbon cutoff)
                Gradient lineGrad = new Gradient();
                lineGrad.SetKeys(
                    new GradientColorKey[] {
                        new GradientColorKey(Color.white, 0f),
                        new GradientColorKey(new Color(0.8f,0.8f,0.8f), 0.5f),
                        new GradientColorKey(new Color(0.6f,0.6f,0.6f), 1f)
                    },
                    new GradientAlphaKey[] {
                        new GradientAlphaKey(visible * strengthFactor, 0f),
                        new GradientAlphaKey(visible * strengthFactor * 0.6f, 0.4f),
                        new GradientAlphaKey(visible * strengthFactor * 0.2f, 0.7f),
                        new GradientAlphaKey(0f, 1f)
                    }
                );
                lr.colorGradient = lineGrad;

                lineWasActive[i] = true;
            }
            else
            {
                lr.enabled = false;
                lineWasActive[i] = false;
            }
        }
    }

    private class PartScore
    {
        public Part part;
        public float width;
        public float forward;
        public float lift;

        public PartScore(Part p, float w, float f, float l)
        {
            part = p;
            width = w;
            forward = f;
            lift = l;
        }
    }
}
