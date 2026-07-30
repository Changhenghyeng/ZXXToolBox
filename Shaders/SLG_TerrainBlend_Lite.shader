Shader "Custom/Terrain Blend Lite"
{
    Properties
    {
        [Header(Terrain Range Control)]
        [NoScaleOffset] _ControlMap("Control Map (R Type A, G Type B, B Type C, A Type D)", 2D) = "red" {}
        _ControlSharpness("Control Edge Sharpness", Range(0.5, 4.0)) = 1.0

        [Header(Bottom Base Layer)]
        [Toggle(_SLG_BOTTOM_LOD)] _UseBottomLod("Bottom Participates In Far LOD", Float) = 1
        _BottomTint("Bottom Tint", Color) = (1, 1, 1, 1)
        _BottomBase("Bottom Base", 2D) = "black" {}
        _BottomDissolveSoftness("Bottom Dissolve Softness", Range(0.01, 0.49)) = 0.15
        _BottomUvDistortion("Bottom UV Distortion Strength", Range(0, 1)) = 0.3

        [Header(Base Terrain Textures)]
        _Terrain0Tint("Type A Tint", Color) = (1, 1, 1, 1)
        _Terrain0Base("Type A Base (R)", 2D) = "white" {}
        _Terrain1Tint("Type B Tint", Color) = (1, 1, 1, 1)
        _Terrain1Base("Type B Base (G)", 2D) = "white" {}
        _Terrain2Tint("Type C Tint", Color) = (1, 1, 1, 1)
        _Terrain2Base("Type C Base (B)", 2D) = "white" {}
        _Terrain3Tint("Type D Tint", Color) = (1, 1, 1, 1)
        _Terrain3Base("Type D Base (A)", 2D) = "white" {}
        
        
        
        

        [Header(Optional Per Terrain Variant)]
        [Toggle(_SLG_VARIANTS)] _UseVariants("Enable Variant Textures", Float) = 0
        _Terrain0VariantRatio("Type A Variant Coverage", Range(0, 1)) = 0.5
        _Terrain0Variant("Type A Variant", 2D) = "white" {}
        _Terrain1VariantRatio("Type B Variant Coverage", Range(0, 1)) = 0.5
        _Terrain1Variant("Type B Variant", 2D) = "white" {}
        _Terrain2VariantRatio("Type C Variant Coverage", Range(0, 1)) = 0.5
        _Terrain2Variant("Type C Variant", 2D) = "white" {}
        _Terrain3VariantRatio("Type D Variant Coverage", Range(0, 1)) = 0.5
        _Terrain3Variant("Type D Variant", 2D) = "white" {}
        _BlendNoise("LOD0 Near Detail Noise RGBA", 2D) = "gray" {}
        [HideInInspector] _BlendNoiseNearInfluence("Near Noise Influence", Range(0, 1)) = 0.25
        _BlendNoiseMid("LOD0-1 Middle Detail Noise RGBA", 2D) = "gray" {}
        [HideInInspector] _BlendNoiseMidInfluence("Middle Noise Influence", Range(0, 1)) = 0.5
        _BlendNoiseFar("LOD0-2 Far Base Noise RGBA", 2D) = "gray" {}
        _VariantEdgeSoftness("Variant Edge Softness", Range(0.01, 0.49)) = 0.2
        _FarVariantStrength("Far Variant Strength", Range(0, 1)) = 0.35
        _VariantNearToMiddleAt("LOD0 To LOD1 At (Camera Size)", Float) = 30
        _VariantMiddleToFarAt("LOD1 To LOD2 At (Camera Size)", Float) = 45
        _VariantDisappearAt("LOD3 Complete At (Camera Size)", Float) = 60
        _Lod3ToBottomRange("LOD3 To Bottom Range", Float) = 12
        _VariantLodTransitionRatio("LOD Transition Smoothness", Range(0.01, 1.0)) = 0.2
        [HideInInspector] _VariantSoftness("Legacy Variant Softness", Vector) = (0.2, 0.2, 0.2, 0.2)

        [Header(Global)]
        _GlobalTint("Global Tint", Color) = (1, 1, 1, 1)
        _Saturation("Saturation", Range(0, 2)) = 1

    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }

            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            // Optional terrain stages use local keywords so unused work is stripped.
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag

            #pragma shader_feature_local_fragment _SLG_BOTTOM_LOD
            #pragma shader_feature_local_fragment _SLG_VARIANTS
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_ControlMap);
            SAMPLER(sampler_ControlMap);

            TEXTURE2D(_BottomBase);
            SAMPLER(sampler_BottomBase);

            TEXTURE2D(_Terrain0Base);
            SAMPLER(sampler_Terrain0Base);
            TEXTURE2D(_Terrain1Base);
            SAMPLER(sampler_Terrain1Base);
            TEXTURE2D(_Terrain2Base);
            SAMPLER(sampler_Terrain2Base);
            TEXTURE2D(_Terrain3Base);
            SAMPLER(sampler_Terrain3Base);

            #if defined(_SLG_VARIANTS)
                TEXTURE2D(_Terrain0Variant);
                SAMPLER(sampler_Terrain0Variant);
                TEXTURE2D(_Terrain1Variant);
                SAMPLER(sampler_Terrain1Variant);
                TEXTURE2D(_Terrain2Variant);
                SAMPLER(sampler_Terrain2Variant);
                TEXTURE2D(_Terrain3Variant);
                SAMPLER(sampler_Terrain3Variant);
                TEXTURE2D(_BlendNoise);
                SAMPLER(sampler_BlendNoise);
                TEXTURE2D(_BlendNoiseMid);
                SAMPLER(sampler_BlendNoiseMid);
                TEXTURE2D(_BlendNoiseFar);
                SAMPLER(sampler_BlendNoiseFar);
            #endif

            CBUFFER_START(UnityPerMaterial)
                float4 _BottomBase_ST;
                float4 _Terrain0Base_ST;
                float4 _Terrain1Base_ST;
                float4 _Terrain2Base_ST;
                float4 _Terrain3Base_ST;
                float4 _Terrain0Variant_ST;
                float4 _Terrain1Variant_ST;
                float4 _Terrain2Variant_ST;
                float4 _Terrain3Variant_ST;
                float4 _BlendNoise_ST;
                float4 _BlendNoiseMid_ST;
                float4 _BlendNoiseFar_ST;
                half4 _BottomTint;
                half4 _Terrain0Tint;
                half4 _Terrain1Tint;
                half4 _Terrain2Tint;
                half4 _Terrain3Tint;
                half4 _GlobalTint;
                float _BlendNoiseNearInfluence;
                float _BlendNoiseMidInfluence;
                float _VariantEdgeSoftness;
                float _FarVariantStrength;
                float _BottomDissolveSoftness;
                float _BottomUvDistortion;
                float _ControlSharpness;
                float _Terrain0VariantRatio;
                float _Terrain1VariantRatio;
                float _Terrain2VariantRatio;
                float _Terrain3VariantRatio;
                float _VariantNearToMiddleAt;
                float _VariantMiddleToFarAt;
                float _VariantDisappearAt;
                float _Lod3ToBottomRange;
                float _VariantLodTransitionRatio;
                float _Saturation;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half3 normalWS : TEXCOORD2;
                half fogFactor : TEXCOORD3;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv = input.uv;
                output.fogFactor = ComputeFogFactor(positionInputs.positionCS.z);
                return output;
            }

            half4 NormalizeControlWeights(half4 weights)
            {
                weights = saturate(weights);
                // Sharpness 1 is the normal/default case. Avoid four pow
                // operations when the control weights do not need reshaping.
                [branch]
                if (_ControlSharpness != 1.0)
                {
                    weights = pow(weights, _ControlSharpness);
                }
                half sum = dot(weights, half4(1, 1, 1, 1));
                if (sum > 0.0001h)
                {
                    return weights / sum;
                }

                // A black control pixel belongs to none of the four terrain types.
                return half4(0, 0, 0, 0);
            }

            #if defined(_SLG_BOTTOM_LOD)
                half DistanceFade(
                    float distanceToCamera,
                    float fadeStart,
                    float fadeEnd)
                {
                    float safeEnd = max(fadeEnd, fadeStart + 0.001);
                    return 1.0h - smoothstep(
                        fadeStart,
                        safeEnd,
                        distanceToCamera);
                }
            #endif

            #if defined(_SLG_VARIANTS) || defined(_SLG_BOTTOM_LOD)
                float GetLodMetric(float3 positionWS)
                {
                    // unity_OrthoParams.y is the full visible height. Unity's Camera
                    // inspector shows half that value as Orthographic Size.
                    float lodMetric = max(
                        unity_OrthoParams.y * 0.5,
                        0.001);
                    [branch]
                    if (unity_OrthoParams.w <= 0.5)
                    {
                        lodMetric = distance(
                            _WorldSpaceCameraPos,
                            positionWS);
                    }
                    return lodMetric;
                }
            #endif

            half3 ApplySaturation(half3 color, half saturation)
            {
                [branch]
                if (saturation == 1.0h)
                {
                    return color;
                }

                half luminance = dot(color, half3(0.2126h, 0.7152h, 0.0722h));
                return lerp(luminance.xxx, color, saturation);
            }

            float2 TransformTerrainUV(float2 terrainUV, float4 textureST)
            {
                return terrainUV * textureST.xy + textureST.zw;
            }

            #if defined(_SLG_VARIANTS) || defined(_SLG_BOTTOM_LOD)
                struct TerrainLodState
                {
                    float metric;
                    float nearStart;
                    float nearEnd;
                    float middleStart;
                    float middleEnd;
                    float bottomStart;
                    float bottomEnd;
                    half noiseLod;
                };

                TerrainLodState EvaluateTerrainLod(float3 positionWS)
                {
                    TerrainLodState lod = (TerrainLodState)0;
                    float transitionRatio = clamp(
                        _VariantLodTransitionRatio,
                        0.001,
                        1.0);
                    float nearCenter = max(_VariantNearToMiddleAt, 0.0);
                    float middleCenter = max(
                        _VariantMiddleToFarAt,
                        nearCenter + 0.001);
                    lod.bottomEnd = max(
                        _VariantDisappearAt,
                        middleCenter + 0.001);

                    float nearWidth = max(
                        (middleCenter - nearCenter) * transitionRatio,
                        0.001);
                    float middleWidth = max(
                        (lod.bottomEnd - middleCenter) * transitionRatio,
                        0.001);
                    lod.metric = GetLodMetric(positionWS);
                    lod.nearStart = max(
                        nearCenter - nearWidth * 0.5,
                        0.0);
                    lod.nearEnd = nearCenter + nearWidth * 0.5;
                    lod.middleStart = max(
                        middleCenter - middleWidth * 0.5,
                        lod.nearEnd);
                    lod.middleEnd = max(
                        middleCenter + middleWidth * 0.5,
                        lod.middleStart + 0.001);
                    lod.bottomStart = max(
                        lod.bottomEnd -
                        max(_Lod3ToBottomRange, 0.001),
                        0.0);
                    lod.noiseLod =
                        smoothstep(
                            lod.nearStart,
                            max(lod.nearEnd, lod.nearStart + 0.001),
                            lod.metric) +
                        smoothstep(
                            lod.middleStart,
                            max(
                                lod.middleEnd,
                                lod.middleStart + 0.001),
                            lod.metric);
                    return lod;
                }
            #endif

            #if defined(_SLG_VARIANTS)
                half4 CompensateNoiseBlend(
                    half4 blendedNoise,
                    half blend,
                    half correlation)
                {
                    half inverseBlend = 1.0h - blend;
                    half variance = max(
                        inverseBlend * inverseBlend +
                        blend * blend +
                        2.0h * correlation *
                        inverseBlend * blend,
                        0.001h);
                    return saturate(
                        0.5h +
                        (blendedNoise - 0.5h) *
                        rsqrt(variance));
                }

                half4 SampleVariantNoise(
                    float2 terrainUV,
                    TerrainLodState lod)
                {
                    half4 farNoise = SAMPLE_TEXTURE2D(
                        _BlendNoiseFar, sampler_BlendNoiseFar,
                        TransformTerrainUV(terrainUV, _BlendNoiseFar_ST));
                    half4 result = farNoise;
                    float noiseLod = clamp(
                        (float)lod.noiseLod,
                        0.0,
                        2.0);

                    [branch]
                    if (noiseLod < 2.0)
                    {
                        half middleInfluence =
                            (half)_BlendNoiseMidInfluence;
                        half4 middleNoise = SAMPLE_TEXTURE2D(
                            _BlendNoiseMid, sampler_BlendNoiseMid,
                            TransformTerrainUV(
                                terrainUV,
                                _BlendNoiseMid_ST));
                        half middleNormalization = rsqrt(
                            1.0h + middleInfluence * middleInfluence);
                        half4 middleState = 0.5h + (
                            (farNoise - 0.5h) +
                            (middleNoise - 0.5h) * middleInfluence) *
                            middleNormalization;

                        [branch]
                        if (noiseLod < 1.0)
                        {
                            half nearInfluence =
                                (half)_BlendNoiseNearInfluence;
                            half4 nearNoise = SAMPLE_TEXTURE2D(
                                _BlendNoise, sampler_BlendNoise,
                                TransformTerrainUV(
                                    terrainUV,
                                    _BlendNoise_ST));
                            half nearNormalization = rsqrt(
                                1.0h +
                                middleInfluence * middleInfluence +
                                nearInfluence * nearInfluence);
                            half4 nearState = 0.5h + (
                                (farNoise - 0.5h) +
                                (middleNoise - 0.5h) * middleInfluence +
                                (nearNoise - 0.5h) * nearInfluence) *
                                nearNormalization;
                            half blend = (half)noiseLod;
                            half correlation = sqrt(
                                (1.0h +
                                 middleInfluence * middleInfluence) /
                                (1.0h +
                                 middleInfluence * middleInfluence +
                                 nearInfluence * nearInfluence));
                            result = CompensateNoiseBlend(
                                lerp(nearState, middleState, blend),
                                blend,
                                correlation);
                        }
                        else
                        {
                            half blend =
                                (half)(noiseLod - 1.0);
                            result = CompensateNoiseBlend(
                                lerp(middleState, farNoise, blend),
                                blend,
                                middleNormalization);
                        }
                    }

                    return saturate(result);
                }

                half4 BuildVariantBlendMask(
                    half4 noiseMask,
                    half lodProgress,
                    half variantStrength)
                {
                    half nearSoftness = clamp(
                        (half)_VariantEdgeSoftness,
                        0.001h,
                        0.499h);
                    half farSoftness = max(
                        nearSoftness * 0.35h,
                        0.001h);
                    half artistSoftness = lerp(
                        nearSoftness,
                        farSoftness,
                        lodProgress);
                    half4 softness = max(
                        half4(
                            artistSoftness,
                            artistSoftness,
                            artistSoftness,
                            artistSoftness),
                        fwidth(noiseMask) * 0.5h);
                    half4 variantRatio = saturate(half4(
                        _Terrain0VariantRatio,
                        _Terrain1VariantRatio,
                        _Terrain2VariantRatio,
                        _Terrain3VariantRatio));

                    // Coverage moves only the threshold. Edge softness no longer
                    // changes the occupied area.
                    half4 threshold = 1.0h - variantRatio;
                    half4 mask = smoothstep(
                        threshold - softness,
                        threshold + softness,
                        noiseMask);

                    // Preserve exact all-base/all-variant endpoints.
                    mask *= step(0.0001h, variantRatio);
                    mask = lerp(
                        mask,
                        half4(1.0h, 1.0h, 1.0h, 1.0h),
                        step(0.9999h, variantRatio));
                    return mask * variantStrength;
                }

                half BuildNoiseDissolve(
                    half noise,
                    half transition,
                    half softness)
                {
                    half safeSoftness = clamp(
                        max(
                            softness,
                            fwidth(noise) * 0.5h),
                        0.001h,
                        0.499h);
                    half threshold = lerp(
                        safeSoftness,
                        1.0h - safeSoftness,
                        saturate(noise));
                    return smoothstep(
                        threshold - safeSoftness,
                        threshold + safeSoftness,
                        transition);
                }
            #endif

            float2 BuildBottomUV(
                float2 terrainUV,
                half4 noise,
                half detailAmount)
            {
                half2 distortion = half2(
                    noise.r - noise.b,
                    noise.g - noise.a);
                return TransformTerrainUV(
                    terrainUV,
                    _BottomBase_ST) +
                    (float2)distortion *
                    (_BottomUvDistortion * 0.1) *
                    detailAmount;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                half4 controlWeights = saturate(
                    SAMPLE_TEXTURE2D(_ControlMap, sampler_ControlMap, input.uv));
                half terrainCoverage = saturate(
                    dot(controlWeights, half4(1, 1, 1, 1)));
                half4 weights = NormalizeControlWeights(controlWeights);

                float2 terrainUV = input.uv;

                half3 terrain0 = SAMPLE_TEXTURE2D(
                    _Terrain0Base, sampler_Terrain0Base,
                    TransformTerrainUV(terrainUV, _Terrain0Base_ST)).rgb *
                    _Terrain0Tint.rgb;
                half3 terrain1 = SAMPLE_TEXTURE2D(
                    _Terrain1Base, sampler_Terrain1Base,
                    TransformTerrainUV(terrainUV, _Terrain1Base_ST)).rgb *
                    _Terrain1Tint.rgb;
                half3 terrain2 = SAMPLE_TEXTURE2D(
                    _Terrain2Base, sampler_Terrain2Base,
                    TransformTerrainUV(terrainUV, _Terrain2Base_ST)).rgb *
                    _Terrain2Tint.rgb;
                half3 terrain3 = SAMPLE_TEXTURE2D(
                    _Terrain3Base, sampler_Terrain3Base,
                    TransformTerrainUV(terrainUV, _Terrain3Base_ST)).rgb *
                    _Terrain3Tint.rgb;

                half3 resolvedTerrain0 = terrain0;
                half3 resolvedTerrain1 = terrain1;
                half3 resolvedTerrain2 = terrain2;
                half3 resolvedTerrain3 = terrain3;

                #if defined(_SLG_VARIANTS) || defined(_SLG_BOTTOM_LOD)
                    // One coherent LOD evaluation is shared by variant sampling and
                    // the optional final fade to Bottom.
                    TerrainLodState terrainLod =
                        EvaluateTerrainLod(input.positionWS);
                #endif

                half bottomLodStrength = 1.0h;
                #if defined(_SLG_BOTTOM_LOD)
                    bottomLodStrength = DistanceFade(
                        terrainLod.metric,
                        terrainLod.bottomStart,
                        terrainLod.bottomEnd);
                #endif

                half variantLodProgress = 0.0h;
                half4 variantNoise =
                    half4(0.5h, 0.5h, 0.5h, 0.5h);
                #if defined(_SLG_VARIANTS)
                    variantLodProgress = saturate(
                        terrainLod.noiseLod * 0.5h);
                    half variantStrength = lerp(
                        1.0h,
                        (half)_FarVariantStrength,
                        variantLodProgress) * bottomLodStrength;
                    [branch]
                    if (bottomLodStrength > 0.001h)
                    {
                        variantNoise = SampleVariantNoise(
                            terrainUV,
                            terrainLod);
                    }

                    // This branch is coherent for a top-down camera and skips the entire
                    // secondary layer after the final Bottom fade.
                    [branch]
                    if (variantStrength > 0.001h)
                    {
                        half4 variantMask = BuildVariantBlendMask(
                            variantNoise,
                            variantLodProgress,
                            variantStrength);

                        // Fixed sampling keeps the cost and appearance predictable on
                        // mobile GPUs. Per-pixel texture branches are intentionally
                        // avoided here.
                        half3 variant0 = SAMPLE_TEXTURE2D(
                            _Terrain0Variant, sampler_Terrain0Variant,
                            TransformTerrainUV(
                                terrainUV,
                                _Terrain0Variant_ST)).rgb * _Terrain0Tint.rgb;
                        half3 variant1 = SAMPLE_TEXTURE2D(
                            _Terrain1Variant, sampler_Terrain1Variant,
                            TransformTerrainUV(
                                terrainUV,
                                _Terrain1Variant_ST)).rgb * _Terrain1Tint.rgb;
                        half3 variant2 = SAMPLE_TEXTURE2D(
                            _Terrain2Variant, sampler_Terrain2Variant,
                            TransformTerrainUV(
                                terrainUV,
                                _Terrain2Variant_ST)).rgb * _Terrain2Tint.rgb;
                        half3 variant3 = SAMPLE_TEXTURE2D(
                            _Terrain3Variant, sampler_Terrain3Variant,
                            TransformTerrainUV(
                                terrainUV,
                                _Terrain3Variant_ST)).rgb * _Terrain3Tint.rgb;

                        // Resolve each terrain's primary/secondary pair before the
                        // four painted terrain weights are applied.
                        resolvedTerrain0 = lerp(
                            terrain0, variant0, variantMask.r);
                        resolvedTerrain1 = lerp(
                            terrain1, variant1, variantMask.g);
                        resolvedTerrain2 = lerp(
                            terrain2, variant2, variantMask.b);
                        resolvedTerrain3 = lerp(
                            terrain3, variant3, variantMask.a);
                    }
                #endif

                half bottomUvDetail = 0.0h;
                #if defined(_SLG_VARIANTS)
                    bottomUvDetail = bottomLodStrength;
                #endif
                half3 bottomAlbedo = SAMPLE_TEXTURE2D(
                    _BottomBase,
                    sampler_BottomBase,
                    BuildBottomUV(
                        terrainUV,
                        variantNoise,
                        bottomUvDetail)).rgb *
                    _BottomTint.rgb;

                half3 terrainAlbedo =
                    resolvedTerrain0 * weights.r +
                    resolvedTerrain1 * weights.g +
                    resolvedTerrain2 * weights.b +
                    resolvedTerrain3 * weights.a;

                half terrainLodCoverage = terrainCoverage;
                #if defined(_SLG_BOTTOM_LOD)
                    // Optionally use the bottom texture as the far LOD for painted
                    // terrain. Black control pixels always remain on the bottom layer.
                    half resolvedBottomStrength = bottomLodStrength;
                    #if defined(_SLG_VARIANTS)
                        half terrainNoise = dot(variantNoise, weights);
                        half bottomBlend = BuildNoiseDissolve(
                            terrainNoise,
                            1.0h - bottomLodStrength,
                            (half)_BottomDissolveSoftness);
                        resolvedBottomStrength = 1.0h - bottomBlend;
                    #endif
                    terrainLodCoverage *= resolvedBottomStrength;
                #endif
                half3 albedo = lerp(
                    bottomAlbedo, terrainAlbedo, terrainLodCoverage);

                albedo *= _GlobalTint.rgb;
                albedo = ApplySaturation(albedo, _Saturation);

                half3 normalWS = normalize(input.normalWS);
                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                half nDotL = saturate(dot(normalWS, mainLight.direction));
                half3 ambientLighting = max(SampleSH(normalWS), 0.0h);
                half3 directLighting = mainLight.color *
                    (nDotL * mainLight.distanceAttenuation *
                    mainLight.shadowAttenuation);
                albedo *= ambientLighting + directLighting;

                albedo = MixFog(albedo, input.fogFactor);
                return half4(albedo, 1.0h);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
