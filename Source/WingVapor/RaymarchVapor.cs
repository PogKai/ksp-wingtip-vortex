using System;
using System.Collections.Generic;
using UnityEngine;
using VortexVapor.Aeronautic;

namespace VortexVapor
{
    // The raymarched wing vapor on one vessel: the KSP side feeding VaporDriver.
    public class RaymarchVapor
    {
        // The active vessel's: only it writes the shared readings below (other craft: OtherCraftVapor).
        public bool Primary = true;
        // A lifting part this thick for its planform is a body (a Mk2 fuselage), not a wing.
        const float BodyThickness = 0.35f;

        VaporDriver driver;
        Vessel vessel;
        int partCount = -1;
        uint partSignature;
        bool built;
        bool measuredGrounded;
        bool groundFrameFlipped;
        Quaternion frameLocal;   // the canonical frame in the root part's frame
        readonly List<ModuleLiftingSurface> liftSurfaces = new List<ModuleLiftingSurface>();
        // wing outline points (part-local) and where they sat when measured, for the flex.
        readonly List<Part> flexParts = new List<Part>();
        readonly List<Vector3> flexLocal = new List<Vector3>(), flexRest = new List<Vector3>();
        Vector2 wingFlex;
        Vector3 flexShift, flexTurn;
        // Limit adaptive coarsening when the craft fills the screen.
        const float GameStepScaleCap = 1.5f;
        // flight IDs of the parts under the canard's sheet, for the puffs.
        public static readonly HashSet<uint> CanardParts = new HashSet<uint>();
        bool hooked, camerasLogged;
        float nextLog;

        // What LateUpdate asked for this frame; drawn when the flight camera is about to render.
        int requestedFrame = -1, steppedFrame = -1;
        float daylight, opacity, reachScale, lastStepTime = -1f;

        public bool Drawing { get; private set; }
        public static bool Available { get { return VaporShaders.Raymarch != null; } }

        // Angle of attack in the measured craft frame; zero before measurement or on the ground.
        public double AngleOfAttack(Vessel v)
        {
            if (!built || v != vessel || v.LandedOrSplashed) return 0;
            Vector3 velocity = v.GetSrfVelocity();
            if (velocity.sqrMagnitude < 1f) return 0;
            Quaternion attitude = v.rootPart.transform.rotation * frameLocal;
            Vector3 flow = -velocity.normalized;
            return Math.Atan2(Vector3.Dot(flow, attitude * Vector3.up), Vector3.Dot(flow, attitude * Vector3.right));
        }

        public void Prepare(Vessel v)
        {
            if (!Available || v == null || v.rootPart == null) return;
            if (v != vessel || v.parts.Count != partCount || PartSignature(v) != partSignature) Rebuild(v);
            else if (built && PoseMoved()) Remeasure(v);
            else if (measuredGrounded && !v.LandedOrSplashed && v.srfSpeed >= 30)
            {
                if (groundFrameFlipped) Rebuild(v);
                else
                {
                    measuredGrounded = false;
                    Debug.Log("[VORTEX] wing vapor: takeoff reused unchanged airframe for " + v.vesselName);
                }
            }
        }

        // The part set by flight ID: a part swapped for another (undock, dock) keeps the count.
        static uint PartSignature(Vessel v)
        {
            uint sum = 0;
            for (int i = 0; i < v.parts.Count; i++) sum += v.parts[i].flightID * 2654435761u;
            return sum;
        }

        // The wing outlines sit further from the measured pose than the flex fit explains
        // (a swing wing, a broken joint) for PoseHoldS; PoseMinGapS apart at most.
        const float PoseDriftM = 0.3f, PoseHoldS = 0.25f, PoseMinGapS = 0.5f;
        // Off only in the dev tools, to show what a swing wing did before the fix.
        public static bool SwingFix = true;
        // How far the wing outlines sit from the measured pose now (dev logs).
        public static float PoseDriftNow;
        float poseResidual, poseDriftSince = -1f, lastMeasureTime = -10f;

        bool PoseMoved()
        {
            PoseDriftNow = poseResidual;
            if (!SwingFix || poseResidual < PoseDriftM) { poseDriftSince = -1f; return false; }
            float now = Time.time;
            if (poseDriftSince < 0f) poseDriftSince = now;
            return now - poseDriftSince >= PoseHoldS && now - lastMeasureTime >= PoseMinGapS;
        }

