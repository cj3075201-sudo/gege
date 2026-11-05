using UnityEngine;
using UnityEngine.InputSystem;

namespace DeliveryRider
{
    [RequireComponent(typeof(Rigidbody))]
    public sealed partial class RiderMotor : MonoBehaviour
    {
        [SerializeField] private Transform view;
        [SerializeField] private DeliverySession session;
        [SerializeField] private float speed = 10f;
        [SerializeField] private float acceleration = 22f;
        [SerializeField] private float turnSpeed = 12f;
        [SerializeField] private float recoveryHeight = -8f;
        [SerializeField] private bool followSlopes;
        [SerializeField, Range(0, 60)] private float maximumSlope = 35;
        [SerializeField] private LayerMask groundMask = 1;
        [SerializeField, Min(0.05f)] private float groundReach = 0.3f;
        [Tooltip("Close support tolerance; groundReach is reserved for following a road already being walked on.")]
        [SerializeField, Min(0.001f)] private float groundContactTolerance = 0.03f;
        [SerializeField, Min(1)] private float safeFallDepth = 6;
        [Header("Punch and jump (test values)")]
        [SerializeField, Min(0.05f)] private float punchCooldown = 0.35f;
        [SerializeField, Min(0.1f)] private float punchReach = 1.2f;
        [SerializeField, Min(0.1f)] private float punchRadius = 0.9f;
        [SerializeField, Min(0f)] private float punchHeight = 0.9f;
        [SerializeField] private LayerMask punchMask = ~0;
        [SerializeField, Min(0.1f)] private float jumpSpeed = 5.5f;
        private readonly Collider[] punchHits = new Collider[16];
        private readonly System.Collections.Generic.List<IPunchable> punched = new System.Collections.Generic.List<IPunchable>(8);
        private float nextPunchAt;
        private bool jumpQueued;
        private bool normalJumpActive;
        public event System.Action Punched;
        public event System.Action<float> ContactImpact;
        public event System.Action SafePositionRestored;
        // Raised when an authored hazard actually hits (crash or slip); used for chain ignition.
        public event System.Action<UnityEngine.Object> HazardHit;
        private readonly RaycastHit[] groundHits = new RaycastHit[16];
        private readonly RaycastHit[] bodyHits = new RaycastHit[16];
        private CapsuleCollider capsule;
        private float trafficRecoveryUntil;
        private float nextTrafficHitAt;
        private float nextRelaunchAt;
        private bool trafficRecoveryActive;
        private UnityEngine.Object lastHazard;
        private float lastHazardHitAt = float.NegativeInfinity;
        private int hazardChainCount;
        [SerializeField, Min(0)] private float slipSteering = 4;
        [SerializeField, Min(0.1f)] private float slipMaximumSpeed = 8;
        private UnityEngine.Object slipOwner;
        private float slipUntil;
        private bool groundedForHazards;
        private Vector3 lastGroundPoint;
        public bool IsGroundedForHazards => groundedForHazards;
        public bool IsSlipping => slipOwner != null && Time.time < slipUntil;
        public int HazardChainCount => Time.time - lastHazardHitAt <= 8 ? hazardChainCount : 0;
        private Rigidbody body;
        private InputActionMap gameplay;
        private InputAction move;
        private InputAction look;
        private InputAction pause;
        private InputAction map;
        private InputAction callFirst;
        private InputAction callSecond;
        private InputAction punch;
        private InputAction jump;
        private Vector2 movement;
        [Header("Attached chickens")]
        [SerializeField, Range(0f, 1f)] private float chickenSpeedScale = 0.7f;
        [SerializeField, Min(0.1f)] private float chickenHopInterval = 1.5f;
        [SerializeField, Range(0f, 1f)] private float chickenHopHeightScale = 0.5f;
        private float chickenAttachedUntil, nextChickenHopAt;
        private readonly System.Collections.Generic.List<UnityEngine.Object> chickenSources = new System.Collections.Generic.List<UnityEngine.Object>(4);
        private bool HasAttachedChickens => chickenSources.Count > 0 && Time.time < chickenAttachedUntil;

