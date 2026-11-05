using System;
using System.Collections.Generic;
using UnityEngine;

namespace DeliveryRider
{
    // One drivable path: a lane along a road edge, or a turn connector inside a junction.
    [Serializable]
    public sealed class TrafficPath
    {
        public Vector3[] points = Array.Empty<Vector3>();
        public float[] cumulative = Array.Empty<float>();
        public float length;
        [Tooltip("Junction this connector belongs to, or -1 for a lane.")]
        public int junction = -1;
        [Tooltip("Speed cap on this path (turns are slower).")]
        public float speedLimit = 100f;
        public int[] next = Array.Empty<int>();
        public float[] nextWeights = Array.Empty<float>();
        public bool IsConnector => junction >= 0;
    }

    [Serializable]
    public sealed class TrafficJunctionData
    {
        public Vector3 center;
    }

    [Serializable]
    public sealed class TrafficSignalStop
    {
        public int path;
        public float distance;
        public PedestrianCrossingSignal signal;
    }

    // Road traffic for the four-district map: lanes and junction connectors baked from the road splines
    // (Delivery Rider -> Apply Traffic Network), vehicles spawned at start. Junctions are reserved so crossing
    // flows never meet; cars entering from the same lane through the same connector may follow each other in.
    public sealed class TrafficNetwork : MonoBehaviour
    {
        [Serializable]
        public struct VehicleKind
        {
            public TrafficCar prefab;
            [Min(0f)] public float share;
            public Color[] colors;
        }

        [SerializeField] private DeliverySession session;
        [SerializeField] private TrafficPath[] paths = Array.Empty<TrafficPath>();
        [SerializeField] private TrafficJunctionData[] junctions = Array.Empty<TrafficJunctionData>();
        [SerializeField] private TrafficSignalStop[] signalStops = Array.Empty<TrafficSignalStop>();
        [Header("Spawning (test values)")]
        [SerializeField] private VehicleKind[] kinds = Array.Empty<VehicleKind>();
        [SerializeField, Min(0)] private int vehicleCount = 50;
        [Header("열린 구역 차량 수 (다시 배치 시 적용)")]
        [SerializeField, Min(0)] private int[] vehicleCountByDay = { 30, 40, 50, 60, 60 };
        [SerializeField, Min(4f)] private float minimumSpacing = 14f;
        [Tooltip("No car starts this close to the rider's start position.")]
        [SerializeField, Min(0f)] private float playerClearance = 10f;
        [Tooltip("Off: always use the seed below, to reproduce a layout.")]
        [SerializeField] private bool randomEachRun = true;
        [SerializeField] private int seed = 7;

        private sealed class JunctionState
        {
            public readonly List<TrafficCar> occupants = new List<TrafficCar>();
            public readonly List<TrafficCar> waiting = new List<TrafficCar>();
            public int connector = -1;
        }

        // Refreshed on spawn/day reload, never per car or physics step. Null preserves legacy scenes.
        private bool[] openPaths;
        private List<TrafficCar>[] occupants;
        private JunctionState[] states;
        private readonly List<TrafficCar> cars = new List<TrafficCar>();

        public DeliverySession Session => session;
        public int PathCount => paths.Length;
        public int JunctionCount => junctions.Length;
        public TrafficPath Path(int index) => paths[index];
        public IReadOnlyList<TrafficCar> Cars => cars;
        public IReadOnlyList<VehicleKind> Kinds => kinds;

        public void Configure(DeliverySession deliverySession, TrafficPath[] bakedPaths, TrafficJunctionData[] bakedJunctions, VehicleKind[] vehicleKinds)
        {
            session = deliverySession;
            paths = bakedPaths;
            junctions = bakedJunctions;
            kinds = vehicleKinds;
        }

        private void Awake()
        {
            occupants = new List<TrafficCar>[paths.Length];
            for (int i = 0; i < occupants.Length; i++) occupants[i] = new List<TrafficCar>();
            states = new JunctionState[junctions.Length];
            for (int i = 0; i < states.Length; i++) states[i] = new JunctionState();
        }

        private void Start() => Spawn();

        // Developer tools: replaces every car with a fresh fleet from the current settings. Switching a car off
        // first releases its lane and junction places.
        public void Respawn()
        {
            foreach (var car in cars)
                if (car != null) { car.gameObject.SetActive(false); Destroy(car.gameObject); }
            cars.Clear();
            Spawn();
        }

