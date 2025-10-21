#ifndef RTLB_GLOBAL_RESOURCES_INCLUDED
#define RTLB_GLOBAL_RESOURCES_INCLUDED

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Texture.hlsl"

TEXTURE2D(_RTLB_BaseMap);         SAMPLER(sampler_RTLB_BaseMap);
TEXTURE2D(_RTLB_BumpMap);         SAMPLER(sampler_RTLB_BumpMap);
TEXTURE2D(_RTLB_SpecGlossMap);    SAMPLER(sampler_RTLB_SpecGlossMap);
TEXTURE2D(_RTLB_EmissionMap);     SAMPLER(sampler_RTLB_EmissionMap);

float4 _RTLB_BaseMap_ST;
float4 _RTLB_BumpMap_ST;
float4 _RTLB_SpecGlossMap_ST;
float4 _RTLB_EmissionMap_ST;
float3 _RTLB_BakeCameraPos;
float  _RTLB_HasSpecGlossMap;
float4 _RTLB_BaseColor;
float4 _RTLB_SpecColor;
float  _RTLB_Smoothness;
float4 _RTLB_EmissionColor;
float  _RTLB_HasEmissionMap;

void RTLB_GetGlobalResources_float(
    out UnityTexture2D baseMap,
    out UnitySamplerState baseMapSampler,
    out UnityTexture2D bumpMap,
    out UnitySamplerState bumpMapSampler,
    out UnityTexture2D specGlossMap,
    out UnitySamplerState specGlossMapSampler,
    out UnityTexture2D emissionMap,
    out UnitySamplerState emissionMapSampler,
    out float4 baseMapST,
    out float4 bumpMapST,
    out float4 specGlossMapST,
    out float4 emissionMapST,
    out float3 bakeCameraPos,
    out float hasSpecGlossMap,
    out float hasEmissionMap,
    out float4 baseColor,
    out float4 specColor,
    out float4 emissionColor,
    out float smoothness)
{
    baseMap            = UnityBuildTexture2DStructNoScaleNoTexelSize(_RTLB_BaseMap);
    baseMapSampler     = UnityBuildSamplerStateStruct(sampler_RTLB_BaseMap);
    bumpMap            = UnityBuildTexture2DStructNoScaleNoTexelSize(_RTLB_BumpMap);
    bumpMapSampler     = UnityBuildSamplerStateStruct(sampler_RTLB_BumpMap);
    specGlossMap       = UnityBuildTexture2DStructNoScaleNoTexelSize(_RTLB_SpecGlossMap);
    specGlossMapSampler= UnityBuildSamplerStateStruct(sampler_RTLB_SpecGlossMap);
    emissionMap        = UnityBuildTexture2DStructNoScaleNoTexelSize(_RTLB_EmissionMap);
    emissionMapSampler = UnityBuildSamplerStateStruct(sampler_RTLB_EmissionMap);

    baseMapST          = _RTLB_BaseMap_ST;
    bumpMapST          = _RTLB_BumpMap_ST;
    specGlossMapST     = _RTLB_SpecGlossMap_ST;
    emissionMapST      = _RTLB_EmissionMap_ST;
    bakeCameraPos      = _RTLB_BakeCameraPos;
    hasSpecGlossMap    = _RTLB_HasSpecGlossMap;
    hasEmissionMap     = _RTLB_HasEmissionMap;
    baseColor          = _RTLB_BaseColor;
    specColor          = _RTLB_SpecColor;
    emissionColor      = _RTLB_EmissionColor;
    smoothness         = _RTLB_Smoothness;
}

#endif // RTLB_GLOBAL_RESOURCES_INCLUDED
