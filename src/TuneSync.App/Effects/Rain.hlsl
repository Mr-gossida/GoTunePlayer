// 비 (background effect): rain falling at a slant over a soft glow in the cover's color, onto a
// water surface across the bottom of the screen where each drop leaves a flat ring (and a fainter
// second one) spreading out - the seek bar's 빗방울 look made into a whole scene.
//
// The streaks are long and fade toward their tails, so the light's 15 steps a second read as
// falling rain rather than jumping dots. Three depths: far rain is thin, faint and slow and lands
// higher on the water; near rain is thicker, brighter and faster and lands lower.
//
// Everything is drawn in shared coordinates (P), so with several monitors the rain and the water
// carry on from one screen to the next.
//
// Same inputs as Aurora.hlsl. Recompile after editing:
//   dotnet run --project tools/ShaderCompiler -- src/TuneSync.App/Effects/Rain.hlsl src/TuneSync.App/Effects/Rain.ps

sampler2D input : register(s0);

float time : register(c0);
float4 color1 : register(c1);
float4 color2 : register(c2);
float aspect : register(c3);
float intensity : register(c4);
float2 offset : register(c5);
float2 span : register(c6);

// where the water starts, in screen heights from the top
static const float Surface = 0.80;
// the rain's slant: sideways drift per unit of fall
static const float Slant = 0.22;

float Hash(float n) { return frac(sin(n * 12.9898 + 4.1414) * 43758.5453); }

// One depth of rain: columns of streaks, each column one drop on its own speed and phase.
float RainLayer(float2 P, float t, float z, float columnWidth, float seed)
{
    // sheared so each drop's path is a vertical line (x stays put as it falls)
    float qx = P.x - P.y * Slant;
    float column = floor(qx / columnWidth);
    float r1 = Hash(column + seed);
    float r2 = Hash(column * 1.7 + seed + 31.0);
    float r3 = Hash(column * 2.3 + seed + 67.0);
    float r4 = Hash(column * 3.1 + seed + 97.0);

    // not every column has rain at once
    float present = step(0.3, r4);

    // across the streak: a thin line, a little off the column's middle
    float across = (frac(qx / columnWidth) - 0.5 - (r1 - 0.5) * 0.6) * columnWidth;
    float halfWidth = 0.0004 + 0.0009 * z;
    float thin = 1.0 - smoothstep(halfWidth * 0.4, halfWidth * 1.6, abs(across));

    // along the streak: the head falls and loops; brightest at the head, fading up the tail
    float speed = (1.1 + 0.6 * r2) * z;          // screen heights a second
    float loop = 1.6;                            // fall height plus a pause above the screen
    float head = frac(r3 + t * speed / loop) * loop - 0.25;
    float len = (0.06 + 0.06 * r2) * z;
    float s = (head - P.y) / len;
    float streak = saturate(1.0 - s) * step(0.0, s);

    // it ends where it lands on the water: far rain lands higher, near rain lower
    float land = Surface + 0.015 + 0.15 * (z - 0.45);
    streak *= 1.0 - smoothstep(land - 0.01, land, P.y);

    return thin * streak * present * (0.14 + 0.42 * z);
}

// Rings on the water: one drop per cell, landing over and over at its own pace. The ring never
// grows past its cell, so only the pixel's own cell has to be looked at.
float Ripples(float2 P, float t, float2 cell, float2 shift, float seed)
{
    float2 W = P - float2(0.0, Surface) + shift;
    // each row slid sideways by its own amount, so the rings don't line up in columns
    W.x += Hash(floor(W.y / cell.y) * 5.1 + seed) * cell.x;
    float2 index = floor(W / cell);
    float2 inCell = W - (index + 0.5) * cell;
    float id = index.x * 7.31 + index.y * 13.7 + seed;
    float h1 = Hash(id);
    float h2 = Hash(id + 19.0);
    float h3 = Hash(id + 41.0);

    // nearer water (lower on the screen) gets bigger rings
    float depth = saturate((P.y - Surface) / (1.0 - Surface));
    float size = 0.55 + 0.45 * depth;

    float2 center = float2((h1 - 0.5) * 0.5 * cell.x, (h2 - 0.5) * 0.4 * cell.y);
    float period = 1.6 + 2.4 * h3;
    float cycle = t / period + h1 * 3.7;
    float age = frac(cycle) * period;
    float life = 1.1;
    float p = saturate(age / life);
    // a drop lands in only some cells each round, so the water isn't covered in rings
    float alive = step(age, life) * step(0.5, Hash(id + floor(cycle) * 1.37));

    float grow = 1.0 - (1.0 - p) * (1.0 - p);
    float2 d = inCell - center;
    float dist = length(float2(d.x, d.y / 0.28));
    float radius = (0.004 + 0.22 * cell.x * size * grow);
    float width = 0.0025;
    float ring = 1.0 - smoothstep(0.0, width, abs(dist - radius));
    float inner = (1.0 - smoothstep(0.0, width, abs(dist - radius * 0.6))) * step(0.15, p) * 0.5;
    float flash = (1.0 - smoothstep(0.0, 0.004, dist)) * saturate(1.0 - p / 0.1) * 1.4;

    return (ring + inner + flash) * (1.0 - p) * alive * (0.35 + 0.45 * size);
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 P = span.y > 0.0 ? offset + uv * span : float2(uv.x * aspect, uv.y);
    float wide = span.y > 0.0 ? max(span.x + offset.x, 1.0) : aspect;
    float t = time;
    float4 back = tex2D(input, uv);

    // a soft glow of the cover's color drifting slowly across the sky
    float2 g = P - float2(wide * (0.35 + 0.15 * sin(t * 0.07)), 0.35 + 0.05 * cos(t * 0.05));
    float glow = exp(-dot(g, g) * 2.2);
    float3 sky = back.rgb + color1.rgb * glow * 0.55 * intensity;

    // the water: darker, with a faint line where it starts
    float water = smoothstep(Surface - 0.06, Surface + 0.04, P.y);
    sky *= 1.0 - 0.45 * water * intensity;
    float horizon = (1.0 - smoothstep(0.0, 0.0025, abs(P.y - (Surface - 0.05)))) * 0.10;

    float rain = RainLayer(P, t, 0.45, 0.020, 0.0)
               + RainLayer(P, t, 0.70, 0.026, 113.0)
               + RainLayer(P, t, 1.00, 0.034, 271.0);

    float rings = 0.0;
    if (P.y > Surface)
    {
        rings = Ripples(P, t, float2(0.22, 0.06), float2(0.0, 0.0), 5.0)
              + Ripples(P, t, float2(0.22, 0.06), float2(0.11, 0.03), 59.0);
    }

    float3 pale = lerp(color2.rgb, float3(1, 1, 1), 0.55);
    float3 light = pale * (rain + rings * 0.65 + horizon);
    float3 col = sky + light * intensity;

    // grain: about one step of 8-bit color, against banding in the glow
    float grain = frac(sin(dot(uv * 1000.0, float2(12.9898, 78.233))) * 43758.5453) - 0.5;
    col += grain / 255.0;

    float level = max(max(light.r, light.g), light.b) * intensity;
    float alpha = saturate(back.a + level + glow * 0.55 * intensity);
    return float4(min(col, alpha), alpha);
}
