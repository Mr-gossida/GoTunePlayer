// Aurora projector light for the screen saver: slow, flowing bands of light like an aurora
// lamp throwing its colors onto a ceiling. Domain-warped sine ridges; brightest at the top,
// fading toward the bottom so the cover and lyrics stay readable.
//
// Compiled to Aurora.ps (ps_3_0), which the app embeds. After editing, recompile:
//   dotnet run --project tools/ShaderCompiler -- src/TuneSync.App/Effects/Aurora.hlsl src/TuneSync.App/Effects/Aurora.ps

sampler2D input : register(s0);

float time : register(c0);      // seconds, animated
float4 color1 : register(c1);   // main light color (from the cover)
float4 color2 : register(c2);   // second light color (hue-shifted)
float aspect : register(c3);    // width / height of the element
float intensity : register(c4); // 0..1 overall strength

// 1 at the crest of each sine wave, falling off fast: thin bright streaks.
float ridge(float v, float sharpness)
{
    return pow(1.0 - abs(sin(v)), sharpness);
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 p = float2(uv.x * aspect, uv.y) * 3.0;
    float t = time;

    // Let the waves push each other around: this is what makes the light "flow".
    [unroll] for (int i = 1; i < 6; i++)
    {
        float fi = i;
        p.x += 0.65 / fi * sin(fi * p.y + t * 0.31 + 0.3 * fi);
        p.y += 0.55 / fi * cos(fi * p.x * 0.8 + t * 0.23 + 0.5 * fi);
    }

    // Wide, soft bands of light over a gentle haze; the fine ripples only faintly. Low ridge
    // sharpness keeps the edges blurred, like light diffused onto a ceiling.
    float bands = ridge(p.x * 1.2 + p.y * 0.35 + t * 0.08, 2.2);
    float bands2 = ridge(p.y * 1.1 - p.x * 0.6 - t * 0.11 + 1.7, 2.2);
    float fine = ridge(p.x * 2.6 + p.y * 1.5 - t * 0.06, 3.0);
    float glow = 0.5 + 0.5 * sin(p.x * 0.5 + p.y * 0.7 + t * 0.17);

    float m1 = bands * 0.7 * (0.6 + 0.4 * glow) + fine * 0.08 + glow * 0.22;
    float m2 = bands2 * 0.65 * (1.0 - 0.4 * glow) + fine * 0.06 + (1.0 - glow) * 0.16;
    float3 light = color1.rgb * m1 + color2.rgb * m2;

    // Brightest at the top, like light thrown onto a ceiling.
    float fall = saturate(1.2 - uv.y * 1.15);
    fall = 0.25 + 0.75 * fall * fall;

    float level = max(max(light.r, light.g), light.b);
    float alpha = saturate(level) * intensity * fall;
    float3 hue = light / max(level, 0.001);
    return float4(hue * alpha, alpha); // premultiplied
}
