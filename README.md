<div align="center">

# KSP Wingtip Vortex

**Physics-based wingtip vortices, contrails and wing vapor for Kerbal Space Program**

[![Release](https://img.shields.io/github/v/release/PogKai/ksp-wingtip-vortex?style=flat-square&label=release&color=2b7bb9)](https://github.com/PogKai/ksp-wingtip-vortex/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/PogKai/ksp-wingtip-vortex/total?style=flat-square&color=2b7bb9)](https://github.com/PogKai/ksp-wingtip-vortex/releases)
[![KSP](https://img.shields.io/badge/KSP-1.12.x-4c9a4c?style=flat-square)](#requirements-and-compatibility)
[![License](https://img.shields.io/badge/license-MIT-lightgrey?style=flat-square)](license.md)
[![Dependencies](https://img.shields.io/badge/dependencies-none-lightgrey?style=flat-square)](#installation)

[**Download**](https://github.com/PogKai/ksp-wingtip-vortex/releases/latest) &nbsp;·&nbsp;
[Features](#features) &nbsp;·&nbsp;
[Installation](#installation) &nbsp;·&nbsp;
[How it works](#how-it-works) &nbsp;·&nbsp;
[Troubleshooting](#troubleshooting) &nbsp;·&nbsp;
[Changelog](CHANGELOG.md) &nbsp;·&nbsp;
[Forum thread](https://forum.kerbalspaceprogram.com/topic/230208-ksp-vorticescontrails-mod-release/#comment-4508713)

</div>

---

## Overview

Vortices, contrails and vapor that react to how you are flying and how your craft is built. Drop it in and it works: no GUI, no config, no per-craft setup.

* **Wingtip vortices** that follow the real circulation of the wing, on multi-part wings, canted surfaces, forward canards and vertically-launched rockets
* **Wing vapor** (new in 1.2.0): the white sheet that forms over a fighter's wings in a hard pull, worked out from the physics of the air over each lifting part rather than from a list of triggers
* **Wake physics** (new in 1.2.0): cores that spread the way a real wake's do, a ground that pushes the vortex pair apart, and strongly loaded cores that can burst

Aircraft vortices are drawn with a procedural tube mesh that curves inward under mutual induction, the way a real counter-rotating vortex pair does. Rockets use trail rendering throughout, which suits a craft that spends its whole ascent rotating.

---

## Features

| Effect | What you see | Driven by |
|---|---|---|
| **Wingtip vortices** | A counter-rotating tube pair curling inward behind each wingtip; trails on rocket fins and secondary surfaces | Circulation: load factor, air density, airspeed, mass and span together |
| **Wing vapor** *(1.2.0)* | A haze along the leading edge that spreads back over the wing as the pull tightens | Pressure drop over each wing section, the cooling that goes with it, and the water that condenses out |
| **Viscous core growth** *(1.2.0)* | A heavy aircraft's wake stays a tight rope; a light one goes soft in seconds | Lamb-Oseen diffusion fed by Squire's eddy viscosity; widening is paired with dimming |
| **Ground effect** *(1.2.0)* | Behind a low pass the two ropes splay apart instead of running parallel | A mirror vortex under the ground: descent cancelled, each vortex pushed outboard |
| **Vortex breakdown** *(1.2.0)* | A core kinks into a corkscrew, or bursts into a bubble, closer to the tip the harder the load | Swirl ratio (peak tangential over axial velocity) |

<details>
<summary><b>Wingtip vortices in detail</b></summary>

&nbsp;

* Respond to **circulation** rather than G-force alone, so they appear on a slow, high-alpha landing approach as well as in a hard break turn
* **Body-relative**: nothing in the effect model is keyed to altitude in metres, so behaviour is correct on every planet without per-body tuning
* Supports complex geometry: multi-part wings, stacked wings, canted / angled wing tips, forward canards, and **rocket and booster fins** in any count and radial arrangement
* Craft type is identified by **shape**, not launch attitude, so it survives a mid-flight vessel switch
* Mesh-accurate wingtip detection that finds the true aerodynamic tip; a canard that reaches the outboard end of the span is treated as the wingtip it is
* Secondary surfaces (canards, small fins) need real load to appear, and their wakes are short: a canard vortex bursts over the wing within a chord or two rather than trailing
* Capped at four vortices per aircraft, structurally
* Smooth fade in and out at every threshold, no popping
* Scales with aircraft size
* Robust against visual mods that use oversized renderer bounds

</details>

<details>
<summary><b>Wing vapor in detail</b></summary>

&nbsp;

Real wing vapor is not an effect somebody switched on. Air flowing over the top of a wing drops in pressure, and because it does so too quickly to exchange heat it also cools. If it cools below its dew point, the water in it condenses into cloud, which vanishes again once the air passes the wing and recovers.

The mod does that calculation for every lifting part, every physics step:

1. **How hard each part is working**: its lift, corrected for the other parts around it. A wing built from many parts, clipped parts and stacked parts are treated as the one wing they form, and control surfaces are part of the section they sit in
2. **How that load is spread along the chord**: smoothly with the camber, and piled into a suction peak just behind the leading edge as the angle of attack grows
3. **How much the air cools** going through that pressure drop, from the local pressure and temperature
4. **How much of the water it holds condenses out**

What you see:

* A thin haze along the leading edge first, spreading back over the top of the wing as the pull tightens, thinner toward the tips
* **It is hard to get.** A passenger jet at its structural limit shows a faint trace at most. A fighter needs a real pull in humid air. A takeoff roll, cruise or a slow approach shows nothing
* Slow, hard-working wings condense sooner than fast ones at the same load, and wings near Mach 1 sooner still, as real ones do
* Different on each wing in a bank
* White in sunlight and dimmed on the night side, from the star's actual flux, so eclipses and planet shadow are handled

Limits, on purpose:

* **Lower atmosphere only.** Air thinner than 10% of the body's sea-level density (about 14 km on Kerbin) has a hard cutoff: no vapor, and the wing-vapor code does no work at all, so orbit and space stations cost nothing
* **A surface lifting against the aircraft's net lift makes no vapor**: a tailplane trimming the aircraft, a canard set against the wing. KSP loads these far harder than a real aircraft would, and they looked like artifacts
* **KSP has no weather**, so the air's moisture is a stand-in: a fixed dew-point spread (12 K in the humid lowest layer, 18 K above it). It does not follow the time of day
* Wing vapor is drawn on the active vessel only, as a particle effect: not volumetric, no shadows

</details>

---

## Installation

1. Download `WingtipVortex_<version>.zip` from the [latest release](https://github.com/PogKai/ksp-wingtip-vortex/releases/latest)
2. Unzip it and copy the `WingtipVortex` folder (it sits inside a `GameData` folder in the zip) into your KSP `GameData/`
3. Fly

```text
Kerbal Space Program/
└── GameData/
    └── WingtipVortex/
        ├── Plugins/WingtipVortex.dll
        └── ...
```

To upgrade, replace the old `WingtipVortex` folder. To remove it, delete the folder. There is nothing to configure: the effects are fully automatic.

---

## Requirements and compatibility

| | |
|---|---|
| **KSP** | 1.12.x (developed and tested on 1.12.5) |
| **Dependencies** | None |
| **Aerodynamics** | Stock. Built around the stock aero modules (`ModuleLiftingSurface` / `ModuleControlSurface`) |
| **Visual mods** | Works alongside Scatterer, EVE, Parallax, Deferred and Singularity |
| **Other bodies** | Works with Kopernicus and rescale mods: the atmosphere model reads each body's own curves |
| **Modded aircraft** | Supported, though detection depends on proper aero modules |

> [!WARNING]
> **Not compatible with Ferram Aerospace Research.** FAR replaces the stock lifting modules that vortex placement and the wing vapor detect, so neither appears under it. When FAR is running the load factor is read from its own aerodynamic force, but that alone does not make it a supported configuration.

---

## How it works

### Vortices

Vortex strength follows circulation, **Γ ≈ L / (ρ · V · b)**:

* **Lift** is read per-surface from KSP's own `ModuleLiftingSurface.liftForce`, not from felt G. Those agree in a steady turn but come apart where it matters: felt G counts thrust and impacts, and misses spoilers dumping lift entirely
* **Density and airspeed** move the threshold rather than the measurement. The same lift makes proportionally *more* circulation as speed and density fall, which is why a 96 m/s approach at 1 g sits at roughly 88% of a 3 g break turn at 250 m/s
* **Mass and span** enter as one quantity, m/b, soft-saturated. Constant wing loading means m ∝ b², so bigger simply means stronger, which is exactly how real wake-turbulence categories are drawn

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

Detection re-runs by itself when it needs to (on staging, part loss, docking, or switching craft), so a wake never keeps drawing from parts you are no longer flying.

### Wing vapor and wake physics

Wing vapor is a second, independent effect in the same mod: every lifting part is a wing section, and the vapor is the water that condenses where the air over it cools past its dew point (see [above](#features)). Its source is in [`Source/WingVapor/`](Source/WingVapor). The wake physics act on the vortices themselves: the core grows by Lamb-Oseen diffusion, the ground is modelled by a mirror vortex, and a core whose swirl outruns its axial flow breaks down into a spiral or a bubble. The full account of every change is in the [changelog](CHANGELOG.md).

---

## Performance

| | Cost |
|---|---|
| **Vortices** | No particle systems: tube meshes on main vortices, trails on secondary surfaces. Vessel measurement and mass lookups are throttled, not per-frame; cross-section geometry uses a precomputed unit-circle table |
| **Wing vapor** | One particle system per aircraft with a fixed cap on puffs, so a craft in a hard pull cannot cost more than a fixed amount. On a 137-part fighter in hard turns its physics averaged under 2.5 ms per physics step, and drawing it about a quarter of a millisecond a frame. Craft with many parts are updated every 2 to 4 physics steps, which its own smoothing hides |
| **Above the vapor's altitude limit** | Almost nothing: the wing-vapor code does no work at all |

---

## Known issues

* Highly canted or unconventional wing setups may have slight placement offsets
* Front vortex detection depends on valid lifting surfaces
* Very large aircraft can expose edge cases in placement or scaling
* Low FPS environments may introduce minor visual instability

---

## Troubleshooting

Search `KSP.log` for `[VORTEX]`. The mod logs its full selection pass, including a part inventory, every candidate with its lateral reach, which slots were filled, and the measured airframe.

<details>
<summary><b>No vortices showing</b></summary>

&nbsp;

* Ensure your craft has lifting/control surfaces
* Ensure the craft is airborne: vortices are suppressed on the ground by design
* Check the measurement line: `[VORTEX] measured <craft>: span=... mass=... sizeFactor=...`. A wildly wrong span means a mod's renderer got past the bounds filter; the log will name it

</details>

<details>
<summary><b>Nothing at altitude</b></summary>

&nbsp;

* Check `[VORTEX] contrail band on <body>` for the sampled temperature range

</details>

<details>
<summary><b>Core growth, ground effect and breakdown</b></summary>

&nbsp;

* Each logs a one-shot line the first time it engages (`[VORTEX] core growth`, `[VORTEX] ground effect`, `[VORTEX] breakdown engaged`), and `[VORTEX] session peaks` summarises the flight when you leave it. Please include those lines in a report

</details>

<details>
<summary><b>No wing vapor</b></summary>

&nbsp;

* It is meant to be rare. It needs a real pull, humid low air and a wing working hard for its speed, and only below about 14 km on Kerbin
* To see what it is doing, create an empty file named `verbose.txt` in the mod's folder (next to `Plugins`), fly, and search `KSP.log` for `[VORTEX] wing vapor:`: part loading, step times, which parts are condensing, and each time the altitude limit is crossed. Without that file the wing vapor logs only a few lines when a flight starts (whether FAR was found, the version, and which particle shader it is using)
* After five errors the wing vapor switches itself off for the flight and says so in the log; the vortices are unaffected

</details>

---

## Roadmap

* Better handling for extreme geometry and modded parts
* Optional debug visualization for vortex spawn points
* Manual placement / override system
* Further refinement of curl and dissipation behavior

---

## Changelog

See [CHANGELOG.md](CHANGELOG.md). Notes for each release are also on the [releases page](https://github.com/PogKai/ksp-wingtip-vortex/releases).

## License

[MIT](license.md)

## Author

PogKai

## Links

* [KSP forum thread](https://forum.kerbalspaceprogram.com/topic/230208-ksp-vorticescontrails-mod-release/#comment-4508713)
* [Releases](https://github.com/PogKai/ksp-wingtip-vortex/releases)
