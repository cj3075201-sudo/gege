using UnityEngine;

namespace DeliveryRider
{
    // Sidewalk walker spawned from the pedestrian prefab by PedestrianNetwork at start (like TrafficCar).
    [RequireComponent(typeof(Rigidbody), typeof(CapsuleCollider))]
    public sealed partial class TrafficPedestrian : MonoBehaviour, IPunchable
    {
        [SerializeField] private Transform visual;
        [SerializeField] private Renderer coat;
        [Header("Walking (test values)")]
        [SerializeField] private Vector2 walkingSpeed = new Vector2(1.1f, 1.65f);
        [SerializeField, Min(0f)] private float laneOffset = 0.35f;
        [SerializeField, Min(0.1f)] private float turnSpeed = 8f;
        [SerializeField, Min(0.1f)] private float obstacleReach = 1.1f;
        [SerializeField] private LayerMask obstacleMask = ~0;
        [Header("Avoidance (test values)")]
        [Tooltip("How long to keep to the passing side before stepping back into the walking lane.")]
        [SerializeField, Min(0f)] private float avoidHoldSeconds = 1.2f;
        [Tooltip("Allowed time without forward progress, in addition to the time needed for a sidestep.")]
        [SerializeField, Min(0f)] private float turnBackSeconds = 1f;
        [Header("Punch response (test values)")]
        [SerializeField] private float punchSpeed = 5f;
        [SerializeField] private float punchLift = 3.5f;
        [SerializeField] private float minimumFlight = 0.5f;
        [SerializeField] private float returnTimeout = 6f;
        [SerializeField, Min(0f)] private float getUpSeconds = 0.6f;
        [Tooltip("Extra time allowed for walking back to the sidewalk before snapping there.")]
        [SerializeField, Min(0f)] private float returnWalkSlack = 4f;
        [Header("Body knock and chain (test values)")]
        [Tooltip("Rider speed needed to bowl a walker over by running into them.")]
        [SerializeField, Min(0f)] private float knockMinimumSpeed = 4f;
        [Tooltip("Share of the rider's speed the bowled walker flies off with.")]
        [SerializeField, Min(0f)] private float knockSpeedShare = 1.2f;
        [Tooltip("A flying pedestrian at least this fast knocks over the walkers it hits.")]
        [SerializeField, Min(0f)] private float chainMinimumSpeed = 4f;
        [SerializeField, Range(0f, 1f)] private float chainSpeedShare = 0.7f;
        [Tooltip("How many times one hit may be passed on from pedestrian to pedestrian.")]
        [SerializeField, Min(0)] private int chainLimit = 2;
        [Tooltip("A pedestrian sent flying by another one knocks the rider down when flying at them this fast.")]
        [SerializeField, Min(0f)] private float riderHitMinimumSpeed = 6f;
        private readonly RaycastHit[] hits = new RaycastHit[16];
        private readonly Collider[] overlaps = new Collider[16];
        private PedestrianNetwork network;
        private Rigidbody body;
        private System.Random random;
        private int path;
        private float distance, speed, waitUntil, hitAt, nextReturnAt, idleAt, nextObstacleAt;
        private bool flying, blocked;
        private float floorAt = float.NegativeInfinity;
        // The visual turns about the middle of the body, not the feet, and its centre is raised
        // whenever needed so the turned body never sinks below the feet plane.
        private const float BodyCenter = 0.9f;
        private const float BodyTop = 1.85f;
        private const float BodyThickness = 0.25f;
        private Quaternion restVisual;
        private Vector3 restVisualPosition;
        private Quaternion pose = Quaternion.identity;
        private float poseHeight = BodyCenter;
        private float visualSpin, lieAngle;
        private bool lying;
        // Signed sideways offset from the path centre; positive keeps right. Avoidance moves it across the sidewalk.
        private float sideOffset, avoidUntil, progressDeadline, avoidStartDistance;
        private Vector3 progressPosition;
        private enum AvoidPhase { None, Passing, ReturningToLane }
        private AvoidPhase avoidPhase;
        private int triedSides;
        private int chainDepth;
        private Vector3 flightVelocity;
        // After landing: get up where the flight ended, then walk to this sidewalk spot and join its path.
        private bool returning;
        private Vector3 returnFeet;
        private int returnPath;
        private float returnDistance, returnGiveUpAt, getUpStartedAt;
        private Quaternion getUpFrom;

