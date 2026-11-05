#ifndef DELIVERY_RIDER_HAZARD_GHOST
#define DELIVERY_RIDER_HAZARD_GHOST
// Per-draw rendering-layer data keeps the existing shared materials and SRP/instancing layout.
// Bit 30 marks a preview; bits 16..23 carry opacity. Original renderer layers are restored afterward.
void HazardGhostClip(float2 pixelPosition)
{
    uint state = asuint(unity_RenderingLayer.x);
    if ((state & 0x40000000u) == 0u) return;
    float opacity = float((state >> 16u) & 255u) / 255.0;
    const float pattern[16] = { 0,8,2,10, 12,4,14,6, 3,11,1,9, 15,7,13,5 };
    uint2 pixel = uint2(pixelPosition) & 3u;
    clip(opacity - (pattern[pixel.y * 4u + pixel.x] + 0.5) / 16.0);
}
#endif
