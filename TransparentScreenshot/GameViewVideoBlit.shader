Shader "Hidden/ZXX/GameViewVideoBlit"
{
    Properties { _MainTex ("Frame", 2D) = "white" {} _Overlay ("UI", 2D) = "black" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex, _Overlay;
            float4 _OverlayRect;
            float _UseOverlay;
            float4 frag(v2f_img i) : SV_Target
            {
                float4 frame = tex2D(_MainTex, i.uv);
                float2 uv = (i.uv - _OverlayRect.xy) / _OverlayRect.zw;
                if (_UseOverlay > 0.5 && all(uv >= 0) && all(uv <= 1))
                {
                    float4 ui = tex2D(_Overlay, uv);
                    frame.rgb = lerp(frame.rgb, ui.rgb, ui.a);
                }
                return float4(frame.rgb, 1);
            }
            ENDCG
        }
    }
}
