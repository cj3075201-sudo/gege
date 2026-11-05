using UnityEngine;
using UnityEngine.InputSystem;

namespace DeliveryRider
{
    // HTML catUpd/catPal. The planner owns selection; this component owns one duel per activation.
    public sealed class CatBoxerHazard : MonoBehaviour, IPlacedHazard, IPunchable
    {
        public enum Mode { Waiting, Bout, Knockout, Pal, Leaving, Spent }
        public enum Beat { Gap, Tell, Open, Chase }
        public enum Attack { Jab, Hook, Uppercut }
        [SerializeField] private CatBoxerView presentation;
        [Header("Duel — HTML CAT_R / CAT_T / CAT_HP / CAT_OPEN")]
        [SerializeField, Min(1)] private int unlockDay = 2;
        [SerializeField, Min(0.1f)] private float triggerRadius = 5f;
        [SerializeField, Min(1f)] private float boutSeconds = 22f;
        [SerializeField, Min(1)] private int catHealth = 3;
        [SerializeField, Min(1)] private int lossHits = 3;
        [SerializeField, Min(0.05f)] private float missOpenSeconds = 1.1f;
        [SerializeField, Min(0.05f)] private float hitOpenSeconds = 0.5f;
        [SerializeField, Min(0.05f)] private float jabTellSeconds = 0.55f;
        [SerializeField, Min(0.05f)] private float hookTellSeconds = 0.7f;
        [SerializeField, Min(0.05f)] private float upperTellSeconds = 0.85f;
        [SerializeField] private Vector2 firstGapSeconds = new Vector2(0.5f, 0.8f);
        [SerializeField] private Vector2 nextGapSeconds = new Vector2(0.7f, 1.2f);
        [SerializeField] private Vector2 punchedGapSeconds = new Vector2(0.9f, 1.3f);
        [SerializeField, Min(0.01f)] private float blockedGapSeconds = 0.25f;
        [Header("Reach and avoidance (m)")]
        [SerializeField, Min(0.1f)] private float approachSpeed = 4.5f;
        [SerializeField, Min(0.1f)] private float approachStop = 2f;
        [SerializeField, Min(0.1f)] private float attackReach = 3.2f;
        [SerializeField, Min(0.1f)] private float upperChoiceReach = 2.2f;
        [SerializeField, Min(0.1f)] private float jabWidth = 0.9f;
        [SerializeField, Min(0.1f)] private float jabHeight = 1.2f;
        [SerializeField, Min(0.1f)] private float hookReach = 2.6f;
        [SerializeField, Min(0.1f)] private float hookHeight = 0.7f;
        [SerializeField, Min(0.1f)] private float upperReach = 1.7f;
        [SerializeField, Min(0.1f)] private float upperHeight = 2.5f;
        [SerializeField, Min(0.1f)] private float startHeight = 1.5f;
        [SerializeField, Min(0.1f)] private float punchRange = 2.6f;
        [SerializeField, Min(0f)] private float idleTrackSpeed = 3.8f;
        [SerializeField, Min(0f)] private float idleTrackDistance = 12f;
        [Header("Launch velocities — already converted from HTML gravity 22 to 9.81")]
        [SerializeField, Min(0f)] private float jabForward = 11f;
        [SerializeField, Min(0f)] private float jabUp = 5.5f;
        [SerializeField, Min(0f)] private float hookSide = 15f;
        [SerializeField, Min(0f)] private float hookForward = 3f;
        [SerializeField, Min(0f)] private float hookUp = 8f;
        [SerializeField, Min(0f)] private float upperForward = 3f;
        [SerializeField] private Vector2 upperUp = new Vector2(22f, 25f);
        [SerializeField, Min(0f)] private float lossForward = 15f;
        [SerializeField] private Vector2 lossUp = new Vector2(24f, 27f);
        [SerializeField, Min(0.1f)] private float recoverySeconds = 0.6f;
        [Header("Escape and friend")]
        [SerializeField, Min(0f)] private float escapeDistance = 7.5f;
        [SerializeField, Min(0f)] private float escapeSpeed = 3f;
        [SerializeField, Min(0f)] private float escapeDelay = 0.5f;
        [SerializeField, Min(0f)] private float sentGraceSeconds = 2.5f;
        [SerializeField, Min(0.1f)] private float chaseSpeed = 13f;
        [SerializeField, Min(0.1f)] private float chaseSeconds = 2.5f;
        [SerializeField, Min(0.1f)] private float chaseReach = 1.5f;
        [SerializeField, Min(0f)] private float chaseForward = 15f;
        [SerializeField, Min(0f)] private float chaseUp = 7f;
        [SerializeField, Min(0f)] private float knockoutSeconds = 1.8f;
        [SerializeField, Min(0.1f)] private float palSeconds = 70f;
        [SerializeField, Min(0.1f)] private float palSpeed = 10.5f;
        [SerializeField, Min(0f)] private float palOffset = 1.6f;
        [SerializeField, Min(0f)] private float palFollowDistance = 2.2f;
        [SerializeField, Min(0.1f)] private float palTeleportDistance = 30f;
        [SerializeField, Min(0.1f)] private float leaveSeconds = 1.2f;
        [SerializeField, Min(0.1f)] private float shoutSeconds = 3f;
        [SerializeField, Min(.1f)] private float palCoverInterval = .5f;
        private DeliverySession session;
        private RiderMotor rider;
        private HazardSlot slot;
        private RoadNetwork ground;
        private HazardZoneFilter openRoads;
        private Collider playerCollider;
        private Collider[] punchVolumes;
        private DeliveryOrderOffer order;
        private Vector3 origin, laneForward, laneRight, aim;
        private float halfWidth = 3f, lateral, elapsed, remaining, farTime, sentTime, stagger;
        private bool wasSelected;
        private float nextPalCoverAt;
        private InputAction shout;
        public Mode State { get; private set; }
        public Beat Phase { get; private set; }
        public Attack CurrentAttack { get; private set; }
        public int Health { get; private set; }
        public int TakenHits { get; private set; }
        public bool KeepsFoodOnCurrentHit { get; private set; }
        public Vector3 Position => presentation != null ? presentation.transform.position : transform.position;
        private float Scale => slot != null ? slot.TriggerScale : 1f;
        private bool Selected => slot == null || slot.StrikeAllowed;
        private bool FoodReady => slot != null ? slot.StrikeAllowed : HazardPlanner.DeliveryInProgress(session);
        private bool Open(Vector3 p) => openRoads == null || openRoads.PointOpen(p);

