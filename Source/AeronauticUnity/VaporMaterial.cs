// Uploads aeronautic's vapor field to the WingVapor shader.
// After aeronautic (github.com/Romainlg29/aeronautic @ ca535855f7), vapor-material.ts and wing-vapor-core.ts.
// MIT License, (c) 2026 Romain Le Gall; see THIRD-PARTY-NOTICES.md.
using UnityEngine;

namespace VortexVapor.Aeronautic
{
    public class VaporMaterial
    {
        // Past this share of the screen the march coarsens as the square root of the area, up to
        // MaxStepScale; under DetailHeight of the screen the moisture's patches are dropped.
        const float StepArea = 0.15f, DetailHeight = 0.06f;
        // How coarse the march may get when the craft fills the screen; 1 never coarsens.
        public static float StepScaleCap = 3f;

        public readonly Material Material;
        readonly Texture2D table, shape, trails;
        Texture2D cover;
        WingCover written;
        readonly Color[] tablePixels = new Color[MoistCondensation.Texels];
        readonly Color[] shapePixels = new Color[WingShape.Stations * 3];
        readonly Color[] trailPixels = new Color[TrailPaths.Points * 4];

        static readonly int TableId = Shader.PropertyToID("_VaporTable"), ShapeId = Shader.PropertyToID("_VaporShape");
        static readonly int TrailsId = Shader.PropertyToID("_VaporTrails"), TipOnId = Shader.PropertyToID("_EffectTip");
        static readonly int BoxMinId = Shader.PropertyToID("_BoxMin"), BoxMaxId = Shader.PropertyToID("_BoxMax");
        static readonly int SunDirId = Shader.PropertyToID("_SunDir"), SunLightId = Shader.PropertyToID("_SunLight"), SkyLightId = Shader.PropertyToID("_SkyLight");
        static readonly int ExposureId = Shader.PropertyToID("_Exposure"), ExtinctionId = Shader.PropertyToID("_Extinction");
        static readonly int PixelStepId = Shader.PropertyToID("_PixelStep");
        static readonly int GammaOutId = Shader.PropertyToID("_GammaOut");
        static readonly int CoverId = Shader.PropertyToID("_VaporCover"), CoverOnId = Shader.PropertyToID("_CoverOn"), CoverGridId = Shader.PropertyToID("_CoverGrid");
        static readonly int CoverFlatId = Shader.PropertyToID("_CoverFlat");
        static readonly int FrameId = Shader.PropertyToID("_FrameIndex");
        static readonly int ForeId = Shader.PropertyToID("_ForeEnvelope");
        int frame;
        static readonly int BodyDeficitId = Shader.PropertyToID("_BodyDeficit"), BodyReachMId = Shader.PropertyToID("_BodyReachM");
        static readonly int BodyBoundMId = Shader.PropertyToID("_BodyBoundM"), BodyBoxId = Shader.PropertyToID("_BodyBox");
        static readonly int FlowLeanId = Shader.PropertyToID("_FlowLean"), BodyTrailMId = Shader.PropertyToID("_BodyTrailM");
        static readonly int CoverFrontMId = Shader.PropertyToID("_CoverFrontM"), ProfilePowerId = Shader.PropertyToID("_ProfilePower"), RaiseOnWingId = Shader.PropertyToID("_RaiseOnWing");
        static readonly int CanardAId = Shader.PropertyToID("_CanardA"), CanardBId = Shader.PropertyToID("_CanardB"), CanardCId = Shader.PropertyToID("_CanardC");
        static readonly int InnerSpanMId = Shader.PropertyToID("_InnerSpanM"), BodyCoverId = Shader.PropertyToID("_BodyCover");
        static readonly int AnisotropyId = Shader.PropertyToID("_Anisotropy"), EddyId = Shader.PropertyToID("_EddyM"), ShutterId = Shader.PropertyToID("_ShutterS");
        static readonly int TimeId = Shader.PropertyToID("_VaporTime"), SeedId = Shader.PropertyToID("_Seed");
        static readonly int StepScaleId = Shader.PropertyToID("_StepScale"), DetailId = Shader.PropertyToID("_Detail"), MaxStepsId = Shader.PropertyToID("_MaxSteps");
        static readonly int WingOnId = Shader.PropertyToID("_EffectWing"), EdgeOnId = Shader.PropertyToID("_EffectEdge");
        static readonly int ConeOnId = Shader.PropertyToID("_EffectCone"), ShadowOnId = Shader.PropertyToID("_EffectShadow");

