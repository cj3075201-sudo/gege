using System.Collections.Generic;
using UnityEngine;

namespace DeliveryRider
{
    // Placed HTML chases use the road corridor; the legacy sidewalk helpers remain for existing installer checks.
    [RequireComponent(typeof(BoxCollider))]
    public sealed partial class MusclemanHazard : MonoBehaviour, IPlacedHazard
    {
        private enum Phase { Ready, Running, Turning, Spent, Winding }
        [SerializeField] private Transform runner;
        [SerializeField] private PedestrianNetwork sidewalks;
        [Header("Rush (test values)")]
        [SerializeField, Min(0.1f)] private float spawnBehindDistance = 10f;
        [Tooltip("If less straight sidewalk remains behind, the approach must come around a previous corner.")]
        [SerializeField, Min(0f)] private float minimumStraightBehind = 10f;
        [SerializeField, Min(0.1f)] private float speedMultiplier = 2.5f;
        [SerializeField, Min(0.1f)] private float disappearAfterTurn = 1f;
        [SerializeField, Min(0.1f)] private float runnerHeight = 3f;
        [SerializeField, Min(0.1f)] private float runnerRadius = 0.75f;
        [Header("Sideways shove (test values)")]
        [SerializeField, Min(0f)] private float shoveSpeed = 8f;
        [SerializeField, Min(0f)] private float shoveForward = 4f;
        [Tooltip("Share of the rider's own horizontal speed kept in the shove.")]
        [SerializeField, Min(0f)] private float shoveRiderShare = 0.3f;
        [SerializeField, Min(0f)] private float shoveLift = 2.9f;
        [SerializeField, Min(0f)] private float pedestrianLift = 1f;
        [SerializeField, Min(0.1f)] private float recoverySeconds = 0.6f;
        [Tooltip("A rider touching the trigger where no sidewalk route exists is tried again after this long, not every physics step.")]
        [SerializeField, Min(0.02f)] private float routeRetrySeconds = 0.25f;
        [SerializeField, Min(.1f)] private float detectionRadius = 7f;
        [SerializeField] private Vector2 windSeconds = new Vector2(.8f, 2f);
        [SerializeField, Min(.1f)] private float maximumChaseSeconds = 8f;
        private float windElapsed, chaseElapsed;
        private float nextRouteTryAt;
        private DeliverySession session;
        private RiderMotor rider;
        private CapsuleCollider playerShape;
        private BoxCollider volume;
        private HazardSlot slot;
        private float TriggerScale => slot != null ? slot.TriggerScale : 1f;
        private Phase phase;
        private int path, firstPath, turnConnector;
        private float distance, rushSpeed, turningTime;
        private readonly RaycastHit[] sweepHits = new RaycastHit[32];
        private readonly Collider[] overlaps = new Collider[32];
        private readonly HashSet<Rigidbody> struck = new HashSet<Rigidbody>();
        private readonly List<int> approach = new List<int>();
        private int approachIndex;

        public void Place(DeliverySession owner, RoadNetwork ground, HazardCandidate spot)
        {
            session = owner; chaseGround = ground; htmlOrigin = spot.position; htmlForward = spot.rotation * Vector3.forward;
        }

        private void Awake()
        {
            rider = session != null && session.Player != null ? session.Player.GetComponent<RiderMotor>() : null;
            if (sidewalks == null) sidewalks = FindFirstObjectByType<PedestrianNetwork>();
            volume = GetComponent<BoxCollider>();
            volume.isTrigger = true;
            slot = GetComponent<HazardSlot>();
            if (rider == null || sidewalks == null && chaseGround == null || runner == null) { enabled = false; return; }
            playerShape = rider.GetComponent<CapsuleCollider>();
            if(chaseGround!=null) htmlPath = new PursuitHazardPath(session,chaseGround,transform,roadsideReach);
            runner.gameObject.SetActive(false);
        }

        private void OnEnable() { if(slot!=null&&slot.StartedFor(session)){Finish();return;} phase = Phase.Ready; nextRouteTryAt = 0f; ResetHtmlChase(); }
        private void OnDisable() => Finish();

        private void Update()
        {
            if (session != null && session.IsSettled) Finish();
        }

        private bool Touching() => playerShape != null && Physics.ComputePenetration(volume, transform.position, transform.rotation,
            playerShape, playerShape.transform.position, playerShape.transform.rotation, out _, out _);

