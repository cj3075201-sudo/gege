using UnityEngine;

namespace DeliveryRider
{
    // Authored ground-following path: a controlled hazard, not a free rigidbody vehicle.
    public sealed partial class RollingTireHazard : MonoBehaviour, IPlacedHazard
    {
        private enum Phase { Ready, Warning, Rolling, Spent }
        [SerializeField] private DeliverySession session;
        [SerializeField] private SwingDoorHazard linkedDoor;
        [SerializeField] private Transform wheel;
        [SerializeField] private Renderer warningLamp;
        [SerializeField] private Vector3[] localPath;
        [SerializeField] private bool proximityActivation = true;
        [SerializeField] private Vector3 approachCenter;
        [SerializeField] private Vector3 approachSize = new Vector3(4, 3, 5);
        [SerializeField, Min(0.1f)] private float radius = 0.55f;
        [SerializeField, Min(0.1f)] private float warningSeconds = 0.8f;
        [SerializeField, Min(0.1f)] private float initialSpeed = 4;
        [SerializeField, Min(0.1f)] private float maximumSpeed = 7;
        [SerializeField, Min(0)] private float slopeAcceleration = 8;
        [SerializeField, Min(0.1f)] private float maximumLifetime = 8;
        [SerializeField, Min(0)] private float impulse = 6;
        [SerializeField, Min(0)] private float upwardImpulse = 5.8f;
        [SerializeField, Min(0.1f)] private float recoverySeconds = 1.1f;
        [SerializeField, Min(0)] private float rearmSeconds = 8;
        [SerializeField] private LayerMask collisionMask = ~0;
        [Header("Road run (test values)")]
        [Tooltip("Half length along the road of the zone that starts the tire.")]
        [SerializeField, Min(0.1f)] private float triggerHalfLength = 26f;
        [Tooltip("The tire starts this far past its slot, on the far side from the rider.")]
        [SerializeField, Min(0f)] private float startBehind = 18f;
        [Tooltip("How far past the slot the tire keeps rolling toward the rider's side.")]
        [SerializeField, Min(0.1f)] private float rollPast = 40f;
        private const float PathStep = 0.75f, WheelHeight = 0.65f, ClearanceRayHeight = 1.2f;
        private RoadNetwork road;
        private Vector3 parkedLocal;
        private float laneHalfWidth;
        private bool roadRun;
        private readonly Collider[] overlaps = new Collider[32];
        private readonly RaycastHit[] sweepHits = new RaycastHit[32];
        private RiderMotor rider;
        private Phase phase;
        private float elapsed;
        private float speed;
        private float spin;
        private int segment;
        private bool hitThisCycle;
        private bool initialized;
        private Quaternion initialRotation;
        private HazardSlot slot;
        private float TriggerScale => slot != null ? slot.TriggerScale : 1f;

        // Runtime spawn: the tire waits parked at the kerb; when started it rolls down the road toward the rider.
        public void Place(DeliverySession owner, RoadNetwork ground, HazardCandidate spot)
        {
            session = owner;
            linkedDoor = null;
            proximityActivation = true;
            approachCenter = new Vector3(0f, 1f, 0f);
            road = ground;
            roadRun = true;
            laneHalfWidth = Mathf.Max(0f, spot.halfWidth - 1f);
            // Tall enough that a sloped road does not hide a rider at the far end of the zone.
            approachSize = new Vector3((spot.halfWidth + 1.5f) * 2f, 6f, triggerHalfLength * 2f);
            float start = spot.halfWidth + (spot.alley ? 0.5f : 2.5f);
            var right = transform.right;
            var first = ground.OnGround(transform.position + right * (start * spot.side), WheelHeight);
            parkedLocal = transform.InverseTransformPoint(first);
            localPath = new[] { parkedLocal, parkedLocal };
            wheel.SetPositionAndRotation(first, Quaternion.LookRotation(-right * spot.side));
            if (warningLamp != null) { warningLamp.transform.position = first + Vector3.up; warningLamp.enabled = false; }
        }

        private void Awake()
        {
            if (session == null || session.Player == null || wheel == null || localPath == null || localPath.Length < 2)
            { Debug.LogError("Tire requires a session, wheel and at least two path points.", this); enabled = false; return; }
            rider = session.Player.GetComponent<RiderMotor>();
            if (rider == null) { enabled = false; return; }
            initialRotation = wheel.localRotation;
            slot = GetComponent<HazardSlot>();
            InitializeWave();
            initialized = true;
            ResetWheel();
        }

