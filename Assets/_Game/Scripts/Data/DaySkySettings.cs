using UnityEngine;

namespace DeliveryRider
{
    // Time of day over the work day (planning doc 5.1): the sun starts high at departure (midday) and touches the
    // horizon at closing time; sunlight, ambient light, fog and sky colours follow it, and the far clouds drift with
    // the wind, with a few soft cloud heaps nearer by and cloud shadows passing over the town. After closing, the last
    // delivery runs into a blue dusk that holds at a readable brightness.
    // Curves and gradients run from departure (left, 0) to closing (right, 1). Every value is a test value.
    [CreateAssetMenu(menuName = "Delivery Rider/Day Sky Settings", fileName = "DaySkySettings")]
    public sealed class DaySkySettings : ScriptableObject
    {
        [Header("Sun path (degrees; azimuth 0 = +Z, 90 = +X)")]
        [SerializeField, Range(0f, 90f)] private float startElevation = 65f;
        [SerializeField, Range(-10f, 30f)] private float endElevation = 0f;
        [SerializeField, Range(0f, 360f)] private float startAzimuth = 180f;
        [SerializeField, Range(0f, 360f)] private float endAzimuth = 260f;
        [Tooltip("Share of the height left over the day: 1 at departure, 0 at closing. The default sinks slowly at first, like the afternoon sun.")]
        [SerializeField] private AnimationCurve height = new AnimationCurve(new Keyframe(0f, 1f, 0f, 0f), new Keyframe(1f, 0f, -1.6f, -1.6f));
        [Tooltip("Sunlight never comes from lower than this, so shadows keep a sane length while the visible sun reaches the horizon.")]
        [SerializeField, Range(0f, 45f)] private float minimumLightElevation = 18f;

        [Header("Light over the day")]
        [SerializeField] private Gradient sunColor = Blend((0f, new Color(1f, 0.97f, 0.92f)), (0.6f, new Color(1f, 0.92f, 0.8f)), (1f, new Color(1f, 0.6f, 0.35f)));
        [SerializeField] private AnimationCurve sunIntensity = new AnimationCurve(new Keyframe(0f, 1.3f), new Keyframe(0.7f, 1.2f), new Keyframe(1f, 0.8f));
        [SerializeField] private Gradient ambientColor = Blend((0f, new Color(0.65f, 0.72f, 0.8f)), (0.7f, new Color(0.68f, 0.7f, 0.74f)), (1f, new Color(0.62f, 0.55f, 0.58f)));
        [Tooltip("Matches the sky's horizon so the bent edge of the curved world meets the sky without a band.")]
        [SerializeField] private Gradient fogColor = Blend((0f, new Color(0.776f, 0.875f, 0.847f)), (0.6f, new Color(0.83f, 0.87f, 0.82f)),
            (0.85f, new Color(0.97f, 0.84f, 0.66f)), (1f, new Color(0.96f, 0.68f, 0.55f)));

        [Header("Sky (the horizon takes the fog colour)")]
        [SerializeField] private Gradient zenithColor = Blend((0f, new Color(0.42f, 0.66f, 0.88f)), (0.6f, new Color(0.47f, 0.68f, 0.88f)),
            (0.85f, new Color(0.58f, 0.64f, 0.8f)), (1f, new Color(0.45f, 0.43f, 0.68f)));
        [SerializeField, Range(0.5f, 6f)] private float sunSize = 2.5f;

        [Header("Far clouds and wind")]
        [Tooltip("0 clear, 1 overcast. Wide patches still gather and clear around this value.")]
        [SerializeField, Range(0f, 1f)] private float cloudCoverage = 0.45f;
        [Tooltip("Larger numbers make smaller clouds.")]
        [SerializeField, Range(0.2f, 5f)] private float cloudScale = 1.6f;
        [Tooltip("Direction the wind blows toward (0 = +Z, 90 = +X). Shared with the near clouds and cloud shadows.")]
        [SerializeField, Range(0f, 360f)] private float windAzimuth = 90f;
        [SerializeField, Range(0f, 30f)] private float windSpeed = 5f;
        [Tooltip("How fast cloud shapes change (gather and break up), in noise units per second.")]
        [SerializeField, Range(0f, 0.2f)] private float cloudChange = 0.02f;
        [Tooltip("Share of the sky's colour in the shaded side of every cloud, near and far; the rest is a grey of the same brightness. 1 makes the undersides sky blue.")]
        [SerializeField, Range(0f, 1f)] private float cloudShadeTint = 0.5f;

