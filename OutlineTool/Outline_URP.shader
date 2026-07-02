Shader "Custom/Outline"
{
    Properties
    {
        [HDR]_Color ("Color", Color) = (1,1,0,1)
        _Width ("Width", Range(0.001, 0.5)) = 0.02

        [Toggle(_SMOOTH_ON)] _Smooth ("Smooth Normals (baked in UV2)", Float) = 0

        _ZOffset ("Z Offset", Float) = 0
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry+1" }

        Cull Front
        ZWrite On
        ZTest LEqual

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma shader_feature_local _SMOOTH_ON

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                float _Width, _ZOffset;
            CBUFFER_END

            struct Attributes
            {
                float4 posOS  : POSITION;
                float3 nrmOS  : NORMAL;
                float4 uv2    : TEXCOORD2;  // baked smooth normal
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 posHCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings vert(Attributes IN)
            {
                Varyings o;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, o);

                // 外扩方向，object space
                #if _SMOOTH_ON
                    // UV2 全零时回退顶点法线
                    float d2 = dot(IN.uv2.xyz, IN.uv2.xyz);
                    float3 n = d2 > 0.0001 ? normalize(IN.uv2.xyz) : normalize(IN.nrmOS);
                #else
                    float3 n = normalize(IN.nrmOS);
                #endif

                IN.posOS.xyz += n * _Width;
                float4 cs = TransformObjectToHClip(IN.posOS.xyz);
                cs.z += _ZOffset * cs.w;
                o.posHCS = cs;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                return _Color;
            }
            ENDHLSL
        }
    }
}
