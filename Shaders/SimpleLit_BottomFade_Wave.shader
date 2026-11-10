Shader "Custom/SimpleLit_BottomFade_Wave"
{
    Properties
    {
        _BaseMap   ("Albedo (Base Map)", 2D)         = "white" {}
        _BaseColor ("Base Color", Color)             = (1,1,1,1)

        [Header(Lighting)]
        [Toggle] _UnlitMode ("Unlit Mode", Float) = 0

        [Header(Wind)]
        _WindDirection ("Wind Direction (XZ)", Vector) = (0.5, 0.3, 0, 0)
        _WindStrength  ("Wind Strength", Range(0, 2))  = 0.3
        _WindSpeed     ("Wind Speed", Range(0, 5))     = 1.2
        _WindScale     ("Wind Wave Scale", Range(0.01, 1)) = 0.15
        _WindChaos     ("Wind Chaos", Range(0, 1))     = 0.3

        [Header(Bottom Fade)]
        _FadeStart ("Fade Start (底部完全透明的世界Y)", Float) = 0.0
        _FadeRange ("Fade Range (渐变宽度)",           Float) = 1.0

        [Toggle(_RECEIVE_LIGHTING_ON)] _ReceiveLighting ("Receive Lighting (受光)", Float) = 1

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
            #pragma shader_feature_local _RECEIVE_LIGHTING_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float4 _BaseColor;
                float  _UnlitMode;
                float  _FadeStart;
                float  _FadeRange;
                float2 _WindDirection;
                float  _WindStrength;
                float  _WindSpeed;
                float  _WindScale;
                float  _WindChaos;
                float  _ReceiveLighting;
            CBUFFER_END

            // Wind displacement is derived only from POSITION.
            // Do not use UV/UV2/normal/tangent here: those attributes can differ
            // across split vertices at seams and would physically tear the mesh.
            float3 CalculateWindOffset(float3 positionOS)
            {
                float3 worldPos = TransformObjectToWorld(positionOS);
                float  time     = _Time.y * _WindSpeed;

                // Preserve the original bottom-to-top weighting behavior, but use
                // object-space Y only so coincident split vertices move identically.
                float  heightFactor = saturate(positionOS.y);

                float wave1 = sin(worldPos.x * _WindScale + time)
                            * cos(worldPos.z * _WindScale * 1.3 + time * 0.6);
                float wave2 = sin(worldPos.z * _WindScale * 3.7 + time * 1.7)
                            * cos(worldPos.x * _WindScale * 2.3 - time * 1.1);
                float combined = wave1 + wave2 * _WindChaos;

                return float3(_WindDirection.x, 0, _WindDirection.y)
                     * (combined * _WindStrength * heightFactor);
            }

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

                // IMPORTANT:
                // Use POSITION only for vertex displacement. Vertices split by UV,
                // lightmap UV, hard normals, etc. can share the same position but
                // have different per-vertex attributes. POSITION-only displacement
                // keeps those coincident vertices welded visually during animation.
                float3 posOS = IN.positionOS.xyz + CalculateWindOffset(IN.positionOS.xyz);

                VertexPositionInputs posInputs  = GetVertexPositionInputs(posOS);
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

                // 显式 Unlit 开关优先于原有 Receive Lighting 设置。
                if (_UnlitMode >= 0.5)
                {
                    return half4(MixFog(albedo.rgb, IN.fogFactor), alpha);
                }

            #ifdef _RECEIVE_LIGHTING_ON
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
            #else
                // 不受光：直接输出贴图颜色 + 雾效
                half3 rgb = MixFog(albedo.rgb, IN.fogFactor);
                return half4(rgb, alpha);
            #endif
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Simple Lit"
}