        public bool TryAttachChicken(UnityEngine.Object source, float initialSeconds, float extraSeconds)
        {
            if (!isActiveAndEnabled || source == null || session == null || session.IsPaused || session.IsMapOpen || session.IsSettled
                || ReactionState != RiderReactionState.Normal) return false;
            if (!HasAttachedChickens) ClearChickenAttachments();
            bool firstAttachment = chickenSources.Count == 0;
            if (!chickenSources.Contains(source)) chickenSources.Add(source);
            if (firstAttachment) nextChickenHopAt = Time.time + Mathf.Max(0.1f, chickenHopInterval);
            float duration = firstAttachment ? initialSeconds : extraSeconds;
            chickenAttachedUntil = Mathf.Max(Time.time, chickenAttachedUntil) + Mathf.Max(0.1f, duration);
            return true;
        }

        public bool HasChickenAttachment(UnityEngine.Object source)
            => isActiveAndEnabled && HasAttachedChickens && ReactionState == RiderReactionState.Normal && chickenSources.Contains(source);

        public void EndChickenAttachment(UnityEngine.Object source)
        {
            chickenSources.Remove(source);
            if (chickenSources.Count == 0) ClearChickenAttachments();
        }

        private void ClearChickenAttachments()
        {
            chickenSources.Clear();
            chickenAttachedUntil = nextChickenHopAt = 0f;
        }
        private Vector3 spawn;
        public Vector2 Look => session.CanMove ? look.ReadValue<Vector2>() : Vector2.zero;
        public bool CanLook => session != null && !session.IsPaused && !session.IsMapOpen && !session.IsSettled;
        public bool CameraFrozen => session == null || session.IsPaused || session.IsCallOpen || session.IsSettled;
        public Vector2 CameraLook => CanLook ? look.ReadValue<Vector2>() : Vector2.zero;
        public float HorizontalSpeed => body == null ? 0f : Vector3.ProjectOnPlane(body.linearVelocity, Vector3.up).magnitude;
        public Vector3 Velocity => body == null ? Vector3.zero : body.linearVelocity;
        // Velocity before the latest contacts were solved; a hit uses it rather than the already-pushed velocity.
        public Vector3 ContactVelocity => collisionVelocity;
        public float Speed => speed * inlineSpeedScale * (HasAttachedChickens ? chickenSpeedScale : 1f);
        public bool HasInline { get; private set; }
        // Inline skates scale the tuned base values at runtime; the serialized speed and acceleration stay the base.
        private float inlineSpeedScale = 1f, inlineAccelerationScale = 1f;
        private bool inputBlocked;

        // Inline skates only raise speed (with a little inertia); time limits are unchanged.
        public void EquipInline(float speedMultiplier, float accelerationMultiplier)
        {
            if (HasInline) return;
            HasInline = true;
            inlineSpeedScale = Mathf.Max(1f, speedMultiplier);
            inlineAccelerationScale = Mathf.Clamp(accelerationMultiplier, 0.1f, 1f);
        }

        // Developer tools take the keyboard and mouse while their panel is open; the world keeps running.
        public void SetInputBlocked(bool blocked)
        {
            inputBlocked = blocked;
            if (blocked) gameplay.Disable();
            else if (isActiveAndEnabled) gameplay.Enable();
        }
        public bool IsRecovering => trafficRecoveryActive;

        // Traffic never changes food ownership.
        public bool TryTrafficCrash(Vector3 impulse, float recoverySeconds)
            => TryContactCrash(impulse, recoverySeconds);

