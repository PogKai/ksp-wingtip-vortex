# Changelog

All notable changes to KSP Wingtip Vortex.

---

## 1.0.0 — First Stable Release

Everything below this section is the development history that led here. 0.6.0 was the last
public release, so every 0.6.x entry that follows ships for the first time as part of 1.0.0.

### The headline changes since 0.6.0

* **The above-200 m bug is fixed.** Trails no longer spike forward during high-G manoeuvres. It
  was a Krakensbane / floating-origin frame mismatch, root-caused by decompiling KSP's own
  `Assembly-CSharp.dll` and reading the IL rather than by guesswork. The 200 m threshold was
  never about altitude at all — `Krakensbane.SafeToEngage()` gates the velocity frame on ground
  clearance, so below it the frame simply never engaged and the bug could not appear.

* **Vortex strength follows circulation, not G-force.** Γ ≈ L/(ρ·V·b), with lift read per-surface
  from KSP's own `ModuleLiftingSurface.liftForce`. Vortices now appear on a slow, high-alpha
  landing approach as well as in a hard break turn — a 96 m/s approach at 1 g sits at roughly 88%
  of a 3 g break turn at 250 m/s, which is why real approach photographs look the way they do.

* **Altitude is gone from the effect model entirely.** The contrail band is drawn by ambient
  temperature against physical anchors, and the fade-out by air density. Behaviour is correct on
  every planet with no per-body tuning, and each body's own temperature curve is sampled at load.

* **A procedural tube mesh replaces the trail for main vortices**, curving inward under mutual
  induction the way a real counter-rotating pair does. Where a tube exists it is the only
  renderer for that source at any speed.

* **Source selection rewritten.** One rule, four slots: {left, right} × {main, secondary}, capped
  structurally rather than by heuristics agreeing. Fixes duplicate vortices on multi-part wings,
  anchors drifting off canted tips, and canard vortices spawning at the root instead of the tip.

* **Robust on heavily modded installs.** Visual mods give their meshes enormous bounds to defeat
  frustum culling; one such renderer parented under a part used to poison the whole airframe
  measurement and collapse every vortex to zero width and alpha.

### Known limitations

* Highly canted or unconventional wing setups may still have slight placement offsets.
* Front vortex detection depends on parts carrying valid stock aero modules.
* FAR is not supported — it replaces `ModuleLiftingSurface`, which is what this reads.

---

## 0.6.41 — Tube Exclusive

### Fixed

* **On re-entry a second renderer grew to full length and size before the tube took over.** Not
  the `TrailRenderer` — on a tube source that is already cleared and disabled in the same frame
  the trail block enables it, so it cannot draw. It was **line mode**:

  1. Re-entry runs well past `lineModeSpeed`, so `useLineMode` engages
  2. `trailFade` goes to 0, so `shouldUseTrail` goes false
  3. the tube's own point feed was gated on `shouldUseTrail`, so the tube was starved and aged out
  4. the `LineRenderer` drew instead, growing to full length
  5. dropping under the hysteresis speed handed the rope back to the tube

  Where a tube exists it now owns the source outright — line mode is suppressed for it, and the
  tube is fed by visibility alone with no `trailFade` term, so the line-mode blend can no longer
  starve it.

  Dropping the handover is safe rather than merely convenient: line mode exists because
  accumulated `TrailRenderer` geometry outruns its own resampling at extreme speed, and that
  reasoning does not transfer to the tube — ring spacing already scales with speed against a
  fixed ring budget.

* **The tube's lifetime would have frozen.** It read `life` from `tr.time`, which was only ever
  updated inside the trail block — the block that no longer runs for a tube source. `stability`
  and `targetTime` are now hoisted so both renderers read the same lifetime curve.

### Added

* `ribbonMaxLength` (2500 m). Line mode was also what stopped a hypersonic wake running away, and
  a time-based lifetime gives no length bound — at 2 km/s a 5 s rope is 10 km long. Expressed as
  length rather than as a speed-dependent lifetime fudge, because length is what matters
  visually. Below ~500 m/s the lifetime binds first and nothing changes; above it the rope simply
  stops getting longer.

---

## 0.6.40 — Vacuum Fade

### Fixed

