using UnityEngine;
using KSP;
using System.Collections;
using System.Collections.Generic;

// The name and version a companion mod depends on (KSPAssemblyDependency).
[assembly: KSPAssembly("WingtipVortex", 1, 5)]

// One per craft, created and destroyed by WingtipVortexManager and bound to that craft for its
// whole life.
public class WingtipVortex : MonoBehaviour
{
    public const string ModVersion = "1.5.0";

    // The craft this controller draws. Set by the manager immediately after AddComponent, which
    // is before Start() runs.
    public Vessel target;

    // Captured at Start, so lines logged from OnDestroy after the craft is gone keep their tag.
    // Localised: stock craft carry names like "#autoLOC_501256".
    private string logLabel;

    void Log(string msg)
    {
        Debug.Log(logLabel != null ? $"[VORTEX] [{logLabel}] {msg}" : "[VORTEX] " + msg);
    }

    // Static materials are destroyed on scene load, so they are fetched through accessors that
    // rebuild them, with shader fallbacks, and a missing shader never renders magenta.
    private static Material sharedTrailMat;
    private static Material sharedLineMat;

    static Material MakeAdditive(string label)
    {
        Shader sh = Shader.Find("Legacy Shaders/Particles/Additive");
        if (sh == null) sh = Shader.Find("Particles/Additive");
        if (sh == null) sh = Shader.Find("Sprites/Default");
        Debug.Log($"[VORTEX] {label} material built, shader={(sh != null ? sh.name : "NONE FOUND")}");
        return (sh != null) ? new Material(sh) : null;
    }

    static Material TrailMat()
    {
        if (sharedTrailMat == null) sharedTrailMat = MakeAdditive("trail/tube");
        return sharedTrailMat;
    }

    static Material LineMat()
    {
        if (sharedLineMat == null) sharedLineMat = MakeAdditive("line");
        return sharedLineMat;
    }

    private Vessel vessel;
    private Transform vesselTransform;

    private float boundsRefreshTimer = 0f;
    private const float boundsRefreshInterval = 0.5f;

    private List<TrailRenderer> trails = new List<TrailRenderer>();
    private List<LineRenderer> lines = new List<LineRenderer>();
    private List<GameObject> trailObjs = new List<GameObject>();
    private List<Transform> anchors = new List<Transform>();

    // Rigid anchors: each anchor's offset is captured in the root part's frame, and the wake
    // follows that point on the airframe, not the flexing part, so joint wobble is not drawn into
    // the rope. Falls back to the part's own position if the root has changed or the two disagree
    // by more than a part flexes.
    private List<Part> anchorRoots = new List<Part>();
    private List<Vector3> anchorRootOffsets = new List<Vector3>();
    private bool rigidAnchors = true;
    private float rigidAnchorMaxFlexSpans = 0.25f;   // beyond this the capture is not trusted
    private float peakTipFlex = 0f;                  // metres, reported with the session peaks

    Vector3 AnchorPosition(int i)
    {
        Transform anchor = anchors[i];
        Part root = anchorRoots[i];
        if (!rigidAnchors || root == null || root.vessel != vessel) return anchor.position;
        Vector3 rigid = root.transform.TransformPoint(anchorRootOffsets[i]);
        float flex = (anchor.position - rigid).magnitude;
        if (flex > rigidAnchorMaxFlexSpans * Mathf.Max(vesselSpan, 1f)) return anchor.position;
        if (flex > peakTipFlex) peakTipFlex = flex;
        return rigid;
    }
    private List<float> strengths = new List<float>();
    // Each source's lateral reach as a fraction of the main wing's; sets the load a secondary
    // surface needs before its vortex shows.
    private List<float> spanRatios = new List<float>();

    // Wake tube: a generated mesh, so the cross-section can twist about the centreline (a
    // TrailRenderer billboards to the camera and cannot). A tube, not a single ribbon, which goes
    // edge-on and disappears.
    private bool enableRibbonMesh = true;
    private bool ribbonFirstSourceOnly = false;

    // Every source gets a tube. The inward curve scales with shed strength, so a weak secondary
    // curves proportionally less.
    private bool ribbonMainOnly = false;

    // Hard cap on rope length in metres. Below ~500 m/s the lifetime binds first.
    private float ribbonMaxLength = 2500f;

    // Wake smoothing: Laplacian smoothing over a window near the head, one pass per frame. Ring 0
    // is never touched, so the head stays on the tip; the window is bounded, so each ring is
    // smoothed a fixed number of times. Attenuation per pass is 1 - amount*(1 - cos(2*pi/N)) for a
    // wavelength of N rings.
    private int ribbonSmoothWindow = 16;      // rings behind the head that get a pass each frame
    private float ribbonSmoothAmount = 0.6f;  // 0 disables
    private int ribbonSides = 5;                // cross-section vertices per ring
    private int ribbonMaxPoints = 320;          // ring budget; buffers are sized to this once
    private float ribbonMinPointDist = 0.8f;    // metres of travel before a new ring is laid
    private float ribbonRadiusScale = 0.15f;    // metres of radius per unit shed strength
    private float ribbonTwistPerMetre = 0.25f;  // radians of cross-section rotation per metre

    // The cross-section is an ellipse (ribbonAspect) with one brighter side (ribbonStripe), so the
    // twist is visible.
    private float ribbonAspect = 0.4f;          // minor axis as a fraction of major
    private float ribbonStripe = 0.6f;          // 0 = uniform, 1 = one side fully dark
    // Twist per ring (radians) at which the section starts rounding off, and is fully round.
    // The ellipse aliases at pi/2 per ring; this finishes well before it. See BuildRibbonMesh.
    private float twistAliasStart = 0.5f;
    private float twistAliasFull = 1.0f;

    // Centreline helix. Off at 0; the machinery stays, gated on amplitude > 0.
    private float ribbonHelixAmp = 0f;          // metres of centreline displacement, fully open

    // Inward curve: the rope bends toward the centreline over ribbonInwardGrow metres, then runs
    // parallel. The direction is set when the ring is laid.
    private float ribbonInwardAmp = 1.2f;       // metres of convergence, once evened out
    private float ribbonInwardGrow = 20f;       // metres of wake over which it bends in
    private float ribbonHelixWavelength = 50f;  // metres per turn
    private float ribbonHelixGrow = 8f;         // metres of wake before it reaches full amplitude
    private bool loggedRibbon = false;
    // Brightness falloff along the rope: full until ribbonPeakFraction of its age, then a taper
    // over the remainder.
    private float ribbonPeakFraction = 0.45f;   // age at which the tail fade begins
    private float ribbonFadePow = 1.1f;

    // Head ramp: alpha and width rise from near zero over the first ribbonHeadRampFraction of the
    // rope, so it does not start as a full-strength line.
    private float ribbonHeadRampFraction = 0.40f;
    private float ribbonHeadWidthFloor = 0.15f; // width at the anchor, as a share of full

    // Fraction of the rope, by position in the buffer rather than age, over which the tail closes
    // to nothing.
    private float ribbonEndFade = 0.25f;

    // Brightness variation along the rope, sampled against the birth odometer so it stays fixed in
    // the air and streams backward. Scaled by contrailBlend: at altitude brightness is otherwise
    // uniform and the rope shows no motion.
    private float ribbonFlowDepth = 0.45f;      // brightness swing, at full contrail blend
    private float ribbonFlowScale = 0.03f;      // cycles per metre (~33 m per Perlin unit)
    private List<WakeRibbon> ribbons = new List<WakeRibbon>();

    // Unit-circle table for the cross-section, built once, so each ring needs two trig calls
    // instead of 2*K.
    private float[] ringCos, ringSin;

    private List<Queue<Vector3>> lineHistory = new List<Queue<Vector3>>();
    private int historyLength = 8;

    private List<bool> trailWasActive = new List<bool>();
    private List<bool> lineWasActive = new List<bool>();
    private List<Vector3> lastFlows = new List<Vector3>();
    private List<Vector3> lastAnchorPositions = new List<Vector3>();

    private bool useLineMode = false;
    private float currentIntensity = 0f;

    // Both fast: circulation tracks the lift the wing makes now. Persistence comes from the
    // recorded trail history (ApplyShedAppearance).
    private float buildSpeed = 5f;
    private float decaySpeed = 4f;
    private float lineActivationBlend = 0f;
    private float lineActivationSpeed = 0.6f;

    private float maxDelta = 0.05f;
    private float abnormalFrameThreshold = 0.20f;

    // High-altitude condensation regime, keyed on ambient temperature so it is body-relative. 253 K
    // (-20 C) is the warm limit and 233 K (-40 C) the cold anchor, raised per body to the coldest
    // air it has (RefreshContrailBand).
    private float contrailTempWarm = 253f;   // K: above this, no persistent contrail
    private float contrailTempCold = 233f;   // K: at or below this, fully developed
    private float contrailBandMin = 12f;     // K: never let the band collapse to a step

    // Illumination: a contrail is visible by scattering sunlight, so brightness follows
    // VortexVapor.Sunlight (1 with the sun above the craft's horizon, fading through twilight). The
    // night floor keeps the wake faintly visible, since an additive wake is brighter against a dark
    // sky.
    private float nightFloorScale = 0.20f;   // ambient floor in full shadow
    private float dayLightScale = 0.70f;     // brightness in full daylight; 1.0 for no dimming
    private bool loggedSunFlux = false;
    private bool loggedSecondaryGate = false;
    private bool loggedTrailMat = false;
    private CelestialBody contrailBody = null;
    private float bodyContrailWarm = 253f, bodyContrailCold = 233f;
    private float contrailTimeMin = 2.5f;          // trail lifetime at the bottom of the band
    private float contrailTimeMax = 5.0f;          // ...and at the top
    private float contrailMinVertexDist = 1.5f;    // contrails are near-straight; sample coarsely
    private float contrailWidthScale = 2.2f;

    // Contrail spreading: flat until contrailSpreadStart, then a quadratic widening to 1 +
    // contrailSpreadMax at the end, scaled by contrailBlend. Opacity falls as
    // spread^-contrailSpreadDimPow.
    private float contrailSpreadStart = 0.5f;     // fraction of lifetime before breakup
    private float contrailSpreadMax = 2.0f;       // extra width at the very end, x
    private float contrailSpreadDimPow = 0.4f;
    // At full contrailBlend the tail-closing window shrinks to this, so the closure rounds off the
    // tip without cancelling the spread.
    private float contrailEndFade = 0.08f;

    float ContrailSpread(float ageFrac, float contrailBlend)
    {
        float s = Mathf.Clamp01((ageFrac - contrailSpreadStart) / Mathf.Max(1f - contrailSpreadStart, 0.01f));
        return 1f + contrailSpreadMax * contrailBlend * s * s;
    }

    // Line mode is only the extreme-velocity regime.
    private float lineModeSpeed = 600f;

    // Wake helix; off at 0. Applied to stored vertices with a radius that grows with each vertex's
    // age, so the origin stays on the anchor. Winding is a wavelength in metres.
    private float helixRadiusMax = 0f;       // metres, fully developed
    private float helixWavelength = 25f;     // metres per full turn (1:17 at full amplitude)
    private float helixGrowTime = 0.8f;      // seconds downstream to reach full radius

    // Driven by load factor, which needs no geometry guess, rather than an angle-of-attack proxy.
    private float helixBaseDrive = 0.35f;    // drive at or below helixGMin, so it never vanishes
    private float helixGMin = 1f;
    private float helixGFull = 5f;
    private bool loggedHelixCheck = false;

    // Half-width of the band at the wing tip inside which the forward-most vertex wins.
    private const float tipBand = 0.15f;

    // Secondary-source gate: a non-main surface needs both an onset load and an airspeed. The onset
    // is a multiple of the main wing's, scaled by 1/spanRatio down to this floor, since circulation
    // grows with span share over lift share.
    private float secondaryOnsetMultMin = 2.0f;   // biggest secondaries still need 2x the wing
    private float secondaryOnsetMultMax = 4.0f;   // small nose canards, capped here
    private float secondaryGSpan = 1.0f;          // load range from onset to full strength
    // Airspeed floor for secondaries: a practical guard, not a circulation term. Raw airspeed, not
    // dynamic pressure, which falls with altitude.
    private float secondarySpeedMin = 45f;
    private float secondarySpeedFull = 90f;

    // Secondary wakes are capped to this length in metres, because a canard's vortex merges into
    // the wing's system within a few chords instead of trailing. Scaled by span ratio; at full tip
    // likeness it returns to the main-wing length.
    private float secondaryWakeLength = 25f;   // metres of visible cord at full secondary size
    // Seconds; only guarantees the tube enough rings to be geometry.
    private float secondaryMinLife = 0.05f;

    // Tip likeness: a surface reaching the wing's span is treated as a wingtip. Below
    // mainLikeRatioMin it gets full secondary treatment, ramping to main-wing treatment as it
    // approaches the wing's span.
    private float mainLikeRatioMin = 0.92f;    // below this, full secondary treatment
    private float mainLikeRatioFull = 0.95f;   // at or above this, identical to a main wingtip

    // Once a secondary clears its onset its strength is anchored near 1; small surfaces are held
    // down by the quadratic in span ratio.
    private float secondaryStrengthMin = 0.95f; // strength at the top of the secondary range

    // Test switch: gives secondaries strength 1 and span ratio 1, bypassing every secondary gate.
    private bool treatSecondariesAsMain = false;

    // SOURCE SELECTION thresholds. See FindVortexSources.
    private float minSourceArea = 0.3f;            // rejects trim tabs and greebles
    private float minLateralOffset = 0.3f;         // rejects centre sections and vertical fins
    private float secondaryMinSeparation = 0.5f;   // metres a surface must sit clear of the wing
    private float aftSecondarySpanRatio = 0.8f;    // an aft surface must be this fraction of the
                                                   // main wing's span to count as a second wing
                                                   // rather than a stabiliser

    // Maneuver-puff trail lifetime, at least as long as the decay it shows.
    private float maneuverTimeMin = 1.2f;
    private float maneuverTimeMax = 3.5f;

    // Circulation model: strength scales with m/b (mass over span), soft-saturated by 2x/(1+x).
    // Speed is not a term, since the load-factor and onset scaling already carry it.
    private float refMass = 8f;      // metric tons
    private float refSpan = 16f;     // metres
    private float sizeFactor = 1f;   // refreshed on the bounds timer

    // Circulation onset: the load needed for onset scales with rho*V relative to the reference
    // condition (sea level, 250 m/s, scale 1), because the same lift makes more circulation in slow
    // or thin air. The onset span scales with it.
    private float onsetLoadRef = 3.0f;        // load factor for onset at the reference condition
    private float onsetSpanRef = 10f;         // ...and the range from onset to full intensity
    private float onsetRefDensity = 1.225f;   // kg/m^3
    private float onsetRefSpeed = 250f;       // m/s
    // The floor keeps onset above 1 g, so a parked craft (rawLiftG falls back to geeForce, 1 g)
    // makes no vortices.
    private float onsetScaleMin = 0.40f;
    private float onsetScaleMax = 2.5f;

    // Below this there is no meaningful airflow, so rawLiftG is gear load or noise.
    private float flyingSpeedMin = 20f;   // m/s, fully gated below
    private float flyingSpeedFull = 40f;  // m/s, ungated above

    // Ground roll: the lift-coefficient proxy L/(q*b^2) does not shrink with speed on the takeoff
    // roll, so airborne is gated directly (what carries the aircraft). Not a ground-proximity test:
    // short-final vapor is in ground effect.
    private float airborneBlend = 0f;
    private float airborneBlendSpeed = 2.5f;   // ~0.4 s fade in at rotation, so it does not pop

    // Humid boundary layer: near the surface the air is close to dew point, so little core pressure
    // drop is needed. Depth is density relative to the body's sea-level density, and 'working hard
    // for its speed' is the proxy L/(q*b^2) = CL/aspect ratio.
    private float humidDensityMin = 0.62f;   // fraction of sea-level density where the layer ends
    private float humidLoadMin = 0.10f;      // CL/AR: ordinary cruise sits far below this
    private float humidLoadFull = 0.26f;     // ...near CLmax for a typical aspect ratio
    // Second requirement: the wings must be carrying the aircraft (also covers touch-and-go).
    private float humidLiftShareMin = 0.80f;
    private float humidLiftShareFull = 1.00f;
    private float humidFloor = 0.40f;        // peak visibility floor on a slow, hard-working wing

    // Viscous core growth: Lamb-Oseen r(t) = sqrt(r0^2 + 4*alpha*nu*t), alpha = 1.25643, with
    // Squire's eddy viscosity nu_t = delta * Gamma. The molecular and turbulent terms are summed.
    private float coreGrowthEnable = 1f;      // 0 restores the pure tailRatio look; blends 0-1
    private float squireDelta = 2e-4f;        // nu_t / Gamma; literature sits around 1e-4 to 1e-3
    private const float lambOseenAlpha = 1.25643f;
    // Visible core radius at shedding, as a fraction of span. The vapor fills the low-pressure
    // region, which is wider than the viscous core (3-5% of span).
    private float coreSpanFraction = 0.08f;
    private float coreGrowthMax = 2.5f;       // last line of defence on the multiplier
    // Dimming as the core spreads: the pressure deficit falls as 1/r^2, but visibility is a
    // threshold effect, so the exponent is below 2.
    private float coreDimPow = 0.8f;
    // Per-frame result, in 1/s: the bracket of r(t)/r0 = sqrt(1 + rate*t).
    private float coreGrowthRate = 0f;
    // Two one-shot latches: one for the first manoeuvre (usually low), one once the contrail band
    // is reached.
    private bool loggedCoreGrowth = false;
    private bool loggedCoreGrowthHigh = false;

    // Viscosity from Sutherland's law at the ambient temperature: mu(T) = mu0 * (T/T0)^1.5 *
    // (T0+S)/(T+S).
    private float sutherlandMu0 = 1.716e-5f;   // Pa.s at T0
    private float sutherlandT0 = 273.15f;      // K
    private float sutherlandS = 110.4f;        // K
    // No Reynolds gate: Gamma/nu is 1e6 to 1e8 in every case that draws anything, so the wake is
    // always turbulent. The molecular term is capped, since nu = mu/rho diverges in thin air.
    private float nuMolecularCap = 5e-4f;      // m^2/s
    // Ambient stirring rides the depth-in-atmosphere blend of the humid boundary layer.
    private float ambientTurbScale = 0.6f;     // extra eddy viscosity at full atmospheric depth

    // Ground effect (method of images): the mirror vortex cancels the pair's self-induced descent
    // and pushes each vortex outboard. This moves and diffuses the rope; it never gates visibility.
    // For a pair at height h with separation b0 = (pi/4)*b: lateral drift v = Gamma/(4*pi) * b0^2 /
    // (h * (4h^2 + b0^2)); descent retained = 4h^2 / (4h^2 + b0^2). h is floored at b0/2 in the
    // drift.
    private float groundEffectEnable = 1f;      // 0 disables both drift and near-ground diffusion
    private float groundSpreadScale = 1f;
    private float groundSpreadMaxSpans = 1.5f;  // backstop on total lateral drift
    // Ground boundary-layer separation, as extra eddy viscosity scaled by the ground factor.
    private float groundTurbScale = 1.5f;
    // Beyond this many spans the image terms are below 1%.
    private float groundEffectCeilingSpans = 10f;
    private bool loggedGroundEffect = false;

