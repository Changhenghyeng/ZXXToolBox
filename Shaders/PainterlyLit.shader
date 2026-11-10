
Shader "Custom/Painterly Lit"
{
    Properties
    {
        [Header(Base)]
        [MainColor] _BaseColor("Base Color", Color) = (1,1,1,1)
        [MainTexture] _BaseMap("Base Map", 2D) = "white" {}

        [Header(Two Tone)]
        _DarkShadowColor("Dark Color", Color) = (0.35,0.40,0.48,1)
        _LightTint("Light Color", Color) = (1,0.94,0.83,1)
        _ToonThreshold("Light Dark Threshold", Range(0,1)) = 0.5
        _ToonSoftness("Light Dark Softness", Range(0.001,0.2)) = 0.02

        [Header(Brush)]
        _BrushMap("Brush Map (R)", 2D) = "gray" {}
        [Enum(UV,0,ObjectTriplanar,1)] _BrushMapping("Brush Mapping", Float) = 1
        [HideInInspector] _BrushScale("Legacy Brush Scale", Float) = 1
        _BrushInfluence("Brush Influence", Range(0,1)) = 0.3
        _BrushContrast("Brush Contrast", Range(0.1,4)) = 1
        _BrushToneVariation("Brush Tone Variation", Range(0,0.5)) = 0.15
        _BrushRotation("Brush Rotation", Range(-180,180)) = 0
        _BrushCurvature("Brush Curvature", Range(-0.5,0.5)) = 0

        [Header(Shadow)]
        _RealtimeShadowStrength("Realtime Shadow Strength", Range(0,1)) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
        }

        Cull Back
        ZWrite On

        HLSLINCLUDE

        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

        TEXTURE2D(_BaseMap);
        SAMPLER(sampler_BaseMap);

        TEXTURE2D(_BrushMap);
        SAMPLER(sampler_BrushMap);

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            float4 _BrushMap_ST;
            half4 _BaseColor;
            half4 _DarkShadowColor;
            half4 _LightTint;
            half _ToonThreshold;
            half _ToonSoftness;
            float _BrushMapping;
            float _BrushScale;
            half _BrushInfluence;
            half _BrushContrast;
            half _BrushToneVariation;
            float _BrushRotation;
            float _BrushCurvature;
            half _RealtimeShadowStrength;
        CBUFFER_END

        struct Attributes
        {
            float4 positionOS : POSITION;
            half3 normalOS : NORMAL;
            float2 uv : TEXCOORD0;

            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            float3 positionOS : TEXCOORD1;
            half3 normalWS : TEXCOORD2;
            half3 normalOS : TEXCOORD3;
            float2 uv : TEXCOORD4;
            half fogFactor : TEXCOORD5;
            #if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                float4 screenShadowCoord : TEXCOORD6;
            #endif

            UNITY_VERTEX_INPUT_INSTANCE_ID
            UNITY_VERTEX_OUTPUT_STEREO
        };

        Varyings ForwardVert(Attributes input)
        {
            Varyings o = (Varyings)0;

            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_TRANSFER_INSTANCE_ID(input, o);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

            VertexPositionInputs p =
                GetVertexPositionInputs(input.positionOS.xyz);

            o.positionCS = p.positionCS;
            #if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                o.screenShadowCoord = GetShadowCoord(p);
            #endif
            o.positionWS = p.positionWS;
            o.positionOS = input.positionOS.xyz;

            o.normalWS =
                TransformObjectToWorldNormal(input.normalOS);

            o.normalOS = input.normalOS;
            o.uv = input.uv;

            o.fogFactor =
                ComputeFogFactor(p.positionCS.z);

            return o;
        }

        float2 TransformBrushUV(float2 uv)
        {
            uv = TRANSFORM_TEX(uv, _BrushMap);
            float angle = radians(_BrushRotation);

            float sine, cosine;
            sincos(angle, sine, cosine);

            float2 rotatedUV = float2(uv.x * cosine - uv.y * sine, uv.x * sine + uv.y * cosine);

            // Periodic curved brush strokes
            float arc = 0.5 * (
                1.0 - cos(rotatedUV.y * 6.28318530718)
            );

            rotatedUV.x += _BrushCurvature * arc;

            return rotatedUV;
        }

        // BRUSH MAP

        half SampleBrush(float2 uv, float3 positionOS, half3 normalOS)
        {
            half brush;
            if (_BrushMapping < 0.5)
            {
                brush = SAMPLE_TEXTURE2D(
                    _BrushMap,
                    sampler_BrushMap,
                    TransformBrushUV(uv * _BrushScale)
                ).r;
            }
            else
            {
                half3 weights = normalOS * normalOS;

                weights /= max(weights.x + weights.y + weights.z, 0.0001h);

                float3 scaledPosition = positionOS * _BrushScale;

                half sampleX = SAMPLE_TEXTURE2D(
                    _BrushMap,
                    sampler_BrushMap,
                    TransformBrushUV(scaledPosition.yz)
                ).r;

                half sampleY = SAMPLE_TEXTURE2D(
                    _BrushMap,
                    sampler_BrushMap,
                    TransformBrushUV(scaledPosition.zx)
                ).r;

                half sampleZ = SAMPLE_TEXTURE2D(
                    _BrushMap,
                    sampler_BrushMap,
                    TransformBrushUV(scaledPosition.xy)
                ).r;

                brush = dot(half3(sampleX, sampleY, sampleZ), weights);
            }
            // Remap the sampled grayscale before using it for either shading effect.
            // A contrast of one preserves the original brush values.
            return saturate((brush - 0.5h) * _BrushContrast + 0.5h);
        }

        ENDHLSL

        // FORWARD LIT

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForwardOnly" }

            HLSLPROGRAM

            #pragma target 3.0
            #pragma vertex ForwardVert
            #pragma fragment ForwardFrag

            #pragma multi_compile_instancing
            #pragma multi_compile_fog

            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            half4 ForwardFrag(Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                half3 albedo = SAMPLE_TEXTURE2D(
                    _BaseMap, sampler_BaseMap,
                    input.uv * _BaseMap_ST.xy + _BaseMap_ST.zw).rgb * _BaseColor.rgb;
                half3 normalWS = normalize(input.normalWS);
                half3 normalOS = normalize(input.normalOS);
                half brush = SampleBrush(input.uv, input.positionOS, normalOS);

                #if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
                    float4 shadowCoord = input.screenShadowCoord;
                #else
                    float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS);
                #endif
                Light mainLight = GetMainLight(shadowCoord);

                // One lighting field drives both the silhouette of the terminator
                // and brush-shaped light/dark patches across the surface.
                half lightField = dot(normalWS, mainLight.direction) * 0.5h + 0.5h;
                lightField += (brush - 0.5h) * 2.0h * _BrushInfluence;
                lightField = saturate(lightField);
                lightField *= lerp(1.0h, mainLight.shadowAttenuation, _RealtimeShadowStrength);

                half width = max(_ToonSoftness, fwidth(lightField) * 0.5h);
                half lightMask = smoothstep(
                    _ToonThreshold - width, _ToonThreshold + width, lightField);
                half3 color = albedo * lerp(_DarkShadowColor.rgb, _LightTint.rgb, lightMask);
                // Preserve restrained grayscale variation inside both lighting bands.
                color *= 1.0h + (brush - 0.5h) * 2.0h * _BrushToneVariation;
                return half4(MixFog(color, input.fogFactor), 1.0h);
            }

            ENDHLSL
        }

        // SHADOW CASTER

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            ZWrite On
            ZTest LEqual
            ColorMask 0

            HLSLPROGRAM

            #pragma target 3.0
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag

            #pragma multi_compile_instancing
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            float3 _LightDirection;
            float3 _LightPosition;

            float4 ShadowVert(Attributes input) : SV_POSITION
            {
                UNITY_SETUP_INSTANCE_ID(input);

                float3 p =
                    TransformObjectToWorld(input.positionOS.xyz);

                float3 n =
                    TransformObjectToWorldNormal(input.normalOS);

                #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
                    float3 l = normalize(_LightPosition - p);
                #else
                    float3 l = _LightDirection;
                #endif

                float4 clip =
                    TransformWorldToHClip(
                        ApplyShadowBias(p, n, l)
                    );

                #if UNITY_REVERSED_Z
                    clip.z = min(clip.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    clip.z = max(clip.z, UNITY_NEAR_CLIP_VALUE);
                #endif

                return clip;
            }

            half4 ShadowFrag() : SV_Target
            {
                return 0;
            }

            ENDHLSL
        }

        // DEPTH ONLY

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask 0

            HLSLPROGRAM

            #pragma target 3.0
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing

            float4 DepthVert(Attributes input) : SV_POSITION
            {
                UNITY_SETUP_INSTANCE_ID(input);

                return TransformObjectToHClip(input.positionOS.xyz);
            }

            half4 DepthFrag() : SV_Target
            {
                return 0;
            }

            ENDHLSL
        }
    }

    FallBack Off
}