        private void FixedUpdate()
        {
            if (rider == null || session.IsPaused || session.IsSettled) return;
            if(htmlPath!=null) { TickHtmlChase(); return; }
            if (session.IsMapOpen) { if (phase == Phase.Winding || phase == Phase.Running || phase == Phase.Turning) Finish(); return; }
            switch (phase)
            {
                case Phase.Ready:
                    if (Time.time >= nextRouteTryAt && (slot == null || slot.StrikeAllowed)
                        && Vector3.ProjectOnPlane(rider.transform.position - transform.position, Vector3.up).sqrMagnitude <= detectionRadius * detectionRadius * TriggerScale * TriggerScale)
                    {
                        if (windSeconds.y <= 0f) { if (!BeginRush()) nextRouteTryAt = Time.time + routeRetrySeconds; }
                        else { slot?.MarkStarted(session);phase = Phase.Winding; windElapsed = 0f; }
                    }
                    break;
                case Phase.Winding:
                    if (slot != null && !slot.StrikeAllowed) { Finish(); break; }
                    windElapsed += Time.fixedDeltaTime;
                    if (windElapsed >= windSeconds.y || windElapsed >= windSeconds.x
                        && Vector3.ProjectOnPlane(rider.transform.position - transform.position, Vector3.up).magnitude >= spawnBehindDistance)
                    {
                        if (!BeginRush()) { if(slot!=null&&slot.StartedFor(session))Finish();else { phase = Phase.Ready; nextRouteTryAt = Time.time + routeRetrySeconds; } }
                    }
                    break;
                case Phase.Running:
                case Phase.Turning:
                    Advance();
                    break;
                case Phase.Spent:
                    if ((slot==null||slot.Role!=HazardRole.Trigger)&&!Touching()) phase = Phase.Ready;
                    break;
            }
        }

        // Use only sidewalk lanes with room behind and an actual left/right corner ahead.
        public static bool TryRoute(PedestrianNetwork network, Vector3 point, Vector3 preferredForward, float behind, float radius,
            out int pathIndex, out float along, float minimumStraight = 10f)
        {
            pathIndex = -1; along = 0f;
            float best = float.PositiveInfinity;
            preferredForward = Vector3.ProjectOnPlane(preferredForward, Vector3.up).normalized;
            var scratch = new List<int>();
            for (int p = 0; p < network.PathCount; p++)
            {
                var lane = network.Path(p);
                if (lane.connector || lane.halfWidth < radius + 0.05f || ChooseTurn(network, p) < 0) continue;
                for (int i = 1; i < lane.points.Length; i++)
                {
                    var a = lane.points[i - 1];
                    var ab = Vector3.ProjectOnPlane(lane.points[i] - a, Vector3.up);
                    float t = Mathf.Clamp01(Vector3.Dot(point - a, ab) / Mathf.Max(0.0001f, ab.sqrMagnitude));
                    float d = Mathf.Lerp(lane.cumulative[i - 1], lane.cumulative[i], t);
                    if (lane.length - d < 2f) continue;
                    var onLane = Vector3.Lerp(a, lane.points[i], t);
                    float lateral = Vector3.ProjectOnPlane(point - onLane, Vector3.up).magnitude;
                    if (lateral > lane.halfWidth || Mathf.Abs(point.y - onLane.y) > 3f) continue;
                    float score = lateral * 10f + (1f - Vector3.Dot(ab.normalized, preferredForward)) * 2f;
                    if (score >= best) continue;
                    if (!TryApproach(network, p, d, behind, radius, minimumStraight, scratch, out _)) continue;
                    best = score; pathIndex = p; along = d;
                }
            }
            return pathIndex >= 0;
        }