        // Measure again in place: the history and the load filters stay.
        // Where a measurement's time went (verbose log): outlines, body, airframe, cover mesh, finish.
        readonly double[] stageMs = new double[5];
        static double Lap(System.Diagnostics.Stopwatch w) { double ms = w.Elapsed.TotalMilliseconds; w.Restart(); return ms; }

        static string lastSideReach = "";
        double loggedSideKeep;

        // One side's lifting outlines reach under this share of the other's: its wing is gone.
        const float LostSideShare = 0.45f;
        // After a loss the short side must also keep this share of the reach ratio the craft first had, so a stump is not a wing.
        const float StumpShare = 0.85f;
        Vessel ratioVessel;
        double firstRatio = -1, pendingSide = double.NaN;

        // 0 with a wing each side, else the sign (in z) of the side that still has one.
        // The tail is left out: outlines centred in the craft's aft quarter (x runs nose to tail) are stabilisers, not the wing.
        double LostSide(List<SurfacePolygon> surfaces, double noseX, double tailX)
        {
            double plus = 0, minus = 0, aftFrom = noseX + 0.75 * (tailX - noseX);
            foreach (SurfacePolygon s in surfaces)
            {
                double centre = 0;
                for (int i = 0; i < s.X.Length; i++) centre += s.X[i] / s.X.Length;
                if (centre > aftFrom) continue;
                for (int i = 0; i < s.Z.Length; i++) { plus = Math.Max(plus, s.Z[i]); minus = Math.Max(minus, -s.Z[i]); }
            }
            lastSideReach = plus.ToString("F1") + "/" + minus.ToString("F1");
            double hi = Math.Max(plus, minus), ratio = hi > 0 ? Math.Min(plus, minus) / hi : 1;
            if (firstRatio < 0) firstRatio = ratio;
            if (ratio >= LostSideShare && ratio >= StumpShare * firstRatio) return 0;
            return minus < plus ? 1 : -1;
        }

        // True while Measure runs from Remeasure (the dev recorder keeps only the first measurement).
        bool remeasuring;

        void Remeasure(Vessel v)
        {
            float moved = poseResidual;
            var timing = System.Diagnostics.Stopwatch.StartNew();
            remeasuring = true;
            try { built = Measure(v); }
            catch (Exception e) { built = false; Debug.Log("[VORTEX] wing vapor: raymarch remeasure failed: " + e.Message); }
            finally { remeasuring = false; }
            if (WingVaporAddon.Verbose)
                Debug.Log($"[VORTEX] wing vapor: wings moved {moved:F2} m off the measured pose, airframe again in {timing.Elapsed.TotalMilliseconds:F1} ms (outlines {stageMs[0]:F0}, body {stageMs[1]:F0}, airframe {stageMs[2]:F0}, cover {stageMs[3]:F0}, finish {stageMs[4]:F0}) for {v.vesselName}");
        }

        // A piece torn off a craft: debris, or a chunk with nothing to fly it (no command part).
        // Off only in the dev tools, to show what a torn-off piece drew before.
        public static bool HideBrokenOff = true;

        public static bool BrokenOff(Vessel v)
        {
            if (!HideBrokenOff) return false;
            switch (v.vesselType)
            {
                case VesselType.Debris:
                case VesselType.DroppedPart:
                case VesselType.EVA:
                case VesselType.Flag:
                case VesselType.SpaceObject:
                case VesselType.Unknown:
                    return true;
            }
            for (int i = 0; i < v.parts.Count; i++)
                if (v.parts[i] != null && v.parts[i].Modules.Contains("ModuleCommand")) return false;
            return true;
        }

        public void Request(Vessel v, float daylight, float opacity, float reachScale)
        {
            Drawing = false;
            if (!Available || v == null || v.rootPart == null || BrokenOff(v)) return;
            Prepare(v);
            if (!built) return;
            this.daylight = daylight;
            this.opacity = opacity;
            this.reachScale = reachScale;
            requestedFrame = Time.frameCount;
            if (!hooked)
            {
                Camera.onPreCull += OnPreCull;
                hooked = true;
            }
        }