        private bool CanReact => network != null && !flying
            && (network.Session == null || !network.Session.IsPaused && !network.Session.IsSettled);

        // Editor-time wiring for the prefab.
        public void SetAppearance(Transform appearance, Renderer coatRenderer) { visual = appearance; coat = coatRenderer; }

        private void Awake()
        {
            body = GetComponent<Rigidbody>();
            if (visual != null) { restVisual = visual.localRotation; restVisualPosition = visual.localPosition; }
        }

        // Called right after spawning; the walker stays still until then.
        public void Initialize(PedestrianNetwork owner, int route, float along, int randomSeed, Material coatMaterial)
        {
            InitializeState(owner, route, along, randomSeed, coatMaterial);
            network.Sample(path, distance, out var feet, out var forward);
            transform.SetPositionAndRotation(feet + Vector3.Cross(Vector3.up, forward) * Lane(network.Path(path)), Quaternion.LookRotation(forward));
        }

        private void InitializeState(PedestrianNetwork owner, int route, float along, int randomSeed, Material coatMaterial)
        {
            network = owner;
            random = new System.Random(randomSeed);
            speed = Mathf.Lerp(walkingSpeed.x, walkingSpeed.y, (float)random.NextDouble());
            path = route; distance = along;
            ResetAvoidance();
            idleAt = Time.time + 8f + (float)random.NextDouble() * 15f;
            if (coat != null && coatMaterial != null) coat.sharedMaterial = coatMaterial;
            SetWalking();
        }

        private void SetWalking()
        {
            flying = false;
            body.isKinematic = true;
            body.useGravity = false;
            body.constraints = RigidbodyConstraints.FreezeRotation;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        }

        private void FixedUpdate()
        {
            if (network == null || path < 0 || path >= network.PathCount) return;
            if (network.Session != null && (network.Session.IsPaused || network.Session.IsSettled))
            {
                if (network.Session.IsSettled && flying) { body.linearVelocity = body.angularVelocity = Vector3.zero; }
                return;
            }
            if (flying)
            {
                // Velocity before this physics step, so a contact sees the speed the flight hit with.
                flightVelocity = body.linearVelocity;
                bool landed = Time.time - floorAt <= Time.fixedDeltaTime * 2f && body.linearVelocity.magnitude < 1.5f;
                bool lost = Time.time - hitAt >= returnTimeout || body.position.y < -8f;
                if (Time.time >= nextReturnAt && (Time.time - hitAt >= minimumFlight && landed || lost)) BeginReturn(lost);
                return;
            }
            if (returning) { WalkBack(); return; }
            if (crowdMember && UpdateCrowdGoal()) return;
            if (Time.time < waitUntil) return;
            if (!crowdMember && avoidPhase == AvoidPhase.None && Time.time >= idleAt)
            {
                waitUntil = Time.time + 0.5f + (float)random.NextDouble();
                idleAt = Time.time + 10f + (float)random.NextDouble() * 20f;
                return;
            }
            network.Sample(path, distance, out var currentFeet, out var forward);
            var currentRoute = network.Path(path);
            var right = Vector3.Cross(Vector3.up, forward);
            if (!UpdateAvoidance(currentFeet, forward, right, currentRoute)) return;
            var currentTarget = currentFeet + right * Lane(currentRoute);
            // An outer corner or U-turn is longer than the centre line. Catch up before advancing
            // route distance, otherwise repeated turns leave the actor increasingly behind its path.
            // Sidesteps use the same catch-up, so the walker moves across before going on.
            if ((currentTarget - body.position).sqrMagnitude > 0.0144f)
            {
                var step = Vector3.MoveTowards(body.position, currentTarget, speed * Time.fixedDeltaTime);
                if (!ClearMove(body.position, step)) { blocked = true; nextObstacleAt = 0f; return; }
                body.MovePosition(step);
                body.MoveRotation(Quaternion.Slerp(body.rotation, Quaternion.LookRotation(forward), turnSpeed * Time.fixedDeltaTime));
                return;
            }
            float nextDistance = distance + speed * Time.fixedDeltaTime;
            int nextPath = path;
            var route = network.Path(path);
            bool turningBack = false;
            if (nextDistance >= route.length)
            {
                nextPath = network.Next(path, random);
                nextDistance -= route.length;
                turningBack = nextPath == route.reverse;
                route = network.Path(nextPath);
            }
            network.Sample(nextPath, nextDistance, out var feet, out forward);
            float nextOffset = nextPath == path ? Lane(route) : Mathf.Clamp(laneOffset, -Limit(route), Limit(route));
            var target = feet + Vector3.Cross(Vector3.up, forward) * nextOffset;
            var nextPosition = Vector3.MoveTowards(body.position, target, speed * Time.fixedDeltaTime);
            if (!ClearMove(body.position, nextPosition)) { blocked = true; nextObstacleAt = 0f; return; }
            if (nextPath != path) ResetAvoidance();
            path = nextPath;
            distance = nextDistance;
            if (turningBack) waitUntil = Time.time + 0.3f;
            // Turn/side changes ease toward the new walking lane instead of teleporting sideways.
            body.MovePosition(nextPosition);
            body.MoveRotation(Quaternion.Slerp(body.rotation, Quaternion.LookRotation(forward), turnSpeed * Time.fixedDeltaTime));
        }

