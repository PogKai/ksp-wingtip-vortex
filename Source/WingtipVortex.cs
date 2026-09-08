using UnityEngine;
using KSP;
using System.Collections;
using System.Collections.Generic;

[KSPAddon(KSPAddon.Startup.Flight, false)]
public class WingtipVortex : MonoBehaviour
{
    public const string ModVersion = "1.0.1";

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
    // Each source's lateral reach as a fraction of the main wing's. Drives how much load a
    // secondary surface must carry before its vortex shows — see the gate in Update().
    // (This slot used to hold a per-source area that nothing ever read.)
    private List<float> spanRatios = new List<float>();

    // ── WAKE TUBE (procedural mesh) ─────────────────────────────────────────────────────
    // Generated geometry instead of a TrailRenderer, because a TrailRenderer billboards to the
    // camera and therefore has no orientation to twist — it can snake, but it can never read
    // as a rotating rope. Owning the mesh is the only way to get a cross-section that rotates
    // about the centreline.
    //
    // A TUBE rather than a single twisted ribbon on purpose: a lone ribbon goes edge-on twice per
    // revolution and visually disappears, which reads as a beaded, flickering rope.
    private bool enableRibbonMesh = true;
    // Was the A/B: source 0 got a tube, the rest kept TrailRenderers, so the two could be
    // compared on one aircraft. Now off. Set true to get the side-by-side back.
    private bool ribbonFirstSourceOnly = false;

    // MAIN WING ONLY. Secondary surfaces keep their TrailRenderer, and not just to save frames:
    // the inward curve models a free counter-rotating PAIR converging under its own induced flow,
    // and a canard's vortex does not get to do that. It passes straight over the main wing and is
    // entrained into the wing's own vortex system, so drawing it as an independent, cleanly
    // converging rope actively misrepresents it. The amplitude is scaled by shed strength but not
    // by span either, so 1.2 m of convergence off a 0.6 m-extremity winglet would be absurd.
    private bool ribbonMainOnly = true;

    // Hard cap on how long the rope is allowed to get, in METRES. Line mode used to be what
    // stopped a re-entry wake from running away, and the tube no longer hands over to it, so the
    // tube needs its own limit. A time-based lifetime alone does not give one: at 2 km/s a 5 s
    // rope is 10 km long.
    //
    // Expressed as length rather than as a speed-dependent lifetime fudge because length is the
    // thing that actually matters visually. Below ~500 m/s the lifetime binds first and nothing
    // changes; above it the rope simply stops getting longer.
    private float ribbonMaxLength = 2500f;
    private int ribbonSides = 5;                // cross-section vertices per ring
    // 200 was not enough at altitude: contrail lifetime runs to 5 s, and a ring is committed
    // once per frame, so at 200 m/s and 60 fps the buffer held 660 m while the lifetime wanted
    // 1000 m. The buffer bound instead of the lifetime and the rope was cut short.
    private int ribbonMaxPoints = 320;          // ring budget; buffers are sized to this once
    private float ribbonMinPointDist = 0.8f;    // metres of travel before a new ring is laid
    private float ribbonRadiusScale = 0.15f;    // metres of radius per unit shed strength
    private float ribbonTwistPerMetre = 0.25f;  // radians of cross-section rotation per metre

    // The cross-section has to be ASYMMETRIC or the twist has nothing to display against.
    // 0.6.24 rotated a regular polygon at constant radius about its own centre, which maps it
    // onto itself — the twist moved every vertex and changed the silhouette, the shading and
    // the outline not at all. Mechanism correct, no visible signal; the same class of mistake as
    // the helix wavelength.
    //
    // ribbonAspect flattens the section into an ellipse, so rotating it visibly ROLLS the rope,
    // and unlike a flat ribbon the minor axis never reaches zero so there are no edge-on dropouts.
    // ribbonStripe brightens one side of the ring; under additive blending that reads as a bright
    // seam winding along the rope, which is close to what real vortex vapour looks like.
    private float ribbonAspect = 0.4f;          // minor axis as a fraction of major
    private float ribbonStripe = 0.6f;          // 0 = uniform, 1 = one side fully dark

    // CENTRELINE HELIX. The measured reason nothing looked like it was spiralling: the tube is
    // ~0.3 m across on a ~40 m wake, so from a chase camera its cross-section is one or two pixels
    // wide. Rotating a one-pixel-wide cross-section is invisible no matter how elliptical or how
    // striped it is — which is why ribbonAspect and ribbonStripe changed nothing, and why the
    // twist looked identical to no twist.
    //
    // What is visible at that viewing distance is the ROPE ITSELF moving, by metres. So the helix
    // goes on the centreline, not the cross-section. That failed against a TrailRenderer because
    // it needed per-vertex age inferred from an index and accumulated per-frame deltas whose
    // phase drifted; here the arc length of every ring is exact and the whole curve is rebuilt
    // from scratch each frame, so there is nothing to drift.
    // Spiral OFF. Real wingtip ropes are close to straight — what the reference photos show
    // is two nearly parallel lines that converge slightly and then run flat, not a corkscrew.
    // The machinery stays, gated on this being > 0, in case it is ever wanted again.
    private float ribbonHelixAmp = 0f;          // metres of centreline displacement, fully open

    // INWARD CURVE. The rope bends toward the aircraft centreline over the first
    // ribbonInwardGrow metres and then runs parallel — the "flow inwards slightly before
    // evening out" shape. This is also what really happens: the core rolls up inboard of the
    // geometric tip, and the counter-rotating pair induces a slight mutual convergence.
    //
    // The direction is frozen in world space at birth, exactly like the helix phase was, so shed
    // geometry cannot be re-aimed by the aircraft manoeuvring afterwards.
    private float ribbonInwardAmp = 1.2f;       // metres of convergence, once evened out
    private float ribbonInwardGrow = 20f;       // metres of wake over which it bends in
    private float ribbonHelixWavelength = 50f;  // metres per turn
    // 25 m left the leading 25 m of rope near-straight and stuck to the wingtip before the
    // spiral opened — that flat leading section was the "follows the wingtip" part. 8 m keeps
    // the anchor weld without a long dead run in front of it.
    private float ribbonHelixGrow = 8f;         // metres of wake before it reaches full amplitude
    private bool loggedRibbon = false;
    private float ribbonFadePow = 2.0f;

    // Fraction of the rope over which the tail closes to nothing. Keyed to POSITION IN THE
    // BUFFER, not to age, and that distinction is the fix: once the ring budget is what limits
    // the rope (rather than lifetime), the oldest ring's age never reaches `life`, so an
    // age-based fade never reaches zero and the rope ends on a blunt, still-visible face. A new
    // ring pushes the oldest out every frame, so that blunt end jumps forward — which is what
    // read as the end being dragged along behind the aircraft. Position-in-buffer always closes,
    // whichever constraint happens to bind.
    private float ribbonEndFade = 0.25f;

    // LONGITUDINAL FLOW STRUCTURE, and the reason the rope reads as towed rather than streaming
    // once the contrail band is reached. Down low, `visible` tracks G, so brightness varies along
    // the rope and that variation sits still in the air while the aircraft pulls away from it
    // — which is the whole cue that says "this is being left behind". Up high the altitude
    // floor pins `visible` near-constant, every ring comes out identical, and a uniform straight
    // line translating along its own axis has no visible motion at all. Nothing was actually
    // dragging; there was simply nothing on the rope to see moving.
    //
    // Sampled against the birth odometer, so the pattern is frozen in the wake exactly like the
    // helix phase and the inward direction, and streams backward for free. Scaled by
    // contrailBlend so the low-altitude look, which already has G-driven variation, is untouched.
    private float ribbonFlowDepth = 0.45f;      // brightness swing, at full contrail blend
    private float ribbonFlowScale = 0.03f;      // cycles per metre (~33 m per Perlin unit)
    private List<WakeRibbon> ribbons = new List<WakeRibbon>();

    // Unit-circle table for the cross-section, built once. Every ring needs cos/sin of
    // (twist + 2*pi*k/K); with the table only cos/sin of the twist itself are computed and the
    // per-vertex values come from an angle addition. That is 2 transcendentals per ring instead
    // of 2*K, which matters now that four tubes rebuild every frame rather than one.
    private float[] ringCos, ringSin;

    private List<Queue<Vector3>> lineHistory = new List<Queue<Vector3>>();
    private int historyLength = 8;

    private List<bool> trailWasActive = new List<bool>();
    private List<bool> lineWasActive = new List<bool>();
    private List<Vector3> lastFlows = new List<Vector3>();
    private List<Vector3> lastAnchorPositions = new List<Vector3>();

    private bool useLineMode = false;
    private float currentIntensity = 0f;

    // Both fast, deliberately. Circulation is set by the lift the wing is making RIGHT NOW, and
    // collapses with it — the research is explicit that the newly shed vortex weakens the
    // instant the pilot unloads, the same way it stops when spoilers dump lift on landing. These
    // two numbers govern only what is being shed at the wing this frame.
    //
    // 0.6.4 slowed decay to 0.22 to try to make the wake persist. That was the wrong lever: it
    // persisted by refusing to let the INSTANTANEOUS strength fall, which smeared the collapse
    // across the whole trail instead. Persistence is now a property of the recorded trail
    // history (see ApplyShedAppearance), which is where it physically belongs, so instantaneous
    // response goes back to tracking lift closely.
    private float buildSpeed = 5f;
    private float decaySpeed = 4f;
    private float lineActivationBlend = 0f;
    private float lineActivationSpeed = 0.6f;

    private float maxDelta = 0.05f;
    private float abnormalFrameThreshold = 0.20f;

    // HIGH-ALTITUDE CONDENSATION REGIME.
    // The trail module now owns the 8-15 km band that line mode used to. It could not before:
    // above Krakensbane's altitude gate the recorded trail vertices were drifting metres per
    // physics frame (see OnFloatingOriginShift), so the band was handed to a renderer that
    // rebuilds from scratch every frame and therefore could not drift. With the frame fix in
    // place the trail is the better tool up there — a condensation trail IS accumulated
    // world-space geometry, which is what a TrailRenderer stores and what a LineRenderer only
    // imitates procedurally.
    // BODY-RELATIVE, not altitude. 8000-15000 m only ever meant anything on Kerbin: altitude is
    // a property of the craft, and says nothing about whether there is air outside it. What
    // actually decides whether a persistent contrail forms is that the ambient air is already
    // near saturation, i.e. cold enough — which is why the effect being G-gated down low and
    // essentially default-on up high is correct, and it is TEMPERATURE, not height, that draws
    // that line.
    //
    // Keying off vessel.atmosphericTemperature makes it fall out per planet with no per-body
    // tuning: Eve is hot and deep, so its band sits very high; Duna is cold and thin, so the
    // band is low but the density gate keeps it faint; Laythe lands in between.
    // The anchors are the real atmospheric-science numbers rather than tuned ones: persistent
    // contrails need the air at or near ice saturation, which on Earth means roughly -40 C
    // (233 K, where homogeneous ice nucleation is certain) at the cold end and around -20 C
    // (253 K) as the warm end below which it essentially never happens. Absolute Kelvin is the
    // right unit here — water does not care which planet it is on.
    //
    // 220 K was below the coldest air any KSP body actually has, which meant contrailBlend could
    // never reach 1 anywhere: the fully-developed contrail look was unreachable by construction.
    // RefreshContrailBand samples the body's own temperature curve and lifts the cold anchor if
    // needed, so full development is always reachable somewhere in that body's atmosphere while
    // never being handed out warmer than the physics allows.
    private float contrailTempWarm = 253f;   // K: above this, no persistent contrail
    private float contrailTempCold = 233f;   // K: at or below this, fully developed
    private float contrailBandMin = 12f;     // K: never let the band collapse to a step

    // ILLUMINATION. Condensation has nothing luminous about it: a contrail is visible only
    // because it SCATTERS SUNLIGHT. So the light term is not decoration, it is the whole reason
    // the effect is visible at all, and three things were wrong with how it was applied.
    //
    // 1. The contrail floor bypassed it. The floor is applied with Max(), so high-altitude wakes
    //    stayed at full brightness on the night side while every other path correctly went dim.
    //
    // 2. It was never normalised. Dividing solar flux by a hard-coded 2000 is wrong twice over:
    //    Kerbin's daylight flux is about 1360, so full noon only ever reached 0.68 and the effect
    //    never reached its own top end anywhere — and the same constant pins Duna (~600) at
    //    0.30 for no physical reason while Eve (~2500) clamps flat. Normalising against the
    //    UNOCCLUDED flux at the vessel's own distance from the star makes 1.0 mean "full
    //    daylight" on every body, which is what body-relative means here.
    //
    // 3. The floor is not really about physics. The wake material is additive, so it ADDS light
    //    to whatever is behind it — the same alpha reads far brighter against a black sky than
    //    against a bright one, and a wake tuned to look right at noon looks like a neon tube at
    //    midnight. That is the real reason night needs its own term, and why the floor is not
    //    zero either: planetshine and starlight are not nothing, and a wake that vanished
    //    outright would read as a bug.
    //
    // vessel.solarFlux already accounts for occlusion by the body, so this keeps the good case
    // for free: a craft at altitude still sees the sun after the ground below it has gone dark,
    // which is exactly when real contrails are at their most striking.
    private float nightFloorScale = 0.20f;   // ambient floor in full shadow
    private float dayLightScale = 0.75f;     // brightness in full daylight; 1.0 for no dimming
    private bool loggedSunFlux = false;
    private CelestialBody contrailBody = null;
    private float bodyContrailWarm = 253f, bodyContrailCold = 233f;
    private float contrailTimeMin = 2.5f;          // trail lifetime at the bottom of the band
    private float contrailTimeMax = 5.0f;          // ...and at the top
    private float contrailMinVertexDist = 1.5f;    // contrails are near-straight; sample coarsely
    private float contrailWidthScale = 2.2f;

    // Line mode is now only the extreme-velocity regime, where its reentry width clamp and
    // stress-damped procedural terms still earn their place. Was 250 m/s, which dragged ordinary
    // jet cruise into line mode and left the trail module unused exactly where contrails belong.
    private float lineModeSpeed = 600f;

