# Wing vapor

[← Back to the README](../README.md)

The white sheet that forms over a fighter's wings in a hard pull, worked out from the physics of the air rather than from a list of triggers.

**On this page:** [How real wing vapor forms](#how-real-wing-vapor-forms) · [How the mod draws it](#how-the-mod-draws-it) · [What you see](#what-you-see) · [Limits, on purpose](#limits-on-purpose) · [Legacy puffs](#legacy-puffs) · [Source](#source)

---

## How real wing vapor forms

Air flowing over the top of a wing drops in pressure, and because it does so too quickly to exchange heat it also cools. If it cools below its dew point, the water in it condenses into cloud, which vanishes again once the air passes the wing and the pressure recovers. Nobody switched an effect on: it is just what humid air does over a hard-working wing.

## How the mod draws it

Since 2.0 the vapor is a **raymarched volume**: a port of the wing vapor in [aeronautic](https://github.com/Romainlg29/aeronautic) by Romain Le Gall (MIT), fitted to KSP.

1. **The airframe is measured** when you start flying: each wing's planform, strakes (LEX), canards and the fuselage, from the craft's own parts and meshes. Missiles and stores under the wings are left out
2. **Every frame the mod works out the pressure drop** over each wing station from the load, the angle of attack and the Mach number, and how much the humid air cools through it
3. **The GPU marches through that field** in a box around the aircraft and draws the cloud wherever the air is cold enough to condense, lit by the sun (warm at sunrise and sunset, dimmed at night)

## What you see

* **A sheet over the wings** that stands up from the skin as the pull tightens, thin toward the tips and even across the span
* **LEX ropes** on aircraft with strakes: a cord of vapor from the strake's vortex, flowing straight back along the airframe
* **Over the fuselage** in the hardest pulls, the sheet spreads across the body between the wing roots
* **Cobras and high angle of attack** condense from the angle the wing actually meets the air, not only from g
* **On other aircraft too**: up to three aircraft within 2 km of the camera (formations, AI wingmen, BDArmory opponents) get their own vapor
* **It is hard to get.** A fighter needs a real pull in the lower atmosphere; cruise, a takeoff roll or a slow approach show nothing

## Limits, on purpose

* **Lower atmosphere only.** Air thinner than 10% of the body's sea-level density (about 14 km on Kerbin) has a hard cutoff: no vapor and no work done
* **KSP has no weather**, so the air's moisture is a fixed stand-in, not the time of day
* **It needs its shader.** The raymarch needs `Shaders/wingvapor-<platform>.bundle` in the mod's folder and a graphics API that can run it (Direct3D 11, OpenGL Core, Metal or Vulkan). Without it the mod falls back to the legacy puffs and says so in the log

## Legacy puffs

The settings window (app launcher button, or **Alt+V** in flight) has a **Legacy particle vapor** switch that brings back the pre-2.0 look: puffs of KSP's own particles over each lifting part, on your craft only. The same window sets the vapor's density and height from 0 to 200%.

## Source

The [implementation reference](implementation.md) maps each effect to its source. The [public source](https://github.com/PogKai/ksp-wingtip-vortex/tree/main/Source) includes the volume integration (`WingVapor/RaymarchVapor.cs`), aerodynamic model (`Aeronautic/` and `AeronauticUnity/`), shader source (`Shaders/`), and legacy puffs (`WingVapor/WingVapor.cs` and `VaporRenderer.cs`). The aeronautic source revision and license are recorded in [THIRD-PARTY-NOTICES](../THIRD-PARTY-NOTICES.md).

---

Something not right? See [troubleshooting](troubleshooting.md#no-wing-vapor).