        private static float Limit(PedestrianPath route) => Mathf.Max(0f, route.halfWidth - 0.35f);
        private float Lane(PedestrianPath route) => Mathf.Clamp(sideOffset, -Limit(route), Limit(route));

        private void ResetAvoidance()
        {
            avoidPhase = AvoidPhase.None;
            triedSides = 0;
            sideOffset = laneOffset;
            avoidUntil = progressDeadline = 0f;
            blocked = false;
            nextObstacleAt = 0f;
        }

        private bool UpdateAvoidance(Vector3 feet, Vector3 forward, Vector3 right, PedestrianPath route)
        {
            if (avoidPhase != AvoidPhase.None)
            {
                // Sideways movement must not keep the no-progress watchdog alive.
                if (Vector3.Dot(body.position - progressPosition, forward) >= 0.2f)
                {
                    progressPosition = body.position;
                    progressDeadline = Time.time + Mathf.Max(0.1f, turnBackSeconds);
                }
                if (Time.time >= progressDeadline)
                {
                    if (avoidPhase != AvoidPhase.Passing || !TrySidestep(feet, forward, right, route))
                    { TurnBack(route); return false; }
                    blocked = false;
                    nextObstacleAt = 0f;
                }
            }
            if (Time.time < nextObstacleAt) return !blocked;
            nextObstacleAt = Time.time + 0.1f;
            var target = feet + right * Lane(route);
            bool movingSideways = (target - body.position).sqrMagnitude > 0.0144f;
            if (avoidPhase == AvoidPhase.None)
            {
                if (!blocked && !Blocked(body.position, forward)) return true;
                avoidPhase = AvoidPhase.Passing;
                triedSides = 0;
                avoidStartDistance = distance;
                progressPosition = body.position;
                progressDeadline = Time.time + Mathf.Max(0.1f, turnBackSeconds);
                blocked = !TrySidestep(feet, forward, right, route);
                return !blocked;
            }
            // Keep the selected lane while approaching it. Do not retest the old forward lane
            // and choose the opposite side on every obstacle scan.
            blocked = movingSideways ? !ClearMove(body.position, target) : Blocked(body.position, forward);
            if (avoidPhase == AvoidPhase.ReturningToLane && !movingSideways)
            {
                bool obstacle = blocked;
                ResetAvoidance();
                blocked = obstacle;
            }
            else if (avoidPhase == AvoidPhase.Passing && !blocked && !movingSideways && Time.time >= avoidUntil
                && distance - avoidStartDistance >= obstacleReach + 0.4f)
                TryReturnToLane(feet, forward, right, route);
            return !blocked;
        }