* **Trails were left hanging in the air after the tubes had gone, on the way out of the
  atmosphere.** The vacuum early-out returned from the update loop the instant density crossed
  0.01, cutting it off in the middle of a fade that `atmFactor` was already running smoothly —
  and the two renderers did not survive that cut the same way:

  * A tube had been rebuilt on its last live frame with visibility near zero, so its rings were
    already closed to no radius and no alpha. It vanished cleanly.
  * A `TrailRenderer` had not. `ApplyShedAppearance` is what walks its recorded shed history and
    slides that profile toward the tail as the wake ages, and it stopped being called entirely.
    The trail froze at the width and colour of its last live frame and simply sat there, so
    secondary vortices hung in the air after the main ones had gone.

  There was never a fade to special-case: `atmFactor` already takes visibility to zero across the
  0.01–0.10 density band and both renderers close out through their own normal paths when it
  does. The early-out now fires only once there is genuinely nothing left to draw, keeping the
  cost saving in orbit without ever truncating a fade. Both ropes now age out together.

* **The contrail floor bypassed the density fade.** It is applied with `Mathf.Max`, so it never
  saw `atmFactor`. That was masked by the hard early-out above, but ambient temperature outside
  the atmosphere is cold, so `contrailBlend` reaches 1 there — without the cliff, the floor would
  have relit every vortex at half strength **in orbit**. Now scaled by `atmFactor`: no air, no
  condensation, whatever the temperature says.

---

## 0.6.39 — Bounds Sanity

The headline fix: **the effect went completely invisible on heavily modded installs.**

### Fixed

* **Rogue renderers poisoned every vessel measurement.** `ComputeVesselBounds` used
  `part.GetComponentInChildren<Renderer>()`, which walks a part's entire transform subtree.
  Visual mods deliberately give their meshes enormous bounds so Unity's frustum culling never
  discards them — Scatterer, EVE, Singularity and Deferred all do this — and one such renderer
  parented under any part was encapsulated as if it were airframe geometry. `Bounds.Encapsulate`
  has no tolerance for a single bad member, so the whole airframe measurement was destroyed:

  ```
  [VORTEX] axes resolved — span=1373763000000000000.0m
  ```

  `vesselSpan = 1.37e18` → `sizeRatio ≈ 1.5e-16` → `sizeFactor ≈ 3e-16` → `circFactor ≈ 3e-16`
  → `visible ≈ 0`. Every vortex source spawned correctly and then rendered at zero width and
  zero alpha. A stock install has nothing that does this, which is why it was invisible in
  testing.

  A shared `TryGetPartBounds` now filters every renderer before it is measured, and is used by
  the vessel measurement, candidate gathering *and* the inventory diagnostic so the three can
  never disagree. It rejects:
  * renderers belonging to an attached **child part** (the same exclusion `TryFindTipVertex`
    already used — a wingtip-mounted pod is not part of the wing)
  * `TrailRenderer` / `LineRenderer` / `ParticleSystemRenderer`, **including this mod's own** —
    a trail's bounds cover the entire wake, so measuring an aircraft with one in scope grows
    without limit
  * NaN and infinite bounds
  * anything over 200 m, far above any real part and far below the garbage

  Part **origins** always count toward the measurement, so it can never come back empty even if
  every renderer on a craft is rejected, and `vesselSpan` / `vesselSize` are clamped as a last
  line of defence — anything that gets past the filter degrades to wrong-looking rather than to
  invisible.

* **Multi-mesh parts were measured short.** The old code took only the *first* renderer per
  part. All valid renderers are now merged.

* **Bogus atmosphere temperature samples.** Kopernicus and rescale mods can leave a body's
  temperature curve undefined at the top of its atmosphere, reporting `air 0.0-288.2K`.
  Harmless on Kerbin, but on a body whose true minimum sits above the physical anchor it would
  have made the reachability guard inert. Non-positive and non-finite samples are discarded.

### Added

* Diagnostic logging for both of the above:
  ```
  [VORTEX] measured <craft>: span=20.0m size=31.4m mass=48.2t sizeFactor=1.66
  [VORTEX] ignoring oversized renderer '<name>' on part=<part> size=...m
  ```

---

## 0.6.38 — Tip Likeness

### Changed

