Shader "URP/ShadowOnlyTransparent"
{
    Properties
    {
        [MainColor]_ShadowTint("Shadow Tint", Color) = (0,0,0,1) // 影の色（黒推奨）
        _ShadowOpacity("Shadow Opacity", Range(0,1)) = 1         // 影の最大不透明度（全体ゲイン）
        [Toggle(_RECEIVE_SHADOWS_OFF)] _ReceiveShadows("Receive Shadows (toggle)", Float) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline"="UniversalPipeline"
            "RenderType"="Transparent"
            "Queue"="Transparent"
        }

        Pass
        {
            Name "UniversalForward"
            Tags { "LightMode"="UniversalForward" }

            // 影以外は透過
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Back
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 4.5
            #pragma multi_compile_instancing

            // ライト／影（URPのキーワード）
            #pragma shader_feature_local _ _RECEIVE_SHADOWS_OFF
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile _ _SHADOWS_SOFT

            #pragma vertex   vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float3 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _ShadowTint;
                half  _ShadowOpacity;
            CBUFFER_END

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                float3 ws = TransformObjectToWorld(IN.positionOS);
                OUT.positionWS = ws;
                OUT.positionCS = TransformWorldToHClip(ws);
                OUT.normalWS   = TransformObjectToWorldNormal(IN.normalOS);
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                // ====== 影の可視度（1=明るい, 0=完全な影）を計算 ======
                // メインライト
                float4 sc = TransformWorldToShadowCoord(IN.positionWS);
                Light mainL = GetMainLight(sc);
                half visMain = 1.0h;
                #if !defined(_RECEIVE_SHADOWS_OFF)
                    visMain = mainL.shadowAttenuation;   // 1=明, 0=影
                #endif

                // 追加ライト（Forward）
                half visAddProduct = 1.0h;
                #ifdef _ADDITIONAL_LIGHTS
                {
                    uint count = GetAdditionalLightsCount();
                    [loop] for (uint i = 0u; i < count; ++i)
                    {
                        // 影の可視度（1=明, 0=影）
                        half vis = 1.0h;
                        #if defined(_ADDITIONAL_LIGHT_SHADOWS) && !defined(_RECEIVE_SHADOWS_OFF)
                            vis = AdditionalLightRealtimeShadow(i, IN.positionWS);
                        #endif
                        // 全追加ライトの影を積算（複数ライトの重なりで濃くなる）
                        visAddProduct *= vis;
                    }
                }
                #endif

                // 総合可視度（1=明, 0=影）
                half visibility = saturate(visMain * visAddProduct);

                // 影の濃さ（1=真っ黒影, 0=影なし）
                half shadowAmount = saturate((1.0h - visibility) * _ShadowOpacity);

                // 出力：色＝影色×濃さ、アルファ＝影の濃さ → 影以外は透明
                half3 rgb = _ShadowTint.rgb * shadowAmount;
                return half4(rgb, shadowAmount);
            }
            ENDHLSL
        }

        // キャッチャーは通常「影を落とさない」想定なので ShadowCaster は入れていません。
        // 必要なら MeshRenderer 側の Cast Shadows を Off にしてください。
    }

    Fallback Off
}
