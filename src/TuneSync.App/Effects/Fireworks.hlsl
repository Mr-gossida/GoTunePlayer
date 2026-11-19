// 불꽃놀이 (background effect): a fireworks show. A rocket climbs on a glittering trail and bursts;
// each burst is one of five kinds of shell, picked at random each time:
//   모란 (peony)          - a ball of bright stars, no trails, often changing color as it burns
//   국화 (chrysanthemum)  - stars dragging long streaks
//   버드나무 (willow)       - long golden trails that droop and hang in the air
//   링 (ring)             - all stars at one speed: a clean ring, with a second color inside
//   크래클 (crackle)        - stars that break into glitter, flickering as they fall
// Stars fly out at different speeds (a sphere seen from the front: a filled disc, thickest at
// the edge), sag under gravity - more at the head than along the trail, so the streaks curve
// down - and lose their light; the burst lights the sky around it for a moment. Colors come from
// the cover (and hue-shifted versions of it), with gold and silver shells among them.
// Six shells at staggered times, each somewhere new each time.
// Each shell is worked out in polar terms (which of its stars a pixel is near, how far out that
// star is), so a pixel costs a few dozen instructions per shell, not one per star.
//
// Same inputs as Aurora.hlsl. Recompile after editing:
//   dotnet run --project tools/ShaderCompiler -- src/TuneSync.App/Effects/Fireworks.hlsl src/TuneSync.App/Effects/Fireworks.ps

sampler2D input : register(s0);

float time : register(c0);
float4 color1 : register(c1);
float4 color2 : register(c2);
float aspect : register(c3);
float intensity : register(c4);
float2 offset : register(c5);
float2 span : register(c6);

static const float Period = 3.0;   // shells are placed along a strip this wide (repeating sideways)
static const float Cycle = 4.4;    // seconds from one launch of a shell to its next
static const float Rise = 0.85;    // seconds of the rocket's climb

float hash(float p) { return frac(sin(p * 91.3458) * 47453.5453); }

