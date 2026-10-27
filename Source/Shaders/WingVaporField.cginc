// The wing vapor's field and raymarch, shared by WingVapor.shader and the GPU test.
// Ported from aeronautic (github.com/Romainlg29/aeronautic @ ca535855f7),
// vapor-material.ts, tsl/field.ts and tsl/noise.ts.
// MIT License, (c) 2026 Romain Le Gall; see THIRD-PARTY-NOTICES.md.
#ifndef WINGVAPOR_FIELD_INCLUDED
#define WINGVAPOR_FIELD_INCLUDED
#include "UnityCG.cginc"
#include "AgxTonemap.cginc"

#define PI 3.14159265
#define FAR 1e5
#define BOUND_SAFETY 0.85
#define OPAQUE_TRANSMITTANCE 0.01
#define MULTIPLE_SCATTERING 4.0
#define ISOTROPIC_SHARE 0.5
#define MULTIPLE_SHADE 0.25
#define PIXEL_STEP 0.0015
#define CONDENSATION_MIN_RATIO 0.3
#define CONDENSATION_TEXELS 128.0
#define SHAPE_STATIONS 64.0
#define SHAPE_ROWS 3.0
#define NOSE_RADIUS 0.03
#define NOSE_REACH 0.3
#define NOSE_BUILD_M 0.6
#define FRONT_BUILD_M 1
#define ROOT_FADE 0.12
#define ROOT_BLUR 1.0
#define SHOCK_WIDTH 0.015
#define BURST_LENGTH 0.08
#define BURST_SWELL 2.0
#define CONE_SHOCK_LEAN 0.08
#define CONE_SHOCK_THICKNESS 0.01
#define CONE_POCKET_LENGTH 0.45
#define TRAIL_POINTS 64.0
#define TRAIL_SAFETY 0.85
#define TIP_START_SHARE 0.4
#define BOUND_FLOOR 0.003

sampler2D _VaporTable;
sampler2D _VaporShape;
sampler2D _VaporTrails;
// WingCover : x, folded span; read texel by texel. _CoverGrid: origin x, cell, width, height.
sampler2D _VaporCover;
float4 _CoverGrid;
// WingCover.FlowLean.
float _FlowLean, _BodyTrailM;
float4 _VaporFog, _VaporFogColor;
float _VaporDepthFlip;
float4 _ForeEnvelope;
float _CoverOn, _InnerSpanM, _BodyCover, _CoverFlat, _CoverBodyRadius, _FrameIndex, _CoverFrontM, _ProfilePower, _RaiseOnWing;

// WingCover.Profile : exp(-(h/reach)^power) with a cover, exp(-h/reach) without.
float Profile(float height, float reach)
{
    float r = max(height, 0) / max(reach, 1e-6);
    return exp(-(_CoverOn > 0.5 ? pow(r, _ProfilePower) : r));
}

float ProfileTop(float ratio)
{
    float l = log(max(ratio, 1));
    return _CoverOn > 0.5 ? pow(l, 1 / _ProfilePower) : l;
}
float _BodyDeficit, _BodyReachM, _BodyBoundM, _BodyFloorBlendM;
// the canard's sheet. A: root span, semispan, root leading edge, root chord; B: tip leading
// edge, tip chord, root and tip height; C: section lift per chord, bound.
float4 _CanardA, _CanardB, _CanardC;
float4 _BodyBox; // x range, half span, margin
UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

float3 _BoxMin, _BoxMax;
// the wing's flex, rise = (x + y s) s at s outboard of the root (z).
float4 _WingFlex;
// Component opacity masks (wing, body, canard); zero leaves every component enabled.
float4 _DebugOff;
float3 _SunDir, _SunLight, _SkyLight;
float _GammaOut;
// Camera-dependent pixel step (VaporMaterial.WritePixelStep).
float _PixelStep;
float _Exposure, _Extinction, _Anisotropy, _EddyM, _ShutterS, _VaporTime, _Seed, _StepScale, _Detail, _MaxSteps;
float _EffectTip, _EffectWing, _EffectEdge, _EffectCone, _EffectShadow;

float _Mach, _SpeedMPerS, _CosAlpha, _SinAlpha, _FlowZ, _RollBias, _SlipBias, _VortexK, _SaturationDeficit;
float _SemispanM, _RootSpanM, _SectionLiftM, _ReachChordM, _Separation, _Cos2Sweep;
float _EdgeApexM, _EdgeGradient, _EdgeLengthM, _EdgeCore, _EdgeHeight, _EdgeBurstM, _EdgeBoundM;
float _WingBoundM, _EvaporationM;
float _NoseM, _BodyLengthM, _BodyRadiusM, _BodyHeightM, _ConeStrength, _ConeShock, _ConeShockWidth, _ConeFade, _ConeBoundM;
float _WingScale, _WingSonic, _WingBeta, _WingKt, _WingCap, _WingReach, _WingTravel, _WingSupersonic;
float _ConeScale, _ConeBeta, _ConeKt, _ConeCap, _ConeReachM;
float _TipLeadingM, _TipChordM, _TipHeightM, _TipCirculation, _TipCore2M2, _TipStart2M2, _TipRollupM, _TipGrowthM, _TipReachM, _TipBoundM, _TrailSpacingM;
float _SecondSemispanM, _SecondTipLeadingM, _SecondTipChordM, _SecondTipHeightM, _SecondCirculation, _SecondCore2M2, _SecondStart2M2;
float _SecondRollupM, _SecondGrowthM, _SecondReachM, _SecondBoundM, _SecondSpacingM;

struct v2f
{
    float4 pos : SV_POSITION;
    float3 local : TEXCOORD0;
    float3 view : TEXCOORD1;
    float4 screen : TEXCOORD2;
};

// The unit cube opened to the field's bounds.
v2f vert(float4 vertex : POSITION)
{
    v2f o;
    float3 local = _BoxMin + (vertex.xyz + 0.5) * (_BoxMax - _BoxMin);
    o.pos = UnityObjectToClipPos(float4(local, 1));
    o.local = local;
    o.view = UnityObjectToViewPos(float4(local, 1));
    o.screen = ComputeScreenPos(o.pos);
    return o;
}

