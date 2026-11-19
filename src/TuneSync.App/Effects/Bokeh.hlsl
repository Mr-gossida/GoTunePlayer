// 빛망울 (background effect): soft out-of-focus lights - the round glows of a city at night seen
// through a lens - drifting slowly upward and pulsing gently, in the cover's two colors. Twelve
// discs, each a handful of instructions.
//
// Same inputs as Aurora.hlsl. Recompile after editing:
//   dotnet run --project tools/ShaderCompiler -- src/TuneSync.App/Effects/Bokeh.hlsl src/TuneSync.App/Effects/Bokeh.ps

sampler2D input : register(s0);

float time : register(c0);
float4 color1 : register(c1);
float4 color2 : register(c2);
float aspect : register(c3);
float intensity : register(c4);
float2 offset : register(c5);
float2 span : register(c6);

// The lights live on a strip 3 units wide (a screen is about 1.8 wide); it repeats sideways, so
// several monitors side by side each see their own part.
static const float Period = 3.0;

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 P = span.y > 0.0 ? offset + uv * span : float2(uv.x * aspect, uv.y);
    float t = time;
    float3 light = 0;

    [unroll] for (int k = 0; k < 12; k++)
    {
        float fk = k;
        float h1 = frac(sin(fk * 12.9898) * 43758.5453);
        float h2 = frac(sin(fk * 78.233) * 12345.678);
        float h3 = frac(sin(fk * 39.425) * 24634.634);
        // rising slowly (a minute or two across), swaying a little
        float2 c = float2(h1 * Period + 0.08 * sin(t * 0.05 + fk * 1.7), 1.2 - frac(h2 + t * (0.006 + 0.006 * h3)) * 1.4);
        float2 d = P - c;
        d.x -= Period * round(d.x / Period);
        float r = 0.07 + 0.13 * h3;
        float dist = length(d);
        float disc = 1.0 - smoothstep(r * 0.82, r, dist);
        float rim = smoothstep(r * 0.55, r * 0.97, dist) * disc;
        float pulse = 0.6 + 0.4 * sin(t * (0.25 + 0.2 * h1) + fk * 2.3);
        light += lerp(color1.rgb, color2.rgb, h2) * (disc * 0.24 + rim * 0.2) * pulse;
    }

    float fall = saturate(1.25 - uv.y * 1.05);
    fall = 0.35 + 0.65 * fall;

    float level = max(max(light.r, light.g), light.b);
    float alpha = saturate(level) * intensity * fall;
    float3 hue = light / max(level, 0.001);
    float4 back = tex2D(input, uv);
    return float4(hue * alpha + back.rgb * (1.0 - alpha), alpha + back.a * (1.0 - alpha));
}