        // A car that does not stop may hit the rider again while still down; each such hit relaunches
        // instead of pushing the rider along. minimumInterval keeps one contact from relaunching every step.
        public bool TryTrafficRelaunch(Vector3 velocity, float recoverySeconds, float minimumInterval)
        {
            if (zoneHopping || session == null || session.IsPaused || session.IsMapOpen || session.IsSettled || Time.time < nextRelaunchAt) return false;
            nextRelaunchAt = Time.time + minimumInterval;
            lastHazard = null;
            hazardChainCount = 0;
            ApplyContactCrash(velocity, recoverySeconds);
            return true;
        }

        // Contact hazards share a short recovery.
        public bool TryContactCrash(Vector3 impulse, float recoverySeconds)
        {
            if (session == null || !session.CanMove || IsRecovering || Time.time < nextTrafficHitAt) return false;
            lastHazard = null;
            hazardChainCount = 0;
            ApplyContactCrash(impulse, recoverySeconds);
            return true;
        }

        // Distinct authored hazards may replace a contact impulse, never an owned incident.
        public bool TryHazardCrash(UnityEngine.Object source, Vector3 impulse, float recoverySeconds)
        {
            if (!AcceptHazard(source)) return false;
            ApplyContactCrash(impulse, recoverySeconds);
            HazardHit?.Invoke(source);
            return true;
        }

        // Melons use the same flight, rolling and get-up recovery as vehicle crashes.
        public bool TryHazardTumble(UnityEngine.Object source, float recoverySeconds, bool repeatContact = false, Vector3 extraVelocity = default)
        {
            if (repeatContact)
            {
                // Each melon is consumed on contact, so a new melon can restart the accident reaction.
                if (zoneHopping || source == null || session == null || session.IsPaused || session.IsMapOpen || session.IsSettled) return false;
                hazardChainCount = lastHazard != null && Time.time - lastHazardHitAt <= 8f ? hazardChainCount + 1 : 1;
                lastHazard = source;
                lastHazardHitAt = Time.time;
            }
            else if (!AcceptHazard(source)) return false;
            // Preserve contact motion, with an optional small horizontal shove for an already downed rider.
            ApplyContactCrash(body.linearVelocity + Vector3.ProjectOnPlane(extraVelocity, Vector3.up), recoverySeconds);
            HazardHit?.Invoke(source);
            return true;
        }

        public bool TryHazardSlip(UnityEngine.Object source, Vector3 direction, float duration)
        {
            if (!groundedForHazards || !AcceptHazard(source)) return false;
            physicalTumble = false;
            trafficRecoveryActive = false;
            SetReactionState(RiderReactionState.Sliding);
            slipOwner = source;
            slipUntil = Time.time + Mathf.Max(0.1f, duration);
            var horizontal = Vector3.ProjectOnPlane(body.linearVelocity, Vector3.up);
            if (horizontal.magnitude < Speed) horizontal = Vector3.ProjectOnPlane(direction, Vector3.up).normalized * Speed;
            horizontal = Vector3.ClampMagnitude(horizontal, slipMaximumSpeed);
            body.linearVelocity = new Vector3(horizontal.x, body.linearVelocity.y, horizontal.z);
            HazardHit?.Invoke(source);
            return true;
        }

        public void CancelSlip(UnityEngine.Object source)
        {
            if (slipOwner != source) return;
            slipOwner = null;
            if (ReactionState == RiderReactionState.Sliding) SetReactionState(RiderReactionState.Normal);
        }

        private bool AcceptHazard(UnityEngine.Object source)
        {
            if (zoneHopping || source == null || session == null || session.IsPaused || session.IsMapOpen || session.IsSettled) return false;
            bool chain = lastHazard != null && lastHazard != source && Time.time - lastHazardHitAt <= 8;
            if (Time.time - lastHazardHitAt < 0.35f) return false;
            if (!chain && (IsRecovering || Time.time < nextTrafficHitAt)) return false;
            hazardChainCount = chain ? hazardChainCount + 1 : 1;
            lastHazard = source;
            lastHazardHitAt = Time.time;
            return true;
        }

