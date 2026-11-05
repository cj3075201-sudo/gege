using System;
using System.Collections.Generic;
using UnityEngine;

namespace DeliveryRider
{
    [Serializable]
    public sealed class PedestrianPath
    {
        public Vector3[] points = Array.Empty<Vector3>();
        public float[] cumulative = Array.Empty<float>();
        public float length;
        public float halfWidth = 1.5f;
        public int reverse;
        public bool connector;
        public int[] next = Array.Empty<int>();
    }

    // Independent sidewalk traffic; no vehicle junction reservations or delivery recipient logic.
    // Paths are baked by Delivery Rider -> Apply Pedestrian Network; walkers are spawned from the prefab at start.
    public sealed class PedestrianNetwork : MonoBehaviour
    {
        [SerializeField] private DeliverySession session;
        [SerializeField] private PedestrianPath[] paths = Array.Empty<PedestrianPath>();
        [Header("Spawning (test values)")]
        [SerializeField] private TrafficPedestrian prefab;
        [SerializeField] private Material[] coats = Array.Empty<Material>();
        [SerializeField, Min(0.5f)] private float spawnSpacing = 3f;
        [SerializeField, Min(0)] private int population = 48;
        [Tooltip("No walker starts this close to the rider's start position.")]
        [SerializeField, Min(0f)] private float playerClearance = 10f;
        [Tooltip("Off: always use the seed below, to reproduce a layout.")]
        [SerializeField] private bool randomEachRun = true;
        [SerializeField] private int seed = 37;
        private const float LaneOffset = 0.35f;
        // Lane offset slack when ruling out sidewalks and segments that cannot beat the best spot so far.
        private const float ReturnMargin = 0.01f;
        private Bounds[] returnBounds;
        public int ReturnSegmentsChecked { get; private set; }
        public int ReturnSpotsSampled { get; private set; }
        private readonly List<TrafficPedestrian> pedestrians = new List<TrafficPedestrian>();
        private readonly Collider[] spawnHits = new Collider[8];
        public DeliverySession Session => session;
        public int PathCount => paths.Length;
        public int PedestrianCount => pedestrians.Count;
        public int Population => population;
        public TrafficPedestrian Prefab => prefab;
        public IReadOnlyList<TrafficPedestrian> Pedestrians => pedestrians;
        public PedestrianPath Path(int index) => paths[index];
        public void Configure(DeliverySession owner, PedestrianPath[] routes, TrafficPedestrian walker, Material[] coatMaterials)
        { session = owner; paths = routes; prefab = walker; coats = coatMaterials; }

        private void Start() => Spawn();

        // Developer tools: replaces every walker with a fresh crowd from the current settings.
        public void Respawn()
        {
            foreach (var walker in pedestrians)
                if (walker != null) { walker.gameObject.SetActive(false); Destroy(walker.gameObject); }
            pedestrians.Clear();
            Spawn();
        }

        // Walkers go on sidewalk lanes, weighted by lane length, spaced apart and clear of solid props.
        private void Spawn()
        {
            if (prefab == null) return;
            var random = randomEachRun ? new System.Random() : new System.Random(seed);
            var lanes = new List<int>();
            float total = 0f;
            for (int i = 0; i < paths.Length; i++)
                if (!paths[i].connector && paths[i].length > 6f) { lanes.Add(i); total += paths[i].length; }
            if (lanes.Count == 0) return;
            var start = session != null && session.Player != null ? session.Player.position : (Vector3?)null;

            int attempts = 0;
            while (pedestrians.Count < population && attempts++ < population * 60)
            {
                float pick = (float)random.NextDouble() * total;
                int lane = lanes[lanes.Count - 1];
                foreach (int candidate in lanes) { if (pick < paths[candidate].length) { lane = candidate; break; } pick -= paths[candidate].length; }
                float distance = 1f + (float)random.NextDouble() * (paths[lane].length - 2f);
                Sample(lane, distance, out var feet, out var forward);
                feet += Vector3.Cross(Vector3.up, forward) * Mathf.Min(LaneOffset, Mathf.Max(0f, paths[lane].halfWidth - 0.35f));
                if (start.HasValue && (feet - start.Value).sqrMagnitude < playerClearance * playerClearance) continue;
                if (!Free(feet, null) || !Clear(feet)) continue;

                var walker = Instantiate(prefab, feet, Quaternion.LookRotation(forward), transform);
                walker.name = prefab.name + "_" + pedestrians.Count;
                walker.Initialize(this, lane, distance, random.Next(), coats.Length > 0 ? coats[random.Next(coats.Length)] : null);
                pedestrians.Add(walker);
            }
        }

