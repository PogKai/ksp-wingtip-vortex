# How it works

[← Back to the README](../README.md)

This page is the physics behind the vortices and the wake. You don't need any of it to use the mod: it is here for the curious, and for anyone reporting a problem.

**On this page:** [Vortices](#vortices) · [Wake physics (1.2.0)](#wake-physics-120) · [Performance](#performance) · [Roadmap](#roadmap)

For the wing vapor, see [wing-vapor.md](wing-vapor.md).

---

## Vortices

Vortex strength follows circulation, **Γ ≈ L / (ρ · V · b)**:

* **Lift** is read per surface from KSP's own `ModuleLiftingSurface.liftForce`, not from felt G. Those agree in a steady turn but come apart where it matters: felt G counts thrust and impacts, and misses spoilers dumping lift entirely
* **Density and airspeed** move the threshold rather than the measurement. The same lift makes proportionally *more* circulation as speed and density fall, which is why a 96 m/s approach at 1 g sits at roughly 88% of a 3 g break turn at 250 m/s
* **Mass and span** enter as one quantity, m/b, soft-saturated. Constant wing loading means m ∝ b², so bigger simply means stronger, which is exactly how real wake-turbulence categories are drawn

Visibility is condensation, not circulation, so two saturation floors sit on top:

* **Cold air** near ice saturation, from `vessel.atmosphericTemperature` against physical anchors (233 K / 253 K), with the band checked against each body's own temperature curve at load
* **The humid boundary layer** near the surface, from air density against the body's own sea-level density combined with how hard the wing is working for its speed

### What the mod does, step by step

1. Detects valid lifting surfaces, rejecting non-part geometry
2. Resolves the airframe's own geometric frame rather than trusting the root part's axes
3. Finds the true wingtip from mesh vertices, measured from the roll axis
4. Groups physically continuous surfaces so one wing cannot produce several vortices
5. Places anchors: {left, right} × {main, secondary} on aircraft, or one per fin on a rocket
6. Applies visual behaviour based on flight conditions

Detection re-runs by itself when it needs to (on staging, part loss or docking), so a wake never keeps drawing from parts that have left the craft.

### Every aircraft (1.3.0)

Each craft gets its own vortex controller, bound to it for its whole life: the one you fly, plus other loaded aircraft with lifting surfaces, nearest first, up to ten in all. Debris, EVA kerbals, flags and BDArmory missiles are skipped. Because each wake belongs to its own craft, switching vessels touches no wake at all: the craft you leave keeps its trails, and the one you take over already has its own.

The airframe is measured in the craft's own frame, so span and the wingtip search give the same answer whether the craft is sitting level on the runway or was spawned banked in the air.

### Details worth knowing

* Aircraft vortices are drawn with a procedural tube mesh that curves inward under mutual induction, the way a real counter-rotating vortex pair does. Rockets use trail rendering throughout, which suits a craft that spends its whole ascent rotating
* **Body-relative**: nothing is keyed to altitude in metres, so behaviour is correct on every planet without per-body tuning
* Craft type is identified by **shape**, not launch attitude, so it survives a mid-flight vessel switch
* Mesh-accurate wingtip detection finds the true aerodynamic tip; a canard that reaches the outboard end of the span is treated as the wingtip it is
* Secondary surfaces (canards, small fins) need real load to appear, and their wakes are short: a canard vortex bursts over the wing within a chord or two rather than trailing
* Capped at four vortices per aircraft, structurally
* Smooth fade in and out at every threshold, no popping; scales with aircraft size
* The rope starts as a thin, faint thread at the tip and builds to full width and brightness over its first 40%, the way tip-vortex condensation thickens as the core rolls up
* Robust against visual mods that use oversized renderer bounds

---

## Wake physics (1.2.0)

Three pieces of real physics act on the vortices themselves.

| Effect | What it is | The physics |
|---|---|---|
| **Viscous core growth** | A heavy aircraft's wake stays a tight rope; a light one goes soft in seconds | Lamb-Oseen diffusion, r(t) = √(r₀² + 4αν t), fed by Squire's eddy viscosity (proportional to the vortex's own circulation) plus a molecular term from Sutherland's law. Widening is paired with dimming, because circulation is conserved |
| **Ground effect** | Behind a low pass the two ropes splay apart instead of running parallel | A mirror vortex of opposite sign under the ground cancels the pair's descent, pushes each vortex outboard, and raises near-ground diffusion. It changes where the rope goes, never whether it is drawn |
| **Vortex breakdown** | A core kinks and throws a turn or two before dispersing (spiral), or bursts into a bulge (bubble) | Set by the swirl ratio, peak tangential over axial velocity, with thresholds from Spall, Gatski and Grosch (1987). The burst moves toward the tip as swirl rises |

There is no laminar/turbulent switch, on purpose: across every flight case that draws anything, the vortex Reynolds number is 10⁶ to 10⁸, so the wake is always turbulent.

Each effect logs a one-shot line the first time it engages, and `[VORTEX] session peaks` summarises the flight when you leave it. The breakdown thresholds are literature values applied to an estimated swirl, so that line is what says whether they are in the right place.

**FAR:** Ferram Aerospace Research replaces the stock lifting modules, so wings are also found by FAR's own (`FARWingAerodynamicModel`, `FARControllableSurface`), and each wing's force is read from it by reflection, with no dependency on FAR. The load factor sums the wings' lift, as under stock, rather than FAR's whole-vessel force, which includes fuselage body lift. The wing vapor reads each wing's lift and stall; a stalled wing loses its leading-edge suction peak. Two vapor readings are FAR-only, because FAR flies the wing at a real angle of attack: the suction peak is also capped where the flow over the wing would reach local Mach 1.35, and clipped panels are read at their full chord (FAR's own lift coefficient) rather than their unshared share. Under stock both stay as in 1.3.0.

**Rigid wake anchor:** the wake follows each wingtip's offset from the root part, captured when the vortex source is set up, not the tip part itself. KSP's part joints let a many-part wing flex by up to 2 m at the tip in a hard pull, and a wake that recorded it zigzagged. `session peaks` reports the flex removed as `tip flex peak`.

---

## Performance

* Vortices use no particle systems: tube meshes on main vortices, trails on secondary surfaces
* Vessel measurement and mass lookups are throttled, not per-frame
* One controller per aircraft, capped at ten, nearest first: each rebuilds its own tube meshes every frame, so a large BDArmory match costs more than a duel
* Cross-section geometry uses a precomputed unit-circle table
* The wing vapor's cost is on [its own page](wing-vapor.md#performance)

---

## Roadmap

* Better handling for extreme geometry and modded parts
* Optional debug visualization for vortex spawn points
* Manual placement / override system
* Further refinement of curl and dissipation behavior
