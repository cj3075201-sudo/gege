using System;
using System.Collections.Generic;
using UnityEngine;

namespace DeliveryRider
{
    // Shares DaySky's displayed timeline, including the F1 preview. All material changes use private copies.
    [DefaultExecutionOrder(100)]
    public sealed class NeighborhoodEveningLights : MonoBehaviour
    {
        [Serializable] public struct Surface { public Renderer renderer; public int slot; public int group; }
        [SerializeField] private DaySky daySky;
        [SerializeField] private Material windowMaterial;
        [SerializeField] private Material lampMaterial;
        [SerializeField] private Surface[] windows = Array.Empty<Surface>();
        [SerializeField] private Surface[] lamps = Array.Empty<Surface>();
        [SerializeField] private Light[] streetLights = Array.Empty<Light>();
        [SerializeField, Range(0f, 1f)] private float lightsBegin = .72f;
        [SerializeField, Range(.01f, .3f)] private float fadeDuration = .14f;
        [SerializeField, Range(0f, .08f)] private float windowStagger = .035f;
        [SerializeField] private Color windowWarm = new Color(1f, .70f, .36f);
        [SerializeField] private Color lampWarm = new Color(1f, .83f, .60f);
        [SerializeField, Min(0f)] private float windowEmission = 1.35f;
        [SerializeField, Min(0f)] private float lampEmission = 2.2f;
        [SerializeField, Min(0f)] private float streetIntensity = 2.2f;
        [SerializeField, Min(1)] private int nearbyLightLimit = 8;
        [SerializeField, Min(1f)] private float lightDistance = 32f;
        private static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
        private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");
        private readonly Dictionary<Renderer, Material[]> originals = new Dictionary<Renderer, Material[]>();
        private Material[] windowCopies;
        private Material lampCopy;
        private Camera viewer;
        private bool[] chosen;
        private float shown = float.NaN, nextLightUpdate;

        private void Awake()
        {
            if (daySky == null) daySky = FindFirstObjectByType<DaySky>();
            viewer = Camera.main;
            if (daySky == null) { Debug.LogError("Neighborhood lighting needs DaySky.", this); enabled = false; }
        }
        private void OnEnable() { if (Application.isPlaying) Initialize(); }
        private void OnDisable() => Restore();
        private void OnDestroy() => Restore();
        private bool Initialize()
        {
            if (windowCopies != null) return true;
            if (windowMaterial == null || lampMaterial == null) return false;
            windowCopies = new Material[4];
            for (int i = 0; i < 4; i++) windowCopies[i] = new Material(windowMaterial) { name = "Evening Windows " + i, hideFlags = HideFlags.HideAndDontSave };
            lampCopy = new Material(lampMaterial) { name = "Evening Lamps", hideFlags = HideFlags.HideAndDontSave };
            chosen = new bool[streetLights.Length];
            foreach (var surface in windows) Bind(surface, windowCopies[Mathf.Clamp(surface.group, 0, 3)]);
            foreach (var surface in lamps) Bind(surface, lampCopy);
            shown = float.NaN;
            return true;
        }
        private void Bind(Surface surface, Material material)
        {
            if (surface.renderer == null) return;
            var materials = surface.renderer.sharedMaterials;
            if (surface.slot < 0 || surface.slot >= materials.Length) return;
            if (!originals.ContainsKey(surface.renderer)) originals.Add(surface.renderer, materials);
            materials = (Material[])materials.Clone();
            materials[surface.slot] = material;
            surface.renderer.sharedMaterials = materials;
        }
        private float Blend(float timeline, int group) => Mathf.SmoothStep(0f, 1f,
            Mathf.InverseLerp(lightsBegin + group * windowStagger, lightsBegin + group * windowStagger + fadeDuration, timeline));
        private void LateUpdate()
        {
            if (daySky == null || !Initialize()) return;
            float timeline = daySky.ShownTimeline;
            if (float.IsNaN(shown) || Mathf.Abs(timeline - shown) > .001f) ApplyMaterials(timeline);
            if (Time.unscaledTime >= nextLightUpdate)
            {
                UpdateNearbyLights(timeline, viewer != null ? viewer.transform.position : transform.position);
                nextLightUpdate = Time.unscaledTime + .25f;
            }
        }
        private void ApplyMaterials(float timeline)
        {
            for (int i = 0; i < 4; i++)
            {
                float blend = Blend(timeline, i);
                windowCopies[i].SetColor(BaseColor, Color.Lerp(windowMaterial.GetColor(BaseColor), windowWarm * .65f, blend));
                windowCopies[i].SetColor(EmissionColor, windowWarm * (windowEmission * blend));
            }
            float lampsOn = Blend(timeline, 0);
            lampCopy.SetColor(BaseColor, Color.Lerp(lampMaterial.GetColor(BaseColor), lampWarm, lampsOn));
            lampCopy.SetColor(EmissionColor, lampWarm * (lampEmission * lampsOn));
            shown = timeline;
        }
        private void UpdateNearbyLights(float timeline, Vector3 viewPosition)
        {
            Array.Clear(chosen, 0, chosen.Length);
            float brightness = Blend(timeline, 0);
            if (brightness > .01f)
                for (int rank = 0; rank < nearbyLightLimit; rank++)
                {
                    int best = -1; float distance = lightDistance * lightDistance;
                    for (int i = 0; i < streetLights.Length; i++)
                    {
                        if (chosen[i] || streetLights[i] == null) continue;
                        float d = (streetLights[i].transform.position - viewPosition).sqrMagnitude;
                        if (d < distance) { best = i; distance = d; }
                    }
                    if (best < 0) break;
                    chosen[best] = true;
                }
            for (int i = 0; i < streetLights.Length; i++) if (streetLights[i] != null)
            {
                streetLights[i].intensity = streetIntensity * brightness;
                streetLights[i].enabled = chosen[i];
            }
        }
        // Editor previews use the same appearance code without entering Play Mode or changing the clock.
        public void PreviewAt(float timeline, Vector3 viewPosition)
        {
            if (!Initialize()) return;
            ApplyMaterials(timeline);
            UpdateNearbyLights(timeline, viewPosition);
        }
        public void Restore()
        {
            foreach (var pair in originals) if (pair.Key != null) pair.Key.sharedMaterials = pair.Value;
            originals.Clear();
            foreach (var light in streetLights) if (light != null) { light.enabled = false; light.intensity = 0f; }
            if (windowCopies != null) foreach (var material in windowCopies) Release(material);
            Release(lampCopy);
            windowCopies = null; lampCopy = null; chosen = null; shown = float.NaN;
        }
        private static void Release(UnityEngine.Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) Destroy(value); else DestroyImmediate(value);
        }
    }
}