* **"Main wing" and "secondary surface" were slots in the selection pass, not aerodynamic
  facts** — and the code treated the slot as the fact. The widest surface on each side got full
  strength and the base onset; a surface 1 cm narrower got 40% strength and a 3.5–6 g onset.
  Nothing physical happens at that boundary.

  Strength and onset now come from **how much of the widest span a surface actually reaches**
  (`MainLikeness`, smoothstepped 0.80 → 0.95 of the widest span), not from which slot it won.
  If a canard is canted or long enough that its tip is the widest point of the airframe, that
  tip *is* the wingtip: it is the last place the pressure difference between upper and lower
  surface can escape around, which is the entire mechanism, and sitting ahead of the wing
  changes nothing about it.

  At full likeness the strength reaches exactly `1f`, which makes the secondary gate skip
  entirely — such a surface runs the *identical code path* as a main wing rather than a
  secondary path tuned to imitate one.

  | Canard reach vs wing | Before | After |
  |---|---|---|
  | 101% (widest) | strength 1.0, onset 3.0 g | unchanged |
  | 99% | strength 0.4, onset 3.5 g | strength 1.0, onset 3.0 g |
  | 90% | strength 0.4, onset 3.6 g | strength 0.75, onset ~3.2 g |
  | 33% (nose canard) | strength 0.4, onset 5.2 g | unchanged |

  This also fixes the mirror case: when a canard *is* widest it takes the main slot and the real
  main wing is demoted to the secondary slot, where it would previously have rendered at 40%
  strength behind a raised onset.

* Small nose canards are untouched — they reach a third of the span, so they stay fully gated
  and the 0.6.26 secondary tightening is preserved.

---

## 0.6.37 — Ground Roll

### Fixed

* **Vortices spawned during the takeoff roll.** Introduced by the humidity floor in 0.6.36. The
  lift coefficient proxy `L/(q·b²)` is a **ratio** whose numerator and denominator both fall as
  V², so it does not shrink with speed at all — it just reports the gear-attitude CL, which for
  a low aspect ratio stock wing can sit right on the threshold. A craft accelerating down the
  runway therefore read as "slow and working hard".

  The physical discriminator is not speed and not CL, it is **what is carrying the aircraft**.
  On the runway the gear does and the wing carries a fraction of the weight, so there is no
  developed tip vortex to condense; once airborne the wing carries all of it by definition. Two
  independent gates now:
  * `airborneBlend` from `vessel.LandedOrSplashed`, smoothed over ~0.4 s so vapour appears at
    rotation rather than popping. Gates the load-factor path as well as the humidity floor.
  * A lift-share term on the humidity floor requiring load factor ≥ ~0.8→1.0. A steady approach
    is `L = W·cos γ` ≈ 1 g and passes; any roll where the gear takes load fails. This also
    covers a bounce or touch-and-go where `LandedOrSplashed` flickers.

### Notes

* Deliberately **not** suppressed by ground proximity or ground effect. The classic short-final
  wingtip-vapour photograph is taken deep in ground effect, typically under half a wingspan, so
  a height-above-terrain test would kill the exact case the feature exists to produce. This is
  recorded in the source so it does not get "fixed" later.

---

## 0.6.35 / 0.6.36 — Circulation Onset

The single largest behavioural change since 0.6.0.

### Added

* **Vortices on a slow, high-alpha landing approach.** Previously impossible: the gate was
  `Clamp01((g - 3f) / 10f)`, a hard zero below 3 g, and a stabilised approach is 1 g by
  definition.

  Load factor is not circulation. Γ = L/(ρ·V·b) and n = L/W, so Γ ∝ n·m/(ρ·V·b) — the *same*
  lift makes proportionally *more* circulation as speed and density fall. That is why a slow
  approach vortexes despite pulling no g. For a 20 t / 12 m fighter: a 3 g break turn at 250 m/s
  at sea level gives Γ ≈ 159 m²/s, and a 96 m/s approach at 1 g gives ≈ 140 m²/s — **88% of the
  break turn, from an aircraft doing nothing but flying straight.**

  Rather than rescale `g` and invalidate every threshold tuned against it, the **thresholds
  move**: required load scales with ρ·V. At the reference condition (sea level, 250 m/s) the
  scale is exactly 1, so all prior tuning is preserved untouched.

  | Condition | Scale | Onset |
  |---|---|---|
  | Sea level, 250 m/s | 1.00 | **3.00 g** (unchanged) |
  | Sea level, 400 m/s | 1.60 | 4.80 g |
  | Sea level, 96 m/s approach | 0.40 | 1.20 g |
  | 8 km, 250 m/s | 0.40 | 1.20 g |

  The onset *span* scales with it too, or the curve would be stretched across a range the
  aircraft cannot reach at approach speed.

