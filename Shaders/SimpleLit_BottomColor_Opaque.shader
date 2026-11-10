Shader "Custom/SimpleLit_BottomColor_Opaque"
{
    Properties
    {
        _BaseMap   ("Albedo (Base Map)", 2D)         = "white" {}
        _BaseColor ("Base Color", Color)             = (1,1,1,1)

        [Header(Lighting)]
        [Toggle] _UnlitMode ("Unlit Mode", Float)    = 0

        [Header(Normal)]
        _NormalStrength ("Normal", Range(0, 1))       = 0
        [Normal] [NoScaleOffset] _NormalMap ("Normal Map", 2D) = "bump" {}

        [Header(Smoothness)]
        _Smoothness ("Smoothness", Range(0, 1))       = 0
        [NoScaleOffset] _MetallicSmoothnessMap ("Metallic (RGB) Smoothness (A) Map", 2D) = "black" {}

        [Header(Bottom Color)]
        _BottomColor ("Bottom Color (A = Strength)", Color) = (0.25,0.25,0.25,0.5)
        [Toggle] _UseRendererBounds ("Use Model Bounds (模型范围模式)", Float) = 1
        _FadeStart ("Fade Start (世界Y / 模型比例)", Float) = 0.0
        _FadeRange ("Fade Range (世界宽度 / 模型比例)", Float) = 1.0
        _FadeRotation ("Fade Rotation (Degrees)", Range(-180, 180)) = 0
        _FadeBend ("Fade Bend", Float) = 0

        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull Mode", Float) = 2
    }

    SubShader
    {
        Tags
        {
            "RenderType"     = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue"          = "Geometry"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Blend Off
            ZWrite On
            ZTest LEqual
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_NormalMap);
            SAMPLER(sampler_NormalMap);
            TEXTURE2D(_MetallicSmoothnessMap);
            SAMPLER(sampler_MetallicSmoothnessMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float4 _BottomColor;
                float  _UnlitMode;
                float  _NormalStrength;
                float  _Smoothness;
                float  _UseRendererBounds;
                float  _FadeStart;
                float  _FadeRange;
                float  _FadeRotation;
                float  _FadeBend;
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
                float4 positionCS : SV_POSITION;
                float2 uv         : TEXCOORD0;
                float3 positionWS : TEXCOORD1;
                float3 normalWS   : TEXCOORD2;
                float3 viewDirWS  : TEXCOORD3;
                float  fogFactor  : TEXCOORD4;
                float4 tangentWS  : TEXCOORD5;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                VertexPositionInputs posInputs  = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs   normInputs = GetVertexNormalInputs(IN.normalOS, IN.tangentOS);

                OUT.positionCS = posInputs.positionCS;
                OUT.positionWS = posInputs.positionWS;
                OUT.normalWS   = normInputs.normalWS;
                OUT.tangentWS  = float4(normInputs.tangentWS, IN.tangentOS.w * GetOddNegativeScale());
                OUT.viewDirWS  = GetWorldSpaceViewDir(posInputs.positionWS);
                OUT.uv         = TRANSFORM_TEX(IN.uv, _BaseMap);
                OUT.fogFactor  = ComputeFogFactor(posInputs.positionCS.z);

                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);

                half3 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv).rgb * _BaseColor.rgb;

                // 以模型中心 X 和 Fade Start 高度为轴心，在世界 XY 平面塑造渐变。
                float rendererHeight = max(unity_RendererBounds_Max.y - unity_RendererBounds_Min.y, 0.0001);
                float centerX = (unity_RendererBounds_Min.x + unity_RendererBounds_Max.x) * 0.5;
                float2 gradientXY = float2(IN.positionWS.x - centerX, IN.positionWS.y);
                float2 boundsXY = float2(gradientXY.x / rendererHeight,
                    saturate((gradientXY.y - unity_RendererBounds_Min.y) / rendererHeight));
                gradientXY = lerp(gradientXY, boundsXY, step(0.5, _UseRendererBounds));
                gradientXY.y -= _FadeStart;

                float rotationSin, rotationCos;
                sincos(radians(_FadeRotation), rotationSin, rotationCos);
                float across = dot(gradientXY, float2(rotationCos, rotationSin));
                float height = dot(gradientXY, float2(-rotationSin, rotationCos));
                // 正值使渐变边界的两侧抬高，负值使两侧下压。
                float fadePosition = height - _FadeBend * across * across;
                float fadeT = smoothstep(0.0, max(_FadeRange, 0.0001), fadePosition);
                half overlay = (1.0h - fadeT) * saturate(_BottomColor.a);
                albedo = lerp(albedo, _BottomColor.rgb, overlay);

                // 参数为 0 时保持原来的顶点法线；为 1 时完整使用法线贴图。
                half3 normalTS = UnpackNormal(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap, IN.uv));
                normalTS = normalize(lerp(half3(0.0h, 0.0h, 1.0h), normalTS, (half)_NormalStrength));

                half3 normalWS   = normalize(IN.normalWS);
                half3 tangentWS  = normalize(IN.tangentWS.xyz);
                half3 bitangentWS = normalize(cross(normalWS, tangentWS) * IN.tangentWS.w);
                normalWS = normalize(mul(normalTS, half3x3(tangentWS, bitangentWS, normalWS)));

                // 打包贴图：RGB 为金属度，Alpha 为 Smoothness。
                half4 metallicSmoothness = SAMPLE_TEXTURE2D(
                    _MetallicSmoothnessMap, sampler_MetallicSmoothnessMap, IN.uv);
                half metallic = saturate(metallicSmoothness.r);
                half smoothness = saturate((half)_Smoothness * metallicSmoothness.a);

                InputData inputData;
                ZERO_INITIALIZE(InputData, inputData);
                inputData.positionWS      = IN.positionWS;
                inputData.normalWS        = normalWS;
                inputData.viewDirectionWS = normalize(IN.viewDirWS);
                inputData.fogCoord        = IN.fogFactor;
                inputData.shadowCoord     = TransformWorldToShadowCoord(IN.positionWS);
                inputData.bakedGI         = SampleSH(inputData.normalWS);

                SurfaceData surfaceData;
                ZERO_INITIALIZE(SurfaceData, surfaceData);
                surfaceData.albedo    = albedo;
                surfaceData.alpha     = 1.0h;
                // SimpleLit 使用镜面反射颜色，因此将金属度转换到它的高光输入。
                // Smoothness 为 0 时仍保持原 Shader 的“无高光”效果。
                surfaceData.metallic  = metallic;
                surfaceData.specular  = lerp(half3(0.04h, 0.04h, 0.04h), albedo, metallic)
                                      * step(0.0001h, smoothness);
                surfaceData.smoothness = smoothness;
                surfaceData.normalTS  = normalTS;
                surfaceData.occlusion = 1.0;

                half4 color = UniversalFragmentBlinnPhong(inputData, surfaceData);
                // 默认使用 SimpleLit；开启 Unlit Mode 后直接输出基础颜色。
                color.rgb   = lerp(color.rgb, albedo, step(0.5h, (half)_UnlitMode));
                color.rgb   = MixFog(color.rgb, IN.fogFactor);
                color.a     = 1.0h;

                return color;
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Simple Lit"
}