    // Vortex breakdown, spiral and bubble modes. Swirl ratio S = 0.715*Gamma/(2*pi*rc*V) = 1.42 *
    // clProxy, with rc the viscous core (4% of span). Onset from Spall, Gatski & Grosch (1987):
    // Rossby number below ~0.65, swirl above ~1.5, ramped in slightly before.
    private float breakdownEnable = 1f;
    private float viscousCoreSpanFraction = 0.04f;
    private float breakdownSwirlOnset = 1.4f;
    private float breakdownSwirlFull = 2.2f;
    private float bubbleSwirlMin = 1.7f;        // below: pure spiral
    private float bubbleSwirlMax = 2.1f;        // above: pure bubble
    // The burst point moves upstream as swirl rises.
    private float breakdownStationFarSpans = 8f;
    private float breakdownStationNearSpans = 1f;
    private float breakdownSmoothTime = 0.3f;   // s; Gamma reads raw lift and would otherwise flicker
    private float breakdownDevelopTime = 0.25f; // s for the broken structure to develop past the burst
    // Extra diffusion accumulates only after the burst.
    private float breakdownDiffusion = 1.5f;
    private float breakdownGrowthMax = 2f;
    // Depth at which the spiral and bubble reach full size.
    private float breakdownSizeSaturateDepth = 0.3f;
    // Spiral geometry, in visible core radii. spiralDecayTurns bounds the spiral to a turn or two;
    // the minimum rings per turn prevents aliasing.
    private float spiralAmpCores = 0.5f;
    private float spiralWavelengthCores = 20f;
    private float spiralDecayTurns = 1.5f;      // e-folding of the displacement, in wavelengths
    private float spiralMinRingsPerTurn = 8f;
    private float spiralPrecession = 1.5f;      // rad/s; 0 freezes the corkscrew in the air
    private float spiralFadeRate = 0.8f;        // 1/s at full depth
    // Bubble geometry; the flare is not paired with dimming.
    private float bubbleAmp = 1.5f;             // extra radius at the bubble's widest, full depth
    private float bubbleLengthCores = 6f;
    private float bubbleMinRings = 4f;
    private float bubbleFadeRate = 2.5f;        // 1/s at full depth; the core is gone soon after
    private float smoothedSwirl = 0f;
    private bool loggedBreakdown = false;
    // Session peaks, reported on destroy, to check the breakdown thresholds against real flights.
    private float peakSwirl = 0f, peakSwirlSpeed = 0f, peakSwirlGamma = 0f;
    private float peakGroundFac = 0f;

    // Per-frame values for PushRibbonPoint and BuildRibbonMesh.
    private float frameGamma = 0f, frameSpeed = 1f, frameB0 = 1f, frameR0Vis = 0.1f;
    private float frameAGL = -1f, frameGroundFac = 0f;
    private bool frameGroundOn = false;
    private Vector3 frameUp = Vector3.up, frameVesselPos = Vector3.zero;
    private float frameTurbRate = 0f;           // coreGrowthRate before coreGrowthEnable
    private float frameBreakT = 1e6f, frameBreakDepth = 0f, frameBreakMode = 0f;

    // Wake sink: nudges every existing trail vertex down on each tick, a uniform sink rate. 0
    // disables.
    private float trailSinkRate = 1.2f;      // m/s at the young end; ~300 ft/min. 0f disables.
    private float sinkAgeFalloff = 0.35f;    // rate multiplier at the oldest end ("slowing as they age")

    // 0 applies the sink every frame; batching it teleports the trail and detaches it from the tip.
    private float sinkUpdateInterval = 0f;
    private float sinkTimer = 0f;

    // Ring buffer of shed-strength history, replayed by ApplyShedAppearance. 64 * 0.08 s covers
    // contrailTimeMax.
    private const int shedSamples = 64;
    private const int shedKeyCount = 8;       // Gradient accepts at most 8 alpha keys
    private float shedSampleInterval = 0.08f;
    private float shedSampleTimer = 0f;
    private int shedWrite = 0;
    private List<float[]> shedHistory = new List<float[]>();
    private List<float> trailAges = new List<float>();       // age of the OLDEST live point
    private List<float> trailHeadAges = new List<float>();   // age of the NEWEST live point
    private float trailAlphaGain = 1.0f;

    // Reused scratch: TrailRenderer copies curve and gradient data on assignment. Width gets a
    // denser key set than Gradient's 8-key limit, to resolve the head ramp.
    private const int widthKeyCount = 14;
    private Keyframe[] widthKeys = new Keyframe[widthKeyCount];

    // Leading-end shape: the trail converges toward the anchor over rollupMinMetres (an absolute
    // length, converted to a curve fraction each frame) and stops at headWidthFraction, not zero,
    // so there is a visible origin.
    private float rollupMinMetres = 1.5f;
    private float rollupMaxMetres = 12f;
    private float rollupSizeScale = 0.25f;   // fraction of vesselSize used as the roll-up length
    private float headWidthFraction = 0.10f;
    private int headKeyCount = 6;            // of widthKeyCount, spent inside the ramp
    private int trailCapVertices = 3;        // rounds both ends instead of cutting them square
    private bool loggedWidthProfile = false;
    private GradientAlphaKey[] shedAlphaKeys = new GradientAlphaKey[shedKeyCount];
    private GradientColorKey[] shedColorKeys = new GradientColorKey[] {
        new GradientColorKey(Color.white, 0f),
        new GradientColorKey(new Color(0.85f, 0.85f, 0.85f), 1f)
    };
    private AnimationCurve shedCurve = new AnimationCurve();
    private Gradient shedGradient = new Gradient();
    private Gradient lineGradient = new Gradient(); // line-mode's own scratch gradient — see its use below

    // Cached on the bounds timer; both calls allocate or walk every part.
    private List<ModuleLiftingSurface> liftSurfaces = new List<ModuleLiftingSurface>();
    private float vesselMassTons = 1f;
    private float gravityAccel = 9.81f;

    // FAR replaces ModuleLiftingSurface, so under FAR the load factor sums each
    // FARWingAerodynamicModel's own lift, not FARAPI.VesselAerodynamicForce, which includes
    // fuselage body lift. Bound by reflection, so FAR stays optional.
    static class FARBridge
    {
        private static System.Type wingType;                       // ferram4.FARWingAerodynamicModel
        private static System.Reflection.FieldInfo fiWorldForce;   // this wing's force, world space, kN
        private static System.Reflection.FieldInfo fiShielded;
        private static bool initialized = false;
        private static bool available = false;

        public static bool Available { get { return available; } }

        public static void Init()
        {
            if (initialized) return;
            initialized = true;
            try
            {
                foreach (System.Reflection.Assembly asm in System.AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (!asm.GetName().Name.StartsWith("FerramAerospaceResearch")) continue;
                    wingType = asm.GetType("ferram4.FARWingAerodynamicModel");
                    if (wingType == null) continue;
                    var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
                    fiWorldForce = wingType.GetField("worldSpaceForce", flags);
                    fiShielded = wingType.GetField("isShielded", flags);
                    available = fiWorldForce != null && fiWorldForce.FieldType == typeof(Vector3);
                    break;
                }
            }
            catch { available = false; }
            Debug.Log(available
                ? "[VORTEX] FAR detected — reading lift from FAR's wing modules"
                : "[VORTEX] FAR not detected (or API mismatch) — reading lift from stock modules");
        }

        // The vessel's FAR wing modules; control surfaces derive from FARWingAerodynamicModel.
        public static void FindWings(Vessel v, List<PartModule> into)
        {
            into.Clear();
            if (!available || v == null) return;
            foreach (Part p in v.parts)
            {
                if (p == null) continue;
                foreach (PartModule pm in p.Modules)
                    if (wingType.IsInstanceOfType(pm)) into.Add(pm);
            }
        }

        // Sum of each wing's lift magnitude, kN, matching the stock sum. Lift is the force less its
        // component along the flow.
        public static float GetLiftKN(Vessel v, List<PartModule> wings, Vector3 flowNormalized)
        {
            if (!available || v == null || v.atmDensity <= 0) return 0f;
            float sum = 0f;
            try
            {
                for (int i = 0; i < wings.Count; i++)
                {
                    PartModule pm = wings[i];
                    if (pm == null) continue;   // Unity null check also catches destroyed parts
                    if (fiShielded != null && (bool)fiShielded.GetValue(pm)) continue;
                    Vector3 f = (Vector3)fiWorldForce.GetValue(pm);
                    sum += (f - Vector3.Dot(f, flowNormalized) * flowNormalized).magnitude;
                }
            }
            catch
            {
                // Stop trying after a FAR failure rather than pay the try/catch every frame.
                available = false;
                return 0f;
            }
            return sum;
        }
    }
    private List<PartModule> farWings = new List<PartModule>();

    // Detected by module name, so FAR is not a dependency. Lift and control stay separate, as in
    // stock.
    internal static bool IsLiftPart(Part p)
    {
        return p.Modules.Contains("ModuleLiftingSurface") || p.Modules.Contains("FARWingAerodynamicModel");
    }

    internal static bool IsControlPart(Part p)
    {
        return p.Modules.Contains("ModuleControlSurface") || p.Modules.Contains("FARControllableSurface");
    }

    internal static bool IsAeroSurface(Part p) { return IsLiftPart(p) || IsControlPart(p); }

    // Low-pass on the lift measurement, not the build/decay rates: raw liftForce is noisy from
    // joint flex. 0.12 s still reads a real unload as immediate.
    private float smoothedLiftG = 1f;
    private float liftSmoothTime = 0.12f;

    private Bounds vesselBounds;
    private float vesselSize;

    // The vessel's frame, resolved once by ComputeVesselAxes and held in the root part's local
    // space; world directions are derived on demand.
    private Vector3 axLateralLocal = Vector3.right;
    private Vector3 axLengthLocal = Vector3.forward;
    private Vector3 axVertLocal = Vector3.up;
    private bool axLengthSigned = false;         // false: nose direction could not be determined

    private Vector3 axLateral { get { return vesselTransform.TransformDirection(axLateralLocal); } }
    private Vector3 axLength { get { return vesselTransform.TransformDirection(axLengthLocal); } }
    private Vector3 axVert { get { return vesselTransform.TransformDirection(axVertLocal); } }
    private float vesselSpan = 16f;   // ExtentAlong(vesselBounds, right) * 2; see massSpanFactor

    // Rogue renderer rejection: visual mods give meshes enormous bounds to defeat culling, which
    // would poison the airframe measurement.
    private float maxPartExtent = 200f;   // metres: far above any real part, far below the garbage

    // Second tier: large effect meshes (plumes, reentry) pass maxPartExtent. A large renderer is
    // checked against its part's collider extent, which effect meshes lack.
    private float rendererColliderCheck = 5f;   // metres: below this, no collider test at all
    private float rendererColliderSlack = 3f;   // allowed multiple of the part's collider size
    private bool boundsLogged = false;
    private bool rogueRendererLogged = false;

    // Previous frame's Krakensbane-relative speed, for the discontinuity guard.
    private float lastRbSpeed = 0f;

    // Vessel readiness: detection runs once, so it waits until the vessel is unpacked and its parts
    // have settled, or the length axis can resolve wrong.
    private float sourceReadyTimeout = 30f;   // seconds; start anyway rather than never starting
    private int sourceSettleFrames = 5;       // physics frames to let part positions settle
    private float sourceConfirmDelay = 3f;    // see the confirmation pass in Start()

    // Staging: sources hold a Transform on the part they were found on. The stale check runs on the
    // bounds cadence and catches decoupling, docking, destruction and vessel switching.
    private float staleCheckTimer = 0f;
    private const float staleCheckInterval = 0.5f;

    // Skips vertically-launched craft entirely. Decided once, at first detection, on the pad.
    private bool skipVerticalLaunchers = false;
    private float rocketNoseUpDot = 0.85f;

    // Slenderness (length over width, from part positions) separates rockets from aircraft: the MiG
    // reads ~1.2, the Redstone ~12, and 4 sits between.
    private float rocketSlenderness = 4.0f;

    // Line mode is off for rockets: its procedural length is much shorter than the trail's, so
    // switching reads as a cut-off.
    private bool rocketUseLineMode = false;
    private int verticalLauncher = -1;   // -1 unknown, 0 no, 1 yes

    // Rocket fin sources: the aircraft rule assumes left and right, which fails on a cruciform
    // tail. Take the lifting surfaces at the bottom of the stack, one vortex each, up to four, with
    // offset measured radially from the roll axis.
    private float finBandFraction = 0.12f;   // how far up from the aft-most surface still counts
    private float finBandMin = 1.5f;         // ...with a floor, for short stacks
    private float minRadialOffset = 0.25f;   // a fin has to actually stick out of the fuselage

    // Rocket fins use the trail/line renderers, not the tube, which assumes roughly steady flight.
    private bool rocketUseRibbons = false;

    // Shutdown time constant shared by both renderers, so they fade together on leaving the
    // atmosphere.
    private float wakeShutdownTau = 0.5f;   // seconds

    // Sector selection: cluster candidates by angle about the roll axis and start a new sector at
    // each gap above sectorGapDeg. Computed and logged, but unused unless useSectorSelection is
    // set.
    private bool useSectorSelection = false;
    private float sectorGapDeg = 40f;
    private int sectorSourceCap = 6;   // performance clamp, not an aerodynamic rule

    IEnumerator Start()
    {
        // See sourceReadyTimeout.
        if (target != null) logLabel = KSP.Localization.Localizer.Format(target.vesselName);
        float waited = 0f;
        while (waited < sourceReadyTimeout)
        {
            Vessel v = target;
            if (v == null) yield break;   // gone before it was ever ready; the manager reaps us
            if (FlightGlobals.ready && v.loaded && !v.packed
                && v.rootPart != null && v.parts != null && v.parts.Count > 0)
                break;
            waited += Time.unscaledDeltaTime;
            yield return null;
        }

        // Unpacked is not enough: part positions settle over the following physics frames.
        for (int f = 0; f < sourceSettleFrames; f++)
            yield return new WaitForFixedUpdate();

        vessel = target;
        if (vessel == null || !vessel.loaded)
        {
            Log($"no ready vessel after {waited:F1}s — not starting");
            yield break;
        }
        vesselTransform = vessel.transform;

        Log($"WingtipVortex v{ModVersion} starting "
                  + $"(vessel ready after {waited:F1}s, packed={vessel.packed})");

        FARBridge.Init();
        TrailMat();
        LineMat();

        // Start from no sources, so a stray set cannot double every wake.
        ClearSources();
        ComputeVesselBounds();
        FindVortexSources();

        GameEvents.onFloatingOriginShift.Add(OnFloatingOriginShift);

        // Confirmation pass: detection runs a second time a few seconds later, while the craft is
        // still on the ground, so a mistimed first pass heals itself.
        if (vessel.LandedOrSplashed)
        {
            yield return new WaitForSeconds(sourceConfirmDelay);
            if (vessel != null && vessel.loaded && !vessel.packed && vessel.LandedOrSplashed)
            {
                Log("confirmation re-detect (still on the ground)");
                ClearSources();
                ComputeVesselBounds();
                FindVortexSources();
            }
        }
    }

    // Tears down every per-source object and the parallel lists that index them.
    // True when any source no longer belongs to the craft being flown (destroyed anchor, or the
    // part left on another vessel).
    bool SourcesStale()
    {
        for (int i = 0; i < anchors.Count; i++)
        {
            Transform a = anchors[i];
            if (a == null) return true;
            Part p = a.GetComponentInParent<Part>();
            if (p == null || p.vessel != vessel) return true;
        }
        return false;
    }

    void ClearSources()
    {
        for (int i = 0; i < ribbons.Count; i++)
        {
            if (ribbons[i] == null) continue;
            if (ribbons[i].mesh != null) Destroy(ribbons[i].mesh);
            if (ribbons[i].obj != null) Destroy(ribbons[i].obj);
        }
        for (int i = 0; i < trailObjs.Count; i++)
            if (trailObjs[i] != null) Destroy(trailObjs[i]);
        for (int i = 0; i < anchors.Count; i++)
            if (anchors[i] != null) Destroy(anchors[i].gameObject);

        ribbons.Clear(); trailObjs.Clear(); anchors.Clear();
        anchorRoots.Clear(); anchorRootOffsets.Clear();
        trails.Clear(); lines.Clear();
        strengths.Clear(); spanRatios.Clear();
        lineHistory.Clear();
        trailWasActive.Clear(); lineWasActive.Clear();
        lastFlows.Clear(); lastAnchorPositions.Clear();
        shedHistory.Clear(); trailAges.Clear(); trailHeadAges.Clear();
        shedWrite = 0;
    }

    void OnDestroy()
    {
        GameEvents.onFloatingOriginShift.Remove(OnFloatingOriginShift);

        // Calibration summary, whether or not either effect ever fired. See peakSwirl.
        if (peakSwirl > 0f || peakGroundFac > 0f)
        {
            Log($"session peaks: swirl={peakSwirl:F2} at V={peakSwirlSpeed:F0}m/s "
                      + $"Gamma={peakSwirlGamma:F0}m2/s (breakdown onset {breakdownSwirlOnset:F2}, "
                      + $"bubble from {bubbleSwirlMin:F2}) | ground factor={peakGroundFac:F2} "
                      + $"| tip flex peak={peakTipFlex:F2}m (removed from the wake)");
        }

        // The vortex objects are unparented to stay in world space, so they need explicit cleanup.
        ClearSources();
    }

    // KSP's FloatingOrigin does not move TrailRenderer vertices, so a shift leaves them in the old
    // frame (the forward spike); they are corrected in place. A vortex is world-anchored, so the
    // correction is the full offset (offset + nonFrame); vessels move by offset alone. LineRenderer
    // points are rebuilt every frame and need no correction.
    void OnFloatingOriginShift(Vector3d offsetVessel, Vector3d nonFrame)
    {
        Vector3 worldOffset = (Vector3)(offsetVessel + nonFrame);

        // Anchors ride the vessel's Transform, so mirror setOffset()'s branch to keep the
        // discontinuity guard from reading an origin shift as a teleport.
        Vector3 anchorOffset = (vessel != null && vessel.loaded && !vessel.packed && !vessel.LandedOrSplashed)
            ? (Vector3)offsetVessel
            : worldOffset;

        for (int i = 0; i < lastAnchorPositions.Count; i++)
            lastAnchorPositions[i] -= anchorOffset;

        if (worldOffset.sqrMagnitude <= 0f) return;

        // Per-index Get/SetPosition: positionCount is read-only and SetPositions would allocate
        // every physics frame.
        for (int i = 0; i < trails.Count; i++)
        {
            var tr = trails[i];
            int n = tr.positionCount;
            for (int j = 0; j < n; j++)
                tr.SetPosition(j, tr.GetPosition(j) - worldOffset);
        }

        // The tube's history needs the same correction, as a plain array walk.
        for (int i = 0; i < ribbons.Count; i++)
        {
            var rb = ribbons[i];
            if (rb == null) continue;
            for (int j = 0; j < rb.count; j++) rb.pts[j] -= worldOffset;
        }
    }

    // Projecting a Bounds onto a direction is the sum of absolute per-axis contributions; a dot
    // product lets opposite axes cancel.
    static float ExtentAlong(Bounds b, Vector3 dir)
    {
        Vector3 e = b.extents;
        return Mathf.Abs(e.x * dir.x) + Mathf.Abs(e.y * dir.y) + Mathf.Abs(e.z * dir.z);
    }

    // Finds the outermost mesh vertex of a part on the given side, in world space. 'Outermost' is
    // span distance from the roll axis (x and y); the forward preference is a tie-break among
    // vertices already at the tip. Scans every MeshFilter and uses sharedMesh.
    // The shedding point on a rocket fin: furthest from the roll axis, then furthest aft, with no
    // side test.
    bool TryFindRadialTip(Part part, out Vector3 tipWorld)
    {
        tipWorld = Vector3.zero;
        var filters = part.GetComponentsInChildren<MeshFilter>();
        if (filters == null || filters.Length == 0) return false;

        float maxR = float.MinValue;
        foreach (var mf in filters)
        {
            if (mf.GetComponentInParent<Part>() != part) continue;
            Mesh mesh = mf.sharedMesh;
            if (mesh == null) continue;
            Vector3[] verts = mesh.vertices;
            for (int v = 0; v < verts.Length; v++)
            {
                Vector3 l = ToVesselFrame(mf.transform.TransformPoint(verts[v]));
                float r = Mathf.Sqrt(l.x * l.x + l.y * l.y);
                if (r > maxR) maxR = r;
            }
        }
        if (maxR == float.MinValue) return false;

        float bestAft = float.MaxValue;
        bool found = false;
        foreach (var mf in filters)
        {
            if (mf.GetComponentInParent<Part>() != part) continue;
            Mesh mesh = mf.sharedMesh;
            if (mesh == null) continue;
            Vector3[] verts = mesh.vertices;
            for (int v = 0; v < verts.Length; v++)
            {
                Vector3 world = mf.transform.TransformPoint(verts[v]);
                Vector3 l = ToVesselFrame(world);
                float r = Mathf.Sqrt(l.x * l.x + l.y * l.y);
                if (r < maxR - tipBand) continue;
                if (l.z < bestAft) { bestAft = l.z; tipWorld = world; found = true; }
            }
        }
        return found;
    }

