Shader "Custom/Spine Skeleton Line Clip" {
	Properties {
        [Header(Clip Baseline)]
        _ClipBottomY ("Bottom Offset From Pivot (World Units)", Float) = 0
        [Header(Clip Line 1)]
        [Toggle] _Line1Enabled ("Line 1 Enabled", Float) = 0
        _Line1PositionY ("Line 1 Height From Bottom (World Units)", Float) = 0
        _Line1Angle ("Line 1 Angle (Degrees)", Range(-89,89)) = 0
        [Header(Clip Line 2)]
        [Toggle] _Line2Enabled ("Line 2 Enabled", Float) = 0
        _Line2PositionY ("Line 2 Height From Bottom (World Units)", Float) = 0
        _Line2Angle ("Line 2 Angle (Degrees)", Range(-89,89)) = 0
		_Cutoff ("Shadow alpha cutoff", Range(0,1)) = 0.1
        _VisibleAlphaCutoff ("Texture Alpha Cutoff (Includes Baked Shadow)", Range(0,1)) = 0
		[NoScaleOffset] _MainTex ("Main Texture", 2D) = "black" {}
		[Toggle(_STRAIGHT_ALPHA_INPUT)] _StraightAlphaInput("Straight Alpha Texture", Int) = 0
		[HideInInspector] _StencilRef("Stencil Reference", Float) = 1.0
		[HideInInspector][Enum(UnityEngine.Rendering.CompareFunction)] _StencilComp("Stencil Comparison", Float) = 8 // Set to Always as default

		// Outline properties are drawn via custom editor.
		[HideInInspector] _OutlineWidth("Outline Width", Range(0,8)) = 3.0
		[HideInInspector][MaterialToggle(_USE_SCREENSPACE_OUTLINE_WIDTH)] _UseScreenSpaceOutlineWidth("Width in Screen Space", Float) = 0
		[HideInInspector] _OutlineColor("Outline Color", Color) = (1,1,0,1)
		[HideInInspector][MaterialToggle(_OUTLINE_FILL_INSIDE)]_Fill("Fill", Float) = 0
		[HideInInspector] _OutlineReferenceTexWidth("Reference Texture Width", Int) = 1024
		[HideInInspector] _ThresholdEnd("Outline Threshold", Range(0,1)) = 0.25
		[HideInInspector] _OutlineSmoothness("Outline Smoothness", Range(0,1)) = 1.0
		[HideInInspector][MaterialToggle(_USE8NEIGHBOURHOOD_ON)] _Use8Neighbourhood("Sample 8 Neighbours", Float) = 1
		[HideInInspector] _OutlineOpaqueAlpha("Opaque Alpha", Range(0,1)) = 1.0
		[HideInInspector] _OutlineMipLevel("Outline Mip Level", Range(0,3)) = 0
	}

	SubShader {
        CGINCLUDE
        #include "UnityCG.cginc"
        float _VisibleAlphaCutoff;
        float _ClipBottomY;
        float _Line1Enabled;
        float _Line1PositionY;
        float _Line1Angle;
        float _Line2Enabled;
        float _Line2PositionY;
        float _Line2Angle;

        // World-unit distances in the skeleton plane, relative to this renderer's pivot.
        // No atlas UVs or animated bounds are used, so the lines remain stable during animation.
        float2 GetClipPosition(float4 vertexOS) {
            float3 offsetWS = mul((float3x3)unity_ObjectToWorld, vertexOS.xyz);
            float3 rightWS = normalize(mul((float3x3)unity_ObjectToWorld, float3(1,0,0)));
            float3 rawUpWS = mul((float3x3)unity_ObjectToWorld, float3(0,1,0));
            float3 upWS = normalize(rawUpWS - rightWS * dot(rawUpWS, rightWS));
            return float2(dot(offsetWS, rightWS), dot(offsetWS, upWS));
        }

        void ApplyLineClip(float2 position) {
            if (_Line1Enabled > 0.5) {
                float angle = radians(_Line1Angle);
                float2 normal = float2(-sin(angle), cos(angle));
                clip(dot(position, normal) - (_ClipBottomY + _Line1PositionY));
            }
            if (_Line2Enabled > 0.5) {
                float angle = radians(_Line2Angle);
                float2 normal = float2(-sin(angle), cos(angle));
                clip(dot(position, normal) - (_ClipBottomY + _Line2PositionY));
            }
        }
        ENDCG

		Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" "DisableBatching"="True" }

		Fog { Mode Off }
		Cull Off
		ZWrite Off
		Blend One OneMinusSrcAlpha
		Lighting Off

		Stencil {
			Ref[_StencilRef]
			Comp[_StencilComp]
			Pass Keep
		}

		Pass {
			Name "Normal"

			CGPROGRAM
			#pragma shader_feature _ _STRAIGHT_ALPHA_INPUT
			#pragma vertex vert
			#pragma fragment frag
			#include "UnityCG.cginc"
			#include "Assets/Plugins/Spine/spine-unity/Runtime/spine-unity/Shaders/CGIncludes/Spine-Common.cginc"
			sampler2D _MainTex;

			struct VertexInput {
				float4 vertex : POSITION;
				float2 uv : TEXCOORD0;
				float4 vertexColor : COLOR;
			};

			struct VertexOutput {
				float4 pos : SV_POSITION;
                float2 clipPosition : TEXCOORD2;
				float2 uv : TEXCOORD0;
				float4 vertexColor : COLOR;
			};

			VertexOutput vert (VertexInput v) {
				VertexOutput o;
				o.pos = UnityObjectToClipPos(v.vertex);
                o.clipPosition = GetClipPosition(v.vertex);
				o.uv = v.uv;
				o.vertexColor = PMAGammaToTargetSpace(v.vertexColor);
				return o;
			}

			float4 frag (VertexOutput i) : SV_Target {
                ApplyLineClip(i.clipPosition);
				float4 texColor = tex2D(_MainTex, i.uv);
                // Test texture alpha before vertex tint/fades, so animation opacity
                // does not change which baked shadow pixels pass the threshold.
                if (_VisibleAlphaCutoff > 0.0)
                    clip(texColor.a - _VisibleAlphaCutoff);

				#if defined(_STRAIGHT_ALPHA_INPUT)
				texColor.rgb *= texColor.a;
				#endif

				return (texColor * i.vertexColor);
			}
			ENDCG
		}

		Pass {
			Name "Caster"
			Tags { "LightMode"="ShadowCaster" }
			Offset 1, 1
			ZWrite On
			ZTest LEqual

			Fog { Mode Off }
			Cull Off
			Lighting Off

			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#pragma multi_compile_shadowcaster
			#pragma fragmentoption ARB_precision_hint_fastest
			#include "UnityCG.cginc"
			sampler2D _MainTex;
			fixed _Cutoff;

			struct VertexOutput {
				V2F_SHADOW_CASTER;
				float4 uvAndAlpha : TEXCOORD1;
                float2 clipPosition : TEXCOORD2;
			};

			VertexOutput vert (appdata_base v, float4 vertexColor : COLOR) {
				VertexOutput o;
				o.uvAndAlpha = v.texcoord;
                o.clipPosition = GetClipPosition(v.vertex);
				o.uvAndAlpha.a = vertexColor.a;
				TRANSFER_SHADOW_CASTER(o)
				return o;
			}

			float4 frag (VertexOutput i) : SV_Target {
                ApplyLineClip(i.clipPosition);
				fixed4 texcol = tex2D(_MainTex, i.uvAndAlpha.xy);
				clip(texcol.a * i.uvAndAlpha.a - _Cutoff);
				SHADOW_CASTER_FRAGMENT(i)
			}
			ENDCG
		}
	}

}
