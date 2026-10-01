# Design notes

[← Back to the README](../README.md)

The reasoning behind the code, moved out of the source comments so the files stay short. It is the original comment text, kept as written, grouped by file and in file order. Each entry is headed by the declaration it was written above. Version numbers mentioned are the releases the notes refer to.


---

## WingtipVortex.cs

### `public class WingtipVortex : MonoBehaviour`

One per craft, created and destroyed by WingtipVortexManager (bottom of this file) and bound to
that craft for its whole life. Switching vessels therefore touches no wake: the craft you leave
keeps drawing its own, and the one you take over already has one.

### `private static Material sharedTrailMat;`

MAGENTA IS UNITY SAYING "NO MATERIAL". These are static, so they outlive the addon but not
the scene: a Material created with `new Material(...)` and never marked DontDestroyOnLoad is
destroyed on scene load, and the static reference then holds a destroyed object. Start()
checked for that, but nothing else did — and since 1.1.16 sources are also built from
the vessel-switch path in Update(), which never touched the materials.

Rockets are where it shows because they are the only sources with no tube: since 1.1.5
every aircraft source renders through a MeshRenderer, and a trail that never draws cannot
look wrong. 1.1.18 then kept boosters on the trail for their whole ascent instead of handing
them to line mode, which is what finally put a long-dormant renderer back on screen.

Fetched through accessors that rebuild on demand, with shader fallbacks, so a missing
shader degrades to a working material instead of to magenta — and says so in the log.

### `private List<Part> anchorRoots`

RIGID ANCHORS. KSP joins parts with springy joints, and under a hard pull at high dynamic
pressure a wing built from dozens of parts wobbles up and down at a few Hz, metres at the
tip on a large craft. Read straight off the part, that put the rope's head on a vertical
wave ~50 m long at jet speed: too long for the ring smoothing to remove, so it drew a
zigzag seen from the side and invisible from above. A real vortex core does not record a
wing's flutter, and KSP's joint wobble is far larger than real wing bending anyway.

So each anchor's offset is captured in the ROOT PART's frame when the source is made, and
the wake follows that point on the rigid airframe: every real motion of the aircraft
(pitch, roll, the flight path itself) and none of the flexing. Falls back to the part's own
position if the root has changed or the two disagree by more than a part flexes (a part
shot off, or a stale capture), so a bad capture can never displace a wake.

### `private List<float> spanRatios`

Each source's lateral reach as a fraction of the main wing's. Drives how much load a
secondary surface must carry before its vortex shows — see the gate in Update().
(This slot used to hold a per-source area that nothing ever read.)

### `private bool enableRibbonMesh`

── WAKE TUBE (procedural mesh) 
Generated geometry instead of a TrailRenderer, because a TrailRenderer billboards to the
camera and therefore has no orientation to twist — it can snake, but it can never read
as a rotating rope. Owning the mesh is the only way to get a cross-section that rotates
about the centreline.

A TUBE rather than a single twisted ribbon on purpose: a lone ribbon goes edge-on twice per
revolution and visually disappears, which reads as a beaded, flickering rope.

### `private bool ribbonMainOnly`

MAIN WING ONLY. Secondary surfaces keep their TrailRenderer, and not just to save frames:
the inward curve models a free counter-rotating PAIR converging under its own induced flow,
and a canard's vortex does not get to do that. It passes straight over the main wing and is
entrained into the wing's own vortex system, so drawing it as an independent, cleanly
converging rope actively misrepresents it. The amplitude is scaled by shed strength but not
by span either, so 1.2 m of convergence off a 0.6 m-extremity winglet would be absurd.
Was true from 0.6.31, which put secondaries on the TrailRenderer. The reasoning then was
aerodynamic: the tube's inward curve models a free counter-rotating PAIR converging under
mutual induction, and a canard's vortex is entrained into the main wing's system instead of
doing that. That is still true of the CURVE.

But it is not a reason to make a secondary a different KIND of object, and it turned out to
cost more than it bought. The trail's width is a flat 0.2 * widthScale with shed strength
only reaching the width CURVE and the colour alpha, so a gated-down secondary reads as a
thin constant smear. The tube's radius is directly proportional to shed strength, so the
same numbers become geometry you can actually see respond.

Switched off: every source gets a tube. The curve objection handles itself — the
inward displacement is already ribbonInwardAmp * r.shed[src], so a secondary gated down to
a fifth of full strength curves a fifth as far and never pretends to be half of a freely
converging pair.

### `private float ribbonMaxLength`

Hard cap on how long the rope is allowed to get, in METRES. Line mode used to be what
stopped a re-entry wake from running away, and the tube no longer hands over to it, so the
tube needs its own limit. A time-based lifetime alone does not give one: at 2 km/s a 5 s
rope is 10 km long.

Expressed as length rather than as a speed-dependent lifetime fudge because length is the
thing that actually matters visually. Below ~500 m/s the lifetime binds first and nothing
changes; above it the rope simply stops getting longer.

### `private int ribbonSmoothWindow`

WAKE SMOOTHING. A real vortex core is a fluid structure with inertia and viscosity: it does
not reproduce every twitch of the wingtip that made it, and what high-frequency detail it
does inherit diffuses out of it within moments. The wake here had no such property — it
recorded the anchor's position exactly, so an airframe hunting under SAS or flying
off-prograde wrote every oscillation into the rope as a permanent sawtooth. That is why it
looks right again the moment the craft settles: the mod was never adding the zig-zag, it
was faithfully recording one.

Laplacian smoothing over a WINDOW near the head, one pass per frame. Two properties matter:

  - Index 0 is never touched, so the head stays welded to the tip. Smoothing the input
    instead would have lagged the head, and at 300 m/s even 0.1 s of lag is 30 m of rope
    detached from the wingtip.
  - The window is bounded, so each ring is smoothed a fixed number of times as it ages out
    of it and then never again. Smoothing the whole rope every frame would compound into
    hundreds of passes and shrink the curve away entirely.

Attenuation per pass is 1 - amount*(1 - cos(2*pi/N)) for a wavelength of N rings, so a
one-ring alternation (N=2) dies in a single pass while genuine metre-scale shape survives.

### `private int ribbonMaxPoints`

200 was not enough at altitude: contrail lifetime runs to 5 s, and a ring is committed
once per frame, so at 200 m/s and 60 fps the buffer held 660 m while the lifetime wanted
1000 m. The buffer bound instead of the lifetime and the rope was cut short.

### `private float ribbonAspect`

The cross-section has to be ASYMMETRIC or the twist has nothing to display against.
0.6.24 rotated a regular polygon at constant radius about its own centre, which maps it
onto itself — the twist moved every vertex and changed the silhouette, the shading and
the outline not at all. Mechanism correct, no visible signal; the same class of mistake as
the helix wavelength.

ribbonAspect flattens the section into an ellipse, so rotating it visibly ROLLS the rope,
and unlike a flat ribbon the minor axis never reaches zero so there are no edge-on dropouts.
ribbonStripe brightens one side of the ring; under additive blending that reads as a bright
seam winding along the rope, which is close to what real vortex vapour looks like.

### `private float ribbonHelixAmp`

CENTRELINE HELIX. The measured reason nothing looked like it was spiralling: the tube is
~0.3 m across on a ~40 m wake, so from a chase camera its cross-section is one or two pixels
wide. Rotating a one-pixel-wide cross-section is invisible no matter how elliptical or how
striped it is — which is why ribbonAspect and ribbonStripe changed nothing, and why the
twist looked identical to no twist.

What is visible at that viewing distance is the ROPE ITSELF moving, by metres. So the helix
goes on the centreline, not the cross-section. That failed against a TrailRenderer because
it needed per-vertex age inferred from an index and accumulated per-frame deltas whose
phase drifted; here the arc length of every ring is exact and the whole curve is rebuilt
from scratch each frame, so there is nothing to drift.
Spiral OFF. Real wingtip ropes are close to straight — what the reference photos show
is two nearly parallel lines that converge slightly and then run flat, not a corkscrew.
The machinery stays, gated on this being > 0, in case it is ever wanted again.

### `private float ribbonInwardAmp`

INWARD CURVE. The rope bends toward the aircraft centreline over the first
ribbonInwardGrow metres and then runs parallel — the "flow inwards slightly before
evening out" shape. This is also what really happens: the core rolls up inboard of the
geometric tip, and the counter-rotating pair induces a slight mutual convergence.

The direction is frozen in world space at birth, exactly like the helix phase was, so shed
geometry cannot be re-aimed by the aircraft manoeuvring afterwards.

### `private float ribbonHelixGrow`

25 m left the leading 25 m of rope near-straight and stuck to the wingtip before the
spiral opened — that flat leading section was the "follows the wingtip" part. 8 m keeps
the anchor weld without a long dead run in front of it.

### `private float ribbonPeakFraction`

FALLOFF SHAPE ALONG THE ROPE. Two things conspired to make the wake read as uniformly
white for most of its length, and neither was the fade curve being too slow.

First, trailAlphaGain is 1.25 and a main-wing shed strength sits near 1.0, so
Clamp01(shed * gain * fade) SATURATES: the product stays above 1 until the fade term has
already fallen to 0.8, and everything before that renders as a flat plateau at pure white.
The curve was doing its job; the clamp was throwing away the first fifth of it.

Second, 1 - ageFrac^2 is deliberately flat at the start — that is what a squared term
does — so even unclamped it barely moves over the first third of the rope. Between them,
roughly the first 60% was indistinguishable.

Replaced with an explicit peak window plus a taper over the remainder, which is both what
was asked for and closer to the physics: the core is tightest and brightest right at the
tip, and condensation thins out progressively as the core diffuses and pressure recovers.
ribbonPeakFraction is the age at which the fade begins; the rest fades across the whole
remaining length rather than cramming the transition into the last third. The head ramp
below now supplies the variation near the tip that the old short peak window did.

### `private float ribbonHeadRampFraction`

HEAD RAMP. The rope used to leave the anchor already at peak alpha: a thin thread, but a
fully white one, which read as a line stuck onto the wingtip rather than vapour forming
behind it. Condensation in a real tip vortex starts wispy and thickens as the core rolls up
and its pressure drop deepens, and the opacity of a condensed tube goes with the path
length through it — its width — so a thread should also be faint. Both now rise from near
nothing over the first ribbonHeadRampFraction of the rope, alpha with an ease-out so the
rope is readable before it is wide. Keyed to position in the buffer for the same reason
as ribbonEndFade. ribbonPeakFraction sits just past the ramp so the brightest stretch is
mid-rope, not a plateau from the tip.

### `private float ribbonEndFade`

Fraction of the rope over which the tail closes to nothing. Keyed to POSITION IN THE
BUFFER, not to age, and that distinction is the fix: once the ring budget is what limits
the rope (rather than lifetime), the oldest ring's age never reaches `life`, so an
age-based fade never reaches zero and the rope ends on a blunt, still-visible face. A new
ring pushes the oldest out every frame, so that blunt end jumps forward — which is what
read as the end being dragged along behind the aircraft. Position-in-buffer always closes,
whichever constraint happens to bind.

### `private float ribbonFlowDepth`

LONGITUDINAL FLOW STRUCTURE, and the reason the rope reads as towed rather than streaming
once the contrail band is reached. Down low, `visible` tracks G, so brightness varies along
the rope and that variation sits still in the air while the aircraft pulls away from it
— which is the whole cue that says "this is being left behind". Up high the altitude
floor pins `visible` near-constant, every ring comes out identical, and a uniform straight
line translating along its own axis has no visible motion at all. Nothing was actually
dragging; there was simply nothing on the rope to see moving.

Sampled against the birth odometer, so the pattern is frozen in the wake exactly like the
helix phase and the inward direction, and streams backward for free. Scaled by
contrailBlend so the low-altitude look, which already has G-driven variation, is untouched.

### `private float[] ringCos, ringSin;`

Unit-circle table for the cross-section, built once. Every ring needs cos/sin of
(twist + 2*pi*k/K); with the table only cos/sin of the twist itself are computed and the
per-vertex values come from an angle addition. That is 2 transcendentals per ring instead
of 2*K, which matters now that four tubes rebuild every frame rather than one.

### `private float buildSpeed`

Both fast, deliberately. Circulation is set by the lift the wing is making RIGHT NOW, and
collapses with it — the research is explicit that the newly shed vortex weakens the
instant the pilot unloads, the same way it stops when spoilers dump lift on landing. These
two numbers govern only what is being shed at the wing this frame.

0.6.4 slowed decay to 0.22 to try to make the wake persist. That was the wrong lever: it
persisted by refusing to let the INSTANTANEOUS strength fall, which smeared the collapse
across the whole trail instead. Persistence is now a property of the recorded trail
history (see ApplyShedAppearance), which is where it physically belongs, so instantaneous
response goes back to tracking lift closely.

### `private float contrailTempWarm`

HIGH-ALTITUDE CONDENSATION REGIME.
The trail module now owns the 8-15 km band that line mode used to. It could not before:
above Krakensbane's altitude gate the recorded trail vertices were drifting metres per
physics frame (see OnFloatingOriginShift), so the band was handed to a renderer that
rebuilds from scratch every frame and therefore could not drift. With the frame fix in
place the trail is the better tool up there — a condensation trail IS accumulated
world-space geometry, which is what a TrailRenderer stores and what a LineRenderer only
imitates procedurally.
BODY-RELATIVE, not altitude. 8000-15000 m only ever meant anything on Kerbin: altitude is
a property of the craft, and says nothing about whether there is air outside it. What
actually decides whether a persistent contrail forms is that the ambient air is already
near saturation, i.e. cold enough — which is why the effect being G-gated down low and
essentially default-on up high is correct, and it is TEMPERATURE, not height, that draws
that line.

Keying off vessel.atmosphericTemperature makes it fall out per planet with no per-body
tuning: Eve is hot and deep, so its band sits very high; Duna is cold and thin, so the
band is low but the density gate keeps it faint; Laythe lands in between.
The anchors are the real atmospheric-science numbers rather than tuned ones: persistent
contrails need the air at or near ice saturation, which on Earth means roughly -40 C
(233 K, where homogeneous ice nucleation is certain) at the cold end and around -20 C
(253 K) as the warm end below which it essentially never happens. Absolute Kelvin is the
right unit here — water does not care which planet it is on.

220 K was below the coldest air any KSP body actually has, which meant contrailBlend could
never reach 1 anywhere: the fully-developed contrail look was unreachable by construction.
RefreshContrailBand samples the body's own temperature curve and lifts the cold anchor if
needed, so full development is always reachable somewhere in that body's atmosphere while
never being handed out warmer than the physics allows.

### `private float nightFloorScale`

ILLUMINATION. Condensation has nothing luminous about it: a contrail is visible only
because it SCATTERS SUNLIGHT. So the light term is not decoration, it is the whole reason
the effect is visible at all, and three things were wrong with how it was applied.

1. The contrail floor bypassed it. The floor is applied with Max(), so high-altitude wakes
   stayed at full brightness on the night side while every other path correctly went dim.

2. It was never normalised. Dividing solar flux by a hard-coded 2000 is wrong twice over:
   Kerbin's daylight flux is about 1360, so full noon only ever reached 0.68 and the effect
   never reached its own top end anywhere — and the same constant pins Duna (~600) at
   0.30 for no physical reason while Eve (~2500) clamps flat. Normalising against the
   UNOCCLUDED flux at the vessel's own distance from the star makes 1.0 mean "full
   daylight" on every body, which is what body-relative means here.

3. The floor is not really about physics. The wake material is additive, so it ADDS light
   to whatever is behind it — the same alpha reads far brighter against a black sky than
   against a bright one, and a wake tuned to look right at noon looks like a neon tube at
   midnight. That is the real reason night needs its own term, and why the floor is not
   zero either: planetshine and starlight are not nothing, and a wake that vanished
   outright would read as a bug.

4. It dimmed every afternoon, not just the night. vessel.solarFlux includes the atmosphere's
   absorption along the sun's path, so it falls steeply as the sun gets low: a late
   afternoon at KSC read as 20-45% lit under a still-bright sky and drew the wake at a half
   to a third of its midday strength, where an additive trail is hardest to see anyway.
   Point 3 is about the sky going dark, which follows the sun's elevation, so the light now
   comes from VortexVapor.Sunlight: 1 with the sun above the craft's own horizon, fading
   through twilight. The horizon dips with altitude, so a craft up high still sees the sun
   after the ground below it has gone dark, which is when real contrails are most striking.

dayLightScale was 0.75 when full daylight still read about 0.85 through the atmosphere,
which drew a midday wake at about 0.66; 0.70 keeps that look now that daylight reads 1.

### `private float contrailSpreadStart`

CONTRAIL SPREADING. A real high-altitude contrail goes through two regimes. While the wake's
vortex pair is intact it holds the ice in two tight cores and the trail stays narrow and
sharp. Then the pair breaks up (Crow instability links the cores into rings, and they burst),
the ice is released into the surrounding air, and the trail widens far faster than
diffusion alone would widen it — the familiar crisp double line that turns soft and wide a
way behind the aircraft. After that it thins as it spreads: the same ice over more width.

The wake here lives seconds, not minutes, so this is that sequence compressed onto the
trail's own lifetime: flat until contrailSpreadStart, then an accelerating (quadratic)
widening to 1 + contrailSpreadMax at the end, on top of tailRatio's steady spread. Scaled by
contrailBlend, so it is a cold-air effect only; a low manoeuvre puff never does it.
Opacity falls as spread^-contrailSpreadDimPow: less than the column-density answer (-1),
because a spreading contrail is still visibly there as a soft band rather than vanishing.

### `private float contrailEndFade`

The tube closes its last ribbonEndFade of length to a point so it never ends as a blunt
disc. At full contrailBlend that window shrinks to this, so the closure rounds off the tip
instead of cancelling the spread across the whole last quarter.

### `private float lineModeSpeed`

Line mode is now only the extreme-velocity regime, where its reentry width clamp and
stress-damped procedural terms still earn their place. Was 250 m/s, which dragged ordinary
jet cruise into line mode and left the trail module unused exactly where contrails belong.

### `private float helixRadiusMax`

Restored from 0.4.0 (dropped in 0.5.0): a wingtip vortex core is helical, so the trail
emitter traces a spiral rather than a straight line, with Perlin jitter so the two sides
never look mechanically identical. Set helixRadiusMax to 0f to disable.

