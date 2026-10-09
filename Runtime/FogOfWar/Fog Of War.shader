Shader "Custom/Fog Of War"
{
    Properties
    {
        _MainTex ("Mask Texture (R Fog, B Dissolve)", 2D) = "white" {}
        _CloudTex ("Cloud Texture", 2D) = "white" {}
        _DissolveTex ("Dissolve Texture", 2D) = "gray" {}
        _DissolveStrength ("Dissolve Strength", Range(0, 2)) = 1
        _DissolveSpeedX ("Dissolve Speed X", Float) = 0.1
        _DissolveSpeedY ("Dissolve Speed Y", Float) = 0.06

        [HDR] _CloudColor ("Cloud Color", Color) = (1, 1, 1, 0.55)
        _ShadowColor ("Shadow Color", Color) = (0, 0, 0, 0.6)

        _TillingFactor ("Cloud Layer 1 Tiling", Float) = 2
        _CloudSpeed ("Cloud Speed (Layer1 XY, Layer2 ZW)", Vector) = (0.018, 0.011, -0.013, -0.017)

        [Header(Mask Edge)]
        _MaskGradientWidth ("Gradient Width (Cells)", Float) = 2
        _MaskGradientTransparency ("Gradient Transparency Strength", Range(0, 2)) = 1
        _MaskDistortCenterX ("Distortion Center X", Float) = 0.5
        _MaskDistortCenterY ("Distortion Center Y", Float) = 0.5
        _MaskDistortStrength ("Distortion Strength", Float) = 10

        [Header(Shadow)]
        _ShadowOffset ("Shadow Mask UV Offset XY", Vector) = (0, 0, 0, 0)

        [Header(Scene Depth Occlusion)]
        [Toggle] _EnableDepthOcclusion ("Enable Depth Occlusion", Float) = 1
        _DepthOcclusionDistance ("Occlusion Soft Distance", Float) = 1
        _DepthOcclusionStrength ("Occlusion Strength", Range(0, 1)) = 1
        _DepthOcclusionOffset ("Occlusion Offset", Float) = 0
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
        ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

        TEXTURE2D(_MainTex);
        SAMPLER(sampler_MainTex);
        TEXTURE2D(_CloudTex);
        SAMPLER(sampler_CloudTex);
        TEXTURE2D(_DissolveTex);
        SAMPLER(sampler_DissolveTex);

        CBUFFER_START(UnityPerMaterial)
            float4 _MainTex_ST;
            float4 _MainTex_TexelSize;
            float4 _CloudTex_ST;
            float4 _DissolveTex_ST;
            float _DissolveStrength;
            float _DissolveSpeedX;
            float _DissolveSpeedY;
            half4 _CloudColor;
            half4 _ShadowColor;
            float _TillingFactor;
            float4 _CloudSpeed;
            float _MaskGradientWidth;
            float _MaskGradientTransparency;
            float _MaskDistortCenterX;
            float _MaskDistortCenterY;
            float _MaskDistortStrength;
            float4 _ShadowOffset;
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
            float2 uv : TEXCOORD0;
            float3 positionWS : TEXCOORD1;
        };

        Varyings VertDefault(Attributes input)
        {
            Varyings output;
            float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
            output.positionHCS = TransformWorldToHClip(positionWS);
            output.uv = input.uv;
            output.positionWS = positionWS;
            return output;
        }

        // Reference: sample one cloud texture twice with different tiling and motion.
        half4 BlendTwoCloud(float2 uv)
        {
            half4 c = SAMPLE_TEXTURE2D(
                _CloudTex,
                sampler_CloudTex,
                uv * _TillingFactor + _Time.x * _CloudSpeed.xy);
            half4 c2 = SAMPLE_TEXTURE2D(
                _CloudTex,
                sampler_CloudTex,
                uv + _Time.x * _CloudSpeed.zw);
            c.rgb = (c.rgb + c2.rgb) * 0.5h;
            return c;
        }

        // Uniform line integration of the existing binary R mask.
        // Across a mask edge, the integral changes linearly with distance.
        half GetOutwardMaskGradient(float2 maskUV)
        {
            half center = SAMPLE_TEXTURE2D(
                _MainTex,
                sampler_MainTex,
                maskUV).r;

            // The empty area always stays empty. Only the solid side receives the band.
            if (center <= 0.5h)
            {
                return 0.0h;
            }

            float2 texel = _MainTex_TexelSize.xy;
            float widthInCells = max(0.01, _MaskGradientWidth);
            half integralX = 0.0h;
            half integralY = 0.0h;
            half integralDiagonalA = 0.0h;
            half integralDiagonalB = 0.0h;

            // Four continuous lines cover straight sides and square corners. Unlike the
            // previous eight-point ring search, no direction suddenly appears/disappears.
            [unroll]
            for (int sampleIndex = -8; sampleIndex <= 8; sampleIndex++)
            {
                float lineOffset = sampleIndex * 0.125 * widthInCells;
                float2 offsetX = float2(lineOffset, 0.0) * texel;
                float2 offsetY = float2(0.0, lineOffset) * texel;
                float2 offsetDiagonalA = float2(lineOffset, lineOffset) * texel;
                float2 offsetDiagonalB = float2(lineOffset, -lineOffset) * texel;

                integralX += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, maskUV + offsetX).r;
                integralY += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, maskUV + offsetY).r;
                integralDiagonalA += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, maskUV + offsetDiagonalA).r;
                integralDiagonalB += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, maskUV + offsetDiagonalB).r;
            }

            half lineAverage = min(
                min(integralX, integralY),
                min(integralDiagonalA, integralDiagonalB)) / 17.0h;

            // The line average is 0.5 at the original boundary and reaches 1 after
            // widthInCells, so this is the outward linear 0 -> 1 remap.
            half linearGradient = saturate(lineAverage * 2.0h - 1.0h);
            half transparencyStrength = max(
                0.0h,
                (half)_MaskGradientTransparency);
            if (transparencyStrength <= 0.0001h)
            {
                return 1.0h;
            }

            // Strength changes only the opacity curve inside the same band:
            // 1 = linear, 2 = more transparent middle, with unchanged 0/1 endpoints.
            return pow(linearGradient, transparencyStrength);
        }

        // Reference: B is the per-cell dissolve progress.
        half DissolveMaskR(half3 mask, float2 dissolveUV)
        {
            half dissolveSample =
                SAMPLE_TEXTURE2D(_DissolveTex, sampler_DissolveTex, dissolveUV).r;
            half dissolve = saturate(
                0.5h + (dissolveSample - 0.5h) * _DissolveStrength);
            return lerp(
                mask.r * smoothstep(
                    0.0h,
                    saturate(dissolve + mask.b) + 0.01h,
                    mask.b),
                mask.r,
                mask.b);
        }

        half4 EvaluateFog(float2 uv, float2 maskExtraOffset)
        {
            float2 cloudUV = uv * _CloudTex_ST.xy + _CloudTex_ST.zw;
            half4 cloud = BlendTwoCloud(cloudUV);

            // Reference: reuse the moving cloud color as mask-UV noise.
            half gray = (cloud.r + cloud.g + cloud.b) / 3.0h;
            float2 distortion = float2(
                gray - _MaskDistortCenterX,
                gray - _MaskDistortCenterY) * _MaskDistortStrength * 0.01;

            float2 maskUV = uv * _MainTex_ST.xy + _MainTex_ST.zw;
            float2 sampledMaskUV = maskUV + distortion + maskExtraOffset;
            half4 mask = SAMPLE_TEXTURE2D(
                _MainTex,
                sampler_MainTex,
                sampledMaskUV);
            mask.r = GetOutwardMaskGradient(sampledMaskUV);

            // Keep dissolve detail stable when the grid resolution changes.
            // With one mask pixel per cell, ST tiling now controls repeats per cell
            // instead of repeats across the entire fog surface.
            float2 gridResolution = max(_MainTex_TexelSize.zw, float2(1.0, 1.0));
            float2 dissolveUV =
                uv * gridResolution * _DissolveTex_ST.xy
                + _DissolveTex_ST.zw
                + _Time.y * float2(_DissolveSpeedX, _DissolveSpeedY);
            mask.r = DissolveMaskR(mask.rgb, dissolveUV);

            cloud.rgb *= _CloudColor.rgb;
            cloud.a = mask.r;
            return cloud;
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

        // The reference renders the cloud shadow before the cloud color.
        Pass
        {
            Name "CloudShadow"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            HLSLPROGRAM
            #pragma vertex VertDefault
            #pragma fragment FragShadow

            half4 FragShadow(Varyings input) : SV_Target
            {
                half4 fog = EvaluateFog(input.uv, _ShadowOffset.xy);
                half depthOcclusion = GetDepthOcclusion(input.positionHCS, input.positionWS);
                return half4(
                    _ShadowColor.rgb,
                    _ShadowColor.a * fog.a * depthOcclusion);
            }
            ENDHLSL
        }

        Pass
        {
            Name "CloudColor"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex VertDefault
            #pragma fragment FragCloud

            half4 FragCloud(Varyings input) : SV_Target
            {
                half4 fog = EvaluateFog(input.uv, float2(0.0, 0.0));
                fog.a *= _CloudColor.a
                    * GetDepthOcclusion(input.positionHCS, input.positionWS);
                return fog;
            }
            ENDHLSL
        }
    }

    FallBack Off
}