* **Humid boundary layer floor.** Circulation alone undersells how photogenic approach vapour
  is, and the reason is not aerodynamic: the ground and ocean are the moisture source, so
  relative humidity in the bottom kilometre or two runs 70–90% and very little core pressure
  drop is needed to reach dew point. A visibility floor mirroring the cold-air contrail floor,
  requiring both:
  * **depth in the atmosphere** — `ρ / body.atmDensityASL`, measured against the body's *own*
    sea-level density, so it is per-planet for free
  * **a wing working near its lift limit** — `L/(q·b²)`, which is CL/aspect-ratio. Needs no wing
    area and no angle of attack, which matters because KSP does not expose one.

### Changed

* Secondary (canard) load gates scale by the same ρ·V factor, so the 0.6.26 tightening still
  holds exactly where it was tuned (cruise) while a slow approach is judged on circulation.
* `secondarySpeedMin` / `secondarySpeedFull` dropped from 120/200 m/s to 45/90 m/s. That floor
  was an instrument-noise guard, not a physical circulation term, and it silently vetoed every
  canard on a landing approach regardless of how hard the foreplane was working.

### Fixed

* **A parked aircraft would have sprouted vortices.** `rawLiftG` falls back to `vessel.geeForce`
  when the wings make no lift, and a craft sitting on its gear reads 1 g — so any onset floor
  below 1 g fires on the runway. The floor is 0.40 (onset 1.20 g, unreachable parked or in level
  cruise) plus a `flyingGate` ramp over 20→40 m/s.

* **`contrailBlend` could never reach 1.0 on any body.** The cold anchor sat at 220 K, below the
  coldest air any KSP body actually has, so the fully-developed contrail look was unreachable by
  construction. Anchors are now the real atmospheric-science numbers — 233 K (−40 °C, where
  homogeneous ice nucleation is certain) and 253 K (−20 °C) — and `RefreshContrailBand` samples
  the body's own temperature curve at load, lifting the cold anchor if the body's true minimum
  is warmer. Full development is now reachable on every body by construction, while never being
  handed out warmer than the physics allows. Logs the result:
  ```
  [VORTEX] contrail band on Kerbin: air 233.0-293.2K, band 233.0->253.0K, rho_ASL 1.225
  ```

---

## 0.6.34 — Body Relative

### Changed

* **Altitude removed from the effect model entirely.** The 8000–15000 m contrail band only ever
  meant anything on Kerbin: altitude is a property of the craft and says nothing about whether
  there is air outside it. What decides whether a persistent contrail forms is that the ambient
  air is already near saturation — i.e. cold enough. It is temperature, not height, that draws
  the line between the G-gated manoeuvre vortex down low and the essentially default-on contrail
  up high. Keyed off `vessel.atmosphericTemperature`, per-planet behaviour falls out with no
  per-body tuning.

* **The altitude ceiling is gone.** Fading out on leaving the atmosphere is now air density's
  job (`atmFactor`), which is the physically right reason for the effect to stop and is
  body-relative for free — a metres-based ceiling could only ever be correct on one planet.

* **Circulation model simplified to size alone, soft-saturated.** Γ ∝ (n·m)/(ρ·V·b): load factor
  is the g curve, density is already handled as visibility, leaving m/b — and that is *one*
  quantity, not two knobs. Real aircraft hold roughly constant wing loading, so m ∝ b² and m/b
  ∝ b. Bigger simply means stronger, which is exactly why wake-turbulence categories are drawn
  by size.

  The old hard clamp at `[0.5, 2.0]` was found by measurement to be pinning `circFactor` at its
  ceiling below 333 m/s for a 20 t craft and below 500 m/s for a 50 t one — so mass, span **and**
  speed were all inert across the entire normal envelope. Replaced with `2x/(1+x)`, which
  saturates (condensation *is* a threshold effect) without destroying the ordering above it:

  | Craft | Before | After |
  |---|---|---|
  | Light 3 t / 8 m | 0.86 | 0.86 |
  | Fighter 20 t / 12 m | **2.00** | 1.54 |
  | Heavy 50 t / 20 m | **2.00** | 1.67 |
  | Airliner 150 t / 35 m | **2.00** | 1.79 |

---

## 0.6.33 — Contrail Flow

### Fixed

