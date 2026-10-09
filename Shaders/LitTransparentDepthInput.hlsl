// Project-local URP 14.2.0-t1 Lit input with Base Color alpha and invisible-surface discard.
#ifndef UNIVERSAL_LIT_INPUT_INCLUDED
#define UNIVERSAL_LIT_INPUT_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/ParallaxMapping.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DBuffer.hlsl"

#if defined(_DETAIL_MULX2) || defined(_DETAIL_SCALED)
#define _DETAIL
#endif


// NOTE: Do not ifdef the properties here as SRP batcher can not handle different layouts.
CBUFFER_START(UnityPerMaterial)
UNITY_DEFINE_PROP(float4, _BaseMap_ST, 0)
UNITY_DEFINE_PROP(float4, _DetailAlbedoMap_ST, UNITY_GDRP_NEXT_INDEX(_BaseMap_ST))
UNITY_DEFINE_PROP(half4, _BaseColor, UNITY_GDRP_NEXT_INDEX(_DetailAlbedoMap_ST))
UNITY_DEFINE_PROP(half4, _SpecColor, UNITY_GDRP_NEXT_INDEX(_BaseColor))
UNITY_DEFINE_PROP(half4, _EmissionColor, UNITY_GDRP_NEXT_INDEX(_SpecColor))
UNITY_DEFINE_PROP(half, _Cutoff, UNITY_GDRP_NEXT_INDEX(_EmissionColor))
UNITY_DEFINE_PROP(half, _Smoothness, UNITY_GDRP_NEXT_INDEX(_Cutoff))
UNITY_DEFINE_PROP(half, _Metallic, UNITY_GDRP_NEXT_INDEX(_Smoothness))
UNITY_DEFINE_PROP(half, _BumpScale, UNITY_GDRP_NEXT_INDEX(_Metallic))
UNITY_DEFINE_PROP(half, _Parallax, UNITY_GDRP_NEXT_INDEX(_BumpScale))
UNITY_DEFINE_PROP(half, _OcclusionStrength, UNITY_GDRP_NEXT_INDEX(_Parallax))
UNITY_DEFINE_PROP(half, _ClearCoatMask, UNITY_GDRP_NEXT_INDEX(_OcclusionStrength))
UNITY_DEFINE_PROP(half, _ClearCoatSmoothness, UNITY_GDRP_NEXT_INDEX(_ClearCoatMask))
UNITY_DEFINE_PROP(half, _DetailAlbedoMapScale, UNITY_GDRP_NEXT_INDEX(_ClearCoatSmoothness))
UNITY_DEFINE_PROP(half, _DetailNormalMapScale, UNITY_GDRP_NEXT_INDEX(_DetailAlbedoMapScale))
UNITY_DEFINE_PROP(half, _Surface, UNITY_GDRP_NEXT_INDEX(_DetailNormalMapScale))
CBUFFER_END

// NOTE: Do not ifdef the properties for dots instancing, but ifdef the actual usage.
// Otherwise you might break CPU-side as property constant-buffer offsets change per variant.
// NOTE: Dots instancing is orthogonal to the constant buffer above.
#ifdef UNITY_DOTS_INSTANCING_ENABLED

UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
    UNITY_DOTS_INSTANCED_PROP(float4, _BaseColor)
    UNITY_DOTS_INSTANCED_PROP(float4, _SpecColor)
    UNITY_DOTS_INSTANCED_PROP(float4, _EmissionColor)
    UNITY_DOTS_INSTANCED_PROP(float , _Cutoff)
    UNITY_DOTS_INSTANCED_PROP(float , _Smoothness)
    UNITY_DOTS_INSTANCED_PROP(float , _Metallic)
    UNITY_DOTS_INSTANCED_PROP(float , _BumpScale)
    UNITY_DOTS_INSTANCED_PROP(float , _Parallax)
    UNITY_DOTS_INSTANCED_PROP(float , _OcclusionStrength)
    UNITY_DOTS_INSTANCED_PROP(float , _ClearCoatMask)
    UNITY_DOTS_INSTANCED_PROP(float , _ClearCoatSmoothness)
    UNITY_DOTS_INSTANCED_PROP(float , _DetailAlbedoMapScale)
    UNITY_DOTS_INSTANCED_PROP(float , _DetailNormalMapScale)
    UNITY_DOTS_INSTANCED_PROP(float , _Surface)
UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)

// Here, we want to avoid overriding a property like e.g. _BaseColor with something like this:
// #define _BaseColor UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _BaseColor0)
//
// It would be simpler, but it can cause the compiler to regenerate the property loading code for each use of _BaseColor.
//
// To avoid this, the property loads are cached in some static values at the beginning of the shader.
// The properties such as _BaseColor are then overridden so that it expand directly to the static value like this:
// #define _BaseColor unity_DOTS_Sampled_BaseColor
//
// This simple fix happened to improve GPU performances by ~10% on Meta Quest 2 with URP on some scenes.
static float4 unity_DOTS_Sampled_BaseColor;
static float4 unity_DOTS_Sampled_SpecColor;
static float4 unity_DOTS_Sampled_EmissionColor;
static float  unity_DOTS_Sampled_Cutoff;
static float  unity_DOTS_Sampled_Smoothness;
static float  unity_DOTS_Sampled_Metallic;
static float  unity_DOTS_Sampled_BumpScale;
static float  unity_DOTS_Sampled_Parallax;
static float  unity_DOTS_Sampled_OcclusionStrength;
static float  unity_DOTS_Sampled_ClearCoatMask;
static float  unity_DOTS_Sampled_ClearCoatSmoothness;
static float  unity_DOTS_Sampled_DetailAlbedoMapScale;
static float  unity_DOTS_Sampled_DetailNormalMapScale;
static float  unity_DOTS_Sampled_Surface;

void SetupDOTSLitMaterialPropertyCaches()
{
    unity_DOTS_Sampled_BaseColor            = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _BaseColor);
    unity_DOTS_Sampled_SpecColor            = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _SpecColor);
    unity_DOTS_Sampled_EmissionColor        = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4, _EmissionColor);
    unity_DOTS_Sampled_Cutoff               = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _Cutoff);
    unity_DOTS_Sampled_Smoothness           = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _Smoothness);
    unity_DOTS_Sampled_Metallic             = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _Metallic);
    unity_DOTS_Sampled_BumpScale            = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _BumpScale);
    unity_DOTS_Sampled_Parallax             = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _Parallax);
    unity_DOTS_Sampled_OcclusionStrength    = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _OcclusionStrength);
    unity_DOTS_Sampled_ClearCoatMask        = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _ClearCoatMask);
    unity_DOTS_Sampled_ClearCoatSmoothness  = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _ClearCoatSmoothness);
    unity_DOTS_Sampled_DetailAlbedoMapScale = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _DetailAlbedoMapScale);
    unity_DOTS_Sampled_DetailNormalMapScale = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _DetailNormalMapScale);
    unity_DOTS_Sampled_Surface              = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _Surface);
}

#undef UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES
#define UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES() SetupDOTSLitMaterialPropertyCaches()

#define _BaseColor              unity_DOTS_Sampled_BaseColor
#define _SpecColor              unity_DOTS_Sampled_SpecColor
#define _EmissionColor          unity_DOTS_Sampled_EmissionColor
#define _Cutoff                 unity_DOTS_Sampled_Cutoff
#define _Smoothness             unity_DOTS_Sampled_Smoothness
#define _Metallic               unity_DOTS_Sampled_Metallic
#define _BumpScale              unity_DOTS_Sampled_BumpScale
#define _Parallax               unity_DOTS_Sampled_Parallax
#define _OcclusionStrength      unity_DOTS_Sampled_OcclusionStrength
#define _ClearCoatMask          unity_DOTS_Sampled_ClearCoatMask
#define _ClearCoatSmoothness    unity_DOTS_Sampled_ClearCoatSmoothness
#define _DetailAlbedoMapScale   unity_DOTS_Sampled_DetailAlbedoMapScale
#define _DetailNormalMapScale   unity_DOTS_Sampled_DetailNormalMapScale
#define _Surface                unity_DOTS_Sampled_Surface

#endif