        private void OnEnable() { if (linkedDoor != null) linkedDoor.ContactHit += OnDoorHit; if (initialized) { if(slot!=null&&slot.StartedFor(session)){Finish();return;} phase = Phase.Ready; elapsed = 0f; hitThisCycle = false; PrepareWave(); ResetWheel(); } }
        private void OnDisable()
        {
            if (linkedDoor != null) linkedDoor.ContactHit -= OnDoorHit;
            SetLamp(false);
            if (initialized) { wheel.gameObject.SetActive(false); HideWave(); phase = Phase.Spent; elapsed = 0; }
        }
        private void OnDoorHit() => TryActivate();

        // Settlement sets timeScale to zero, so cleanup cannot depend on FixedUpdate.
        private void Update() { if (initialized && session.IsSettled) Finish(); }

        public bool TryActivate()
        {
            if (!initialized || !isActiveAndEnabled || phase != Phase.Ready || session.IsPaused || session.IsMapOpen || session.IsSettled) return false;
            if (slot != null && (!slot.StrikeAllowed||slot.StartedFor(session))) return false;
            ResetWheel();
            if (roadRun) BuildRoadRun();
            hitThisCycle = false;
            speed = roadRun ? (waveLarge ? largeSpeed : smallSpeed) : Mathf.Min(initialSpeed, maximumSpeed);
            segment = 1;
            slot?.MarkStarted(session);
            Enter(Phase.Warning);
            SetLamp(true);
            return true;
        }

        private void FixedUpdate()
        {
            if (session.IsPaused) return;
            if (session.IsSettled) { Finish(); return; }
            if (session.IsMapOpen && (phase == Phase.Warning || phase == Phase.Rolling)) { Finish(); return; }
            elapsed += Time.fixedDeltaTime;
            switch (phase)
            {
                case Phase.Ready:
                    if (proximityActivation && !rider.IsRecovering && InApproach()) TryActivate();
                    break;
                case Phase.Warning:
                    SetLamp(Mathf.Repeat(elapsed, 0.2f) < 0.12f);
                    if (elapsed >= warningSeconds) { SetLamp(false); Enter(Phase.Rolling); }
                    break;
                case Phase.Rolling:
                    if (elapsed >= maximumLifetime) { Finish(); break; }
                    Advance();
                    break;
                case Phase.Spent:
                    if ((slot==null||slot.Role!=HazardRole.Trigger)&&(elapsed >= rearmSeconds) && !InApproach() &&
                        Vector3.Distance(rider.transform.position, transform.TransformPoint(localPath[0])) > 4)
                    { ResetWheel(); Enter(Phase.Ready); }
                    break;
            }
        }

        // A straight line down the road at the rider's lane, from behind the slot to past it on the rider's side.
        private void BuildRoadRun()
        {
            var local = transform.InverseTransformPoint(rider.transform.position);
            var along = transform.forward * (local.z >= 0f ? 1f : -1f);
            var lane = transform.position + transform.right * Mathf.Clamp(local.x, -laneHalfWidth, laneHalfWidth);
            float behind = ClearDistanceBehind(lane, -along);
            int steps = Mathf.Max(1, Mathf.CeilToInt((behind + rollPast) / PathStep));
            localPath = new Vector3[steps + 1];
            for (int i = 0; i <= steps; i++)
                localPath[i] = transform.InverseTransformPoint(road.OnGround(lane + along * Mathf.Lerp(-behind, rollPast, i / (float)steps), CurrentWheelHeight));
            wheel.localPosition = localPath[0];
        }

        // A wall behind the slot shortens the run-up; a wheel started inside it would end at once.
        private float ClearDistanceBehind(Vector3 lane, Vector3 direction)
        {
            int count = Physics.RaycastNonAlloc(road.OnGround(lane, ClearanceRayHeight), direction, sweepHits, startBehind, collisionMask, QueryTriggerInteraction.Ignore);
            float clear = startBehind;
            for (int i = 0; i < count; i++)
            {
                var hit = sweepHits[i];
                if (hit.collider.transform.IsChildOf(transform) || hit.collider.transform.IsChildOf(rider.transform)) continue;
                clear = Mathf.Min(clear, hit.distance - radius * 2f);
            }
            return Mathf.Max(0f, clear);
        }

        private void Advance()
        {
            Vector3 target = transform.TransformPoint(localPath[segment]);
            Vector3 direction = (target - wheel.position).normalized;
            speed = Mathf.Clamp(speed - direction.y * slopeAcceleration * Time.fixedDeltaTime, 1, roadRun ? (waveLarge ? largeSpeed : smallSpeed) : maximumSpeed);
            float remaining = speed * Time.fixedDeltaTime;
            // Bound segment transitions by path length, including duplicate points.
            for (int guard = 0; guard < localPath.Length && remaining > 0; guard++)
            {
                target = transform.TransformPoint(localPath[segment]);
                Vector3 delta = target - wheel.position;
                float travel = Mathf.Min(delta.magnitude, remaining);
                direction = delta.normalized;
                Vector3 from = wheel.position;
                if (CheckWaveContact(from, direction, travel)) { Finish(); return; }
                wheel.position = from + direction * travel;
                spin = Mathf.Repeat(spin + travel / radius * Mathf.Rad2Deg, 360);
                if (direction.sqrMagnitude > 0.01f)
                    wheel.rotation = Quaternion.LookRotation(direction, Vector3.up) * Quaternion.Euler(spin, 0, 0);
                ShowWave(direction);
                remaining -= travel;
                if (delta.magnitude > travel + 0.0001f) break;
                if (++segment >= localPath.Length) { Finish(); return; }
            }
        }

