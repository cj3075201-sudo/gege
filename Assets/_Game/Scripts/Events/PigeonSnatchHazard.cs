using UnityEngine;

namespace DeliveryRider
{
    // Pigeon snatch: a perched pigeon swoops on a rider carrying food, grabs it and flies off along the road.
    // Recovery reuses FoodThief: punch the pigeon to get the food back, or re-collect at the shop if it escapes.
    public sealed class PigeonSnatchHazard : MonoBehaviour, IPlacedHazard
    {
        private enum Phase { Ready, Warning, Swoop, Flying, Spent }
        // Shorter than this (16 m, about 3 s at flySpeed) the rider has no real chase, so the place is not used.
        public const int MinimumEscapePoints = 5;
        private const float EscapeStart = 4f, EscapeSpacing = 3f;
        [SerializeField] private DeliverySession session;
        [SerializeField] private FoodThief pigeon;
        [SerializeField] private Transform perch;
        [SerializeField] private Transform[] escapeRoute = System.Array.Empty<Transform>();
        [SerializeField] private Renderer warningLamp;
        [Header("Test values")]
        [SerializeField, Min(0.1f)] private float triggerRadius = 4f;
        [SerializeField, Min(0.05f)] private float warningSeconds = 0.5f;
        [SerializeField, Min(0.05f)] private float swoopSeconds = 0.35f;
        [SerializeField, Min(0.1f)] private float flySpeed = 5.4f;
        [SerializeField, Min(1f)] private float escapeSeconds = 35f;
        private RiderMotor rider;
        private HazardSlot slot;
        private float TriggerScale => slot != null ? slot.TriggerScale : 1f;
        private Phase phase;
        private float elapsed;
        private Vector3 swoopFrom;

        // Runtime spawn: the pole stands beside the road; the escape route runs along the road from the slot.
        public void Place(DeliverySession owner, RoadNetwork ground, HazardCandidate spot)
        {
            session = owner;
            perch.rotation = Quaternion.LookRotation(-transform.right * spot.side);
            if (pigeon != null) pigeon.transform.SetPositionAndRotation(perch.position, perch.rotation);
            // The flight ends before the first closed point: the rider could never follow it past the wall.
            int open = OpenEscapePoints(spot, new HazardZoneFilter(owner.ZoneMap, owner.IsZoneOpen), escapeRoute.Length);
            System.Array.Resize(ref escapeRoute, Mathf.Max(1, open));
            for (int i = 0; i < escapeRoute.Length; i++)
                escapeRoute[i].position = ground.OnGround(EscapePoint(spot, i), 1.5f);
        }

        public static Vector3 EscapePoint(HazardCandidate spot, int index)
        {
            var center = spot.position - spot.rotation * Vector3.right * (spot.side * (spot.halfWidth + 1f));
            var direction = spot.rotation * (spot.flip ? Vector3.back : Vector3.forward);
            return center + direction * (EscapeStart + index * EscapeSpacing);
        }

        // How many route points come before the first closed one, looking at no more than the given count.
        public static int OpenEscapePoints(HazardCandidate spot, HazardZoneFilter roads, int count)
        {
            for (int i = 0; i < count; i++)
                if (!roads.PointOpen(EscapePoint(spot, i))) return i;
            return count;
        }

        private void Awake()
        {
            rider = session != null && session.Player != null ? session.Player.GetComponent<RiderMotor>() : null;
            if (rider == null || pigeon == null || perch == null || escapeRoute.Length == 0) { enabled = false; return; }
            slot = GetComponent<HazardSlot>();
            Perch();
        }

        private void OnEnable() { if (rider != null) { Perch(); phase = slot!=null&&slot.StartedFor(session)?Phase.Spent:Phase.Ready; } }
        private void OnDisable()
        {
            SetLamp(false);
            // A consumed/hidden slot cannot leave an invisible recovery target behind.
            if(phase==Phase.Flying&&session!=null&&pigeon!=null)
            {
                session.RequireReplacement(pigeon.transform);
                pigeon.Rest();
            }
            if(pigeon!=null)pigeon.gameObject.SetActive(false);
            phase=Phase.Spent;
        }

        private void Update() { if (session.IsSettled && phase != Phase.Spent && phase != Phase.Flying) { SetLamp(false); Perch(); phase = Phase.Spent; } }

        private void FixedUpdate()
        {
            if (session.IsPaused || session.IsSettled) return;
            elapsed += Time.fixedDeltaTime;
            switch (phase)
            {
                case Phase.Ready:
                    if ((slot == null || slot.StrikeAllowed) && !session.IsMapOpen && session.Stage == DeliveryStage.Carrying
                        && !rider.IsRecovering && Vector3.Distance(rider.transform.position, perch.position) <= triggerRadius * TriggerScale)
                    {
                        slot?.MarkStarted(session);
                        Enter(Phase.Warning);
                    }
                    break;
                case Phase.Warning:
                    SetLamp(Mathf.Repeat(elapsed, 0.2f) < 0.12f);
                    if (session.IsMapOpen || session.Stage != DeliveryStage.Carrying) { SetLamp(false); Perch(); Enter(Phase.Spent); break; }
                    if (elapsed >= warningSeconds) { SetLamp(false); swoopFrom = pigeon.transform.position; Enter(Phase.Swoop); }
                    break;
                case Phase.Swoop:
                    Vector3 head = rider.transform.position + Vector3.up * 0.6f;
                    pigeon.transform.position = Vector3.Lerp(swoopFrom, head, Mathf.Clamp01(elapsed / swoopSeconds));
                    if (elapsed < swoopSeconds) break;
                    if (session.TryLoseFood(pigeon.transform))
                    {
                        pigeon.Initialize(session, escapeRoute, flySpeed, escapeSeconds);
                        Enter(Phase.Flying);
                    }
                    else { Perch(); Enter(Phase.Spent); }
                    break;
                case Phase.Flying:
                    if (pigeon.Finished || !pigeon.gameObject.activeInHierarchy) { Perch(); Enter(Phase.Spent); }
                    break;
                case Phase.Spent:
                    if ((slot==null||slot.Role!=HazardRole.Trigger)&&session.Stage == DeliveryStage.Carrying && Vector3.Distance(rider.transform.position, perch.position) > triggerRadius * 2f * TriggerScale)
                    {
                        Perch();
                        Enter(Phase.Ready);
                    }
                    break;
            }
        }

        private void Perch()
        {
            if (pigeon == null || perch == null) return;
            pigeon.Rest();
            pigeon.gameObject.SetActive(true);
            pigeon.transform.SetPositionAndRotation(perch.position, perch.rotation);
            var body = pigeon.GetComponent<Rigidbody>();
            if (body != null && !body.isKinematic) body.linearVelocity = Vector3.zero;
        }

        private void Enter(Phase value) { phase = value; elapsed = 0f; }
        private void SetLamp(bool on) { if (warningLamp != null) warningLamp.enabled = on; }
    }
}