#ifdef UNITY_GPU_DRIVEN_PIPELINE
#if defined(SHADER_API_MOBILE) && defined(SHADER_API_METAL) || defined(UNITY_UNIFIED_SHADER_PRECISION_MODEL)
#define _BaseMap_ST             UNITY_GDRP_MATERIAL_LOAD(float4, UNITY_GDRP_MATERIAL_PAGE_OFFSET, UNITY_GDRP_PROP_OFFSET(_BaseMap_ST))
#define _DetailAlbedoMap_ST     UNITY_GDRP_MATERIAL_LOAD(float4, UNITY_GDRP_MATERIAL_PAGE_OFFSET, UNITY_GDRP_PROP_OFFSET(_DetailAlbedoMap_ST))
#define _BaseColor              UNITY_GDRP_MATERIAL_LOAD(half4, UNITY_GDRP_MATERIAL_PAGE_OFFSET, UNITY_GDRP_PROP_OFFSET(_BaseColor))
#define _SpecColor              UNITY_GDRP_MATERIAL_LOAD(half4, UNITY_GDRP_MATERIAL_PAGE_OFFSET, UNITY_GDRP_PROP_OFFSET(_SpecColor))
#define _EmissionColor          UNITY_GDRP_MATERIAL_LOAD(half4, UNITY_GDRP_MATERIAL_PAGE_OFFSET, UNITY_GDRP_PROP_OFFSET(_EmissionColor))
#define _Cutoff                 UNITY_GDRP_MATERIAL_LOAD(half , UNITY_GDRP_MATERIAL_PAGE_OFFSET, UNITY_GDRP_PROP_OFFSET(_Cutoff))
#define _Smoothness             UNITY_GDRP_MATERIAL_LOAD(half , UNITY_GDRP_MATERIAL_PAGE_OFFSET, UNITY_GDRP_PROP_OFFSET(_Smoothness))
#define _Metallic               UNITY_GDRP_MATERIAL_LOAD(half , UNITY_GDRP_MATERIAL_PAGE_OFFSET, UNITY_GDRP_PROP_OFFSET(_Metallic))
#define _BumpScale              UNITY_GDRP_MATERIAL_LOAD(half , UNITY_GDRP_MATERIAL_PAGE_OFFSET, UNITY_GDRP_PROP_OFFSET(_BumpScale))
#define _Parallax               UNITY_GDRP_MATERIAL_LOAD(half , UNITY_GDRP_MATERIAL_PAGE_OFFSET, UNITY_GDRP_PROP_OFFSET(_Parallax))
#define _OcclusionStrength      UNITY_GDRP_MATERIAL_LOAD(half , UNITY_GDRP_MATERIAL_PAGE_OFFSET, UNITY_GDRP_PROP_OFFSET(_OcclusionStrength))
#define _ClearCoatMask          UNITY_GDRP_MATERIAL_LOAD(half , UNITY_GDRP_MATERIAL_PAGE_OFFSET, UNITY_GDRP_PROP_OFFSET(_ClearCoatMask))
#define _ClearCoatSmoothness    UNITY_GDRP_MATERIAL_LOAD(half , UNITY_GDRP_MATERIAL_PAGE_OFFSET, UNITY_GDRP_PROP_OFFSET(_ClearCoatSmoothness))
#define _DetailAlbedoMapScale   UNITY_GDRP_MATERIAL_LOAD(half , UNITY_GDRP_MATERIAL_PAGE_OFFSET, UNITY_GDRP_PROP_OFFSET(_DetailAlbedoMapScale))
#define _DetailNormalMapScale   UNITY_GDRP_MATERIAL_LOAD(half , UNITY_GDRP_MATERIAL_PAGE_OFFSET, UNITY_GDRP_PROP_OFFSET(_DetailNormalMapScale))
#define _Surface                UNITY_GDRP_MATERIAL_LOAD(half , UNITY_GDRP_MATERIAL_PAGE_OFFSET, UNITY_GDRP_PROP_OFFSET(_Surface))
#else
#define _BaseMap_ST             UNITY_GDRP_MATERIAL_LOAD(float4, UNITY_GDRP_MATERIAL_PAGE_OFFSET, UNITY_GDRP_PROP_OFFSET(_BaseMap_ST))
#define _DetailAlbedoMap_ST     UNITY_GDRP_MATERIAL_LOAD(float4, UNITY_GDRP_MATERIAL_PAGE_OFFSET, UNITY_GDRP_PROP_OFFSET(_DetailAlbedoMap_ST))
#define _BaseColor              UNITY_GDRP_MATERIAL_LOAD(float4, UNITY_GDRP_MATERIAL_PAGE_OFFSET, UNITY_GDRP_PROP_OFFSET(_BaseColor))
#define _SpecColor              UNITY_GDRP_MATERIAL_LOAD(float4, UNITY_GDRP_MATERIAL_PAGE_OFFSET, UNITY_GDRP_PROP_OFFSET(_SpecColor))
#define _EmissionColor          UNITY_GDRP_MATERIAL_LOAD(float4, UNITY_GDRP_MATERIAL_PAGE_OFFSET, UNITY_GDRP_PROP_OFFSET(_EmissionColor))
#define _Cutoff                 UNITY_GDRP_MATERIAL_LOAD(float , UNITY_GDRP_MATERIAL_PAGE_OFFSET, UNITY_GDRP_PROP_OFFSET(_Cutoff))
#define _Smoothness             UNITY_GDRP_MATERIAL_LOAD(float , UNITY_GDRP_MATERIAL_PAGE_OFFSET, UNITY_GDRP_PROP_OFFSET(_Smoothness))
#define _Metallic               UNITY_GDRP_MATERIAL_LOAD(float , UNITY_GDRP_MATERIAL_PAGE_OFFSET, UNITY_GDRP_PROP_OFFSET(_Metallic))
#define _BumpScale              UNITY_GDRP_MATERIAL_LOAD(float , UNITY_GDRP_MATERIAL_PAGE_OFFSET, UNITY_GDRP_PROP_OFFSET(_BumpScale))
#define _Parallax               UNITY_GDRP_MATERIAL_LOAD(float , UNITY_GDRP_MATERIAL_PAGE_OFFSET, UNITY_GDRP_PROP_OFFSET(_Parallax))
#define _OcclusionStrength      UNITY_GDRP_MATERIAL_LOAD(float , UNITY_GDRP_MATERIAL_PAGE_OFFSET, UNITY_GDRP_PROP_OFFSET(_OcclusionStrength))
#define _ClearCoatMask          UNITY_GDRP_MATERIAL_LOAD(float , UNITY_GDRP_MATERIAL_PAGE_OFFSET, UNITY_GDRP_PROP_OFFSET(_ClearCoatMask))
#define _ClearCoatSmoothness    UNITY_GDRP_MATERIAL_LOAD(float , UNITY_GDRP_MATERIAL_PAGE_OFFSET, UNITY_GDRP_PROP_OFFSET(_ClearCoatSmoothness))
#define _DetailAlbedoMapScale   UNITY_GDRP_MATERIAL_LOAD(float , UNITY_GDRP_MATERIAL_PAGE_OFFSET, UNITY_GDRP_PROP_OFFSET(_DetailAlbedoMapScale))
#define _DetailNormalMapScale   UNITY_GDRP_MATERIAL_LOAD(float , UNITY_GDRP_MATERIAL_PAGE_OFFSET, UNITY_GDRP_PROP_OFFSET(_DetailNormalMapScale))
#define _Surface                UNITY_GDRP_MATERIAL_LOAD(float , UNITY_GDRP_MATERIAL_PAGE_OFFSET, UNITY_GDRP_PROP_OFFSET(_Surface))
#endif


