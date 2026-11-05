using UnityEngine;

namespace DeliveryRider
{
    // Hydrant behaves like a manhole (bursts on contact) with its own launch numbers set per instance.
    public enum GroundHazardKind { Manhole, Melon, Hydrant }

    [RequireComponent(typeof(BoxCollider))]
    public sealed class GroundContactHazard : MonoBehaviour, IPlacedHazard
    {
        private enum Phase { Ready, Active, Spent }
        [SerializeField] private DeliverySession session;
        [SerializeField] private GroundHazardKind kind;
        [SerializeField] private Renderer warningLamp;
        [SerializeField] private GameObject effect;
        [Tooltip("How long the burst effect stays visible after contact.")]
        [SerializeField, Min(0.1f)] private float activeSeconds = 0.6f;
        [SerializeField, Min(0)] private float forwardImpulse = 6;
        [SerializeField, Min(0)] private float upwardImpulse = 4.5f;
        [Tooltip("Manhole only: rider speed share added to forwardImpulse, thrown along the rider's heading.")]
        [SerializeField, Min(0)] private float riderSpeedShare;
        [SerializeField, Min(0.1f)] private float slipSeconds = 2.2f;
        [SerializeField, Min(0f)] private float objectRearmSeconds = 10f;
        [SerializeField, Min(0.1f)] private float landingRadius = 2.6f;
        private BoxCollider volume;
        private CapsuleCollider playerShape;
        private RiderMotor rider;
        private Phase phase;
        private float elapsed;
        private float nextArmedAt;
        private bool contactConsumed;
        private HazardSlot slot;
        private bool CanStrike => slot == null || slot.StrikeAllowed;

        public void Place(DeliverySession owner, RoadNetwork ground, HazardCandidate spot) => session = owner;

        private void Awake()
        {
            volume = GetComponent<BoxCollider>();
            slot = GetComponent<HazardSlot>();
            if (session == null || session.Player == null)
            { Debug.LogError("Ground hazard requires a delivery session.", this); enabled = false; return; }
            rider = session.Player.GetComponent<RiderMotor>();
            playerShape = session.Player.GetComponent<CapsuleCollider>();
            if (rider == null || playerShape == null) { enabled = false; return; }
            volume.isTrigger = true;
            ClearEffects();
        }

        // Settlement stops FixedUpdate via timeScale; teardown must still run.
        private void Update()
        {
            if (session != null && (session.IsSettled || session.IsMapOpen && phase != Phase.Ready && phase != Phase.Spent)) Finish();
        }
        private void OnDisable() { ClearEffects(); if (rider != null) rider.CancelSlip(this); phase = Phase.Spent; elapsed = 0; }
        private void FixedUpdate()
        {
            if (session.IsPaused || session.IsSettled) return;
            elapsed += Time.fixedDeltaTime;
            switch (phase)
            {
                case Phase.Ready:
                    if (!CanStrike) break;
                    TryContact();
                    break;
                case Phase.Active:
                    if (elapsed >= activeSeconds) Finish();
                    break;
                case Phase.Spent:
                    // Rearm after the HTML cooldown, once the rider has left the contact volume.
                    if (Time.time >= nextArmedAt && !Touching()) Enter(Phase.Ready);
                    break;
            }
        }
        private bool Touching()
        {
            if (Physics.ComputePenetration(volume, transform.position, transform.rotation, playerShape,
                playerShape.transform.position, playerShape.transform.rotation, out _, out _)) return true;
            var delta = playerShape.bounds.min.y - volume.bounds.max.y;
            var planar = Vector3.ProjectOnPlane(rider.transform.position - transform.position, Vector3.up);
            float scale = slot != null ? slot.TriggerScale : 1f;
            return rider.Velocity.y < -0.1f && delta >= -0.2f && delta <= 0.5f && planar.sqrMagnitude <= landingRadius * landingRadius * scale * scale;
        }
        private void TryContact()
        {
            if (contactConsumed || session.IsMapOpen || kind == GroundHazardKind.Melon && !rider.IsGroundedForHazards) return;
            if (!Touching()) return;
            contactConsumed = true;
            nextArmedAt = Time.time + objectRearmSeconds;
            // Consume even rejected contact: this activation cannot hit repeatedly later.
            if (kind == GroundHazardKind.Melon) { rider.TryHazardSlip(this, transform.forward, slipSeconds); Finish(); return; }
            // No warning: at running speed the rider would be past before a timed burst, so it erupts on touch.
            Enter(Phase.Active);
            if (warningLamp != null) warningLamp.enabled = true;
            if (effect != null) effect.SetActive(true);
            rider.TryHazardCrash(this, LaunchHorizontal() + transform.up * upwardImpulse, 0.6f);
        }
        // A manhole throws the rider onward the way they were heading; a standing rider goes along the road.
        private Vector3 LaunchHorizontal()
        {
            if (kind != GroundHazardKind.Manhole) return transform.forward * forwardImpulse;
            var heading = Vector3.ProjectOnPlane(rider.Velocity, Vector3.up);
            float speed = heading.magnitude;
            var direction = speed > 0.5f ? heading / speed : transform.forward;
            return direction * (forwardImpulse + speed * riderSpeedShare);
        }
        private void Enter(Phase value) { phase = value; elapsed = 0; if (value == Phase.Ready) contactConsumed = false; }
        private void Finish() { if (phase != Phase.Spent) Enter(Phase.Spent); ClearEffects(); }
        private void ClearEffects() { if (warningLamp != null) warningLamp.enabled = false; if (effect != null) effect.SetActive(false); }
    }
}
