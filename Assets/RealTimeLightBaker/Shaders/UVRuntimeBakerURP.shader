Shader "Hidden/RealTimeLightBaker/UVRuntimeBakerURP"
{
    Properties
    {
        _BaseMap ("Base Map (albedo)", 2D) = "white" {}
        _BumpMap ("Normal Map", 2D) = "bump" {}
        _SpecGlossMap ("Specular", 2D) = "white" {}
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
            #pragma multi_compile_fragment _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _LIGHT_LAYERS
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);        SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap);        SAMPLER(sampler_BumpMap);
            TEXTURE2D(_SpecGlossMap);   SAMPLER(sampler_SpecGlossMap);
            TEXTURE2D(_RTLB_BaseMap);   SAMPLER(sampler_RTLB_BaseMap);
            TEXTURE2D(_RTLB_BumpMap);   SAMPLER(sampler_RTLB_BumpMap);
            TEXTURE2D(_RTLB_SpecGlossMap); SAMPLER(sampler_RTLB_SpecGlossMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BumpMap_ST;
                float4 _SpecGlossMap_ST;
                float4 _BaseColor;
                float _Cutoff;
                float _MultiplyAlbedo;
                float _FlipY;
            CBUFFER_END

            float4 _RTLB_BaseMap_ST;
            float4 _RTLB_BumpMap_ST;
            float4 _RTLB_SpecGlossMap_ST;

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
                float4 positionCS   : SV_POSITION;
                float2 uv0          : TEXCOORD0;
                float3 positionWS   : TEXCOORD1;
                float3 normalWS     : TEXCOORD2;
                float4 shadowCoord  : TEXCOORD3;
                float3 tangentWS    : TEXCOORD4;
                float3 bitangentWS  : TEXCOORD5;
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
                output.uv0 = input.uv0;
                output.positionWS = posInputs.positionWS;
                output.normalWS = normalize(normalInputs.normalWS);
                output.shadowCoord = TransformWorldToShadowCoord(output.positionWS);
                output.tangentWS = normalInputs.tangentWS;
                output.bitangentWS = normalInputs.bitangentWS;
                return output;
            }

            half4 frag(Varyings input, bool isFrontFace : SV_IsFrontFace) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                float2 uvBase = input.uv0 * _RTLB_BaseMap_ST.xy + _RTLB_BaseMap_ST.zw;
                float4 albedoSample = SAMPLE_TEXTURE2D(_RTLB_BaseMap, sampler_RTLB_BaseMap, uvBase) * _BaseColor;
                clip(albedoSample.a - _Cutoff);

                float2 uvSpec = input.uv0 * _RTLB_SpecGlossMap_ST.xy + _RTLB_SpecGlossMap_ST.zw;
                float4 specGlossSample = SAMPLE_TEXTURE2D(_RTLB_SpecGlossMap, sampler_RTLB_SpecGlossMap, uvSpec);

                float2 uvBump = input.uv0 * _RTLB_BumpMap_ST.xy + _RTLB_BumpMap_ST.zw;
                float3 normalTS = UnpackNormal(SAMPLE_TEXTURE2D(_RTLB_BumpMap, sampler_RTLB_BumpMap, uvBump));
                float3x3 tbn = CreateTangentToWorld(input.normalWS, input.tangentWS, isFrontFace ? 1.0 : -1.0);
                float3 N = TransformTangentToWorld(normalTS, tbn);

                Light mainLight = GetMainLight(input.shadowCoord);
                float3 lighting = float3(0.0, 0.0, 0.0);

                float ndotlMain = saturate(dot(N, mainLight.direction));
                lighting += ndotlMain * mainLight.color * mainLight.distanceAttenuation * mainLight.shadowAttenuation;

            #ifdef _ADDITIONAL_LIGHTS
                uint lightsCount = GetAdditionalLightsCount();
                LIGHT_LOOP_BEGIN(lightsCount)
                    Light light = GetAdditionalLight(lightIndex, input.positionWS);
                    #if defined(_ADDITIONAL_LIGHT_SHADOWS)
                        uint visibleIdx = GetPerObjectLightIndex(lightIndex);
                        half sRT = AdditionalLightRealtimeShadow(visibleIdx, input.positionWS, light.direction);
                        light.shadowAttenuation = min(light.shadowAttenuation, sRT);
                    #else
                        light.shadowAttenuation = 1.0h;
                    #endif

                    float ndotl = saturate(dot(N, light.direction));
                    lighting += ndotl * light.color * light.distanceAttenuation * light.shadowAttenuation;
                LIGHT_LOOP_END
            #endif

                // TODO: Use specGlossSample for specular lighting calculation if needed.
                // float3 specular = specGlossSample.rgb;
                // float smoothness = specGlossSample.a;

                float3 baked = lighting;
                float3 outColor = lerp(baked, baked * albedoSample.rgb, saturate(_MultiplyAlbedo));
                return half4(outColor, 1.0f);
            }
            ENDHLSL
        }
    }
}