        // Cars go on lanes only, weighted by lane length, at least minimumSpacing apart.
        private void Spawn()
        {
            RefreshOpenPaths();
            int count = openPaths != null && vehicleCountByDay != null && vehicleCountByDay.Length > 0
                ? Mathf.Max(0, vehicleCountByDay[Mathf.Clamp((session.Progress?.Day ?? 1) - 1, 0, Mathf.Min(4, vehicleCountByDay.Length - 1))])
                : vehicleCount;
            var random = randomEachRun ? new System.Random() : new System.Random(seed);
            var lanes = new List<int>();
            float total = 0f;
            for (int i = 0; i < paths.Length; i++)
                if (IsPathOpen(i) && !paths[i].IsConnector && paths[i].length > 12f) { lanes.Add(i); total += paths[i].length; }
            float shareTotal = 0f;
            foreach (var kind in kinds) if (kind.prefab != null) shareTotal += kind.share;
            if (lanes.Count == 0 || shareTotal <= 0f)
            {
                if (openPaths != null && count > 0) Debug.LogWarning("TrafficNetwork: no open circulating lanes or vehicle prefabs; fleet cannot spawn.", this);
                return;
            }

            int attempts = 0;
            while (cars.Count < count && attempts++ < count * (openPaths != null ? 100 : 40))
            {
                float pick = (float)random.NextDouble() * total;
                int lane = lanes[lanes.Count - 1];
                foreach (int candidate in lanes) { if (pick < paths[candidate].length) { lane = candidate; break; } pick -= paths[candidate].length; }
                var kind = PickKind(random, shareTotal);
                float half = kind.prefab.Length * 0.5f;
                float distance = half + 2f + (float)random.NextDouble() * Mathf.Max(0f, paths[lane].length - kind.prefab.Length - 4f);
                bool clear = true;
                foreach (var other in occupants[lane])
                    if (Mathf.Abs(other.Distance - distance) < minimumSpacing + (other.Length + kind.prefab.Length) * 0.5f) { clear = false; break; }
                if (!clear) continue;
                Sample(lane, distance, out var position, out _);
                if (session != null && session.Player != null
                    && (position - session.Player.position).sqrMagnitude < playerClearance * playerClearance) continue;

                var car = Instantiate(kind.prefab, transform);
                car.name = kind.prefab.name + "_" + cars.Count;
                if (kind.colors != null && kind.colors.Length > 0) car.Paint(kind.colors[random.Next(kind.colors.Length)]);
                car.Initialize(this, lane, distance, random.Next());
                cars.Add(car);
            }
            if (openPaths != null && cars.Count < count)
                Debug.LogWarning($"TrafficNetwork: placed {cars.Count}/{count}; open circulating lanes have insufficient free spacing.", this);
        }

        // A path needs an open start, distance midpoint and end. Then remove branches that cannot
        // reach a circulating route. Cars never enter a boundary dead end, so no visible teleport,
        // U-turn through opposing traffic, junction reservation or endless boundary wait is needed.
        private void RefreshOpenPaths()
        {
            if (session == null || session.ZoneMap == null) { openPaths = null; return; }
            var filter = new HazardZoneFilter(session.ZoneMap, session.IsZoneOpen);
            openPaths = new bool[paths.Length];
            for (int i = 0; i < paths.Length; i++)
            {
                var path = paths[i];
                if (path.points == null || path.points.Length < 2) continue;
                Sample(i, path.length * 0.5f, out var middle, out _);
                openPaths[i] = filter.PointOpen(path.points[0]) && filter.PointOpen(middle)
                    && filter.PointOpen(path.points[path.points.Length - 1]);
            }
            bool changed;
            do
            {
                changed = false;
                for (int i = 0; i < paths.Length; i++)
                {
                    if (!openPaths[i]) continue;
                    bool exit = false;
                    foreach (int next in paths[i].next) if (IsPathOpen(next)) { exit = true; break; }
                    if (!exit) { openPaths[i] = false; changed = true; }
                }
            } while (changed);
        }

        public bool IsPathOpen(int path) => path >= 0 && path < paths.Length && (openPaths == null || openPaths[path]);

        private VehicleKind PickKind(System.Random random, float shareTotal)
        {
            float pick = (float)random.NextDouble() * shareTotal;
            foreach (var kind in kinds)
            {
                if (kind.prefab == null) continue;
                if (pick < kind.share) return kind;
                pick -= kind.share;
            }
            return kinds[0];
        }

        public void Sample(int path, float distance, out Vector3 position, out Vector3 forward)
        {
            var p = paths[path];
            var points = p.points;
            distance = Mathf.Clamp(distance, 0f, p.length);
            int i = Array.BinarySearch(p.cumulative, distance);
            if (i < 0) i = ~i - 1;
            i = Mathf.Clamp(i, 0, points.Length - 2);
            float span = p.cumulative[i + 1] - p.cumulative[i];
            position = Vector3.Lerp(points[i], points[i + 1], span > 0f ? (distance - p.cumulative[i]) / span : 0f);
            forward = points[i + 1] - points[i];
            forward = forward.sqrMagnitude > 1e-6f ? forward.normalized : Vector3.forward;
        }

