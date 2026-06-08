Shader "Hidden/ShadowBaker/Blur"
{
    Properties
    {
        _MainTex    ("Texture", 2D) = "white" {}
        _FalloffPx  ("Falloff distance (px)", Float) = 32
        _MinAlpha   ("Min alpha at far edge", Range(0,1)) = 0
        _MaxAlpha   ("Max alpha at contact",  Range(0,1)) = 1
        _FalloffPow ("Falloff curve exponent", Range(0.2, 4.0)) = 1.5
        _Samples    ("Radial samples", Float) = 32
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Cull Off ZWrite Off ZTest Always

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_MainTex);
        SAMPLER(sampler_MainTex);
        float4 _MainTex_TexelSize;

        float _FalloffPx;
        float _MinAlpha;
        float _MaxAlpha;
        float _FalloffPow;
        float _Samples;

        struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
        struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

        Varyings Vert (Attributes IN)
        {
            Varyings OUT;
            OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
            OUT.uv = IN.uv;
            return OUT;
        }

        // -------- Distance field + falloff in one pass --------
        //
        // For each pixel, find the distance (in pixels) to the nearest pixel of opposite
        // class — i.e. if we're INSIDE the shadow, distance to the nearest outside pixel;
        // if outside, distance to nearest inside.
        //
        // We use radial sampling (spiral outward from the pixel) and binary-search-like
        // refinement. This is approximate but visually indistinguishable from exact JFA
        // for our purposes, and runs in one pass.
        half4 FragDistanceFalloff (Varyings IN) : SV_Target
        {
            float falloff = max(_FalloffPx, 0.0001);
            int samples = max(8, (int)_Samples);
            float2 texel = _MainTex_TexelSize.xy;

            // Read center: are we currently inside the shadow?
            float centerVal = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv).r;
            bool inside = centerVal > 0.5;

            // We search OUTWARD up to `falloff` pixels in many directions and find the
            // closest sample of opposite class. The result is the distance to the edge.
            // Spiral pattern: 4 full turns, samples placed by sqrt(t) for uniform density.
            float bestDist = falloff;

            UNITY_LOOP
            for (int i = 0; i < samples; i++)
            {
                float t = ((float)i + 0.5) / (float)samples;     // 0..1
                float r = sqrt(t) * falloff;                     // radius in pixels
                float ang = t * 6.2831853 * 4.0;                 // 4 spiral turns
                float2 off = float2(cos(ang), sin(ang)) * r * texel;

                float sampleVal = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv + off).r;
                bool sampleInside = sampleVal > 0.5;

                // If this sample is of opposite class from self, r is an upper bound
                // on our distance to the boundary.
                if (sampleInside != inside)
                {
                    bestDist = min(bestDist, r);
                }
            }

            // Convert distance to alpha:
            //
            // INSIDE the shadow (the building's silhouette + everything covered by it):
            //   uniform MaxAlpha — no gradients, no internal artifacts. The original
            //   attempt at a signed distance field caused "ghost rings" wherever the
            //   silhouette had concave regions or internal sub-shapes (porches, eaves,
            //   walls projecting at different angles).
            //
            // OUTSIDE the shadow:
            //   fade from MaxAlpha at the boundary to MinAlpha at FalloffPx distance.
            float alpha;
            if (inside)
            {
                alpha = _MaxAlpha;
            }
            else
            {
                // bestDist is in [0, falloff]; t goes from 1 at the boundary to 0 far away.
                float t = 1.0 - saturate(bestDist / falloff);
                t = pow(t, _FalloffPow);
                alpha = lerp(_MinAlpha, _MaxAlpha, t);
            }

            // Output as grayscale: white = no shadow, black = full shadow.
            float v = 1.0 - alpha;
            return half4(v, v, v, 1);
        }

        // Plain Gaussian blur (kept as a fallback option for users who want it).
        half4 FragGaussian (Varyings IN) : SV_Target
        {
            float radius = max(_FalloffPx, 0.0001);
            float sigma  = radius * 0.5;
            float twoSigmaSq = 2.0 * sigma * sigma;
            int kernelSize = max(1, (int)floor(radius));
            float2 texel = _MainTex_TexelSize.xy;

            // 2D Gaussian in one pass (small radii) — simpler than two passes here
            // since we only have one alternative mode.
            float sum = 0, weight = 0;
            UNITY_LOOP
            for (int y = -kernelSize; y <= kernelSize; y++)
            {
                UNITY_LOOP
                for (int x = -kernelSize; x <= kernelSize; x++)
                {
                    float d2 = (float)(x*x + y*y);
                    float w = exp(-d2 / twoSigmaSq);
                    sum    += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv + float2(x, y) * texel).r * w;
                    weight += w;
                }
            }
            float mask = sum / max(weight, 1e-4);
            float v = 1.0 - mask;
            return half4(v, v, v, 1);
        }
        ENDHLSL

        Pass
        {
            Name "DistanceFalloff"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragDistanceFalloff
            ENDHLSL
        }

        Pass
        {
            Name "Gaussian"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragGaussian
            ENDHLSL
        }
    }
}