    // Delegates so the aircraft path is unchanged: theta 0 is +lateral and 180 is -lateral.
    bool TryFindTipVertex(Part part, bool isRight, out Vector3 tipWorld)
    {
        return TryFindTipVertex(part, isRight ? 0f : 180f, out tipWorld);
    }

    // Outward direction given as an angle about the roll axis, so a fin pointing straight up still
    // has a tip.
    bool TryFindTipVertex(Part part, float outwardThetaDeg, out Vector3 tipWorld)
    {
        tipWorld = Vector3.zero;
        var filters = part.GetComponentsInChildren<MeshFilter>();
        if (filters == null || filters.Length == 0) return false;

        float ct = Mathf.Cos(outwardThetaDeg * Mathf.Deg2Rad);
        float st = Mathf.Sin(outwardThetaDeg * Mathf.Deg2Rad);

        float maxSpan = float.MinValue;
        foreach (var mf in filters)
        {
            // Skip meshes belonging to an attached child part.
            if (mf.GetComponentInParent<Part>() != part) continue;
            Mesh mesh = mf.sharedMesh;
            if (mesh == null) continue;
            Vector3[] verts = mesh.vertices;
            for (int v = 0; v < verts.Length; v++)
            {
                Vector3 local = ToVesselFrame(mf.transform.TransformPoint(verts[v]));
                if (local.x * ct + local.y * st < 0.05f) continue;   // behind, or on, the tip plane
                // Distance from the roll axis, never the fore/aft term.
                float span = Mathf.Sqrt(local.x * local.x + local.y * local.y);
                if (span > maxSpan) maxSpan = span;
            }
        }
        if (maxSpan == float.MinValue) return false;

        float bestForward = float.MinValue;
        bool found = false;
        foreach (var mf in filters)
        {
            if (mf.GetComponentInParent<Part>() != part) continue;
            Mesh mesh = mf.sharedMesh;
            if (mesh == null) continue;
            Vector3[] verts = mesh.vertices;
            for (int v = 0; v < verts.Length; v++)
            {
                Vector3 world = mf.transform.TransformPoint(verts[v]);
                Vector3 local = ToVesselFrame(world);
                if (local.x * ct + local.y * st < 0.05f) continue;
                float span = Mathf.Sqrt(local.x * local.x + local.y * local.y);
                if (span < maxSpan - tipBand) continue;
                if (local.z > bestForward) { bestForward = local.z; tipWorld = world; found = true; }
            }
        }
        return found;
    }

    // Resolves the vessel's geometric frame. Local component indices cannot be trusted: parts are
    // authored with the stack along +Y, so on an aircraft the root's local Y is the nose and Z is
    // vertical. Only the lateral axis is safe.
    // Spread of part positions along a world direction, immune to renderer contamination.
    float PartSpread(Vector3 dir)
    {
        if (vessel == null || vessel.parts == null) return 0f;
        float lo = float.MaxValue, hi = float.MinValue;
        for (int i = 0; i < vessel.parts.Count; i++)
        {
            var p = vessel.parts[i];
            if (p == null || p.transform == null) continue;
            float d = Vector3.Dot(p.transform.position - vesselTransform.position, dir);
            if (d < lo) lo = d;
            if (d > hi) hi = d;
        }
        return (hi >= lo) ? (hi - lo) : 0f;
    }

    void ComputeVesselAxes()
    {
        axLateralLocal = Vector3.right;

        // The length axis is picked from part positions, not renderer bounds, which effect meshes
        // inflate (a 17 m fighter measured 46.3 m long and 44.1 m tall). Only the comparison is
        // needed, and part positions give about 16 m against 3 m.
        float fwdSpread = PartSpread(vesselTransform.forward);
        float upSpread = PartSpread(vesselTransform.up);
        bool haveSpread = (fwdSpread + upSpread) > 0.01f;
        if (!haveSpread)
        {
            fwdSpread = ExtentAlong(vesselBounds, vesselTransform.forward);
            upSpread = ExtentAlong(vesselBounds, vesselTransform.up);
        }
        // Ties go to forward.
        axLengthLocal = (fwdSpread >= upSpread * 0.95f) ? Vector3.forward : Vector3.up;

        // The nose direction comes from a command pod, which sits ahead of the centre on
        // essentially every aircraft.
        axLengthSigned = false;
        float podZ = 0f;
        int podCount = 0;
        foreach (Part p in vessel.parts)
        {
            if (p == null || !p.Modules.Contains("ModuleCommand")) continue;
            podZ += Vector3.Dot(p.transform.position - vesselTransform.position, axLength);
            podCount++;
        }

        // Reference point is the part centroid, not vesselBounds.center, which effect meshes drag
        // aft and can invert the nose direction.
        Vector3 centroid = Vector3.zero;
        int nParts = 0;
        for (int i = 0; i < vessel.parts.Count; i++)
        {
            var p = vessel.parts[i];
            if (p == null || p.transform == null) continue;
            centroid += p.transform.position;
            nParts++;
        }
        float bodyZ = (nParts > 0)
            ? Vector3.Dot((centroid / nParts) - vesselTransform.position, axLength)
            : Vector3.Dot(vesselBounds.center - vesselTransform.position, axLength);

        if (podCount > 0)
        {
            podZ /= podCount;
            if (podZ < bodyZ) axLengthLocal = -axLengthLocal;
            axLengthSigned = true;
        }
        float noseMargin = podZ - bodyZ;

        // Vertical completes the frame, signed against the planet while on the runway.
        axVertLocal = Vector3.Cross(axLengthLocal, axLateralLocal);
        if (axVertLocal.sqrMagnitude < 0.001f) axVertLocal = Vector3.up;   // degenerate guard
        axVertLocal = axVertLocal.normalized;
        if (Vector3.Dot(axVert, (Vector3)vessel.upAxis) < 0f) axVertLocal = -axVertLocal;

        // All three full extents, with part spread, which is what chose the axis.
        Log($"axes resolved — nose={(axLengthSigned ? "known" : "UNKNOWN")} " +
                  $"length={ExtentAlong(vesselBounds, axLength) * 2f:F1}m " +
                  $"span={vesselSpan:F1}m " +   // frame-true; length and height are the world box
                  $"height={ExtentAlong(vesselBounds, axVert) * 2f:F1}m " +
                  $"(part spread fwd={fwdSpread:F1}m up={upSpread:F1}m"
                  + (haveSpread ? "" : ", FALLBACK to bounds")
                  + $", nose margin={noseMargin:F1}m)");
    }

    // World to vessel frame as (lateral, vertical, forward). Dots against the local basis, since
    // the tip scan calls this per mesh vertex.
    Vector3 ToVesselFrame(Vector3 world)
    {
        Vector3 local = vesselTransform.InverseTransformPoint(world);
        return new Vector3(Vector3.Dot(local, axLateralLocal),
                           Vector3.Dot(local, axVertLocal),
                           Vector3.Dot(local, axLengthLocal));
    }

    Vector3 FromVesselFrame(Vector3 f)
    {
        return vesselTransform.TransformPoint(axLateralLocal * f.x + axVertLocal * f.y + axLengthLocal * f.z);
    }

    // Sampled once per body: walks its temperature curve for the coldest air, so the contrail band
    // is reachable.
    void RefreshContrailBand(CelestialBody body)
    {
        contrailBody = body;
        bodyContrailWarm = contrailTempWarm;
        bodyContrailCold = contrailTempCold;
        if (body == null || !body.atmosphere) return;

        // Non-positive samples are discarded; rescale mods can leave the curve undefined at the
        // top.
        double depth = body.atmosphereDepth;
        float minT = float.MaxValue, maxT = float.MinValue;
        for (int s = 0; s <= 48; s++)
        {
            float t = (float)body.GetTemperature(depth * s / 48.0);
            if (t <= 1f || float.IsNaN(t) || float.IsInfinity(t)) continue;
            if (t < minT) minT = t;
            if (t > maxT) maxT = t;
        }
        if (minT == float.MaxValue) { minT = contrailTempCold; maxT = contrailTempWarm; }

        bodyContrailCold = Mathf.Max(contrailTempCold, minT);
        bodyContrailWarm = Mathf.Max(contrailTempWarm, bodyContrailCold + contrailBandMin);
        Log($"contrail band on {body.bodyName}: air {minT:F1}-{maxT:F1}K, "
                  + $"band {bodyContrailCold:F1}->{bodyContrailWarm:F1}K, "
                  + $"rho_ASL {body.atmDensityASL:F3}");
    }

    // A part's physical extent from its colliders; effect meshes have none.
    bool TryGetColliderBounds(Part p, out Bounds bounds)
    {
        bounds = new Bounds();
        if (p == null) return false;

        var cols = p.GetPartColliders();
        if (cols == null) return false;

        bool any = false;
        for (int i = 0; i < cols.Length; i++)
        {
            var c = cols[i];
            if (c == null || !c.enabled) continue;
            Bounds cb = c.bounds;
            if (float.IsNaN(cb.size.x) || float.IsInfinity(cb.size.x)) continue;
            if (cb.size.x > maxPartExtent || cb.size.y > maxPartExtent || cb.size.z > maxPartExtent) continue;
            if (!any) { bounds = cb; any = true; } else bounds.Encapsulate(cb);
        }
        return any;
    }

    // Renderer bounds for one part with non-part geometry rejected; `accepted` collects the
    // renderers that passed.
    bool TryGetPartBounds(Part p, out Bounds bounds, List<Renderer> accepted = null)
    {
        bounds = new Bounds();
        if (p == null) return false;

        Bounds colB;
        bool hasCol = TryGetColliderBounds(p, out colB);

        var rends = p.GetComponentsInChildren<Renderer>(false);
        bool any = false;
        for (int i = 0; i < rends.Length; i++)
        {
            var r = rends[i];
            if (r == null) continue;

            // Belongs to an attached child part.
            if (r.GetComponentInParent<Part>() != p) continue;

            // Wake geometry: a TrailRenderer's bounds cover its whole trail.
            if (r is TrailRenderer || r is LineRenderer || r is ParticleSystemRenderer) continue;

            Bounds b = r.bounds;
            Vector3 s = b.size, c = b.center;
            if (float.IsNaN(s.x) || float.IsNaN(s.y) || float.IsNaN(s.z)
                || float.IsInfinity(s.x) || float.IsInfinity(s.y) || float.IsInfinity(s.z)
                || float.IsNaN(c.x) || float.IsInfinity(c.x)) continue;

            // A large renderer reaching beyond its part's collider extent is an effect mesh (see
            // rendererColliderCheck).
            bool oversizedForPart = hasCol && s.magnitude > rendererColliderCheck
                                    && (s.magnitude > colB.size.magnitude * rendererColliderSlack
                                        || (c - colB.center).magnitude > colB.size.magnitude * rendererColliderSlack);

            // The cull-defeating giants (see maxPartExtent).
            if (oversizedForPart
                || s.x > maxPartExtent || s.y > maxPartExtent || s.z > maxPartExtent
                || (c - p.transform.position).sqrMagnitude > maxPartExtent * maxPartExtent)
            {
                if (!rogueRendererLogged)
                {
                    rogueRendererLogged = true;
                    Log($"ignoring oversized renderer '{r.name}' on part={p.name} "
                              + $"size={s.magnitude:F0}m — it is not part geometry "
                              + "(visual mods use huge bounds to defeat frustum culling)");
                }
                continue;
            }

            if (!any) { bounds = b; any = true; } else bounds.Encapsulate(b);
            if (accepted != null) accepted.Add(r);
        }
        return any;
    }

    // A part's reach along the craft's lateral axis. Renderer.bounds is world-aligned, so a banked
    // craft measured up to 1.8x its span; a mesh's own bounds rotate with it, so their corners are
    // exact.
    static readonly List<Renderer> lateralScratch = new List<Renderer>();
    void PartLateralRange(Part p, ref float lo, ref float hi)
    {
        lateralScratch.Clear();
        Bounds wb;
        if (!TryGetPartBounds(p, out wb, lateralScratch)) return;
        Vector3 origin = vesselTransform.position, right = vesselTransform.right;
        for (int i = 0; i < lateralScratch.Count; i++)
        {
            Renderer r = lateralScratch[i];
            MeshFilter mf = r is MeshRenderer ? r.GetComponent<MeshFilter>() : null;
            Transform space;
            Bounds lb;
            if (mf != null && mf.sharedMesh != null) { lb = mf.sharedMesh.bounds; space = r.transform; }
            else { lb = r.bounds; space = null; }   // skinned or other: the world box, as before
            Vector3 c = lb.center, e = lb.extents;
            for (int k = 0; k < 8; k++)
            {
                Vector3 q = c + new Vector3((k & 1) != 0 ? e.x : -e.x, (k & 2) != 0 ? e.y : -e.y, (k & 4) != 0 ? e.z : -e.z);
                if (space != null) q = space.TransformPoint(q);
                float x = Vector3.Dot(q - origin, right);
                if (x < lo) lo = x;
                if (x > hi) hi = x;
            }
        }
    }

    // How far a part reaches from the centreline, in the craft's frame (see PartLateralRange).
    float LateralReach(Part p, Bounds worldB)
    {
        float lo = float.MaxValue, hi = float.MinValue;
        PartLateralRange(p, ref lo, ref hi);
        if (hi < lo)
            return Mathf.Abs(Vector3.Dot(worldB.center - vesselTransform.position, vesselTransform.right))
                   + ExtentAlong(worldB, vesselTransform.right);
        return Mathf.Max(Mathf.Abs(lo), Mathf.Abs(hi));
    }

    // True once every renderer has finished fading out.
    bool NothingLeftToDraw()
    {
        for (int i = 0; i < trails.Count; i++)
            if (trails[i] != null && trails[i].positionCount > 0) return false;
        for (int i = 0; i < ribbons.Count; i++)
            if (ribbons[i] != null && ribbons[i].count > 0) return false;
        return true;
    }

    void ComputeVesselBounds()
    {
        vesselBounds = new Bounds(vesselTransform.position, Vector3.zero);
        bool initialized = false;
        foreach (var part in vessel.parts)
        {
            if (part == null) continue;

            // The part origin always counts, so the measurement is never empty.
            if (!initialized) { vesselBounds = new Bounds(part.transform.position, Vector3.zero); initialized = true; }
            else vesselBounds.Encapsulate(part.transform.position);

            Bounds pb;
            if (TryGetPartBounds(part, out pb)) vesselBounds.Encapsulate(pb);
        }
        vesselSize = Mathf.Clamp(vesselBounds.size.magnitude, 0.1f, 1000f);

        // Span and mass are read at the throttled cadence (GetTotalMass walks every part). Clamped
        // as a last defence, so a bad measurement degrades to wrong-looking rather than invisible.
        // Span is measured in the craft's own frame.
        float latLo = float.MaxValue, latHi = float.MinValue;
        foreach (var part in vessel.parts)
        {
            if (part == null) continue;
            float x = Vector3.Dot(part.transform.position - vesselTransform.position, vesselTransform.right);
            if (x < latLo) latLo = x;
            if (x > latHi) latHi = x;
            PartLateralRange(part, ref latLo, ref latHi);
        }
        vesselSpan = Mathf.Clamp(latHi > latLo ? latHi - latLo : 0f, 1f, 500f);
        vesselMassTons = Mathf.Max(vessel.GetTotalMass(), 0.001f);
        float sizeRatio = (vesselMassTons / vesselSpan) / (refMass / refSpan);
        sizeFactor = (2f * sizeRatio) / (1f + Mathf.Max(sizeRatio, 0f));

        if (!boundsLogged)
        {
            boundsLogged = true;
            Log($"measured: span={vesselSpan:F1}m "
                      + $"size={vesselSize:F1}m mass={vesselMassTons:F1}t sizeFactor={sizeFactor:F2}");
        }

        // ModuleControlSurface derives from ModuleLiftingSurface, so this catches canards and
        // elevons too.
        liftSurfaces = vessel.FindPartModulesImplementing<ModuleLiftingSurface>();
        FARBridge.FindWings(vessel, farWings);
        gravityAccel = Mathf.Max((float)FlightGlobals.getGeeForceAtPosition(vessel.CoM).magnitude, 0.01f);
    }

