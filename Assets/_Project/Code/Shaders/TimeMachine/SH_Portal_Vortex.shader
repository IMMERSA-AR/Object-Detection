Shader "Custom/Time Machine/SH_Portal_Vortex_Rewriten"
{
    Properties
    {
        [HDR] _PrimaryColor("Primary Color", Color) = (1, 1, 1, 1) // HDR for extra brightness and glowing effect
        [HDR] _SecondaryColor("Secondary Color", Color) = (1, 1, 1, 1)
        [MainTexture] _NoiseTexture("Noise Texture", 2D) = "white" {}
        _RotationSpeed("Rotation Speed", Float) = 0.1
        _SwirlStrength ("Swirl Strength", Float) = 2
        _VortexRadius("Vortex Radius", Range(0.0, 0.5)) = 0.42
        _CoreSize("Core Size", Range(0.0, 0.5)) = 0.02
        _NoiseStrength ("Noise Distortion", Range(0, 2)) = 0.5
        _Intensity ("Emission Intensity", Float) = 5.0
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Cull Off
            ZWrite Off // transparent objects should not write into depth buffer
            Blend One One // for color over-saturation for HDR bloom effect

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

            TEXTURE2D(_NoiseTexture);
            SAMPLER(sampler_NoiseTexture);

            CBUFFER_START(UnityPerMaterial)
                half4 _PrimaryColor;
                half4 _SecondaryColor;
                float4 _NoiseTexture_ST;
                float _RotationSpeed;
                float _SwirlStrength;
                float _VortexRadius;
                float _CoreSize;
                float _NoiseStrength;
                float _Intensity;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;

                OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.uv = TRANSFORM_TEX(IN.uv, _NoiseTexture);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // logic is done in fragment shader because quad has only 4 vertices and cannot accommodate for the spiral, so we need to do the spiral logic on each pixel
                // make the noise move irregularly for later spiraling noise effect
                float2 scrollingUV = IN.uv * 2.0 + float2(_Time.y * 0.2, _Time.y * 0.2 * 0.5); // uv*2 for smaller finer noise texture details for a plasma effect
                half4 noiseSample = SAMPLE_TEXTURE2D(_NoiseTexture, sampler_NoiseTexture, scrollingUV);

                // vertex shader had origin at 0,0 as it deals with Model Space, but fragment shader deals with UV Space so we should translate center to origin
                float2 centeredUV = IN.uv - 0.5;

                float r = length(centeredUV); // calculate the length of the radius of each vertex from center
                float theta = atan2(centeredUV.y, centeredUV.x); // calculate the angle of each vertex from x-axis
                theta += (noiseSample.r - 0.5) * _NoiseStrength; // add the noise to the angle to create wobbly effect (subtract -0.5 so that noise pushed both ways -0.5 to 0.5), and multiple by strength factor

                float safeR = r + 0.1; // to prevent division by zero

                // radius gets smaller towards the center due to the hyperbolic function
                // theta + RotationSpeed + Spiral effect
                float spiralTheta = theta + _Time.y * _RotationSpeed + (_SwirlStrength / safeR);
                float wave = sin(spiralTheta * 5.0 - _Time.y * 10.0 + r * 20.0); // wave with 5 spiral arms, higher speed
                float wave2 = cos(spiralTheta * 3.0 - _Time.y * 5.0 + r * 10.0); // wave with 3 spiral arms, lower speed
                float spiralWave = saturate((wave + wave2) * 0.5 + 0.5); // adding both mismatched waves to create a wave interference for plasma effect

                spiralWave *= noiseSample.r * 1.5; // brighten up colors again as noise dims them

                float3 gradientColor = lerp(_PrimaryColor, _SecondaryColor, spiralWave);

                float coreMask = smoothstep(_CoreSize, _CoreSize + 0.15, r); // central core for a black hole vortex look
                float circularEdgeMask = 1.0 - smoothstep(_VortexRadius, _VortexRadius + 0.05, r);// circular shape for vortex by making anything outside vortex radius transparent smoothly

                float finalMask = coreMask * circularEdgeMask;

                gradientColor *= finalMask * spiralWave * _Intensity; // added some emission intensity
                gradientColor = pow(gradientColor, 1.2);

                return float4(gradientColor, 1.0);
            }
            ENDHLSL
        }
    }
}