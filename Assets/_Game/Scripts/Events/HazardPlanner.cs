using System.Collections.Generic;
using UnityEngine;

namespace DeliveryRider
{
    // Objects are active all the time; triggers wait for the food when triggersRequireFood is on. Theft always needs food.
    public sealed class HazardPlanner : MonoBehaviour
    {
        [SerializeField] private DeliverySession session;
        [SerializeField] private RiderMotor rider;
        [SerializeField] private RoadNetwork ground;
        [SerializeField] private HazardSpawnSettings settings;
        [SerializeField] private HazardCandidates candidates;
        [Tooltip("Off: always use the seed below, to reproduce a layout.")]
        [SerializeField] private bool randomEachRun = true;
        [SerializeField] private int seed = 11;
        [Tooltip("On: triggers strike only while the rider carries the food (HTML prototype). Off: they strike regardless (team rule).")]
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
        [SerializeField, Min(0f)] private float foodExtraUp = 2.23f;
        private DroppedFood droppedFood;
        private HazardZoneFilter openRoads;
        private bool appliedFoodRule;
        private readonly List<HazardSlot> slots = new List<HazardSlot>();
        private int day = 1;
        public int LiveTriggers
        {
            get
            {
                int count = 0;
                foreach (var slot in slots) if (slot != null && slot.Role == HazardRole.Trigger && slot.gameObject.activeSelf) count++;
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

        // Reserve the flock's scarce main-road candidates first, then place objects and the other triggers.
        // All passes use the same spacing rules; a later type cannot consume a reserved flock location.
        private void Spawn()
        {
            if (settings == null || candidates == null || ground == null) { Debug.LogError("Hazard planner needs spawn settings, candidates and the road network.", this); return; }
            var random = randomEachRun ? new System.Random() : new System.Random(seed);
            var placed = new List<(Vector3 position, string type)>();
            openRoads = new HazardZoneFilter(session.ZoneMap, session.IsZoneOpen);
            float openShare = openRoads.OpenRoadShare(ground.Roads);
            var shuffled = new List<int>();
            // Prefabs are instantiated under an inactive parent so they can be fitted before they wake.
            var staging = new GameObject("HazardStaging");
            staging.SetActive(false);
            for (int priority = 0; priority < 3; priority++)
                foreach (var kind in settings.Kinds)
                {
                    int placementPriority = kind.type == "pigeonflock" ? 0 : kind.role == HazardRole.Object ? 1 : 2;
                    if (placementPriority != priority || kind.prefab == null || kind.unlockDay > day) continue;
                    // Settings hold whole-map counts, so an open part of the map keeps the same hazards per metre.
                    int want = HazardZoneFilter.ScaleCount(kind.Count(day), openShare), count = 0;
                    shuffled.Clear();
                    for (int i = 0; i < candidates.Candidates.Count; i++)
                        if (candidates.Candidates[i].type == kind.type && CandidateOpen(candidates.Candidates[i])) shuffled.Add(i);
                    for (int i = shuffled.Count - 1; i > 0; i--) { int j = random.Next(i + 1); (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]); }
                    foreach (int index in shuffled)
                    {
                        if (count >= want) break;
                        var spot = candidates.Candidates[index];
                        if (!HasRoom(placed, spot.position, kind.type)) continue;
                        SpawnOne(kind, spot, staging.transform);
                        placed.Add((spot.position, kind.type));
                        count++;
                    }
                    if (count < want) Debug.LogWarning($"Hazards: placed {count} of {want} {kind.type} (not enough spaced candidates).", this);
                }
            Destroy(staging);
        }

        // The hazard itself and the road spot it belongs to must both be on the open side of every closed wall.
        private bool CandidateOpen(HazardCandidate candidate)
        {
            if (!openRoads.PointOpen(candidate.position)) return false;
            var spots = candidates.Spots;
            return candidate.spot < 0 || candidate.spot >= spots.Count || openRoads.PointOpen(spots[candidate.spot].position);
        }

        // Developer tools: replaces today's hazards with a fresh layout from the current settings. Switching a hazard
        // off first runs its teardown (chicken control and slips end; a pigeon holding food sends the rider back to
        // the shop).
        public void Respawn()
        {
            foreach (var slot in slots)
                if (slot != null) { slot.gameObject.SetActive(false); Destroy(slot.gameObject); }
            slots.Clear();
            Spawn();
            OnSessionChanged();
        }

        private bool HasRoom(List<(Vector3 position, string type)> placed, Vector3 position, string type)
        {
            foreach (var other in placed)
            {
                float gap = other.type == type ? settings.SameTypeSpacing : settings.Spacing;
                var offset = other.position - position;
                if (offset.x * offset.x + offset.z * offset.z < gap * gap) return false;
            }
            return true;
        }

        private void SpawnOne(HazardSpawnSettings.Kind kind, HazardCandidate spot, Transform staging)
        {
            var obj = Instantiate(kind.prefab, spot.position, spot.rotation, staging);
            obj.name = $"{slots.Count:00}_{kind.type}" + (spot.alley ? "_alley" : "");
            HazardSpawned?.Invoke(obj, kind.type);
            foreach (var hazard in obj.GetComponents<IPlacedHazard>()) hazard.Place(session, ground, spot);
            var slot = obj.AddComponent<HazardSlot>();
            slot.Configure(kind.type, kind.role, kind.unlockDay, candidates.Routes(spot.spot));
            slot.StrikeAllowed = CanStrike(slot);
            obj.SetActive(true);
            obj.transform.SetParent(transform, true);
            slots.Add(slot);
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
            appliedFoodRule = triggersRequireFood;
            foreach (var slot in slots)
                if (slot != null) slot.StrikeAllowed = CanStrike(slot);
        }

        private bool CanStrike(HazardSlot slot)
        {
            bool needsFood = slot.Type == "theft" || triggersRequireFood && slot.Role == HazardRole.Trigger;
            return !needsFood || session.Stage == DeliveryStage.Carrying;
        }

        // The developer panel can flip the rule mid-run, between session changes.
        private void Update()
        {
            if (appliedFoodRule != triggersRequireFood) OnSessionChanged();
        }

        // Melons are the weak hits that never cost the food.
        private void OnRiderHazardHit(Object source)
        {
            if (source is RollingMelon || droppedFood != null || session.Stage != DeliveryStage.Carrying || Random.value >= foodDropChance) return;
            var launch = rider.ContactVelocity;
            var sideways = Vector3.ProjectOnPlane(launch, Vector3.up) * foodSpeedShare
                + new Vector3(Random.Range(-foodSpreadSpeed, foodSpreadSpeed), 0f, Random.Range(-foodSpreadSpeed, foodSpreadSpeed));
            float up = Mathf.Max(0f, launch.y) * foodUpShare + foodExtraUp;
            var start = rider.transform.position;
            var landing = start + sideways * (2f * up / Mathf.Max(0.01f, -Physics.gravity.y));
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