        // Each passing side can be selected at most once in one avoidance attempt.
        private bool TrySidestep(Vector3 feet, Vector3 forward, Vector3 right, PedestrianPath route)
        {
            float limit = Limit(route), current = Lane(route);
            for (int side = -1; side <= 1; side += 2)
            {
                int mask = side < 0 ? 1 : 2;
                if ((triedSides & mask) != 0) continue;
                triedSides |= mask;
                float offset = side * limit;
                if (Mathf.Abs(offset - current) < 0.3f) continue;
                var spot = feet + right * offset;
                if (!HasRoom(spot) || Blocked(spot, forward) || !ClearMove(body.position, spot)) continue;
                sideOffset = offset;
                avoidUntil = Time.time + avoidHoldSeconds;
                progressPosition = body.position;
                progressDeadline = Time.time + Vector3.Distance(body.position, spot) / Mathf.Max(0.1f, speed)
                    + Mathf.Max(0.1f, turnBackSeconds);
                return true;
            }
            return false;
        }

        private void TryReturnToLane(Vector3 feet, Vector3 forward, Vector3 right, PedestrianPath route)
        {
            var spot = feet + right * Mathf.Clamp(laneOffset, -Limit(route), Limit(route));
            if (HasRoom(spot) && !Blocked(spot, forward) && ClearMove(body.position, spot))
            {
                sideOffset = laneOffset;
                avoidPhase = AvoidPhase.ReturningToLane;
                progressPosition = body.position;
                progressDeadline = Time.time + Vector3.Distance(body.position, spot) / Mathf.Max(0.1f, speed)
                    + Mathf.Max(0.1f, turnBackSeconds);
            }
            else avoidUntil = Time.time + 0.3f;
        }

        // Both sides stayed blocked: walk the same sidewalk the other way instead of waiting forever.
        private void TurnBack(PedestrianPath route)
        {
            float previousOffset = Lane(route);
            if (route.connector)
            {
                // A connector's reverse points to its incoming lane, not a reversed connector.
                path = network.Path(route.reverse).reverse;
                distance = 0f;
            }
            else
            {
                distance = Mathf.Clamp(route.length - distance, 0f, route.length);
                path = route.reverse;
            }
            ResetAvoidance();
            waitUntil = Time.time + 0.3f;
            // Reverse forward motion where the walker stands instead of first cutting across
            // the same obstacle to reach the opposite walking lane.
            sideOffset = route.connector ? laneOffset : -previousOffset;
            avoidPhase = AvoidPhase.Passing;
            avoidStartDistance = distance;
            progressPosition = body.position;
            network.Sample(path, distance, out var feet, out var forward);
            var target = feet + Vector3.Cross(Vector3.up, forward) * Lane(network.Path(path));
            progressDeadline = waitUntil + Vector3.Distance(body.position, target) / Mathf.Max(0.1f, speed)
                + Mathf.Max(0.1f, turnBackSeconds);
            avoidUntil = waitUntil + avoidHoldSeconds;
        }

        private bool Blocked(Vector3 feet, Vector3 direction)
            => SweepBlocked(feet, direction, obstacleReach);

        private bool ClearMove(Vector3 from, Vector3 to)
        {
            var delta = to - from;
            return delta.sqrMagnitude < 0.000001f || !SweepBlocked(from, delta.normalized, delta.magnitude);
        }

