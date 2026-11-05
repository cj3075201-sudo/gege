using System;
using UnityEngine;

namespace DeliveryRider
{
    // A moving queue spans the supported corridor before the rider arrives. Only the queue's tail opens the road.
    public sealed class CrossingFlockHazard : MonoBehaviour, IPlacedHazard
    {
        private enum Phase { Ready, Crossing, Spent }
        [SerializeField] private DeliverySession session;
        [SerializeField] private Transform[] birds = Array.Empty<Transform>();
        [SerializeField] private Vector3 localStart;
        [SerializeField] private Vector3 localEnd;
        [SerializeField] private Vector3 approachCenter = new Vector3(0f, 1f, 0f);
        [SerializeField] private Vector3 approachSize = new Vector3(12f, 3f, 16f);
        [Header("횡단과 대기 (시험값)")]
        [SerializeField, Min(0f)] private float maximumLeadDistance = 50f;
        [SerializeField, Min(0f)] private float arrivalMargin = 0.3f;
        [SerializeField, HideInInspector] private float waitingSpacing = 0.7f; // Retain serialized legacy data.
        [SerializeField, Min(0.1f)] private float walkSpeed = 1.5f;
        [SerializeField, Min(0.1f)] private float spacing = 1.6f;
        [SerializeField, Min(0.1f)] private float hitRadius = 0.85f;
        [Tooltip("Time for which the moving queue covers the entire corridor simultaneously.")]
        [SerializeField, Min(0f)] private float blockingSeconds = 6f;
        [Header("벽과 바닥 탐색 (시험값)")]
        [Tooltip("Total horizontal width about the source road centre. Open ends permit a lateral detour. Never shrinks below the carriageway.")]
        [SerializeField, Min(2f)] private float maximumCrossingWidth = 24f;
        [SerializeField, HideInInspector] private float boundarySearchDistance = 256f; // Retain legacy data; no longer searches across the map.
        [SerializeField, Min(0.05f)] private float boundaryStep = 0.2f;
        [SerializeField, Min(0.5f)] private float birdClearance = 0.5f;
        [SerializeField, Min(0.01f)] private float maximumGroundStep = 0.4f;
        [Header("점프 대응 (시험값)")]
        [SerializeField, Min(0.1f)] private float jumpReactionRange = 4f;
        [Tooltip("Rider feet above the crossing by this amount may fly over it. Normal jump apex is about 0.91 m.")]
        [SerializeField, Min(1.2f)] private float highFlightPassHeight = 2.2f;
        [SerializeField, Min(0.1f)] private float duckJumpHeight = 1.05f;
        [SerializeField, Min(0.1f)] private float duckJumpSeconds = 0.8f;
        [Header("충돌 (시험값)")]
        [SerializeField, Min(0f)] private float bounceImpulse = 18.8f;
        [SerializeField, Min(0f)] private float bounceLift = 6.96f;
        [SerializeField, Min(0.1f)] private float recoverySeconds = 0.8f;
        [SerializeField, Min(0.1f)] private float repeatHitDelay = 1f;

        private const int PlacementMask = ~(1 << 8); // Zone barriers belong to the rider, never to the map boundary.
        private readonly RaycastHit[] placementHits = new RaycastHit[32];
        private readonly Collider[] placementOverlaps = new Collider[32];
        private RiderMotor rider;
        private HazardSlot slot;
        private Phase phase;
        private float head, nextHitAt;
        private float effectiveSpacing, configuredSpan = -1f, configuredRow = -1f;
        private int activeBirds;
        private float[] jumpUntil = Array.Empty<float>();
        private bool[] hopped = Array.Empty<bool>();
        private Vector3[] groundLine = Array.Empty<Vector3>();
        private bool placementValid = true;
        private RoadNetwork placedGround;
        private HazardCandidate placedSpot;
        private bool hasPlacement;
        private float preparedWidth;
        private bool placementPrepared;
        private float TriggerScale => slot != null ? slot.TriggerScale : 1f;

        public void Place(DeliverySession owner, RoadNetwork ground, HazardCandidate spot)
        {
            session = owner;
            placedGround = ground;
            placedSpot = spot;
            hasPlacement = true;
            placementPrepared=false;
        }

