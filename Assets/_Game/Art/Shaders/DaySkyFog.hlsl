#ifndef DELIVERY_RIDER_DAY_SKY_FOG_INCLUDED
#define DELIVERY_RIDER_DAY_SKY_FOG_INCLUDED

#include "CurvedWorld.hlsl"

// Set every frame by DaySky.cs. Unset (edit mode, other scenes) they are zero: no sun glow, the horizon colour only.
// The visible sun: xyz toward it, w dusk after closing. The sunlight itself never goes below its minimum height.
float4 _DayCloudSun;
// Sky colour overhead; the horizon is the fog colour.
float4 _DaySkyZenith;
// rgb sun glow in the sky, a how far thin haze leans to the zenith colour.
float4 _DaySkyGlow;
// x start, y 1 / (end - start), z share of the fog that thins with height, w 1 / height range. y is 0 without DaySky.
float4 _DayFog;

// The sky seen in a direction, without clouds or the sun disc. Keep in step with the gradient in Sky.shader.
float3 DaySkyBehind(float3 direction)
{
    float3 color = lerp(unity_FogColor.rgb, _DaySkyZenith.rgb, pow(saturate(direction.y), 0.45) * _DaySkyZenith.a);
    float towardSun = pow(saturate(dot(direction, _DayCloudSun.xyz)), 6.0);
    float lowSun = 1.0 - saturate(_DayCloudSun.y * 2.5);
    return color + _DaySkyGlow.rgb * towardSun * (0.08 + 0.32 * lowSun) * (1.0 - saturate(direction.y * 1.5));
}

// Aerial perspective: clear nearby, thin haze leans blue, and thick haze becomes exactly the sky behind, so the far
// town dissolves without a band. positionWS is the bent position, the one seen on screen.
float3 DaySkyFog(float3 color, float3 positionWS)
{
    float3 toFragment = positionWS - _WorldSpaceCameraPos;
    float range = length(toFragment);
    float depth = max(range - _DayFog.x, 0.0) * _DayFog.y;
    // The bend only lowers things, so the drop gives back the height above the flat ground.
    float height = max(positionWS.y + CurvedWorldDrop(positionWS), 0.0);
    float thin = lerp(1.0, exp(-height * _DayFog.w), _DayFog.z);
    // 3.9 makes the fog about 98% at the end distance on the ground.
    float fog = 1.0 - exp(-3.9 * depth * depth * thin);
    float3 blueHaze = lerp(unity_FogColor.rgb, _DaySkyZenith.rgb, _DaySkyGlow.a);
    return lerp(color, lerp(blueHaze, DaySkyBehind(toFragment / max(range, 0.001)), fog), fog);
}

// Replaces URP's MixFog while DaySky runs; everything else keeps the URP fog.
float3 DayFogMix(float3 color, float fogCoord, float3 positionWS)
{
    float3 mixed;
    if (_DayFog.y > 0.0 && IsFogEnabled()) mixed = DaySkyFog(color, positionWS);
    else mixed = MixFogColor(color, unity_FogColor.rgb, fogCoord);
    return mixed;
}

#endif