        public void Place(DeliverySession owner, RoadNetwork roads, HazardCandidate spot)
        {
            session = owner;
            ground = roads;
            origin = spot.position;
            laneForward = spot.rotation * Vector3.forward;
            laneRight = spot.rotation * Vector3.right;
            halfWidth = Mathf.Max(0.6f, spot.halfWidth);
            openRoads = new HazardZoneFilter(owner.ZoneMap, owner.IsZoneOpen);
        }

        private void Awake()
        {
            slot = GetComponent<HazardSlot>();
            if (presentation == null) presentation = GetComponentInChildren<CatBoxerView>(true);
            if (session == null || session.Player == null || presentation == null) { enabled = false; return; }
            rider = session.Player.GetComponent<RiderMotor>();
            playerCollider = session.Player.GetComponent<Collider>();
            if (rider == null) { enabled = false; return; }
            // The solid query volume receives the existing punch, but must never block the rider's escape.
            punchVolumes = presentation.GetComponentsInChildren<Collider>(true);
            shout = new InputAction("CatBoxerShout", InputActionType.Button, "<Keyboard>/q");
            ResetDuel();
        }

        private void OnEnable() { if (rider != null) { ResetDuel(); shout?.Enable(); } }
        private void OnDisable() { shout?.Disable(); Spend(); wasSelected = false; }
        private void OnDestroy() => shout?.Dispose();

        private void ResetDuel()
        {
            State = Mode.Waiting; Phase = Beat.Gap; Health = catHealth; TakenHits = 0;
            elapsed = remaining = farTime = sentTime = stagger = lateral = 0f;
            KeepsFoodOnCurrentHit = false;
            if (presentation != null) { presentation.transform.position = origin; presentation.gameObject.SetActive(false); }
        }

        // Update also tears down when settlement has stopped physics time.
        private void Update()
        {
            if (session == null || rider == null) return;
            bool dayOpen = session.Progress == null || session.Progress.Day >= unlockDay;
            bool selected = Selected && FoodReady && dayOpen && Open(origin);
            if (order != session.ActiveOrder || selected && !wasSelected) { ResetDuel(); order = session.ActiveOrder; }
            wasSelected = selected;
            if (!selected || !FoodReady || !dayOpen || session.IsSettled || !Open(origin) || !Open(Position))
            { Spend(); return; }
            if (State == Mode.Spent) return;
            if (!presentation.gameObject.activeSelf)
            {
                presentation.gameObject.SetActive(true);
                // IgnoreCollision belongs to active collider instances; renew it after each visibility cycle.
                foreach (var shape in punchVolumes)
                    if (playerCollider != null) Physics.IgnoreCollision(shape, playerCollider);
            }
            if (!session.IsPaused && !session.IsMapOpen && shout != null && shout.WasPressedThisFrame()
                && Flat(rider.transform.position - Position).magnitude < 11f) stagger = shoutSeconds;
            presentation.Show(this, rider.transform.position, remaining, elapsed, leaveSeconds);
        }

