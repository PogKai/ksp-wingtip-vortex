# KSP Wingtip Vortex Mod

Adds wingtip vortices to aircraft in Kerbal Space Program.

---

## Overview

This mod generates wingtip vortices that react to how you’re flying. It switches between trail-based vortices at lower speeds and line-based vortices at higher speeds so things stay stable and don’t fall apart visually.

---

## Features

* Vortices respond to G-force and flight conditions
* Two rendering modes:

  * Trails at low speed / low altitude
  * Lines at high speed / high altitude
* High-speed vortices curve with pitch and roll
* Front vortices show up later than the rear ones
* Smooth fade in and out (no popping)
* Trails dissipate naturally instead of snapping back
* Always keeps exactly 4 vortices
* Built to stay stable at high speeds

---

## Requirements

* No dependencies

---

## Compatibility

* Works with most aircraft and lifting surfaces
* Built around stock aero
* Should work fine alongside most visual mods

---

## Installation

Drop into GameData/

---

## How It Works

Vortices are driven mainly by G-force:

* More G’s → stronger vortices
* Turns and aggressive maneuvers bring them out
* They fade out as G’s drop

### Rendering Modes

**Trail Mode (Low Speed / Low Altitude)**

* Below ~165 m/s and ~4,000 m
* Smooth, dissipating trails

**Line Mode (High Speed / High Altitude)**

* Above ~180 m/s or ~8,000 m
* Clean, curved streaks

The switch between modes is latched so it doesn’t flicker around the threshold.

---

## Controls

* None
* Everything is automatic

---

## Performance

* Trails used where they look best
* Lines used where trails would break
* Keeps things stable at high speed

---

## Known Bugs

* Can still get minor visual issues if speed/G’s change very quickly
* The ~165–180 m/s range can feel a bit abrupt sometimes
* Line curvature can look off on very small aircraft
* Vortices may hang around slightly longer at very low speeds
* Extreme maneuvers (high roll + high G + rapid slowdown) can cause brief glitches

---

## Troubleshooting

**No vortices showing**

* Make sure your craft actually has lifting/control surfaces
* Make sure you're fast enough or pulling enough G’s

**Weird visuals during transitions**

* Usually happens during rapid changes
* Should settle on its own quickly

---

## Roadmap

* Improve curl / spiral behavior
* Better dissipation visuals
* More tuning around edge cases
* Clean up remaining transition issues

---

## License

MIT

---

## Author

PogKai

---

## Version

v0.3.0

Link to KSP Forum post: https://forum.kerbalspaceprogram.com/topic/230208-ksp-vorticescontrails-mod-release/#comment-4508713
