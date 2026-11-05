using System.Collections.Generic;
using UnityEngine;

namespace DeliveryRider
{
    // Objects remain armed; call-selected triggers share one delivery-stage gate.
    public sealed partial class HazardPlanner : MonoBehaviour
    {
        [SerializeField] private DeliverySession session;
        [SerializeField] private RiderMotor rider;
        [SerializeField] private RoadNetwork ground;
        [SerializeField] private HazardSpawnSettings settings;
        [SerializeField] private HazardCandidates candidates;
        [Tooltip("Off: always use the seed below, to reproduce a layout.")]
        [SerializeField] private bool randomEachRun = true;
        [SerializeField] private int seed = 11;
        [Tooltip("On: selected triggers strike throughout delivery, including food recovery. Off: selected triggers strike before pickup too.")]
        [SerializeField] private bool triggersRequireFood = true;
        [Header("Food knocked loose by a hazard (test values)")]
        [SerializeField, Range(0f, 1f)] private float foodDropChance = 0.18f;
        [SerializeField, Min(0.1f)] private float foodPickupRadius = 1.8f;
        [Tooltip("Share of the rider's launch speed the food keeps sideways.")]
        [SerializeField, Min(0f)] private float foodSpeedShare = 0.55f;
        [Tooltip("Random sideways speed added on each axis.")]
        [SerializeField, Min(0f)] private float foodSpreadSpeed = 5f;
        [Tooltip("Upward speed = rider's launch upward speed x this + foodExtraUp.")]
        [SerializeField, Min(0f)] private float foodUpShare = 0.7f;
        [SerializeField, Min(0f)] private float foodExtraUp = 5f;
        [Header("함정 배율")]
        [Tooltip("종류별 열린 구역 개수를 계산한 뒤 곱합니다. 사건 다시 배치로 적용합니다.")]
        [SerializeField, Range(0.25f, 5f)] private float hazardCountScale = 1f;
        [Tooltip("인스턴스의 라이더 감지 거리와 콜라이더에 즉시 적용합니다.")]
        [SerializeField, Range(0.5f, 4f)] private float hazardTriggerScale = 1f;
        private float appliedTriggerScale = 1f;
        private readonly Dictionary<string, int> requestedCounts = new Dictionary<string, int>();
        public IReadOnlyDictionary<string, int> RequestedCounts => requestedCounts;
        private DroppedFood droppedFood;
        private HazardZoneFilter openRoads;
        private PedestrianNetwork placementSidewalks;
        private bool appliedFoodRule;
        private readonly List<HazardSlot> slots = new List<HazardSlot>();
        private int day = 1;
        public int LiveTriggers
        {
            get
            {
                int count = 0;
                foreach (var slot in slots) if (slot != null && slot.Role == HazardRole.Trigger && slot.RuntimeActive) count++;
                return count;
            }
        }
        public IReadOnlyList<HazardSlot> Slots => slots;
        // Raised for each new hazard before it is placed and woken, so developer tools can apply tuned values first.
        public event System.Action<GameObject, string> HazardSpawned;

        private void Start()
        {
            day = session.Progress != null ? session.Progress.Day : 1;
            Spawn();
            OnSessionChanged();
        }

        // Resolve grouped and independent safe candidates before instantiating the day's fixed slot pool.
        private void Spawn()
        {
            if (settings == null || candidates == null || ground == null) { Debug.LogError("Hazard planner needs spawn settings, candidates and the road network.", this); return; }
            requestedCounts.Clear();
            var random = randomEachRun ? new System.Random() : new System.Random(seed);
            openRoads = new HazardZoneFilter(session.ZoneMap, session.IsZoneOpen);
            placementSidewalks=FindFirstObjectByType<PedestrianNetwork>();
            var staging = new GameObject("HazardStaging"); staging.SetActive(false);
            PrepareDailyCandidates();
            PlaceDailyLayout(random, staging.transform);
            appliedTriggerScale = hazardTriggerScale;
            if (Application.isPlaying) Destroy(staging);
            else DestroyImmediate(staging);
        }

        // The hazard itself and the road spot it belongs to must both be on the open side of every closed wall.
        private bool CandidateOpen(HazardCandidate candidate)
        {
            if (!openRoads.PointOpen(candidate.position)) return false;
            foreach (var point in settings.PlacementKeepouts)
                if (Vector3.ProjectOnPlane(candidate.position - point, Vector3.up).sqrMagnitude < 64f) return false;
            if (candidate.type == "theft" && PigeonSnatchHazard.OpenEscapePoints(candidate, openRoads, PigeonSnatchHazard.MinimumEscapePoints) < PigeonSnatchHazard.MinimumEscapePoints) return false;
            var spots = candidates.Spots;
            return candidate.spot < 0 || candidate.spot >= spots.Count || openRoads.PointOpen(spots[candidate.spot].position);
        }

        // Developer tools: replaces today's hazards with a fresh layout from the current settings. Switching a hazard
        // off first runs its teardown (chicken control and slips end; a pigeon holding food sends the rider back to
        // the shop).
        public void Respawn()
        {
            foreach (var slot in slots)
                if (slot != null)
                {
                    slot.gameObject.SetActive(false);
                    if (Application.isPlaying) Destroy(slot.gameObject);
                    else DestroyImmediate(slot.gameObject);
                }
            slots.Clear();
            rolledOrder = null;
            Spawn();
            OnSessionChanged();
        }