        // VaporField and VaporConstants, by the shader's names.
        static readonly int MachId = Shader.PropertyToID("_Mach");
        static readonly int SpeedMPerSId = Shader.PropertyToID("_SpeedMPerS");
        static readonly int CosAlphaId = Shader.PropertyToID("_CosAlpha");
        static readonly int SinAlphaId = Shader.PropertyToID("_SinAlpha");
        static readonly int FlowZId = Shader.PropertyToID("_FlowZ");
        static readonly int RollBiasId = Shader.PropertyToID("_RollBias");
        static readonly int SlipBiasId = Shader.PropertyToID("_SlipBias");
        static readonly int VortexKId = Shader.PropertyToID("_VortexK");
        static readonly int SaturationDeficitId = Shader.PropertyToID("_SaturationDeficit");
        static readonly int SemispanMId = Shader.PropertyToID("_SemispanM");
        static readonly int RootSpanMId = Shader.PropertyToID("_RootSpanM");
        static readonly int SectionLiftMId = Shader.PropertyToID("_SectionLiftM");
        static readonly int ReachChordMId = Shader.PropertyToID("_ReachChordM");
        static readonly int SeparationId = Shader.PropertyToID("_Separation");
        static readonly int Cos2SweepId = Shader.PropertyToID("_Cos2Sweep");
        static readonly int EdgeApexMId = Shader.PropertyToID("_EdgeApexM");
        static readonly int EdgeGradientId = Shader.PropertyToID("_EdgeGradient");
        static readonly int EdgeLengthMId = Shader.PropertyToID("_EdgeLengthM");
        static readonly int EdgeCoreId = Shader.PropertyToID("_EdgeCore");
        static readonly int EdgeRopeMId = Shader.PropertyToID("_EdgeRopeM"), EdgeRopeDeficitId = Shader.PropertyToID("_EdgeRopeDeficit"), EdgeRopeLengthMId = Shader.PropertyToID("_EdgeRopeLengthM"), EdgeRopeWalkMId = Shader.PropertyToID("_EdgeRopeWalkM"), EdgeRopeFlareId = Shader.PropertyToID("_EdgeRopeFlare"), EdgeTrailMId = Shader.PropertyToID("_EdgeTrailM"),
            StrakeApexMId = Shader.PropertyToID("_StrakeApexM"), StrakeEndMId = Shader.PropertyToID("_StrakeEndM");
        static readonly int EdgeHeightId = Shader.PropertyToID("_EdgeHeight");
        static readonly int EdgeBurstMId = Shader.PropertyToID("_EdgeBurstM");
        static readonly int EdgeBoundMId = Shader.PropertyToID("_EdgeBoundM");
        static readonly int WingBoundMId = Shader.PropertyToID("_WingBoundM");
        static readonly int EvaporationMId = Shader.PropertyToID("_EvaporationM");
        static readonly int NoseMId = Shader.PropertyToID("_NoseM");
        static readonly int BodyLengthMId = Shader.PropertyToID("_BodyLengthM");
        static readonly int BodyRadiusMId = Shader.PropertyToID("_BodyRadiusM");
        static readonly int BodyHeightMId = Shader.PropertyToID("_BodyHeightM");
        static readonly int ConeStrengthId = Shader.PropertyToID("_ConeStrength");
        static readonly int ConeShockId = Shader.PropertyToID("_ConeShock");
        static readonly int ConeShockWidthId = Shader.PropertyToID("_ConeShockWidth");
        static readonly int ConeFadeId = Shader.PropertyToID("_ConeFade");
        static readonly int ConeBoundMId = Shader.PropertyToID("_ConeBoundM");
        static readonly int WingScaleId = Shader.PropertyToID("_WingScale");
        static readonly int WingSonicId = Shader.PropertyToID("_WingSonic");
        static readonly int WingBetaId = Shader.PropertyToID("_WingBeta");
        static readonly int WingKtId = Shader.PropertyToID("_WingKt");
        static readonly int WingCapId = Shader.PropertyToID("_WingCap");
        static readonly int WingReachId = Shader.PropertyToID("_WingReach");
        static readonly int WingTravelId = Shader.PropertyToID("_WingTravel");
        static readonly int WingSupersonicId = Shader.PropertyToID("_WingSupersonic");
        static readonly int ConeScaleId = Shader.PropertyToID("_ConeScale");
        static readonly int ConeBetaId = Shader.PropertyToID("_ConeBeta");
        static readonly int ConeKtId = Shader.PropertyToID("_ConeKt");
        static readonly int ConeCapId = Shader.PropertyToID("_ConeCap");
        static readonly int ConeReachMId = Shader.PropertyToID("_ConeReachM");
        static readonly int TipLeadingMId = Shader.PropertyToID("_TipLeadingM");
        static readonly int TipChordMId = Shader.PropertyToID("_TipChordM");
        static readonly int TipHeightMId = Shader.PropertyToID("_TipHeightM");
        static readonly int TipCirculationId = Shader.PropertyToID("_TipCirculation");
        static readonly int TipCore2M2Id = Shader.PropertyToID("_TipCore2M2");
        static readonly int TipStart2M2Id = Shader.PropertyToID("_TipStart2M2");
        static readonly int TipRollupMId = Shader.PropertyToID("_TipRollupM");
        static readonly int TipGrowthMId = Shader.PropertyToID("_TipGrowthM");
        static readonly int TipReachMId = Shader.PropertyToID("_TipReachM");
        static readonly int TipBoundMId = Shader.PropertyToID("_TipBoundM");
        static readonly int TrailSpacingMId = Shader.PropertyToID("_TrailSpacingM");
        static readonly int SecondSemispanMId = Shader.PropertyToID("_SecondSemispanM");
        static readonly int SecondTipLeadingMId = Shader.PropertyToID("_SecondTipLeadingM");
        static readonly int SecondTipChordMId = Shader.PropertyToID("_SecondTipChordM");
        static readonly int SecondTipHeightMId = Shader.PropertyToID("_SecondTipHeightM");
        static readonly int SecondCirculationId = Shader.PropertyToID("_SecondCirculation");
        static readonly int SecondCore2M2Id = Shader.PropertyToID("_SecondCore2M2");
        static readonly int SecondStart2M2Id = Shader.PropertyToID("_SecondStart2M2");
        static readonly int SecondRollupMId = Shader.PropertyToID("_SecondRollupM");
        static readonly int SecondGrowthMId = Shader.PropertyToID("_SecondGrowthM");
        static readonly int SecondReachMId = Shader.PropertyToID("_SecondReachM");
        static readonly int SecondBoundMId = Shader.PropertyToID("_SecondBoundM");
        static readonly int SecondSpacingMId = Shader.PropertyToID("_SecondSpacingM");

