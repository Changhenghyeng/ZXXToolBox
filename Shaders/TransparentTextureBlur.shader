Shader "Custom/Opaque Lit Texture Blur"
{
    Properties
    {
        [MainTexture] _BaseMap ("颜色贴图", 2D) = "white" {}
        [MainColor] _BaseColor ("颜色", Color) = (1,1,1,1)
        _BlurRadius ("模糊半径 (贴图像素)", Range(0,32)) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            Name "OpaqueLitBlur"
            Tags { "LightMode"="UniversalForwardOnly" }
            Cull Back
            ZWrite On
            ZTest LEqual
            Blend One Zero

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ _FORWARD_PLUS
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            float4 _BaseMap_TexelSize;
            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                float _BlurRadius;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR;
                float3 positionWS : TEXCOORD1;
                half3 normalWS : TEXCOORD2;
                half fogFactor : TEXCOORD3;
            };
            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.color = input.color;
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.fogFactor = ComputeFogFactor(output.positionCS.z);
                return output;
            }

            // Opaque surface: blur RGB only and respect the texture wrap mode.
            half4 ReadColor(float2 uv)
            {
                return half4(SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv).rgb, 1);
            }

            half4 Frag(Varyings input) : SV_Target
            {
                float4 sum = 0;
                if (_BlurRadius < 0.001)
                    sum = ReadColor(input.uv);
                else
                {
                    float weightSum = 0;
                    float2 stride = abs(_BaseMap_TexelSize.xy) * (_BlurRadius / 3.0);
                    // 7x7 Gaussian kernel in texture-pixel units.
                    [unroll] for (int y = -3; y <= 3; ++y)
                    {
                        [unroll] for (int x = -3; x <= 3; ++x)
                        {
                            float weight = exp(-float(x*x + y*y) / 4.5);
                            sum += ReadColor(input.uv + float2(x,y) * stride) * weight;
                            weightSum += weight;
                        }
                    }
                    sum /= weightSum;
                }
                InputData lightingInput = (InputData)0;
                lightingInput.positionWS = input.positionWS;
                lightingInput.normalWS = NormalizeNormalPerPixel(input.normalWS);
                lightingInput.viewDirectionWS = GetWorldSpaceNormalizeViewDir(input.positionWS);
                #if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                    lightingInput.shadowCoord = ComputeScreenPos(TransformWorldToHClip(input.positionWS));
                #else
                    lightingInput.shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                #endif
                lightingInput.bakedGI = SampleSH(lightingInput.normalWS);
                lightingInput.vertexLighting = VertexLighting(input.positionWS, lightingInput.normalWS);
                lightingInput.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                lightingInput.shadowMask = half4(1,1,1,1);
                SurfaceData surface = (SurfaceData)0;
                surface.albedo = sum.rgb * _BaseColor.rgb * input.color.rgb;
                surface.alpha = 1;
                surface.occlusion = 1;
                surface.normalTS = half3(0,0,1);
                half4 color = UniversalFragmentBlinnPhong(lightingInput, surface);
                color.rgb = MixFog(color.rgb, input.fogFactor);
                return half4(color.rgb, 1);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
