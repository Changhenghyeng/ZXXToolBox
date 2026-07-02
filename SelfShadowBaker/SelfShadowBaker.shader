// Hidden bake shader for SelfShadowBaker.
// Pass 0 : render light-space *linear* depth (occluder distance from the light).
// Pass 1 : rasterize the mesh into its uv2 (lightmap UV) atlas and sample the
//          depth map with PCF to produce a self-shadow visibility mask.
//
// Output convention (Pass 1):
//   rgb = visibility, 1 = lit, 0 = shadow      (consumed as lerp(1, R, _Shadow_Strength))
//   a   = coverage,   1 = texel covered by a triangle, 0 = gutter
// The C# side reads R as visibility and A as coverage, then dilates + packs.
Shader "Hidden/SelfShadowBaker"
{
    Properties { }
    SubShader
    {
        // ---------------------------------------------------------------
        // Pass 0 : light-space linear depth
        // ---------------------------------------------------------------
        Pass
        {
            Name "LightDepth"
            Cull Off ZWrite On ZTest LEqual
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            float4x4 _LightView; // world -> light view (GL convention, -Z forward)

            struct appdata { float4 vertex : POSITION; };
            struct v2f { float4 pos : SV_POSITION; float viewZ : TEXCOORD0; };

            v2f vert (appdata v)
            {
                v2f o;
                float3 wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.pos   = UnityWorldToClipPos(wpos);                // uses VP set by CommandBuffer
                o.viewZ = -mul(_LightView, float4(wpos, 1.0)).z;    // positive linear distance from light
                return o;
            }

            float frag (v2f i) : SV_Target { return i.viewZ; }
            ENDHLSL
        }

        // ---------------------------------------------------------------
        // Pass 1 : uv2 unwrap + PCF self-shadow
        // ---------------------------------------------------------------
        Pass
        {
            Name "UnwrapBake"
            Cull Off ZWrite Off ZTest Always
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _LightDepthTex;
            float4    _LightDepthTex_TexelSize; // (1/w, 1/h, w, h)
            float4x4  _LightView;  // world -> light view
            float     _HalfW;      // light ortho half-width  (world units)
            float     _HalfH;      // light ortho half-height (world units)
            float     _DepthBias;  // in light-space texels
            float     _NormalBias; // in light-space texels
            float     _PcfRadius;  // in shadow-map texels
            float     _TexelWorld; // world size of one shadow-map texel
            float     _FlipV;      // flip bake atlas vertically (escape hatch)
            int       _PcfTaps;    // 1 / 3 / 5 / 7 / 9 (NxN)

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv2 : TEXCOORD1; };
            struct v2f     { float4 pos : SV_POSITION; float3 wpos : TEXCOORD0; float3 wnrm : TEXCOORD1; };

            v2f vert (appdata v)
            {
                v2f o;
                float2 p = v.uv2 * 2.0 - 1.0;          // uv2 [0,1] -> clip [-1,1]
                #if UNITY_UV_STARTS_AT_TOP
                    p.y = -p.y;   // D3D/Metal/console render targets have a top-left origin, so the
                                  // read-back atlas is V-flipped vs how uv2 samples at runtime.
                                  // Compensate per-platform here so the DEFAULT (Flip V off) is correct.
                #endif
                if (_FlipV > 0.5) p.y = -p.y;           // manual override (escape hatch), normally off
                o.pos  = float4(p, 0.0, 1.0);
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.wnrm = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                float3 n    = normalize(i.wnrm);
                // normal-offset the sample point: resolution-independent, kills most acne
                float3 wpos = i.wpos + n * (_NormalBias * _TexelWorld);

                // Analytic orthographic mapping into the light depth map.
                // Pass 0 rasterized each surface at clip.xy = (viewX/_HalfW, viewY/_HalfH);
                // sampling at that same uv round-trips on every platform with no projection-matrix
                // flip ambiguity — this is what removes the vertical mirror.
                float3 vp    = mul(_LightView, float4(wpos, 1.0)).xyz;
                float  fragZ = -vp.z;
                float2 uv    = float2(vp.x / _HalfW, vp.y / _HalfH) * 0.5 + 0.5;

                // outside the shadow frustum -> treat as fully lit, but still covered
                if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
                    return float4(1.0, 1.0, 1.0, 1.0);

                float depthBiasW = _DepthBias * _TexelWorld;

                int   halfN      = (int)max((_PcfTaps - 1) / 2, 0);
                float stepTexels = (halfN > 0) ? (_PcfRadius / halfN) : 0.0;
                float2 texel     = _LightDepthTex_TexelSize.xy;

                float lit = 0.0; int count = 0;
                [loop] for (int y = -halfN; y <= halfN; ++y)
                [loop] for (int x = -halfN; x <= halfN; ++x)
                {
                    float2 off = float2(x, y) * stepTexels * texel;
                    float  occ = tex2Dlod(_LightDepthTex, float4(uv + off, 0, 0)).r;
                    lit  += (fragZ - depthBiasW <= occ) ? 1.0 : 0.0; // 1 = lit (not occluded)
                    count++;
                }
                lit /= max(count, 1);

                return float4(lit, lit, lit, 1.0); // rgb = visibility, a = coverage
            }
            ENDHLSL
        }

        // ---------------------------------------------------------------
        // Pass 2 : AO accumulate (additive, one sample direction per draw).
        // Output is summed across many directions via Blend One One:
        //   r += w * openness   g += w   a += coverage
        // C# resolves AO = r / g (cosine-weighted average openness), 1 = open, 0 = occluded.
        // ---------------------------------------------------------------
        Pass
        {
            Name "AoAccumulate"
            Cull Off ZWrite Off ZTest Always
            Blend One One
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _LightDepthTex;
            float4x4  _LightView;
            float     _HalfW, _HalfH, _TexelWorld, _DepthBias, _NormalBias, _FlipV;
            float3    _AoDir;     // sample direction (world): from the surface toward the sky/source
            float3    _LightVec;  // toward the sun (world) = -lightForward, used for light bias
            float     _LightBias; // 0 = uniform ambient, 1 = biased toward the sun
            float     _AoMaxDist; // world units; occluders beyond this are ignored (local AO)

            struct appdata { float4 vertex : POSITION; float3 normal : NORMAL; float2 uv2 : TEXCOORD1; };
            struct v2f     { float4 pos : SV_POSITION; float3 wpos : TEXCOORD0; float3 wnrm : TEXCOORD1; };

            v2f vert (appdata v)
            {
                v2f o;
                float2 p = v.uv2 * 2.0 - 1.0;
                #if UNITY_UV_STARTS_AT_TOP
                    p.y = -p.y;
                #endif
                if (_FlipV > 0.5) p.y = -p.y;
                o.pos  = float4(p, 0.0, 1.0);
                o.wpos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.wnrm = UnityObjectToWorldNormal(v.normal);
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                float3 N = normalize(i.wnrm);
                float  w = max(0.0, dot(N, _AoDir));                          // cosine hemisphere weight
                w *= lerp(1.0, saturate(dot(_AoDir, _LightVec)), _LightBias); // optional light-dir bias
                if (w <= 0.0) return float4(0, 0, 0, 0);                      // below horizon / culled

                float3 wpos  = i.wpos + N * (_NormalBias * _TexelWorld);
                float3 vp    = mul(_LightView, float4(wpos, 1.0)).xyz;
                float  fragZ = -vp.z;
                float2 uv    = float2(vp.x / _HalfW, vp.y / _HalfH) * 0.5 + 0.5;

                float openness = 1.0; // 1 = nothing blocking along this direction
                if (uv.x >= 0.0 && uv.x <= 1.0 && uv.y >= 0.0 && uv.y <= 1.0)
                {
                    float occ = tex2Dlod(_LightDepthTex, float4(uv, 0, 0)).r;
                    float gap = fragZ - _DepthBias * _TexelWorld - occ; // distance to occluder along dir
                    if (gap > 0.0)
                        openness = saturate(gap / max(_AoMaxDist, 1e-4)); // near occluder -> ~0 (dark)
                }
                return float4(w * openness, w, 0.0, 1.0); // additive accumulation
            }
            ENDHLSL
        }
    }
    Fallback Off
}
