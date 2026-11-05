using UnityEngine;

namespace DeliveryRider
{
    public sealed partial class TrafficPedestrian
    {
        [SerializeField, Min(0.1f)] private float crowdAcceleration = 30f;
        [SerializeField, Min(0f)] private float crowdStepHeight = 0.3f;
        private bool crowdMember, crowdFan;
        private Transform crowdCenter;
        private Vector3 crowdHome;
        private float crowdPhase, crowdBaseMass, crowdMassMultiplier = 1f;
        private CelebrityCrowdHazard crowdContact;
        private float nextCrowdSlowAt = float.NegativeInfinity;
        private float crowdPushUntil = float.NegativeInfinity;
        public bool IsCrowdFan => crowdMember && crowdFan;
        public float BodyResistance { get; private set; } = 1f;

        // Only collision, tumble and get-up reactions are shared with sidewalk pedestrians.
        public void InitializeCrowd(PedestrianNetwork owner, int route, float along, int seed, Material material,
            Transform center, Vector3 spawnPosition, bool fan, float speedMultiplier, float resistance, float massMultiplier = 1f)
        {
            InitializeState(owner, route, along, seed, material);
            crowdMember = true;
            crowdFan = fan;
            crowdCenter = center;
            crowdHome = spawnPosition;
            crowdPhase = (seed % 1000) * 0.01f;
            crowdBaseMass = body.mass;
            crowdMassMultiplier = Mathf.Max(0.1f, massMultiplier);
            speed *= speedMultiplier;
            BodyResistance = Mathf.Max(1f, resistance);
            TeleportCrowd(spawnPosition);
            SetCrowdWalking();
        }

        public void ConfigureCrowdContact(CelebrityCrowdHazard settings) => crowdContact = settings;

        // Rider sweep handles fans separately; ordinary walkers keep their existing knock/resistance rule.
        public bool TryCrowdContact(RiderMotor rider, Vector3 velocity)
        {
            if (!IsCrowdFan || crowdContact == null) return false;
            if (!CanReact || rider.IsRecovering) return true;
            if (Time.time >= nextCrowdSlowAt)
            {
                nextCrowdSlowAt = Time.time + crowdContact.FanContactCooldown;
                rider.ApplyCrowdContact(crowdContact.FanContactSpeedShare,
                    crowdContact.RiderMinimumProgressSpeed, crowdContact.FanContactCooldown);
            }
            if (TryBodyKnock(velocity)) return true;
            var direction = Vector3.ProjectOnPlane(velocity, Vector3.up).normalized;
            if (direction.sqrMagnitude < 0.01f) return true;
            // Yield physically while being pushed instead of driving back into the rider every step.
            // A small sideways component opens a gap; collisions with walls remain solid.
            var side = Vector3.Cross(Vector3.up, direction);
            float sign = Vector3.Dot(body.position - rider.transform.position, side) >= 0f ? 1f : -1f;
            float progress = Mathf.Max(crowdContact.RiderMinimumProgressSpeed, rider.HorizontalSpeed);
            var desired = direction * progress + side * (sign * crowdContact.RiderMinimumProgressSpeed);
            var current = body.linearVelocity;
            body.linearVelocity = new Vector3(desired.x, current.y, desired.z);
            crowdPushUntil = Time.time + crowdContact.FanContactCooldown;
            return true;
        }

        public void SetCrowdCenter(Transform center) => crowdCenter = center;

        private void SetCrowdWalking()
        {
            if (!crowdFan) return;
            body.isKinematic = false;
            body.useGravity = true;
            body.mass = crowdBaseMass * crowdMassMultiplier;
            body.constraints = RigidbodyConstraints.FreezeRotation;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            body.WakeUp();
        }

        private bool UpdateCrowdGoal()
        {
            if (!crowdFan || crowdCenter == null) return true;
            if (Time.time < crowdPushUntil) { FaceCrowdCenter(); return true; }
            var away = Vector3.ProjectOnPlane(body.position - crowdCenter.position, Vector3.up);
            float radius = away.magnitude;
            var outward = radius > 0.001f ? away / radius
                : Vector3.ProjectOnPlane(crowdHome - crowdCenter.position, Vector3.up).normalized;
            if (outward.sqrMagnitude < 0.001f) outward = -transform.forward;
            float error = radius - CelebrityCrowdHazard.FanDistance;
            float targetSpeed = Mathf.Abs(error) > 0.03f
                ? Mathf.Clamp(error / Time.fixedDeltaTime, -speed, speed) : 0f;
            var desired = -outward * targetSpeed;
            var current = Vector3.ProjectOnPlane(body.linearVelocity, Vector3.up);
            // Keep pushing toward the celebrity even during a person contact. The physics solver
            // supplies blocking and sliding; do not cancel the intention or overwrite lateral contact motion.
            var acceleration = Vector3.ClampMagnitude((desired - current) / Time.fixedDeltaTime, crowdAcceleration);
            body.AddForce(acceleration, ForceMode.Acceleration);
            TryCrowdStep(desired * Time.fixedDeltaTime);
            FaceCrowdCenter();
            return true;
        }

        private void TryCrowdStep(Vector3 step)
        {
            if (step.sqrMagnitude < 0.000001f || body.linearVelocity.y > 1f) return;
            var from = body.position;
            var next = from + step;
            float height = GroundHeight(next, from.y);
            float rise = height - from.y;
            if (rise <= 0.02f || rise > crowdStepHeight) return;
            // Step over a low kerb only. A person never cancels the forward force above.
            int count = Physics.CapsuleCastNonAlloc(from + Vector3.up * 0.65f, from + Vector3.up * 1.45f,
                0.28f, step.normalized, hits, step.magnitude + 0.02f, obstacleMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
                if (hits[i].rigidbody == null && hits[i].normal.y < 0.7f) return;
            if (count < hits.Length) body.position = new Vector3(from.x, height + 0.01f, from.z);
        }

        private void TeleportCrowd(Vector3 position)
        {
            var facing = transform.rotation;
            if (crowdFan && crowdCenter != null)
            {
                var toward = Vector3.ProjectOnPlane(crowdCenter.position - position, Vector3.up);
                if (toward.sqrMagnitude > 0.001f) facing = Quaternion.LookRotation(toward);
            }
            // Set matching Transform/Rigidbody poses so transform synchronization cannot restore
            // the ordinary pedestrian's route position after a rotation update.
            body.interpolation = RigidbodyInterpolation.None;
            transform.SetPositionAndRotation(position, facing);
            body.position = position;
            body.rotation = facing;
            body.interpolation = RigidbodyInterpolation.Interpolate;
        }

        private void FaceCrowdCenter()
        {
            if (crowdCenter == null) return;
            var look = Vector3.ProjectOnPlane(crowdCenter.position - body.position, Vector3.up);
            if (look.sqrMagnitude < 0.001f) return;
            body.MoveRotation(Quaternion.Slerp(body.rotation, Quaternion.LookRotation(look), turnSpeed * Time.fixedDeltaTime));
        }

        // Get up where a fan lands, then resume crowd movement without returning to a sidewalk route.
        private void BeginCrowdReturn(bool snap)
        {
            body.linearVelocity = body.angularVelocity = Vector3.zero;
            SetWalking();
            if (snap) TeleportCrowd(crowdHome);
            else body.position = new Vector3(body.position.x, GroundHeight(body.position, body.position.y), body.position.z);
            returning = true;
            getUpStartedAt = Time.time;
            getUpFrom = pose;
            blocked = false;
        }
    }
}
