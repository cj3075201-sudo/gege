using System;
using UnityEngine;

namespace DeliveryRider
{
    // Objects are always placed for the day and strike any time (even before pickup).
    // Placed triggers are active without a call. Only the food thief requires carried food.
    public enum HazardRole { Object, Trigger }

    // Planner data for one spawned hazard; HazardPlanner adds it at spawn and turns the hazard on and off.
    [DisallowMultipleComponent]
    public sealed class HazardSlot : MonoBehaviour
    {
        [SerializeField] private string type;
        [SerializeField] private HazardRole role;
        [SerializeField, Min(1)] private int unlockDay = 1;
        [Tooltip("Call routes (shop index x door count + door index) that pass within 7 m.")]
        [SerializeField] private int[] routes = Array.Empty<int>();

        public string Type => type;
        public HazardRole Role => role;
        public int UnlockDay => unlockDay;
        // Set by the planner; hazards check it before starting an attack.
        public bool StrikeAllowed { get; set; } = true;

        public bool IsOnRoute(int route) => route >= 0 && Array.IndexOf(routes, route) >= 0;

        public void Configure(string slotType, HazardRole slotRole, int slotUnlockDay, int[] slotRoutes)
        {
            type = slotType;
            role = slotRole;
            unlockDay = slotUnlockDay;
            routes = slotRoutes ?? Array.Empty<int>();
        }
    }
}
