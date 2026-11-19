// 성운 (background effect): slow clouds of colored gas in the cover's two colors, folding into
// each other, with a scatter of faint stars that twinkle. Value noise, three octaves, warped
// once - a little more than the aurora's cost, still a small shader.
//
// Same inputs as Aurora.hlsl. Recompile after editing:
//   dotnet run --project tools/ShaderCompiler -- src/TuneSync.App/Effects/Nebula.hlsl src/TuneSync.App/Effects/Nebula.ps

sampler2D input : register(s0);

float time : register(c0);
float4 color1 : register(c1);
float4 color2 : register(c2);
float aspect : register(c3);
float intensity : register(c4);
float2 offset : register(c5);
float2 span : register(c6);

float hash(float2 p)
{
    return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
}

float noise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    f = f * f * (3.0 - 2.0 * f);
    return lerp(lerp(hash(i), hash(i + float2(1, 0)), f.x), lerp(hash(i + float2(0, 1)), hash(i + float2(1, 1)), f.x), f.y);
}

float fbm(float2 p)
{
    float v = 0.0;
    float a = 0.5;
    [unroll] for (int o = 0; o < 3; o++)
    {
        v += a * noise(p);
        p = p * 2.03 + 11.7;
        a *= 0.5;
    }
    return v;
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 P = span.y > 0.0 ? offset + uv * span : float2(uv.x * aspect, uv.y);
    float t = time;

    float2 q = P * 1.6 + float2(t * 0.012, -t * 0.008);
    float warp = fbm(q + t * 0.01);
    float gas1 = fbm(q + warp * 1.6);
    float gas2 = fbm(q * 1.3 - warp + 5.2 - t * 0.006);
    float3 light = color1.rgb * pow(saturate(gas1 * 1.25 - 0.2), 2.2) * 1.0
                 + color2.rgb * pow(saturate(gas2 * 1.25 - 0.24), 2.4) * 0.85;

    // stars: one in a few dozen cells, a few pixels across, twinkling slowly
    float2 g = P * 38.0;
    float2 cell = floor(g);
    float h = hash(cell);
    float2 at = frac(g) - 0.5 - (float2(hash(cell + 1.3), hash(cell + 2.7)) - 0.5) * 0.6;
    float star = step(0.972, h) * smoothstep(0.16, 0.0, length(at)) * (0.55 + 0.45 * sin(t * (0.6 + h * 2.0) + h * 40.0));
    light += (0.6 + 0.4 * lerp(color1.rgb, float3(1, 1, 1), 0.7)) * star * 0.9;

    float fall = saturate(1.25 - uv.y * 1.05);
    fall = 0.3 + 0.7 * fall;

    float level = max(max(light.r, light.g), light.b);
    float alpha = saturate(level) * intensity * fall;
    float3 hue = light / max(level, 0.001);
    float4 back = tex2D(input, uv);
    return float4(hue * alpha + back.rgb * (1.0 - alpha), alpha + back.a * (1.0 - alpha));
}
