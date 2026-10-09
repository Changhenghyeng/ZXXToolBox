Shader "Custom/StylizedWaterfall"
{
    Properties
    {
        [Header(Color)]
        _TopColor ("顶部颜色", Color) = (0.35, 0.8, 0.82, 0.75)
        _BottomColor ("底部颜色", Color) = (0.12, 0.45, 0.5, 0.9)

        [Header(Flow Streaks)]
        [NoScaleOffset] _FlowMap ("流纹贴图", 2D) = "white" {}
        _FlowTilingX ("主流纹平铺 X", Range(0.1, 20)) = 2
        _FlowTilingY ("主流纹平铺 Y", Range(0.1, 30)) = 8
        _FlowSpeed ("主流纹速度", Range(-3, 3)) = 0.8
        _FlowTiling2X ("次流纹平铺 X", Range(0.1, 20)) = 3
        _FlowTiling2Y ("次流纹平铺 Y", Range(0.1, 30)) = 11
        _FlowSpeed2 ("次流纹速度", Range(-3, 3)) = 1.15
        _FlowContrast ("流纹对比度", Range(0.1, 8)) = 2
        _FlowBrightness ("流纹亮度", Range(0, 2)) = 0.5
        _FlowBreakup ("流纹透明变化", Range(0, 1)) = 0.25

        [Header(Surface Normal)]
        [NoScaleOffset][Normal] _NormalMap ("法线贴图", 2D) = "bump" {}
        _NormalTilingX ("主法线平铺 X", Range(0.1, 20)) = 2
        _NormalTilingY ("主法线平铺 Y", Range(0.1, 30)) = 8
        _NormalSpeed ("主法线速度", Range(-3, 3)) = 0.7
        _NormalTiling2X ("次法线平铺 X", Range(0.1, 20)) = 3
        _NormalTiling2Y ("次法线平铺 Y", Range(0.1, 30)) = 12
        _NormalSpeed2 ("次法线速度", Range(-3, 3)) = 1.05
        _NormalStrength ("法线强度", Range(0, 1)) = 0.35

        [Header(Bottom And Intersection Foam)]
        [Toggle(_FOAM_OFF)] _DisableFoam ("关闭底部泡沫", Float) = 0
        _FoamColor ("泡沫颜色", Color) = (0.9, 1, 1, 0.9)
        _BottomFoamWidth ("底部泡沫宽度", Range(0.001, 0.5)) = 0.12
        _IntersectionFoamWidth ("穿插泡沫宽度", Range(0.01, 2)) = 0.3
        _FoamStrength ("泡沫强度", Range(0, 2)) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderType"="Transparent"
            "RenderPipeline"="UniversalPipeline"
            "Queue"="Transparent"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma shader_feature_local_fragment _FOAM_OFF
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile_fog

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            TEXTURE2D(_FlowMap); SAMPLER(sampler_FlowMap);
            TEXTURE2D(_NormalMap); SAMPLER(sampler_NormalMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _TopColor;
                float4 _BottomColor;
                float4 _FoamColor;
                float _FlowTilingX, _FlowTilingY, _FlowSpeed;
                float _FlowTiling2X, _FlowTiling2Y, _FlowSpeed2;
                float _FlowContrast, _FlowBrightness, _FlowBreakup;
                float _NormalTilingX, _NormalTilingY, _NormalSpeed;
                float _NormalTiling2X, _NormalTiling2Y, _NormalSpeed2;
                float _NormalStrength;
                float _DisableFoam, _BottomFoamWidth;
                float _IntersectionFoamWidth, _FoamStrength;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                half3 tangentWS : TEXCOORD2;
                half3 bitangentWS : TEXCOORD3;
                float2 uv : TEXCOORD4;
                half fogFactor : TEXCOORD5;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                VertexPositionInputs positionInputs = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(
                    IN.normalOS, IN.tangentOS);

                OUT.positionCS = positionInputs.positionCS;
                OUT.positionWS = positionInputs.positionWS;
                OUT.normalWS = normalize(normalInputs.normalWS);
                OUT.tangentWS = normalize(normalInputs.tangentWS
                    - OUT.normalWS * dot(normalInputs.tangentWS, OUT.normalWS));
                OUT.bitangentWS = normalize(cross(OUT.normalWS, OUT.tangentWS))
                    * IN.tangentOS.w * GetOddNegativeScale();
                OUT.uv = IN.uv;
                OUT.fogFactor = ComputeFogFactor(positionInputs.positionCS.z);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);

                float time = _Time.y;

                float2 flowUV1 = IN.uv * float2(_FlowTilingX, _FlowTilingY);
                flowUV1.y -= time * _FlowSpeed;
                float2 flowUV2 = IN.uv * float2(_FlowTiling2X, _FlowTiling2Y)
                    + float2(0.37, 0.71);
                flowUV2.y -= time * _FlowSpeed2;

                half flow1 = SAMPLE_TEXTURE2D(
                    _FlowMap, sampler_FlowMap, flowUV1).r;
                half flow2 = SAMPLE_TEXTURE2D(
                    _FlowMap, sampler_FlowMap, flowUV2).r;
                half flowPattern = saturate(flow1 * flow2 * 2.0h);
                flowPattern = saturate(
                    (flowPattern - 0.5h) * _FlowContrast + 0.5h);

                float2 normalUV1 = IN.uv
                    * float2(_NormalTilingX, _NormalTilingY);
                normalUV1.y -= time * _NormalSpeed;
                float2 normalUV2 = IN.uv
                    * float2(_NormalTiling2X, _NormalTiling2Y)
                    + float2(0.53, 0.19);
                normalUV2.y -= time * _NormalSpeed2;

                half3 normal1 = UnpackNormal(SAMPLE_TEXTURE2D(
                    _NormalMap, sampler_NormalMap, normalUV1));
                half3 normal2 = UnpackNormal(SAMPLE_TEXTURE2D(
                    _NormalMap, sampler_NormalMap, normalUV2));

                half3 rnmBase = normal1 + half3(0.0h, 0.0h, 1.0h);
                half3 rnmDetail = normal2 * half3(-1.0h, -1.0h, 1.0h);
                half3 normalTS = normalize(
                    rnmBase * dot(rnmBase, rnmDetail)
                    - rnmDetail * rnmBase.z);

                half3x3 TBN = half3x3(
                    IN.tangentWS, IN.bitangentWS, IN.normalWS);
                half3 detailNormalWS = normalize(mul(normalTS, TBN));
                half3 normalWS = normalize(lerp(
                    IN.normalWS, detailNormalWS, _NormalStrength));

                half gradient = saturate(IN.uv.y);
                half4 waterfallColor = lerp(
                    _BottomColor, _TopColor, gradient);

                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                half NdotL = saturate(dot(normalWS, mainLight.direction));
                half3 lighting = SampleSH(normalWS)
                    + mainLight.color * NdotL
                    * mainLight.distanceAttenuation
                    * mainLight.shadowAttenuation;

                half3 color = waterfallColor.rgb * lighting;
                color += color * flowPattern * _FlowBrightness;

                half alpha = waterfallColor.a * lerp(
                    1.0h, flowPattern, _FlowBreakup);

                #if !defined(_FOAM_OFF)
                half bottomFoam = 1.0h - smoothstep(
                    0.0h, _BottomFoamWidth, IN.uv.y);

                float2 screenUV = GetNormalizedScreenSpaceUV(IN.positionCS);
                float rawSceneDepth = SampleSceneDepth(screenUV);
                float sceneEyeDepth = unity_OrthoParams.w == 0.0
                    ? LinearEyeDepth(rawSceneDepth, _ZBufferParams)
                    : LinearDepthToEyeDepth(rawSceneDepth);
                float surfaceEyeDepth = LinearEyeDepth(
                    IN.positionWS, GetWorldToViewMatrix());
                float intersectionDepth = max(
                    0.0, sceneEyeDepth - surfaceEyeDepth);
                half intersectionFoam = 1.0h - smoothstep(
                    0.0h, _IntersectionFoamWidth, intersectionDepth);

                half foamNoise = saturate(0.35h + flowPattern * 0.65h);
                half foamMask = saturate(
                    max(bottomFoam, intersectionFoam)
                    * foamNoise * _FoamStrength);
                half foamBlend = foamMask * _FoamColor.a;
                color = lerp(color, _FoamColor.rgb, foamBlend);
                alpha = lerp(alpha, 1.0h, foamBlend);
                #endif

                color = MixFog(color, IN.fogFactor);
                return half4(color, saturate(alpha));
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Simple Lit"
}