        public VaporMaterial(Shader shader)
        {
            Material = new Material(shader) { name = "WingVapor" };
            table = NewTexture(MoistCondensation.Texels, 1, "WingVaporCondensation");
            shape = NewTexture(WingShape.Stations, 3, "WingVaporShape");
            // Read texel by texel: a trail runs hundreds of metres, so full floats and no filtering.
            trails = NewTexture(TrailPaths.Points, 4, "WingVaporTrails");
            trails.filterMode = FilterMode.Point;
            Material.SetTexture(TableId, table);
            Material.SetTexture(ShapeId, shape);
            Material.SetTexture(TrailsId, trails);
        }

        static Texture2D NewTexture(int width, int height, string name)
        {
            // Full floats: the lookups are filtered, and half floats would put a 10 m chord's
            // stations 8 mm out.
            return new Texture2D(width, height, TextureFormat.RGBAFloat, false, true)
            {
                name = name, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, anisoLevel = 0,
            };
        }

        // MoistCondensation.Table's Texels x 4 floats.
        public void WriteTable(float[] values)
        {
            for (int i = 0; i < tablePixels.Length; i++)
                tablePixels[i] = new Color(values[i * 4], values[i * 4 + 1], values[i * 4 + 2], values[i * 4 + 3]);
            table.SetPixels(tablePixels);
            table.Apply(false, false);
        }

        // Row 0 the stations (leading edge, chord, mid-plane, thickness), row 1 the loading, row 2
        // the leading-edge vortex's path (span, surface under it, local semispan).
        public void WriteShape(WingShape wing, EdgePath path)
        {
            int n = WingShape.Stations;
            for (int k = 0; k < n; k++)
            {
                shapePixels[k] = new Color(wing.LeadingM[k], wing.ChordM[k], wing.MidM[k], wing.Thickness[k]);
                shapePixels[n + k] = new Color(wing.Loading[k], 0, 0, 0);
                shapePixels[2 * n + k] = new Color(path.SpanM[k], path.SurfaceM[k], path.ReachM[k], 0);
            }
            shape.SetPixels(shapePixels);
            shape.Apply(false, false);
            WriteCover(wing.Cover);
        }

