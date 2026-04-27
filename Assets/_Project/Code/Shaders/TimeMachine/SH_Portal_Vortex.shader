Shader "Custom/SH_Portal_Vortex"
{
    Properties
    {
        [HDR] _MainColor ("Main Color", Color) = (0, 0.8, 1, 1)
        [HDR] _SecondaryColor ("Secondary Color", Color) = (0.5, 0, 1, 1)
        
        // NEW: The noise texture and its strength
        [NoScaleOffset] _NoiseTex ("Noise Texture", 2D) = "white" {}
        _NoiseStrength ("Noise Distortion", Range(0, 2)) = 0.5
        
        _Speed ("Rotation Speed", Float) = 3
        _SwirlStrength ("Swirl Strength", Float) = 2
        _Intensity ("Emission Intensity", Float) = 5
        _CoreSize ("Core Size", Range(0.0, 0.5)) = 0.05
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }
        Blend One One
        ZWrite Off 
        Cull Off   
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            float4 _MainColor;
            float4 _SecondaryColor;
            
            // Declare our new noise variables
            sampler2D _NoiseTex;
            float _NoiseStrength;
            
            float _Speed;
            float _SwirlStrength;
            float _Intensity;
            float _CoreSize;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 uv = i.uv - 0.5;
                float r = length(uv);

                // NEW: Sample the noise texture. 
                // We pan the UVs over time so the smoke constantly shifts and boils.
                // i.uv * 2.0 scales the noise, _Time.y * 0.2 makes it move diagonally.
                float noise = tex2D(_NoiseTex, i.uv * 2.0 + _Time.y * 0.2).r;

                float angle = atan2(uv.y, uv.x);
                
                // NEW: Add the noise to the angle! This breaks the "perfect" math
                // and causes the spiral to warp and wobble like plasma.
                angle += (noise - 0.5) * _NoiseStrength;
                
                angle += _Time.y * _Speed + (_SwirlStrength / (r + 0.1));

                // Calculate energy ribbons
                float ribbons = sin(angle * 5.0 - _Time.y * 10.0 + r * 20.0);
                float ribbons2 = cos(angle * 3.0 - _Time.y * 5.0 + r * 10.0);
                
                // NEW: Multiply the energy flow by the noise to create thick and thin patches
                float energyFlow = saturate((ribbons + ribbons2) * 0.5 + 0.5);
                energyFlow *= noise * 1.5; // Boosted slightly so it doesn't get too dark

                // Create the Portal Masks
                float coreMask = smoothstep(_CoreSize, _CoreSize + 0.15, r); 
                
                // Tighter edge mask so it doesn't bleed over your 3D model
                float edgeMask = 1.0 - smoothstep(0.42, 0.48, r);
                
                float finalMask = coreMask * edgeMask;

                // Apply Colors
                float3 col = lerp(_MainColor.rgb, _SecondaryColor.rgb, energyFlow);

                // Final Output
                col *= finalMask * energyFlow * _Intensity;

                // To ensure absolute black (transparent in Additive mode) between the ribbons, 
                // we apply one last power function to increase contrast.
                col = pow(col, 1.2);

                return float4(col, 1);
            }
            ENDCG
        }
    }
}