using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace VortexVapor
{
    // Wing vapor: the white sheet over the wings of an aircraft in a hard pull, worked out from the
    // physics of the air over each lifting part. A second addon in the WingtipVortex assembly; it
    // shares nothing with the wingtip-vortex code but the log tag and the version.
    //
    // Wires AeroState -> TrailedVorticity (each part's loading) -> WingVapor (condensation at every
    // point of every wing) -> VaporRenderer (puffs riding the aircraft) every physics step, with no
    // anchors, no config file and no GUI.
    [KSPAddon(KSPAddon.Startup.Flight, false)]
    public class WingVaporAddon : MonoBehaviour
    {
        // Per-second diagnostics (part loading, step times, what is condensing) are written to
        // KSP.log only when a file named verbose.txt sits in the mod's folder, next to Plugins.
        // Otherwise the addon logs one line per flight, and nothing more.
        public static bool Verbose { get; private set; }

        // After this many exceptions the addon switches itself off for the flight rather than throw
        // every physics step.
        const int MaxFailures = 5;
        private int failures = 0;
        private bool disabled = false;

        private Vessel vessel;
        private readonly VaporRenderer vaporRenderer = new VaporRenderer();
        private Vector3 vaporDown;
        private float nextLogTime = 0f;
        private bool dumped = false;
        private readonly Stopwatch stepTimer = new Stopwatch();
        private int stepsTimed = 0;
        private double stepWorstMs = 0.0;
        private readonly Stopwatch emitTimer = new Stopwatch();
        private int emitFrames = 0;
        private double emitWorstMs = 0.0;
        private readonly List<Surface> surfaces = new List<Surface>();
        private readonly VesselBodies bodies = new VesselBodies();
        // The vapor's inputs are refreshed every `stride` physics steps, more rarely the bigger the
        // craft: one step in PartsPerStride-many parts' worth, at most MaxStride. The vapor is
        // smoothed over WingVapor.SmoothTime (0.12 s) anyway, so 4 steps (0.08 s) between updates
        // does not show. The loading and the vapor are computed on different steps of the stride
        // so neither lands on top of the other as a hitch.
        const int PartsPerStride = 60, MaxStride = 4;
        private int step = 0, stride = 1;
        private bool vaporPending = false;
        private float vaporRho, vaporSpeed;
        private bool wasInAir = true;

        IEnumerator Start()
        {
            while (!FlightGlobals.ready || FlightGlobals.ActiveVessel == null || !FlightGlobals.ActiveVessel.loaded)
                yield return null;

            Verbose = FindVerboseSwitch();
            bool setupOk = true;
            try { AeroState.Init(); }
            catch (Exception e) { Debug.Log("[VORTEX] wing vapor: aero setup failed, vapor disabled: " + e); setupOk = false; }
            if (!setupOk) { disabled = true; yield break; }
            TrailedVorticity.BuildLines = false;   // the trailing lines are for the wake research, not shipped
            Follow(FlightGlobals.ActiveVessel);
            GameEvents.onVesselChange.Add(OnVesselChange);
            string version = global::WingtipVortex.ModVersion;
            Debug.Log($"[VORTEX] wing vapor: WingtipVortex v{version} starting on {(vessel != null ? vessel.vesselName : "nothing yet")}"
                      + (Verbose ? " (verbose log on)" : ""));
        }

        // GameData/<this mod>/verbose.txt, next to the Plugins folder.
        static bool FindVerboseSwitch()
        {
            try
            {
                string plugins = Path.GetDirectoryName(typeof(WingVaporAddon).Assembly.Location);
                string root = plugins != null ? Path.GetDirectoryName(plugins) : null;
                return root != null && File.Exists(Path.Combine(root, "verbose.txt"));
            }
            catch { return false; }
        }

        // A new craft starts with no vapor, and every cache is keyed by the old craft's parts.
        void Retarget(Vessel v)
        {
            if (v != null && v != vessel)
                Debug.Log($"[VORTEX] wing vapor: now on {Name(v)}" + (vessel != null ? $" (off {Name(vessel)})" : ""));
            vessel = v;
            bodies.vessel = v;
            bodies.Clear();
            vaporPending = false;
            AeroState.ClearCache();
            TrailedVorticity.ClearCache();
            WingVapor.Clear();
            vaporRenderer.Clear();
            dumped = false;
            // Start from the vessel's real state, so a craft loaded in orbit does not log a crossing.
            wasInAir = v != null && v.mainBody != null
                       && (float)v.atmDensity / Mathf.Max((float)v.mainBody.atmDensityASL, 1e-4f) >= Condensation.CutoffRatio;
        }

        // Moves the vapor to the active craft, except onto a fired missile (BDArmoryCraft.IsMissile):
        // BDArmory makes a missile the active vessel to follow it, and the vapor then left the
        // aircraft that fired it for a craft with no stock lifting surfaces to condense on. It
        // stays on the aircraft instead, which keeps drawing its own vapor while you watch the shot.
        // `skippedMissile` is the missile last declined, so the per-step poll does not re-test it.
        Vessel skippedMissile;
        void Follow(Vessel active)
        {
            if (active == null || active == vessel || active == skippedMissile) return;
            if (BDArmoryCraft.IsMissile(active))
            {
                skippedMissile = active;
                bool keep = vessel != null && vessel.loaded;
                Debug.Log($"[VORTEX] wing vapor: camera on missile {Name(active)}, "
                          + (keep ? $"staying on {Name(vessel)}" : "no aircraft to stay on"));
                if (!keep) Retarget(null);
                return;
            }
            skippedMissile = null;
            Retarget(active);
        }

        static string Name(Vessel v) { return KSP.Localization.Localizer.Format(v.vesselName); }

        void OnVesselChange(Vessel v) { Follow(v); }

        void FixedUpdate()
        {
            if (disabled) return;
            try { PhysicsStep(); }
            catch (Exception e) { Fail("physics step", e); }
        }

        // An exception in a physics step or a frame would repeat 50 or 60 times a second. Log the
        // first few in full, then stop the addon for the flight and tear down what it made.
        void Fail(string where, Exception e)
        {
            failures++;
            Debug.Log($"[VORTEX] wing vapor: error in {where} ({failures}/{MaxFailures}): {e}");
            if (failures < MaxFailures) return;
            disabled = true;
            try { WingVapor.Clear(); vaporRenderer.Clear(); } catch { }
            Debug.Log("[VORTEX] wing vapor: too many errors, switched off for this flight");
        }

        void PhysicsStep()
        {
            // The aircraft the vapor was held on (see Follow) destroyed while the camera was on a
            // missile: nothing switched vessels, so drop its vapor here. Unity's == null is true
            // for a destroyed object whose reference is still set.
            if (vessel == null && !ReferenceEquals(vessel, null)) Retarget(null);
            Follow(FlightGlobals.ActiveVessel);
            if (vessel == null || !vessel.loaded) return;

            // On-rails warp: flush rather than carry vapor through it.
            if (vessel.packed)
            {
                WingVapor.Clear();
                return;
            }

            Vector3 velocity = vessel.GetSrfVelocity();
            float speed = velocity.magnitude;
            float rho = (float)vessel.atmDensity;
            // The vapor lives in the lower atmosphere only, with a hard cutoff
            // (Condensation.CutoffRatio). Outside it the addon does nothing at all.
            float rhoRatio = rho / Mathf.Max(RhoSeaLevel(), 1e-4f);
            bool inAir = rhoRatio >= (wasInAir ? Condensation.CutoffRatio * Condensation.CutoffHysteresis : Condensation.CutoffRatio);
            if (inAir != wasInAir)
            {
                wasInAir = inAir;
                if (Verbose)
                    Debug.Log($"[VORTEX] wing vapor: {(inAir ? "entered" : "left")} the vapor's atmosphere at altitude {vessel.altitude:F0} m "
                              + $"(density {rhoRatio:P1} of sea level, cutoff {Condensation.CutoffRatio:P0})");
            }
            bool inBand = speed >= 5f && inAir;

            long ticksBefore = stepTimer.ElapsedTicks;
            stepTimer.Start();
            List<PartAeroState> parts = null;
            Vector3 downstream = speed > 1e-3f ? -velocity / speed : Vector3.zero;
            step++;
            if (!inBand)
            {
                if (WingVapor.Fields.Count > 0) WingVapor.Clear();
                vaporPending = false;
            }
            else if (step % stride == 0)
            {
                parts = AeroState.ReadStock(vessel);
                AeroState.Surfaces(parts, surfaces);
                stride = Mathf.Clamp((surfaces.Count + PartsPerStride - 1) / PartsPerStride, 1, MaxStride);
                if (surfaces.Count > 0)
                {
                    bodies.Refresh();
                    TrailedVorticity.Compute(surfaces, bodies, velocity, rho, speed, Time.time);
                    vaporPending = true;
                    vaporRho = rho;
                    vaporSpeed = speed;
                }
                else WingVapor.Clear();
                if (TrailedVorticity.Dump.Count > 0)
                {
                    foreach (string line in TrailedVorticity.Dump) Debug.Log("[VORTEX] wing vapor: dump " + line);
                    TrailedVorticity.Dump.Clear();
                }
            }
            // Half a stride after the loading (the same step when the stride is 1), on the
            // surfaces the loading was computed for, so the panel indices match.
            if (vaporPending && (stride == 1 || step % stride == stride / 2))
            {
                vaporPending = false;
                WingVapor.Compute(surfaces, downstream, vaporRho, vaporSpeed,
                    (float)vessel.atmosphericTemperature, (float)(vessel.staticPressurekPa * 1000.0),
                    Humidity(), (float)vessel.mach, stride * Time.fixedDeltaTime, bodies);
            }
            vaporDown = downstream;
            stepTimer.Stop();
            stepsTimed++;
            double thisStepMs = (stepTimer.ElapsedTicks - ticksBefore) * 1000.0 / Stopwatch.Frequency;
            if (thisStepMs > stepWorstMs) stepWorstMs = thisStepMs;

            if (Verbose && parts != null && parts.Count > 0) LogValidation(parts, speed);
        }

        // Vapor is emitted per rendered frame, so its density does not depend on the physics rate.
        void LateUpdate()
        {
            if (disabled) return;
            try { EmitFrame(); }
            catch (Exception e) { Fail("frame update", e); }
        }

        void EmitFrame()
        {
            if (vessel == null || !vessel.loaded || vessel.packed) return;
            if (WingVapor.Fields.Count == 0) { vaporRenderer.Emit(vessel, vaporDown, 1f, Time.deltaTime); return; }   // tears down an idle system
            long t0 = emitTimer.ElapsedTicks;
            emitTimer.Start();
            vaporRenderer.Emit(vessel, vaporDown, Daylight(), Time.deltaTime);
            emitTimer.Stop();
            emitFrames++;
            double ms = (emitTimer.ElapsedTicks - t0) * 1000.0 / Stopwatch.Frequency;
            if (ms > emitWorstMs) emitWorstMs = ms;
        }

        float RhoSeaLevel()
        {
            return vessel.mainBody != null ? (float)vessel.mainBody.atmDensityASL : 1.225f;
        }

        float Humidity()
        {
            return Condensation.RelativeHumidity((float)vessel.atmDensity, RhoSeaLevel(), (float)vessel.atmosphericTemperature);
        }

        // How lit the air around the vessel is, 0-1, measured the way the wingtip vortices measure
        // it (Sunlight): by the sun's height above the craft's horizon, so an afternoon sun low in
        // the sky no longer greys the vapor the way the atmosphere-absorbed solar flux did.
        float Daylight() { return Sunlight.Fraction(vessel); }

        // Every second while there is vapor to look at, every five otherwise (verbose only): the
        // numbers the model rests on, so they can be checked against a real aircraft of similar
        // size and load.
        void LogValidation(List<PartAeroState> parts, float speed)
        {
            if (Time.time < nextLogTime) return;
            nextLogTime = Time.time + (WingVapor.Coverage > 0f || WingVapor.PeakWater > 0f ? 1f : 5f);

            // Two lift figures, so the log shows how much a plain sum of magnitudes overcounts:
            // `sum` adds every part's |lift|; `net` is the component along the total lift
            // direction, which is what actually holds the aircraft up.
            Vector3 total = Vector3.zero;
            float sumKN = 0f;
            foreach (var p in parts) { total += p.liftForce; sumKN += p.liftForce.magnitude; }
            float netKN = total.magnitude;
            float weightKN = vessel.GetTotalMass() * (float)FlightGlobals.getGeeForceAtPosition(vessel.CoM).magnitude;
            double stepMs = stepsTimed > 0 ? stepTimer.Elapsed.TotalMilliseconds / stepsTimed : 0.0;
            double emitMs = emitFrames > 0 ? emitTimer.Elapsed.TotalMilliseconds / emitFrames : 0.0;
            double stepWorst = stepWorstMs, emitWorst = emitWorstMs;
            stepTimer.Reset();
            stepsTimed = 0;
            stepWorstMs = 0.0;
            emitTimer.Reset();
            emitFrames = 0;
            emitWorstMs = 0.0;

            Debug.Log($"[VORTEX] wing vapor: speed={speed:F0} m/s rho={vessel.atmDensity:F3} "
                      + $"lift sum={sumKN:F1} net={netKN:F1} kN weight={weightKN:F1} kN "
                      + $"net/weight={netKN / Mathf.Max(weightKN, 0.01f):F2} g={vessel.geeForce:F1} "
                      + $"| step={stepMs:F2} ms (worst {stepWorst:F1}) (lifting parts={surfaces.Count}, updated every {stride} step(s); "
                      + $"puffs={vaporRenderer.LiveParticles}, emit={emitMs:F2} ms/frame, worst {emitWorst:F1})");

            Debug.Log($"[VORTEX] wing vapor:   alt={vessel.altitude:F0} m T={vessel.atmosphericTemperature:F1} K p={vessel.staticPressurekPa:F1} kPa "
                      + $"RH={Humidity():F2} mach={vessel.mach:F2} "
                      + $"condensed water peak={WingVapor.PeakWater:F2} g/kg coverage={WingVapor.Coverage:P0}");
            string condensing = WingVapor.Report(surfaces, 4);
            if (condensing != null) Debug.Log("[VORTEX] wing vapor:   condensing: " + condensing);

            // One edge-by-edge dump per flight, once the aircraft is really flying.
            if (!dumped && speed > 60f)
            {
                dumped = true;
                TrailedVorticity.DebugDump = true;
            }
        }

        void OnDestroy()
        {
            GameEvents.onVesselChange.Remove(OnVesselChange);
            vaporRenderer.Clear();
        }
    }
}
