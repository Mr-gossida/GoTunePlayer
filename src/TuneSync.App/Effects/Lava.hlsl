// 라바 램프 (background effect): soft blobs of light rising and sinking slowly, merging when they
// meet and pulling apart again, like a lava lamp, in the cover's two colors. Seven metaballs.
//
// Same inputs as Aurora.hlsl. Recompile after editing:
//   dotnet run --project tools/ShaderCompiler -- src/TuneSync.App/Effects/Lava.hlsl src/TuneSync.App/Effects/Lava.ps

sampler2D input : register(s0);

float time : register(c0);
float4 color1 : register(c1);
float4 color2 : register(c2);
float aspect : register(c3);
float intensity : register(c4);
float2 offset : register(c5);
float2 span : register(c6);

static const float Period = 3.0;

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 P = span.y > 0.0 ? offset + uv * span : float2(uv.x * aspect, uv.y);
    float t = time;
    float field = 0.0;
    float warm = 0.0;

    [unroll] for (int k = 0; k < 7; k++)
    {
        float fk = k;
        float h1 = frac(sin(fk * 12.9898) * 43758.5453);
        float h2 = frac(sin(fk * 78.233) * 12345.678);
        // up and down over a minute or so, drifting a little sideways
        float2 c = float2((fk + 0.5) / 7.0 * Period + 0.18 * sin(t * 0.045 + fk * 2.1),
                          0.5 + 0.62 * sin(t * (0.035 + 0.025 * h1) + fk * 1.7));
        float2 d = P - c;
        d.x -= Period * round(d.x / Period);
        d.y *= 0.85; // a little taller than wide
        float r = 0.13 + 0.09 * h2;
        float f = r * r / (dot(d, d) + 0.0004);
        field += f;
        warm += f * h2;
    }

    // a defined edge, like wax in a lamp; brighter toward a blob's heart, a faint glow around
    float blob = smoothstep(0.95, 1.2, field);
    float core = saturate((field - 1.0) * 0.5);
    float glow = saturate(field * 0.18);
    float3 tint = lerp(color1.rgb, color2.rgb, saturate(warm / max(field, 0.001) * 1.6));
    float3 light = tint * (blob * (0.5 + 0.35 * core) + glow * 0.15);

    float fall = saturate(1.25 - uv.y * 1.0);
    fall = 0.35 + 0.65 * fall;

    float level = max(max(light.r, light.g), light.b);
    float alpha = saturate(level) * intensity * fall;
    float3 hue = light / max(level, 0.001);
    float4 back = tex2D(input, uv);
    return float4(hue * alpha + back.rgb * (1.0 - alpha), alpha + back.a * (1.0 - alpha));
}
