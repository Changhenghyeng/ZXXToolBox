Shader "Custom/Wall World Variation (URP)"
{
    Properties
    {

        [Header(UV Surface)]
        [MainColor] _BaseColor ("Base Color", Color) = (1,1,1,1)
        [MainTexture] _BaseMap ("Base Map (Model UV)", 2D) = "white" {}
        _Metallic ("Metallic", Range(0,1)) = 0
        _Smoothness ("Smoothness", Range(0,1)) = 0.35

        [Header(Horizontal World Pattern)]
        [NoScaleOffset] _PatternMap ("Pattern Map (Linear Grayscale, Horizontal Repeat)", 2D) = "gray" {}
        _PatternWorldTiling ("Pattern Horizontal Tiling (Per Meter)", Range(0.001,1)) = 0.053
        _PatternVerticalTiling ("Pattern Vertical Tiling", Float) = 1
        _PatternOffset ("Pattern Horizontal Offset", Float) = 0
        _PatternStrength ("Pattern Strength", Range(0,1)) = 1
        [Normal][NoScaleOffset] _PatternNormalMap ("Pattern Normal (Same Coordinates)", 2D) = "bump" {}
        _PatternNormalScale ("Pattern Normal Scale", Range(0,2)) = 1

        [Header(World Projection)]
        [ToggleUI] _ProjectionX ("Project X (YZ Plane)", Float) = 1
        [ToggleUI] _ProjectionY ("Project Y (Top Down / XZ Plane)", Float) = 0
        [ToggleUI] _ProjectionZ ("Project Z (XY Plane)", Float) = 1

        [Header(Rendering)]
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
        }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
        TEXTURE2D(_PatternMap); SAMPLER(sampler_PatternMap);
        TEXTURE2D(_PatternNormalMap); SAMPLER(sampler_PatternNormalMap);

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half _Metallic;
            half _Smoothness;
            float _PatternWorldTiling;
            float _PatternVerticalTiling;
            float _PatternOffset;
            half _PatternStrength;
            half _PatternNormalScale;
            half _ProjectionX;
            half _ProjectionY;
            half _ProjectionZ;
            half _Cull;
        CBUFFER_END

        half WallCoverage(float2 uv)
        {
            half alpha = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, TRANSFORM_TEX(uv, _BaseMap)).a;
            return saturate(alpha * _BaseColor.a);
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            ZTest LEqual
            Cull [_Cull]
            ZWrite Off

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex WallVertex
            #pragma fragment WallFragment
            #define _SURFACE_TYPE_TRANSPARENT 1
            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                half3 normalOS : NORMAL;
                half4 tangentOS : TANGENT;
                float2 uv : TEXCOORD0;
                float2 staticLightmapUV : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float2 uv : TEXCOORD1;
                half3 normalWS : TEXCOORD2;
                half3 tangentWS : TEXCOORD3;
                half3 bitangentWS : TEXCOORD4;
                DECLARE_LIGHTMAP_OR_SH(staticLightmapUV, vertexSH, 5);
                half fogFactor : TEXCOORD6;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings WallVertex(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS, input.tangentOS);
                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.uv = input.uv;
                output.normalWS = normalInputs.normalWS;
                output.tangentWS = normalInputs.tangentWS;
                output.bitangentWS = normalInputs.bitangentWS;
                OUTPUT_LIGHTMAP_UV(input.staticLightmapUV, unity_LightmapST, output.staticLightmapUV);
                OUTPUT_SH(output.normalWS, output.vertexSH);
                output.fogFactor = ComputeFogFactor(positionInputs.positionCS.z);
                return output;
            }

            void SampleProjectedPattern(float2 uv, half3 axisU, half3 axisV,
                half3 surfaceNormal, out half pattern, out half3 projectedNormal)
            {
                pattern = SAMPLE_TEXTURE2D(_PatternMap, sampler_PatternMap, uv).r;
                half3 detailTS = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_PatternNormalMap, sampler_PatternNormalMap, uv),
                    _PatternNormalScale);

                // Orient normal-map detail to the projection plane, then the actual surface.
                // A flat normal map must preserve the mesh normal for every axis combination.
                half3 detailWS = detailTS.x * axisU + detailTS.y * axisV;
                detailWS -= surfaceNormal * dot(surfaceNormal, detailWS);
                projectedNormal = normalize(surfaceNormal * max(detailTS.z, 0.001h) + detailWS);
            }

            half4 WallFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                float2 baseUV = TRANSFORM_TEX(input.uv, _BaseMap);
                // Preserve the original atlas alignment at vertical tiling = 1.
                float patternV = baseUV.y * max(_PatternVerticalTiling, 0.001);
                float topPatternV = input.positionWS.z * max(_PatternVerticalTiling, 0.001);
                half4 baseSample = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, baseUV);
                half opacity = saturate(baseSample.a * _BaseColor.a);
                // Fully invisible pixels are discarded; all visible coverage uses alpha blending.
                clip(opacity <= 0.0h ? -1.0h : 1.0h);

                half3x3 tangentToWorld = half3x3(
                    normalize(input.tangentWS),
                    normalize(input.bitangentWS),
                    normalize(input.normalWS));
                half3 enabledAxes = step(0.5h, half3(_ProjectionX, _ProjectionY, _ProjectionZ));
                half axisCount = dot(enabledAxes, half3(1, 1, 1));
                half patternEncoded;
                half3 normalTS;
                half3 normalWS;

                if (axisCount < 0.5h)
                {
                    patternEncoded = 0.5h;
                    normalTS = half3(0, 0, 1);
                    normalWS = normalize(input.normalWS);
                }
                else if (enabledAxes.x > 0.5h && enabledAxes.z > 0.5h)
                {
                    // X + Z is exactly the original mapping, including its tangent-space normal.
                    float wallDistance = dot(input.positionWS, normalize(input.tangentWS));
                    float2 patternUV = float2(wallDistance * _PatternWorldTiling + _PatternOffset, patternV);
                    patternEncoded = SAMPLE_TEXTURE2D(_PatternMap, sampler_PatternMap, patternUV).r;
                    normalTS = UnpackNormalScale(
                        SAMPLE_TEXTURE2D(_PatternNormalMap, sampler_PatternNormalMap, patternUV),
                        _PatternNormalScale);
                    normalWS = NormalizeNormalPerPixel(TransformTangentToWorld(normalTS, tangentToWorld));
                    if (enabledAxes.y > 0.5h)
                    {
                        half3 surfaceNormal = normalize(input.normalWS);
                        half ySign = surfaceNormal.y < 0.0h ? -1.0h : 1.0h;
                        float3 position = input.positionWS * _PatternWorldTiling;
                        half topPattern;
                        half3 topNormal;
                        SampleProjectedPattern(float2(position.x + _PatternOffset, -topPatternV * ySign),
                            half3(1, 0, 0), half3(0, 0, -ySign), surfaceNormal, topPattern, topNormal);
                        float3 weights = abs(surfaceNormal);
                        weights *= weights;
                        weights *= weights;
                        half topWeight = weights.y / max(weights.x + weights.y + weights.z, 0.00001);
                        patternEncoded = lerp(patternEncoded, topPattern, topWeight);
                        normalWS = NormalizeNormalPerPixel(lerp(normalWS, topNormal, topWeight));
                        normalTS = half3(dot(normalWS, tangentToWorld[0]),
                            dot(normalWS, tangentToWorld[1]), dot(normalWS, tangentToWorld[2]));
                    }
                }
                else
                {
                    half3 surfaceNormal = normalize(input.normalWS);
                    float3 weights = abs(surfaceNormal);
                    weights *= weights;
                    weights *= weights;
                    weights *= enabledAxes;
                    float weightSum = weights.x + weights.y + weights.z;
                    // A single enabled projection also works on faces parallel to its direction.
                    weights = weightSum > 0.00001 ? weights / max(weightSum, 0.00001)
                        : (float3)enabledAxes / axisCount;
                    half3 axisSign = step(0.0h, surfaceNormal) * 2.0h - 1.0h;
                    // Side projections retain the original atlas V and mesh UV orientation.
                    half sideUFromZ = input.tangentWS.z < 0.0h ? -1.0h : 1.0h;
                    half sideUFromX = input.tangentWS.x < 0.0h ? -1.0h : 1.0h;
                    float3 position = input.positionWS * _PatternWorldTiling;
                    float2 offset = float2(_PatternOffset, 0);
                    patternEncoded = 0;
                    normalWS = 0;
                    half axisPattern;
                    half3 axisNormal;

                    if (enabledAxes.x > 0.5h)
                    {
                        SampleProjectedPattern(float2(position.z * sideUFromZ, patternV) + offset,
                            half3(0, 0, sideUFromZ), normalize(input.bitangentWS), surfaceNormal,
                            axisPattern, axisNormal);
                        patternEncoded += axisPattern * weights.x;
                        normalWS += axisNormal * weights.x;
                    }
                    if (enabledAxes.y > 0.5h)
                    {
                        SampleProjectedPattern(float2(position.x, -topPatternV * axisSign.y) + offset,
                            half3(1, 0, 0), half3(0, 0, -axisSign.y), surfaceNormal,
                            axisPattern, axisNormal);
                        patternEncoded += axisPattern * weights.y;
                        normalWS += axisNormal * weights.y;
                    }
                    if (enabledAxes.z > 0.5h)
                    {
                        SampleProjectedPattern(float2(position.x * sideUFromX, patternV) + offset,
                            half3(sideUFromX, 0, 0), normalize(input.bitangentWS), surfaceNormal,
                            axisPattern, axisNormal);
                        patternEncoded += axisPattern * weights.z;
                        normalWS += axisNormal * weights.z;
                    }
                    normalWS = NormalizeNormalPerPixel(normalWS);
                    normalTS = half3(dot(normalWS, tangentToWorld[0]),
                        dot(normalWS, tangentToWorld[1]), dot(normalWS, tangentToWorld[2]));
                }
                half patternMultiplier = max(patternEncoded * 2.0h, 0.0h);

                half3 albedo = baseSample.rgb * _BaseColor.rgb;
                albedo *= lerp(1.0h, patternMultiplier, _PatternStrength);

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = albedo;
                surfaceData.metallic = _Metallic;
                surfaceData.specular = 0.0h.xxx;
                surfaceData.smoothness = _Smoothness;
                surfaceData.normalTS = normalTS;
                surfaceData.occlusion = 1.0h;
                surfaceData.emission = 0.0h.xxx;
                surfaceData.alpha = opacity;
                surfaceData.clearCoatMask = 0;
                surfaceData.clearCoatSmoothness = 0;

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.positionCS = input.positionCS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                inputData.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                inputData.fogCoord = input.fogFactor;
                inputData.vertexLighting = VertexLighting(input.positionWS, normalWS);
                inputData.bakedGI = SAMPLE_GI(input.staticLightmapUV, input.vertexSH, normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = SAMPLE_SHADOWMASK(input.staticLightmapUV);

                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                color.rgb = MixFog(color.rgb, inputData.fogCoord);
                color.a = surfaceData.alpha;
                return color;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            Cull [_Cull]
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex ShadowVertex
            #pragma fragment ShadowFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            // URP 14.2.0-t1 Shadows.hlsl calls LerpWhiteTo, which is declared here
            // but is not pulled in by Core.hlsl for a standalone ShadowCaster pass.
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct ShadowAttributes
            {
                float4 positionOS : POSITION;
                half3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            ShadowVaryings ShadowVertex(ShadowAttributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                ShadowVaryings output;
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                output.uv = input.uv;
                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                half3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                float3 lightDirectionWS = _LightDirection;
                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    lightDirectionWS = normalize(_LightPosition - positionInputs.positionWS);
                #endif
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionInputs.positionWS, normalWS, lightDirectionWS));
                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                output.positionCS = positionCS;
                return output;
            }

            half4 ShadowFragment(ShadowVaryings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                clip(WallCoverage(input.uv) <= 0.0h ? -1.0h : 1.0h);
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            Cull [_Cull]
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex DepthVertex
            #pragma fragment DepthFragment
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct DepthAttributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct DepthVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            DepthVaryings DepthVertex(DepthAttributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                DepthVaryings output;
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                output.uv = input.uv;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }
            half4 DepthFragment(DepthVaryings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                clip(WallCoverage(input.uv) <= 0.0h ? -1.0h : 1.0h);
                return input.positionCS.z;
            }
            ENDHLSL
        }

        // Reuse URP Lit's bake pass so the UV base color contributes to lightmaps.
        UsePass "Universal Render Pipeline/Lit/Meta"
    }

    CustomEditor "X26Art.Editor.WallWorldVariationShaderGUI"
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
