# 目的

Unity 6.2（URP 17.2）で **Forward（非＋）**・**HLSL** を用いて、**メインライト + 追加ライト**のリアルタイム影（カスケード対応）を **受ける**最小構成の書き方をまとめます。スクリーンスペース影は使いません。

---

## 0. プロジェクト側設定（必須）

* **URP Renderer（Forward）**

  * **Additional Lights = Per Pixel**
  * **Cast Shadows = On**
  * **Per Object Limit** は推奨 **8** 以上（ライト割当の揺れ防止）
  * 透明オブジェクトで影を受けるなら **Transparent Receive Shadows = On**
* **各ライト**

  * ライトの **Cast Shadows** を On
  * 影の欠け/浮きはライトの **Normal Bias / Depth Bias** を微調整

---

## 1. Forward パスの基本構成

```hlsl
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

    // ▼ 影・ライト関連のバリアント（重要）
    #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
    #pragma multi_compile _ _ADDITIONAL_LIGHTS
    #pragma multi_compile _ _ADDITIONAL_LIGHT_SHADOWS
    #pragma multi_compile _ _SHADOWS_SOFT

    #pragma vertex   vert
    #pragma fragment frag

    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
    // Lighting.hlsl が RealtimeLights.hlsl / Shadows.hlsl などを内部参照
```

### 解説

* **multi_compile** のセットアップが **影テクスチャをバインドさせる鍵**です。

  * `_MAIN_LIGHT_SHADOWS` / `_MAIN_LIGHT_SHADOWS_CASCADE` … メインライト影（カスケード対応）
  * `_ADDITIONAL_LIGHTS` … 追加ライト（Per Pixel 必須）
  * `_ADDITIONAL_LIGHT_SHADOWS` … **追加ライト影**の有効化（これがないと影が来ない）
  * `_SHADOWS_SOFT` … ソフトシャドウ（有効ならサンプル数が増える）
* `Core.hlsl`/`Lighting.hlsl` を `#include` して **URP の標準API** を使います。

---

## 2. 頂点/フラグメントの最小パターン

```hlsl
struct Attributes {
    float3 positionOS : POSITION;
    float3 normalOS   : NORMAL;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings {
    float4 positionCS : SV_POSITION;
    float3 positionWS : TEXCOORD0;
    float3 normalWS   : TEXCOORD1;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

Varyings vert (Attributes i)
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
```

### 解説

* **`TransformWorldToHClip`**, **`TransformObjectToWorldNormal`** など、URP の座標変換ヘルパーを使います。
* Lambert のみ（Specular や IBL は無効）にして影の検証をシンプルに。

---

## 3. メインライトの影を“受ける”

```hlsl
// 位置からメインライトの影座標を生成（カスケード対応）
float4 shadowCoord = TransformWorldToShadowCoord(i.positionWS);
// 影減衰込みのメインライト情報を取得
Light mainL = GetMainLight(shadowCoord);
// 減衰（距離 × 影）
half mainAtten = mainL.distanceAttenuation * mainL.shadowAttenuation;
// Lambert で加算
color += ShadeLambert(albedo, normalize(i.normalWS), mainL, mainAtten);
```

### よくある落とし穴

* `_MAIN_LIGHT_SHADOWS`（または `_MAIN_LIGHT_SHADOWS_CASCADE`）が **未定義**だと、影テクスチャが **バインドされません**。
* SRP Batcher 互換にするなら **CBUFFER（`UnityPerMaterial`）** を使い、グローバル変数にしない。

---

## 4. 追加ライトの影を“受ける”（**重要ポイント**）

```hlsl
#ifdef _ADDITIONAL_LIGHTS
{
    uint count = GetAdditionalLightsCount();
    half3 N = normalize(i.normalWS);
    for (uint perObj = 0u; perObj < count; ++perObj)
    {
        // per-object index でライトの基本情報を取る
        Light L = GetAdditionalLight(perObj, i.positionWS);

        #if defined(_ADDITIONAL_LIGHT_SHADOWS)
            // ★ per-object → visible index へ変換（必須）
            uint visibleIdx = GetPerObjectLightIndex(perObj);
            // ★ 第3引数は「ピクセル→ライト方向」。Point Light のキューブ面選択で使用される
            half sRT = AdditionalLightRealtimeShadow(visibleIdx, i.positionWS, L.direction);
            // ★ SimpleLit と同様、保守的に min で統合（内部影とのズレを防ぐ）
            L.shadowAttenuation = min(L.shadowAttenuation, sRT);
        #else
            L.shadowAttenuation = 1.0h;
        #endif

        // URP 17.x では Spot のコーン減衰は distanceAttenuation に内包
        half atten = L.distanceAttenuation * L.shadowAttenuation;
        color += ShadeLambert(albedo, N, L, atten);
    }
}
#endif
```

### ここが肝

* **`GetAdditionalLight(perObj, …)` の引数は per-object index**（オブジェクトに割り当てられた 0..N-1）。
* **`AdditionalLightRealtimeShadow` の第1引数は「visible light index」**。必ず `GetPerObjectLightIndex(perObj)` で変換してから渡す。
  これを怠ると **Point Light のキューブ面参照がズレる**ことがある（矩形欠け/反転の原因）。