        private HazardSlot SpawnOne(HazardSpawnSettings.Kind kind, HazardCandidate spot, Transform staging)
        {
            var obj = Instantiate(kind.prefab, spot.position, spot.rotation, staging);
            obj.name = $"{slots.Count:00}_{kind.type}" + (spot.alley ? "_alley" : "");
            HazardSpawned?.Invoke(obj, kind.type);
            var crowd=obj.GetComponent<CelebrityCrowdHazard>();
            if(crowd!=null&&placementInfo.TryGetValue(spot,out var placement))crowd.PrepareCenter(placement.centerPath,placement.centerAlong);
            foreach (var hazard in obj.GetComponents<IPlacedHazard>()) hazard.Place(session, ground, spot);
            var slot = obj.AddComponent<HazardSlot>();
            slot.Configure(kind.type, kind.role, kind.unlockDay, candidates.Routes(spot.spot));
            slot.Planner = this;
            slot.Candidate = spot;
            slot.StrikeAllowed = kind.role == HazardRole.Object;
            slot.SetTriggerScale(hazardTriggerScale);
            obj.SetActive(true);
            obj.transform.SetParent(transform, true);
            slots.Add(slot);
            if (kind.role == HazardRole.Trigger) slot.SetRuntimeActive(false);
            return slot;
        }

        private void OnEnable()
        {
            session.Changed += OnSessionChanged;
            if (rider != null) rider.HazardHit += OnRiderHazardHit;
        }

        private void OnDisable()
        {
            session.Changed -= OnSessionChanged;
            if (rider != null) rider.HazardHit -= OnRiderHazardHit;
        }

        private void OnSessionChanged()
        {
            RefreshCall();
            appliedFoodRule = triggersRequireFood;
            foreach (var slot in slots)
                if (slot != null) slot.StrikeAllowed = CanStrike(slot) && (slot.Role == HazardRole.Object || slot.Selected);
            ApplyPendingSlots();
        }

        private bool CanStrike(HazardSlot slot)
        {
            return slot.Role == HazardRole.Object || !triggersRequireFood && slot.Type != "theft" && slot.Type != "clone" || DeliveryInProgress(session);
        }

        public static bool DeliveryInProgress(DeliverySession owner) => owner != null
            && (owner.Stage == DeliveryStage.Carrying || owner.Stage == DeliveryStage.FoodLost);

        // The developer panel can flip the rule mid-run, between session changes.
        private void Update()
        {
            ApplyPendingSlots();
            if (appliedFoodRule != triggersRequireFood) OnSessionChanged();
            if (appliedTriggerScale == hazardTriggerScale) return;
            appliedTriggerScale = hazardTriggerScale;
            foreach (var slot in slots) if (slot != null) slot.SetTriggerScale(hazardTriggerScale);
        }

        // Melons are the weak hits that never cost the food.
        private void OnRiderHazardHit(Object source)
        {
            Ignite(source);
            if (source is CatBoxerHazard cat && cat.KeepsFoodOnCurrentHit) return;
            if (source is Component hitPart && hitPart.GetComponentInParent<RollingTireHazard>(true) is RollingTireHazard tire && tire.KeepsFoodOnCurrentHit) return;
            if (source is RollingMelon || droppedFood != null || session.Stage != DeliveryStage.Carrying || Random.value >= foodDropChance) return;
            var launch = rider.ContactVelocity;
            var sideways = Vector3.ProjectOnPlane(launch, Vector3.up) * foodSpeedShare
                + new Vector3(Random.Range(-foodSpreadSpeed, foodSpreadSpeed), 0f, Random.Range(-foodSpreadSpeed, foodSpreadSpeed));
            float up = Mathf.Max(0f, launch.y) * foodUpShare + foodExtraUp;
            var start = rider.transform.position;
            var landing = start + sideways * (2f * up / Mathf.Max(0.01f, -Physics.gravity.y * rider.AccidentGravityScale));
            var recovery = rider.GetComponent<RiderSafeRecovery>();
            if (recovery != null) landing = FoodRestingPoint(recovery, session.ZoneMap != null ? openRoads : null, landing, start);
            droppedFood = new GameObject("DroppedFood").AddComponent<DroppedFood>();
            droppedFood.Launch(session, rider.transform, start, ground.OnGround(landing, 0.25f), up, foodPickupRadius, StolenFoodLook());
        }

        // Food always comes to rest where the rider can walk to it: on a road, never on a roof or behind a closed wall.
        public static Vector3 FoodRestingPoint(RiderSafeRecovery recovery, HazardZoneFilter roads, Vector3 landing, Vector3 fallback)
            => roads != null && recovery.TryFindOpenRoad(landing, roads, out var open) ? open : recovery.FindSafePosition(landing, fallback, roads);

        // The food looks the same as the bag a thief carries; without that art the guide marker still leads to it.
        private GameObject StolenFoodLook()
        {
            foreach (var kind in settings.Kinds)
            {
                if (kind.type != "theft" || kind.prefab == null) continue;
                var thief = kind.prefab.GetComponentInChildren<FoodThief>(true);
                var bag = thief != null ? thief.transform.Find("StolenFood") : null;
                if (bag != null) return bag.gameObject;
            }
            return null;
        }

    }
}
