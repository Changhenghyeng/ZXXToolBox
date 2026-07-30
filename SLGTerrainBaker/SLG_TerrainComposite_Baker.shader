Shader "Hidden/SLG/Terrain Mobile Baker"
{
    Properties
    {
        _ControlMap("Control", 2D) = "red" {}
        _BottomBase("Bottom", 2D) = "black" {}
        _Terrain0Base("Type A Base", 2D) = "white" {}
        _Terrain1Base("Type B Base", 2D) = "white" {}
        _Terrain2Base("Type C Base", 2D) = "white" {}
        _Terrain3Base("Type D Base", 2D) = "white" {}
        _Terrain0Variant("Type A Variant", 2D) = "white" {}
        _Terrain1Variant("Type B Variant", 2D) = "white" {}
        _Terrain2Variant("Type C Variant", 2D) = "white" {}
        _Terrain3Variant("Type D Variant", 2D) = "white" {}
        _NoiseNear("Near Noise", 2D) = "gray" {}
        _NoiseMiddle("Middle Noise", 2D) = "gray" {}
        _NoiseFar("Far Noise", 2D) = "gray" {}
        _CopyTex("Copy Texture", 2D) = "white" {}
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Cull Off
        ZWrite Off
        ZTest Always

        HLSLINCLUDE
        #pragma target 3.5
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_ControlMap);
        SAMPLER(sampler_ControlMap);
        TEXTURE2D(_BottomBase);
        SAMPLER(sampler_BottomBase);
        TEXTURE2D(_Terrain0Base);
        SAMPLER(sampler_Terrain0Base);
        TEXTURE2D(_Terrain1Base);
        SAMPLER(sampler_Terrain1Base);
        TEXTURE2D(_Terrain2Base);
        SAMPLER(sampler_Terrain2Base);
        TEXTURE2D(_Terrain3Base);
        SAMPLER(sampler_Terrain3Base);
        TEXTURE2D(_Terrain0Variant);
        SAMPLER(sampler_Terrain0Variant);
        TEXTURE2D(_Terrain1Variant);
        SAMPLER(sampler_Terrain1Variant);
        TEXTURE2D(_Terrain2Variant);
        SAMPLER(sampler_Terrain2Variant);
        TEXTURE2D(_Terrain3Variant);
        SAMPLER(sampler_Terrain3Variant);
        TEXTURE2D(_NoiseNear);
        SAMPLER(sampler_NoiseNear);
        TEXTURE2D(_NoiseMiddle);
        SAMPLER(sampler_NoiseMiddle);
        TEXTURE2D(_NoiseFar);
        SAMPLER(sampler_NoiseFar);
        TEXTURE2D(_CopyTex);
        SAMPLER(sampler_CopyTex);

        CBUFFER_START(UnityPerMaterial)
            float4 _BottomBase_ST;
            float4 _Terrain0Base_ST;
            float4 _Terrain1Base_ST;
            float4 _Terrain2Base_ST;
            float4 _Terrain3Base_ST;
            float4 _Terrain0Variant_ST;
            float4 _Terrain1Variant_ST;
            float4 _Terrain2Variant_ST;
            float4 _Terrain3Variant_ST;
            float4 _NoiseNear_ST;
            float4 _NoiseMiddle_ST;
            float4 _NoiseFar_ST;
            half4 _BottomTint;
            half4 _Terrain0Tint;
            half4 _Terrain1Tint;
            half4 _Terrain2Tint;
            half4 _Terrain3Tint;
            float _ControlSharpness;
            float _Terrain0VariantRatio;
            float _Terrain1VariantRatio;
            float _Terrain2VariantRatio;
            float _Terrain3VariantRatio;
            float _VariantEdgeSoftness;
            float _FarVariantStrength;
            float _NearInfluence;
            float _MiddleInfluence;
            float _NoiseBakeLod;
            float _UseVariants;
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
        };

        Varyings Vert(Attributes input)
        {
            Varyings output = (Varyings)0;
            output.positionCS =
                TransformObjectToHClip(input.positionOS.xyz);
            output.uv = input.uv;
            return output;
        }

        float2 TransformUv(float2 uv, float4 st)
        {
            return uv * st.xy + st.zw;
        }

        half4 NormalizeWeights(half4 weights)
        {
            weights = saturate(weights);
            if (_ControlSharpness != 1.0)
            {
                weights = pow(weights, _ControlSharpness);
            }
            half sum = dot(weights, half4(1, 1, 1, 1));
            return sum > 0.0001h
                ? weights / sum
                : half4(0, 0, 0, 0);
        }

        // 三个噪声层始终参与，通过权重连续衰减替代硬切
        // nearWeight: lod=0时为1，lod=1时为0（线性衰减）
        // middleWeight: lod=1时为1，lod=2时为0（线性衰减）
        // farNoise 始终为基底，不受 lod 影响
        half4 BuildCombinedNoise(float2 uv, float lod)
        {
            half4 farNoise = SAMPLE_TEXTURE2D(
                _NoiseFar,
                sampler_NoiseFar,
                TransformUv(uv, _NoiseFar_ST));

            half nearWeight = saturate(1.0 - lod);
            half middleWeight = saturate(2.0 - lod);

            half nearInf = nearWeight * (half)_NearInfluence;
            half middleInf = middleWeight * (half)_MiddleInfluence;

            half invNorm = rsqrt(
                1.0h +
                nearInf * nearInf +
                middleInf * middleInf);

            half4 nearNoise = SAMPLE_TEXTURE2D(
                _NoiseNear,
                sampler_NoiseNear,
                TransformUv(uv, _NoiseNear_ST));
            half4 middleNoise = SAMPLE_TEXTURE2D(
                _NoiseMiddle,
                sampler_NoiseMiddle,
                TransformUv(uv, _NoiseMiddle_ST));

            half4 result = 0.5h + (
                (farNoise - 0.5h) +
                (middleNoise - 0.5h) * middleInf +
                (nearNoise - 0.5h) * nearInf) * invNorm;
            return saturate(result);
        }

        half4 BuildVariantMask(half4 noise)
        {
            half softness = clamp(
                (half)_VariantEdgeSoftness * 0.35h,
                0.001h,
                0.499h);
            half4 ratio = saturate(half4(
                _Terrain0VariantRatio,
                _Terrain1VariantRatio,
                _Terrain2VariantRatio,
                _Terrain3VariantRatio));
            half4 threshold = 1.0h - ratio;
            half4 mask = smoothstep(
                threshold - softness,
                threshold + softness,
                noise);
            mask *= step(0.0001h, ratio);
            mask = lerp(
                mask,
                half4(1.0h, 1.0h, 1.0h, 1.0h),
                step(0.9999h, ratio));
            return mask * (half)_FarVariantStrength;
        }

        half3 SampleBottom(float2 uv)
        {
            return SAMPLE_TEXTURE2D(
                _BottomBase,
                sampler_BottomBase,
                TransformUv(uv, _BottomBase_ST)).rgb *
                _BottomTint.rgb;
        }

        half4 FragFarBasemap(Varyings input) : SV_Target
        {
            half4 rawControl = saturate(SAMPLE_TEXTURE2D(
                _ControlMap,
                sampler_ControlMap,
                input.uv));
            half coverage = saturate(dot(
                rawControl,
                half4(1, 1, 1, 1)));
            half4 weights = NormalizeWeights(rawControl);

            half3 base0 = SAMPLE_TEXTURE2D(
                _Terrain0Base,
                sampler_Terrain0Base,
                TransformUv(input.uv, _Terrain0Base_ST)).rgb *
                _Terrain0Tint.rgb;
            half3 base1 = SAMPLE_TEXTURE2D(
                _Terrain1Base,
                sampler_Terrain1Base,
                TransformUv(input.uv, _Terrain1Base_ST)).rgb *
                _Terrain1Tint.rgb;
            half3 base2 = SAMPLE_TEXTURE2D(
                _Terrain2Base,
                sampler_Terrain2Base,
                TransformUv(input.uv, _Terrain2Base_ST)).rgb *
                _Terrain2Tint.rgb;
            half3 base3 = SAMPLE_TEXTURE2D(
                _Terrain3Base,
                sampler_Terrain3Base,
                TransformUv(input.uv, _Terrain3Base_ST)).rgb *
                _Terrain3Tint.rgb;

            if (_UseVariants > 0.5)
            {
                half4 mask = BuildVariantMask(
                    BuildCombinedNoise(input.uv, 2.0));
                half3 variant0 = SAMPLE_TEXTURE2D(
                    _Terrain0Variant,
                    sampler_Terrain0Variant,
                    TransformUv(
                        input.uv,
                        _Terrain0Variant_ST)).rgb *
                        _Terrain0Tint.rgb;
                half3 variant1 = SAMPLE_TEXTURE2D(
                    _Terrain1Variant,
                    sampler_Terrain1Variant,
                    TransformUv(
                        input.uv,
                        _Terrain1Variant_ST)).rgb *
                        _Terrain1Tint.rgb;
                half3 variant2 = SAMPLE_TEXTURE2D(
                    _Terrain2Variant,
                    sampler_Terrain2Variant,
                    TransformUv(
                        input.uv,
                        _Terrain2Variant_ST)).rgb *
                        _Terrain2Tint.rgb;
                half3 variant3 = SAMPLE_TEXTURE2D(
                    _Terrain3Variant,
                    sampler_Terrain3Variant,
                    TransformUv(
                        input.uv,
                        _Terrain3Variant_ST)).rgb *
                        _Terrain3Tint.rgb;
                base0 = lerp(base0, variant0, mask.r);
                base1 = lerp(base1, variant1, mask.g);
                base2 = lerp(base2, variant2, mask.b);
                base3 = lerp(base3, variant3, mask.a);
            }

            half3 terrain =
                base0 * weights.r +
                base1 * weights.g +
                base2 * weights.b +
                base3 * weights.a;
            return half4(
                lerp(SampleBottom(input.uv), terrain, coverage),
                1.0h);
        }

        half4 FragPackedNoise(Varyings input) : SV_Target
        {
            return BuildCombinedNoise(input.uv, _NoiseBakeLod);
        }

        half4 FragCopy(Varyings input) : SV_Target
        {
            return SAMPLE_TEXTURE2D(
                _CopyTex,
                sampler_CopyTex,
                input.uv);
        }
        ENDHLSL

        Pass
        {
            Name "FarBasemap"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragFarBasemap
            ENDHLSL
        }

        Pass
        {
            Name "PackedNoise"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragPackedNoise
            ENDHLSL
        }

        Pass
        {
            Name "CopyTerrainSlice"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragCopy
            ENDHLSL
        }
    }

    FallBack Off
}