#endif //UNITY_GPU_DRIVEN_PIPELINE


TEXTURE2D(_ParallaxMap);        SAMPLER(sampler_ParallaxMap);
TEXTURE2D(_OcclusionMap);       SAMPLER(sampler_OcclusionMap);
TEXTURE2D(_DetailMask);         SAMPLER(sampler_DetailMask);
TEXTURE2D(_DetailAlbedoMap);    SAMPLER(sampler_DetailAlbedoMap);
TEXTURE2D(_DetailNormalMap);    SAMPLER(sampler_DetailNormalMap);
TEXTURE2D(_MetallicGlossMap);   SAMPLER(sampler_MetallicGlossMap);
TEXTURE2D(_SpecGlossMap);       SAMPLER(sampler_SpecGlossMap);
TEXTURE2D(_ClearCoatMap);       SAMPLER(sampler_ClearCoatMap);

#ifdef _SPECULAR_SETUP
    #define SAMPLE_METALLICSPECULAR(uv) SAMPLE_TEXTURE2D(_SpecGlossMap, sampler_SpecGlossMap, uv)
#else
    #define SAMPLE_METALLICSPECULAR(uv) SAMPLE_TEXTURE2D(_MetallicGlossMap, sampler_MetallicGlossMap, uv)
#endif

