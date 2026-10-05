# Wingtip Vortex

A lightweight Kerbal Space Program mod that adds dynamic, physics-inspired **wingtip vortices, wing vapor and atmospheric contrails** to aircraft, driven by the lift the wings are actually producing and by the real atmosphere of the celestial body you are flying on.

Wingtip Vortex uses **procedural vortex meshes and trails** instead of traditional particle effects for the vortices, and a small, capped particle system for the wing vapor.

**Current version: 1.5.0** · KSP 1.12.x · no dependencies

---

## Overview

Wingtip Vortex generates visual vortices that respond dynamically to the aerodynamic state of the aircraft.

Rather than using fixed G-force and altitude thresholds, the mod models vortex strength around **circulation**:

**Γ ≈ L / (ρ · V · b)**

where lift, atmospheric density, airspeed and wingspan determine the strength of the resulting vortex.

Lift is read per surface from the aerodynamics actually in use: KSP's `ModuleLiftingSurface.liftForce` under stock, or each wing's own force under **Ferram Aerospace Research**. The effect responds to what the wings are doing rather than to the aircraft's felt G-force.

Contrail formation is atmosphere-driven too. Instead of a fixed altitude band, the mod samples each celestial body's **ambient temperature and air density** to decide where persistent contrails can form.

The result adapts to Kerbin, Eve, Duna, Kopernicus planets, rescaled systems and different aircraft without per-body altitude tuning.

---

## Features

