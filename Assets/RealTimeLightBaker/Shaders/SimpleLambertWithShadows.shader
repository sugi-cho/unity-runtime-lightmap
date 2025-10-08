Shader "URP/SimpleLambertWithShadowsPro"
{
    Properties
    {
        [MainColor]_BaseColor("Base Color", Color) = (1,1,1,1)

        // 0: Lambert（通常）  1: FlatShadow（陰影なしで影だけ乗算） 2: ShadowMask（影係数そのものを出力）
        _LightingMode("Lighting Mode (0:Lambert,1:FlatShadow,2:ShadowMask)", Int) = 0

        // 影の強度調整
        _MainShadowGain("Main Shadow Gain", Range(0,2)) = 1
        _AddShadowGain ("Additional Shadows Gain", Range(0,2)) = 1

        // 追加ライトのうち特定Indexだけ影を強める（-1で無効）
        _AddShadowBoostIndex("Boosted Add Light Index (-1=off)", Float) = -1
        _AddShadowBoost("Boosted Add Shadow Gain", Range(0,4)) = 1

        // シェーダ側で影を受ける/受けない（SimpleLit互換トグル）
        [Toggle(_RECEIVE_SHADOWS_OFF)] _ReceiveShadows("Receive Shadows (toggle)", Float) = 1
    }

    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }

        // -------- Forward（非＋） --------
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

            // 影・ライト関連キーワード（VS/FS両方に載るマクロを使用）
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
                half4 _BaseColor;
                int   _LightingMode;
                half  _MainShadowGain;
                half  _AddShadowGain;
                float _AddShadowBoostIndex;
                half  _AddShadowBoost;
            CBUFFER_END

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                float3 ws  = TransformObjectToWorld(IN.positionOS);
                OUT.positionWS = ws;
                OUT.positionCS = TransformWorldToHClip(ws);
                OUT.normalWS   = TransformObjectToWorldNormal(IN.normalOS);
                return OUT;
            }

            // Lambert 単灯寄与
            inline half3 ShadeLambert(half3 albedo, half3 N, Light L)
            {
                half ndl = saturate(dot(N, L.direction));
                return albedo * L.color.rgb * (ndl * L.distanceAttenuation * L.shadowAttenuation);
            }

            half4 frag (Varyings IN) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                half3 N = normalize(IN.normalWS);
                half3 baseCol = _BaseColor.rgb;

                // ------ Main light（カスケード対応） ------
                float4 sc = TransformWorldToShadowCoord(IN.positionWS);
                Light mainL = GetMainLight(sc);

                // 影係数（後でFlat/Maskでも使う）
                half mainShadow = 1.0h;
                #if !defined(_RECEIVE_SHADOWS_OFF)
                    mainShadow = mainL.shadowAttenuation;
                #endif
                mainShadow = lerp(1.0h, mainShadow, _MainShadowGain);

                // ------ Additional lights ------
                half addShadowProduct = 1.0h;  // Flat/Mask 用：全追加影を乗算
                half3 lambertSum = 0;          // Lambert 用：陰影つき

                #ifdef _ADDITIONAL_LIGHTS
                {
                    uint count = GetAdditionalLightsCount();
                    int boostIdx = (int)round(_AddShadowBoostIndex);

                    [loop] for (uint i = 0u; i < count; ++i)
                    {
                        Light li = GetAdditionalLight(i, IN.positionWS);

                        // Realtime 影係数
                        half sRT = 1.0h;
                        #if defined(_ADDITIONAL_LIGHT_SHADOWS) && !defined(_RECEIVE_SHADOWS_OFF)
                            sRT = AdditionalLightRealtimeShadow(i, IN.positionWS);
                        #endif

                        // URP内部の shadowAttenuation（点/スポット向けフィルタ等）
                        half s = li.shadowAttenuation * sRT;

                        // ブースト（特定追加ライトの影だけ強める）
                        half gain = _AddShadowGain;
                        if (boostIdx >= 0 && (int)i == boostIdx)
                            gain *= _AddShadowBoost;

                        // Flat/Mask 用（影だけ）：影だけを積算（強度反映）
                        addShadowProduct *= lerp(1.0h, s, gain);

                        // Lambert 用（陰影）：通常の明るさも計算
                        Light lambertL = li;
                        lambertL.shadowAttenuation = lerp(1.0h, s, gain);
                        lambertSum += ShadeLambert(baseCol, N, lambertL);
                    }
                }
                #endif

                // ------ モード別合成 ------
                // 0: Lambert（通常） = メインLambert + 追加Lambert
                // 1: FlatShadow     = 陰影なし。BaseColorに (main * add) の影だけ乗算
                // 2: ShadowMask     = 影係数そのものを出力（グレースケール）
                if (_LightingMode == 0)
                {
                    // Main のLambert
                    half3 col = ShadeLambert(baseCol, N, mainL);
                    // mainShadowGain を反映（LambertではshadowAttenuationに掛ける）
                    #if !defined(_RECEIVE_SHADOWS_OFF)
                        col *= (mainShadow / max(mainL.shadowAttenuation, 1e-4h));
                    #endif
                    col += lambertSum;
                    return half4(saturate(col), 1);
                }
                else if (_LightingMode == 1)
                {
                    // 陰影は出さず、影だけを色に乗算
                    half shadowAll = saturate(mainShadow * addShadowProduct);
                    return half4(baseCol * shadowAll, 1);
                }
                else // _LightingMode == 2
                {
                    // 影マスクをそのまま出す（デバッグ＆合成用）
                    half shadowAll = saturate(mainShadow * addShadowProduct);
                    return half4(shadowAll.xxx, 1);
                }
            }
            ENDHLSL
        }

        // -------- 自分の影を落とす --------
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