        // Upstream works the frame out in onBeforeRender, once every transform is final: here,
        // when the flight camera is about to cull.
        void OnPreCull(Camera cam)
        {
            if (requestedFrame != Time.frameCount || vessel == null || vessel.rootPart == null) return;
            Camera flight = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (cam != flight) return;
            if (steppedFrame == Time.frameCount) return;
            steppedFrame = Time.frameCount;
            VaporFrameInput input = Inputs();
            // Ultra only: fewer steps run out on long rays through the box (nose-on views).
            driver.MaxSteps = VaporQuality.MaxSteps(VaporQuality.Ultra);
            // The dedicated wake renderer owns tip vortices; this volume draws wing vapor only.
            driver.Effects.TipVortices = false;
            driver.Effects.Cone = false;
            driver.Effects.Wing = true;
            driver.UpstreamMapping = false;
            driver.Humidity = KspBaseline.Humidity;
            driver.OffsetK = KspBaseline.OffsetK;
            driver.Step(input);
            if (driver.Visible)
            {
                // The march stops at the craft's own surfaces, read from the camera's depth texture.
                if ((cam.depthTextureMode & DepthTextureMode.Depth) == 0) cam.depthTextureMode |= DepthTextureMode.Depth;
                VaporMaterial.StepScaleCap = GameStepScaleCap;
                driver.Draw(cam, vessel.rootPart.gameObject.layer);
                Drawing = true;
                Log(cam);
            }
        }

        Vector3 SunTint() { return Sunlight.Tint(SunElevation()); }

        float SunElevation()
        {
            float elevation;
            Sunlight.Fraction(vessel, out elevation);
            return elevation;
        }

        // What the vessel did this frame, for the driver.
        VaporFrameInput Inputs()
        {
            Transform root = vessel.rootPart.transform;
            Quaternion attitude = root.rotation * frameLocal;
            Vector3 toSun = Vector3.up;
            CelestialBody star = Planetarium.fetch != null ? Planetarium.fetch.Sun : null;
            if (star != null) toSun = ((Vector3)(star.position - vessel.CoMD)).normalized;
            MeasureFlex(root.position, attitude);
            Quaternion turn = flexTurn == Vector3.zero ? Quaternion.identity : Quaternion.AngleAxis(flexTurn.magnitude * Mathf.Rad2Deg, flexTurn);
            return new VaporFrameInput
            {
                Position = root.position + attitude * flexShift,
                Attitude = attitude * turn,
                Velocity = vessel.GetSrfVelocity(),
                RawLoadG = MeasureLoad(attitude * Vector3.up),
                AltitudeM = vessel.altitude,
                SunDirection = toSun,
                Daylight = daylight,
                SunTint = SunTint(),
                SkyScale = Sunlight.Slant(SunElevation()),
                Opacity = opacity,
                ReachScale = reachScale,
                Dt = StepDelta(),
                Grounded = vessel.LandedOrSplashed,
                GearDown = vessel.ActionGroups[KSPActionGroup.Gear],
                WingFlex = wingFlex,
            };
        }

        // Game seconds since the last step: frames the vapor was not asked for read as one long
        // frame, which the trails' history splits up, or past two seconds starts again from.
        float StepDelta()
        {
            float now = Time.time, dt = lastStepTime < 0 ? Time.deltaTime : now - lastStepTime;
            lastStepTime = now;
            return dt;
        }