    // Restored from 0.4.0 (dropped in 0.5.0): a wingtip vortex core is helical, so the trail
    // emitter traces a spiral rather than a straight line, with Perlin jitter so the two sides
    // never look mechanically identical. Set helixRadiusMax to 0f to disable.
    //
    // The helix belongs to the WAKE, not to the emitter. Up to 0.6.14 it displaced the emitter,
    // which meant the trail's origin orbited the wingtip — the trail was never actually welded
    // to the anchor. It is now applied to stored vertices with a radius that grows from zero with
    // each vertex's own age, so the origin sits exactly on the anchor and the spiral only opens
    // out downstream, which is also what a real core does: it is pinned at the tip and the
    // helical structure develops behind it.
    //
    // Driven by AoA AND speed rather than speed alone. AoA gates it (no incidence, no vortex to
    // wind up) and speed only modulates, so a fast pass at zero AoA stays straight.
    // Winding is a WAVELENGTH IN METRES, not a rate in rad/s. That parameterisation is the whole
    // reason 0.6.15 showed no swirl at all: at 1.8 rad/s and 200 m/s one turn spanned
    // 2*pi*v/w = ~700 m, so the helix was a 0.1 m deviation over a 700 m period — a ratio of
    // 1:7000, which is a straight line. A corkscrew only reads as one at roughly 1:20 to 1:60,
    // so both numbers had to move: metres-based period, and an amplitude to match it.
    // 0f — the wake helix is OFF. Reverted after the spiral chase (0.6.14-0.6.17) and the
    // particle-cloud attempt (0.6.18) both failed: a camera-billboarded ribbon cannot read as a
    // rotating tube however it is parameterised, and the particle route broke outright. The
    // machinery below is left intact but inert, gated by this one value, so nothing else in the
    // file had to be unpicked to switch it off. Shape work from 0.6.13 (metre-based roll-up
    // taper) is retained — that part worked.
    private float helixRadiusMax = 0f;       // metres, fully developed
    private float helixWavelength = 25f;     // metres per full turn (1:17 at full amplitude)
    private float helixGrowTime = 0.8f;      // seconds downstream to reach full radius

    // Driven by G, not by an angle-of-attack proxy. KSP exposes no AoA field, so the previous
    // driver measured the angle between the vessel's long axis and the velocity vector — which
    // is only AoA if the long-axis pick is correct, and on the test craft that pick is nearly a
    // coin flip (length 15.7 m vs height 14.2 m). Worse, in ordinary flight AoA is 2-4 degrees, so
    // the drive sat near 0.14 and scaled the amplitude down to ~0.1 m: invisible regardless of how
    // the wavelength was set. Load factor comes from summed lift and needs no geometry guess.
    private float helixBaseDrive = 0.35f;    // drive at or below helixGMin, so it never vanishes
    private float helixGMin = 1f;
    private float helixGFull = 5f;
    private bool loggedHelixCheck = false;

    // Half-width of the band at the wing tip inside which the forward-most vertex wins.
    private const float tipBand = 0.15f;

    // SECONDARY-SOURCE GATE. Canards, foreplanes and any other non-main surface have to earn
    // their vortex: BOTH thresholds must be met, and both scale in together. Replaces a gate that
    // took the MAX of a G term and (220 - speed)/40 — the second of which rose as speed FELL,
    // so above 8 km at 180 m/s or less it evaluated to 1.0 and the G term was ignored entirely.
    // That is why they hung around through mild manoeuvres: they were not gated on load at all in
    // that regime, and were actually easier to trigger the slower you flew.
    // The onset scales with how big the surface is relative to the main wing, because a flat
    // threshold is wrong for one of the two things that land in this slot:
    //
    //   A PRIMARY canard (Eurofighter, Rafale, Gripen) sits in clean air ahead of the wing, and
    //   canard pitch stability REQUIRES the foreplane to stall first — so it is deliberately
    //   flown at a HIGHER lift coefficient than the main wing, not a lower one. Its vapour appears
    //   alongside the wing's, not several g later. Demanding +2g of it would be wrong.
    //
    //   A small NOSE canard carries a few percent of total lift over a short span. Circulation
    //   goes as L/(rho*V*b), so far less lift means far less circulation, a far weaker core
    //   pressure drop, and genuinely more load needed before anything condenses.
    //
    // Span ratio separates them, and it is already known at selection time.
    private float secondaryGMinSmall = 6.0f;   // onset for a small nose canard
    private float secondaryGMinLarge = 3.5f;   // onset for a full-size canard (main wing is 3g)
    private float secondaryGSpan = 2.0f;       // load range from onset to full strength
    // Airspeed floor. This is an instrument-noise guard, NOT a physical circulation term — at
    // a walking pace liftForce is noise and the span-ratio logic has nothing meaningful to chew
    // on. It used to sit at 120/200 m/s, which silently vetoed every canard on a landing approach
    // no matter how hard the foreplane was working, and would have gone on doing so even after
    // the onset rescaling below. Dropped to a genuine noise floor; the load gate, which now
    // scales with rho*V, is what actually does the gating.
    private float secondarySpeedMin = 45f;
    private float secondarySpeedFull = 90f;

    // TIP LIKENESS. "Main wing" and "secondary surface" are SLOTS in the selection pass, not
    // aerodynamic facts, and treating the slot as the fact left a cliff: the widest surface on
    // each side got full strength and the 3 g onset, and a surface 1 cm narrower got 40% strength
    // and a 3.5-6 g onset. Nothing physical happens at that boundary.
    //
    // What actually matters is whether the surface reaches the outboard end of the span. If a
    // canard is canted or long enough that its tip is the widest point of the airframe, then that
    // tip IS the wingtip — it is the last place the pressure difference between upper and lower
    // surface can escape around, which is the entire mechanism, and the fact that it happens to
    // sit ahead of the wing changes nothing about it. The selection pass already lands the MAIN
    // slot there in that case, because it sorts on lateral reach; this closes the cliff on either
    // side of it, so a canard reaching 95% of the wing is treated as a wingtip too, and a wing
    // demoted to the secondary slot by an even wider canard gets its own treatment back.
    //
    // A small nose canard is untouched: it reaches a third of the span, so it stays fully gated,
    // which preserves the secondary tightening from 0.6.26.
    private float mainLikeRatioMin = 0.80f;    // below this, full secondary treatment
    private float mainLikeRatioFull = 0.95f;   // at or above this, identical to a main wingtip
    private float secondaryStrengthMin = 0.4f; // strength floor for a genuinely minor surface

    // SOURCE SELECTION thresholds. See FindVortexSources.
    private float minSourceArea = 0.3f;            // rejects trim tabs and greebles
    private float minLateralOffset = 0.3f;         // rejects centre sections and vertical fins
    private float secondaryMinSeparation = 0.5f;   // metres a surface must sit clear of the wing
    private float aftSecondarySpanRatio = 0.8f;    // an aft surface must be this fraction of the
                                                   // main wing's span to count as a second wing
                                                   // rather than a stabiliser

    // MANEUVER-PUFF LIFETIME. Companion to decaySpeed above: the trail's own point lifetime has
    // to be at least as long as the decay it's supposed to show, or the history buffer runs out
    // before the fading-out intensity does and the "lingers, then fades" behaviour is invisible.
    // Was a fixed 0.25-0.7s (now the contrail band's bottom rung; see contrailTimeMin/Max).
    private float maneuverTimeMin = 1.2f;
    private float maneuverTimeMax = 3.5f;

    // CIRCULATION MODEL. Real wingtip-vortex strength is Gamma ~ (load factor * weight) /
    // (wingspan * airspeed) — the classic aircraft wake-vortex relation. G-force (via
    // currentIntensity above) already stands in for load factor; this factor supplies the three
    // terms G-force alone can't: heavier aircraft, narrower spans, and slower speeds all make a
    // physically stronger vortex at the SAME G. It's a ratio against a reference light aircraft,
    // clamped to a modest range so it nudges the existing G-based curve rather than replacing it
    // — a craft at the reference values leaves today's tuning untouched.
    // Circulation goes as (n * m) / (rho * V * b). Load factor n is the G curve below; density
    // is already handled as visibility by atmFactor; what is left is m/b, and that is ONE
    // quantity, not two knobs — real aircraft hold roughly constant wing loading, so m scales
    // with b^2 and m/b therefore scales with b. Bigger simply means stronger, which is exactly
    // why wake-turbulence categories are drawn by size.
    //
    // Speed is dropped. Not because it is redundant with G (a 900 m/s level pass is still 1g),
    // but because once the G gate is doing the work its residual effect is second order, it is
    // the term that reads backwards to a player (physics says slower is stronger), and in the
    // previous form it was inert anyway: with a hard clamp at 2.0, circFactor sat pinned at its
    // ceiling below 333 m/s for a 20t craft and below 500 m/s for a 50t one, so mass, span AND
    // speed all did precisely nothing across the normal envelope.
    //
    // Soft saturation rather than a clamp. Condensation IS a threshold effect so saturating is
    // right, but a hard cap destroys the ordering above it; 2x/(1+x) maps 0->0, 1->1, inf->2 and
    // keeps a 150t airliner ahead of a 20t fighter instead of both sitting on the cap.
    private float refMass = 8f;      // metric tons
    private float refSpan = 16f;     // metres
    private float sizeFactor = 1f;   // refreshed on the bounds timer

    // CIRCULATION ONSET. The load-factor thresholds below used to be constants — 3g for the
    // main wing, 3.5/6g for secondaries — and that is what made a landing approach render
    // nothing at all, because a stabilised approach is 1g by definition.
    //
    // But load factor is not circulation. Gamma = L/(rho*V*b), and n = L/W, so Gamma is
    // proportional to n*m/(rho*V*b): the SAME lift makes proportionally MORE circulation as
    // speed and density fall. That is the whole reason a slow, high-alpha approach vortexes
    // despite pulling no g. Run the numbers on a 20 t / 12 m fighter: a 3 g break turn at
    // 250 m/s at sea level gives Gamma ~159 m^2/s, and a 96 m/s approach at 1 g gives ~140 —
    // 88% of the break turn, from an aircraft doing nothing but flying straight.
    //
    // Rather than rescale g and break every threshold tuned against it, the thresholds themselves
    // now move: the load required for onset scales with rho*V. At the reference condition
    // (sea level, 250 m/s) the scale is exactly 1, so every number tuned so far is preserved
    // untouched. Slower or thinner air lowers the bar; faster or denser air raises it, which is
    // also correct — a 400 m/s pass is at low lift coefficient and genuinely should need more.
    // The onset SPAN scales with it too, or the curve would be stretched across a range the
    // aircraft cannot reach at approach speed.
    private float onsetLoadRef = 3.0f;        // load factor for onset at the reference condition
    private float onsetSpanRef = 10f;         // ...and the range from onset to full intensity
    private float onsetRefDensity = 1.225f;   // kg/m^3
    private float onsetRefSpeed = 250f;       // m/s
    // The floor has to keep the onset ABOVE 1 g, and that is not a taste call. rawLiftG falls
    // back to vessel.geeForce when the wings are making no lift at all, and a parked aircraft
    // reads 1 g sitting on its gear — so any floor that puts the onset below 1 g makes a
    // stationary craft on the runway sprout vortices. 0.40 puts the onset at 1.2 g, which a
    // parked or level-cruising aircraft cannot reach and a flare or any real manoeuvre can.
    private float onsetScaleMin = 0.40f;
    private float onsetScaleMax = 2.5f;

    // Second half of the same guard, and the one that does not depend on a tuned number: below
    // this there is no meaningful airflow over the wing, so whatever rawLiftG is reporting is
    // gear load or noise rather than aerodynamics.
    private float flyingSpeedMin = 20f;   // m/s, fully gated below
    private float flyingSpeedFull = 40f;  // m/s, ungated above

    // GROUND ROLL. An airspeed gate alone does not cover this, and the reason is worth writing
    // down because it is the mistake this replaces: the lift coefficient proxy L/(q*b^2) is a
    // RATIO, and on the takeoff roll its numerator and denominator both fall as V^2. It does not
    // shrink with speed at all — it just reports the gear-attitude CL, which for a low aspect
    // ratio stock wing can sit right on the threshold. So a craft accelerating down the runway
    // read as "slow and working hard" and grew vortices.
    //
    // The physical discriminator is not speed and not CL, it is WHAT IS CARRYING THE AIRCRAFT.
    // On the runway the gear does, and the wing carries only a fraction of the weight, so there
    // is no developed tip vortex to condense. Once airborne the wing carries all of it by
    // definition. KSP answers that directly, so it is gated directly.
    //
    // Deliberately NOT a ground-proximity or ground-effect test: the classic short-final vapour
    // photograph is taken deep in ground effect, so suppressing on height above terrain would
    // kill the exact case this whole feature exists to produce.
    private float airborneBlend = 0f;
    private float airborneBlendSpeed = 2.5f;   // ~0.4 s fade in at rotation, so it does not pop

    // HUMID BOUNDARY LAYER — the low-altitude mirror of the cold-air contrail floor above.
    // The onset scaling gets a landing approach to the right CIRCULATION, but circulation alone
    // undersells how photogenic approach vapour actually is, and the reason is not aerodynamic.
    // The ground and the ocean are the moisture source, so relative humidity in the bottom
    // kilometre or two runs 70-90%, and very little core pressure drop is then needed to reach
    // dew point. That is why the classic wingtip-vapour-on-short-final photograph exists at all.
    //
    // Body-relative like everything else: depth in the atmosphere is measured against the body's
    // OWN sea-level density, and "working hard for its speed" is a lift coefficient proxy,
    // L/(q*b^2), which is CL/aspect-ratio — no wing area needed, and no AoA needed either,
    // which matters because KSP does not expose one.
    private float humidDensityMin = 0.62f;   // fraction of sea-level density where the layer ends
    private float humidLoadMin = 0.10f;      // CL/AR: ordinary cruise sits far below this
    private float humidLoadFull = 0.26f;     // ...near CLmax for a typical aspect ratio
    // Second, independent requirement on the same floor: the wing has to actually be carrying
    // the aircraft. A steady approach or descent is L = W*cos(gamma), so this is ~1 in the case
    // we want, and well under 1 on any roll where the gear is taking the load. Belt and braces
    // with the airborne gate, and it also covers a bounce or a touch-and-go where
    // LandedOrSplashed flickers.
    private float humidLiftShareMin = 0.80f;
    private float humidLiftShareFull = 1.00f;
    private float humidFloor = 0.40f;        // peak visibility floor on a slow, hard-working wing

    // PERSISTED-WAKE SINK (OFF by default — see the writeup for why). Real trailing vortices
    // sink under their own induced flow, a few hundred ft/min early on, slower as they age. This
    // approximates that by nudging every EXISTING trail vertex down by a small amount on a fixed
    // cadence, so points that have existed longer end up lower — the "older = sunk further"
    // shape falls out of simple accumulation without tracking a per-vertex age. It is a uniform,
    // not decaying-with-age, sink rate, which is the part of the real behaviour this does NOT
    // capture; see the design notes for what a truer version would need.
    //
    // This touches the exact GetPosition/SetPosition path OnFloatingOriginShift depends on, just
    // on every tick instead of only on a shift event, so it ships disabled (rate 0f is a hard
    // no-op, checked before the loop runs) until it's been validated in-game. Set trailSinkRate
    // to something like 0.3-0.6 (m/s) to try it.
    private float trailSinkRate = 1.2f;      // m/s at the young end; ~300 ft/min. 0f disables.
    private float sinkAgeFalloff = 0.35f;    // rate multiplier at the oldest end ("slowing as they age")

