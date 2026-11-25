sampler2D scene : register(s0);
sampler2D light : register(s1);
sampler2D transmission : register(s2);

float4 main(float2 uv : TEXCOORD0) : COLOR0
{
    return float4(tex2D(light, uv).rgb + tex2D(scene, uv).rgb * tex2D(transmission, uv).rgb, 1);
}
