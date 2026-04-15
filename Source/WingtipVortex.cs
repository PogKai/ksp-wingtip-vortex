using UnityEngine;
using KSP;
using System.Collections;
using System.Collections.Generic;

[KSPAddon(KSPAddon.Startup.Flight, false)]
public class WingtipVortex : MonoBehaviour
{
    private static Material sharedTrailMat;
    private static Material sharedLineMat;

    private Vessel vessel;
    private Transform vesselTransform;

    private float boundsRefreshTimer = 0f;
    private const float boundsRefreshInterval = 0.5f;

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
    private List<Vector3> lastFlows = new List<Vector3>();
    private List<Vector3> trailPositions = new List<Vector3>();

    private bool useLineMode = false;
    private float currentIntensity = 0f;

    private float buildSpeed = 2f;
    private float decaySpeed = 0.8f;
    private float lineActivationBlend = 0f;
    private float lineActivationSpeed = 0.6f;

    private float maxDelta = 0.05f;
    private float abnormalFrameThreshold = 0.20f;

    private Bounds vesselBounds;
    private float vesselSize;

    IEnumerator Start()
    {
        while (FlightGlobals.ActiveVessel == null || !FlightGlobals.ActiveVessel.loaded)
            yield return null;

        vessel = FlightGlobals.ActiveVessel;
        vesselTransform = vessel.transform;

        if (sharedTrailMat == null)
            sharedTrailMat = new Material(Shader.Find("Legacy Shaders/Particles/Additive"));
        if (sharedLineMat == null)
            sharedLineMat = new Material(Shader.Find("Legacy Shaders/Particles/Additive"));

        FindVortexSources();
    }

    void ComputeVesselBounds()
    {
        vesselBounds = new Bounds(vesselTransform.position, Vector3.zero);
        bool initialized = false;

        foreach (var part in vessel.parts)
        {
            var pr = part.GetComponentInChildren<Renderer>();
            if (pr == null) continue;

            if (!initialized)
            {
                vesselBounds = pr.bounds;
                initialized = true;
            }
            else
            {
                vesselBounds.Encapsulate(pr.bounds);
            }
        }

        vesselSize = vesselBounds.size.magnitude;
    }

