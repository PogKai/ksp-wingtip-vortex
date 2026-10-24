// AgX and sRGB out, shared by the vapor and the afterburner.
#ifndef AGX_TONEMAP_INCLUDED
#define AGX_TONEMAP_INCLUDED

// three r186 ToneMappingFunctions.js, ColorSpaceFunctions.js (AgX).
// TSL mat3(a, b, c) takes columns, HLSL float3x3 rows: mul(v, float3x3(a, b, c)).
float3 AgxContrast(float3 x)
{
    float3 x2 = x * x, x4 = x2 * x2;
    return 15.5 * x4 * x2 - 40.14 * x4 * x + 31.96 * x4 - 6.868 * x2 * x + 0.4298 * x2 + 0.1191 * x - 0.00232;
}

float3 AgxToneMapping(float3 color)
{
    color = mul(color, float3x3(0.6274, 0.0691, 0.0164, 0.3293, 0.9195, 0.0880, 0.0433, 0.0113, 0.8956));
    color = mul(color, float3x3(0.856627153315983, 0.137318972929847, 0.11189821299995,
                                0.0951212405381588, 0.761241990602591, 0.0767994186031903,
                                0.0482516061458583, 0.101439036467562, 0.811302368396859));
    color = log2(max(color, 1e-10));
    color = saturate((color + 12.47393) / (4.026069 + 12.47393));
    color = AgxContrast(color);
    color = mul(color, float3x3(1.1271005818144368, -0.1413297634984383, -0.14132976349843826,
                                -0.11060664309660323, 1.157823702216272, -0.11060664309660294,
                                -0.016493938717834573, -0.016493938717834257, 1.2519364065950405));
    color = pow(max(color, 0), 2.2);
    color = mul(color, float3x3(1.6605, -0.1246, -0.0182, -0.5876, 1.1329, -0.1006, -0.0728, -0.0083, 1.1187));
    return saturate(color);
}

float3 LinearToSrgb(float3 c)
{
    return lerp(1.055 * pow(c, 0.41666) - 0.055, c * 12.92, step(c, 0.0031308));
}

#endif
