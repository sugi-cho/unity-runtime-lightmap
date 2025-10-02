Shader "Hidden/RealTimeLightBaker/UVRuntimeBakerURP"
{
    Properties
    {
        _BaseMap ("Base Map (albedo)", 2D) = "white" {}
        _BaseColor ("Base Color", Color) = (1,1,1,1)
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0
        _MultiplyAlbedo ("Multiply Albedo", Range(0,1)) = 1
        _FlipY ("Flip UV.y for RT", Float) = 1
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" "RenderType" = "Opaque" }
        ZWrite Off
        ZTest Always
        Cull Off
        Blend One Zero

        Pass
        {
            Name "UVBake"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fragment _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float _Cutoff;
                float _MultiplyAlbedo;
                float _FlipY;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv0        : TEXCOORD0;
                float2 uv2        : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS  : SV_Position;
                float2 uv0         : TEXCOORD0;
                float3 positionWS  : TEXCOORD1;
                float3 normalWS    : TEXCOORD2;
                float4 shadowCoord : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes v)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                VertexPositionInputs posInputs = GetVertexPositionInputs(v.positionOS);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(v.normalOS, v.tangentOS);

                float2 uv = v.uv2;
                uv.y = (_FlipY > 0.5) ? (1.0 - uv.y) : uv.y;

                o.positionCS = float4(uv * 2.0 - 1.0, 0.0, 1.0);
                o.uv0 = TRANSFORM_TEX(v.uv0, _BaseMap);
                o.positionWS = posInputs.positionWS;
                o.normalWS = normalize(normalInputs.normalWS);
                o.shadowCoord = TransformWorldToShadowCoord(posInputs.positionWS);
                return o;
            }

                                                                                    float4 frag (Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                float4 baseSample = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv0) * _BaseColor;
                clip(baseSample.a - _Cutoff);

                float3 normalWS = SafeNormalize(i.normalWS);

                Light mainLight = GetMainLight(i.shadowCoord);
                float3 lighting = saturate(dot(normalWS, mainLight.direction)) * mainLight.color * mainLight.distanceAttenuation * mainLight.shadowAttenuation;

                uint additionalCount = GetAdditionalLightsCount();
                [loop] for (uint li = 0u; li < additionalCount; ++li)
                {
                    Light lightData = GetAdditionalLight(li, i.positionWS);
                    float ndotl = saturate(dot(normalWS, lightData.direction));
                    lighting += ndotl * lightData.color * lightData.distanceAttenuation * lightData.shadowAttenuation;
                }

                lighting += SampleSH(normalWS);

                float3 outColor = lerp(lighting, lighting * baseSample.rgb, saturate(_MultiplyAlbedo));
                return float4(outColor, 1.0);
            }
            ENDHLSL
        }
    }
}