* **The ribbon stopped flowing and appeared to drag at altitude.** Down low, brightness tracks
  G, so variation along the rope sits still in the air while the aircraft pulls away from it —
  that variation *is* the cue that says "this is being left behind". Up high the contrail floor
  pinned brightness near-constant, every ring came out identical, and a uniform straight line
  translating along its own axis has no visible motion at all. Nothing was actually dragging;
  there was simply nothing on the rope to see moving.

  Longitudinal brightness variation is now sampled against the per-ribbon birth odometer, so the
  pattern is frozen in the wake exactly like the helix phase and streams backward for free.
  Scaled by contrail blend, so the low-altitude look is untouched.

* **Ribbon ring budget bound before its lifetime.** Contrail lifetime runs to 5 s and a ring is
  committed once per frame, so at 200 m/s and 60 fps the 200-ring buffer held 660 m while the
  lifetime wanted 1000 m. Raised to 320.

---

## 0.6.32 — Tail Fade

### Fixed

* **The ribbon tail dragged behind the aircraft instead of fading.** Age-based fade never
  reached zero once the ring budget bound rather than the lifetime, so the rope ended on a blunt,
  still-visible face — and a new ring pushed the oldest out every frame, so that blunt end jumped
  forward. Close-out is now keyed to **position in the buffer**, which always closes whichever
  constraint happens to bind, and closes **radius as well as alpha** (without it the tube keeps
  full cross-section to the last ring — wider than the middle at altitude, since the tail ratio
  runs to 1.6 — and no amount of alpha fade hides a blunt tube end).

---

## 0.6.31 — Main Tubes

### Changed

* Tube (ribbon mesh) restricted to **main wing sources**; secondary surfaces keep their
  `TrailRenderer`. Not just to save frames: the inward curve models a free counter-rotating
  *pair* converging under its own induced flow, and a canard's vortex does not get to do that —
  it passes straight over the main wing and is entrained into the wing's own vortex system, so
  drawing it as an independent converging rope actively misrepresents it.

---

## 0.6.29 / 0.6.30 — Inward Curve, All Tubes

### Added

* **Procedural ribbon/tube mesh** replacing `TrailRenderer` for main vortices. Real vortex pairs
  curve *inwards* under mutual induction before evening out; the tube models that instead of
  spiralling.
* Unit-circle lookup table for the cross-section: 2 transcendentals per ring instead of 2×K,
  which matters once four tubes rebuild every frame rather than one.

### Fixed

* **The ribbon appeared to pull the previous trail when it restarted.** Helix phase was keyed to
  arc-length-from-head, which changes as the head moves. Replaced with a per-ring birth odometer,
  so shape is frozen in the wake at the moment each ring is laid.

---

## 0.6.14 – 0.6.28 — Shape, Jitter and Gating

### Fixed

* **Jitter around the wingtip anchor** — three separate discretisation faults:
  * the wake sink was batched at 10 Hz, producing a 12 cm teleport per application
  * shed history used `RoundToInt`, snapping at 12.5 Hz
  * raw lift was fed in unfiltered

* **A square trail head at the wingtip anchor.** The roll-up ramp was expressed as a *fraction of
  trail length*, which on a 500 m wake meant 500 m of taper — invisible. Reworked as a
  metres-based roll-up.

* **Anchor drift under high AoA.** The helix used to displace the *emitter*, so the trail's
  origin orbited the wingtip and was never actually welded to the anchor. It is now applied to
  stored vertices with a radius growing from zero, so the origin stays fixed.