    // --- CANTED CANARD LOGIC (kept intact) ---
    void FindVortexSources()
    {
        List<PartScore> rearParts = new List<PartScore>();
        List<PartScore> frontParts = new List<PartScore>();

        foreach (Part p in vessel.parts)
        {
            bool isLift = p.Modules.Contains("ModuleLiftingSurface");
            bool isControl = p.Modules.Contains("ModuleControlSurface");

            if (!(isLift || isControl)) continue;

            Vector3 local = vesselTransform.InverseTransformPoint(p.transform.position);

            bool isElevator = isControl && local.z < -0.5f && Mathf.Abs(local.x) < 2.0f;
            if (isElevator) continue;

            var r = p.GetComponentInChildren<Renderer>();
            if (r == null) continue;

            if (Mathf.Abs(local.x) < 0.3f) continue;

            float liftScore = r.bounds.size.x * r.bounds.size.z;

            float slant = Mathf.Abs((float)Vector3.Dot(p.transform.up, vesselTransform.right));
            float slantBonus = Mathf.Clamp01(slant - 0.3f) * 0.5f;

            liftScore *= (1f + slantBonus);

            PartScore score = new PartScore(p, Mathf.Abs(local.x), local.z, liftScore);

            if (local.z < 0f)
                rearParts.Add(score);
            else
                frontParts.Add(score);
        }

        rearParts.Sort((a, b) => b.lift.CompareTo(a.lift));

        Part leftRear = null;
        Part rightRear = null;

        foreach (var p in rearParts)
        {
            Vector3 local = vesselTransform.InverseTransformPoint(p.part.transform.position);

            if (local.x < 0f && leftRear == null) leftRear = p.part;
            if (local.x > 0f && rightRear == null) rightRear = p.part;

            if (leftRear != null && rightRear != null) break;
        }

        if (leftRear != null) AddWingVortex(leftRear, false, 1f);
        if (rightRear != null) AddWingVortex(rightRear, true, 1f);

        frontParts.Sort((a, b) =>
        {
            float aScore = (a.forward * 2f) + a.width * 0.2f;
            float bScore = (b.forward * 2f) + b.width * 0.2f;
            return bScore.CompareTo(aScore);
        });

        Part leftFront = null;
        Part rightFront = null;

        foreach (var p in frontParts)
        {
            Vector3 local = vesselTransform.InverseTransformPoint(p.part.transform.position);

            if (local.z < 0.15f) continue;
            if (p.part == leftRear || p.part == rightRear) continue;

            float forwardAlign = Vector3.Dot(p.part.transform.forward, vesselTransform.forward);
            bool isForwardSurface = forwardAlign > 0.2f;

            if (local.x < 0f && leftFront == null && isForwardSurface)
                leftFront = p.part;

            if (local.x > 0f && rightFront == null && isForwardSurface)
                rightFront = p.part;

            if (leftFront != null && rightFront != null) break;
        }

        if (leftFront == null || rightFront == null)
        {
            foreach (var p in frontParts)
            {
                Vector3 local = vesselTransform.InverseTransformPoint(p.part.transform.position);

                if (p.part == leftRear || p.part == rightRear) continue;

                if (local.x < 0f && leftFront == null) leftFront = p.part;
                if (local.x > 0f && rightFront == null) rightFront = p.part;

                if (leftFront != null && rightFront != null) break;
            }
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
        obj.transform.parent = vesselTransform;

        TrailRenderer tr = obj.AddComponent<TrailRenderer>();
        tr.time = 1.5f;
        tr.startWidth = 0.15f;
        tr.endWidth = 0.03f;
        tr.sharedMaterial = sharedTrailMat;
        tr.enabled = false;

        LineRenderer lr = obj.AddComponent<LineRenderer>();
        lr.positionCount = historyLength;
        lr.startWidth = 0.1f;
        lr.endWidth = 0.02f;
        lr.sharedMaterial = sharedLineMat;
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
        lastFlows.Add(Vector3.zero);
        trailPositions.Add(anchor.position);
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
            float score = vesselTransform.InverseTransformPoint(world).x * (isRight ? 1f : -1f);
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

        float rawDelta = Time.deltaTime;
        float dt = Mathf.Min(rawDelta, maxDelta);

        if (!Application.isFocused || rawDelta > abnormalFrameThreshold)
        {
            for (int i = 0; i < trails.Count; i++)
                ResetVortexState(i);
            return;
        }

        float speed = vessel.GetSrfVelocity().magnitude;

        boundsRefreshTimer += dt;
        if (boundsRefreshTimer >= boundsRefreshInterval)
        {
            ComputeVesselBounds();
            boundsRefreshTimer = 0f;
        }

        // if leaving atmosphere, fade out instead of hard cut
        if (vessel.atmDensity <= 0.01f)
        {
            for (int i = 0; i < trails.Count; i++)
            {
                trails[i].emitting = false;
                trails[i].time = Mathf.Lerp(trails[i].time, 0f, dt * 0.5f);

                // let existing line fade and drift back
                if (lines[i].enabled)
                {
                    lines[i].startWidth *= 0.98f;
                    lines[i].endWidth *= 0.98f;
                }
            }
            return;
        }

        float altitude = (float)vessel.altitude;
        float g = Mathf.Min((float)vessel.geeForce, 15f);

        Vector3 flow = vessel.GetSrfVelocity().normalized;

        float targetIntensity = Mathf.Clamp01((g - 3f) / 10f);



        if (targetIntensity > currentIntensity)
            currentIntensity = Mathf.MoveTowards(currentIntensity, targetIntensity, buildSpeed * dt);
        else
            currentIntensity = Mathf.MoveTowards(currentIntensity, targetIntensity, decaySpeed * dt);

        float intensity = currentIntensity;

        bool shouldEnterLineMode = (altitude >= 8000f && altitude <= 15000f && vessel.atmDensity > 0.01f && speed >= 180f);
        bool shouldExitLineMode = speed <= 165f && altitude <= 4000f && g <= 3.1f;

        // force line mode takeover at high speed
        if (speed >= 250f)
        {
            useLineMode = true;
        }
        else if (!useLineMode && shouldEnterLineMode)
        {
            useLineMode = true;
            for (int h = 0; h < lineHistory.Count; h++)
                lineHistory[h].Clear();
        }
        else if (useLineMode && shouldExitLineMode)
        {
            useLineMode = false;
            for (int h = 0; h < lineHistory.Count; h++)
                lineHistory[h].Clear();
        }

        float targetLineBlend = useLineMode ? 1f : 0f;
        lineActivationBlend = Mathf.MoveTowards(lineActivationBlend, targetLineBlend, lineActivationSpeed * dt);

        float trailFade = 1f - lineActivationBlend;
        float lineFade = lineActivationBlend;

        for (int i = 0; i < trails.Count; i++)
        {
            float referenceSize = 15f;
            float vesselScale = vesselSize / referenceSize;
            float sizeScale = 1.0f + Mathf.Clamp(vesselScale - 1.0f, 0f, 2.5f) * 0.25f;

            var tr = trails[i];
            var lr = lines[i];
            var obj = trailObjs[i];
            var anchor = anchors[i];
            var history = lineHistory[i];

            float visible = intensity * strengths[i];

            // night brightness reduction
            float lightFactor = Mathf.Clamp01((float)(vessel.solarFlux / 2000f));
            visible *= Mathf.Lerp(0.2f, 1f, lightFactor);

            // fade-in in upper atmosphere
            float atmFactor = Mathf.Clamp01((float)((vessel.atmDensity - 0.01f) / 0.09f));
            visible *= atmFactor;

            // altitude-based visibility (8k–15k only)
            float altitudeScale = 0f;
            if (altitude >= 8000f && altitude <= 15000f)
            {
                float altBlend = Mathf.Clamp01((altitude - 8000f) / 7000f);
                float forcedVis = Mathf.Lerp(0.1f, 0.5f, altBlend);
                visible = Mathf.Max(visible, forcedVis);

                // stronger scaling for contrails
                altitudeScale = Mathf.Lerp(1f, 3f, altBlend);
                visible *= Mathf.Lerp(1f, 1.5f, altBlend);
            }
            else if (altitude > 15000f)
            {
                visible = 0f;
            }
            else if (altitude > 15000f)
            {
                visible = 0f;
            }

            // front canard behavior tuning (strengths < 1f assumed front surfaces)
            if (strengths[i] < 1f)
            {
                if (altitude < 8000f)
                {
                    // require higher G at low altitude for front canards
                    float gFactor = Mathf.Clamp01((g - 4.5f) / 1.0f);
                    visible *= gFactor;
                }
                else
                {
                    // at high altitude: show if either G is high OR speed is low
                    float gFactor = Mathf.Clamp01((g - 3.5f) / 1.5f);
                    float speedFactor = Mathf.Clamp01((220f - speed) / 40f);
                    float combo = Mathf.Max(gFactor, speedFactor);
                    visible *= combo;
                }
            }
            Vector3 currentPoint = anchor.position;

            // maintain history (required for line rendering)
            history.Enqueue(currentPoint);
            if (history.Count > historyLength) history.Dequeue();

            obj.transform.position = anchor.position;

            lastFlows[i] = flow;

            bool shouldUseLine = lineFade > 0.01f && visible > 0.01f;
            bool shouldUseTrail = trailFade > 0.01f && visible > 0.01f;

            // TRAIL MODE
            if (shouldUseTrail)
            {
                if (lineWasActive[i])
                {
                    lr.enabled = false;
                    lr.positionCount = historyLength;
                    history.Clear();
                    tr.Clear();
                    tr.time = 1.5f;
                }

                tr.enabled = true;
                tr.emitting = true;
                tr.time = 0.5f; // allow proper trailing while staying tight to wingtip
                tr.startWidth = 0.2f * visible * trailFade;
                tr.endWidth = 0.05f * visible * trailFade;

                trailWasActive[i] = true;
                lineWasActive[i] = false;
            }
            else
            {
                tr.emitting = false;
            }

            // LINE MODE (STABLE)
            if (shouldUseLine)
            {
                lr.enabled = true;

                if (trailWasActive[i])
                {
                    tr.Clear();
                    tr.emitting = false;
                    tr.enabled = false;
                    history.Clear();
                    lr.positionCount = historyLength;
                }

                // scale line length ONLY at altitude
                float altitudeFactor = Mathf.Clamp01((altitude - 8000f) / 7000f);
                if (altitude > 15000f) altitudeFactor = 0f;
                int dynamicLength = Mathf.RoundToInt(Mathf.Lerp(historyLength, historyLength * 2.5f, altitudeFactor));
                if (lr.positionCount != dynamicLength)
                    lr.positionCount = dynamicLength;

                Vector3 perp = Vector3.Cross(flow, vessel.transform.up).normalized;
                float sideSign = Mathf.Sign(vessel.transform.InverseTransformPoint(anchor.position).x);
                float cruiseFactor = Mathf.Clamp01((altitude - 8000f) / 8000f);
                float stressFactor = Mathf.Clamp01((g - 4f) / 6f + (speed - 200f) / 200f);
                float lengthScale = Mathf.Lerp(1.0f, 4.0f, Mathf.Clamp01(speed / 300f));
                lengthScale *= Mathf.Lerp(1f, 0.4f, stressFactor);

                for (int idx = 0; idx < dynamicLength; idx++)
                {
                    float t = idx;
                    float spacing = Mathf.Pow(t, 1.05f);
                    spacing = Mathf.Min(spacing, t * 1.2f);
                    Vector3 offset = -flow * (spacing * 1.5f * lengthScale);

                    float downwashStrength = 0.2f * visible;
                    float falloff = (dynamicLength > 1) ? (float)idx / (dynamicLength - 1) : 0f;
                    Vector3 downwashOffset = -vessel.transform.up * (idx * 0.02f * downwashStrength * falloff);

                    float curveStrength = 0.3f * visible;
                    float curve = curveStrength * idx * 0.15f * falloff;
                    Vector3 curvedOffset = perp * curve * sideSign;

                    float speedNorm = Mathf.Clamp01(speed / 250f);
                    float warpAmp = Mathf.Lerp(0.2f, 1.2f, speedNorm) * visible;
                    warpAmp *= Mathf.Lerp(1f, 0.3f, stressFactor);
                    float warpFreq = Mathf.Lerp(0.5f, 1.5f, cruiseFactor);
                    float warp = Mathf.Sin(idx * 0.4f + Time.time * warpFreq) * warpAmp;
                    Vector3 warpOffset = perp * warp * falloff;

                    Vector3 flowRight = Vector3.Cross(vessel.transform.up, flow).normalized;
                    float drift = Mathf.Sin(Time.time * 0.6f + idx * 0.25f) * 0.15f * visible;
                    Vector3 driftOffset = flowRight * drift * idx * falloff;

                    Vector3 finalPos = anchor.position + offset + curvedOffset + warpOffset + downwashOffset + driftOffset;

                    lr.SetPosition(idx, finalPos);
                }

                float strengthFactor = strengths[i];
                float lineScale = 1f;
                if (altitude >= 8000f && altitude <= 15000f)
                {
                    float altBlendLS = Mathf.Clamp01((altitude - 8000f) / 7000f);
                    lineScale = Mathf.Lerp(1.5f, 3f, altBlendLS); // raised baseline
                }

                float baseScale = Mathf.Clamp(vesselSize / 25f, 0.3f, 1.5f);
                lr.startWidth = 0.15f * visible * lineFade * strengthFactor * lineScale * baseScale;
                lr.endWidth = 0.03f * visible * lineFade * strengthFactor * lineScale * baseScale;

                // smooth fade along the line (prevents abrupt ribbon cutoff)
                Gradient grad = new Gradient();
                grad.SetKeys(
                    new GradientColorKey[] {
                        new GradientColorKey(Color.white, 0f),
                        new GradientColorKey(new Color(0.8f,0.8f,0.8f), 0.5f),
                        new GradientColorKey(new Color(0.6f,0.6f,0.6f), 1f)
                    },
                    new GradientAlphaKey[] {
                        new GradientAlphaKey(visible * strengthFactor, 0f),
                        new GradientAlphaKey(visible * strengthFactor * 0.6f, 0.5f),
                        new GradientAlphaKey(0f, 1f)
                    }
                );
                lr.colorGradient = grad;

                lineWasActive[i] = true;
                trailWasActive[i] = false;
            }
            else
            {
                lr.enabled = false;
                lineWasActive[i] = false;
            }
        }
    }

    void ResetVortexState(int i)
    {
        trails[i].Clear();
        trails[i].time = 1.5f;
        trails[i].emitting = false;

        lines[i].positionCount = historyLength;
        lines[i].enabled = false;

        lineHistory[i].Clear();

        trailWasActive[i] = false;
        lineWasActive[i] = false;
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
