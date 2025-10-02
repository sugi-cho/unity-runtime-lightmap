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
        Cull Off
        ZTest Always
        ZWrite Off
        Blend One Zero

        Pass
        {
            Name "UVBake"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fragment _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

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
                float4 positionCS  : SV_POSITION;
                float2 uv0         : TEXCOORD0;
                float3 positionWS  : TEXCOORD1;
                float3 normalWS    : TEXCOORD2;
                float4 shadowCoord : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs posInputs = GetVertexPositionInputs(input.positionOS);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS, input.tangentOS);

                float2 uv = input.uv2;
                uv.y = (_FlipY > 0.5f) ? (1.0f - uv.y) : uv.y;

                output.positionCS = float4(uv * 2.0f - 1.0f, 0.0f, 1.0f);
                output.uv0 = TRANSFORM_TEX(input.uv0, _BaseMap);
                output.positionWS = posInputs.positionWS;
                output.normalWS = normalize(normalInputs.normalWS);
                output.shadowCoord = TransformWorldToShadowCoord(output.positionWS);
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                float4 albedoSample = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv0) * _BaseColor;
                clip(albedoSample.a - _Cutoff);

                float3 normalWS = SafeNormalize(input.normalWS);

                Light mainLight = GetMainLight(input.shadowCoord);
                float3 lighting = saturate(dot(normalWS, -mainLight.direction)) * mainLight.color * mainLight.shadowAttenuation;

                uint additionalCount = GetAdditionalLightsCount();
                [loop] for (uint li = 0u; li < additionalCount; ++li)
                {
                    Light lightData = GetAdditionalLight(li, input.positionWS);
                    float ndotl = saturate(dot(normalWS, -lightData.direction));
                    lighting += ndotl * lightData.color * lightData.shadowAttenuation;
                }

                float3 baked = lighting;
                float3 outColor = lerp(baked, baked * albedoSample.rgb, saturate(_MultiplyAlbedo));
                return half4(outColor, 1.0f);
            }
            ENDHLSL
        }
    }
}