* **Canted wingtip and canard detection.** Vortices spawned in mid-air near canted tips, and at
  the *root* of nose canards rather than the tip. Root causes:
  * Fore/aft was measured against the root part, whose orientation is a build-order accident.
    KSP parts are authored rocket-style with the stack along +Y, so on an aircraft built from a
    cockpit the root's local Y is the **nose** and local Z is **vertical** — the exact opposite
    of what the geometry code assumed. Measured on the test airframe: local Z spanned 3.9 m (the
    fuselage's height) while local Y spanned 21 m (its length). Only the lateral axis was ever
    safe, which is why wingtips still worked and this stayed hidden.
  * Tip search included the chordwise term, so the most-forward vertex of a nose canard beat its
    actual tip. Distance is now measured from the roll axis only.

* **Duplicate vortices on large multi-part wings.** Three defects, all putting several sources on
  one swept wing:
  1. The main-wing pass read `isLift`/`isControl` and then never tested them, so any large
     off-centre part — nacelle, pylon, tank — could win the slot.
  2. The canard pass excluded the main wing by *part identity*, which does nothing when the wing
     is several parts. On a swept wing an inboard panel legitimately sits further forward than
     the tip panel, so an inner section of the same wing became a bogus "canard".
  3. Fore/aft measured against the root part again.

  Replaced with **one rule, four slots**: {left, right} × {main, secondary}. The winner in each
  slot is simply the part reaching furthest from the centreline. Membership of a surface is
  decided by whether a part belongs to the same physically connected aero structure (part-tree
  *and* bounds adjacency flood fill), never by "is this forward of some plane". This is what caps
  an aircraft at four vortices structurally rather than by heuristics agreeing.

### Changed

* **Secondary / canard gating tightened** (0.6.26) — secondaries were persistent during mild
  manoeuvres. Onset is now interpolated by span ratio: a full-size canard is held near the main
  wing's onset, a small nose canard to 6 g. These are genuinely different aerodynamic cases — a
  primary canard sits in clean air ahead of the wing and canard pitch stability *requires* the
  foreplane to stall first, so it is deliberately flown at a higher lift coefficient than the
  main wing; a small nose canard carries a few percent of total lift over a short span.

* **Aft surfaces are no longer treated as equivalent to forward ones** (0.6.7 was wrong here).
  Every finite lifting surface sheds tip vortices, tailplanes included, so "does it shed one" is
  the wrong question. This mod draws *condensation*, which needs the core pressure drop to cool
  air past dew point, and that drop scales with circulation **squared**. A conventional stabiliser
  carries a few percent of the aircraft's lift over a much shorter span inside the wing's
  downwash — roughly a tenth of the circulation is a hundredth of the core pressure drop, nowhere
  near condensing. This is why photographs of hard-manoeuvring jets show vapour off wingtips,
  LERX and flap edges and essentially never off stabiliser tips. The one aft case kept is a true
  tandem wing, separated cleanly by span ratio.

* **Gradual fade at the atmospheric ceiling**, in both directions — the effect no longer pops out
  of existence in one frame on the way up or back in on the way down.

* **Memory leak fixed** — `OnDestroy` now destroys trail objects, anchors and ribbons.

### Removed

* **Particles (twice).** `velocityOverLifetime.orbital` orbits the *system transform*, so emitted
  particles kept spinning with the aircraft's roll long after they were shed and far behind it.
  `orbitalOffset` is a single scalar, not per-particle, and `simulationSpace = World` does not fix
  it. Unrealistic and easy to spot; reverted in favour of the trail renderer.

* **The visible corkscrew via `TrailRenderer`** (0.6.14–0.6.17). Diagnosed rather than
  re-attempted: a `TrailRenderer` is camera-billboarded and cannot twist, and a cross-section
  twist on a symmetric polygon is invisible by definition. At 1.8 rad/s and 200 m/s one turn
  spanned ~700 m at 0.1 m amplitude — a ratio of 1:7000. Retained as a wake-space helix, off by
  default.

---

## 0.6.1 — The Above-200 m Fix

The bug that started this branch: trails produced a violent forward spike during high-G
manoeuvres, but only above roughly 200 m altitude.

### Fixed

* **Krakensbane / floating-origin frame mismatch.** `FloatingOrigin.setOffset()` moves
  *world-anchored* geometry by one offset and the vessel frame by another, and the mod was
  compensating with the wrong one. Trail vertices, line history and ribbon points are all stored
  in world space, so they must be shifted by `offsetVessel + nonFrame`, while anchor positions
  belonging to a loaded, unpacked, flying vessel shift by `offsetVessel` alone.

  The 200 m threshold was never about altitude: `Krakensbane.SafeToEngage()` gates the velocity
  frame on being clear of the ground, so below ~200 m the frame simply never engaged and the bug
  could not appear.

  Root-caused by decompiling KSP's `Assembly-CSharp.dll` with Mono.Cecil and reading the actual
  IL, rather than by guesswork.

* **Teleport guard rewritten** from a frame-time test to a distance test. Origin shifts are now
  compensated properly, so anything left is a real teleport (vessel switch, docking, kraken):
  the anchor moved further than its own Unity-space velocity can account for. The bound uses
  `rb_velocity`, which is the Krakensbane-relative velocity that actually carries the anchor
  through Unity world space, so it stays correct whether or not the velocity frame is engaged —
  unlike true surface velocity, which would flag every frame above the gate.

---

## 0.6.0 and earlier

See the repository history.
