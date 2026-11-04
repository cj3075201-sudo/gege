using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace DeliveryRider
{
    // Little dust clouds behind the rider's feet: small cloud meshes (CloudShapes) on the cloud material, so each one
    // swells up opaque, rises a little and sinks away again. Faster means bigger and closer together. They are spaced
    // by distance on the ground because the rider has no walk cycle or foot events yet. Each puff replaces the sky
    // clouds' shared colours with the ground's light: those go dark at dusk, while the street stays readable.
    public sealed class RiderDust : MonoBehaviour
    {
        private static readonly int FadeId = Shader.PropertyToID("_Fade");
        private static readonly int SeedId = Shader.PropertyToID("_Seed");
        private static readonly int TintId = Shader.PropertyToID("_Tint");
        private static readonly int LitId = Shader.PropertyToID("_DayCloudLit");
        private static readonly int ShadeId = Shader.PropertyToID("_DayCloudShade");

        [SerializeField] private RiderMotor rider;
        [SerializeField] private Mesh[] shapes = System.Array.Empty<Mesh>();
        [SerializeField] private Material material;
        [Header("Dust (test values)")]
        [Tooltip("Slower than this (m/s) raises no dust.")]
        [SerializeField, Min(0f)] private float minimumSpeed = 1.5f;
        [Tooltip("At this speed (m/s) the dust is at its biggest and closest together. Inline skates reach about 16.")]
        [SerializeField, Min(0.1f)] private float fullSpeed = 16f;
        [Tooltip("Metres between puffs: x at the minimum speed, y at full speed.")]
        [SerializeField] private Vector2 spacing = new Vector2(2.2f, 1f);
        [Tooltip("Width of a puff in metres: x at the minimum speed, y at full speed.")]
        [SerializeField] private Vector2 size = new Vector2(0.35f, 0.8f);
        [Tooltip("Seconds a puff lives.")]
        [SerializeField, Min(0.1f)] private float lifetime = 0.5f;
        [Tooltip("Share of its life a puff spends swelling up; the rest it sinks away.")]
        [SerializeField, Range(0.05f, 0.9f)] private float swellShare = 0.35f;
        [Tooltip("How much a puff widens over its life (0.4 is 40%).")]
        [SerializeField, Range(0f, 2f)] private float widening = 0.4f;
        [Tooltip("Metres per second a puff rises.")]
        [SerializeField, Min(0f)] private float rise = 0.5f;
        [Tooltip("Metres per second a puff may drift sideways, at random.")]
        [SerializeField, Min(0f)] private float spread = 0.25f;
        [Tooltip("Metres to each side of the rider's middle; the puffs alternate left and right.")]
        [SerializeField, Min(0f)] private float footSpacing = 0.18f;
        [Tooltip("Puffs alive at once; at the limit a new one is skipped.")]
        [SerializeField, Range(1, 24)] private int maximumPuffs = 12;
        [Tooltip("Dust colour, lit like the ground.")]
        [SerializeField, Range(0f, 1f)] private float dustRed = 0.8f;
        [SerializeField, Range(0f, 1f)] private float dustGreen = 0.74f;
        [SerializeField, Range(0f, 1f)] private float dustBlue = 0.65f;

        private sealed class Puff
        {
            public Transform transform;
            public MeshFilter filter;
            public MeshRenderer renderer;
            public Vector3 velocity;
            public float age, width, seed;
        }

        private readonly List<Puff> puffs = new List<Puff>();
        private MaterialPropertyBlock block;
        private CapsuleCollider capsule;
        private float travelled;
        private float side = 1f;

        private void Start()
        {
            if (rider == null || material == null || shapes.Length == 0) { enabled = false; return; }
            capsule = rider.GetComponent<CapsuleCollider>();
            block = new MaterialPropertyBlock();
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            Advance(dt);
            float speed = rider.HorizontalSpeed;
            if (!rider.IsGroundedForHazards || rider.ReactionState != RiderReactionState.Normal || speed < minimumSpeed) return;
            float strength = Mathf.InverseLerp(minimumSpeed, fullSpeed, speed);
            float gap = Mathf.Max(0.1f, Mathf.Lerp(spacing.x, spacing.y, strength));
            travelled += speed * dt;
            if (travelled < gap) return;
            travelled %= gap;
            side = -side;
            var body = rider.transform;
            var middle = body.position;
            Emit(new Vector3(middle.x, capsule.bounds.min.y, middle.z) + body.right * (footSpacing * side), strength);
        }

        private void Advance(float dt)
        {
            foreach (var puff in puffs)
            {
                if (!puff.renderer.enabled) continue;
                puff.age += dt;
                float life = puff.age / lifetime;
                if (life >= 1f) { puff.renderer.enabled = false; continue; }
                puff.transform.position += puff.velocity * dt;
                puff.transform.localScale = Vector3.one * (puff.width * (1f + widening * life));
                Show(puff, life < swellShare ? life / swellShare : (1f - life) / (1f - swellShare));
            }
        }

        private void Emit(Vector3 feet, float strength)
        {
            Puff puff = null;
            foreach (var candidate in puffs) if (!candidate.renderer.enabled) { puff = candidate; break; }
            if (puff == null)
            {
                if (puffs.Count >= maximumPuffs) return;
                puff = Add();
            }
            var drift = Random.insideUnitCircle * spread;
            puff.velocity = new Vector3(drift.x, rise, drift.y);
            puff.age = 0f;
            puff.width = Mathf.Lerp(size.x, size.y, strength) * Random.Range(0.85f, 1.15f);
            puff.seed = Random.Range(0f, 100f);
            puff.filter.sharedMesh = shapes[Random.Range(0, shapes.Length)];
            puff.transform.SetPositionAndRotation(feet, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));
            puff.transform.localScale = Vector3.one * puff.width;
            puff.renderer.enabled = true;
            Show(puff, 0f);
        }

        private Puff Add()
        {
            var go = new GameObject("DustPuff", typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(transform, false);
            var puff = new Puff { transform = go.transform, filter = go.GetComponent<MeshFilter>(), renderer = go.GetComponent<MeshRenderer>() };
            puff.renderer.sharedMaterial = material;
            puff.renderer.shadowCastingMode = ShadowCastingMode.Off;
            puff.renderer.receiveShadows = false;
            puff.renderer.lightProbeUsage = LightProbeUsage.Off;
            puffs.Add(puff);
            return puff;
        }

        private void Show(Puff puff, float fade)
        {
            block.SetFloat(FadeId, fade);
            block.SetFloat(SeedId, puff.seed);
            block.SetColor(TintId, new Color(dustRed, dustGreen, dustBlue));
            // Linear, as the shader takes them; SetVector passes them on unconverted.
            var ambient = RenderSettings.ambientLight.linear;
            var sun = RenderSettings.sun;
            var direct = sun != null ? sun.color.linear * sun.intensity : Color.black;
            block.SetVector(ShadeId, ambient);
            block.SetVector(LitId, ambient + direct);
            puff.renderer.SetPropertyBlock(block);
        }
    }
}
