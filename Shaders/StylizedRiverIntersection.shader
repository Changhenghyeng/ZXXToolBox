Shader "Custom/StylizedRiverIntersection"
{
    Properties
    {
        [Header(Water Color And Depth)]
        _ShallowColor ("浅水色", Color) = (0.2, 0.65, 0.55, 1)
        _DeepColor ("深水色", Color) = (0.05, 0.18, 0.28, 1)
        _DepthDistance ("深水过渡距离", Range(0.1, 8)) = 2
        _DepthPower ("深浅过渡曲线", Range(0.1, 2)) = 1
        _Smoothness ("光滑度", Range(0, 1)) = 0.75
        _SpecularStrength ("高光强度", Range(0, 10)) = 2
        [Toggle] _UseCameraCenterHighlight ("启用镜头中心高光", Float) = 1
        _CameraHighlightRadius ("镜头中心高光范围", Range(0.05, 1.5)) = 0.75
        _CameraHighlightSoftness ("镜头中心高光柔和", Range(0, 1)) = 0.7
        _CameraHighlightEdgeStrength ("四周高光保留", Range(0, 1)) = 0.05
        _OverallOpacity ("整体透明度", Range(0, 1)) = 1

        [Header(UV Shore)]
        [Toggle(_UV_SHORE)] _UseUVShore ("使用 UV.X 控制岸边和深浅水", Float) = 0
        _UVShoreWidth ("UV 岸边泡沫宽度", Range(0.001, 0.5)) = 0.05
        _UVShoreSoftness ("UV 岸边泡沫柔化", Range(0, 0.1)) = 0.02
        _UVDepthWidth ("UV 浅水过渡宽度", Range(0.001, 0.5)) = 0.5
        _UVDepthSmoothing ("UV 深浅拉扯软化", Range(0, 0.15)) = 0.03
        _UVDepthSceneBlend ("底模深度辅助圆角", Range(0, 1)) = 0.35

        [Header(UV Edge Transparency)]
        [Toggle(_UV_EDGE_FADE)] _UseUVEdgeFade ("启用 UV.X 边缘渐变透明", Float) = 0
        _UVEdgeFadeWidth ("边缘透明渐变宽度", Range(0.001, 0.5)) = 0.08
        _UVEdgeFadeHardness ("边缘透明软硬", Range(0, 1)) = 0

        [Header(Common Flow)]
        _FlowDirection ("流动方向 (XZ)", Vector) = (1, 0, 0, 0)

        [Header(Vertex Waves)]
        [Toggle(_WAVES_OFF)] _DisableWaves ("关闭顶点波浪", Float) = 0
        _WaveAmplitude ("波浪高度", Range(0, 2)) = 0.05
        _WaveFrequency ("波动频率（世界空间）", Range(0.1, 4)) = 1
        _WaveTravelSpeed ("波浪行进速度", Range(-5, 5)) = 1

        [Header(Surface Normal)]
        [NoScaleOffset][Normal] _NormalMap ("法线贴图", 2D) = "bump" {}
        _NormalStrength ("法线强度", Range(0, 0.2)) = 0.02
        _NormalTilingX ("主法线平铺 X", Range(0.005, 0.5)) = 0.05
        _NormalTilingY ("主法线平铺 Y", Range(0.005, 0.5)) = 0.05
        _FlowSpeed ("主法线流速", Range(-0.5, 0.5)) = 0.1
        _NormalTiling2X ("次法线平铺 X", Range(0.005, 0.5)) = 0.05
        _NormalTiling2Y ("次法线平铺 Y", Range(0.005, 0.5)) = 0.05
        _NormalSpeed2 ("次法线流速", Range(-0.5, 0.5)) = -0.08

        [Header(Slope Flow)]
        [Toggle(_SLOPE_FLOW_OFF)] _DisableSlopeFlow ("关闭坡面流纹", Float) = 0
        [NoScaleOffset] _SlopeFlowTex ("坡面流纹贴图", 2D) = "white" {}
        _SlopeFlowTilingX ("坡面流纹平铺 X", Range(0.1, 20)) = 2
        _SlopeFlowTilingY ("坡面流纹平铺 Y", Range(0.1, 40)) = 10
        _SlopeFlowSpeed ("坡面流纹速度", Range(-3, 3)) = 0.8
        _SlopeFlowNormalDistortion ("法线扰动流纹", Range(0, 5)) = 1
        _SlopeFlowContrast ("坡面流纹对比度", Range(0.1, 8)) = 2
        _SlopeFlowStrength ("坡面流纹强度", Range(0, 2)) = 0.6
        _SlopeFlowStart ("流纹开始坡度 (0平面1垂直)", Range(0, 1)) = 0.03
        _SlopeFlowEnd ("流纹完全显示坡度", Range(0.001, 1)) = 0.35
        _SlopeFlowRangeSoftness ("流纹范围软化", Range(0, 1)) = 0.2

        [Header(Flow Light)]
        [Toggle(_FLOW_LIGHT_OFF)] _DisableFlowLight ("关闭流光", Float) = 0
        _FlowLightColor ("流光颜色", Color) = (1, 1, 1, 1)
        _FlowLightDensity ("流光密度", Range(1, 10)) = 4
        _FlowLightSpeed ("流光速度", Range(-1, 1)) = 0.05
        _FlowLightWidth ("流光实心宽度", Range(0.005, 0.2)) = 0.08
        _FlowLightSoftness ("流光渐变长度", Range(0.001, 1)) = 0.12
        _FlowLightNormalDissolve ("法线溶解流光", Range(0, 5)) = 0.5

        [Header(Caustics)]
        [Toggle(_CAUSTICS_OFF)] _DisableCaustics ("关闭焦散", Float) = 0
        [NoScaleOffset] _CausticsTex ("焦散贴图", 2D) = "white" {}
        _CausticsBrightness ("焦散亮度", Range(0, 2)) = 1
        _CausticsTiling ("第一层焦散平铺 X", Range(0.005, 0.5)) = 0.05
        _CausticsTilingY ("第一层焦散平铺 Y", Range(0.005, 0.5)) = 0.05
        _CausticsSpeed ("焦散流速", Range(-0.3, 0.3)) = 0.1
        _CausticsTiling2 ("第二层焦散平铺 X", Range(0.005, 0.5)) = 0.06
        _CausticsTiling2Y ("第二层焦散平铺 Y", Range(0.005, 0.5)) = 0.06
        _CausticsSpeed2 ("第二层焦散流速", Range(-0.3, 0.3)) = 0.04
        _CausticsDepth ("焦散消失深度", Range(0.1, 8)) = 2

        [Header(Shore Foam)]
        [Toggle(_FOAM_OFF)] _DisableFoam ("关闭泡沫", Float) = 0
        _FoamColor ("泡沫颜色", Color) = (0.95, 0.97, 1, 1)
        [NoScaleOffset] _FoamTex ("岸边扰动贴图", 2D) = "white" {}
        _IntersectionWidth ("穿插边缘宽度", Range(0.01, 1)) = 0.2
        _FoamSolidRatio ("贴边实心比例", Range(0, 1)) = 0.15
        _FoamEdgeHardness ("泡沫边缘硬度", Range(0, 1)) = 0
        _FoamNoiseAmount ("边缘扰动比例", Range(0, 5)) = 0.3
        _FoamTiling ("岸边扰动平铺 (世界XZ)", Vector) = (5, 5, 0, 0)
        _FoamSpeed ("岸边扰动流速", Range(-2, 2)) = 0.5
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
            #pragma shader_feature_local_vertex _WAVES_OFF
            #pragma shader_feature_local_fragment _CAUSTICS_OFF
            #pragma shader_feature_local_fragment _FOAM_OFF
            #pragma shader_feature_local_fragment _SLOPE_FLOW_OFF
            #pragma shader_feature_local_fragment _FLOW_LIGHT_OFF
            #pragma shader_feature_local_fragment _UV_SHORE
            #pragma shader_feature_local_fragment _UV_EDGE_FADE

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            TEXTURE2D(_NormalMap); SAMPLER(sampler_NormalMap);
            TEXTURE2D(_SlopeFlowTex); SAMPLER(sampler_SlopeFlowTex);
            TEXTURE2D(_CausticsTex); SAMPLER(sampler_CausticsTex);
            TEXTURE2D(_FoamTex); SAMPLER(sampler_FoamTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _ShallowColor, _DeepColor, _FoamColor, _FlowDirection;
                float4 _FlowLightColor;
                float _DepthDistance, _DepthPower, _Smoothness;
                float _SpecularStrength;
                float _UseCameraCenterHighlight, _CameraHighlightRadius;
                float _CameraHighlightSoftness, _CameraHighlightEdgeStrength;
                float _OverallOpacity, _FlowSpeed;
                float _UseUVShore, _UVShoreWidth, _UVShoreSoftness;
                float _UVDepthWidth;
                float _UVDepthSmoothing, _UVDepthSceneBlend;
                float _UseUVEdgeFade, _UVEdgeFadeWidth, _UVEdgeFadeHardness;
                float _WaveAmplitude, _WaveFrequency, _WaveTravelSpeed;
                float _DisableWaves, _DisableCaustics, _DisableFoam;
                float _NormalTilingX, _NormalTilingY;
                float _NormalTiling2X, _NormalTiling2Y;
                float _NormalSpeed2, _NormalStrength;
                float _DisableSlopeFlow, _DisableFlowLight;
                float _SlopeFlowTilingX, _SlopeFlowTilingY, _SlopeFlowSpeed;
                float _SlopeFlowNormalDistortion;
                float _SlopeFlowContrast, _SlopeFlowStrength;
                float _SlopeFlowStart, _SlopeFlowEnd, _SlopeFlowRangeSoftness;
                float _FlowLightDensity, _FlowLightSpeed, _FlowLightWidth;
                float _FlowLightSoftness;
                float _FlowLightNormalDissolve;
                float _CausticsTiling, _CausticsTilingY, _CausticsSpeed;
                float _CausticsTiling2, _CausticsTiling2Y, _CausticsSpeed2;
                float _CausticsDepth, _CausticsBrightness;
                float4 _FoamTiling;
                float _FoamSpeed, _IntersectionWidth, _FoamSolidRatio, _FoamEdgeHardness;
                float _FoamNoiseAmount;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 tangentWS : TEXCOORD2;
                float3 bitangentWS : TEXCOORD3;
                float3 flowData : TEXCOORD4; // xy=normalized flow, z=base water Y
                float2 uv : TEXCOORD5;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            float EvaluateUVDepth(float bankDistance, float depthWidth)
            {
                return smoothstep(0.0, depthWidth, bankDistance);
            }

            float FilterUVDepth(
                float bankDistance,
                float depthWidth,
                float smoothing)
            {
                depthWidth = max(0.001, depthWidth);
                float radius = min(max(0.0, smoothing), depthWidth * 0.24);
                if (radius <= 0.00001)
                    return EvaluateUVDepth(bankDistance, depthWidth);

                // Five-point Gaussian-style filtering softens the visible
                // diagonal pull caused by UV interpolation inside long,
                // skewed river triangles. The endpoint remap keeps the bank
                // at zero depth and the river centre at full depth.
                float filtered =
                    EvaluateUVDepth(bankDistance - radius * 2.0, depthWidth)
                    + EvaluateUVDepth(bankDistance - radius, depthWidth) * 4.0
                    + EvaluateUVDepth(bankDistance, depthWidth) * 6.0
                    + EvaluateUVDepth(bankDistance + radius, depthWidth) * 4.0
                    + EvaluateUVDepth(bankDistance + radius * 2.0, depthWidth);
                filtered *= (1.0 / 16.0);

                float low = (
                    EvaluateUVDepth(radius, depthWidth) * 4.0
                    + EvaluateUVDepth(radius * 2.0, depthWidth))
                    * (1.0 / 16.0);
                float high = 1.0 - low;
                return saturate((filtered - low) / max(0.0001, high - low));
            }

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                VertexNormalInputs norm = GetVertexNormalInputs(IN.normalOS, IN.tangentOS);

                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                float2 flowDirection = _FlowDirection.xz;
                flowDirection = dot(flowDirection, flowDirection) > 0.000001
                    ? normalize(flowDirection) : float2(1.0, 0.0);
                OUT.flowData = float3(flowDirection, positionWS.y);

                #if !defined(_WAVES_OFF)
                // Continuous world-space phase avoids repeated UV islands moving
                // every river section up and down in sync.
                float wavePhase = dot(positionWS.xz, flowDirection)
                    * _WaveFrequency
                    - _Time.y * _WaveTravelSpeed;
                positionWS.y += sin(wavePhase) * _WaveAmplitude;
                #endif

                OUT.positionCS = TransformWorldToHClip(positionWS);
                OUT.positionWS = positionWS;
                OUT.uv = IN.uv;
                OUT.normalWS = norm.normalWS;
                OUT.tangentWS = normalize(norm.tangentWS
                    - OUT.normalWS * dot(norm.tangentWS, OUT.normalWS));
                OUT.bitangentWS = normalize(cross(OUT.normalWS, OUT.tangentWS))
                    * IN.tangentOS.w * GetOddNegativeScale();
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);

                float2 worldUV = IN.positionWS.xz;
                float2 flowDir = IN.flowData.xy;
                float2 normalFlow1 = flowDir * (_Time.y * _FlowSpeed);

                // Reconstruct the opaque scene surface in world space. Vertical
                // world-space depth remains stable for a horizontal river in both
                // perspective and orthographic cameras.
                float2 screenUV = GetNormalizedScreenSpaceUV(IN.positionCS);
                float rawSceneDepth = SampleSceneDepth(screenUV);
                // SampleSceneDepth returns 0..1, but ComputeWorldSpacePosition
                // expects OpenGL NDC depth (-1..1) on GLES platforms.
                #if !UNITY_REVERSED_Z
                rawSceneDepth = lerp(
                    UNITY_NEAR_CLIP_VALUE, 1.0, rawSceneDepth);
                #endif
                float3 scenePositionWS = ComputeWorldSpacePosition(
                    screenUV, rawSceneDepth, UNITY_MATRIX_I_VP);
                // Stable depth drives water color; displaced depth is retained for
                // the physical shoreline intersection and foam.
                float colorWaterThickness = max(0.0, IN.flowData.z - scenePositionWS.y);
                float waterThickness = max(0.0, IN.positionWS.y - scenePositionWS.y);
                float sceneDepth01 = saturate(
                    colorWaterThickness / max(0.001, _DepthDistance));
                float depth01 = sceneDepth01;
                #if defined(_UV_SHORE) || defined(_UV_EDGE_FADE)
                float riverU = saturate(IN.uv.x);
                float uvBankDistance = min(riverU, 1.0 - riverU);
                #endif
                #if defined(_UV_SHORE)
                float uvDepth01 = FilterUVDepth(
                    uvBankDistance,
                    _UVDepthWidth,
                    _UVDepthSmoothing);
                // UV.x supplies the stable bank-to-centre layout. A controlled
                // amount of actual bottom depth breaks up the angular contour
                // inherited from skewed mesh triangles without affecting the
                // UV-only edge transparency or shore foam.
                depth01 = lerp(
                    uvDepth01,
                    sceneDepth01,
                    saturate(_UVDepthSceneBlend));
                #endif

                // URP Common.hlsl provides PositivePow specifically to avoid
                // negative-base warnings and platform-dependent NaN results.
                float colorMask = PositivePow(
                    depth01, max(0.001, _DepthPower));
                half3 waterColor = lerp(_ShallowColor.rgb, _DeepColor.rgb, colorMask);
                half waterAlpha = lerp(_ShallowColor.a, _DeepColor.a, colorMask);

                float2 normalUV1 = worldUV
                    * float2(_NormalTilingX, _NormalTilingY)
                    + normalFlow1;
                half3 n1 = UnpackNormal(SAMPLE_TEXTURE2D(
                    _NormalMap, sampler_NormalMap, normalUV1));
                float2 normalFlow2 = flowDir * (_Time.y * _NormalSpeed2);
                half normalInteraction = min(0.1h, _NormalStrength * 0.5h);
                float2 normalUV2 = worldUV
                    * float2(_NormalTiling2X, _NormalTiling2Y)
                    + normalFlow2
                    + float2(0.37, 0.73)
                    + n1.xy * normalInteraction;
                half3 n2 = UnpackNormal(SAMPLE_TEXTURE2D(
                    _NormalMap, sampler_NormalMap, normalUV2));
                // Reoriented normal blending lets both layers affect the final
                // surface direction instead of merely adding their XY values.
                half3 rnmBase = n1 + half3(0.0h, 0.0h, 1.0h);
                half3 rnmDetail = n2 * half3(-1.0h, -1.0h, 1.0h);
                half3 normalTS = normalize(
                    rnmBase * dot(rnmBase, rnmDetail)
                    - rnmDetail * rnmBase.z);
                half3x3 TBN = half3x3(IN.tangentWS, IN.bitangentWS, IN.normalWS);
                half3 detailNormalWS = mul(normalTS, TBN);
                half3 normalWS = normalize(lerp(
                    IN.normalWS, detailNormalWS, _NormalStrength));

                half slopeFlowMask = 0.0h;
                #if !defined(_SLOPE_FLOW_OFF)
                // The river generator writes a smoothed surface normal for each
                // section. Using the interpolated base normal keeps the slope
                // mask continuous instead of exposing individual triangles.
                half slope01 = 1.0h - abs(normalize(IN.normalWS).y);
                half slopeSoftStart = max(
                    0.0h, _SlopeFlowStart - _SlopeFlowRangeSoftness * 0.5h);
                half slopeSoftEnd = min(
                    1.0h, _SlopeFlowEnd + _SlopeFlowRangeSoftness * 0.5h);
                half slopeMask = smoothstep(
                    slopeSoftStart,
                    max(slopeSoftStart + 0.001h, slopeSoftEnd),
                    slope01);

                float2 slopeFlowUV = IN.uv
                    * float2(_SlopeFlowTilingX, _SlopeFlowTilingY);
                // RNM-combined two-layer normal bends the streak shape while
                // the streak's own time offset keeps its travel speed separate.
                slopeFlowUV += normalTS.xy
                    * (_NormalStrength * _SlopeFlowNormalDistortion);
                slopeFlowUV.y -= _Time.y * _SlopeFlowSpeed;
                half slopeFlowSample = SAMPLE_TEXTURE2D(
                    _SlopeFlowTex, sampler_SlopeFlowTex, slopeFlowUV).r;
                slopeFlowSample = saturate(
                    (slopeFlowSample - 0.5h) * _SlopeFlowContrast + 0.5h);
                slopeFlowMask = saturate(
                    slopeMask * slopeFlowSample * _SlopeFlowStrength);
                #endif

                half3 caustics = 0.0h;
                #if !defined(_CAUSTICS_OFF)
                float2 causticsFlowOffset = flowDir * (_Time.y * _CausticsSpeed);
                float2 causticsBaseUV = worldUV
                    * float2(_CausticsTiling, _CausticsTilingY);
                float2 causticUV1 = causticsBaseUV + causticsFlowOffset;
                float2 causticUV2 = worldUV
                    * float2(_CausticsTiling2, _CausticsTiling2Y)
                    + flowDir * (_Time.y * _CausticsSpeed2)
                    + float2(3.17, 7.31);
                half3 caustic1 = SAMPLE_TEXTURE2D(
                    _CausticsTex, sampler_CausticsTex, causticUV1).rgb;
                half3 caustic2 = SAMPLE_TEXTURE2D(
                    _CausticsTex, sampler_CausticsTex, causticUV2).rgb;
                half3 causticSample = min(caustic1, caustic2);
                #if defined(_UV_SHORE)
                half causticsDepthMask = 1.0h - smoothstep(
                    0.0h, 1.0h, depth01);
                #else
                half causticsDepthMask = 1.0h - smoothstep(
                    0.0, _CausticsDepth, colorWaterThickness);
                #endif
                caustics = causticSample * causticsDepthMask * _CausticsBrightness;
                #endif

                half foamMask = 0.0h;
                #if !defined(_FOAM_OFF)
                float2 foamFlowOffset = flowDir * (_Time.y * _FoamSpeed);
                float2 foamBaseUV = worldUV * _FoamTiling.xy;
                float2 edgeNoiseUV = foamBaseUV * 0.2;
                half edgeNoise1 = SAMPLE_TEXTURE2D(_FoamTex, sampler_FoamTex,
                    edgeNoiseUV + foamFlowOffset * 0.15).r;
                half edgeNoise2 = SAMPLE_TEXTURE2D(_FoamTex, sampler_FoamTex,
                    edgeNoiseUV * 0.73 + foamFlowOffset * 0.11 + float2(7.13, 3.71)).r;

                #if defined(_UV_SHORE)
                waterThickness = uvBankDistance;
                float foamWidth = _UVShoreWidth;
                float foamSoftness = _UVShoreSoftness;
                #else
                float foamWidth = _IntersectionWidth;
                float foamSoftness = 0.0;
                #endif
                float edgeNoise = (edgeNoise1 - edgeNoise2)
                    * (foamWidth * _FoamNoiseAmount);
                float foamSolidWidth = foamWidth * _FoamSolidRatio;
                float foamEdgeEnd = max(
                    foamSolidWidth + 0.001, foamWidth);
                float noisyEdgeEnd = max(
                    foamSolidWidth + 0.001, foamEdgeEnd + edgeNoise);
                float softenedFoamStart = max(
                    0.0, foamSolidWidth - foamSoftness);
                float softenedFoamEnd = max(
                    softenedFoamStart + 0.001,
                    noisyEdgeEnd + foamSoftness);
                half edgeMask = 1.0h - smoothstep(
                    softenedFoamStart,
                    softenedFoamEnd,
                    waterThickness);
                half edgeContrast = lerp(1.0h, 16.0h, _FoamEdgeHardness);
                edgeMask = saturate(
                    (edgeMask - 0.5h) * edgeContrast + 0.5h);
                foamMask = saturate(edgeMask);
                #endif

                Light mainLight = GetMainLight();
                half NdotL = saturate(dot(normalWS, mainLight.direction));
                half3 diffuse = waterColor * (SampleSH(normalWS)
                    + mainLight.color * NdotL);

                // Orthographic cameras have parallel view rays, so a normal
                // perspective distance falloff cannot create a stable bright
                // centre. Use an aspect-correct screen-space radial mask that
                // follows the camera framing in both projection modes.
                float2 cameraCenterOffset = screenUV - float2(0.5, 0.5);
                cameraCenterOffset.x *= _ScreenParams.x
                    / max(1.0, _ScreenParams.y);
                half cameraCenterDistance = saturate(
                    length(cameraCenterOffset)
                    / max(0.001, _CameraHighlightRadius));
                half cameraFalloffPower = lerp(
                    4.0h,
                    0.5h,
                    (half)_CameraHighlightSoftness);
                half cameraHighlightMask = PositivePow(
                    saturate(1.0h - cameraCenterDistance),
                    cameraFalloffPower);
                cameraHighlightMask = lerp(
                    (half)_CameraHighlightEdgeStrength,
                    1.0h,
                    cameraHighlightMask);
                cameraHighlightMask = lerp(
                    1.0h,
                    cameraHighlightMask,
                    saturate((half)_UseCameraCenterHighlight));
                half localSmoothness =
                    (half)_Smoothness * cameraHighlightMask;

                half3 viewDirectionWS = SafeNormalize(
                    GetWorldSpaceViewDir(IN.positionWS));
                half3 halfDirectionWS = SafeNormalize(
                    mainLight.direction + viewDirectionWS);
                half specularPower = exp2(lerp(
                    2.0h, 7.0h, localSmoothness));
                half specularTerm = PositivePow(
                    saturate(dot(normalWS, halfDirectionWS)),
                    specularPower);
                // Keep an art-directed minimum highlight around the camera
                // centre, even when the directional-light reflection angle is
                // weak in a top-down orthographic view.
                specularTerm = max(
                    specularTerm,
                    cameraHighlightMask * 0.2h);
                half fresnel = PositivePow(
                    1.0h - saturate(dot(normalWS, viewDirectionWS)),
                    5.0h);
                half specularStrength = lerp(
                    0.5h, 2.5h, fresnel)
                    * localSmoothness
                    * (half)_SpecularStrength;
                half3 specular = mainLight.color
                    * specularTerm * specularStrength;

                // The generated river ribbon stores width in UV.x (0..1) and
                // continuous spline progress in UV.y (0..1). Moving along UV.y
                // follows bends in the river and still spans the complete width.
                half flowLightMask = 0.0h;
                #if !defined(_FLOW_LIGHT_OFF)
                float flowLightPhase = IN.uv.y * _FlowLightDensity
                    - _Time.y * _FlowLightSpeed;
                half flowLightDistance = abs(
                    frac(flowLightPhase) - 0.5h) * 2.0h;
                half flowLightWidth = clamp(
                    _FlowLightWidth * 0.5h, 0.0025h, 0.4h);
                half flowLightEdge = min(
                    0.99h,
                    flowLightWidth + max(0.001h, _FlowLightSoftness * 2.0h));
                half flowLightEnvelope = 1.0h - smoothstep(
                    flowLightWidth, flowLightEdge, flowLightDistance);
                // Measure the final visible normal after _NormalStrength has
                // been applied, so the flow light follows the same surface.
                half normalSlope = saturate(
                    length(normalWS - normalize(IN.normalWS)) * 20.0h);
                half normalFlowResponse = saturate(
                    1.0h - normalSlope * _FlowLightNormalDissolve);
                flowLightMask = flowLightEnvelope * normalFlowResponse;
                #endif

                half3 color = diffuse + specular + caustics;
                color += color * _FlowLightColor.rgb * flowLightMask;
                half foamBlend = foamMask * _FoamColor.a;
                color = lerp(color, _FoamColor.rgb, foamBlend);
                // Slope streaks are the final surface detail, so they remain
                // visible over water, caustics, flow light and shore foam.
                color += slopeFlowMask.xxx;
                half finalAlpha = lerp(waterAlpha, 1.0h, foamBlend);
                #if defined(_UV_EDGE_FADE)
                half softEdgeAlpha = smoothstep(
                    0.0h, max(0.001h, _UVEdgeFadeWidth), uvBankDistance);
                half hardEdgeAlpha = step(_UVEdgeFadeWidth, uvBankDistance);
                half edgeAlpha = lerp(
                    softEdgeAlpha, hardEdgeAlpha, _UVEdgeFadeHardness);
                finalAlpha *= edgeAlpha;
                #endif
                finalAlpha *= (half)_OverallOpacity;
                return half4(color, saturate(finalAlpha));
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Simple Lit"
}