        private void PreparePlacement()
        {
            placementPrepared=true;
            preparedWidth=maximumCrossingWidth;
            var ground=placedGround;var spot=placedSpot;
            var center = transform.position;
            placementValid = TryFloor(center, out center);
            if (!placementValid) { HideBirds(); return; }
            var across = Vector3.ProjectOnPlane(transform.right, Vector3.up).normalized * (spot.side < 0 ? -1f : 1f);
            CrossingLimits(ground, spot, across, out float startLimit, out float endLimit);
            Vector3 start = FindBoundary(center, across, startLimit);
            Vector3 end = FindBoundary(center, -across, endLimit);
            placementValid = Vector3.Distance(start, end) >= 1f;
            localStart = transform.InverseTransformPoint(start);
            localEnd = transform.InverseTransformPoint(end);
            approachCenter = transform.InverseTransformPoint((start + end) * 0.5f) + Vector3.up;
            approachSize = new Vector3(Vector3.Distance(start, end) + 2f, 5f, 16f);
            BakeGroundLine(start, end);
            EnsureFlock();
            if (placementValid) ShowWaiting(); else HideBirds();
        }

        public void ShowWaitingPreview()
        {
            if(hasPlacement&&(!placementPrepared||!Mathf.Approximately(preparedWidth,maximumCrossingWidth)))PreparePlacement();
            ShowWaiting();
        }

        // Check supported feet, then the full duck volume. A kerb is support, not a wall.
        private Vector3 FindBoundary(Vector3 center, Vector3 direction, float limit)
        {
            Vector3 point = center;
            float step = Mathf.Max(0.05f, boundaryStep);
            var facing = Quaternion.LookRotation(direction);
            for (float distance = 0f; distance < limit - .001f;)
            {
                float travel = Mathf.Min(step, limit - distance);
                var next = point + direction * travel;
                if (!TrySupportedPoint(next, direction, out next) || Mathf.Abs(next.y - point.y) > maximumGroundStep)
                    return point;
                // Reserve the whole jump envelope as well as the standing body, including facade overhangs.
                float jump = duckJumpHeight * .5f;
                int count = Physics.OverlapBoxNonAlloc(next + Vector3.up * (.34f + jump),
                    new Vector3(.25f, .3f + jump, birdClearance), placementOverlaps, facing, PlacementMask, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < count; i++)
                {
                    var collider = placementOverlaps[i];
                    if (collider.attachedRigidbody == null && !collider.transform.IsChildOf(transform)) return point;
                }
                point = next;
                distance += travel;
            }
            // A capped, supported open end is intentional: the rider can choose to go around it.
            return point;
        }

        private void CrossingLimits(RoadNetwork ground, HazardCandidate spot, Vector3 across, out float start, out float end)
        {
            float half = Mathf.Max(spot.halfWidth, maximumCrossingWidth * .5f);
            start = end = half;
            if (ground == null) return;
            var roads = ground.Roads;
            RoadSurface source = null;
            float nearest = float.PositiveInfinity;
            var center = Vector3.ProjectOnPlane(transform.position, Vector3.up);
            var forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            foreach (var road in roads)
            {
                Vector3 a = road.Container.EvaluatePosition(0f), b = road.Container.EvaluatePosition(1f);
                a.y = b.y = 0f;
                var line = b - a;
                if (line.sqrMagnitude < .01f || Mathf.Abs(road.HalfWidth - spot.halfWidth) > .25f
                    || Mathf.Abs(Vector3.Dot(line.normalized, forward)) < .95f) continue;
                float t = Mathf.Clamp01(Vector3.Dot(center - a, line) / line.sqrMagnitude);
                float distance = (center - Vector3.Lerp(a, b, t)).sqrMagnitude;
                if (distance < nearest) { nearest = distance; source = road; }
            }
            foreach (var road in roads)
            {
                if (road == source) continue;
                Vector3 a = road.Container.EvaluatePosition(0f), b = road.Container.EvaluatePosition(1f);
                a.y = b.y = 0f;
                var line = b - a;
                if (line.sqrMagnitude < .01f) continue;
                var along = line.normalized;
                var right = Vector3.Cross(Vector3.up, along);
                var offset = center - (a + b) * .5f;
                float lo = -half - birdClearance, hi = half + birdClearance;
                if (!ClipRoadAxis(Vector3.Dot(offset, along), Vector3.Dot(across, along), line.magnitude * .5f, ref lo, ref hi)
                    || !ClipRoadAxis(Vector3.Dot(offset, right), Vector3.Dot(across, right), road.HalfWidth, ref lo, ref hi)) continue;
                // Do not enter another street or continue along a junction arm beyond this road's own edge.
                float ownEdge = spot.halfWidth + birdClearance;
                if (hi > spot.halfWidth + .01f) start = Mathf.Min(start, Mathf.Max(ownEdge, lo - birdClearance));
                if (lo < -spot.halfWidth - .01f) end = Mathf.Min(end, Mathf.Max(ownEdge, -hi - birdClearance));
            }
        }

