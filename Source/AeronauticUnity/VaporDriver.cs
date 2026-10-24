// One aircraft's raymarched vapor, from flight inputs to the draw; KSP-independent.
// After aeronautic (github.com/Romainlg29/aeronautic @ ca535855f7), packages/wing-vapor/src/wing-vapor-core.ts.
// MIT License, (c) 2026 Romain Le Gall; see THIRD-PARTY-NOTICES.md.
using System;
using UnityEngine;
using VortexVapor.Aeronautic;

namespace VortexVapor
{
    // What the craft did this frame. World space; the airframe's frame (x aft, y up, z along a
    // wing) is `Attitude` at `Position`.
    public struct VaporFrameInput
    {
        public Vector3 Position;
        public Quaternion Attitude;
        public Vector3 Velocity;      // through the air, m/s
        public float RawLoadG;        // the wings' lift over the weight, along the airframe's up
        public double AltitudeM;
        public Vector3 SunDirection;  // towards the sun
        public float Daylight;        // 0 night .. 1 day
        public Vector3 SunTint;       // the sunlight left after the air it crossed (zero: none lost)
        public float SkyScale;        // the sky's light against a high sun's (zero: unchanged)
        public float Opacity;         // the player's intensity, 0..2
        public float ReachScale;      // the Depth slider, 0..2
        public float Dt;              // game seconds since the last frame
        public bool Grounded;         // on the ground: the angle of attack is the gear's attitude
        public bool GearDown;         // landing gear deployed
        // the wing's flex, its rise (m) at s m outboard of the root: x*s + y*s*s.
        public Vector2 WingFlex;
    }

    public class VaporDriver
    {
        const double AltitudeStepM = 10;
        // Upstream's: the measured roll and the load (its tracker's acceleration) eased over these.
        const float RollResponseS = 0.15f, LoadResponseS = 0.4f;
        // the load eases back down over this, so the vapor fades rather than snapping off.
        const float LoadReleaseS = 0.25f;
        const float MaxDeltaS = 0.1f, MinSpeed = 15f;

        // Upstream's docs stage, day and night. Its night keeps a dim moonlit sun; KSP's sun is under
        // the horizon then, so here the sun goes out and only the night sky is left.
        static readonly Vector3 DaySun = new Vector3(1f, 0.96f, 0.9f);
        static readonly Vector3 NightSky = new Vector3(0.08f, 0.1f, 0.18f), DaySky = new Vector3(0.55f, 0.68f, 0.9f);
        const float DaySunIntensity = 3f, NightSkyIntensity = 0.15f, DaySkyIntensity = 1f;

        static int instances;

        readonly Shader shader;
        readonly VaporLook look = new VaporLook();
        readonly float seed;
        readonly float[] tableValues = new float[MoistCondensation.Texels * 4];
        VaporMaterial material;
        Mesh cube;
        VaporAirframe airframe;
        WingShape shape;
        bool shapeWritten;
        double tableAltitude = double.NaN, tableHumidity, tableOffsetK;
        MoistAir air;
        VaporState state;   // reused every frame: drawing allocates nothing
        // The craft's path through the air, for the tip trails to follow.
        readonly TrailHistory history = new TrailHistory();
        float time;
        Quaternion lastAttitude;
        bool haveAttitude, haveLoad;
        Matrix4x4 objectToWorld;
        Vector3 boxMin, boxMax;

        public VaporEffects Effects;
        public int MaxSteps = 160;
        // Ambient moisture and temperature offset.
        public double Humidity = KspBaseline.Humidity, OffsetK = KspBaseline.OffsetK;
        // Use the upstream load mapping without KSP calibration.
        public bool UpstreamMapping;

