Shader "Custom/Fog Of War"
{
    Properties
    {
        _MainTex ("Mask Texture", 2D) = "white" {}
        _CloudTex ("Cloud Texture", 2D) = "white" {}
        [HDR] _CloudColor ("Cloud Color", Color) = (1, 1, 1, 0.55)
        _ShadowColor ("Shadow Color", Color) = (0, 0, 0, 0.6)

        _TilingFactor ("Cloud Layer 1 Tiling Factor", Float) = 2
        _SecondTilingFactor ("Cloud Layer 2 Tiling Factor", Float) = 1
        _CloudLayer1SpeedX ("Cloud Layer 1 Speed X", Float) = 0.018
        _CloudLayer1SpeedY ("Cloud Layer 1 Speed Y", Float) = 0.011
        _CloudLayer2SpeedX ("Cloud Layer 2 Speed X", Float) = -0.013
        _CloudLayer2SpeedY ("Cloud Layer 2 Speed Y", Float) = -0.017
        _CloudAlphaPower ("Cloud Alpha Power", Float) = 1.25

        _MaskEdgeSoftness ("Mask Edge Softness", Range(0.001, 0.49)) = 0.05
        _MaskEdgeContrast ("Mask Edge Contrast", Range(0.1, 1)) = 1
        _ShadeOffset ("Mask Distort Correction XY Strength", Vector) = (0.5, 0.5, 1, 0)

        _ShadowOffsetX ("Shadow Offset X", Float) = 0
        _ShadowOffsetY ("Shadow Offset Y", Float) = 0

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

        CBUFFER_START(UnityPerMaterial)
            float4 _MainTex_ST;
            float4 _CloudTex_ST;
            half4 _CloudColor;
            half4 _ShadowColor;
            float _TilingFactor;
            float _SecondTilingFactor;
            float _CloudLayer1SpeedX;
            float _CloudLayer1SpeedY;
            float _CloudLayer2SpeedX;
            float _CloudLayer2SpeedY;
            float _CloudAlphaPower;
            float _MaskEdgeSoftness;
            float _MaskEdgeContrast;
            float4 _ShadeOffset;
            float _ShadowOffsetX;
            float _ShadowOffsetY;
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

        // Remap the bilinear mask edge around 0.5, then shape it with a contrast curve.
        float ApplyMaskEdgeSoftness(float mask, float softness, float contrast)
        {
            float halfWidth = saturate(softness);
            float edge0 = max(0.0001, 0.5 - halfWidth);
            float edge1 = min(0.9999, 0.5 + halfWidth);
            float s = saturate(smoothstep(edge0, edge1, mask));

            // The material range is kept artist-friendly. Internally multiply to make the curve more visible.
            float c = saturate(contrast) * 3.0;
            float lo = 0.5 * pow(abs(s / 0.5), c);
            float hi = 1.0 - 0.5 * pow(abs((1.0 - s) / 0.5), c);
            return lerp(lo, hi, step(0.5, s));
        }

        // Blend two moving samples from the same tileable cloud texture.
        half4 BlendTwoCloud(float2 uv)
        {
            float2 layer1UV = uv * _TilingFactor + _Time.x * float2(_CloudLayer1SpeedX, _CloudLayer1SpeedY);
            float2 layer2UV = uv * _SecondTilingFactor + _Time.x * float2(_CloudLayer2SpeedX, _CloudLayer2SpeedY) + float2(0.5, 0.5);

            half4 c = SAMPLE_TEXTURE2D(_CloudTex, sampler_CloudTex, layer1UV);
            half4 c2 = SAMPLE_TEXTURE2D(_CloudTex, sampler_CloudTex, layer2UV);
            c.rgb = (c.rgb + c2.rgb) * 0.5h;
            c.a = (c.a + c2.a) * 0.5h;
            return c;
        }

        // Use cloud luminance to perturb mask UVs.
        // _ShadeOffset.xy compensates the average offset, _ShadeOffset.z controls strength.
        float2 GetMaskDistortOffset(half4 cloud)
        {
            float cloudGray = (cloud.r + cloud.g + cloud.b) * 0.3333333;
            return float2(cloudGray - _ShadeOffset.x, cloudGray - _ShadeOffset.y) * _ShadeOffset.z * 0.01;
        }

        half SampleFogMask(float2 maskUV)
        {
            half fogMask = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, maskUV).r;
            return ApplyMaskEdgeSoftness(fogMask, _MaskEdgeSoftness, _MaskEdgeContrast);
        }

        // Optional high-quality scene-depth occlusion.
        // The pass uses ZTest Always so hard depth-test clipping does not happen before this code runs.
        // Scene depth is reconstructed to world space, then compared along the camera forward axis.
        // This path behaves consistently for both perspective and orthographic cameras.
        // Requires URP Depth Texture. Disable this feature when the camera/URP asset does not provide depth.
        half GetDepthOcclusion(float4 positionHCS, float3 fogWorldPos)
        {
            half enabled = step(0.5h, (half)_EnableDepthOcclusion);
            if (enabled <= 0.0h)
            {
                return 1.0h;
            }

            float2 screenUV = GetNormalizedScreenSpaceUV(positionHCS);
            float sceneRawDepth = SampleSceneDepth(screenUV);
            float3 sceneWorldPos = ComputeWorldSpacePosition(screenUV, sceneRawDepth, UNITY_MATRIX_I_VP);
            float3 cameraForwardWS = -UNITY_MATRIX_V[2].xyz;

            // Positive when opaque geometry is closer to the camera than the fog plane.
            float occluderDepth = dot(fogWorldPos - sceneWorldPos, cameraForwardWS) - _DepthOcclusionOffset;
            half occlusion = occluderDepth > 0.0
                ? saturate(occluderDepth / max(0.0001, _DepthOcclusionDistance))
                : 0.0h;

            half alphaScale = 1.0h - occlusion;
            return lerp(1.0h, alphaScale, saturate((half)_DepthOcclusionStrength));
        }
        ENDHLSL

        // Shadow pass must render before the cloud color pass. It gives the fog more visual depth.
        Pass
        {
            Name "CloudShadow"
            Tags { "LightMode" = "SRPDefaultUnlit" }

            HLSLPROGRAM
            #pragma vertex VertDefault
            #pragma fragment FragShadow

            half4 FragShadow(Varyings input) : SV_Target
            {
                float2 cloudUV = input.uv * _CloudTex_ST.xy + _CloudTex_ST.zw;
                half4 cloud = BlendTwoCloud(cloudUV);

                float2 maskUV = input.uv * _MainTex_ST.xy + _MainTex_ST.zw;
                maskUV += float2(_ShadowOffsetX, _ShadowOffsetY) * 0.001;
                maskUV += GetMaskDistortOffset(cloud);

                half fogMask = SampleFogMask(maskUV);
                half alpha = _ShadowColor.a * fogMask * GetDepthOcclusion(input.positionHCS, input.positionWS);
                return half4(_ShadowColor.rgb, alpha);
            }
            ENDHLSL
        }

        // Main visible cloud pass. RGB comes from the cloud texture, alpha comes from mask * cloud alpha.
        Pass
        {
            Name "CloudColor"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex VertDefault
            #pragma fragment FragCloud

            half4 FragCloud(Varyings input) : SV_Target
            {
                float2 maskUV = input.uv * _MainTex_ST.xy + _MainTex_ST.zw;
                float2 cloudUV = input.uv * _CloudTex_ST.xy + _CloudTex_ST.zw;
                half4 cloud = BlendTwoCloud(cloudUV);

                half fogMask = SampleFogMask(maskUV + GetMaskDistortOffset(cloud));
                half cloudTexAlpha = saturate(pow(abs(saturate(cloud.a)), max(0.0001, _CloudAlphaPower)));
                half alpha = _CloudColor.a * fogMask * cloudTexAlpha * GetDepthOcclusion(input.positionHCS, input.positionWS);
                half3 color = cloud.rgb * _CloudColor.rgb;
                return half4(color, alpha);
            }
            ENDHLSL
        }
    }

    FallBack Off
}
