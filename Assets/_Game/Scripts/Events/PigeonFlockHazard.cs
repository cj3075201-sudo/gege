using UnityEngine;

namespace DeliveryRider
{
    [RequireComponent(typeof(SphereCollider), typeof(Rigidbody))]
    public sealed class PigeonFlockHazard : MonoBehaviour, IPlacedHazard
    {
        [SerializeField] private Transform[] birds = System.Array.Empty<Transform>();
        [SerializeField] private Transform[] leftWings = System.Array.Empty<Transform>();
        [SerializeField] private Transform[] rightWings = System.Array.Empty<Transform>();
        [SerializeField, Min(0.1f)] private float obscureSeconds = 3f;
        [SerializeField, Min(0f)] private float contactCooldown = 8f;
        [SerializeField, Min(0f)] private float driftDistance = 2.5f;
        [SerializeField, Min(0f)] private float driftSpeed = 0.8f;
        [SerializeField, Min(0.1f)] private float orbitSpeed = 1.4f;
        [SerializeField, Min(0.1f)] private float orbitRadius = 1.45f;
        private DeliverySession session;
        private HazardSlot slot;
        private SphereCollider volume;
        private Rigidbody body;
        private Vector3 home;
        private float elapsed, nextContactAt;
        private bool contactConsumed;

        public void Place(DeliverySession owner, RoadNetwork ground, HazardCandidate spot) => session = owner;

        private void Awake()
        {
            body = GetComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            volume = GetComponent<SphereCollider>();
            volume.isTrigger = true;
            slot = GetComponent<HazardSlot>();
            home = transform.position;
            if (session == null) { enabled = false; return; }
            AnimateBirds();
        }

        private void FixedUpdate()
        {
            if (session.IsPaused || session.IsSettled) return;
            elapsed += Time.fixedDeltaTime;
            body.MovePosition(home + transform.right * (Mathf.Sin(elapsed * driftSpeed) * driftDistance));
            AnimateBirds();
        }

        private void AnimateBirds()
        {
            for (int i = 0; i < birds.Length; i++)
            {
                float phase = elapsed * orbitSpeed * (0.8f + i * 0.07f) + i * Mathf.PI * 0.25f;
                var plane = Quaternion.Euler(25f + i * 23f, i * 47f, i * 31f);
                float radius = orbitRadius * (0.65f + 0.35f * (i % 3) / 2f);
                var radial = plane * new Vector3(Mathf.Cos(phase), Mathf.Sin(phase), 0f);
                var tangent = plane * new Vector3(-Mathf.Sin(phase), Mathf.Cos(phase), 0f);
                birds[i].localPosition = volume.center + radial * radius;
                birds[i].localRotation = UprightFlightRotation(tangent);
                float flap = 20f + Mathf.Sin(elapsed * 12f + i) * 35f;
                if (i < leftWings.Length) leftWings[i].localRotation = Quaternion.Euler(0f, 0f, -flap);
                if (i < rightWings.Length) rightWings[i].localRotation = Quaternion.Euler(0f, 0f, flap);
            }
        }

        public static Quaternion UprightFlightRotation(Vector3 tangent)
        {
            // The orbit can tilt freely, but the bird's back must keep facing the sky.
            var heading = Vector3.ProjectOnPlane(tangent, Vector3.up);
            return Quaternion.LookRotation(heading.sqrMagnitude > 0.0001f ? heading : Vector3.forward, Vector3.up);
        }

        private void OnEnable() { contactConsumed=false; nextContactAt=0f; }

        private bool IsPlayer(Collider other) => session != null && other.attachedRigidbody != null
            && other.attachedRigidbody.transform == session.Player;

        private void OnTriggerEnter(Collider other) => TryContact(other);
        private void OnTriggerStay(Collider other) => TryContact(other);
        private void OnTriggerExit(Collider other) { if (IsPlayer(other)) contactConsumed = false; }

        private void TryContact(Collider other)
        {
            if (!enabled || !IsPlayer(other) || contactConsumed || Time.time < nextContactAt
                || session.IsPaused || session.IsMapOpen || session.IsSettled || slot != null && !slot.StrikeAllowed) return;
            // Consume this encounter even when another flock already obscures the screen: no duration stacking.
            contactConsumed = true;
            nextContactAt = Time.time + contactCooldown;
            session.TryObscureWithPigeons(obscureSeconds);
        }
    }
}
