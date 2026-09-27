Shader "RVO/Agent Colors"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Pass
        {
            // 不指定 LightMode：内置管线按 Always、URP 按 SRPDefaultUnlit 处理。
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"
            struct Attributes { float4 position : POSITION; float4 color : COLOR; };
            struct Varyings { float4 position : SV_POSITION; float4 color : COLOR; };
            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.position = UnityObjectToClipPos(input.position);
                output.color = input.color;
                return output;
            }
            half4 Frag(Varyings input) : SV_Target { return input.color; }
            ENDHLSL
        }
    }
}
