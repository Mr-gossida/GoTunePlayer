// 창문 빗방울 (background effect): rain on a window pane. Drops cling to the glass, run down in
// little jerks leaving a wet trail, small beads stay where they landed and fade - and the
// backdrop behind is bent through every drop, the way a rainy window bends the lights outside.
// One evaluation of the drop field per pixel; the bending comes from its screen-space slope
// (ddx/ddy), so no extra passes or samples beyond one look-up of the backdrop.
//
// Same inputs as Aurora.hlsl. Recompile after editing:
//   dotnet run --project tools/ShaderCompiler -- src/TuneSync.App/Effects/RainWindow.hlsl src/TuneSync.App/Effects/RainWindow.ps

sampler2D input : register(s0);

float time : register(c0);
float4 color1 : register(c1);
float4 color2 : register(c2);
float aspect : register(c3);
float intensity : register(c4);
float2 offset : register(c5);
float2 span : register(c6);

float N1(float t) { return frac(sin(t * 12345.564) * 7658.76); }

float3 N13(float p)
{
    float3 p3 = frac(float3(p, p, p) * float3(0.1031, 0.11369, 0.13787));
    p3 += dot(p3, p3.yzx + 19.19);
    return frac(float3((p3.x + p3.y) * p3.z, (p3.x + p3.z) * p3.y, (p3.y + p3.z) * p3.x));
}

// rises to 1 at b, back to 0 at 1: a drop that slides quickly then pauses
float Saw(float b, float t) { return smoothstep(0.0, b, t) * smoothstep(1.0, b, t); }

// One layer of running drops: x = the drop (and the beads it leaves), y = its wet trail.
float2 DropLayer(float2 uv, float t)
{
    float2 UV = uv;
    uv.y += t * 0.75;
    float2 a = float2(6.0, 1.0);
    float2 grid = a * 2.0;
    float2 id = floor(uv * grid);
    uv.y += N1(id.x);
    id = floor(uv * grid);
    float3 n = N13(id.x * 35.2 + id.y * 2376.1);
    float2 st = frac(uv * grid) - float2(0.5, 0.0);

    float x = n.x - 0.5;
    float y = UV.y * 20.0;
    x += sin(y + sin(y)) * (0.5 - abs(x)) * (n.z - 0.5);
    x *= 0.7;
    float ti = frac(t + n.z);
    y = (Saw(0.85, ti) - 0.5) * 0.9 + 0.5;
    float2 p = float2(x, y);

    float d = length((st - p) * a.yx);
    float drop = smoothstep(0.4, 0.0, d);

    float r = sqrt(smoothstep(1.0, y, st.y));
    float cd = abs(st.x - x);
    float trail = smoothstep(0.23 * r, 0.15 * r * r, cd);
    float front = smoothstep(-0.02, 0.02, st.y - y);
    trail *= front * r * r;

    float yy = frac(UV.y * 10.0) + (st.y - 0.5);
    float beads = smoothstep(0.3, 0.0, length(st - float2(x, yy)));
    return float2(drop + beads * r * front, trail);
}

// Small drops sitting still, appearing and drying up.
float StaticDrops(float2 uv, float t)
{
    uv *= 40.0;
    float2 id = floor(uv);
    uv = frac(uv) - 0.5;
    float3 n = N13(id.x * 107.45 + id.y * 3543.654);
    float2 p = (n.xy - 0.5) * 0.7;
    float fade = Saw(0.025, frac(t + n.z));
    return smoothstep(0.3, 0.0, length(uv - p)) * frac(n.z * 10.0) * fade;
}

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 P = span.y > 0.0 ? offset + uv * span : float2(uv.x * aspect, uv.y);
    // the drop field works with y up, so the drops run down the screen
    float2 q = float2(P.x, -P.y) * 0.9;
    float t = time * 0.06;

    float still = StaticDrops(q, t);
    float2 big = DropLayer(q, t);
    float2 small = DropLayer(q * 1.85, t);
    float c = smoothstep(0.3, 1.0, still + big.x + small.x);
    float trail = max(big.y, small.y);

    // the glass bends the light: by the drop field's slope on screen
    float2 bend = float2(ddx(c), ddy(c));
    float4 back = tex2D(input, uv + bend * 0.35);
    float4 plain = tex2D(input, uv);

    // the drops catch a little light, in the cover's color; their lower edge glints
    float3 glint = lerp(color1.rgb, float3(1, 1, 1), 0.6) * saturate(c * 0.18 + max(0.0, -bend.y) * 6.0);
    float3 wet = back.rgb * (1.0 + trail * 0.06) + glint * back.a;

    float strength = saturate(intensity / 0.7);
    float3 rgb = lerp(plain.rgb, wet, strength);
    return float4(rgb, plain.a);
}
