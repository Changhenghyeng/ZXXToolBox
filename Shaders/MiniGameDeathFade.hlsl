#ifndef MINIGAME_DEATH_FADE_INCLUDED
#define MINIGAME_DEATH_FADE_INCLUDED
// Applied after texture and lighting so coloured equipment is desaturated as well.
half3 MiniGameDeathColor(half3 color, half grayAmount)
{
    half gray = dot(color, half3(0.2126h, 0.7152h, 0.0722h)) * 0.78h;
    return lerp(color, gray.xxx, grayAmount);
}
// Opaque material coverage fade: keeps original lighting, outline and shadow passes.
// A single per-renderer float avoids cloned transparent materials and sorting costs.
void MiniGameClipDeath(float2 pixel, half opacity)
{
    if (opacity >= 0.9999h) return;
    const half thresholds[16] = {
        0.03125h, 0.53125h, 0.15625h, 0.65625h,
        0.78125h, 0.28125h, 0.90625h, 0.40625h,
        0.21875h, 0.71875h, 0.09375h, 0.59375h,
        0.96875h, 0.46875h, 0.84375h, 0.34375h
    };
    uint2 cell = (uint2)pixel & 3u;
    clip(opacity - thresholds[cell.x + cell.y * 4u]);
}
#endif
