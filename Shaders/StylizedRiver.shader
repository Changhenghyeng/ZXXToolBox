Shader "Custom/StylizedRiver"
{
    Properties
    {
        _ShallowColor ("浅水色", Color) = (0.2, 0.65, 0.55, 1)
        _DeepColor    ("深水色", Color) = (0.05, 0.18, 0.28, 1)
        _DepthPower   ("渐变均匀度", Float) = 1.0

        _FlowDirection ("流动方向 (XZ)", Vector) = (1, 0, 0, 0)
        _FlowSpeed     ("流动速度", Range(0, 2)) = 0.5

        [NoScaleOffset][Normal] _NormalMap ("法线贴图", 2D) = "bump" {}
        _NormalTiling       ("法线平铺", Float) = 1.5
        _NormalTiling2      ("法线平铺2", Float) = 4.0
        _NormalSpeed2       ("法线流速2", Range(-10, 10)) = -0.5
        _NormalStrength     ("法线强度", Range(0, 1)) = 0.25
        _SpeedStretch       ("速度线拉伸 (X=U, Y=V)", Vector) = (15, 1, 0, 0)
        _SpeedFlow          ("速度线流速", Range(0, 2)) = 0.8
        _SpeedDisplace      ("速度线错位", Range(0, 1)) = 0.5
        _SpeedThreshold     ("速度线阈值", Range(0, 1)) = 0.6
        _SpeedIntensity     ("速度线强度", Range(0, 2)) = 0.3

        [NoScaleOffset] _CausticsTex ("焦散贴图", 2D) = "white" {}
        _CausticsTiling     ("焦散平铺", Float) = 0.6
        _CausticsSpeed      ("焦散流速", Range(0, 0.5)) = 0.06
        _CausticsFade       ("焦散衰减", Range(0, 3)) = 0.0
        _CausticsDistort    ("焦散扭曲", Range(0, 1)) = 0.0
        _CausticsBrightness ("焦散亮度", Range(0, 3)) = 1.0

        [NoScaleOffset] _FoamTex ("泡沫贴图", 2D) = "white" {}
        _FoamTiling         ("泡沫平铺 (X=U, Y=V)", Vector) = (5, 5, 0, 0)
        _FoamWidth          ("泡沫宽度", Range(0.01, 0.5)) = 0.08
        _FoamSolid          ("实心宽度", Range(0, 0.2)) = 0.04
        _FoamNoise          ("边缘扰动", Range(0, 1.0)) = 0.06
        _FoamNoise          ("边缘扰动", Range(0, 1.0)) = 0.06
        _FoamColor          ("泡沫颜色", Color) = (0.95, 0.97, 1, 1)

        _Alpha ("透明度", Range(0.2, 1)) = 0.85
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }

            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            TEXTURE2D(_NormalMap);   SAMPLER(sampler_NormalMap);
            TEXTURE2D(_CausticsTex); SAMPLER(sampler_CausticsTex);
            TEXTURE2D(_FoamTex);     SAMPLER(sampler_FoamTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _ShallowColor, _DeepColor, _FoamColor, _FlowDirection;
                float  _DepthPower, _FlowSpeed;
                float  _NormalTiling, _NormalTiling2, _NormalSpeed2, _NormalStrength;
                float4 _SpeedStretch;
                float  _SpeedFlow, _SpeedDisplace, _SpeedThreshold, _SpeedIntensity;
                float  _CausticsTiling, _CausticsSpeed, _CausticsFade, _CausticsDistort, _CausticsBrightness;
                float4 _FoamTiling;
                float  _FoamWidth, _FoamSolid, _FoamNoise;
                float  _Alpha;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                float2 uv         : TEXCOORD0;
                float2 uv1        : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float3 tangentWS  : TEXCOORD2;
                float3 bitangentWS: TEXCOORD3;
                float2 uv         : TEXCOORD4;
                float2 uv1        : TEXCOORD5;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);

                VertexPositionInputs pos  = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs   norm = GetVertexNormalInputs(IN.normalOS, IN.tangentOS);

                OUT.positionCS  = pos.positionCS;
                OUT.positionWS  = pos.positionWS;
                OUT.normalWS    = norm.normalWS;
                OUT.tangentWS   = norm.tangentWS;
                OUT.bitangentWS = norm.bitangentWS;
                OUT.uv          = IN.uv;
                OUT.uv1         = IN.uv1;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);

                float2 worldUV = IN.positionWS.xz;
                float2 flowDir = normalize(_FlowDirection.xz + 0.0001);
                float  flowTime   = _Time.y * _FlowSpeed;
                float2 flowOffset = flowDir * flowTime;

                // ── 1. 深浅 ──
                float  shoreDist = abs(IN.uv.x - 0.5) * 2.0;
                float  shoreMask = 1.0 - shoreDist;
                float  colorMask = 1.0 - pow(saturate(shoreDist), _DepthPower);
                float3 waterColor = lerp(_ShallowColor.rgb, _DeepColor.rgb, colorMask);

                // ── 2. 法线 —— 双层混合 ──
                float3 n1 = UnpackNormal(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap,
                    worldUV * _NormalTiling  + flowOffset));
                float3 n2 = UnpackNormal(SAMPLE_TEXTURE2D(_NormalMap, sampler_NormalMap,
                    worldUV * _NormalTiling2 + flowOffset * _NormalSpeed2));
                float3 normalTS = normalize(float3(n1.xy + n2.xy, n1.z * n2.z));
                float3x3 TBN = float3x3(IN.tangentWS, IN.bitangentWS, IN.normalWS);
                float3 normalWS = normalize(lerp(IN.normalWS, mul(normalTS, TBN), _NormalStrength));
                float2 normalDistort = (normalWS.xz - IN.normalWS.xz) * _NormalStrength;

                // ── 3. 焦散 —— 完全独立层 ──
                float2 causticDistort = normalDistort * shoreMask * _CausticsDistort;
                float2 causticUV1 = worldUV * _CausticsTiling + flowOffset * _CausticsSpeed + causticDistort;
                float2 causticUV2 = worldUV * _CausticsTiling * 0.8 - flowOffset * _CausticsSpeed * 1.2 - causticDistort;
                float3 c1 = SAMPLE_TEXTURE2D(_CausticsTex, sampler_CausticsTex, causticUV1).rgb;
                float3 c2 = SAMPLE_TEXTURE2D(_CausticsTex, sampler_CausticsTex, causticUV2).rgb;
                float  causticMask = saturate(1.0 - _CausticsFade * shoreMask);
                float3 caustics = min(c1, c2) * causticMask * _CausticsBrightness;

                // ── 4. 泡沫 ──
                //  双层采样（不同比例+反向流动）→ 纹理有变化
                float2 foamUV  = IN.uv * _FoamTiling.xy + float2(0, flowTime);
                float  foam1   = SAMPLE_TEXTURE2D(_FoamTex, sampler_FoamTex, foamUV).r;
                float  foam2   = SAMPLE_TEXTURE2D(_FoamTex, sampler_FoamTex, foamUV * 0.7 - float2(0, flowTime * 1.3)).r;
                float  foamTex = saturate(foam1 + foam2);
                //  边缘噪声：仅在实心区之后生效，岸边绝对不受影响
                float  edgeN = SAMPLE_TEXTURE2D(_FoamTex, sampler_FoamTex, foamUV * 0.2).r;
                float  noiseInfluence = smoothstep(_FoamSolid, _FoamSolid + 0.01, shoreMask);
                float  depthNoisy = shoreMask + (edgeN - 0.5) * _FoamNoise * noiseInfluence;
                float  cutoff = (depthNoisy - _FoamSolid) / max(0.001, _FoamWidth - _FoamSolid);
                float  foamMask = smoothstep(cutoff - 0.01, cutoff + 0.01, foamTex);

                // ── 5. 光照 ──
                Light L = GetMainLight();
                float3 diffuse = waterColor * (SampleSH(normalWS) + L.color * saturate(dot(normalWS, L.direction)));

                // ── 6. 合成：水色层 → +焦散层 → 泡沫层 → 速度线 ──
                float3 color = diffuse;
                color += caustics;
                color = lerp(color, _FoamColor.rgb, foamMask * _FoamColor.a);
                // ── 速度线层：UV1压缩 + 噪声错位打散tiling ──
                float2 speedUV = IN.uv1 / _SpeedStretch.xy;
                float  displace = SAMPLE_TEXTURE2D(_FoamTex, sampler_FoamTex, IN.uv1 * 0.3).r;
                speedUV.y += _Time.y * _SpeedFlow + (displace - 0.5) * _SpeedDisplace;
                float  speedLine = SAMPLE_TEXTURE2D(_FoamTex, sampler_FoamTex, speedUV * 3.0).r;
                speedLine = smoothstep(_SpeedThreshold, _SpeedThreshold + 0.1, speedLine);
                color += speedLine * _SpeedIntensity;
                return half4(saturate(color), _Alpha);
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Simple Lit"
}