// ---- noise.ts ----
float HashCell(float3 cell)
{
    float3 p = frac(cell * 0.3183099 + float3(0.1, 0.2, 0.3)) * 17;
    return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
}

float ValueNoise(float3 p)
{
    float3 cell = floor(p);
    float3 f = frac(p);
    float3 e = f * f * (3 - 2 * f);
    return lerp(
        lerp(lerp(HashCell(cell), HashCell(cell + float3(1, 0, 0)), e.x),
             lerp(HashCell(cell + float3(0, 1, 0)), HashCell(cell + float3(1, 1, 0)), e.x), e.y),
        lerp(lerp(HashCell(cell + float3(0, 0, 1)), HashCell(cell + float3(1, 0, 1)), e.x),
             lerp(HashCell(cell + float3(0, 1, 1)), HashCell(cell + float3(1, 1, 1)), e.x), e.y),
        e.z);
}

float Patchiness(float3 p)
{
    return (ValueNoise(p) * 0.65 + ValueNoise(p * 2.37 + float3(5.2, 1.3, 7.7)) * 0.35 - 0.5) * 2.4;
}

// ---- field.ts ----
float4 ShapeRow(float share, float row)
{
    float position = saturate(share) * (SHAPE_STATIONS - 1);
    float index = min(floor(position), SHAPE_STATIONS - 2);
    float v = (row + 0.5) / SHAPE_ROWS;
    float4 a = tex2Dlod(_VaporShape, float4((index + 0.5) / SHAPE_STATIONS, v, 0, 0));
    float4 b = tex2Dlod(_VaporShape, float4((index + 1.5) / SHAPE_STATIONS, v, 0, 0));
    return lerp(a, b, position - index);
}

float VortexDeficit(float circulation, float radius2, float core2)
{
    return _VortexK * circulation * circulation / max(radius2 + core2, 1e-6);
}

float Capped(float deficit, float cap)
{
    return deficit > 0 ? cap * (1 - exp(-deficit / max(cap, 1e-4))) : deficit;
}

float KarmanTsien(float incompressible, float beta, float kt)
{
    return incompressible / max(beta + kt * incompressible, beta * 0.5);
}

float SideLoading(float span)
{
    return clamp(_RollBias * span / max(_SemispanM, 1e-3) + _SlipBias * sign(span) + 1, 0.2, 1.8);
}

struct Part { float deficit; float outside; float step; };

// One point of a trail, by texel: rows are the wing's +z and -z tips, then the second surface's.
float3 TrailTexel(float index, float row)
{
    return tex2Dlod(_VaporTrails, float4((index + 0.5) / TRAIL_POINTS, (row + 0.5) / 4.0, 0, 0)).xyz;
}

float3 TrailAt(float row, float along, float spacing)
{
    float position = clamp(along / max(spacing, 1e-6), 0, TRAIL_POINTS - 1);
    float index = min(floor(position), TRAIL_POINTS - 2);
    float t = position - index;
    return lerp(TrailTexel(index, row), TrailTexel(index + 1, row), t);
}

// A point along a trail and the point a spacing further on, from three texels: on the last
// segment the third is the second again.
void TrailSpan(float row, float along, float spacing, out float3 at, out float3 next)
{
    float position = clamp(along / max(spacing, 1e-6), 0, TRAIL_POINTS - 1);
    float index = min(floor(position), TRAIL_POINTS - 2);
    float t = position - index;
    float3 a = TrailTexel(index, row);
    float3 b = TrailTexel(index + 1, row);
    float3 c = TrailTexel(min(index + 2, TRAIL_POINTS - 1), row);
    at = lerp(a, b, t);
    next = lerp(b, c, t);
}

struct Tip { float leading, chord, height, semispan, circulation, core2, start2, rollup, growth, reach, bound, spacing, row; };

Tip TipOf(float second)
{
    Tip t;
    if (second < 0.5)
    {
        t.leading = _TipLeadingM; t.chord = _TipChordM; t.height = _TipHeightM; t.semispan = _SemispanM;
        t.circulation = _TipCirculation; t.core2 = _TipCore2M2; t.start2 = _TipStart2M2; t.rollup = _TipRollupM;
        t.growth = _TipGrowthM; t.reach = _TipReachM; t.bound = _TipBoundM; t.spacing = _TrailSpacingM; t.row = 0;
    }
    else
    {
        t.leading = _SecondTipLeadingM; t.chord = _SecondTipChordM; t.height = _SecondTipHeightM; t.semispan = _SecondSemispanM;
        t.circulation = _SecondCirculation; t.core2 = _SecondCore2M2; t.start2 = _SecondStart2M2; t.rollup = _SecondRollupM;
        t.growth = _SecondGrowthM; t.reach = _SecondReachM; t.bound = _SecondBoundM; t.spacing = _SecondSpacingM; t.row = 2;
    }
    return t;
}

