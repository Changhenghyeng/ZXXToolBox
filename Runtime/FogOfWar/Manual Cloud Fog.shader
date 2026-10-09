Shader "Custom/Manual Cloud Fog"
{
    Properties
    {
        [MainTexture] _CloudTex("Cloud Texture", 2D) = "white" {}
        [HDR] _CloudColor("Cloud Color", Color) = (1, 1, 1, 0.55)
        _ShadowColor("Shadow Color", Color) = (0, 0, 0, 0.25)

        [Header(Cloud Motion)]
        _Layer1Tiling("Layer 1 Tiling", Float) = 2
        _Layer2Tiling("Layer 2 Tiling", Float) = 1
        _CloudSpeed("Cloud Speed (Layer1 XY, Layer2 ZW)", Vector) = (0.018, 0.011, -0.013, -0.017)

        [Header(Cloud Edge)]
        _EdgeFadeWidth("Edge Fade Width", Range(0.001, 0.5)) = 0.12
        _EdgeNoiseTiling("Edge Noise Tiling", Float) = 3
        _EdgeNoiseSpeed("Edge Noise Speed (Layer1 XY, Layer2 ZW)", Vector) = (0.012, 0.008, -0.009, 0.014)
        _EdgeDistortionStrength("Edge Distortion Strength", Range(0, 1)) = 0.06

        [Header(Shadow)]
        _ShadowUVOffset("Shadow UV Offset XY", Vector) = (0.005, 0.005, 0, 0)

        [Header(Scene Depth Intersection)]
        [Toggle] _EnableDepthOcclusion("Enable Depth Fade", Float) = 1
        _DepthOcclusionDistance("Occlusion Soft Distance", Float) = 1
        _DepthOcclusionStrength("Occlusion Strength", Range(0, 1)) = 1
        _DepthOcclusionOffset("Occlusion Offset", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent+60"
            "RenderType" = "Transparent"
        }

        Cull Off
        ZWrite Off
        // Scene-depth sampling below performs the occlusion and soft intersection.
        ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

        TEXTURE2D(_CloudTex);
        SAMPLER(sampler_CloudTex);

        CBUFFER_START(UnityPerMaterial)
            float4 _CloudTex_ST;
            half4 _CloudColor;
            half4 _ShadowColor;
            float _Layer1Tiling;
            float _Layer2Tiling;
            float4 _CloudSpeed;
            float _EdgeFadeWidth;
            float _EdgeNoiseTiling;
            float4 _EdgeNoiseSpeed;
            float _EdgeDistortionStrength;
            float4 _ShadowUVOffset;
            float _EnableDepthOcclusion;
            float _DepthOcclusionDistance;
            float _DepthOcclusionStrength;
            float _DepthOcclusionOffset;
        CBUFFER_END

        struct Attributes
        {
            float4 positionOS : POSITION;
            float2 uv : TEXCOORD0;
        };

        struct Varyings
        {
            float4 positionHCS : SV_POSITION;
            float2 meshUV : TEXCOORD0;
            float2 cloudUV : TEXCOORD1;
            float3 positionWS : TEXCOORD2;
        };

        Varyings Vert(Attributes input)
        {
            Varyings output;
            output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
            output.positionHCS = TransformWorldToHClip(output.positionWS);
            output.meshUV = input.uv;
            output.cloudUV = input.uv * _CloudTex_ST.xy + _CloudTex_ST.zw;
            return output;
        }

        // Two independently tiled samples prevent an obvious repeating scroll pattern.
        half4 SampleCloud(float2 uv)
        {
            half4 layer1 = SAMPLE_TEXTURE2D(
                _CloudTex,
                sampler_CloudTex,
                uv * _Layer1Tiling + _Time.x * _CloudSpeed.xy);
            half4 layer2 = SAMPLE_TEXTURE2D(
                _CloudTex,
                sampler_CloudTex,
                uv * _Layer2Tiling + _Time.x * _CloudSpeed.zw);
            return (layer1 + layer2) * 0.5h;
        }

        // Displace the rectangular boundary in both directions with cloud noise. A very
        // narrow guard still forces alpha to zero at the physical edge of the mesh.
        half GetEdgeMask(float2 meshUV)
        {
            float2 distanceToSides = min(meshUV, 1.0 - meshUV);
            float edgeTiling = max(0.0001, _EdgeNoiseTiling);
            float2 noiseUV1 =
                meshUV * edgeTiling
                + _Time.y * _EdgeNoiseSpeed.xy;
            float2 noiseUV2 =
                meshUV * edgeTiling
                + float2(0.37, 0.61)
                + _Time.y * _EdgeNoiseSpeed.zw;
            half3 noiseColorX = SAMPLE_TEXTURE2D(
                _CloudTex,
                sampler_CloudTex,
                noiseUV1).rgb;
            half3 noiseColorY = SAMPLE_TEXTURE2D(
                _CloudTex,
                sampler_CloudTex,
                noiseUV2).rgb;
            half noise1 = (noiseColorX.r + noiseColorX.g + noiseColorX.b) / 3.0h;
            half noise2 = (noiseColorY.r + noiseColorY.g + noiseColorY.b) / 3.0h;
            noise1 = saturate((noise1 - 0.5h) * 3.0h + 0.5h);
            noise2 = saturate((noise2 - 0.5h) * 3.0h + 0.5h);
            half edgeNoise = (noise1 + noise2) * 0.5h;

            float distanceToEdge = min(distanceToSides.x, distanceToSides.y);
            float edgeBandWidth = max(
                0.0001,
                _EdgeFadeWidth + _EdgeDistortionStrength);
            half edgeWeight = 1.0h - smoothstep(
                0.0,
                edgeBandWidth,
                distanceToEdge);

            // Erode only the border. Noise influence reaches zero before the interior,
            // so tiled noise can never punch holes through the middle of the cloud.
            float distortedDistance = distanceToEdge
                - (1.0h - edgeNoise)
                * _EdgeDistortionStrength
                * edgeWeight;
            return smoothstep(
                0.0,
                max(0.0001, _EdgeFadeWidth),
                distortedDistance);
        }

        // Reconstruct scene depth in world space and compare it with the fog plane
        // along the camera forward axis. This also works with orthographic cameras.
        half GetDepthOcclusion(float4 positionHCS, float3 fogWorldPos)
        {
            if (_EnableDepthOcclusion < 0.5)
            {
                return 1.0h;
            }

            float2 screenUV = GetNormalizedScreenSpaceUV(positionHCS);
            float sceneRawDepth = SampleSceneDepth(screenUV);
#if UNITY_REVERSED_Z
            if (sceneRawDepth <= 0.00001)
            {
                return 1.0h;
            }
#else
            if (sceneRawDepth >= 0.99999)
            {
                return 1.0h;
            }
            sceneRawDepth = lerp(UNITY_NEAR_CLIP_VALUE, 1.0, sceneRawDepth);
#endif
            float3 sceneWorldPos = ComputeWorldSpacePosition(
                screenUV,
                sceneRawDepth,
                UNITY_MATRIX_I_VP);
            float3 cameraForwardWS = -UNITY_MATRIX_V[2].xyz;
            float occluderDepth =
                dot(fogWorldPos - sceneWorldPos, cameraForwardWS)
                - _DepthOcclusionOffset;
            half occlusion = occluderDepth > 0.0
                ? saturate(occluderDepth / max(0.0001, _DepthOcclusionDistance))
                : 0.0h;

            return lerp(
                1.0h,
                1.0h - occlusion,
                saturate((half)_DepthOcclusionStrength));
        }
        ENDHLSL

        Pass
        {
            Name "CloudShadow"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex Vert
            #pragma fragment FragShadow

            half4 FragShadow(Varyings input) : SV_Target
            {
                half cloudAlpha = SampleCloud(input.cloudUV + _ShadowUVOffset.xy).a;
                half edgeMask = GetEdgeMask(input.meshUV);
                half depthOcclusion = GetDepthOcclusion(input.positionHCS, input.positionWS);
                return half4(
                    _ShadowColor.rgb,
                    cloudAlpha * edgeMask * _ShadowColor.a * depthOcclusion);
            }
            ENDHLSL
        }

        Pass
        {
            Name "CloudColor"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex Vert
            #pragma fragment FragCloud

            half4 FragCloud(Varyings input) : SV_Target
            {
                half4 cloud = SampleCloud(input.cloudUV);
                cloud.rgb *= _CloudColor.rgb;
                cloud.a *= GetEdgeMask(input.meshUV)
                    * _CloudColor.a
                    * GetDepthOcclusion(input.positionHCS, input.positionWS);
                return cloud;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