#ifdef UNITY_GPU_DRIVEN_PIPELINE
    #undef SAMPLE_METALLICSPECULAR
#ifdef _SPECULAR_SETUP
    #define SAMPLE_METALLICSPECULAR(uv, ddx, ddy) SAMPLE_TEXTURE2D_GRAD(_SpecGlossMap, sampler_SpecGlossMap, uv, ddx, ddy)
#else
    #define SAMPLE_METALLICSPECULAR(uv, ddx, ddy) SAMPLE_TEXTURE2D_GRAD(_MetallicGlossMap, sampler_MetallicGlossMap, uv, ddx, ddy)
#endif
#endif


half4 SampleMetallicSpecGloss(float2 uv, half albedoAlpha
                              UNITY_GDRP_MATERIAL_PAGE_OFFSET_PARAMETER
                              UNITY_GDRP_DDXDDY_PARAMETER)
{
    half4 specGloss;

#ifdef _METALLICSPECGLOSSMAP
    #ifdef UNITY_GPU_DRIVEN_PIPELINE
        specGloss = half4(SAMPLE_METALLICSPECULAR(uv, ddx, ddy));
    #else
        specGloss = half4(SAMPLE_METALLICSPECULAR(uv));
    #endif

    #ifdef _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
        specGloss.a = albedoAlpha * _Smoothness;
    #else
        specGloss.a *= _Smoothness;
    #endif
#else // _METALLICSPECGLOSSMAP
    #if _SPECULAR_SETUP
        specGloss.rgb = _SpecColor.rgb;
    #else
        specGloss.rgb = _Metallic.rrr;
    #endif

    #ifdef _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A
        specGloss.a = albedoAlpha * _Smoothness;
    #else
        specGloss.a = _Smoothness;
    #endif
#endif

    return specGloss;
}

half SampleOcclusion(float2 uv
    UNITY_GDRP_MATERIAL_PAGE_OFFSET_PARAMETER
    UNITY_GDRP_DDXDDY_PARAMETER)
{
#ifdef _OCCLUSIONMAP
    #ifdef UNITY_GPU_DRIVEN_PIPELINE
        half occ = SAMPLE_TEXTURE2D_GRAD(_OcclusionMap, sampler_OcclusionMap, uv, ddx, ddy).g;
    #else
        half occ = SAMPLE_TEXTURE2D(_OcclusionMap, sampler_OcclusionMap, uv).g;
    #endif
        return LerpWhiteTo(occ, _OcclusionStrength);
#else
        return half(1.0);
#endif
}


// Returns clear coat parameters
// .x/.r == mask
// .y/.g == smoothness
half2 SampleClearCoat(float2 uv
    UNITY_GDRP_DDXDDY_PARAMETER)
{
#if defined(_CLEARCOAT) || defined(_CLEARCOATMAP)
    half2 clearCoatMaskSmoothness = half2(_ClearCoatMask, _ClearCoatSmoothness);

#if defined(_CLEARCOATMAP)
#ifdef UNITY_GPU_DRIVEN_PIPELINE
    clearCoatMaskSmoothness *= SAMPLE_TEXTURE2D_GRAD(_ClearCoatMap, sampler_ClearCoatMap, uv, ddx, ddy).rg;
#else
    clearCoatMaskSmoothness *= SAMPLE_TEXTURE2D(_ClearCoatMap, sampler_ClearCoatMap, uv).rg;
#endif
#endif

    return clearCoatMaskSmoothness;
#else
    return half2(0.0, 1.0);
#endif  // _CLEARCOAT
}

void ApplyPerPixelDisplacement(half3 viewDirTS, inout float2 uv
    UNITY_GDRP_MATERIAL_PAGE_OFFSET_PARAMETER)
{
#if defined(_PARALLAXMAP)
    uv += ParallaxMapping(TEXTURE2D_ARGS(_ParallaxMap, sampler_ParallaxMap), viewDirTS, _Parallax, uv);
#endif
}