        private static bool ClipRoadAxis(float origin, float direction, float half, ref float lo, ref float hi)
        {
            if (Mathf.Abs(direction) < .0001f) return Mathf.Abs(origin) <= half;
            float a = (-half - origin) / direction, b = (half - origin) / direction;
            lo = Mathf.Max(lo, Mathf.Min(a, b));
            hi = Mathf.Min(hi, Mathf.Max(a, b));
            return lo <= hi;
        }

        private bool TrySupportedPoint(Vector3 point, Vector3 along, out Vector3 floor)
        {
            if (!TryFloor(point, out floor)) return false;
            var across = Vector3.Cross(Vector3.up, along);
            float height = floor.y;
            // Include footprint corners so a low facade cannot enter the belly while the centre is clear.
            for (int x = -1; x <= 1; x++)
                for (int z = -1; z <= 1; z++)
                {
                    if (x == 0 && z == 0) continue;
                    if (!TryFloor(point + across * (x * .25f) + along * (z * birdClearance), out var support)) return false;
                    if (Mathf.Abs(support.y - height) <= maximumGroundStep) floor.y = Mathf.Max(floor.y, support.y);
                }
            return true;
        }

        private bool TryFloor(Vector3 point, out Vector3 floor)
        {
            int count = Physics.RaycastNonAlloc(point + Vector3.up * 1.2f, Vector3.down, placementHits,
                2.8f, PlacementMask, QueryTriggerInteraction.Ignore);
            float nearest = float.PositiveInfinity;
            floor = point;
            for (int i = 0; i < count; i++)
            {
                var hit = placementHits[i];
                if (hit.rigidbody != null || hit.transform.IsChildOf(transform) || hit.normal.y < 0.7f || hit.distance >= nearest) continue;
                nearest = hit.distance;
                floor = hit.point;
            }
            return !float.IsPositiveInfinity(nearest);
        }

        private void BakeGroundLine(Vector3 start, Vector3 end)
        {
            int count = Mathf.CeilToInt(Vector3.Distance(start, end) / Mathf.Max(.05f, boundaryStep)) + 1;
            groundLine = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                var point = Vector3.Lerp(start, end, i / (float)(count - 1));
                if (!TrySupportedPoint(point, Vector3.ProjectOnPlane(end - start, Vector3.up).normalized, out var floor)) placementValid = false;
                groundLine[i] = transform.InverseTransformPoint(floor);
            }
        }

        private void Awake()
        {
            rider = session != null && session.Player != null ? session.Player.GetComponent<RiderMotor>() : null;
            slot = GetComponent<HazardSlot>();
            if (rider == null || birds.Length == 0) { enabled = false; return; }
            EnsureFlock();
            HideBirds();
        }

        private void OnDisable() { HideBirds(); phase = Phase.Spent; }
        private void OnEnable()
        {
            if (rider == null) return;
            if(slot!=null&&slot.StartedFor(session)){phase=Phase.Spent;HideBirds();return;}
            phase = Phase.Ready;
            nextHitAt = 0f;
            if(hasPlacement&&!placementPrepared)
            {
                if(slot!=null&&!slot.StrikeAllowed&&!slot.Selected){HideBirds();return;}
                PreparePlacement();
            }
            ShowWaiting();
        }

        private void Update() { if (session != null && session.IsSettled && phase == Phase.Crossing) Finish(); }