        // WingCover's cells, read texel by texel (the shader interpolates as WingCover.Sample does).
        void WriteCover(WingCover c)
        {
            if (c == written && (c == null || cover != null)) return;
            written = c;
            if (c == null)
            {
                Material.SetFloat(CoverOnId, 0);
                return;
            }
            if (cover == null || cover.width != c.Width || cover.height != c.Height)
            {
                if (cover != null)
                {
                    if (Application.isPlaying) Object.Destroy(cover);
                    else Object.DestroyImmediate(cover);
                }
                cover = new Texture2D(c.Width, c.Height, TextureFormat.RGBAFloat, false, true)
                {
                    name = "WingVaporCover", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, anisoLevel = 0,
                };
            }
            var pixels = new Color[c.Wing.Length];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color(c.Wing[i], c.Top[i], c.Body != null ? c.Body[i] : 0, c.Mantle[i]);
            cover.SetPixels(pixels);
            cover.Apply(false, false);
            Material.SetTexture(CoverId, cover);
            Material.SetVector(CoverGridId, new Vector4((float)c.OriginX, (float)c.CellM, c.Width, c.Height));
            Material.SetFloat(CoverFlatId, (float)c.CraftFlat);
            Material.SetFloat("_CoverBodyRadius", (float)c.BodyRadiusM);
            Material.SetFloat(CoverOnId, 1);
        }

        // The tip vortices' trails, a row each: the wing's +z and -z tips, then a canard's or a
        // tailplane's.
        public void WriteTrails(TrailPaths wing, TrailPaths second)
        {
            int n = TrailPaths.Points;
            for (int row = 0; row < 4; row++)
            {
                float[] points = row == 0 ? wing.Positive : row == 1 ? wing.Negative : row == 2 ? second.Positive : second.Negative;
                for (int k = 0; k < n; k++) trailPixels[row * n + k] = new Color(points[k * 3], points[k * 3 + 1], points[k * 3 + 2], 0);
            }
            trails.SetPixels(trailPixels);
            trails.Apply(false, false);
        }