// One tip vortex at one point: deficit, distance outside its tube, step.
Part TipSide(Tip tip, float3 p, float side)
{
    float row = tip.row + (side > 0 ? 0 : 1);
    float tipLe = tip.leading, tipTe = tip.leading + tip.chord;
    float lengthM = tip.spacing * (TRAIL_POINTS - 1);
    float3 start = TrailTexel(0, row);
    float along = dot(p - start, float3(_CosAlpha, _SinAlpha, _FlowZ));
    bool ahead = along < 0;
    float3 axis = float3(clamp(p.x, tipLe, tipTe), tip.height, tip.semispan * side);
    if (!ahead)
    {
        // Onto the straight trail, then twice onto the trail's own tangent (nearest_along).
        [unroll] for (int s = 0; s < 2; s++)
        {
            float here = clamp(along, 0, lengthM);
            float3 at, next;
            TrailSpan(row, here, tip.spacing, at, next);
            float3 d = next - at;
            float l = length(d);
            if (l > 1e-6) along = max(here + dot(p - at, d / l), 0);
        }
        axis = TrailAt(row, along, tip.spacing);
    }
    float trailed = max(along, 0);
    float3 offset = p - axis;
    float radius2 = dot(offset, offset);
    // Rolling up behind it: from a share of the circulation round a thin core to all of it round
    // the rolled-up one.
    float rolled = 1 - exp(-trailed / max(tip.rollup, 1e-3));
    float formed = ahead ? saturate((p.x - tipLe) / tip.chord) * TIP_START_SHARE : rolled * (1 - TIP_START_SHARE) + TIP_START_SHARE;
    float core2 = tip.start2 + (tip.core2 - tip.start2) * rolled + tip.growth * trailed;
    float circulation = tip.circulation * formed * SideLoading(tip.semispan * side);
    // How wide the tube is here, by the bounds' rule; the distance is not trusted in full, nor for
    // more than a few tube widths at once.
    float needed = max(_SaturationDeficit, BOUND_FLOOR);
    float wide2 = _VortexK * circulation * circulation / needed - core2;
    float wide = min(sqrt(max(wide2, 0)) * 1.15 + 0.1, tip.bound);
    float outside = min((sqrt(radius2) - wide + max(along - tip.reach, 0)) * TRAIL_SAFETY, tip.bound * 4 + 1);
    Part r;
    r.deficit = VortexDeficit(circulation, radius2, core2);
    r.outside = tip.bound > 0 ? outside : FAR;
    r.step = clamp(sqrt(core2) * 0.4, 0.03, 1.5);
    return r;
}

// Every tip vortex at one point, each along its own trail (tip_vortex). Only tips that fog pay
// for their trails.
Part TipVortex(float3 p)
{
    Part total;
    total.deficit = 0;
    total.outside = FAR;
    total.step = FAR;
    [unroll] for (int which = 0; which < 2; which++)
    {
        Tip tip = TipOf(which);
        if (tip.bound > 0)
        {
            [unroll] for (int k = 0; k < 2; k++)
            {
                Part part = TipSide(tip, p, k == 0 ? 1 : -1);
                total.deficit += part.deficit;
                total.step = part.outside < total.outside ? part.step : total.step;
                total.outside = min(total.outside, part.outside);
            }
        }
    }
    return total;
}

// VaporField.EdgeRopeM, EdgeRopeDeficit, EdgeRopeLengthM, VaporFieldModel.RopeTrail.
#define ROPE_TRAIL 0.2
#define ROPE_OUT 0.9
// VaporFieldModel.RopeLift, RopeClear, RopeBreak.
#define ROPE_LIFT 0.35
#define ROPE_CLEAR 0.7
#define ROPE_BREAK 0.06
// Aerodynamics.LeadingEdgeVortexSpan.
#define LEADING_EDGE_VORTEX_SPAN 0.7
float _EdgeRopeM, _EdgeRopeDeficit, _EdgeRopeLengthM, _EdgeRopeWalkM, _EdgeRopeFlare, _EdgeTrailM;
float4 _RopeAxisA, _RopeAxisB;

Part EdgePart(float3 p, float side)
{
    Part r;
    float along = p.x - _EdgeApexM;
    float lengthM = max(_EdgeLengthM, 1e-3);
    float onWing = clamp(along, 0, _EdgeLengthM);
    float past = max(along - _EdgeLengthM, 0);
    float4 path = ShapeRow(onWing / lengthM, 2);
    float vz = path.x;
    float reach = path.z;
    float vy = path.y + _EdgeHeight * reach + past * (_SinAlpha / max(_CosAlpha, 0.1));
    float2 across = float2(p.y - vy, p.z - vz);
    float radius2 = dot(across, across);
    float burst = smoothstep(_EdgeBurstM, _EdgeBurstM + lengthM * BURST_LENGTH, along);
    float core = max(_EdgeCore * reach, 0.02) * (burst * BURST_SWELL + 1);
    float leaving = exp(-past / (lengthM * 0.3));
    float styled = 0;
    float rope = 0;
    if (_EdgeRopeM > 0)
    {
        // The coherent core follows the free airflow axis, not the edge table.
        float ropeLength = max(_EdgeRopeLengthM, 0.1);
        float start = _RopeAxisA.x;
        float share = (along - start) / ropeLength;
        float distance = max(along - start, 0);
        float drift = _RopeAxisB.y * distance;
        float sourceSide = p.z * side - drift >= 0 ? 1 : -1;
        vz = sourceSide * _RopeAxisA.y + drift;
        rope = _EdgeRopeM * (0.3 + 0.7 * min(share, 1));
        vy = _RopeAxisA.z + _RopeAxisB.x * distance;
        across = float2(p.y - vy, p.z * side - vz);
        radius2 = dot(across, across);
        float flat = radius2 / (rope * rope);
        styled = _EdgeGradient > 0 ? _EdgeRopeDeficit * SideLoading(sourceSide * _RopeAxisA.y) * smoothstep(0, 0.2, share) * exp(-max(share - 1, 0) / (_EdgeRopeWalkM > 0 ? ROPE_BREAK : ROPE_TRAIL)) * exp(-flat) : 0;
    }
    r.deficit = along > 0
        ? (_EdgeRopeM > 0 ? styled : VortexDeficit(_EdgeGradient * SideLoading(vz * side) * onWing * (1 - 0.3 * burst) * leaving, radius2, core * core))
        : 0;
    float outside = (sqrt(radius2) - _EdgeBoundM + max(-along, 0) + max(past - _EdgeTrailM, 0)) * BOUND_SAFETY;
    r.outside = _EdgeBoundM > 0 ? outside : FAR;
    r.step = clamp((_EdgeRopeM > 0 ? rope : core) * 0.4, 0.03, 1);
    return r;
}