        // the lifting surfaces' flex since they were measured, fitted at once as a small
        // shift and turn of the whole (a bending fuselage) and a symmetric rise (x + y s) s at s m
        // outboard of the root (a bending wing); eased, and nothing under a centimetre.
        const float FlexEaseS = 0.08f, FlexMinM = 0.01f;
        readonly double[,] flexNormal = new double[8, 8];
        readonly double[] flexRight = new double[8], flexRow = new double[8], flexFit = new double[8];
        void MeasureFlex(Vector3 origin, Quaternion attitude)
        {
            WingShape shape = driver != null ? driver.Shape : null;
            if (shape == null || flexParts.Count < 8 || vessel.LandedOrSplashed) { wingFlex = Vector2.zero; flexShift = flexTurn = Vector3.zero; return; }
            Quaternion toCanonical = Quaternion.Inverse(attitude);
            Vector3 centre = Vector3.zero;
            for (int i = 0; i < flexRest.Count; i++) centre += flexRest[i];
            centre /= flexRest.Count;
            Array.Clear(flexNormal, 0, 64);
            Array.Clear(flexRight, 0, 8);
            for (int i = 0; i < flexParts.Count; i++)
            {
                if (flexParts[i] == null) continue;
                Vector3 rest = flexRest[i], r = rest - centre;
                Vector3 moved = toCanonical * (flexParts[i].transform.TransformPoint(flexLocal[i]) - origin) - rest;
                double span = Math.Max(Math.Abs(rest.z) - shape.RootSpanM, 0);
                for (int c = 0; c < 3; c++)
                {
                    Array.Clear(flexRow, 0, 8);
                    flexRow[c] = 1;
                    // The turn's share, (w x r)[c].
                    if (c == 0) { flexRow[4] = r.z; flexRow[5] = -r.y; }
                    else if (c == 1) { flexRow[3] = -r.z; flexRow[5] = r.x; flexRow[6] = span; flexRow[7] = span * span; }
                    else { flexRow[3] = r.y; flexRow[4] = -r.x; }
                    for (int a = 0; a < 8; a++)
                    {
                        if (flexRow[a] == 0) continue;
                        flexRight[a] += flexRow[a] * moved[c];
                        for (int b = 0; b < 8; b++) flexNormal[a, b] += flexRow[a] * flexRow[b];
                    }
                }
            }
            if (!Solve(flexNormal, flexRight, flexFit)) return;
            poseResidual = FlexResidual(toCanonical, origin, centre);
            Vector3 turn = new Vector3((float)flexFit[3], (float)flexFit[4], (float)flexFit[5]);
            // The turn is about the points' centre; as a turn about the frame's origin, the shift absorbs it.
            Vector3 shift = new Vector3((float)flexFit[0], (float)flexFit[1], (float)flexFit[2]) - Vector3.Cross(turn, centre);
            Vector2 bend = new Vector2((float)flexFit[6], (float)flexFit[7]);
            float tip = (float)Math.Max(shape.SemispanM - shape.RootSpanM, 0), reach = Mathf.Max(tip + (float)shape.RootSpanM, 1f);
            if (Mathf.Abs((bend.x + bend.y * tip) * tip) < FlexMinM) bend = Vector2.zero;
            if (shift.magnitude < FlexMinM && turn.magnitude * reach < FlexMinM) { shift = Vector3.zero; turn = Vector3.zero; }
            float ease = 1f - Mathf.Exp(-Time.deltaTime / FlexEaseS);
            wingFlex = Vector2.Lerp(wingFlex, bend, ease);
            flexShift = Vector3.Lerp(flexShift, shift, ease);
            flexTurn = Vector3.Lerp(flexTurn, turn, ease);
        }

        // The largest distance a measured point sits from where the fitted shift, turn and bend put it.
        float FlexResidual(Quaternion toCanonical, Vector3 origin, Vector3 centre)
        {
            WingShape shape = driver.Shape;
            Vector3 turn = new Vector3((float)flexFit[3], (float)flexFit[4], (float)flexFit[5]);
            Vector3 shift = new Vector3((float)flexFit[0], (float)flexFit[1], (float)flexFit[2]);
            float worst = 0f;
            for (int i = 0; i < flexParts.Count; i++)
            {
                if (flexParts[i] == null) continue;
                Vector3 rest = flexRest[i], r = rest - centre;
                Vector3 moved = toCanonical * (flexParts[i].transform.TransformPoint(flexLocal[i]) - origin) - rest;
                double span = Math.Max(Math.Abs(rest.z) - shape.RootSpanM, 0);
                Vector3 predicted = shift + Vector3.Cross(turn, r) + new Vector3(0f, (float)((flexFit[6] + flexFit[7] * span) * span), 0f);
                worst = Mathf.Max(worst, (moved - predicted).magnitude);
            }
            return worst;
        }

        // Gaussian elimination with partial pivoting; false when singular.
        static bool Solve(double[,] m, double[] rhs, double[] x)
        {
            int n = rhs.Length;
            var a = (double[,])m.Clone();
            var y = (double[])rhs.Clone();
            for (int k = 0; k < n; k++)
            {
                int pivot = k;
                for (int i = k + 1; i < n; i++) if (Math.Abs(a[i, k]) > Math.Abs(a[pivot, k])) pivot = i;
                if (Math.Abs(a[pivot, k]) < 1e-9) return false;
                if (pivot != k)
                {
                    for (int j = 0; j < n; j++) { double t = a[k, j]; a[k, j] = a[pivot, j]; a[pivot, j] = t; }
                    double ty = y[k]; y[k] = y[pivot]; y[pivot] = ty;
                }
                for (int i = k + 1; i < n; i++)
                {
                    double f = a[i, k] / a[k, k];
                    for (int j = k; j < n; j++) a[i, j] -= f * a[k, j];
                    y[i] -= f * y[k];
                }
            }
            for (int k = n - 1; k >= 0; k--)
            {
                double sum = y[k];
                for (int j = k + 1; j < n; j++) sum -= a[k, j] * x[j];
                x[k] = sum / a[k, k];
            }
            return true;
        }