        private void ApplyContactCrash(Vector3 impulse, float recoverySeconds)
        {
            physicalTumble = false;
            slipOwner = null;
            groundedForHazards = false;
            trafficRecoveryActive = true;
            trafficRecoveryUntil = Time.time + Mathf.Max(0.1f, recoverySeconds);
            nextTrafficHitAt = trafficRecoveryUntil + 1;
            movement = Vector2.zero;
            body.linearVelocity = Vector3.zero;
            body.AddForce(impulse, ForceMode.VelocityChange);
            BeginFlight(impulse);
            ContactImpact?.Invoke(impulse.magnitude);
        }

        private void Awake()
        {
            body = GetComponent<Rigidbody>();
            capsule = GetComponent<CapsuleCollider>();
            InitializeCollisionMaterials();
            spawn = transform.position;
            safeRecovery = GetComponent<RiderSafeRecovery>();
            gameplay = new InputActionMap("Gameplay");
            move = gameplay.AddAction("Move", InputActionType.Value);
            move.AddCompositeBinding("2DVector").With("Up", "<Keyboard>/w")
                .With("Down", "<Keyboard>/s").With("Left", "<Keyboard>/a").With("Right", "<Keyboard>/d");
            look = gameplay.AddAction("Look", InputActionType.Value, "<Mouse>/delta");
            pause = gameplay.AddAction("Pause", InputActionType.Button, "<Keyboard>/escape");
            map = gameplay.AddAction("Map", InputActionType.Button, "<Keyboard>/m");
            callFirst = gameplay.AddAction("CallFirst", InputActionType.Button, "<Keyboard>/1");
            callSecond = gameplay.AddAction("CallSecond", InputActionType.Button, "<Keyboard>/2");
            punch = gameplay.AddAction("Punch", InputActionType.Button, "<Keyboard>/f");
            punch.AddBinding("<Mouse>/leftButton");
            jump = gameplay.AddAction("Jump", InputActionType.Button, "<Keyboard>/space");
            recover = gameplay.AddAction("Recover", InputActionType.Button, "<Keyboard>/r");
        }

        private void OnEnable()
        {
            if (!inputBlocked) gameplay.Enable();
            UpdateCollisionMaterial();
        }

        private void OnDisable()
        {
            gameplay.Disable();
            movement = Vector2.zero;
            ResetReaction();
            capsule.sharedMaterial = originalCollisionMaterial;
        }

        private void OnDestroy()
        {
            gameplay.Dispose();
            Destroy(normalCollisionMaterial);
            Destroy(rollingCollisionMaterial);
        }

        private void Update()
        {
            if (session.IsSettled)
            {
                CancelZoneHop();
                slipOwner = null;
                trafficRecoveryActive = false;
                groundedForHazards = false;
                body.linearVelocity = body.angularVelocity = Vector3.zero;
                ResetReaction();
            }
            else if (session.IsMapOpen && IsSlipping) CancelSlip(slipOwner);
            if (pause.WasPressedThisFrame()) session.HandleEscape();
            else if (callFirst.WasPressedThisFrame()) session.HandleCallKey(0);
            else if (callSecond.WasPressedThisFrame()) session.HandleCallKey(1);
            else if (map.WasPressedThisFrame()) session.ToggleMap();
            movement = session.CanMove ? Vector2.ClampMagnitude(move.ReadValue<Vector2>(), 1f) : Vector2.zero;
            airMovement = CanLook ? Vector2.ClampMagnitude(move.ReadValue<Vector2>(), 1f) : Vector2.zero;
            if (CanLook && recover.WasPressedThisFrame()) RestoreSafePosition();
            if (session.CanPickup) session.TryPickup();
            if (session.CanMove && punch.WasPressedThisFrame()) TryPunch();
            if (session.CanMove && jump.WasPressedThisFrame()) jumpQueued = true;
        }