#define COVER_EMPTY -1e4
// VaporFieldModel.CoverTrailM.
#define COVER_TRAIL_M 1.2
// VaporFieldModel.FrontRiseM.
#define FRONT_RISE_M 0.1
// VaporFieldModel.FrontShare, FrontGrowth.
#define FRONT_SHARE 0.35
#define FRONT_GROWTH 0.45
// WingCover.WrapFull, WrapNone, WrapFullM, WrapNoneM.
#define WRAP_FULL 1
#define WRAP_NONE 1.5
#define WRAP_FULL_M 0.6
#define WRAP_NONE_M 1.0
#define COVER_MAX_RISE 1.2
#define BODY_DOME_EDGE 0.35

// WingCover.Base: how far a rise is left unwrapped, 0 to 1.
float Unwrapped(float rise, float sheet)
{
    return max(smoothstep(WRAP_FULL * sheet, WRAP_NONE * sheet, rise), smoothstep(WRAP_FULL_M, WRAP_NONE_M, rise));
}

float4 CoverTexel(int u, int v)
{
    v = v < 0 ? -v - 1 : v;
    if (u < 0 || u >= (int)_CoverGrid.z || v >= (int)_CoverGrid.w) return float4(0, COVER_EMPTY, 0, COVER_EMPTY);
    return tex2Dlod(_VaporCover, float4((u + 0.5) / _CoverGrid.z, (v + 0.5) / _CoverGrid.w, 0, 0));
}

float CoverHeight(float4 values, float4 weights)
{
    float4 valid = float4(values.x > COVER_EMPTY, values.y > COVER_EMPTY, values.z > COVER_EMPTY, values.w > COVER_EMPTY);
    float4 supported = weights * valid;
    float weight = dot(supported, float4(1, 1, 1, 1));
    return weight >= 0.5 ? dot(values, supported) / max(weight, 1e-6) : COVER_EMPTY;
}

// Mask weights interpolate normally; missing roof samples are never elevations.
float4 CoverAt(float x, float span)
{
    float u = (x - _CoverGrid.x) / _CoverGrid.y - 0.5;
    float v = span / _CoverGrid.y - 0.5;
    if (u < -0.5 || v > _CoverGrid.w - 0.5 || u > _CoverGrid.z - 0.5) return float4(0, COVER_EMPTY, 0, COVER_EMPTY);
    int u0 = (int)floor(u), v0 = (int)floor(v);
    float tu = u - u0, tv = v - v0;
    float4 a = CoverTexel(u0, v0), b = CoverTexel(u0 + 1, v0), c = CoverTexel(u0, v0 + 1), d = CoverTexel(u0 + 1, v0 + 1);
    float4 near = a + (b - a) * tu;
    float4 result = near + ((c + (d - c) * tu) - near) * tv;
    float4 weights = float4((1-tu)*(1-tv), tu*(1-tv), (1-tu)*tv, tu*tv);
    result.y = CoverHeight(float4(a.y, b.y, c.y, d.y), weights);
    result.w = CoverHeight(float4(a.w, b.w, c.w, d.w), weights);
    return result;
}

// VaporFieldModel.StrakeRamp : nothing over a strake's front StrakeQuiet, full where the wing starts;
// eased across the root (RootInside) and started further back toward the root, along the swept strake.
#define STRAKE_QUIET 0.3
#define STRAKE_SWEEP_SHARE 0.4
#define ROOT_BLEND_M 0.6
float _StrakeApexM, _StrakeEndM;
float RootInside(float span)
{
    return 1 - smoothstep(_RootSpanM - ROOT_BLEND_M, _RootSpanM + ROOT_BLEND_M, abs(span));
}
float StrakeRamp(float x, float span)
{
    float inside = RootInside(span);
    if (_StrakeEndM <= _StrakeApexM || inside <= 0) return 1;
    float share = saturate(span / max(_RootSpanM, 1e-3));
    float ramp = smoothstep(_StrakeApexM + (STRAKE_QUIET + STRAKE_SWEEP_SHARE * share) * (_StrakeEndM - _StrakeApexM), _StrakeEndM, x);
    return lerp(1, ramp, inside);
}

// VaporFieldModel.BodyDeficit: a flat body's own cloud.
float ForeWeight(float x, float span)
{
    if (_ForeEnvelope.z <= 0 || _ForeEnvelope.y <= _ForeEnvelope.w) return 0;
    return _ForeEnvelope.z * (1 - smoothstep(_ForeEnvelope.w, _ForeEnvelope.y, x))
        * (1 - smoothstep(_RootSpanM, _RootSpanM + 0.4, abs(span)));
}

float ForeMask(float3 p)
{
    if (_ForeEnvelope.z <= 0) return 1;
    return 1 - ForeWeight(p.x, p.z) * smoothstep(_ForeEnvelope.x - 0.2, _ForeEnvelope.x, p.y);
}

#define BODY_AFT_SLOPE 3
float BodyFloorWeight(float3 p, float4 cover)
{
    if (cover.y <= COVER_EMPTY) return 0;
    float basis = max(cover.y, min(cover.w, cover.y + COVER_MAX_RISE));
    float mid = ShapeRow(abs(p.z) / _SemispanM, 0).z;
    float blend = cover.y <= mid + WRAP_NONE_M ? _BodyFloorBlendM : 0;
    float floor = basis - blend;
    if (p.y < cover.y || p.y < floor) return 0;
    return blend > 1e-5 ? smoothstep(floor, basis, p.y) : 1;
}

Part BodyPart(float3 p)
{
    Part r;
    float span = abs(p.z);
    float4 cover = CoverAt(p.x, span);
    float bodyBase = max(cover.y, min(cover.w, cover.y + COVER_MAX_RISE));
    float height = max(p.y - bodyBase, 0);
    float t = min(span / max(_BodyBox.z, 1e-3), 1);
    float dome = BODY_DOME_EDGE + (1 - BODY_DOME_EDGE) * (1 - t * t);
    float ramp = StrakeRamp(p.x, span);
    // Aft of the body the cloud's top slopes down: higher up it ends sooner.
    float mask = min(cover.z, CoverAt(p.x + BODY_AFT_SLOPE * max(height, 0), span).z);
    // as low as it is thin at the body's edge.
    r.deficit = mask > 0 ? _BodyDeficit * mask * Profile(height, _BodyReachM * dome * ramp * mask) * ramp * BodyFloorWeight(p, cover) : 0;
    r.deficit *= ForeMask(p);
    float outside = max(max(height - _BodyBoundM, span - _BodyBox.z - _BodyBox.w),
                        max(_BodyBox.x - _BodyBox.w - p.x, p.x - _BodyBox.y - _BodyBox.w)) * BOUND_SAFETY;
    r.outside = _BodyBoundM > 0 ? outside : FAR;
    r.step = clamp(_BodyReachM * 0.25, 0.04, 1);
    return r;
}

