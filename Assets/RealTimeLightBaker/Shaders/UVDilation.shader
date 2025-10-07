Shader "Hidden/RealTimeLightBaker/UVDilation"
{
    Properties
    {
        _Radius ("Dilation Radius (texels)", Range(0,8)) = 1
        _AlphaThreshold ("Empty Pixel Alpha Threshold", Range(0,1)) = 0.001
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Overlay" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend One Zero

        Pass
        {
            Name "UVDilation"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_BlitTexture);

            CBUFFER_START(UnityPerMaterial)
                float4 _BlitTexture_TexelSize;
                float _Radius;
                float _AlphaThreshold;
            CBUFFER_END
            
            struct Attributes { uint vertexID : SV_VertexID; };

            struct Varyings
            {
                float4 positionCS : SV_Position;
                float2 uv         : TEXCOORD0;
            };
            
            Varyings vert(Attributes v)
            {
                Varyings o;
                // フルスクリーントライアングルの頂点IDからUVを取得する
                o.positionCS = GetFullScreenTriangleVertexPosition(v.vertexID);
                o.uv   = GetFullScreenTriangleTexCoord(v.vertexID);
                return o;
            }

            float4 frag (Varyings i) : SV_Target
            {
                float4 center = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, i.uv);
                const int maxRadius = 8;
                int radius = clamp((int)round(_Radius), 0, maxRadius);
                float threshold = saturate(_AlphaThreshold);

                if (radius == 0 || center.a > threshold)
                {
                    return center;
                }

                float2 texel = _BlitTexture_TexelSize.xy;

                float3 accum = 0;
                float weight = 0;

                [loop] for (int y = -maxRadius; y <= maxRadius; ++y)
                {
                    if (abs(y) > radius) continue;

                    [loop] for (int x = -maxRadius; x <= maxRadius; ++x)
                    {
                        if (abs(x) > radius || (x == 0 && y == 0)) continue;

                        float2 offset = float2(x, y) * texel;
                        float2 sampleUV = i.uv + offset;
                        float4 sampleCol = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, sampleUV);
                        if (sampleCol.a > threshold)
                        {
                            accum += sampleCol.rgb;
                            weight += 1.0;
                        }
                    }
                }

                if (weight > 0.0)
                {
                    float3 color = accum / weight;
                    return float4(color, 1.0);
                }

                return center;
            }
            ENDHLSL
        }
    }
}
