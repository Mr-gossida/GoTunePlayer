// 물빛 (background effect): light thrown up from rippling water, the bright net of caustics
// that dances on a ceiling over a pool, in the cover's two colors. Four iterations of a warped
// sine field - about the aurora's cost. Brightest at the top, like the aurora.
//
// Same inputs as Aurora.hlsl. Recompile after editing:
//   dotnet run --project tools/ShaderCompiler -- src/TuneSync.App/Effects/Caustics.hlsl src/TuneSync.App/Effects/Caustics.ps

sampler2D input : register(s0);

float time : register(c0);
float4 color1 : register(c1);
float4 color2 : register(c2);
float aspect : register(c3);
float intensity : register(c4);
float2 offset : register(c5);
float2 span : register(c6);

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 P = span.y > 0.0 ? offset + uv * span : float2(uv.x * aspect, uv.y);
    float t = time * 0.22;

    float2 p = P * 3.2 - 250.0;
    float2 i = p;
    float c = 1.0;
    [unroll] for (int n = 0; n < 4; n++)
    {
        float tt = t * (1.0 - 3.5 / (n + 1.0));
        i = p + float2(cos(tt - i.x) + sin(tt + i.y), sin(tt - i.y) + cos(tt + i.x));
        c += 1.0 / length(float2(p.x / (sin(i.x + tt) / 0.005), p.y / (cos(i.y + tt) / 0.005)));
    }
    c /= 4.0;
    c = 1.17 - pow(abs(c), 1.4);
    float net = saturate(pow(abs(c), 6.0) * 1.3);

    // the two colors drift across the picture; a faint wash under the bright lines
    float mixer = 0.5 + 0.5 * sin(P.x * 0.8 + P.y * 0.5 + time * 0.07);
    float3 tint = lerp(color1.rgb, color2.rgb, mixer);
    float3 light = tint * (net * 1.05 + 0.05);

    float fall = saturate(1.2 - uv.y * 1.15);
    fall = 0.25 + 0.75 * fall * fall;

    float level = max(max(light.r, light.g), light.b);
    float alpha = saturate(level) * intensity * fall;
    float3 hue = light / max(level, 0.001);
    float4 back = tex2D(input, uv);
    return float4(hue * alpha + back.rgb * (1.0 - alpha), alpha + back.a * (1.0 - alpha));
}
