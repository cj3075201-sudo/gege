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
            [Tooltip("Triggers: how many are placed and remain active independently of calls.")]
            [Min(0)] public int poolSize = 10;

            public int Count(int day) => role == HazardRole.Trigger ? poolSize
                : countPerDay.Length == 0 ? 0 : countPerDay[Mathf.Clamp(day - 1, 0, countPerDay.Length - 1)];
        }

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
