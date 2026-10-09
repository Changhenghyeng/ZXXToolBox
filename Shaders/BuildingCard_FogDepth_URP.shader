Shader "Custom/Building Card Fog Depth URP"
{
    Properties
    {
        [MainTexture] _BaseMap("Base Map", 2D) = "white" {}
        [MainColor] _BaseColor("Base Color", Color) = (1, 1, 1, 1)
        _BuildingDepthAlphaCutoff("Building Depth Alpha Cutoff", Range(0, 1)) = 0.9
        [HideInInspector] _UseAtlasUV("Use Atlas UV", Float) = 0
        [HideInInspector] _SelectionAlphaCutoff("Selection Alpha Cutoff", Range(0, 1)) = 0.95
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "AlphaTest"
            "RenderType" = "TransparentCutout"
            "IgnoreProjector" = "True"
        }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_BaseMap);
        SAMPLER(sampler_BaseMap);

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            half4 _BaseColor;
            half _BuildingDepthAlphaCutoff;
            half _UseAtlasUV;
            half _SelectionAlphaCutoff;
        CBUFFER_END

        float _ObjectId;
        float _PassValue;
        float4 _SelectionID;

        struct Attributes
        {
            float4 positionOS : POSITION;
            float2 uv0 : TEXCOORD0;
            float2 atlasUV : TEXCOORD2;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 uv : TEXCOORD0;
            UNITY_VERTEX_INPUT_INSTANCE_ID
            UNITY_VERTEX_OUTPUT_STEREO
        };

        Varyings CardVert(Attributes input)
        {
            Varyings output = (Varyings)0;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_TRANSFER_INSTANCE_ID(input, output);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
            output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
            float2 selectedUV = lerp(input.uv0, input.atlasUV, saturate(_UseAtlasUV));
            output.uv = TRANSFORM_TEX(selectedUV, _BaseMap);
            return output;
        }

        half4 SampleCard(Varyings input)
        {
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            return SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
        }

        void ClipBuildingDepth(half alpha)
        {
            clip(alpha - _BuildingDepthAlphaCutoff);
        }

        half ForwardDepthFrag(Varyings input) : SV_Target
        {
            half alpha = SampleCard(input).a;
            ClipBuildingDepth(alpha);
            return 0.0h;
        }

        half4 VisualFrag(Varyings input) : SV_Target
        {
            return SampleCard(input);
        }

        half DepthFrag(Varyings input) : SV_Target
        {
            half alpha = SampleCard(input).a;
            ClipBuildingDepth(alpha);
            return 0.0h;
        }

        half4 SelectionFrag(Varyings input) : SV_Target
        {
            clip(SampleCard(input).a - _SelectionAlphaCutoff);
            return half4(_ObjectId, _PassValue, 1.0h, 1.0h);
        }

        half4 PickingFrag(Varyings input) : SV_Target
        {
            clip(SampleCard(input).a - _SelectionAlphaCutoff);
            return _SelectionID;
        }
        ENDHLSL

        // The complete card appearance is independent from its depth coverage.
        Pass
        {
            Name "CardVisual"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off
            ZWrite Off
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex CardVert
            #pragma fragment VisualFrag
            #pragma multi_compile_instancing
            ENDHLSL
        }

        // The forward depth writer is required when URP copies depth after opaques.
        Pass
        {
            Name "BuildingCardDepthForward"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex CardVert
            #pragma fragment ForwardDepthFrag
            #pragma multi_compile_instancing
            ENDHLSL
        }

        // URP renders this pass into Camera Depth Texture before the fog card is drawn.
        Pass
        {
            // Use a dedicated pass name so materials converted from URP Transparent do not
            // inherit its serialized "DepthOnly disabled" state for this pass.
            Name "BuildingCardDepth"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ZTest LEqual
            ColorMask R
            Cull Off

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex CardVert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing
            ENDHLSL
        }

        Pass
        {
            Name "SceneSelectionPass"
            Tags { "LightMode" = "SceneSelectionPass" }
            BlendOp Add
            Blend One Zero
            ZWrite On
            Cull Off

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex CardVert
            #pragma fragment SelectionFrag
            #pragma editor_sync_compilation
            #pragma multi_compile_instancing
            ENDHLSL
        }

        Pass
        {
            Name "ScenePickingPass"
            Tags { "LightMode" = "Picking" }
            BlendOp Add
            Blend One Zero
            ZWrite On
            Cull Off

            HLSLPROGRAM
            #pragma target 2.0
            #pragma vertex CardVert
            #pragma fragment PickingFrag
            #pragma editor_sync_compilation
            #pragma multi_compile_instancing
            ENDHLSL
        }
    }

    FallBack Off
}
