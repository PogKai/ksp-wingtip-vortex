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

        ComputeVesselBounds();
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
            if (!initialized) { vesselBounds = pr.bounds; initialized = true; }
            else vesselBounds.Encapsulate(pr.bounds);
        }
        vesselSize = vesselBounds.size.magnitude;
    }

    void FindVortexSources()
    {
        // ═══════════════════════════════════════════════════════════════
        // DIAGNOSTIC — full aero-surface inventory
        // Logs EVERY part that has a lift or control module.
        // If nose canards don't appear here, they have no aero module
        // and we rely on the geometry fallback below.
        // ═══════════════════════════════════════════════════════════════
        Debug.Log("[VORTEX] === AERO INVENTORY ===");
        foreach (Part p in vessel.parts)
        {
            bool hasLift = p.Modules.Contains("ModuleLiftingSurface");
            bool hasCtrl = p.Modules.Contains("ModuleControlSurface");
            if (!hasLift && !hasCtrl) continue;
            Vector3 lp = vesselTransform.InverseTransformPoint(p.transform.position);
            var r = p.GetComponentInChildren<Renderer>();
            float area = r != null ? r.bounds.size.x * r.bounds.size.z : 0f;
            Debug.Log($"[VORTEX] AERO part={p.name} z={lp.z:F2} x={lp.x:F2} area={area:F2} lift={hasLift} ctrl={hasCtrl}");
        }
        Debug.Log("[VORTEX] === END INVENTORY ===");

        // ═══════════════════════════════════════════════════════════════
        // PASS 1 — REAR SYSTEM  (geometry-based, unchanged)
        // ═══════════════════════════════════════════════════════════════
        List<PartScore> rearParts = new List<PartScore>();

        foreach (Part p in vessel.parts)
        {
            bool isLift = p.Modules.Contains("ModuleLiftingSurface");
            bool isControl = p.Modules.Contains("ModuleControlSurface");

            Vector3 local = vesselTransform.InverseTransformPoint(p.transform.position);
            float localZ = local.z;

            bool isElevator = isControl && localZ < -0.5f && Mathf.Abs(local.x) < 2.0f;
            if (isElevator) continue;

            var r = p.GetComponentInChildren<Renderer>();
            if (r == null) continue;

            float area = r.bounds.size.x * r.bounds.size.z;
            if (area < 0.3f) continue;
            if (Mathf.Abs(local.x) < 0.3f) continue;

            float liftValue = 0f;
            if (isLift)
            {
                var lm = p.FindModuleImplementing<ModuleLiftingSurface>();
                if (lm != null) liftValue = lm.deflectionLiftCoeff;
            }
            else if (isControl)
            {
                var cm = p.FindModuleImplementing<ModuleControlSurface>();
                if (cm != null) liftValue = cm.ctrlSurfaceRange;
            }

            rearParts.Add(new PartScore(p, Mathf.Abs(local.x), localZ, liftValue));
        }

        // Absolute lateral extremity per side
        float maxLeftX = 0f;
        float maxRightX = 0f;
        foreach (var part in vessel.parts)
        {
            var r = part.GetComponentInChildren<Renderer>();
            if (r == null) continue;
            float cProj = Vector3.Dot(r.bounds.center - vesselTransform.position, vesselTransform.right);
            float eProj = Mathf.Abs(Vector3.Dot(r.bounds.extents, vesselTransform.right));
            float ext = Mathf.Abs(cProj) + eProj;
            if (cProj < 0f) maxLeftX = Mathf.Max(maxLeftX, ext);
            if (cProj > 0f) maxRightX = Mathf.Max(maxRightX, ext);
        }

        foreach (var ps in rearParts)
        {
            var r = ps.part.GetComponentInChildren<Renderer>();
            if (r == null) continue;
            float cProj = Vector3.Dot(r.bounds.center - vesselTransform.position, vesselTransform.right);
            float eProj = Mathf.Abs(Vector3.Dot(r.bounds.extents, vesselTransform.right));
            Debug.Log($"[VORTEX] CHECK part={ps.part.name} extremity={Mathf.Abs(cProj) + eProj:F2} maxL={maxLeftX:F2} maxR={maxRightX:F2}");
        }

        Part leftRear = null, rightRear = null;
        float bestL = float.MinValue, bestR = float.MinValue;

        foreach (var ps in rearParts)
        {
            var part = ps.part;
            var r = part.GetComponentInChildren<Renderer>();
            if (r == null) continue;
            float area = r.bounds.size.x * r.bounds.size.z;
            if (area < 0.3f) continue;

            float cProj = Vector3.Dot(r.bounds.center - vesselTransform.position, vesselTransform.right);
            float eProj = Mathf.Abs(Vector3.Dot(r.bounds.extents, vesselTransform.right));
            float ext = Mathf.Abs(cProj) + eProj;
            bool isLeft = cProj < 0f;
            bool isRight = cProj > 0f;
            bool inBandL = isLeft && ext >= maxLeftX * 0.85f;
            bool inBandR = isRight && ext >= maxRightX * 0.85f;
            if (!(inBandL || inBandR)) continue;

            float absRef = isLeft ? maxLeftX : maxRightX;
            if (ext < absRef * 0.9f) continue;

            float upDot = Mathf.Abs(Vector3.Dot(part.transform.up, vesselTransform.up));
            float score = ext * (1f - upDot * 0.75f);

            Debug.Log($"[VORTEX] REAR CANDIDATE part={part.name} extremity={ext:F2} centerDist={Mathf.Abs(cProj):F2} score={score:F2}");
            if (inBandL && score > bestL) { bestL = score; leftRear = part; }
            if (inBandR && score > bestR) { bestR = score; rightRear = part; }
        }

        // Symmetry fallback for rear mains
        bool mirrorLeft = false;
        bool mirrorRight = false;

        if (leftRear == null && rightRear != null)
        {
            leftRear = rightRear;
            mirrorLeft = true;
        }
        if (rightRear == null && leftRear != null)
        {
            rightRear = leftRear;
            mirrorRight = true;
        }

        if (leftRear != null)
        {
            var rL = leftRear.GetComponentInChildren<Renderer>();
            Debug.Log($"[VORTEX] FINAL LEFT MAIN part={leftRear.name} distFromCenter={Mathf.Abs(Vector3.Dot(rL.bounds.center - vesselTransform.position, vesselTransform.right)):F2}");
            AddWingVortex(leftRear, false, 1f, mirrorLeft);
        }
        if (rightRear != null)
        {
            var rR = rightRear.GetComponentInChildren<Renderer>();
            Debug.Log($"[VORTEX] FINAL RIGHT MAIN part={rightRear.name} distFromCenter={Mathf.Abs(Vector3.Dot(rR.bounds.center - vesselTransform.position, vesselTransform.right)):F2}");
            AddWingVortex(rightRear, true, 1f, mirrorRight);
        }

        // define main wing reference Z (fallback: 0 if no rear detected)
        float mainWingZ = 0f;
        if (leftRear != null)
            mainWingZ = vesselTransform.InverseTransformPoint(leftRear.transform.position).z;
        else if (rightRear != null)
            mainWingZ = vesselTransform.InverseTransformPoint(rightRear.transform.position).z;

        float maxWidth = Mathf.Max(maxLeftX, maxRightX);
        float forwardThreshold = mainWingZ + 0.3f; // retained for debug, no longer used for hard gating   // must be clearly ahead of main wing
        float spanLimit = maxWidth * 0.6f;    // must not be at the outer wingtip

        // Collect rear part set for exclusion
        HashSet<Part> rearSet = new HashSet<Part>();
        if (leftRear != null) rearSet.Add(leftRear);
        if (rightRear != null) rearSet.Add(rightRear);

        List<PartScore> leftCandidates = new List<PartScore>();
        List<PartScore> rightCandidates = new List<PartScore>();

        // ═══════════════════════════════════════════════════════════════
        // FRONT SYSTEM (original working version restored)
        // ═══════════════════════════════════════════════════════════════

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

            if (local.z > 0f)
                frontParts.Add(score);
        }

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

        // ── VALIDATION: ensure front surfaces are meaningfully ahead of main wing ──
        bool validFront = true;

        if (leftFront != null)
        {
            float z = vesselTransform.InverseTransformPoint(leftFront.transform.position).z;
            if (z - mainWingZ < 0.2f) validFront = false;
        }
        if (rightFront != null)
        {
            float z = vesselTransform.InverseTransformPoint(rightFront.transform.position).z;
            if (z - mainWingZ < 0.2f) validFront = false;
        }

        if (!validFront)
        {
            Debug.Log("[VORTEX] FRONT DISABLED — no sufficiently forward surfaces");
            return;
        }

        if (leftFront != null)
        {
            Debug.Log($"[VORTEX] FINAL FRONT LEFT part={leftFront.name}");
            AddWingVortex(leftFront, false, 0.4f);
        }

        if (rightFront != null)
        {
            Debug.Log($"[VORTEX] FINAL FRONT RIGHT part={rightFront.name}");
            AddWingVortex(rightFront, true, 0.4f);
        }
    }

    void AddWingVortex(Part wing, bool isRight, float strength, bool mirror = false)
    {
        // If this side is mirrored from the opposite wing, first sample the real tip on the source wing,
        // then mirror the world position across the vessel centerline.
        Transform anchor = CreateWingtipAnchor(wing, mirror ? !isRight : isRight);
        if (anchor == null) return;

        if (mirror)
        {
            Vector3 local = vesselTransform.InverseTransformPoint(anchor.position);
            local.x *= -1f;
            anchor.position = vesselTransform.TransformPoint(local);
        }

        Renderer r = wing.GetComponentInChildren<Renderer>();
        float area = (r != null) ? r.bounds.size.x * r.bounds.size.z : 1f;

        GameObject obj = new GameObject("Vortex");
        // CRITICAL: do NOT parent to vessel — prevents trail inheriting vessel motion
        obj.transform.parent = null;

        TrailRenderer tr = obj.AddComponent<TrailRenderer>();
        tr.time = 1.5f; tr.startWidth = 0.15f; tr.endWidth = 0.03f;
        tr.sharedMaterial = sharedTrailMat; tr.enabled = false;
        // NOTE: KSP's Unity version does not support useWorldSpace on TrailRenderer
        // Detaching from vessel (no parent) already ensures world-space behavior

        LineRenderer lr = obj.AddComponent<LineRenderer>();
        lr.positionCount = historyLength; lr.startWidth = 0.1f; lr.endWidth = 0.02f;
        lr.sharedMaterial = sharedLineMat; lr.enabled = false;

        trailObjs.Add(obj); trails.Add(tr); lines.Add(lr);
        anchors.Add(anchor); strengths.Add(strength); areas.Add(area);
        lineHistory.Add(new Queue<Vector3>());
        trailWasActive.Add(false); lineWasActive.Add(false);
        lastFlows.Add(Vector3.zero); trailPositions.Add(anchor.position);
    }

    Transform CreateWingtipAnchor(Part wing, bool isRight)
    {
        MeshFilter mf = wing.GetComponentInChildren<MeshFilter>();
        if (mf == null) return null;

        Vector3 best = mf.mesh.vertices[0];
        float bestScore = float.MinValue;

        // STEP 1: find TRUE tip (lateral + forward bias to avoid root snapping)
        foreach (var v in mf.mesh.vertices)
        {
            Vector3 world = mf.transform.TransformPoint(v);
            Vector3 localVessel = vesselTransform.InverseTransformPoint(world);

            float lateral = localVessel.x * (isRight ? 1f : -1f);
            float forward = localVessel.z;

            // reject near-center vertices (prevents root selection)
            if (Mathf.Abs(localVessel.x) < 0.05f) continue;

            // bias outward AND slightly forward
            float score = lateral + forward * 0.25f;

            if (score > bestScore)
            {
                bestScore = score;
                best = v;
            }
        }

        Vector3 anchorWorld = mf.transform.TransformPoint(best);

        // STEP 2: climb upward if another part exists above this X/Z position
        // SKIP climb if this is already an extremity wing (prevents rear shifting forward)
        {
            var rCheck = wing.GetComponentInChildren<Renderer>();
            if (rCheck != null)
            {
                float maxExt = 0f;
                foreach (var part in vessel.parts)
                {
                    var rr = part.GetComponentInChildren<Renderer>();
                    if (rr == null) continue;
                    float cProj = Vector3.Dot(rr.bounds.center - vesselTransform.position, vesselTransform.right);
                    float eProj = Mathf.Abs(Vector3.Dot(rr.bounds.extents, vesselTransform.right));
                    float ext = Mathf.Abs(cProj) + eProj;
                    maxExt = Mathf.Max(maxExt, ext);
                }

                float thisC = Vector3.Dot(rCheck.bounds.center - vesselTransform.position, vesselTransform.right);
                float thisE = Mathf.Abs(Vector3.Dot(rCheck.bounds.extents, vesselTransform.right));
                float thisExt = Mathf.Abs(thisC) + thisE;

                if (thisExt >= maxExt * 0.95f)
                {
                    // already true wingtip → do NOT climb
                    GameObject anchorNoClimb = new GameObject("Anchor");
                    anchorNoClimb.transform.position = anchorWorld;
                    anchorNoClimb.transform.parent = wing.transform;
                    return anchorNoClimb.transform;
                }
            }
        }
        Part currentPart = wing;
        bool foundHigher = true;
        int safety = 0;

        while (foundHigher && safety < 10)
        {
            foundHigher = false;
            safety++;

            Vector3 baseLocal = vesselTransform.InverseTransformPoint(anchorWorld);
            Debug.Log($"[VORTEX] CLIMB CHECK iter={safety} baseY={baseLocal.y:F2} part={currentPart.name}");

            Part bestAbovePart = null;
            float bestHorizDist = float.MaxValue;
            float bestTopY = float.MinValue;
            float bestArea = 0f;

            foreach (Part p in vessel.parts)
            {
                bool isLift = p.Modules.Contains("ModuleLiftingSurface");
                bool isControl = p.Modules.Contains("ModuleControlSurface");

                // HARD FILTER: only real lifting surfaces (this is the missing piece)
                if (!isLift) continue;

                var r = p.GetComponentInChildren<Renderer>();
                if (r == null) continue;

                float sizeX = r.bounds.size.x;
                float sizeY = r.bounds.size.y;
                float sizeZ = r.bounds.size.z;
                float area = sizeX * sizeZ;

                // remove tiny junk (more aggressive to kill small control surfaces)
                if (area < 0.10f) continue; // allow smaller structural wings

                // RELAXED: allow structural wings that may not be perfectly "flat"
                // Only reject obviously non-wing shapes

                // reject very tall/thick parts (fuselage/intakes)
                if (sizeY > Mathf.Max(sizeX, sizeZ)) continue;

                // allow wider range of orientations (canted wings)
                float upDot = Mathf.Abs(Vector3.Dot(p.transform.up, vesselTransform.up));
                if (upDot < 0.2f) continue; // only reject extreme verticals
                if (r == null) continue;

                Bounds b = r.bounds;
                Vector3 minLocal = vesselTransform.InverseTransformPoint(b.min);
                Vector3 maxLocal = vesselTransform.InverseTransformPoint(b.max);
                Vector3 centerLocal = vesselTransform.InverseTransformPoint(b.center);

                // RELAX: use part center height instead of bounds max (handles rotated/canted meshes)
                Vector3 currCenterLocal = vesselTransform.InverseTransformPoint(currentPart.transform.position);
                if (centerLocal.y <= currCenterLocal.y + 0.01f) continue;

                // horizontal distance from current X/Z column to this part's X/Z bounds
                float dx = 0f;
                if (baseLocal.x < minLocal.x) dx = minLocal.x - baseLocal.x;
                else if (baseLocal.x > maxLocal.x) dx = baseLocal.x - maxLocal.x;

                // Lateral column distance only (ignore Z so we can climb stacks that are slightly fore/aft)
                float horizDist = Mathf.Abs(centerLocal.x - baseLocal.x);

                // ignore parts that are too far away horizontally
                if (horizDist > 4.5f) continue; // allow wider search for offset stacks // expand search radius for offset canards

                // Prefer TRUE wing surface: highest + widest + aligned
                float lateralOffset = Mathf.Abs(centerLocal.x - baseLocal.x);
                float widthScore = sizeX; // wider surface preferred (structuralWing > connector chain)

                if (bestAbovePart == null ||
                    // strong preference: wider AND higher
                    (widthScore > bestArea * 1.1f && centerLocal.y > bestTopY - 0.1f) ||
                    // otherwise: pick highest among similar width
                    (Mathf.Abs(widthScore - bestArea) < 0.1f && centerLocal.y > bestTopY + 0.05f) ||
                    // fallback: closest lateral alignment
                    lateralOffset < bestHorizDist * 0.7f)
                {
                    bestArea = widthScore;
                    bestHorizDist = lateralOffset;
                    bestTopY = centerLocal.y;
                    bestAbovePart = p;
                }

                Debug.Log($"[VORTEX] CLIMB CAND part={p.name} horiz={horizDist:F2} topY={maxLocal.y:F2} centerY={centerLocal.y:F2}");
            }

            if (bestAbovePart != null)
            {
                var r = bestAbovePart.GetComponentInChildren<Renderer>();
                Bounds b = r.bounds;
                Vector3 maxLocal = vesselTransform.InverseTransformPoint(b.max);

                Debug.Log($"[VORTEX] CLIMB HIT part={bestAbovePart.name} horiz={bestHorizDist:F2} topY={maxLocal.y:F2}");

                currentPart = bestAbovePart;
                // FIX: don't snap to absolute top (causes hovering above surface)
                // instead project slightly BELOW top to sit on surface
                float surfaceY = maxLocal.y - 0.02f; // small offset downward
                Vector3 nextLocal = new Vector3(baseLocal.x, surfaceY, baseLocal.z);
                anchorWorld = vesselTransform.TransformPoint(nextLocal);
                foundHigher = true;
            }
            else
            {
                Debug.Log($"[VORTEX] CLIMB STOP at part={currentPart.name}");
            }
        }

        // STEP 3: once at highest part, recompute lateral extremity on THAT part
        var finalMF = currentPart.GetComponentInChildren<MeshFilter>();
        if (finalMF != null)
        {
            Vector3 bestFinal = finalMF.mesh.vertices[0];
            float bestScoreFinal = float.MinValue;

            foreach (var v in finalMF.mesh.vertices)
            {
                Vector3 world = finalMF.transform.TransformPoint(v);
                Vector3 localVessel = vesselTransform.InverseTransformPoint(world);

                float lateral = localVessel.x * (isRight ? 1f : -1f);
                float forward = localVessel.z;

                if (Mathf.Abs(localVessel.x) < 0.05f) continue;

                float score = lateral + forward * 0.25f;

                if (score > bestScoreFinal)
                {
                    bestScoreFinal = score;
                    bestFinal = v;
                }
            }

            anchorWorld = finalMF.transform.TransformPoint(bestFinal);

            // FINAL FIX: pull anchor slightly inward toward surface to avoid floating above thin wings
            Vector3 inward = -vesselTransform.up * 0.02f;
            anchorWorld += inward;
        }

        GameObject anchor = new GameObject("Anchor");
        anchor.transform.position = anchorWorld;
        anchor.transform.parent = currentPart.transform;
        return anchor.transform;
    }

    void Update()
    {
        if (vessel == null || !vessel.loaded) return;

        float rawDelta = Time.deltaTime;
        float dt = Mathf.Min(rawDelta, maxDelta);
        float smoothDt = Mathf.Clamp(Time.smoothDeltaTime, 0.001f, maxDelta);

        // HARD FRAME PROTECTION
        bool badFrame = rawDelta > abnormalFrameThreshold;

        if (!Application.isFocused)
        {
            for (int i = 0; i < trails.Count; i++)
                ResetVortexState(i);
            return;
        }

        // IMPORTANT: do NOT reset on bad frames — this causes snapping/explosions
        // Instead, we degrade update quality but keep state intact
        if (badFrame)
        {
            dt = 0.01f; // clamp aggressively

            // CRITICAL: clear trail history to prevent forward spike artifact
            for (int i = 0; i < trails.Count; i++)
            {
                trails[i].Clear();
            }
        }

        // RECOVERY: ensure renderers come back clean after a bad frame
        for (int i = 0; i < trails.Count; i++)
        {
            if (!trails[i].enabled) trails[i].enabled = true;
            if (!lines[i].enabled && useLineMode) lines[i].enabled = true;
        }

        float speed = vessel.GetSrfVelocity().magnitude;

        boundsRefreshTimer += dt;
        if (boundsRefreshTimer >= boundsRefreshInterval)
        {
            ComputeVesselBounds();
            boundsRefreshTimer = 0f;
        }

        if (vessel.atmDensity <= 0.01f)
        {
            for (int i = 0; i < trails.Count; i++)
            {
                trails[i].emitting = false;
                trails[i].time = Mathf.Lerp(trails[i].time, 0f, dt * 0.5f);
                if (lines[i].enabled) { lines[i].startWidth *= 0.98f; lines[i].endWidth *= 0.98f; }
            }
            return;
        }

        float altitude = (float)vessel.altitude;
        float g = Mathf.Min((float)vessel.geeForce, 15f);
        Vector3 flow = vessel.GetSrfVelocity().normalized;

        float targetIntensity = Mathf.Clamp01((g - 3f) / 10f);
        currentIntensity = (targetIntensity > currentIntensity)
            ? Mathf.MoveTowards(currentIntensity, targetIntensity, buildSpeed * dt)
            : Mathf.MoveTowards(currentIntensity, targetIntensity, decaySpeed * dt);

        float intensity = currentIntensity;

        bool shouldEnterLineMode = altitude >= 8000f && altitude <= 15000f && vessel.atmDensity > 0.01f && speed >= 180f;
        bool shouldExitLineMode = speed <= 165f && altitude <= 4000f && g <= 3.1f;

        if (speed >= 250f) { useLineMode = true; }
        else if (!useLineMode && shouldEnterLineMode) { useLineMode = true; for (int h = 0; h < lineHistory.Count; h++) lineHistory[h].Clear(); }
        else if (useLineMode && shouldExitLineMode) { useLineMode = false; for (int h = 0; h < lineHistory.Count; h++) lineHistory[h].Clear(); }

        lineActivationBlend = Mathf.MoveTowards(lineActivationBlend, useLineMode ? 1f : 0f, lineActivationSpeed * dt);
        float trailFade = 1f - lineActivationBlend;
        float lineFade = lineActivationBlend;

        for (int i = 0; i < trails.Count; i++)
        {
            var tr = trails[i];
            var lr = lines[i];
            var obj = trailObjs[i];
            var anchor = anchors[i];
            var history = lineHistory[i];

            float visible = intensity * strengths[i];

            float lightFactor = Mathf.Clamp01((float)(vessel.solarFlux / 2000f));
            visible *= Mathf.Lerp(0.2f, 1f, lightFactor);

            float atmFactor = Mathf.Clamp01((float)((vessel.atmDensity - 0.01f) / 0.09f));
            visible *= atmFactor;

            if (altitude >= 8000f && altitude <= 15000f)
            {
                float altBlend = Mathf.Clamp01((altitude - 8000f) / 7000f);
                visible = Mathf.Max(visible, Mathf.Lerp(0.1f, 0.5f, altBlend));
                visible *= Mathf.Lerp(1f, 1.5f, altBlend);
            }
            else if (altitude > 15000f) visible = 0f;

            if (strengths[i] < 1f)
            {
                if (altitude < 8000f)
                    visible *= Mathf.Clamp01((g - 4.5f) / 1.0f);
                else
                    visible *= Mathf.Max(Mathf.Clamp01((g - 3.5f) / 1.5f), Mathf.Clamp01((220f - speed) / 40f));
            }

            // STABILITY: clamp history growth on unstable frames
            history.Enqueue(anchor.position);
            while (history.Count > historyLength)
                history.Dequeue();

            // FIX: always use true anchor position (no smoothing — preserves flow direction)
            obj.transform.position = anchor.position;
            lastFlows[i] = flow;

            bool shouldUseLine = lineFade > 0.01f && visible > 0.01f;
            bool shouldUseTrail = trailFade > 0.01f && visible > 0.01f && altitude <= 5000f;

            // ── TRAIL ─────────────────────────────────────────────────
            if (shouldUseTrail)
            {
                if (lineWasActive[i])
                {
                    lr.enabled = false; lr.positionCount = historyLength;
                    history.Clear(); tr.Clear(); tr.time = 1.5f;
                }
                tr.enabled = true; tr.emitting = true;
                // FRAME-ROBUST TRAIL SETTINGS
                float stability = Mathf.Clamp01((abnormalFrameThreshold - rawDelta) / abnormalFrameThreshold);
                float targetTime = Mathf.Lerp(0.25f, 0.7f, stability); // shorter trails on bad frames
                tr.time = Mathf.Lerp(tr.time, targetTime, smoothDt * 2f);

                float targetMinDist = Mathf.Lerp(0.35f, 0.12f, stability); // fewer verts on bad frames
                tr.minVertexDistance = Mathf.Lerp(tr.minVertexDistance, targetMinDist, smoothDt * 3f);
                float sizeNormT = Mathf.Clamp(vesselSize / 30f, 0.2f, 1f);
                float ss = 1f + Mathf.Pow(sizeNormT, 2.2f) * 2.0f;
                tr.startWidth = 0.2f * visible * trailFade * ss;
                tr.endWidth = 0.05f * visible * trailFade * ss;

                // IMPORTANT: never fully stop emission during bad frames (causes forward shooting bug)
                tr.emitting = true;

                // DAY/NIGHT VISIBILITY (match line behavior)
                float trailLightFactor = Mathf.Clamp01((float)(vessel.solarFlux / 2000f));
                // increase night visibility floor
                // FIX: visible already includes lighting, do NOT double-darken
                float trailAlpha = Mathf.Max(visible, 0.4f);

                Gradient tGrad = new Gradient();
                tGrad.SetKeys(
                    new GradientColorKey[] {
                        new GradientColorKey(Color.white, 0f),
                        new GradientColorKey(new Color(0.85f,0.85f,0.85f), 1f)
                    },
                    new GradientAlphaKey[] {
                        new GradientAlphaKey(trailAlpha, 0f),
                        new GradientAlphaKey(trailAlpha * 0.5f, 0.5f),
                        new GradientAlphaKey(0f, 1f)
                    }
                );
                tr.colorGradient = tGrad;

                trailWasActive[i] = true; lineWasActive[i] = false;
            }
            else { tr.emitting = false; }

            // ── LINE ──────────────────────────────────────────────────
            if (shouldUseLine)
            {
                lr.enabled = true;
                if (trailWasActive[i])
                {
                    tr.Clear(); tr.emitting = false; tr.enabled = false;
                    history.Clear(); lr.positionCount = historyLength;
                }

                float altFactor = Mathf.Clamp01((altitude - 8000f) / 7000f);
                if (altitude > 15000f) altFactor = 0f;
                int dynLen = Mathf.RoundToInt(Mathf.Lerp(historyLength, historyLength * 2.5f, altFactor));
                if (lr.positionCount != dynLen) lr.positionCount = dynLen;

                Vector3 perp = Vector3.Cross(flow, vessel.transform.up).normalized;
                float sideSign = Mathf.Sign(vessel.transform.InverseTransformPoint(anchor.position).x);
                float cruiseFactor = Mathf.Clamp01((altitude - 8000f) / 8000f);
                float stressFactor = Mathf.Clamp01((g - 4f) / 6f + (speed - 200f) / 200f);
                float lengthScale = Mathf.Lerp(1.0f, 4.0f, Mathf.Clamp01(speed / 300f)) * Mathf.Lerp(1f, 0.4f, stressFactor);

                for (int idx = 0; idx < dynLen; idx++)
                {
                    float falloff = dynLen > 1 ? (float)idx / (dynLen - 1) : 0f;
                    float spacing = Mathf.Min(Mathf.Pow(idx, 1.05f), idx * 1.2f);
                    Vector3 offset = -flow * (spacing * 1.5f * lengthScale);
                    Vector3 downwash = -vessel.transform.up * (idx * 0.02f * 0.2f * visible * falloff);
                    Vector3 curved = perp * (0.3f * visible * idx * 0.15f * falloff * sideSign);
                    float wAmp = Mathf.Lerp(0.2f, 1.2f, Mathf.Clamp01(speed / 250f)) * visible * Mathf.Lerp(1f, 0.3f, stressFactor);
                    Vector3 warp = perp * (Mathf.Sin(idx * 0.4f + Time.time * Mathf.Lerp(0.5f, 1.5f, cruiseFactor)) * wAmp * falloff);
                    Vector3 flowR = Vector3.Cross(vessel.transform.up, flow).normalized;
                    Vector3 drift = flowR * (Mathf.Sin(Time.time * 0.6f + idx * 0.25f) * 0.15f * visible * idx * falloff);
                    lr.SetPosition(idx, anchor.position + offset + curved + warp + downwash + drift);
                }

                float lineScale = 1f;
                if (altitude >= 8000f && altitude <= 15000f)
                {
                    float ab = Mathf.Clamp01((altitude - 8000f) / 7000f);
                    lineScale = Mathf.Lerp(1.5f, 3f, ab);
                }
                float sizeNorm = Mathf.Clamp(vesselSize / 30f, 0.2f, 1f);
                // FIX: prevent excessive scaling during high-speed reentry
                float speedFactor = Mathf.Clamp01(speed / 600f); // normalize high-speed regime
                float reentryClamp = Mathf.Lerp(1f, 0.5f, speedFactor); // shrink at extreme speeds

                float finalScale = lineScale
                                   * Mathf.Lerp(0.6f, 1.2f, sizeNorm)
                                   * (1f + Mathf.Pow(sizeNorm, 2.2f) * 2.0f)
                                   * reentryClamp;

                lr.startWidth = 0.15f * visible * lineFade * strengths[i] * finalScale;
                lr.endWidth = 0.03f * visible * lineFade * strengths[i] * finalScale;

                Gradient grad = new Gradient();

                // SPEED-BASED OPACITY (high altitude behavior)
                float highSpeedFactor = Mathf.Clamp01((speed - 1000f) / 500f); // begins at 1000 m/s
                float alphaBoost = Mathf.Lerp(1f, 1.8f, highSpeedFactor); // stronger / more solid at high speed

                float baseAlpha = visible * strengths[i];
                float finalAlpha = baseAlpha * alphaBoost;

                grad.SetKeys(
                    new GradientColorKey[] {
                        new GradientColorKey(Color.white,                0f),
                        new GradientColorKey(new Color(0.8f,0.8f,0.8f), 0.5f),
                        new GradientColorKey(new Color(0.6f,0.6f,0.6f), 1f)
                    },
                    new GradientAlphaKey[] {
                        new GradientAlphaKey(finalAlpha,        0f),
                        new GradientAlphaKey(finalAlpha * 0.7f, 0.5f),
                        new GradientAlphaKey(0f,                1f)
                    }
                );
                lr.colorGradient = grad;
                lineWasActive[i] = true; trailWasActive[i] = false;
            }
            else { lr.enabled = false; lineWasActive[i] = false; }
        }
    }

    void ResetVortexState(int i)
    {
        trails[i].Clear(); trails[i].time = 1.5f; trails[i].emitting = false;
        lines[i].positionCount = historyLength; lines[i].enabled = false;
        lineHistory[i].Clear();
        trailWasActive[i] = false; lineWasActive[i] = false;
    }

    private class PartScore
    {
        public Part part;
        public float width, forward, lift;
        public PartScore(Part p, float w, float f, float l) { part = p; width = w; forward = f; lift = l; }
    }
}