// Used for scaling detail albedo. Main features:
// - Depending if detailAlbedo brightens or darkens, scale magnifies effect.
// - No effect is applied if detailAlbedo is 0.5.
half3 ScaleDetailAlbedo(half3 detailAlbedo, half scale)
{
    // detailAlbedo = detailAlbedo * 2.0h - 1.0h;
    // detailAlbedo *= _DetailAlbedoMapScale;
    // detailAlbedo = detailAlbedo * 0.5h + 0.5h;
    // return detailAlbedo * 2.0f;

    // A bit more optimized
    return half(2.0) * detailAlbedo * scale - scale + half(1.0);
}

half3 ApplyDetailAlbedo(float2 detailUv, half3 albedo, half detailMask
    UNITY_GDRP_DDXDDY_PARAMETER
    UNITY_GDRP_MATERIAL_PAGE_OFFSET_PARAMETER)
{
#if defined(_DETAIL)
#ifdef UNITY_GPU_DRIVEN_PIPELINE
    half3 detailAlbedo = SAMPLE_TEXTURE2D_GRAD(_DetailAlbedoMap, sampler_DetailAlbedoMap, detailUv, ddx, ddy).rgb;
#else
    half3 detailAlbedo = SAMPLE_TEXTURE2D(_DetailAlbedoMap, sampler_DetailAlbedoMap, detailUv).rgb;
#endif

    // In order to have same performance as builtin, we do scaling only if scale is not 1.0 (Scaled version has 6 additional instructions)
#if defined(_DETAIL_SCALED)
    detailAlbedo = ScaleDetailAlbedo(detailAlbedo, _DetailAlbedoMapScale);
#else
    detailAlbedo = half(2.0) * detailAlbedo;
#endif

    return albedo * LerpWhiteTo(detailAlbedo, detailMask);
#else
    return albedo;
#endif
}

half3 ApplyDetailNormal(float2 detailUv, half3 normalTS, half detailMask
    UNITY_GDRP_DDXDDY_PARAMETER
    UNITY_GDRP_MATERIAL_PAGE_OFFSET_PARAMETER)
{
#if defined(_DETAIL)
#ifdef UNITY_GPU_DRIVEN_PIPELINE
#if BUMP_SCALE_NOT_SUPPORTED
    half3 detailNormalTS = UnpackNormal(SAMPLE_TEXTURE2D_GRAD(_DetailNormalMap, sampler_DetailNormalMap, detailUv, ddx, ddy));
#else
    half3 detailNormalTS = UnpackNormalScale(SAMPLE_TEXTURE2D_GRAD(_DetailNormalMap, sampler_DetailNormalMap, detailUv, ddx, ddy), _DetailNormalMapScale);
#endif
#else
#if BUMP_SCALE_NOT_SUPPORTED
    half3 detailNormalTS = UnpackNormal(SAMPLE_TEXTURE2D(_DetailNormalMap, sampler_DetailNormalMap, detailUv));
#else
    half3 detailNormalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_DetailNormalMap, sampler_DetailNormalMap, detailUv), _DetailNormalMapScale);
#endif
#endif

    // With UNITY_NO_DXT5nm unpacked vector is not normalized for BlendNormalRNM
    // For visual consistancy we going to do in all cases
    detailNormalTS = normalize(detailNormalTS);

    return lerp(normalTS, BlendNormalRNM(normalTS, detailNormalTS), detailMask); // todo: detailMask should lerp the angle of the quaternion rotation, not the normals
#else
    return normalTS;
#endif
}