The helix belongs to the WAKE, not to the emitter. Up to 0.6.14 it displaced the emitter,
which meant the trail's origin orbited the wingtip — the trail was never actually welded
to the anchor. It is now applied to stored vertices with a radius that grows from zero with
each vertex's own age, so the origin sits exactly on the anchor and the spiral only opens
out downstream, which is also what a real core does: it is pinned at the tip and the
helical structure develops behind it.

Driven by AoA AND speed rather than speed alone. AoA gates it (no incidence, no vortex to
wind up) and speed only modulates, so a fast pass at zero AoA stays straight.
Winding is a WAVELENGTH IN METRES, not a rate in rad/s. That parameterisation is the whole
reason 0.6.15 showed no swirl at all: at 1.8 rad/s and 200 m/s one turn spanned
2*pi*v/w = ~700 m, so the helix was a 0.1 m deviation over a 700 m period — a ratio of
1:7000, which is a straight line. A corkscrew only reads as one at roughly 1:20 to 1:60,
so both numbers had to move: metres-based period, and an amplitude to match it.
0f — the wake helix is OFF. Reverted after the spiral chase (0.6.14-0.6.17) and the
particle-cloud attempt (0.6.18) both failed: a camera-billboarded ribbon cannot read as a
rotating tube however it is parameterised, and the particle route broke outright. The
machinery below is left intact but inert, gated by this one value, so nothing else in the
file had to be unpicked to switch it off. Shape work from 0.6.13 (metre-based roll-up
taper) is retained — that part worked.

### `private float helixBaseDrive`

Driven by G, not by an angle-of-attack proxy. KSP exposes no AoA field, so the previous
driver measured the angle between the vessel's long axis and the velocity vector — which
is only AoA if the long-axis pick is correct, and on the test craft that pick is nearly a
coin flip (length 15.7 m vs height 14.2 m). Worse, in ordinary flight AoA is 2-4 degrees, so
the drive sat near 0.14 and scaled the amplitude down to ~0.1 m: invisible regardless of how
the wavelength was set. Load factor comes from summed lift and needs no geometry guess.

### `private float secondaryOnsetMultMin`

SECONDARY-SOURCE GATE. Canards, foreplanes and any other non-main surface have to earn
their vortex: BOTH thresholds must be met, and both scale in together. Replaces a gate that
took the MAX of a G term and (220 - speed)/40 — the second of which rose as speed FELL,
so above 8 km at 180 m/s or less it evaluated to 1.0 and the G term was ignored entirely.
That is why they hung around through mild manoeuvres: they were not gated on load at all in
that regime, and were actually easier to trigger the slower you flew.
The onset scales with how big the surface is relative to the main wing, because a flat
threshold is wrong for one of the two things that land in this slot:

  A PRIMARY canard (Eurofighter, Rafale, Gripen) sits in clean air ahead of the wing, and
  canard pitch stability REQUIRES the foreplane to stall first — so it is deliberately
  flown at a HIGHER lift coefficient than the main wing, not a lower one. Its vapour appears
  alongside the wing's, not several g later. Demanding +2g of it would be wrong.

  A small NOSE canard carries a few percent of total lift over a short span. Circulation
  goes as L/(rho*V*b), so far less lift means far less circulation, a far weaker core
  pressure drop, and genuinely more load needed before anything condenses.

Span ratio separates them, and it is already known at selection time.
Expressed as a MULTIPLE of the main wing's onset rather than as an absolute g figure, and
that change is the point rather than a tidy-up. As absolutes they were 6.0 and 3.5 against
a main-wing onset of 3.0, so the handicap was pinned at exactly 2.0x — and because
onsetScale multiplies both, it stayed 2.0x at every speed and altitude. At 150 m/s at sea
level that put a small canard's onset at 3.6 g, which is an ordinary manoeuvre, so it lit
up during perfectly normal flying. Making it dimmer did not help: it was still ACTIVATING
at the same moment.

The multiple is 1/spanRatio, which is what the circulation argument actually gives. To
reach the wing's condensing circulation, a surface needs load n = n_onset * r/f, where r is
its span share and f its lift share. A nose canard carries something like 10-20% of the
lift over 30-50% of the span, so r/f lands around 2.5-3.5 — well past the 2.0x ceiling
it used to be clamped to.

The floor matters too: even a two-thirds-span canard is held to 1.5x, so no secondary ever
sits at the main wing's own threshold unless tip likeness lifts it there deliberately.
1.5x was not enough separation to READ as separation. At 84 m/s onsetScale sits on its
0.40 floor, which compresses every threshold toward the bottom: the main wing's onset is
1.2 g there, so a 1.5x secondary fires at 1.8 g and a tip-likeness blend pulled that to
1.64 g. Four tenths of a g apart is not a visible difference in when something appears,
however correct the ratio looks on paper. Doubling it is the smallest multiple that still
separates the two events at the bottom of the speed range, where they are closest together.

### `private float secondaryGSpan`

Halved. Spread over 2 g at the reference condition, a secondary spent a long stretch of
the envelope at a fraction of its own strength, which read as a permanently faint smear
rather than as something that switches on. The onset multiple is what makes it late; this
just stops it lingering in a washed-out in-between state once it gets there.

### `private float secondarySpeedMin`

AIRSPEED FLOOR, back to a genuine noise floor at 45/90. The 120/200 version suppressed
canards on approach by refusing to draw them at all, which got the right picture for the
wrong reason and threw away a real phenomenon to do it. Replaced by secondaryWakeLength:
the canard vortex is drawn, it is simply drawn SHORT. See that field.

What survives of the old reasoning, and why 45/90 rather than zero:

The honest description is the one the old comment already had: this is a PRACTICAL GUARD,
not a circulation term. But the reason it is the right guard is NOT that a canard stops
working on approach. It is the opposite: canard pitch stability requires the foreplane to
stall first, so it is flown at a HIGHER lift coefficient than the wing, and at approach
alpha a close-coupled canard is near its maximum. Its vortex there is real and, on a
canard-delta, strong by design.

What that vortex does not do is TRAIL. It passes straight over the wing and is entrained
into the wing's own vortex system, usually bursting within a chord or two. Approach
photography shows wing and LERX vapour, sometimes a canard core just aft of the canard,
essentially never a clean cord streaming hundreds of metres back. A long trailing rope is
the one thing this mod knows how to draw, so drawing one there would be wrong even though
the vortex itself exists.

Hence a speed floor rather than a load term: it suppresses the case where the ROPE is the
wrong model, without pretending the aerodynamics are absent. Same conclusion the tube-vs-
trail split reached in 0.6.31, arrived at from the other direction.

Raw airspeed rather than dynamic pressure, deliberately. q would be the body-relative
spelling, but it also falls with altitude — and circulation RISES with altitude for the
same lift, so a q floor would suppress canards exactly where they should be strongest.
Speed keeps the guard where the proxy is weak without fighting the physics elsewhere.

### `private float secondaryWakeLength`

SHORT WAKE FOR SECONDARIES. The physically honest form of "a canard should not look like a
wingtip on approach".

A canard's vortex on final is real and, on a close-coupled canard-delta, strong by design
— pitch stability requires the foreplane to stall first, so it is flown at a HIGHER lift
coefficient than the wing and at approach alpha it is near its maximum. What it does not do
is trail: it passes straight over the wing and is entrained into the wing's own vortex
system, usually bursting within a chord or two. Approach photography shows wing and LERX
vapour, sometimes a canard core just aft of the canard, essentially never a clean cord
streaming hundreds of metres back.

So the error was never the brightness or the threshold, it was the LENGTH. A long rope is
the one thing this mod knows how to draw, and for a canard it is the wrong object. Capping
the wake length turns it into the stub it actually is, and does so at every speed, because
"bursts after a few chords" is a distance and not a duration.

Scaled by span ratio because chord scales with the surface: a bigger foreplane has a longer
chord and its vortex survives further before merging. At full tip likeness it lerps back to
the main-wing length, so a canard that IS the wingtip keeps a wingtip's wake.

### `private float secondaryMinLife`

Seconds, and deliberately small. This exists only to guarantee the tube gets enough rings
to be geometry at all — at 0.10 s it started winning against the length term above 250 m/s
and inflated the stub back to 40 m, which is the speed-dependence this whole approach is
meant to remove. At 0.05 s a full-size canard's cord stays a fixed 23 m out past 450 m/s,
and ring count is still fine: PushRibbonPoint sub-steps up to 8 per frame, so ~3 frames
lays ~24 rings.

### `private float mainLikeRatioMin`

TIP LIKENESS. "Main wing" and "secondary surface" are SLOTS in the selection pass, not
aerodynamic facts, and treating the slot as the fact left a cliff: the widest surface on
each side got full strength and the 3 g onset, and a surface 1 cm narrower got 40% strength
and a 3.5-6 g onset. Nothing physical happens at that boundary.

What actually matters is whether the surface reaches the outboard end of the span. If a
canard is canted or long enough that its tip is the widest point of the airframe, then that
tip IS the wingtip — it is the last place the pressure difference between upper and lower
surface can escape around, which is the entire mechanism, and the fact that it happens to
sit ahead of the wing changes nothing about it. The selection pass already lands the MAIN
slot there in that case, because it sorts on lateral reach; this closes the cliff on either
side of it, so a canard reaching 95% of the wing is treated as a wingtip too, and a wing
demoted to the secondary slot by an even wider canard gets its own treatment back.

A small nose canard is untouched: it reaches a third of the span, so it stays fully gated,
which preserves the secondary tightening from 0.6.26.
The band was 0.80-0.95 and that was too generous by half. What 0.6.38 actually asked for
was the case where a canard IS the widest point on the airframe — then its tip is the
wingtip and there is nothing to argue about. A canard at 85% of the wing's span is not that
case, and yet it was picking up a quarter of main-wing treatment: a lower onset AND, until
1.1.3, a permanently open slice of gate.

Narrowed so only a surface that essentially matches the wing qualifies. A canard reaching
95%+ still resolves to strength 1 and runs the main-wing path unchanged, which is the
behaviour that was actually requested.

### `private float secondaryStrengthMin`

The quadratic falloff from 1.1.1 is right about ORDERING — core pressure drop goes as
circulation squared, so a small nose canard should be far fainter than a large one — but
the ANCHOR was the problem, not the shape. At 0.4, and then 0.7, a large canard was still
multiplied by a gate ramping up from zero, and the product landed around 0.12-0.30 of a
main vortex. That is the "very dim" that survived five builds of threshold work, because
every one of those builds moved the threshold and none of them moved this.

Anchored near 1 instead. A surface that has cleared its onset is making a real vortex and
should look like one; the thing that distinguishes it from the main wing is WHEN it starts,
not how washed out it is afterwards. Small surfaces are still held down hard by the
quadratic — at a third of the wing's span this is still only ~0.14.

### `private bool treatSecondariesAsMain`

ABSOLUTE TEST. Spawns secondary sources with strength 1 and span ratio 1, which makes the
`strengths[i] < 1f` block in Update() skip entirely — no size penalty, no onset
multiple, no airspeed gate. A canard becomes, in every respect the renderer can see, a main
wingtip.

The point is not the look, it is what it RULES OUT. Five builds of threshold work have
produced no visible change, and there are only two explanations left: either the secondary
path is being gated somewhere not yet found, or the vortex on screen is not coming from
the secondary path at all. If a canard at full main-wing treatment still renders dim and
unchanging, the second explanation is the right one and every threshold in this file is
irrelevant to it.
Off now — it did its job. Forcing a canard to main treatment made it render correctly,
which ruled out the renderer, the anchor and the source selection all at once and left
exactly one culprit: the strength x gate product. 1.1.5 had already given secondaries tubes
and they still looked wrong, so the only difference between that and the working test was
these two numbers. Kept as a switch because it is the fastest way to re-run that isolation
if a secondary ever misbehaves again.

### `private float maneuverTimeMin`