float LiftShape(float at)
{
    return lerp(sqrt((1 - at) / (at + NOSE_RADIUS)), (1 - at) * PI, _Separation);
}

float NormalCoefficient(float at, float section, float thickness)
{
    return KarmanTsien((section / -PI * LiftShape(at) - thickness * 1.3 * sin(at * PI)) / _Cos2Sweep, _WingBeta, _WingKt);
}

Part WingPart(float3 p, float side)
{
    Part r;
    float span = p.z;
    // with the cover one layer from tip to tip, inside the root on the root's station.
    float spanAt = span;
    float eta = spanAt / _SemispanM;
    float4 station = ShapeRow(eta, 0);
    float loading = ShapeRow(eta, 1).x;
    float leading = station.x;
    float chord = max(station.y, 0.05);
    float thickness = station.w;
    float xi = (p.x - leading) / chord;
    float xi0 = xi;
    float along = saturate(xi);
    float surface = station.z + thickness * chord * 2 * along * (1 - along);
    float frontRise = lerp(FRONT_RISE_M, 0.6, ForeWeight(p.x, span));
    float front = _CoverOn > 0.5 ? smoothstep(0, frontRise / chord, xi) : smoothstep(-0.04, 0, xi);
    float coverWing = 1;
    float intakeFloor = -FAR;
    float noFill = 1;
    float floorWeight = 1;
    float section = _SectionLiftM * loading * SideLoading(span * side) / chord;
    float peak = NormalCoefficient(0.1, section, thickness);
    float frontScale = _StrakeEndM <= _StrakeApexM ? 1 : 1 - 0.45 * (1 - smoothstep(_StrakeApexM, _StrakeEndM, p.x)) * RootInside(span);
    if (_CoverOn > 0.5)
        frontScale *= 1 - 0.55 * (1 - _CoverFlat) * (1 - smoothstep(_RootSpanM, _RootSpanM + 0.8, span)) * (1 - smoothstep(0.15, 0.55, along));
    float full = frontScale * _WingReach * min(chord, _ReachChordM);
    if (_CoverOn > 0.5)
    {
        float shoulderFront = (1 - _CoverFlat) * (1 - smoothstep(_RootSpanM, _RootSpanM + 0.8, span)) * (1 - smoothstep(0.3, 0.7, along));
        shoulderFront = max(shoulderFront, (1 - _CoverFlat) * (1 - smoothstep(_RootSpanM - 0.15, _RootSpanM + 0.25, span)));
        full = lerp(full, min(full, 0.9 * max(_RootSpanM - _CoverBodyRadius, 0.3)), shoulderFront);
    }
    float below = p.y - surface;
    float height = below;
    if (_CoverOn > 0.5)
    {
        float4 cover = CoverAt(p.x, span);
        float shoulder = _CoverBodyRadius > 0 ? (1 - _CoverFlat) * smoothstep(_CoverBodyRadius * 0.35, _CoverBodyRadius * 0.75, span)
            * (1 - smoothstep(_RootSpanM + 0.3, _RootSpanM + 0.8, span)) * (1 - smoothstep(0.45, 0.75, along)) : 0;
        coverWing = max(cover.x, shoulder);
        if (_CoverFlat > 0.5 && span < _RootSpanM) floorWeight = BodyFloorWeight(p, cover);
        // filled up to the base on the mantle, measured from it.
        float sheet = max(full * ProfileTop(Capped(-(peak * _WingScale), _WingCap) / max(_SaturationDeficit, 1e-9)), 1e-4);
        float rise = clamp(cover.w - surface, 0, COVER_MAX_RISE * frontScale);
        // up with the surface to what the sheet wraps, then level: no ridge at a tall part's edge.
        float basis = surface + max(min(rise, min(WRAP_FULL * sheet, WRAP_FULL_M)), rise * coverWing * _RaiseOnWing) * smoothstep(0, 0.12, along);   // hugs the leading edge
        float mantle = max(surface, min(cover.w, surface + COVER_MAX_RISE));
        basis = lerp(basis, mantle, shoulder);
        float roundRoot = (1 - _CoverFlat) * (1 - smoothstep(_RootSpanM - 0.15, _RootSpanM + 0.25, span));
        basis = lerp(basis, min(basis, max(surface, cover.y) + 0.1), roundRoot);
        if (shoulder > 0.5) intakeFloor = max(intakeFloor, basis);
        // no fill below the base where the cover's top holds nothing (a canard's thin tip).
        if (p.y < basis && cover.y <= COVER_EMPTY + 1) noFill = 0;
        height = max(p.y - basis, 0);
        // read upstream by _FlowLean times the height.
        float lean = _FlowLean + ForeWeight(p.x, span) * max(1.6 - _FlowLean, 0);
        xi = (p.x - lean * height - leading) / chord;
        along = saturate(xi);
        front = smoothstep(0, frontRise / chord, xi);
    }

    float here = NormalCoefficient(along, section, thickness);
    float ramp = StrakeRamp(p.x, span);
    if (_CoverOn > 0.5 && _CoverBodyRadius > 0)
        ramp = lerp(ramp, 1, (1 - _CoverFlat) * smoothstep(_CoverBodyRadius * 0.35, _CoverBodyRadius * 0.75, span)
            * (1 - smoothstep(_RootSpanM + 0.3, _RootSpanM + 0.8, span)) * (1 - smoothstep(0.45, 0.75, along)));

    // The shock: where the subsonic suction comes back through sonic, pushed aft as
    // Mach one nears.
    float target = _WingSonic * _WingBeta / (1 - _WingKt * _WingSonic) * _Cos2Sweep;
    float q = target * -PI / max(section, 1e-6);
    float q2 = q * q;
    float sonicAt = saturate(lerp((1 - q2 * NOSE_RADIUS) / (q2 + 1), q / -PI + 1, _Separation));
    float shock = (_WingSupersonic > 0.5 || _WingSonic >= 0) ? 1 : sonicAt + (1 - sonicAt) * _WingTravel;
    float ahead = max(1 - smoothstep(shock - SHOCK_WIDTH, shock + SHOCK_WIDTH, along),
                      exp(-max(along - shock, 0) * chord / max(_EvaporationM, 1e-3)));
    float pocket = lerp(max(here, _WingSonic), min(here, peak), ahead);
    float coefficient = peak < _WingSonic ? pocket : here;

    // The reach builds up over a chord share, but over no more than a few metres on a long root chord.
    float noseReach = min(NOSE_REACH, NOSE_BUILD_M / chord), frontGrowth = min(FRONT_GROWTH, FRONT_BUILD_M / chord);
    float reach = full * min((along + NOSE_RADIUS) / noseReach, 1)
        * (_CoverOn > 0.5 ? lerp(FRONT_SHARE, 1, smoothstep(0, frontGrowth, along)) : 1) * ramp;
    float root = _InnerSpanM / _SemispanM;
    float blur = height * ROOT_BLUR / _SemispanM;
    float fade = (_CoverOn > 0.5 ? 1 : smoothstep(root - blur, root + ROOT_FADE + blur, eta))
        * (1 - smoothstep(0.9, 1, eta))
        * front
        * (1 - smoothstep(1, 1 + (_CoverOn > 0.5 ? lerp(COVER_TRAIL_M, _BodyTrailM, 1 - smoothstep(_RootSpanM, _RootSpanM + 0.5, span)) / chord : 0.06), xi));

    float atSurface = Capped(-(coefficient * _WingScale), _WingCap) * fade;
    if (_CoverOn > 0.5)
    {
        atSurface *= 1 - (1 - coverWing) * (1 - _CoverFlat) * (1 - smoothstep(_RootSpanM - 0.3, _RootSpanM + 0.3, span));
        atSurface *= 1 - ForeWeight(p.x, span) * (1 - smoothstep(0, 0.8, coverWing));
    }
    float top = reach * ProfileTop(max(atSurface, 1e-6) / max(_SaturationDeficit, 1e-6));
    r.deficit = (below < 0 || eta >= 1 || p.y < intakeFloor) ? 0 : atSurface * Profile(max(height, 0), reach) * ramp * noFill;
    r.deficit *= ForeMask(p) * floorWeight;

    // Unleaned and over the highest base, so the bound stays a distance ('s base slopes).
    float raised = _CoverOn > 0.5 ? COVER_MAX_RISE : 0;
    float outside = max(max(max(-below, below - _WingBoundM - raised), max(-xi0 * chord - _CoverFrontM, (xi0 - 1) * chord - (_CoverOn > 0.5 ? max(COVER_TRAIL_M, _BodyTrailM) + _FlowLean * (_WingBoundM + raised) : 0.06 * chord))),
                        max((_CoverOn > 0.5 ? -span : _InnerSpanM - span), span - _SemispanM)) * BOUND_SAFETY;
    r.outside = _WingBoundM > 0 ? outside : FAR;
    r.step = clamp(max(reach * 0.25, (height - top) * (_CoverOn > 0.5 ? 0.25 : 0.5)), 0.04, 1);
    return r;
}