    // Source selection: one rule over four slots, {left, right} x {main wing, secondary surface}.
    // Within a slot the winner is the part reaching furthest from the centreline, which caps an
    // aircraft at four vortices. A surface counts as separate only if it is not physically
    // connected to the main wing's assembly.
    void FindVortexSources()
    {
        // Diagnostic: full aero-surface inventory. Resolve the vessel's frame before measuring
        // against it.
        ComputeVesselAxes();

        Log("=== AERO INVENTORY ===");
        foreach (Part p in vessel.parts)
        {
            if (p == null) continue;
            bool hasLift = IsLiftPart(p);
            bool hasCtrl = IsControlPart(p);
            if (!hasLift && !hasCtrl) continue;
            // In the resolved frame: lat = span station, vert = height, fwd = nose-positive.
            Vector3 lp = ToVesselFrame(p.transform.position);
            Bounds ib;
            float ai = TryGetPartBounds(p, out ib) ? ib.size.x * ib.size.z : 0f;
            Log($"AERO part={p.name} fwd={lp.z:F2} lat={lp.x:F2} vert={lp.y:F2} area={ai:F2} lift={hasLift} ctrl={hasCtrl}");
        }
        Log("=== END INVENTORY ===");

        // Sectors are computed and logged for every craft; used only when useSectorSelection is
        // set.
        List<AeroCandidate> radialPool = GatherCandidates(true);
        List<List<AeroCandidate>> sectors = BuildSectors(radialPool);
        LogSectors(radialPool, sectors);

        if (useSectorSelection)
        {
            FindSourcesBySector(radialPool, sectors);
            Log($"{trails.Count} vortex source(s) created (sector pass)");
            return;
        }

        // Evaluated on every detection. Slenderness is geometric, so it holds in flight; the
        // nose-up term only adds a yes on the pad, for a stubby rocket.
        {
            float lenSpread = PartSpread(axLength);
            float latSpread = PartSpread(axLateral);
            bool slender = lenSpread > latSpread * rocketSlenderness;

            float noseUpDot = Mathf.Abs(Vector3.Dot(axLength, (Vector3)vessel.upAxis));
            bool noseUp = vessel.LandedOrSplashed && noseUpDot > rocketNoseUpDot;

            verticalLauncher = (slender || noseUp) ? 1 : 0;
            Log($"craft shape: along={lenSpread:F1}m across={latSpread:F1}m "
                      + $"ratio={lenSpread / Mathf.Max(latSpread, 0.01f):F1} slender={slender} "
                      + $"noseUp={noseUp} (dot={noseUpDot:F2}) — "
                      + $"{(verticalLauncher == 1 ? "VERTICAL LAUNCHER" : "aircraft")}");
        }
        if (verticalLauncher == 1)
        {
            if (skipVerticalLaunchers)
            {
                Log("vertical launcher — skipped by skipVerticalLaunchers");
                return;
            }
            FindRocketFinSources();
            return;
        }

        // Candidate gathering
        List<AeroCandidate> pool = GatherCandidates(false);

        if (pool.Count == 0)
        {
            Log("no qualifying lifting surfaces — no vortices");
            return;
        }

        // Sorted once by lateral reach, so each slot's winner is the first entry matching the
        // filter.
        pool.Sort((a, b) => b.extremity.CompareTo(a.extremity));
        foreach (var c in pool)
            Log($"CANDIDATE part={c.part.name} side={(c.isLeft ? "L" : "R")} ext={c.extremity:F2} z={c.z:F2}");

        // Main wing: the furthest-reaching surface on each side.
        AeroCandidate mainLeft = BestOnSide(pool, true, null);
        AeroCandidate mainRight = BestOnSide(pool, false, null);

        // Assembly: everything physically continuous with those winners.
        HashSet<Part> mainAssembly = BuildAssembly(pool, mainLeft, mainRight);
        Log($"main assembly = {mainAssembly.Count} part(s)");

        // Longitudinal span of the main wing: a panel the flood fill missed must still sit clear of
        // the wing's chord to count as separate.
        float mainZLo = float.MaxValue, mainZHi = float.MinValue;
        foreach (var c in pool)
        {
            if (!mainAssembly.Contains(c.part)) continue;
            if (c.zLo < mainZLo) mainZLo = c.zLo;
            if (c.zHi > mainZHi) mainZHi = c.zHi;
        }
        if (mainZLo > mainZHi) { mainZLo = 0f; mainZHi = 0f; }
        float sep = Mathf.Max(secondaryMinSeparation, (mainZHi - mainZLo) * 0.15f);

        // Secondary: the best surface that is not the main wing. A forward surface (canard) is
        // preferred outright: it sits in clean air and carries a real share of lift. An aft one is
        // kept only at tandem scale, since a stabiliser carries a few percent of the lift over a
        // short span inside the wing's downwash, and core pressure drop goes as circulation
        // squared.
        float mainExt = 0f;
        if (mainLeft != null) mainExt = Mathf.Max(mainExt, mainLeft.extremity);
        if (mainRight != null) mainExt = Mathf.Max(mainExt, mainRight.extremity);

        List<AeroCandidate> foreSec = new List<AeroCandidate>();
        List<AeroCandidate> aftSec = new List<AeroCandidate>();
        if (axLengthSigned)
        {
            foreach (var c in pool)
            {
                if (mainAssembly.Contains(c.part)) continue;
                if (c.z > mainZHi + sep) foreSec.Add(c);
                else if (c.z < mainZLo - sep) aftSec.Add(c);
            }
        }

        // Both sides take their secondary from the same station.
        List<AeroCandidate> station = null;
        string stationName = null;
        if (foreSec.Count > 0)
        {
            station = foreSec;
            stationName = "FORE";
        }
        else if (aftSec.Count > 0 && mainExt > 0f && aftSec[0].extremity >= mainExt * aftSecondarySpanRatio)
        {
            station = aftSec;
            stationName = "AFT (tandem-scale)";
        }
        else if (aftSec.Count > 0)
        {
            float pct = (aftSec[0].extremity / Mathf.Max(mainExt, 0.01f)) * 100f;
            Log($"aft surface ignored — span is {pct:F0}% of the wing, a stabiliser not a wing");
        }

        AeroCandidate secLeft = null, secRight = null;
        HashSet<Part> secAssembly = null;
        if (station != null)
        {
            secLeft = BestOnSide(station, true, null);
            secRight = BestOnSide(station, false, null);
            secAssembly = BuildAssembly(station, secLeft, secRight);
            Log($"secondary station = {stationName}, {secAssembly.Count} part(s)");
        }
        else
        {
            Log("no secondary surface qualified");
        }

        // Spawn: at most one per slot.
        bool okML = SpawnSlot(pool, mainLeft, mainAssembly, false, 1f, "MAIN LEFT");
        bool okMR = SpawnSlot(pool, mainRight, mainAssembly, true, 1f, "MAIN RIGHT");

        // Symmetry fallback: mirror the surviving side's tip across the centreline.
        if (!okML && okMR && mainRight != null)
            AddWingVortex(mainRight.part, false, 1f, true, mainAssembly);
        if (!okMR && okML && mainLeft != null)
            AddWingVortex(mainLeft.part, true, 1f, true, mainAssembly);

        // Size of this secondary relative to the main wing; the gate in Update() reads it.
        float ratioL = (mainExt > 0f && secLeft != null) ? Mathf.Clamp01(secLeft.extremity / mainExt) : 1f;
        float ratioR = (mainExt > 0f && secRight != null) ? Mathf.Clamp01(secRight.extremity / mainExt) : 1f;
        if (secLeft != null || secRight != null)
            Log($"secondary span ratio L={ratioL:F2} R={ratioR:F2} (of main wing)");

        // Strength from span reach, not slot (see MainLikeness).
        float strL = SecondaryStrength(ratioL);
        float strR = SecondaryStrength(ratioR);

        if (treatSecondariesAsMain)
        {
            strL = strR = 1f;
            ratioL = ratioR = 1f;
        }

        if (secLeft != null || secRight != null)
        {
            if (treatSecondariesAsMain)
                Log("treatSecondariesAsMain: secondaries forced to strength 1.00, "
                          + "span ratio 1.00 — no secondary gate at all");
            else
                Log($"secondary onset L={SecondaryOnsetMult(ratioL):F2}x R={SecondaryOnsetMult(ratioR):F2}x "
                          + $"of the main wing ({onsetLoadRef * SecondaryOnsetMult(ratioL):F1} g at the reference condition)");
            Log($"secondary strength L={strL:F2} R={strR:F2} "
                      + $"(tip-likeness L={MainLikeness(ratioL):F2} R={MainLikeness(ratioR):F2})");
        }

        bool okSL = SpawnSlot(pool, secLeft, secAssembly, false, strL, "SECONDARY LEFT", ratioL);
        bool okSR = SpawnSlot(pool, secRight, secAssembly, true, strR, "SECONDARY RIGHT", ratioR);

        if (!okSL && okSR && secRight != null)
            AddWingVortex(secRight.part, false, strR, true, secAssembly, ratioR);
        if (!okSR && okSL && secLeft != null)
            AddWingVortex(secLeft.part, true, strL, true, secAssembly, ratioL);

        Log($"{trails.Count} vortex source(s) created (hard cap 4)");
    }

    // How much of a wingtip this surface is, from the share of the widest span it reaches,
    // smoothstepped.
    float MainLikeness(float spanRatio)
    {
        float t = Mathf.Clamp01((spanRatio - mainLikeRatioMin)
                                / Mathf.Max(mainLikeRatioFull - mainLikeRatioMin, 0.01f));
        return t * t * (3f - 2f * t);
    }

    // Strength for a secondary slot: exactly 1 for a surface that is a wingtip in all but name,
    // which skips the gate in Update(). Below the main-like band the floor falls off quadratically
    // with span ratio, since core pressure drop goes as circulation squared.
    // How many times the main wing's onset load the surface must reach (see secondaryOnsetMultMin).
    float SecondaryOnsetMult(float spanRatio)
    {
        return Mathf.Clamp(1f / Mathf.Max(spanRatio, 0.05f),
                           secondaryOnsetMultMin, secondaryOnsetMultMax);
    }

    float SecondaryStrength(float spanRatio)
    {
        float r = Mathf.Clamp01(spanRatio / Mathf.Max(mainLikeRatioMin, 0.01f));
        return Mathf.Lerp(secondaryStrengthMin * r * r, 1f, MainLikeness(spanRatio));
    }

    // One vortex per fin, radially (see finBandFraction).
    void FindRocketFinSources()
    {
        List<Part> fins = new List<Part>();
        List<float> finZ = new List<float>();
        List<float> finR = new List<float>();

        float lowestZ = float.MaxValue, highestZ = float.MinValue;
        foreach (Part p in vessel.parts)
        {
            if (p == null) continue;
            if (!IsAeroSurface(p)) continue;

            Bounds b;
            if (!TryGetPartBounds(p, out b)) continue;
            if (b.size.x * b.size.z < minSourceArea) continue;

            Vector3 c = ToVesselFrame(b.center);
            float radial = Mathf.Sqrt(c.x * c.x + c.y * c.y);
            if (radial < minRadialOffset) continue;   // buried in the fuselage, not a fin

            fins.Add(p); finZ.Add(c.z); finR.Add(radial);
            if (c.z < lowestZ) lowestZ = c.z;
            if (c.z > highestZ) highestZ = c.z;
        }

        if (fins.Count == 0)
        {
            Log("rocket: no fins found — no vortices");
            return;
        }

        // The lowest fins as a band, not a single value.
        float band = Mathf.Max(finBandMin, (highestZ - lowestZ) * finBandFraction);

        List<int> keep = new List<int>();
        for (int i = 0; i < fins.Count; i++)
            if (finZ[i] <= lowestZ + band) keep.Add(i);

        // Furthest-reaching first, so the cap of four keeps the biggest.
        keep.Sort((a, b2) => finR[b2].CompareTo(finR[a]));

        int made = 0;
        for (int k = 0; k < keep.Count && made < 4; k++)
        {
            int i = keep[k];
            Vector3 tip;
            if (!TryFindRadialTip(fins[i], out tip)) continue;

            GameObject a = new GameObject("Anchor");
            a.transform.position = tip;
            a.transform.parent = fins[i].transform;

            // Fins use tubes. The inward displacement scales with source strength, and a tube
            // source never hands over to line mode, which avoids the abrupt cut-off on ascent.
            if (AddSourceAtAnchor(a.transform, 1f, 1f, !rocketUseRibbons))
            {
                made++;
                Log($"ROCKET FIN part={fins[i].name} radial={finR[i]:F2} z={finZ[i]:F2}");
            }
            else Destroy(a);
        }

        Log($"rocket: {made} fin vortex source(s) from {keep.Count} in the aft band "
                  + $"(band={band:F2}m, {fins.Count} lifting surface(s) total)");
    }

    // One gatherer, two gates. radialGate=false is the lateral-offset behaviour; radialGate=true is
    // roll-invariant and a superset (a fin at lat=0 is kept by it).
    List<AeroCandidate> GatherCandidates(bool radialGate)
    {
        List<AeroCandidate> pool = new List<AeroCandidate>();
        foreach (Part p in vessel.parts)
        {
            if (p == null) continue;

            // Only lifting surfaces.
            if (!IsAeroSurface(p)) continue;

            // Same rejection as the vessel measurement, so a cull-defeating renderer cannot win a
            // slot.
            Bounds b;
            if (!TryGetPartBounds(p, out b)) continue;
            if (b.size.sqrMagnitude <= 0f) continue;
            if (b.size.x * b.size.z < minSourceArea) continue;

            // Measured from the bounds centre, not transform.position, which sits at a panel's
            // inboard attach node. World-space dots, so values match the live path exactly.
            Vector3 rel = b.center - vesselTransform.position;
            float cProj = Vector3.Dot(rel, axLateral);
            float vProj = Vector3.Dot(rel, axVert);
            float radial = Mathf.Sqrt(cProj * cProj + vProj * vProj);

            if (radialGate) { if (radial < minRadialOffset) continue; }
            else { if (Mathf.Abs(cProj) < minLateralOffset) continue; }   // centre sections, fins

            float ext = LateralReach(p, b);
            float zc = Vector3.Dot(b.center - vesselTransform.position, axLength);
            float halfZ = ExtentAlong(b, axLength);
            float th = Mathf.Atan2(vProj, cProj) * Mathf.Rad2Deg;

            pool.Add(new AeroCandidate(p, b, cProj < 0f, ext, zc, halfZ, radial, th));
        }
        return pool;
    }

    // Cluster by angle about the roll axis: sort by theta, open a new sector wherever the gap to
    // the previous candidate exceeds sectorGapDeg, wrapping around. A pure function of the
    // candidate set, so re-detection after staging is stable.
    List<List<AeroCandidate>> BuildSectors(List<AeroCandidate> pool)
    {
        List<List<AeroCandidate>> sectors = new List<List<AeroCandidate>>();
        if (pool.Count == 0) return sectors;

        List<AeroCandidate> byAngle = new List<AeroCandidate>(pool);
        byAngle.Sort((a, b) => a.theta.CompareTo(b.theta));

        // Start the walk after the widest gap.
        int cut = 0;
        float widest = -1f;
        for (int i = 0; i < byAngle.Count; i++)
        {
            int prev = (i - 1 + byAngle.Count) % byAngle.Count;
            float gap = byAngle[i].theta - byAngle[prev].theta;
            while (gap < 0f) gap += 360f;
            if (gap > widest) { widest = gap; cut = i; }
        }

        List<AeroCandidate> cur = new List<AeroCandidate>();
        for (int k = 0; k < byAngle.Count; k++)
        {
            var c = byAngle[(cut + k) % byAngle.Count];
            if (cur.Count > 0)
            {
                float gap = c.theta - cur[cur.Count - 1].theta;
                while (gap < 0f) gap += 360f;
                if (gap > sectorGapDeg) { sectors.Add(cur); cur = new List<AeroCandidate>(); }
            }
            cur.Add(c);
        }
        if (cur.Count > 0) sectors.Add(cur);
        return sectors;
    }

    void LogSectors(List<AeroCandidate> radialPool, List<List<AeroCandidate>> sectors)
    {
        Log($"SECTORS: {sectors.Count} from {radialPool.Count} radial candidate(s), "
                  + $"gap>{sectorGapDeg:F0}deg  [{(useSectorSelection ? "LIVE" : "shadow, not used")}]");
        for (int s = 0; s < sectors.Count; s++)
        {
            var sec = sectors[s];
            float maxR = 0f, tSum = 0f;
            string parts = "";
            for (int i = 0; i < sec.Count; i++)
            {
                if (sec[i].radialDist > maxR) maxR = sec[i].radialDist;
                tSum += sec[i].theta;
                parts += (i > 0 ? ", " : "") + sec[i].part.name;
            }
            Log($"  sector {s}: theta~{tSum / sec.Count:F0}deg maxRadial={maxR:F2} "
                      + $"n={sec.Count} [{parts}]");
        }
    }

    // Sector x station, generalising the four slots: one source per sector, plus a fore/aft second
    // when one sits clear of the main member.
    void FindSourcesBySector(List<AeroCandidate> pool, List<List<AeroCandidate>> sectors)
    {
        float maxR = 0f;
        for (int i = 0; i < pool.Count; i++)
            if (pool[i].radialDist > maxR) maxR = pool[i].radialDist;
        if (maxR <= 0f) { Log("sector pass: nothing reaches out — no vortices"); return; }

        // A tube models a counter-rotating pair, which two wingtips are and three or four fins are
        // not.
        bool forceTrail = (sectors.Count != 2);

        // Widest sector first, so the clamp keeps the biggest.
        List<List<AeroCandidate>> ordered = new List<List<AeroCandidate>>(sectors);
        ordered.Sort((a, b) => SectorMaxRadial(b).CompareTo(SectorMaxRadial(a)));

        int made = 0;
        for (int s = 0; s < ordered.Count && made < sectorSourceCap; s++)
        {
            var sec = ordered[s];

            AeroCandidate main = sec[0];
            for (int i = 1; i < sec.Count; i++)
                if (sec[i].radialDist > main.radialDist) main = sec[i];

            if (SpawnSectorSource(main, maxR, forceTrail, $"SECTOR {s} MAIN")) made++;
            if (made >= sectorSourceCap) break;

            // Station axis within this sector: fore preferred outright, aft only at tandem scale.
            if (!axLengthSigned) continue;

            float sep = Mathf.Max(secondaryMinSeparation, (main.zHi - main.zLo) * 0.15f);
            AeroCandidate fore = null, aft = null;
            for (int i = 0; i < sec.Count; i++)
            {
                var c = sec[i];
                if (c == main) continue;
                if (c.z > main.zHi + sep) { if (fore == null || c.radialDist > fore.radialDist) fore = c; }
                else if (c.z < main.zLo - sep) { if (aft == null || c.radialDist > aft.radialDist) aft = c; }
            }

            AeroCandidate second = fore;
            if (second == null && aft != null && aft.radialDist >= main.radialDist * aftSecondarySpanRatio)
                second = aft;

            if (second != null && SpawnSectorSource(second, maxR, forceTrail, $"SECTOR {s} SECOND")) made++;
        }

        Log($"sector pass: {made} source(s) from {sectors.Count} sector(s) "
                  + $"(cap {sectorSourceCap}, tubes={(forceTrail ? "no" : "yes")})");
    }

    float SectorMaxRadial(List<AeroCandidate> sec)
    {
        float m = 0f;
        for (int i = 0; i < sec.Count; i++) if (sec[i].radialDist > m) m = sec[i].radialDist;
        return m;
    }

    bool SpawnSectorSource(AeroCandidate c, float maxR, bool forceTrail, string label)
    {
        Vector3 tip;
        if (!TryFindTipVertex(c.part, c.theta, out tip))
        {
            Log($"{label} part={c.part.name} — no tip vertex found, skipped");
            return false;
        }

        GameObject a = new GameObject("Anchor");
        a.transform.position = tip;
        a.transform.parent = c.part.transform;

        // Span ratio against the widest reach on the vessel: on a rocket every fin scores ~1, on an
        // aircraft a rudder is gated hard.
        float ratio = Mathf.Clamp01(c.radialDist / maxR);
        float strength = SecondaryStrength(ratio);

        if (!AddSourceAtAnchor(a.transform, strength, ratio, forceTrail)) { Destroy(a); return false; }
        Log($"{label} part={c.part.name} radial={c.radialDist:F2} theta={c.theta:F0}deg "
                  + $"ratio={ratio:F2} strength={strength:F2}");
        return true;
    }

    // First entry on the requested side of an extremity-sorted list.
    AeroCandidate BestOnSide(List<AeroCandidate> sorted, bool wantLeft, HashSet<Part> skip)
    {
        for (int i = 0; i < sorted.Count; i++)
        {
            var c = sorted[i];
            if (c.isLeft != wantLeft) continue;
            if (skip != null && skip.Contains(c.part)) continue;
            return c;
        }
        return null;
    }

    // Everything physically continuous with the seed parts. Panels of one wing are either linked in
    // the part tree or touching, depending on how it was built, so both are tested. Anything in the
    // set is the same wing and never a second source.
    HashSet<Part> BuildAssembly(List<AeroCandidate> pool, AeroCandidate seedA, AeroCandidate seedB)
    {
        HashSet<Part> assembly = new HashSet<Part>();
        if (seedA != null) assembly.Add(seedA.part);
        if (seedB != null) assembly.Add(seedB.part);
        if (assembly.Count == 0) return assembly;

        // Small on purpose: too generous swallows a closely mounted canard into the wing.
        float margin = Mathf.Max(0.15f, vesselSize * 0.01f);
        List<Part> pending = new List<Part>();

        for (int pass = 0; pass < 64; pass++)
        {
            pending.Clear();
            for (int i = 0; i < pool.Count; i++)
            {
                var c = pool[i];
                if (c.part == null || assembly.Contains(c.part)) continue;

                for (int j = 0; j < pool.Count; j++)
                {
                    var m = pool[j];
                    if (m.part == null || !assembly.Contains(m.part)) continue;

                    bool tree = (c.part.parent == m.part) || (m.part.parent == c.part);
                    Bounds eb = c.bounds;
                    eb.Expand(margin);
                    if (tree || eb.Intersects(m.bounds)) { pending.Add(c.part); break; }
                }
            }
            if (pending.Count == 0) break;
            for (int i = 0; i < pending.Count; i++) assembly.Add(pending[i]);
        }
        return assembly;
    }

    // Fills one slot, trying the chosen part and then any other part of the same assembly on that
    // side.
    bool SpawnSlot(List<AeroCandidate> pool, AeroCandidate chosen, HashSet<Part> group,
                   bool isRight, float strength, string label, float spanRatio = 1f)
    {
        if (chosen == null) return false;

        if (AddWingVortex(chosen.part, isRight, strength, false, group, spanRatio))
        {
            Log($"{label} part={chosen.part.name} ext={chosen.extremity:F2}");
            return true;
        }

        bool wantLeft = !isRight;
        for (int i = 0; i < pool.Count; i++)
        {
            var c = pool[i];
            if (c == chosen || c.part == null) continue;
            if (c.isLeft != wantLeft) continue;
            if (group != null && !group.Contains(c.part)) continue;
            if (AddWingVortex(c.part, isRight, strength, false, group, spanRatio))
            {
                Log($"{label} (fallback) part={c.part.name} ext={c.extremity:F2}");
                return true;
            }
        }

        Log($"{label} — no usable geometry");
        return false;
    }

    // Returns false when the part yields no usable tip.
    bool AddWingVortex(Part wing, bool isRight, float strength, bool mirror = false,
                       HashSet<Part> restrictTo = null, float spanRatio = 1f)
    {
        // If mirrored, sample the real tip on the source wing, then mirror it across the
        // centreline.
        Transform anchor = CreateWingtipAnchor(wing, mirror ? !isRight : isRight, restrictTo);
        if (anchor == null) return false;

        if (mirror)
        {
            Vector3 local = ToVesselFrame(anchor.position);
            local.x *= -1f;
            anchor.position = FromVesselFrame(local);
        }


        return AddSourceAtAnchor(anchor, strength, spanRatio, false);
    }

