Shader "Custom/SH_Glow"
{
    Properties
    {
        _GlowColor ("Glow Color", Color) = (0.5, 0.0, 1.0, 1.0)
        _Intensity ("Glow Intensity", Range(0, 10)) = 1.5
        _Falloff ("Softness", Range(0.1, 5.0)) = 1.5
    }

    SubShader
    {
        // "IgnoreProjector" is good practice for VR transparents
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "IgnoreProjector"="True" }
        
        // Additive Blending (Adds light to the background)
        Blend One One 
        ZWrite Off
        Cull Off

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

            float4 _GlowColor;
            float _Intensity;
            float _Falloff;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                // 1. Center the UV coordinates to (0,0)
                float2 uv = i.uv - 0.5;
                
                // 2. Measure distance from the center
                float dist = length(uv);

                // 3. Create the radial gradient
                // Multiply distance by 2 so the fade hits pure black exactly at the quad's edges
                float glowMask = saturate(1.0 - (dist * 2.0));

                // 4. Bend the gradient curve to make it look softer and more natural
                glowMask = pow(glowMask, _Falloff);

                // 5. Apply color and intensity
                float3 finalColor = _GlowColor.rgb * glowMask * _Intensity;

                return float4(finalColor, 1.0);
            }
            ENDCG
        }
    }
}