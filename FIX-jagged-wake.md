# Fix: jagged vortex wake in hard pulls (7+ g) at speed

Working note for applying and testing at home. **Not part of the mod.** Delete it before the next
release commit; `tools/package.py` doesn't pack root-level notes, but it shouldn't linger.

**Status:** code change is in `Source/WingtipVortex.cs` on branch `claude/nifty-mayer-2hqx8b`.
**Not compiled.** The session had no C# compiler, so build it and fly it before releasing.

---

## Symptom

At 292 m/s and 6 km, and in any pull of about 7 g or more, the tube wake draws as a regular
sawtooth. It shows from the side and not from above, it's there for the whole length of the rope,
and there is one tooth per rendered frame.

## Cause

The rope's **inward curve** (`ribbonInwardAmp`, 1.2 m toward the roll axis) was aimed separately
for every ring laid in a frame, measured from that ring's own position.

1. A frame doesn't lay one ring. `Update()` fills the gap from last frame's head to the wingtip
   with interpolated rings, and that gap is V × frame time: 6–12 m at 292 m/s.
2. `PushRibbonPoint` computed each ring's inward direction as "away from the roll axis, from this
   point", using `p - vesselTransform.position` with the `axLength` component projected out.
3. Behind the wingtip, the rings sit along the **flight path**, not the roll axis. The two differ
   by the angle of attack. A ring d metres back therefore has d·sin(AoA) of flight path left in its
   "radial" vector, and its inward direction tips toward the belly.
4. So the first ring of each frame was tipped the most and the ring at the tip not at all, and
   the next frame started over. That gives a sawtooth with one tooth per frame, in the pull plane.
5. The ring smoothing (`ribbonSmoothWindow`) only ever smoothed `pts`, never `inDir`. The inward
   offset is added to the centreline after smoothing, so nothing damped it.

It grows with **airspeed × sin(AoA)**. Holding 7+ g takes a high AoA at any speed, so every hard
pull draws it, and the extra airspeed at 292 m/s makes it worse. It is in the body-vertical plane,
which is why it shows from the side and not from above. 1.4.0's rigid-anchor fix was for the same
report, but it removed wing flex, not this.

### Numbers

Measured with a replica of the ring-laying code (`Update` + `PushRibbonPoint` + smoothing +
`BuildRibbonMesh`'s inward ease). Setup: Krakensbane-style sliding world, 292 m/s, 7 g pull, tip
6 m out from the root. The metric is each ring's inward displacement minus the average of its
neighbours, past the 20 m ease-in:

| Case | Before | After |
|---|---|---|
| AoA 10°, 30–45 fps | 0.21 m | 0.000 m |
| AoA 15°, 55–65 fps | 0.16 m | 0.000 m |
| AoA 20°, 30–45 fps | 0.39 m | 0.000 m |
| AoA 20°, 30–45 fps, rolling 180°/s | 0.44 m | 0.0006 m |

The tube's radius is about 0.2–0.5 m, so before the fix the teeth were as big as the rope itself.

---

## The fix (3 changes, all in `Source/WingtipVortex.cs`)

1. **One inward direction per frame, taken at the wingtip** (the root cause). The new
   `RibbonInward(rb, anchorPos)` measures the direction at the anchor only. The anchor is a fixed
   point on the rigid airframe, so this direction only turns when the airframe turns. The rings
   between last frame's head and the tip get `Vector3.Slerp(lastHeadDir, headDir, u)`, so a
   fast roll still turns the direction smoothly. `PushRibbonPoint` now receives the direction
   instead of computing it.
2. **The inward direction goes through the same smoothing pass as the points.** It's the second
   input into the drawn centreline. This is a safety net against attitude noise (for example
   root-part wobble at high q); in the replica, change 1 alone removes the sawtooth.
3. **Spiral-breakdown phase is fixed at birth.** It used to be `odo / spiralLambda`, recomputed
   every frame. `odo` is the whole flight's distance (easily 100 km), and `spiralLambda` is floored
   at 8 × ring spacing, which binds at jet speed. Ring spacing follows the wake lifetime, and that
   moves with frame time (`stability`), so a 1% wobble moved the phase by hundreds of radians. Any
   spiral already drawn re-wound at random every frame. The phase is now accumulated ring by ring
   in `PushRibbonPoint` (`sPhase[]`) at the wavelength in force when each ring was laid.
   This only matters when breakdown is engaged (high swirl: a hard pull at altitude or low speed).

Unchanged: the rope's shape at rest, the convergence amount, the ground-effect drift (it reads the
new, smooth direction), and the trail-renderer and line paths.

