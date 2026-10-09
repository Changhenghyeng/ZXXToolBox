Shader "Custom/SimpleLit_BottomFade_Shadow"
{
    Properties
    {
        _BaseMap   ("Albedo (Base Map)", 2D)         = "white" {}
        _BaseColor ("Base Color", Color)             = (1,1,1,1)

        [Header(Bottom Fade)]
        _FadeStart ("Fade Start (底部完全透明的世界Y)", Float) = 0.0
        _FadeRange ("Fade Range (渐变宽度)",           Float) = 1.0

        [Header(Shadow)]
        _ShadowClipThreshold ("Shadow Caster Clip Threshold", Range(0,1)) = 0.5

        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull Mode", Float) = 2
    }

    SubShader
    {
        Tags
        {
            "RenderType"     = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "Queue"          = "Transparent"
        }

        // 单一 Forward Pass 自身写深度，避免 URP 对多 Pass 透明物体的不稳定匹配与排序。
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
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

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float  _FadeStart;
                float  _FadeRange;
                float  _ShadowClipThreshold;
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
                OUT.viewDirWS  = GetWorldSpaceViewDir(posInputs.positionWS);
                OUT.uv         = TRANSFORM_TEX(IN.uv, _BaseMap);
                OUT.fogFactor  = ComputeFogFactor(posInputs.positionCS.z);

                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);

                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv) * _BaseColor;

                // FadeStart = 完全透明起点，FadeStart + FadeRange = 完全不透明终点
                float fadeT = smoothstep(_FadeStart, _FadeStart + max(_FadeRange, 0.0001), IN.positionWS.y);
                float alpha = albedo.a * fadeT;
                clip(alpha - 0.01);

                InputData inputData;
                ZERO_INITIALIZE(InputData, inputData);
                inputData.positionWS      = IN.positionWS;
                inputData.normalWS        = normalize(IN.normalWS);
                inputData.viewDirectionWS = normalize(IN.viewDirWS);
                inputData.fogCoord        = IN.fogFactor;
                inputData.shadowCoord     = TransformWorldToShadowCoord(IN.positionWS);
                inputData.bakedGI         = SampleSH(inputData.normalWS);

                SurfaceData surfaceData;
                ZERO_INITIALIZE(SurfaceData, surfaceData);
                surfaceData.albedo    = albedo.rgb;
                surfaceData.alpha     = alpha;
                surfaceData.specular  = half3(0, 0, 0);  // 无高光
                surfaceData.occlusion = 1.0;

                half4 color = UniversalFragmentBlinnPhong(inputData, surfaceData);
                color.rgb   = MixFog(color.rgb, IN.fogFactor);
                color.a     = surfaceData.alpha;

                return color;
            }
            ENDHLSL
        }

        // ShadowCaster
        // 关键：完全不 include Shadows.hlsl，从而绕开 URP 14 中 LerpWhiteTo 未定义的编译问题。
        // 自行实现 ApplyShadowBias 逻辑（与 URP 内置等价）。
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float  _FadeStart;
                float  _FadeRange;
                float  _ShadowClipThreshold;
            CBUFFER_END

            // URP 在 ShadowCaster Pass 中通过这些全局变量传入光照与偏移参数
            float3 _LightDirection;
            float3 _LightPosition;
            float4 _ShadowBias;  // x: depth bias, y: normal bias

            struct ShadowAttribs
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct ShadowVaryings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            // 等价于 URP 内置 ApplyShadowBias，不引入 Shadows.hlsl 的依赖链
            float3 ApplyShadowBiasLocal(float3 positionWS, float3 normalWS, float3 lightDirection)
            {
                float invNdotL = 1.0 - saturate(dot(lightDirection, normalWS));
                float scale = invNdotL * _ShadowBias.y;

                positionWS = lightDirection * _ShadowBias.xxx + positionWS;
                positionWS = normalWS      * scale.xxx        + positionWS;
                return positionWS;
            }

            ShadowVaryings ShadowVert(ShadowAttribs IN)
            {
                ShadowVaryings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                float3 normalWS   = TransformObjectToWorldNormal(IN.normalOS);

            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif

                positionWS = ApplyShadowBiasLocal(positionWS, normalWS, lightDirectionWS);
                float4 positionCS = TransformWorldToHClip(positionWS);

            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif

                OUT.positionCS = positionCS;
                OUT.positionWS = positionWS;
                return OUT;
            }

            half4 ShadowFrag(ShadowVaryings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                float fadeT = smoothstep(_FadeStart, _FadeStart + max(_FadeRange, 0.0001), IN.positionWS.y);
                // 低于阈值的区域不投影，让地面影子跟随渐变一起淡出
                clip(fadeT - saturate(_ShadowClipThreshold));
                return 0;
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Simple Lit"
}