        // Load in g from summed wing lift over weight (FAR: felt acceleration).
        float MeasureLoad(Vector3 up)
        {
            Vector3 gravity = (Vector3)FlightGlobals.getGeeForceAtPosition(vessel.CoM);
            float weightKN = vessel.GetTotalMass() * gravity.magnitude;
            float load;
            if (liftSurfaces.Count > 0)
            {
                float liftKN = 0f;
                for (int i = 0; i < liftSurfaces.Count; i++)
                    if (liftSurfaces[i] != null) liftKN += Vector3.Dot(liftSurfaces[i].liftForce, up);
                load = weightKN > 0.001f ? liftKN / weightKN : 0f;
            }
            else
            {
                load = Vector3.Dot((Vector3)vessel.acceleration - gravity, up) / Mathf.Max(gravity.magnitude, 0.01f);
            }
            return Mathf.Clamp(load, -50f, 50f);
        }

        void Log(Camera cam)
        {
            if (Primary && !camerasLogged)
            {
                camerasLogged = true;
                Debug.Log("[VORTEX] wing vapor: colour space " + QualitySettings.activeColorSpace);
                foreach (Camera c in Camera.allCameras)
                    Debug.Log($"[VORTEX] wing vapor: camera {c.name}: near {c.nearClipPlane} far {c.farClipPlane} depth {c.depth} mask 0x{c.cullingMask:X} {(c == cam ? "(vapor drawn here)" : "")}");
            }
            if (!Primary || !WingVaporAddon.Verbose || Time.time < nextLog) return;
            nextLog = Time.time + 2f;
            var f = driver.State.Field;
            Vector3 box = driver.BoxMax - driver.BoxMin;
            Debug.Log($"[VORTEX] wing vapor: raymarch: {driver.Speed:F0} m/s alpha {driver.Alpha * 180 / Math.PI:F1} lift {driver.LoadG:F1} g (field {KspBaseline.FieldLoad(driver.EffectiveLoad):F1} g at {driver.Flight.AngleOfAttackRad * 180 / Math.PI:F1} deg) "
                      + $"beta {driver.Beta * 180 / Math.PI:F1} roll {driver.RollRate:F2} rad/s, alt {driver.Altitude:F0} m, "
                      + $"pose drift {poseResidual:F2} m, "
                      + $"bounds wing {f.WingBoundM:F2} edge {f.EdgeBoundM:F2} cone {f.ConeBoundM:F2} m, box {box}, step x{driver.StepScale:F2} detail {driver.Detail}, steps {driver.MaxSteps}");
        }

        // The craft's frame and airframe, from its parts: again when it gains or loses a part.
        void Rebuild(Vessel v)
        {
            var timing = System.Diagnostics.Stopwatch.StartNew();
            vessel = v;
            partCount = v.parts.Count;
            partSignature = PartSignature(v);
            built = false;
            if (driver == null) driver = new VaporDriver(VaporShaders.Raymarch);
            driver.Reset();
            lastStepTime = -1f;
            liftSurfaces.Clear();
            foreach (Part p in v.parts)
            {
                ModuleLiftingSurface ls = p.FindModuleImplementing<ModuleLiftingSurface>();
                if (ls != null) liftSurfaces.Add(ls);
            }
            try { built = Measure(v); }
            catch (Exception e) { Debug.Log("[VORTEX] wing vapor: raymarch airframe failed: " + e.Message); }
            Debug.Log($"[VORTEX] wing vapor: airframe build {timing.Elapsed.TotalMilliseconds:F1} ms for {v.vesselName}, {partCount} parts, grounded {measuredGrounded}");
        }

