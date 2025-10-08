Shader "URP/SimpleLambertMinimalShadows_v2"
{
    Properties
    {
        [MainColor]_BaseColor("Base Color", Color) = (1,1,1,1)
        [Toggle(_RECEIVE_SHADOWS_OFF)] _ReceiveShadows("Receive Shadows", Float) = 1
    }

    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }

        Pass
        {
            Name "UniversalForward"
            Tags { "LightMode"="UniversalForward" }
            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 4.5
            #pragma multi_compile_instancing

            // ライト／影（VS/FS 両方）
            #pragma shader_feature_local _ _RECEIVE_SHADOWS_OFF
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _SHADOWS_SOFT

            #pragma vertex   vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes { float3 positionOS:POSITION; float3 normalOS:NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings   { float4 positionCS:SV_POSITION; float3 positionWS:TEXCOORD0; float3 normalWS:TEXCOORD1; UNITY_VERTEX_INPUT_INSTANCE_ID UNITY_VERTEX_OUTPUT_STEREO };

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
            CBUFFER_END

            Varyings vert (Attributes i)
            {
                Varyings o; UNITY_SETUP_INSTANCE_ID(i); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float3 ws = TransformObjectToWorld(i.positionOS);
                o.positionWS = ws;
                o.positionCS = TransformWorldToHClip(ws);
                o.normalWS   = TransformObjectToWorldNormal(i.normalOS);
                return o;
            }

            inline half3 ShadeLambert(half3 albedo, half3 N, Light L)
            {
                half ndl = saturate(dot(N, L.direction));
                return albedo * L.color.rgb * (ndl * L.distanceAttenuation * L.shadowAttenuation);
            }

            half4 frag (Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                half3 N      = normalize(i.normalWS);
                half3 albedo = _BaseColor.rgb;
                half3 col    = 0;

                // --- Main light (cascade OK) ---
                float4 sc = TransformWorldToShadowCoord(i.positionWS);
                Light mainL = GetMainLight(sc);
                #if defined(_RECEIVE_SHADOWS_OFF)
                    mainL.shadowAttenuation = 1.0h;
                #endif
                col += ShadeLambert(albedo, N, mainL);

                // --- Additional lights ---
                #ifdef _ADDITIONAL_LIGHTS
                {
                    uint count = GetAdditionalLightsCount();
                    [loop] for (uint idx = 0u; idx < count; ++idx)
                    {
                        Light L = GetAdditionalLight(idx, i.positionWS);

                        #if defined(_ADDITIONAL_LIGHT_SHADOWS) && !defined(_RECEIVE_SHADOWS_OFF)
                            // ★ 法線あり版を使用（角度依存の軽減）
                            half sRT = AdditionalLightRealtimeShadow(idx, i.positionWS, N);
                            // ★ 二重適用を避ける（GetAdditionalLight 内で影が入る版との両対応）
                            L.shadowAttenuation = sRT;
                        #else
                            L.shadowAttenuation = 1.0h;
                        #endif

                        col += ShadeLambert(albedo, N, L);
                    }
                }
                #endif

                return half4(saturate(col), 1);
            }
            ENDHLSL
        }

        // 自分の影を落とす（必要なら）
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            Cull Back
            ZWrite On
            ZTest LEqual
            ColorMask 0
            HLSLPROGRAM
            #pragma vertex   ShadowPassVertex
            #pragma fragment ShadowPassFragment
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"
            ENDHLSL
        }
    }

    Fallback Off
}
