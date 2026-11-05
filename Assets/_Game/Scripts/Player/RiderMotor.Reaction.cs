using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DeliveryRider
{
    public enum RiderReactionState { Normal, Airborne, Sliding, Rolling, GettingUp }

    public sealed partial class RiderMotor
    {
        [Header("Accident movement (test values)")]
        [SerializeField, Min(0f)] private float airSteering = 5f;
        [SerializeField, Min(0f)] private float airInputSpeedLimit = 10f;
        [SerializeField, Range(0f, 1f)] private float wallReflection = 0.5f;
        [SerializeField, Range(0f, 1f)] private float floorReflection = 0.35f;
        [Tooltip("Horizontal speed kept after each floor bounce.")]
        [SerializeField, Range(0f, 1f)] private float floorFriction = 0.78f;
        [SerializeField, Min(0f)] private float minimumBounceSpeed = 5f;
        [SerializeField, Min(0)] private int maximumFloorBounces = 2;
        [SerializeField, Min(0.01f)] private float contactInterval = 0.12f;
        [SerializeField, Min(0f)] private float rollDeceleration = 8f;
        [SerializeField, Min(0f)] private float getUpSpeed = 1.5f;
        [SerializeField, Min(0f)] private float stableGroundSeconds = 0.15f;
        [SerializeField, Min(0.01f)] private float getUpSeconds = 0.45f;
        [SerializeField, Min(1f)] private float accidentTimeout = 8f;
        [SerializeField, Min(0.1f)] private float stuckSeconds = 2f;
        [Header("Rolling friction (test values)")]
        [SerializeField, Range(0f, 1f)] private float rollingStaticFriction = 0.6f;
        [SerializeField, Range(0f, 1f)] private float rollingDynamicFriction = 0.6f;
        private PhysicsMaterial normalCollisionMaterial, rollingCollisionMaterial, originalCollisionMaterial;
        public RiderReactionState ReactionState { get; private set; }
        public bool IsAccidentAirborne => ReactionState == RiderReactionState.Airborne;
        public float GetUpDuration => getUpSeconds;
        public bool IsReactionGrounded { get; private set; }
        public Vector3 ReactionGroundNormal { get; private set; } = Vector3.up;
        public Vector3 ReactionGroundPoint { get; private set; }
        public event Action<RiderReactionState> ReactionChanged;
        // World-space outgoing velocity, usable by a future ragdoll presenter.
        public event Action<Vector3> ReactionImpulse;
        private InputAction recover;
        private RiderSafeRecovery safeRecovery;
        private Vector2 airMovement;
        private Vector3 collisionVelocity, pendingNormal, pendingVelocity, lastProgressPosition;
        private float pendingStrength, nextContactAt, reactionStartedAt, stableGroundTime, getUpStartedAt, noProgressTime;
        private int floorBounces;
        private bool physicalTumble;
        private float supportContactAt = float.NegativeInfinity;
        private Vector3 supportContactNormal, supportContactPoint;
        private Collider supportCollider;

        private void InitializeCollisionMaterials()
        {
            originalCollisionMaterial = capsule.sharedMaterial;
            normalCollisionMaterial = new PhysicsMaterial("Rider Normal (No Friction)")
            {
                hideFlags = HideFlags.DontSave,
                staticFriction = 0f,
                dynamicFriction = 0f,
                frictionCombine = PhysicsMaterialCombine.Minimum,
                bounciness = 0f,
                bounceCombine = PhysicsMaterialCombine.Minimum
            };
            rollingCollisionMaterial = new PhysicsMaterial("Rider Rolling (Friction)")
            {
                hideFlags = HideFlags.DontSave,
                staticFriction = rollingStaticFriction,
                dynamicFriction = rollingDynamicFriction,
                frictionCombine = PhysicsMaterialCombine.Average,
                bounciness = 0f,
                bounceCombine = PhysicsMaterialCombine.Minimum
            };
            UpdateCollisionMaterial();
        }

        private void UpdateCollisionMaterial()
        {
            if (capsule == null || normalCollisionMaterial == null) return;
            // Sliding hazards and airborne reactions must not regain wall friction.
            var material = ReactionState == RiderReactionState.Rolling || ReactionState == RiderReactionState.GettingUp
                ? rollingCollisionMaterial : normalCollisionMaterial;
            if (capsule.sharedMaterial != material) capsule.sharedMaterial = material;
        }

        private void SetReactionState(RiderReactionState state)
        {
            if (state != RiderReactionState.Rolling && state != RiderReactionState.GettingUp) hazardStunned = false;
            if (state != RiderReactionState.Normal) ClearChickenAttachments();
            trafficRecoveryActive = state == RiderReactionState.Airborne || state == RiderReactionState.Rolling || state == RiderReactionState.GettingUp;
            if (ReactionState == state) return;
            ReactionState = state;
            UpdateCollisionMaterial();
            if (state == RiderReactionState.Normal) physicalTumble = false;
            stableGroundTime = 0f;
            if (state == RiderReactionState.GettingUp) getUpStartedAt = Time.time;
            ReactionChanged?.Invoke(state);
        }

        private void BeginFlight(Vector3 velocity)
        {
            normalJumpActive = false;
            jumpQueued = false;
            reactionStartedAt = Time.time;
            noProgressTime = 0f;
            lastProgressPosition = body.position;
            floorBounces = 0;
            supportContactAt = float.NegativeInfinity;
            IsReactionGrounded = false;
            pendingStrength = 0f;
            collisionVelocity = velocity;
            nextContactAt = Time.time + Time.fixedDeltaTime;
            SetReactionState(RiderReactionState.Airborne);
            ReactionImpulse?.Invoke(velocity);
        }

        private void ResetReaction()
        {
            ClearChickenAttachments();
            CancelZoneHop();
            slipOwner = null;
            airMovement = Vector2.zero;
            jumpQueued = false;
            normalJumpActive = false;
            pendingStrength = noProgressTime = 0f;
            supportContactAt = float.NegativeInfinity;
            IsReactionGrounded = false;
            SetReactionState(RiderReactionState.Normal);
        }

        private void RestoreSafePosition()
        {
            CancelZoneHop();
            Vector3 destination = spawn;
            if (safeRecovery != null) destination = safeRecovery.FindSafePosition(body.position, spawn, OpenRoads);
            body.position = destination;
            body.linearVelocity = body.angularVelocity = Vector3.zero;
            spawn = destination;
            ResetReaction();
            nextTrafficHitAt = Time.time + 1f;
            SafePositionRestored?.Invoke();
        }

        private bool TickReaction()
        {
            float dt = Time.fixedDeltaTime;
            if (zoneHopping) { AdvanceZoneHop(); return true; }
            if (TickHazardStun()) return true;
            if (ReactionState == RiderReactionState.Sliding && !IsSlipping) SetReactionState(RiderReactionState.Normal);
            ApplyPendingContact();
            IsReactionGrounded = ReactionState != RiderReactionState.Normal && TryReactionSupport();
            if (ReactionState != RiderReactionState.Normal) groundedForHazards = IsReactionGrounded;
            if (!IsRecovering)
            {
                collisionVelocity = body.linearVelocity;
                return false;
            }
            if ((body.position - lastProgressPosition).sqrMagnitude >= 0.04f)
            {
                lastProgressPosition = body.position;
                noProgressTime = 0f;
            }
            else noProgressTime += dt;
            // Road membership is a save/recovery destination rule, not a get-up prerequisite.
            // Never teleport a character that has already reached walkable support.
            if (!IsReactionGrounded && (Time.time - reactionStartedAt >= accidentTimeout
                || noProgressTime >= stuckSeconds && body.linearVelocity.magnitude <= getUpSpeed))
            {
                RestoreSafePosition();
                return true;
            }
            switch (ReactionState)
            {
                case RiderReactionState.Airborne:
                    if (physicalTumble)
                    {
                        if (groundedForHazards) SetReactionState(RiderReactionState.Rolling);
                        break;
                    }
                    if (groundedForHazards && Time.time >= trafficRecoveryUntil && Time.time >= nextContactAt)
                        SetReactionState(RiderReactionState.Rolling);
                    else
                    {
                        var horizontal = Vector3.ProjectOnPlane(body.linearVelocity, Vector3.up);
                        var direction = Vector3.ProjectOnPlane(view.forward, Vector3.up).normalized * airMovement.y
                            + Vector3.ProjectOnPlane(view.right, Vector3.up).normalized * airMovement.x;
                        var controlled = horizontal + direction * (airSteering * dt);
                        controlled = Vector3.ClampMagnitude(controlled, Mathf.Max(airInputSpeedLimit, horizontal.magnitude));
                        body.linearVelocity = controlled + Vector3.up * body.linearVelocity.y;
                    }
                    break;
                case RiderReactionState.Rolling:
                    if (!groundedForHazards) { SetReactionState(RiderReactionState.Airborne); break; }
                    if (IsReactionGrounded)
                    {
                        var tangent = Vector3.MoveTowards(Vector3.ProjectOnPlane(body.linearVelocity, ReactionGroundNormal), Vector3.zero, rollDeceleration * dt);
                        // Only a rider coming to rest is checked: flying over a closed zone is allowed.
                        if (stableGroundTime == 0f && tangent.magnitude <= getUpSpeed && TryBeginZoneHop()) break;
                        body.linearVelocity = tangent;
                        stableGroundTime = tangent.magnitude <= getUpSpeed ? stableGroundTime + dt : 0f;
                        if (stableGroundTime >= stableGroundSeconds && (!physicalTumble || Time.time >= trafficRecoveryUntil)) SetReactionState(RiderReactionState.GettingUp);
                    }
                    break;
                case RiderReactionState.GettingUp:
                    if (!groundedForHazards) { SetReactionState(RiderReactionState.Airborne); break; }
                    body.linearVelocity = Vector3.zero;
                    if (Time.time >= getUpStartedAt + getUpSeconds)
                    {
                        nextTrafficHitAt = Mathf.Max(nextTrafficHitAt, Time.time + 1f);
                        if (SpawnAllowedHere) spawn = body.position;
                        SetReactionState(RiderReactionState.Normal);
                    }
                    break;
            }
            collisionVelocity = body.linearVelocity;
            return true;
        }

        private void OnCollisionEnter(Collision collision) => GatherContact(collision);
        private void OnCollisionStay(Collision collision) => GatherContact(collision);
        private void GatherContact(Collision collision)
        {
            // Keep actual supporting contacts even when no new impact is accepted.
            // A narrow proximity cast can miss support on a slope or at a collider seam.
            if (collision.rigidbody == null)
                for (int i = 0; i < collision.contactCount; i++)
                {
                    var contact = collision.GetContact(i);
                    if (contact.normal.y < Mathf.Cos(maximumSlope * Mathf.Deg2Rad)) continue;
                    supportContactAt = Time.fixedTime;
                    supportContactNormal = contact.normal;
                    supportContactPoint = contact.point;
                    supportCollider = collision.collider;
                    break;
                }
            if (physicalTumble || ReactionState != RiderReactionState.Airborne && ReactionState != RiderReactionState.Sliding) return;
            if (Time.time < nextContactAt) return;
            var incoming = collisionVelocity;
            // Gravity acts after FixedUpdate, so include this step for falling contacts.
            if (ReactionState == RiderReactionState.Airborne) incoming += Physics.gravity * Time.fixedDeltaTime;
            for (int i = 0; i < collision.contactCount; i++)
            {
                var normal = collision.GetContact(i).normal;
                float strength = -Vector3.Dot(incoming, normal);
                if (strength <= pendingStrength || strength < 1f) continue;
                pendingStrength = strength;
                pendingNormal = normal;
                pendingVelocity = incoming;
            }
        }

        private bool TryReactionSupport()
        {
            if (supportCollider != null && supportCollider.enabled && supportCollider.gameObject.activeInHierarchy
                && Time.fixedTime - supportContactAt <= Time.fixedDeltaTime * 2f
                && Vector3.Dot(body.linearVelocity, supportContactNormal) <= 0.5f)
            {
                ReactionGroundNormal = supportContactNormal;
                ReactionGroundPoint = supportContactPoint;
                return true;
            }
            // Include the physical contact skin for resting reactions; leave jump tolerance unchanged.
            float tolerance = groundContactTolerance + capsule.contactOffset + Physics.defaultContactOffset;
            if (!TryGround(out var normal, false, tolerance) || Vector3.Dot(body.linearVelocity, normal) > 0.5f) return false;
            ReactionGroundNormal = normal;
            ReactionGroundPoint = lastGroundPoint;
            return true;
        }

        private void ApplyPendingContact()
        {
            if (pendingStrength <= 0f) return;
            float strength = pendingStrength;
            pendingStrength = 0f;
            if (Time.time < nextContactAt || ReactionState != RiderReactionState.Airborne && ReactionState != RiderReactionState.Sliding) return;
            nextContactAt = Time.time + contactInterval;
            bool floor = pendingNormal.y >= Mathf.Cos(maximumSlope * Mathf.Deg2Rad);
            if (ReactionState == RiderReactionState.Sliding && floor) return;
            if (floor && (strength < minimumBounceSpeed || floorBounces >= maximumFloorBounces))
            {
                body.linearVelocity = Vector3.ProjectOnPlane(pendingVelocity, pendingNormal);
                SetReactionState(RiderReactionState.Rolling);
                return;
            }
            var outgoing = pendingVelocity + pendingNormal * (strength * (1f + (floor ? floorReflection : wallReflection)));
            if (floor)
            {
                floorBounces++;
                outgoing = new Vector3(outgoing.x * floorFriction, outgoing.y, outgoing.z * floorFriction);
            }
            if (ReactionState == RiderReactionState.Sliding)
            {
                slipOwner = null;
                outgoing += Vector3.up * Mathf.Min(3f, strength * 0.3f);
                trafficRecoveryUntil = Time.time + 0.1f;
                BeginFlight(outgoing);
                nextContactAt = Time.time + contactInterval;
            }
            else ReactionImpulse?.Invoke(outgoing);
            body.linearVelocity = outgoing;
            collisionVelocity = outgoing;
            groundedForHazards = false;
            ContactImpact?.Invoke(strength);
        }
    }
}
