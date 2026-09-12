# KSP Wingtip Vortex Mod

Adds dynamic, geometry-aware wingtip vortices to aircraft in Kerbal Space Program.

---

## Overview

This mod generates wingtip vortices that react to how you’re flying and how your craft is built. It supports complex wing setups — multi-part wings, canted surfaces, forward canards — and, as of 1.1.0, vertically-launched rockets, whose fins are found radially rather than by which side of the centreline they sit on.

Aircraft vortices are drawn with a procedural tube mesh that curves inward under mutual induction, the way a real counter-rotating vortex pair does, and that tube is the only renderer for those sources at any speed. Rockets use trail rendering throughout, which suits a craft that spends its whole ascent rotating.

---

## Features

* Vortices respond to **circulation** — load factor, air density, airspeed, mass and wingspan together, not G-force alone
* Appear on a slow, high-alpha landing approach as well as in a hard break turn
* **Body-relative**: nothing in the effect model is keyed to altitude in metres, so behaviour is correct on every planet without per-body tuning
* Procedural tube mesh for main vortices, trails for secondary surfaces
* Supports complex geometry:
  * Multi-part wings
  * Stacked wings
  * Canted / angled wing tips
  * Forward canards
  * **Rocket and booster fins**, any count, any radial arrangement
* Craft type is identified by **shape**, not launch attitude, so it survives a mid-flight vessel switch
* Secondary surfaces (canards, small fins) need real load to appear, and their wakes are short — a canard vortex bursts over the wing within a chord or two rather than trailing
* Mesh-accurate wingtip detection that finds the true aerodynamic tip
* A canard that reaches the outboard end of the span is treated as the wingtip it is
* Capped at four vortices per aircraft, structurally
* Smooth fade in and out at every threshold — no popping
* Scales with aircraft size
* Robust against visual mods that use oversized renderer bounds

---

## Requirements

* No dependencies

---

## Compatibility

* Works with most aircraft and lifting surfaces
* Built around stock aero modules (`ModuleLiftingSurface` / `ModuleControlSurface`)
* Works alongside visual mods including Scatterer, EVE, Parallax, Deferred and Singularity
* Works with Kopernicus and rescale mods — the atmosphere model reads each body's own curves
* Supports modded aircraft, though detection depends on proper aero modules

---

## Installation

Drop into `GameData/`

---

## How It Works

Vortex strength follows circulation, Γ ≈ L / (ρ · V · b):

* **Lift** is read per-surface from KSP's own `ModuleLiftingSurface.liftForce`, not from felt G. Those agree in a steady turn but come apart where it matters — felt G counts thrust and impacts, and misses spoilers dumping lift entirely.
* **Density and airspeed** move the threshold rather than the measurement. The same lift makes proportionally *more* circulation as speed and density fall, which is why a 96 m/s approach at 1 g sits at roughly 88% of a 3 g break turn at 250 m/s.
* **Mass and span** enter as one quantity, m/b, soft-saturated. Constant wing loading means m ∝ b², so bigger simply means stronger — which is exactly how real wake-turbulence categories are drawn.

Visibility is condensation, not circulation, so two saturation floors sit on top:

* **Cold air** near ice saturation, from `vessel.atmosphericTemperature` against physical anchors (233 K / 253 K), with the band checked against each body's own temperature curve at load
* **The humid boundary layer** near the surface, from air density against the body's own sea-level density combined with how hard the wing is working for its speed

The system:

1. Detects valid lifting surfaces, rejecting non-part geometry
2. Resolves the airframe's own geometric frame rather than trusting the root part's axes
3. Finds the true wingtip from mesh vertices, measured from the roll axis
4. Groups physically continuous surfaces so one wing cannot produce several vortices
5. Places anchors: {left, right} × {main, secondary} on aircraft, or one per fin on a rocket
6. Applies visual behaviour based on flight conditions

Detection re-runs by itself when it needs to — on staging, part loss, docking, or switching craft — so a wake never keeps drawing from parts you are no longer flying.

---

## Controls

* None
* Fully automatic

---

## Performance

* No particle systems
* Tube meshes only on main vortices; secondary surfaces use trails
* Vessel measurement and mass lookups are throttled, not per-frame
* Cross-section geometry uses a precomputed unit-circle table

---

## Known Issues

* Highly canted or unconventional wing setups may have slight placement offsets
* Front vortex detection depends on valid lifting surfaces
* Very large aircraft can expose edge cases in placement or scaling
* Low FPS environments may introduce minor visual instability

---

## Troubleshooting

Search `KSP.log` for `[VORTEX]` — the mod logs its full selection pass, including a part inventory, every candidate with its lateral reach, which slots were filled, and the measured airframe.

**No vortices showing**

* Ensure your craft has lifting/control surfaces
* Ensure the craft is airborne — vortices are suppressed on the ground by design
* Check the measurement line: `[VORTEX] measured <craft>: span=... mass=... sizeFactor=...`. A wildly wrong span means a mod's renderer got past the bounds filter; the log will name it.

**Nothing at altitude**

* Check `[VORTEX] contrail band on <body>` for the sampled temperature range

---

## Roadmap

* Better handling for extreme geometry and modded parts
* Optional debug visualization for vortex spawn points
* Manual placement / override system
* Further refinement of curl and dissipation behavior

---

## Changelog

See [CHANGELOG.md](CHANGELOG.md).

---

## License

MIT

---

## Author

PogKai

---

## Version

v1.1.0

---

Link to KSP Forum post:  
https://forum.kerbalspaceprogram.com/topic/230208-ksp-vorticescontrails-mod-release/#comment-4508713