        public void WriteField(VaporField f, VaporConstants c)
        {
            Material.SetFloat(TipLeadingMId, (float)f.TipLeadingM);
            Material.SetFloat(TipChordMId, (float)f.TipChordM);
            Material.SetFloat(TipHeightMId, (float)f.TipHeightM);
            Material.SetFloat(TipCirculationId, (float)f.TipCirculation);
            Material.SetFloat(TipCore2M2Id, (float)f.TipCore2M2);
            Material.SetFloat(TipStart2M2Id, (float)f.TipStart2M2);
            Material.SetFloat(TipRollupMId, (float)f.TipRollupM);
            Material.SetFloat(TipGrowthMId, (float)f.TipGrowthM);
            Material.SetFloat(TipReachMId, (float)f.TipReachM);
            Material.SetFloat(TipBoundMId, (float)f.TipBoundM);
            Material.SetFloat(TrailSpacingMId, (float)f.TrailSpacingM);
            Material.SetFloat(SecondSemispanMId, (float)f.SecondSemispanM);
            Material.SetFloat(SecondTipLeadingMId, (float)f.SecondTipLeadingM);
            Material.SetFloat(SecondTipChordMId, (float)f.SecondTipChordM);
            Material.SetFloat(SecondTipHeightMId, (float)f.SecondTipHeightM);
            Material.SetFloat(SecondCirculationId, (float)f.SecondCirculation);
            Material.SetFloat(SecondCore2M2Id, (float)f.SecondCore2M2);
            Material.SetFloat(SecondStart2M2Id, (float)f.SecondStart2M2);
            Material.SetFloat(SecondRollupMId, (float)f.SecondRollupM);
            Material.SetFloat(SecondGrowthMId, (float)f.SecondGrowthM);
            Material.SetFloat(SecondReachMId, (float)f.SecondReachM);
            Material.SetFloat(SecondBoundMId, (float)f.SecondBoundM);
            Material.SetFloat(SecondSpacingMId, (float)f.SecondSpacingM);
            Material.SetFloat(MachId, (float)f.Mach);
            Material.SetFloat(SpeedMPerSId, (float)f.SpeedMPerS);
            Material.SetFloat(CosAlphaId, (float)f.CosAlpha);
            Material.SetFloat(SinAlphaId, (float)f.SinAlpha);
            Material.SetFloat(FlowZId, (float)f.FlowZ);
            Material.SetFloat(RollBiasId, (float)f.RollBias);
            Material.SetFloat(SlipBiasId, (float)f.SlipBias);
            Material.SetFloat(VortexKId, (float)f.VortexK);
            Material.SetFloat(SaturationDeficitId, (float)f.SaturationDeficit);
            Material.SetFloat(SemispanMId, (float)f.SemispanM);
            Material.SetFloat(RootSpanMId, (float)f.RootSpanM);
            Material.SetFloat(InnerSpanMId, (float)f.InnerSpanM);
            Material.SetFloat(BodyCoverId, (float)f.BodyCover);
            Material.SetFloat(BodyDeficitId, (float)f.BodyDeficitAtSurface);
            Material.SetFloat(BodyReachMId, Mathf.Max((float)f.BodyReachM, 1e-3f));
            Material.SetFloat(BodyBoundMId, (float)f.BodyBoundM);
            Material.SetFloat(CoverFrontMId, written != null ? (float)written.FrontM : 0f);
            Material.SetFloat(ProfilePowerId, (float)WingCover.ProfilePower);
            Material.SetFloat(RaiseOnWingId, (float)WingCover.RaiseOnWing);
            Material.SetFloat(FlowLeanId, (float)WingCover.FlowLean);
            Material.SetFloat(BodyTrailMId, (float)f.BodyTrailM);
            Material.SetVector(CanardAId, new Vector4((float)f.CanardRootSpanM, (float)f.CanardSemispanM, (float)f.CanardRootLeadingM, (float)f.CanardRootChordM));
            Material.SetVector(CanardBId, new Vector4((float)f.CanardTipLeadingM, (float)f.CanardTipChordM, (float)f.CanardRootHeightM, (float)f.CanardTipHeightM));
            Material.SetVector(CanardCId, new Vector4((float)f.CanardSection, (float)f.CanardBoundM, 0, 0));
            WingCover bc = written;
            Material.SetVector(BodyBoxId, bc != null && bc.Body != null
                ? new Vector4((float)bc.BodyMinX, (float)bc.BodyMaxX, (float)bc.BodySpanM, (float)bc.CellM * 3)
                : Vector4.zero);
            Material.SetFloat(SectionLiftMId, (float)f.SectionLiftM);
            Material.SetFloat(ReachChordMId, (float)f.ReachChordM);
            Material.SetFloat(SeparationId, (float)f.Separation);
            Material.SetFloat(Cos2SweepId, (float)f.Cos2Sweep);
            Material.SetFloat(EdgeApexMId, (float)f.EdgeApexM);
            Material.SetVector("_RopeAxisA", new Vector4((float)f.RopeStartM, (float)f.RopeSpanM, (float)f.RopeHeightM, 0));
            Material.SetVector("_RopeAxisB", new Vector4((float)f.RopeSlopeY, (float)f.RopeSlopeZ, 0, 0));
            Material.SetFloat(EdgeGradientId, (float)f.EdgeGradient);
            Material.SetFloat(EdgeLengthMId, (float)f.EdgeLengthM);
            Material.SetFloat(EdgeCoreId, (float)f.EdgeCore);
            Material.SetFloat(EdgeRopeMId, (float)f.EdgeRopeM);
            Material.SetFloat(EdgeRopeDeficitId, (float)f.EdgeRopeDeficit);
            Material.SetFloat(EdgeRopeLengthMId, (float)f.EdgeRopeLengthM);
            Material.SetFloat(EdgeRopeWalkMId, (float)f.EdgeRopeWalkM);
            Material.SetFloat(EdgeRopeFlareId, (float)f.EdgeRopeFlare);
            Material.SetFloat(EdgeTrailMId, (float)f.EdgeTrailM);
            Material.SetFloat(StrakeApexMId, (float)f.StrakeApexM);
            Material.SetFloat(StrakeEndMId, (float)f.StrakeEndM);
            Material.SetFloat("_BodyFloorBlendM", (float)f.BodyFloorBlendM);
            Material.SetVector(ForeId, new Vector4((float)f.ForeTopM, (float)f.ForeEndM, (float)f.ForeEnabled, (float)f.ForeStartM));
            Material.SetFloat(EdgeHeightId, (float)f.EdgeHeight);
            Material.SetFloat(EdgeBurstMId, (float)f.EdgeBurstM);
            Material.SetFloat(EdgeBoundMId, (float)f.EdgeBoundM);
            Material.SetFloat(WingBoundMId, (float)f.WingBoundM);
            Material.SetFloat(EvaporationMId, (float)f.EvaporationM);
            Material.SetFloat(NoseMId, (float)f.NoseM);
            Material.SetFloat(BodyLengthMId, (float)f.BodyLengthM);
            Material.SetFloat(BodyRadiusMId, (float)f.BodyRadiusM);
            Material.SetFloat(BodyHeightMId, (float)f.BodyHeightM);
            Material.SetFloat(ConeStrengthId, (float)f.ConeStrength);
            Material.SetFloat(ConeShockId, (float)f.ConeShock);
            Material.SetFloat(ConeShockWidthId, (float)f.ConeShockWidth);
            Material.SetFloat(ConeFadeId, (float)f.ConeFade);
            Material.SetFloat(ConeBoundMId, (float)f.ConeBoundM);
            Material.SetFloat(WingScaleId, (float)c.WingScale);
            Material.SetFloat(WingSonicId, (float)c.WingSonic);
            Material.SetFloat(WingBetaId, (float)c.WingBeta);
            Material.SetFloat(WingKtId, (float)c.WingKt);
            Material.SetFloat(WingCapId, (float)c.WingCap);
            Material.SetFloat(WingReachId, (float)c.WingReach);
            Material.SetFloat(WingTravelId, (float)c.WingTravel);
            Material.SetFloat(WingSupersonicId, (float)c.WingSupersonic);
            Material.SetFloat(ConeScaleId, (float)c.ConeScale);
            Material.SetFloat(ConeBetaId, (float)c.ConeBeta);
            Material.SetFloat(ConeKtId, (float)c.ConeKt);
            Material.SetFloat(ConeCapId, (float)c.ConeCap);
            Material.SetFloat(ConeReachMId, (float)c.ConeReachM);
        }