// A shell's color: the cover's two, hue-shifted versions of them, gold or silver; bright.
float3 Palette(float h)
{
    float3 c = h < 0.22 ? color1.rgb
             : h < 0.42 ? color2.rgb
             : h < 0.56 ? float3(1.0, 0.72, 0.30)
             : h < 0.68 ? float3(0.85, 0.92, 1.0)
             : h < 0.84 ? color1.gbr
             : color2.brg;
    c /= max(max(c.r, c.g), max(c.b, 0.05));
    return lerp(c, float3(1, 1, 1), 0.15);
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 P = span.y > 0.0 ? offset + uv * span : float2(uv.x * aspect, uv.y);
    float3 light = 0;

    [unroll] for (int k = 0; k < 6; k++)
    {
        float fk = k;
        float clock = time + fk * Cycle / 6.0 + hash(fk) * 2.0;
        float round_ = floor(clock / Cycle);
        float lt = clock - round_ * Cycle;            // seconds into this shell's cycle
        float seed = round_ * 7.13 + fk * 3.71;
        float kind = floor(hash(seed + 5.3) * 5.0);   // 0 peony, 1 chrysanthemum, 2 willow, 3 ring, 4 crackle
        float willow = kind == 2.0 ? 1.0 : 0.0;
        float2 c = float2(hash(seed) * Period, 0.14 + 0.38 * hash(seed + 1.7));
        float3 colA = willow > 0.0 ? float3(1.0, 0.68, 0.28) : Palette(hash(seed + 2.9));
        float3 colB = Palette(hash(seed + 6.1));

        // the rocket, climbing on a short glittering trail
        float climb = saturate(lt / Rise);
        float2 rocket = float2(c.x + 0.02 * sin(seed), lerp(1.15, c.y, 1.0 - (1.0 - climb) * (1.0 - climb)));
        float2 dr = P - rocket;
        dr.x -= Period * round(dr.x / Period);
        float up = step(lt, Rise);
        float head = smoothstep(0.006, 0.0, length(dr)) * up;
        float glitter = step(0.55, hash(floor(dr.y * 160.0) + floor(lt * 20.0)));
        float trail = smoothstep(0.0028, 0.0, abs(dr.x)) * smoothstep(0.0, 0.03, dr.y) * smoothstep(0.14, 0.0, dr.y) * up * (0.4 + 0.6 * glitter);
        light += (head * 1.4 + trail * 0.6) * float3(1.0, 0.9, 0.75);

        // the burst
        float bt = lt - Rise;
        float on = step(0.0, bt);
        float size = 0.22 + 0.24 * hash(seed + 4.1);
        float R = size * (1.0 - exp(-(willow > 0.0 ? 2.0 : 3.0) * bt));
        float2 d0 = P - c;
        d0.x -= Period * round(d0.x / Period);
        float r0 = length(d0);
        // gravity: the heads (out at R) have fallen furthest, the older parts of a trail less
        float sag = (willow > 0.0 ? 0.11 : 0.045) * bt * bt * saturate(r0 / max(R, 0.001));
        float2 d = float2(d0.x, d0.y - sag);
        float r = length(d);

        float rays = 22.0 + floor(hash(seed + 8.3) * 18.0);
        float turn = atan2(d.y, d.x) / 6.2831853;
        float a = turn * rays;
        float rayId = floor(a);
        float h = hash(rayId * 1.37 + seed);
        // each star a little off its even spoke, so the burst isn't a perfect wheel
        float jitter = kind == 3.0 ? 0.0 : (hash(rayId * 2.91 + seed) - 0.5) * 0.5;
        float arc = abs(frac(a) - 0.5 - jitter) * 6.2831853 / rays * r;   // distance across to the star's line
        // each star its own speed (a ring: all the same)
        float speed = kind == 3.0 ? 1.0 : 0.55 + 0.45 * sqrt(h);
        float Rk = R * speed;
        float tailFrom = kind == 0.0 ? 0.9 : kind == 3.0 ? 0.85 : kind == 4.0 ? 0.75 : 0.25;
        float width = willow > 0.0 ? 0.0032 : 0.0042;
        float streak = smoothstep(width, 0.0, arc) * smoothstep(Rk * tailFrom, Rk * 0.98, r) * smoothstep(Rk + 0.008, Rk, r);
        float star = smoothstep(kind == 0.0 ? 0.013 : 0.009, 0.0, length(float2(arc, r - Rk)));

        // burning down: willows hang longest; crackle and late stars flicker
        float fade = exp(-(willow > 0.0 ? 0.55 : 1.1) * bt);
        float flicker = kind == 4.0 && bt > 0.7 ? step(0.45, hash(rayId + floor(bt * 14.0) + seed)) * 1.4
                      : bt > 1.4 ? 0.6 + 0.4 * sin(bt * 22.0 + h * 40.0) : 1.0;
        float3 tint = lerp(colA, colB, kind == 0.0 || kind == 3.0 ? saturate((bt - 0.5) * 0.9) : 0.0);
        // a ring shell carries a second, smaller ring in its other color
        float inner = kind == 3.0 ? smoothstep(0.009, 0.0, length(float2(arc, r - Rk * 0.55))) : 0.0;
        // a second, smaller set of stars thrown slower, in between (the same angle, no new atan2)
        float rays2 = floor(rays * 1.6) + 3.0;
        float a2 = turn * rays2 + 0.37;
        float id2 = floor(a2);
        float h2 = hash(id2 * 3.17 + seed + 0.5);
        float arc2 = abs(frac(a2) - 0.5 - (hash(id2 * 1.93 + seed) - 0.5) * 0.6) * 6.2831853 / rays2 * r;
        float Rk2 = R * (0.3 + 0.5 * h2);
        float star2 = kind == 3.0 ? 0.0 : smoothstep(0.008, 0.0, length(float2(arc2, r - Rk2)))
                    * (0.5 + 0.5 * step(0.35, h2));
        float spark = streak * (willow > 0.0 ? 0.7 : 0.75) + star * (willow > 0.0 ? 0.5 : 1.5) + star2 * (willow > 0.0 ? 0.35 : 1.0);
        float flash = smoothstep(0.1, 0.0, r0) * exp(-10.0 * bt);
        float sky = exp(-r0 * r0 / (size * size) * 1.6) * 0.09 * exp(-2.0 * bt);
        light += (tint * (spark * flicker) + colB * inner * 1.2) * fade * on + (flash * float3(1, 0.95, 0.85) + sky * colA) * on;
    }

    float level = max(max(light.r, light.g), light.b);
    float alpha = saturate(saturate(level) * intensity / 0.7 * 0.9);
    float3 hue = light / max(level, 0.001);
    float4 back = tex2D(input, uv);
    return float4(hue * alpha + back.rgb * (1.0 - alpha), alpha + back.a * (1.0 - alpha));
}
