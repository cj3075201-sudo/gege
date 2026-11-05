using UnityEngine;

namespace DeliveryRider
{
    // Duck flock: the ducks wait in sight at the kerb; when the rider heads for their crossing line they set off so the
    // middle of the line reaches the rider's lane just as the rider does. Touching one bounces the rider off.
    public sealed class CrossingFlockHazard : MonoBehaviour, IPlacedHazard
    {
        private enum Phase { Ready, Crossing, Spent }
        [SerializeField] private DeliverySession session;
        [SerializeField] private Transform[] birds = System.Array.Empty<Transform>();
        [Tooltip("Crossing line in local space; birds walk from start to end.")]
        [SerializeField] private Vector3 localStart;
        [SerializeField] private Vector3 localEnd;
        [Tooltip("Close range that always starts the crossing (and must be left before it re-arms).")]
        [SerializeField] private Vector3 approachCenter = new Vector3(0f, 1f, 0f);
        [SerializeField] private Vector3 approachSize = new Vector3(12f, 3f, 16f);
        [Header("Test values")]
        [Tooltip("Furthest distance along the road at which the timed start looks for an approaching rider.")]
        [SerializeField, Min(0f)] private float maximumLeadDistance = 50f;
        [Tooltip("Start this much earlier than the computed meeting time, so the line is already on the road.")]
        [SerializeField, Min(0f)] private float arrivalMargin = 0.3f;
        [SerializeField, Min(0.1f)] private float waitingSpacing = 0.7f;
        [SerializeField, Min(0.1f)] private float walkSpeed = 2.6f;
        [SerializeField, Min(0.1f)] private float spacing = 1.1f;
        [SerializeField, Min(0.1f)] private float hitRadius = 0.7f;
        [SerializeField, Min(0f)] private float bounceImpulse = 6.5f;
        [SerializeField, Min(0f)] private float bounceLift = 3f;
        [SerializeField, Min(0.1f)] private float recoverySeconds = 0.8f;
        private RiderMotor rider;
        private HazardSlot slot;
        private Phase phase;
        private float head;
        private bool hitThisCrossing;

        // Runtime spawn: the ducks cross kerb to kerb (2 m past each kerb), from the slot's side.
        public void Place(DeliverySession owner, RoadNetwork ground, HazardCandidate spot)
        {
            session = owner;
            float reach = spot.halfWidth + 2f;
            var across = transform.right * spot.side * reach;
            localStart = transform.InverseTransformPoint(ground.OnGround(transform.position + across, 0f));
            localEnd = transform.InverseTransformPoint(ground.OnGround(transform.position - across, 0f));
            approachSize = new Vector3(spot.halfWidth * 2f + 4f, 3f, 16f);
        }

        private void Awake()
        {
            rider = session != null && session.Player != null ? session.Player.GetComponent<RiderMotor>() : null;
            if (rider == null || birds.Length == 0) { enabled = false; return; }
            slot = GetComponent<HazardSlot>();
            HideBirds();
        }

        private void OnDisable() { HideBirds(); phase = Phase.Spent; }
        private void OnEnable() { if (rider != null) { phase = Phase.Ready; ShowWaiting(); } }

        private void Update() { if (session.IsSettled && phase == Phase.Crossing) Finish(); }

        private void FixedUpdate()
        {
            if (session.IsPaused || session.IsSettled) return;
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
                    // Leaving the approach area re-arms this crossing without a new call.
                    if (!InApproach()) { phase = Phase.Ready; ShowWaiting(); }
                    break;
            }
        }

        // True when the rider, heading for the crossing line, would reach it no later than the middle of the duck line
        // reaches the rider's lane (with arrivalMargin to spare).
        private bool MeetsRider()
        {
            Vector3 start = transform.TransformPoint(localStart), end = transform.TransformPoint(localEnd);
            var line = Vector3.ProjectOnPlane(end - start, Vector3.up);
            float length = line.magnitude;
            if (length < 0.1f) return false;
            var local = transform.InverseTransformPoint(rider.transform.position);
            if (Mathf.Abs(local.z) > maximumLeadDistance || Mathf.Abs(local.y) > 3f || Mathf.Abs(local.x - approachCenter.x) > approachSize.x * 0.5f) return false;
            float closing = -Mathf.Sign(local.z) * transform.InverseTransformDirection(rider.Velocity).z;
            if (closing < 0.5f) return false;
            float lateral = Mathf.Clamp(Vector3.Dot(Vector3.ProjectOnPlane(rider.transform.position - start, Vector3.up), line / length), 0f, length);
            float ducks = (lateral + (birds.Length - 1) * spacing * 0.5f) / walkSpeed;
            return Mathf.Abs(local.z) / closing <= ducks + arrivalMargin;
        }

        private void Begin()
        {
            ShowWaiting();
            head = 0f;
            hitThisCrossing = false;
            phase = Phase.Crossing;
        }

        // The flock waits in a short row along the kerb at the start of its crossing line.
        private void ShowWaiting()
        {
            Vector3 start = transform.TransformPoint(localStart), end = transform.TransformPoint(localEnd);
            var facing = Quaternion.LookRotation(Vector3.ProjectOnPlane(end - start, Vector3.up));
            var along = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
            for (int i = 0; i < birds.Length; i++)
            {
                birds[i].SetPositionAndRotation(start + along * ((i - (birds.Length - 1) * 0.5f) * waitingSpacing), facing);
                birds[i].gameObject.SetActive(true);
            }
        }

        private void Advance()
        {
            head += walkSpeed * Time.fixedDeltaTime;
            Vector3 start = transform.TransformPoint(localStart), end = transform.TransformPoint(localEnd);
            float length = Vector3.Distance(start, end);
            Vector3 player = rider.transform.position;
            for (int i = 0; i < birds.Length; i++)
            {
                float along = head - i * spacing;
                // Ducks not yet on the line keep waiting in sight at the kerb.
                if (along < 0f) continue;
                bool visible = along <= length;
                if (birds[i].gameObject.activeSelf != visible) birds[i].gameObject.SetActive(visible);
                if (!visible) continue;
                birds[i].position = Vector3.Lerp(start, end, along / length);
                birds[i].rotation = Quaternion.LookRotation(end - start);
                Vector3 offset = player - birds[i].position;
                if (hitThisCrossing || slot != null && !slot.StrikeAllowed || Mathf.Abs(offset.y) > 1.8f || new Vector2(offset.x, offset.z).sqrMagnitude > hitRadius * hitRadius) continue;
                hitThisCrossing = true;
                var push = Vector3.ProjectOnPlane(offset, Vector3.up).normalized;
                if (push.sqrMagnitude < 0.01f) push = -transform.forward;
                rider.TryHazardCrash(this, push * bounceImpulse + Vector3.up * bounceLift, recoverySeconds);
            }
            if (head - (birds.Length - 1) * spacing > length) Finish();
        }

        private bool InApproach()
        {
            Vector3 p = transform.InverseTransformPoint(rider.transform.position) - approachCenter;
            Vector3 half = approachSize * 0.5f;
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