    // 0 = apply the sink every frame, which is what it wants to be. 0.6.5 batched it at 0.1s to
    // save CPU, and that was a mistake worth spelling out: at 1.2 m/s a 0.1s batch teleports the
    // WHOLE trail 12cm downward in a single frame, ten times a second, while the emitter stays
    // pinned to the moving wingtip. The trail visibly detached from the tip and a fresh vertex
    // spawned to bridge the gap, over and over — which is exactly the "constantly spawning at
    // the wingtip" jitter. Per-frame it is ~2cm at 60fps and continuous. Raise this only if the
    // per-frame vertex rewrite ever shows up in a profile; it trades smoothness for CPU.
    private float sinkUpdateInterval = 0f;
    private float sinkTimer = 0f;

    // SHED-STRENGTH HISTORY. A ring buffer of how strong the vortex was at each moment it was
    // being shed, replayed along the trail by ApplyShedAppearance so that already-shed geometry
    // renders at the strength it was born with instead of at whatever the wing is doing now.
    // 64 * 0.08s = 5.1s of history, which covers contrailTimeMax.
    private const int shedSamples = 64;
    private const int shedKeyCount = 8;       // Gradient accepts at most 8 alpha keys
    private float shedSampleInterval = 0.08f;
    private float shedSampleTimer = 0f;
    private int shedWrite = 0;
    private List<float[]> shedHistory = new List<float[]>();
    private List<float> trailAges = new List<float>();       // age of the OLDEST live point
    private List<float> trailHeadAges = new List<float>();   // age of the NEWEST live point
    private float trailAlphaGain = 1.25f;

    // Reused scratch — TrailRenderer copies curve/gradient data on assignment, so one shared
    // instance is safe and avoids the per-frame allocation the old code did.
    // WIDTH gets its own, denser key set. AnimationCurve has no key limit — only Gradient is
    // capped at 8 — and the head ramp lives inside the first few percent of the trail, which
    // uniform 8-key spacing (0, 0.143, 0.286 ...) cannot resolve at all.
    private const int widthKeyCount = 14;
    private Keyframe[] widthKeys = new Keyframe[widthKeyCount];

    // Leading-end shape. The trail used to begin at full width, so it ended in a flat slab face
    // that pointed nowhere — which is why the origin was hard to locate, especially on a weak
    // low-AoA vortex or one anchored somewhere other than the true wingtip. It now converges
    // toward the anchor over the first headRampFrac of its length, giving the eye a wedge to
    // follow back to the source. It stops at headWidthFraction rather than reaching zero, so a
    // faint vortex still HAS a visible origin instead of fading out exactly where you need to
    // read it. Raise headWidthFraction if the point is too fine to see; lower it for a sharper
    // taper. Both are geometry only — no colour or alpha is touched.
    // Roll-up length in METRES, which is the whole point and what 0.6.11/0.6.12 got wrong.
    // Both expressed the taper as a FRACTION of trail length, and a trail is enormous — at
    // 520 m/s with a ~4.8 s lifetime it is about 2.5 km long, so "20% of the trail" was half a
    // kilometre of taper. The stretch you actually see beside the aircraft was the first ~2% of
    // that, where the width is essentially constant, so it still terminated on a flat face.
    //
    // A real vortex rolls up over roughly a chord — metres, not hundreds of metres — so the
    // ramp is now an absolute distance converted to a curve fraction against the trail's live
    // length each frame. headWidthFraction is where it starts, not zero, so a faint low-AoA
    // vortex still has a locatable origin.
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

    // Cached on the bounds timer: FindPartModulesImplementing allocates and GetTotalMass walks
    // every part, so neither belongs in the per-frame path.
    private List<ModuleLiftingSurface> liftSurfaces = new List<ModuleLiftingSurface>();
    private float vesselMassTons = 1f;
    private float gravityAccel = 9.81f;

    // Low-pass on the LIFT MEASUREMENT, which is a different thing from the build/decay rates
    // above and must not be confused with them. KSP recomputes liftForce at physics rate off
    // instantaneous part velocity, and KSP airframes visibly flex at their joints, so the raw sum
    // carries real high-frequency measurement noise — which Update() then samples at render
    // rate, adding a beat between the two. 0.6.4 hid that behind a slow decaySpeed; 0.6.5 made
    // decay fast (correctly, on physical grounds) and the noise came straight through to the
    // width and alpha of the newest trail segment, flickering at the wingtip.
    // Filtering the sensor is the right fix; slowing the physics is not. 0.12s is short enough
    // that a real unload still reads as immediate.
    private float smoothedLiftG = 1f;
    private float liftSmoothTime = 0.12f;

    private Bounds vesselBounds;
    private float vesselSize;

    // The vessel's geometric frame, resolved once by ComputeVesselAxes().
    //
    // Held in the ROOT PART'S LOCAL space, not world space, deliberately. Which local axis is the
    // fuselage never changes, but the world direction it points in changes every time the aircraft
    // moves — so caching world vectors here would hand every later caller a snapshot taken on
    // the runway. The properties below re-derive the world directions on demand instead.
    private Vector3 axLateralLocal = Vector3.right;
    private Vector3 axLengthLocal = Vector3.forward;
    private Vector3 axVertLocal = Vector3.up;
    private bool axLengthSigned = false;         // false: nose direction could not be determined

    private Vector3 axLateral { get { return vesselTransform.TransformDirection(axLateralLocal); } }
    private Vector3 axLength { get { return vesselTransform.TransformDirection(axLengthLocal); } }
    private Vector3 axVert { get { return vesselTransform.TransformDirection(axVertLocal); } }
    private float vesselSpan = 16f;   // ExtentAlong(vesselBounds, right) * 2; see massSpanFactor

    // ROGUE RENDERER REJECTION. Every vessel measurement in this file is built by encapsulating
    // part renderer bounds, and encapsulation has no tolerance for a single bad member: one
    // renderer with absurd bounds silently poisons the whole airframe measurement.
    //
    // This is not hypothetical. On a heavily modded install the effect went completely dark and
    // the log said why: "span=1373763000000000000.0m". Visual mods DELIBERATELY give their meshes
    // enormous bounds to defeat frustum culling — Scatterer, EVE, Singularity and Deferred all
    // do it — and GetComponentInChildren walks the entire transform subtree of a part, so one
    // of those parented under any part is picked up as if it were part geometry. From there
    // vesselSpan goes to 1e18, sizeFactor to 3e-16, circFactor with it, and every vortex renders
    // at zero width and zero alpha. The sources all spawn correctly; nothing is visible.
    //
    // A stock install has nothing that does this, which is exactly why it survived testing.
    private float maxPartExtent = 200f;   // metres: far above any real part, far below the garbage

    // SECOND-TIER CONTAMINATION. maxPartExtent catches the 1e18 cull-defeating quads, but not the
    // merely-large: engine plumes, reentry effects and similar visual meshes are tens of metres
    // and sail straight through a 200 m cap. On a modded install that inflated a 17 m fighter to
    // a "46 m long, 44 m tall" airframe — see ComputeVesselAxes for why near-equal length and
    // height is so much worse than simply being wrong.
    //
    // Colliders are the discriminator. Effect meshes have none: they are visual only. So a
    // renderer is checked against its own part's PHYSICAL extent, which cannot be inflated.
    // Only applied to renderers already large in absolute terms, so ordinary part geometry on a
    // stock install is never second-guessed.
    private float rendererColliderCheck = 5f;   // metres: below this, no collider test at all
    private float rendererColliderSlack = 3f;   // allowed multiple of the part's collider size
    private bool boundsLogged = false;
    private bool rogueRendererLogged = false;

    // Previous frame's Krakensbane-relative speed, held so the discontinuity guard does not
    // false-trip on the one frame where Krakensbane engages and rb_velocity collapses to ~0
    // while the anchor has genuinely just travelled a full frame at the old speed.
    private float lastRbSpeed = 0f;

    // VESSEL READINESS. Source detection runs ONCE, and every anchor it places is measured
    // against the airframe as it stands at that moment — so it has to run against an airframe
    // that has finished being built. "loaded" is not that test: on a scene load the vessel is
    // loaded while still PACKED, with its parts not yet at their flight positions.
    //
    // ComputeVesselAxes resolves the length axis by comparing bounds extents along two candidate
    // directions, so a bounds read mid-settle can resolve the WRONG axis — and that is the
    // failure this file already documents at length as the one that puts canard anchors at wing
    // roots. Detection then bakes it in permanently.
    private float sourceReadyTimeout = 30f;   // seconds; start anyway rather than never starting
    private int sourceSettleFrames = 5;       // physics frames to let part positions settle
    private float sourceConfirmDelay = 3f;    // see the confirmation pass in Start()

    IEnumerator Start()
    {
        // See sourceReadyTimeout. Waiting on `loaded` alone was the bug behind "Revert to Launch
        // moves the wingtip anchors, and only recovering and respawning puts them back": a revert
        // restores a cached scene and comes up faster than a fresh launch, so this coroutine won
        // the race and measured a vessel that was loaded but still packed. Wrong bounds, wrong
        // length axis, wrong tips — baked in for the rest of the flight, because detection
        // never runs again.
        float waited = 0f;
        while (waited < sourceReadyTimeout)
        {
            Vessel v = FlightGlobals.ActiveVessel;
            if (FlightGlobals.ready && v != null && v.loaded && !v.packed
                && v.rootPart != null && v.parts != null && v.parts.Count > 0)
                break;
            waited += Time.unscaledDeltaTime;
            yield return null;
        }

        // Unpacked is necessary but not sufficient: KSP settles part positions over the following
        // physics frames, and it is those transforms the tip search reads.
        for (int f = 0; f < sourceSettleFrames; f++)
            yield return new WaitForFixedUpdate();

        vessel = FlightGlobals.ActiveVessel;
        if (vessel == null || !vessel.loaded)
        {
            Debug.Log($"[VORTEX] no ready vessel after {waited:F1}s — not starting");
            yield break;
        }
        vesselTransform = vessel.transform;

        Debug.Log($"[VORTEX] WingtipVortex v{ModVersion} starting "
                  + $"(vessel ready after {waited:F1}s, packed={vessel.packed})");

        if (sharedTrailMat == null)
            sharedTrailMat = new Material(Shader.Find("Legacy Shaders/Particles/Additive"));
        if (sharedLineMat == null)
            sharedLineMat = new Material(Shader.Find("Legacy Shaders/Particles/Additive"));

        ComputeVesselBounds();
        FindVortexSources();

        GameEvents.onFloatingOriginShift.Add(OnFloatingOriginShift);

        // CONFIRMATION PASS. The wait above should be enough on its own, but it is a timing gate
        // and timing gates are exactly the kind of thing that holds on one install and not on
        // another — a slow modded load, or a part whose mesh is still being resized by
        // something like procedural wings. Detecting once means any miss is permanent, and the
        // player's only recourse was to recover and respawn.
        //
        // So detection is simply run a second time a few seconds later, which costs one extra
        // pass per flight and makes a mistimed first pass self-healing. Gated on the craft still
        // being on the ground, so it can never interrupt a wake that is already being drawn: that
        // is also precisely the situation the bug appears in, since a revert puts you back on the
        // runway.
        if (vessel.LandedOrSplashed)
        {
            yield return new WaitForSeconds(sourceConfirmDelay);
            if (vessel != null && vessel.loaded && !vessel.packed && vessel.LandedOrSplashed)
            {
                Debug.Log("[VORTEX] confirmation re-detect (still on the ground)");
                ClearSources();
                ComputeVesselBounds();
                FindVortexSources();
            }
        }
    }

    // Tears down every per-source object and the parallel lists that index them. Shared by
    // OnDestroy and the confirmation pass, so the two can never fall out of step as sources
    // gain new per-source state.
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

