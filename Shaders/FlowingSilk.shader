Shader "Custom/Flowing Silk"
{
    Properties
    {
        [Header(Base Material)]
        [MainColor] _BaseColor ("绸面颜色", Color) = (0.98, 0.94, 0.84, 1)
        _ShadowColor ("绸面暗部", Color) = (0.38, 0.46, 0.49, 1)
        _Sheen ("丝绸柔光", Range(0, 1)) = 0.45

        [Header(Shape)]
        _Width ("整条宽度", Range(0.5, 6)) = 3.4
        _Span ("左右展开", Range(8, 16)) = 11.5
        _Arch ("舒展高度", Range(11, 17)) = 12.5

        [Header(Animation)]
        _Bend ("整体弯曲幅度", Range(0, 3)) = 2.2
        _Lift ("上下摆动幅度", Range(0, 3)) = 2.2
        _Roll ("绸面翻动幅度", Range(0, 1.2)) = 0.8
        _Period ("循环时长（秒）", Range(6, 40)) = 18

        [Header(Transparency)]
        _Opacity ("整体不透明度", Range(0, 1)) = 0.88
        _EdgeFade ("侧边柔化", Range(0.005, 0.2)) = 0.035
        _EndFade ("头尾渐隐", Range(0.01, 0.3)) = 0.07
        [HideInInspector] _PreviewTime ("Preview Time", Float) = -1
    }
    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
        }
        Pass
        {
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            Cull Off
            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            CBUFFER_START(UnityPerMaterial)
                half4 _BaseColor;
                half4 _ShadowColor;
                float _Width;
                float _Span;
                float _Arch;
                float _Bend;
                float _Lift;
                float _Roll;
                float _Period;
                float _Opacity;
                float _Sheen;
                float _EdgeFade;
                float _EndFade;
                float _PreviewTime;
            CBUFFER_END
            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                half fog : TEXCOORD3;
                float phase : TEXCOORD4;
            };
            float3 Centre(float u, float phase)
            {
                float angle = u * PI;
                float sinAngle = sin(angle);
                float cosAngle = cos(angle);
                float3 positionOS;
                positionOS.x = -_Span * cosAngle + _Bend * sin(2 * angle + phase) * (0.3 + 0.7 * abs(cosAngle));
                positionOS.y = -4 + _Arch * sinAngle + 1.5 * u + _Lift * sin(2 * angle-phase) * cosAngle + 0.55 * sin(phase) * sinAngle;
                positionOS.z = 6 + 1.4 * sin(2 * angle-phase) + 0.65 * sin(phase);
                // The cross-centre arc stays above the actor; the sides remain outside.
                float clearanceHeight = 9 * (1-smoothstep(4.5, 9.5, abs(positionOS.x)));
                float heightDifference = positionOS.y-clearanceHeight;
                positionOS.y = 0.5 * (positionOS.y + clearanceHeight + sqrt(heightDifference * heightDifference + 0.16));
                return positionOS;
            }
            Varyings Vert(Attributes input)
            {
                Varyings output;
                float animationTime = _PreviewTime >= 0?_PreviewTime : _Time.y;
                float phase = TWO_PI * frac(animationTime/max(_Period, 0.1));
                float u = input.uv.x;
                float widthOffset = input.uv.y - 0.5;
                float3 tangent = normalize(Centre(u + 0.001, phase)-Centre(u-0.001, phase));
                float3 side = normalize(cross(tangent, float3(0, 0, 1)));
                float roll = _Roll * sin(u * TWO_PI-phase);
                float3 across = side * cos(roll) + cross(tangent, side) * sin(roll);
                float3 normalOS = normalize(cross(across, tangent));
                float billow = sin(input.uv.y * PI) * sin(u * 4 * PI-phase * 2) * 0.12;
                float3 positionOS = Centre(u, phase) + across * (widthOffset * _Width) + normalOS * billow;
                output.positionWS = TransformObjectToWorld(positionOS);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.normalWS = TransformObjectToWorldNormal(normalOS);
                output.uv = input.uv;
                output.fog = ComputeFogFactor(output.positionCS.z);
                output.phase = phase;
                return output;
            }
            half4 Frag(Varyings input) : SV_Target
            {
                float u = input.uv.x;
                float v = input.uv.y;
                half3 normalWS = normalize(input.normalWS);
                half3 viewDirectionWS = normalize(GetWorldSpaceViewDir(input.positionWS));
                float facing = abs(dot(normalWS, viewDirectionWS));
                float softLight = 0.68 + 0.22 * abs(dot(normalWS, normalize(float3(-0.3, 0.8, -0.5))));
                float fold = 0.06 * sin(v * TWO_PI + sin(u * 9-input.phase));
                half3 color = lerp(_ShadowColor.rgb, _BaseColor.rgb, saturate(softLight + fold));
                float silkHighlight = pow(saturate(1-abs(v-0.34-sin(u * 8-input.phase) * 0.08) * 3), 5);
                color += _BaseColor.rgb * _Sheen * (silkHighlight * 0.20 + pow(1-facing, 3) * 0.12);
                float edgeFade = smoothstep(0, _EdgeFade, v) * smoothstep(0, _EdgeFade, 1-v);
                float endFade = smoothstep(0, _EndFade, u) * smoothstep(0, _EndFade, 1-u);
                return half4(MixFog(color, input.fog), _BaseColor.a * _Opacity * edgeFade * endFade * (0.94 + 0.06 * sin(input.phase)));
            }
            ENDHLSL
        }
    }
}