// VaporFieldModel.CanardDeficit.
#define CANARD_THICKNESS 0.06
Part CanardPart(float3 p, float side)
{
    Part r;
    float span = p.z;
    // inside its root, the root's section over the body.
    float t = saturate((span - _CanardA.x) / (_CanardA.y - _CanardA.x));
    float leading = _CanardA.z + (_CanardB.x - _CanardA.z) * t;
    float chord = max(_CanardA.w + (_CanardB.y - _CanardA.w) * t, 0.05);
    float xi = (p.x - leading) / chord;
    float xi0 = xi;
    float along = saturate(xi);
    float surface = _CanardB.z + (_CanardB.w - _CanardB.z) * t + 2 * CANARD_THICKNESS * chord * along * (1 - along);
    float below = p.y - surface;
    float height = below;
    float baseFade = smoothstep(0, 0.04, height);
    float section = _CanardC.x * SideLoading(span * side);
    float full = 0.7 * _WingReach * min(chord, _ReachChordM);
    if (_CoverOn > 0.5)
    {
        float sheet = max(full * ProfileTop(Capped(-(NormalCoefficient(0.1, section, CANARD_THICKNESS) * _WingScale), _WingCap) / max(_SaturationDeficit, 1e-9)), 1e-4);
        float rise = clamp(CoverAt(p.x, span).w - surface, 0, COVER_MAX_RISE);
        float raisedBase = min(rise, min(WRAP_FULL * sheet, WRAP_FULL_M));
        baseFade = smoothstep(0, max(raisedBase, 0.04), below);
        height = max(below - raisedBase, 0);
    }
    xi = (p.x - _FlowLean * max(height, 0) - leading) / chord;
    along = saturate(xi);
    float coefficient = NormalCoefficient(min(along, 0.85), section, CANARD_THICKNESS);
    float reach = full * min((along + NOSE_RADIUS) / NOSE_REACH, 1) * lerp(FRONT_SHARE, 1, smoothstep(0, FRONT_GROWTH, along));
    float gap = max(ShapeRow(span / _SemispanM, 0).x - (leading + chord), 0);
    float spread = min(1.2, 0.15 + 0.12 * max(p.x - leading - chord, 0) + 0.15 * max(height, 0));
    float rootFade = smoothstep(0, max(_CanardA.x, 0.15), span);
    float fade = rootFade * baseFade * (1 - smoothstep(_CanardA.y - 0.15, _CanardA.y + spread, span)) * smoothstep(0, 0.45 / chord, xi) * (1 - smoothstep(1 + gap / chord, 1 + (gap + COVER_TRAIL_M) / chord, xi));
    bool inside = below >= 0;
    r.deficit = inside ? Capped(-(coefficient * _WingScale), _WingCap) * Profile(height, reach) * fade : 0;
    float raised = _CoverOn > 0.5 ? COVER_MAX_RISE : 0;
    float outside = max(max(max(-below - 0.1, below - _CanardC.y - raised), max(-xi0 * chord, (xi0 - 1) * chord - gap - COVER_TRAIL_M - _FlowLean * (_CanardC.y + raised))),
                        span - _CanardA.y - 1.2) * BOUND_SAFETY;
    r.outside = _CanardC.y > 0 ? outside : FAR;
    r.step = clamp(reach * 0.25, 0.04, 1);
    return r;
}