        bool Measure(Vessel v)
        {
            Transform root = v.rootPart.transform;

            // Canonical frame from the root part: length axis by part spread, nose toward the pods.
            Vector3 centroid = Vector3.zero;
            foreach (Part p in v.parts) centroid += p.transform.position;
            centroid /= Mathf.Max(v.parts.Count, 1);
            Vector3 length = Spread(v, root.forward) >= 0.95f * Spread(v, root.up) ? Vector3.forward : Vector3.up;
            float pod = 0f;
            int pods = 0;
            foreach (Part p in v.parts)
            {
                if (p.FindModuleImplementing<ModuleCommand>() == null) continue;
                pod += Vector3.Dot(p.transform.position - centroid, root.TransformDirection(length));
                pods++;
            }
            if (pods > 0 && pod < 0) length = -length;
            Vector3 vertical = Vector3.Cross(length, Vector3.right).normalized;
            bool grounded = v.LandedOrSplashed || v.srfSpeed < 30;
            measuredGrounded = grounded;
            groundFrameFlipped = grounded && Vector3.Dot(root.TransformDirection(vertical), (Vector3)v.upAxis) < 0;
            if (groundFrameFlipped) vertical = -vertical;
            frameLocal = CanonicalFrame.FromAxes(length, vertical);
            Quaternion rotation = root.rotation * frameLocal;
            Quaternion toCanonical = Quaternion.Inverse(rotation);
            Vector3 origin = root.position;
            Vector3 upWorld = rotation * Vector3.up;

            flexParts.Clear(); flexLocal.Clear(); flexRest.Clear();
            poseResidual = 0f; poseDriftSince = -1f; lastMeasureTime = Time.time;
            wingFlex = Vector2.zero;
            flexShift = flexTurn = Vector3.zero;
            var lap = System.Diagnostics.Stopwatch.StartNew();
            var surfaces = new List<SurfacePolygon>();
            var owners = new List<Part>();
            var lifting = new HashSet<Part>();
            foreach (PartAeroState s in AeroState.Read(v, -upWorld))
            {
                if (BDArmoryCraft.IsWingStore(s.part, upWorld)) continue;
                PartBox box = s.box;
                if (box == null || box.planform == null || box.planform.hull == null || box.planform.hull.Length < 3) continue;
                Planform pf = box.planform;
                float smallest = Mathf.Min(pf.gridMax.x - pf.gridMin.x, pf.gridMax.y - pf.gridMin.y);
                if (pf.thickness > BodyThickness * smallest) continue;
                lifting.Add(s.part);
                PlaneFrame frame = box.Frame();
                int m = pf.hull.Length;
                var poly = new SurfacePolygon { X = new double[m], Y = new double[m], Z = new double[m], ThicknessM = pf.thickness };
                for (int i = 0; i < m; i++)
                {
                    Vector3 world = frame.ToWorld(pf.hull[i]);
                    Vector3 c = toCanonical * (world - origin);
                    poly.X[i] = c.x; poly.Y[i] = c.y; poly.Z[i] = c.z;
                    flexParts.Add(s.part); flexLocal.Add(s.part.transform.InverseTransformPoint(world)); flexRest.Add(c);
                }
                poly.NormalY = Mathf.Abs(Vector3.Dot(frame.n, upWorld));
                surfaces.Add(poly);
                owners.Add(s.part);
            }
            int flags = AddWingFlags(v, lifting, surfaces, toCanonical, origin);
            stageMs[0] = Lap(lap);

            // The body: the craft's extent along x for the cone, and the widest part near the
            // centreline that does not lift for its radius.
            double noseX = double.PositiveInfinity, tailX = double.NegativeInfinity, radius = 0, height = 0, heightWeight = 0;
            foreach (Part p in v.parts)
            {
                if (BDArmoryCraft.IsWingStore(p, upWorld)) continue;
                PartBox box = AeroState.GetBox(p);
                if (box == null || box.t == null) continue;
                Vector3 lo = Vector3.positiveInfinity, hi = Vector3.negativeInfinity;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 local = box.center + Vector3.Scale(box.extents, new Vector3((corner & 1) != 0 ? 1 : -1, (corner & 2) != 0 ? 1 : -1, (corner & 4) != 0 ? 1 : -1));
                    Vector3 c = toCanonical * (box.t.TransformPoint(local) - origin);
                    lo = Vector3.Min(lo, c);
                    hi = Vector3.Max(hi, c);
                }
                noseX = Math.Min(noseX, lo.x);
                tailX = Math.Max(tailX, hi.x);
                if (lifting.Contains(p)) continue;
                float centreZ = (lo.z + hi.z) / 2, halfWidth = (hi.z - lo.z) / 2;
                if (Mathf.Abs(centreZ) > Mathf.Max(halfWidth, 0.5f)) continue;
                radius = Math.Max(radius, 0.5 * Math.Min(hi.y - lo.y, hi.z - lo.z));
                double volume = (hi.x - lo.x) * (hi.y - lo.y) * (hi.z - lo.z);
                height += volume * (lo.y + hi.y) / 2;
                heightWeight += volume;
            }
            if (double.IsInfinity(noseX)) return false;
            if (heightWeight > 0) height /= heightWeight;
            stageMs[1] = Lap(lap);

