Shader "Custom/SceneHSV"
{
    Properties
    {
        [MainTexture] [NoScaleOffset] _BaseMap ("Color Texture (RGB) Alpha (A)", 2D) = "white" {}
        [MainColor] _BaseColor ("Tint", Color) = (1,1,1,1)
        [Header(Independent UV Transforms)]
        _RGBTiling ("RGB Tiling", Vector) = (1,1,0,0)
        _RGBOffset ("RGB Offset", Vector) = (0,0,0,0)
        _AlphaTiling ("Alpha Tiling", Vector) = (1,1,0,0)
        _AlphaOffset ("Alpha Offset", Vector) = (0,0,0,0)
        [Header(HSV Color Adjustment)]
        _HSVHue ("Hue Shift (Degrees)", Range(-180,180)) = 0
        _HSVSaturation ("Saturation Multiplier", Range(0,2)) = 1
        _HSVValue ("Value Multiplier", Range(0,4)) = 1
        [Header(Surface)]
        _Opacity ("Opacity", Range(0,1)) = 1
        [Toggle(_ALPHATEST_ON)] _AlphaClip ("Alpha Clipping", Float) = 0
        _Cutoff ("Alpha Clip Threshold", Range(0,1)) = 0.5
        [Toggle(_SCENE_UNLIT)] _Unlit ("Unlit Mode", Float) = 0
        _Smoothness ("Smoothness", Range(0,1)) = 0
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull Mode", Float) = 2
    }

    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Transparent" "Queue"="Transparent" }
        Pass
        {
            Name "Forward"
            Tags { "LightMode"="UniversalForwardOnly" }
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha
            ZWrite Off
            Cull [_Cull]

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma shader_feature_local_fragment _SCENE_UNLIT
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile _ _CLUSTERED_LIGHTING
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ USE_LIGHTMAP_SINGLE
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            // Transparent surfaces use world-space shadow sampling, not opaque screen depth.
            #define _SURFACE_TYPE_TRANSPARENT 1
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _RGBTiling;
                float4 _RGBOffset;
                float4 _AlphaTiling;
                float4 _AlphaOffset;
                float _HSVHue;
                float _HSVSaturation;
                float _HSVValue;
                float _Opacity;
                float _AlphaClip;
                float _Cutoff;
                float _Unlit;
                float _Smoothness;
                float _Cull;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                float2 lightmapUV : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                half3 normalWS : TEXCOORD2;
                half4 fogAndVertexLight : TEXCOORD3;
                DECLARE_LIGHTMAP_OR_SH(lightmapUV, vertexSH, 4);
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                VertexPositionInputs position = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = position.positionCS;
                output.positionWS = position.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv = input.uv;
                output.fogAndVertexLight.x = ComputeFogFactor(position.positionCS.z);
                output.fogAndVertexLight.yzw = VertexLighting(position.positionWS, output.normalWS);
                OUTPUT_LIGHTMAP_UV(input.lightmapUV, unity_LightmapST, output.lightmapUV);
                OUTPUT_SH(output.normalWS, output.vertexSH);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 rgbUV = input.uv * _RGBTiling.xy + _RGBOffset.xy;
                float2 alphaUV = input.uv * _AlphaTiling.xy + _AlphaOffset.xy;
                float3 textureColor = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, rgbUV).rgb;
                half3 albedo = textureColor * _BaseColor.rgb;
                half alpha = saturate(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, alphaUV).a * _BaseColor.a * _Opacity);
                #if defined(_ALPHATEST_ON)
                    clip(alpha - _Cutoff);
                #endif
                half3 color = albedo;
                InputData lighting = (InputData)0;
                lighting.fogCoord = InitializeInputDataFog(float4(input.positionWS, 1), input.fogAndVertexLight.x);
                #if !defined(_SCENE_UNLIT)
                    lighting.positionWS = input.positionWS;
                    lighting.normalWS = NormalizeNormalPerPixel(input.normalWS);
                    lighting.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                    lighting.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                    lighting.vertexLighting = input.fogAndVertexLight.yzw;
                    lighting.bakedGI = SAMPLE_GI(input.lightmapUV, input.vertexSH, lighting.normalWS);
                    lighting.shadowMask = SAMPLE_SHADOWMASK(input.lightmapUV);
                    lighting.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                    SurfaceData surface = (SurfaceData)0;
                    surface.albedo = albedo;
                    surface.alpha = alpha;
                    surface.smoothness = _Smoothness;
                    surface.normalTS = half3(0,0,1);
                    surface.occlusion = 1;
                    color = UniversalFragmentPBR(lighting, surface).rgb;
                #endif
                float3 outputColor = MixFog(color, lighting.fogCoord);
                // Grade in display-space HSV after lighting/fog, before blending/post-processing.
                // Bypass neutral settings exactly, without a conversion round trip.
                if (_HSVHue != 0.0 || _HSVSaturation != 1.0 || _HSVValue != 1.0)
                {
                    #if !defined(UNITY_COLORSPACE_GAMMA)
                        outputColor = LinearToSRGB(outputColor);
                    #endif
                    float3 hsv = RgbToHsv(max(outputColor, 0.0));
                    hsv.x = frac(hsv.x + _HSVHue / 360.0);
                    hsv.y = saturate(hsv.y * _HSVSaturation);
                    // Preserve HDR headroom instead of clipping value to one.
                    hsv.z *= _HSVValue;
                    outputColor = HsvToRgb(hsv);
                    #if !defined(UNITY_COLORSPACE_GAMMA)
                        outputColor = SRGBToLinear(outputColor);
                    #endif
                }
                return half4(outputColor, alpha);
            }
            ENDHLSL
        }
    }
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