        private bool SweepBlocked(Vector3 feet, Vector3 direction, float reach)
        {
            int count = Physics.CapsuleCastNonAlloc(feet + Vector3.up * 0.35f, feet + Vector3.up * 1.45f,
                0.28f, direction, hits, reach, obstacleMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
                if (hits[i].rigidbody != body && hits[i].normal.y < 0.7f) return true;
            return count == hits.Length;
        }

        public bool HasRoom(Vector3 feet)
        {
            int count = Physics.OverlapCapsuleNonAlloc(feet + Vector3.up * 0.35f, feet + Vector3.up * 1.45f,
                0.27f, overlaps, obstacleMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++) if (overlaps[i].attachedRigidbody != body) return false;
            return count < overlaps.Length;
        }

        public bool ReceivePunch(Vector3 direction)
        {
            if (!CanReact || network.Session != null && network.Session.IsMapOpen) return false;
            var away = Vector3.ProjectOnPlane(direction, Vector3.up).normalized;
            Launch(away * punchSpeed + Vector3.up * punchLift, 0);
            return true;
        }

        // Running into a walker at speed bowls them over. Never a delivery or food recovery; those need a punch.
        public bool TryBodyKnock(Vector3 riderVelocity)
        {
            var horizontal = Vector3.ProjectOnPlane(riderVelocity, Vector3.up);
            if (!CanReact || horizontal.magnitude < knockMinimumSpeed) return false;
            Launch(horizontal * (knockSpeedShare / BodyResistance) + Vector3.up * punchLift, 0);
            return true;
        }

        // Hazards can trip a walker even when the hazard has slowed below the running-hit threshold.
        public bool TryHazardKnock(Vector3 velocity)
        {
            if (!CanReact || network.Session != null && (network.Session.IsMapOpen || network.Session.IsPaused || network.Session.IsSettled)) return false;
            Launch(velocity, 0);
            return true;
        }

        private void Launch(Vector3 velocity, int depth)
        {
            flying = true; hitAt = Time.time; floorAt = float.NegativeInfinity;
            returning = false;
            chainDepth = depth;
            ResetAvoidance();
            // Hit again while down: tumble on from the lying pose instead of popping upright.
            visualSpin = lying ? lieAngle : 0f;
            lying = false;
            body.isKinematic = false;
            body.useGravity = true;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            body.WakeUp();
            body.linearVelocity = flightVelocity = velocity;
        }

        // A fast flier passes the hit on: walkers it reaches fall over, and a pedestrian sent flying
        // by another one can bowl the rider over. The rider's own victims never knock the rider down.
        private void OnCollisionEnter(Collision collision)
        {
            if (!flying || collision.rigidbody == null) return;
            var horizontal = Vector3.ProjectOnPlane(flightVelocity, Vector3.up);
            float flightSpeed = horizontal.magnitude;
            if (collision.rigidbody.TryGetComponent<TrafficPedestrian>(out var other))
            {
                if (chainDepth < chainLimit && flightSpeed >= chainMinimumSpeed && other.CanReact)
                    other.Launch((horizontal + Vector3.up * punchLift) * chainSpeedShare, chainDepth + 1);
            }
            else if (chainDepth > 0 && flightSpeed >= riderHitMinimumSpeed && collision.rigidbody.TryGetComponent<RiderMotor>(out var rider)
                && Vector3.Dot(horizontal, rider.transform.position - body.position) > 0f)
                rider.TryContactCrash(horizontal / flightSpeed * Mathf.Min(flightSpeed, 4f) + Vector3.up * 2f, 1.1f);
        }

        private void OnCollisionStay(Collision collision)
        {
            if (!flying) return;
            for (int i = 0; i < collision.contactCount; i++)
                if (collision.GetContact(i).normal.y > 0.7f) { floorAt = Time.time; break; }
        }

        // A landed pedestrian gets up on the spot and walks back to the nearest free sidewalk spot.
        // Falling off the map or a flight that never settles still snaps straight back.
        private void BeginReturn(bool snap)
        {
            nextReturnAt = Time.time + 0.5f;
            if (crowdMember) { BeginCrowdReturn(snap); return; }
            if (!network.FindReturn(body.position, this, out var route, out var along, out var feet)) return;
            body.linearVelocity = body.angularVelocity = Vector3.zero;
            SetWalking();
            returnPath = route; returnDistance = along; returnFeet = feet;
            if (snap)
            {
                body.position = feet;
                JoinPath();
                return;
            }
            returning = true;
            getUpStartedAt = Time.time;
            // Get up from the pose the fall ended in; the walk back turns the body toward the sidewalk.
            getUpFrom = pose;
            var toward = Vector3.ProjectOnPlane(feet - body.position, Vector3.up);
            returnGiveUpAt = Time.time + getUpSeconds + toward.magnitude / speed + returnWalkSlack;
            blocked = false; nextObstacleAt = 0f;
        }

        private void WalkBack()
        {
            if (Time.time < getUpStartedAt + getUpSeconds) return;
            if (crowdMember)
            {
                returning = false;
                lying = false;
                ApplyPose(Quaternion.identity, BodyCenter);
                SetCrowdWalking();
                return;
            }
            var toward = Vector3.ProjectOnPlane(returnFeet - body.position, Vector3.up);
            if (toward.sqrMagnitude <= 0.0144f) { JoinPath(); return; }
            if (Time.time >= returnGiveUpAt)
            {
                body.position = returnFeet;
                JoinPath();
                return;
            }
            var direction = toward.normalized;
            if (Time.time >= nextObstacleAt)
            {
                nextObstacleAt = Time.time + 0.1f;
                blocked = Blocked(body.position, direction);
            }
            // Wait for whatever stands in the way; a lasting block ends in the snap above.
            if (blocked) return;
            var next = body.position + direction * Mathf.Min(speed * Time.fixedDeltaTime, toward.magnitude);
            next.y = GroundHeight(next, body.position.y);
            body.MovePosition(next);
            body.MoveRotation(Quaternion.Slerp(body.rotation, Quaternion.LookRotation(direction), turnSpeed * Time.fixedDeltaTime));
        }

        // Off the sidewalk path the walker follows the terrain itself.
        private float GroundHeight(Vector3 at, float fallback)
        {
            int count = Physics.RaycastNonAlloc(at + Vector3.up * 1.2f, Vector3.down, hits, 3f, obstacleMask, QueryTriggerInteraction.Ignore);
            float nearest = float.PositiveInfinity, height = fallback;
            for (int i = 0; i < count; i++)
            {
                var hit = hits[i];
                if (hit.rigidbody != null || hit.normal.y < 0.7f || hit.distance >= nearest) continue;
                nearest = hit.distance;
                height = hit.point.y;
            }
            return height;
        }

        private void JoinPath()
        {
            returning = false;
            path = returnPath; distance = returnDistance;
            network.Sample(path, distance, out _, out var forward);
            body.rotation = Quaternion.LookRotation(forward);
            lying = false;
            ApplyPose(Quaternion.identity, BodyCenter);
            waitUntil = Time.time + 0.3f;
            ResetAvoidance();
        }

        private void LateUpdate()
        {
            if (visual == null || network == null || network.Session != null && (network.Session.IsPaused || network.Session.IsSettled)) return;
            if (flying)
            {
                // The first floor contact ends the tumble: settle face down or face up, whichever is nearer.
                if (!lying && Time.time - hitAt >= 0.15f && Time.time - floorAt <= 0.1f)
                {
                    lying = true;
                    lieAngle = Mathf.Repeat(visualSpin, 360f) < 180f ? 90f : 270f;
                }
                if (lying)
                {
                    var rotation = Quaternion.RotateTowards(pose, Quaternion.Euler(lieAngle, 0f, 0f), 540f * Time.deltaTime);
                    float resting = RestingHeight(rotation);
                    ApplyPose(rotation, Mathf.Max(resting, Mathf.MoveTowards(poseHeight, resting, 6f * Time.deltaTime)));
                }
                else
                {
                    visualSpin += 260f * Time.deltaTime;
                    ApplyPose(Quaternion.Euler(visualSpin, 0f, 12f), BodyCenter);
                }
            }
            else if (returning && Time.time < getUpStartedAt + getUpSeconds)
            {
                var rotation = Quaternion.Slerp(getUpFrom, Quaternion.identity, (Time.time - getUpStartedAt) / Mathf.Max(0.01f, getUpSeconds));
                ApplyPose(rotation, RestingHeight(rotation));
            }
            else
            {
                if (poseHeight != BodyCenter || pose != Quaternion.identity) ApplyPose(Quaternion.identity, BodyCenter);
                visual.localRotation = restVisual * Quaternion.Euler(0f, 0f, !blocked && Time.time >= waitUntil ? Mathf.Sin(Time.time * speed * 5f) * 3f : 0f);
                if (crowdMember)
                    visual.localPosition = restVisualPosition + Vector3.up * ((Mathf.Sin(Time.time * 9f + crowdPhase) + 1f) * 0.09f);
            }
        }

        private void ApplyPose(Quaternion rotation, float centerHeight)
        {
            pose = rotation;
            poseHeight = centerHeight;
            if (visual == null) return;
            visual.localRotation = rotation * restVisual;
            visual.localPosition = restVisualPosition + Vector3.up * centerHeight - rotation * (Vector3.up * BodyCenter);
        }

        // Lowest body-centre height that keeps the turned body (feet to head top) above the feet plane.
        private static float RestingHeight(Quaternion rotation)
        {
            float up = (rotation * Vector3.up).y;
            return Mathf.Max(0f, BodyCenter * up, (BodyCenter - BodyTop) * up) + BodyThickness * Mathf.Sqrt(Mathf.Max(0f, 1f - up * up));
        }
    }
}
