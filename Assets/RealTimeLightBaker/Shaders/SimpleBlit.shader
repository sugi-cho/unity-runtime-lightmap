// Assets/Shaders/BlitTest.shader
Shader "Hidden/RG/BlitTest"
{
    Properties
    {    }
    HLSLINCLUDE
    // Core RP の Blit ユーティリティを使用します。共通定義を先に読み込んで型やマクロを定義します。
    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/TextureXR.hlsl"
    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

    // Blit.hlsl を直接含めるとパッケージ側で
    // 同じシェーダ変数が定義され重複エラーになる場合があるため、
    // 必要最小限の宣言だけを行います。
    TEXTURE2D(_BlitTexture);
    SAMPLER(sampler_LinearClamp);
    float4 _BlitScaleBias;

        struct Attributes { uint vertexID : SV_VertexID; };
        struct Varyings   { float4 positionCS : SV_Position; float2 texcoord : TEXCOORD0; };

        Varyings Vert(Attributes v)
        {
            Varyings o;
            // フルスクリーントライアングルの頂点位置と UV を取得
            o.positionCS = GetFullScreenTriangleVertexPosition(v.vertexID);
            o.texcoord   = GetFullScreenTriangleTexCoord(v.vertexID);
            return o;
        }

        half4 Frag(Varyings i) : SV_Target
        {
            // UV をスケール/バイアスで調整
            float2 uv = i.texcoord * _BlitScaleBias.xy + _BlitScaleBias.zw;

            // テクスチャを sample
            half4 c = SAMPLE_TEXTURE2D(_BlitTexture, sampler_LinearClamp, uv);
            return c;
        }
    ENDHLSL

    SubShader
    {
        // SRP に依存しないシェーダ。RenderPipeline を選ばず（URP/HDRP など）動作します。
        Pass
        {
            Name "CopyTint"
            ZTest Always ZWrite Off Cull Off
            HLSLPROGRAM
                #pragma vertex Vert
                #pragma fragment Frag
            ENDHLSL
        }
    }
    Fallback Off
}