        private void FixedUpdate()
        {
            if (session == null || session.IsPaused || session.IsSettled) return;
            if(hasPlacement&&!placementPrepared)
            {
                if(slot!=null&&!slot.StrikeAllowed&&!slot.Selected)return;
                PreparePlacement();
            }
            if (hasPlacement && !Mathf.Approximately(preparedWidth, maximumCrossingWidth))
            {
                float traveled = Mathf.Max(0f, head - configuredSpan);
                Place(session, placedGround, placedSpot);PreparePlacement();
                if (phase == Phase.Crossing) head = configuredSpan + traveled;
                if (phase == Phase.Spent) HideBirds();
            }
            if (!placementValid) return;
            EnsureFlock(); // Rebuild only when an F1 value actually changes, including trigger scale.
            if (session.IsMapOpen && phase == Phase.Crossing) { Finish(); return; }
            switch (phase)
            {
                case Phase.Ready:
                    if ((slot == null || slot.StrikeAllowed) && !session.IsMapOpen && !rider.IsRecovering && (InApproach() || MeetsRider())) Begin();
                    break;
                case Phase.Crossing:
                    Advance();
                    break;
                case Phase.Spent:
                    // The queue clears once per accepted call; leaving its range never starts it over.
                    break;
            }
        }

        private void EnsureFlock()
        {
            if (birds.Length == 0) return;
            float span = Vector3.Distance(transform.TransformPoint(localStart), transform.TransformPoint(localEnd));
            float gap = Mathf.Min(Mathf.Max(.1f, spacing), Mathf.Max(.1f, hitRadius * TriggerScale * 1.9f));
            float row = span + blockingSeconds * Mathf.Max(.1f, walkSpeed);
            int count = Mathf.Max(2, Mathf.CeilToInt(row / gap) + 1);
            if (count == activeBirds && Mathf.Approximately(effectiveSpacing, gap)
                && Mathf.Approximately(configuredSpan, span) && Mathf.Approximately(configuredRow, row)) return;
            bool grew=count>birds.Length;
            if (count > birds.Length)
            {
                int original = birds.Length;
                Array.Resize(ref birds, count);
                for (int i = original; i < count; i++)
                {
                    birds[i] = Instantiate(birds[i % original], birds[0].parent);
                    birds[i].name = "Duck_Runtime_" + i;
                    birds[i].gameObject.SetActive(false);
                }
            }
            activeBirds = count;
            effectiveSpacing = gap;
            configuredSpan = span;
            configuredRow = row;
            jumpUntil = new float[birds.Length];
            hopped = new bool[birds.Length];
            for (int i = count; i < birds.Length; i++) birds[i].gameObject.SetActive(false);
            if (phase == Phase.Ready) ShowWaiting();
            if(grew&&slot!=null)slot.RefreshOwnedComponents();
        }

        // The waiting row already spans the road. Starting it early cannot let the queue pass before arrival.
        private bool MeetsRider()
        {
            var local = transform.InverseTransformPoint(rider.transform.position) - approachCenter;
            if (Mathf.Abs(local.x) > approachSize.x * .5f * TriggerScale || Mathf.Abs(local.y) > approachSize.y * TriggerScale) return false;
            float closing = -Mathf.Sign(local.z) * transform.InverseTransformDirection(rider.Velocity).z;
            if (closing < .5f) return false;
            float lead = Mathf.Min(maximumLeadDistance * TriggerScale,
                closing * Mathf.Max(0f, blockingSeconds - arrivalMargin));
            return Mathf.Abs(local.z) <= lead;
        }

        private void Begin()
        {
            if(slot!=null&&slot.StartedFor(session))return;
            slot?.MarkStarted(session);
            EnsureFlock();
            head = configuredSpan;
            nextHitAt = 0f;
            Array.Clear(jumpUntil, 0, jumpUntil.Length);
            Array.Clear(hopped, 0, hopped.Length);
            phase = Phase.Crossing;
            PositionBirds(false);
        }

        private void ShowWaiting()
        {
            if (!placementValid || birds.Length == 0) return;
            // During Place this can run before Awake; no rider is needed for posing the row.
            if (activeBirds == 0) { EnsureFlock(); return; }
            head = configuredSpan;
            PositionBirds(false);
        }

