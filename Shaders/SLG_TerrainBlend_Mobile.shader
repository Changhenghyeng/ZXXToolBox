Shader "Custom/Terrain Blend Mobile"
{
    Properties
    {
        [Header(Terrain Control)]
        [NoScaleOffset] _ControlMap("Control Map (RGBA = Type A-D)", 2D) = "red" {}
        _ControlSharpness("Control Edge Sharpness", Range(0.5, 4.0)) = 1.0

        [Header(Terrain Texture Array)]
        [NoScaleOffset] _TerrainArray("Terrain Array (Base 0-3, Variant 4-7)", 2DArray) = "" {}
        _BaseST0("Type A Base Tiling Offset", Vector) = (1, 1, 0, 0)
        _BaseST1("Type B Base Tiling Offset", Vector) = (1, 1, 0, 0)
        _BaseST2("Type C Base Tiling Offset", Vector) = (1, 1, 0, 0)
        _BaseST3("Type D Base Tiling Offset", Vector) = (1, 1, 0, 0)
        _VariantST0("Type A Variant Tiling Offset", Vector) = (1, 1, 0, 0)
        _VariantST1("Type B Variant Tiling Offset", Vector) = (1, 1, 0, 0)
        _VariantST2("Type C Variant Tiling Offset", Vector) = (1, 1, 0, 0)
        _VariantST3("Type D Variant Tiling Offset", Vector) = (1, 1, 0, 0)

        [Header(Terrain Appearance)]
        _Terrain0Tint("Type A Tint", Color) = (1, 1, 1, 1)
        _Terrain1Tint("Type B Tint", Color) = (1, 1, 1, 1)
        _Terrain2Tint("Type C Tint", Color) = (1, 1, 1, 1)
        _Terrain3Tint("Type D Tint", Color) = (1, 1, 1, 1)
        _Terrain0HueShift("Type A Hue Shift", Range(-180, 180)) = 0
        _Terrain1HueShift("Type B Hue Shift", Range(-180, 180)) = 0
        _Terrain2HueShift("Type C Hue Shift", Range(-180, 180)) = 0
        _Terrain3HueShift("Type D Hue Shift", Range(-180, 180)) = 0
        _Terrain0Saturation("Type A Saturation", Range(0, 2)) = 1
        _Terrain1Saturation("Type B Saturation", Range(0, 2)) = 1
        _Terrain2Saturation("Type C Saturation", Range(0, 2)) = 1
        _Terrain3Saturation("Type D Saturation", Range(0, 2)) = 1

        [Header(Variant)]
        [Toggle(_SLG_VARIANTS)] _UseVariants("Enable Variant Textures", Float) = 1
        [NoScaleOffset] _PackedNoise("Noise LOD Array", 2DArray) = "" {}
        _Terrain0VariantRatio("Type A Variant Coverage", Range(0, 1)) = 0.5
        _Terrain1VariantRatio("Type B Variant Coverage", Range(0, 1)) = 0.5
        _Terrain2VariantRatio("Type C Variant Coverage", Range(0, 1)) = 0.5
        _Terrain3VariantRatio("Type D Variant Coverage", Range(0, 1)) = 0.5
        _VariantEdgeSoftness("Variant Edge Softness", Range(0.01, 0.49)) = 0.2
        _FarVariantStrength("Far Variant Strength", Range(0, 1)) = 0.35
        [HideInInspector] _VariantSoftness("Legacy Variant Softness", Vector) = (0.2, 0.2, 0.2, 0.2)
        [HideInInspector] _NoiseCorrelation01("Noise Correlation 0-1", Float) = 0.976
        [HideInInspector] _NoiseCorrelation12("Noise Correlation 1-2", Float) = 0.894

        [Header(Bottom And Far Basemap)]
        [Toggle(_SLG_BOTTOM_LOD)] _UseBottomLod("Bottom Participates In LOD3", Float) = 0
        _BottomBase("Bottom Base", 2D) = "black" {}
        _BottomTint("Bottom Tint", Color) = (1, 1, 1, 1)
        _BottomDissolveSoftness("Bottom Dissolve Softness", Range(0.01, 0.49)) = 0.15
        _BottomUvDistortion("Bottom UV Distortion Strength", Range(0, 1)) = 0.3
        [NoScaleOffset] _FarBasemap("LOD3 Far Basemap", 2D) = "black" {}

        [Header(Orthographic LOD)]
        _LodNearToMiddleAt("LOD0 To LOD1 At (Camera Size)", Float) = 30
        _LodMiddleToFarAt("LOD1 To LOD2 At (Camera Size)", Float) = 45
        _LodFarCompleteAt("LOD3 Complete At (Camera Size)", Float) = 60
        _Lod3ToBottomRange("LOD3 To Bottom Range", Float) = 12
        _LodTransitionRatio("LOD Transition Smoothness", Range(0.01, 1.0)) = 0.2

        [Header(Lighting)]
        [Toggle(_SLG_UNLIT)] _Unlit("Unlit (Ignore Lighting And Shadows)", Float) = 0

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
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag

            #pragma shader_feature_local_fragment _SLG_UNLIT
            #pragma shader_feature_local_fragment _SLG_VARIANTS
            #pragma shader_feature_local_fragment _SLG_BOTTOM_LOD
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_ControlMap);
            SAMPLER(sampler_ControlMap);
            TEXTURE2D_ARRAY(_TerrainArray);
            SAMPLER(sampler_TerrainArray);
            TEXTURE2D_ARRAY(_PackedNoise);
            SAMPLER(sampler_PackedNoise);
            TEXTURE2D(_BottomBase);
            SAMPLER(sampler_BottomBase);
            TEXTURE2D(_FarBasemap);
            SAMPLER(sampler_FarBasemap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BottomBase_ST;
                float4 _BaseST0;
                float4 _BaseST1;
                float4 _BaseST2;
                float4 _BaseST3;
                float4 _VariantST0;
                float4 _VariantST1;
                float4 _VariantST2;
                float4 _VariantST3;
                half4 _Terrain0Tint;
                half4 _Terrain1Tint;
                half4 _Terrain2Tint;
                half4 _Terrain3Tint;
                float _Terrain0HueShift;
                float _Terrain1HueShift;
                float _Terrain2HueShift;
                float _Terrain3HueShift;
                float _Terrain0Saturation;
                float _Terrain1Saturation;
                float _Terrain2Saturation;
                float _Terrain3Saturation;
                half4 _BottomTint;
                half4 _GlobalTint;
                float _ControlSharpness;
                float _Terrain0VariantRatio;
                float _Terrain1VariantRatio;
                float _Terrain2VariantRatio;
                float _Terrain3VariantRatio;
                float _VariantEdgeSoftness;
                float _FarVariantStrength;
                float _BottomDissolveSoftness;
                float _BottomUvDistortion;
                float _NoiseCorrelation01;
                float _NoiseCorrelation12;
                float _LodNearToMiddleAt;
                float _LodMiddleToFarAt;
                float _LodFarCompleteAt;
                float _Lod3ToBottomRange;
                float _LodTransitionRatio;
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

            struct TerrainLodState
            {
                float metric;
                float noiseMip;
                half farBlend;
            };

            struct TopTwoTerrain
            {
                int firstId;
                int secondId;
                half secondBlend;
                half coverage;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                VertexPositionInputs positionInputs =
                    GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv = input.uv;
                output.fogFactor =
                    ComputeFogFactor(positionInputs.positionCS.z);
                return output;
            }

            half SmoothRange(float value, float start, float end)
            {
                return smoothstep(start, max(end, start + 0.001), value);
            }

            float GetLodMetric(float3 positionWS)
            {
                // Unity Camera 面板里的 Orthographic Size 是可视高度的一半。
                float metric = max(unity_OrthoParams.y * 0.5, 0.001);
                [branch]
                if (unity_OrthoParams.w <= 0.5)
                {
                    metric = distance(_WorldSpaceCameraPos, positionWS);
                }
                return metric;
            }

            TerrainLodState EvaluateTerrainLod(float3 positionWS)
            {
                TerrainLodState lod = (TerrainLodState)0;
                float transitionRatio =
                    clamp(_LodTransitionRatio, 0.001, 3.0);
                float nearCenter = max(_LodNearToMiddleAt, 0.0);
                float middleCenter = max(
                    _LodMiddleToFarAt,
                    nearCenter + 0.001);
                float farEnd = max(
                    _LodFarCompleteAt,
                    middleCenter + 0.001);

                float nearWidth = max(
                    (middleCenter - nearCenter) * transitionRatio,
                    0.001);
                float middleWidth = max(
                    (farEnd - middleCenter) * transitionRatio,
                    0.001);
                float nearStart = max(
                    nearCenter - nearWidth * 0.5,
                    0.0);
                float nearEnd = nearCenter + nearWidth * 0.5;
                float middleStart = max(
                    middleCenter - middleWidth * 0.5,
                    nearEnd);
                float middleEnd = max(
                    middleCenter + middleWidth * 0.5,
                    middleStart + 0.001);
                float farStart = max(
                    farEnd - max(_Lod3ToBottomRange, 0.001),
                    0.0);

                lod.metric = GetLodMetric(positionWS);
                lod.noiseMip =
                    (float)SmoothRange(lod.metric, nearStart, nearEnd) +
                    (float)SmoothRange(
                        lod.metric,
                        middleStart,
                        middleEnd);
                lod.farBlend = SmoothRange(
                    lod.metric,
                    farStart,
                    farEnd);
                return lod;
            }

            void ConsiderTerrain(
                half weight,
                int id,
                inout half firstWeight,
                inout int firstId,
                inout half secondWeight,
                inout int secondId)
            {
                if (weight > firstWeight)
                {
                    secondWeight = firstWeight;
                    secondId = firstId;
                    firstWeight = weight;
                    firstId = id;
                }
                else if (weight > secondWeight)
                {
                    secondWeight = weight;
                    secondId = id;
                }
            }

            TopTwoTerrain ResolveTopTwo(half4 rawWeights)
            {
                TopTwoTerrain result = (TopTwoTerrain)0;
                rawWeights = saturate(rawWeights);
                result.coverage = saturate(dot(
                    rawWeights,
                    half4(1, 1, 1, 1)));

                [branch]
                if (_ControlSharpness != 1.0)
                {
                    rawWeights = pow(rawWeights, _ControlSharpness);
                }

                half firstWeight = -1.0h;
                half secondWeight = -1.0h;
                int firstId = 0;
                int secondId = 0;
                ConsiderTerrain(
                    rawWeights.r,
                    0,
                    firstWeight,
                    firstId,
                    secondWeight,
                    secondId);
                ConsiderTerrain(
                    rawWeights.g,
                    1,
                    firstWeight,
                    firstId,
                    secondWeight,
                    secondId);
                ConsiderTerrain(
                    rawWeights.b,
                    2,
                    firstWeight,
                    firstId,
                    secondWeight,
                    secondId);
                ConsiderTerrain(
                    rawWeights.a,
                    3,
                    firstWeight,
                    firstId,
                    secondWeight,
                    secondId);

                half pairSum = max(firstWeight + secondWeight, 0.0001h);
                result.firstId = firstId;
                result.secondId = secondId;
                result.secondBlend = saturate(secondWeight / pairSum);
                return result;
            }

            float4 SelectBaseST(int id)
            {
                if (id == 0) return _BaseST0;
                if (id == 1) return _BaseST1;
                if (id == 2) return _BaseST2;
                return _BaseST3;
            }

            float4 SelectVariantST(int id)
            {
                if (id == 0) return _VariantST0;
                if (id == 1) return _VariantST1;
                if (id == 2) return _VariantST2;
                return _VariantST3;
            }

            half3 SelectTint(int id)
            {
                if (id == 0) return _Terrain0Tint.rgb;
                if (id == 1) return _Terrain1Tint.rgb;
                if (id == 2) return _Terrain2Tint.rgb;
                return _Terrain3Tint.rgb;
            }

            half SelectComponent(half4 value, int id)
            {
                if (id == 0) return value.r;
                if (id == 1) return value.g;
                if (id == 2) return value.b;
                return value.a;
            }

            half SelectVariantRatio(int id)
            {
                if (id == 0) return (half)_Terrain0VariantRatio;
                if (id == 1) return (half)_Terrain1VariantRatio;
                if (id == 2) return (half)_Terrain2VariantRatio;
                return (half)_Terrain3VariantRatio;
            }

            half SelectTerrainSaturation(int id)
            {
                if (id == 0) return (half)_Terrain0Saturation;
                if (id == 1) return (half)_Terrain1Saturation;
                if (id == 2) return (half)_Terrain2Saturation;
                return (half)_Terrain3Saturation;
            }

            half SelectTerrainHueShift(int id)
            {
                if (id == 0) return (half)_Terrain0HueShift;
                if (id == 1) return (half)_Terrain1HueShift;
                if (id == 2) return (half)_Terrain2HueShift;
                return (half)_Terrain3HueShift;
            }

            half4 CompensatePackedNoise(
                half4 packedNoise,
                float noiseMip)
            {
                half mipBlend = (half)frac(noiseMip);
                half correlation = noiseMip < 1.0
                    ? (half)_NoiseCorrelation01
                    : (half)_NoiseCorrelation12;
                half inverseBlend = 1.0h - mipBlend;
                half variance = max(
                    inverseBlend * inverseBlend +
                    mipBlend * mipBlend +
                    2.0h * correlation *
                    inverseBlend * mipBlend,
                    0.001h);
                return saturate(
                    0.5h +
                    (packedNoise - 0.5h) * rsqrt(variance));
            }

            half4 SampleNoiseArray(
                float2 mapUV,
                float noiseLod)
            {
                float clampedLod = clamp(noiseLod, 0.0, 2.0);
                int lowerLayer = (int)floor(clampedLod);
                int upperLayer = min(lowerLayer + 1, 2);
                half layerBlend = (half)frac(clampedLod);
                half4 result = SAMPLE_TEXTURE2D_ARRAY(
                    _PackedNoise,
                    sampler_PackedNoise,
                    mapUV,
                    lowerLayer);

                [branch]
                if (layerBlend > 0.001h)
                {
                    half4 upperNoise = SAMPLE_TEXTURE2D_ARRAY(
                        _PackedNoise,
                        sampler_PackedNoise,
                        mapUV,
                        upperLayer);
                    result = CompensatePackedNoise(
                        lerp(result, upperNoise, layerBlend),
                        clampedLod);
                }

                return result;
            }

            half GetVariantSoftness(half lodProgress)
            {
                half nearSoftness = clamp(
                    (half)_VariantEdgeSoftness,
                    0.001h,
                    0.499h);
                return lerp(
                    nearSoftness,
                    max(nearSoftness * 0.35h, 0.001h),
                    lodProgress);
            }

            half SampleVariantMask(
                half4 packedNoise,
                half4 noiseFilterWidth,
                int id,
                half artistSoftness)
            {
                half noise = SelectComponent(packedNoise, id);
                half softness = max(
                    artistSoftness,
                    SelectComponent(noiseFilterWidth, id));
                half ratio = saturate(SelectVariantRatio(id));
                half threshold = 1.0h - ratio;
                half mask = smoothstep(
                    threshold - softness,
                    threshold + softness,
                    noise);
                mask *= step(0.0001h, ratio);
                mask = lerp(
                    mask,
                    1.0h,
                    step(0.9999h, ratio));
                return mask;
            }

            half BuildNoiseDissolve(
                half noise,
                half transition,
                half softness,
                half filterWidth)
            {
                half safeSoftness = clamp(
                    max(
                        softness,
                        filterWidth),
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

            half3 SampleTerrainArray(
                float2 mapUV,
                int terrainId,
                bool variant)
            {
                float4 st = variant
                    ? SelectVariantST(terrainId)
                    : SelectBaseST(terrainId);
                float2 uv = mapUV * st.xy + st.zw;
                float2 uvDx = ddx(mapUV) * st.xy;
                float2 uvDy = ddy(mapUV) * st.xy;
                int slice = terrainId + (variant ? 4 : 0);
                return SAMPLE_TEXTURE2D_ARRAY_GRAD(
                    _TerrainArray,
                    sampler_TerrainArray,
                    uv,
                    slice,
                    uvDx,
                    uvDy).rgb * SelectTint(terrainId);
            }

            half3 ResolveTerrain(
                float2 mapUV,
                int terrainId,
                half4 packedNoise,
                half4 noiseFilterWidth,
                half variantVisibility,
                half variantSoftness)
            {
                half3 resolvedColor = SampleTerrainArray(
                    mapUV,
                    terrainId,
                    false);

                #if defined(_SLG_VARIANTS)
                    [branch]
                    if (variantVisibility > 0.001h)
                    {
                        half3 variantColor = SampleTerrainArray(
                            mapUV,
                            terrainId,
                            true);
                        half variantMask =
                            SampleVariantMask(
                                packedNoise,
                                noiseFilterWidth,
                                terrainId,
                                variantSoftness) *
                            variantVisibility;
                        resolvedColor = lerp(
                            resolvedColor,
                            variantColor,
                            variantMask);
                    }
                #endif

                return resolvedColor;
            }

            float2 BuildBottomUV(
                float2 mapUV,
                half4 packedNoise,
                half detailAmount)
            {
                half2 distortion = half2(
                    packedNoise.r - packedNoise.b,
                    packedNoise.g - packedNoise.a);
                return mapUV * _BottomBase_ST.xy +
                    _BottomBase_ST.zw +
                    (float2)distortion *
                    (_BottomUvDistortion * 0.1) *
                    detailAmount;
            }

            half3 SampleBottom(
                float2 mapUV,
                half4 packedNoise,
                half detailAmount)
            {
                return SAMPLE_TEXTURE2D(
                    _BottomBase,
                    sampler_BottomBase,
                    BuildBottomUV(
                        mapUV,
                        packedNoise,
                        detailAmount)).rgb *
                    _BottomTint.rgb;
            }

            half3 SampleFarBasemap(float2 mapUV)
            {
                return SAMPLE_TEXTURE2D(
                    _FarBasemap,
                    sampler_FarBasemap,
                    mapUV).rgb;
            }

            half3 SampleLod3Target(
                float2 mapUV,
                half4 packedNoise,
                half detailAmount)
            {
                #if defined(_SLG_BOTTOM_LOD)
                    return SampleBottom(
                        mapUV,
                        packedNoise,
                        detailAmount);
                #else
                    return SampleFarBasemap(mapUV);
                #endif
            }

            half3 ApplySaturation(half3 color, half saturation)
            {
                half3 result = color;
                [branch]
                if (saturation != 1.0h)
                {
                    half luminance = dot(
                        color,
                        half3(0.2126h, 0.7152h, 0.0722h));
                    result = lerp(
                        luminance.xxx,
                        color,
                        saturation);
                }

                return result;
            }

            half3 ApplyHueShift(half3 color, half hueShiftDegrees)
            {
                half3 result = color;
                [branch]
                if (abs(hueShiftDegrees) >= 0.001h)
                {
                    const half4 k = half4(
                        0.0h,
                        -0.333333h,
                        0.666667h,
                        -1.0h);
                    half4 p = lerp(
                        half4(color.bg, k.wz),
                        half4(color.gb, k.xy),
                        step(color.b, color.g));
                    half4 q = lerp(
                        half4(p.xyw, color.r),
                        half4(color.r, p.yzx),
                        step(p.x, color.r));
                    half delta = q.x - min(q.w, q.y);
                    half epsilon = 0.0001h;
                    half3 hsv = half3(
                        abs(q.z + (q.w - q.y) /
                            (6.0h * delta + epsilon)),
                        delta / (q.x + epsilon),
                        q.x);
                    hsv.x = frac(
                        hsv.x + hueShiftDegrees / 360.0h);

                    const half4 hsvToRgbK = half4(
                        1.0h,
                        0.666667h,
                        0.333333h,
                        3.0h);
                    half3 rgb = saturate(
                        abs(
                            frac(hsv.xxx + hsvToRgbK.xyz) * 6.0h -
                            hsvToRgbK.www) -
                        hsvToRgbK.xxx);
                    result = hsv.z * lerp(
                        hsvToRgbK.xxx,
                        rgb,
                        hsv.y);
                }

                return result;
            }

            half3 ApplyLighting(
                half3 albedo,
                half3 normalWS,
                float3 positionWS)
            {
                float4 shadowCoord =
                    TransformWorldToShadowCoord(positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                half nDotL = saturate(
                    dot(normalWS, mainLight.direction));
                half3 ambientLighting =
                    max(SampleSH(normalWS), 0.0h);
                half3 directLighting = mainLight.color *
                    (nDotL * mainLight.distanceAttenuation *
                    mainLight.shadowAttenuation);
                return albedo * (ambientLighting + directLighting);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                TerrainLodState lod =
                    EvaluateTerrainLod(input.positionWS);
                half3 albedo;

                // 正交摄像机下该分支全屏一致。完全进入LOD3后只采样远景图。
                [branch]
                if (lod.farBlend >= 0.999h)
                {
                    albedo = SampleLod3Target(
                        input.uv,
                        half4(0.5h, 0.5h, 0.5h, 0.5h),
                        0.0h);
                }
                else
                {
                    half4 rawControl = SAMPLE_TEXTURE2D(
                        _ControlMap,
                        sampler_ControlMap,
                        input.uv);
                    TopTwoTerrain terrain = ResolveTopTwo(rawControl);

                    half4 packedNoise = half4(0.5h, 0.5h, 0.5h, 0.5h);
                    half4 noiseFilterWidth =
                        half4(0.001h, 0.001h, 0.001h, 0.001h);
                    #if defined(_SLG_VARIANTS)
                        packedNoise = SampleNoiseArray(
                            input.uv,
                            lod.noiseMip);
                        noiseFilterWidth = max(
                            fwidth(packedNoise) * 0.5h,
                            0.001h);
                    #endif

                    half variantLodProgress = saturate(
                        (half)lod.noiseMip * 0.5h);
                    half variantSoftness = GetVariantSoftness(
                        variantLodProgress);
                    half variantVisibility = lerp(
                        1.0h,
                        (half)_FarVariantStrength,
                        variantLodProgress) * (1.0h - lod.farBlend);
                    half bottomUvDetail = 0.0h;
                    #if defined(_SLG_VARIANTS)
                        bottomUvDetail = 1.0h - lod.farBlend;
                    #endif
                    half3 firstColor = ResolveTerrain(
                        input.uv,
                        terrain.firstId,
                        packedNoise,
                        noiseFilterWidth,
                        variantVisibility,
                        variantSoftness);
                    firstColor = ApplySaturation(
                        firstColor,
                        SelectTerrainSaturation(terrain.firstId));
                    firstColor = ApplyHueShift(
                        firstColor,
                        SelectTerrainHueShift(terrain.firstId));
                    half3 secondColor = ResolveTerrain(
                        input.uv,
                        terrain.secondId,
                        packedNoise,
                        noiseFilterWidth,
                        variantVisibility,
                        variantSoftness);
                    secondColor = ApplySaturation(
                        secondColor,
                        SelectTerrainSaturation(terrain.secondId));
                    secondColor = ApplyHueShift(
                        secondColor,
                        SelectTerrainHueShift(terrain.secondId));
                    half3 terrainColor = lerp(
                        firstColor,
                        secondColor,
                        terrain.secondBlend);
                    albedo = terrainColor;

                    // LOD0 近景只显示地表层，避免 Control Map 过滤产生的
                    // 微小覆盖缺口让 Bottom Base 漏出来。进入 LOD1 后再逐渐
                    // 恢复原有的 Bottom 覆盖关系。
                    half bottomCoverage =
                        (1.0h - terrain.coverage) *
                        saturate((half)lod.noiseMip);
                    [branch]
                    if (bottomCoverage > 0.001h)
                    {
                        albedo = lerp(
                            terrainColor,
                            SampleBottom(
                                input.uv,
                                packedNoise,
                                bottomUvDetail),
                            bottomCoverage);
                    }

                    [branch]
                    if (lod.farBlend > 0.001h)
                    {
                        half resolvedFarBlend = lod.farBlend;
                        #if defined(_SLG_VARIANTS) && defined(_SLG_BOTTOM_LOD)
                            half firstNoise = SelectComponent(
                                packedNoise,
                                terrain.firstId);
                            half secondNoise = SelectComponent(
                                packedNoise,
                                terrain.secondId);
                            half terrainNoise = lerp(
                                firstNoise,
                                secondNoise,
                                terrain.secondBlend);
                            half terrainFilterWidth = lerp(
                                SelectComponent(
                                    noiseFilterWidth,
                                    terrain.firstId),
                                SelectComponent(
                                    noiseFilterWidth,
                                    terrain.secondId),
                                terrain.secondBlend);
                            resolvedFarBlend = BuildNoiseDissolve(
                                terrainNoise,
                                lod.farBlend,
                                (half)_BottomDissolveSoftness,
                                terrainFilterWidth);
                        #endif
                        albedo = lerp(
                            albedo,
                            SampleLod3Target(
                                input.uv,
                                packedNoise,
                                bottomUvDetail),
                            resolvedFarBlend);
                    }
                }

                albedo *= _GlobalTint.rgb;
                albedo = ApplySaturation(albedo, _Saturation);
                #if !defined(_SLG_UNLIT)
                    albedo = ApplyLighting(
                        albedo,
                        normalize(input.normalWS),
                        input.positionWS);
                #endif
                albedo = MixFog(albedo, input.fogFactor);
                return half4(albedo, 1.0h);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