        // Trace only the baked sidewalk graph backwards, then reverse it into a fixed arrival route.
        public static bool TryApproach(PedestrianNetwork network, int targetPath, float targetDistance, float behind, float radius,
            float minimumStraight, List<int> route, out float spawnDistance)
        {
            route.Clear();
            spawnDistance = 0f;
            int current = targetPath;
            float available = targetDistance, remaining = behind;
            bool cornerRequired = targetDistance < minimumStraight;
            route.Add(current);
            for (int guard = 0; guard < network.PathCount; guard++)
            {
                if (remaining <= available)
                {
                    spawnDistance = available - remaining;
                    route.Reverse();
                    return true;
                }
                remaining -= available;
                int previous = -1;
                float best = float.PositiveInfinity;
                var lane = network.Path(current);
                if (lane.connector)
                {
                    var source = network.Path(lane.reverse);
                    if (source.halfWidth >= radius + 0.05f && System.Array.IndexOf(source.next, current) >= 0) previous = lane.reverse;
                }
                else
                {
                    var outgoing = Vector3.ProjectOnPlane(lane.points[1] - lane.points[0], Vector3.up);
                    for (int p = 0; p < network.PathCount; p++)
                    {
                        var corner = network.Path(p);
                        if (!corner.connector || corner.halfWidth < radius + 0.05f || System.Array.IndexOf(corner.next, current) < 0) continue;
                        var source = network.Path(corner.reverse);
                        if (source.halfWidth < radius + 0.05f) continue;
                        var incoming = Vector3.ProjectOnPlane(source.points[source.points.Length - 1] - source.points[source.points.Length - 2], Vector3.up);
                        float angle = Vector3.Angle(incoming, outgoing);
                        if (angle > 150f || cornerRequired && angle < 30f) continue;
                        float score = Mathf.Abs(90f - angle);
                        if (score < best && !route.Contains(p)) { best = score; previous = p; }
                    }
                    cornerRequired = false;
                }
                if (previous < 0 || route.Contains(previous)) return false;
                current = previous;
                route.Add(current);
                available = network.Path(current).length;
            }
            return false;
        }

        public static int ChooseTurn(PedestrianNetwork network, int laneIndex)
        {
            var lane = network.Path(laneIndex);
            var incoming = Vector3.ProjectOnPlane(lane.points[lane.points.Length - 1] - lane.points[lane.points.Length - 2], Vector3.up);
            int choice = -1;
            float best = float.PositiveInfinity;
            foreach (int next in lane.next)
            {
                var corner = network.Path(next);
                if (!corner.connector || corner.next.Length == 0) continue;
                var exit = network.Path(corner.next[0]);
                float angle = Vector3.Angle(incoming, Vector3.ProjectOnPlane(exit.points[1] - exit.points[0], Vector3.up));
                if (angle < 30f || angle > 150f) continue;
                float score = Mathf.Abs(90f - angle);
                if (score < best) { best = score; choice = next; }
            }
            return choice;
        }

        private bool BeginRush()
        {
            var heading = rider.HorizontalSpeed > 0.5f ? rider.Velocity : rider.transform.forward;
            if (!TryRoute(sidewalks, rider.transform.position, heading, spawnBehindDistance, runnerRadius, out path, out distance, minimumStraightBehind)) return false;
            firstPath = path;
            turnConnector = ChooseTurn(sidewalks, path);
            if (!TryApproach(sidewalks, path, distance, spawnBehindDistance, runnerRadius, minimumStraightBehind, approach, out var spawnDistance)) return false;
            approachIndex = 0;
            path = approach[0];
            distance = spawnDistance;
            // Lock speed and route now: subsequent player movement does not steer the runner.
            rushSpeed = rider.Speed * speedMultiplier;
            turningTime = chaseElapsed = 0f;
            struck.Clear();
            sidewalks.Sample(path, distance, out var feet, out var forward);
            runner.SetPositionAndRotation(feet, Quaternion.LookRotation(forward));
            runner.gameObject.SetActive(true);
            slot?.MarkStarted(session);
            phase = Phase.Running;
            return true;
        }

        private void Advance()
        {
            float dt = Time.fixedDeltaTime;
            chaseElapsed += dt;
            if (chaseElapsed >= maximumChaseSeconds) { Finish(); return; }
            if (phase == Phase.Turning) turningTime += dt;
            if (phase == Phase.Turning && turningTime >= disappearAfterTurn) { Finish(); return; }
            float travel = rushSpeed * dt;
            var before = runner.position;
            for (int guard = 0; travel > 0f && guard < 12; guard++)
            {
                var lane = sidewalks.Path(path);
                float step = Mathf.Min(travel, Mathf.Max(0f, lane.length - distance));
                distance += step;
                travel -= step;
                if (distance < lane.length - 0.001f) break;
                if (path == firstPath && phase == Phase.Running)
                {
                    path = turnConnector;
                    phase = Phase.Turning;
                    turningTime = 0f;
                }
                else if (phase == Phase.Running && approachIndex + 1 < approach.Count) path = approach[++approachIndex];
                else if (lane.next.Length > 0) path = lane.next[0];
                else break; // Stop at the sidewalk end instead of crossing the carriageway.
                distance = 0f;
            }
            sidewalks.Sample(path, distance, out var feet, out var forward);
            runner.SetPositionAndRotation(feet, Quaternion.LookRotation(forward));
            ShoveAlong(before, feet, forward);
        }

