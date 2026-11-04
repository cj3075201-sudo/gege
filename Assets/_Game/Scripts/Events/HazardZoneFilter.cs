using System;
using System.Collections.Generic;
using UnityEngine;

namespace DeliveryRider
{
    // Today's open roads for hazard placement. Without a zone map every road counts as open.
    // A main road runs along zone borders, so it is open when either side is; an alley belongs to the zone it lies in.
    public sealed class HazardZoneFilter
    {
        private const float SideProbeMargin = 4f, RoadSampleSpacing = 4f;
        private readonly ZoneMap zones;
        private readonly Func<string, bool> isZoneOpen;

        public HazardZoneFilter(ZoneMap zoneMap, Func<string, bool> isOpen)
        {
            zones = zoneMap;
            isZoneOpen = isOpen;
        }

        // A road outside every zone stays open: it has no zone that could be closed.
        public bool RoadOpen(Vector3 centre, float halfWidth, bool alley)
        {
            if (zones == null) return true;
            if (alley) return ZoneAllows(zones.ZoneAt(centre), out bool open) ? open : true;
            float probe = halfWidth + SideProbeMargin;
            bool anyZone = false;
            for (int i = 0; i < SideProbes.Length; i++)
            {
                if (!ZoneAllows(zones.ZoneAt(centre + SideProbes[i] * probe), out bool sideOpen)) continue;
                if (sideOpen) return true;
                anyZone = true;
            }
            return !anyZone;
        }

        // A road running on across a border wall counts as open by its side probes for a few metres past the wall. Nobody
        // may be put there: this asks about the spot itself. Outside every zone nothing can be closed.
        public bool PointOpen(Vector3 position)
        {
            if (zones == null) return true;
            return !ZoneAllows(zones.ZoneAt(position), out bool open) || open;
        }

        // A landing is closed when its road is closed, or off road where no open zone holds it.
        public bool LandingClosed(Vector3 position, bool onRoad, Vector3 roadCentre, float halfWidth, bool alley)
        {
            if (zones == null) return false;
            if (onRoad) return !RoadOpen(roadCentre, halfWidth, alley) || !PointOpen(roadCentre);
            return !ZoneAllows(zones.ZoneAt(position), out bool open) || !open;
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
                AddRoad(ref open, ref total, line.CalculateLength(), t => (Vector3)line.EvaluatePosition(t), road.HalfWidth, road.Kind == RoadKind.Alley);
            }
            return total > 0f ? open / total : 1f;
        }

        public void AddRoad(ref float open, ref float total, float length, Func<float, Vector3> pointAt, float halfWidth, bool alley)
        {
            int samples = Mathf.Max(1, Mathf.CeilToInt(length / RoadSampleSpacing));
            int openSamples = 0;
            for (int i = 0; i < samples; i++)
                if (RoadOpen(pointAt((i + 0.5f) / samples), halfWidth, alley)) openSamples++;
            open += length * openSamples / samples;
            total += length;
        }

        public static int ScaleCount(int wholeMapCount, float openShare) => Mathf.FloorToInt(wholeMapCount * openShare + 0.5f);

        private static readonly Vector3[] SideProbes = { Vector3.right, Vector3.left, Vector3.forward, Vector3.back };

        private bool ZoneAllows(string zone, out bool open)
        {
            open = zone != null && isZoneOpen(zone);
            return zone != null;
        }
    }
}