            VaporAirframe airframe;
            WingShape shape;
            if (!AirframeBuilder.Build(surfaces, noseX, tailX, radius, height, v.GetTotalMass() * 1000.0, out airframe, out shape))
            {
                Debug.Log($"[VORTEX] wing vapor: raymarch: no wing found on {v.vesselName} ({surfaces.Count} lifting outlines)");
                return false;
            }
            stageMs[2] = Lap(lap);
            if (ratioVessel != v) { ratioVessel = v; firstRatio = -1; }
            double side = LostSide(surfaces, noseX, tailX);
            // A pose re-measure must read a change twice before it flips the side: one reading mid-sweep can be off.
            if (remeasuring && side != loggedSideKeep)
            {
                if (side != pendingSide) { pendingSide = side; side = loggedSideKeep; }
            }
            else pendingSide = double.NaN;
            airframe.SideKeep = side;
            // Logged on the first measurement and when the answer changes, not on every re-measure.
            if (!remeasuring || airframe.SideKeep != loggedSideKeep)
            {
                loggedSideKeep = airframe.SideKeep;
                if (airframe.SideKeep != 0) Debug.Log($"[VORTEX] wing vapor: one wing is gone on {v.vesselName}, vapor kept on the {(airframe.SideKeep > 0 ? "+" : "-")} side only (reach {lastSideReach} m)");
                else Debug.Log($"[VORTEX] wing vapor: side reach {lastSideReach} m on {v.vesselName}");
            }
            shape.Cover = WingCover.Build(Triangles(v, toCanonical, origin), surfaces, airframe.RootSpanM);
            stageMs[3] = Lap(lap);
            if (Primary) CanardParts.Clear();
            if (Primary && shape.Canard != null)
                for (int i = 0; i < owners.Count; i++)
                    if (UnderCanard(shape.Canard, surfaces[i])) CanardParts.Add(owners[i].flightID);
            WingCover.SizeBody(shape.Cover, airframe);
            if (shape.Cover != null) shape.Cover.BuildBody(shape);
            KspBaseline.Prepare(shape);
            driver.SetAirframe(airframe, shape);
            stageMs[4] = Lap(lap);
            Debug.Log($"[VORTEX] wing vapor: raymarch airframe for {v.vesselName}: span {airframe.SpanM:F1} m, root chord {airframe.RootChordM:F1} m, "
                      + $"tip chord {airframe.TipChordM:F1} m, sweep {airframe.LeadingEdgeSweepRad * 180 / Math.PI:F0} deg, root at {airframe.RootSpanM:F2} m, "
                      + $"secondary {(shape.Secondary == null ? "none" : (shape.Secondary.IsCanard ? "canard" : "tail") + $" {shape.Secondary.SemispanM * 2:F1} m")}, "
                      + $"body {airframe.FuselageLengthM:F1} m x {airframe.FuselageRadiusM:F2} m, {surfaces.Count} outlines ({flags} of them flags on a wing), "
                      + $"axes nose={(pods > 0 ? "pod" : "guess")} up={(grounded ? "sky" : "frame")}");
            return true;
        }

        // Flat flags attached to lifting parts count as wing surface.
        const float FlagMinNormalY = 0.5f;

        static bool UnderCanard(SecondarySurface c, SurfacePolygon poly)
        {
            double x = 0, z = 0;
            for (int i = 0; i < poly.X.Length; i++) { x += poly.X[i]; z += Math.Abs(poly.Z[i]); }
            x /= poly.X.Length; z /= poly.X.Length;
            double t = (z - c.RootSpanM) / Math.Max(c.SemispanM - c.RootSpanM, 1e-3);
            if (t < -0.2 || t > 1.05) return false;
            t = Math.Min(Math.Max(t, 0), 1);
            double leading = c.RootLeadingM + (c.TipLeadingM - c.RootLeadingM) * t, chord = c.RootChordM + (c.TipChordM - c.RootChordM) * t;
            return x >= leading - 0.2 && x <= leading + chord + 0.2;
        }

        static bool IsFlag(Part p)
        {
            return p.partInfo != null && p.partInfo.name.StartsWith("flagPart", StringComparison.Ordinal);
        }

