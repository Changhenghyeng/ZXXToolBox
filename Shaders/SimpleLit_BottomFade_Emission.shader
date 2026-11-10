Shader "Custom/SimpleLit_BottomFade_Emission"
{
    Properties
    {
        _BaseMap   ("Albedo (Base Map)", 2D)         = "white" {}
        _BaseColor ("Base Color", Color)             = (1,1,1,1)


        [Header(Output Blur)]
        _BlurRadius ("整体模糊半径 (贴图像素，含 Alpha)", Range(0, 32)) = 0

        [Header(Lighting)]
        [Toggle] _UnlitMode ("Unlit Mode", Float)    = 0

        [Header(Normal)]
        _NormalStrength ("Normal", Range(0, 1))       = 0
        [Normal] [NoScaleOffset] _NormalMap ("Normal Map", 2D) = "bump" {}

        [Header(Smoothness)]
        _Smoothness ("Smoothness", Range(0, 1))       = 0
        [NoScaleOffset] _MetallicSmoothnessMap ("Metallic (RGB) Smoothness (A) Map", 2D) = "black" {}

        [Header(Emission)]
        [NoScaleOffset] _EmissionMap ("Emission Map", 2D) = "white" {}
        [HDR] _EmissionColor ("Emission Color", Color) = (0,0,0,1)
        _EmissionIntensity ("Emission Intensity", Float) = 1

        [Header(Alpha Inner Outline Emission)]
        [Toggle(_ALPHA_INNER_OUTLINE)] _AlphaInnerOutline ("Enable Alpha Inner Outline", Float) = 0
        [HDR] _OutlineEmissionColor ("Outline Emission Color", Color) = (1,1,1,1)
        _OutlineEmissionIntensity ("Outline Emission Intensity", Float) = 1
        _OutlineWidth ("Outline Width (Texture Pixels)", Range(0, 16)) = 1

        [Header(Vertical Float)]
        [Toggle] _FloatEnabled ("Enable Vertical Float", Float) = 0
        _FloatAmplitude ("Float Amplitude (World Units)", Range(0, 5)) = 0.1
        _FloatSpeed ("Float Speed (Cycles Per Second)", Range(0, 5)) = 0.25
        _FloatPhase ("Float Phase (Cycles)", Range(0, 1)) = 0
        _FloatRandomness ("Float Randomness", Range(0, 1)) = 0

        [Header(Bottom Fade)]
        [Toggle] _UseRendererBounds ("Use Model Space (模型本地坐标)", Float) = 0
        _FadeStart ("Fade Start (世界Y / 本地Y)", Float) = 0.0
        _FadeRange ("Fade Range (世界宽度 / 本地宽度)", Float) = 1.0

        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull Mode", Float) = 2
    }

    SubShader
    {
        Tags
        {
            "RenderType"     = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "Queue"          = "Transparent"
            "DisableBatching" = "True"
        }



        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull [_Cull]
            HLSLPROGRAM




            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_NormalMap);
            SAMPLER(sampler_NormalMap);
            TEXTURE2D(_MetallicSmoothnessMap);
            SAMPLER(sampler_MetallicSmoothnessMap);

            TEXTURE2D(_EmissionMap);
            SAMPLER(sampler_EmissionMap);
            float4 _BaseMap_TexelSize;

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float  _BlurRadius;
                float4 _EmissionColor;
                float  _EmissionIntensity;
                float4 _OutlineEmissionColor;
                float  _OutlineEmissionIntensity;
                float  _OutlineWidth;
                float  _AlphaInnerOutline;
                float  _FloatEnabled;
                float  _FloatAmplitude;
                float  _FloatSpeed;
                float  _FloatPhase;
                float  _FloatRandomness;
                float  _UnlitMode;
                float  _NormalStrength;
                float  _Smoothness;
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
                float  localY     : TEXCOORD6;
                float  fadeWorldY : TEXCOORD7;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                VertexPositionInputs posInputs  = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs   normInputs = GetVertexNormalInputs(IN.normalOS, IN.tangentOS);

                // 整个模型沿世界 Y 平移；渐隐使用移动前坐标，随模型一起浮动。
                // 同一模型共用原点种子，避免顶点各自随机导致模型变形。
                float3 objectOriginWS = TransformObjectToWorld(float3(0, 0, 0));
                float randomPhase = frac(sin(dot(objectOriginWS, float3(12.9898, 78.233, 37.719))) * 43758.5453);
                float randomSpeed = frac(sin(dot(objectOriginWS, float3(39.3468, 11.135, 83.155)) + 19.19) * 24634.6345);
                float speedMultiplier = lerp(1.0, lerp(0.75, 1.25, randomSpeed), _FloatRandomness);
                // 连续正弦振荡：整个行程自然加减速，端点柔和换向，无停留帧或急反弹。
                // 速度单位为每秒周期数，0.25 对应约 4 秒一轮。
                float floatAngle = (_Time.y * _FloatSpeed * speedMultiplier + _FloatPhase
                                    + randomPhase * _FloatRandomness) * 6.28318530718;
                float floatWave = sin(floatAngle);
                float floatOffset = floatWave * _FloatAmplitude * step(0.5, _FloatEnabled);
                float3 animatedPositionWS = posInputs.positionWS + float3(0, floatOffset, 0);
                OUT.positionCS = TransformWorldToHClip(animatedPositionWS);
                OUT.positionWS = animatedPositionWS;
                OUT.fadeWorldY = posInputs.positionWS.y;
                OUT.localY     = IN.positionOS.y;
                OUT.normalWS   = normInputs.normalWS;
                OUT.tangentWS  = float4(normInputs.tangentWS, IN.tangentOS.w * GetOddNegativeScale());
                OUT.viewDirWS  = GetWorldSpaceViewDir(animatedPositionWS);
                OUT.uv         = TRANSFORM_TEX(IN.uv, _BaseMap);
                OUT.fogFactor  = ComputeFogFactor(OUT.positionCS.z);

                return OUT;
            }

            // Evaluate the complete decal output before blurring: lighting,
            // emission, outline and coverage all participate in the same kernel.
            half4 EvaluateDecal(Varyings IN)
            {
                UNITY_SETUP_INSTANCE_ID(IN);

                half4 baseSample = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv);
                half4 albedo = baseSample * _BaseColor;

                // 本地模式直接使用网格顶点 Y，不依赖 Renderer 世界范围的更新。
                // 保留属性名以继承现有材质开关；本地参数以模型原点和模型单位为准。
                float fadePosition = lerp(IN.fadeWorldY, IN.localY, step(0.5, _UseRendererBounds));

                // FadeStart = 完全透明起点，FadeStart + FadeRange = 完全不透明终点。
                float fadeT = smoothstep(_FadeStart, _FadeStart + max(_FadeRange, 0.0001), fadePosition);
                float alpha = albedo.a * fadeT;

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
                surfaceData.albedo    = albedo.rgb;
                surfaceData.alpha     = alpha;
                // SimpleLit 使用镜面反射颜色，因此将金属度转换到它的高光输入。
                // Smoothness 为 0 时仍保持原 Shader 的“无高光”效果。
                surfaceData.metallic  = metallic;
                surfaceData.specular  = lerp(half3(0.04h, 0.04h, 0.04h), albedo.rgb, metallic)
                                      * step(0.0001h, smoothness);
                surfaceData.smoothness = smoothness;
                surfaceData.normalTS  = normalTS;
                surfaceData.occlusion = 1.0;

                half4 color = UniversalFragmentBlinnPhong(inputData, surfaceData);
                // 默认使用 SimpleLit；开启 Unlit Mode 后直接输出基础颜色。
                color.rgb   = lerp(color.rgb, albedo.rgb, step(0.5h, (half)_UnlitMode));
                // 自发光同时支持 Lit / Unlit，随输出 Alpha 淡出，并正常参与雾效。
                half3 emission = SAMPLE_TEXTURE2D(_EmissionMap, sampler_EmissionMap, IN.uv).rgb
                                 * _EmissionColor.rgb * max(_EmissionIntensity, 0.0);
                color.rgb  += emission;
                #if defined(_ALPHA_INNER_OUTLINE)
                    // 用八方向 Alpha 腐蚀提取内部边缘，宽度以基础贴图像素计。
                    // 只读取贴图 Alpha，底部渐隐不会额外产生一条描边。
                    float2 outlineOffset = _BaseMap_TexelSize.xy * _OutlineWidth;
                    half innerAlpha = baseSample.a;
                    innerAlpha = min(innerAlpha, SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv + float2(outlineOffset.x, 0)).a);
                    innerAlpha = min(innerAlpha, SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv - float2(outlineOffset.x, 0)).a);
                    innerAlpha = min(innerAlpha, SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv + float2(0, outlineOffset.y)).a);
                    innerAlpha = min(innerAlpha, SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv - float2(0, outlineOffset.y)).a);
                    float2 diagonalOffset = outlineOffset * 0.70710678;
                    innerAlpha = min(innerAlpha, SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv + diagonalOffset).a);
                    innerAlpha = min(innerAlpha, SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv - diagonalOffset).a);
                    innerAlpha = min(innerAlpha, SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv + float2(diagonalOffset.x, -diagonalOffset.y)).a);
                    innerAlpha = min(innerAlpha, SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv + float2(-diagonalOffset.x, diagonalOffset.y)).a);
                    half outlineMask = saturate(baseSample.a - innerAlpha);
                    color.rgb += outlineMask * _OutlineEmissionColor.rgb * max(_OutlineEmissionIntensity, 0.0);
                #endif
                color.rgb   = MixFog(color.rgb, IN.fogFactor);
                color.a     = surfaceData.alpha;

                return color;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_BRANCH
                if (_BlurRadius <= 0.001)
                {
                    half4 original = EvaluateDecal(IN);
                    clip(original.a - 0.01);
                    return original;
                }

                float2 stride = abs(_BaseMap_TexelSize.xy) * (_BlurRadius * 0.5);
                float3 premultipliedColor = 0;
                float alphaSum = 0;
                float weightSum = 0;
                // Accumulate premultiplied RGB so invisible PNG colors cannot
                // introduce a black fringe. Alpha is blurred, not restored.
                [unroll] for (int y = -2; y <= 2; ++y)
                {
                    [unroll] for (int x = -2; x <= 2; ++x)
                    {
                        float weight = exp(-float(x * x + y * y) * 0.5);
                        Varyings sampleInput = IN;
                        sampleInput.uv = IN.uv + float2(x, y) * stride;
                        // The region outside the decal texture is transparent,
                        // even when the texture importer is set to Repeat.
                        if (all(sampleInput.uv >= 0.0) && all(sampleInput.uv <= 1.0))
                        {
                            half4 sampleColor = EvaluateDecal(sampleInput);
                            premultipliedColor += sampleColor.rgb * sampleColor.a * weight;
                            alphaSum += sampleColor.a * weight;
                        }
                        weightSum += weight;
                    }
                }

                float alpha = alphaSum / weightSum;
                // Preserve the bottom fade; discard only fully invisible fragments.
                clip(alpha - 0.0001);
                float3 color = premultipliedColor / max(alphaSum, 0.00001);
                return half4(color, alpha);
            }
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma shader_feature_local_fragment _ALPHA_INNER_OUTLINE
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile_fog
            #pragma multi_compile_instancing
            ENDHLSL
        }
    }
    FallBack "Universal Render Pipeline/Simple Lit"
}