Part ConePart(float3 p)
{
    Part r;
    float xi = (p.x + _NoseM) / _BodyLengthM;
    float along = clamp(xi, 1e-3, 1 - 1e-3);
    float radialDistance = length(float2(p.y - _BodyHeightM, p.z));
    float u = max(along * (1 - along) * 4, 1e-3);
    float radius = _BodyRadiusM * pow(u, 0.75);
    float shock = _ConeShock + radialDistance * CONE_SHOCK_LEAN / _BodyLengthM;
    float ahead = max(1 - smoothstep(shock - CONE_SHOCK_THICKNESS, shock + _ConeShockWidth, xi),
                      exp(-max(xi - shock, 0) * _BodyLengthM / max(_EvaporationM, 1e-3)));
    float pocket = smoothstep(shock - CONE_POCKET_LENGTH, shock, xi);
    float reach = _ConeReachM * (pocket * 0.8 + 0.2);
    float lateral = log(reach / max(radialDistance, radius) + 1) / log(reach / max(radius, 1e-3) + 1);
    float coefficient = KarmanTsien(-(_ConeStrength * pocket), _ConeBeta, _ConeKt);
    bool inside = xi > 0 && xi < 1;
    r.deficit = inside ? Capped(-(coefficient * _ConeScale), _ConeCap) * lateral * lateral * ahead * _ConeFade : 0;
    float shockX = (shock + _ConeShockWidth + 0.02) * _BodyLengthM - _NoseM;
    float outside = max(radialDistance - _ConeBoundM, max(-_NoseM - p.x, p.x - shockX)) * BOUND_SAFETY;
    r.outside = _ConeBoundM > 0 ? outside : FAR;
    r.step = clamp(_ConeBoundM / 14, 0.1, 1.5);
    return r;
}

// The whole field at a point; `tips` 0 leaves the tip vortices out (shadow samples).
Part FieldParts(float3 p, float tips, out float untipped)
{
    float flexSpan = max(abs(p.z) - _WingFlex.z, 0);
    p.y -= (_WingFlex.x + _WingFlex.y * flexSpan) * flexSpan;
    float3 folded = float3(p.x, p.y, abs(p.z));
    float side = p.z < 0 ? -1 : 1;
    Part total;
    total.deficit = 0;
    total.outside = FAR;
    total.step = FAR;
    untipped = FAR;
    if (tips > 0.5 && _EffectTip > 0.5)
    {
        Part t = TipVortex(p);
        total.deficit += t.deficit;
        total.outside = min(total.outside, t.outside);
        total.step = min(total.step, t.outside <= 0 ? t.step : FAR);
    }
    if (_EffectEdge > 0.5)
    {
        Part e = EdgePart(folded, side);
        total.deficit += e.deficit;
        total.outside = min(total.outside, e.outside);
        total.step = min(total.step, e.outside <= 0 ? e.step : FAR);
        untipped = min(untipped, e.outside);
    }
    if (_EffectWing > 0.5)
    {
        Part w = WingPart(folded, side);
        total.deficit += w.deficit * (1 - _DebugOff.x);
        total.outside = min(total.outside, w.outside);
        total.step = min(total.step, w.outside <= 0 ? w.step : FAR);
        untipped = min(untipped, w.outside);
    }
    if (_EffectWing > 0.5 && _CanardC.y > 0)
    {
        Part cp = CanardPart(folded, side);
        total.deficit += cp.deficit * (1 - _DebugOff.z);
        total.outside = min(total.outside, cp.outside);
        total.step = min(total.step, cp.outside <= 0 ? cp.step : FAR);
        untipped = min(untipped, cp.outside);
    }
    if (_EffectWing > 0.5 && _BodyBoundM > 0)
    {
        Part bp = BodyPart(p);
        total.deficit += bp.deficit * (1 - _DebugOff.y);
        total.outside = min(total.outside, bp.outside);
        total.step = min(total.step, bp.outside <= 0 ? bp.step : FAR);
        untipped = min(untipped, bp.outside);
    }
    if (_EffectCone > 0.5)
    {
        Part c = ConePart(p);
        total.deficit += c.deficit;
        total.outside = min(total.outside, c.outside);
        total.step = min(total.step, c.outside <= 0 ? c.step : FAR);
        untipped = min(untipped, c.outside);
    }
    return total;
}

Part Field(float3 p)
{
    float untipped;
    return FieldParts(p, 1, untipped);
}

// ---- vapor-material.ts ----
float3 WaterAt(float deficit)
{
    float ratio = 1 - clamp(deficit, 0, 1 - CONDENSATION_MIN_RATIO);
    float u = (saturate((ratio - CONDENSATION_MIN_RATIO) / (1 - CONDENSATION_MIN_RATIO)) * (CONDENSATION_TEXELS - 1) + 0.5) / CONDENSATION_TEXELS;
    return tex2Dlod(_VaporTable, float4(u, 0.5, 0, 0)).xyz;
}

float HenyeyGreenstein(float cosine, float g)
{
    float g2 = g * g;
    return (1 - g2) / pow(max(g2 + 1 - 2 * g * cosine, 1e-4), 1.5) / (4 * PI);
}