    // Everything AddWingVortex does once it has an anchor, shared with the rocket pass.
    bool AddSourceAtAnchor(Transform anchor, float strength, float spanRatio, bool forceTrail)
    {
        if (anchor == null) return false;

        GameObject obj = new GameObject("Vortex");
        // Do not parent to the vessel, so the trail does not inherit its motion.
        obj.transform.parent = null;

        TrailRenderer tr = obj.AddComponent<TrailRenderer>();
        tr.time = 1.5f; tr.startWidth = 0.15f; tr.endWidth = 0.03f;
        tr.numCapVertices = trailCapVertices;   // round the ends; square caps read as a cut slab
        tr.sharedMaterial = TrailMat(); tr.enabled = false;
        // TrailRenderer here has no useWorldSpace; an unparented object is already world-space.

        LineRenderer lr = obj.AddComponent<LineRenderer>();
        lr.positionCount = historyLength; lr.startWidth = 0.1f; lr.endWidth = 0.02f;
        lr.sharedMaterial = LineMat(); lr.enabled = false;

        trailObjs.Add(obj); trails.Add(tr); lines.Add(lr);
        anchors.Add(anchor); strengths.Add(strength); spanRatios.Add(Mathf.Clamp01(spanRatio));
        Part rootPart = vessel != null ? vessel.rootPart : null;
        anchorRoots.Add(rootPart);
        anchorRootOffsets.Add(rootPart != null ? rootPart.transform.InverseTransformPoint(anchor.position) : Vector3.zero);
        lineHistory.Add(new Queue<Vector3>());
        trailWasActive.Add(false); lineWasActive.Add(false);
        lastFlows.Add(Vector3.zero); lastAnchorPositions.Add(anchor.position);
        shedHistory.Add(new float[shedSamples]); trailAges.Add(0f); trailHeadAges.Add(0f);

        bool wantRibbon = enableRibbonMesh && !forceTrail
                          && (!ribbonMainOnly || strength >= 1f)
                          && (!ribbonFirstSourceOnly || ribbons.Count == 0);
        ribbons.Add(wantRibbon ? CreateRibbon() : null);
        return true;
    }

    // One vortex rope's geometry and wake history. Buffers are allocated once at full size and
    // rewritten in place; surplus rings collapse onto the tail at zero radius and alpha, so mesh
    // sizes never change (KSP is GC-sensitive).
    private class WakeRibbon
    {
        public GameObject obj;
        public Mesh mesh;

        // Wake history, index 0 newest, in world space like the TrailRenderer vertices.
        public Vector3[] pts;
        public float[] birth;
        public float[] shed;

        // Total distance travelled when each ring was laid; the only thing phases may key off,
        // since it is fixed at birth.
        public float[] odo;
        public float odometer;

        // Inward direction, world space, set when the ring is laid (see RibbonInward) and smoothed
        // while inside the smoothing window.
        public Vector3[] inDir;

        // Ground-effect drift velocity and factor, frozen at birth.
        public Vector3[] geVel;
        public float[] geFac;

        // Breakdown state at birth: seconds after shedding at which this stretch bursts (1e6 for
        // never), how deep past onset, and spiral (0) to bubble (1).
        public float[] bdT;
        public float[] bdDepth;
        public float[] bdMode;

        // Spiral-breakdown phase, accumulated ring to ring and frozen at birth (see SpiralLambda).
        public float[] sPhase;
        public float spiralPhase;

        public float spacing;   // current ring spacing, metres

        public float seed;   // so the two wingtips never band identically

        public int count;

        // Reused mesh buffers.
        public Vector3[] verts;
        public Vector2[] uvs;
        public Color[] cols;
    }

    void EnsureRingTable()
    {
        int K = Mathf.Max(3, ribbonSides);
        if (ringCos != null && ringCos.Length == K) return;
        ringCos = new float[K];
        ringSin = new float[K];
        for (int k = 0; k < K; k++)
        {
            float a = (Mathf.PI * 2f * k) / K;
            ringCos[k] = Mathf.Cos(a);
            ringSin[k] = Mathf.Sin(a);
        }
    }

    WakeRibbon CreateRibbon()
    {
        int rings = ribbonMaxPoints;
        int K = Mathf.Max(3, ribbonSides);
        EnsureRingTable();

        var r = new WakeRibbon();
        r.pts = new Vector3[rings];
        r.birth = new float[rings];
        r.shed = new float[rings];
        r.odo = new float[rings];
        r.inDir = new Vector3[rings];
        r.geVel = new Vector3[rings];
        r.geFac = new float[rings];
        r.bdT = new float[rings];
        r.bdDepth = new float[rings];
        r.bdMode = new float[rings];
        r.sPhase = new float[rings];
        r.spacing = ribbonMinPointDist;
        r.odometer = 0f;
        r.count = 0;

        r.verts = new Vector3[rings * K];
        r.uvs = new Vector2[rings * K];
        r.cols = new Color[rings * K];

        // The index buffer is built once, since every ring is always emitted.
        int[] tris = new int[(rings - 1) * K * 6];
        int t = 0;
        for (int i = 0; i < rings - 1; i++)
        {
            int a = i * K, b = (i + 1) * K;
            for (int k = 0; k < K; k++)
            {
                int k2 = (k + 1) % K;
                tris[t++] = a + k;  tris[t++] = b + k;  tris[t++] = a + k2;
                tris[t++] = a + k2; tris[t++] = b + k;  tris[t++] = b + k2;
            }
        }

        r.seed = ribbons.Count * 37.19f;

        r.obj = new GameObject("VortexTube");
        r.obj.transform.parent = null;          // world space, same as the trail objects
        r.mesh = new Mesh();
        r.mesh.MarkDynamic();
        r.mesh.vertices = r.verts;
        r.mesh.triangles = tris;

        var mf = r.obj.AddComponent<MeshFilter>();
        mf.sharedMesh = r.mesh;
        var mr = r.obj.AddComponent<MeshRenderer>();
        mr.sharedMaterial = TrailMat();
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;

        return r;
    }

    // Lays a ring only after the wingtip has travelled far enough, so ring spacing is a distance.
    // Brightness multiplier for the next ring, from the odometer so it is fixed in space.
    float RibbonFlowVar(WakeRibbon r, float depth)
    {
        if (depth <= 0f) return 1f;
        return 1f + depth * ((Mathf.PerlinNoise(r.odometer * ribbonFlowScale, r.seed) - 0.5f) * 2f);
    }

    // Toward the roll axis, from the anchor: the radial-outward direction with the roll-axis
    // component projected out, negated. No side test, so it is correct for a fin pointing straight
    // up. Measured once per frame at the anchor, because a ring laid behind it lies along the
    // flight path, which is angle of attack off the roll axis, and would tip toward the belly.
    // Update() blends the rings in between from the last head's direction.
    Vector3 RibbonInward(WakeRibbon r, Vector3 anchorPos)
    {
        Vector3 rel = anchorPos - vesselTransform.position;
        Vector3 radialOut = rel - axLength * Vector3.Dot(rel, axLength);
        if (radialOut.sqrMagnitude > 1e-6f) return -radialOut.normalized;
        return (r.count > 0) ? r.inDir[0] : -axLateral;   // degenerate: hold the last
    }

    // Spiral wavelength for a ring spacing, floored so a turn is never shorter than
    // spiralMinRingsPerTurn rings.
    float SpiralLambda(float spacing)
    {
        return Mathf.Max(spiralWavelengthCores * frameR0Vis, spiralMinRingsPerTurn * Mathf.Max(spacing, 0.1f));
    }

    void PushRibbonPoint(WakeRibbon r, Vector3 p, float now, float shed, float flowDepth, Vector3 inward)
    {
        if (r.count > 0)
        {
            r.shed[0] = shed * RibbonFlowVar(r, flowDepth);   // keep the head's strength live
            if ((p - r.pts[0]).sqrMagnitude < 0.0625f) return;   // 0.25 m, anti-bunching only
        }
        if (r.count > 0)
        {
            float step = (p - r.pts[0]).magnitude;
            r.odometer += step;
            // The spiral's phase advances by this step at the wavelength in force now and is
            // stored, not derived later from odo / lambda, which would re-wind every spiral when
            // lambda moves.
            r.spiralPhase = Mathf.Repeat(r.spiralPhase + step * (Mathf.PI * 2f) / SpiralLambda(r.spacing),
                                         Mathf.PI * 2f);
        }

        int n = Mathf.Min(r.count, r.pts.Length - 1);
        System.Array.Copy(r.pts, 0, r.pts, 1, n);
        System.Array.Copy(r.birth, 0, r.birth, 1, n);
        System.Array.Copy(r.shed, 0, r.shed, 1, n);
        System.Array.Copy(r.odo, 0, r.odo, 1, n);
        System.Array.Copy(r.inDir, 0, r.inDir, 1, n);
        System.Array.Copy(r.geVel, 0, r.geVel, 1, n);
        System.Array.Copy(r.geFac, 0, r.geFac, 1, n);
        System.Array.Copy(r.bdT, 0, r.bdT, 1, n);
        System.Array.Copy(r.bdDepth, 0, r.bdDepth, 1, n);
        System.Array.Copy(r.bdMode, 0, r.bdMode, 1, n);
        System.Array.Copy(r.sPhase, 0, r.sPhase, 1, n);

        r.inDir[0] = inward;   // see RibbonInward

        // Ground effect for this ring. Height is the vessel's height above ground plus the point's
        // offset along local up. Drift is horizontal and outboard: the inward direction flattened
        // onto the ground plane.
        r.geVel[0] = Vector3.zero;
        r.geFac[0] = 0f;
        if (frameGroundOn)
        {
            float h = Mathf.Max(frameAGL + Vector3.Dot(p - frameVesselPos, frameUp), 0f);
            float b0sq = frameB0 * frameB0;
            r.geFac[0] = groundEffectEnable * b0sq / (4f * h * h + b0sq);

            Vector3 outward = -r.inDir[0];
            outward -= frameUp * Vector3.Dot(outward, frameUp);
            if (outward.sqrMagnitude > 1e-4f)
            {
                float hEff = Mathf.Max(h, 0.5f * frameB0);
                float vOut = frameGamma / (4f * Mathf.PI) * b0sq / (hEff * (4f * hEff * hEff + b0sq));
                r.geVel[0] = outward.normalized * (vOut * groundSpreadScale * groundEffectEnable);
            }
        }

        r.bdT[0] = frameBreakT; r.bdDepth[0] = frameBreakDepth; r.bdMode[0] = frameBreakMode;
        r.sPhase[0] = r.spiralPhase;

        r.pts[0] = p; r.birth[0] = now; r.shed[0] = shed * RibbonFlowVar(r, flowDepth); r.odo[0] = r.odometer;
        r.count = Mathf.Min(r.count + 1, r.pts.Length);
    }

    // r(t)/r0 = sqrt(1 + (4*alpha*nu_eff/r0^2) * t), with the expensive part folded into
    // coreGrowthRate once per frame.
    float CoreGrowth(float age)
    {
        return CoreGrowth(age, 1f);
    }

    // rateMult carries local extra eddy viscosity (the ground boundary layer).
    float CoreGrowth(float age, float rateMult)
    {
        if (coreGrowthRate <= 0f) return 1f;
        return Mathf.Min(Mathf.Sqrt(1f + coreGrowthRate * rateMult * Mathf.Max(age, 0f)), coreGrowthMax);
    }

    // The conservation half of core growth: whatever diffusion widens, this dims (see coreDimPow).
    // Keyed on the growth it is handed, so breakdown diffusion still dims with core growth off.
    float CoreDim(float growth)
    {
        if (growth <= 1f) return 1f;
        return Mathf.Pow(growth, -coreDimPow);
    }

    void BuildRibbonMesh(WakeRibbon r, float now, float widthScale, float contrailBlend, float life, float inwardScale)
    {
        int K = Mathf.Max(3, ribbonSides);
        int rings = r.pts.Length;
        int n = r.count;

        if (n < 2)
        {
            for (int v = 0; v < r.verts.Length; v++) { r.verts[v] = Vector3.zero; r.cols[v] = Color.clear; }
            r.mesh.vertices = r.verts;
            r.mesh.colors = r.cols;
            r.mesh.bounds = new Bounds(Vector3.zero, Vector3.one);
            return;
        }

        Vector3 origin = r.pts[0];
        r.obj.transform.position = origin;
        r.obj.transform.rotation = Quaternion.identity;

        float rollup = Mathf.Clamp(vesselSize * rollupSizeScale, rollupMinMetres, rollupMaxMetres);
        float tailRatio = Mathf.Lerp(0.6f, 1.6f, contrailBlend);   // the tube spreads with age

        // Breakdown geometry this frame, floored by ring spacing so a narrow bubble or short spiral
        // turn does not alias.
        float spacingNow = Mathf.Max(r.spacing, 0.1f);
        float bubbleWidthT = Mathf.Max(bubbleLengthCores * frameR0Vis, bubbleMinRings * spacingNow)
                             / Mathf.Max(frameSpeed, 1f);
        // Sets only how fast the spiral decays; its phase was fixed at birth (see r.sPhase).
        float spiralLambda = SpiralLambda(spacingNow);
        float groundCap = groundSpreadMaxSpans * vesselSpan;

        // Parallel transport: carry the previous ring's normal forward and re-orthogonalise,
        // instead of a Cross(tangent, up) frame that flips near the reference axis.
        Vector3 prevN = Vector3.zero;
        float arc = 0f;
        Vector3 lo = Vector3.positiveInfinity, hi = Vector3.negativeInfinity;

        for (int i = 0; i < rings; i++)
        {
            int src = Mathf.Min(i, n - 1);          // surplus rings collapse onto the tail
            bool live = i < n;

            Vector3 p = r.pts[src];
            Vector3 tangent;
            if (src == 0)                    tangent = (r.pts[0] - r.pts[1]).normalized;
            else if (src >= n - 1)           tangent = (r.pts[n - 2] - r.pts[n - 1]).normalized;
            else                             tangent = (r.pts[src - 1] - r.pts[src + 1]).normalized;
            if (tangent.sqrMagnitude < 0.5f) tangent = Vector3.forward;

            if (i == 0)
            {
                Vector3 seed = Mathf.Abs(Vector3.Dot(tangent, Vector3.up)) > 0.9f ? Vector3.right : Vector3.up;
                prevN = Vector3.ProjectOnPlane(seed, tangent).normalized;
            }
            else
            {
                Vector3 t2 = Vector3.ProjectOnPlane(prevN, tangent);
                prevN = (t2.sqrMagnitude > 1e-6f) ? t2.normalized : prevN;
            }
            Vector3 binormal = Vector3.Cross(tangent, prevN);

            // Only live rings advance arc; surplus rings sit on the tail.
            if (i > 0 && i < n) arc += (r.pts[src] - r.pts[src - 1]).magnitude;

            float age = Mathf.Max(now - r.birth[src], 0f);
            float ageFrac = Mathf.Clamp01(age / Mathf.Max(life, 0.01f));

            // Roll-up in metres, on a real arc length.
            float grow = Mathf.Clamp01(arc / Mathf.Max(rollup, 0.01f));
            grow = grow * grow * (3f - 2f * grow);
            // Close the last stretch to nothing in both radius and alpha; a full-radius tube end
            // cannot be hidden by alpha.
            float endPos = (n > 1) ? (float)src / (n - 1) : 0f;
            float endFade = 1f - Mathf.Clamp01((endPos - (1f - ribbonEndFade)) / Mathf.Max(ribbonEndFade, 0.01f));
            endFade = endFade * endFade * (3f - 2f * endFade);
            // Radius closes over a shorter window in the contrail band; alpha keeps the full
            // window.
            float endWin = Mathf.Lerp(ribbonEndFade, contrailEndFade, contrailBlend);
            float endFadeR = 1f - Mathf.Clamp01((endPos - (1f - endWin)) / Mathf.Max(endWin, 0.01f));
            endFadeR = endFadeR * endFadeR * (3f - 2f * endFadeR);
            float spread = ContrailSpread(ageFrac, contrailBlend);

            // See ribbonHeadRampFraction. Width comes up linearly, alpha as 1-(1-u)^2.
            float headU = Mathf.Clamp01(endPos / Mathf.Max(ribbonHeadRampFraction, 0.01f));
            float headWidth = Mathf.Lerp(ribbonHeadWidthFloor, 1f, headU);
            float headAlpha = 1f - (1f - headU) * (1f - headU);

            // tailRatio (how much of the core is still condensed) and CoreGrowth (the core
            // diffusing) are separate; visible radius is their product.
            float coreG = CoreGrowth(age, 1f + groundTurbScale * r.geFac[src]);

            // Breakdown: everything past the burst scales with depth, which is continuous in swirl,
            // so neighbouring rings do not step. Past the burst: extra diffusion (with dimming), a
            // bubble flare centred two widths downstream, and a fade quick for a bubble and gentler
            // for a spiral.
            float bdP = age - r.bdT[src];
            float bdDepth = r.bdDepth[src];
            bool broken = bdP > 0f && bdDepth > 0f;
            float bdDiff = 1f, bdFlare = 1f, bdFade = 1f, bdOnset = 0f, bdBubble = r.bdMode[src];
            float bdSize = 0f;
            if (broken)
            {
                bdOnset = 1f - Mathf.Exp(-bdP / Mathf.Max(breakdownDevelopTime, 0.01f));
                bdSize = Mathf.Clamp01(bdDepth / Mathf.Max(breakdownSizeSaturateDepth, 0.01f));
                bdSize = bdSize * bdSize * (3f - 2f * bdSize);
                bdDiff = Mathf.Min(Mathf.Sqrt(1f + frameTurbRate * breakdownDiffusion * bdDepth * bdP),
                                   breakdownGrowthMax);
                if (bdBubble > 0f)
                {
                    float x = (bdP - 2f * bubbleWidthT) / Mathf.Max(bubbleWidthT, 0.001f);
                    bdFlare = 1f + bubbleAmp * bdSize * bdBubble * Mathf.Exp(-x * x);
                }
                bdFade = Mathf.Exp(-bdP * bdDepth * Mathf.Lerp(spiralFadeRate, bubbleFadeRate, bdBubble));
            }
            float diffG = coreG * bdDiff;

            float radius = ribbonRadiusScale * r.shed[src] * widthScale
                           * Mathf.Lerp(headWidthFraction, 1f, grow)
                           * Mathf.Lerp(1f, tailRatio, ageFrac)
                           * diffG * bdFlare * spread
                           * endFadeR * headWidth;

            // Ramp in over the head, full until ribbonPeakFraction, then a taper over the rest.
            float fadeT = Mathf.Clamp01((ageFrac - ribbonPeakFraction)
                                        / Mathf.Max(1f - ribbonPeakFraction, 0.01f));
            float alpha = live
                ? Mathf.Clamp01(r.shed[src] * trailAlphaGain * (1f - Mathf.Pow(fadeT, ribbonFadePow))
                                * endFade * headAlpha * CoreDim(diffG) * bdFade
                                * Mathf.Pow(spread, -contrailSpreadDimPow))
                : 0f;
            if (!live) radius = 0f;

            // Bend toward the centreline, easing in over ribbonInwardGrow and then holding. The
            // direction was set at birth; only the magnitude eases.
            float ease = Mathf.Clamp01(arc / Mathf.Max(ribbonInwardGrow, 0.01f));
            ease = ease * ease * (3f - 2f * ease);
            // Scaled by the source's strength, not the ring's shed, so load wobble does not zigzag
            // the centreline. The pair settles at a position set by the span loading's shape, (1 -
            // pi/4) b/2 inboard for an elliptic wing, not by the lift.
            Vector3 centre = p + r.inDir[src] * (ribbonInwardAmp * inwardScale * ease);

            // Ground-effect drift: zero at the head, opening linearly with the ring's own age.
            if (r.geFac[src] > 0f)
            {
                Vector3 drift = r.geVel[src] * age;
                if (drift.sqrMagnitude > groundCap * groundCap) drift = drift.normalized * groundCap;
                centre += drift;
            }

            // Spiral breakdown. The basis is built from this ring's own tangent and local up, not
            // the transported frame, which is re-derived from the head every frame and would
            // rewrite every spiral already laid.
            if (broken && bdBubble < 1f)
            {
                Vector3 sN = Vector3.ProjectOnPlane(frameUp, tangent);
                if (sN.sqrMagnitude < 1e-4f) sN = Vector3.ProjectOnPlane(Vector3.right, tangent);
                sN.Normalize();
                Vector3 sB = Vector3.Cross(tangent, sN);
                // Decay time is spiralDecayTurns wavelengths of air passing at flight speed.
                float sDecayT = spiralDecayTurns * spiralLambda / Mathf.Max(frameSpeed, 1f);
                float sAmp = spiralAmpCores * frameR0Vis * bdSize * (1f - bdBubble) * bdOnset
                             * Mathf.Exp(-bdP / Mathf.Max(sDecayT, 0.01f));
                float sPhase = r.sPhase[src] + spiralPrecession * age + r.seed;
                centre += (sN * Mathf.Cos(sPhase) + sB * Mathf.Sin(sPhase)) * sAmp;
            }

            // Helix retained but inert at ribbonHelixAmp = 0.
            if (ribbonHelixAmp > 0f)
            {
                float hAmp = ribbonHelixAmp * r.shed[src]
                             * Mathf.Clamp01(arc / Mathf.Max(ribbonHelixGrow, 0.01f));
                float hPhase = (r.odo[src] / Mathf.Max(ribbonHelixWavelength, 1f)) * Mathf.PI * 2f;
                centre += (prevN * Mathf.Cos(hPhase) + binormal * Mathf.Sin(hPhase)) * hAmp;
            }

            float twist = ribbonTwistPerMetre * r.odo[src];   // frozen for the same reason
            float uy = ageFrac;

            // Twist aliasing: ring spacing is speed / frame rate, so past a quarter turn per ring
            // the rotation aliases into a sawtooth. Where a ring's spacing nears that, its section
            // eases to a round tube of the same area.
            float seg = src > 0 ? (r.pts[src] - r.pts[src - 1]).magnitude
                      : (n > 1 ? (r.pts[1] - r.pts[0]).magnitude : 0f);
            float twistStep = ribbonTwistPerMetre * seg;
            float shapeKeep = 1f - Mathf.SmoothStep(0f, 1f,
                (twistStep - twistAliasStart) / Mathf.Max(twistAliasFull - twistAliasStart, 1e-3f));
            float roundR = Mathf.Sqrt(ribbonAspect);          // equal-area circle, in major-axis units
            float majorR = radius * Mathf.Lerp(roundR, 1f, shapeKeep);
            float minorR = radius * Mathf.Lerp(roundR, ribbonAspect, shapeKeep);
            float stripeAmt = ribbonStripe * shapeKeep;

            // cos/sin of the twist once, then rotate the precomputed unit circle into place.
            float tc = Mathf.Cos(twist), ts = Mathf.Sin(twist);
            for (int k = 0; k < K; k++)
            {
                float ca = tc * ringCos[k] - ts * ringSin[k];
                float sa = ts * ringCos[k] + tc * ringSin[k];

                // Elliptical, so the twist has an orientation to show.
                Vector3 off = prevN * (ca * majorR) + binormal * (sa * minorR);
                Vector3 w = centre + off - origin;

                // Bright seam on one side; the twist carries it around.
                float stripe = 1f - stripeAmt * (0.5f - 0.5f * ca);

                int vi = i * K + k;
                r.verts[vi] = w;
                r.cols[vi] = new Color(1f, 1f, 1f, alpha * stripe);
                r.uvs[vi] = new Vector2((float)k / K, uy);
                lo = Vector3.Min(lo, w); hi = Vector3.Max(hi, w);
            }
        }

        if (!loggedRibbon && r.count > 120)   // established wake, not a 21-ring stub
        {
            loggedRibbon = true;
            float aMin = 2f, aMax = -1f;
            for (int v = 0; v < r.cols.Length; v++)
            {
                if (r.cols[v].a < aMin) aMin = r.cols[v].a;
                if (r.cols[v].a > aMax) aMax = r.cols[v].a;
            }
            var mr2 = r.obj.GetComponent<MeshRenderer>();
            string shader = (mr2 != null && mr2.sharedMaterial != null) ? mr2.sharedMaterial.shader.name : "NULL";
            Vector3 bsize = hi - lo;
            Log($"tube rings={r.count}/{rings} verts={r.mesh.vertexCount} " +
                      $"boundsSize=({bsize.x:F1},{bsize.y:F1},{bsize.z:F1}) " +
                      $"arc={arc:F1}m twistTail={(ribbonTwistPerMetre * arc):F2}rad " +
                      $"alpha={aMin:F2}..{aMax:F2} rendOn={(mr2 != null && mr2.enabled)} " +
                      $"active={r.obj.activeInHierarchy} layer={r.obj.layer} shader={shader}");
        }

        r.mesh.vertices = r.verts;
        r.mesh.colors = r.cols;
        r.mesh.uv = r.uvs;
        // Set explicitly rather than RecalculateBounds; the extents are known from the loop.
        r.mesh.bounds = new Bounds((lo + hi) * 0.5f, hi - lo);
    }

