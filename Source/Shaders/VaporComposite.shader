// After aeronautic db04669b volume-pass.ts; MIT, Romain Le Gall, 2026.
Shader "WingtipVortex/VaporComposite"
{
    SubShader
    {
        Pass
        {
            Cull Off ZWrite Off ZTest Always
            Blend One OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            sampler2D _WVVolumeTexture;
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
            float4 _WVVolumeSize;
            v2f_img vert(appdata_img v)
            {
                v2f_img o;
                o.pos = float4(v.vertex.xy, 0, 1);
                o.uv = v.texcoord;
                return o;
            }
            float4 frag(v2f_img i) : SV_Target
            {
                float2 uv = i.uv;
                float2 depthUV = uv;
                float own = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, depthUV));
                float2 pixel = uv * _WVVolumeSize.xy - 0.5;
                float2 baseUV = (floor(pixel) + 0.5) * _WVVolumeSize.zw;
                float2 f = frac(pixel);
                float4 sum = 0;
                float total = 0;
                [unroll] for (int y = 0; y < 2; y++)
                [unroll] for (int x = 0; x < 2; x++)
                {
                    float2 sampleUV = baseUV + float2(x,y) * _WVVolumeSize.zw;
                    float2 sampleDepthUV = sampleUV;
                    float neighbor = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, sampleDepthUV));
                    float apart = abs(neighbor - own) / (max(abs(own), 1e-3) * 0.03);
                    float weight = (x == 0 ? 1-f.x : f.x) * (y == 0 ? 1-f.y : f.y) * (exp2(-apart*apart) + 1e-4);
                    sum += tex2D(_WVVolumeTexture, sampleUV) * weight;
                    total += weight;
                }
                return sum / max(total, 1e-8);
            }
            ENDCG
        }
    }
}
