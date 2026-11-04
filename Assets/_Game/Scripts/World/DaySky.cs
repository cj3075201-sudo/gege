using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace DeliveryRider
{
    // Moves the sun with the work day (DaySkySettings): the configured morning/noon/evening arc reaches the horizon at closing,
    // with sunlight colour and intensity, ambient light, fog and the sky shader following, and the far clouds
    // drifting with the wind. Cloud shadows pass over the town as a light cookie: a repeating pattern drawn every
    // frame (CloudShadow.shader) and moved with the wind at the near clouds' speed. After closing, the game time spent on the last delivery carries the sky into a blue dusk
    // that holds at a readable brightness (it stops with the game, e.g. under the settlement screen). The light stays
    // above the minimum elevation even when the visible sun is lower. The sky is drawn from a runtime copy of the
    // skybox material, so Play Mode never edits the asset. Developer tools can show another time without touching
    // the clock.
    public sealed class DaySky : MonoBehaviour
    {
        // Sky.shader cloud plane: one noise unit spans this many metres, which turns the wind speed into drift.
        private const float CloudMetresPerUnit = 600f;
        private static readonly int ZenithId = Shader.PropertyToID("_ZenithColor");
        private static readonly int HorizonId = Shader.PropertyToID("_HorizonColor");
        private static readonly int SunColorId = Shader.PropertyToID("_SunColor");
        private static readonly int SunDirectionId = Shader.PropertyToID("_SunDirection");
        private static readonly int SunSizeId = Shader.PropertyToID("_SunSize");
        private static readonly int DuskId = Shader.PropertyToID("_Dusk");
        private static readonly int CoverageId = Shader.PropertyToID("_CloudCoverage");
        private static readonly int CloudScaleId = Shader.PropertyToID("_CloudScale");
        private static readonly int CloudOffsetId = Shader.PropertyToID("_CloudOffset");
        private static readonly int CloudTimeId = Shader.PropertyToID("_CloudTime");
        private static readonly int SkyCloudLitId = Shader.PropertyToID("_CloudLit");
        private static readonly int SkyCloudShadeId = Shader.PropertyToID("_CloudShade");
        private static readonly int CloudLitId = Shader.PropertyToID("_DayCloudLit");
        private static readonly int CloudShadeId = Shader.PropertyToID("_DayCloudShade");
        private static readonly int CloudSunId = Shader.PropertyToID("_DayCloudSun");
        private static readonly int SkyZenithId = Shader.PropertyToID("_DaySkyZenith");
        private static readonly int SkyGlowId = Shader.PropertyToID("_DaySkyGlow");
        private static readonly int FogId = Shader.PropertyToID("_DayFog");
        private static readonly int ShadowCoverageId = Shader.PropertyToID("_Coverage");
        private static readonly int ShadowStrengthId = Shader.PropertyToID("_Strength");
        private static readonly int ShadowTimeId = Shader.PropertyToID("_ShapeTime");
        private const int CloudShadowResolution = 256;

        [SerializeField] private DeliverySession session;
        [SerializeField] private Light sun;
        [SerializeField] private DaySkySettings settings;
        [Tooltip("Skybox material (DeliveryRider/Sky); a runtime copy replaces it in the render settings.")]
        [SerializeField] private Material skybox;
        [Tooltip("Hidden/DeliveryRider/CloudShadow; draws the cloud shadow pattern on the sunlight.")]
        [SerializeField] private Shader cloudShadowShader;
        private Material sky, cloudShadowMaterial;
        private RenderTexture cloudShadows;
        private UniversalAdditionalLightData sunData;
        private Vector2 cloudOffset, cloudShadowDrift;
        private float cloudTime, duskElapsed;
        private float shadowStrength = 1f;

        // Developer tools: a timeline position shown instead of the session's (null follows the game) and its playback
        // speed per second. 0-1 is the day from departure to closing, 1-2 the dusk after closing. Neither affects the
        // game clock.
        public float? Preview { get; set; }
        public float PreviewSpeed { get; set; }
        public float ShownDay { get; private set; }
        public float ShownDusk { get; private set; }
        public float ShownTimeline => ShownDay + ShownDusk;
        public DaySkySettings Settings => settings;
        public Light Sun => sun;

        private void Awake()
        {
            // Reimported map scenes may lose scene-object links. Resolve them once, never every frame.
            if (sun == null) sun = GetComponent<Light>();
            if (session == null) session = FindFirstObjectByType<DeliverySession>();
            if (sun == null || session == null || settings == null)
            {
                Debug.LogError("DaySky needs a delivery session, sun and settings.", this);
                enabled = false;
                return;
            }
            RenderSettings.sun = sun;
            if (skybox != null)
            {
                sky = new Material(skybox) { hideFlags = HideFlags.HideAndDontSave };
                RenderSettings.skybox = sky;
            }
            if (sun == null) return;
            shadowStrength = sun.shadowStrength;
            if (cloudShadowShader == null) return;
            cloudShadowMaterial = new Material(cloudShadowShader) { hideFlags = HideFlags.HideAndDontSave };
            cloudShadows = new RenderTexture(CloudShadowResolution, CloudShadowResolution, 0, RenderTextureFormat.R8, RenderTextureReadWrite.Linear)
            {
                name = "CloudShadows", useMipMap = true, autoGenerateMips = true, wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Trilinear, anisoLevel = 4, hideFlags = HideFlags.HideAndDontSave,
            };
            sunData = sun.GetUniversalAdditionalLightData();
        }

        private void OnDestroy()
        {
            // Shader globals outlive Play Mode; edit mode and other scenes go back to the URP fog.
            Shader.SetGlobalVector(FogId, Vector4.zero);
            if (sky != null) Destroy(sky);
            if (cloudShadows == null) return;
            if (sun != null && sun.cookie == cloudShadows) sun.cookie = null;
            Destroy(cloudShadows);
            Destroy(cloudShadowMaterial);
        }

        private void LateUpdate()
        {
            if (session == null || sun == null || settings == null) return;
            // Unscaled, so a preview keeps playing while the game is paused; clouds and dusk follow game time.
            if (Preview.HasValue && PreviewSpeed > 0f) Preview = Mathf.Repeat(Preview.Value + PreviewSpeed * Time.unscaledDeltaTime, 2f);
            cloudOffset += settings.WindDirection * (settings.WindSpeed * Time.deltaTime / CloudMetresPerUnit);
            cloudShadowDrift += settings.WindDirection * (settings.NearCloudSpeed * Time.deltaTime);
            cloudTime += settings.CloudChange * Time.deltaTime;
            if (session.HasDayStarted && session.IsDayClosed) duskElapsed += Time.deltaTime;
            else duskElapsed = 0f;
            if (Preview.HasValue) Apply(Mathf.Min(Preview.Value, 1f), Preview.Value - 1f);
            else Apply(session.HasDayStarted ? (float)session.DayProgress : 0f, duskElapsed / Mathf.Max(1f, settings.DuskSeconds));
        }

        public void Apply(float day, float dusk)
        {
            ShownDay = Mathf.Clamp01(day);
            ShownDusk = Mathf.Clamp01(dusk);
            float blend = Mathf.SmoothStep(0f, 1f, ShownDusk);
            float azimuth = settings.Azimuth(ShownDay);
            float elevation = Mathf.Lerp(settings.Elevation(ShownDay), settings.DuskSunElevation, blend);
            sun.transform.rotation = Quaternion.LookRotation(-Direction(Mathf.Max(elevation, settings.MinimumLightElevation), azimuth));
            var sunColor = settings.SunColor(ShownDay);
            sun.color = Color.Lerp(sunColor, settings.DuskLightColor, blend);
            sun.intensity = Mathf.Lerp(settings.SunIntensity(ShownDay), settings.DuskLightIntensity, blend);
            sun.shadowStrength = Mathf.Lerp(shadowStrength, settings.DuskShadowStrength, blend);
            // Cloud shadows fade with the other shadows as the direct sun goes.
            ShowCloudShadows(settings.CloudShadowStrength * Mathf.Lerp(1f, settings.DuskShadowStrength, blend));
            var horizon = Color.Lerp(settings.FogColor(ShownDay), settings.DuskHorizon, blend);
            RenderSettings.ambientLight = Color.Lerp(settings.AmbientColor(ShownDay), settings.DuskAmbient, blend);
            RenderSettings.fogColor = horizon;
            var zenith = Color.Lerp(settings.ZenithColor(ShownDay), settings.DuskZenith, blend);
            var glow = Color.Lerp(sunColor, settings.DuskGlow, blend);
            // Cloud colours for both the near clouds (Cloud.shader) and the far layer (Sky.shader). After closing the
            // clouds lose the direct sun: dimmer, with the afterglow on their lit side. The shade takes part of the
            // sky's colour and a grey of the same brightness for the rest.
            var cloudLit = Color.Lerp(Color.white, glow, 0.6f) * Mathf.Lerp(1f, 0.6f, blend);
            var skyShade = Color.Lerp(Color.Lerp(zenith, horizon, 0.55f) * 0.95f, zenith * 0.8f, blend);
            float grey = skyShade.grayscale;
            var cloudShade = Color.Lerp(new Color(grey, grey, grey), skyShade, settings.CloudShadeTint);
            // The near clouds are lit from the visible sun like the far ones, not from the light, which stays higher.
            Shader.SetGlobalColor(CloudLitId, ShaderColor(cloudLit));
            Shader.SetGlobalColor(CloudShadeId, ShaderColor(cloudShade));
            var visibleSun = Direction(elevation, azimuth);
            Shader.SetGlobalVector(CloudSunId, new Vector4(visibleSun.x, visibleSun.y, visibleSun.z, blend));
            // Distant near clouds melt into the sky behind them, not only the horizon colour.
            Shader.SetGlobalColor(SkyZenithId, ShaderColor(new Color(zenith.r, zenith.g, zenith.b, 1f)));
            // So does the town (DaySkyFog.hlsl).
            Shader.SetGlobalColor(SkyGlowId, ShaderColor(new Color(glow.r, glow.g, glow.b, settings.FogBlue)));
            float fogSpan = settings.FogEnd - settings.FogStart;
            Shader.SetGlobalVector(FogId, RenderSettings.fog
                ? new Vector4(settings.FogStart, 1f / fogSpan, settings.FogHeightShare, 1f / settings.FogHeightRange)
                : Vector4.zero);
            if (sky == null) return;
            sky.SetColor(ZenithId, zenith);
            sky.SetColor(HorizonId, horizon);
            sky.SetColor(SunColorId, glow);
            sky.SetVector(SunDirectionId, Direction(elevation, azimuth));
            sky.SetFloat(SunSizeId, settings.SunSize);
            sky.SetFloat(DuskId, blend);
            sky.SetFloat(CoverageId, settings.CloudCoverage);
            sky.SetFloat(CloudScaleId, settings.CloudScale);
            sky.SetVector(CloudOffsetId, cloudOffset);
            sky.SetFloat(CloudTimeId, cloudTime);
            sky.SetColor(SkyCloudLitId, cloudLit);
            sky.SetColor(SkyCloudShadeId, cloudShade);
        }

        private void ShowCloudShadows(float strength)
        {
            if (cloudShadows == null) return;
            if (strength <= 0f || settings.CloudCoverage <= 0f)
            {
                if (sun.cookie == cloudShadows) sun.cookie = null;
                return;
            }
            cloudShadowMaterial.SetFloat(ShadowCoverageId, settings.CloudCoverage);
            cloudShadowMaterial.SetFloat(ShadowStrengthId, strength);
            cloudShadowMaterial.SetFloat(ShadowTimeId, cloudTime);
            Graphics.Blit(null, cloudShadows, cloudShadowMaterial);
            sun.cookie = cloudShadows;
            // The tile lies across the sunlight, so the wind's drift over the ground is seen along the light's own axes;
            // the pattern repeats every tile, so the offset wraps there.
            float size = settings.CloudShadowSize;
            var drift = new Vector3(cloudShadowDrift.x, 0f, cloudShadowDrift.y);
            sunData.lightCookieSize = new Vector2(size, size);
            sunData.lightCookieOffset = new Vector2(Mathf.Repeat(Vector3.Dot(drift, sun.transform.right), size),
                Mathf.Repeat(Vector3.Dot(drift, sun.transform.up), size));
        }

        // Global shader colours are passed as they are, unlike material colours (the sky's), which Unity converts to
        // the linear colour space; convert them the same way so the near clouds match the far ones.
        private static Color ShaderColor(Color color) => QualitySettings.activeColorSpace == ColorSpace.Linear ? color.linear : color;

        // Unit vector toward the sun.
        private static Vector3 Direction(float elevation, float azimuth)
        {
            float up = elevation * Mathf.Deg2Rad, around = azimuth * Mathf.Deg2Rad;
            return new Vector3(Mathf.Sin(around) * Mathf.Cos(up), Mathf.Sin(up), Mathf.Cos(around) * Mathf.Cos(up));
        }
    }
}