        private void FixedUpdate()
        {
            if (session == null || rider == null || !Selected || !FoodReady || session.IsPaused || session.IsMapOpen || session.IsSettled
                || State == Mode.Spent || !Open(origin) || !Open(Position) || !Open(rider.transform.position)) return;
            if (session.Progress != null && session.Progress.Day < unlockDay) return;
            float dt = Time.fixedDeltaTime;
            elapsed += dt;
            var offset = Flat(rider.transform.position - Position);
            float distance = offset.magnitude;
            float height = Mathf.Max(0f, rider.transform.position.y - Position.y);
            if (State == Mode.Leaving) { if (elapsed >= leaveSeconds) Spend(); return; }
            if (State == Mode.Knockout) { if (elapsed >= knockoutSeconds) Enter(Mode.Pal); return; }
            if (State == Mode.Pal)
            {
                if (slot != null && slot.Planner != null && Time.time >= nextPalCoverAt)
                {
                    nextPalCoverAt = Time.time + palCoverInterval;
                    slot.Planner.TryConsumeSlot(rider.transform.position, 6f, slot);
                }
                if (elapsed >= palSeconds) { Enter(Mode.Leaving); return; }
                if (distance > palFollowDistance) Move(rider.transform.position - Heading() * palOffset, palSpeed, dt, distance > palTeleportDistance);
                return;
            }
            if (State == Mode.Waiting)
            {
                var fromOrigin = Flat(rider.transform.position - origin);
                if (Mathf.Abs(Vector3.Dot(fromOrigin, laneForward)) < idleTrackDistance)
                    lateral = Mathf.MoveTowards(lateral, Mathf.Clamp(Vector3.Dot(fromOrigin, laneRight), -halfWidth + 0.6f, halfWidth - 0.6f), idleTrackSpeed * dt);
                var waiting = origin + laneRight * lateral;
                if (Open(waiting)) presentation.transform.position = waiting;
                if (Flat(rider.transform.position - Position).magnitude < triggerRadius * Scale && height < startHeight && !rider.IsRecovering)
                { Enter(Mode.Bout); Phase = Beat.Gap; remaining = Pick(firstGapSeconds); }
                return;
            }
            if (stagger > 0f) { stagger -= dt; return; }
            if (elapsed > boutSeconds) { Enter(Mode.Leaving); return; }
            if (Phase == Beat.Chase)
            {
                remaining -= dt;
                Move(rider.transform.position, chaseSpeed, dt);
                if (distance < chaseReach * Scale && !rider.IsRecovering && Strike(Heading() * chaseForward + Vector3.up * chaseUp, false)) Enter(Mode.Leaving);
                else if (remaining <= 0f) Enter(Mode.Leaving);
                return;
            }
            sentTime -= dt;
            if (distance > escapeDistance && !rider.IsRecovering && sentTime <= 0f
                && Vector3.Dot(Flat(rider.Velocity), offset.normalized) > escapeSpeed)
            {
                farTime += dt;
                if (farTime > escapeDelay) { Phase = Beat.Chase; remaining = chaseSeconds; return; }
            }
            else farTime = 0f;
            remaining -= dt;
            switch (Phase)
            {
                case Beat.Gap:
                    if (distance > approachStop) Move(rider.transform.position, approachSpeed, dt);
                    if (remaining <= 0f && distance < attackReach * Scale && height < upperHeight)
                    {
                        CurrentAttack = (Attack)Random.Range(0, distance < upperChoiceReach * Scale ? 3 : 2);
                        aim = distance > 0.001f ? offset / distance : laneForward;
                        Phase = Beat.Tell;
                        remaining = CurrentAttack == Attack.Jab ? jabTellSeconds : CurrentAttack == Attack.Hook ? hookTellSeconds : upperTellSeconds;
                    }
                    break;
                case Beat.Tell:
                    if (remaining <= 0f) ResolveAttack(offset, height);
                    break;
                case Beat.Open:
                    if (remaining <= 0f) { Phase = Beat.Gap; remaining = Pick(nextGapSeconds); }
                    break;
            }
        }

