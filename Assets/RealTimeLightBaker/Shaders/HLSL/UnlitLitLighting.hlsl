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
    float3 viewDirWS;
    float smoothness;
    float specularStrength;
    float3 baseColor;
    float occlusion;
};

struct LightingResult
{
    float3 diffuse;
    float3 specular;
};

static LightingResult ComputeMainLightContribution(in UnlitLitInput inputData)
{
    LightingResult result;
    result.diffuse = 0.0f;
    result.specular = 0.0f;

    Light mainLight = GetMainLight();

    float4 shadowCoord = TransformWorldToShadowCoord(inputData.positionWS);
    #if defined(_MAIN_LIGHT_SHADOWS)
        float realtimeShadow = MainLightRealtimeShadow(shadowCoord);
    #else
        float realtimeShadow = 1.0f;
    #endif

    float smoothness = saturate(inputData.smoothness);
    float shininess = exp2(10.0f * smoothness + 1.0f);

    float NdotL = saturate(dot(inputData.normalWS, mainLight.direction));
    float attenuation = mainLight.distanceAttenuation * mainLight.shadowAttenuation * realtimeShadow;

    float3 lightContribution = mainLight.color * attenuation;
    result.diffuse = lightContribution * NdotL;

    if (NdotL > 0.0f)
    {
        float3 halfDir = normalize(mainLight.direction + inputData.viewDirWS);
        float specularTerm = pow(saturate(dot(inputData.normalWS, halfDir)), shininess);
        result.specular = lightContribution * specularTerm * inputData.specularStrength;
    }

    return result;
}

static LightingResult ComputeAdditionalLightsContribution(in UnlitLitInput inputData)
{
    LightingResult result;
    result.diffuse = 0.0f;
    result.specular = 0.0f;

    uint count = GetAdditionalLightsCount();
    float smoothness = saturate(inputData.smoothness);
    float shininess = exp2(10.0f * smoothness + 1.0f);

    [loop]
    for (uint perObj = 0u; perObj < count; ++perObj)
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
        float3 lightContribution = light.color * attenuation;
        result.diffuse += lightContribution * NdotL;

        if (NdotL > 0.0f)
        {
            float3 halfDir = normalize(light.direction + inputData.viewDirWS);
            float specularTerm = pow(saturate(dot(inputData.normalWS, halfDir)), shininess);
            result.specular += lightContribution * specularTerm * inputData.specularStrength;
        }
    }

    return result;
}

void UnlitLitLighting_float(float3 positionWS, float3 normalWS, float3 albedo, float occlusion, float smoothness, float specularStrength, out float3 color)
{
    UnlitLitInput inputData;
    inputData.positionWS = positionWS;
    inputData.normalWS = normalize(normalWS);
    inputData.viewDirWS = normalize(GetWorldSpaceViewDir(positionWS));
    inputData.smoothness = saturate(smoothness);
    inputData.specularStrength = max(specularStrength, 0.0f);
    inputData.baseColor = albedo;
    inputData.occlusion = occlusion;

    LightingResult mainLight = ComputeMainLightContribution(inputData);
    LightingResult additionalLights = ComputeAdditionalLightsContribution(inputData);

    float3 directLighting = mainLight.diffuse + additionalLights.diffuse;
    float3 indirectLighting = SampleSH(inputData.normalWS);
    float3 specularLighting = mainLight.specular + additionalLights.specular;

    float occlusionFactor = max(inputData.occlusion, 0.0f);
    float3 combinedLighting = (directLighting + indirectLighting) * occlusionFactor;
    specularLighting *= occlusionFactor;

    color = inputData.baseColor * combinedLighting + specularLighting;
}

#endif // UNLIT_LIT_LIGHTING_INCLUDED