        private void ShoveAlong(Vector3 from, Vector3 to, Vector3 forward)
        {
            var bottom = from + Vector3.up * runnerRadius;
            var top = from + Vector3.up * (runnerHeight - runnerRadius);
            var delta = to - from;
            if (TriggerScale != 1f)
            {
                float radius = runnerRadius * TriggerScale;
                float height = Mathf.Max(runnerHeight * TriggerScale, radius * 2f);
                if (delta.sqrMagnitude > 0.00001f)
                {
                    int count = Physics.CapsuleCastNonAlloc(from + Vector3.up * radius, from + Vector3.up * (height - radius),
                        radius, delta.normalized, sweepHits, delta.magnitude, HazardPhysics.Everything, QueryTriggerInteraction.Ignore);
                    for (int i = 0; i < count; i++)
                        if (sweepHits[i].collider.transform.IsChildOf(rider.transform)) Shove(sweepHits[i].collider, forward);
                }
                int countAtEnd = Physics.OverlapCapsuleNonAlloc(to + Vector3.up * radius, to + Vector3.up * (height - radius),
                    radius, overlaps, HazardPhysics.Everything, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < countAtEnd; i++)
                    if (overlaps[i].transform.IsChildOf(rider.transform)) Shove(overlaps[i], forward);
            }
            if (delta.sqrMagnitude > 0.00001f)
            {
                int count = Physics.CapsuleCastNonAlloc(bottom, top, runnerRadius, delta.normalized, sweepHits, delta.magnitude, HazardPhysics.Everything, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < count; i++)
                    if (TriggerScale == 1f || !sweepHits[i].collider.transform.IsChildOf(rider.transform)) Shove(sweepHits[i].collider, forward);
            }
            int overlapCount = Physics.OverlapCapsuleNonAlloc(to + Vector3.up * runnerRadius, to + Vector3.up * (runnerHeight - runnerRadius), runnerRadius, overlaps, HazardPhysics.Everything, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < overlapCount; i++)
                if (TriggerScale == 1f || !overlaps[i].transform.IsChildOf(rider.transform)) Shove(overlaps[i], forward);
        }

        private void Shove(Collider target, Vector3 forward)
        {
            var body = target.attachedRigidbody;
            if (body == null || struck.Contains(body)) return;
            if (!body.TryGetComponent<RiderMotor>(out var player) && !body.TryGetComponent<TrafficPedestrian>(out _)) return;
            // The food may be gone since the rush started: the runner passes the rider but still bowls over walkers.
            if (player != null && slot != null && !slot.StrikeAllowed) return;
            if(player!=null&&htmlPath!=null&&(HtmlFootHeight>=1.2f
                ||Vector3.ProjectOnPlane(player.transform.position-runner.position,Vector3.up).sqrMagnitude>runnerRadius*runnerRadius*TriggerScale*TriggerScale))return;
            struck.Add(body);
            var right = Vector3.Cross(Vector3.up, forward).normalized;
            float side = Vector3.Dot(body.position - runner.position, right) >= 0f ? 1f : -1f;
            var sideways = right * (side * shoveSpeed);
            if (player != null)
            {
                var carried = Vector3.ProjectOnPlane(player.Velocity, Vector3.up) * shoveRiderShare;
                player.TryHazardCrash(this, sideways + forward * shoveForward + carried + Vector3.up * shoveLift, recoverySeconds);
            }
            else if (body.TryGetComponent<TrafficPedestrian>(out var pedestrian)) pedestrian.TryHazardKnock(sideways + Vector3.up * pedestrianLift);
        }

        private void Finish()
        {
            if (runner != null) runner.gameObject.SetActive(false);
            phase = Phase.Spent;
        }
    }
}
