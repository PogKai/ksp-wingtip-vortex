# Wing vapor

[← Back to the README](../README.md)

The white sheet that forms over a fighter's wings in a hard pull, worked out from the physics of the air rather than from a list of triggers.

**On this page:** [How real wing vapor forms](#how-real-wing-vapor-forms) · [What the mod calculates](#what-the-mod-calculates) · [What you see](#what-you-see) · [Limits, on purpose](#limits-on-purpose) · [Performance](#performance) · [Source](#source)

---

## How real wing vapor forms

Air flowing over the top of a wing drops in pressure, and because it does so too quickly to exchange heat it also cools. If it cools below its dew point, the water in it condenses into cloud, which vanishes again once the air passes the wing and the pressure recovers. Nobody switched an effect on: it is just what humid air does over a hard-working wing.

## What the mod calculates

For every lifting part, every physics step:

1. **How hard the part is working.** Its lift, corrected for the other parts around it. A wing built from many parts, clipped parts and stacked parts are treated as the one wing they form, and control surfaces are part of the section they sit in
2. **How that load is spread along the chord.** Smoothly with the camber, and piled into a suction peak just behind the leading edge as the angle of attack grows (sharpened toward Mach 1)
3. **How much the air cools** going through that pressure drop, from the local pressure and temperature
4. **How much of the water it holds condenses out.** That is the vapor

## What you see

* A thin haze along the leading edge first, spreading back over the top of the wing as the pull tightens, thinner toward the tips
* **It is hard to get.** A passenger jet at its structural limit shows a faint trace at most. A fighter needs a real pull in humid air. A takeoff roll, cruise or a slow approach shows nothing
* Slow, hard-working wings condense sooner than fast ones at the same load, and wings near Mach 1 sooner still, as real ones do
* Different on each wing in a bank
* White in sunlight and dimmed on the night side, from the star's actual flux, so eclipses and planet shadow are handled
* Parts buried inside a fuselage or an engine nacelle make no vapor

## Limits, on purpose

* **Lower atmosphere only.** Air thinner than 10% of the body's sea-level density (about 14 km on Kerbin) has a hard cutoff: no vapor, and the wing-vapor code does no work at all, so orbit and space stations cost nothing
* **A surface lifting against the aircraft's net lift makes no vapor**: a tailplane trimming the aircraft, a canard set against the wing. KSP loads these far harder than a real aircraft would, and they looked like artifacts. This is a style choice, not physics
* **KSP has no weather**, so the air's moisture is a stand-in: a fixed dew-point spread (12 K in the humid lowest layer, 18 K above it). It does not follow the time of day
* Wing vapor is drawn on the active vessel only, as a particle effect: not volumetric, no shadows
* Needs stock aerodynamics (`ModuleLiftingSurface`). Under Ferram Aerospace Research no vapor appears

## Performance

One particle system per aircraft with a fixed cap on puffs, so a craft in a hard pull cannot cost more than a fixed amount. On a 137-part fighter in hard turns its physics averaged under 2.5 ms per physics step, and drawing it about a quarter of a millisecond a frame. Craft with many parts are updated every 2 to 4 physics steps, which its own smoothing hides. Above the altitude limit it costs almost nothing. There are no extra assets: KSP's own particle shader and a texture generated in code.

## Source

[`Source/WingVapor/`](../Source/WingVapor). It is a second addon in the same DLL, and shares nothing with the vortex code but the log tag and the version.

---

Something not right? See [troubleshooting](troubleshooting.md#no-wing-vapor).
