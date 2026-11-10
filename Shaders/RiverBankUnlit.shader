Shader "Custom/RiverBankUnlit"
{
    Properties
    {
        [MainTexture] _BaseMap ("河岸贴图", 2D) = "white" {}
        [MainColor] _BaseColor ("河岸颜色", Color) = (1, 1, 1, 1)
        _ShadowStrength ("接收阴影强度", Range(0, 1)) = 0.6
        _CutCenter ("中央裁剪位置 (横向 UV)", Range(0, 1)) = 0.5
        _CutWidth ("中央裁剪宽度 (占总宽比例，0关闭)", Range(0, 1)) = 0.2
        _LeftFadeWidth ("左外侧渐变宽度 (占总宽比例)", Range(0, 0.5)) = 0.08
        _RightFadeWidth ("右外侧渐变宽度 (占总宽比例)", Range(0, 0.5)) = 0.08
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Unlit"
            "RenderType" = "Transparent"
            "Queue" = "Transparent-1"
        }

        Pass
        {
            Name "Transparent"
            Tags { "LightMode" = "UniversalForwardOnly" }
            Cull Back
            ZTest LEqual
            ZWrite Off
            Blend SrcAlpha OneMinusSrcAlpha, One OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex BankVertex
            #pragma fragment BankFragment
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            // Transparent receivers must sample the light's shadow atlas instead
            // of the opaque screen-depth shadow result, including after flattening.
            #define _SURFACE_TYPE_TRANSPARENT 1
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                float _CutCenter, _CutWidth;
                float _LeftFadeWidth, _RightFadeWidth;
                half _ShadowStrength;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float4 projectionData : TEXCOORD7;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 textureUV : TEXCOORD0;
                float widthUV : TEXCOORD1;
                float fogFactor : TEXCOORD2;
                float projectionWeight : TEXCOORD3;
                float3 positionWS : TEXCOORD4;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings BankVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                // Camera-flattened meshes carry the original perspective weights
                // in UV7; ordinary meshes have no marker and use weight 1.
                bool projected = input.projectionData.z > 0.5;
                float weight = projected ? max(0.000001, input.projectionData.x) : 1.0;
                output.projectionWeight = weight;
                output.textureUV = TRANSFORM_TEX(input.uv, _BaseMap) * weight;
                // The river source uses UV0.x across its width. Keep clipping and
                // edge fades independent of the texture's tiling and offset.
                output.widthUV = input.uv.x * weight;
                output.fogFactor = ComputeFogFactor(projected ? input.projectionData.y : output.positionCS.z) * weight;
                return output;
            }

            half4 BankFragment(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float inverseWeight = rcp(max(0.000001, input.projectionWeight));
                float widthUV = input.widthUV * inverseWeight;
                if (_CutWidth > 0.0)
                    clip(abs(widthUV - _CutCenter) - _CutWidth * 0.5);

                float left = _LeftFadeWidth > 0.0
                    ? smoothstep(0.0, max(0.00001, _LeftFadeWidth), widthUV) : 1.0;
                float right = _RightFadeWidth > 0.0
                    ? smoothstep(0.0, max(0.00001, _RightFadeWidth), 1.0 - widthUV) : 1.0;
                half3 color = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.textureUV * inverseWeight).rgb;
                color *= _BaseColor.rgb;
                // Resolve the cascade per pixel on the actual receiving surface.
                // This only darkens the unlit texture; alpha and cutouts stay intact.
                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                half shadow = MainLightShadow(shadowCoord, input.positionWS,
                    half4(1, 1, 1, 1), half4(0, 0, 0, 0));
                color *= lerp(1.0h, shadow, saturate(_ShadowStrength));
                return half4(MixFog(color, input.fogFactor * inverseWeight), saturate(_BaseColor.a * left * right));
            }
            ENDHLSL
        }
    }
    FallBack Off
}
