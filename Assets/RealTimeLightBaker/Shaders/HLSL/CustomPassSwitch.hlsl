// File: SelectForwardOnly.hlsl
#ifndef CUSTOM_SELECT_FORWARD_ONLY_INCLUDED
#define CUSTOM_SELECT_FORWARD_ONLY_INCLUDED

// ─────────────────────────────────────────────────────────────────────────────
// SHADERPASS 定義（参考：URP ShaderGraph/Includes/ShaderPass.hlsl）
//   SHADERPASS_FORWARD (0)
//   SHADERPASS_GBUFFER (1)
//   SHADERPASS_DEPTHONLY (2)
//   SHADERPASS_SHADOWCASTER (3)
//   SHADERPASS_META (4)
//   SHADERPASS_2D (5)
//   SHADERPASS_UNLIT (6)
//   SHADERPASS_SPRITELIT (7)
//   SHADERPASS_SPRITENORMAL (8)
//   SHADERPASS_SPRITEFORWARD (9)
//   SHADERPASS_SPRITEUNLIT (10)
//   SHADERPASS_DEPTHNORMALSONLY (11)
//   SHADERPASS_DBUFFER_PROJECTOR (12)
//   SHADERPASS_DBUFFER_MESH (13)
//   SHADERPASS_FORWARD_EMISSIVE_PROJECTOR (14)
//   SHADERPASS_FORWARD_EMISSIVE_MESH (15)
//   SHADERPASS_FORWARD_PREVIEW (16)
//   SHADERPASS_DECAL_SCREEN_SPACE_PROJECTOR (17)
//   SHADERPASS_DECAL_SCREEN_SPACE_MESH (18)
//   SHADERPASS_DECAL_GBUFFER_PROJECTOR (19)
//   SHADERPASS_DECAL_GBUFFER_MESH (20)
//   SHADERPASS_DEPTHNORMALS (21)
//   SHADERPASS_MOTION_VECTORS (22)
// （バージョンにより増減あり）
// ─────────────────────────────────────────────────────────────────────────────

// SHADERPASS マクロが未定義の場合はヘッダを取り込み（多重includeはガードで安全）
#ifndef SHADERPASS_FORWARD
    #include "Packages/com.unity.render-pipelines.universal/Editor/ShaderGraph/Includes/ShaderPass.hlsl"
#endif

// Forward系かどうか（FORWARD / GBUFFER / FORWARD_PREVIEW）を判定して A/B を選択
// ※ このロジックは全オーバーロードから呼ばれます
#if defined(SHADERPASS) && \
    ((SHADERPASS == SHADERPASS_FORWARD) || \
     (SHADERPASS == SHADERPASS_GBUFFER) || \
     (SHADERPASS == SHADERPASS_UNLIT) || \
     (SHADERPASS == SHADERPASS_PREVIEW))
    #define SELECT_FWD_ONLY_IMPL(OUT, A, B) OUT = (A)
#else
    #define SELECT_FWD_ONLY_IMPL(OUT, A, B) OUT = (B)
#endif

#define SELECT_FWD_ONLY(OUT, A, B) \
    do { \
        /* Forward path family only */ \
        SELECT_FWD_ONLY_IMPL(OUT, A, B); \
    } while(0)

// ========================= _float（Graph Precision: Float）=====================

void SelectForwardOnly_float(float A, float B, out float Out)              { SELECT_FWD_ONLY(Out, A, B); }
void SelectForwardOnly_float(float2 A, float2 B, out float2 Out)           { SELECT_FWD_ONLY(Out, A, B); }
void SelectForwardOnly_float(float3 A, float3 B, out float3 Out)           { SELECT_FWD_ONLY(Out, A, B); }
void SelectForwardOnly_float(float4 A, float4 B, out float4 Out)           { SELECT_FWD_ONLY(Out, A, B); }

// ========================= _half（Graph Precision: Half）======================

void SelectForwardOnly_half(half A, half B, out half Out)                  { SELECT_FWD_ONLY(Out, A, B); }
void SelectForwardOnly_half(half2 A, half2 B, out half2 Out)               { SELECT_FWD_ONLY(Out, A, B); }
void SelectForwardOnly_half(half3 A, half3 B, out half3 Out)               { SELECT_FWD_ONLY(Out, A, B); }
void SelectForwardOnly_half(half4 A, half4 B, out half4 Out)               { SELECT_FWD_ONLY(Out, A, B); }

#undef SELECT_FWD_ONLY
#undef SELECT_FWD_ONLY_IMPL
#endif // CUSTOM_SELECT_FORWARD_ONLY_INCLUDED

