Shader "Hidden/VintageEffect"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _SepiaIntensity ("Sepia Intensity", Range(0, 1)) = 1.0
        _VignetteIntensity ("Vignette Intensity", Range(0, 3)) = 1.5
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            sampler2D _MainTex;
            float _SepiaIntensity;
            float _VignetteIntensity;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 col = tex2D(_MainTex, i.uv);

                // 1. Sepia Tone Calculation
                float r = (col.r * 0.393) + (col.g * 0.769) + (col.b * 0.189);
                float g = (col.r * 0.349) + (col.g * 0.686) + (col.b * 0.168);
                float b = (col.r * 0.272) + (col.g * 0.534) + (col.b * 0.131);
                
                fixed4 sepiaCol = fixed4(r, g, b, col.a);
                col = lerp(col, sepiaCol, _SepiaIntensity);

                // 2. Vignette Calculation (darkening the edges)
                float2 dist = i.uv - 0.5f;
                float vignette = 1.0 - dot(dist, dist) * _VignetteIntensity;
                col.rgb *= smoothstep(0.0, 1.0, vignette);

                return col;
            }
            ENDCG
        }
    }
}