    Transform CreateWingtipAnchor(Part wing, bool isRight, HashSet<Part> restrictTo = null)
    {
        // Step 1: find the true tip (span-based and cant-aware, see TryFindTipVertex).
        Vector3 anchorWorld;
        if (!TryFindTipVertex(wing, isRight, out anchorWorld)) return null;

        // Step 2: climb to a part above this X/Z position, skipped if this is already an extremity
        // wing.
        {
            // TryGetPartBounds, not a raw renderer lookup: this test decides whether the anchor
            // stays on the true tip, and raw bounds included whatever effect meshes were enabled,
            // which moved anchors after a revert.
            Bounds rCheckB;
            if (TryGetPartBounds(wing, out rCheckB))
            {
                float maxExt = 0f;
                foreach (var part in vessel.parts)
                {
                    Bounds rrB;
                    if (!TryGetPartBounds(part, out rrB)) continue;
                    maxExt = Mathf.Max(maxExt, LateralReach(part, rrB));
                }

                float thisExt = LateralReach(wing, rCheckB);
                Log($"CLIMB GATE part={wing.name} ext={thisExt:F2} vs widest={maxExt:F2} "
                          + $"— {(thisExt >= maxExt * 0.95f ? "already the tip, NO climb" : "climbing")}");

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

            Vector3 baseLocal = ToVesselFrame(anchorWorld);
            Log($"CLIMB CHECK iter={safety} baseY={baseLocal.y:F2} part={currentPart.name}");

            Part bestAbovePart = null;
            float bestHorizDist = float.MaxValue;
            float bestTopY = float.MinValue;
            float bestArea = 0f;

            foreach (Part p in vessel.parts)
            {
                // The climb stays within the anchor's own assembly, so a canard under the main wing
                // cannot hop onto it and draw a second vortex.
                if (restrictTo != null && !restrictTo.Contains(p)) continue;

                bool isLift = IsLiftPart(p);
                bool isControl = IsControlPart(p);

                // Only real lifting surfaces.
                if (!isLift) continue;

                // Never re-select the part being stood on.
                if (p == currentPart) continue;

                Bounds pb;
                if (!TryGetPartBounds(p, out pb)) continue;

                float sizeX = pb.size.x;
                float sizeY = pb.size.y;
                float sizeZ = pb.size.z;
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

                Bounds b = pb;
                // Projected extents, not the corners run through the transform: b.min/b.max are
                // corners of a WORLD-aligned box, and converting those two points into a rotated
                // frame does not give that box's extent in the new frame.
                Vector3 centerLocal = ToVesselFrame(b.center);
                Vector3 halfLocal = new Vector3(ExtentAlong(b, axLateral), ExtentAlong(b, axVert), ExtentAlong(b, axLength));
                Vector3 minLocal = centerLocal - halfLocal;
                Vector3 maxLocal = centerLocal + halfLocal;

                // RELAX: use part center height instead of bounds max (handles rotated/canted meshes)
                Vector3 currCenterLocal = ToVesselFrame(currentPart.transform.position);
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

                Log($"CLIMB CAND part={p.name} horiz={horizDist:F2} topY={maxLocal.y:F2} centerY={centerLocal.y:F2}");
            }

            if (bestAbovePart != null)
            {
                Bounds b;
                if (!TryGetPartBounds(bestAbovePart, out b)) break;
                Vector3 maxLocal = ToVesselFrame(b.center) +
                                   new Vector3(ExtentAlong(b, axLateral), ExtentAlong(b, axVert), ExtentAlong(b, axLength));

                Log($"CLIMB HIT part={bestAbovePart.name} horiz={bestHorizDist:F2} topY={maxLocal.y:F2}");

                currentPart = bestAbovePart;
                // Project slightly below the top, so the anchor sits on the surface instead of
                // hovering above it.
                float surfaceY = maxLocal.y - 0.02f; // small offset downward
                Vector3 nextLocal = new Vector3(baseLocal.x, surfaceY, baseLocal.z);
                anchorWorld = FromVesselFrame(nextLocal);
                foundHigher = true;
            }
            else
            {
                Log($"CLIMB STOP at part={currentPart.name}");
            }
        }

        // Step 3: recompute the tip on the highest part, keeping the step 1 tip if it has no usable
        // mesh.
        Vector3 finalTip;
        if (TryFindTipVertex(currentPart, isRight, out finalTip))
        {
            anchorWorld = finalTip;

            // Seat the anchor just inside the surface by nudging toward the part's own render
            // centre, which is cant-agnostic.
            Bounds finalB;
            Vector3 toward = TryGetPartBounds(currentPart, out finalB)
                ? finalB.center
                : currentPart.transform.position;
            Vector3 inward = toward - anchorWorld;
            if (inward.sqrMagnitude > 1e-6f)
                anchorWorld += inward.normalized * 0.03f;
        }

        GameObject anchor = new GameObject("Anchor");
        anchor.transform.position = anchorWorld;
        anchor.transform.parent = currentPart.transform;
        return anchor.transform;
    }

    void Update()
    {
        // Bound to one craft for life (see `target`); vessel stays null until Start() has waited
        // out unpack and settle.
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

        // Do not reset on bad frames: degrade update quality but keep state.
        if (badFrame)
            dt = 0.01f; // clamp aggressively


        // RECOVERY: ensure renderers come back clean after a bad frame
        for (int i = 0; i < trails.Count; i++)
        {
            // ...except where a tube has taken over, so its TrailRenderer is not re-enabled every
            // frame.
            bool tubeOwnsThis = i < ribbons.Count && ribbons[i] != null;
            if (!tubeOwnsThis && !trails[i].enabled) trails[i].enabled = true;
            if (!lines[i].enabled && useLineMode) lines[i].enabled = true;
        }

        float speed = vessel.GetSrfVelocity().magnitude;

        boundsRefreshTimer += dt;
        if (boundsRefreshTimer >= boundsRefreshInterval)
        {
            ComputeVesselBounds();
            boundsRefreshTimer = 0f;
        }

        // See staleCheckTimer. Rebuilding discards the wake of the parts that just left.
        staleCheckTimer += dt;
        if (staleCheckTimer >= staleCheckInterval)
        {
            staleCheckTimer = 0f;
            if (trails.Count > 0 && SourcesStale())
            {
                Log("sources no longer belong to this vessel — re-detecting");
                ClearSources();
                ComputeVesselBounds();
                FindVortexSources();
                return;   // lists were just rebuilt; pick it up next frame
            }
        }

        // Vacuum: the early-out fires only once there is nothing left to draw. atmFactor already
        // takes visible to zero across the 0.01-0.10 density band and both renderers close out
        // through their own paths, so a fade is never truncated.
        if (vessel.atmDensity <= 0.01f && NothingLeftToDraw())
        {
            for (int i = 0; i < trails.Count; i++)
            {
                trails[i].emitting = false;
                if (lines[i].enabled) lines[i].enabled = false;
            }
            return;
        }

        Vector3 flow = vessel.GetSrfVelocity().normalized;

        // Lift-driven load factor: summed per-surface lift over weight, not felt G, which counts
        // thrust and impacts and misses dumped lift. Under FAR, ModuleLiftingSurface is gone, so
        // FAR's wing modules are summed instead.
        float liftKN;
        if (FARBridge.Available)
        {
            liftKN = FARBridge.GetLiftKN(vessel, farWings, flow);
        }
        else
        {
            liftKN = 0f;
            for (int s = 0; s < liftSurfaces.Count; s++)
            {
                var ms = liftSurfaces[s];
                if (ms == null) continue;   // Unity null check also catches destroyed parts
                liftKN += ms.liftForce.magnitude;
            }
        }
        float weightKN = vesselMassTons * gravityAccel;
        float rawLiftG = (liftKN > 0f && weightKN > 0.001f)
            ? Mathf.Min(liftKN / weightKN, 15f)
            : Mathf.Min((float)vessel.geeForce, 15f);   // degrade rather than go dark

        // Frame-rate independent smoothing of the measurement (see liftSmoothTime).
        smoothedLiftG = Mathf.Lerp(smoothedLiftG, rawLiftG, 1f - Mathf.Exp(-dt / liftSmoothTime));
        float g = smoothedLiftG;

        // See onsetLoadRef: thresholds move with rho*V so the effect keys off circulation; the
        // scale is 1 at sea level and 250 m/s.
        float rho = (float)vessel.atmDensity;
        float onsetScale = Mathf.Clamp(rho * speed / (onsetRefDensity * onsetRefSpeed),
                                       onsetScaleMin, onsetScaleMax);
        float gOnset = onsetLoadRef * onsetScale;

        airborneBlend = Mathf.MoveTowards(airborneBlend, vessel.LandedOrSplashed ? 0f : 1f,
                                          airborneBlendSpeed * dt);
        float flyingGate = airborneBlend
                           * Mathf.Clamp01((speed - flyingSpeedMin)
                                           / Mathf.Max(flyingSpeedFull - flyingSpeedMin, 0.01f));
        float targetIntensity = flyingGate
            * Mathf.Clamp01((g - gOnset) / Mathf.Max(onsetSpanRef * onsetScale, 0.01f));
        currentIntensity = (targetIntensity > currentIntensity)
            ? Mathf.MoveTowards(currentIntensity, targetIntensity, buildSpeed * dt)
            : Mathf.MoveTowards(currentIntensity, targetIntensity, decaySpeed * dt);

        float intensity = currentIntensity;

        // Circulation model (see massSpanFactor): mass/span cached on the bounds timer, speed live.
        float circFactor = sizeFactor;

        // Regime selection: line mode is kept for the extreme-velocity regime. Gated on the mode,
        // not the renderer: suppressing only shouldUseLine would leave useLineMode flipping at
        // lineModeSpeed and take the trail's fade to zero, leaving a booster with no wake.
        bool lineModeAllowed = rocketUseLineMode || verticalLauncher != 1;
        bool shouldEnterLineMode = speed >= lineModeSpeed && lineModeAllowed;
        // Forced exit as well, so a craft in line mode when focus switches to a booster hands back
        // to the trail.
        bool shouldExitLineMode = speed <= lineModeSpeed * 0.85f || !lineModeAllowed;

        if (!useLineMode && shouldEnterLineMode) { useLineMode = true; for (int h = 0; h < lineHistory.Count; h++) lineHistory[h].Clear(); }
        else if (useLineMode && shouldExitLineMode) { useLineMode = false; for (int h = 0; h < lineHistory.Count; h++) lineHistory[h].Clear(); }

        // 0 below the condensation band, 1 at the top: drives the maneuver-vortex -> contrail
        // transition across trail lifetime, spacing, width, spread and opacity.
        if (contrailBody != vessel.mainBody) RefreshContrailBand(vessel.mainBody);
        float ambientK = (float)vessel.atmosphericTemperature;
        float contrailBlend = Mathf.Clamp01((bodyContrailWarm - ambientK)
                                            / Mathf.Max(bodyContrailWarm - bodyContrailCold, 0.01f));

        // Approach-vapor floor (see humidDensityMin): every term must hold at once - deep in the
        // atmosphere, a wing near its lift limit, and the wing carrying the aircraft.
        // How lit the wake is, 0 (night) to 1 (day); see nightFloorScale and VortexVapor.Sunlight.
        float sunElevation;
        float sunLit = VortexVapor.Sunlight.Fraction(vessel, out sunElevation);
        if (!loggedSunFlux)
        {
            loggedSunFlux = true;
            Log($"illumination: sun {sunElevation:F1} deg above the horizon (flux={vessel.solarFlux:F0}) "
                      + $"sunLit={sunLit:F2} scale={Mathf.Lerp(nightFloorScale, dayLightScale, sunLit):F2}");
        }

        float rhoASL = (vessel.mainBody != null) ? (float)vessel.mainBody.atmDensityASL : 1.225f;
        float depthBlend = Mathf.Clamp01((rho / Mathf.Max(rhoASL, 0.001f) - humidDensityMin)
                                         / Mathf.Max(1f - humidDensityMin, 0.01f));
        float clProxy = (liftKN * 1000f)
                        / Mathf.Max(0.5f * rho * speed * speed * vesselSpan * vesselSpan, 1f);
        float loadBlend = Mathf.Clamp01((clProxy - humidLoadMin)
                                        / Mathf.Max(humidLoadFull - humidLoadMin, 0.01f));
        float shareBlend = Mathf.Clamp01((g - humidLiftShareMin)
                                         / Mathf.Max(humidLiftShareFull - humidLiftShareMin, 0.01f));
        float approachFloor = humidFloor * depthBlend * loadBlend * shareBlend * flyingGate;

        // Viscous core growth: scalar work once per frame, resolved to a single rate in 1/s. See
        // coreGrowthEnable.
        coreGrowthRate = 0f;
        // The circulation Gamma = L/(rho*V*b), hoisted because ground effect and breakdown need it
        // too.
        float vRef = Mathf.Max(speed, 1f);
        bool airOk = rho > 0.001f;
        float gamma = airOk ? (liftKN * 1000f) / Mathf.Max(rho * vRef * vesselSpan, 0.001f) : 0f;
        float r0 = Mathf.Max(coreSpanFraction * vesselSpan, 0.05f);

        frameGamma = gamma;
        frameSpeed = vRef;
        frameB0 = 0.25f * Mathf.PI * Mathf.Max(vesselSpan, 0.1f);
        frameR0Vis = r0;
        frameUp = (Vector3)vessel.upAxis;
        frameVesselPos = (Vector3)vessel.CoM;
        frameTurbRate = 0f;

        if (airOk)
        {
            float tK = Mathf.Max(ambientK, 1f);
            float mu = sutherlandMu0 * Mathf.Pow(tK / sutherlandT0, 1.5f)
                       * (sutherlandT0 + sutherlandS) / (tK + sutherlandS);
            float nuRaw = mu / rho;                       // kinematic viscosity, m^2/s

            float nuEff = Mathf.Min(nuRaw, nuMolecularCap)
                          + squireDelta * gamma * (1f + ambientTurbScale * depthBlend);

            frameTurbRate = (4f * lambOseenAlpha * nuEff) / (r0 * r0);
            coreGrowthRate = coreGrowthEnable * frameTurbRate;

            bool lowShot = coreGrowthEnable > 0f && !loggedCoreGrowth && intensity > 0.3f && speed > 60f;
            bool highShot = coreGrowthEnable > 0f && !loggedCoreGrowthHigh && contrailBlend > 0.5f && intensity > 0.2f;
            if (lowShot || highShot)
            {
                if (lowShot) loggedCoreGrowth = true; else loggedCoreGrowthHigh = true;
                // Wake life this regime gets at a steady frame rate: the lerp of maneuverTimeMax to
                // contrailTimeMax.
                float wakeLife = Mathf.Lerp(maneuverTimeMax, contrailTimeMax, contrailBlend);
                float gMid = CoreGrowth(wakeLife * 0.5f), gEnd = CoreGrowth(wakeLife);
                Log($"core growth ({(lowShot ? "low" : "band")}): T={tK:F0}K "
                          + $"rho={rho:F3} cb={contrailBlend:F2} nu={nuRaw:E2} Gamma={gamma:F0}m2/s "
                          + $"nuEff={nuEff:E2} r0={r0:F2}m rate={coreGrowthRate:F3}/s life={wakeLife:F1}s | "
                          + $"mid: w x{gMid:F2} a x{CoreDim(gMid):F2} | "
                          + $"tail: w x{gEnd:F2} a x{CoreDim(gEnd):F2}"
                          + (gEnd >= coreGrowthMax - 0.001f ? "  <<< CLAMPED" : ""));
            }
        }

        // Ground effect, vessel level. The raycast height sees runways and buildings, so it is
        // preferred while within 50 m of radar altitude; a stale raycast reading low could fake
        // ground effect. Over an ocean the sea surface is the ground plane.
        {
            double agl = vessel.radarAltitude;
            if (vessel.heightFromTerrain >= 0f && System.Math.Abs(vessel.heightFromTerrain - agl) < 50.0)
                agl = vessel.heightFromTerrain;
            if (vessel.mainBody != null && vessel.mainBody.ocean)
                agl = System.Math.Min(agl, vessel.altitude);
            frameAGL = (float)agl;

            frameGroundOn = groundEffectEnable > 0f && airOk && agl >= 0.0
                            && agl < groundEffectCeilingSpans * Mathf.Max(vesselSpan, 1f);
            float hV = Mathf.Max(frameAGL, 0f);
            float b0sq = frameB0 * frameB0;
            frameGroundFac = frameGroundOn ? groundEffectEnable * b0sq / (4f * hV * hV + b0sq) : 0f;

            if (flyingGate > 0.9f && frameGroundFac > peakGroundFac) peakGroundFac = frameGroundFac;

            // Either source of visible vapor counts, including a gentle approach lit only by the
            // floor.
            if (!loggedGroundEffect && frameGroundFac > 0.3f && flyingGate > 0.9f
                && (intensity > 0.2f || approachFloor > 0.05f))
            {
                loggedGroundEffect = true;
                float hEff = Mathf.Max(hV, 0.5f * frameB0);
                float vOut = gamma / (4f * Mathf.PI) * b0sq / (hEff * (4f * hEff * hEff + b0sq));
                float w0 = gamma / (2f * Mathf.PI * frameB0);
                float life = Mathf.Lerp(maneuverTimeMax, contrailTimeMax, contrailBlend);
                Log($"ground effect: AGL={hV:F1}m (raycast={vessel.heightFromTerrain:F1} "
                          + $"radar={vessel.radarAltitude:F1}) b0={frameB0:F1}m h/b0={hV / frameB0:F2} "
                          + $"factor={frameGroundFac:F2} Gamma={gamma:F0}m2/s | drift={vOut:F2}m/s "
                          + $"(free descent {w0:F2}m/s) -> {Mathf.Min(vOut * life * groundSpreadScale, groundSpreadMaxSpans * vesselSpan):F1}m "
                          + $"outboard over {life:F1}s | sink kept x{1f - frameGroundFac:F2} "
                          + $"growth rate x{1f + groundTurbScale * frameGroundFac:F2}");
            }
        }

        // Breakdown, vessel level. Swirl is smoothed because Gamma reads raw lift, and ropes
        // flickering in and out of breakdown would pop. Gated on flyingGate: on the runway the CL
        // proxy reports gear attitude.
        {
            float swirlCoeff = 0.715f / (2f * Mathf.PI * Mathf.Max(viscousCoreSpanFraction, 0.001f));
            float swirlRaw = (breakdownEnable > 0f && airOk && speed > flyingSpeedMin)
                ? swirlCoeff * gamma / (Mathf.Max(vesselSpan, 0.1f) * vRef)
                : 0f;
            smoothedSwirl = Mathf.Lerp(smoothedSwirl, swirlRaw,
                                       1f - Mathf.Exp(-dt / Mathf.Max(breakdownSmoothTime, 0.01f)));
            float swirl = smoothedSwirl;

            frameBreakDepth = breakdownEnable * flyingGate
                              * Mathf.Clamp01((swirl - breakdownSwirlOnset)
                                              / Mathf.Max(breakdownSwirlFull - breakdownSwirlOnset, 0.01f));
            frameBreakMode = Mathf.Clamp01((swirl - bubbleSwirlMin)
                                           / Mathf.Max(bubbleSwirlMax - bubbleSwirlMin, 0.01f));
            float station = Mathf.Lerp(breakdownStationFarSpans, breakdownStationNearSpans, frameBreakDepth)
                            * Mathf.Max(vesselSpan, 0.1f);
            frameBreakT = frameBreakDepth > 0f ? station / vRef : 1e6f;

            if (flyingGate > 0.9f && swirl > peakSwirl)
            {
                peakSwirl = swirl; peakSwirlSpeed = speed; peakSwirlGamma = gamma;
            }

            if (!loggedBreakdown && frameBreakDepth > 0.05f && intensity > 0.2f)
            {
                loggedBreakdown = true;
                Log($"breakdown engaged: swirl={swirl:F2} (onset {breakdownSwirlOnset:F2}) "
                          + $"V={speed:F0}m/s Gamma={gamma:F0}m2/s clProxy={clProxy:F2} "
                          + $"depth={frameBreakDepth:F2} mode={(frameBreakMode <= 0f ? "spiral" : frameBreakMode >= 1f ? "bubble" : "mixed " + frameBreakMode.ToString("F2"))} "
                          + $"burst at {station:F0}m ({frameBreakT:F2}s) behind the tip");
            }
        }

        // Fading out on leaving the atmosphere is atmFactor's job; air density is body-relative.

        lineActivationBlend = Mathf.MoveTowards(lineActivationBlend, useLineMode ? 1f : 0f, lineActivationSpeed * dt);
        float trailFade = 1f - lineActivationBlend;
        float lineFade = lineActivationBlend;

        // Guard reference speed: the faster of this frame and last, since AddExcess() zeroes
        // velocity on the frame Krakensbane engages.
        float rbSpeed = vessel.rb_velocity.magnitude;
        float guardSpeed = Mathf.Max(rbSpeed, lastRbSpeed);
        lastRbSpeed = rbSpeed;

        // True away-from-planet direction, not the vessel's own up, which follows attitude.
        Vector3 worldUp = (Vector3)vessel.upAxis;

        // Advance the shed-history cursor on a fixed cadence; the current bucket is overwritten
        // with live intensity every frame.
        shedSampleTimer += dt;
        if (shedSampleTimer >= shedSampleInterval)
        {
            shedSampleTimer = 0f;
            shedWrite = (shedWrite + 1) % shedSamples;
        }

        // Wake sink: a shed pair descends under its own induced flow, ~1.5 m/s early on. Nudging
        // every vertex down each frame gives 'older has sunk further'. Commutes with
        // OnFloatingOriginShift; trailSinkRate 0 disables.
        // How hard the wake is winding, from load factor: floored at helixBaseDrive, full by
        // helixGFull, faded out toward the contrail band.
        float helixDrive = Mathf.Lerp(helixBaseDrive, 1f,
                                      Mathf.Clamp01((g - helixGMin) / Mathf.Max(helixGFull - helixGMin, 0.01f)))
                           * (1f - contrailBlend * 0.8f);
        bool doHelix = helixRadiusMax > 0f && helixDrive > 0.001f;

        if (trailSinkRate > 0f || doHelix)
        {
            sinkTimer += dt;
            if (sinkTimer >= sinkUpdateInterval)
            {
                float sinkDt = sinkTimer;
                sinkTimer = 0f;

                Vector3 hPerp = Vector3.Cross(flow, worldUp).normalized;

                // Angular rate that yields the requested spatial period at the current speed.
                float helixOmega = (2f * Mathf.PI * Mathf.Max(speed, 1f)) / Mathf.Max(helixWavelength, 1f);
                float nowT = Time.time;

                for (int s = 0; s < trails.Count; s++)
                {
                    var strail = trails[s];
                    int sn = strail.positionCount;
                    if (sn < 2) continue;

                    // The newest end is determined at runtime: it is whichever end sits at the
                    // emitter.
                    Vector3 emitter = trailObjs[s].transform.position;
                    bool zeroIsNewest = (strail.GetPosition(0) - emitter).sqrMagnitude
                                     <= (strail.GetPosition(sn - 1) - emitter).sqrMagnitude;

                    float hAge = trailHeadAges[s];
                    float tAge = Mathf.Max(trailAges[s], hAge + 0.01f);
                    float phase = (s + 1) * 2.39996f;   // golden angle, so the four never sync up

                    for (int sj = 0; sj < sn; sj++)
                    {
                        float ageFrac = (float)sj / (sn - 1);
                        if (!zeroIsNewest) ageFrac = 1f - ageFrac;

                        Vector3 p = strail.GetPosition(sj);

                        if (trailSinkRate > 0f)
                        {
                            float rate = trailSinkRate * Mathf.Lerp(1f, sinkAgeFalloff, ageFrac);
                            // Near the ground the image vortex cancels the descent more as the
                            // vertex gets lower; per vertex, from its own height.
                            if (frameGroundOn)
                            {
                                float hv = Mathf.Max(frameAGL + Vector3.Dot(p - frameVesselPos, worldUp), 0f);
                                rate *= (4f * hv * hv) / (4f * hv * hv + frameB0 * frameB0);
                            }
                            p -= worldUp * (rate * sinkDt);
                        }

                        if (doHelix)
                        {
                            // Radius grows with the vertex's own age, so it is zero at the anchor.
                            float age = Mathf.Lerp(hAge, tAge, ageFrac);
                            float amp = helixRadiusMax * helixDrive;
                            float rNow = amp * Mathf.Clamp01(age / helixGrowTime);
                            float rNext = amp * Mathf.Clamp01((age + sinkDt) / helixGrowTime);
                            float dR = rNext - rNow;

                            // Only vertices still opening out need touching.
                            if (dR > 0f)
                            {
                                // Phase frozen per vertex: (now - age) is when it was laid, so the
                                // corkscrew sits still in the air and the per-frame delta stays
                                // radial and small.
                                float th = helixOmega * (nowT - age) + phase;
                                p += (hPerp * Mathf.Cos(th) + worldUp * (Mathf.Sin(th) * 0.6f)) * dR;
                            }
                        }

                        strail.SetPosition(sj, p);
                    }

                    // Diagnostic: how far the trail bows from the straight chord over its first 60
                    // points.
                    if (!loggedHelixCheck && s == 0 && sn > 60)
                    {
                        loggedHelixCheck = true;
                        int i0 = zeroIsNewest ? 0 : sn - 1;
                        int step = zeroIsNewest ? 1 : -1;
                        Vector3 pa = strail.GetPosition(i0);
                        Vector3 pb = strail.GetPosition(i0 + step * 59);
                        Vector3 axis = pb - pa;
                        float axisLen = axis.magnitude;
                        float maxDev = 0f;
                        if (axisLen > 0.01f)
                        {
                            Vector3 nrm = axis / axisLen;
                            for (int q = 1; q < 59; q++)
                                maxDev = Mathf.Max(maxDev,
                                    Vector3.ProjectOnPlane(strail.GetPosition(i0 + step * q) - pa, nrm).magnitude);
                        }
                        Log($"helix check g={g:F2} drive={helixDrive:F3} " +
                                  $"commandedAmp={helixRadiusMax * helixDrive:F2}m " +
                                  $"chord={axisLen:F0}m measuredBow={maxDev:F2}m points={sn}");
                    }
                }
            }
        }

        for (int i = 0; i < trails.Count; i++)
        {
            var tr = trails[i];
            var lr = lines[i];
            var obj = trailObjs[i];
            var anchor = anchors[i];
            var history = lineHistory[i];

            // Belt and braces with the staleness check: a part can be destroyed between checks.
            if (anchor == null) continue;

            float visible = intensity * strengths[i] * circFactor;

            // Applied before the light and density multipliers, so a night landing does not glow.
            if (approachFloor > 0f)
                visible = Mathf.Max(visible, approachFloor * strengths[i]);

            // See nightFloorScale; dayLightScale is the tuned daytime look.
            float nightScale = Mathf.Lerp(nightFloorScale, dayLightScale, sunLit);
            visible *= nightScale;

            float atmFactor = Mathf.Clamp01((float)((vessel.atmDensity - 0.01f) / 0.09f));
            visible *= atmFactor;

            // Cold air is near saturation, so the contrail floor makes the effect default-on up
            // high. Scaled by atmFactor, or an unscaled floor would relight every vortex in orbit,
            // and by nightScale, since no light means nothing to scatter.
            if (contrailBlend > 0f)
            {
                visible = Mathf.Max(visible,
                                    Mathf.Lerp(0.1f, 0.5f, contrailBlend)
                                    * strengths[i] * atmFactor * nightScale);
                visible *= Mathf.Lerp(1f, 1.5f, contrailBlend);
            }

            if (strengths[i] < 1f)
            {
                // Onset interpolated by span ratio (a full-size canard near the main wing's 3 g, a
                // small nose canard 6 g) and scaled by the same rho*V factor as the main wing, then
                // blended toward the main wing's onset by tip likeness; at full likeness it is
                // exactly the main-wing rule. The handicap is a multiple of the main wing's onset,
                // so scaling both by rho*V leaves the ratio intact (see secondaryOnsetMultMin).
                float mainLike = MainLikeness(spanRatios[i]);
                float sizeMult = SecondaryOnsetMult(spanRatios[i]);
                float gMin = Mathf.Lerp(onsetLoadRef * sizeMult, onsetLoadRef, mainLike) * onsetScale;
                // AND, not MAX: load and airspeed together, so no band lets the requirement vanish.
                float gGate = Mathf.Clamp01((g - gMin) / Mathf.Max(secondaryGSpan * onsetScale, 0.01f));
                float vGate = Mathf.Clamp01((speed - secondarySpeedMin) / Mathf.Max(secondarySpeedFull - secondarySpeedMin, 0.01f));

                // Not Lerp(gate, 1, mainLike): that bypasses a fraction of the gate, so it can
                // never reach zero and a near-main-wing canard rendered dim and constant. Tip
                // likeness already lowers the bar through gMin; at span ratio 0.95 and up
                // SecondaryStrength returns 1 and this block is skipped.
                float gate = gGate * vGate;
                visible *= gate;

                // One-shot log, the first time any secondary lights up, with every term that
                // decides it.
                if (!loggedSecondaryGate && visible > 0.002f)
                {
                    loggedSecondaryGate = true;
                    Log($"secondary live: span={spanRatios[i]:F2} mainLike={mainLike:F2} "
                              + $"sizeMult={sizeMult:F2}x g={g:F2} gMin={gMin:F2} "
                              + $"gGate={gGate:F2} vGate={vGate:F2} gate={gate:F2} "
                              + $"strength={strengths[i]:F3} visible={visible:F3}");
                }
            }

            // Player intensity (ModSettings), applied last; zero when switched off.
            visible *= VortexVapor.ModSettings.VortexScale;

            // Record what is being shed now; ApplyShedAppearance replays it along the trail.
            shedHistory[i][shedWrite] = visible;

            Vector3 anchorPos = AnchorPosition(i);

            // Discontinuity guard: origin shifts are compensated in OnFloatingOriginShift, so
            // anything left is a real teleport (vessel switch, docking). The anchor must not move
            // further than its Unity-space velocity (rb_velocity, Krakensbane-relative) allows.
            float armRadius = Vector3.Distance(anchorPos, vessel.CoM);
            float plausibleStep = (guardSpeed + vessel.angularVelocity.magnitude * armRadius)
                                  * rawDelta * 3f
                                  + Mathf.Max(5f, vesselSize);
            Vector3 anchorStep = anchorPos - lastAnchorPositions[i];
            if (anchorStep.sqrMagnitude > plausibleStep * plausibleStep)
            {
                Log($"anchor teleport {anchorStep.magnitude:F1}m > plausible {plausibleStep:F1}m, clearing trail {i}");
                tr.Clear();
                history.Clear();
                trailAges[i] = 0f; trailHeadAges[i] = 0f;
            }
            lastAnchorPositions[i] = anchorPos;

            // Clamp history growth on unstable frames.
            history.Enqueue(anchorPos);
            while (history.Count > historyLength)
                history.Dequeue();

            // The emitter sits exactly on the anchor, so the trail is welded to the wingtip.
            obj.transform.position = anchorPos;
            lastFlows[i] = flow;

            // Where a tube exists it owns the source outright, trail and line. Line mode exists
            // because accumulated trail geometry outruns its resampling at extreme speed, which
            // does not apply to a tube: ring spacing scales with speed and ribbonMaxLength bounds
            // the rope.
            bool hasRibbon = i < ribbons.Count && ribbons[i] != null;
            bool shouldUseLine = !hasRibbon && lineFade > 0.01f && visible > 0.01f;
            bool shouldUseTrail = !hasRibbon && trailFade > 0.01f && visible > 0.01f;

            // The tube is fed by visibility alone, so the line-mode blend cannot starve it.
            bool ribbonLive = hasRibbon && visible > 0.01f;

            // The tube's lifetime is read here, since tr.time is only updated in the trail block.
            float shutdownK = 1f - Mathf.Exp(-dt / Mathf.Max(wakeShutdownTau, 0.01f));
            float stability = Mathf.Clamp01((abnormalFrameThreshold - rawDelta) / abnormalFrameThreshold);
            float targetTime = Mathf.Lerp(Mathf.Lerp(maneuverTimeMin, maneuverTimeMax, stability),
                                          Mathf.Lerp(contrailTimeMin, contrailTimeMax, stability),
                                          contrailBlend);

            // See secondaryWakeLength. Main sources keep the full ribbonMaxLength; a secondary gets
            // a stub whose length follows its size.
            float wakeLength = ribbonMaxLength;
            float minLife = 0.5f;
            if (strengths[i] < 1f)
            {
                float sizeLen = secondaryWakeLength * Mathf.Clamp01(spanRatios[i] / Mathf.Max(mainLikeRatioMin, 0.01f));
                wakeLength = Mathf.Lerp(sizeLen, ribbonMaxLength, MainLikeness(spanRatios[i]));
                minLife = secondaryMinLife;
            }
            float lifeCap = Mathf.Max(wakeLength / Mathf.Max(speed, 1f), minLife);

            // ── TRAIL ─────────────────────────────────────────────────
            if (shouldUseTrail)
            {
                // Hold the line until it has faded.
                if (lineWasActive[i] && !shouldUseLine)
                {
                    lr.enabled = false; lr.positionCount = historyLength;
                    history.Clear(); tr.Clear(); tr.time = 1.5f; trailAges[i] = 0f; trailHeadAges[i] = 0f;
                    lineWasActive[i] = false;
                }
                tr.enabled = true; tr.emitting = true;

                if (!loggedTrailMat)
                {
                    loggedTrailMat = true;
                    var tm = tr.sharedMaterial;
                    Log($"trail renderer live: material={(tm == null ? "NULL (this is the magenta)" : tm.name)} "
                              + $"shader={(tm == null || tm.shader == null ? "NULL" : tm.shader.name)}");
                }

                // Low altitude: a short maneuver puff; high altitude: a long condensation trail.
                // Vertex count is frame rate * lifetime (a TrailRenderer adds at most one point per
                // update), which OnFloatingOriginShift rewrites every physics frame;
                // contrailTimeMax is set against that budget.
                tr.time = Mathf.Lerp(tr.time, Mathf.Min(targetTime, lifeCap), smoothDt * 2f);

                // Wider vertex spacing with altitude: a contrail is close to straight.
                float targetMinDist = Mathf.Lerp(Mathf.Lerp(0.35f, 0.12f, stability),
                                                 contrailMinVertexDist, contrailBlend);
                tr.minVertexDistance = Mathf.Lerp(tr.minVertexDistance, targetMinDist, smoothDt * 3f);

                // Never fully stop emission during bad frames (causes a forward-shooting bug).
                tr.emitting = true;

                trailWasActive[i] = true; lineWasActive[i] = false;
            }
            else
            {
                tr.emitting = false;
                // Retract rather than hold, so the trail does not age out over up to five seconds.
                tr.time = Mathf.Lerp(tr.time, 0f, shutdownK);
            }

            // Tube: where active it replaces the trail for that source.
            if (hasRibbon)
            {
                var rb = ribbons[i];
                // One-shot, since the trail block is skipped for a tube source.
                if (tr.enabled) { tr.Clear(); tr.enabled = false; }

                float rNorm = Mathf.Clamp(vesselSize / 30f, 0.2f, 1f);
                float rScale = 1f + Mathf.Pow(rNorm, 2.2f) * 2.0f;
                float now = Time.time;

                // Same lifetime curve as the TrailRenderer, read from targetTime, bounded by
                // ribbonMaxLength so a hypersonic re-entry cannot draw a 10 km rope.
                float life = Mathf.Max(Mathf.Min(targetTime, lifeCap), minLife);
                while (rb.count > 0 && (now - rb.birth[rb.count - 1]) > life) rb.count--;

                // Ring spacing widens so a full lifetime of wake fits the ring budget.
                float spacing = Mathf.Max(ribbonMinPointDist,
                                          (speed * life) / Mathf.Max(ribbonMaxPoints - 4, 1));
                float flowDepth = ribbonFlowDepth * contrailBlend;
                rb.spacing = spacing;
                if (ribbonLive)
                {
                    // One inward direction per frame at the anchor; the rings between get a slerp
                    // from the last head's (see RibbonInward).
                    Vector3 headIn = RibbonInward(rb, anchorPos);
                    if (rb.count == 0) PushRibbonPoint(rb, anchorPos, now, visible, flowDepth, headIn);
                    else
                    {
                        Vector3 last = rb.pts[0];
                        Vector3 lastIn = rb.inDir[0];
                        float gap = Vector3.Distance(anchorPos, last);
                        int steps = Mathf.Clamp(Mathf.CeilToInt(gap / spacing), 1, 8);
                        for (int sub = 1; sub <= steps; sub++)
                        {
                            float u = (float)sub / steps;
                            PushRibbonPoint(rb, Vector3.Lerp(last, anchorPos, u), now, visible, flowDepth,
                                            Vector3.Slerp(lastIn, headIn, u));
                        }
                    }
                }
                // See ribbonSmoothWindow. Translation-invariant, so it commutes with
                // OnFloatingOriginShift. The inward direction gets the same pass.
                if (ribbonSmoothAmount > 0f && rb.count >= 3)
                {
                    int w = Mathf.Min(ribbonSmoothWindow, rb.count - 2);
                    for (int j = 1; j <= w; j++)
                    {
                        rb.pts[j] = Vector3.Lerp(rb.pts[j],
                                                 (rb.pts[j - 1] + rb.pts[j + 1]) * 0.5f,
                                                 ribbonSmoothAmount);
                        Vector3 d = Vector3.Lerp(rb.inDir[j],
                                                 (rb.inDir[j - 1] + rb.inDir[j + 1]) * 0.5f,
                                                 ribbonSmoothAmount);
                        if (d.sqrMagnitude > 1e-6f) rb.inDir[j] = d.normalized;
                    }
                }

                BuildRibbonMesh(rb, now, rScale * Mathf.Lerp(1f, contrailWidthScale, contrailBlend), contrailBlend, life,
                                Mathf.Clamp01(strengths[i]));
            }

            // Width and colour apply while the trail ages out, even after emission stops (see
            // ApplyShedAppearance).
            if (shouldUseTrail || tr.positionCount > 0)
            {
                float sizeNormT = Mathf.Clamp(vesselSize / 30f, 0.2f, 1f);
                float ss = 1f + Mathf.Pow(sizeNormT, 2.2f) * 2.0f;
                float altScale = Mathf.Lerp(1f, contrailWidthScale, contrailBlend);
                ApplyShedAppearance(i, tr, trailFade * ss * altScale, contrailBlend, speed);
            }
            // The oldest point ages until tr.time. The newest is pinned at age 0 only while
            // shedding. A fully aged-out trail resets its recorded age span, or the history would
            // be mapped across a new stub.
            if (tr.positionCount == 0) { trailAges[i] = 0f; trailHeadAges[i] = 0f; }

            trailAges[i] = Mathf.Min(trailAges[i] + dt, Mathf.Max(tr.time, 0.01f));
            trailHeadAges[i] = shouldUseTrail ? 0f : Mathf.Min(trailHeadAges[i] + dt, Mathf.Max(tr.time, 0.01f));

            // ── LINE ──────────────────────────────────────────────────
            if (shouldUseLine)
            {
                lr.enabled = true;
                // Only once the trail has finished fading, not when line mode becomes eligible:
                // both are live during the cross-fade.
                if (trailWasActive[i] && !shouldUseTrail)
                {
                    tr.Clear(); tr.emitting = false; tr.enabled = false; trailAges[i] = 0f; trailHeadAges[i] = 0f;
                    history.Clear(); lr.positionCount = historyLength;
                    trailWasActive[i] = false;
                }

                float altFactor = contrailBlend;
                int dynLen = Mathf.RoundToInt(Mathf.Lerp(historyLength, historyLength * 2.5f, altFactor));
                if (lr.positionCount != dynLen) lr.positionCount = dynLen;

                Vector3 perp = Vector3.Cross(flow, worldUp).normalized;
                float sideSign = Mathf.Sign(vessel.transform.InverseTransformPoint(anchor.position).x);
                float cruiseFactor = contrailBlend;
                float stressFactor = Mathf.Clamp01((g - 4f) / 6f + (speed - 200f) / 200f);
                float lengthScale = Mathf.Lerp(1.0f, 4.0f, Mathf.Clamp01(speed / 300f)) * Mathf.Lerp(1f, 0.4f, stressFactor);

                for (int idx = 0; idx < dynLen; idx++)
                {
                    float falloff = dynLen > 1 ? (float)idx / (dynLen - 1) : 0f;
                    float spacing = Mathf.Min(Mathf.Pow(idx, 1.05f), idx * 1.2f);
                    Vector3 offset = -flow * (spacing * 1.5f * lengthScale);
                    Vector3 downwash = -worldUp * (idx * 0.02f * 0.2f * visible * falloff);
                    Vector3 curved = perp * (0.3f * visible * idx * 0.15f * falloff * sideSign);
                    float wAmp = Mathf.Lerp(0.2f, 1.2f, Mathf.Clamp01(speed / 250f)) * visible * Mathf.Lerp(1f, 0.3f, stressFactor);
                    Vector3 warp = perp * (Mathf.Sin(idx * 0.4f + Time.time * Mathf.Lerp(0.5f, 1.5f, cruiseFactor)) * wAmp * falloff);
                    Vector3 flowR = Vector3.Cross(worldUp, flow).normalized;
                    Vector3 drift = flowR * (Mathf.Sin(Time.time * 0.6f + idx * 0.25f) * 0.15f * visible * idx * falloff);

                    // Helical vortex core with Perlin jitter, damped by stressFactor so deformation
                    // calms when the geometry is least stable.
                    float seed = (i + 1) * 13.37f + idx * 7.91f;
                    float ampJitter = 0.75f + 0.5f * Mathf.PerlinNoise(seed, Time.time * 0.2f);
                    float freqJitter = 0.85f + 0.3f * Mathf.PerlinNoise(seed * 0.5f, Time.time * 0.15f);
                    float helixRadius = Mathf.Lerp(0.05f, 0.25f, cruiseFactor) * visible * ampJitter
                                        * Mathf.Lerp(1f, 0.3f, stressFactor);
                    float helixAngle = Time.time * Mathf.Lerp(1.0f, 2.5f, cruiseFactor) * freqJitter
                                       + idx * 0.6f + seed * 0.37f;
                    Vector3 helix = (perp * Mathf.Sin(helixAngle)
                                     + worldUp * (Mathf.Cos(helixAngle) * 0.6f))
                                    * (helixRadius * falloff);

                    lr.SetPosition(idx, anchor.position + offset + curved + warp + downwash + drift + helix);
                }

                // No upper bound: above the band this holds at its widest and the density fade
                // takes it out.
                float lineScale = (contrailBlend > 0f) ? Mathf.Lerp(1.5f, 3f, contrailBlend) : 1f;
                float sizeNorm = Mathf.Clamp(vesselSize / 30f, 0.2f, 1f);
                // Prevent excessive scaling during high-speed reentry.
                float speedFactor = Mathf.Clamp01(speed / 600f); // normalize high-speed regime
                float reentryClamp = Mathf.Lerp(1f, 0.5f, speedFactor); // shrink at extreme speeds

                float finalScale = lineScale
                                   * Mathf.Lerp(0.6f, 1.2f, sizeNorm)
                                   * (1f + Mathf.Pow(sizeNorm, 2.2f) * 2.0f)
                                   * reentryClamp;

                lr.startWidth = 0.15f * visible * lineFade * strengths[i] * finalScale;
                lr.endWidth = 0.03f * visible * lineFade * strengths[i] * finalScale;

                // Reused gradient: this runs per line-mode source every frame, and the assignment
                // copies the data.
                Gradient grad = lineGradient;

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
            else if (lr.enabled)
            {
                // The line fades out (widths driven to zero) instead of switching off outright; it
                // is disabled only when nothing is left to see.
                lr.startWidth = Mathf.Lerp(lr.startWidth, 0f, shutdownK);
                lr.endWidth = Mathf.Lerp(lr.endWidth, 0f, shutdownK);
                if (lr.startWidth < 0.002f) { lr.enabled = false; lineWasActive[i] = false; }
            }
            else lineWasActive[i] = false;
        }
    }

    // Bakes the recorded shedding strength along the trail instead of one global width and alpha,
    // so a stretch shed under 7 g keeps its strength after the pilot unloads. TrailRenderer's
    // startWidth and endWidth are global, so widthCurve and colorGradient (0 at the head, 1 at the
    // oldest point) are driven from a ring buffer of past intensity. Position maps to age, which
    // coincide at constant speed.
    // Reads the ring buffer `back` samples behind the write cursor.
    float SampleShed(float[] hist, int back)
    {
        int idx = ((shedWrite - back) % shedSamples + shedSamples) % shedSamples;
        return Mathf.Max(hist[idx], 0f);
    }

    // Shed strength at normalised position u, interpolated between history buckets.
    float ShedAt(float[] hist, float u, float headAge, float tailAge)
    {
        float age = Mathf.Lerp(headAge, tailAge, u);
        float fpos = Mathf.Max((age - shedSampleTimer) / shedSampleInterval, 0f);
        int b0 = Mathf.Clamp(Mathf.FloorToInt(fpos), 0, shedSamples - 1);
        int b1 = Mathf.Min(b0 + 1, shedSamples - 1);
        return Mathf.Lerp(SampleShed(hist, b0), SampleShed(hist, b1), Mathf.Clamp01(fpos - b0));
    }

    void ApplyShedAppearance(int i, TrailRenderer tr, float widthScale, float contrailBlend, float speedMps)
    {
        float[] hist = shedHistory[i];

        // The age span the current geometry covers, which is not [0, tr.time]: just after a Clear
        // the trail holds a fraction of a second, and once emission stops the head ages too, so the
        // strong stretch slides toward the tail and expires.
        float maxHist = shedSamples * shedSampleInterval;
        float headAge = Mathf.Clamp(trailHeadAges[i], 0f, maxHist);
        float tailAge = Mathf.Clamp(Mathf.Min(trailAges[i], tr.time), 0.05f, maxHist);
        if (tailAge < headAge) tailAge = headAge;

        float tailRatio = Mathf.Lerp(0.25f, 1.5f, contrailBlend);   // >1: contrails SPREAD with age
        // Fade exponent: a contrail holds opacity longer than a manoeuvre puff, over a range that
        // reads as a gradient.
        float fadePow = Mathf.Lerp(1.0f, 1.5f, contrailBlend);

        // Near-ground diffusion at the vessel's height, not per key; the trail path is rockets and
        // secondaries.
        float groundRateMult = 1f + groundTurbScale * frameGroundFac;

        // How much of the curve the roll-up distance covers: the trail's live length is speed times
        // the age span.
        float rollupMetres = Mathf.Clamp(vesselSize * rollupSizeScale, rollupMinMetres, rollupMaxMetres);
        float trailMetres = Mathf.Max(speedMps * Mathf.Max(tailAge - headAge, 0.01f), 0.01f);
        float rampFrac = Mathf.Clamp(rollupMetres / trailMetres, 0.0002f, 0.5f);

        // Width: keys placed explicitly, most inside the ramp; a power distribution would leave the
        // taper between samples.
        int headKeys = Mathf.Clamp(headKeyCount, 2, widthKeyCount - 2);
        for (int k = 0; k < widthKeyCount; k++)
        {
            float u;
            if (k < headKeys)
                u = rampFrac * ((float)k / (headKeys - 1));
            else
                u = Mathf.Lerp(rampFrac, 1f, (float)(k - headKeys + 1) / (widthKeyCount - headKeys));

            float shed = ShedAt(hist, u, headAge, tailAge);

            // Converge toward the anchor instead of starting at full width, stopping short of
            // zero so the origin stays visible.
            float ramp = Mathf.Clamp01(u / rampFrac);
            ramp = ramp * ramp * (3f - 2f * ramp);

            // Same split as the tube: tailRatio is how much is still condensed, CoreGrowth the core
            // diffusing; u maps to real age.
            widthKeys[k] = new Keyframe(u, shed
                                           * Mathf.Lerp(1f, tailRatio, u)
                                           * ContrailSpread(u, contrailBlend)
                                           * CoreGrowth(Mathf.Lerp(headAge, tailAge, u), groundRateMult)
                                           * Mathf.Lerp(headWidthFraction, 1f, ramp));
        }

        // Alpha, 8 keys (Gradient's limit); same peak-window-then-taper shape as the tube, sharing
        // ribbonPeakFraction.
        for (int k = 0; k < shedKeyCount; k++)
        {
            float u = (float)k / (shedKeyCount - 1);
            float shed = ShedAt(hist, u, headAge, tailAge);
            float fadeT = Mathf.Clamp01((u - ribbonPeakFraction)
                                        / Mathf.Max(1f - ribbonPeakFraction, 0.01f));
            // Paired with the CoreGrowth applied to the width keys above, for the same reason.
            float dim = CoreDim(CoreGrowth(Mathf.Lerp(headAge, tailAge, u), groundRateMult))
                        * Mathf.Pow(ContrailSpread(u, contrailBlend), -contrailSpreadDimPow);
            shedAlphaKeys[k] = new GradientAlphaKey(
                Mathf.Clamp01(shed * trailAlphaGain * (1f - Mathf.Pow(fadeT, fadePow)) * dim), u);
        }

        shedCurve.keys = widthKeys;
        tr.widthCurve = shedCurve;
        tr.widthMultiplier = 0.2f * widthScale;

        if (tr.numCapVertices != trailCapVertices) tr.numCapVertices = trailCapVertices;

        // One-shot diagnostic of the width profile, on an established, visible trail.
        if (!loggedWidthProfile && i == 0 && tr.positionCount > 40 && widthKeys[widthKeyCount - 1].value > 0.05f)
        {
            loggedWidthProfile = true;
            var wc = tr.widthCurve;
            Log($"width profile keys={wc.length} mult={tr.widthMultiplier:F3} " +
                      $"caps={tr.numCapVertices} points={tr.positionCount} " +
                      $"rollup={rollupMetres:F1}m trail={trailMetres:F0}m rampFrac={rampFrac:F5} | " +
                      $"u0={wc.Evaluate(0f):F3} uRamp={wc.Evaluate(rampFrac):F3} " +
                      $"u.5={wc.Evaluate(0.5f):F3} u1={wc.Evaluate(1f):F3}");
        }

        shedGradient.SetKeys(shedColorKeys, shedAlphaKeys);
        tr.colorGradient = shedGradient;
    }

    void ResetVortexState(int i)
    {
        trails[i].Clear(); trails[i].time = 1.5f; trails[i].emitting = false;
        trailAges[i] = 0f; trailHeadAges[i] = 0f;
        lines[i].positionCount = historyLength; lines[i].enabled = false;
        lineHistory[i].Clear();
        trailWasActive[i] = false; lineWasActive[i] = false;
    }

    // A lifting surface that qualified as a possible vortex source.
    private class AeroCandidate
    {
        public Part part;
        public Bounds bounds;
        public bool isLeft;
        public float extremity;   // furthest lateral reach from the vessel centreline
        public float z;           // longitudinal position of the bounds centre
        public float zLo, zHi;    // longitudinal extent

        // Roll-axis-relative: radialDist is the 2D magnitude perpendicular to the length axis and
        // theta its angle in degrees; not the same as extremity, so radialDist >= |extremity|.
        public float radialDist;
        public float theta;

        public AeroCandidate(Part p, Bounds b, bool left, float ext, float zc, float halfZ,
                             float rDist, float th)
        {
            part = p; bounds = b; isLeft = left; extremity = ext;
            z = zc; zLo = zc - halfZ; zHi = zc + halfZ;
            radialDist = rDist; theta = th;
        }
    }
}

// Gives each craft worth drawing its own WingtipVortex controller: the craft you fly, plus other
// loaded, unpacked craft with lifting surfaces, nearest first, up to MaxControllers. Wing vapor
// stays on the active craft only (WingVaporAddon), since it runs a per-part aerodynamic solve every
// physics step.
[KSPAddon(KSPAddon.Startup.Flight, false)]
public class WingtipVortexManager : MonoBehaviour
{
    // Including the active craft; bounds the cost of tube rebuilds in a big BDArmory match.
    const int MaxControllers = 10;
    const float ScanInterval = 0.5f;   // s; craft come and go on a timescale of seconds

    readonly Dictionary<Vessel, WingtipVortex> controllers = new Dictionary<Vessel, WingtipVortex>();
    readonly List<Vessel> wanted = new List<Vessel>();
    readonly List<Vessel> drop = new List<Vessel>();
    float nextScan = 0f;

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + ScanInterval;
        if (!FlightGlobals.ready) return;

        Vessel active = FlightGlobals.ActiveVessel;
        wanted.Clear();
        // Not a fired missile, though: BDArmory makes one the active vessel to follow it.
        if (active != null && active.loaded && !VortexVapor.BDArmoryCraft.IsMissile(active)) wanted.Add(active);

        List<Vessel> loaded = FlightGlobals.VesselsLoaded;
        if (loaded != null)
        {
            int start = wanted.Count;
            for (int i = 0; i < loaded.Count; i++)
            {
                Vessel v = loaded[i];
                if (v != null && v != active && IsAircraft(v)) wanted.Add(v);
            }
            // Nearest first, measured from the craft you are flying (or the camera's craft).
            if (active != null && wanted.Count - start > 1)
            {
                Vector3 from = active.transform.position;
                wanted.Sort(start, wanted.Count - start, Comparer<Vessel>.Create((a, b) =>
                    (a.transform.position - from).sqrMagnitude.CompareTo((b.transform.position - from).sqrMagnitude)));
            }
            if (wanted.Count > MaxControllers) wanted.RemoveRange(MaxControllers, wanted.Count - MaxControllers);
        }

        drop.Clear();
        foreach (var kv in controllers)
            if (kv.Key == null || kv.Value == null || !wanted.Contains(kv.Key)) drop.Add(kv.Key);
        foreach (Vessel v in drop)
        {
            WingtipVortex c;
            if (controllers.TryGetValue(v, out c) && c != null) Destroy(c.gameObject);
            controllers.Remove(v);
        }

        foreach (Vessel v in wanted)
        {
            if (controllers.ContainsKey(v)) continue;
            var go = new GameObject("WingtipVortex " + v.vesselName);
            var c = go.AddComponent<WingtipVortex>();
            c.target = v;   // before Start(), which runs next frame
            controllers[v] = c;
        }
    }

    // A craft that can shed a wake and is being simulated. Unpacked only: a packed craft's parts
    // are not where the tip search needs them (see sourceReadyTimeout).
    static bool IsAircraft(Vessel v)
    {
        if (!v.loaded || v.packed || v.parts == null || v.parts.Count == 0) return false;
        switch (v.vesselType)
        {
            case VesselType.Debris:
            case VesselType.EVA:
            case VesselType.Flag:
            case VesselType.SpaceObject:
            case VesselType.Unknown:
            case VesselType.DeployedScienceController:
            case VesselType.DeployedSciencePart:
            case VesselType.DroppedPart:
                return false;
        }
        for (int i = 0; i < v.parts.Count; i++)
        {
            Part p = v.parts[i];
            if (p != null && p.Modules != null && WingtipVortex.IsAeroSurface(p))
                return !VortexVapor.BDArmoryCraft.IsMissile(v);   // a modular missile built on stock fins
        }
        return false;
    }

    void OnDestroy()
    {
        foreach (var kv in controllers)
            if (kv.Value != null) Destroy(kv.Value.gameObject);
        controllers.Clear();
    }
}
