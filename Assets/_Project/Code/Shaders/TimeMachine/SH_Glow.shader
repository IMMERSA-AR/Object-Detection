Shader "Custom/Time Machine/SH_Glow_Rewritten"
{
    Properties
    {
        _GlowColor ("Glow Color", Color) = (0.5, 0.0, 1.0, 1.0)
        _Intensity ("Glow Intensity", Range(0, 10)) = 1.5
        _Softness ("Softness", Range(0.1, 5.0)) = 1.5
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Cull Off
            ZWrite Off
            Blend One One

            HLSLPROGRAM

            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _GlowColor;
                float _Intensity;
                float _Softness;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = IN.uv;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 centeredUV = IN.uv - 0.5;
                float r = length(centeredUV);

                float glowMask = saturate(1.0 - (r * 2.0)); // multiply distance by 2 so the fade hits pure black exactly at the quad's edges

                glowMask = pow(glowMask, _Softness); // bend the gradient curve to make it look softer and more natural

                float pulse = 1.0 + sin(_Time.y * 3.0) * 0.2; // fluctuating pulse
                float3 color = _GlowColor.rgb * glowMask * _Intensity * pulse;

                return float4(color, 1.0);
            }
            ENDHLSL
        }
    }
}