        public int ChooseNext(int path, System.Random random)
        {
            var p = paths[path];
            if (p.next.Length == 0) return -1;
            float total = 0f;
            for (int i = 0; i < p.next.Length; i++)
                if (IsPathOpen(p.next[i])) total += i < p.nextWeights.Length ? p.nextWeights[i] : 1f;
            float pick = (float)random.NextDouble() * total;
            for (int i = 0; i < p.next.Length; i++)
            {
                if (!IsPathOpen(p.next[i])) continue;
                pick -= i < p.nextWeights.Length ? p.nextWeights[i] : 1f;
                if (pick <= 0f) return p.next[i];
            }
            for (int i = p.next.Length - 1; i >= 0; i--) if (IsPathOpen(p.next[i])) return p.next[i];
            return -1;
        }

        public void Enter(TrafficCar car, int path) { if (path >= 0) occupants[path].Add(car); }
        public void Leave(TrafficCar car, int path) { if (path >= 0) occupants[path].Remove(car); }

        // Free space from the car's front bumper to the rear of the nearest car ahead along its planned paths.
        public float GapAhead(TrafficCar car, float lookAhead)
        {
            float best = lookAhead;
            float offset = 0f;
            int path = car.PathIndex;
            float from = car.Distance;
            for (int step = 0; step < 4 && path >= 0 && offset < lookAhead; step++)
            {
                foreach (var other in occupants[path])
                {
                    if (other == car) continue;
                    float ahead = offset + other.Distance - from;
                    if (ahead <= 0f) continue;
                    float gap = ahead - (other.Length + car.Length) * 0.5f;
                    if (gap < best) best = gap;
                }
                foreach (var stop in signalStops)
                {
                    if (stop.path != path || stop.signal == null || !stop.signal.VehiclesMustStop) continue;
                    float gap = offset + stop.distance - from - car.Length * 0.5f;
                    // Vehicles whose front already passed the line must clear the crossing.
                    if (gap >= 0f) best = Mathf.Min(best, gap);
                }
                offset += paths[path].length - from;
                from = 0f;
                path = step == 0 ? car.NextIndex : (paths[path].next.Length == 1 ? paths[path].next[0] : -1);
            }
            return best;
        }

        // The whole car must fit beyond the junction, even when stopped at the next stop line.
        // A stop line needs only its boundary margin; an actual car ahead needs the following gap.
        public bool HasEntryRoom(int lane, TrafficCar car, float followingGap, float entryClearance)
        {
            float half = car.Length * 0.5f;
            float stopCenter = paths[lane].length - half - TrafficCar.JunctionStopMargin;
            foreach (var other in occupants[lane])
                if (other != car)
                    stopCenter = Mathf.Min(stopCenter, other.Distance - other.Length * 0.5f - followingGap - half);

            // A leader's center may already be on the next connector while its rear is still in this lane.
            foreach (int next in paths[lane].next)
                foreach (var other in occupants[next])
                {
                    if (other == car) continue;
                    float rear = other.Distance - other.Length * 0.5f;
                    if (rear < 0f)
                        stopCenter = Mathf.Min(stopCenter, paths[lane].length + rear - followingGap - half);
                }
            return stopCenter >= half + entryClearance;
        }

        // A junction admits one connector at a time (several cars may follow on that same connector), first come first served.
        public bool TryEnterJunction(int junction, TrafficCar car, int connector)
        {
            var state = states[junction];
            state.occupants.RemoveAll(c => c == null || !c.isActiveAndEnabled);
            state.waiting.RemoveAll(c => c == null || !c.isActiveAndEnabled);
            if (state.occupants.Contains(car)) return true;
            if (!state.waiting.Contains(car)) state.waiting.Add(car);
            // Keep the admitted flow while a vehicle's rear is still clearing this junction.
            bool sameFlow = state.occupants.Count > 0 && state.connector == connector;
            bool othersWaiting = state.waiting.Exists(c => c != car && c.NextIndex != connector);
            bool grant = state.occupants.Count == 0 ? state.waiting[0] == car : sameFlow && !othersWaiting;
            if (!grant) return false;
            state.waiting.Remove(car);
            state.occupants.Add(car);
            state.connector = connector;
            return true;
        }

        public void LeaveJunction(int junction, TrafficCar car)
        {
            if (junction < 0 || junction >= states.Length) return;
            states[junction].occupants.Remove(car);
            states[junction].waiting.Remove(car);
        }

        private void OnDrawGizmosSelected()
        {
            for (int i = 0; i < paths.Length; i++)
            {
                Gizmos.color = paths[i].IsConnector ? Color.yellow : Color.cyan;
                var points = paths[i].points;
                for (int k = 1; k < points.Length; k++) Gizmos.DrawLine(points[k - 1] + Vector3.up * 0.3f, points[k] + Vector3.up * 0.3f);
            }
            Gizmos.color = Color.magenta;
            foreach (var junction in junctions) Gizmos.DrawWireSphere(junction.center, 2f);
        }
    }
}