        // Punches go where the camera looks, so a standing rider can still aim at the recipient.
        private void TryPunch()
        {
            if (Time.time < nextPunchAt || IsSlipping) return;
            nextPunchAt = Time.time + punchCooldown;
            var forward = Vector3.ProjectOnPlane(view.forward, Vector3.up);
            forward = forward.sqrMagnitude > 0.001f ? forward.normalized : transform.forward;
            body.MoveRotation(Quaternion.LookRotation(forward));
            var center = body.position + forward * punchReach + Vector3.up * punchHeight;
            int count = Physics.OverlapSphereNonAlloc(center, punchRadius, punchHits, punchMask, QueryTriggerInteraction.Ignore);
            punched.Clear();
            for (int i = 0; i < count; i++)
            {
                var hit = punchHits[i];
                if (hit.attachedRigidbody == body) continue;
                var target = hit.GetComponentInParent<IPunchable>();
                if (target == null || punched.Contains(target)) continue;
                punched.Add(target);
                target.ReceivePunch(forward);
            }
            Punched?.Invoke();
        }

        private void FixedUpdate()
        {
            if (chickenSources.Count > 0 && Time.time >= chickenAttachedUntil) ClearChickenAttachments();
            bool wasGrounded = groundedForHazards;
            groundedForHazards = TryGround(out var supportNormal, false) && Vector3.Dot(body.linearVelocity, supportNormal) <= 0.5f;
            // Only keep an already grounded walk attached to a descending road.
            // A descending jump must retain gravity until close support is actually reached.
            if (!groundedForHazards && wasGrounded && !normalJumpActive && followSlopes
                && ReactionState == RiderReactionState.Normal)
                groundedForHazards = TryGround(out _, true);
            if (normalJumpActive && groundedForHazards && body.linearVelocity.y <= 0f) normalJumpActive = false;
            bool belowSafeGround = followSlopes && body.position.y < spawn.y - safeFallDepth;
            if (body.position.y < recoveryHeight || belowSafeGround)
            {
                RestoreSafePosition();
                return;
            }
            KnockPedestriansAhead();
            if (TickReaction()) return;
            if (TryReturnFromClosedZone()) return;
            var forward = Vector3.ProjectOnPlane(view.forward, Vector3.up).normalized;
            var right = Vector3.ProjectOnPlane(view.right, Vector3.up).normalized;
            var direction = session.CanMove ? forward * movement.y + right * movement.x : Vector3.zero;
            var current = body.linearVelocity;
            bool jumping = jumpQueued && groundedForHazards && session.CanMove && !IsSlipping;
            jumpQueued = false;
            if (IsSlipping && session.CanMove)
            {
                var drift = Vector3.ProjectOnPlane(current, Vector3.up);
                drift += direction * (slipSteering * Time.fixedDeltaTime);
                if (groundedForHazards && TryGround(out var slipNormal))
                {
                    drift += Vector3.ProjectOnPlane(Physics.gravity, slipNormal) * Time.fixedDeltaTime;
                    drift = Vector3.ClampMagnitude(drift, slipMaximumSpeed);
                    body.linearVelocity = Vector3.ProjectOnPlane(drift, slipNormal).normalized * drift.magnitude;
                    body.AddForce(-Physics.gravity, ForceMode.Acceleration);
                }
                else
                {
                    drift = Vector3.ClampMagnitude(drift, slipMaximumSpeed);
                    body.linearVelocity = new Vector3(drift.x, current.y, drift.z);
                }
                collisionVelocity = body.linearVelocity;
                return;
            }
            var horizontal = session.CanMove ? Vector3.MoveTowards(new Vector3(current.x, 0f, current.z), direction * Speed,
                acceleration * inlineAccelerationScale * Time.fixedDeltaTime) : Vector3.zero;
            bool chickenHop = HasAttachedChickens && Time.time >= nextChickenHopAt && session.CanMove;
            if (chickenHop)
            {
                nextChickenHopAt = Time.time + Mathf.Max(0.1f, chickenHopInterval);
                float angle = Random.Range(0f, Mathf.PI * 2f);
                horizontal = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * Speed;
            }
            if (jumping || chickenHop)
            {
                normalJumpActive = true;
                groundedForHazards = false;
                float lift = jumping ? jumpSpeed : jumpSpeed * Mathf.Sqrt(chickenHopHeightScale);
                body.linearVelocity = new Vector3(horizontal.x, lift, horizontal.z);
            }
            else if (followSlopes && groundedForHazards && TryGround(out var normal))
            {
                var tangent = Vector3.ProjectOnPlane(horizontal, normal).normalized * horizontal.magnitude;
                body.linearVelocity = tangent;
                // Cancel gravity only while grounded; airborne accident impulses remain untouched.
                body.AddForce(-Physics.gravity, ForceMode.Acceleration);
                if (session.CanMove && SpawnAllowedHere) spawn = body.position;
            }
            else body.linearVelocity = new Vector3(horizontal.x, current.y, horizontal.z);
            if (direction.sqrMagnitude > 0.01f)
                body.MoveRotation(Quaternion.Slerp(body.rotation, Quaternion.LookRotation(direction), turnSpeed * Time.fixedDeltaTime));
        }