        // Flight state from the last update.
        public bool Visible { get; private set; }
        public float Speed { get; private set; }
        public double Alpha { get; private set; }
        public double Beta { get; private set; }
        public float LoadG { get; private set; }
        // the load the vapor reads, with the angle of attack's share, smoothed as LoadG.
        public double EffectiveLoad { get; private set; }
        double landing;
        public float RollRate { get; private set; }
        public float RawRoll { get; private set; }
        public double Altitude { get; private set; }
        public VaporFlight Flight { get; private set; }
        public float StepScale { get; private set; }
        public float Detail { get; private set; }
        public VaporState State { get { return state; } }
        public VaporAirframe Airframe { get { return airframe; } }
        public WingShape Shape { get { return shape; } }
        public Vector3 BoxMin { get { return boxMin; } }
        public Vector3 BoxMax { get { return boxMax; } }

        public VaporDriver(Shader shader)
        {
            this.shader = shader;
            Effects = VaporEffects.Default();
            // Upstream's: two aircraft never fog in lockstep, and a replay draws the same patches.
            seed = (instances++ * 7.31f) % 100f;
        }

        // A new craft, or the same one measured again.
        public void SetAirframe(VaporAirframe a, WingShape s)
        {
            airframe = a;
            shape = s;
            shapeWritten = false;
            Visible = false;
        }

        // Forget what the last frames measured: a new craft, or a jump.
        public void Reset()
        {
            haveAttitude = haveLoad = false;
            RollRate = RawRoll = LoadG = 0f;
            EffectiveLoad = 0;
            landing = 0;
            history.Reset();
            Visible = false;
        }