        static int AddWingFlags(Vessel v, HashSet<Part> lifting, List<SurfacePolygon> surfaces, Quaternion toCanonical, Vector3 origin)
        {
            int added = 0;
            foreach (Part p in v.parts)
            {
                if (!IsFlag(p)) continue;
                Part host = p.parent;
                while (host != null && IsFlag(host)) host = host.parent;
                if (host == null || !lifting.Contains(host)) continue;
                var points = new List<Vector3>();
                Vector3 normal = Vector3.zero;
                float bestArea = 0f;
                foreach (MeshFilter mf in p.GetComponentsInChildren<MeshFilter>(false))
                {
                    Mesh mesh = mf.sharedMesh;
                    MeshRenderer mr = mf.GetComponent<MeshRenderer>();
                    if (mesh == null || mr == null || !mr.enabled || !mesh.isReadable) continue;
                    Vector3[] verts = mesh.vertices;
                    for (int i = 0; i < verts.Length; i++) verts[i] = toCanonical * (mf.transform.TransformPoint(verts[i]) - origin);
                    points.AddRange(verts);
                    int[] tris = mesh.triangles;
                    for (int t = 0; t + 2 < tris.Length; t += 3)
                    {
                        Vector3 c = Vector3.Cross(verts[tris[t + 1]] - verts[tris[t]], verts[tris[t + 2]] - verts[tris[t]]);
                        if (c.magnitude > bestArea) { bestArea = c.magnitude; normal = c.normalized; }
                    }
                }
                if (points.Count < 3 || bestArea <= 0f || Mathf.Abs(normal.y) < FlagMinNormalY) continue;
                List<Vector3> hull = HullXZ(points);
                if (hull.Count < 3) continue;
                float lo = float.MaxValue, hi = float.MinValue;
                foreach (Vector3 q in points) { float d = Vector3.Dot(q, normal); lo = Mathf.Min(lo, d); hi = Mathf.Max(hi, d); }
                var poly = new SurfacePolygon { X = new double[hull.Count], Y = new double[hull.Count], Z = new double[hull.Count], ThicknessM = Mathf.Max(hi - lo, 0.02f), NormalY = Mathf.Abs(normal.y) };
                for (int i = 0; i < hull.Count; i++) { poly.X[i] = hull[i].x; poly.Y[i] = hull[i].y; poly.Z[i] = hull[i].z; }
                surfaces.Add(poly);
                added++;
            }
            return added;
        }

        // The convex hull of the points seen from above (x, z), each corner keeping its height.
        static List<Vector3> HullXZ(List<Vector3> points)
        {
            var sorted = new List<Vector3>(points);
            sorted.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.z.CompareTo(b.z));
            var hull = new List<Vector3>();
            for (int pass = 0; pass < 2; pass++)
            {
                int start = hull.Count;
                for (int k = 0; k < sorted.Count; k++)
                {
                    Vector3 q = pass == 0 ? sorted[k] : sorted[sorted.Count - 1 - k];
                    while (hull.Count >= start + 2)
                    {
                        Vector3 a = hull[hull.Count - 2], b = hull[hull.Count - 1];
                        if ((b.x - a.x) * (q.z - a.z) - (b.z - a.z) * (q.x - a.x) > 1e-6f) break;
                        hull.RemoveAt(hull.Count - 1);
                    }
                    hull.Add(q);
                }
                hull.RemoveAt(hull.Count - 1);
            }
            return hull;
        }

        // Every visible mesh's triangles in the canonical frame, nine floats each.
        static float[] Triangles(Vessel v, Quaternion toCanonical, Vector3 origin)
        {
            var list = new List<float>();
            foreach (CraftMeshes.Surface mesh in CraftMeshes.Read(v, Quaternion.Inverse(toCanonical) * Vector3.up))
                {
                    Vector3[] verts = mesh.Vertices;
                    int[] tris = mesh.Triangles;
                    for (int i = 0; i < verts.Length; i++) verts[i] = toCanonical * (verts[i] - origin);
                    foreach (int t in tris)
                    {
                        list.Add(verts[t].x); list.Add(verts[t].y); list.Add(verts[t].z);
                    }
                }
            return list.ToArray();
        }

        static float Spread(Vessel v, Vector3 direction)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (Part p in v.parts)
            {
                float d = Vector3.Dot(p.transform.position, direction);
                lo = Mathf.Min(lo, d);
                hi = Mathf.Max(hi, d);
            }
            return hi >= lo ? hi - lo : 0f;
        }

        // Forget the craft (a new one, or the vapor switched off).
        public void Clear()
        {
            vessel = null;
            partCount = -1;
            built = false;
            Drawing = false;
            requestedFrame = -1;
        }

        public void Destroy()
        {
            Clear();
            if (hooked) Camera.onPreCull -= OnPreCull;
            hooked = false;
            if (driver != null) driver.Destroy(false);
            driver = null;
            if (Primary) VaporVolumePass.Release();
        }
    }
}