float4 frag(v2f i) : SV_Target
{
    float3 camera = mul(unity_WorldToObject, float4(_WorldSpaceCameraPos, 1)).xyz;
    float3 ray = i.local - camera;
    float3 direction = ray / length(ray);

    // The opaque scene along this ray, in metres: the frames differ by a rotation.
    float4 depthScreen = i.screen;
    if (_VaporDepthFlip > 0.5) depthScreen.y = depthScreen.w - depthScreen.y;
    float rawDepth = SAMPLE_DEPTH_TEXTURE_PROJ(_CameraDepthTexture, UNITY_PROJ_COORD(depthScreen));
    float sceneT = LinearEyeDepth(rawDepth) / max(-normalize(i.view).z, 1e-6);

    float3 safeDir = float3(abs(direction.x) < 1e-6 ? 1e-6 : direction.x,
                            abs(direction.y) < 1e-6 ? 1e-6 : direction.y,
                            abs(direction.z) < 1e-6 ? 1e-6 : direction.z);
    float3 inverse = 1 / safeDir;
    float3 tLow = (_BoxMin - camera) * inverse;
    float3 tHigh = (_BoxMax - camera) * inverse;
    float3 tNear = min(tLow, tHigh);
    float3 tFar = max(tLow, tHigh);
    float enter = max(max(max(tNear.x, tNear.y), tNear.z), 0);
    float leave = min(min(min(tFar.x, tFar.y), tFar.z), sceneT);
    if (leave <= enter) discard;

    float cosine = dot(direction, _SunDir);
    float3 sun = _SunLight * MULTIPLE_SCATTERING;
    float3 sunSingle = sun * HenyeyGreenstein(cosine, _Anisotropy) * (1 - ISOTROPIC_SHARE);
    float3 sunMultiple = sun * (ISOTROPIC_SHARE / (4 * PI));

    float3 flow = float3(_CosAlpha, _SinAlpha, _FlowZ);
    float drift = _SpeedMPerS * _VaporTime;
    float streak = _EddyM + _SpeedMPerS * _ShutterS;

    float3 light = 0;
    float fogged = 0;
    float transmittance = 1;
    float t = enter;
    int maxSteps = (int)_MaxSteps;

    [loop]
    for (int s = 0; s < maxSteps; s++)
    {
        if (t > leave) break;
        float3 p = camera + direction * t;
        float untipped;
        Part sample = FieldParts(p, 1, untipped);
        float footprint = t * _PixelStep;

        if (sample.outside > 0)
        {
            t += max(sample.outside, max(footprint, 0.05));
            continue;
        }

        float here = min(max(sample.step * _StepScale, footprint), leave - t + 1e-3);
        if (_MaxSteps >= 160) here = min(here, max(footprint, sceneT - t < 1 ? 0.02 : 0.08));

        // Midpoint integration retains the entry segment without a screen-space dither grid.
        p = camera + direction * (t + here * 0.5);
        sample = FieldParts(p, 1, untipped);

        if (sample.deficit > _SaturationDeficit)
        {
            float3 water = WaterAt(sample.deficit);
            float3 air = p - flow * drift + float3(37.1, 11.3, 23.7) * _Seed;
            float downstream = dot(air, flow);
            float3 across = air - flow * downstream;
            float patch = 0;
            if (_Detail > 0.5)
                patch = clamp(Patchiness(float3(downstream / streak, across.yz / _EddyM)), -1, 1);
            float grams = patch < 0 ? lerp(water.y, water.x, -patch) : lerp(water.y, water.z, patch);
            float extinction = max(grams, 0) * _Extinction;
            float through = exp(-extinction * here);

            float shade = 0;
            if (_EffectShadow > 0.5 && _Detail > 0.5)
            {
                // Toward the sun at 0.4, 1.4 and 4 m, standing for 0.8, 1.6 and 4 m of path.
                float3 distances = float3(0.4, 1.4, 4);
                float3 lengths = float3(0.8, 1.6, 4);
                for (int k = 0; k < 3; k++)
                {
                    // Only a sample that may land in something is looked up, and without the
                    // tips: in a trail behind the aircraft, none are.
                    if (untipped < distances[k])
                    {
                        float ignored;
                        Part toward = FieldParts(p + _SunDir * distances[k], 0, ignored);
                        if (toward.outside <= 0) shade += WaterAt(toward.deficit).y * _Extinction * lengths[k];
                    }
                }
            }

            float3 source = sunSingle * exp(-shade) + sunMultiple * exp(-shade * MULTIPLE_SHADE) + _SkyLight;
            float viewDepth = t * max(-normalize(i.view).z, 1e-6);
            float fog = 0;
            if (_VaporFog.x > 0.5 && _VaporFog.x < 1.5)
                fog = saturate((viewDepth - _VaporFog.z) / max(_VaporFog.w - _VaporFog.z, 1e-6));
            else if (_VaporFog.x > 1.5 && _VaporFog.x < 2.5)
                fog = 1 - exp(-_VaporFog.y * viewDepth);
            else if (_VaporFog.x > 2.5)
                fog = 1 - exp(-pow(_VaporFog.y * viewDepth, 2));
            float stopped = transmittance * (1 - through);
            light += source * stopped * (1 - fog);
            fogged += stopped * fog;
            transmittance *= through;
        }

        if (transmittance < OPAQUE_TRANSMITTANCE) break;
        t += here;
    }

    float coverage = 1 - transmittance;
    if (coverage < 1e-4) discard;
    // Upstream's light is linear HDR (a sun of 3), graded by AgX and sent out as sRGB; KSP's frame
    // has neither, so both here, on the unpremultiplied colour (exact where the vapor is opaque).
    float3 color = AgxToneMapping(light * _Exposure / coverage);
    // Keep unlit vapor just above black so its volume survives KSP's darkest night scenes.
    color = max(color, float3(0.0331048, 0.0331048, 0.0331048));
    if (_GammaOut > 0.5) color = LinearToSrgb(color);
    return float4(color * coverage + _VaporFogColor.rgb * fogged, coverage);
}

#endif
