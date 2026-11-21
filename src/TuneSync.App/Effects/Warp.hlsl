// 워프 (background effect): flying forward through a field of stars, as from a spaceship -
// stars stream out of the middle of the screen, faint and slow at first, faster, brighter and
// longer as they come near and leave past the edges. A faint glow of the cover's color marks
// where they come from; the stars are white, lightly tinted with the cover's two colors.
//
// Each star is a streak along its path, from where it was a moment ago to where it is now:
// the light moves 15 steps a second, and the streak covers more than one step's travel, so the
// flight reads as smooth rather than as dots jumping outward.
//
// Every screen flies toward its own middle (one vanishing point across several monitors would
// sit on the seam between two of them).
//
// Same inputs as Aurora.hlsl. Recompile after editing:
//   dotnet run --project tools/ShaderCompiler -- src/TuneSync.App/Effects/Warp.hlsl src/TuneSync.App/Effects/Warp.ps

sampler2D input : register(s0);

float time : register(c0);
float4 color1 : register(c1);
float4 color2 : register(c2);
float aspect : register(c3);
float intensity : register(c4);
float2 offset : register(c5);
float2 span : register(c6);

static const float Tau = 6.2831853;
// how far out (in screen heights) a star is when it appears, at depth 1
static const float Near = 0.02;
// how far beyond that a star may first show (screen heights; the corners are ~0.9 out)
static const float Spread = 0.6;
// how long a moment the streak covers, in seconds (more than the light's 1/15 s step)
static const float Trail = 0.16;

float Hash(float n) { return frac(sin(n * 12.9898 + 4.1414) * 43758.5453); }

// One depth of stars: the circle around the middle cut into `slices` thin wedges, two stars in
// each, flying outward at their own pace. Returns light (rgb) for this pixel.
float3 StarLayer(float r, float u, float t, float slices, float speed, float seed)
{
    float3 light = 0;
    float cellF = u * slices;
    float cell = floor(cellF);
    float within = frac(cellF);

    [unroll] for (int k = 0; k < 2; k++)
    {
        float id = cell * 1.37 + seed + k * 57.0;
        float h1 = Hash(id);
        float h2 = Hash(id + 11.0);
        float h3 = Hash(id + 23.0);
        float h4 = Hash(id + 41.0);
        float h5 = Hash(id + 67.0);

        // where in the wedge it flies (kept off the wedge's edges, where its light would be cut)
        float place = 0.25 + 0.5 * h1;
        float pace = speed * (0.7 + 0.6 * h3);
        // depth: 1 far away, near 0 as it passes; wraps around to far again
        float z = frac(h2 + k * 0.5 - t * pace);
        z = max(z, 0.015);
        // Where it first shows, as seen from here: anywhere on the screen, as a real star field
        // projects (all starting at one small radius, they crowded into a ring round the
        // middle). Spread out, but leaning a little toward the middle (h5^0.75: between even
        // over the area and even over the distance), so the middle doesn't empty out.
        float start = Near + Spread * pow(h5, 0.75);
        float head = start / z;
        float tail = start / (z + pace * Trail);

        // How far along its trip across the screen it is: 0 where it shows, 1 as it leaves past
        // the corner (about 1 screen height out). A star that starts far out leaves sooner, so
        // brightness and width follow this, not the depth alone - by depth, those stars were
        // still dim when they left.
        float leave = start / 1.0;
        float trip = saturate((1.0 - z) / max(1.0 - leave, 0.05));

        // across the streak: the distance from its path
        float across = r * abs(within - place) * Tau / slices;
        // (was 0.0006 + 0.0022: near stars were ~10 px wide bands at 1080p; now ~3-4 px)
        float width = 0.0004 + 0.0008 * trip;
        float thin = 1.0 - smoothstep(width * 0.4, width * 1.6, across);

        // along it: brightest at the head, fading back to the tail
        float s = (r - tail) / max(head - tail, 1e-4);
        float streak = saturate(s) * step(r, head) * step(tail, r);

        // brighter as it comes near; a new one fades in rather than popping up
        float bright = (0.12 + 0.88 * trip * trip) * (0.5 + 0.5 * h4) * smoothstep(1.0, 0.9, z);

        // white, a little toward one of the cover's colors
        float3 tint = lerp(float3(1, 1, 1), lerp(color1.rgb, color2.rgb, h4), 0.3);
        light += tint * thin * streak * bright;
    }
    return light;
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    // this screen's own middle, in screen heights
    float screenAspect = span.y > 0.0 ? span.x / span.y : aspect;
    float2 q = (uv - 0.5) * float2(screenAspect, 1.0);
    float r = length(q);
    float u = atan2(q.y, q.x) / Tau + 0.5;
    float t = time;
    float4 back = tex2D(input, uv);

    float3 stars = StarLayer(r, u, t, 110.0, 0.10, 0.0)
                 + StarLayer(r, u, t, 170.0, 0.075, 311.0)
                 + StarLayer(r, u, t, 260.0, 0.055, 727.0);

    // the far end of the tunnel: a faint glow of the cover's color, slowly breathing
    float glow = exp(-r * r * 9.0) * (0.85 + 0.15 * sin(t * 0.4));
    // and a little of the second color toward the edges, where the stars rush past
    // (kept faint: stronger, a warm second color turned the corners a muddy brown)
    float rim = smoothstep(0.45, 1.1, r) * 0.1;
    float3 sky = back.rgb + (color1.rgb * glow * 0.45 + color2.rgb * rim * 0.25) * intensity;

    // (a little brighter since they got thinner, so they don't fade into the dark)
    float3 light = stars * 2.0;
    float3 col = sky + light * intensity;

    // grain: about one step of 8-bit color, against banding in the glow
    float grain = frac(sin(dot(uv * 1000.0, float2(12.9898, 78.233))) * 43758.5453) - 0.5;
    col += grain / 255.0;

    float level = max(max(light.r, light.g), light.b) * intensity;
    float alpha = saturate(back.a + level + (glow * 0.45 + rim * 0.25) * intensity);
    return float4(min(col, alpha), alpha);
}