        // Once a frame, whatever draws it: the physics and every value but the march's budget.
        // Returns whether anything can fog.
        public bool Step(VaporFrameInput input)
        {
            Visible = false;
            float dt = Mathf.Max(input.Dt, 0f);
            MeasureRoll(input.Attitude, dt);
            MeasureLoad(input.RawLoadG, dt);
            time += Mathf.Min(dt, MaxDeltaS);
            if (airframe == null || shader == null) return false;

            Speed = input.Velocity.magnitude;
            if (!UpstreamMapping && input.Grounded) return false;
            Quaternion q = input.Attitude;
            if (Speed > 1e-3f)
            {
                // The air's direction past the craft, in its frame: aft, and up by the angle of attack.
                Vector3 flow = -input.Velocity / Speed;
                double fx = Vector3.Dot(flow, q * Vector3.right), fy = Vector3.Dot(flow, q * Vector3.up), fz = Vector3.Dot(flow, q * Vector3.forward);
                Alpha = Math.Atan2(fy, fx);
                Beta = -Math.Asin(Math.Max(-1, Math.Min(1, fz)));
            }
            landing = KspBaseline.EaseLanding(landing, input.GearDown, Math.Min(dt, MaxDeltaS));
            double wanted = KspBaseline.EquivalentLoad(input.RawLoadG, input.Grounded ? 0 : Alpha, landing, Speed);
            float settle = Math.Abs(wanted) >= Math.Abs(EffectiveLoad) ? LoadResponseS : LoadReleaseS;
            EffectiveLoad += (wanted - EffectiveLoad) * (1 - Math.Exp(-Math.Min(dt, MaxDeltaS) / settle));
            // Upstream lays the trails' history every frame, fogging or not, so vapor that comes
            // back comes back with the trail it should have behind it.
            if (Effects.TipVortices)
            {
                double keep = state != null ? Math.Max(state.Field.TipReachM, state.Field.SecondReachM) : 0;
                history.Advance(dt, new Quat(q.x, q.y, q.z, q.w), Speed, Math.Cos(Alpha) * Math.Cos(Beta), Math.Sin(Alpha) * Math.Cos(Beta), keep, -Math.Sin(Beta));
            }
            float opacity = UpstreamMapping ? input.Opacity : input.Opacity * (float)(KspBaseline.Fade(Speed) * KspBaseline.Density * KspBaseline.LoadGate(Math.Max(LoadG, EffectiveLoad)) * KspBaseline.LoadOpacity(Math.Max(LoadG, EffectiveLoad)));
            if (Speed < MinSpeed || opacity <= 0.01f) return false;

            if (material == null) Create();
            // Upstream's: to the nearest 10 m, so a climb rebuilds the table every 10 m.
            double altitude = Math.Round(Math.Max(input.AltitudeM, 0) / AltitudeStepM) * AltitudeStepM;
            if (altitude != tableAltitude || Humidity != tableHumidity || OffsetK != tableOffsetK)
            {
                air = MoistAir.FromAltitude(altitude, Humidity, OffsetK);
                material.WriteTable(MoistCondensation.Table(air, look.HumiditySpread, tableValues));
                tableAltitude = altitude;
                tableHumidity = Humidity;
                tableOffsetK = OffsetK;
            }
            Altitude = altitude;

            if (UpstreamMapping)
            {
                look.ReachScale = input.ReachScale;
                Flight = new VaporFlight
                {
                    AirspeedMPerS = Speed,
                    AngleOfAttackRad = KspBaseline.AngleForLoad(airframe, LoadG, Speed, air),
                    SideslipRad = Beta,
                    RollRateRadPerS = RollRate,
                };
            }
            else
            {
                look.ReachScale = input.ReachScale * KspBaseline.BaseReach * KspBaseline.SpeedReach(Speed);
                Flight = KspBaseline.Flight(airframe, air, EffectiveLoad, Beta, RollRate);
            }
            VaporFlight flight = Flight;
            flight.BodyCover = KspBaseline.BodyCover(UpstreamMapping ? LoadG : EffectiveLoad);
            if (!UpstreamMapping)
            {
                double sheet = KspBaseline.SheetStrength(LoadG, Speed);
                // the wing keeps a bounded veil in a post-stall hold; canards and the body stay on the measured load.
                double wingSheet = Math.Max(sheet, KspBaseline.PostStallSheet(Alpha, Speed, landing));
                flight.BodyCover = KspBaseline.BodyCover(Math.Max(LoadG, 0)) * KspBaseline.SheetSpeed(Speed);
                flight.CanardCut = 1 - sheet;
                flight.SheetCut = 1 - wingSheet;
                flight.RopeGain = KspBaseline.RopeStrength(EffectiveLoad);
                flight.RopeRadiusM = KspBaseline.RopeRadiusM;
            }
            Flight = flight;
            // the lift's strength comes from the load, but the air goes where it really
            // goes: the patches' drift and the vortices' trails follow the measured angles.
            bool tips = Effects.TipVortices;
            state = VaporFieldModel.Compute(airframe, Flight, air, look, shape, tips, state, tips ? history : null, true, Alpha, Beta);
            if (!state.Visible) return false;

            if (!shapeWritten)
            {
                material.WriteShape(state.Shape, state.Path);
                shapeWritten = true;
            }
            if (tips) material.WriteTrails(state.Trails, state.SecondTrails);
            material.WriteField(state.Field, VaporFieldModel.Constants(state.Field));
            boxMin = new Vector3((float)state.BoundsMin[0], (float)state.BoundsMin[1], (float)state.BoundsMin[2]);
            boxMax = new Vector3((float)state.BoundsMax[0], (float)state.BoundsMax[1], (float)state.BoundsMax[2]);
            if (input.WingFlex != Vector2.zero)
            {
                float s = (float)Math.Max(shape.SemispanM - shape.RootSpanM, 0);
                float rise = (input.WingFlex.x + input.WingFlex.y * s) * s;
                boxMax.y += Mathf.Max(rise, 0f);
                boxMin.y += Mathf.Min(rise, 0f);
            }
            material.WriteFlex(input.WingFlex, (float)shape.RootSpanM);
            material.WriteBox(boxMin, boxMax);
            cube.bounds = new Bounds((boxMin + boxMax) / 2, boxMax - boxMin);

            Vector3 sunLight, skyLight;
            Light(input.Daylight, out sunLight, out skyLight);
            if (input.SunTint != Vector3.zero) sunLight = Vector3.Scale(sunLight, input.SunTint);
            if (input.SkyScale > 0f) skyLight *= input.SkyScale;
            material.WriteLight(Quaternion.Inverse(q) * input.SunDirection.normalized, sunLight, skyLight, (float)look.Exposure);
            material.WriteLook(look, opacity);
            objectToWorld = Matrix4x4.TRS(input.Position, q, Vector3.one);
            Visible = true;
            return true;
        }

