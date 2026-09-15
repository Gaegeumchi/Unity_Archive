Shader "Hidden/Interaction/URPOutlineGlow"
{
    Properties
    {
        [HDR] _OutlineColor ("Outline Color", Color) = (0.1, 0.8, 1, 1)
        _OutlineWidth ("Outline Width (Pixels)", Range(1, 12)) = 3
        [HideInInspector] _OutlineCenterOS ("Outline Center", Vector) = (0, 0, 0, 0)
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent+10"
            "RenderType" = "Transparent"
        }

        Pass
        {
            Name "InteractionOutline"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            Cull Front
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            CBUFFER_START(UnityPerMaterial)
                half4 _OutlineColor;
                float _OutlineWidth;
                float4 _OutlineCenterOS;
            CBUFFER_END

            Varyings Vert(Attributes input)
            {
                Varyings output;
                float4 positionCS = TransformObjectToHClip(input.positionOS.xyz);
                float4 centerCS = TransformObjectToHClip(_OutlineCenterOS.xyz);

                float2 positionNDC = positionCS.xy / max(positionCS.w, 0.0001);
                float2 centerNDC = centerCS.xy / max(centerCS.w, 0.0001);
                float2 outlineDirection = positionNDC - centerNDC;
                float directionLength = length(outlineDirection);

                if (directionLength > 0.0001)
                    outlineDirection /= directionLength;

                float2 pixelOffset = outlineDirection * (_OutlineWidth * 2.0 / _ScreenParams.xy);
                positionCS.xy += pixelOffset * positionCS.w;
                output.positionCS = positionCS;
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                return _OutlineColor;
            }
            ENDHLSL
        }
    }
}
