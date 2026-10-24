using System;
using System.Collections.Generic;
using UnityEngine;

namespace VortexVapor
{
    // The raymarched vapor on other aircraft near the camera (formations, dogfights); the active
    // craft's is WingVaporAddon's.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class OtherCraftVapor : MonoBehaviour
    {
        const int MaxOthers = 3;
        const float MaxDistanceM = 2000f;
        readonly Dictionary<Vessel, RaymarchVapor> drawn = new Dictionary<Vessel, RaymarchVapor>();
        readonly List<Vessel> wanted = new List<Vessel>(), gone = new List<Vessel>();
        readonly List<KeyValuePair<float, Vessel>> near = new List<KeyValuePair<float, Vessel>>();
        static readonly Comparison<KeyValuePair<float, Vessel>> ByDistance = (a, b) => a.Key.CompareTo(b.Key);
        float nextPick;

        void LateUpdate()
        {
            if (!RaymarchVapor.Available || !ModSettings.VaporSheet || ModSettings.VaporScale <= 0.01f || !FlightGlobals.ready) { Clear(); return; }
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (cam == null) return;
            if (Time.time >= nextPick) { nextPick = Time.time + 0.5f; Pick(cam.transform.position); }
            gone.Clear();
            foreach (KeyValuePair<Vessel, RaymarchVapor> d in drawn)
                if (d.Key == null || !wanted.Contains(d.Key)) gone.Add(d.Key);
            foreach (Vessel v in gone) { drawn[v].Destroy(); drawn.Remove(v); }
            foreach (Vessel v in wanted)
            {
                RaymarchVapor r;
                if (!drawn.TryGetValue(v, out r)) drawn[v] = r = new RaymarchVapor { Primary = false };
                r.Request(v, Sunlight.Fraction(v), ModSettings.VaporScale, Mathf.Max(ModSettings.SheetDepth, 0.1f));
            }
        }

        // The nearest flying aircraft other than the active one, in the vapor's air.
        void Pick(Vector3 camera)
        {
            wanted.Clear();
            near.Clear();
            foreach (Vessel v in FlightGlobals.VesselsLoaded)
            {
                if (v == null || v == FlightGlobals.ActiveVessel || v.packed || v.LandedOrSplashed || v.srfSpeed < 30 || BDArmoryCraft.IsMissile(v)) continue;
                if (v.mainBody == null || (float)v.atmDensity / Mathf.Max((float)v.mainBody.atmDensityASL, 1e-4f) < Condensation.CutoffRatio) continue;
                float d = (v.CoM - camera).magnitude;
                if (d > MaxDistanceM || !Lifts(v)) continue;
                near.Add(new KeyValuePair<float, Vessel>(d, v));
            }
            near.Sort(ByDistance);
            for (int i = 0; i < near.Count && i < MaxOthers; i++) wanted.Add(near[i].Value);
        }

        static bool Lifts(Vessel v)
        {
            foreach (Part p in v.parts)
                if (p.Modules.Contains("ModuleLiftingSurface")) return true;
            return false;
        }

        void Clear()
        {
            foreach (RaymarchVapor r in drawn.Values) r.Destroy();
            drawn.Clear();
            wanted.Clear();
        }

        void OnDestroy() { Clear(); }
    }
}
