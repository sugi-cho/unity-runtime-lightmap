#ifndef UNLIT_LIT_LIGHTING_INCLUDED
#define UNLIT_LIT_LIGHTING_INCLUDED
#define _MAIN_LIGHT_SHADOWS

#include_with_pragmas "Assets/RealTimeLightBaker/Shaders/HLSL/UnlitLitVariants.hlsl"

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"


struct UnlitLitInput
{
    float3 positionWS;
    float3 normalWS;
    float3 baseColor;
    float occlusion;
};

static float3 ComputeMainLightContribution(in UnlitLitInput inputData)
{
    Light mainLight = GetMainLight();

    float4 shadowCoord = TransformWorldToShadowCoord(inputData.positionWS);
    #if defined(_MAIN_LIGHT_SHADOWS)
        float realtimeShadow = MainLightRealtimeShadow(shadowCoord);
    #else
        float realtimeShadow = 1.0f;
    #endif

    float NdotL = saturate(dot(inputData.normalWS, mainLight.direction));
    float attenuation = mainLight.distanceAttenuation * mainLight.shadowAttenuation * realtimeShadow;

    return mainLight.color * NdotL * attenuation;
}

static float3 ComputeAdditionalLightsContribution(in UnlitLitInput inputData)
{
    float3 lighting = 0.0f;
    uint count = GetAdditionalLightsCount();

    [loop]
    for (uint perObj = 0u; perObj < GetAdditionalLightsCount(); ++perObj)
    {
        Light light = GetAdditionalLight(perObj, inputData.positionWS);
        float NdotL = saturate(dot(inputData.normalWS, light.direction));

        #if defined(_ADDITIONAL_LIGHT_SHADOWS)
            const uint visibleIdx = GetPerObjectLightIndex(perObj);
            float realtimeShadow = AdditionalLightRealtimeShadow(visibleIdx, inputData.positionWS, light.direction);
        #else
            float realtimeShadow = 1.0f;
        #endif

        float attenuation = light.distanceAttenuation * min(light.shadowAttenuation, realtimeShadow);
        lighting += light.color * NdotL * attenuation;
    }

    return lighting;
}

void UnlitLitLighting_float(float3 positionWS, float3 normalWS, float3 albedo, float occlusion, out float3 color)
{
    UnlitLitInput inputData;
    inputData.positionWS = positionWS;
    inputData.normalWS = normalize(normalWS);
    inputData.baseColor = albedo;
    inputData.occlusion = occlusion;

    float3 directLighting = ComputeMainLightContribution(inputData);
    directLighting += ComputeAdditionalLightsContribution(inputData);

    float3 indirectLighting = SampleSH(inputData.normalWS);

    float3 combinedLighting = (directLighting + indirectLighting) * max(inputData.occlusion, 0.0f);
    color = inputData.baseColor * combinedLighting;
}

#endif // UNLIT_LIT_LIGHTING_INCLUDED