        static readonly int WingFlexId = Shader.PropertyToID("_WingFlex");

        public void WriteFlex(Vector2 flex, float rootSpanM)
        {
            Material.SetVector(WingFlexId, new Vector4(flex.x, flex.y, rootSpanM, 0));
        }

        public void WriteBox(Vector3 min, Vector3 max)
        {
            Material.SetVector(BoxMinId, min);
            Material.SetVector(BoxMaxId, max);
        }

        // Sun direction in the canonical frame (unit, towards the sun); light as colour times intensity.
        public void WriteLight(Vector3 sunDirection, Vector3 sunLight, Vector3 skyLight, float exposure)
        {
            Material.SetVector(SunDirId, sunDirection);
            Material.SetVector(SunLightId, sunLight);
            Material.SetVector(SkyLightId, skyLight);
            Material.SetFloat(ExposureId, exposure);
            // The tone-mapped colour is encoded for a gamma-space project.
            Material.SetFloat(GammaOutId, QualitySettings.activeColorSpace == ColorSpace.Gamma ? 1 : 0);
        }

        public void WriteLook(VaporLook look, float opacityScale)
        {
            // A gram per cubic metre of droplets this size: 3 w / 2 rho r.
            Material.SetFloat(ExtinctionId, (float)(3e-3 / (2 * 1000 * System.Math.Max(look.DropletRadiusM, 1e-8))) * opacityScale);
            Material.SetFloat(AnisotropyId, Mathf.Clamp((float)look.Anisotropy, -0.95f, 0.95f));
            Material.SetFloat(EddyId, Mathf.Max((float)look.EddyM, 1e-3f));
            Material.SetFloat(ShutterId, Mathf.Max((float)look.ShutterS, 0f));
        }

        // the least step as a share of distance, narrowed with the camera's field of view.
        public void WritePixelStep(Camera camera)
        {
            float fov = camera != null && !camera.orthographic ? camera.fieldOfView : 60f;
            float narrow = Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) / Mathf.Tan(30f * Mathf.Deg2Rad);
            Material.SetFloat(PixelStepId, 0.0015f * Mathf.Clamp(narrow, 0.03f, 1.5f));
        }