inline void InitializeStandardLitSurfaceData(float2 uv, out SurfaceData outSurfaceData
    UNITY_GDRP_MATERIAL_PAGE_OFFSET_PARAMETER
    UNITY_GDRP_DDXDDY_PARAMETER)
{
#ifdef UNITY_GPU_DRIVEN_PIPELINE
    float4 ddxddy = float4(ddx, ddy);
    ddx *= _BaseMap_ST.xy;
    ddy *= _BaseMap_ST.xy;
#endif

    half4 albedoAlpha = SampleAlbedoAlpha(uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap) UNITY_GDRP_DDXDDY_ARGUMENT);
    outSurfaceData.alpha = Alpha(albedoAlpha.a, _BaseColor, _Cutoff);
    // Zero opacity must not leave depth, normals, or baked surface contributions.
    clip(outSurfaceData.alpha <= 0.0h ? -1.0h : 1.0h);

    half4 specGloss = SampleMetallicSpecGloss(uv, albedoAlpha.a UNITY_GDRP_MATERIAL_PAGE_OFFSET_ARGUMENT UNITY_GDRP_DDXDDY_ARGUMENT);
    outSurfaceData.albedo = albedoAlpha.rgb * _BaseColor.rgb;
    outSurfaceData.albedo = AlphaModulate(outSurfaceData.albedo, outSurfaceData.alpha);

#if _SPECULAR_SETUP
    outSurfaceData.metallic = half(1.0);
    outSurfaceData.specular = specGloss.rgb;
#else
    outSurfaceData.metallic = specGloss.r;
    outSurfaceData.specular = half3(0.0, 0.0, 0.0);
#endif

    outSurfaceData.smoothness = specGloss.a;
    outSurfaceData.normalTS = SampleNormal(uv, TEXTURE2D_ARGS(_BumpMap, sampler_BumpMap) UNITY_GDRP_DDXDDY_ARGUMENT, _BumpScale);
    outSurfaceData.occlusion = SampleOcclusion(uv UNITY_GDRP_MATERIAL_PAGE_OFFSET_ARGUMENT UNITY_GDRP_DDXDDY_ARGUMENT);
    outSurfaceData.emission = SampleEmission(uv, _EmissionColor.rgb, TEXTURE2D_ARGS(_EmissionMap, sampler_EmissionMap) UNITY_GDRP_DDXDDY_ARGUMENT);

#if defined(_CLEARCOAT) || defined(_CLEARCOATMAP)
#ifdef UNITY_GPU_DRIVEN_PIPELINE
    half2 clearCoat = SampleClearCoat(uv UNITY_GDRP_DDXDDY_ARGUMENT);
#else
    half2 clearCoat = SampleClearCoat(uv);
#endif
    outSurfaceData.clearCoatMask       = clearCoat.r;
    outSurfaceData.clearCoatSmoothness = clearCoat.g;
#else
    outSurfaceData.clearCoatMask       = half(0.0);
    outSurfaceData.clearCoatSmoothness = half(0.0);
#endif

#if defined(_DETAIL)
#ifdef UNITY_GPU_DRIVEN_PIPELINE
    half detailMask = SAMPLE_TEXTURE2D_GRAD(_DetailMask, sampler_DetailMask, uv, ddx, ddy).a;
    ddx = ddxddy.xy * _DetailAlbedoMap_ST.xy;
    ddy = ddxddy.zw * _DetailAlbedoMap_ST.xy;
#else
    half detailMask = SAMPLE_TEXTURE2D(_DetailMask, sampler_DetailMask, uv).a;
#endif
    float2 detailUv = uv * _DetailAlbedoMap_ST.xy + _DetailAlbedoMap_ST.zw;
    outSurfaceData.albedo = ApplyDetailAlbedo(detailUv, outSurfaceData.albedo, detailMask UNITY_GDRP_DDXDDY_ARGUMENT UNITY_GDRP_MATERIAL_PAGE_OFFSET_ARGUMENT);
    outSurfaceData.normalTS = ApplyDetailNormal(detailUv, outSurfaceData.normalTS, detailMask UNITY_GDRP_DDXDDY_ARGUMENT UNITY_GDRP_MATERIAL_PAGE_OFFSET_ARGUMENT);
#endif
}

#ifdef UNITY_GPU_DRIVEN_PIPELINE
void GPUDrivenAlphaClip(float2 uv, uint materialOffset)
{
    uv = uv * _BaseMap_ST.xy + _BaseMap_ST.zw;
    float alphaTex = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv).a * _BaseColor.a;
    float alphaValue = lerp(0, 1, alphaTex);
    alphaValue = AlphaDiscard(alphaValue, _Cutoff);
    clip(alphaValue - _Cutoff);
}
#endif

#endif // UNIVERSAL_INPUT_SURFACE_PBR_INCLUDED