- **Vortices on every aircraft:** twin ropes curl in behind the wingtips, stronger the harder the wing works. Drawn on your craft and the ten nearest others, AI wingmen and BDArmory opponents included. Works on multi-part wings, canards and rockets
- **Circulation-based strength:** driven by real per-surface lift, airspeed and air density, so a slow, high-lift landing approach vortexes at 1 G. No minimum G requirement
- **Wing vapor:** the white sheet over the wings in a hard pull, worked out from the pressure drop, cooling and dew point over each wing section. Deliberately hard to get. Optional volumetric cloud via the [Volumetric Wing Vapor](https://github.com/PogKai/ksp-volumetric-wing-vapor) add-on
- **Wake physics:** viscous core growth, ground effect, and vortex breakdown (spiral or bubble), with a wake that starts as a thin, faint thread and builds up
- **Atmosphere-driven contrails:** based on each body's own ambient temperature and air density, not altitude, and spreading wide and soft toward their end
- **Procedural tube meshes:** main vortices curve inward under mutual induction; canards use short trails; no particle systems for the vortices
- **Automatic wing detection:** handles multi-part wings, canards and canted surfaces without duplicate vortices, and measures each craft in its own frame
- **In-game settings:** app launcher button or **Alt+V** to switch each effect on or off and set its intensity from 0 to 200%. Saved to `PluginData/settings.cfg`
- **Smooth, stable flight behavior:** fades with load, steady in hard maneuvers, none while parked or on the takeoff roll
- **Planet-aware lighting:** white in sunlight, dimmed at night, normalized across bodies
- **Modded-install support:** works with Scatterer, EVE, Parallax, Deferred, Singularity and Kopernicus, and ignores oversized effect-mesh bounds
- **Low overhead:** effects are generated only where needed, nothing runs in space, and everything cleans up on vessel and scene changes

---

## How It Works

Wingtip Vortex scans each aircraft's aerodynamic surfaces and determines which surfaces form the primary and secondary vortex system.

For each relevant lifting surface, the mod reads the actual aerodynamic lift and evaluates vortex strength using the relationship between:

- Lift
- Air density
- Airspeed
- Wingspan

This means vortex behavior changes naturally with flight conditions.

A slow aircraft generating large amounts of lift can produce strong vortices even at **1 G**: a 96 m/s approach at 1 g sits at roughly 88% of a 3 g break turn at 250 m/s. A fast aircraft generating relatively little lift produces a much weaker effect.

The primary wingtip pair is rendered as a procedural mesh that curves inward behind the aircraft. Secondary surfaces, such as canards, keep trail-style rendering because their vortices interact differently with the main wing system. Rockets use trails throughout.

Wing vapor is evaluated separately, for every lifting part, every physics step: how hard the part is working, how that load is spread along the chord (with a suction peak near the leading edge as the angle of attack grows), how much the air cools going through that pressure drop, and how much of the water it holds condenses out.

Contrails are evaluated from the atmosphere around the aircraft. Ambient temperature determines the potential contrail region, while atmospheric density controls whether the resulting effect is strong enough to remain visible.

Nothing is hardcoded to a particular altitude. See [docs/how-it-works.md](docs/how-it-works.md) and [docs/wing-vapor.md](docs/wing-vapor.md) for the full physics.

---

## Behavior

Vortices appear automatically whenever an aircraft is generating sufficient aerodynamic circulation.

You may see strong vortices during:

- Hard turns and high-G maneuvers
- Landing approaches
- High-angle-of-attack flight
- Low-speed, high-lift conditions
- Other situations where the wings are carrying substantial aerodynamic load

Vortices fade naturally as circulation decreases.

Wing vapor appears only in a real, hard pull in humid low air, and thins out past the stall.

Persistent contrails appear when atmospheric temperature and density are suitable rather than at a predetermined altitude, so the altitude at which they appear varies significantly between celestial bodies.

### Altitude limits

Both effects are limited by air density, not height, so the limits fall at a different altitude on every body. Kerbin figures are approximate.

- **Vortices** are drawn at full strength in every part of the atmosphere with air density of 0.10 kg/m³ or more (roughly 15 km on Kerbin), fade out above that, and are gone entirely at 0.01 kg/m³ (roughly 25 km), so nothing is drawn in orbit
- **Wing vapor** forms only below 10% of the body's sea-level air density (about 14 km on Kerbin), with a hard cutoff above that
- **High-altitude contrails** need cold air, so on each body they appear only inside the band of temperatures it actually has

---

## Installation

1. Delete any previous `WingtipVortex` installation.
2. Download the zip from the [latest release](https://github.com/PogKai/ksp-wingtip-vortex/releases/latest).
3. Copy the `WingtipVortex` folder from the zip's `GameData` into your Kerbal Space Program `GameData/` directory.
4. Verify that the mod is located at:

`GameData/WingtipVortex/`

alongside:

`GameData/Squad/`

To upgrade, replace the folder (this resets the in-game settings). To remove, delete it.

**Do not leave multiple versions installed.** Two copies of the plugin can run simultaneously and produce duplicate vortices.

---

## Usage

Fully automatic: **no controls or configuration are required.**

Install the mod and fly normally. Wingtip Vortex detects each aircraft's lifting surfaces, evaluates the current aerodynamic and atmospheric conditions, and renders the appropriate effects.

If you want to tone an effect down, turn it up or switch it off, use the in-game settings window (app launcher button, or **Alt+V**).

---

## Add-on: Volumetric Wing Vapor

[Volumetric Wing Vapor](https://github.com/PogKai/ksp-volumetric-wing-vapor) is a separate, optional mod that draws the wing vapor as one soft volumetric cloud over each wing, in place of the puffs, using [Waterfall](https://github.com/post-kerbin-mining-corporation/Waterfall)'s shader. It needs Wingtip Vortex 1.5.0 or later and Waterfall.

- **When it is installed**, the settings window gains a **Volumetric cloud (Waterfall)** switch and a thickness slider
- **Without it**, nothing changes, and Wingtip Vortex still has no dependencies

---

## Debugging

If vortex placement or behavior looks incorrect, search `KSP.log` for:

`[VORTEX]`

The mod logs detailed diagnostic information, with each line tagged by the craft it is about:

- Detected aircraft parts
- Lifting-surface candidates and their lateral reach
- Selected vortex slots
- Measured aircraft dimensions
- Atmospheric conditions
- Contrail temperature band for the current celestial body
- Whether FAR was detected
- One-shot lines when core growth, ground effect and vortex breakdown first engage, and a `session peaks` summary when you leave the flight

For wing vapor, create an empty file named `verbose.txt` in the mod's folder (next to `Plugins`), fly, and search for `[VORTEX] wing vapor:`.

Please include the relevant `[VORTEX]` output when submitting bug reports. See [docs/troubleshooting.md](docs/troubleshooting.md).

---

## Known Limitations

- Vortex and vapor behavior is visual and does not modify the aircraft's aerodynamics.
- Placement is based on detected lifting surfaces rather than CFD simulation.
- Highly canted or unconventional wing configurations may produce minor placement offsets.
- Some modded lifting surfaces may not expose valid stock or FAR aerodynamic modules.
- Extremely large or unusual aircraft may expose edge cases in placement or scaling.
- Vortices are drawn on the ten nearest aircraft; debris, EVA kerbals, flags and missiles never get them.
- Wing vapor is drawn on the craft you fly only, as a particle effect (not volumetric, no shadows) unless the Volumetric Wing Vapor add-on is installed.
- KSP has no weather, so the air's moisture is a fixed stand-in rather than following the time of day.

---

## Compatibility

- **Kerbal Space Program 1.12.x**
- **No dependencies**
- **Stock aerodynamics** or **Ferram Aerospace Research** (tested with FAR 0.16.2), detected automatically
- BDArmory aircraft and AI wingmen
- Kopernicus planets and rescaled systems
- Scatterer, EVE, Parallax, Deferred, Singularity and other visual-heavy installs

---

## License

[CC BY-NC-SA 4.0](license.md): free to use, change and share with credit, never to sell. Versions up to 1.2.0 were released under MIT and stay under it.

---

## Author

**PogKai**

Built with help from Claude, Anthropic's AI assistant (research support, baseline code and debugging). The ideas, direction, core KSP integration and all in-game testing are PogKai's.
