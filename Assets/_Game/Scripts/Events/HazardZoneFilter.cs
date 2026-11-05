using System;
using System.Collections.Generic;
using UnityEngine;

namespace DeliveryRider
{
    // Today's open map, for anything the game puts down: hazards, a recovering rider, dropped food.
    // Every shared zone border has a wall, so a place counts by which side of the wall it is on. Without a zone map
    // everything is open; a place outside every zone has no zone that could be closed.
    public sealed class HazardZoneFilter
    {
        // Half a wall (0.3 m) + the rider's radius (0.5 m) + a margin, so nothing starts inside a wall.
        public const float WallClearance = 1f;
        private const float RoadSampleSpacing = 4f, OnEdge = 0.0001f;
        private readonly ZoneMap zones;
        private readonly Func<string, bool> isZoneOpen;

        public HazardZoneFilter(ZoneMap zoneMap, Func<string, bool> isOpen)
        {
            zones = zoneMap;
            isZoneOpen = isOpen;
        }

        // May something be put here today? Not in a closed zone, and not against one of its walls.
        public bool PointOpen(Vector3 position) => zones == null || !ClosedWithin(position, WallClearance);

        // Did a rider come to rest in the wrong place? On or past a closed border, or off road outside every zone.
        public bool LandingClosed(Vector3 position, bool onRoad)
        {
            if (zones == null) return false;
            return ClosedWithin(position, OnEdge) || !onRoad && zones.ZoneAt(position) == null;
        }

        // Share of the whole road length open today; counts in the settings are for the whole map.
        public float OpenRoadShare(IReadOnlyList<RoadSurface> roads)
        {
            if (zones == null || roads == null) return 1f;
            float open = 0f, total = 0f;
            foreach (var road in roads)
            {
                if (road == null) continue;
                var line = road.Container;
                AddRoad(ref open, ref total, line.CalculateLength(), t => (Vector3)line.EvaluatePosition(t));
            }
            return total > 0f ? open / total : 1f;
        }

        public void AddRoad(ref float open, ref float total, float length, Func<float, Vector3> pointAt)
        {
            int samples = Mathf.Max(1, Mathf.CeilToInt(length / RoadSampleSpacing));
            int openSamples = 0;
            for (int i = 0; i < samples; i++)
                if (PointOpen(pointAt((i + 0.5f) / samples))) openSamples++;
            open += length * openSamples / samples;
            total += length;
        }

        public static int ScaleCount(int wholeMapCount, float openShare) => Mathf.FloorToInt(wholeMapCount * openShare + 0.5f);

        // Inside, on or within reach of the edge of a closed zone. A point on a shared border is closed if either side is,
        // whichever zone ZoneAt happens to report first.
        private bool ClosedWithin(Vector3 position, float reach)
        {
            var point = new Vector2(position.x, position.z);
            foreach (var zone in zones.Zones)
            {
                var polygon = zone.polygon;
                if (string.IsNullOrEmpty(zone.id) || polygon == null || polygon.Length < 3 || isZoneOpen(zone.id)) continue;
                bool inside = false;
                for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
                {
                    Vector2 a = polygon[j], b = polygon[i], edge = b - a;
                    float t = edge.sqrMagnitude > 0f ? Mathf.Clamp01(Vector2.Dot(point - a, edge) / edge.sqrMagnitude) : 0f;
                    if ((point - (a + edge * t)).sqrMagnitude <= reach * reach) return true;
                    if ((a.y > point.y) != (b.y > point.y) && point.x < (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
                }
                if (inside) return true;
            }
            return false;
        }
    }
}
