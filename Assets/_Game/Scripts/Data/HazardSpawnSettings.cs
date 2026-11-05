using System;
using System.Collections.Generic;
using UnityEngine;

namespace DeliveryRider
{
    // How many hazards of each type the planner spawns at random candidates each run. Every number is a test value.
    public sealed class HazardSpawnSettings : ScriptableObject
    {
        [Serializable]
        public sealed class Kind
        {
            public string type;
            public GameObject prefab;
            public HazardRole role;
            [Min(1)] public int unlockDay = 1;
            [Tooltip("Objects: how many are placed on day 1, 2, ...; the last value repeats.")]
            public int[] countPerDay = Array.Empty<int>();
            [Tooltip("Triggers: reserved daily slots; each accepted call activates a subset.")]
            [Min(0)] public int poolSize = 10;
            [Tooltip("HTML whole-map pool split, independently scaled by open main/alley road length.")]
            public int mainPoolSize = -1;
            public int alleyPoolSize;
            [Range(0f, 1f)] public float objectAlleyShare;

            [Tooltip("Whole-map main-road slots for days 1 to 5; day 5 repeats thereafter.")]
            public int[] mainCountPerDay = Array.Empty<int>();
            [Tooltip("Whole-map car-free-road slots for days 1 to 5; day 5 repeats thereafter.")]
            public int[] alleyCountPerDay = Array.Empty<int>();

            private static int DayCount(int[] counts, int day, int fallback) => counts == null || counts.Length == 0
                ? fallback : Mathf.Max(0, counts[Mathf.Clamp(day - 1, 0, Mathf.Min(4, counts.Length - 1))]);
            public int MainCount(int day) => DayCount(mainCountPerDay, day,
                mainPoolSize >= 0 ? mainPoolSize : poolSize - Mathf.RoundToInt(poolSize * objectAlleyShare));
            public int AlleyCount(int day) => DayCount(alleyCountPerDay, day,
                mainPoolSize >= 0 ? alleyPoolSize : Mathf.RoundToInt(poolSize * objectAlleyShare));
            public int Count(int day) => role == HazardRole.Trigger ? MainCount(day) + AlleyCount(day)
                : DayCount(countPerDay, day, 0);
        }

        [Serializable]
        public sealed class ComboMember
        {
            public string type;
            public float along;
            public int side = 1;
            // Negative: the type's default transverse offset.
            public float lateralFraction = -1f;
        }
        [Serializable]
        public sealed class ComboTemplate
        {
            public string name;
            public bool alley;
            public int count;
            public ComboMember[] members = Array.Empty<ComboMember>();
        }
        [SerializeField] private Vector3[] placementKeepouts = Array.Empty<Vector3>();
        public IReadOnlyList<Vector3> PlacementKeepouts => placementKeepouts;
        public void ConfigureKeepouts(Vector3[] points) => placementKeepouts = points;
        [SerializeField] private ComboTemplate[] combos = Array.Empty<ComboTemplate>();
        public IReadOnlyList<ComboTemplate> Combos => combos;
        public void ConfigureCombos(ComboTemplate[] value) => combos = value;
        [SerializeField] private Kind[] kinds = Array.Empty<Kind>();
        [Tooltip("Smallest distance between any two hazards.")]
        [SerializeField, Min(0f)] private float spacing = 8f;
        [Tooltip("Smallest distance between two hazards of the same type.")]
        [SerializeField, Min(0f)] private float sameTypeSpacing = 25f;

        public IReadOnlyList<Kind> Kinds => kinds;
        public float Spacing => spacing;
        public float SameTypeSpacing => sameTypeSpacing;

        public void Configure(Kind[] hazardKinds) => kinds = hazardKinds;
    }
}
