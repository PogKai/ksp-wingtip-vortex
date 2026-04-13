using UnityEngine;
using KSP;
using System.Collections;
using System.Collections.Generic;

[KSPAddon(KSPAddon.Startup.Flight, false)]
public class WingtipVortex : MonoBehaviour
{
    private Vessel vessel;

    private List<TrailRenderer> trails = new List<TrailRenderer>();
    private List<GameObject> trailObjs = new List<GameObject>();
    private List<Transform> anchors = new List<Transform>();
    private List<float> strengths = new List<float>();

    private float currentIntensity = 0f;
    private bool wasSpawning = false;

    float buildSpeed = 2f;
    float decaySpeed = 0.8f;

    Vector3 smoothedFlow = Vector3.zero;

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
                  p.Modules.Contains("ModuleControlSurface")))
                continue;

            Renderer r = p.GetComponentInChildren<Renderer>();
            if (r == null) continue;

            Vector3 local = vessel.transform.InverseTransformPoint(p.transform.position);

            if (Mathf.Abs(local.x) < 0.3f)
                continue;

            float width = Mathf.Abs(local.x);
            float forward = local.z;

            float liftScore = r.bounds.size.x * r.bounds.size.z;

            float upDot = Mathf.Abs(Vector3.Dot(p.transform.up, vessel.upAxis));
            float orientationFactor = Mathf.Lerp(0.2f, 1f, upDot);

            liftScore *= orientationFactor;

            allParts.Add(new PartScore(p, width, forward, liftScore));
        }

        allParts.Sort((a, b) => b.lift.CompareTo(a.lift));

        Part leftRear = null;
        Part rightRear = null;

        foreach (var p in allParts)
        {
            Vector3 local = vessel.transform.InverseTransformPoint(p.part.transform.position);

            if (local.x < 0f && leftRear == null)
                leftRear = p.part;

            if (local.x > 0f && rightRear == null)
                rightRear = p.part;

            if (leftRear != null && rightRear != null)
                break;
        }

        if (leftRear != null)
            AddWingVortex(leftRear, false, 1f);

        if (rightRear != null)
            AddWingVortex(rightRear, true, 1f);

        // --- Add front vortices (weaker) ---
        allParts.Sort((a, b) => b.forward.CompareTo(a.forward));

        Part leftFront = null;
        Part rightFront = null;

        foreach (var p in allParts)
        {
            Vector3 local = vessel.transform.InverseTransformPoint(p.part.transform.position);

            if (local.x < 0f && leftFront == null && p.part != leftRear)
                leftFront = p.part;

            if (local.x > 0f && rightFront == null && p.part != rightRear)
                rightFront = p.part;

            if (leftFront != null && rightFront != null)
                break;
        }

        if (leftFront != null)
            AddWingVortex(leftFront, false, 0.4f);

        if (rightFront != null)
            AddWingVortex(rightFront, true, 0.4f);
    }

    void AddWingVortex(Part wing, bool isRight, float strength)
    {
        Transform anchor = CreateWingtipAnchor(wing, isRight);

        if (anchor != null)
            CreateTrail(anchor, strength);
    }

    void CreateTrail(Transform anchor, float strength)
    {
        GameObject obj = new GameObject("WingtipTrail");

        TrailRenderer tr = obj.AddComponent<TrailRenderer>();
        tr.time = 1.5f;
        tr.startWidth = 0.15f;
        tr.endWidth = 0.03f;
        tr.material = new Material(Shader.Find("Legacy Shaders/Particles/Additive"));
        tr.alignment = LineAlignment.View;
        tr.enabled = false;
        tr.emitting = false;

        trailObjs.Add(obj);
        trails.Add(tr);
        anchors.Add(anchor);
        strengths.Add(strength);
    }

    Transform CreateWingtipAnchor(Part wing, bool isRight)
    {
        MeshFilter mf = wing.GetComponentInChildren<MeshFilter>();
        if (mf == null) return null;

        Vector3[] vertices = mf.mesh.vertices;

        Vector3 bestVertex = vertices[0];
        float bestScore = float.MinValue;

        foreach (var v in vertices)
        {
            Vector3 world = mf.transform.TransformPoint(v);
            Vector3 vesselLocal = vessel.transform.InverseTransformPoint(world);

            float score = vesselLocal.x * (isRight ? 1f : -1f);

            if (score > bestScore)
            {
                bestScore = score;
                bestVertex = v;
            }
        }

        Vector3 bestWorld = mf.transform.TransformPoint(bestVertex);

        GameObject anchor = new GameObject(isRight ? "RightWingtip" : "LeftWingtip");
        anchor.transform.position = bestWorld;
        anchor.transform.parent = wing.transform;

        return anchor.transform;
    }

    void Update()
    {
        if (vessel == null || !vessel.loaded) return;

        Vector3 velocity = vessel.srf_velocity;
        float speed = velocity.magnitude;

        if (speed < 1f)
        {
            DisableAllTrails();
            return;
        }

        Vector3 flow = velocity.normalized;

        float gForce = (float)vessel.geeForce;
        float gCap = Mathf.Min(gForce, 15f); // cap at break point

        float targetIntensity = 0f;

        if (speed >= 60f && gCap >= 3f)
        {
            float gFactor = Mathf.Clamp01((gCap - 3f) / 10f);

            // softer buildup curve
            gFactor = Mathf.Pow(gFactor, 2f);

            targetIntensity = Mathf.Min(gFactor, 0.9f); // prevent overdrive
        }

        bool isSpawning = targetIntensity > 0.02f;

        // Reset system when vortices begin forming
        if (isSpawning && !wasSpawning)
        {
            currentIntensity = 0f;
            smoothedFlow = velocity.normalized;

            for (int i = 0; i < trails.Count; i++)
            {
                trails[i].Clear();
                trailObjs[i].transform.position = anchors[i].position - smoothedFlow * 0.5f;
                trails[i].emitting = false;
                trails[i].enabled = false;
            }
        }

        wasSpawning = isSpawning;

        if (targetIntensity > currentIntensity)
            currentIntensity = Mathf.MoveTowards(currentIntensity, targetIntensity, buildSpeed * Time.deltaTime);
        else
            currentIntensity = Mathf.MoveTowards(currentIntensity, targetIntensity, decaySpeed * Time.deltaTime);

        for (int i = 0; i < trails.Count; i++)
        {
            var tr = trails[i];
            var obj = trailObjs[i];
            var anchor = anchors[i];

            float visible = Mathf.Clamp01(currentIntensity) * strengths[i];

            if (visible <= 0.01f)
            {
                // allow smooth decay instead of instant removal
                tr.emitting = false;
                tr.enabled = true;
                continue;
            }

            // Incremental movement instead of snapping (fixes flow break)
            Vector3 move = flow * speed * Time.deltaTime * 0.5f;

            // Ensure it starts near anchor but then flows naturally
            if (Vector3.Distance(obj.transform.position, anchor.position) > 2.0f)
            {
                obj.transform.position = anchor.position - flow * 0.5f;
            }
            else
            {
                obj.transform.position += move;
            }

            tr.enabled = true;
            tr.emitting = true;
            tr.startWidth = 0.15f * visible;
            tr.time = Mathf.Lerp(0.8f, 3.0f, visible);
            tr.transform.forward = flow;
        }
    }

    void DisableAllTrails()
    {
        for (int i = 0; i < trails.Count; i++)
        {
            trails[i].emitting = false;
            trails[i].enabled = false;
            trails[i].Clear();
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
