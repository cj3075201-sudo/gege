using System.Collections.Generic;
using UnityEngine;

namespace DeliveryRider
{
    // Every placed hazard is active without a call; only theft needs food.
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
            var shuffled = new List<int>();
            // Prefabs are instantiated under an inactive parent so they can be fitted before they wake.
            var staging = new GameObject("HazardStaging");
            staging.SetActive(false);
            for (int priority = 0; priority < 3; priority++)
                foreach (var kind in settings.Kinds)
                {
                    int placementPriority = kind.type == "pigeonflock" ? 0 : kind.role == HazardRole.Object ? 1 : 2;
                    if (placementPriority != priority || kind.prefab == null || kind.unlockDay > day) continue;
                    int want = kind.Count(day), count = 0;
                    shuffled.Clear();
                    for (int i = 0; i < candidates.Candidates.Count; i++)
                        if (candidates.Candidates[i].type == kind.type) shuffled.Add(i);
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
            slot.StrikeAllowed = kind.type != "theft" || session.Stage == DeliveryStage.Carrying;
            obj.SetActive(true);
            obj.transform.SetParent(transform, true);
            slots.Add(slot);
        }

        private void OnEnable()
        {
            session.Changed += OnSessionChanged;
        }

        private void OnDisable()
        {
            session.Changed -= OnSessionChanged;
        }

        private void OnSessionChanged()
        {
            foreach (var slot in slots)
                if (slot != null) slot.StrikeAllowed = slot.Type != "theft" || session.Stage == DeliveryStage.Carrying;
        }

    }
}
