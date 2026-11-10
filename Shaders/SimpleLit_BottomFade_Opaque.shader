Shader "Custom/SimpleLit_BottomFade_Opaque"
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

        [Header(Ambient Occlusion)]
        [NoScaleOffset] _OcclusionMap ("AO Map (G)", 2D) = "white" {}
        _OcclusionStrength ("AO Strength", Range(0, 1)) = 1

        [Header(Bottom Fade)]
        [Toggle] _UseRendererBounds ("Use Model Bounds (模型范围模式)", Float) = 0
        _FadeStart ("Fade Start (世界Y / 模型比例)", Float) = 0.0
        _FadeRange ("Fade Range (世界宽度 / 模型比例)", Float) = 1.0

        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull Mode", Float) = 2
    }

    SubShader
    {
        // 两个颜色 Pass 在同一队列绘制；在普通不透明物体之后合成渐隐。
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry+500" }
        HLSLINCLUDE
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_NormalMap);
            SAMPLER(sampler_NormalMap);
            TEXTURE2D(_MetallicSmoothnessMap);
            SAMPLER(sampler_MetallicSmoothnessMap);
            TEXTURE2D(_OcclusionMap);
            SAMPLER(sampler_OcclusionMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float  _UnlitMode;
                float  _NormalStrength;
                float  _Smoothness;
                float  _OcclusionStrength;
                float  _UseRendererBounds;
                float  _FadeStart;
                float  _FadeRange;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                float2 staticLightmapUV : TEXCOORD1;
                float2 dynamicLightmapUV : TEXCOORD2;
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
                DECLARE_LIGHTMAP_OR_SH(staticLightmapUV, vertexSH, 6);
                #ifdef DYNAMICLIGHTMAP_ON
                    float2 dynamicLightmapUV : TEXCOORD7;
                #endif
                #ifdef _ADDITIONAL_LIGHTS_VERTEX
                    half3 vertexLighting : TEXCOORD8;
                #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);

                VertexPositionInputs posInputs  = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs   normInputs = GetVertexNormalInputs(IN.normalOS, IN.tangentOS);

                OUT.positionCS = posInputs.positionCS;
                OUT.positionWS = posInputs.positionWS;
                OUT.normalWS   = normInputs.normalWS;
                OUT.tangentWS  = float4(normInputs.tangentWS, IN.tangentOS.w * GetOddNegativeScale());
                OUT.viewDirWS  = GetWorldSpaceViewDir(posInputs.positionWS);
                OUT.uv         = TRANSFORM_TEX(IN.uv, _BaseMap);
                OUT.fogFactor  = ComputeFogFactor(posInputs.positionCS.z);
                OUTPUT_LIGHTMAP_UV(IN.staticLightmapUV, unity_LightmapST, OUT.staticLightmapUV);
                OUTPUT_SH(OUT.normalWS, OUT.vertexSH);
                #ifdef DYNAMICLIGHTMAP_ON
                    OUT.dynamicLightmapUV = IN.dynamicLightmapUV * unity_DynamicLightmapST.xy + unity_DynamicLightmapST.zw;
                #endif
                #ifdef _ADDITIONAL_LIGHTS_VERTEX
                    OUT.vertexLighting = VertexLighting(posInputs.positionWS, normInputs.normalWS);
                #endif

                return OUT;
            }

            float BottomFadeAlpha(float3 positionWS, half textureAlpha)
            {
                float rendererHeight = max(unity_RendererBounds_Max.y - unity_RendererBounds_Min.y, 0.0001);
                float boundsY = saturate((positionWS.y - unity_RendererBounds_Min.y) / rendererHeight);
                float fadePosition = lerp(positionWS.y, boundsY, step(0.5, _UseRendererBounds));
                return saturate(textureAlpha * smoothstep(_FadeStart, _FadeStart + max(_FadeRange, 0.0001), fadePosition));
            }

            void ClipBottomFade(float alpha, bool fadePass)
            {
                // 两个区间严格互斥，边界归实体部分，避免重复混合。
                if (fadePass) {
                    if (alpha <= 0.0 || alpha >= 0.9999) discard;
                } else {
                    clip(alpha - 0.9999);
                }
            }

            half4 ShadeBottomFade(Varyings IN, bool fadePass)
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv) * _BaseColor;

                float alpha = BottomFadeAlpha(IN.positionWS, albedo.a);
                ClipBottomFade(alpha, fadePass);

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
                inputData.fogCoord        = InitializeInputDataFog(float4(IN.positionWS, 1.0), IN.fogFactor);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(IN.positionCS);
                #if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                    inputData.shadowCoord = ComputeScreenPos(TransformWorldToHClip(IN.positionWS));
                #elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
                    inputData.shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                #endif
                inputData.shadowMask = SAMPLE_SHADOWMASK(IN.staticLightmapUV);
                #if defined(DYNAMICLIGHTMAP_ON)
                    inputData.bakedGI = SAMPLE_GI(IN.staticLightmapUV, IN.dynamicLightmapUV, IN.vertexSH, inputData.normalWS);
                #else
                    inputData.bakedGI = SAMPLE_GI(IN.staticLightmapUV, IN.vertexSH, inputData.normalWS);
                #endif
                #ifdef _ADDITIONAL_LIGHTS_VERTEX
                    inputData.vertexLighting = IN.vertexLighting;
                #endif

                SurfaceData surfaceData;
                ZERO_INITIALIZE(SurfaceData, surfaceData);
                surfaceData.albedo    = albedo.rgb;
                surfaceData.alpha     = alpha;
                // 与 URP Lit 一致的 Metallic PBR 工作流；粗糙表面仍保留物理反射。
                surfaceData.metallic  = metallic;
                surfaceData.specular  = half3(0.0h, 0.0h, 0.0h);
                surfaceData.smoothness = smoothness;
                surfaceData.normalTS  = normalTS;
                half ao = SAMPLE_TEXTURE2D(_OcclusionMap, sampler_OcclusionMap, IN.uv).g;
                surfaceData.occlusion = lerp(1.0h, ao, saturate((half)_OcclusionStrength));

                half4 color = UniversalFragmentPBR(inputData, surfaceData);
                // 默认使用 URP Lit PBR；开启 Unlit Mode 后直接输出基础颜色。
                color.rgb   = lerp(color.rgb, albedo.rgb, step(0.5h, (half)_UnlitMode));
                color.rgb   = MixFog(color.rgb, inputData.fogCoord);
                color.a = fadePass ? alpha : 1.0h;

                return color;
            }

            half4 FragOpaque(Varyings IN) : SV_Target { return ShadeBottomFade(IN, false); }
            half4 FragFade(Varyings IN) : SV_Target { return ShadeBottomFade(IN, true); }
        ENDHLSL
        Pass
        {
            Name "Opaque"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Blend One Zero
            ZWrite On
            ZTest LEqual
            Cull [_Cull]
            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment FragOpaque

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTERED_LIGHTING
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DYNAMICLIGHTMAP_ON
            #pragma multi_compile _ USE_LIGHTMAP_SINGLE
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer

            ENDHLSL
        }
        Pass
        {
            Name "Fade"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite On
            ZTest LEqual
            Cull [_Cull]
            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment FragFade

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ EVALUATE_SH_MIXED EVALUATE_SH_VERTEX
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BOX_PROJECTION
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _LIGHT_COOKIES
            #pragma multi_compile _ _LIGHT_LAYERS
            #pragma multi_compile _ _CLUSTERED_LIGHTING
            #pragma multi_compile _ LIGHTMAP_SHADOW_MIXING
            #pragma multi_compile _ SHADOWS_SHADOWMASK
            #pragma multi_compile _ DIRLIGHTMAP_COMBINED
            #pragma multi_compile _ LIGHTMAP_ON
            #pragma multi_compile _ DYNAMICLIGHTMAP_ON
            #pragma multi_compile _ USE_LIGHTMAP_SINGLE
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            #pragma instancing_options renderinglayer

            ENDHLSL
        }
    }
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
