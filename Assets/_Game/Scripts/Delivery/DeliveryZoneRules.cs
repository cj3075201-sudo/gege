using System;
using System.Collections.Generic;

namespace DeliveryRider
{
    // HTML v5.5 zone-call rules. No scene state or Unity random state is read here.
    public static class DeliveryZoneRules
    {
        public static List<string> OpenZonesForDay(int day, IReadOnlyList<string> zoneOrder, IReadOnlyList<int> openCountByDay)
        {
            var zones = new List<string>();
            if (zoneOrder == null || zoneOrder.Count == 0) return zones;
            int count = openCountByDay == null || openCountByDay.Count == 0 ? 1
                : openCountByDay[Math.Max(0, Math.Min(openCountByDay.Count - 1, day - 1))];
            count = Math.Max(1, Math.Min(zoneOrder.Count, count));
            foreach (string zone in zoneOrder)
            {
                if (!string.IsNullOrEmpty(zone) && !zones.Contains(zone)) zones.Add(zone);
                if (zones.Count >= count) break;
            }
            return zones;
        }

        public static List<string> CandidateZones(IReadOnlyList<string> openZones, string currentZone,
            string previousZone, int count, Func<int, int> randomIndex)
        {
            var zones = new List<string>();
            if (openZones == null || openZones.Count == 0 || count <= 0) return zones;
            foreach (string zone in openZones)
                if (zone != currentZone && zone != previousZone && !zones.Contains(zone)) zones.Add(zone);
            while (zones.Count > count) zones.RemoveAt(randomIndex(zones.Count));
            for (int pass = 0; pass < 2 && zones.Count < count; pass++)
                foreach (string zone in openZones)
                    if ((zone == currentZone) == (pass == 1) && !zones.Contains(zone) && zones.Count < count) zones.Add(zone);
            while (zones.Count < count) zones.Add(openZones[zones.Count % openZones.Count]);
            return zones;
        }

        public static List<int> EligibleDoors(IReadOnlyList<int> available, ISet<int> used, ISet<int> offered,
            Func<int, float> routeDistance, Func<int, float> distanceFromPlayer, float minimumDistance, float reuseExclusionDistance)
        {
            var doors = new List<int>();
            for (int pass = 0; pass < 2 && doors.Count == 0; pass++)
                foreach (int door in available)
                {
                    float distance = routeDistance(door);
                    if (offered.Contains(door) || float.IsNaN(distance) || distance < 0 || distance >= 1e17f) continue;
                    if (pass == 0 ? used.Contains(door) : distanceFromPlayer(door) <= reuseExclusionDistance) continue;
                    if (!doors.Contains(door)) doors.Add(door);
                }
            var far = doors.FindAll(door => routeDistance(door) >= minimumDistance);
            if (far.Count > 0) return far;
            // Keep exactly the same eligible pool: this fallback must never reintroduce a used/offered house.
            doors.Sort((a, b) => routeDistance(b).CompareTo(routeDistance(a)));
            if (doors.Count > 2) doors.RemoveRange(2, doors.Count - 2);
            return doors;
        }
    }
}
