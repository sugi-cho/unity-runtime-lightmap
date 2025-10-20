Shader "URP/SimpleLambertWithShadow"
{
    Properties
    {
        [MainColor]_BaseColor("Base Color", Color) = (1,1,1,1)
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

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _SHADOWS_SOFT

            #pragma vertex   vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes { float3 positionOS:POSITION; float3 normalOS:NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings
            {
                float4 positionCS:SV_POSITION;
                float3 positionWS:TEXCOORD0;
                float3 normalWS  :TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
            CBUFFER_END

            Varyings vert(Attributes i)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(i);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float3 ws = TransformObjectToWorld(i.positionOS);
                o.positionWS = ws;
                o.positionCS = TransformWorldToHClip(ws);
                o.normalWS   = TransformObjectToWorldNormal(i.normalOS);
                return o;
            }

            inline half3 ShadeLambert(half3 albedo, half3 N, Light L, half atten)
            {
                half ndl = saturate(dot(N, L.direction));
                return albedo * L.color * (ndl * atten);
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);

                const half3 N = normalize(i.normalWS);
                const half3 albedo = _BaseColor.rgb;
                half3 col = 0;

                // ===== Main light =====
                float4 sc = TransformWorldToShadowCoord(i.positionWS);
                Light mainL = GetMainLight(sc);
                half mainAtten = mainL.distanceAttenuation * mainL.shadowAttenuation;
                col += ShadeLambert(albedo, N, mainL, mainAtten);

                // ===== Additional lights =====
                #ifdef _ADDITIONAL_LIGHTS
                {
                    const uint count = GetAdditionalLightsCount();
                    [loop] for (uint idx = 0u; idx < count; ++idx)
                    {
                        // idx は per-object index
                        Light L = GetAdditionalLight(idx, i.positionWS);

                        #if defined(_ADDITIONAL_LIGHT_SHADOWS)
                            // ★ 可視ライトの index へ変換してからサンプリング
                            uint visibleIdx = GetPerObjectLightIndex(idx);
                            half sRT = AdditionalLightRealtimeShadow(visibleIdx, i.positionWS, L.direction);
                            // ★ SimpleLit と同じく “min” で統合（実装差分による二重適用ズレを防止）
                            L.shadowAttenuation = min(L.shadowAttenuation, sRT);
                        #else
                            L.shadowAttenuation = 1.0h;
                        #endif

                        half atten = L.distanceAttenuation * L.shadowAttenuation; // spot減衰は距離減衰に内包
                        col += ShadeLambert(albedo, N, L, atten);
                    }
                }
                #endif

                return half4(saturate(col), 1);
            }
            ENDHLSL
        }

        // 自身の影を落とす
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