        // For each camera that draws it, after Step: the march's budget for that view, and the draw.
        public void Draw(Camera camera, int layer)
        {
            if (!Visible || camera == null) return;
            float stepScale, detail;
            VaporMaterial.Budget(camera, objectToWorld, boxMin, boxMax, out stepScale, out detail);
            StepScale = stepScale;
            Detail = detail;
            material.WriteFrame(time, seed, stepScale, detail, MaxSteps, Effects);
            material.WritePixelStep(camera);
            material.Material.SetVector("_VaporFog", new Vector4(RenderSettings.fog ? (int)RenderSettings.fogMode : 0,
                RenderSettings.fogDensity, RenderSettings.fogStartDistance, RenderSettings.fogEndDistance));
            material.Material.SetColor("_VaporFogColor", QualitySettings.activeColorSpace == ColorSpace.Linear ? RenderSettings.fogColor.linear : RenderSettings.fogColor);
            float distance = Mathf.Max(1, Vector3.Distance(camera.transform.position, objectToWorld.MultiplyPoint3x4((boxMin + boxMax) * 0.5f)));
            float diameter = (float)Mathf.Max((float)state.Field.EdgeRopeM * 2, 0.03f);
            float pixels = diameter * camera.pixelHeight / (2 * distance * Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f));
            bool protectRopes = (Effects.LeadingEdgeVortices || Effects.TipVortices) && pixels < 4;
            if (!VaporVolumePass.Submit(camera, cube, objectToWorld, material.Material, protectRopes))
                Graphics.DrawMesh(cube, objectToWorld, material.Material, layer, camera, 0, null, false, false);
        }

        public Vector3 CameraLocalPosition(Camera camera)
        {
            return objectToWorld.inverse.MultiplyPoint3x4(camera.transform.position);
        }

        // Upstream's measureRoll: the turn since the last frame, in the craft's own frame then,
        // about the canonical x; positive puts the +z wing down.
        void MeasureRoll(Quaternion attitude, float dt)
        {
            if (haveAttitude && dt > 1e-4f && dt < 1f)
            {
                Quaternion turn = Quaternion.Inverse(lastAttitude) * attitude;
                if (turn.w < 0) turn = new Quaternion(-turn.x, -turn.y, -turn.z, -turn.w);
                RawRoll = 2f * Mathf.Atan2(turn.x, turn.w) / dt;
                RollRate += (RawRoll - RollRate) * (1f - Mathf.Exp(-dt / RollResponseS));
            }
            lastAttitude = attitude;
            haveAttitude = true;
        }

        void MeasureLoad(float rawLoadG, float dt)
        {
            if (!haveLoad) { LoadG = rawLoadG; haveLoad = true; return; }
            float response = Mathf.Abs(rawLoadG) >= Mathf.Abs(LoadG) ? LoadResponseS : LoadReleaseS;
            if (dt > 0f) LoadG += (rawLoadG - LoadG) * (1f - Mathf.Exp(-Mathf.Min(dt, MaxDeltaS) / response));
        }

        // The sun's and the sky's light, from night to day.
        static void Light(float daylight, out Vector3 sun, out Vector3 sky)
        {
            float t = Mathf.Clamp01(daylight);
            sun = DaySun * (DaySunIntensity * t);
            sky = Vector3.Lerp(NightSky, DaySky, t) * Mathf.Lerp(NightSkyIntensity, DaySkyIntensity, t);
        }

        void Create()
        {
            material = new VaporMaterial(shader);
            cube = VaporMaterial.UnitCube();
            cube.MarkDynamic();
        }

        // `immediate` outside play mode (the editor's batch tools).
        public void Destroy(bool immediate)
        {
            if (material != null) material.Destroy(immediate);
            if (cube != null)
            {
                if (immediate) UnityEngine.Object.DestroyImmediate(cube);
                else UnityEngine.Object.Destroy(cube);
            }
            material = null;
            cube = null;
            Visible = false;
        }
    }
}