MANEUVER-PUFF LIFETIME. Companion to decaySpeed above: the trail's own point lifetime has
to be at least as long as the decay it's supposed to show, or the history buffer runs out
before the fading-out intensity does and the "lingers, then fades" behaviour is invisible.
Was a fixed 0.25-0.7s (now the contrail band's bottom rung; see contrailTimeMin/Max).

### `private float refMass`

CIRCULATION MODEL. Real wingtip-vortex strength is Gamma ~ (load factor * weight) /
(wingspan * airspeed) — the classic aircraft wake-vortex relation. G-force (via
currentIntensity above) already stands in for load factor; this factor supplies the three
terms G-force alone can't: heavier aircraft, narrower spans, and slower speeds all make a
physically stronger vortex at the SAME G. It's a ratio against a reference light aircraft,
clamped to a modest range so it nudges the existing G-based curve rather than replacing it
— a craft at the reference values leaves today's tuning untouched.
Circulation goes as (n * m) / (rho * V * b). Load factor n is the G curve below; density
is already handled as visibility by atmFactor; what is left is m/b, and that is ONE
quantity, not two knobs — real aircraft hold roughly constant wing loading, so m scales
with b^2 and m/b therefore scales with b. Bigger simply means stronger, which is exactly
why wake-turbulence categories are drawn by size.

Speed is dropped. Not because it is redundant with G (a 900 m/s level pass is still 1g),
but because once the G gate is doing the work its residual effect is second order, it is
the term that reads backwards to a player (physics says slower is stronger), and in the
previous form it was inert anyway: with a hard clamp at 2.0, circFactor sat pinned at its
ceiling below 333 m/s for a 20t craft and below 500 m/s for a 50t one, so mass, span AND
speed all did precisely nothing across the normal envelope.

Soft saturation rather than a clamp. Condensation IS a threshold effect so saturating is
right, but a hard cap destroys the ordering above it; 2x/(1+x) maps 0->0, 1->1, inf->2 and
keeps a 150t airliner ahead of a 20t fighter instead of both sitting on the cap.

### `private float onsetLoadRef`

CIRCULATION ONSET. The load-factor thresholds below used to be constants — 3g for the
main wing, 3.5/6g for secondaries — and that is what made a landing approach render
nothing at all, because a stabilised approach is 1g by definition.

But load factor is not circulation. Gamma = L/(rho*V*b), and n = L/W, so Gamma is
proportional to n*m/(rho*V*b): the SAME lift makes proportionally MORE circulation as
speed and density fall. That is the whole reason a slow, high-alpha approach vortexes
despite pulling no g. Run the numbers on a 20 t / 12 m fighter: a 3 g break turn at
250 m/s at sea level gives Gamma ~159 m^2/s, and a 96 m/s approach at 1 g gives ~140 —
88% of the break turn, from an aircraft doing nothing but flying straight.

Rather than rescale g and break every threshold tuned against it, the thresholds themselves
now move: the load required for onset scales with rho*V. At the reference condition
(sea level, 250 m/s) the scale is exactly 1, so every number tuned so far is preserved
untouched. Slower or thinner air lowers the bar; faster or denser air raises it, which is
also correct — a 400 m/s pass is at low lift coefficient and genuinely should need more.
The onset SPAN scales with it too, or the curve would be stretched across a range the
aircraft cannot reach at approach speed.

### `private float onsetScaleMin`

The floor has to keep the onset ABOVE 1 g, and that is not a taste call. rawLiftG falls
back to vessel.geeForce when the wings are making no lift at all, and a parked aircraft
reads 1 g sitting on its gear — so any floor that puts the onset below 1 g makes a
stationary craft on the runway sprout vortices. 0.40 puts the onset at 1.2 g, which a
parked or level-cruising aircraft cannot reach and a flare or any real manoeuvre can.

### `private float flyingSpeedMin`

Second half of the same guard, and the one that does not depend on a tuned number: below
this there is no meaningful airflow over the wing, so whatever rawLiftG is reporting is
gear load or noise rather than aerodynamics.

### `private float airborneBlend`

GROUND ROLL. An airspeed gate alone does not cover this, and the reason is worth writing
down because it is the mistake this replaces: the lift coefficient proxy L/(q*b^2) is a
RATIO, and on the takeoff roll its numerator and denominator both fall as V^2. It does not
shrink with speed at all — it just reports the gear-attitude CL, which for a low aspect
ratio stock wing can sit right on the threshold. So a craft accelerating down the runway
read as "slow and working hard" and grew vortices.

The physical discriminator is not speed and not CL, it is WHAT IS CARRYING THE AIRCRAFT.
On the runway the gear does, and the wing carries only a fraction of the weight, so there
is no developed tip vortex to condense. Once airborne the wing carries all of it by
definition. KSP answers that directly, so it is gated directly.

Deliberately NOT a ground-proximity or ground-effect test: the classic short-final vapour
photograph is taken deep in ground effect, so suppressing on height above terrain would
kill the exact case this whole feature exists to produce.

### `private float humidDensityMin`

HUMID BOUNDARY LAYER — the low-altitude mirror of the cold-air contrail floor above.
The onset scaling gets a landing approach to the right CIRCULATION, but circulation alone
undersells how photogenic approach vapour actually is, and the reason is not aerodynamic.
The ground and the ocean are the moisture source, so relative humidity in the bottom
kilometre or two runs 70-90%, and very little core pressure drop is then needed to reach
dew point. That is why the classic wingtip-vapour-on-short-final photograph exists at all.

Body-relative like everything else: depth in the atmosphere is measured against the body's
OWN sea-level density, and "working hard for its speed" is a lift coefficient proxy,
L/(q*b^2), which is CL/aspect-ratio — no wing area needed, and no AoA needed either,
which matters because KSP does not expose one.

### `private float humidLiftShareMin`

Second, independent requirement on the same floor: the wing has to actually be carrying
the aircraft. A steady approach or descent is L = W*cos(gamma), so this is ~1 in the case
we want, and well under 1 on any roll where the gear is taking the load. Belt and braces
with the airborne gate, and it also covers a bounce or a touch-and-go where
LandedOrSplashed flickers.

### `private float coreGrowthEnable`

VISCOUS CORE GROWTH (Lamb-Oseen, with Squire's eddy viscosity).

Both renderers already spread the wake with age, through their own tailRatio, but that is a
tuned lerp with no physics in it: the same ratio whatever the craft, the air or the load.
The real spreading law is known, cheap, and explains something the lerp cannot — why a
heavy aircraft's wake holds together as a tight rope while a light one goes soft in seconds.

Lamb-Oseen gives r(t) = sqrt(r0^2 + 4*alpha*nu*t) with alpha = 1.25643. Fed MOLECULAR
viscosity that is invisible: nu ~ 1.5e-5 m^2/s is about two centimetres of growth in five
seconds. That is the right answer for laminar flow and the wrong one for a wake, which
spreads by TURBULENT diffusion. Squire's hypothesis supplies the eddy viscosity as
proportional to the vortex's own circulation, nu_t = delta * Gamma — a stronger vortex
stirs its own surroundings harder and diffuses faster.

Both terms are summed rather than switched, so air too thin to sustain turbulence falls
back toward the molecular floor instead of to nothing.

### `private float coreSpanFraction`

VISIBLE core radius at shedding, as a fraction of span, and the distinction in that first
word is the whole reason this number is 0.08 and not the 0.04 it started at.

3-5% of span is the right figure for the VISCOUS core, and it is the wrong object to put
here: what this scales is the radius of the drawn feature, and the vapour fills the whole
low-pressure region, which is materially wider than the viscous core. r0 enters the rate
SQUARED and in the denominator, so anchoring it to the wrong quantity was amplified
fourfold — the first flight test pinned coreGrowthMax at 2 seconds on every sample, which
is the clamp doing the modelling instead of backstopping it.

The cross-check that settles it is nondimensional time, t* = t*Gamma/(2*pi*b0^2) with
b0 = (pi/4)*b, the standard yardstick for wake age. A measured break turn (MiG-29A, span
11.6 m, Gamma 619 m^2/s) reaches only t* = 2.4 after two seconds, and a wake stays
organised to t* of roughly 4-8. So the core at that point should have grown moderately,
not tripled. 0.08 puts it at 1.9x at the end of a low-altitude wake's life and leaves the
clamp untouched in every flight case measured.

It is still what makes the spreading scale-relative, which is the point: identical
absolute diffusion is a large fraction of a light aircraft's small core and a small
fraction of an airliner's large one.

### `private float coreDimPow`

DIMMING AS IT SPREADS, and the half that makes the growth physical rather than just fatter.
Circulation is conserved as the core diffuses, so spreading it over a wider core drops the
peak vorticity and with it the core pressure deficit, which goes as 1/r^2. Condensation
follows that deficit, so a core that has doubled in radius should be markedly fainter, not
the same brightness over twice the area — widening alone reads as a glowing cone.

The exponent is well under 2 on purpose. Visibility is not linear in pressure drop: vapour
is a THRESHOLD effect (the same reason massSpanFactor soft-saturates), so once the core is
condensing at all, halving the deficit does not halve what you see. 0.8 keeps width*alpha
roughly flat, so the wake softens and spreads instead of either blooming or vanishing.

### `private bool loggedCoreGrowth`

Two one-shots, not one. The first qualifying manoeuvre on any flight is almost always down
low, so a single latch reports sea-level numbers and then goes quiet for the rest of the
session — including the contrail regime, where the growth is largest and stacks on top of
tailRatio. The second latch fires once the band is actually reached.

### `private float sutherlandMu0`

VISCOSITY. Sutherland's law rather than a fixed value, because the mod already reads real
ambient temperature and a constant here would just be a Kerbin sea-level assumption wearing
a physics coat: mu(T) = mu0 * (T/T0)^1.5 * (T0+S)/(T+S). Absolute Kelvin, so like the
contrail band it needs no per-body tuning to be right on Duna or Eve.

### `private float nuMolecularCap`

NO REYNOLDS GATE, and that is a measured decision rather than an omission. A laminar/
turbulent blend was built here first, on span Reynolds and then on vortex Reynolds
(Gamma/nu, the quantity the wake-vortex literature actually uses). Both read 1e6 to 1e8
across every flight case that draws anything — stock jet, airliner on final, contrail
cruise, rocket ascent, re-entry — so the blend evaluated to "fully turbulent" always, and
it still did at the thin-air limit where the mod stops drawing at all (rho 0.01 puts
Gamma/nu near 1e6). The honest statement is the simple one: a trailing vortex in air thick
enough to condense anything is turbulent, without exception, so there is no transition to
model. Three tunable constants that can only ever evaluate to 1.0 would have been the
helix-wavelength mistake again — correct mechanism, no signal.

Ceiling on the MOLECULAR term. nu = mu/rho diverges as the air runs out, and without this
a re-entry trail would fatten on a laminar diffusion term in air far too thin to condense
anything, which is growth with nothing on screen to justify it.

### `private float ambientTurbScale`

Ambient stirring. The lower atmosphere is convectively mixed and the air near the surface
more so, which is a real and well-measured accelerant on wake decay. Rides the same
depth-in-atmosphere blend the humid boundary layer already computes, so it costs nothing
extra and stays body-relative.

### `private float groundEffectEnable`

GROUND EFFECT (method of images). A wall is modelled exactly by a mirror vortex of opposite
sign beneath it, and the mirror does two things to a real wake pair: it cancels the pair's
self-induced descent, and it pushes each vortex OUTBOARD. That lateral drift is why wake
turbulence migrates onto parallel runways, and it is the visible signature — behind a
low pass the two ropes splay apart instead of running parallel.

Not a visibility gate, by the rule at airborneBlend: suppressing near the ground would kill
the short-final vapour this mod exists to draw. This changes where the rope goes and how
fast it diffuses. It never decides whether it is drawn.

For a pair at height h with separation b0 = (pi/4)*b, own image minus partner's image:
  lateral drift    v = Gamma/(4*pi) * b0^2 / (h * (4h^2 + b0^2))
  descent retained    4h^2 / (4h^2 + b0^2)
h is floored at b0/2 in the drift. That is the height an inviscid pair asymptotes to and
can never get below, and it is where the formula stops being the physics and starts being
a singularity.

### `private float groundTurbScale`

The ground boundary layer separates under a vortex and rolls up a secondary vortex of
opposite sign, which is what saps a wake's circulation near the surface and shortens its
life. Expressed as extra eddy viscosity scaled by the ground factor, so it rides the same
growth law as everything else instead of inventing a second decay.

### `private float groundEffectCeilingSpans`

Beyond this many spans the image terms are below one percent. Also a guard: a stale
altitude reading can only ever produce a spurious effect if it reads LOW, and this bounds
how wrong it can be before it is simply ignored.

### `private float breakdownEnable`

VORTEX BREAKDOWN, dual mode. A swirling core with too much rotation for its axial flow
stagnates on its axis and bursts. Two forms: SPIRAL, where the core kinks into a
precessing corkscrew and then disperses, and BUBBLE, an abrupt axisymmetric bulge with
recirculation inside, after which the core is gone. Spiral appears near onset; bubble
takes over at higher swirl.

The controlling quantity is the swirl ratio, peak tangential velocity over axial velocity.
For a Lamb-Oseen core v_max = 0.715 * Gamma / (2*pi*rc), so S = 0.715*Gamma/(2*pi*rc*V).
Everything in that is already known, and at fixed load it rises as speed falls, because
Gamma = L/(rho*V*b) — which is exactly the angle-of-attack dependence, without an AoA
field KSP does not have. Written out it is 1.42 * clProxy: the same CL/AR measure the
humid boundary layer already keys off.

rc here is the VISCOUS core, 4% of span, not coreSpanFraction's 8% visible radius. Swirl
is a property of the flow, and the flow's peak velocity sits at the viscous core edge.

Thresholds from Spall, Gatski & Grosch (1987): breakdown when the Rossby number W/(r*Omega)
falls below about 0.65, i.e. swirl above about 1.5. Onset ramps in slightly before that so
the effect grows in rather than switching on.

### `private float breakdownStationFarSpans`

Where along the wake it bursts. The burst point moves UPSTREAM as swirl rises — the same
behaviour measured on delta-wing vortices, where it walks forward with AoA. Barely past
onset it happens far back; deep in breakdown, within a span of the tip.

### `private float breakdownSizeSaturateDepth`

Depth at which the spiral and bubble reach full SIZE. A breakdown mode is a finite-
amplitude structure: once the flow is past critical it takes its size quickly, rather than
growing in proportion to how far past it is. Scaling size linearly with depth left the
spiral at half a metre by the time bubble took over, which is invisible from a chase
camera. How far upstream it bursts and how fast it fades still follow depth.

### `private float spiralAmpCores`

Spiral geometry, in visible core radii so it scales with the airframe. The minimum rings
per turn stops a short wavelength aliasing into a polygon on coarse ring spacing.
Tuned down from 1.5 / 10 after in-flight review: a pull only ever gets just past onset
(logged depth ~0.05), where size is already saturated but the depth-scaled fade is nearly
zero, so the old values laid a regular coil hundreds of metres long. Seen along the rope
with any foreshortening it read as loops. A real spiral breakdown is a kink that throws a
turn or two and then disperses; spiralDecayTurns bounds it to that, independent of depth.

### `private float peakSwirl`

Session peaks, reported on destroy. Breakdown thresholds are literature values applied to
a swirl estimate built on an assumed core size, so the peak a real flight reaches is the
number that says whether they are in the right place — reported whether or not it fired.

### `private float trailSinkRate`

PERSISTED-WAKE SINK (OFF by default — see the writeup for why). Real trailing vortices
sink under their own induced flow, a few hundred ft/min early on, slower as they age. This
approximates that by nudging every EXISTING trail vertex down by a small amount on a fixed
cadence, so points that have existed longer end up lower — the "older = sunk further"
shape falls out of simple accumulation without tracking a per-vertex age. It is a uniform,
not decaying-with-age, sink rate, which is the part of the real behaviour this does NOT
capture; see the design notes for what a truer version would need.

This touches the exact GetPosition/SetPosition path OnFloatingOriginShift depends on, just
on every tick instead of only on a shift event, so it ships disabled (rate 0f is a hard
no-op, checked before the loop runs) until it's been validated in-game. Set trailSinkRate
to something like 0.3-0.6 (m/s) to try it.

### `private float sinkUpdateInterval`

0 = apply the sink every frame, which is what it wants to be. 0.6.5 batched it at 0.1s to
save CPU, and that was a mistake worth spelling out: at 1.2 m/s a 0.1s batch teleports the
WHOLE trail 12cm downward in a single frame, ten times a second, while the emitter stays
pinned to the moving wingtip. The trail visibly detached from the tip and a fresh vertex
spawned to bridge the gap, over and over — which is exactly the "constantly spawning at
the wingtip" jitter. Per-frame it is ~2cm at 60fps and continuous. Raise this only if the
per-frame vertex rewrite ever shows up in a profile; it trades smoothness for CPU.

### `private const int shedSamples`

SHED-STRENGTH HISTORY. A ring buffer of how strong the vortex was at each moment it was
being shed, replayed along the trail by ApplyShedAppearance so that already-shed geometry
renders at the strength it was born with instead of at whatever the wing is doing now.
64 * 0.08s = 5.1s of history, which covers contrailTimeMax.

### `private float trailAlphaGain`

1.0, not 1.25. The headroom existed to let a weak shed still read on screen, but it also
guaranteed that a strong one clamped flat. Peak alpha is now simply the shed strength, so
the whole falloff curve is visible instead of its top being cut off.

### `private const int widthKeyCount`

Reused scratch — TrailRenderer copies curve/gradient data on assignment, so one shared
instance is safe and avoids the per-frame allocation the old code did.
WIDTH gets its own, denser key set. AnimationCurve has no key limit — only Gradient is
capped at 8 — and the head ramp lives inside the first few percent of the trail, which
uniform 8-key spacing (0, 0.143, 0.286 ...) cannot resolve at all.

### `private float rollupMinMetres`

Leading-end shape. The trail used to begin at full width, so it ended in a flat slab face
that pointed nowhere — which is why the origin was hard to locate, especially on a weak
low-AoA vortex or one anchored somewhere other than the true wingtip. It now converges
toward the anchor over the first headRampFrac of its length, giving the eye a wedge to
follow back to the source. It stops at headWidthFraction rather than reaching zero, so a
faint vortex still HAS a visible origin instead of fading out exactly where you need to
read it. Raise headWidthFraction if the point is too fine to see; lower it for a sharper
taper. Both are geometry only — no colour or alpha is touched.
Roll-up length in METRES, which is the whole point and what 0.6.11/0.6.12 got wrong.
Both expressed the taper as a FRACTION of trail length, and a trail is enormous — at
520 m/s with a ~4.8 s lifetime it is about 2.5 km long, so "20% of the trail" was half a
kilometre of taper. The stretch you actually see beside the aircraft was the first ~2% of
that, where the width is essentially constant, so it still terminated on a flat face.

A real vortex rolls up over roughly a chord — metres, not hundreds of metres — so the
ramp is now an absolute distance converted to a curve fraction against the trail's live
length each frame. headWidthFraction is where it starts, not zero, so a faint low-AoA
vortex still has a locatable origin.

### `static class FARBridge`

FAR REPLACES ModuleLiftingSurface on every wing it touches, so liftSurfaces above comes up
empty under FAR and the load factor below silently degrades to geeForce — precisely the
"climbing on thrust alone spawns vortices" case this file exists to avoid. So under FAR the
sum reads each FARWingAerodynamicModel's own force instead: the same quantity as the stock
sum, lift on the lifting surfaces only.

Per wing, deliberately NOT FARAPI.VesselAerodynamicForce (what 1.2 read). That is the whole
vessel's force, fuselage body lift included, and body lift sheds weak vortices off the
fuselage, not off the wingtips; counting it made a FAR craft reach every onset threshold at
a lower wing loading than the same craft under stock.

Bound by reflection so there is no hard dependency: a missing or incompatible FAR just
leaves FARBridge.Available false and the mod behaves exactly as it did before FAR.

### `public static float GetLiftKN(Vessel v, List<PartModule> wings, Vector3 flowNormalized)`

Sum of each wing's lift magnitude, kN: the same convention as summing
ModuleLiftingSurface.liftForce.magnitude, so a tail carrying download adds rather than
cancels, exactly as it does under stock. Lift is each wing's force less its component
along the flow (drag), the split FAR itself uses. FAR leaves worldSpaceForce at its last
value when it stops computing (vacuum, shielded in a bay), so those wings count as zero.

### `internal static bool IsLiftPart(Part p)`

Surface detection by module name. FAR swaps the stock modules for its own on every wing it
patches (ModuleLiftingSurface -> FARWingAerodynamicModel, ModuleControlSurface ->
FARControllableSurface), so matching only the stock names finds no wings under FAR and no
vortex ever spawns. Names, not types, so FAR stays an optional dependency. Lift and control
stay separate the way stock keeps them: a control surface is not a "lift" part.

### `private float smoothedLiftG`

Low-pass on the LIFT MEASUREMENT, which is a different thing from the build/decay rates
above and must not be confused with them. KSP recomputes liftForce at physics rate off
instantaneous part velocity, and KSP airframes visibly flex at their joints, so the raw sum
carries real high-frequency measurement noise — which Update() then samples at render
rate, adding a beat between the two. 0.6.4 hid that behind a slow decaySpeed; 0.6.5 made
decay fast (correctly, on physical grounds) and the noise came straight through to the
width and alpha of the newest trail segment, flickering at the wingtip.
Filtering the sensor is the right fix; slowing the physics is not. 0.12s is short enough
that a real unload still reads as immediate.

### `private Vector3 axLateralLocal`

The vessel's geometric frame, resolved once by ComputeVesselAxes().

Held in the ROOT PART'S LOCAL space, not world space, deliberately. Which local axis is the
fuselage never changes, but the world direction it points in changes every time the aircraft
moves — so caching world vectors here would hand every later caller a snapshot taken on
the runway. The properties below re-derive the world directions on demand instead.

### `private float maxPartExtent`

ROGUE RENDERER REJECTION. Every vessel measurement in this file is built by encapsulating
part renderer bounds, and encapsulation has no tolerance for a single bad member: one
renderer with absurd bounds silently poisons the whole airframe measurement.

This is not hypothetical. On a heavily modded install the effect went completely dark and
the log said why: "span=1373763000000000000.0m". Visual mods DELIBERATELY give their meshes
enormous bounds to defeat frustum culling — Scatterer, EVE, Singularity and Deferred all
do it — and GetComponentInChildren walks the entire transform subtree of a part, so one
of those parented under any part is picked up as if it were part geometry. From there
vesselSpan goes to 1e18, sizeFactor to 3e-16, circFactor with it, and every vortex renders
at zero width and zero alpha. The sources all spawn correctly; nothing is visible.

A stock install has nothing that does this, which is exactly why it survived testing.

### `private float rendererColliderCheck`

SECOND-TIER CONTAMINATION. maxPartExtent catches the 1e18 cull-defeating quads, but not the
merely-large: engine plumes, reentry effects and similar visual meshes are tens of metres
and sail straight through a 200 m cap. On a modded install that inflated a 17 m fighter to
a "46 m long, 44 m tall" airframe — see ComputeVesselAxes for why near-equal length and
height is so much worse than simply being wrong.

Colliders are the discriminator. Effect meshes have none: they are visual only. So a
renderer is checked against its own part's PHYSICAL extent, which cannot be inflated.
Only applied to renderers already large in absolute terms, so ordinary part geometry on a
stock install is never second-guessed.

### `private float lastRbSpeed`

Previous frame's Krakensbane-relative speed, held so the discontinuity guard does not
false-trip on the one frame where Krakensbane engages and rb_velocity collapses to ~0
while the anchor has genuinely just travelled a full frame at the old speed.

### `private float sourceReadyTimeout`

VESSEL READINESS. Source detection runs ONCE, and every anchor it places is measured
against the airframe as it stands at that moment — so it has to run against an airframe
that has finished being built. "loaded" is not that test: on a scene load the vessel is
loaded while still PACKED, with its parts not yet at their flight positions.

ComputeVesselAxes resolves the length axis by comparing bounds extents along two candidate
directions, so a bounds read mid-settle can resolve the WRONG axis — and that is the
failure this file already documents at length as the one that puts canard anchors at wing
roots. Detection then bakes it in permanently.

### `private float staleCheckTimer`

STAGING. Sources are detected ONCE and then hold a Transform parented to the part they were
found on. Nothing ever checked that the part was still ours — and on a staged rocket it
stops being ours the moment the booster separates. The fins the anchors were sitting on
leave with the debris vessel, so the wake goes on being drawn from a booster that is no
longer the craft being flown, and when that debris finally unloads the anchor Transform is
destroyed underneath us. That is the bright streak left hanging off the capsule.

Not a rocket-only problem, which is why this is not gated on craft type: a spaceplane
dropping boosters, a shuttle stack, or an aircraft that simply loses a wing all leave the
source list pointing at parts that are gone or that belong to something else.

Checked on the existing bounds cadence rather than through GameEvents: one comparison per
source twice a second is nothing, and it catches every cause — decoupling, docking,
destruction, vessel switching — without depending on which events KSP fires for each.

### `private bool skipVerticalLaunchers`

VERTICALLY-LAUNCHED CRAFT. Off by default: on the ascent footage this actually looks right,
and rocket fins do shed real vortices transonic. Set true to skip such craft entirely and
leave re-entry to the stock effects (or to Firefly, if installed).

The test only works while the craft is still on the pad, where a rocket sits nose-up and an
aeroplane sits nose-along-the-runway, so the answer is decided once at first detection and
then reused — by the time anything re-detects after staging, attitude means nothing.

### `private float rocketSlenderness`

SHAPE, not attitude. Launch attitude only exists while the craft is on the pad, so it
cannot answer the question after a mid-flight vessel switch — and it silently answered
"not a rocket" there, which dropped a booster into the aircraft selection rule and cost it
the two fins that sit at zero lateral offset.

A rocket is SLENDER: nearly all of its extent is along the roll axis. Measured from part
positions, which is attitude-independent and stable in flight, the two craft that have been
through this are not close — the MiG reads 14.0 m along by 11.6 m across (ratio 1.2) and
the Redstone 15.3 m by about 1.2 m (ratio ~12). A threshold of 4 sits in empty space
between them, with room for a small-winged SSTO to still land on the aircraft side.

### `private bool rocketUseLineMode`

Line mode is off for rockets. It is the SHORT renderer: its length comes from a procedural
per-point offset that tops out around historyLength * spacing and is then shortened further
by stressFactor at exactly the speeds a booster flies, while the trail's length is simply
speed * tr.time — capped at ribbonMaxLength, so up to 2.5 km. One wake abruptly becoming
a fraction of the other is what reads as the cut-off on ascent.

Line mode was written because accumulated trail geometry outran its own resampling at
extreme velocity. That was really the Krakensbane frame bug, fixed in 0.6.1, and the trail
has had a hard length cap since 1.1.9 — so the original justification has quietly
expired for this case. Set true to put boosters back on it.

### `private float finBandFraction`

ROCKET FIN SOURCES. A vertically-launched craft gets its own selection rule, because the
aircraft rule cannot express what a rocket is. That rule is {left, right} x {main,
secondary}, picked by reach from the centreline, and it rests on "left" and "right" being
real — which they are on a wing and are not on a cruciform tail. Roll a Redstone 45
degrees and the same four fins swap between qualifying and being rejected as centre
sections, because minLateralOffset was written to throw away an aircraft's RUDDER. On a
rocket that reasoning has nothing to attach to: all four fins carry the same load over the
same span, and which pair happens to be "horizontal" is just how the craft was built.

So: take the lifting surfaces at the BOTTOM of the stack — they are the fins, wherever
they point — and give each one its own vortex, up to the same hard cap of four. Offset
is measured RADIALLY from the roll axis rather than along one lateral axis, which is the
only part of this that has to change to make it roll-invariant.

### `private bool rocketUseRibbons`

Rocket fins go on the trail/line renderers, not the tube. The tube is built around a wake
that trails a lifting surface in roughly steady flight; a booster spends its whole ascent
rotating, and on the way back down it is tumbling through thin air at several km/s, which
is what line mode was written for in the first place.

1.1.10 moved fins ONTO the tube to escape two discontinuities in the trail/line path. Those
are fixed properly below rather than avoided, so this can go back to the renderer that
suits the flight regime. Set true to put fins back on tubes.

### `private float wakeShutdownTau`

SHUTDOWN TIME CONSTANT, shared by both renderers, and the sharing is the whole point.

shouldUseTrail and shouldUseLine both go false on the same test (visible > 0.01), so on
leaving the atmosphere they stop together — but they used to STOP AT DIFFERENT RATES.
The line's widths were driven to zero in about a third of a second, while the trail simply
held whatever tr.time it last had, up to five seconds of geometry aging out on its own.
One renderer vanished while the other was visibly still going, which reads as a cut even
though neither was cut.

Both now decay with this one time constant, so whatever they were showing goes away
together. Exponential and frame-rate independent, the same form as every other smoothed
quantity in this file. Coming back down, the trail block lerps tr.time back up to its
target, so a retracted wake rebuilds rather than snapping to full length.

### `private bool useSectorSelection`

SECTOR SELECTION. {left, right} x {main, secondary} is not two ideas: it is ONE SECTOR AXIS
and one station axis, with the sector count hard-coded to 2 and the sectors nailed to
plus/minus lateral. Everything awkward about rockets follows from that constant — roll a
cruciform tail 45 degrees and the same four fins swap between qualifying and being rejected
as centre sections.

So let the sector count come from the CRAFT: cluster candidates by angle about the roll
axis and start a new sector wherever the angular gap exceeds sectorGapDeg. A wing gives 2
(gap 180), a cruciform tail 4 (gap 90), a three-fin booster 3 (gap 120). Gap-based rather
than fixed-count is what makes 3-fin and 4-fin both work without asking which it is.

SHIPPED INERT. Sectors are computed and logged on every detection, and nothing consumes
them unless useSectorSelection is set. The acceptance test is free and comes from the log:
every aircraft that works today must come back as exactly two sectors matching its current
left/right split. Validate that across the fleet BEFORE anything depends on it — the
last four bugs in this file were all reasoning that was never measured.

### `IEnumerator Start()`

vertex is rewritten in OnFloatingOriginShift on every
physics frame, and each tube rebuilds a 320-ring mesh
per frame. This is not the old cap of 4, which was
aerodynamic and is gone with the slots.

### `if (target !`

See sourceReadyTimeout. Waiting on `loaded` alone was the bug behind "Revert to Launch
moves the wingtip anchors, and only recovering and respawning puts them back": a revert
restores a cached scene and comes up faster than a fresh launch, so this coroutine won
the race and measured a vessel that was loaded but still packed. Wrong bounds, wrong
length axis, wrong tips — baked in for the rest of the flight, because detection
never runs again.

### `ClearSources();`

Defensive: before per-craft controllers, Update()'s active-vessel check built sources
while this coroutine waited and this pass added a second set on top (4 sources on a
2-tip craft, kept all flight by a craft that started airborne). Nothing builds early
any more, but a stray set here would double every wake, so start from none.

### `if (vessel.LandedOrSplashed)`

CONFIRMATION PASS. The wait above should be enough on its own, but it is a timing gate
and timing gates are exactly the kind of thing that holds on one install and not on
another — a slow modded load, or a part whose mesh is still being resized by
something like procedural wings. Detecting once means any miss is permanent, and the
player's only recourse was to recover and respawn.

So detection is simply run a second time a few seconds later, which costs one extra
pass per flight and makes a mistimed first pass self-healing. Gated on the craft still
being on the ground, so it can never interrupt a wake that is already being drawn: that
is also precisely the situation the bug appears in, since a revert puts you back on the
runway.

### `bool SourcesStale()`

Tears down every per-source object and the parallel lists that index them. Shared by
OnDestroy and the confirmation pass, so the two can never fall out of step as sources
gain new per-source state.
True when any source no longer belongs to the craft being flown. Unity's null check
catches a destroyed anchor; the vessel comparison catches the part still existing but
having left on another vessel, which is what staging does and what a plain null check
would sail straight past.

### `ClearSources();`

The vortex objects are deliberately unparented so recorded trail geometry stays in world
space — which also means nothing else in the scene ever cleans them up. Without this,
every Flight scene load left a full set behind, still holding geometry, forever.

### `void OnFloatingOriginShift(Vector3d offsetVessel, Vector3d nonFrame)`

KSP's FloatingOrigin has no hook for TrailRenderer — its recorded vertices are baked
absolute world-space data, not driven by any Transform — so a shift leaves them in the
old frame while the anchor jumps to the new one, producing one long connecting segment
(the "forward spike"). We correct those vertices in place rather than clearing the trail,
which would only hide the spike by discarding the whole trail's history.

WHICH offset is the part 0.6.1 got wrong. FloatingOrigin.setOffset() moves WORLD-anchored
content — celestial bodies, registered ParticleSystems, EffectBehaviour particles, the
terrain shader — by offsetNonKrakensbane, which is (offset + nonFrame). Loaded,
unpacked, airborne vessels move by offset ALONE. nonFrame is Krakensbane's FrameVel *
fixedDeltaTime: how far the world slides past the vessel each physics frame while the
velocity frame is engaged.

A vortex is a contrail hanging in the air, so it is world-anchored and needs the FULL
world offset. Subtracting offset alone pinned the recorded vertices to the VESSEL's frame
and left a residual error of exactly nonFrame on every single frame. That is why the spike
survived 0.6.1, and why it only shows above ~200 m: Krakensbane.SafeToEngage() gates the
velocity frame on radarAltitude against altThreshold (200) / altThresholdAlone (50), so
below the gate FrameVel is zero, nonFrame is zero, and offset alone happened to be
correct — which is exactly the "fine below, broken above" split.

LineRenderer needs no correction: its points are rebuilt from scratch every frame from
the (self-correcting) anchor position, never from stored history.

### `Vector3 anchorOffset`

Anchors ride the vessel's Transform, so their recorded baseline moves by whatever KSP
applied to the vessel. Mirror setOffset()'s own branch here so the discontinuity guard
in Update() never reads a legitimate origin shift as a teleport.

### `for (int i`

Per-index Get/SetPosition rather than the array form: TrailRenderer.positionCount is
read-only, so SetPositions(Vector3[]) cannot express "same count, moved" without
risking a truncation, and it would allocate on every physics frame that Krakensbane
is engaged — which, above the altitude gate, is all of them.

### `for (int i`

The tube's own history is world-space too and needs the same correction — but as a
plain array walk rather than two Unity interop calls per vertex, so it is strictly
cheaper than the TrailRenderer path above.

### `static float ExtentAlong(Bounds b, Vector3 dir)`

Projecting an axis-aligned Bounds onto an arbitrary direction is the SUM of the absolute
per-axis contributions. Vector3.Dot(extents, dir) lets opposite axes cancel, so it
understates the reach of any part whose box is not aligned with the direction being
measured — i.e. exactly the canted wings and angled winglets that were losing the
"which part sticks out furthest" comparison and handing the vortex to the wrong surface.

### `bool TryFindRadialTip(Part part, out Vector3 tipWorld)`

Finds the outermost mesh vertex of a part on the given side, in world space.

"Outermost" is span distance from the vessel's roll axis (x and y together), not raw
lateral x. On a winglet or canard angled up or down, the furthest point is displaced in Y
as much as in X, so an x-only scan settles mid-chord and leaves the anchor hanging beside
the surface rather than on its tip. On a level wing y barely varies across the part, so
this reduces to the previous behaviour.

The forward preference is a TIE-BREAK among vertices already at the tip, not a weight
added to the score. As a weight (lateral + forward * 0.25f) it could trade real span for
forward reach and walk the anchor inboard along a swept leading edge.

All of the part's MeshFilters are scanned, not GetComponentInChildren's first hit: KSP
wing parts routinely carry several sub-meshes and the first is not reliably the
aerodynamic surface. sharedMesh, not mesh — MeshFilter.mesh instantiates and leaks a
copy of the mesh on every access.
The point on a rocket fin where the vortex sheds: furthest from the ROLL AXIS, and among
those the furthest aft, since that is where roll-up completes. No side test — a fin
pointing straight up has every vertex at x ~ 0 and would fail the aircraft version's
"which side of the centreline" filter on both sides, which is exactly why two of the four
Redstone fins came back with no tip at all.

### `bool TryFindTipVertex(Part part, bool isRight, out Vector3 tipWorld)`

Kept so the aircraft path is untouched. theta 0 is +lateral and 180 is -lateral, and the
dot-product filter below reduces to exactly the old `local.x * side >= 0.05` for both, so
this delegation is equivalent rather than merely similar.

### `bool TryFindTipVertex(Part part, float outwardThetaDeg, out Vector3 tipWorld)`

Outward direction given as an angle about the roll axis rather than a side. A fin pointing
straight up has every vertex at x ~ 0 and fails a "which side of the centreline" test on
BOTH sides, which is why two of the four Redstone fins came back with no tip at all.

### `if (mf.GetComponentInParent<Part>() !`

GetComponentsInChildren walks the transform hierarchy, and in KSP a child PART is
a transform child — so drop meshes that belong to something attached to this
wing, or a wingtip-mounted pod wins the "furthest out" contest instead of the wing.

### `float span`

Distance from the ROLL axis: lateral and vertical together, never the fore/aft
term. Including chordwise distance here is what made the most-forward vertex of
a nose-mounted canard beat its actual tip.

### `float PartSpread(Vector3 dir)`

Resolves the vessel's geometric frame.

Local component indices cannot be trusted for this, and that is not a subtlety — it is
the bug that put canard anchors at the wing root. KSP parts are authored rocket-style with
the stack along +Y, so on an aircraft built from a cockpit the ROOT part's local Y is the
NOSE direction and local Z is VERTICAL, the exact opposite of the "z is forward, y is up"
convention this file's geometry code assumed. Measured on the test airframe: local Z spanned
3.9 m (the fuselage's height) while local Y spanned 21 m (its length).

Only the lateral axis was ever safe, which is why wingtips were still found and this stayed
hidden: on a big wing the lateral term dominates every distance it appears in. Give it a
short surface mounted far off-centre — a nose canard — and the mis-assigned axis takes
over. See TryFindTipVertex for exactly how that lands the anchor on the leading edge root.
Spread of PART POSITIONS along a world direction. Immune to renderer contamination, which
is exactly what the axis comparison needs.

### `float fwdSpread`

An aircraft is longer nose-to-tail than it is deep, so the airframe's own extent picks
the axis regardless of how the root part happens to be oriented.

Measured from PART POSITIONS, not from vesselBounds, and that distinction is the whole
fix for "reverting to launch moves the wingtip anchors". vesselBounds is built from
renderers, so any effect mesh that survives the filter inflates it — and it inflates
fore/aft and vertical together. A 17 m fighter measured 46.3 m long and 44.1 m tall,
a margin of 5%, which makes this comparison a coin flip decided by whichever effect
renderers happened to be enabled on that particular scene load. Flip it and the frame's
length and height axes swap, so the tip search's "most forward vertex" pass becomes
"highest vertex" and every anchor moves. Detection runs once, so it stays moved.

Part transforms are airframe by definition and cannot be inflated by anything. They
understate absolute extent (a wing panel's origin sits at its inboard node), but this
only needs the COMPARISON, and for that they are strictly better. On the same craft
they give roughly 16 m against 3 m: a 5:1 margin instead of 1.05:1.

### `Vector3 centroid`

Reference point is the PART CENTROID, not vesselBounds.center, for the same reason the
axis choice above no longer reads bounds — and this one is the more dangerous of the
two. An effect mesh trailing behind the engines drags the bounds centre aft; carry it
past the cockpit and the nose direction INVERTS. The tip search's second pass then
takes the most REARWARD vertex instead of the most forward one, so every anchor jumps
to the opposite edge of the wing. That is a flip with no visual warning and it lands
wherever the contamination happened to sit on that particular scene load, which is
exactly the "revert to launch moves the anchors" report.

### `Log($"axes resolved — nose`

All three full extents. The previous line printed length as a HALF extent next to two
full ones, which made a 9.4 m fuselage read as 4.7 m against an 8.9 m height.
Part spread is printed alongside, because it is what actually chose the axis and a
small margin there is the signature of the bug this replaced.

### `Vector3 ToVesselFrame(Vector3 world)`

World -> vessel frame as (lateral, vertical, forward), so the geometry code below can keep
reading .x/.y/.z as right/up/nose and actually be correct about it. Dots against the LOCAL
basis rather than the world properties: the tip scan calls this once per mesh vertex, and
the world form would run three TransformDirection calls on every one of them.

### `double depth`

Non-positive samples are discarded. Kopernicus and rescale mods can leave the curve
undefined at the very top of the atmosphere, and the modded install reported
"air 0.0-288.2K" — a 0 K reading would make the guard below inert on any body whose
real minimum is above the physical anchor.

### `bool TryGetPartBounds(Part p, out Bounds bounds, List<Renderer> accepted`

Renderer bounds for one part, with everything that is not part geometry rejected. Used by
every measurement in this file, so the rejection happens once and applies everywhere.
`accepted`, when given, collects the renderers that passed, for PartLateralRange.

### `if (r is TrailRenderer || r is LineRenderer || r is ParticleSystemRenderer) continue;`

Wake geometry, ours included. A TrailRenderer's bounds cover the entire trail, so
measuring the aircraft with one in scope measures the aircraft plus its own wake
and grows without limit.

### `static readonly List<Renderer> lateralScratch`

A part's reach along the craft's own lateral axis, widening [lo, hi]. Renderer.bounds is
a box aligned to the WORLD axes, so projecting it onto the craft's axes is only tight while
the craft is lined up with the world. On the runway it roughly is; banked, turned or pitched
in flight it is not, and span came out up to 1.8x (Su-33 at 19.7 m against 12.1 m, when
switched to mid-match). Span sets the circulation, the core size and the swirl, so the same
jet looked different depending on how it happened to be oriented when measured, and
BDArmory spawns every AI craft in the air. A mesh's own bounds are in its local frame and
rotate with it, so their corners are exact whatever the attitude.

### `float LateralReach(Part p, Bounds worldB)`

How far a part reaches from the centreline, in the craft's frame (see PartLateralRange).
The candidate ranking and the climb gate used |centre| + the world box's extent, which
inflated with attitude: a BDArmory-spawned F-22 put delta.small at 6.47 m on a 10.7 m span
and moved both anchors onto it from the wingtip it sits in a dead heat with when level.

### `float latLo`

Span + mass are read at the same throttled cadence as the rest of this method (mass in
particular: Vessel.GetTotalMass() walks every part and sums resource mass, so it's not
something to call from the per-frame Update() loop). See massSpanFactor's declaration.
Clamped as a last line of defence. Belt and braces with the rejection above: if some
future mod finds a way past it, the effect degrades to wrong-looking rather than to
invisible, and the log below says so.
Span in the craft's own frame (see PartLateralRange), not off the world-aligned box.

### `void FindVortexSources()`

SOURCE SELECTION

One rule, four slots: {left, right} x {main wing, secondary surface}. Within a slot the
winner is simply the part that reaches FURTHEST from the vessel centreline, which is what
"the wingtip" means. That single rule is what caps an aircraft at four vortices and what
stops a wing built from several chained panels producing one vortex per panel.

Three defects in the pass this replaced, all of which put duplicate sources on one large
multi-part swept wing:

  1. The main-wing pass read isLift/isControl and then never tested them, so ANY large
     enough off-centre part — engine nacelle, pylon, tank — could win the slot. Only
     the climb stage had the filter, under a comment calling it "the missing piece"; it was
     never back-ported to where candidates are gathered.

  2. The canard pass excluded the main wing by PART IDENTITY (p.part == leftRear), which
     does nothing when the wing is several parts. Its only other guard was "at least 0.2m
     ahead of the main wing", and on a SWEPT wing an inboard panel legitimately sits
     further forward than the tip panel — so an inner section of the very same wing
     cleared the gate and became a second, bogus "canard".

  3. Fore/aft was measured against vesselTransform, i.e. the ROOT PART, whose position is
     a build-order accident rather than anything about the airframe.

The replacement never asks "is this part forward of some plane" to decide whether a part
is a separate surface. It asks whether the part belongs to the same physically connected
aero structure as the main wing, which is the question that actually decides it.

### `ComputeVesselAxes();`

DIAGNOSTIC — full aero-surface inventory

Resolve the vessel's geometric frame before anything is measured against it.

### `Vector3 lp`

In the resolved frame: lat = span station, vert = height, fwd = nose-positive.
The raw local components this used to print were whichever axes the root part
happened to use, which on the canard airframe made "z" the fuselage HEIGHT.

### `List<AeroCandidate> radialPool`

Sectors are computed for EVERY craft, before either selection path, so the log shows
what the unified rule would do on aircraft and rockets alike. Consumed only when
useSectorSelection is set.

### `(inline)`

Evaluated on every detection rather than latched once. Slenderness is geometric, so it
does not flicker in flight the way attitude would, and re-deciding is what lets a
mid-flight vessel switch reach the right answer at all. The nose-up term is kept purely
as an extra way to say yes while still on the pad, for a rocket stubby enough to miss
the slenderness test.

### `pool.Sort((a, b)`

Sorted once, descending by lateral reach. Every "pick a winner" below is then just
"the first entry matching the filter", which is what makes the four-slot cap structural
rather than something that emerges from two heuristics agreeing.

### `float mainZLo`

Longitudinal span of the main wing. Second line of defence: a panel the flood fill
missed (a build where nothing is chained AND nothing quite touches) still has to sit
clear of the wing's own chord before it can count as a separate surface.

### `float mainExt`

── SECONDARY: the best surface that is NOT the main wing 

Forward and aft secondaries are NOT equivalent, and 0.6.7 was wrong to treat them as if
they were.

Every finite lifting surface sheds tip vortices, tailplanes included, so "does it shed
one" is the wrong question. What this mod draws is CONDENSATION, which needs the vortex
core's pressure drop to cool air past its dew point — and that drop scales with
circulation SQUARED. A conventional stabiliser carries a few percent of the aircraft's
lift (usually downward, trimming the nose-down moment of a CG ahead of the wing's centre
of lift), over a much shorter span, inside the main wing's downwash. Roughly: a tenth of
the wing's circulation is a hundredth of its core pressure drop. That is nowhere near
condensing, which is why photographs of hard-manoeuvring jets show vapour off wingtips,
LERX and flap edges and essentially never off the stabiliser tips.

A canard is the opposite case — clean undisturbed air ahead of the wing, a real share
of total lift, high AoA exactly when it matters — and those genuinely do show. Hence
forward is preferred outright.

The one aft case worth keeping is a true tandem wing, where the rear surface is a second
wing carrying a comparable load rather than a trim device. Span ratio separates them
cleanly: a stabiliser is roughly a third of the wing, a tandem wing nearly all of it.

### `float ratioL`

How big this secondary is next to the main wing. A primary canard on a canard aircraft
is a large fraction of the span and carries a real share of the lift; a nose-mounted
control canard is a fraction of that. The gate in Update() reads this so the two are not
held to the same threshold.

### `float SecondaryOnsetMult(float spanRatio)`

Strength for a secondary slot. Reaches exactly 1 for a surface that is a wingtip in all but
name, which also makes the gate in Update() skip it entirely (it is guarded on
strengths[i] < 1f), so such a surface is handled by the identical code path as a main wing
rather than by a secondary path tuned to imitate one.

Below the main-like band the floor is no longer FLAT. It used to be: every surface under
0.80 span ratio got exactly secondaryStrengthMin, so a canard reaching 15% of the wing and
one reaching 75% of it rendered at identical brightness. That is not a small inaccuracy
— what condenses is the vortex CORE PRESSURE DROP, and that goes as circulation
SQUARED. Two surfaces differing 5x in span differ roughly 25x in how visible their cores
should be.

So the floor now falls off quadratically as the surface shrinks, anchored so that a
surface AT the main-like threshold is unchanged: r = 1 gives exactly secondaryStrengthMin,
and everything at or above mainLikeRatioMin therefore behaves precisely as it did before.
Only small surfaces move, and they move in the direction the physics already argued for in
the secondaryOnsetMultMin comment.
How many times the main wing's onset load this surface has to reach before it condenses.
See secondaryOnsetMultMin for the derivation.

### `float band`

"Lowest on the stack" as a BAND, not a single value: a fin set is never at exactly one
station once the parts are placed by hand, and a grid fin sits at a different height
from a tail fin on the same rocket.

### `if (AddSourceAtAnchor(a.transform, 1f, 1f, !rocketUseRibbons))`

Tubes, not trails. This used to force the trail renderer on the grounds that the
tube models a counter-rotating PAIR converging under mutual induction and four fins
in a cross are not that. The objection was about the CURVE, and 1.1.5 settled it for
secondaries the same way it settles here: the inward displacement is already
ribbonInwardAmp * r.shed[src], so amplitude scales itself and a fin never pretends
to be half of a freely converging pair.

What forcing the trail actually bought was the LINE-MODE HANDOVER, and that is the
abrupt cut-off on a rocket ascent. A trail source is eligible for line mode, so
crossing lineModeSpeed clears the accumulated trail in one frame and hands the
source to the LineRenderer — which is then switched off outright by
`else { lr.enabled = false; }` with no fade at all when visibility drops. Two
discontinuities on the way up, neither of which a tube has: a tube source sets
shouldUseLine false permanently and ages out through its own alpha and radius.

### `List<AeroCandidate> GatherCandidates(bool radialGate)`

One gatherer, two gates. radialGate=false reproduces the lateral-offset behaviour exactly,
so the live path is bit-for-bit what it was; radialGate=true is the roll-invariant version
the sector pass needs, and it is a SUPERSET — a fin at lat=0, vert=0.61 is rejected by
the lateral gate and kept by the radial one, which is the whole Redstone problem in one
line.

### `Vector3 rel`

Measured from the bounds CENTRE, not transform.position: a wing panel's origin sits
at its inboard attach node, which badly understates where the panel actually is.
World-space dots, exactly as before. ToVesselFrame would have been the tidier
spelling but it goes through InverseTransformPoint, which also divides by the
transform's scale — so cProj would no longer be bit-identical to what the live
path has always used. The angle is scale-invariant either way, since both
components divide by the same factor.

### `List<List<AeroCandidate>> BuildSectors(List<AeroCandidate> pool)`

Cluster by angle about the roll axis. Sorted by theta, then a walk that opens a new sector
wherever the gap to the previous candidate exceeds sectorGapDeg, wrapping from last to
first. Deliberately a pure function of the candidate set — detection re-runs mid-flight
since 1.0.2, and any dependence on part order or on the previous result would make anchors
jump to different parts after staging.

### `void FindSourcesBySector(List<AeroCandidate> pool, List<List<AeroCandidate>> sectors)`

Sector x station, the direct generalisation of the four slots. One source per sector, plus
a fore/aft second within a sector when one sits clear of the main member — which is the
canard case, and the reason the station axis survives this rewrite untouched in spirit.

### `bool forceTrail`

A tube models a counter-rotating PAIR converging under mutual induction. Two wingtips
are that; three or four fins in a ring are not, which is why canards went back to
trails in 0.6.31 and fins are on trails in 1.0.3.

### `if (!axLengthSigned) continue;`

Station axis, within this sector. Fore is preferred outright and aft is only taken
at tandem scale — a stabiliser carries a few percent of the lift over a short
span inside the wing's downwash, and core pressure drop goes as circulation
SQUARED, so it is nowhere near condensing.

### `float ratio`

Span ratio against the widest reach on the whole vessel, so the existing gating decides
strength: on a rocket every fin reaches the same distance and they all score ~1, while
an aircraft's rudder reaches a quarter of the wing and is gated hard. Same rule,
opposite outcomes, and no craft-type classification anywhere.

### `HashSet<Part> BuildAssembly(List<AeroCandidate> pool, AeroCandidate seedA, AeroCandidate s`

Everything physically continuous with the seed parts.

Segments of one wing are either linked in the part tree or physically touching, and which
of the two depends on how the player built it: chaining panel to panel makes them
parent/child, while attaching each panel separately to the fuselage makes them siblings
that only adjacency relates. Testing BOTH is what makes this hold regardless of build
order. Anything landing in this set is the SAME wing and can never become a second source.

### `float margin`

Small, and deliberately so: too generous and a closely mounted canard gets swallowed
into the wing. Erring that way costs a vortex; erring the other way is the duplicate
this whole rewrite exists to remove.

### `bool AddSourceAtAnchor(Transform anchor, float strength, float spanRatio, bool forceTrail)`

Everything AddWingVortex does once it HAS an anchor. Split out so the rocket pass can
register a source without going near the aircraft-shaped tip search or the climb stage,
and so renderer setup exists in exactly one place.

### `private class WakeRibbon`

One vortex rope's worth of geometry and the wake history behind it.

Buffers are allocated ONCE at full size and rewritten in place. Every ring is always emitted,
even before the wake has filled — surplus rings collapse onto the tail point at zero
radius and zero alpha, which costs nothing visually and means the vertex count, the index
buffer and the triangle list never change size. KSP is GC-sensitive; per-frame reallocation
of a few thousand vertices is exactly the kind of churn that shows up as stutter.

### `public float[] odo;`

Total distance the wingtip had travelled when each ring was laid. This is the ONLY
thing the helix phase may key off: it is fixed at birth, so shed geometry cannot move
afterwards. Arc-from-the-head cannot be used, because the head keeps moving and every
insertion would then shift every existing ring's phase, sliding the whole pattern
along the rope — which is what dragged old wake when emission restarted.

### `float RibbonFlowVar(WakeRibbon r, float depth)`

Lays a new ring only once the wingtip has actually travelled far enough, so ring spacing is
a DISTANCE rather than a frame rate. That is what makes every downstream measurement —
age, arc length, roll-up, twist — exact rather than inferred from an index, which is the
estimate that made the TrailRenderer helix phase too noisy to hold a shape.
Brightness multiplier for the ring about to be laid, from the odometer so it is fixed in
space rather than in time.

### `Vector3 RibbonInward(WakeRibbon r, Vector3 anchorPos)`

Toward the ROLL AXIS, from the anchor.

This used to pick between +lateral and -lateral on the sign of the ring's lateral
offset, and that is the zig-zag. On a cruciform tail two of the four fins sit at
lateral offset ~0 by construction, so `side` hovers on the sign boundary and ordinary
airframe wobble flips it from ring to ring. Consecutive rings then get inward
directions pointing OPPOSITE ways, and because the displacement is scaled by `ease`,
which grows with distance from the head, the alternation opens into a sawtooth that
widens down the wake — exactly the shape in the report, and exactly why it settles
down when the craft stops manoeuvring and the wobble stops crossing zero.

The real quantity was never "which side of the centreline", it was "which way is the
axis from here". Projecting out the roll-axis component gives that directly: no sign
test, no boundary to sit on, and correct for a fin pointing straight up, where toward
the centreline means DOWN and neither lateral direction was ever right.

Unchanged for a wing — at a wingtip the radial-outward direction is lateral plus a
little dihedral, so inward still points essentially along the span toward the root.

Measured at the ANCHOR, once per frame, and never at a ring laid behind it. The rings a
frame lays are spread back along the flight path to where the last frame's head now sits,
V * frame time behind the tip: 6-12 m at 292 m/s. The flight path is not the roll axis,
it is angle of attack below it, so projecting out the roll axis at a point d metres back
leaves d * sin(AoA) of the flight path in the "radial" direction. Every frame's rings
were therefore tipped toward the belly by an amount that ramped from most at its first
ring to nothing at the tip, then jumped back at the next frame's: a sawtooth of up to
0.4 m on a 12 m span at 15-20 degrees AoA and 30-45 fps, as large as the tube itself,
one tooth per frame. Seen from the side and gone from above. It grows with airspeed times
sin(AoA), and holding 7+ g takes a high AoA at any speed, so every hard pull drew it, and
the ring smoothing never touched it: that only ever acted on the points. The anchor is a
fixed point on the rigid airframe, so this direction only turns as the airframe does;
Update() hands the rings in between a slerp from the last head's direction to this.

### `float SpiralLambda(float spacing)`

Spiral-breakdown wavelength for a given ring spacing: a turn shorter than
spiralMinRingsPerTurn rings cannot be drawn by this mesh and would alias rather than read
as small, so ring spacing floors it.

### `r.spiralPhase`

The spiral's phase advances by this step at the wavelength in force NOW, and is
stored rather than derived later as odo / lambda. Lambda is floored by ring
spacing, which follows speed and the wake lifetime and so moves a little every
frame; odo is the whole flight's distance, so phase = odo / lambda with the
current lambda moved by hundreds of radians per frame for a 1% change, and every
spiral already laid re-wound at random each frame at jet speed.

### `r.geVel[0]`

GROUND EFFECT for this ring. Height is the vessel's own height above the ground plus
this point's offset from it along local up, which assumes the ground under the wingtip
is level with the ground under the vessel — true to well under a metre across a span.
Drift is horizontal and outboard: the inward direction flipped and flattened onto the
ground plane, so a banked wing still drifts along the ground rather than into it.

### `float CoreDim(float growth)`

The conservation half of the same event: whatever diffusion widens, this dims. Always
paired with it, never applied on its own. See coreDimPow. Keyed on the growth it is handed
rather than on coreGrowthRate, so breakdown's diffusion still dims with core growth off.

### `float spacingNow`

Breakdown geometry for this frame, in seconds and metres. Both floored by ring spacing:
a bubble narrower than a few rings or a spiral turn shorter than eight simply cannot be
drawn by this mesh, and would alias rather than read as small.

### `Vector3 prevN`

Parallel transport. A naive Cross(tangent, up) frame flips violently wherever the
tangent passes near the reference axis; carrying the previous ring's normal forward and
re-orthogonalising it against the new tangent is what keeps the tube from writhing.

### `if (i > 0 && i < n) arc +`

Only live rings advance arc. Surplus rings all sit on the tail, so the old form
re-added that final segment once per dead ring and inflated arc roughly eightfold
(measured: 560 m across a wake that was actually ~70 m long).

### `float endPos`

Close the last stretch of the rope to nothing, in BOTH radius and alpha. Radius
matters as much as alpha here: without it the tube keeps its full cross-section to
the very last ring — wider than the middle at altitude, since tailRatio runs to
1.6 — and no amount of alpha fade hides a blunt tube end.

### `float coreG`

tailRatio and CoreGrowth are separate on purpose and both belong here. The core
itself only ever gets WIDER (diffusion is one-way), which is CoreGrowth; how much
of that core is still condensed is what tailRatio carries, and in dry air that
falls below 1 as the vapour evaporates and the pressure recovers. Visible radius is
the product: a real core, times how much of it you can see.

### `float bdP`

BREAKDOWN. Everything past the burst scales with depth, and depth is continuous in
swirl, so a stretch shed just past onset differs only slightly from one shed just
short of it — no step between neighbouring rings. Past the burst: extra turbulent
diffusion (paired with dimming like all diffusion), a bubble flare centred two
widths downstream so it rises from nothing at the burst point, and a fade that
is quick for a bubble and gentler for a spiral.

### `float ease`

Bend toward the centreline, easing in over ribbonInwardGrow and then holding, so
the rope converges a little and afterwards runs parallel. Direction was set at birth
and settles once the ring leaves the smoothing window; only the magnitude eases, and
it saturates within the first few metres, so nothing already shed can be re-aimed
by later manoeuvring.

### `Vector3 centre`

Scaled by the SOURCE's strength, not by this ring's shed. Shed is the live load at
the moment the ring was laid, so any wobble in the load (a pitch oscillation at high
q, FAR's control surfaces working) moved consecutive rings inward by different
amounts and the centreline zigzagged sideways — worst at altitude, where the wide
contrail tube makes it obvious. Where a vortex pair settles is set by the span
loading's SHAPE, (1 - pi/4) b/2 inboard for an elliptic wing, not by how much lift
it carries, so the offset should not follow the load at all. A secondary still
curves in by its fixed strength, which was the reason for scaling this in 1.1.5.

### `if (broken && bdBubble < 1f)`

Spiral breakdown. The basis is built from THIS ring's tangent and local up, not from
the transported frame above: that frame is re-derived from the head every frame, so
a turn at the wingtip would rotate it down the whole rope and set every corkscrew
already laid writhing. A ring's own tangent does not change once it is laid.

### `float seg`

TWIST ALIASING. A ring is laid per frame, so ring spacing is speed / frame rate: ~4 m
at 244 m/s and 60 fps, 8 m at 30 fps. At 0.25 rad/m that is 1-2 rad of twist between
neighbouring rings, and an ellipse repeats every half turn, so past a quarter turn
per ring the rotation aliases and the quads joining the rings draw a sawtooth — the
high-speed zigzag, worst on the wide contrail tube. Where this ring's own spacing
gets near that limit, its section eases to a round tube of the same area with no
seam, which has nothing to alias. Slow passes keep the full twisting-rope look.

### `if (!loggedRibbon && r.count > 120) // established wake, not a 21-ring stub`

One-shot ground truth. Two ambiguous visual reports in a row is the signal to measure
rather than guess again: this separates "the mesh is not rendering at all" from "it
renders but the twist has no visible signal", which need completely different fixes.

### `Bounds rCheckB;`

TryGetPartBounds, not a raw renderer lookup, and this is THE fix for "reverting to
launch moves the anchors". This test decides whether the anchor stays on the true
tip or climbs to a part above it, by comparing this wing's lateral reach against
the widest reach on the aircraft. Read raw, maxExt included whatever effect meshes
happened to be enabled on that scene load, so the wingtip could not clear 95% of it
— the guard failed, the climb ran, and the anchor was relocated inboard.

It is load-dependent because effect renderers are: on the first spawn the guard
held and the anchor sat on the tip; after a revert a plume was live, maxExt jumped,
and the same wing was suddenly no longer "the extremity". Nothing else in the frame
differed — same axes, same mass, same span, same chosen part.

### `if (restrictTo !`

The climb may only walk within the assembly this anchor belongs to. Without
this it can hop onto a DIFFERENT surface entirely — a short canard sitting
under the main wing is close enough laterally to satisfy the search radius, and
the canard's anchor would climb onto the wing and draw a second vortex there,
which is the very duplication the selection above exists to prevent.

### `if (p`

Never re-select the part we are standing on. Its bounds centre and its transform
origin are different points, so without this a part can clear its own "is it
higher" test and the climb spins on it until the safety counter stops it.

### `Vector3 centerLocal`

Projected extents, not the corners run through the transform: b.min/b.max are
corners of a WORLD-aligned box, and converting those two points into a rotated
frame does not give that box's extent in the new frame.

### `Vector3 finalTip;`

STEP 3: once at the highest part, recompute the tip on THAT part. If it has no usable
mesh we keep the STEP 1 tip, which at least sits on real geometry, rather than the
point the climb left behind — that one is projected to the top of an axis-aligned
bounding box, which for a canted surface is a spot in mid-air above it.

### `Bounds finalB;`

Seat the anchor just inside the surface. Nudging along -vesselTransform.up only
works for a level wing; on a canted or near-vertical winglet that direction runs
ALONG the surface instead of into it, which left the anchor floating just off the
tip. Nudging toward the part's own render centre is cant-agnostic.

### `if (vessel`

Bound to one craft for life (see `target`); WingtipVortexManager handles which craft
have a controller, including the one you switch to. `vessel` stays null until Start()
has waited out the craft's unpack and settle.

### `for (int i`

NOTE: this used to Clear() every trail whenever rawDelta crossed the threshold. A slow
frame is not an anomaly — the aircraft legitimately travels further and the resulting
segment is long but straight — so that test discarded the entire trail history for
the wrong reason while never catching the real cause (see OnFloatingOriginShift). The
targeted displacement check now lives in the per-vortex loop below.

### `staleCheckTimer +`

See staleCheckTimer. Rebuilding discards the wake, which is correct here rather than
merely acceptable: the wake being discarded is the one that belonged to the parts that
just left.

### `if (vessel.atmDensity <`

VACUUM. This used to return the instant density crossed 0.01, which cut the update
loop off in the middle of a fade that atmFactor was already running smoothly — and
the two renderers did not survive that cut the same way, which is what made leaving the
atmosphere look broken:

  A tube had been rebuilt on its last live frame with visible near zero, so its rings
  were already closed to no radius and no alpha. It vanished cleanly.

  A TrailRenderer had not. ApplyShedAppearance is what walks its recorded shed history
  and slides that profile toward the tail as the wake ages, and it stopped being called
  entirely. The trail froze at the width and colour of its last live frame and simply
  sat there, so the secondary vortices hung in the air after the main ones had gone.

There is no fade to special-case: atmFactor already takes visible to zero across the
0.01-0.10 density band, and both renderers close out through their own normal paths
when it does. So the early-out now fires only once there is genuinely nothing left to
draw, which keeps the cost saving in orbit without ever truncating a fade.

### `float liftKN;`

LIFT-DRIVEN LOAD FACTOR.
"A vortex is a direct byproduct of lift... its strength at any instant is set by how
much lift the wing is making right then." So the driver has to be lift, not felt G.
Those agree in a steady turn, which is why geeForce looked fine, but they come apart
exactly where it matters:
  - geeForce counts THRUST. A high-TWR craft climbing on engine power alone reads
    several G with the wings doing nothing, and used to spawn vortices for it.
  - geeForce counts impacts and collisions, which flashed the effect on touchdown.
  - geeForce MISSES the case the research calls out by name: spoilers up, lift dumped,
    vortices stop essentially instantly while the G reading barely moves.
ModuleLiftingSurface.liftForce is KSP's own per-surface aerodynamic lift, recomputed
every physics frame, so summing it is the real quantity rather than a proxy for it.
Dividing by weight keeps the result a load factor, so every threshold downstream
(the 3G onset, the 4.5G canard gate) keeps the meaning it was tuned with.
Under FAR, ModuleLiftingSurface is gone from every patched part, so liftSurfaces is
empty and there is nothing for the loop below to sum — sum FAR's wing modules instead.

### `float rho`

See onsetLoadRef: the thresholds move with rho*V so that constant CIRCULATION, not
constant load factor, is what the effect keys off. Unity at sea level / 250 m/s, so
everything tuned before this change behaves identically there.

### `float circFactor`

Circulation model (see massSpanFactor's declaration): mass/span component is cached on
the bounds timer, speed component is live. Clamped so it modulates strengths[i] rather
than dominating it.

### `bool lineModeAllowed`

REGIME SELECTION. The 8-15 km band used to hand off to LINE mode because the trail
module could not survive up there — that was the Krakensbane frame bug, now fixed,
so the band goes back to the trail module. Line mode is kept for the extreme-velocity
regime, where its reentry width clamp and stress-damped procedural terms still earn
their place and where accumulated trail geometry would outrun its own resampling.
Gated on the MODE, not on the renderer. Suppressing only shouldUseLine would leave
useLineMode still flipping at lineModeSpeed, which drives lineActivationBlend, which
takes trailFade to zero — and then shouldUseTrail goes false too and the booster
ends up with no wake at all rather than the trail it is supposed to keep.

### `float sunElevation;`

Approach-vapour floor (see humidDensityMin). Every term has to be true at once: deep
in the atmosphere, a wing working near its lift limit, AND that wing carrying the
aircraft rather than the gear. Cruise fails the second, a high-altitude pass fails the
first, and the takeoff roll fails the third — which is the one it used to pass,
because the CL proxy is scale-free in speed and says nothing about who holds the weight.
How lit the wake is, 0 (night) to 1 (day), on any body. See nightFloorScale and
VortexVapor.Sunlight, which the wing vapor shares.

### `float vRef`

The same circulation the whole effect is keyed to: Gamma = L/(rho*V*b). Read from
summed lift rather than felt G for the reasons given at massSpanFactor. Hoisted out of
the core-growth block because ground effect and breakdown need it too, with or
without core growth switched on.

### `float wakeLife`

Reported at the wake life this regime actually gets at a steady frame rate —
the same lerp targetTime makes, maneuverTimeMax below the band and
contrailTimeMax inside it. The previous form used contrailTimeMin for low
altitude, which understated a 3.5 s wake as 2.5 s.

### `(inline)`

GROUND EFFECT, vessel level. The raycast height sees the runway and KSC buildings where
the PQS terrain height does not, so it is preferred — but only while it agrees with
radar altitude to within 50 m. A raycast that has stopped updating would otherwise hold
a low reading into the climb, and a low reading is the one direction that can fake
ground effect. Over an ocean the sea surface is the ground plane, and a raycast that
passed through the water to the seabed would read high.

### `(inline)`

BREAKDOWN, vessel level. Swirl is smoothed because Gamma reads raw lift, and a stretch
of rope flickering in and out of breakdown would be exactly the popping this file has
spent its whole history removing. Gated on flyingGate for the reason given at GROUND
ROLL: on the runway the CL proxy reports gear attitude, not aerodynamics.

### `lineActivationBlend`

The altitude ceiling is gone with the altitude band. Fading out as you leave the
atmosphere is atmFactor's job below, and air density is the physically right reason for
the effect to stop — it is also body-relative for free, where a metres-based ceiling
could only ever have been correct on one planet.

### `float rbSpeed`

Guard reference speed: the faster of this frame and last. AddExcess() zeroes the
vessel's Unity-space velocity on the frame Krakensbane engages, so rb_velocity alone
— read after that happens — would understate the motion that frame.

### `Vector3 worldUp`

True "away from the planet" direction, not the vessel's own belly-to-canopy axis.
vessel.transform.up tracks aircraft ATTITUDE, so during a roll or inverted flight it
points sideways or straight down — wrong for anything meant to be gravity-relative
(downwash, wake curvature, sink). A shed vortex settles toward the planet regardless
of which way the aircraft that made it happens to be pointed right now.

### `float helixDrive`

PERSISTED-WAKE SINK. A shed vortex pair descends under its own induced flow — a few
hundred ft/min (~1.5 m/s) early on, slowing as it ages. Nudging every stored vertex
down each frame gives "older has sunk further" for free, since a point that has existed
longer has simply received more nudges; the per-vertex rate falloff supplies the
"slowing as they age" half.

Independent of OnFloatingOriginShift: both are pure translations of the same vertices,
so they commute in any order, and the discontinuity guard measures the ANCHOR transform,
which this never touches. Set trailSinkRate to 0f to compile it out at runtime.
How hard the wake is winding, from load factor. Keeps a floor at helixBaseDrive so a
1G cruise still shows some structure rather than nothing at all, and opens to full by
helixGFull. Faded out toward the contrail band, where a condensation trail is straight.

### `Vector3 emitter`

Which end is newest is determined at runtime rather than assumed, so this
does not rely on TrailRenderer's undocumented index ordering: the newest
point is whichever end is still sitting at the emitter.

### `if (frameGroundOn)`

Near the ground the image vortex cancels the descent, and the
cancellation tightens as the vertex gets lower, so a trail settles
toward the runway instead of sinking through it. Per vertex, from
its own height, since each has sunk a different distance.

### `float th`

Phase FROZEN per vertex. (now - age) is the instant this vertex
was laid down, which does not change as it ages, so the corkscrew
sits still in the air instead of spinning — and the per-frame
delta stays purely radial and small. A rotating phase could not
work at a visible wavelength: omega would be ~30 rad/s, roughly
half a turn per frame, which aliases into noise.

### `if (!loggedHelixCheck && s`

GROUND TRUTH. Measures how far the trail actually bows away from the straight
chord across its first 60 points. If the helix is accumulating, this lands
near the commanded amplitude; if the per-frame deltas are cancelling each
other out (which they would if the age-per-vertex estimate is noisy enough to
scramble the phase), it reads ~0 no matter what amplitude is commanded. That
distinguishes "not enough amplitude" from "the scheme cannot accumulate".

### `if (anchor`

Belt and braces with the staleness check above, which only runs twice a second:
a part can be destroyed between two of those and every read below goes through
this Transform.

### `if (contrailBlend > 0f)`

Cold air is already near saturation, so a contrail forms without needing a strong
core: the floor here is what makes the effect default-on up high and G-gated down
low. That split is real, and now it is drawn by temperature rather than by height.
Scaled by atmFactor, which the floor previously bypassed because it is applied
with Max(). That did not matter while the vacuum early-out was a hard cliff, but it
does now: ambient temperature above the atmosphere is cold, so contrailBlend goes
to 1 there, and an unscaled floor would relight every vortex at half strength in
orbit. No air, no condensation — whatever the temperature says.

Scaled by nightScale for the same class of reason: no light, nothing to scatter.
This was the one path in the frame that ignored the sun, which is why contrails
stayed bright on the night side while everything else went dim.

### `float mainLike`

Onset interpolated by span ratio: a full-size canard is held to nearly the main
wing's 3g, a small nose canard to 6g. See the field comments for why that is not
a fudge — the two are genuinely different aerodynamic cases.
Scaled by the same rho*V factor as the main wing, so the tightening the
secondary gate was given still holds exactly where it was tuned (cruise) while
a slow approach is judged on circulation rather than on raw g.

...and then blended toward the MAIN WING's own onset by tip likeness, so a
surface that reaches the outboard end of the span is judged as the wingtip it
is. At full likeness this is exactly the main-wing rule: onset onsetLoadRef,
no secondary gate at all.
See secondaryOnsetMultMin. The handicap is a multiple of the MAIN WING's own
onset, so it survives onsetScale intact: scaling both by rho*V leaves the ratio
untouched, which is exactly the property the old absolute figures did not have.

### `float gate`

NOT Lerp(gate, 1, mainLike), which is what this was and which was the whole
complaint. Lerping the GATE toward 1 does not make a near-main-wing surface
behave like a main wing; it bypasses a FRACTION of the gate, so the gate can
never reach zero. A canard at span ratio 0.85 has mainLike 0.26 and therefore
rendered at 26% of full strength no matter what the aircraft was doing —
dim, perfectly constant, and lit from the moment the main vortices appeared.
Exactly the reported symptom, and no amount of tuning the threshold could have
touched it, because the leak was downstream of the threshold.

Tip likeness already does its job on gMin just above: a surface that reaches
the outboard end of the span is judged against the MAIN WING'S onset. That is
the correct expression of "treat it as the wingtip it is" — a lower bar, not
a partly-disabled gate. At span ratio 0.95 and up SecondaryStrength returns
exactly 1, so this whole block is skipped and the surface is handled by the
identical code path as a main wing, which is what 0.6.38 actually asked for.

### `if (!loggedSecondaryGate && visible > 0.002f)`

One-shot, the first time any secondary actually lights up. Every term that
decides whether a canard is on screen, in one line, so the next round of this
is a log read rather than a theory.

### `float armRadius`

DISCONTINUITY GUARD — the distance-based replacement for the old frame-time test.
Origin shifts are already compensated in OnFloatingOriginShift, so anything left
here is a real teleport (vessel switch, docking, kraken): the anchor moved further
than its own Unity-space velocity can account for over the elapsed time.
rb_velocity is the Krakensbane-relative velocity, which is precisely what carries
the anchor through Unity world space, so this bound stays correct whether or not
the velocity frame is engaged — unlike true surface velocity, which would flag
every frame above the altitude gate.

### `obj.transform.position`

The emitter sits EXACTLY on the anchor and nothing displaces it, so the trail is
welded to the wingtip. The helix that used to live here now acts on stored vertices
instead (see the wake-evolution loop above), where it can start at zero and open out
with distance rather than swinging the origin around.

### `bool hasRibbon`

WHERE A TUBE EXISTS IT OWNS THE SOURCE OUTRIGHT — trail AND line.

The trail was already suppressed (the tube block below clears and disables it), but
LINE MODE was not, and that is what was visible growing to full length on the way
down before the tube appeared. The chain: re-entry runs well past lineModeSpeed, so
trailFade goes to 0, so shouldUseTrail goes false — and the tube's own point
feed was gated on shouldUseTrail, so the tube was starved and aged out while the
LineRenderer drew instead. Dropping back under the hysteresis speed then handed the
rope over, which read as one renderer growing and another replacing it.

Line mode exists because accumulated TrailRenderer geometry outruns its own
resampling at extreme speed. That reasoning does not transfer to the tube: ring
spacing already scales with speed against a fixed ring budget, and ribbonMaxLength
bounds the rope outright. So a tube source never needs the handover at all.

### `float shutdownK`

Hoisted out of the trail block below, which no longer runs for a tube source. The
tube took its lifetime from tr.time, and tr.time was only ever updated in there —
so left where it was, a tube source's lifetime would freeze at whatever the trail
last happened to set.

### `tr.time`

Low altitude: a short maneuver puff. High altitude: a long-lived condensation
trail. Vertex count is governed by frame rate * lifetime (a TrailRenderer adds
at most one point per update), so a 5 s trail at 60 fps is ~300 points per
vortex — which OnFloatingOriginShift then rewrites every physics frame.
That budget is what contrailTimeMax is set against. (stability and targetTime
are computed above, so a tube source gets them too.)

### `float life`

Same lifetime curve the TrailRenderer uses, so a tube and a trail on the same
aircraft stay the same length under the same conditions — but read from
targetTime directly rather than from tr.time, which no longer updates here.
Then bounded by ribbonMaxLength so a hypersonic re-entry cannot draw a 10 km
rope now that line mode no longer takes over to prevent it.

### `float spacing`

Ring spacing widens as needed so that a full lifetime of wake still fits inside
the ring budget — otherwise the tube is cut short by running out of points
long before it runs out of time. Now that the rope is close to straight, coarse
spacing costs nothing: there is no curve left to under-sample.

### `if (ribbonSmoothAmount > 0f && rb.count >`

See ribbonSmoothWindow. Translation-invariant, so it commutes with
OnFloatingOriginShift and needs no special handling there. The inward direction
gets the same pass: it is the second channel into the drawn centreline, and
smoothing only the points left any ring-to-ring noise in it drawn as is.

### `if (shouldUseTrail || tr.positionCount > 0)`

Width and colour are applied whether or not we are still shedding: a trail that has
stopped emitting still has to draw the history it is holding while that ages out.
startWidth/endWidth are deliberately no longer used — see ApplyShedAppearance.

### `if (tr.positionCount`

Oldest live point keeps aging until it hits tr.time and starts expiring. The
NEWEST one is pinned at age 0 only while we are still shedding — once the wing
unloads and emission stops, the head ages too, and the whole recorded profile has
to slide toward the tail rather than staying frozen where it was.
A trail that has fully aged out holds no geometry at all, so its recorded age span
has to reset with it. Without this, coming back down into the band would map a full
lifetime of shed history across a brand-new stub of trail, and the effect would
return wearing the width and opacity it had when it left rather than building up.

### `if (trailWasActive[i] && !shouldUseTrail)`

Only once the trail has actually finished fading, NOT the moment line mode
becomes eligible. lineActivationBlend exists to cross-fade the two, and both
are live while it runs — clearing here on trailWasActive alone destroyed a
full trail's worth of geometry in one frame at the crossover, which is the
abrupt cut-off on a rocket ascent. shouldUseTrail already goes false when
trailFade decays past 0.01, so that is the honest moment to reclaim it.

### `float seed`

Restored from 0.4.0 (dropped in 0.5.0): helical vortex core with Perlin
amplitude and frequency jitter. Damped by stressFactor for the same reason
the warp term is — deformation should calm down, not intensify, exactly
when the geometry is least stable.

### `Gradient grad`

Reused rather than `new Gradient()` here: this runs per line-mode source, every
frame. SetKeys below overwrites its keys before lr.colorGradient reads them, and
that assignment copies the data out, so sharing this one instance across sources
and frames is safe.

### `lr.startWidth`

The line used to be switched off outright the frame visibility crossed 0.01,
with no fade of any kind — the second discontinuity, and the one that shows
when a booster leaves the atmosphere. Nothing else updates its widths once
shouldUseLine is false, so they are simply driven to zero here and the renderer
is disabled only when there is nothing left to see.

### `float SampleShed(float[] hist, int back)`

Bakes the RECORDED shedding strength along the trail instead of stamping one global width
and alpha across the whole thing.

This is the behaviour the research is most specific about: circulation is set by the lift
the wing is making right now, so when the pilot unloads, the newly shed vortex collapses
immediately — but the vortex shed a second ago is already a free, detached structure and
keeps the strength it was born with until it decays on its own terms.

A TrailRenderer cannot express that through startWidth/endWidth, because those are global:
every frame they are re-applied across the ENTIRE trail, so letting them fall retroactively
thins geometry that was shed under 7G. What it DOES give us is widthCurve and colorGradient,
both parameterised 0 (head, at the wing) to 1 (tail, oldest). Driving those from a ring
buffer of past intensity makes each stretch of the trail render at its own shed strength.

The one approximation: the curve is parameterised along the trail, and we map that to AGE.
Those coincide at constant speed and skew slightly under hard acceleration.
Reads the ring buffer `back` samples behind the write cursor.

### `float maxHist`

The age span the CURRENT geometry actually covers, which is NOT [0, tr.time]:
  - straight after a Clear the trail holds only a fraction of a second, so mapping u=1
    to a full tr.time would read history this geometry never lived through;
  - once the wing unloads and emission stops, the head is no longer "now" either. It
    ages with everything else, so the span becomes [headAge, tailAge] and the strong
    stretch shed under high G slides toward the tail and expires, instead of sitting
    frozen at a fixed position along the trail. That migration IS the "it hangs in the
    air behind you and decays on its own" behaviour, so the mapping has to carry it.

### `float fadePow`

1.6-3.0 was far too flat to read as a gradient at all. At re-entry altitude
contrailBlend is high, so this sat at 3.0 and the curve was 1 - u^3: still 0.88 alpha at
the halfway point and 0.66 at three quarters, which on an additive shader is
indistinguishable from solid white. The intent behind the contrailBlend term is sound
— a contrail really does hold opacity longer than a manoeuvre puff — so it is
kept, just over a range where the difference is a gradient rather than a cliff at the
very end.

### `float groundRateMult`

Near-ground diffusion at the VESSEL's height, not per key. A trail stores no per-vertex
history to freeze it in, and the trail path is rockets and secondaries, neither of which
spends long enough near the ground for the difference to show.

### `float rollupMetres`

How much of the CURVE that roll-up distance corresponds to. The trail's live length is
its speed times the age span it currently covers, so the same few metres of taper stays
a few metres whether the trail behind is 50 m or 2.5 km.

### `int headKeys`

WIDTH — keys placed explicitly, most of them INSIDE the ramp. A power distribution
cannot help here: once the ramp is a fraction of a percent of the trail, no fixed
spacing lands enough keys inside it, so the taper falls between samples and vanishes.

### `widthKeys[k]`

Same split as the tube: tailRatio is how much of the core is still condensed,
CoreGrowth is the core itself diffusing. u maps to real age through the span this
geometry actually covers, which is what the growth law needs.

### `for (int k`

ALPHA, 8 keys (Gradient's limit). Same peak-window-then-taper shape the tube got in
1.1.13, sharing ribbonPeakFraction so the two renderers agree — a rocket on the trail
and an aircraft on the tube should not fade differently for no reason. The trail never
received that change because it builds its gradient here rather than per-vertex, which
is why boosters were still solid white long after tubes were not.

### `if (!loggedWidthProfile && i`

One-shot ground truth. Reasoning about which end of a TrailRenderer's width curve is the
head has been guesswork; this reports what the renderer actually holds, so if the end is
still square we can tell "the curve is wrong" from "the curve never arrived".
Gated on an ESTABLISHED, actually-visible trail. 0.6.12's version fired two seconds
after load on a five-point stub whose shed history was all zero, which told us nothing.

### `public float radialDist;`

Roll-axis-relative. radialDist is the 2D magnitude in the plane perpendicular to the
length axis, theta its angle in degrees — note this is NOT the same measurement as
extremity, which is a projection onto one axis, so radialDist >= |extremity| always.

### `public class WingtipVortexManager : MonoBehaviour`

Gives each craft worth drawing its own WingtipVortex controller. The craft you fly always has
one; other loaded, unpacked craft with lifting surfaces (AI wingmen, a BDArmory opponent) get
one too, nearest first, up to MaxControllers. Wing vapor is not part of this: it stays on the
active craft only (WingVaporAddon), because it runs a per-part aerodynamic solve every physics
step, where the vortices only read each part's stock lift.

### `static bool IsAircraft(Vessel v)`

A craft that can shed a wake and is being simulated. Unpacked only: a packed craft's parts
are not where the tip search needs them (see sourceReadyTimeout), and on rails it is not
flying through air anyway. The active craft skips this test, as it always has.


---

## AeroState.cs

### `public struct PartAeroState`

One lifting part's aerodynamic state for the current physics frame, in world space. This is
the only place either aero backend (stock or FAR) gets read from — everything downstream
(TrailedVorticity, WakeSimulation) takes these numbers and never touches
ModuleLiftingSurface or FARAPI directly.

### `public class PartBox`

A part's geometry in its own frame, measured once from its meshes and cached: its bounds
(for "is this point inside the part"), its thinnest axis (its surface normal), and its
planform, the outline of the part in the plane of the other two axes. Meshes with absurd
bounds are skipped, since visual mods inflate renderer bounds on purpose to defeat frustum
culling and one of those would otherwise poison every measurement.

### `public PlaneFrame Frame()`

Where the planform is now. The normal is read from geometry rather than from the lift
vector, so it never flips when the part's lift changes sign, which would swap its two
span edges and their ids.

### `public class VesselBodies : IBodyQuery`

The vessel's parts as solid volumes, for TrailedVorticity's carry-through test and the wing
vapor's buried-face test.

Refresh() takes a snapshot of every part's position once; BodiesAt then costs plain
arithmetic per part. Reading Transform.position for every part on every query was the
v0.4.7 F-22's frame-rate hole: a fighter's 137 parts, 200-odd queries per part measured.

### `public static class AeroState`

Reads per-part lift/drag every physics frame, from stock's ModuleLiftingSurface or from
FAR's FARWingAerodynamicModel, which FAR puts on wings in place of the stock module. FAR is
bound by reflection: no hard dependency, and a missing or incompatible FAR just leaves
FarAvailable false and the stock path running alone.

### `if (mf.GetComponentInParent<Part>() !`

Child parts hang under their parent's transform, so GetComponentsInChildren
walks into them. An outer wing panel measured as part of the inner one would
put the inner panel's span edge at the real wingtip.

### `public static void Surfaces(List<PartAeroState> parts, List<Surface> into)`

The lifting parts as TrailedVorticity takes them, lift in newtons.

vaporLift: under stock, a control surface that responds to roll (a taileron, an elevon,
an all-moving stabilator) condenses only from the load its symmetry group carries
together, the mean of their lifts. A pure roll deflects the two halves equal and opposite, the mean is zero,
and neither fogs; a pull loads both alike and they fog as before. Stock hands a small,
fully deflected surface a lift coefficient no real tail reaches, with no downwash from
the wing ahead and no lag before the roll rate damps it, so the half deflected with the
aircraft's lift used to fog at every roll input while the wing stayed clear. A STYLE RULE
like WingVapor.CondenseAgainstLift, not physics: a real stabilator deflected hard at high
load can condense briefly. The wake still sheds from the full lift.
Not applied under FAR: FAR models what the rule stands in for (the tail sits in the
wing's downwash, lift coefficients stall at realistic values), so a FAR tail surface
condenses from its own load and the physics decides.

### `static bool TryReadFarWing(PartModule pm, Vessel vessel, Vector3 flowDir, ref Vector3 lift`

FAR's per-wing force, split into lift and drag the way FAR itself splits it: drag is the
part along the airflow. FAR leaves worldSpaceForce at its last value when it stops
computing (out of the air, crawling, shielded in a bay), so those cases read as zero here.

### `public static List<PartAeroState> Read(Vessel vessel, Vector3 flowDir)`

Every lifting part reports its own lift/drag, from ModuleLiftingSurface under stock or
FARWingAerodynamicModel under FAR, so this is a real per-part read either way. One entry
per part: a part carrying more than one lifting module is still one surface with one
pair of span edges. flowDir is the unit direction of the vessel's motion through the air.


---

## BDArmoryCraft.cs

### `public static bool IsMissile(Vessel v)`

A fired missile: a craft carrying one of BDArmory's missile modules (anything derived
from MissileBase) and no weapon manager. The weapon manager is what separates it from an
aircraft with missiles still on its rails, which carries both.

BDArmory moves the active vessel onto a missile to follow it, so without this the wing
vapor followed the camera off the aircraft that fired it, and the vortex manager spent
a controller slot on it.


---

## Condensation.cs

### `public static class Condensation`

When does the air over a wing turn to cloud? No KSP types, so it can be checked offline.

Air flowing over the suction side of a wing drops in pressure, and it does so too fast to
exchange heat, so it expands adiabatically and cools: T_local = T (p_local / p)^((g-1)/g).
Its water vapour keeps the same share of the pressure (the mixing ratio is fixed), so the
vapour's partial pressure falls with p_local, but the saturation pressure falls much faster
with the temperature. Where the vapour ends up above saturation it condenses. That is the
whole effect: nothing here knows about g-load, Mach or aircraft type. A fighter in a hard pull
condenses because its loading, and so its suction, is huge; an airliner condenses only in
very humid air; a transonic wing condenses because its suction peak sharpens (Prandtl-Glauert).

### `public const float OnsetWater`

Visibility goes by how much water actually condenses, not by whether the air is just
past saturation: a trace of droplets is invisible, and the fog thickens with the
condensed mass. g of liquid water per kg of air at first sight, and at full density.
TUNED by eye against the reference, not measured.

### `public static float SurfaceSpread`

KSP has no humidity, so the air's moisture is a PLACEHOLDER, given as a dew-point spread
(how far the air must cool before it condenses) rather than a relative humidity. The
spread is what decides whether a wing fogs: over-wing condensation is seen when the
temperature is within a couple of degrees of the dew point, i.e. on a humid day, and
a fixed relative humidity turns into a muggy tropical day in KSP's hot lowlands (KSC
runs 305-308 K, where 66% is a 7 K spread). A fixed spread keeps the dew point
tracking the temperature, as it does through a real day.

Ordinary day: 10 K in the humid boundary layer (air denser than 62% of the body's
sea-level density, WingtipVortex's humidDensityMin, so both mods agree on where the
air is moist), 16 K above it. Cooling a wing's air 10 K takes about an 11% pressure
drop, which only a hard-pulled or near-transonic wing produces.

### `public const float CutoffRatio`

Where the vapor can exist at all: a HARD limit, by density relative to the body's
sea-level density (so it means the same on every body). Wing vapor is a lower-troposphere
effect: the air aloft holds almost no water, and a wing in thin air makes only a thin
suction. In air thinner than this the mod does no work at all: no vapor, no part reads,
no particle system, no log. (v0.4.8 ran whenever the surface speed was 5 m/s or more,
which in orbit is always: a station of hundreds of parts was read every step and the log
was written every second, and vapor still formed at 55 km.) About 14.3 km on
Kerbin (measured from a flight log; the verbose log records each crossing).

### `public const float CutoffHysteresis`

Coming back down, the mod resumes at the cutoff exactly; going up, it stops a little
past it, so a craft hovering on the limit does not clear and rebuild its vapor
measurements every few steps.

### `public static float Supersaturation(float kelvin, float pascal, float rh, float suction)`

Supersaturation e / e_sat of air that started at (T, p, RH) and has been sucked down by
`suction` Pa; and the water that condenses out of it, g per kg of air. The vapour's share
of the pressure is fixed, so what exceeds saturation at the new pressure and temperature
condenses (the latent heat it releases is neglected, which overstates it slightly).

### `public const float IdealCL`

Suction on the lifting side at chord fraction x (0 leading edge, 1 trailing edge), Pa,
for a section carrying a mean pressure difference `loading` at dynamic pressure q.

Thin-airfoil theory splits a section's lift in two. Up to its ideal lift coefficient
the camber carries it, spread smoothly along the chord, peaking mid-chord. Everything
above that comes from angle of attack and piles into a suction peak at the leading
edge, as sqrt((1 - x) / x) (the offset rounds off the singularity, as a real nose radius
does). So the peak grows faster than the lift: a wing working near its limit, slow and
hard-pulled, has a far sharper peak than the same load carried fast at a low lift
coefficient. That is why vapor, like WingtipVortex's trails, comes most easily on a
slow, high-alpha wing and needs more g the faster the aircraft goes. Compressibility
(Prandtl-Glauert) sharpens the peak further toward Mach 1 without changing the lift,
which stock's figure already includes, so it scales only the peak's excess over its
mean.

### `public static float MaxPeakCp`

The suction peak cannot grow without limit: past a peak pressure coefficient of a few,
the boundary layer behind it separates and the wing stalls, and the peak collapses
rather than deepening. KSP lets a wing carry far more lift than a real one (5 g at
70 m/s in flight logs), so without this a slow, hard-pulled wing got an impossible
suction peak. Real airfoils reach Cp_min of about -3 to -6 at maximum lift; the
incompressible limit is scaled by Prandtl-Glauert like the rest of the peak.

### `public static float MaxLocalMach`

The second ceiling, and the one that binds at speed. The air over the suction peak
speeds up as the pressure falls, and past a local Mach of about 1.3-1.4 it ends in a
shock strong enough to separate the boundary layer, which collapses the peak. So the
deepest suction is the isentropic pressure drop from flight Mach to that local Mach:
  Cp_max = (1 - ((1 + 0.2 M^2) / (1 + 0.2 Ml^2))^3.5) / (0.7 M^2)
It FALLS with Mach (5.8 at M 0.4, 3.4 at M 0.5, 2.3 at M 0.6), where the stall cap
scaled by Prandtl-Glauert rises (3.8, 4.0, 4.4). Using only that one let a 5 g pull at
M 0.6 reach a pressure drop larger than the whole ambient pressure and condense nearly
all the water in the air (20 g/kg in a flight log). Slow flight keeps the stall cap.

### `public static float PeakCpCap(float mach, bool far)`

Deepest suction coefficient the section can carry at this flight Mach, computed once per
step. Two tunings, picked by which aero model is flying the craft:

  Stock: the stall ceiling alone, scaled by Prandtl-Glauert, as through 1.3.0. Stock's
  lift is not tied to a real angle of attack, and the vapor onset under stock was tuned
  in flight against this cap; the shock cap on top of it made stock vapor very hard to get.

  FAR: the lower of that and the shock ceiling below. FAR flies the wing at a real angle
  of attack and loads it to real lift coefficients, so the physical limit applies.

### `public static float Suction(float camberShape, float peakShape, float leadLoading, float f`

With part of the loading carried by a deflected flap, aileron or elevator behind the
leading edge. Thin-airfoil theory treats a flap deflection as a change of camber: its
lift spreads along the chord (peaking at the hinge) rather than feeding the
leading-edge suction peak the way angle of attack does. So a hard-deflected control
surface loads its section without sharpening the peak where vapor forms first.
Loadings are signed toward the suction side of the whole section; `flapLoading` may be
negative (a flap working against the section).

### `public static void StripTerms(float leadLoading, float flapLoading, float q, out float cam`

The per-point shapes depend only on geometry, so they are computed once per part and
the per-step work is a few multiplies.
The same physics, split in two so the per-sample work is a few multiplies. A section's
loading fixes two terms for the whole strip; each sample then adds only its own shapes:
  cp = camberTerm * camberShape + alphaTerm * peakAdjust
where peakAdjust = max(0, 1 + (peakShape - 1) * pg) is Prandtl-Glauert's sharpening of
the peak's excess over its mean. Identical to Suction(...) above.

### `public class CondensationTable`

CondensedWater as a function of suction alone, for one ambient state: the ambient air is the
same for every point of the airframe in a step, so the exponentials are worked out on a
table once per step rather than once per point.


---

## LiftingLine.cs

### `public class LiftingLine`

Prandtl's lifting line over the airframe's panels, which stock aerodynamics leaves out: in
stock every part lifts as if it were alone in the flow, so nothing feels its neighbours'
downwash, and loading steps at every joint and never falls off toward a tip.

Each panel is one horseshoe. Thin-airfoil theory gives Gamma = pi c V alpha, and an induced
velocity v changes alpha by (v . l)/V, l being the panel's lift direction. So

    Gamma_i = Gamma2D_i + pi c_i (v_i . l_i)

where v_i is induced by every trailing line of the structure the panel belongs to. The
trailing lines start at the lifting line, so each induces half what an infinite line would.
It is taken averaged over the panel's span rather than at its midpoint, because a trailing
line can pass through the middle of a long panel (a flap edge, say). That average has a
closed form, finite everywhere except exactly on the line.

The system is linear in Gamma, so it is factorised once when the geometry or flow direction
is refreshed and back-substituted every step. Afterwards each connected structure is scaled
back to its stock lift: stock's total is what actually holds the aircraft up, and the wake
has to carry exactly that. Only the spanwise distribution comes from here.

Pure math on plain arrays, so it can be checked offline against textbook wings.

### `public void Solve(int np, float[] g2D, float[] gStock, float[] halfSpan, Vector3[] span, V`

Solves for Gamma from Gamma2D, then scales each connected structure back to its stock
lift. Falls back to stock's own loading if not factored or if the answer is implausible,
so the lift the wake carries is never lost.

### `Fallbacks`

Per structure: the lifting-line answer if it is plausible, else shared area alone,
else stock's own loading. Plausible means it needs a sane scale to reach stock's lift
and, once scaled, holds no more total circulation than stock's (1.5x): induction
redistributes and reduces loading, it does not pump up opposing panels that cancel.

### `public static double Kernel(Vector3 center, Vector3 span, float halfSpan, Vector3 gPos, fl`

Normal-to-lift velocity per unit circulation of a semi-infinite trailing line at gPos
(core a), averaged over the panel's span. With sigma along the span from the panel's
centre and H the line's offset from the span line (softened by the core):
  (1 / 2hs) * integral (sigma - sg) / ((sigma - sg)^2 + H^2) dsigma, over [-hs, hs]
  = ln(((hs - sg)^2 + H^2) / ((hs + sg)^2 + H^2)) / (4 hs)
times 1/(4 pi). Positive means upwash along l = down x span.


---

## Planform.cs

### `public class Planform`

A lifting part's outline in its own plane: the convex hull of its mesh, in metres, about the
centre of its bounds. Measured once per part. Pure geometry, so it can be checked offline.

It exists because a wing in KSP is usually many parts, often clipped into each other, and
stock gives every one of them lift for its full area. Two panels stacked in the same place are
one piece of wing, not two, and the samples below are how the model finds out which area is
shared (see PlanformOverlap).

### `public static class PlanformOverlap`

Which area of the airframe's lifting surfaces is shared. Every sample of every planform is
tested against every other planform; a sample covered by m planforms in all gives each of
them 1/m of its area. Stacked duplicates therefore add up to one surface, a part partly
overlapping another gives up only the overlapped area, and a flap butted against a wing
(touching, not overlapping) keeps all of its own.


---

## Sunlight.cs

### `public static class Sunlight`

How lit the air around a craft is, 0 (night) to 1 (day). Shared by the vortices and the wing
vapor, which both dim on the night side: the vortices because their additive material glows
like neon against a dark sky, the vapor because an unlit cloud is not white.

It is keyed to the sun's height above the craft's own horizon, not to vessel.solarFlux as it
was. Stock's solarFlux includes the atmosphere's absorption along the sun's path, so it falls
steeply as the sun gets low: an afternoon at KSC read as 20-45% lit while the sky was still
bright daylight, and both effects were drawn at a half to a third of their daytime strength,
exactly when a bright sky washes out an additive trail. Night is about the sky going dark,
which follows the sun's elevation.


---

## TrailedVorticity.cs

### `public static class TrailedVorticity`

Turns per-part lift into the trailing vortex lines the airframe sheds. There is no notion
here of a wing, a side, a tip or a group, and nothing is anchored.

THE MODEL. Every lifting part is cut into a few spanwise panels, and every panel is a
horseshoe vortex. By Kutta-Joukowski its bound circulation is Gamma = L / (rho V w), w being
its width across the flow. By Helmholtz a vortex line cannot end in the fluid, so at each of
the panel's two span edges that circulation turns and trails downstream: +Gamma at one edge,
-Gamma at the other. Where two panels meet, their trailing legs mostly cancel; where nothing
continues outboard, the whole bound circulation trails. That place is the wingtip, and no
code decides so.

A WING IS MANY PARTS. The same wing built as one part, as ten, or as ten with some clipped on
top of each other must shed the same wake. Stock does not give it one: every part lifts for
its full area, so a clipped stack lifts several times over and the loading piles up wherever
the builder stacked parts. Three things undo that, none of which classifies parts:
  - Shared area. Each panel's stock circulation is scaled by the share of its planform that
    no other lifting part also covers (PlanformOverlap). Stacked duplicates add up to one.
  - Resolution. A long part is cut into up to MaxSubPanels panels, stock's lift spread over
    them by local chord, so one long part and several short ones resolve the span alike.
  - Spanwise induction. Stock has none; LiftingLine adds Prandtl's, so loading falls off
    toward the tips and smooths across joints. Each connected structure is then scaled back
    to its stock lift, which is what actually holds the aircraft up.

Bookkeeping around the edges:
  - Welding. Edges at the same spanwise station are one line: panels meeting at a joint,
    or stacked along the chord (slat, panel, flap) and touching chordwise.
  - Carry-through. An edge that ends inside another part does not end in the fluid. When the
    edges of two lifting surfaces are buried in a part that is not one of their own surfaces,
    face each other and sit level with each other (a wing through a fuselage), the bound
    vortex passes through the body and the two are one line, which cancels in symmetric flight.
  - Folding. Lines too weak to matter are merged into their nearest same-sign neighbour,
    conserving circulation and its centroid, and the count is capped.

Roll-up happens in WakeSimulation, from the lines' induction on each other.

### `public const float BlobFraction`

...but never smaller than this fraction of the local panel width. Each line stands for
the vortex sheet shed across its panels, and a sheet cut into point vortices only
behaves like a sheet if neighbouring cores reach toward each other (the vortex-blob
overlap condition). A third keeps neighbours distinct (their cores do not touch)
while lines much closer than their spacing, like a narrow control surface's two edges,
overlap and net out instead of forming a dipole that flies off at the speed clamp.
Without it a pointed tip got a core of ~0 m (5% of a zero chord). v0.3.0 flights.

### `public const float LiftingLineRefresh`

How often the lifting-line matrix is rebuilt, s. It depends on geometry and on the flow
direction in the vessel's frame, both slow; stock's loads change every step and are
back-substituted every step. Rebuilding is cubic in the panel count (2 ms a step at 0.1 s
on a 242-part craft offline), and a few degrees of angle-of-attack change between
rebuilds barely moves the kernel.

### `if (rewelded || refresh)`

Carry-through depends only on the airframe (which edges weld, which parts they end
in), so like the welds it is worked out when those are, and replayed in between.
Its pairwise search is the costliest thing here on a big craft.

### `float gPart`

Kutta-Joukowski in vector form, rho * w * (Vinf x Gamma) = L, solves to
Gamma = (L x Vinf) / (rho V^2 w). Its component along s is the part's bound
circulation; the sign comes from the lift, so a part carrying download is negative.

### `static bool Weld(int m, float time)`

2. Edges at the same station are one line: side by side along the span (a joint, the
   two edges face each other) or stacked along the chord (parallel edges, parts touching
   chordwise). The chordwise offset is ignored when measuring, since two lines at the
   same cross-flow position are the same line downstream.

   Which edges weld depends only on the airframe and on sideslip, and the all-pairs test
   is the most expensive thing in this file (about two thirds of it at 66 parts), so the
   pairs are found once per AttachRefreshInterval, or when the edge set changes, and
   replayed every step in between.

### `static void PoolInsideBodies(int m)`

3. Carry-through. A group is buried when every one of its edges ends inside a part that
   is not itself one of the group's surfaces (so a joint between two panels, where each
   edge ends inside its neighbour, does not count). Two buried groups facing each other
   across the body, level with each other, are the same bound vortex passing through
   it. Mutual nearest only, so a wing root pairs with the other wing root and not with
   the canard root on the far side.
3b. Everything buried in the same solid body is one line through that body. Vorticity
    that trails from inside a fuselage cannot leave it there; the body carries it. So
    every buried group whose ends lie inside the same non-lifting part is pooled, and in
    symmetric flight the pool cancels. This catches what the facing rule above cannot:
    the joint between two parts clipped into a fuselage has no one outward direction,
    and a KSP fighter can have a dozen of them (v0.3.0 Su-33). A wingtip buried in a tip
    pod still trails from the tip, because nothing else shares the pod, and a wing does
    not count as a body here: its own bound vortex is already in the model.

### `float narrower`

`along` is the gap between them: positive across a fuselage, negative (down to
minus a panel's span) where two panels overlap. Far more negative means the
two point away from each other, like a pair of wingtip pods.

### `static void MatchStockLift(int m, Vector3 down)`

5. The wake must carry exactly the lift that holds the aircraft up, which is stock's. The
   lifting line already scales each structure's panels to it, but carry-through then
   adds lift of its own: the bound vortex spans the fuselage, which stock never gives any
   lift (19% of the total on the offline airliner check). So each structure is scaled
   once more, by the lift its trailing lines actually carry, rho V (t x sum gamma r).

### `static void Fold(Vector3 down, float threshold)`

6. First, lines whose cores overlap in the cross-flow plane are one line, exactly as the
   wake would merge them a step later (same sign: circulation-weighted centroid; opposite
   sign: the net, carried by the stronger). Then fold the weakest line into its nearest
   same-sign neighbour until every line clears the threshold and the count is within
   MaxLines. Circulation and its cross-flow centroid are conserved; a line with no
   same-sign neighbour is dropped.

### `public static bool PanelAt(int surface, Vector3 world, out float gamma, out float chord, o`

The loading the last Compute settled on at a point of surface `surface` (its index in the
list passed to Compute): the bound circulation and chord of the panel spanning that
point, and the share of that chord which is this part's own rather than a clipped
neighbour's. False if the surface got no panels. Used by the wing vapor, so the vapor
sees the same shared-area and lifting-line loading as the wake.


---

## VaporRenderer.cs

### `public class VaporRenderer`

Draws the wing vapor WingVapor computes: short-lived soft puffs over the points of every
lifting part that are condensing, all in ONE particle system riding the vessel's root part
(local simulation space). One system, not one per part: a 200-part craft would otherwise be
200 systems to update and 200 draw calls.

Riding the craft works because wing vapor only lives for a chord or two of flight: it forms
over the suction peak and evaporates once the pressure recovers past the trailing edge. So
there is no floating-origin correction and no world-space trail to manage. The puffs drift
aft, which draws the sheet streaming off the trailing edge. A control surface deflecting
after a puff is born does not carry it along, which over 0.2 s does not show.

Stock shaders only (no Unity Editor): KSP's alpha-blended particle shader, so the vapor
reads as white cloud in sunlight rather than a glow, tinted down on the night side on the
CPU. The puff texture is generated here, so there is no asset to ship or license.

### `public const float MaxPuffsPerSecond`

The whole craft's budget. A big craft in a hard pull would ask for far more; its puffs
are then fewer and larger, covering the same area, so the look holds while the
particle count and the overdraw (the GPU cost) stay bounded.

### `sum +`

Square root: where the vapor is thin it still gets puffs (faint ones, see
PuffAlpha below), so the sheet spreads over the wing instead of crowding
the few densest samples.

### `float d`

Thin vapor is faint and fine-grained, not a few opaque blobs: opacity and size
both grow with density, so the first trace of condensation reads as a haze
along the leading edge and thickens smoothly (v0.4.5: isolated puffs on the
A300's tail at onset looked like artifacts).

### `float rise`

Vapor forms on the suction (upper) surface only. A puff is a disc as wide as
3 m centred on its position, so centred on the skin it would hang half below
the wing. Instead it starts resting on the skin (centre one start-radius up)
and rises at exactly the rate its radius grows (SizeStart..SizeEnd), so its
lower edge stays on the surface for its whole life.


---

## WingVapor.cs

### `public int[] stripStart`

The wing section through each span strip: every lifting part the flow line through the
strip crosses (members stripStart[b] .. stripStart[b + 1] - 1). Every sample of a strip
shares them, so the search and the per-step sums are done per strip, not per sample.

### `public static class WingVapor`

Where the wing vapor forms. No KSP types, so it can be checked offline.

No detection and no anchors, the same rule as the wake: every point of every lifting part
gets the local loading the shedding model already settled on (stock lift, shared area for
clipped stacks, spanwise lifting line), spreads it along the chord the way an airfoil does,
and asks Condensation whether the air there condenses. So vapor shows on the middle of the
wings, where the loading is highest, first at the leading-edge suction peak and spreading
aft as the pull tightens; it thins toward the tips, where the loading falls away; a
tailplane pushing down condenses under itself; and in a bank each wing gets its own.

### `const float SpanRecheck`

How far the in-plane span axis may swing (as a cosine) before the strips are measured
again. 0.85 is 32 degrees: a chord lengthens by 18% over that, which does not show, and
a hard turn swings the flow across every deflected control surface by more than the old
6 degrees on nearly every step (the v0.4.7 F-22 spent 10 ms a step in it).

### `const int MeasuresPerStep`

Parts whose strips are re-measured per step at most, and parts whose buried faces are
measured per step at most (once per part, when the part set changes). A change swings
many parts at once, so the work is spread over a few steps instead of one hitch.

### `public static bool CondenseAgainstLift`

A section lifting against the aircraft's net lift is trimming it: a tailplane pushing
down, a canard set against the wing. KSP puts far more load on these than any real
aircraft does (a flown A300's tail at rotation pushed down at a lift coefficient of
about 1.5, past where a real tail stalls, and condensed while the wing stayed clear),
because stock has no downwash and craft are often trimmed with the centre of mass well
forward. So by default they make no vapor. A DELIBERATE STYLE RULE, not physics: real
fighter tailplanes can fog in a hard pull. Surfaces lifting sideways (a fin, cos of the
angle to the net lift above -AgainstCos) are not affected.

### `static float SectionChord(float chord, float share, bool far)`

The chord a panel's circulation is spread over when its section's loading is formed,
for a panel sharing `share` of its planform with no other lifting part. Two tunings,
picked by which aero model is flying the craft:

  Stock: the unshared share of the chord, as through 1.3.0, so a clipped stack carries
  its lift over the one real chord. The stock vapor onset was tuned in flight with it.

  FAR: the panel's FULL chord. FAR credits a clipped panel with its whole area and flies
  the wing at the angle of attack that gives, and the suction peak follows that angle.
  Each panel's circulation is already scaled back to its full lift, so a stack of k
  copies sums to k*Gamma over k*c: each panel's own lift coefficient. The share chord
  instead squeezed a clipped wing's lift onto a third of its chord (CL 2+ in level
  flight on a Su-33 whose panels own 30-60% of their outline) and fogged it at 1 g.

Unclipped panels have share 1 and read the same either way; the wake never uses this.

### `public static void Compute(List<Surface> surfaces, Vector3 down, float rho, float speed,`

Call right after TrailedVorticity.Compute on the same surfaces.
`bodies` answers which parts contain a point, for the faces buried inside a fuselage or
nacelle; null skips that test.

### `float own`

The share of this part's loading the vapor sees (Surface.vaporLift): 1 on
everything but a roll control surface, where it is how much of its load along
the net lift its symmetry group carries together. Zero in a pure roll. A part
lifting mostly sideways (a twin fin's rudder) keeps its whole load: its component
along the net lift is small and would make the ratio noise.

### `if (f.areaSignature !`

The mean pressure difference across the wing section at each strip, Pa: rho V
times the section's bound circulation (summed over every part in the chordwise
row, signed against this part's span axis) over the section's chord. Stacked
copies each carry their share of the circulation, so they add up to one wing.
Summing is what keeps a control surface in proportion: an aileron deflected hard
in a roll carries a large lift for its own small chord, but as part of the
section it only shifts the whole section's loading by its share. By Kutta-
Joukowski the lift points along +normal where the section's circulation is
positive, so that side is the suction side.

### `float side`

The section's loading over the members' own full panel chords (not
their unshared share, see chordOf), not the length of the flow line through the point: a
panel's circulation belongs to its whole width, and near a pointed or
slanted edge the local line shrinks to nothing while the circulation does
not (v0.4.4: fog at 0.7 g from a few points at the corners of the A300's
parts). What the parts behind the leading edge carry (flaps, ailerons,
elevators) is a camber change, spread along the chord, not an angle of
attack that piles into the leading-edge peak (Condensation.Suction).

### `at *`

A stalled section has no leading-edge suction peak: the flow has
separated from the nose, and what lift remains is spread over a flat,
shallow pressure field. So stall removes the angle-of-attack term, the
peak, and leaves the camber term. FAR reports stall per part (the
leading member of the section decides it); under stock it is always 0,
and Condensation.MaxPeakCp stands in for the stall stock never models.

### `float bound`

Can any sample of the strip condense at all? The most its shapes allow,
through the same physics: if that is below the visible onset, the strip
is quiet and its samples only need to fade out. Almost every strip of
every part is quiet almost all the time.

### `const float ChordGap`

The wing section through each span strip. A wing in KSP is often several parts in a row
along the chord (slat, clipped panels, flap, aileron), and measured on its own each would
put a leading-edge suction peak in the middle of the real wing and carry its own lift as
if it were a whole airfoil. So the section through a strip is the run of lifting
surfaces the flow line through it crosses, in the same plane (PlanformOverlap's slab)
and touching end to end, the same way for every part: its chord, and its member parts.
Each sample then takes its place along that chord (0 at the leading edge, whichever end
faces upstream) from where it lies along the flow line.

### `Vector2 c2`

Mean chord, but never more than the part's own length along the flow: a part nearly
edge-on to the span (a strake along the fuselage) has a tiny span and area/span
would be many metres.

### `static readonly List<int> order`

For the log: the parts condensing most, strongest first, as "name water g/kg, loading
Pa, side, with/against" where `side` is the face the vapor is on (the part's +normal
or -normal) and with/against says whether the part lifts with the aircraft's net lift
or against it (a tail in trim pushes down).

### `static readonly List<long> inside`

Which faces of each sample lie inside another, non-lifting part. KSP builders clip wing
roots, control surfaces and strakes into fuselages and engine nacelles, and stock still
gives those parts lift for their whole area; but a face inside a solid body has no flow
over it, so it cannot condense (v0.4.4: stray puffs at 1-1.5 g at an A300's wing roots
and nacelles). Lifting parts do not count as bodies here: overlapping wing panels are
the same wing (PlanformOverlap).


---

## WingVaporAddon.cs

### `public class WingVaporAddon : MonoBehaviour`

Wing vapor: the white sheet over the wings of an aircraft in a hard pull, worked out from the
physics of the air over each lifting part. A second addon in the WingtipVortex assembly; it
shares nothing with the wingtip-vortex code but the log tag and the version.

Wires AeroState -> TrailedVorticity (each part's loading) -> WingVapor (condensation at every
point of every wing) -> VaporRenderer (puffs riding the aircraft) every physics step, with no
anchors, no config file and no GUI.

### `public static bool Verbose { get; private set; }`

Per-second diagnostics (part loading, step times, what is condensing) are written to
KSP.log only when a file named verbose.txt sits in the mod's folder, next to Plugins.
Otherwise the addon logs one line per flight, and nothing more.

### `const int PartsPerStride`

The vapor's inputs are refreshed every `stride` physics steps, more rarely the bigger the
craft: one step in PartsPerStride-many parts' worth, at most MaxStride. The vapor is
smoothed over WingVapor.SmoothTime (0.12 s) anyway, so 4 steps (0.08 s) between updates
does not show. The loading and the vapor are computed on different steps of the stride
so neither lands on top of the other as a hitch.

### `Vessel skippedMissile;`

Moves the vapor to the active craft, except onto a fired missile (BDArmoryCraft.IsMissile):
BDArmory makes a missile the active vessel to follow it, and the vapor then left the
aircraft that fired it for a craft with no stock lifting surfaces to condense on. It
stays on the aircraft instead, which keeps drawing its own vapor while you watch the shot.
`skippedMissile` is the missile last declined, so the per-step poll does not re-test it.

### `if (vessel`

The aircraft the vapor was held on (see Follow) destroyed while the camera was on a
missile: nothing switched vessels, so drop its vapor here. Unity's == null is true
for a destroyed object whose reference is still set.

### `float Daylight() { return Sunlight.Fraction(vessel); }`

How lit the air around the vessel is, 0-1, measured the way the wingtip vortices measure
it (Sunlight): by the sun's height above the craft's horizon, so an afternoon sun low in
the sky no longer greys the vapor the way the atmosphere-absorbed solar flux did.

### `void LogValidation(List<PartAeroState> parts, float speed)`

Every second while there is vapor to look at, every five otherwise (verbose only): the
numbers the model rests on, so they can be checked against a real aircraft of similar
size and load.

### `Vector3 total`

Two lift figures, so the log shows how much a plain sum of magnitudes overcounts:
`sum` adds every part's |lift|; `net` is the component along the total lift
direction, which is what actually holds the aircraft up.