* **第3引数はライト方向**（`L.direction`）。Point Light の場合、**`CubeMapFaceID(-lightDirection)`** によって面が選ばれるため **法線ではなくライト方向を渡す**。

---

## 5. ShadowCaster パス（“自分が影を落とす”）

```hlsl
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
    // ★ URP17 の正しいパス
    #include "Packages/com.unity.render-pipelines.universal/Shaders/ShadowCasterPass.hlsl"
    ENDHLSL
}
```

### 注意

* include パスは **`…/Shaders/ShadowCasterPass.hlsl`**（`ShaderLibrary` ではない）。
* メッシュ側の **Cast Shadows: On** を忘れずに。

---

## 6. 最小テンプレート（完成形）

Lambert + メイン/追加ライト影。スクリーンスペース影なし。Forward（非＋）。

```hlsl
Shader "URP/SimpleLambertWithShadow"
{
    Properties { [MainColor]_BaseColor("Base Color", Color) = (1,1,1,1) }

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

            struct A{ float3 positionOS:POSITION; float3 normalOS:NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V{ float4 positionCS:SV_POSITION; float3 positionWS:TEXCOORD0; float3 normalWS:TEXCOORD1; UNITY_VERTEX_INPUT_INSTANCE_ID UNITY_VERTEX_OUTPUT_STEREO };

            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
            CBUFFER_END

            V vert(A i){ V o; UNITY_SETUP_INSTANCE_ID(i); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                float3 ws = TransformObjectToWorld(i.positionOS);
                o.positionWS = ws; o.positionCS = TransformWorldToHClip(ws); o.normalWS = TransformObjectToWorldNormal(i.normalOS); return o; }

            inline half3 ShadeLambert(half3 alb, half3 N, Light L, half atten){ half ndl = saturate(dot(N, L.direction)); return alb * L.color * (ndl * atten); }

            half4 frag(V i):SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                const half3 N = normalize(i.normalWS);
                const half3 albedo = _BaseColor.rgb; half3 col = 0;

                // Main light
                float4 sc = TransformWorldToShadowCoord(i.positionWS);
                Light M = GetMainLight(sc);
                col += ShadeLambert(albedo, N, M, M.distanceAttenuation * M.shadowAttenuation);

                // Additional lights
                #ifdef _ADDITIONAL_LIGHTS
                uint count = GetAdditionalLightsCount();
                [loop] for(uint perObj=0u; perObj<count; ++perObj){
                    Light L = GetAdditionalLight(perObj, i.positionWS);
                    #if defined(_ADDITIONAL_LIGHT_SHADOWS)
                        uint visibleIdx = GetPerObjectLightIndex(perObj);
                        half sRT = AdditionalLightRealtimeShadow(visibleIdx, i.positionWS, L.direction); // ★ライト方向
                        L.shadowAttenuation = min(L.shadowAttenuation, sRT);
                    #else
                        L.shadowAttenuation = 1.0h;
                    #endif
                    col += ShadeLambert(albedo, N, L, L.distanceAttenuation * L.shadowAttenuation);
                }
                #endif

                return half4(saturate(col), 1);
            }
            ENDHLSL
        }

        Pass // ShadowCaster
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
}
```

---

## 7. デバッグ/検証のヒント

* **Frame Debugger**

  * 該当 Draw の **Textures** に

    * `_MainLightShadowmapTexture`
    * `_AdditionalLightsShadowmapTexture` が**必ず**バインドされていること
  * **Vectors** に `unity_LightIndices[2]`（per-object → visible 変換の元）が見える
* **症状別チェック**

  * **追加ライト影が来ない**: `_ADDITIONAL_LIGHT_SHADOWS` が有効か/Renderer が *Per Pixel* か
  * **Point で影が欠け/反転**: `GetPerObjectLightIndex(perObj)` を経由しているか／`AdditionalLightRealtimeShadow(..., L.direction)` か
  * **カメラ角度で揺れる**: Per Object Limit が小さすぎないか／Bias を微調整
  * **Cascade で出ない**: `_MAIN_LIGHT_SHADOWS_CASCADE` が外れていないか

---

## 8. 透明で“影だけ受ける”応用（任意）

透明材で影のみを可視化する場合のスケッチ（Renderer の Transparent Receive Shadows を On に）

```hlsl
Blend SrcAlpha OneMinusSrcAlpha
ZWrite Off
// 影の可視度 = main × (追加ライトの可視度積)
// 出力色 = 黒×影量, アルファ=影量
```

---

## 9. まとめ（要点）

* **multi_compile の影セット**が最重要（特に `_ADDITIONAL_LIGHT_SHADOWS`）。
* メイン影: `TransformWorldToShadowCoord` → `GetMainLight(sc)` → `distanceAttenuation * shadowAttenuation`。
* 追加影: `GetAdditionalLight(perObj, …)` → `GetPerObjectLightIndex(perObj)` → `AdditionalLightRealtimeShadow(visibleIdx, posWS, **L.direction**)` → `min()` 合成。
* ShadowCaster パスは `…/Shaders/ShadowCasterPass.hlsl` を include。
* Frame Debugger でテクスチャ/インデックス/キーワードを確認してから、Bias/Limit を調整。