        [Header("Near clouds (soft heaps in the world)")]
        [SerializeField, Range(0, 30)] private int nearCloudCount = 6;
        [Tooltip("Height above the ground in metres (x lowest, y highest).")]
        [SerializeField] private Vector2 nearCloudAltitude = new Vector2(85f, 125f);
        [Tooltip("Length in metres (x smallest, y largest).")]
        [SerializeField] private Vector2 nearCloudSize = new Vector2(50f, 85f);
        [Tooltip("Share of the wind speed; near clouds look faster than the far layer at the same speed.")]
        [SerializeField, Range(0f, 2f)] private float nearCloudSpeedShare = 0.8f;
        [Tooltip("Seconds a cloud lives before it breaks up (x shortest, y longest).")]
        [SerializeField] private Vector2 nearCloudLifetime = new Vector2(90f, 150f);
        [SerializeField, Min(1f)] private float nearCloudFadeSeconds = 20f;
        [Tooltip("Clouds live within this distance of the rider; past it they break up and form again elsewhere. Around where the curved world hides them.")]
        [SerializeField, Min(50f)] private float nearCloudRadius = 220f;
        [Tooltip("Share of the curved world's drop the clouds take. 1 follows the ground, so a cloud rises steeply as you come closer; lower keeps it steadier, with distant clouds still settling toward the horizon.")]
        [SerializeField, Range(0f, 1f)] private float nearCloudCurveShare = 0.4f;
        [SerializeField] private bool nearCloudShadows = true;
        [Tooltip("How far a cloud's surface billows out and in, as a share of its size. 0 keeps the shapes still.")]
        [SerializeField, Range(0f, 0.2f)] private float nearCloudSwell = 0.06f;
        [Tooltip("How fast the billows change, in noise units per second.")]
        [SerializeField, Range(0f, 1f)] private float nearCloudSwellSpeed = 0.08f;
        [Tooltip("Noise cells across a cloud; smaller numbers make broader heaps.")]
        [SerializeField, Range(0.5f, 8f)] private float nearCloudSwellScale = 2.5f;
        [Tooltip("Light along the cloud edges against the sky.")]
        [SerializeField, Range(0f, 1f)] private float nearCloudRim = 0.3f;
        [Tooltip("How much the underside and the gaps between puffs darken (baked into the cloud shapes).")]
        [SerializeField, Range(0f, 1f)] private float nearCloudOcclusion = 0.4f;

        [Header("Cloud shadows (a pattern on the sunlight; follows the cloud amount, near-cloud speed and shape change)")]
        [Tooltip("Share of the sunlight a cloud shadow takes (0.3 barely showed at midday). 0 turns the pattern off.")]
        [SerializeField, Range(0f, 1f)] private float cloudShadowStrength = 0.5f;
        [Tooltip("Metres the pattern spans before it repeats; the shadows themselves are a fraction of it.")]
        [SerializeField, Range(30f, 400f)] private float cloudShadowSize = 120f;

        [Header("Fog (the world dissolves into the sky behind it)")]
        [Tooltip("Metres from the camera with no fog.")]
        [SerializeField, Min(0f)] private float fogStart = 10f;
        [Tooltip("Metres from the camera where the ground is about 98% sky.")]
        [SerializeField, Min(1f)] private float fogEnd = 170f;
        [Tooltip("How much the fog thins on high parts, so rooftops stay clearer than the street below them.")]
        [SerializeField, Range(0f, 1f)] private float fogHeightShare = 0.5f;
        [Tooltip("Metres over which the fog thins with height.")]
        [SerializeField, Min(0.1f)] private float fogHeightRange = 15f;
        [Tooltip("How far thin haze leans to the zenith colour, which separates near and far buildings (aerial perspective).")]
        [SerializeField, Range(0f, 1f)] private float fogBlue = 0.35f;