---

## Applying it at home

Either pull the branch:

```
git fetch origin claude/nifty-mayer-2hqx8b
git checkout claude/nifty-mayer-2hqx8b
```

or save the patch at the bottom as `wake.patch` and run `git apply wake.patch` on top of `ac00e5e`
(1.4.0).

Then build as usual. `tools/package.py` refuses to run unless the local project's copy matches, so
copy `Source/WingtipVortex.cs` over `WingtipVertex/WingtipVortex.cs` first.

## Test checklist

- [ ] It builds. Nothing here has been compiled yet.
- [ ] Same flight as the screenshot: ~290 m/s, ~6 km, pull 7+ g, chase camera from the side. The
      rope should be a smooth curve.
- [ ] A 7+ g pull at a lower speed (~150–200 m/s), where AoA is higher still.
- [ ] Try both a high and a capped frame rate (e.g. 30 fps): the old sawtooth got worse as
      frame time grew.
- [ ] Roll hard while pulling: the rope should stay smooth and still converge inward.
- [ ] Low pass near the runway: the ground-effect splay still opens outward.
- [ ] Nothing new in `KSP.log`. `tube rings=...` still prints once per flight.

### If a zigzag remains at 7+ g

Check the `[VORTEX] ... session peaks: ... tip flex peak=X m` line when you leave the flight.
`AnchorPosition()` switches from the rigid point back to the live, flexing part once flex exceeds
`rigidAnchorMaxFlexSpans` (0.25 × span). It's a hard switch between points ~3 m apart, and
`peakTipFlex` is only recorded when the switch **doesn't** fire. So a peak sitting just under
0.25 × span (≈3 m on a 12 m span) means a hard pull is pushing the wing past it and the anchor is
toggling. This would look different: irregular jumps of metres, not one even tooth per frame. The
fix would be a smooth blend or hysteresis there instead of the hard `return anchor.position`.
I didn't change it, because nothing so far shows it firing.

---

## Changelog text for the next release

> * **The vortex rope zigzagged in a hard pull, seen from the side.** Each frame lays several rings
>   between the last frame's position and the wingtip, and the rope's inward curve was aimed
>   separately for each of them. Behind the tip the flight path is angle-of-attack off the
>   airframe's axis, so every frame's rings were tipped toward the belly by an amount that reset
>   each frame: a sawtooth as wide as the rope, growing with speed and angle of attack, so it
>   showed in every 7+ g pull. The direction is now taken once per frame at the wingtip and
>   blended across the rings in between. 1.4.0's rigid-anchor fix removed wing flex, but not this.
> * **Spiral breakdown re-wound every frame at jet speed.** Its phase was recomputed from the whole
>   flight's distance against a wavelength that moves slightly with frame time, so any spiral
>   already drawn jumped to a random phase every frame. The phase is now set when each stretch of
>   rope is laid.

---

## Patch

Against `ac00e5e` (1.4.0), `Source/WingtipVortex.cs` only.

