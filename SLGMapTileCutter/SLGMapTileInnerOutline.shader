Shader "Custom/Tile Inner Outline"
{
    Properties
    {
        [MainColor] _BaseColor("Base Color", Color) = (1, 1, 1, 1)
        [MainTexture] _BaseMap("Base Map", 2D) = "white" {}
        [NoScaleOffset] _OutlineDistanceMap("Outline Distance Map", 2D) = "white" {}
        [HDR] _OutlineColor("Outline Color (HDR)", Color) = (0, 0, 0, 1)
        _OutlineWidth("Inner Outline Width (Local Units)", Float) = 0.1
        _OutlineSoftness("Outline Softness", Range(0, 1)) = 0.2
        [HideInInspector] _OutlineDistanceScale(
            "Outline Distance Scale", Float) = 1
        [HideInInspector] _ProjectionCenterAndSize(
            "Projection Center And Size", Vector) = (0, 0, 1, 1)
        [HideInInspector] _ProjectionRotation("Projection Rotation", Float) = 0
        [HideInInspector] _ProjectionUvRowX("Projection UV Row X", Vector) = (1, 0, 0, 0)
        [HideInInspector] _ProjectionUvRowY("Projection UV Row Y", Vector) = (0, 0, 1, 0)
        [HideInInspector] _UseProjectionUvRows("Use Projection UV Rows", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
        }

        Pass
        {
            Name "ForwardUnlit"
            Tags { "LightMode" = "UniversalForward" }

            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);
            TEXTURE2D(_OutlineDistanceMap);
            SAMPLER(sampler_OutlineDistanceMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4 _BaseColor;
                half4 _OutlineColor;
                float _OutlineWidth;
                float _OutlineSoftness;
                float _OutlineDistanceScale;
                float4 _ProjectionCenterAndSize;
                float _ProjectionRotation;
                float4 _ProjectionUvRowX;
                float4 _ProjectionUvRowY;
                float _UseProjectionUvRows;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 localPosition : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.localPosition = input.positionOS.xyz;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                half4 baseColor =
                    SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv)
                    * _BaseColor;

                float4 localPosition = float4(
                    input.localPosition.xyz, 1.0);
                float2 outlineUV = float2(
                    dot(localPosition, _ProjectionUvRowX),
                    dot(localPosition, _ProjectionUvRowY));
                float2 legacyPoint = input.localPosition.xz
                    - _ProjectionCenterAndSize.xy;
                float angle = radians(-_ProjectionRotation);
                float sine;
                float cosine;
                sincos(angle, sine, cosine);
                legacyPoint = float2(
                    legacyPoint.x * cosine - legacyPoint.y * sine,
                    legacyPoint.x * sine + legacyPoint.y * cosine);
                float2 legacyUV = legacyPoint
                    / max(_ProjectionCenterAndSize.zw, 0.0001)
                    + 0.5;
                outlineUV = lerp(legacyUV, outlineUV,
                    step(0.5, _UseProjectionUvRows));
                float boundaryDistance = SAMPLE_TEXTURE2D(
                    _OutlineDistanceMap, sampler_OutlineDistanceMap,
                    saturate(outlineUV)).r
                    * _OutlineDistanceScale;

                float width = max(_OutlineWidth, 0.0);
                float feather = width * saturate(_OutlineSoftness);
                // Screen derivatives are used only for sub-pixel antialiasing.
                // The actual outline width remains in object-local units.
                float antialias = max(fwidth(boundaryDistance) * 0.75,
                    0.000001);
                float outline = 1.0 - smoothstep(
                    max(0.0, width - feather) - antialias,
                    width + antialias,
                    boundaryDistance);
                return lerp(baseColor, _OutlineColor, saturate(outline));
            }
            ENDHLSL
        }
    }
}
