# KSP Wingtip Vortex Mod

Realistic wingtip vortices for Kerbal Space Program based on aerodynamic behavior.

---

## Overview

This mod adds dynamic wingtip vortices to aircraft in KSP1. The system reacts to speed, altitude, and G-force, switching between different rendering methods to maintain both realism and performance.

---

## Features

* Dynamic vortex generation based on G-force and flight conditions
* Dual rendering system:

  * Trail vortices at low speed / low altitude
  * Line vortices at high speed / high altitude
* Curved high-speed vortices that respond to pitch and roll
* Front vortices appear later than rear vortices
* Smooth buildup and fade-out (no popping)
* Natural dissipation at low speeds
* Always maintains exactly 4 vortices
* Optimized for performance and stability

---

## Requirements

* Kerbal Space Program 1 (KSP1)
* No external dependencies

---

## Compatibility

* Works with most aircraft and lifting surfaces
* Designed for stock aerodynamics
* Should be compatible with most visual mods

---

## Installation

Drop into GameData/

---

## How It Works

Vortices are generated dynamically based on flight behavior:

* Increase with **G-force**
* Stronger during turns and aggressive maneuvers
* Fade out naturally when G’s decrease

### Rendering Modes

**Trail Mode (Low Speed / Low Altitude)**

* Active below ~165 m/s and below ~4,000 m
* Produces smooth, dissipating airflow trails

**Line Mode (High Speed / High Altitude)**

* Activates above ~180 m/s or above ~8,000 m

* Produces stable, curved vortex streaks

* Switching is latched to prevent flickering or instability

---

## Controls

* No user controls
* Fully automatic based on flight conditions

---

## Performance

* Uses TrailRenderer at low speeds for visual quality
* Uses LineRenderer at high speeds for performance
* Designed to minimize impact during high-speed flight

---

## Known Bugs

* Minor visual artifacts during very rapid speed or G-force changes
* Transition zone (~165–180 m/s) can feel abrupt in edge cases
* Line curvature may look exaggerated on very small aircraft
* Vortices may linger slightly longer than expected at very low speeds
* Extreme maneuvers (high roll + high G + rapid deceleration) can cause brief glitches

---

## Troubleshooting

**Vortices not appearing**

* Ensure aircraft has lifting/control surfaces
* Check that speed and G-force conditions are being met

**Strange visuals during transitions**

* This can happen during rapid flight changes
* Usually stabilizes quickly once conditions settle

---

## Roadmap / Future Plans

* Improved vortex curl / spiral behavior
* Better dissipation visuals
* Additional tuning for edge cases
* Squish more bugs (make transition from trail render to trace render seamless as trail render can't handle high speeds)

---

## License

MIT (or your preferred license)

---

## Author

PogKai

---

## Version

v0.3.0
