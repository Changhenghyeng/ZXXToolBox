Shader "Hidden/ShadowBaker/Silhouette"
{
    Properties
    {
        _LightDir   ("Light Direction (world)", Vector) = (0.3, -1, 0.2, 0)
        _GroundY    ("Ground Y", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "Silhouette"
            // Minimal silhouette pass: oblique-project verts onto the ground, output
            // R = 1 for "this pixel is in shadow". No blending tricks, no per-pixel
            // height — the soft-edge falloff is computed downstream from a distance
            // field, which is far more robust.
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            CBUFFER_START(UnityPerMaterial)
            CBUFFER_END

            // NOT inside UnityPerMaterial: these are per-bake parameters set at runtime
            // via Material.SetVector/SetFloat from ShadowBakerCore. Putting them inside
            // UnityPerMaterial makes SRP Batcher reset them to the shader's default values
            // every frame, so the shader would always project using the hard-coded
            // (0.3, -1, 0.2) regardless of the user's actual light direction — making the
            // baked shadow shape inconsistent with Core's paddedSize/camera coverage.
            float4 _LightDir;
            float  _GroundY;

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings   { float4 positionCS : SV_POSITION; };

            Varyings vert (Attributes IN)
            {
                float3 wp = mul(unity_ObjectToWorld, float4(IN.positionOS.xyz, 1.0)).xyz;
                float ldy = _LightDir.y;
                if (abs(ldy) < 1e-4) ldy = -1.0;
                float t = (_GroundY - wp.y) / ldy;
                wp = wp + _LightDir.xyz * t;
                wp.y = _GroundY;

                Varyings OUT;
                OUT.positionCS = mul(UNITY_MATRIX_VP, float4(wp, 1.0));
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                return half4(1, 1, 1, 1);
            }
            ENDHLSL
        }
    }
}
