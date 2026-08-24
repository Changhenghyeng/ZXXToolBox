Shader "Custom/ToonOutlinePacked"
{
    Properties
    {
        [Header(Surface)]
        [MainColor] _BaseColor ("Base Color", Color) = (1,1,1,1)
        [MainTexture] _BC ("Base Color (RGB, JPEG)", 2D) = "white" {}
        [NoScaleOffset] _N_M_R ("Packed N_M_R (RG = Normal XY, B = Metallic, A = Roughness)", 2D) = "white" {}
        _NormalScale ("Normal Scale", Range(0, 2)) = 1
        _Metallic ("Metallic Scale", Range(0, 1)) = 1
        _Smoothness ("Smoothness Scale", Range(0, 1)) = 1
        [Toggle] _KeepMetallic ("Keep Metallic In Toon", Float) = 0

        [Header(Toon Diffuse)]
        _MiddleColor ("Middle Color (A = Intensity)", Color) = (1,1,1,0.75)
        _MiddleRange ("Middle Range", Range(0, 1)) = 0.55
        _MiddleEdge ("Middle Edge", Range(0.001, 1)) = 0.12

        _DarkColor ("Dark Color (A = Intensity)", Color) = (0.35,0.42,0.55,0.75)
        _DarkRange ("Dark Range", Range(0, 1)) = 0.28
        _DarkEdge ("Dark Edge", Range(0.001, 1)) = 0.08

        [Header(Toon Specular)]
        _SpecularToon ("Specular Toon Strength", Range(0, 1)) = 0
        _SpecularThreshold ("Specular Range", Range(0, 1)) = 0.75
        _SpecularEdge ("Specular Softness", Range(0.001, 1)) = 0.08

        [Header(Toon Blend)]
        _ToonStrength ("Toon Strength", Range(0, 1)) = 0
        _AdditionalLightToonFill ("Additional Light Fill", Range(0, 1)) = 0.25

        [Header(Stylized Rim Light)]
        [HDR] _EnvColor ("Rim Color (A = Intensity)", Color) = (0.2,0.35,0.75,0)
        _EnvRange ("Rim Range", Range(0, 1)) = 0.28
        _EnvEdge  ("Rim Edge",  Range(0.001, 1)) = 0.08
        _EnvBias  ("Rim Direction Bias", Range(0, 1)) = 0
        _EnvYawOffset ("Rim Horizontal Offset", Range(-180, 180)) = 0
        _EnvPitchOffset ("Rim Vertical Offset", Range(-90, 90)) = 0

        [Header(Outline)]
        [Toggle(_OUTLINE_ON)] _UseOutline ("Use Outline", Float) = 1
        _OutlineColor ("Outline Color", Color) = (0,0,0,1)
        _OutlineWidth ("Outline Width", Range(0.001, 0.5)) = 0.02
        [Toggle(_SMOOTH_ON)] _Smooth ("Smooth Normals (baked in UV2)", Float) = 0
        _ZOffset ("Outline Z Offset", Float) = 0

        [Header(Rendering)]
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
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
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Cull [_Cull]
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _LIGHT_COOKIES

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BC);    SAMPLER(sampler_BC);
            TEXTURE2D(_N_M_R); SAMPLER(sampler_N_M_R);

            CBUFFER_START(UnityPerMaterial)
                float4 _BC_ST;
                half4 _BaseColor;
                half _NormalScale;
                half _Metallic;
                half _Smoothness;
                half _KeepMetallic;
                half4 _MiddleColor;
                half _MiddleRange;
                half _MiddleEdge;
                half4 _DarkColor;
                half _DarkRange;
                half _DarkEdge;
                half _SpecularToon;
                half _SpecularThreshold;
                half _SpecularEdge;
                half _ToonStrength;
                half _AdditionalLightToonFill;
                half4 _EnvColor;
                half _EnvRange;
                half _EnvEdge;
                half _EnvYawOffset;
                half _EnvPitchOffset;
                half _EnvBias;
                half _UseOutline;
                half4 _OutlineColor;
                float _OutlineWidth;
                float _ZOffset;
                half _Cull;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS  : TEXCOORD0;
                float2 uv          : TEXCOORD1;
                half3 normalWS     : TEXCOORD2;
                half3 tangentWS    : TEXCOORD3;
                half3 bitangentWS  : TEXCOORD4;
                float4 shadowCoord : TEXCOORD5;
                half fogCoord      : TEXCOORD6;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                VertexPositionInputs positionInputs = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(IN.normalOS, IN.tangentOS);

                OUT.positionHCS = positionInputs.positionCS;
                OUT.positionWS = positionInputs.positionWS;
                OUT.uv = IN.uv;
                OUT.normalWS = normalInputs.normalWS;
                OUT.tangentWS = normalInputs.tangentWS;
                OUT.bitangentWS = normalInputs.bitangentWS;
                OUT.shadowCoord = GetShadowCoord(positionInputs);
                OUT.fogCoord = ComputeFogFactor(positionInputs.positionCS.z);
                return OUT;
            }

            half3 SampleNormalTS(half4 packedNMR)
            {
                half2 normalXY = (packedNMR.rg * 2.0h - 1.0h) * _NormalScale;
                half normalZ = sqrt(saturate(1.0h - dot(normalXY, normalXY)));
                return half3(normalXY, normalZ);
            }

            half3 TransformNormalWS(half3 normalTS, half3x3 tangentToWorld)
            {
                return NormalizeNormalPerPixel(TransformTangentToWorld(normalTS, tangentToWorld));
            }

            SurfaceData BuildSurfaceData(half4 baseColor, half metallic, half smoothness, half3 normalTS)
            {
                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = baseColor.rgb;
                surfaceData.specular = 0.0h.xxx;
                surfaceData.metallic = saturate(metallic);
                surfaceData.smoothness = saturate(smoothness);
                surfaceData.normalTS = normalTS;
                surfaceData.emission = 0.0h.xxx;
                surfaceData.occlusion = 1.0h;
                surfaceData.alpha = baseColor.a;
                surfaceData.clearCoatMask = 0.0h;
                surfaceData.clearCoatSmoothness = 0.0h;
                return surfaceData;
            }

            InputData BuildInputData(Varyings IN, half3 normalWS, half3 viewDirWS, half3x3 tangentToWorld)
            {
                InputData inputData = (InputData)0;
                inputData.positionWS = IN.positionWS;
                inputData.positionCS = IN.positionHCS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = viewDirWS;
                inputData.shadowCoord = IN.shadowCoord;
                inputData.fogCoord = IN.fogCoord;
                inputData.vertexLighting = 0.0h.xxx;
                inputData.bakedGI = SampleSH(normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(IN.positionHCS);
                inputData.shadowMask = half4(1, 1, 1, 1);
                inputData.tangentToWorld = tangentToWorld;
                return inputData;
            }

            struct SplitLightingData
            {
                half3 diffuse;
                half3 specular;
                half3 additionalToonFill;
                half  ndlRaw;
                half3 mainLightDirection;
            };

            half3 DirectDiffuse(BRDFData brdfData, Light light, half3 normalWS)
            {
                half ndl = saturate(dot(normalWS, light.direction));
                half3 radiance = light.color * (light.distanceAttenuation * light.shadowAttenuation * ndl);
                return brdfData.diffuse * radiance;
            }

            half3 DirectSpecular(BRDFData brdfData, Light light, half3 normalWS, half3 viewDirectionWS)
            {
                half ndl = saturate(dot(normalWS, light.direction));
                half3 radiance = light.color * (light.distanceAttenuation * light.shadowAttenuation * ndl);
                return brdfData.specular * DirectBRDFSpecular(brdfData, normalWS, light.direction, viewDirectionWS) * radiance;
            }

            SplitLightingData EvaluateSplitLighting(inout InputData inputData, SurfaceData surfaceData)
            {
                SurfaceData brdfSurface = surfaceData;
                BRDFData brdfData;
                InitializeBRDFData(brdfSurface, brdfData);

                half4 shadowMask = CalculateShadowMask(inputData);
                AmbientOcclusionFactor aoFactor = CreateAmbientOcclusionFactor(inputData, surfaceData);
                uint meshRenderingLayers = GetMeshRenderingLayer();
                Light mainLight = GetMainLight(inputData, shadowMask, aoFactor);
                MixRealtimeAndBakedGI(mainLight, inputData.normalWS, inputData.bakedGI);

                SplitLightingData lighting = (SplitLightingData)0;
                half indirectAO = aoFactor.indirectAmbientOcclusion;
                lighting.diffuse = inputData.bakedGI * brdfData.diffuse * indirectAO;

                half fresnelTerm = Pow4(1.0h - saturate(dot(inputData.normalWS, inputData.viewDirectionWS)));
                half3 reflectVector = reflect(-inputData.viewDirectionWS, inputData.normalWS);
                half3 indirectSpecular = GlossyEnvironmentReflection(reflectVector, inputData.positionWS, brdfData.perceptualRoughness, 1.0h, inputData.normalizedScreenSpaceUV);
                lighting.specular = indirectSpecular * EnvironmentBRDFSpecular(brdfData, fresnelTerm) * indirectAO;

                #ifdef _LIGHT_LAYERS
                    if (IsMatchingLightLayer(mainLight.layerMask, meshRenderingLayers))
                #endif
                {
                    lighting.diffuse += DirectDiffuse(brdfData, mainLight, inputData.normalWS);
                    lighting.specular += DirectSpecular(brdfData, mainLight, inputData.normalWS, inputData.viewDirectionWS);
                }

                lighting.ndlRaw = dot(inputData.normalWS, mainLight.direction);
                lighting.mainLightDirection = mainLight.direction;

                #if defined(_ADDITIONAL_LIGHTS)
                    uint pixelLightCount = GetAdditionalLightsCount();
                    LIGHT_LOOP_BEGIN(pixelLightCount)
                        Light light = GetAdditionalLight(lightIndex, inputData, shadowMask, aoFactor);
                        #ifdef _LIGHT_LAYERS
                            if (IsMatchingLightLayer(light.layerMask, meshRenderingLayers))
                        #endif
                        {
                            lighting.diffuse += DirectDiffuse(brdfData, light, inputData.normalWS);
                            lighting.specular += DirectSpecular(brdfData, light, inputData.normalWS, inputData.viewDirectionWS);
                            half ndl = saturate(dot(inputData.normalWS, light.direction));
                            half3 radiance = light.color * (light.distanceAttenuation * light.shadowAttenuation * ndl);
                            lighting.additionalToonFill += surfaceData.albedo * radiance;
                        }
                    LIGHT_LOOP_END
                #endif

                #if defined(_ADDITIONAL_LIGHTS_VERTEX)
                    lighting.diffuse += inputData.vertexLighting * brdfData.diffuse;
                #endif

                return lighting;
            }

            half SmoothRangeMask(half value, half range, half edge)
            {
                half threshold = 1.0h - saturate(range);
                half halfEdge = max(edge, 0.001h) * 0.5h;
                return smoothstep(threshold - halfEdge, threshold + halfEdge, value);
            }

            half3 BuildFakeEnvDirection(half3 lightDirectionWS)
            {
                half3 baseEnvDir = SafeNormalize(-lightDirectionWS);
                half3 upReference = abs(baseEnvDir.y) > 0.95h ? half3(0.0h, 0.0h, 1.0h) : half3(0.0h, 1.0h, 0.0h);
                half3 right = SafeNormalize(cross(upReference, baseEnvDir));
                half3 up = SafeNormalize(cross(baseEnvDir, right));

                half yawRad = radians(_EnvYawOffset);
                half pitchRad = radians(_EnvPitchOffset);
                half sinYaw;
                half cosYaw;
                half sinPitch;
                half cosPitch;
                sincos(yawRad, sinYaw, cosYaw);
                sincos(pitchRad, sinPitch, cosPitch);

                return SafeNormalize(baseEnvDir * (cosYaw * cosPitch) + right * (sinYaw * cosPitch) + up * sinPitch);
            }

            half3 EvaluateFakeEnv(half3 normalWS, half3 viewDirectionWS, half3 lightDirectionWS)
            {
                // Fresnel rim: strongest at model edges, falls off toward screen center.
                half fresnel = 1.0h - saturate(dot(normalWS, viewDirectionWS));

                // Directional bias so the rim can favor one side of the model.
                half3 envDirectionWS = BuildFakeEnvDirection(lightDirectionWS);
                half dirFacing = saturate(dot(normalWS, envDirectionWS) * 0.5h + 0.5h);

                // Bias = 0  → uniform Fresnel rim around the whole model
                // Bias = 1  → rim only on the side facing envDirection
                half rim = fresnel * lerp(1.0h, dirFacing, saturate(_EnvBias));

                half envMask = SmoothRangeMask(rim, _EnvRange, _EnvEdge) * _EnvColor.a;
                return _EnvColor.rgb * envMask;
            }

            half3 ApplyToonLayers(half3 diffuseColor, half3 albedo, half ndl, half metallic)
            {
                half halfLambert = ndl * 0.5h + 0.5h;
                half diffuseToonWeight = lerp(1.0h - saturate(metallic), 1.0h, saturate(_ToonStrength));

                half middleMin = _MiddleRange - _MiddleEdge * 0.5h;
                half middleMax = _MiddleRange + _MiddleEdge * 0.5h;
                half middleBand = 1.0h - smoothstep(middleMin, middleMax, halfLambert);

                half darkMin = _DarkRange - _DarkEdge * 0.5h;
                half darkMax = _DarkRange + _DarkEdge * 0.5h;
                half darkBand = 1.0h - smoothstep(darkMin, darkMax, halfLambert);

                half3 color = diffuseColor;

                half3 middleTarget = albedo * _MiddleColor.rgb;
                color = lerp(color, middleTarget, saturate(middleBand * _MiddleColor.a * diffuseToonWeight));

                half3 darkTarget = albedo * _DarkColor.rgb;
                color = lerp(color, darkTarget, saturate(darkBand * _DarkColor.a * diffuseToonWeight));

                return color;
            }

            void GetSmoothnessAdjustedToonSpecular(
                half smoothness,
                out half specularToonStrength,
                out half specularRange)
            {
                half smoothnessFactor = saturate(smoothness);
                specularToonStrength = saturate(_SpecularToon) * smoothnessFactor;
                specularRange = lerp(1.0h, saturate(_SpecularThreshold), smoothnessFactor);
            }

            half ToonSpecularMask(half highlight, half specularRange)
            {
                half threshold = 1.0h - saturate(specularRange);
                half edge = max(_SpecularEdge, 0.001h);
                return smoothstep(threshold - edge * 0.5h, threshold + edge * 0.5h, saturate(highlight));
            }

            half3 EvaluatePBRToonSpecular(half3 pbrSpecular, half smoothness)
            {
                half specularToonStrength;
                half specularRange;
                GetSmoothnessAdjustedToonSpecular(smoothness, specularToonStrength, specularRange);

                half luma = max(max(pbrSpecular.r, pbrSpecular.g), pbrSpecular.b);
                half mask = ToonSpecularMask(luma, specularRange);
                half3 toonSpecular = pbrSpecular * mask;
                return lerp(pbrSpecular, toonSpecular, specularToonStrength);
            }

            half3 EvaluatePureToonSpecular(
                half3 normalWS,
                half3 viewDirectionWS,
                half3 lightDirectionWS,
                half3 albedo,
                half smoothness)
            {
                half specularToonStrength;
                half specularRange;
                GetSmoothnessAdjustedToonSpecular(smoothness, specularToonStrength, specularRange);

                half3 halfDir = SafeNormalize(lightDirectionWS + viewDirectionWS);
                half spec = saturate(dot(normalWS, halfDir));
                spec = pow(spec, 32.0h);
                return albedo * ToonSpecularMask(spec, specularRange) * specularToonStrength;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);

                half4 baseSample = SAMPLE_TEXTURE2D(_BC, sampler_BC, TRANSFORM_TEX(IN.uv, _BC));
                half4 baseColor = baseSample * _BaseColor;

                half3x3 tangentToWorld = half3x3(normalize(IN.tangentWS), normalize(IN.bitangentWS), normalize(IN.normalWS));
                half4 packedNMR = SAMPLE_TEXTURE2D(_N_M_R, sampler_N_M_R, IN.uv);
                half3 normalTS = SampleNormalTS(packedNMR);
                half3 normalWS = TransformNormalWS(normalTS, tangentToWorld);
                half3 viewDirWS = normalize(GetWorldSpaceViewDir(IN.positionWS));

                half metallic = packedNMR.b * _Metallic;
                half roughness = packedNMR.a;
                half smoothness = (1.0h - roughness) * _Smoothness;
                metallic = saturate(metallic);
                smoothness = saturate(smoothness);
                SurfaceData surfaceData = BuildSurfaceData(baseColor, metallic, smoothness, normalTS);
                InputData inputData = BuildInputData(IN, normalWS, viewDirWS, tangentToWorld);

                SplitLightingData lighting = EvaluateSplitLighting(inputData, surfaceData);
                half3 fakeEnv = EvaluateFakeEnv(normalWS, viewDirWS, lighting.mainLightDirection);

                // PBR path: full specular (IBL + main light + additional lights) plus the fake env compensation light.
                half3 pbrColor = lighting.diffuse + lighting.specular + fakeEnv + surfaceData.emission;

                // PBR-toon path keeps the BRDF diffuse/specular split; fake env is added after banding so middle/dark colors do not cover it.
                half3 toonBase = ApplyToonLayers(lighting.diffuse, surfaceData.albedo, lighting.ndlRaw, metallic) + fakeEnv;
                half3 toonSpec = EvaluatePBRToonSpecular(lighting.specular, smoothness);
                half3 pbrToonColor = toonBase + toonSpec + surfaceData.emission;

                // Pure-toon path uses albedo-based bands; metallic mode can retain the physical reflection response.
                half keepMetallic = saturate(_KeepMetallic);
                half3 pureDiffuse = surfaceData.albedo;
                half3 additionalToonFill = lighting.additionalToonFill * saturate(_AdditionalLightToonFill);
                half3 pureToonDiffuse = ApplyToonLayers(pureDiffuse, surfaceData.albedo, lighting.ndlRaw, 0.0h) + additionalToonFill;

                // In metallic mode, keep the toon bands but reduce their diffuse contribution
                // using the metallic workflow. Metallic surfaces get their color primarily from reflections.
                half toonDiffuseWeight = lerp(1.0h, 1.0h - metallic, keepMetallic);
                half3 pureToonBase = pureToonDiffuse * toonDiffuseWeight + fakeEnv;
                half3 pureToonSpec = EvaluatePureToonSpecular(
                    normalWS,
                    viewDirWS,
                    lighting.mainLightDirection,
                    surfaceData.albedo,
                    smoothness);

                // PBR specular already contains the metallic/smoothness response from the packed map,
                // so it must not be multiplied by metallic a second time.
                half3 pureSpecular = lerp(pureToonSpec, lighting.specular, keepMetallic);
                half3 pureToonColor = pureToonBase + pureSpecular + surfaceData.emission;

                half pbrToToon = saturate(_ToonStrength * 2.0h);
                half toonToPure = saturate((_ToonStrength - 0.5h) * 2.0h);
                half3 midColor = lerp(pbrColor, pbrToonColor, pbrToToon);
                half3 finalColor = lerp(midColor, pureToonColor, toonToPure);

                finalColor = MixFog(finalColor, IN.fogCoord);
                return half4(finalColor, baseColor.a);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex ShadowVertex
            #pragma fragment ShadowFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            CBUFFER_START(UnityPerMaterial)
                float4 _BC_ST;
                half4 _BaseColor;
                half _NormalScale;
                half _Metallic;
                half _Smoothness;
                half _KeepMetallic;
                half4 _MiddleColor;
                half _MiddleRange;
                half _MiddleEdge;
                half4 _DarkColor;
                half _DarkRange;
                half _DarkEdge;
                half _SpecularToon;
                half _SpecularThreshold;
                half _SpecularEdge;
                half _ToonStrength;
                half _AdditionalLightToonFill;
                half4 _EnvColor;
                half _EnvRange;
                half _EnvEdge;
                half _EnvYawOffset;
                half _EnvPitchOffset;
                half _EnvBias;
                half _UseOutline;
                half4 _OutlineColor;
                float _OutlineWidth;
                float _ZOffset;
                half _Cull;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings ShadowVertex(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(IN.normalOS);

                #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                    float3 lightDirectionWS = normalize(_LightPosition - positionWS);
                #else
                    float3 lightDirectionWS = _LightDirection;
                #endif

                float4 positionHCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
                #if UNITY_REVERSED_Z
                    positionHCS.z = min(positionHCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionHCS.z = max(positionHCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif

                OUT.positionHCS = positionHCS;
                return OUT;
            }

            half4 ShadowFragment(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask R
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex DepthVertex
            #pragma fragment DepthFragment
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BC_ST;
                half4 _BaseColor;
                half _NormalScale;
                half _Metallic;
                half _Smoothness;
                half _KeepMetallic;
                half4 _MiddleColor;
                half _MiddleRange;
                half _MiddleEdge;
                half4 _DarkColor;
                half _DarkRange;
                half _DarkEdge;
                half _SpecularToon;
                half _SpecularThreshold;
                half _SpecularEdge;
                half _ToonStrength;
                half _AdditionalLightToonFill;
                half4 _EnvColor;
                half _EnvRange;
                half _EnvEdge;
                half _EnvYawOffset;
                half _EnvPitchOffset;
                half _EnvBias;
                half _UseOutline;
                half4 _OutlineColor;
                float _OutlineWidth;
                float _ZOffset;
                half _Cull;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings DepthVertex(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                return OUT;
            }

            half DepthFragment(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                return IN.positionHCS.z;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            ZWrite On
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex DepthNormalsVertex
            #pragma fragment DepthNormalsFragment
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BC_ST;
                half4 _BaseColor;
                half _NormalScale;
                half _Metallic;
                half _Smoothness;
                half _KeepMetallic;
                half4 _MiddleColor;
                half _MiddleRange;
                half _MiddleEdge;
                half4 _DarkColor;
                half _DarkRange;
                half _DarkEdge;
                half _SpecularToon;
                half _SpecularThreshold;
                half _SpecularEdge;
                half _ToonStrength;
                half _AdditionalLightToonFill;
                half4 _EnvColor;
                half _EnvRange;
                half _EnvEdge;
                half _EnvYawOffset;
                half _EnvPitchOffset;
                half _EnvBias;
                half _UseOutline;
                half4 _OutlineColor;
                float _OutlineWidth;
                float _ZOffset;
                half _Cull;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                half3 normalWS     : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings DepthNormalsVertex(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                return OUT;
            }

            half4 DepthNormalsFragment(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                return half4(normalize(IN.normalWS), 0.0h);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Outline"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            Cull Front
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma shader_feature_local _OUTLINE_ON
            #pragma shader_feature_local _SMOOTH_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BC_ST;
                half4 _BaseColor;
                half _NormalScale;
                half _Metallic;
                half _Smoothness;
                half _KeepMetallic;
                half4 _MiddleColor;
                half _MiddleRange;
                half _MiddleEdge;
                half4 _DarkColor;
                half _DarkRange;
                half _DarkEdge;
                half _SpecularToon;
                half _SpecularThreshold;
                half _SpecularEdge;
                half _ToonStrength;
                half _AdditionalLightToonFill;
                half4 _EnvColor;
                half _EnvRange;
                half _EnvEdge;
                half _EnvYawOffset;
                half _EnvPitchOffset;
                half _EnvBias;
                half _UseOutline;
                half4 _OutlineColor;
                float _OutlineWidth;
                float _ZOffset;
                half _Cull;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 uv2        : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                #if !defined(_OUTLINE_ON)
                    OUT.positionHCS = float4(0.0, 0.0, 0.0, 0.0);
                    return OUT;
                #endif

                #if defined(_SMOOTH_ON)
                    float d2 = dot(IN.uv2.xyz, IN.uv2.xyz);
                    float3 outlineNormalOS = d2 > 0.0001 ? normalize(IN.uv2.xyz) : normalize(IN.normalOS);
                #else
                    float3 outlineNormalOS = normalize(IN.normalOS);
                #endif

                float3 positionOS = IN.positionOS.xyz + outlineNormalOS * _OutlineWidth;
                float4 positionHCS = TransformObjectToHClip(positionOS);
                positionHCS.z += _ZOffset * positionHCS.w;
                OUT.positionHCS = positionHCS;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                #if !defined(_OUTLINE_ON)
                    discard;
                #endif
                return _OutlineColor;
            }
            ENDHLSL
        }
    }

    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}


