Shader "Custom/ShadowDecal_PlantWind_URP"
{
    Properties
    {
        [MainTexture] _BaseMap("Shadow Texture", 2D) = "white" {}
        [MainColor]   _BaseColor("Shadow Color", Color) = (0, 0, 0, 1)

        // BlendMode — standard URP hidden props for inspector
        [HideInInspector]  _Surface("__surface", Float) = 1.0
        [HideInInspector]  _Blend("__mode", Float) = 3.0
        [HideInInspector]  _Cull("__cull", Float) = 2.0
        [HideInInspector] _Cutoff("Alpha Cutoff", Range(0, 1)) = 0.5
        [HideInInspector] _AlphaClip("__clip", Float) = 0.0
        [HideInInspector] _BlendOp("__blendop", Float) = 0.0
        [HideInInspector] _SrcBlend("__src", Float) = 2.0
        [HideInInspector] _DstBlend("__dst", Float) = 0.0
        [HideInInspector] _SrcBlendAlpha("__srcA", Float) = 2.0
        [HideInInspector] _DstBlendAlpha("__dstA", Float) = 0.0
        [HideInInspector] _ZWrite("__zw", Float) = 0.0
        [HideInInspector] _AlphaToMask("__alphaToMask", Float) = 0.0
        _QueueOffset("Queue offset", Float) = 0.0

        // Obsolete
        [HideInInspector] _MainTex("BaseMap", 2D) = "white" {}
        [HideInInspector] _Color("Base Color", Color) = (0.5, 0.5, 0.5, 1)
        [HideInInspector] _SampleGI("SampleGI", float) = 0.0

        [Header(Plant Wind)]
        _WindDirection ("Wind Direction (World XZ)", Vector) = (1, 0, 0, 0)
        _WindStrength ("Wind Strength (World Units)", Range(0, 2)) = 0.15
        _WindSpeed ("Wind Speed", Range(0, 10)) = 1
        _WindFrequency ("Wind Frequency", Range(0.01, 10)) = 1
        _WindPhaseOffset ("Wind Phase Offset", Float) = 0

        [Header(Shadow Root)]
        _WindRootX ("Root Position X (Texture UV)", Range(0, 1)) = 0.5
        _WindRootY ("Root Position Y (Texture UV)", Range(0, 1)) = 0.5
        _WindRootRadius ("Fixed Root Radius (UV)", Range(0, 0.5)) = 0.03
        _WindFadeWidth ("Root To Canopy Distance (UV)", Range(0.001, 1)) = 0.5

        [Header(Projection Alignment)]
        // Ground projection offset from the tree canopy to its baked shadow.
        // These controls correct the spatial phase; they do not lift the ground quad.
        _CanopyShadowOffsetX ("Canopy To Shadow Offset X (World)", Float) = 0
        _CanopyShadowOffsetZ ("Canopy To Shadow Offset Z (World)", Float) = 0
        [Header(Gradient)]
        [Toggle(_GRADIENT_ON)] _GradientEnable ("Enable", Float) = 0
        _GradientColor     ("Color",        Color)         = (1, 1, 1, 1)
        _GradientDirection ("Directional",  Range(0, 1))   = 0
        _GradientAngle     ("Angle",        Range(0, 360)) = 0
        _GradientEdge      ("Edge",         Range(0, 1))   = 1
        _GradientSize      ("Size",         Range(0, 1))   = 0.5
        _GradientCenterX   ("Center X",     Range(0, 1))   = 0.5
        _GradientCenterY   ("Center Y",     Range(0, 1))   = 0.5
        _GradientRoundness ("Roundness",    Range(0, 1))   = 0
        _GradientAspect    ("Aspect",       Range(1, 5))   = 1

        // Sky/air color bias for the shadow. Light (shallow) shadow leans toward the
        // tint color; deep shadow stays neutral. Depth is read from the shadow texture
        // AFTER the gradient, so the gradient also drives where the tint shows.
        [Header(Sky Tint)]
        [Toggle(_SHADOWTINT_ON)] _ShadowTintEnable ("Enable Sky Tint", Float) = 0
        _ShadowTintColor         ("  Tint Color (sky/air)", Color) = (0.55, 0.7, 1.0, 1)
        _ShadowTintStrength      ("  Tint Strength",       Range(0, 1))     = 0.8
        _ShadowTintFalloff       ("  Depth Falloff",       Range(0.2, 5.0)) = 1.5
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
            "UniversalMaterialType" = "Unlit"
            "Queue" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
        }
        LOD 100

        Blend [_SrcBlend][_DstBlend], [_SrcBlendAlpha][_DstBlendAlpha]
        ZWrite [_ZWrite]
        Cull [_Cull]

        // ═══════════════════════════════════════════
        // Forward Pass — gradient added
        // ═══════════════════════════════════════════
        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForwardOnly" }

            AlphaToMask[_AlphaToMask]

            HLSLPROGRAM
            #pragma target 3.0

            #pragma vertex UnlitPassVertex
            #pragma fragment GradientUnlitFragment

            #pragma shader_feature_local_fragment _SURFACE_TYPE_TRANSPARENT
            #pragma shader_feature_local_fragment _ALPHATEST_ON
            #pragma shader_feature_local_fragment _ALPHAMODULATE_ON
            #pragma shader_feature_local_fragment _GRADIENT_ON
            #pragma shader_feature_local_fragment _SHADOWTINT_ON

            #pragma multi_compile_fog
            #pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION
            #pragma multi_compile_fragment _ _DBUFFER_MRT1 _DBUFFER_MRT2 _DBUFFER_MRT3
            #pragma multi_compile _ DEBUG_DISPLAY
            #pragma multi_compile_fragment _ LOD_FADE_CROSSFADE
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RenderingLayers.hlsl"

            #pragma multi_compile_instancing
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Unlit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #if defined(LOD_FADE_CROSSFADE)
                #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/LODCrossFade.hlsl"
            #endif

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                half4  _BaseColor;
                half   _Cutoff;
                half   _Surface;
                half   _GradientDirection;
                half   _GradientAngle;
                half   _GradientEdge;
                half   _GradientSize;
                half   _GradientCenterX;
                half   _GradientCenterY;
                half   _GradientRoundness;
                half   _GradientAspect;
                half4  _GradientColor;
                half4  _ShadowTintColor;
                half   _ShadowTintStrength;
                half   _ShadowTintFalloff;
                float4 _WindDirection;
                float _WindStrength;
                float _WindSpeed;
                float _WindFrequency;
                float _WindPhaseOffset;
                float _WindRootX;
                float _WindRootY;
                float _WindRootRadius;
                float _WindFadeWidth;
                float _CanopyShadowOffsetX;
                float _CanopyShadowOffsetZ;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                #if defined(DEBUG_DISPLAY)
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float2 uv : TEXCOORD0;
                float  fogCoord : TEXCOORD1;
                float4 positionCS : SV_POSITION;
                float3 groundPositionWS : TEXCOORD5;
                #if defined(DEBUG_DISPLAY)
                float3 positionWS : TEXCOORD2;
                float3 normalWS   : TEXCOORD3;
                float3 viewDirWS  : TEXCOORD4;
                #endif
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            void InitializeInputData(Varyings input, out InputData inputData)
            {
                inputData = (InputData)0;
                #if defined(DEBUG_DISPLAY)
                inputData.positionWS = input.positionWS;
                inputData.normalWS = input.normalWS;
                inputData.viewDirectionWS = input.viewDirWS;
                #else
                inputData.positionWS = float3(0, 0, 0);
                inputData.normalWS = half3(0, 0, 1);
                inputData.viewDirectionWS = half3(0, 0, 1);
                #endif
                inputData.shadowCoord = 0;
                inputData.fogCoord = 0;
                inputData.vertexLighting = half3(0, 0, 0);
                inputData.bakedGI = half3(0, 0, 0);
                inputData.normalizedScreenSpaceUV = 0;
                inputData.shadowMask = half4(1, 1, 1, 1);
            }

            Varyings UnlitPassVertex(Attributes input)
            {
                Varyings output = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs vertexInput = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = vertexInput.positionCS;
                output.groundPositionWS = vertexInput.positionWS;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                #if defined(_FOG_FRAGMENT)
                output.fogCoord = vertexInput.positionVS.z;
                #else
                output.fogCoord = ComputeFogFactor(vertexInput.positionCS.z);
                #endif
                #if defined(DEBUG_DISPLAY)
                VertexNormalInputs normalInput = GetVertexNormalInputs(input.normalOS, input.tangentOS);
                half3 viewDirWS = GetWorldSpaceViewDir(vertexInput.positionWS);
                output.positionWS = vertexInput.positionWS;
                output.normalWS = normalInput.normalWS;
                output.viewDirWS = viewDirWS;
                #endif
                return output;
            }

            float ShadowWindWeight(float2 uv)
            {
                float distanceFromRoot = length(uv - float2(_WindRootX, _WindRootY));
                return smoothstep(_WindRootRadius,
                    _WindRootRadius + max(_WindFadeWidth, 0.001), distanceFromRoot);
            }

            float2 GetWindShadowUV(float2 uv, float3 positionWS)
            {
                // Derivatives recover the actual UV-to-world mapping, including quad
                // rotation and scale. The mesh remains flat and needs no subdivisions.
                float2 dxWS = ddx(positionWS.xz);
                float2 dyWS = ddy(positionWS.xz);
                float2 dxUV = ddx(uv);
                float2 dyUV = ddy(uv);
                float determinant = dxWS.x * dyWS.y - dxWS.y * dyWS.x;
                float inverseDeterminant = abs(determinant) > 1e-12 ? rcp(determinant) : 0;
                float2 worldXToUV = (dxUV * dyWS.y - dyUV * dxWS.y) * inverseDeterminant;
                float2 worldZToUV = (dyUV * dxWS.x - dxUV * dyWS.x) * inverseDeterminant;

                float2 direction = _WindDirection.xz;
                float lengthSq = dot(direction, direction);
                direction = lengthSq > 0.00001 ? direction * rsqrt(lengthSq) : float2(1, 0);
                float2 directionUV = worldXToUV * direction.x + worldZToUV * direction.y;
                float2 sourceUV = uv;
                float2 sourceXZ = positionWS.xz;
                float2 projection = float2(_CanopyShadowOffsetX, _CanopyShadowOffsetZ);
                float time = _Time.y * _WindSpeed;

                // Inverse texture warp: evaluate at the source point to keep the
                // fixed root in place. Use the tree shader's exact two-wave formula.
                [unroll] for (int iteration = 0; iteration < 3; ++iteration)
                {
                    float weight = ShadowWindWeight(sourceUV);
                    float2 treeXZ = sourceXZ - projection * weight;
                    float phase = dot(treeXZ, float2(0.83, 1.17)) * _WindFrequency + _WindPhaseOffset;
                    float wave = sin(phase + time) * 0.75
                               + sin(phase * 1.91 - time * 1.37) * 0.25;
                    float displacement = wave * _WindStrength * weight;
                    sourceUV = uv - directionUV * displacement;
                    sourceXZ = positionWS.xz - direction * displacement;
                }
                return sourceUV;
            }
            void GradientUnlitFragment(
                Varyings input
                , out half4 outColor : SV_Target0
            #ifdef _WRITE_RENDERING_LAYERS
                , out float4 outRenderingLayers : SV_Target1
            #endif
            )
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv = GetWindShadowUV(input.uv, input.groundPositionWS);
                half4 texColor = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, saturate(uv));
                // White is neutral for a multiply decal. Never repeat edge pixels.
                float inside = step(0, uv.x) * step(uv.x, 1) * step(0, uv.y) * step(uv.y, 1);
                texColor = lerp(half4(1, 1, 1, 1), texColor, inside);

            #ifdef _GRADIENT_ON
                float angleRad = radians(_GradientAngle);
                float cosA = cos(angleRad);
                float sinA = sin(angleRad);

                // ══ t_proj — directional sweep along Angle ══
                float p1 = 0, p2 = sinA, p3 = cosA, p4 = sinA + cosA;
                float minProj = min(min(p1, p2), min(p3, p4));
                float maxProj = max(max(p1, p2), max(p3, p4));
                float projRange = maxProj - minProj;

                float proj  = uv.x * sinA + uv.y * cosA;
                float t_proj = projRange > 0.0001 ? (proj - minProj) / projRange : 0;

                // ══ t_dist — Lp-norm distance from center (0 at center, 1 at farthest corner) ══
                float cx = saturate(_GradientCenterX);
                float cy = saturate(_GradientCenterY);
                float asp = max(1.0, _GradientAspect);
                float rx = ((uv.x - cx) * cosA + (uv.y - cy) * sinA) * asp;
                float ry = -(uv.x - cx) * sinA + (uv.y - cy) * cosA;

                // Lp-norm (stable): factor out max to prevent pow() underflow on diagonals
                float r  = saturate(_GradientRoundness);
                float p  = lerp(2.0, 100.0, r);
                float rp = 1.0 / p;

                float mx   = max(abs(rx), abs(ry));
                float dist = mx > 0.0001 ? mx * pow(pow(abs(rx)/mx, p) + pow(abs(ry)/mx, p), rp) : 0;

                // Normalise to the farthest UV corner (same aspect scaling)
                float rx0 = ((0-cx)*cosA + (0-cy)*sinA) * asp; float ry0 = -(0-cx)*sinA + (0-cy)*cosA;
                float mx0 = max(abs(rx0), abs(ry0));
                float  d0 = mx0 > 0.0001 ? mx0 * pow(pow(abs(rx0)/mx0, p) + pow(abs(ry0)/mx0, p), rp) : 0;

                float rx1 = ((1-cx)*cosA + (0-cy)*sinA) * asp; float ry1 = -(1-cx)*sinA + (0-cy)*cosA;
                float mx1 = max(abs(rx1), abs(ry1));
                float  d1 = mx1 > 0.0001 ? mx1 * pow(pow(abs(rx1)/mx1, p) + pow(abs(ry1)/mx1, p), rp) : 0;

                float rx2 = ((0-cx)*cosA + (1-cy)*sinA) * asp; float ry2 = -(0-cx)*sinA + (1-cy)*cosA;
                float mx2 = max(abs(rx2), abs(ry2));
                float  d2 = mx2 > 0.0001 ? mx2 * pow(pow(abs(rx2)/mx2, p) + pow(abs(ry2)/mx2, p), rp) : 0;

                float rx3 = ((1-cx)*cosA + (1-cy)*sinA) * asp; float ry3 = -(1-cx)*sinA + (1-cy)*cosA;
                float mx3 = max(abs(rx3), abs(ry3));
                float  d3 = mx3 > 0.0001 ? mx3 * pow(pow(abs(rx3)/mx3, p) + pow(abs(ry3)/mx3, p), rp) : 0;

                float maxDist = max(max(d0, d1), max(d2, d3));
                float t_dist = maxDist > 0.0001 ? dist / maxDist : 0;

                // Blend: Directional=0 → radial,  Directional=1 → directional sweep
                float t = lerp(t_dist, t_proj, saturate(_GradientDirection));

                // Edge = softness,  Size = position / radius
                float halfW   = saturate(_GradientEdge) * 0.5;
                float edgePos = saturate(_GradientSize);
                float blend   = smoothstep(edgePos - halfW, edgePos + halfW, t) * _GradientColor.a;

                // Fade shadow toward white → multiply by white = no darkening effect
                texColor.rgb = lerp(texColor.rgb, half3(1, 1, 1), blend);
                texColor.a   = lerp(texColor.a,   1,               blend);
            #endif

                half3 color = texColor.rgb * _BaseColor.rgb;
                half alpha  = texColor.a   * _BaseColor.a;

                alpha = AlphaDiscard(alpha, _Cutoff);
                color = AlphaModulate(color, alpha);

            #ifdef _SHADOWTINT_ON
                // ── Sky / air color bias ──
                // The baked texture is grayscale (white = no shadow, black = full shadow)
                // and the gradient above has already pushed faded areas toward white, so a
                // single luminance read gives a gradient-aware "shadow depth" for free.
                //   depth = 0 → no shadow   |   depth = 1 → deepest shadow
                half depth = saturate((1.0 - Luminance(texColor.rgb)) * _BaseColor.a);

                // Multiply color of a fully sky-tinted shadow (white = no darkening).
                half3 tintMul = lerp(half3(1, 1, 1), _ShadowTintColor.rgb, depth);

                // Tint is strongest on shallow shadow and fades out as the shadow deepens,
                // so deep cores keep the original neutral color while soft edges pick up sky.
                half tintAmt = pow(saturate(1.0 - depth), _ShadowTintFalloff) * _ShadowTintStrength;

                color = lerp(color, tintMul, tintAmt);
            #endif

            #ifdef LOD_FADE_CROSSFADE
                LODFadeCrossFade(input.positionCS);
            #endif

                InputData inputData;
                InitializeInputData(input, inputData);
                SETUP_DEBUG_TEXTURE_DATA(inputData, input.uv, _BaseMap);

            #ifdef _DBUFFER
                ApplyDecalToBaseColor(input.positionCS, color);
            #endif

                half4 finalColor = UniversalFragmentUnlit(inputData, color, alpha);

            #if defined(_SCREEN_SPACE_OCCLUSION) && !defined(_SURFACE_TYPE_TRANSPARENT)
                float2 normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                AmbientOcclusionFactor aoFactor = GetScreenSpaceAmbientOcclusion(normalizedScreenSpaceUV);
                finalColor.rgb *= aoFactor.directAmbientOcclusion;
            #endif

            #if defined(_FOG_FRAGMENT)
            #if (defined(FOG_LINEAR) || defined(FOG_EXP) || defined(FOG_EXP2))
                float viewZ = -input.fogCoord;
                float nearToFarZ = max(viewZ - _ProjectionParams.y, 0);
                half fogFactor = ComputeFogFactorZ0ToFar(nearToFarZ);
            #else
                half fogFactor = 0;
            #endif
            #else
                half fogFactor = input.fogCoord;
            #endif
                finalColor.rgb = MixFog(finalColor.rgb, fogFactor);
                finalColor.a = OutputAlpha(finalColor.a, IsSurfaceTypeTransparent(_Surface));

                outColor = finalColor;

            #ifdef _WRITE_RENDERING_LAYERS
                uint renderingLayers = GetMeshRenderingLayer();
                outRenderingLayers = float4(EncodeMeshRenderingLayer(renderingLayers), 0, 0, 0);
            #endif
            }
            ENDHLSL
        }

    }
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}
