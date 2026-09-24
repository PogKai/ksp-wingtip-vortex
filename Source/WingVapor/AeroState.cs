using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace VortexVapor
{
    // One lifting part's aerodynamic state for the current physics frame, in world space. This is
    // the only place either aero backend (stock or FAR) gets read from — everything downstream
    // (TrailedVorticity, WakeSimulation) takes these numbers and never touches
    // ModuleLiftingSurface or FARAPI directly.
    public struct PartAeroState
    {
        public Part part;
        public Vector3 liftForce;   // world space, kN
        public Vector3 dragForce;   // world space, kN
        public Vector3 position;    // world space, centre of the part's mesh bounds
        public PartBox box;         // null if the part has no usable mesh
    }

    // A part's geometry in its own frame, measured once from its meshes and cached: its bounds
    // (for "is this point inside the part"), its thinnest axis (its surface normal), and its
    // planform, the outline of the part in the plane of the other two axes. Meshes with absurd
    // bounds are skipped, since visual mods inflate renderer bounds on purpose to defeat frustum
    // culling and one of those would otherwise poison every measurement.
    public class PartBox
    {
        public Transform t;
        public Vector3 center;       // part-local
        public Vector3 extents;      // part-local half-sizes
        public Vector3 localNormal;  // part-local unit axis along which the part is thinnest
        public Vector3 localU, localV;   // part-local unit axes of the planform plane
        public Planform planform;    // metres, in (u, v) about the centre

        public Vector3 WorldCenter { get { return t.TransformPoint(center); } }

        // Radius about the part's transform origin that holds the whole box, in world metres.
        public float BoundRadius
        {
            get
            {
                if (boundRadius < 0f)
                {
                    Vector3 s = t.lossyScale;
                    Vector3 far = new Vector3((Mathf.Abs(center.x) + extents.x) * Mathf.Abs(s.x),
                                              (Mathf.Abs(center.y) + extents.y) * Mathf.Abs(s.y),
                                              (Mathf.Abs(center.z) + extents.z) * Mathf.Abs(s.z));
                    boundRadius = far.magnitude;
                }
                return boundRadius;
            }
        }
        float boundRadius = -1f;

        // Where the planform is now. The normal is read from geometry rather than from the lift
        // vector, so it never flips when the part's lift changes sign, which would swap its two
        // span edges and their ids.
        public PlaneFrame Frame()
        {
            return new PlaneFrame
            {
                origin = t.TransformPoint(center),
                u = t.TransformDirection(localU),
                v = t.TransformDirection(localV),
                n = t.TransformDirection(localNormal)
            };
        }

        public bool Contains(Vector3 world)
        {
            // A part destroyed since the snapshot: VesselBodies.Refresh runs half a stride before
            // the vapor queries it, and in combat a part can be shot off in between.
            if (t == null) return false;
            Vector3 l = t.InverseTransformPoint(world) - center;
            return Mathf.Abs(l.x) <= extents.x && Mathf.Abs(l.y) <= extents.y && Mathf.Abs(l.z) <= extents.z;
        }
    }

    // The vessel's parts as solid volumes, for TrailedVorticity's carry-through test and the wing
    // vapor's buried-face test.
    //
    // Refresh() takes a snapshot of every part's position once; BodiesAt then costs plain
    // arithmetic per part. Reading Transform.position for every part on every query was the
    // v0.4.7 F-22's frame-rate hole: a fighter's 137 parts, 200-odd queries per part measured.
    public class VesselBodies : IBodyQuery
    {
        public Vessel vessel;
        readonly List<PartBox> boxes = new List<PartBox>();
        readonly List<long> ids = new List<long>();
        Vector3[] centers = new Vector3[0];
        float[] radius2 = new float[0];

        public void Clear() { boxes.Clear(); ids.Clear(); }

        public void Refresh()
        {
            boxes.Clear(); ids.Clear();
            if (vessel == null) return;
            var parts = vessel.Parts;
            if (centers.Length < parts.Count) { centers = new Vector3[2 * parts.Count]; radius2 = new float[2 * parts.Count]; }
            foreach (Part p in parts)
            {
                PartBox b = AeroState.GetBox(p);
                if (b == null || b.t == null) continue;
                int k = boxes.Count;
                boxes.Add(b);
                ids.Add(p.flightID);
                centers[k] = b.t.position;
                float r = b.BoundRadius;
                radius2[k] = r * r;
            }
        }

        public void BodiesAt(Vector3 world, long exclude, List<long> into)
        {
            into.Clear();
            for (int k = 0; k < boxes.Count; k++)
            {
                // Bounding sphere first: most parts are nowhere near the point, and the exact
                // test costs a transform.
                Vector3 d = world - centers[k];
                if (d.sqrMagnitude > radius2[k] || ids[k] == exclude) continue;
                if (boxes[k].Contains(world)) into.Add(ids[k]);
            }
        }
    }

    // Reads per-part lift/drag every physics frame. FAR detection is ported from WingtipVortex's
    // own FARBridge: reflection-bound, no hard dependency, incompatible FAR just leaves
    // FarAvailable false. See DESIGN.md "Spanwise circulation" for why FAR mode doesn't yet do
    // more than stock mode despite being detected here.
    public static class AeroState
    {
        private static MethodInfo miAeroForce;
        private static bool initialized = false;
        private static bool farAvailable = false;

        public static bool FarAvailable { get { return farAvailable; } }

        private const float MaxMeshSize = 100f; // metres; anything larger is a culling-defeat bound, not geometry
        private static readonly Dictionary<Part, PartBox> boxes = new Dictionary<Part, PartBox>();
        private static readonly List<PartAeroState> scratch = new List<PartAeroState>();

        public static void ClearCache() { boxes.Clear(); }

        static readonly List<Vector3> localPoints = new List<Vector3>();
        static readonly List<Vector3> meshVerts = new List<Vector3>();
        static readonly List<Vector2> planPoints = new List<Vector2>();
        const int MaxVerticesPerMesh = 4000;   // above this, every n-th vertex; the outline survives

        public static PartBox GetBox(Part p)
        {
            PartBox box;
            if (boxes.TryGetValue(p, out box)) return box;

            Transform pt = p.transform;
            localPoints.Clear();
            foreach (MeshFilter mf in p.GetComponentsInChildren<MeshFilter>())
            {
                Mesh mesh = mf.sharedMesh;
                if (mesh == null) continue;
                // Child parts hang under their parent's transform, so GetComponentsInChildren
                // walks into them. An outer wing panel measured as part of the inner one would
                // put the inner panel's span edge at the real wingtip.
                if (mf.GetComponentInParent<Part>() != p) continue;
                Renderer r = mf.GetComponent<Renderer>();
                if (r == null || !r.enabled) continue;
                Bounds mb = mesh.bounds;
                if (Vector3.Scale(mb.size, mf.transform.lossyScale).magnitude > MaxMeshSize) continue;

                Matrix4x4 toPart = pt.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                if (mesh.isReadable)
                {
                    // The real vertices, so a swept or tapered panel's outline is its own and not
                    // its bounding box.
                    meshVerts.Clear();
                    mesh.GetVertices(meshVerts);
                    int stride = Mathf.Max(1, meshVerts.Count / MaxVerticesPerMesh);
                    for (int i = 0; i < meshVerts.Count; i += stride) localPoints.Add(toPart.MultiplyPoint3x4(meshVerts[i]));
                }
                else
                {
                    for (int i = 0; i < 8; i++)
                        localPoints.Add(toPart.MultiplyPoint3x4(mb.center + Vector3.Scale(mb.extents,
                            new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f))));
                }
            }

            if (localPoints.Count == 0)
            {
                boxes[p] = null;
                return null;
            }

            Bounds lb = new Bounds(localPoints[0], Vector3.zero);
            foreach (var q in localPoints) lb.Encapsulate(q);

            // Thinnest axis in world units, so a scaled part is judged by its real shape.
            Vector3 scale = new Vector3(pt.TransformVector(Vector3.right).magnitude,
                                        pt.TransformVector(Vector3.up).magnitude,
                                        pt.TransformVector(Vector3.forward).magnitude);
            float sx = scale.x * lb.extents.x, sy = scale.y * lb.extents.y, sz = scale.z * lb.extents.z;
            int nAxis = sx <= sy && sx <= sz ? 0 : (sy <= sz ? 1 : 2);
            int uAxis = (nAxis + 1) % 3, vAxis = (nAxis + 2) % 3;

            planPoints.Clear();
            foreach (var q in localPoints)
            {
                Vector3 d = q - lb.center;
                planPoints.Add(new Vector2(d[uAxis] * scale[uAxis], d[vAxis] * scale[vAxis]));
            }

            box = new PartBox
            {
                t = pt,
                center = lb.center,
                extents = lb.extents,
                localNormal = Axis(nAxis),
                localU = Axis(uAxis),
                localV = Axis(vAxis),
                planform = Planform.Build(planPoints, 2f * lb.extents[nAxis] * scale[nAxis])
            };
            boxes[p] = box;
            return box;
        }

        static Vector3 Axis(int i) { return i == 0 ? Vector3.right : (i == 1 ? Vector3.up : Vector3.forward); }

        // The lifting parts as TrailedVorticity takes them, lift in newtons.
        //
        // vaporLift: a control surface that responds to roll (a taileron, an elevon, an all-moving
        // stabilator) condenses only from the load its symmetry group carries together, the mean
        // of their lifts. A pure roll deflects the two halves equal and opposite, the mean is zero,
        // and neither fogs; a pull loads both alike and they fog as before. Stock hands a small,
        // fully deflected surface a lift coefficient no real tail reaches, with no downwash from
        // the wing ahead and no lag before the roll rate damps it, so the half deflected with the
        // aircraft's lift used to fog at every roll input while the wing stayed clear. A STYLE RULE
        // like WingVapor.CondenseAgainstLift, not physics: a real stabilator deflected hard at high
        // load can condense briefly. The wake still sheds from the full lift.
        public static void Surfaces(List<PartAeroState> parts, List<Surface> into)
        {
            into.Clear();
            liftOf.Clear();
            foreach (var p in parts) liftOf[p.part] = p.liftForce;
            foreach (var p in parts)
            {
                if (p.box == null || p.box.planform == null) continue;
                Vector3 vaporLift = p.liftForce;
                var cs = p.part.FindModuleImplementing<ModuleControlSurface>();
                if (cs != null && !cs.ignoreRoll && p.part.symmetryCounterparts != null && p.part.symmetryCounterparts.Count > 0)
                {
                    Vector3 sum = p.liftForce;
                    int count = 1;
                    foreach (Part c in p.part.symmetryCounterparts)
                    {
                        Vector3 l;
                        if (c != null && liftOf.TryGetValue(c, out l)) { sum += l; count++; }
                    }
                    vaporLift = sum / count;
                }
                into.Add(new Surface
                {
                    id = p.part.flightID,
                    name = p.part.name,
                    planform = p.box.planform,
                    frame = p.box.Frame(),
                    lift = p.liftForce * 1000f,   // kN -> N
                    vaporLift = vaporLift * 1000f
                });
            }
        }
        static readonly Dictionary<Part, Vector3> liftOf = new Dictionary<Part, Vector3>();

        public static void Init()
        {
            if (initialized) return;
            initialized = true;
            try
            {
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!asm.GetName().Name.StartsWith("FerramAerospaceResearch")) continue;
                    Type farApi = asm.GetType("FerramAerospaceResearch.FARAPI");
                    if (farApi == null) continue;
                    miAeroForce = farApi.GetMethod("VesselAerodynamicForce", new[] { typeof(Vessel) });
                    farAvailable = miAeroForce != null;
                    break;
                }
            }
            catch { farAvailable = false; }
            Debug.Log(farAvailable
                ? "[VORTEX] wing vapor: FAR detected: FAR replaces the stock lifting modules this mod reads, so no wing vapor will appear"
                : "[VORTEX] wing vapor: FAR not detected, using the stock lifting surfaces");
        }

        // Stock path: every lifting part reports its own liftForce/dragForce directly, so this is
        // a real per-part read regardless of which aero model stock itself is running. One entry
        // per part: a part carrying more than one lifting module is still one surface with one
        // pair of span edges.
        public static List<PartAeroState> ReadStock(Vessel vessel)
        {
            var result = scratch;   // reused every call; callers must not hold it across frames
            result.Clear();
            if (vessel == null || vessel.Parts == null) return result;
            foreach (Part p in vessel.Parts)
            {
                if (p.Modules == null) continue;
                bool lifting = false;
                Vector3 lift = Vector3.zero, drag = Vector3.zero;
                foreach (PartModule pm in p.Modules)
                {
                    ModuleLiftingSurface mls = pm as ModuleLiftingSurface;
                    if (mls == null) continue;
                    lifting = true;
                    lift += mls.liftForce;
                    drag += mls.dragForce;
                }
                if (!lifting) continue;
                PartBox box = GetBox(p);
                result.Add(new PartAeroState
                {
                    part = p,
                    liftForce = lift,
                    dragForce = drag,
                    position = box != null ? box.WorldCenter : p.transform.position,
                    box = box
                });
            }
            return result;
        }

        // FAR replaces ModuleLiftingSurface, so there is no per-part split to read under FAR —
        // FARAPI.VesselAerodynamicForce gives one whole-vessel force. A real per-section pull
        // needs FAR's internal per-section solve, which FARAPI does not expose today.
        public static Vector3 ReadVesselForceFAR(Vessel vessel)
        {
            if (!farAvailable || vessel == null) return Vector3.zero;
            try { return (Vector3)miAeroForce.Invoke(null, new object[] { vessel }); }
            catch { farAvailable = false; return Vector3.zero; }
        }
    }
}
