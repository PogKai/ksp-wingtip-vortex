# Implementation reference

[← Back to the README](../README.md)

This page maps the effects to their runtime source and explains how the rendering components fit together.

**On this page:** [Runtime map](#runtime-map) · [Vortex path](#vortex-path) · [Wing-vapor path](#wing-vapor-path) · [Rendering](#rendering) · [Settings and compatibility](#settings-and-compatibility) · [Source conventions](#source-conventions)

---

## Runtime map

| Area | Main source | Responsibility |
|---|---|---|
| Vessel manager | `Source/WingtipVortex.cs` | Chooses aircraft, owns one vortex controller per craft, detects lifting surfaces, and updates wake geometry |
| KSP vapor integration | `Source/WingVapor/RaymarchVapor.cs` | Measures a craft, reads its flight state, and feeds the portable vapor model |
| Legacy particle vapor | `Source/WingVapor/WingVapor.cs`, `VaporRenderer.cs` | Samples condensation on exposed upper wing faces and emits hull-bounded puffs in the craft's frame |
| Nearby-aircraft vapor | `Source/WingVapor/OtherCraftVapor.cs` | Assigns raymarched vapor to the nearest eligible aircraft |
| Airframe measurement | `Source/Aeronautic/AirframeBuilder.cs`, `PlanformMeasure.cs`, `WingCover.cs` | Converts KSP parts and meshes into a wing, strake, canard, fuselage, and top-surface model |
| Vapor physics | `Source/Aeronautic/VaporField.cs`, `MoistAir.cs`, `MoistCondensation.cs` | Builds the pressure-deficit field and decides where moist air condenses |
| KSP calibration | `Source/AeronauticUnity/KspBaseline.cs`, `VaporDriver.cs` | Maps KSP load, angle of attack, speed, lighting, and settings into the portable model |
| GPU rendering | `Source/AeronauticUnity/VaporMaterial.cs`, `VaporVolumePass.cs` | Uploads the field, renders the volume, and composites it against scene depth |
| Shaders | `Source/Shaders/` | Raymarches the field, shades the droplets, and performs the depth-aware composite |
| Player settings | `Source/WingVapor/ModSettings.cs` | App-launcher window, Alt+V shortcut, saved toggles, intensity, depth, and legacy fallback |

## Vortex path

`VortexManager` keeps the active craft and the nearest loaded aircraft with lifting surfaces, capped at ten controllers. Missiles, debris, EVA kerbals and non-aircraft are rejected before a controller is created.

Each controller:

1. Finds stock or FAR lifting surfaces and measures the craft in its own aerodynamic frame.
2. Groups connected wing parts so a multi-panel wing produces one source per side.
3. Selects main and eligible secondary surfaces, then places anchors at their physical tips.
4. Reads per-surface lift and converts it to circulation using air density, speed and span.
5. Lays wake samples in world space and compensates KSP floating-origin shifts.
6. Draws main wakes as tube meshes and secondary wakes as shorter trails.

The wake model in `Source/WingtipVortex.cs` applies viscous core growth, mutual-induction curvature, ground effect and breakdown. Stored points are immutable wake history: later aircraft motion changes new samples, not the geometry already left in the air.

## Wing-vapor path

The raymarched effect uses a portable model derived from aeronautic. The exact upstream revisions and MIT terms are in [`THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md).

1. `RaymarchVapor` gathers lifting surfaces and mesh outlines from one vessel.
2. `AirframeBuilder` constructs a canonical airframe. `PlanformMeasure` resolves the wing and secondary surface; `WingCover` records the visible top surface so vapor rests on the aircraft instead of passing through it.
3. `AeroState` reads stock or FAR lift, angle of attack, sideslip, Mach number, landing state and atmospheric conditions.
4. `KspBaseline` converts those values into the model's load and visibility ranges. Broad wing-sheet eligibility remains load-driven; high angle of attack supplies a bounded leading-edge-vortex branch for post-stall flight.
5. `VaporDriver` advances the field and its trailing history.
6. `VaporFieldModel` combines wing suction, leading-edge cords, tip vortices, canard vapor, body cover and the transonic cone into one pressure-deficit field.
7. Moist-air expansion determines where the deficit crosses the condensation threshold.

The model keeps geometry and flight separate. Airframe measurement changes only when the vessel changes; load, flow and atmosphere update every physics step.

## Rendering

`VaporMaterial` uploads the field to `WingVaporField.cginc`. `WingVapor.shader` raymarches a bounded volume around the aircraft. `VaporVolumePass` renders before the flight camera and composites through `VaporComposite.shader` using the scene depth texture, so opaque aircraft geometry correctly hides vapor behind it.

The shader includes:

- spanwise wing and canard sections;
- leading-edge and tip-vortex paths;
- airframe top-surface and fuselage coverage;
- advected patch noise and aft streaking;
- sunlight, sky fill, self-shadowing and AgX tone mapping;
- adaptive march steps with a bounded close-camera quality reduction.

The three platform bundles in `Shaders/` are built from these sources with Unity 2019.4.18f1.

## Settings and compatibility

`ModSettings` stores player choices in `PluginData/settings.cfg`. Runtime source contains no hard dependency on BDArmory or FAR: both are detected by module or type name. BDArmory missiles and stores are excluded from the measured vapor outline, while FAR forces and stall state are read by reflection when FAR is present.

If a platform shader bundle cannot load, the addon falls back to the legacy particle vapor. Wingtip vortices continue independently if wing vapor disables itself after repeated rendering errors.

Legacy sampling uses real part colliders where testable, rather than treating every fuselage bounding box as solid. Suction faces pointing toward the craft's belly are suppressed. Emission stays within the measured wing hull; changing the vessel root, renderer or packed state clears old particles before new sources are built.

## Source conventions

- Ported files identify their aeronautic source revision at the top.
- Units in the portable model are SI: metres, seconds, kilograms, pascals and radians.
- Prebuilt platform bundles are included; no additional shader tools are required to install the mod.