        // The vortex objects are deliberately unparented so recorded trail geometry stays in world
        // space — which also means nothing else in the scene ever cleans them up. Without this,
        // every Flight scene load left a full set behind, still holding geometry, forever.
        ClearSources();
    }

    // KSP's FloatingOrigin has no hook for TrailRenderer — its recorded vertices are baked
    // absolute world-space data, not driven by any Transform — so a shift leaves them in the
    // old frame while the anchor jumps to the new one, producing one long connecting segment
    // (the "forward spike"). We correct those vertices in place rather than clearing the trail,
    // which would only hide the spike by discarding the whole trail's history.
    //
    // WHICH offset is the part 0.6.1 got wrong. FloatingOrigin.setOffset() moves WORLD-anchored
    // content — celestial bodies, registered ParticleSystems, EffectBehaviour particles, the
    // terrain shader — by offsetNonKrakensbane, which is (offset + nonFrame). Loaded,
    // unpacked, airborne vessels move by offset ALONE. nonFrame is Krakensbane's FrameVel *
    // fixedDeltaTime: how far the world slides past the vessel each physics frame while the
    // velocity frame is engaged.
    //
    // A vortex is a contrail hanging in the air, so it is world-anchored and needs the FULL
    // world offset. Subtracting offset alone pinned the recorded vertices to the VESSEL's frame
    // and left a residual error of exactly nonFrame on every single frame. That is why the spike
    // survived 0.6.1, and why it only shows above ~200 m: Krakensbane.SafeToEngage() gates the
    // velocity frame on radarAltitude against altThreshold (200) / altThresholdAlone (50), so
    // below the gate FrameVel is zero, nonFrame is zero, and offset alone happened to be
    // correct — which is exactly the "fine below, broken above" split.
    //
    // LineRenderer needs no correction: its points are rebuilt from scratch every frame from
    // the (self-correcting) anchor position, never from stored history.
    void OnFloatingOriginShift(Vector3d offsetVessel, Vector3d nonFrame)
    {
        Vector3 worldOffset = (Vector3)(offsetVessel + nonFrame);

        // Anchors ride the vessel's Transform, so their recorded baseline moves by whatever KSP
        // applied to the vessel. Mirror setOffset()'s own branch here so the discontinuity guard
        // in Update() never reads a legitimate origin shift as a teleport.
        Vector3 anchorOffset = (vessel != null && vessel.loaded && !vessel.packed && !vessel.LandedOrSplashed)
            ? (Vector3)offsetVessel
            : worldOffset;

        for (int i = 0; i < lastAnchorPositions.Count; i++)
            lastAnchorPositions[i] -= anchorOffset;

        if (worldOffset.sqrMagnitude <= 0f) return;

        // Per-index Get/SetPosition rather than the array form: TrailRenderer.positionCount is
        // read-only, so SetPositions(Vector3[]) cannot express "same count, moved" without
        // risking a truncation, and it would allocate on every physics frame that Krakensbane
        // is engaged — which, above the altitude gate, is all of them.
        for (int i = 0; i < trails.Count; i++)
        {
            var tr = trails[i];
            int n = tr.positionCount;
            for (int j = 0; j < n; j++)
                tr.SetPosition(j, tr.GetPosition(j) - worldOffset);
        }

        // The tube's own history is world-space too and needs the same correction — but as a
        // plain array walk rather than two Unity interop calls per vertex, so it is strictly
        // cheaper than the TrailRenderer path above.
        for (int i = 0; i < ribbons.Count; i++)
        {
            var rb = ribbons[i];
            if (rb == null) continue;
            for (int j = 0; j < rb.count; j++) rb.pts[j] -= worldOffset;
        }
    }

    // Projecting an axis-aligned Bounds onto an arbitrary direction is the SUM of the absolute
    // per-axis contributions. Vector3.Dot(extents, dir) lets opposite axes cancel, so it
    // understates the reach of any part whose box is not aligned with the direction being
    // measured — i.e. exactly the canted wings and angled winglets that were losing the
    // "which part sticks out furthest" comparison and handing the vortex to the wrong surface.
    static float ExtentAlong(Bounds b, Vector3 dir)
    {
        Vector3 e = b.extents;
        return Mathf.Abs(e.x * dir.x) + Mathf.Abs(e.y * dir.y) + Mathf.Abs(e.z * dir.z);
    }

    // Finds the outermost mesh vertex of a part on the given side, in world space.
    //
    // "Outermost" is span distance from the vessel's roll axis (x and y together), not raw
    // lateral x. On a winglet or canard angled up or down, the furthest point is displaced in Y
    // as much as in X, so an x-only scan settles mid-chord and leaves the anchor hanging beside
    // the surface rather than on its tip. On a level wing y barely varies across the part, so
    // this reduces to the previous behaviour.
    //
    // The forward preference is a TIE-BREAK among vertices already at the tip, not a weight
    // added to the score. As a weight (lateral + forward * 0.25f) it could trade real span for
    // forward reach and walk the anchor inboard along a swept leading edge.
    //
    // All of the part's MeshFilters are scanned, not GetComponentInChildren's first hit: KSP
    // wing parts routinely carry several sub-meshes and the first is not reliably the
    // aerodynamic surface. sharedMesh, not mesh — MeshFilter.mesh instantiates and leaks a
    // copy of the mesh on every access.
    bool TryFindTipVertex(Part part, bool isRight, out Vector3 tipWorld)
    {
        tipWorld = Vector3.zero;
        var filters = part.GetComponentsInChildren<MeshFilter>();
        if (filters == null || filters.Length == 0) return false;

        float side = isRight ? 1f : -1f;

        // Pass 1: how far out does this part actually reach?
        float maxSpan = float.MinValue;
        foreach (var mf in filters)
        {
            // GetComponentsInChildren walks the transform hierarchy, and in KSP a child PART is
            // a transform child — so drop meshes that belong to something attached to this
            // wing, or a wingtip-mounted pod wins the "furthest out" contest instead of the wing.
            if (mf.GetComponentInParent<Part>() != part) continue;
            Mesh mesh = mf.sharedMesh;
            if (mesh == null) continue;
            Vector3[] verts = mesh.vertices;
            for (int v = 0; v < verts.Length; v++)
            {
                Vector3 local = ToVesselFrame(mf.transform.TransformPoint(verts[v]));
                if (local.x * side < 0.05f) continue;   // wrong side of, or on, the centerline
                // Distance from the ROLL axis: lateral and vertical together, never the fore/aft
                // term. Including chordwise distance here is what made the most-forward vertex of
                // a nose-mounted canard beat its actual tip.
                float span = Mathf.Sqrt(local.x * local.x + local.y * local.y);
                if (span > maxSpan) maxSpan = span;
            }
        }
        if (maxSpan == float.MinValue) return false;

        // Pass 2: among the vertices already at the tip, take the most forward one.
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
                if (local.x * side < 0.05f) continue;
                float span = Mathf.Sqrt(local.x * local.x + local.y * local.y);
                if (span < maxSpan - tipBand) continue;
                if (local.z > bestForward) { bestForward = local.z; tipWorld = world; found = true; }
            }
        }
        return found;
    }

    // Resolves the vessel's geometric frame.
    //
    // Local component indices cannot be trusted for this, and that is not a subtlety — it is
    // the bug that put canard anchors at the wing root. KSP parts are authored rocket-style with
    // the stack along +Y, so on an aircraft built from a cockpit the ROOT part's local Y is the
    // NOSE direction and local Z is VERTICAL, the exact opposite of the "z is forward, y is up"
    // convention this file's geometry code assumed. Measured on the test airframe: local Z spanned
    // 3.9 m (the fuselage's height) while local Y spanned 21 m (its length).
    //
    // Only the lateral axis was ever safe, which is why wingtips were still found and this stayed
    // hidden: on a big wing the lateral term dominates every distance it appears in. Give it a
    // short surface mounted far off-centre — a nose canard — and the mis-assigned axis takes
    // over. See TryFindTipVertex for exactly how that lands the anchor on the leading edge root.
    // Spread of PART POSITIONS along a world direction. Immune to renderer contamination, which
    // is exactly what the axis comparison needs.
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
        // Trusted: the tip scan already locates wingtips with it.
        axLateralLocal = Vector3.right;

        // An aircraft is longer nose-to-tail than it is deep, so the airframe's own extent picks
        // the axis regardless of how the root part happens to be oriented.
        //
        // Measured from PART POSITIONS, not from vesselBounds, and that distinction is the whole
        // fix for "reverting to launch moves the wingtip anchors". vesselBounds is built from
        // renderers, so any effect mesh that survives the filter inflates it — and it inflates
        // fore/aft and vertical together. A 17 m fighter measured 46.3 m long and 44.1 m tall,
        // a margin of 5%, which makes this comparison a coin flip decided by whichever effect
        // renderers happened to be enabled on that particular scene load. Flip it and the frame's
        // length and height axes swap, so the tip search's "most forward vertex" pass becomes
        // "highest vertex" and every anchor moves. Detection runs once, so it stays moved.
        //
        // Part transforms are airframe by definition and cannot be inflated by anything. They
        // understate absolute extent (a wing panel's origin sits at its inboard node), but this
        // only needs the COMPARISON, and for that they are strictly better. On the same craft
        // they give roughly 16 m against 3 m: a 5:1 margin instead of 1.05:1.
        float fwdSpread = PartSpread(vesselTransform.forward);
        float upSpread = PartSpread(vesselTransform.up);
        bool haveSpread = (fwdSpread + upSpread) > 0.01f;
        if (!haveSpread)
        {
            fwdSpread = ExtentAlong(vesselBounds, vesselTransform.forward);
            upSpread = ExtentAlong(vesselBounds, vesselTransform.up);
        }
        // Ties go to forward. An aircraft that genuinely measures as tall as it is long is not a
        // shape this can resolve, and forward is right far more often.
        axLengthLocal = (fwdSpread >= upSpread * 0.95f) ? Vector3.forward : Vector3.up;

        // Which way along it is the nose. No transform axis answers that, but a command pod does:
        // cockpits and probe cores sit ahead of the airframe's centre on essentially every aircraft.
        axLengthSigned = false;
        float podZ = 0f;
        int podCount = 0;
        foreach (Part p in vessel.parts)
        {
            if (p == null || !p.Modules.Contains("ModuleCommand")) continue;
            podZ += Vector3.Dot(p.transform.position - vesselTransform.position, axLength);
            podCount++;
        }

        // Reference point is the PART CENTROID, not vesselBounds.center, for the same reason the
        // axis choice above no longer reads bounds — and this one is the more dangerous of the
        // two. An effect mesh trailing behind the engines drags the bounds centre aft; carry it
        // past the cockpit and the nose direction INVERTS. The tip search's second pass then
        // takes the most REARWARD vertex instead of the most forward one, so every anchor jumps
        // to the opposite edge of the wing. That is a flip with no visual warning and it lands
        // wherever the contamination happened to sit on that particular scene load, which is
        // exactly the "revert to launch moves the anchors" report.
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

        // Vertical completes the frame. Signed against the planet: source selection runs with the
        // aircraft sitting on the runway, so "away from the body" is the aircraft's own up.
        axVertLocal = Vector3.Cross(axLengthLocal, axLateralLocal);
        if (axVertLocal.sqrMagnitude < 0.001f) axVertLocal = Vector3.up;   // degenerate guard
        axVertLocal = axVertLocal.normalized;
        if (Vector3.Dot(axVert, (Vector3)vessel.upAxis) < 0f) axVertLocal = -axVertLocal;

        // All three full extents. The previous line printed length as a HALF extent next to two
        // full ones, which made a 9.4 m fuselage read as 4.7 m against an 8.9 m height.
        // Part spread is printed alongside, because it is what actually chose the axis and a
        // small margin there is the signature of the bug this replaced.
        Debug.Log($"[VORTEX] axes resolved — nose={(axLengthSigned ? "known" : "UNKNOWN")} " +
                  $"length={ExtentAlong(vesselBounds, axLength) * 2f:F1}m " +
                  $"span={ExtentAlong(vesselBounds, axLateral) * 2f:F1}m " +
                  $"height={ExtentAlong(vesselBounds, axVert) * 2f:F1}m " +
                  $"(part spread fwd={fwdSpread:F1}m up={upSpread:F1}m"
                  + (haveSpread ? "" : ", FALLBACK to bounds")
                  + $", nose margin={noseMargin:F1}m)");
    }

    // World -> vessel frame as (lateral, vertical, forward), so the geometry code below can keep
    // reading .x/.y/.z as right/up/nose and actually be correct about it. Dots against the LOCAL
    // basis rather than the world properties: the tip scan calls this once per mesh vertex, and
    // the world form would run three TransformDirection calls on every one of them.
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

    // Sampled once per body, not per frame: walks the body's own temperature curve to find the
    // coldest air it actually has, so the contrail band is guaranteed to be reachable there.
    void RefreshContrailBand(CelestialBody body)
    {
        contrailBody = body;
        bodyContrailWarm = contrailTempWarm;
        bodyContrailCold = contrailTempCold;
        if (body == null || !body.atmosphere) return;

        // Non-positive samples are discarded. Kopernicus and rescale mods can leave the curve
        // undefined at the very top of the atmosphere, and the modded install reported
        // "air 0.0-288.2K" — a 0 K reading would make the guard below inert on any body whose
        // real minimum is above the physical anchor.
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
        Debug.Log($"[VORTEX] contrail band on {body.bodyName}: air {minT:F1}-{maxT:F1}K, "
                  + $"band {bodyContrailCold:F1}->{bodyContrailWarm:F1}K, "
                  + $"rho_ASL {body.atmDensityASL:F3}");
    }

    // A part's real PHYSICAL extent, from its colliders. Visual-only meshes have no collider, so
    // this is a measure of the part that effect geometry cannot inflate.
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

    // Renderer bounds for one part, with everything that is not part geometry rejected. Used by
    // every measurement in this file, so the rejection happens once and applies everywhere.
    bool TryGetPartBounds(Part p, out Bounds bounds)
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

            // Belongs to an attached CHILD part, not to this one. Same exclusion TryFindTipVertex
            // uses, and for the same reason: a wingtip-mounted pod is not part of the wing.
            if (r.GetComponentInParent<Part>() != p) continue;

            // Wake geometry, ours included. A TrailRenderer's bounds cover the entire trail, so
            // measuring the aircraft with one in scope measures the aircraft plus its own wake
            // and grows without limit.
            if (r is TrailRenderer || r is LineRenderer || r is ParticleSystemRenderer) continue;

            Bounds b = r.bounds;
            Vector3 s = b.size, c = b.center;
            if (float.IsNaN(s.x) || float.IsNaN(s.y) || float.IsNaN(s.z)
                || float.IsInfinity(s.x) || float.IsInfinity(s.y) || float.IsInfinity(s.z)
                || float.IsNaN(c.x) || float.IsInfinity(c.x)) continue;

            // A large renderer that reaches well beyond its own part's physical geometry is an
            // effect mesh, not the part (see rendererColliderCheck).
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
                    Debug.Log($"[VORTEX] ignoring oversized renderer '{r.name}' on part={p.name} "
                              + $"size={s.magnitude:F0}m — it is not part geometry "
                              + "(visual mods use huge bounds to defeat frustum culling)");
                }
                continue;
            }

            if (!any) { bounds = b; any = true; } else bounds.Encapsulate(b);
        }
        return any;
    }

    // True once every renderer has finished aging out, so the vacuum early-out above can tell
    // "the fade has completed" from "the fade has not started yet".
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

            // The part ORIGIN always counts. It cannot be poisoned, so the measurement can never
            // come back empty or absurd even if every renderer on the craft is rejected.
            if (!initialized) { vesselBounds = new Bounds(part.transform.position, Vector3.zero); initialized = true; }
            else vesselBounds.Encapsulate(part.transform.position);

            Bounds pb;
            if (TryGetPartBounds(part, out pb)) vesselBounds.Encapsulate(pb);
        }
        vesselSize = Mathf.Clamp(vesselBounds.size.magnitude, 0.1f, 1000f);

        // Span + mass are read at the same throttled cadence as the rest of this method (mass in
        // particular: Vessel.GetTotalMass() walks every part and sums resource mass, so it's not
        // something to call from the per-frame Update() loop). See massSpanFactor's declaration.
        // Clamped as a last line of defence. Belt and braces with the rejection above: if some
        // future mod finds a way past it, the effect degrades to wrong-looking rather than to
        // invisible, and the log below says so.
        vesselSpan = Mathf.Clamp(ExtentAlong(vesselBounds, vesselTransform.right) * 2f, 1f, 500f);
        vesselMassTons = Mathf.Max(vessel.GetTotalMass(), 0.001f);
        float sizeRatio = (vesselMassTons / vesselSpan) / (refMass / refSpan);
        sizeFactor = (2f * sizeRatio) / (1f + Mathf.Max(sizeRatio, 0f));

        if (!boundsLogged)
        {
            boundsLogged = true;
            Debug.Log($"[VORTEX] measured {vessel.vesselName}: span={vesselSpan:F1}m "
                      + $"size={vesselSize:F1}m mass={vesselMassTons:F1}t sizeFactor={sizeFactor:F2}");
        }

        // Lifting surfaces and local gravity, cached here for the same reason. ModuleControlSurface
        // derives from ModuleLiftingSurface, so this one lookup catches canards and elevons too.
        liftSurfaces = vessel.FindPartModulesImplementing<ModuleLiftingSurface>();
        gravityAccel = Mathf.Max((float)FlightGlobals.getGeeForceAtPosition(vessel.CoM).magnitude, 0.01f);
    }

    // ═══════════════════════════════════════════════════════════════
    // SOURCE SELECTION
    //
    // One rule, four slots: {left, right} x {main wing, secondary surface}. Within a slot the
    // winner is simply the part that reaches FURTHEST from the vessel centreline, which is what
    // "the wingtip" means. That single rule is what caps an aircraft at four vortices and what
    // stops a wing built from several chained panels producing one vortex per panel.
    //
    // Three defects in the pass this replaced, all of which put duplicate sources on one large
    // multi-part swept wing:
    //
    //   1. The main-wing pass read isLift/isControl and then never tested them, so ANY large
    //      enough off-centre part — engine nacelle, pylon, tank — could win the slot. Only
    //      the climb stage had the filter, under a comment calling it "the missing piece"; it was
    //      never back-ported to where candidates are gathered.
    //
    //   2. The canard pass excluded the main wing by PART IDENTITY (p.part == leftRear), which
    //      does nothing when the wing is several parts. Its only other guard was "at least 0.2m
    //      ahead of the main wing", and on a SWEPT wing an inboard panel legitimately sits
    //      further forward than the tip panel — so an inner section of the very same wing
    //      cleared the gate and became a second, bogus "canard".
    //
    //   3. Fore/aft was measured against vesselTransform, i.e. the ROOT PART, whose position is
    //      a build-order accident rather than anything about the airframe.
    //
    // The replacement never asks "is this part forward of some plane" to decide whether a part
    // is a separate surface. It asks whether the part belongs to the same physically connected
    // aero structure as the main wing, which is the question that actually decides it.
    void FindVortexSources()
    {
        // ═══════════════════════════════════════════════════════════════
        // DIAGNOSTIC — full aero-surface inventory
        // ═══════════════════════════════════════════════════════════════
        // Resolve the vessel's geometric frame before anything is measured against it.
        ComputeVesselAxes();

        Debug.Log("[VORTEX] === AERO INVENTORY ===");
        foreach (Part p in vessel.parts)
        {
            if (p == null) continue;
            bool hasLift = p.Modules.Contains("ModuleLiftingSurface");
            bool hasCtrl = p.Modules.Contains("ModuleControlSurface");
            if (!hasLift && !hasCtrl) continue;
            // In the resolved frame: lat = span station, vert = height, fwd = nose-positive.
            // The raw local components this used to print were whichever axes the root part
            // happened to use, which on the canard airframe made "z" the fuselage HEIGHT.
            Vector3 lp = ToVesselFrame(p.transform.position);
            Bounds ib;
            float ai = TryGetPartBounds(p, out ib) ? ib.size.x * ib.size.z : 0f;
            Debug.Log($"[VORTEX] AERO part={p.name} fwd={lp.z:F2} lat={lp.x:F2} vert={lp.y:F2} area={ai:F2} lift={hasLift} ctrl={hasCtrl}");
        }
        Debug.Log("[VORTEX] === END INVENTORY ===");

        // ── CANDIDATE GATHERING ─────────────────────────────────────────
        List<AeroCandidate> pool = new List<AeroCandidate>();
        foreach (Part p in vessel.parts)
        {
            if (p == null) continue;

            // The gate the old main-wing pass computed and then never applied.
            if (!p.Modules.Contains("ModuleLiftingSurface") &&
                !p.Modules.Contains("ModuleControlSurface")) continue;

            // Same rejection as the vessel measurement — otherwise a part carrying a
            // cull-defeating renderer wins every slot with an extremity of 1e18.
            Bounds b;
            if (!TryGetPartBounds(p, out b)) continue;
            if (b.size.sqrMagnitude <= 0f) continue;
            if (b.size.x * b.size.z < minSourceArea) continue;

            // Measured from the bounds CENTRE, not transform.position: a wing panel's origin sits
            // at its inboard attach node, which badly understates where the panel actually is.
            float cProj = Vector3.Dot(b.center - vesselTransform.position, axLateral);
            if (Mathf.Abs(cProj) < minLateralOffset) continue;   // centre sections, vertical fins

            float ext = Mathf.Abs(cProj) + ExtentAlong(b, axLateral);
            float zc = Vector3.Dot(b.center - vesselTransform.position, axLength);
            float halfZ = ExtentAlong(b, axLength);

            pool.Add(new AeroCandidate(p, b, cProj < 0f, ext, zc, halfZ));
        }

        if (pool.Count == 0)
        {
            Debug.Log("[VORTEX] no qualifying lifting surfaces — no vortices");
            return;
        }

        // Sorted once, descending by lateral reach. Every "pick a winner" below is then just
        // "the first entry matching the filter", which is what makes the four-slot cap structural
        // rather than something that emerges from two heuristics agreeing.
        pool.Sort((a, b) => b.extremity.CompareTo(a.extremity));
        foreach (var c in pool)
            Debug.Log($"[VORTEX] CANDIDATE part={c.part.name} side={(c.isLeft ? "L" : "R")} ext={c.extremity:F2} z={c.z:F2}");

        // ── MAIN WING: furthest-reaching surface on each side ───────────
        AeroCandidate mainLeft = BestOnSide(pool, true, null);
        AeroCandidate mainRight = BestOnSide(pool, false, null);

        // ── ASSEMBLY: everything physically continuous with those winners ──
        HashSet<Part> mainAssembly = BuildAssembly(pool, mainLeft, mainRight);
        Debug.Log($"[VORTEX] main assembly = {mainAssembly.Count} part(s)");

        // Longitudinal span of the main wing. Second line of defence: a panel the flood fill
        // missed (a build where nothing is chained AND nothing quite touches) still has to sit
        // clear of the wing's own chord before it can count as a separate surface.
        float mainZLo = float.MaxValue, mainZHi = float.MinValue;
        foreach (var c in pool)
        {
            if (!mainAssembly.Contains(c.part)) continue;
            if (c.zLo < mainZLo) mainZLo = c.zLo;
            if (c.zHi > mainZHi) mainZHi = c.zHi;
        }
        if (mainZLo > mainZHi) { mainZLo = 0f; mainZHi = 0f; }
        float sep = Mathf.Max(secondaryMinSeparation, (mainZHi - mainZLo) * 0.15f);

        // ── SECONDARY: the best surface that is NOT the main wing ───────
        //
        // Forward and aft secondaries are NOT equivalent, and 0.6.7 was wrong to treat them as if
        // they were.
        //
        // Every finite lifting surface sheds tip vortices, tailplanes included, so "does it shed
        // one" is the wrong question. What this mod draws is CONDENSATION, which needs the vortex
        // core's pressure drop to cool air past its dew point — and that drop scales with
        // circulation SQUARED. A conventional stabiliser carries a few percent of the aircraft's
        // lift (usually downward, trimming the nose-down moment of a CG ahead of the wing's centre
        // of lift), over a much shorter span, inside the main wing's downwash. Roughly: a tenth of
        // the wing's circulation is a hundredth of its core pressure drop. That is nowhere near
        // condensing, which is why photographs of hard-manoeuvring jets show vapour off wingtips,
        // LERX and flap edges and essentially never off the stabiliser tips.
        //
        // A canard is the opposite case — clean undisturbed air ahead of the wing, a real share
        // of total lift, high AoA exactly when it matters — and those genuinely do show. Hence
        // forward is preferred outright.
        //
        // The one aft case worth keeping is a true tandem wing, where the rear surface is a second
        // wing carrying a comparable load rather than a trim device. Span ratio separates them
        // cleanly: a stabiliser is roughly a third of the wing, a tandem wing nearly all of it.
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

        // Both sides take their secondary from the SAME station, so a canard on one side and a
        // stabiliser on the other can never be paired up as though they were one surface.
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
            Debug.Log($"[VORTEX] aft surface ignored — span is {pct:F0}% of the wing, a stabiliser not a wing");
        }

        AeroCandidate secLeft = null, secRight = null;
        HashSet<Part> secAssembly = null;
        if (station != null)
        {
            secLeft = BestOnSide(station, true, null);
            secRight = BestOnSide(station, false, null);
            secAssembly = BuildAssembly(station, secLeft, secRight);
            Debug.Log($"[VORTEX] secondary station = {stationName}, {secAssembly.Count} part(s)");
        }
        else
        {
            Debug.Log("[VORTEX] no secondary surface qualified");
        }

        // ── SPAWN: at most one per slot, four slots ─────────────────────
        bool okML = SpawnSlot(pool, mainLeft, mainAssembly, false, 1f, "MAIN LEFT");
        bool okMR = SpawnSlot(pool, mainRight, mainAssembly, true, 1f, "MAIN RIGHT");

        // Symmetry fallback: sample the real tip on the surviving side, then mirror it across
        // the centreline, so a detection failure on one side does not leave a one-sided effect.
        if (!okML && okMR && mainRight != null)
            AddWingVortex(mainRight.part, false, 1f, true, mainAssembly);
        if (!okMR && okML && mainLeft != null)
            AddWingVortex(mainLeft.part, true, 1f, true, mainAssembly);

        // How big this secondary is next to the main wing. A primary canard on a canard aircraft
        // is a large fraction of the span and carries a real share of the lift; a nose-mounted
        // control canard is a fraction of that. The gate in Update() reads this so the two are not
        // held to the same threshold.
        float ratioL = (mainExt > 0f && secLeft != null) ? Mathf.Clamp01(secLeft.extremity / mainExt) : 1f;
        float ratioR = (mainExt > 0f && secRight != null) ? Mathf.Clamp01(secRight.extremity / mainExt) : 1f;
        if (secLeft != null || secRight != null)
            Debug.Log($"[VORTEX] secondary span ratio L={ratioL:F2} R={ratioR:F2} (of main wing)");

        // Strength from span reach rather than from which slot it landed in — see MainLikeness.
        float strL = SecondaryStrength(ratioL);
        float strR = SecondaryStrength(ratioR);
        if (secLeft != null || secRight != null)
            Debug.Log($"[VORTEX] secondary strength L={strL:F2} R={strR:F2} "
                      + $"(tip-likeness L={MainLikeness(ratioL):F2} R={MainLikeness(ratioR):F2})");

        bool okSL = SpawnSlot(pool, secLeft, secAssembly, false, strL, "SECONDARY LEFT", ratioL);
        bool okSR = SpawnSlot(pool, secRight, secAssembly, true, strR, "SECONDARY RIGHT", ratioR);

        if (!okSL && okSR && secRight != null)
            AddWingVortex(secRight.part, false, strR, true, secAssembly, ratioR);
        if (!okSR && okSL && secLeft != null)
            AddWingVortex(secLeft.part, true, strL, true, secAssembly, ratioL);

        Debug.Log($"[VORTEX] {trails.Count} vortex source(s) created (hard cap 4)");
    }

    // How much of a wingtip this surface is, from how much of the widest span it reaches.
    // Smoothstepped so there is no kink at either end of the transition.
    float MainLikeness(float spanRatio)
    {
        float t = Mathf.Clamp01((spanRatio - mainLikeRatioMin)
                                / Mathf.Max(mainLikeRatioFull - mainLikeRatioMin, 0.01f));
        return t * t * (3f - 2f * t);
    }

    // Strength for a secondary slot. Reaches exactly 1 for a surface that is a wingtip in all but
    // name, which also makes the gate in Update() skip it entirely (it is guarded on
    // strengths[i] < 1f), so such a surface is handled by the identical code path as a main wing
    // rather than by a secondary path tuned to imitate one.
    float SecondaryStrength(float spanRatio)
    {
        return Mathf.Lerp(secondaryStrengthMin, 1f, MainLikeness(spanRatio));
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

    // Everything physically continuous with the seed parts.
    //
    // Segments of one wing are either linked in the part tree or physically touching, and which
    // of the two depends on how the player built it: chaining panel to panel makes them
    // parent/child, while attaching each panel separately to the fuselage makes them siblings
    // that only adjacency relates. Testing BOTH is what makes this hold regardless of build
    // order. Anything landing in this set is the SAME wing and can never become a second source.
    HashSet<Part> BuildAssembly(List<AeroCandidate> pool, AeroCandidate seedA, AeroCandidate seedB)
    {
        HashSet<Part> assembly = new HashSet<Part>();
        if (seedA != null) assembly.Add(seedA.part);
        if (seedB != null) assembly.Add(seedB.part);
        if (assembly.Count == 0) return assembly;

        // Small, and deliberately so: too generous and a closely mounted canard gets swallowed
        // into the wing. Erring that way costs a vortex; erring the other way is the duplicate
        // this whole rewrite exists to remove.
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

    // Fills one slot. Tries the chosen part, then any other part of the SAME assembly on that
    // side, so a panel whose mesh scan happens to fail does not cost the side its vortex.
    bool SpawnSlot(List<AeroCandidate> pool, AeroCandidate chosen, HashSet<Part> group,
                   bool isRight, float strength, string label, float spanRatio = 1f)
    {
        if (chosen == null) return false;

        if (AddWingVortex(chosen.part, isRight, strength, false, group, spanRatio))
        {
            Debug.Log($"[VORTEX] {label} part={chosen.part.name} ext={chosen.extremity:F2}");
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
                Debug.Log($"[VORTEX] {label} (fallback) part={c.part.name} ext={c.extremity:F2}");
                return true;
            }
        }

        Debug.Log($"[VORTEX] {label} — no usable geometry");
        return false;
    }

    // Returns false when the part yields no usable tip, so the caller can try another panel of
    // the same wing rather than silently losing that side.
    bool AddWingVortex(Part wing, bool isRight, float strength, bool mirror = false,
                       HashSet<Part> restrictTo = null, float spanRatio = 1f)
    {
        // If this side is mirrored from the opposite wing, first sample the real tip on the source wing,
        // then mirror the world position across the vessel centerline.
        Transform anchor = CreateWingtipAnchor(wing, mirror ? !isRight : isRight, restrictTo);
        if (anchor == null) return false;

        if (mirror)
        {
            Vector3 local = ToVesselFrame(anchor.position);
            local.x *= -1f;
            anchor.position = FromVesselFrame(local);
        }


        GameObject obj = new GameObject("Vortex");
        // CRITICAL: do NOT parent to vessel — prevents trail inheriting vessel motion
        obj.transform.parent = null;

        TrailRenderer tr = obj.AddComponent<TrailRenderer>();
        tr.time = 1.5f; tr.startWidth = 0.15f; tr.endWidth = 0.03f;
        tr.numCapVertices = trailCapVertices;   // round the ends; square caps read as a cut slab
        tr.sharedMaterial = sharedTrailMat; tr.enabled = false;
        // NOTE: KSP's Unity version does not support useWorldSpace on TrailRenderer
        // Detaching from vessel (no parent) already ensures world-space behavior

        LineRenderer lr = obj.AddComponent<LineRenderer>();
        lr.positionCount = historyLength; lr.startWidth = 0.1f; lr.endWidth = 0.02f;
        lr.sharedMaterial = sharedLineMat; lr.enabled = false;

        trailObjs.Add(obj); trails.Add(tr); lines.Add(lr);
        anchors.Add(anchor); strengths.Add(strength); spanRatios.Add(Mathf.Clamp01(spanRatio));
        lineHistory.Add(new Queue<Vector3>());
        trailWasActive.Add(false); lineWasActive.Add(false);
        lastFlows.Add(Vector3.zero); lastAnchorPositions.Add(anchor.position);
        shedHistory.Add(new float[shedSamples]); trailAges.Add(0f); trailHeadAges.Add(0f);

        bool wantRibbon = enableRibbonMesh
                          && (!ribbonMainOnly || strength >= 1f)
                          && (!ribbonFirstSourceOnly || ribbons.Count == 0);
        ribbons.Add(wantRibbon ? CreateRibbon() : null);
        return true;
    }

    // One vortex rope's worth of geometry and the wake history behind it.
    //
    // Buffers are allocated ONCE at full size and rewritten in place. Every ring is always emitted,
    // even before the wake has filled — surplus rings collapse onto the tail point at zero
    // radius and zero alpha, which costs nothing visually and means the vertex count, the index
    // buffer and the triangle list never change size. KSP is GC-sensitive; per-frame reallocation
    // of a few thousand vertices is exactly the kind of churn that shows up as stutter.
    private class WakeRibbon
    {
        public GameObject obj;
        public Mesh mesh;

        // Wake history, index 0 = newest. World space, so the floating-origin correction can
        // treat it exactly like the TrailRenderer vertices it replaces.
        public Vector3[] pts;
        public float[] birth;
        public float[] shed;

        // Total distance the wingtip had travelled when each ring was laid. This is the ONLY
        // thing the helix phase may key off: it is fixed at birth, so shed geometry cannot move
        // afterwards. Arc-from-the-head cannot be used, because the head keeps moving and every
        // insertion would then shift every existing ring's phase, sliding the whole pattern
        // along the rope — which is what dragged old wake when emission restarted.
        public float[] odo;
        public float odometer;

        // Inward direction, world space, frozen when the ring was laid.
        public Vector3[] inDir;

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
        r.odometer = 0f;
        r.count = 0;

        r.verts = new Vector3[rings * K];
        r.uvs = new Vector2[rings * K];
        r.cols = new Color[rings * K];

        // Index buffer is built once and never touched again, which is the point of always
        // emitting every ring.
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
        mr.sharedMaterial = sharedTrailMat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;

        return r;
    }

    // Lays a new ring only once the wingtip has actually travelled far enough, so ring spacing is
    // a DISTANCE rather than a frame rate. That is what makes every downstream measurement —
    // age, arc length, roll-up, twist — exact rather than inferred from an index, which is the
    // estimate that made the TrailRenderer helix phase too noisy to hold a shape.
    // Brightness multiplier for the ring about to be laid, from the odometer so it is fixed in
    // space rather than in time.
    float RibbonFlowVar(WakeRibbon r, float depth)
    {
        if (depth <= 0f) return 1f;
        return 1f + depth * ((Mathf.PerlinNoise(r.odometer * ribbonFlowScale, r.seed) - 0.5f) * 2f);
    }

    void PushRibbonPoint(WakeRibbon r, Vector3 p, float now, float shed, float flowDepth)
    {
        if (r.count > 0)
        {
            r.shed[0] = shed * RibbonFlowVar(r, flowDepth);   // keep the head's strength live
            if ((p - r.pts[0]).sqrMagnitude < 0.0625f) return;   // 0.25 m, anti-bunching only
        }
        if (r.count > 0) r.odometer += (p - r.pts[0]).magnitude;

        int n = Mathf.Min(r.count, r.pts.Length - 1);
        System.Array.Copy(r.pts, 0, r.pts, 1, n);
        System.Array.Copy(r.birth, 0, r.birth, 1, n);
        System.Array.Copy(r.shed, 0, r.shed, 1, n);
        System.Array.Copy(r.odo, 0, r.odo, 1, n);
        System.Array.Copy(r.inDir, 0, r.inDir, 1, n);

        // Toward the centreline, decided now and never revisited.
        Vector3 lat = axLateral;
        float side = Vector3.Dot(p - vesselTransform.position, lat);
        r.inDir[0] = (side >= 0f) ? -lat : lat;

        r.pts[0] = p; r.birth[0] = now; r.shed[0] = shed * RibbonFlowVar(r, flowDepth); r.odo[0] = r.odometer;
        r.count = Mathf.Min(r.count + 1, r.pts.Length);
    }

    void BuildRibbonMesh(WakeRibbon r, float now, float widthScale, float contrailBlend, float life)
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

        // Parallel transport. A naive Cross(tangent, up) frame flips violently wherever the
        // tangent passes near the reference axis; carrying the previous ring's normal forward and
        // re-orthogonalising it against the new tangent is what keeps the tube from writhing.
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

            // Only live rings advance arc. Surplus rings all sit on the tail, so the old form
            // re-added that final segment once per dead ring and inflated arc roughly eightfold
            // (measured: 560 m across a wake that was actually ~70 m long).
            if (i > 0 && i < n) arc += (r.pts[src] - r.pts[src - 1]).magnitude;

            float age = Mathf.Max(now - r.birth[src], 0f);
            float ageFrac = Mathf.Clamp01(age / Mathf.Max(life, 0.01f));

            // Roll-up in metres, exactly as the trail taper does it, but on a real arc length
            // rather than a fraction of an unknown total.
            float grow = Mathf.Clamp01(arc / Mathf.Max(rollup, 0.01f));
            grow = grow * grow * (3f - 2f * grow);
            // Close the last stretch of the rope to nothing, in BOTH radius and alpha. Radius
            // matters as much as alpha here: without it the tube keeps its full cross-section to
            // the very last ring — wider than the middle at altitude, since tailRatio runs to
            // 1.6 — and no amount of alpha fade hides a blunt tube end.
            float endPos = (n > 1) ? (float)src / (n - 1) : 0f;
            float endFade = 1f - Mathf.Clamp01((endPos - (1f - ribbonEndFade)) / Mathf.Max(ribbonEndFade, 0.01f));
            endFade = endFade * endFade * (3f - 2f * endFade);

            float radius = ribbonRadiusScale * r.shed[src] * widthScale
                           * Mathf.Lerp(headWidthFraction, 1f, grow)
                           * Mathf.Lerp(1f, tailRatio, ageFrac)
                           * endFade;

            float alpha = live
                ? Mathf.Clamp01(r.shed[src] * trailAlphaGain * (1f - Mathf.Pow(ageFrac, ribbonFadePow)) * endFade)
                : 0f;
            if (!live) radius = 0f;

            // Bend toward the centreline, easing in over ribbonInwardGrow and then holding, so
            // the rope converges a little and afterwards runs parallel. Direction was frozen at
            // birth; only the magnitude eases, and it saturates within the first few metres, so
            // nothing already shed can be re-aimed by later manoeuvring.
            float ease = Mathf.Clamp01(arc / Mathf.Max(ribbonInwardGrow, 0.01f));
            ease = ease * ease * (3f - 2f * ease);
            Vector3 centre = p + r.inDir[src] * (ribbonInwardAmp * r.shed[src] * ease);

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

            // cos/sin of the twist once, then rotate the precomputed unit circle into place.
            float tc = Mathf.Cos(twist), ts = Mathf.Sin(twist);
            for (int k = 0; k < K; k++)
            {
                float ca = tc * ringCos[k] - ts * ringSin[k];
                float sa = ts * ringCos[k] + tc * ringSin[k];

                // Elliptical, not circular: scaling the two basis directions differently is what
                // gives the section an orientation for the twist to actually show.
                Vector3 off = prevN * (ca * radius) + binormal * (sa * radius * ribbonAspect);
                Vector3 w = centre + off - origin;

                // Bright seam on one side of the ring; the twist carries it around, so the rope
                // reads as winding even where the silhouette is ambiguous.
                float stripe = 1f - ribbonStripe * (0.5f - 0.5f * ca);

                int vi = i * K + k;
                r.verts[vi] = w;
                r.cols[vi] = new Color(1f, 1f, 1f, alpha * stripe);
                r.uvs[vi] = new Vector2((float)k / K, uy);
                lo = Vector3.Min(lo, w); hi = Vector3.Max(hi, w);
            }
        }

        // One-shot ground truth. Two ambiguous visual reports in a row is the signal to measure
        // rather than guess again: this separates "the mesh is not rendering at all" from "it
        // renders but the twist has no visible signal", which need completely different fixes.
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
            Debug.Log($"[VORTEX] tube rings={r.count}/{rings} verts={r.mesh.vertexCount} " +
                      $"boundsSize=({bsize.x:F1},{bsize.y:F1},{bsize.z:F1}) " +
                      $"arc={arc:F1}m twistTail={(ribbonTwistPerMetre * arc):F2}rad " +
                      $"alpha={aMin:F2}..{aMax:F2} rendOn={(mr2 != null && mr2.enabled)} " +
                      $"active={r.obj.activeInHierarchy} layer={r.obj.layer} shader={shader}");
        }

        r.mesh.vertices = r.verts;
        r.mesh.colors = r.cols;
        r.mesh.uv = r.uvs;
        // Set explicitly rather than RecalculateBounds: we already know the extents from the loop.
        r.mesh.bounds = new Bounds((lo + hi) * 0.5f, hi - lo);
    }

    Transform CreateWingtipAnchor(Part wing, bool isRight, HashSet<Part> restrictTo = null)
    {
        // STEP 1: find the TRUE tip — span-based and cant-aware, see TryFindTipVertex
        Vector3 anchorWorld;
        if (!TryFindTipVertex(wing, isRight, out anchorWorld)) return null;

        // STEP 2: climb upward if another part exists above this X/Z position
        // SKIP climb if this is already an extremity wing (prevents rear shifting forward)
        {
            // TryGetPartBounds, not a raw renderer lookup, and this is THE fix for "reverting to
            // launch moves the anchors". This test decides whether the anchor stays on the true
            // tip or climbs to a part above it, by comparing this wing's lateral reach against
            // the widest reach on the aircraft. Read raw, maxExt included whatever effect meshes
            // happened to be enabled on that scene load, so the wingtip could not clear 95% of it
            // — the guard failed, the climb ran, and the anchor was relocated inboard.
            //
            // It is load-dependent because effect renderers are: on the first spawn the guard
            // held and the anchor sat on the tip; after a revert a plume was live, maxExt jumped,
            // and the same wing was suddenly no longer "the extremity". Nothing else in the frame
            // differed — same axes, same mass, same span, same chosen part.
            Bounds rCheckB;
            if (TryGetPartBounds(wing, out rCheckB))
            {
                float maxExt = 0f;
                foreach (var part in vessel.parts)
                {
                    Bounds rrB;
                    if (!TryGetPartBounds(part, out rrB)) continue;
                    float cProj = Vector3.Dot(rrB.center - vesselTransform.position, vesselTransform.right);
                    float eProj = ExtentAlong(rrB, vesselTransform.right);
                    float ext = Mathf.Abs(cProj) + eProj;
                    maxExt = Mathf.Max(maxExt, ext);
                }

                float thisC = Vector3.Dot(rCheckB.center - vesselTransform.position, vesselTransform.right);
                float thisE = ExtentAlong(rCheckB, vesselTransform.right);
                float thisExt = Mathf.Abs(thisC) + thisE;
                Debug.Log($"[VORTEX] CLIMB GATE part={wing.name} ext={thisExt:F2} vs widest={maxExt:F2} "
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
            Debug.Log($"[VORTEX] CLIMB CHECK iter={safety} baseY={baseLocal.y:F2} part={currentPart.name}");

            Part bestAbovePart = null;
            float bestHorizDist = float.MaxValue;
            float bestTopY = float.MinValue;
            float bestArea = 0f;

            foreach (Part p in vessel.parts)
            {
                // The climb may only walk within the assembly this anchor belongs to. Without
                // this it can hop onto a DIFFERENT surface entirely — a short canard sitting
                // under the main wing is close enough laterally to satisfy the search radius, and
                // the canard's anchor would climb onto the wing and draw a second vortex there,
                // which is the very duplication the selection above exists to prevent.
                if (restrictTo != null && !restrictTo.Contains(p)) continue;

                bool isLift = p.Modules.Contains("ModuleLiftingSurface");
                bool isControl = p.Modules.Contains("ModuleControlSurface");

                // HARD FILTER: only real lifting surfaces (this is the missing piece)
                if (!isLift) continue;

                // Never re-select the part we are standing on. Its bounds centre and its transform
                // origin are different points, so without this a part can clear its own "is it
                // higher" test and the climb spins on it until the safety counter stops it.
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

                Debug.Log($"[VORTEX] CLIMB CAND part={p.name} horiz={horizDist:F2} topY={maxLocal.y:F2} centerY={centerLocal.y:F2}");
            }

            if (bestAbovePart != null)
            {
                Bounds b;
                if (!TryGetPartBounds(bestAbovePart, out b)) break;
                Vector3 maxLocal = ToVesselFrame(b.center) +
                                   new Vector3(ExtentAlong(b, axLateral), ExtentAlong(b, axVert), ExtentAlong(b, axLength));

                Debug.Log($"[VORTEX] CLIMB HIT part={bestAbovePart.name} horiz={bestHorizDist:F2} topY={maxLocal.y:F2}");

                currentPart = bestAbovePart;
                // FIX: don't snap to absolute top (causes hovering above surface)
                // instead project slightly BELOW top to sit on surface
                float surfaceY = maxLocal.y - 0.02f; // small offset downward
                Vector3 nextLocal = new Vector3(baseLocal.x, surfaceY, baseLocal.z);
                anchorWorld = FromVesselFrame(nextLocal);
                foundHigher = true;
            }
            else
            {
                Debug.Log($"[VORTEX] CLIMB STOP at part={currentPart.name}");
            }
        }

        // STEP 3: once at the highest part, recompute the tip on THAT part. If it has no usable
        // mesh we keep the STEP 1 tip, which at least sits on real geometry, rather than the
        // point the climb left behind — that one is projected to the top of an axis-aligned
        // bounding box, which for a canted surface is a spot in mid-air above it.
        Vector3 finalTip;
        if (TryFindTipVertex(currentPart, isRight, out finalTip))
        {
            anchorWorld = finalTip;

            // Seat the anchor just inside the surface. Nudging along -vesselTransform.up only
            // works for a level wing; on a canted or near-vertical winglet that direction runs
            // ALONG the surface instead of into it, which left the anchor floating just off the
            // tip. Nudging toward the part's own render centre is cant-agnostic.
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
            dt = 0.01f; // clamp aggressively

        // NOTE: this used to Clear() every trail whenever rawDelta crossed the threshold. A slow
        // frame is not an anomaly — the aircraft legitimately travels further and the resulting
        // segment is long but straight — so that test discarded the entire trail history for
        // the wrong reason while never catching the real cause (see OnFloatingOriginShift). The
        // targeted displacement check now lives in the per-vortex loop below.

        // RECOVERY: ensure renderers come back clean after a bad frame
        for (int i = 0; i < trails.Count; i++)
        {
            // ...except where a tube has taken over: this loop would otherwise re-enable that
            // source's TrailRenderer every frame, forcing a Clear() every frame to undo it.
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

        // VACUUM. This used to return the instant density crossed 0.01, which cut the update
        // loop off in the middle of a fade that atmFactor was already running smoothly — and
        // the two renderers did not survive that cut the same way, which is what made leaving the
        // atmosphere look broken:
        //
        //   A tube had been rebuilt on its last live frame with visible near zero, so its rings
        //   were already closed to no radius and no alpha. It vanished cleanly.
        //
        //   A TrailRenderer had not. ApplyShedAppearance is what walks its recorded shed history
        //   and slides that profile toward the tail as the wake ages, and it stopped being called
        //   entirely. The trail froze at the width and colour of its last live frame and simply
        //   sat there, so the secondary vortices hung in the air after the main ones had gone.
        //
        // There is no fade to special-case: atmFactor already takes visible to zero across the
        // 0.01-0.10 density band, and both renderers close out through their own normal paths
        // when it does. So the early-out now fires only once there is genuinely nothing left to
        // draw, which keeps the cost saving in orbit without ever truncating a fade.
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

        // LIFT-DRIVEN LOAD FACTOR.
        // "A vortex is a direct byproduct of lift... its strength at any instant is set by how
        // much lift the wing is making right then." So the driver has to be lift, not felt G.
        // Those agree in a steady turn, which is why geeForce looked fine, but they come apart
        // exactly where it matters:
        //   - geeForce counts THRUST. A high-TWR craft climbing on engine power alone reads
        //     several G with the wings doing nothing, and used to spawn vortices for it.
        //   - geeForce counts impacts and collisions, which flashed the effect on touchdown.
        //   - geeForce MISSES the case the research calls out by name: spoilers up, lift dumped,
        //     vortices stop essentially instantly while the G reading barely moves.
        // ModuleLiftingSurface.liftForce is KSP's own per-surface aerodynamic lift, recomputed
        // every physics frame, so summing it is the real quantity rather than a proxy for it.
        // Dividing by weight keeps the result a load factor, so every threshold downstream
        // (the 3G onset, the 4.5G canard gate) keeps the meaning it was tuned with.
        float liftKN = 0f;
        for (int s = 0; s < liftSurfaces.Count; s++)
        {
            var ms = liftSurfaces[s];
            if (ms == null) continue;   // Unity null check also catches destroyed parts
            liftKN += ms.liftForce.magnitude;
        }
        float weightKN = vesselMassTons * gravityAccel;
        float rawLiftG = (liftKN > 0f && weightKN > 0.001f)
            ? Mathf.Min(liftKN / weightKN, 15f)
            : Mathf.Min((float)vessel.geeForce, 15f);   // degrade rather than go dark

        // Frame-rate independent exponential smoothing of the measurement (see liftSmoothTime).
        smoothedLiftG = Mathf.Lerp(smoothedLiftG, rawLiftG, 1f - Mathf.Exp(-dt / liftSmoothTime));
        float g = smoothedLiftG;

        // See onsetLoadRef: the thresholds move with rho*V so that constant CIRCULATION, not
        // constant load factor, is what the effect keys off. Unity at sea level / 250 m/s, so
        // everything tuned before this change behaves identically there.
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

        // Circulation model (see massSpanFactor's declaration): mass/span component is cached on
        // the bounds timer, speed component is live. Clamped so it modulates strengths[i] rather
        // than dominating it.
        float circFactor = sizeFactor;

        // REGIME SELECTION. The 8-15 km band used to hand off to LINE mode because the trail
        // module could not survive up there — that was the Krakensbane frame bug, now fixed,
        // so the band goes back to the trail module. Line mode is kept for the extreme-velocity
        // regime, where its reentry width clamp and stress-damped procedural terms still earn
        // their place and where accumulated trail geometry would outrun its own resampling.
        bool shouldEnterLineMode = speed >= lineModeSpeed;
        bool shouldExitLineMode = speed <= lineModeSpeed * 0.85f;   // hysteresis, avoids flicker

        if (!useLineMode && shouldEnterLineMode) { useLineMode = true; for (int h = 0; h < lineHistory.Count; h++) lineHistory[h].Clear(); }
        else if (useLineMode && shouldExitLineMode) { useLineMode = false; for (int h = 0; h < lineHistory.Count; h++) lineHistory[h].Clear(); }

        // 0 below the condensation band, 1 at the top: drives the maneuver-vortex -> contrail
        // transition across trail lifetime, spacing, width, spread and opacity.
        if (contrailBody != vessel.mainBody) RefreshContrailBand(vessel.mainBody);
        float ambientK = (float)vessel.atmosphericTemperature;
        float contrailBlend = Mathf.Clamp01((bodyContrailWarm - ambientK)
                                            / Mathf.Max(bodyContrailWarm - bodyContrailCold, 0.01f));

        // Approach-vapour floor (see humidDensityMin). Every term has to be true at once: deep
        // in the atmosphere, a wing working near its lift limit, AND that wing carrying the
        // aircraft rather than the gear. Cruise fails the second, a high-altitude pass fails the
        // first, and the takeoff roll fails the third — which is the one it used to pass,
        // because the CL proxy is scale-free in speed and says nothing about who holds the weight.
        // Fraction of full sunlight reaching the wake, 0-1, on any body. See nightFloorScale.
        float sunLit = 1f;
        {
            CelestialBody star = (Planetarium.fetch != null) ? Planetarium.fetch.Sun : null;
            double fullFlux = 0.0;
            if (star != null)
            {
                double dSun = (vessel.CoM - star.position).magnitude;
                if (dSun > 1.0)
                    fullFlux = PhysicsGlobals.SolarLuminosity / (4.0 * System.Math.PI * dSun * dSun);
            }
            // Falling back to the raw flux rather than to a constant: if the star cannot be
            // resolved, treating the sky as fully lit is the safer failure than going dark.
            sunLit = (fullFlux > 1.0)
                ? Mathf.Clamp01((float)(vessel.solarFlux / fullFlux))
                : 1f;

            if (!loggedSunFlux)
            {
                loggedSunFlux = true;
                Debug.Log($"[VORTEX] illumination: flux={vessel.solarFlux:F0} full={fullFlux:F0} "
                          + $"sunLit={sunLit:F2} scale={Mathf.Lerp(nightFloorScale, dayLightScale, sunLit):F2}");
            }
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

        // The altitude ceiling is gone with the altitude band. Fading out as you leave the
        // atmosphere is atmFactor's job below, and air density is the physically right reason for
        // the effect to stop — it is also body-relative for free, where a metres-based ceiling
        // could only ever have been correct on one planet.

        lineActivationBlend = Mathf.MoveTowards(lineActivationBlend, useLineMode ? 1f : 0f, lineActivationSpeed * dt);
        float trailFade = 1f - lineActivationBlend;
        float lineFade = lineActivationBlend;

        // Guard reference speed: the faster of this frame and last. AddExcess() zeroes the
        // vessel's Unity-space velocity on the frame Krakensbane engages, so rb_velocity alone
        // — read after that happens — would understate the motion that frame.
        float rbSpeed = vessel.rb_velocity.magnitude;
        float guardSpeed = Mathf.Max(rbSpeed, lastRbSpeed);
        lastRbSpeed = rbSpeed;

        // True "away from the planet" direction, not the vessel's own belly-to-canopy axis.
        // vessel.transform.up tracks aircraft ATTITUDE, so during a roll or inverted flight it
        // points sideways or straight down — wrong for anything meant to be gravity-relative
        // (downwash, wake curvature, sink). A shed vortex settles toward the planet regardless
        // of which way the aircraft that made it happens to be pointed right now.
        Vector3 worldUp = (Vector3)vessel.upAxis;

        // Advance the shed-history cursor on a fixed cadence. The current bucket is overwritten
        // with live intensity every frame, so the head of the curve is never a stale sample.
        shedSampleTimer += dt;
        if (shedSampleTimer >= shedSampleInterval)
        {
            shedSampleTimer = 0f;
            shedWrite = (shedWrite + 1) % shedSamples;
        }

        // PERSISTED-WAKE SINK. A shed vortex pair descends under its own induced flow — a few
        // hundred ft/min (~1.5 m/s) early on, slowing as it ages. Nudging every stored vertex
        // down each frame gives "older has sunk further" for free, since a point that has existed
        // longer has simply received more nudges; the per-vertex rate falloff supplies the
        // "slowing as they age" half.
        //
        // Independent of OnFloatingOriginShift: both are pure translations of the same vertices,
        // so they commute in any order, and the discontinuity guard measures the ANCHOR transform,
        // which this never touches. Set trailSinkRate to 0f to compile it out at runtime.
        // How hard the wake is winding, from load factor. Keeps a floor at helixBaseDrive so a
        // 1G cruise still shows some structure rather than nothing at all, and opens to full by
        // helixGFull. Faded out toward the contrail band, where a condensation trail is straight.
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

                    // Which end is newest is determined at runtime rather than assumed, so this
                    // does not rely on TrailRenderer's undocumented index ordering: the newest
                    // point is whichever end is still sitting at the emitter.
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
                            p -= worldUp * (rate * sinkDt);
                        }

                        if (doHelix)
                        {
                            // Radius grows with the vertex's OWN age, so it is exactly zero where
                            // the trail meets the anchor and opens out behind it.
                            float age = Mathf.Lerp(hAge, tAge, ageFrac);
                            float amp = helixRadiusMax * helixDrive;
                            float rNow = amp * Mathf.Clamp01(age / helixGrowTime);
                            float rNext = amp * Mathf.Clamp01((age + sinkDt) / helixGrowTime);
                            float dR = rNext - rNow;

                            // Only vertices still opening out need touching; past helixGrowTime the
                            // radius has saturated, dR is zero, and the trig is skipped entirely.
                            if (dR > 0f)
                            {
                                // Phase FROZEN per vertex. (now - age) is the instant this vertex
                                // was laid down, which does not change as it ages, so the corkscrew
                                // sits still in the air instead of spinning — and the per-frame
                                // delta stays purely radial and small. A rotating phase could not
                                // work at a visible wavelength: omega would be ~30 rad/s, roughly
                                // half a turn per frame, which aliases into noise.
                                float th = helixOmega * (nowT - age) + phase;
                                p += (hPerp * Mathf.Cos(th) + worldUp * (Mathf.Sin(th) * 0.6f)) * dR;
                            }
                        }

                        strail.SetPosition(sj, p);
                    }

                    // GROUND TRUTH. Measures how far the trail actually bows away from the straight
                    // chord across its first 60 points. If the helix is accumulating, this lands
                    // near the commanded amplitude; if the per-frame deltas are cancelling each
                    // other out (which they would if the age-per-vertex estimate is noisy enough to
                    // scramble the phase), it reads ~0 no matter what amplitude is commanded. That
                    // distinguishes "not enough amplitude" from "the scheme cannot accumulate".
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
                        Debug.Log($"[VORTEX] helix check g={g:F2} drive={helixDrive:F3} " +
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

            float visible = intensity * strengths[i] * circFactor;

            // Applied BEFORE the light and density multipliers, unlike the contrail floor, so a
            // night landing does not glow and the floor still dies out with the air.
            if (approachFloor > 0f)
                visible = Mathf.Max(visible, approachFloor * strengths[i]);

            // See nightFloorScale. dayLightScale is deliberately 0.75 rather than 1.0: with the
            // normalisation fixed, full daylight would otherwise jump from the 0.68 the old
            // hard-coded divisor happened to produce on Kerbin straight to 1.0, brightening every
            // daytime wake by about a third as a side effect of fixing the night. 0.75 keeps the
            // daylight look as tuned and makes the constant explicit instead of accidental.
            float nightScale = Mathf.Lerp(nightFloorScale, dayLightScale, sunLit);
            visible *= nightScale;

            float atmFactor = Mathf.Clamp01((float)((vessel.atmDensity - 0.01f) / 0.09f));
            visible *= atmFactor;

            // Cold air is already near saturation, so a contrail forms without needing a strong
            // core: the floor here is what makes the effect default-on up high and G-gated down
            // low. That split is real, and now it is drawn by temperature rather than by height.
            // Scaled by atmFactor, which the floor previously bypassed because it is applied
            // with Max(). That did not matter while the vacuum early-out was a hard cliff, but it
            // does now: ambient temperature above the atmosphere is cold, so contrailBlend goes
            // to 1 there, and an unscaled floor would relight every vortex at half strength in
            // orbit. No air, no condensation — whatever the temperature says.
            //
            // Scaled by nightScale for the same class of reason: no light, nothing to scatter.
            // This was the one path in the frame that ignored the sun, which is why contrails
            // stayed bright on the night side while everything else went dim.
            if (contrailBlend > 0f)
            {
                visible = Mathf.Max(visible,
                                    Mathf.Lerp(0.1f, 0.5f, contrailBlend)
                                    * strengths[i] * atmFactor * nightScale);
                visible *= Mathf.Lerp(1f, 1.5f, contrailBlend);
            }

            if (strengths[i] < 1f)
            {
                // Onset interpolated by span ratio: a full-size canard is held to nearly the main
                // wing's 3g, a small nose canard to 6g. See the field comments for why that is not
                // a fudge — the two are genuinely different aerodynamic cases.
                // Scaled by the same rho*V factor as the main wing, so the tightening the
                // secondary gate was given still holds exactly where it was tuned (cruise) while
                // a slow approach is judged on circulation rather than on raw g.
                //
                // ...and then blended toward the MAIN WING's own onset by tip likeness, so a
                // surface that reaches the outboard end of the span is judged as the wingtip it
                // is. At full likeness this is exactly the main-wing rule: onset onsetLoadRef,
                // no secondary gate at all.
                float mainLike = MainLikeness(spanRatios[i]);
                float gMin = Mathf.Lerp(Mathf.Lerp(secondaryGMinSmall, secondaryGMinLarge, spanRatios[i]),
                                        onsetLoadRef, mainLike) * onsetScale;
                // AND, not MAX: load AND airspeed together. One rule at every altitude, so there
                // is no band where the requirement quietly disappears.
                float gGate = Mathf.Clamp01((g - gMin) / Mathf.Max(secondaryGSpan * onsetScale, 0.01f));
                float vGate = Mathf.Clamp01((speed - secondarySpeedMin) / Mathf.Max(secondarySpeedFull - secondarySpeedMin, 0.01f));
                visible *= Mathf.Lerp(gGate * vGate, 1f, mainLike);
            }

            // Record what is being shed right now. ApplyShedAppearance replays this along the
            // trail so each stretch of it renders at the strength it was actually born with.
            shedHistory[i][shedWrite] = visible;

            Vector3 anchorPos = anchor.position;

            // DISCONTINUITY GUARD — the distance-based replacement for the old frame-time test.
            // Origin shifts are already compensated in OnFloatingOriginShift, so anything left
            // here is a real teleport (vessel switch, docking, kraken): the anchor moved further
            // than its own Unity-space velocity can account for over the elapsed time.
            // rb_velocity is the Krakensbane-relative velocity, which is precisely what carries
            // the anchor through Unity world space, so this bound stays correct whether or not
            // the velocity frame is engaged — unlike true surface velocity, which would flag
            // every frame above the altitude gate.
            float armRadius = Vector3.Distance(anchorPos, vessel.CoM);
            float plausibleStep = (guardSpeed + vessel.angularVelocity.magnitude * armRadius)
                                  * rawDelta * 3f
                                  + Mathf.Max(5f, vesselSize);
            Vector3 anchorStep = anchorPos - lastAnchorPositions[i];
            if (anchorStep.sqrMagnitude > plausibleStep * plausibleStep)
            {
                Debug.Log($"[VORTEX] anchor teleport {anchorStep.magnitude:F1}m > plausible {plausibleStep:F1}m, clearing trail {i}");
                tr.Clear();
                history.Clear();
                trailAges[i] = 0f; trailHeadAges[i] = 0f;
            }
            lastAnchorPositions[i] = anchorPos;

            // STABILITY: clamp history growth on unstable frames
            history.Enqueue(anchorPos);
            while (history.Count > historyLength)
                history.Dequeue();

            // The emitter sits EXACTLY on the anchor and nothing displaces it, so the trail is
            // welded to the wingtip. The helix that used to live here now acts on stored vertices
            // instead (see the wake-evolution loop above), where it can start at zero and open out
            // with distance rather than swinging the origin around.
            obj.transform.position = anchorPos;
            lastFlows[i] = flow;

            // WHERE A TUBE EXISTS IT OWNS THE SOURCE OUTRIGHT — trail AND line.
            //
            // The trail was already suppressed (the tube block below clears and disables it), but
            // LINE MODE was not, and that is what was visible growing to full length on the way
            // down before the tube appeared. The chain: re-entry runs well past lineModeSpeed, so
            // trailFade goes to 0, so shouldUseTrail goes false — and the tube's own point
            // feed was gated on shouldUseTrail, so the tube was starved and aged out while the
            // LineRenderer drew instead. Dropping back under the hysteresis speed then handed the
            // rope over, which read as one renderer growing and another replacing it.
            //
            // Line mode exists because accumulated TrailRenderer geometry outruns its own
            // resampling at extreme speed. That reasoning does not transfer to the tube: ring
            // spacing already scales with speed against a fixed ring budget, and ribbonMaxLength
            // bounds the rope outright. So a tube source never needs the handover at all.
            bool hasRibbon = i < ribbons.Count && ribbons[i] != null;
            bool shouldUseLine = !hasRibbon && lineFade > 0.01f && visible > 0.01f;
            bool shouldUseTrail = !hasRibbon && trailFade > 0.01f && visible > 0.01f;

            // The tube is fed by visibility alone. No trailFade term, so the line-mode blend can
            // no longer starve it.
            bool ribbonLive = hasRibbon && visible > 0.01f;

            // Hoisted out of the trail block below, which no longer runs for a tube source. The
            // tube took its lifetime from tr.time, and tr.time was only ever updated in there —
            // so left where it was, a tube source's lifetime would freeze at whatever the trail
            // last happened to set.
            float stability = Mathf.Clamp01((abnormalFrameThreshold - rawDelta) / abnormalFrameThreshold);
            float targetTime = Mathf.Lerp(Mathf.Lerp(maneuverTimeMin, maneuverTimeMax, stability),
                                          Mathf.Lerp(contrailTimeMin, contrailTimeMax, stability),
                                          contrailBlend);

            // ── TRAIL ─────────────────────────────────────────────────
            if (shouldUseTrail)
            {
                if (lineWasActive[i])
                {
                    lr.enabled = false; lr.positionCount = historyLength;
                    history.Clear(); tr.Clear(); tr.time = 1.5f; trailAges[i] = 0f; trailHeadAges[i] = 0f;
                }
                tr.enabled = true; tr.emitting = true;

                // Low altitude: a short maneuver puff. High altitude: a long-lived condensation
                // trail. Vertex count is governed by frame rate * lifetime (a TrailRenderer adds
                // at most one point per update), so a 5 s trail at 60 fps is ~300 points per
                // vortex — which OnFloatingOriginShift then rewrites every physics frame.
                // That budget is what contrailTimeMax is set against. (stability and targetTime
                // are computed above, so a tube source gets them too.)
                tr.time = Mathf.Lerp(tr.time, targetTime, smoothDt * 2f);

                // Widen vertex spacing with altitude: a contrail is close to straight, so fine
                // sampling buys no shape and costs shift work on every point it adds.
                float targetMinDist = Mathf.Lerp(Mathf.Lerp(0.35f, 0.12f, stability),
                                                 contrailMinVertexDist, contrailBlend);
                tr.minVertexDistance = Mathf.Lerp(tr.minVertexDistance, targetMinDist, smoothDt * 3f);

                // IMPORTANT: never fully stop emission during bad frames (causes forward shooting bug)
                tr.emitting = true;

                trailWasActive[i] = true; lineWasActive[i] = false;
            }
            else { tr.emitting = false; }

            // TUBE. Where one is active it replaces the trail for that source outright — both
            // drawing the same rope would just read as a doubled effect.
            if (hasRibbon)
            {
                var rb = ribbons[i];
                // Genuinely a one-shot now: the trail block above is skipped for a tube source,
                // so nothing re-enables it on later frames.
                if (tr.enabled) { tr.Clear(); tr.enabled = false; }

                float rNorm = Mathf.Clamp(vesselSize / 30f, 0.2f, 1f);
                float rScale = 1f + Mathf.Pow(rNorm, 2.2f) * 2.0f;
                float now = Time.time;

                // Same lifetime curve the TrailRenderer uses, so a tube and a trail on the same
                // aircraft stay the same length under the same conditions — but read from
                // targetTime directly rather than from tr.time, which no longer updates here.
                // Then bounded by ribbonMaxLength so a hypersonic re-entry cannot draw a 10 km
                // rope now that line mode no longer takes over to prevent it.
                float life = Mathf.Max(Mathf.Min(targetTime, ribbonMaxLength / Mathf.Max(speed, 1f)), 0.5f);
                while (rb.count > 0 && (now - rb.birth[rb.count - 1]) > life) rb.count--;

                // Ring spacing widens as needed so that a full lifetime of wake still fits inside
                // the ring budget — otherwise the tube is cut short by running out of points
                // long before it runs out of time. Now that the rope is close to straight, coarse
                // spacing costs nothing: there is no curve left to under-sample.
                float spacing = Mathf.Max(ribbonMinPointDist,
                                          (speed * life) / Mathf.Max(ribbonMaxPoints - 4, 1));
                float flowDepth = ribbonFlowDepth * contrailBlend;
                if (ribbonLive)
                {
                    if (rb.count == 0) PushRibbonPoint(rb, anchorPos, now, visible, flowDepth);
                    else
                    {
                        Vector3 last = rb.pts[0];
                        float gap = Vector3.Distance(anchorPos, last);
                        int steps = Mathf.Clamp(Mathf.CeilToInt(gap / spacing), 1, 8);
                        for (int sub = 1; sub <= steps; sub++)
                            PushRibbonPoint(rb, Vector3.Lerp(last, anchorPos, (float)sub / steps), now, visible, flowDepth);
                    }
                }
                BuildRibbonMesh(rb, now, rScale * Mathf.Lerp(1f, contrailWidthScale, contrailBlend), contrailBlend, life);
            }

            // Width and colour are applied whether or not we are still shedding: a trail that has
            // stopped emitting still has to draw the history it is holding while that ages out.
            // startWidth/endWidth are deliberately no longer used — see ApplyShedAppearance.
            if (shouldUseTrail || tr.positionCount > 0)
            {
                float sizeNormT = Mathf.Clamp(vesselSize / 30f, 0.2f, 1f);
                float ss = 1f + Mathf.Pow(sizeNormT, 2.2f) * 2.0f;
                float altScale = Mathf.Lerp(1f, contrailWidthScale, contrailBlend);
                ApplyShedAppearance(i, tr, trailFade * ss * altScale, contrailBlend, speed);
            }
            // Oldest live point keeps aging until it hits tr.time and starts expiring. The
            // NEWEST one is pinned at age 0 only while we are still shedding — once the wing
            // unloads and emission stops, the head ages too, and the whole recorded profile has
            // to slide toward the tail rather than staying frozen where it was.
            // A trail that has fully aged out holds no geometry at all, so its recorded age span
            // has to reset with it. Without this, coming back down into the band would map a full
            // lifetime of shed history across a brand-new stub of trail, and the effect would
            // return wearing the width and opacity it had when it left rather than building up.
            if (tr.positionCount == 0) { trailAges[i] = 0f; trailHeadAges[i] = 0f; }

            trailAges[i] = Mathf.Min(trailAges[i] + dt, Mathf.Max(tr.time, 0.01f));
            trailHeadAges[i] = shouldUseTrail ? 0f : Mathf.Min(trailHeadAges[i] + dt, Mathf.Max(tr.time, 0.01f));

            // ── LINE ──────────────────────────────────────────────────
            if (shouldUseLine)
            {
                lr.enabled = true;
                if (trailWasActive[i])
                {
                    tr.Clear(); tr.emitting = false; tr.enabled = false; trailAges[i] = 0f; trailHeadAges[i] = 0f;
                    history.Clear(); lr.positionCount = historyLength;
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

                    // Restored from 0.4.0 (dropped in 0.5.0): helical vortex core with Perlin
                    // amplitude and frequency jitter. Damped by stressFactor for the same reason
                    // the warp term is — deformation should calm down, not intensify, exactly
                    // when the geometry is least stable.
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
                // (via `visible`) takes it out, instead of the scale itself jumping 3.0 -> 1.0.
                float lineScale = (contrailBlend > 0f) ? Mathf.Lerp(1.5f, 3f, contrailBlend) : 1f;
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

    // Bakes the RECORDED shedding strength along the trail instead of stamping one global width
    // and alpha across the whole thing.
    //
    // This is the behaviour the research is most specific about: circulation is set by the lift
    // the wing is making right now, so when the pilot unloads, the newly shed vortex collapses
    // immediately — but the vortex shed a second ago is already a free, detached structure and
    // keeps the strength it was born with until it decays on its own terms.
    //
    // A TrailRenderer cannot express that through startWidth/endWidth, because those are global:
    // every frame they are re-applied across the ENTIRE trail, so letting them fall retroactively
    // thins geometry that was shed under 7G. What it DOES give us is widthCurve and colorGradient,
    // both parameterised 0 (head, at the wing) to 1 (tail, oldest). Driving those from a ring
    // buffer of past intensity makes each stretch of the trail render at its own shed strength.
    //
    // The one approximation: the curve is parameterised along the trail, and we map that to AGE.
    // Those coincide at constant speed and skew slightly under hard acceleration.
    // Reads the ring buffer `back` samples behind the write cursor.
    float SampleShed(float[] hist, int back)
    {
        int idx = ((shedWrite - back) % shedSamples + shedSamples) % shedSamples;
        return Mathf.Max(hist[idx], 0f);
    }

    // Shed strength at normalised position u along the trail, interpolated between adjacent
    // history buckets so the profile does not step as the write cursor advances.
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

        // The age span the CURRENT geometry actually covers, which is NOT [0, tr.time]:
        //   - straight after a Clear the trail holds only a fraction of a second, so mapping u=1
        //     to a full tr.time would read history this geometry never lived through;
        //   - once the wing unloads and emission stops, the head is no longer "now" either. It
        //     ages with everything else, so the span becomes [headAge, tailAge] and the strong
        //     stretch shed under high G slides toward the tail and expires, instead of sitting
        //     frozen at a fixed position along the trail. That migration IS the "it hangs in the
        //     air behind you and decays on its own" behaviour, so the mapping has to carry it.
        float maxHist = shedSamples * shedSampleInterval;
        float headAge = Mathf.Clamp(trailHeadAges[i], 0f, maxHist);
        float tailAge = Mathf.Clamp(Mathf.Min(trailAges[i], tr.time), 0.05f, maxHist);
        if (tailAge < headAge) tailAge = headAge;

        float tailRatio = Mathf.Lerp(0.25f, 1.5f, contrailBlend);   // >1: contrails SPREAD with age
        float fadePow = Mathf.Lerp(1.6f, 3.0f, contrailBlend);      // ...and hold opacity longer

        // How much of the CURVE that roll-up distance corresponds to. The trail's live length is
        // its speed times the age span it currently covers, so the same few metres of taper stays
        // a few metres whether the trail behind is 50 m or 2.5 km.
        float rollupMetres = Mathf.Clamp(vesselSize * rollupSizeScale, rollupMinMetres, rollupMaxMetres);
        float trailMetres = Mathf.Max(speedMps * Mathf.Max(tailAge - headAge, 0.01f), 0.01f);
        float rampFrac = Mathf.Clamp(rollupMetres / trailMetres, 0.0002f, 0.5f);

        // WIDTH — keys placed explicitly, most of them INSIDE the ramp. A power distribution
        // cannot help here: once the ramp is a fraction of a percent of the trail, no fixed
        // spacing lands enough keys inside it, so the taper falls between samples and vanishes.
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

            widthKeys[k] = new Keyframe(u, shed
                                           * Mathf.Lerp(1f, tailRatio, u)
                                           * Mathf.Lerp(headWidthFraction, 1f, ramp));
        }

        // ALPHA — untouched: uniform, 8 keys (Gradient's limit). Kept separate on purpose so
        // this change alters silhouette only and leaves colour and vibrance exactly as tuned.
        for (int k = 0; k < shedKeyCount; k++)
        {
            float u = (float)k / (shedKeyCount - 1);
            float shed = ShedAt(hist, u, headAge, tailAge);
            shedAlphaKeys[k] = new GradientAlphaKey(
                Mathf.Clamp01(shed * trailAlphaGain * (1f - Mathf.Pow(u, fadePow))), u);
        }

        shedCurve.keys = widthKeys;
        tr.widthCurve = shedCurve;
        tr.widthMultiplier = 0.2f * widthScale;

        // Reasserted every frame rather than once at creation: cheap, and it removes cap vertices
        // as a suspect if the end still renders square.
        if (tr.numCapVertices != trailCapVertices) tr.numCapVertices = trailCapVertices;

        // One-shot ground truth. Reasoning about which end of a TrailRenderer's width curve is the
        // head has been guesswork; this reports what the renderer actually holds, so if the end is
        // still square we can tell "the curve is wrong" from "the curve never arrived".
        // Gated on an ESTABLISHED, actually-visible trail. 0.6.12's version fired two seconds
        // after load on a five-point stub whose shed history was all zero, which told us nothing.
        if (!loggedWidthProfile && i == 0 && tr.positionCount > 40 && widthKeys[widthKeyCount - 1].value > 0.05f)
        {
            loggedWidthProfile = true;
            var wc = tr.widthCurve;
            Debug.Log($"[VORTEX] width profile keys={wc.length} mult={tr.widthMultiplier:F3} " +
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

        public AeroCandidate(Part p, Bounds b, bool left, float ext, float zc, float halfZ)
        {
            part = p; bounds = b; isLeft = left; extremity = ext;
            z = zc; zLo = zc - halfZ; zHi = zc + halfZ;
        }
    }
}
