// The wing vapor, raymarched through one box. Object frame: x aft, y up, z out, metres.
// Ported from aeronautic (github.com/Romainlg29/aeronautic @ ca535855f7),
// vapor-material.ts, tsl/field.ts and tsl/noise.ts.
// MIT License, (c) 2026 Romain Le Gall; see THIRD-PARTY-NOTICES.md.
Shader "WingtipVortex/WingVapor"
{
    Properties
    {
        _VaporTable ("Condensation table", 2D) = "black" {}
        _VaporShape ("Wing tables", 2D) = "black" {}
    }
    SubShader
    {
        Tags { "Queue" = "Transparent+50" "RenderType" = "Transparent" "IgnoreProjector" = "True" }
        Pass
        {
            Cull Front
            ZWrite Off
            ZTest Always
            Blend One OneMinusSrcAlpha

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5
            #include "WingVaporField.cginc"
            ENDCG
        }
    }
}
