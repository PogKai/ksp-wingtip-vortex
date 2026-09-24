# Troubleshooting

[← Back to the README](../README.md)

Search `KSP.log` for `[VORTEX]`. The mod logs its full selection pass, including a part inventory, every candidate with its lateral reach, which slots were filled, and the measured airframe.

**On this page:** [No vortices](#no-vortices-showing) · [Nothing at altitude](#nothing-at-altitude) · [No wing vapor](#no-wing-vapor) · [Core growth, ground effect and breakdown](#core-growth-ground-effect-and-breakdown) · [Known issues](#known-issues) · [Reporting a problem](#reporting-a-problem)

---

## No vortices showing

* Ensure your craft has lifting or control surfaces
* Ensure the craft is airborne: vortices are suppressed on the ground by design
* Check the measurement line: `[VORTEX] [<craft>] measured: span=... mass=... sizeFactor=...`. Every line is tagged with the craft it is about, since other aircraft log too. A wildly wrong span means a mod's renderer got past the bounds filter; the log will name it
* On another aircraft: vortices are drawn for the ten nearest craft with lifting surfaces. Debris, EVA kerbals, flags and missiles never get them
* Ferram Aerospace Research is not supported: it removes the stock lifting modules the mod detects

## Nothing at altitude

* Check `[VORTEX] contrail band on <body>` for the sampled temperature range

## No wing vapor

* It is meant to be rare. It needs a real pull, humid low air and a wing working hard for its speed, and only below about 14 km on Kerbin
* To see what it is doing, create an empty file named `verbose.txt` in the mod's folder (next to `Plugins`), fly, and search `KSP.log` for `[VORTEX] wing vapor:`. You get the part loading, step times, which parts are condensing, and each time the altitude limit is crossed. Without that file the wing vapor logs only a few lines when a flight starts (whether FAR was found, the version, and which particle shader it is using), and one line each time it moves to another craft (`now on <craft>`) or stays put because the camera went to a missile
* After five errors the wing vapor switches itself off for the flight and says so in the log; the vortices are unaffected

## Core growth, ground effect and breakdown

* Each logs a one-shot line the first time it engages (`[VORTEX] core growth`, `[VORTEX] ground effect`, `[VORTEX] breakdown engaged`), and `[VORTEX] session peaks` summarises the flight when you leave it

---

## Known issues

* Highly canted or unconventional wing setups may have slight placement offsets
* Front vortex detection depends on valid lifting surfaces
* Very large aircraft can expose edge cases in placement or scaling
* Low FPS environments may introduce minor visual instability

## Reporting a problem

Please include the `[VORTEX]` lines from `KSP.log` (and, for wing vapor, the `[VORTEX] wing vapor:` lines with `verbose.txt` switched on), the craft, and what you expected to see. Post it in the [forum thread](https://forum.kerbalspaceprogram.com/topic/230208-ksp-vorticescontrails-mod-release/#comment-4508713) or as a [GitHub issue](https://github.com/PogKai/ksp-wingtip-vortex/issues).