        public void WriteFrame(float time, float seed, float stepScale, float detail, int maxSteps, VaporEffects effects)
        {
            Material.SetFloat(TimeId, time);
            Material.SetFloat(SeedId, seed);
            Material.SetFloat(FrameId, frame++ % 64);
            Material.SetFloat(StepScaleId, stepScale);
            Material.SetFloat(DetailId, detail);
            Material.SetFloat(MaxStepsId, maxSteps);
            Material.SetFloat(TipOnId, effects.TipVortices ? 1 : 0);
            Material.SetFloat(WingOnId, effects.Wing ? 1 : 0);
            Material.SetFloat(EdgeOnId, effects.LeadingEdgeVortices ? 1 : 0);
            Material.SetFloat(ConeOnId, effects.Cone ? 1 : 0);
            Material.SetFloat(ShadowOnId, effects.SelfShadow ? 1 : 0);
        }

        // How much coarser the march may step, and whether the patches are worth drawing, from how
        // much of the screen the box covers.
        public static void Budget(Camera camera, Matrix4x4 objectToWorld, Vector3 min, Vector3 max, out float stepScale, out float detail)
        {
            Matrix4x4 project = camera.projectionMatrix * camera.worldToCameraMatrix * objectToWorld;
            float left = float.PositiveInfinity, right = float.NegativeInfinity, bottom = float.PositiveInfinity, top = float.NegativeInfinity;
            bool behind = false;
            for (int corner = 0; corner < 8; corner++)
            {
                var p = new Vector4((corner & 1) != 0 ? max.x : min.x, (corner & 2) != 0 ? max.y : min.y, (corner & 4) != 0 ? max.z : min.z, 1);
                Vector4 clip = project * p;
                if (clip.w <= 0) { behind = true; break; }
                float x = clip.x / clip.w, y = clip.y / clip.w;
                left = Mathf.Min(left, x); right = Mathf.Max(right, x);
                bottom = Mathf.Min(bottom, y); top = Mathf.Max(top, y);
            }
            float width = behind ? 2 : Mathf.Max(Mathf.Min(right, 1) - Mathf.Max(left, -1), 0);
            float height = behind ? 2 : Mathf.Max(Mathf.Min(top, 1) - Mathf.Max(bottom, -1), 0);
            float covered = width * height / 4;
            stepScale = Mathf.Min(Mathf.Max(Mathf.Sqrt(covered / StepArea), 1), Mathf.Max(StepScaleCap, 1));
            detail = height / 2 > DetailHeight ? 1 : 0;
        }

        // A cube from -0.5 to 0.5; the shader opens it to the box. Inward-facing faces are drawn
        // (Cull Front), so it works with the camera inside.
        public static Mesh UnitCube()
        {
            var mesh = new Mesh { name = "WingVaporBox" };
            var v = new Vector3[8];
            for (int i = 0; i < 8; i++) v[i] = new Vector3((i & 1) != 0 ? 0.5f : -0.5f, (i & 2) != 0 ? 0.5f : -0.5f, (i & 4) != 0 ? 0.5f : -0.5f);
            mesh.vertices = v;
            mesh.triangles = new[]
            {
                0, 2, 1, 1, 2, 3,   4, 5, 6, 5, 7, 6,   0, 1, 4, 1, 5, 4,
                2, 6, 3, 3, 6, 7,   0, 4, 2, 2, 4, 6,   1, 3, 5, 3, 7, 5,
            };
            mesh.RecalculateBounds();
            return mesh;
        }

        // `immediate` outside play mode (the editor's batch tools).
        public void Destroy(bool immediate = false)
        {
            foreach (Object o in new Object[] { Material, table, shape, trails, cover })
            {
                if (o == null) continue;
                if (immediate) Object.DestroyImmediate(o);
                else Object.Destroy(o);
            }
        }
    }
}

namespace VortexVapor.Aeronautic
{
    public static class CanonicalFrame
    {
        // The rotation (in the frame the axes are given in) whose x is aft, y up and z along a
        // wing, from the nose and up directions. Proper (no mirror), so it can be a transform.
        public static Quaternion FromAxes(Vector3 nose, Vector3 up)
        {
            Vector3 aft = -nose.normalized;
            Vector3 vertical = (up - Vector3.Dot(up, aft) * aft).normalized;
            Vector3 span = Vector3.Cross(aft, vertical);
            return Quaternion.LookRotation(span, vertical);
        }
    }
}
