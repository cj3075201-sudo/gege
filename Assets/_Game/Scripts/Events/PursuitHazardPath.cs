using UnityEngine;

namespace DeliveryRider
{
    // Shared ground pursuit rule: logical closed-border checks sample the whole segment.
    // Physical queries follow HazardPhysics and leave rider-only barrier colliders out.
    // No teleporting through buildings and no escape target across a closed border.
    internal sealed class PursuitHazardPath
    {
        private readonly RoadNetwork ground;
        private readonly RoadSurface[] roads;
        private readonly HazardZoneFilter zones;
        private readonly Transform owner, player;
        private readonly RaycastHit[] hits = new RaycastHit[32];

        internal PursuitHazardPath(DeliverySession session, RoadNetwork network, Transform root)
        {
            ground = network;
            owner = root;
            player = session.Player;
            zones = new HazardZoneFilter(session.ZoneMap, session.IsZoneOpen);
            var source = network != null ? network.Roads : null;
            roads = new RoadSurface[source != null ? source.Count : 0];
            for (int i = 0; i < roads.Length; i++)
            {
                roads[i] = source[i];
                // Footprint is a nonserialized cache; a freshly loaded runtime road has no bounds yet.
                if (roads[i] != null && roads[i].Footprint.size == Vector3.zero) roads[i].UpdateFootprint();
            }
        }

        internal Vector3 Ground(Vector3 position) => ground != null ? ground.OnGround(position, 0.02f) : position;

        internal bool CanStep(Vector3 from, Vector3 to, float radius)
        {
            int samples = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(from, to) / 0.5f));
            for (int i = 0; i <= samples; i++)
            {
                var p = Vector3.Lerp(from, to, (float)i / samples);
                if (!zones.PointOpen(p)) return false;
                if (roads.Length == 0) continue;
                bool onRoad = false;
                foreach (var road in roads)
                    if (road != null && road.FootprintContains(p, 1f)
                        && road.DistanceToCentre(p) <= road.HalfWidth + road.SidewalkWidth - radius)
                    { onRoad = true; break; }
                if (!onRoad) return false;
            }
            var delta = Vector3.ProjectOnPlane(to - from, Vector3.up);
            if (delta.sqrMagnitude < 0.000001f) return true;
            int count = Physics.SphereCastNonAlloc(from + Vector3.up * 0.8f, radius, delta.normalized,
                hits, delta.magnitude + 0.05f, HazardPhysics.Everything, QueryTriggerInteraction.Ignore);
            if (count == hits.Length) return false;
            for (int i = 0; i < count; i++)
            {
                var hit = hits[i];
                if (hit.transform.IsChildOf(owner) || hit.transform.IsChildOf(player)) continue;
                if (hit.rigidbody != null) continue;
                if (hit.normal.y < 0.65f) return false;
            }
            return true;
        }
    }
}