        private void ResolveAttack(Vector3 offset, float height)
        {
            float distance = offset.magnitude, along = Vector3.Dot(offset, aim);
            var right = Vector3.Cross(Vector3.up, aim);
            float side = Vector3.Dot(offset, right);
            bool contact = CurrentAttack == Attack.Jab ? along > 0f && along < attackReach * Scale && Mathf.Abs(side) < jabWidth * Scale && height < jabHeight
                : CurrentAttack == Attack.Hook ? distance < hookReach * Scale && height < hookHeight : distance < upperReach * Scale && height < upperHeight;
            bool got = false;
            if (contact)
            {
                bool loss = TakenHits + 1 >= lossHits;
                Vector3 push;
                if (loss)
                {
                    var direction = session.Target != null ? Flat(session.Target.position - rider.transform.position).normalized : Heading();
                    if (direction.sqrMagnitude < 0.001f) direction = Heading();
                    push = direction * lossForward + Vector3.up * Pick(lossUp);
                }
                else if (CurrentAttack == Attack.Jab) push = aim * jabForward + Vector3.up * jabUp;
                else if (CurrentAttack == Attack.Hook) push = right * ((side >= 0f ? 1f : -1f) * hookSide) + aim * hookForward + Vector3.up * hookUp;
                else push = offset.normalized * upperForward + Vector3.up * Pick(upperUp);
                got = Strike(push, !loss && CurrentAttack == Attack.Jab);
                if (got)
                {
                    TakenHits++; sentTime = sentGraceSeconds;
                    if (loss) { Enter(Mode.Leaving); return; }
                }
            }
            Phase = Beat.Open; remaining = got ? hitOpenSeconds : missOpenSeconds;
        }

        private bool Strike(Vector3 velocity, bool keepFood)
        {
            if (!Selected || !FoodReady || !Open(Position) || !Open(rider.transform.position)) return false;
            // The planner skips food drop on a jab while the shared crash path applies F1 launch scales.
            KeepsFoodOnCurrentHit = keepFood;
            return rider.TryHazardCrash(this, velocity, recoverySeconds);
        }

        public bool ReceivePunch(Vector3 direction)
        {
            if (session == null || session.IsPaused || session.IsMapOpen || session.IsSettled || !Selected || !FoodReady
                || State != Mode.Bout || !Open(Position) || !Open(rider.transform.position)
                || Flat(rider.transform.position - Position).magnitude >= punchRange) return false;
            if (Phase != Beat.Open)
            {
                if (Phase == Beat.Gap) remaining = Mathf.Min(remaining, blockedGapSeconds);
                return false;
            }
            Health = Mathf.Max(0, Health - 1);
            if (Health == 0) Enter(Mode.Knockout);
            else { Phase = Beat.Gap; remaining = Pick(punchedGapSeconds); }
            return true;
        }

        private void Move(Vector3 target, float speed, float dt, bool teleport = false)
        {
            target.y = Position.y;
            var next = teleport ? target : Vector3.MoveTowards(Position, target, speed * dt);
            if (!Open(next)) return;
            // Sweep the whole step, including a catch-up teleport, so closed borders/obstacles cannot be skipped.
            var delta = Flat(next - Position);
            int steps = Mathf.Max(1, Mathf.CeilToInt(delta.magnitude / 0.5f));
            var safe = Position;
            for (int i = 1; i <= steps; i++)
            {
                var p = Vector3.Lerp(Position, next, (float)i / steps);
                if (!Open(p) || Physics.CheckSphere(p + Vector3.up * 0.8f, 0.35f, HazardPhysics.Everything, QueryTriggerInteraction.Ignore)
                    && Blocked(p)) break;
                safe = p;
            }
            presentation.transform.position = ground != null ? ground.OnGround(safe, 0f) : safe;
        }

        private readonly Collider[] obstacles = new Collider[16];
        private bool Blocked(Vector3 p)
        {
            int count = Physics.OverlapSphereNonAlloc(p + Vector3.up * 0.8f, 0.35f, obstacles, HazardPhysics.Everything, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                var shape = obstacles[i];
                if (shape == playerCollider || shape.transform.IsChildOf(transform)) continue;
                return true;
            }
            return false;
        }
        private Vector3 Heading()
        {
            var velocity = Flat(rider.Velocity);
            return velocity.sqrMagnitude > 0.25f ? velocity.normalized : Flat(rider.transform.forward).normalized;
        }
        private static Vector3 Flat(Vector3 v) => Vector3.ProjectOnPlane(v, Vector3.up);
        private static float Pick(Vector2 range) => Random.Range(Mathf.Max(0.01f, range.x), Mathf.Max(0.01f, range.y));
        private void Enter(Mode state) { State = state; elapsed = 0f; }
        private void Spend() { State = Mode.Spent; stagger = 0f; if (presentation != null) presentation.gameObject.SetActive(false); }
    }
}
