# KSP Wingtip Vortex Mod

Adds dynamic, geometry-aware wingtip vortices to aircraft in Kerbal Space Program.

---

## Overview

This mod generates wingtip vortices that react to how you’re flying and how your aircraft is built. It now supports complex wing setups, including multi-part wings, canted surfaces, and forward canards, while staying visually stable across a wide range of speeds and flight conditions.

It switches between trail-based vortices at lower speeds and line-based vortices at higher speeds so effects remain smooth and don’t break visually.

---

## Features

* Vortices respond to G-force, speed, and altitude
* Two rendering modes:

  * Trails at low speed / low altitude  
  * Lines at high speed / high altitude  

* Supports complex aircraft geometry:

  * Multi-part wings  
  * Stacked wings  
  * Canted / angled wing tips  
  * Forward canards  

* Vertical “climb” logic finds the true aerodynamic surface instead of attaching to lower parts  
* High-speed vortices curve and drift based on airflow  
* Front vortices activate later and are less intense than rear vortices  
* Smooth fade in and out (no popping)  
* Trails dissipate naturally instead of snapping back  
* Scales with aircraft size  
* Built to stay stable at high speeds and most flight conditions  

---

## Requirements

* No dependencies

---

## Compatibility

* Works with most aircraft and lifting surfaces  
* Built around stock aero modules  
* Should work alongside most visual mods  
* Supports modded aircraft, though detection depends on proper aero modules  

---

## Installation

Drop into `GameData/`

---

## How It Works

Vortices are primarily driven by G-force and airflow:

* More G’s → stronger vortices  
* Turns and aggressive maneuvers bring them out  
* They fade out as G’s drop  

The system:

1. Detects valid lifting surfaces  
2. Finds the true wingtip using mesh geometry  
3. Climbs upward to attach to the correct aerodynamic surface  
4. Places vortex anchors at those locations  
5. Applies visual behavior based on flight conditions  

---

## Rendering Modes

### Trail Mode (Low Speed / Low Altitude)

* Below ~165 m/s and ~4,000 m  
* Smooth, dissipating trails  
* Strongest during maneuvering  

---

### Line Mode (High Speed / High Altitude)

* Above ~180 m/s or ~8,000 m  
* Clean, curved airflow streaks  
* More solid at high speed  
* Stable under extreme velocity  

---

## Controls

* None  
* Fully automatic  

---

## Performance

* Uses trails where they look best  
* Uses lines where trails would break  
* Designed to remain stable at high speeds  
* Avoids expensive particle systems  

---

## Known Issues

* Under high G with unstable physics timing (yellow/orange clock), trails may briefly spike forward before stabilizing  
* Highly canted or unconventional wing setups may have slight placement offsets  
* Front vortex detection depends on valid lifting surfaces  
* Very large aircraft can expose edge cases in placement or scaling  
* Low FPS environments may introduce minor visual instability  

---

## Troubleshooting

**No vortices showing**

* Ensure your craft has lifting/control surfaces  
* Ensure sufficient speed or G-force  

**Weird visuals during maneuvers**

* Can occur during rapid G or speed changes  
* Usually stabilizes quickly  

---

## Roadmap

* Improve vortex stability under physics time fluctuations  
* Better handling for extreme geometry and modded parts  
* Optional debug visualization for vortex spawn points  
* Manual placement / override system  
* Further refinement of curl and dissipation behavior  

---

## License

MIT

---

## Author

PogKai

---

## Version

v0.6.0

---

Link to KSP Forum post:  
https://forum.kerbalspaceprogram.com/topic/230208-ksp-vorticescontrails-mod-release/#comment-4508713
