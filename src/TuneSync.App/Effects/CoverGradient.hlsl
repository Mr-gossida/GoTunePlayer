// 그라데이션 (background effect): the cover's colors as broad, soft fields that drift and
// flow into each other, like a mesh gradient slowly breathing. No shapes, no edges: six color
// points wander on slow paths and every pixel is the distance-weighted blend of them, the plane
// itself gently warped so the boundaries curve and swirl. A whisper of grain keeps the wide
// gradients from banding.
//
// Same inputs as Aurora.hlsl. Recompile after editing:
//   dotnet run --project tools/ShaderCompiler -- src/TuneSync.App/Effects/CoverGradient.hlsl src/TuneSync.App/Effects/CoverGradient.ps

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
    float wide = span.y > 0.0 ? max(span.x + offset.x, 1.0) : aspect;
    float t = time;

    // a slow warp of the plane: the fields' borders bend and swirl instead of staying round
    P.x += 0.10 * sin(P.y * 2.3 + t * 0.07) + 0.05 * sin(P.y * 4.1 - t * 0.11);
    P.y += 0.10 * cos(P.x * 1.9 - t * 0.06) + 0.05 * cos(P.x * 3.7 + t * 0.09);

    // the six colors: the cover's two, a pale and a deep take on each (no blend of the two:
    // halfway between two hues is often mud - brown between red and yellow)
    float3 deep1 = color1.rgb * 0.55;
    float3 pale2 = lerp(color2.rgb, float3(1, 1, 1), 0.2);
    float3 pale1 = lerp(color1.rgb, float3(1, 1, 1), 0.1);
    float3 deep2 = color2.rgb * 0.55;

    float3 sum = 0;
    float total = 0;
    [unroll] for (int k = 0; k < 6; k++)
    {
        float fk = k;
        float h1 = frac(sin(fk * 12.9898 + 1.3) * 43758.5453);
        float h2 = frac(sin(fk * 78.233 + 0.7) * 12345.678);
        // each point wanders over its own part of the screen on a slow, never-repeating path
        float2 c = float2((0.12 + 0.76 * h1) * wide + 0.30 * sin(t * (0.021 + 0.013 * h2) + fk * 2.4),
                          (0.15 + 0.70 * h2) + 0.28 * cos(t * (0.017 + 0.011 * h1) + fk * 1.9));
        float2 d = P - c;
        float w = 1.0 / pow(dot(d, d) + 0.03, 2.0);
        float3 col = k == 0 ? color1.rgb : k == 1 ? pale2 : k == 2 ? deep1 : k == 3 ? color2.rgb : k == 4 ? deep2 : pale1;
        sum += col * w;
        total += w;
    }
    float3 light = sum / total;

    // a little darker toward the bottom corners, so the song's text there stays easy to read
    float vignette = 1.0 - 0.35 * saturate(length((uv - float2(0.5, 0.35)) * float2(1.0, 1.2)) - 0.3);
    light *= vignette;

    // grain: about one step of 8-bit color, different every pixel, steady in time
    float grain = frac(sin(dot(uv * 1000.0, float2(12.9898, 78.233))) * 43758.5453) - 0.5;
    light += grain / 255.0;

    float alpha = saturate(intensity * 1.15);
    float4 back = tex2D(input, uv);
    return float4(light * alpha + back.rgb * (1.0 - alpha), alpha + back.a * (1.0 - alpha));
}