```diff
diff --git a/Source/WingtipVortex.cs b/Source/WingtipVortex.cs
index f67a543..4382568 100644
--- a/Source/WingtipVortex.cs
+++ b/Source/WingtipVortex.cs
@@ -2820,7 +2820,8 @@ public class WingtipVortex : MonoBehaviour
         public float[] odo;
         public float odometer;
 
-        // Inward direction, world space, frozen when the ring was laid.
+        // Inward direction, world space, set when the ring was laid (see RibbonInward), smoothed
+        // with the points while the ring is inside the smoothing window, then never touched.
         public Vector3[] inDir;
 
         // Ground-effect drift velocity and ground factor, frozen at birth for the same reason as
@@ -2834,6 +2835,11 @@ public class WingtipVortex : MonoBehaviour
         public float[] bdDepth;
         public float[] bdMode;
 
+        // Spiral-breakdown phase, accumulated ring to ring at the wavelength in force when each
+        // ring was laid, and frozen at birth. See SpiralLambda.
+        public float[] sPhase;
+        public float spiralPhase;
+
         public float spacing;   // current ring spacing, metres
 
         public float seed;   // so the two wingtips never band identically
@@ -2877,6 +2883,7 @@ public class WingtipVortex : MonoBehaviour
         r.bdT = new float[rings];
         r.bdDepth = new float[rings];
         r.bdMode = new float[rings];
+        r.sPhase = new float[rings];
         r.spacing = ribbonMinPointDist;
         r.odometer = 0f;
         r.count = 0;
@@ -2931,14 +2938,74 @@ public class WingtipVortex : MonoBehaviour
         return 1f + depth * ((Mathf.PerlinNoise(r.odometer * ribbonFlowScale, r.seed) - 0.5f) * 2f);
     }
 
-    void PushRibbonPoint(WakeRibbon r, Vector3 p, float now, float shed, float flowDepth)
+    // Toward the ROLL AXIS, from the anchor.
+    //
+    // This used to pick between +lateral and -lateral on the sign of the ring's lateral
+    // offset, and that is the zig-zag. On a cruciform tail two of the four fins sit at
+    // lateral offset ~0 by construction, so `side` hovers on the sign boundary and ordinary
+    // airframe wobble flips it from ring to ring. Consecutive rings then get inward
+    // directions pointing OPPOSITE ways, and because the displacement is scaled by `ease`,
+    // which grows with distance from the head, the alternation opens into a sawtooth that
+    // widens down the wake — exactly the shape in the report, and exactly why it settles
+    // down when the craft stops manoeuvring and the wobble stops crossing zero.
+    //
+    // The real quantity was never "which side of the centreline", it was "which way is the
+    // axis from here". Projecting out the roll-axis component gives that directly: no sign
+    // test, no boundary to sit on, and correct for a fin pointing straight up, where toward
+    // the centreline means DOWN and neither lateral direction was ever right.
+    //
+    // Unchanged for a wing — at a wingtip the radial-outward direction is lateral plus a
+    // little dihedral, so inward still points essentially along the span toward the root.
+    //
+    // Measured at the ANCHOR, once per frame, and never at a ring laid behind it. The rings a
+    // frame lays are spread back along the flight path to where the last frame's head now sits,
+    // V * frame time behind the tip: 6-12 m at 292 m/s. The flight path is not the roll axis,
+    // it is angle of attack below it, so projecting out the roll axis at a point d metres back
+    // leaves d * sin(AoA) of the flight path in the "radial" direction. Every frame's rings
+    // were therefore tipped toward the belly by an amount that ramped from most at its first
+    // ring to nothing at the tip, then jumped back at the next frame's: a sawtooth of up to
+    // 0.4 m on a 12 m span at 15-20 degrees AoA and 30-45 fps, as large as the tube itself,
+    // one tooth per frame. Seen from the side and gone from above. It grows with airspeed times
+    // sin(AoA), and holding 7+ g takes a high AoA at any speed, so every hard pull drew it, and
+    // the ring smoothing never touched it: that only ever acted on the points. The anchor is a
+    // fixed point on the rigid airframe, so this direction only turns as the airframe does;
+    // Update() hands the rings in between a slerp from the last head's direction to this.
+    Vector3 RibbonInward(WakeRibbon r, Vector3 anchorPos)
+    {
+        Vector3 rel = anchorPos - vesselTransform.position;
+        Vector3 radialOut = rel - axLength * Vector3.Dot(rel, axLength);
+        if (radialOut.sqrMagnitude > 1e-6f) return -radialOut.normalized;
+        return (r.count > 0) ? r.inDir[0] : -axLateral;   // degenerate: hold the last
+    }
+
+    // Spiral-breakdown wavelength for a given ring spacing: a turn shorter than
+    // spiralMinRingsPerTurn rings cannot be drawn by this mesh and would alias rather than read
+    // as small, so ring spacing floors it.
+    float SpiralLambda(float spacing)
+    {
+        return Mathf.Max(spiralWavelengthCores * frameR0Vis, spiralMinRingsPerTurn * Mathf.Max(spacing, 0.1f));
+    }
+
+    void PushRibbonPoint(WakeRibbon r, Vector3 p, float now, float shed, float flowDepth, Vector3 inward)
     {
         if (r.count > 0)
         {
             r.shed[0] = shed * RibbonFlowVar(r, flowDepth);   // keep the head's strength live
             if ((p - r.pts[0]).sqrMagnitude < 0.0625f) return;   // 0.25 m, anti-bunching only
         }
-        if (r.count > 0) r.odometer += (p - r.pts[0]).magnitude;
+        if (r.count > 0)
+        {
+            float step = (p - r.pts[0]).magnitude;
+            r.odometer += step;
+            // The spiral's phase advances by this step at the wavelength in force NOW, and is
+            // stored rather than derived later as odo / lambda. Lambda is floored by ring
+            // spacing, which follows speed and the wake lifetime and so moves a little every
+            // frame; odo is the whole flight's distance, so phase = odo / lambda with the
+            // current lambda moved by hundreds of radians per frame for a 1% change, and every
+            // spiral already laid re-wound at random each frame at jet speed.
+            r.spiralPhase = Mathf.Repeat(r.spiralPhase + step * (Mathf.PI * 2f) / SpiralLambda(r.spacing),
+                                         Mathf.PI * 2f);
+        }
 
         int n = Mathf.Min(r.count, r.pts.Length - 1);
         System.Array.Copy(r.pts, 0, r.pts, 1, n);
@@ -2951,31 +3018,9 @@ public class WingtipVortex : MonoBehaviour
         System.Array.Copy(r.bdT, 0, r.bdT, 1, n);
         System.Array.Copy(r.bdDepth, 0, r.bdDepth, 1, n);
         System.Array.Copy(r.bdMode, 0, r.bdMode, 1, n);
+        System.Array.Copy(r.sPhase, 0, r.sPhase, 1, n);
 
-        // Toward the ROLL AXIS, decided now and never revisited.
-        //
-        // This used to pick between +lateral and -lateral on the sign of the ring's lateral
-        // offset, and that is the zig-zag. On a cruciform tail two of the four fins sit at
-        // lateral offset ~0 by construction, so `side` hovers on the sign boundary and ordinary
-        // airframe wobble flips it from ring to ring. Consecutive rings then get inward
-        // directions pointing OPPOSITE ways, and because the displacement is scaled by `ease`,
-        // which grows with distance from the head, the alternation opens into a sawtooth that
-        // widens down the wake — exactly the shape in the report, and exactly why it settles
-        // down when the craft stops manoeuvring and the wobble stops crossing zero.
-        //
-        // The real quantity was never "which side of the centreline", it was "which way is the
-        // axis from here". Projecting out the roll-axis component gives that directly: no sign
-        // test, no boundary to sit on, and correct for a fin pointing straight up, where toward
-        // the centreline means DOWN and neither lateral direction was ever right.
-        //
-        // Unchanged for a wing — at a wingtip the radial-outward direction is lateral plus a
-        // little dihedral, so inward still points essentially along the span toward the root.
-        Vector3 rel = p - vesselTransform.position;
-        Vector3 radialOut = rel - axLength * Vector3.Dot(rel, axLength);
-        if (radialOut.sqrMagnitude > 1e-6f)
-            r.inDir[0] = -radialOut.normalized;
-        else
-            r.inDir[0] = (r.count > 0) ? r.inDir[1] : -axLateral;   // degenerate: hold the last
+        r.inDir[0] = inward;   // see RibbonInward
 
         // GROUND EFFECT for this ring. Height is the vessel's own height above the ground plus
         // this point's offset from it along local up, which assumes the ground under the wingtip
@@ -3001,6 +3046,7 @@ public class WingtipVortex : MonoBehaviour
         }
 
         r.bdT[0] = frameBreakT; r.bdDepth[0] = frameBreakDepth; r.bdMode[0] = frameBreakMode;
+        r.sPhase[0] = r.spiralPhase;
 
         r.pts[0] = p; r.birth[0] = now; r.shed[0] = shed * RibbonFlowVar(r, flowDepth); r.odo[0] = r.odometer;
         r.count = Mathf.Min(r.count + 1, r.pts.Length);
@@ -3058,7 +3104,8 @@ public class WingtipVortex : MonoBehaviour
         float spacingNow = Mathf.Max(r.spacing, 0.1f);
         float bubbleWidthT = Mathf.Max(bubbleLengthCores * frameR0Vis, bubbleMinRings * spacingNow)
                              / Mathf.Max(frameSpeed, 1f);
-        float spiralLambda = Mathf.Max(spiralWavelengthCores * frameR0Vis, spiralMinRingsPerTurn * spacingNow);
+        // Sets only how fast the spiral decays; its phase was fixed at birth (see r.sPhase).
+        float spiralLambda = SpiralLambda(spacingNow);
         float groundCap = groundSpreadMaxSpans * vesselSpan;
 
         // Parallel transport. A naive Cross(tangent, up) frame flips violently wherever the
@@ -3175,9 +3222,10 @@ public class WingtipVortex : MonoBehaviour
             if (!live) radius = 0f;
 
             // Bend toward the centreline, easing in over ribbonInwardGrow and then holding, so
-            // the rope converges a little and afterwards runs parallel. Direction was frozen at
-            // birth; only the magnitude eases, and it saturates within the first few metres, so
-            // nothing already shed can be re-aimed by later manoeuvring.
+            // the rope converges a little and afterwards runs parallel. Direction was set at birth
+            // and settles once the ring leaves the smoothing window; only the magnitude eases, and
+            // it saturates within the first few metres, so nothing already shed can be re-aimed
+            // by later manoeuvring.
             float ease = Mathf.Clamp01(arc / Mathf.Max(ribbonInwardGrow, 0.01f));
             ease = ease * ease * (3f - 2f * ease);
             // Scaled by the SOURCE's strength, not by this ring's shed. Shed is the live load at
@@ -3212,7 +3260,7 @@ public class WingtipVortex : MonoBehaviour
                 float sDecayT = spiralDecayTurns * spiralLambda / Mathf.Max(frameSpeed, 1f);
                 float sAmp = spiralAmpCores * frameR0Vis * bdSize * (1f - bdBubble) * bdOnset
                              * Mathf.Exp(-bdP / Mathf.Max(sDecayT, 0.01f));
-                float sPhase = (r.odo[src] / spiralLambda) * Mathf.PI * 2f + spiralPrecession * age + r.seed;
+                float sPhase = r.sPhase[src] + spiralPrecession * age + r.seed;
                 centre += (sN * Mathf.Cos(sPhase) + sB * Mathf.Sin(sPhase)) * sAmp;
             }
 
@@ -4270,25 +4318,41 @@ public class WingtipVortex : MonoBehaviour
                 rb.spacing = spacing;
                 if (ribbonLive)
                 {
-                    if (rb.count == 0) PushRibbonPoint(rb, anchorPos, now, visible, flowDepth);
+                    // One inward direction per frame, at the anchor; the rings in between get a
+                    // slerp from the last head's. See RibbonInward.
+                    Vector3 headIn = RibbonInward(rb, anchorPos);
+                    if (rb.count == 0) PushRibbonPoint(rb, anchorPos, now, visible, flowDepth, headIn);
                     else
                     {
                         Vector3 last = rb.pts[0];
+                        Vector3 lastIn = rb.inDir[0];
                         float gap = Vector3.Distance(anchorPos, last);
                         int steps = Mathf.Clamp(Mathf.CeilToInt(gap / spacing), 1, 8);
                         for (int sub = 1; sub <= steps; sub++)
-                            PushRibbonPoint(rb, Vector3.Lerp(last, anchorPos, (float)sub / steps), now, visible, flowDepth);
+                        {
+                            float u = (float)sub / steps;
+                            PushRibbonPoint(rb, Vector3.Lerp(last, anchorPos, u), now, visible, flowDepth,
+                                            Vector3.Slerp(lastIn, headIn, u));
+                        }
                     }
                 }
                 // See ribbonSmoothWindow. Translation-invariant, so it commutes with
-                // OnFloatingOriginShift and needs no special handling there.
+                // OnFloatingOriginShift and needs no special handling there. The inward direction
+                // gets the same pass: it is the second channel into the drawn centreline, and
+                // smoothing only the points left any ring-to-ring noise in it drawn as is.
                 if (ribbonSmoothAmount > 0f && rb.count >= 3)
                 {
                     int w = Mathf.Min(ribbonSmoothWindow, rb.count - 2);
                     for (int j = 1; j <= w; j++)
+                    {
                         rb.pts[j] = Vector3.Lerp(rb.pts[j],
                                                  (rb.pts[j - 1] + rb.pts[j + 1]) * 0.5f,
                                                  ribbonSmoothAmount);
+                        Vector3 d = Vector3.Lerp(rb.inDir[j],
+                                                 (rb.inDir[j - 1] + rb.inDir[j + 1]) * 0.5f,
+                                                 ribbonSmoothAmount);
+                        if (d.sqrMagnitude > 1e-6f) rb.inDir[j] = d.normalized;
+                    }
                 }
 
                 BuildRibbonMesh(rb, now, rScale * Mathf.Lerp(1f, contrailWidthScale, contrailBlend), contrailBlend, life,
```