        // Running or tumbling into walkers bowls them over before their solid contact can stop the rider.
        // The pedestrian decides whether the speed is enough.
        private void KnockPedestriansAhead()
        {
            var velocity = Vector3.ProjectOnPlane(body.linearVelocity, Vector3.up);
            float travel = velocity.magnitude * Time.fixedDeltaTime;
            if (travel < 0.01f) return;
            var bounds = capsule.bounds;
            float radius = Mathf.Min(bounds.extents.x, bounds.extents.z) * 0.9f;
            var offset = Vector3.up * Mathf.Max(0f, bounds.extents.y - radius);
            int count = Physics.CapsuleCastNonAlloc(bounds.center + offset, bounds.center - offset, radius, velocity.normalized,
                bodyHits, travel + 0.15f, punchMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                var other = bodyHits[i].rigidbody;
                if (other != null && other != body && other.TryGetComponent<TrafficPedestrian>(out var person)
                    && person.TryBodyKnock(velocity) && person.BodyResistance > 1f)
                {
                    var current = body.linearVelocity;
                    body.linearVelocity = new Vector3(current.x / person.BodyResistance, current.y, current.z / person.BodyResistance);
                }
            }
        }

        private bool TryGround(out Vector3 normal, bool snap = true, float supportTolerance = -1f)
        {
            normal = Vector3.up;
            if (capsule == null) return false;
            var bounds = capsule.bounds;
            float radius = Mathf.Min(bounds.extents.x, bounds.extents.z) * 0.85f;
            var origin = new Vector3(bounds.center.x, bounds.min.y + radius + 0.1f, bounds.center.z);
            float reach = snap ? groundReach : supportTolerance >= 0f ? supportTolerance : groundContactTolerance;
            int count = Physics.SphereCastNonAlloc(origin, radius, Vector3.down, groundHits,
                reach + 0.1f, groundMask, QueryTriggerInteraction.Ignore);
            float nearest = float.PositiveInfinity;
            bool found = false;
            for (int i = 0; i < count; i++)
            {
                var hit = groundHits[i];
                if (hit.rigidbody != null || hit.transform == transform || hit.transform.IsChildOf(transform)
                    || hit.normal.y < Mathf.Cos(maximumSlope * Mathf.Deg2Rad) || hit.distance >= nearest) continue;
                nearest = hit.distance;
                normal = hit.normal;
                lastGroundPoint = hit.point;
                found = true;
            }
            // Closing a small ground gap follows a descending road without cancelling a launch.
            if (snap && found && nearest > 0.1f) body.position += Vector3.down * Mathf.Min(nearest - 0.1f, groundReach);
            return found;
        }
    }
}
