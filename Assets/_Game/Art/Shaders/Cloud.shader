// Near soft clouds (Docs/하늘_시간대_구현계획.md, step 5), after AlexStrook/UnlitClouds (CC0). Each cloud sinks as a whole
// with the curved world in every pass (by its origin's drop, so it is not sheared), and distant clouds settle toward
// the horizon. As _Fade (per cloud, set by CloudField) rises, the puffs swell up from their centres one by one (centre
// and turn in the second UV channel, from CloudShapes) and sink back the other way round as it falls, so a cloud forms
// and breaks up fully opaque, with its shadow following. A mesh without that channel grows from its origin as a whole. The surface billows along its normals with noise, broad heaps plus
// faster small boiling (the flat base stays put), and is shaded from the billowed normal with a soft ramp between the
// cloud colours DaySky shares with the far cloud layer (_DayCloudLit/_DayCloudShade). The occlusion baked into the
// vertex colours (CloudShapes) darkens the underside and the gaps between puffs, and the edges catch a rim of light
// against the sky. With distance a cloud melts into the sky behind it (the same horizon-to-zenith gradient as Sky.shader). CloudField sets the amounts
// (_NearCloudSwell, _NearCloudLook) from DaySkySettings; without them (edit mode) the clouds stay still and plain.
Shader "DeliveryRider/Cloud"
{
    Properties
    {
        _FadeStart ("Melt Into Horizon From (m)", Float) = 140
        _FadeEnd ("Melt Into Horizon At (m)", Float) = 240
        [HideInInspector] _Fade ("Fade (set per cloud)", Range(0, 1)) = 1
        [HideInInspector] _Seed ("Seed (set per cloud)", Float) = 0
        [HideInInspector] _Tint ("Tint (set per dust puff, RiderDust)", Color) = (1, 1, 1, 1)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "DaySkyFog.hlsl"

        CBUFFER_START(UnityPerMaterial)
        float _FadeStart, _FadeEnd, _Fade, _Seed;
        float4 _Tint;
        CBUFFER_END

        // Shared cloud colours, set every frame by DaySky. The clouds follow the visible sun past the horizon like the
        // far cloud layer.
        float4 _DayCloudLit, _DayCloudShade;
        // Set every frame by CloudField. Swell: x height (share of the cloud's size), y phase, z noise cells across a
        // cloud. Look: x rim, y occlusion.
        float4 _NearCloudSwell, _NearCloudLook;
        // Share of the curved world's drop the clouds take (CloudField): the full drop makes a cloud rise steeply as
        // the rider comes closer.
        float _NearCloudCurve;

        float Hash(float3 p)
        {
            p = frac(p * 0.3183099 + 0.1);
            p *= 17.0;
            return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
        }

        float Noise(float3 x)
        {
            float3 i = floor(x);
            float3 f = frac(x);
            f = f * f * (3.0 - 2.0 * f);
            return lerp(lerp(lerp(Hash(i), Hash(i + float3(1, 0, 0)), f.x),
                             lerp(Hash(i + float3(0, 1, 0)), Hash(i + float3(1, 1, 0)), f.x), f.y),
                        lerp(lerp(Hash(i + float3(0, 0, 1)), Hash(i + float3(1, 0, 1)), f.x),
                             lerp(Hash(i + float3(0, 1, 1)), Hash(i + float3(1, 1, 1)), f.x), f.y), f.z);
        }

        // Billow height at a point in noise space, about -1..1: two broad noises drifting apart (the shape changes
        // rather than slides) plus faster small boiling.
        float Billow(float3 p, float phase)
        {
            float broad = Noise(p + float3(phase, 0.0, phase * 0.6)) + Noise(p * 1.3 - float3(phase * 0.8, 0.0, -phase) + 31.0) - 1.0;
            float boil = Noise(p * 2.3 + float3(0.0, phase * 1.6, 0.0) + 57.0) - 0.5;
            return broad + boil * 0.7;
        }

        // World position and normal of a cloud vertex: billowed along the normal, then sunk with the curved world. The
        // normal leans with the billow's slope, so the heaps are shaded too. The cloud sinks as a whole by its origin's
        // drop; bending each vertex would shear it into a tall slab at a distance.
        void CloudSurface(float3 positionOS, float3 normalOS, float4 puff, out float3 positionWS, out float3 normalWS)
        {
            // Each puff swells up in its turn: the first a little before the others, each over 40% of the fade.
            float growth = smoothstep(puff.w * 0.6, puff.w * 0.6 + 0.4, _Fade);
            positionOS = lerp(puff.xyz, positionOS, growth);
            float3 origin = GetObjectToWorldMatrix()._m03_m13_m23;
            float size = length(GetObjectToWorldMatrix()._m00_m10_m20);
            positionWS = TransformObjectToWorld(positionOS);
            normalWS = TransformObjectToWorldNormal(normalOS);
            float3 local = (positionWS - origin) / size;
            float3 p = local * _NearCloudSwell.z + _Seed;
            float phase = _NearCloudSwell.y;
            float height = Billow(p, phase);
            const float delta = 0.15;
            float3 slope = (float3(Billow(p + float3(delta, 0.0, 0.0), phase), Billow(p + float3(0.0, delta, 0.0), phase),
                Billow(p + float3(0.0, 0.0, delta), phase)) - height) / delta;
            // Nothing moves at the flat base.
            float amount = _NearCloudSwell.x * saturate(local.y * 8.0) * growth;
            positionWS += normalWS * (height * amount * size);
            normalWS = normalize(normalWS - amount * _NearCloudSwell.z * (slope - normalWS * dot(slope, normalWS)));
            positionWS.y -= CurvedWorldDrop(origin) * _NearCloudCurve;
        }

        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float4 color : COLOR;
            float4 puff : TEXCOORD1;
        };
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float openness : TEXCOORD2;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                CloudSurface(input.positionOS.xyz, input.normalOS, input.puff, output.positionWS, output.normalWS);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                output.openness = input.color.r;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 normalWS = normalize(input.normalWS);
                // Wrapped light with a soft ramp: no hard terminator, the far side fades into the shade colour.
                float lit = smoothstep(0.05, 0.75, dot(normalWS, _DayCloudSun.xyz) * 0.5 + 0.5) * (1.0 - 0.8 * _DayCloudSun.w);
                float3 color = lerp(_DayCloudShade.rgb, _DayCloudLit.rgb, lit) * lerp(1.0, input.openness, _NearCloudLook.y);
                float3 toCamera = normalize(_WorldSpaceCameraPos - input.positionWS);
                color += _DayCloudLit.rgb * pow(1.0 - saturate(dot(normalWS, toCamera)), 3.0) * _NearCloudLook.x;
                color *= _Tint.rgb;
                float distance = length(input.positionWS.xz - _WorldSpaceCameraPos.xz);
                color = lerp(color, DaySkyBehind(-toCamera), smoothstep(_FadeStart, _FadeEnd, distance));
                return half4(color, 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ZTest LEqual
            ColorMask 0
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            float4 vert(Attributes input) : SV_POSITION
            {
                float3 positionWS, normalWS;
                CloudSurface(input.positionOS.xyz, input.normalOS, input.puff, positionWS, normalWS);
                #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                    float3 lightDirection = normalize(_LightPosition - positionWS);
                #else
                    float3 lightDirection = _LightDirection;
                #endif
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirection));
                #if UNITY_REVERSED_Z
                    positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                return positionCS;
            }

            half4 frag(float4 positionCS : SV_POSITION) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