        private bool Clear(Vector3 feet)
        {
            // Starts well above the feet so a sloped sidewalk does not count as an obstruction.
            int count = Physics.OverlapCapsuleNonAlloc(feet + Vector3.up * 0.55f, feet + Vector3.up * 1.45f,
                0.27f, spawnHits, HazardPhysics.Everything, QueryTriggerInteraction.Ignore);
            return count == 0;
        }

        public void Sample(int index, float distance, out Vector3 feet, out Vector3 forward)
        {
            var path = paths[index];
            int i = Array.BinarySearch(path.cumulative, Mathf.Clamp(distance, 0f, path.length));
            if (i < 0) i = ~i - 1;
            i = Mathf.Clamp(i, 0, path.points.Length - 2);
            float span = path.cumulative[i + 1] - path.cumulative[i];
            feet = Vector3.Lerp(path.points[i], path.points[i + 1], span > 0f ? (distance - path.cumulative[i]) / span : 0f);
            forward = Vector3.ProjectOnPlane(path.points[i + 1] - path.points[i], Vector3.up).normalized;
            if (forward.sqrMagnitude < 0.001f) forward = Vector3.forward;
        }

        public int Next(int index, System.Random random)
        {
            var path = paths[index];
            return path.next.Length == 0 ? path.reverse : path.next[random.Next(path.next.Length)];
        }

        public bool FindReturn(Vector3 from, TrafficPedestrian self, out int pathIndex, out float distance, out Vector3 feet)
        {
            pathIndex = -1; distance = 0f; feet = from;
            float best = float.PositiveInfinity;
            ReturnSegmentsChecked = ReturnSpotsSampled = 0;
            if (returnBounds == null || returnBounds.Length != paths.Length) BuildReturnBounds();
            for (int p = 0; p < paths.Length; p++)
            {
                var path = paths[p];
                if (path.connector) continue;
                // Neither a sidewalk nor a segment can give a nearer spot than its line allows, lane offset included.
                float margin = Mathf.Abs(Mathf.Min(LaneOffset, path.halfWidth - 0.35f)) + ReturnMargin;
                if (Beyond(Mathf.Sqrt(returnBounds[p].SqrDistance(from)), margin, best)) continue;
                for (int i = 0; i < path.points.Length - 1; i++)
                {
                    var a = path.points[i];
                    var ab = path.points[i + 1] - a;
                    float t = Mathf.Clamp01(Vector3.Dot(from - a, ab) / Mathf.Max(0.001f, ab.sqrMagnitude));
                    ReturnSegmentsChecked++;
                    if (Beyond((a + ab * t - from).magnitude, margin, best)) continue;
                    ReturnSpotsSampled++;
                    float along = Mathf.Lerp(path.cumulative[i], path.cumulative[i + 1], t);
                    Sample(p, along, out var position, out var direction);
                    position += Vector3.Cross(Vector3.up, direction) * Mathf.Min(LaneOffset, path.halfWidth - 0.35f);
                    float squared = (position - from).sqrMagnitude;
                    if (squared >= best || !Free(position, self) || !self.HasRoom(position)) continue;
                    best = squared; pathIndex = p; distance = along; feet = position;
                }
            }
            return pathIndex >= 0;
        }

        private static bool Beyond(float lineDistance, float margin, float bestSquared)
        {
            float nearest = lineDistance - margin;
            return nearest > 0f && nearest * nearest >= bestSquared;
        }

        private void BuildReturnBounds()
        {
            returnBounds = new Bounds[paths.Length];
            for (int p = 0; p < paths.Length; p++)
            {
                var points = paths[p].points;
                var box = new Bounds(points.Length > 0 ? points[0] : Vector3.zero, Vector3.zero);
                foreach (var point in points) box.Encapsulate(point);
                returnBounds[p] = box;
            }
        }

        public bool Free(Vector3 feet, TrafficPedestrian self)
        {
            foreach (var other in pedestrians)
                if (other != null && other != self && other.gameObject.activeInHierarchy
                    && (other.transform.position - feet).sqrMagnitude < spawnSpacing * spawnSpacing) return false;
            return true;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.2f, 1f, 0.7f, 0.7f);
            foreach (var path in paths)
                for (int i = 1; i < path.points.Length; i++) Gizmos.DrawLine(path.points[i - 1], path.points[i]);
        }
    }
}
