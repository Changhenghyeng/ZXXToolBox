Shader "Custom/SimpleTopBlend"
{
    Properties
    {
        [Header(Base Material)]
        [MainColor] _BaseColor ("基础颜色", Color) = (1, 1, 1, 1)
        [MainTexture] _BaseMap ("基础贴图", 2D) = "white" {}
        [Normal] _BaseNormalMap ("基础法线", 2D) = "bump" {}
        _BaseNormalStrength ("基础法线强度", Range(0, 2)) = 1

        [Space]
        [Header(Top Projection)]
        _TopColor ("顶部覆盖颜色", Color) = (1, 1, 1, 1)
        [NoScaleOffset] _TopMap ("顶部覆盖贴图（四方连续）", 2D) = "white" {}
        [NoScaleOffset][Normal] _TopNormalMap ("顶部覆盖法线", 2D) = "bump" {}
        _TopNormalStrength ("顶部法线强度", Range(0, 2)) = 1
        _TopTiling ("顶部贴图世界平铺", Float) = 1
        [Enum(TopDown, 0, BottomUp, 1)] _ProjectionDirection ("映射方向（TopDown / BottomUp）", Float) = 0
        _TopThreshold ("覆盖坡度", Range(-1, 1)) = 0.45
        _TopBlend ("边缘过渡", Range(0.001, 1)) = 0.15

        [Space]
        [Header(Bottom Height Gradient)]
        [ToggleUI] _BottomGradientLocalSpace ("使用模型本地空间", Float) = 0
        _BottomGradientColor ("底部渐变颜色（Alpha=强度）", Color) = (1, 1, 1, 0)
        [NoScaleOffset] _BottomGradientMap ("底部渐变贴图", 2D) = "white" {}
        _BottomGradientStart ("底部渐变起始世界高度", Float) = 0
        _BottomGradientEnd ("底部渐变结束世界高度", Float) = 1

        [Space]
        [Header(Lighting)]
        [ToggleUI] _UseBakedMap ("使用 Baked Light Map", Float) = 0
        [NoScaleOffset] _BakedMap ("烘焙贴图（R=自阴影 G=AO）", 2D) = "white" {}
        _ShadowStrength ("自阴影强度", Range(0, 1)) = 0.4
        [ToggleUI] _UseAO ("使用 AO", Float) = 1
        _AOStrength ("AO 强度", Range(0, 1)) = 1
        _Smoothness ("光滑度", Range(0, 1)) = 0.25
    }

    SubShader
    {
        Tags
        {
            "RenderType"="Opaque"
            "RenderPipeline"="UniversalPipeline"
            "Queue"="Geometry"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BaseNormalMap); SAMPLER(sampler_BaseNormalMap);
            TEXTURE2D(_TopMap); SAMPLER(sampler_TopMap);
            TEXTURE2D(_TopNormalMap); SAMPLER(sampler_TopNormalMap);
            TEXTURE2D(_BottomGradientMap); SAMPLER(sampler_BottomGradientMap);
            TEXTURE2D(_BakedMap); SAMPLER(sampler_BakedMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseNormalMap_ST;
                float4 _BaseColor;
                float4 _TopColor;
                float _BaseNormalStrength;
                float _TopNormalStrength;
                float _TopTiling;
                float _ProjectionDirection;
                float _TopThreshold;
                float _TopBlend;
                float _BottomGradientLocalSpace;
                float4 _BottomGradientColor;
                float _BottomGradientStart;
                float _BottomGradientEnd;
                float _UseBakedMap;
                float _ShadowStrength;
                float _UseAO;
                float _AOStrength;
                float _Smoothness;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 uv : TEXCOORD0;
                float2 uv1 : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 tangentWS : TEXCOORD2;
                float3 bitangentWS : TEXCOORD3;
                float2 uv : TEXCOORD4;
                float2 normalUV : TEXCOORD5;
                float fogFactor : TEXCOORD6;
                float positionOSY : TEXCOORD7;
                float2 bakedUV : TEXCOORD8;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                VertexPositionInputs positionInputs = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(IN.normalOS, IN.tangentOS);
                OUT.positionCS = positionInputs.positionCS;
                OUT.positionWS = positionInputs.positionWS;
                OUT.normalWS = normalInputs.normalWS;
                OUT.tangentWS = normalInputs.tangentWS;
                OUT.bitangentWS = normalInputs.bitangentWS;
                OUT.uv = TRANSFORM_TEX(IN.uv, _BaseMap);
                OUT.normalUV = TRANSFORM_TEX(IN.uv, _BaseNormalMap);
                OUT.bakedUV = IN.uv1;
                OUT.fogFactor = ComputeFogFactor(positionInputs.positionCS.z);
                OUT.positionOSY = IN.positionOS.y;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);

                float3 geometricNormalWS = normalize(IN.normalWS);
                float3 viewDirectionWS = SafeNormalize(GetWorldSpaceViewDir(IN.positionWS));

                half4 baseSample = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv) * _BaseColor;

                // World-space XZ projection keeps a seamless snow/moss pattern
                // aligned to world up even after the model is rotated.
                float2 topUV = IN.positionWS.xz * _TopTiling;
                half4 topSample = SAMPLE_TEXTURE2D(_TopMap, sampler_TopMap, topUV) * _TopColor;

                half3 baseNormalTS = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_BaseNormalMap, sampler_BaseNormalMap, IN.normalUV),
                    _BaseNormalStrength);
                half3x3 baseTBN = half3x3(
                    normalize(IN.tangentWS),
                    normalize(IN.bitangentWS),
                    geometricNormalWS);
                half3 baseNormalWS = normalize(TransformTangentToWorld(baseNormalTS, baseTBN));

                // Snow/moss deposition follows the actual detailed surface
                // direction, including the base normal map, not only vertices.
                float projectionDirectionY = 1.0 - 2.0 * _ProjectionDirection;
                float projectionNormalY = baseNormalWS.y * projectionDirectionY;
                float topMask = smoothstep(
                    _TopThreshold - _TopBlend,
                    _TopThreshold + _TopBlend,
                    projectionNormalY);
                topMask *= _TopColor.a;

                float bottomGradientRange = max(
                    _BottomGradientEnd - _BottomGradientStart,
                    0.0001);
                float bottomGradientHeight = lerp(
                    IN.positionWS.y,
                    IN.positionOSY,
                    saturate(_BottomGradientLocalSpace));
                float bottomGradientMask = 1.0 - saturate(
                    (bottomGradientHeight - _BottomGradientStart) / bottomGradientRange);
                half3 bottomGradientSample = SAMPLE_TEXTURE2D(
                    _BottomGradientMap,
                    sampler_BottomGradientMap,
                    IN.uv).rgb * _BottomGradientColor.rgb;
                half3 baseWithBottomGradient = lerp(
                    baseSample.rgb,
                    bottomGradientSample,
                    saturate(bottomGradientMask * _BottomGradientColor.a));

                half3 albedo = lerp(baseWithBottomGradient, topSample.rgb, saturate(topMask));

                // Match BakedLightMap.shadergraph: UV1, R = baked self-shadow, G = AO.
                half4 bakedSample = SAMPLE_TEXTURE2D(_BakedMap, sampler_BakedMap, IN.bakedUV);
                half useBakedMap = saturate(_UseBakedMap);
                half bakedShadow = lerp(1.0h, bakedSample.r,
                    saturate(_ShadowStrength) * useBakedMap);
                half bakedAO = lerp(1.0h, bakedSample.g,
                    saturate(_AOStrength) * saturate(_UseAO) * useBakedMap);
                albedo *= bakedShadow;

                half3 topNormalTS = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_TopNormalMap, sampler_TopNormalMap, topUV),
                    _TopNormalStrength);
                // Top UV uses world XZ: tangent=X, bitangent=Z, normal=Y.
                half3 topNormalWS = normalize(half3(
                    topNormalTS.x,
                    topNormalTS.z,
                    topNormalTS.y * projectionDirectionY));
                half3 normalWS = normalize(lerp(baseNormalWS, topNormalWS, saturate(topMask)));

                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                half NdotL = saturate(dot(normalWS, mainLight.direction));
                half3 lighting = SampleSH(normalWS) * bakedAO
                    + mainLight.color * NdotL * mainLight.shadowAttenuation;

                half3 halfDirection = SafeNormalize(mainLight.direction + viewDirectionWS);
                half specular = pow(saturate(dot(normalWS, halfDirection)),
                    lerp(8.0, 128.0, _Smoothness));
                half3 color = albedo * lighting
                    + mainLight.color * specular * _Smoothness * mainLight.shadowAttenuation;
                color = MixFog(color, IN.fogFactor);
                return half4(color, 1.0);
            }
            ENDHLSL
        }

        UsePass "Universal Render Pipeline/Lit/ShadowCaster"
        UsePass "Universal Render Pipeline/Lit/DepthOnly"
    }

    FallBack "Universal Render Pipeline/Lit"
}