        private Vector3 GroundPoint(float fraction)
        {
            if (groundLine.Length < 2) return transform.TransformPoint(Vector3.Lerp(localStart, localEnd, fraction));
            float at = Mathf.Clamp01(fraction) * (groundLine.Length - 1);
            int index = Mathf.Min(Mathf.FloorToInt(at), groundLine.Length - 2);
            return transform.TransformPoint(Vector3.Lerp(groundLine[index], groundLine[index + 1], at - index));
        }

        private void Advance()
        {
            EnsureFlock();
            head += Mathf.Max(.1f, walkSpeed) * Time.fixedDeltaTime;
            PositionBirds(true);
            if (head - (activeBirds - 1) * effectiveSpacing > configuredSpan + effectiveSpacing) Finish();
        }

        private void PositionBirds(bool contact)
        {
            float length = Vector3.Distance(transform.TransformPoint(localStart), transform.TransformPoint(localEnd));
            if (length < .01f) return;
            var facing = Quaternion.LookRotation(Vector3.ProjectOnPlane(transform.TransformVector(localEnd - localStart), Vector3.up));
            Vector3 player = rider != null ? rider.transform.position : Vector3.zero;
            float reach = hitRadius * TriggerScale;
            float passHeight = Mathf.Max(1.2f, highFlightPassHeight * TriggerScale);
            for (int i = 0; i < activeBirds; i++)
            {
                float along = head - i * effectiveSpacing;
                // Clamp the entering/leaving bird for one spacing so there is never an opening at a wall end.
                bool visible = along >= -effectiveSpacing && along <= length + effectiveSpacing;
                if (birds[i].gameObject.activeSelf != visible) birds[i].gameObject.SetActive(visible);
                if (!visible) continue;
                var point = GroundPoint(Mathf.Clamp01(along / length));
                Vector3 offset = player - point;
                float horizontal = new Vector2(offset.x, offset.z).sqrMagnitude;
                if (rider != null && rider.IsGroundedForHazards) hopped[i] = false;
                if (contact && rider != null && !rider.IsGroundedForHazards && rider.Velocity.y > .1f && !hopped[i]
                    && offset.y <= passHeight && horizontal <= jumpReactionRange * TriggerScale * jumpReactionRange * TriggerScale)
                {
                    hopped[i] = true;
                    jumpUntil[i] = Time.time + duckJumpSeconds;
                }
                float t = Mathf.Clamp01(1f - (jumpUntil[i] - Time.time) / duckJumpSeconds);
                float jump = jumpUntil[i] > Time.time ? 4f * duckJumpHeight * t * (1f - t) : 0f;
                birds[i].SetPositionAndRotation(point + Vector3.up * jump, facing);
                // Height is relative to supported ground, never to the duck's jumping visual.
                if (!contact || rider == null || Time.time < nextHitAt || slot != null && !slot.StrikeAllowed
                    || offset.y > passHeight || offset.y < -passHeight || horizontal > reach * reach) continue;
                float incoming = Vector3.Dot(rider.Velocity, transform.forward);
                float side = Mathf.Abs(incoming) > .1f ? -Mathf.Sign(incoming)
                    : Mathf.Sign(Vector3.Dot(player - point, transform.forward));
                if (side == 0f) side = -1f;
                var push = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized * side;
                if (rider.TryHazardCrash(this, push * bounceImpulse + Vector3.up * bounceLift, recoverySeconds))
                    nextHitAt = Time.time + repeatHitDelay;
            }
        }

        private bool InApproach()
        {
            Vector3 p = transform.InverseTransformPoint(rider.transform.position) - approachCenter;
            Vector3 half = approachSize * (.5f * TriggerScale);
            return Mathf.Abs(p.x) <= half.x && Mathf.Abs(p.y) <= half.y && Mathf.Abs(p.z) <= half.z;
        }

        private void Finish() { HideBirds(); phase = Phase.Spent; }
        private void HideBirds() { foreach (var bird in birds) if (bird != null) bird.gameObject.SetActive(false); }

        private void OnDrawGizmosSelected()
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireCube(approachCenter, approachSize);
            Gizmos.color = Color.cyan;
            Gizmos.DrawLine(localStart, localEnd);
        }
    }
}
