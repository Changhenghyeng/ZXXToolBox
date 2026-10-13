Shader "Custom/BakedLightMap_PlantWind"
{
    Properties
    {
        [Header(Base Material)]
        [MainTexture] _BaseMap ("Base Map", 2D) = "white" {}
        [MainColor] _MainColor ("Main Color", Color) = (1, 1, 1, 1)
        [Normal] _BumpMap ("Normal Map", 2D) = "bump" {}
        _NormalStrength ("Normal Strength", Range(0, 1)) = 1
        _MetallicGlossMap ("Metallica Map", 2D) = "white" {}
        _MetallicaStrength ("Metallica Strength", Range(0, 1)) = 1
        _SmoothnessStrength ("Smoothness Strength", Range(0, 1)) = 0.5

        [Header(Baked Lighting)]
        [NoScaleOffset] _BakedMap ("Baked Map (R=Shadow G=AO B=Fill Mask)", 2D) = "white" {}
        _ShadowStrength ("Shadow Strength", Range(0, 1)) = 0.4
        [Toggle] _UseAO ("Use AO", Float) = 1
        _AOStrength ("AO Strength", Range(0, 1)) = 1
        _FillDir ("FillDir", Vector) = (0, 0, 0, 0)
        [HDR] _FillColor ("FillColor", Color) = (0, 0, 0, 0)
        [HDR] _SkyLightColor ("Sky Light Color", Color) = (0.5, 0.65, 1, 1)
        _SkyLightStrength ("Sky Light Strength", Range(0, 2)) = 0

        [Header(Emission)]
        [Toggle] _EmissionSwitch ("EmissionSwitch", Float) = 0
        _EmissionMap ("Emission", 2D) = "white" {}
        [HDR] _EmissionColor ("Color", Color) = (0, 0, 0, 0)

        [Header(Plant Wind)]
        _WindDirection ("Wind Direction (World XZ)", Vector) = (1, 0, 0, 0)
        _WindStrength ("Wind Strength", Range(0, 2)) = 0.15
        _WindSpeed ("Wind Speed", Range(0, 10)) = 1
        _WindFrequency ("Wind Frequency", Range(0.01, 10)) = 1

        [Header(Wind Strength Gradient)]
        [Enum(Bottom To Top, 0, Top To Bottom, 1, Left To Right, 2, Right To Left, 3)]
        _GradientDirection ("Gradient Direction", Float) = 0
        _GradientStart ("Gradient Start (Normalized)", Range(0, 1)) = 0
        _GradientWidth ("Gradient Width (Normalized)", Range(0.001, 1)) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

        TEXTURE2D(_BaseMap);            SAMPLER(sampler_BaseMap);
        TEXTURE2D(_BumpMap);            SAMPLER(sampler_BumpMap);
        TEXTURE2D(_MetallicGlossMap);   SAMPLER(sampler_MetallicGlossMap);
        TEXTURE2D(_BakedMap);           SAMPLER(sampler_BakedMap);
        TEXTURE2D(_EmissionMap);        SAMPLER(sampler_EmissionMap);

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            float4 _MainColor;
            float4 _FillDir;
            float4 _FillColor;
            float4 _SkyLightColor;
            float4 _EmissionColor;
            float4 _WindDirection;
            float _NormalStrength;
            float _MetallicaStrength;
            float _SmoothnessStrength;
            float _ShadowStrength;
            float _UseAO;
            float _AOStrength;
            float _SkyLightStrength;
            float _EmissionSwitch;
            float _WindStrength;
            float _WindSpeed;
            float _WindFrequency;
            float _GradientDirection;
            float _GradientStart;
            float _GradientWidth;
        CBUFFER_END

        float GetWindGradient(float3 positionWS)
        {
            float axisPosition;
            float axisMinimum;
            float axisMaximum;

            if (_GradientDirection < 0.5)
            {
                axisPosition = positionWS.y;
                axisMinimum = unity_RendererBounds_Min.y;
                axisMaximum = unity_RendererBounds_Max.y;
            }
            else if (_GradientDirection < 1.5)
            {
                axisPosition = -positionWS.y;
                axisMinimum = -unity_RendererBounds_Max.y;
                axisMaximum = -unity_RendererBounds_Min.y;
            }
            else if (_GradientDirection < 2.5)
            {
                axisPosition = positionWS.x;
                axisMinimum = unity_RendererBounds_Min.x;
                axisMaximum = unity_RendererBounds_Max.x;
            }
            else
            {
                axisPosition = -positionWS.x;
                axisMinimum = -unity_RendererBounds_Max.x;
                axisMaximum = -unity_RendererBounds_Min.x;
            }

            float normalizedPosition = saturate(
                (axisPosition - axisMinimum) / max(axisMaximum - axisMinimum, 0.0001));
            float gradientEnd = _GradientStart + max(_GradientWidth, 0.001);
            return smoothstep(_GradientStart, gradientEnd, normalizedPosition);
        }

        float3 ApplyPlantWind(float3 positionOS)
        {
            [branch]
            if (_WindStrength == 0.0)
                return positionOS;

            float3 positionWS = TransformObjectToWorld(positionOS);
            float2 directionXZ = _WindDirection.xz;
            float directionLengthSq = dot(directionXZ, directionXZ);
            directionXZ = directionLengthSq > 0.00001
                ? directionXZ * rsqrt(directionLengthSq)
                : float2(1.0, 0.0);

            float phase = dot(positionWS.xz, float2(0.83, 1.17)) * _WindFrequency;
            float time = _Time.y * _WindSpeed;
            float wave = sin(phase + time) * 0.75
                       + sin(phase * 1.91 - time * 1.37) * 0.25;
            float gradient = GetWindGradient(positionWS);
            float3 offsetWS = float3(directionXZ.x, 0.0, directionXZ.y)
                            * (wave * _WindStrength * gradient);

            return positionOS + TransformWorldToObjectDir(offsetWS, false);
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForwardOnly" }

            Cull Off
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex WindLitVertex
            #pragma fragment WindLitFragment

            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile_fog

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 uv0 : TEXCOORD0;
                float2 uv1 : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 baseUV : TEXCOORD0;
                float2 rawUV : TEXCOORD1;
                float2 bakedUV : TEXCOORD2;
                float3 positionWS : TEXCOORD3;
                half3 normalWS : TEXCOORD4;
                half4 tangentWS : TEXCOORD5;
                half4 fogAndVertexLight : TEXCOORD6;
                float4 shadowCoord : TEXCOORD7;
                DECLARE_LIGHTMAP_OR_SH(lightmapUV, vertexSH, 8);
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings WindLitVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 positionOS = ApplyPlantWind(input.positionOS.xyz);
                VertexPositionInputs positionInputs = GetVertexPositionInputs(positionOS);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS, input.tangentOS);

                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.normalWS = NormalizeNormalPerVertex(normalInputs.normalWS);
                output.tangentWS = half4(normalInputs.tangentWS,
                    input.tangentOS.w * GetOddNegativeScale());
                output.baseUV = TRANSFORM_TEX(input.uv0, _BaseMap);
                output.rawUV = input.uv0;
                output.bakedUV = input.uv1;
                output.shadowCoord = GetShadowCoord(positionInputs);
                output.fogAndVertexLight = half4(
                    ComputeFogFactor(positionInputs.positionCS.z),
                    VertexLighting(positionInputs.positionWS, normalInputs.normalWS));
                OUTPUT_LIGHTMAP_UV(input.uv1, unity_LightmapST, output.lightmapUV);
                OUTPUT_SH(output.normalWS, output.vertexSH);
                return output;
            }

            half4 WindLitFragment(
                Varyings input,
                FRONT_FACE_TYPE frontFace : FRONT_FACE_SEMANTIC) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half faceSign = IS_FRONT_VFACE(frontFace, 1.0h, -1.0h);
                half3 geometricNormalWS = normalize(input.normalWS) * faceSign;
                half3 normalTS = half3(0.0h, 0.0h, 1.0h);
                half3 normalWS = geometricNormalWS;
                // These conditions are uniform per material; no extra shader variants are needed.
                [branch]
                if (_NormalStrength != 0.0)
                {
                    half3 tangentWS = normalize(input.tangentWS.xyz);
                    half3 bitangentWS = input.tangentWS.w * cross(geometricNormalWS, tangentWS);
                    normalTS = UnpackNormalScale(
                        SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, input.rawUV),
                        _NormalStrength);
                    normalWS = NormalizeNormalPerPixel(
                        TransformTangentToWorld(normalTS,
                            half3x3(tangentWS, bitangentWS, geometricNormalWS)));
                }

                half4 baseSample = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.baseUV);
                half4 bakedSample = SAMPLE_TEXTURE2D(_BakedMap, sampler_BakedMap, input.bakedUV);
                half4 metallicSample = half4(1.0h, 1.0h, 1.0h, 1.0h);
                [branch]
                if (_MetallicaStrength != 0.0 || _SmoothnessStrength != 0.0)
                {
                    metallicSample = SAMPLE_TEXTURE2D(
                        _MetallicGlossMap, sampler_MetallicGlossMap, input.rawUV);
                }

                half bakedShadow = lerp(1.0h, bakedSample.r, saturate(_ShadowStrength));
                half bakedAO = lerp(1.0h, bakedSample.g, saturate(_AOStrength));
                half aoFactor = lerp(1.0h, bakedAO, saturate(_UseAO));
                half bakedLighting = bakedShadow * aoFactor;
                half3 untintedBase = baseSample.rgb * _MainColor.rgb;

                half3 optionalEmission = half3(0.0h, 0.0h, 0.0h);
                [branch]
                if (_EmissionSwitch > 0.0)
                {
                    float3 fillDirection = -SafeNormalize(_FillDir.xyz);
                    half fillFacing = saturate(dot(geometricNormalWS, fillDirection));
                    half3 fillEmission = baseSample.rgb * _FillColor.rgb
                        * (bakedSample.b * fillFacing);
                    half3 textureEmission = SAMPLE_TEXTURE2D(
                        _EmissionMap, sampler_EmissionMap, input.rawUV).rgb * _EmissionColor.rgb;
                    optionalEmission = (textureEmission + fillEmission)
                        * saturate(_EmissionSwitch);
                }

                half darkMask = (1.0h - bakedLighting) * aoFactor;
                half3 skyEmission = untintedBase * _SkyLightColor.rgb
                    * (_SkyLightStrength * darkMask);

                SurfaceData surfaceData = (SurfaceData)0;
                surfaceData.albedo = untintedBase * bakedLighting;
                surfaceData.metallic = saturate(metallicSample.r * _MetallicaStrength);
                surfaceData.specular = half3(0.0h, 0.0h, 0.0h);
                surfaceData.smoothness = saturate(metallicSample.a * _SmoothnessStrength);
                surfaceData.normalTS = normalTS;
                surfaceData.emission = optionalEmission + skyEmission;
                surfaceData.occlusion = 1.0h;
                surfaceData.alpha = baseSample.a * _MainColor.a;
                surfaceData.clearCoatMask = 0.0h;
                surfaceData.clearCoatSmoothness = 0.0h;

                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.normalWS = normalWS;
                inputData.viewDirectionWS = SafeNormalize(GetWorldSpaceViewDir(input.positionWS));
                inputData.shadowCoord = input.shadowCoord;
                inputData.fogCoord = input.fogAndVertexLight.x;
                inputData.vertexLighting = input.fogAndVertexLight.yzw;
                inputData.bakedGI = SAMPLE_GI(input.lightmapUV, input.vertexSH, normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = SAMPLE_SHADOWMASK(input.lightmapUV);

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

            Cull Off
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex WindShadowVertex
            #pragma fragment WindDepthFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            float3 _LightDirection;
            float3 _LightPosition;

            struct ShadowAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            ShadowVaryings WindShadowVertex(ShadowAttributes input)
            {
                ShadowVaryings output = (ShadowVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                float3 positionOS = ApplyPlantWind(input.positionOS.xyz);
                float3 positionWS = TransformObjectToWorld(positionOS);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif
                output.positionCS = TransformWorldToHClip(
                    ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
            #if UNITY_REVERSED_Z
                output.positionCS.z = min(output.positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                output.positionCS.z = max(output.positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                return output;
            }

            half4 WindDepthFragment(ShadowVaryings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            Cull Off
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex WindDepthVertex
            #pragma fragment WindDepthFragment
            #pragma multi_compile_instancing

            struct DepthAttributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct DepthVaryings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            DepthVaryings WindDepthVertex(DepthAttributes input)
            {
                DepthVaryings output = (DepthVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(ApplyPlantWind(input.positionOS.xyz));
                return output;
            }

            half4 WindDepthFragment(DepthVaryings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }

            Cull Off
            ZWrite On

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex WindDepthNormalsVertex
            #pragma fragment WindDepthNormalsFragment
            #pragma multi_compile_instancing
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

            struct DepthNormalsAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct DepthNormalsVaryings
            {
                float4 positionCS : SV_POSITION;
                half3 normalWS : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            DepthNormalsVaryings WindDepthNormalsVertex(DepthNormalsAttributes input)
            {
                DepthNormalsVaryings output = (DepthNormalsVaryings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = TransformObjectToHClip(ApplyPlantWind(input.positionOS.xyz));
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            half4 WindDepthNormalsFragment(DepthNormalsVaryings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
            #if defined(_GBUFFER_NORMALS_OCT)
                float2 octNormalWS = PackNormalOctQuadEncode(normalize(input.normalWS));
                float2 remappedOctNormalWS = saturate(octNormalWS * 0.5 + 0.5);
                return half4(PackFloat2To888(remappedOctNormalWS), 0.0);
            #else
                return half4(NormalizeNormalPerPixel(input.normalWS), 0.0);
            #endif
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Lit"
}