        [Header("After closing (dusk; blends from the closing look)")]
        [Tooltip("Game seconds from closing to the end of dusk; the dusk then holds until the settlement.")]
        [SerializeField, Min(1f)] private float duskSeconds = 60f;
        [SerializeField, Range(-20f, 0f)] private float duskSunElevation = -8f;
        [SerializeField] private Color duskZenith = new Color(0.16f, 0.2f, 0.42f);
        [Tooltip("Horizon and fog at the end of dusk.")]
        [SerializeField] private Color duskHorizon = new Color(0.58f, 0.45f, 0.56f);
        [Tooltip("Afterglow on the sky and clouds toward the set sun.")]
        [SerializeField] private Color duskGlow = new Color(0.98f, 0.55f, 0.45f);
        [SerializeField] private Color duskAmbient = new Color(0.4f, 0.43f, 0.58f);
        [SerializeField] private Color duskLightColor = new Color(0.6f, 0.66f, 0.92f);
        [Tooltip("Light at the end of dusk; about 35% of midday keeps deliveries readable.")]
        [SerializeField, Range(0f, 2f)] private float duskLightIntensity = 0.45f;
        [Tooltip("Shadows fade toward this strength as the direct sun goes.")]
        [SerializeField, Range(0f, 1f)] private float duskShadowStrength = 0.4f;

        public float MinimumLightElevation => minimumLightElevation;
        public int NearCloudCount => nearCloudCount;
        public Vector2 NearCloudAltitude => nearCloudAltitude;
        public Vector2 NearCloudSize => nearCloudSize;
        public float NearCloudSpeed => windSpeed * nearCloudSpeedShare;
        public Vector2 NearCloudLifetime => nearCloudLifetime;
        public float NearCloudFadeSeconds => nearCloudFadeSeconds;
        public float NearCloudRadius => nearCloudRadius;
        public float NearCloudCurveShare => nearCloudCurveShare;
        public bool NearCloudShadows => nearCloudShadows;
        public float NearCloudSwell => nearCloudSwell;
        public float NearCloudSwellSpeed => nearCloudSwellSpeed;
        public float NearCloudSwellScale => nearCloudSwellScale;
        public float NearCloudRim => nearCloudRim;
        public float NearCloudOcclusion => nearCloudOcclusion;
        public float CloudShadowStrength => cloudShadowStrength;
        public float CloudShadowSize => cloudShadowSize;
        public float FogStart => fogStart;
        public float FogEnd => Mathf.Max(fogEnd, fogStart + 1f);
        public float FogHeightShare => fogHeightShare;
        public float FogHeightRange => fogHeightRange;
        public float FogBlue => fogBlue;
        public float DuskSeconds => duskSeconds;
        public float DuskSunElevation => duskSunElevation;
        public Color DuskZenith => duskZenith;
        public Color DuskHorizon => duskHorizon;
        public Color DuskGlow => duskGlow;
        public Color DuskAmbient => duskAmbient;
        public Color DuskLightColor => duskLightColor;
        public float DuskLightIntensity => duskLightIntensity;
        public float DuskShadowStrength => duskShadowStrength;
        public float SunSize => sunSize;
        public float CloudCoverage => cloudCoverage;
        public float CloudScale => cloudScale;
        public float WindSpeed => windSpeed;
        public float CloudChange => cloudChange;
        public float CloudShadeTint => cloudShadeTint;
        public Vector2 WindDirection => new Vector2(Mathf.Sin(windAzimuth * Mathf.Deg2Rad), Mathf.Cos(windAzimuth * Mathf.Deg2Rad));
        public float Elevation(float day) => Mathf.Lerp(endElevation, startElevation, height.Evaluate(day));
        public float Azimuth(float day) => Mathf.LerpAngle(startAzimuth, endAzimuth, day);
        public Color SunColor(float day) => sunColor.Evaluate(day);
        public float SunIntensity(float day) => sunIntensity.Evaluate(day);
        public Color AmbientColor(float day) => ambientColor.Evaluate(day);
        public Color FogColor(float day) => fogColor.Evaluate(day);
        public Color ZenithColor(float day) => zenithColor.Evaluate(day);

        private static Gradient Blend(params (float time, Color color)[] keys)
        {
            var colors = new GradientColorKey[keys.Length];
            for (int i = 0; i < keys.Length; i++) colors[i] = new GradientColorKey(keys[i].color, keys[i].time);
            var gradient = new Gradient();
            gradient.SetKeys(colors, new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
            return gradient;
        }
    }
}