        private bool CheckContact(Vector3 from, Vector3 direction, float distance)
        {
            // Capsule sweep covers the complete frame displacement, including initial overlap.
            int count = Physics.OverlapSphereNonAlloc(from, radius, overlaps, collisionMask, QueryTriggerInteraction.Ignore);
            if (count == overlaps.Length) return true;
            float playerDistance = float.PositiveInfinity, obstruction = float.PositiveInfinity;
            for (int i = 0; i < count; i++)
            {
                Collider c = overlaps[i];
                if (c.transform.IsChildOf(transform)) continue;
                if (c.transform.IsChildOf(rider.transform)) playerDistance = 0;
                else obstruction = 0; // Authored path must keep the sphere clear of terrain.
            }
            if (distance > 0)
            {
                count = Physics.SphereCastNonAlloc(from, radius, direction, sweepHits, distance, collisionMask, QueryTriggerInteraction.Ignore);
                if (count == sweepHits.Length) return true;
                for (int i = 0; i < count; i++)
                {
                    var hit = sweepHits[i];
                    if (hit.collider.transform.IsChildOf(transform)) continue;
                    if (hit.collider.transform.IsChildOf(rider.transform)) playerDistance = Mathf.Min(playerDistance, hit.distance);
                    else obstruction = Mathf.Min(obstruction, hit.distance);
                }
            }
            // Only the rider query grows/shrinks. Keep the original terrain radius so a large trigger does not
            // make a tire collide with the road it is rolling on, or weaken wall/barrier handling.
            if (TriggerScale != 1f)
            {
                playerDistance = float.PositiveInfinity;
                float reach = radius * TriggerScale;
                count = Physics.OverlapSphereNonAlloc(from, reach, overlaps, collisionMask, QueryTriggerInteraction.Ignore);
                for (int i = 0; i < count; i++) if (overlaps[i].transform.IsChildOf(rider.transform)) playerDistance = 0f;
                if (distance > 0f)
                {
                    count = Physics.SphereCastNonAlloc(from, reach, direction, sweepHits, distance, collisionMask, QueryTriggerInteraction.Ignore);
                    for (int i = 0; i < count; i++)
                        if (sweepHits[i].collider.transform.IsChildOf(rider.transform)) playerDistance = Mathf.Min(playerDistance, sweepHits[i].distance);
                }
            }
            // Check the shared delivery gate again at impact; food recovery remains armed.
            if (!hitThisCycle && (slot == null || slot.StrikeAllowed) && playerDistance <= distance && playerDistance < obstruction)
            {
                hitThisCycle = true;
                Vector3 push = Vector3.ProjectOnPlane(direction, Vector3.up).normalized;
                rider.TryHazardCrash(waveWheels != null ? waveWheels[currentWheel] : (UnityEngine.Object)this, push * (waveLarge ? impulse : smallImpulse) + Vector3.up * (waveLarge ? upwardImpulse : smallUpwardImpulse), recoverySeconds);
            }
            return obstruction <= distance;
        }

        private bool InApproach()
        {
            Vector3 p = transform.InverseTransformPoint(rider.transform.position) - approachCenter;
            Vector3 half = approachSize * (0.5f * TriggerScale);
            return Mathf.Abs(p.x) <= half.x && Mathf.Abs(p.y) <= half.y && Mathf.Abs(p.z) <= half.z;
        }
        private void Enter(Phase value) { phase = value; elapsed = 0; }
        private void SetLamp(bool on) { if (warningLamp != null) warningLamp.enabled = on; }
        private void ResetWheel() { spin = 0; wheel.localPosition = roadRun ? parkedLocal + Vector3.up * (CurrentWheelHeight - WheelHeight) : localPath[0]; wheel.localRotation = initialRotation; wheel.gameObject.SetActive(true); }
        private void Finish() { if (phase != Phase.Spent) Enter(Phase.Spent); SetLamp(false); wheel.gameObject.SetActive(false); HideWave(); }
        private void OnDrawGizmosSelected()
        {
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireCube(approachCenter, approachSize);
            if (localPath == null) return;
            Gizmos.color = Color.cyan;
            for (int i = 1; i < localPath.Length; i++) Gizmos.DrawLine(localPath[i-1], localPath[i]);
        }
    }
}
