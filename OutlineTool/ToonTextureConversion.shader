Shader "Hidden/ZXXTools/ToonTextureConversion"
{
    Properties
    {
        _BaseTex ("Base", 2D) = "white" {}
        _PackedTex ("Packed N_M_R", 2D) = "white" {}
    }

    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        CGINCLUDE
        #include "UnityCG.cginc"
        #include "UnityStandardUtils.cginc"

        sampler2D _BaseTex;
        sampler2D _PackedTex;

        struct Attributes
        {
            float4 vertex : POSITION;
            float2 uv : TEXCOORD0;
        };

        struct Varyings
        {
            float4 vertex : SV_POSITION;
            float2 uv : TEXCOORD0;
        };

        Varyings Vert(Attributes input)
        {
            Varyings output;
            output.vertex = UnityObjectToClipPos(input.vertex);
            output.uv = input.uv;
            return output;
        }
        ENDCG

        Pass
        {
            Name "CopyBase"
            CGPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            fixed4 Frag(Varyings input) : SV_Target
            {
                return tex2D(_BaseTex, input.uv);
            }
            ENDCG
        }

        Pass
        {
            Name "UnpackNormal"
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag

            fixed4 Frag(Varyings input) : SV_Target
            {
                half2 normalXY = tex2D(_PackedTex, input.uv).rg * 2.0h - 1.0h;
                half normalZ = sqrt(saturate(1.0h - dot(normalXY, normalXY)));
                return half4(normalXY * 0.5h + 0.5h, normalZ * 0.5h + 0.5h, 1.0h);
            }
            ENDCG
        }

        Pass
        {
            Name "UnpackMetallicSmoothness"
            CGPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            fixed4 Frag(Varyings input) : SV_Target
            {
                half4 packedNMR = tex2D(_PackedTex, input.uv);
                return half4(packedNMR.b, 0.0h, 0.0h, 1.0h - packedNMR.a);
            }
            ENDCG
        }
    }
}
