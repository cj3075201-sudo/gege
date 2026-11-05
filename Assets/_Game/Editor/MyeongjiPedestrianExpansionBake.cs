using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DeliveryRider.Editor
{
    // Adds sidewalk paths for the widened Myeongji map without rebaking the hand-corrected 202 original paths
    // (Tools/MyeongjiPedestrianBake.cs would overwrite them). Lane rules are ported from that baker, except that every
    // collider in RoadNetwork.ground counts as floor. Original lanes are cut only where a new road with traffic now runs
    // across them; connectors survive when both of their lanes still meet them.
    public static class MyeongjiPedestrianExpansionBake
    {
        private const string ScenePath = "Assets/_Game/Scenes/FourDistrictPrototype_Myeongji.unity";
        private const float SampleStep = 1f, RoadMargin = 0.7f, ConnectorReach = 6f, CornerReach = 15f, FeetLift = 0.02f;
        private const float LaneClearance = 0.4f, LaneRoadMargin = 0.4f, LinkClearance = 0.3f, DetourReach = 1f, DetourStep = 0.25f;
        private const float RoadEdge = 0.5f, TrafficHalfWidth = 3f, StripSearch = 3f, StripStep = 0.1f;
        private const int OriginalPathCount = 202;
        private const float ObstacleCheckRadius = 0.2f; // thinner than the bake's clearance, which the links already bend within
        private const float LaneHalfWidth = 1f, MinimumLaneSpan = 8f, JoinTolerance = 0.1f;
        // The original map; a path point beyond it means this bake has already run.
        private static readonly Rect OriginalMap = Rect.MinMaxRect(-11f, -111f, 126f, 10f);
        private static readonly Collider[] Obstacles = new Collider[64];

        private sealed class Floor
        {
            public Collider collider;
            public Renderer renderer;
            public int[] submeshEnds;
        }

        private static List<Floor> floors;
        private static HashSet<Collider> floorColliders;

        public static void Apply()
        {
            var scene = OpenScene();
            var roots = scene.GetRootGameObjects();
            var network = roots.Single(g => g.name == "MyeongjiRoutes").GetComponent<RoadNetwork>();
            var walkers = roots.Single(g => g.name == "Pedestrians").GetComponent<PedestrianNetwork>();
            var session = roots.Single(g => g.name == "DeliverySession").GetComponent<DeliverySession>();
            var old = Enumerable.Range(0, walkers.PathCount).Select(walkers.Path).ToList();
            if (old.Any(p => p.points.Any(q => !OriginalMap.Contains(new Vector2(q.x, q.z)))))
                throw new InvalidOperationException("The widened paths are already baked; restore the scene first.");
            LoadFloors(network);
            if (floors.All(f => ((MeshCollider)f.collider).sharedMesh.name != "Ground_Expanded")) throw new InvalidOperationException("The new ground is not in RoadNetwork.ground yet.");
            var barriers = roots.FirstOrDefault(g => g.name == "ZoneBarriers");
            bool barriersActive = barriers != null && barriers.activeSelf;
            if (barriers != null) barriers.SetActive(false); // their walls are not obstacles to walkers
            Physics.SyncTransforms();
            var roads = LoadRoads(network, out var newRoads);
            var result = CutOriginal(old, newRoads, out int cut, out int droppedLinks);
            int originalCount = result.Count;
            var lanes = new List<PedestrianPath>();
            foreach (var road in newRoads) foreach (int side in new[] { -1, 1 }) BakeSide(road, side, roads, network, lanes);
            lanes = JoinStraight(lanes, roads, network);
            int newLanes = lanes.Count;
            foreach (var lane in lanes)
            {
                lane.reverse += originalCount;
                result.Add(lane);
            }
            int links = LinkAcross(result, originalCount, roads, network);
            Validate(result);
            if (barriers != null) barriers.SetActive(barriersActive);
            var data = new SerializedObject(walkers);
            var coatArray = data.FindProperty("coats");
            var coats = Enumerable.Range(0, coatArray.arraySize).Select(i => (Material)coatArray.GetArrayElementAtIndex(i).objectReferenceValue).ToArray();
            walkers.Configure(session, result.ToArray(), walkers.Prefab, coats);
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("Could not save the scene.");
            Debug.Log($"[MapExpansion] Pedestrians: {old.Count} original paths -> {originalCount} after {cut} cuts ({droppedLinks} links dropped), {newLanes} new lanes, {links} new links, total {result.Count}.");
        }

        // Read-only: what the cut of the original lanes would do. Works before the new ground exists.
        public static void ReportCuts()
        {
            var scene = OpenScene();
            var roots = scene.GetRootGameObjects();
            var network = roots.Single(g => g.name == "MyeongjiRoutes").GetComponent<RoadNetwork>();
            var walkers = roots.Single(g => g.name == "Pedestrians").GetComponent<PedestrianNetwork>();
            var old = Enumerable.Range(0, walkers.PathCount).Select(walkers.Path).ToList();
            floors = new List<Floor>();
            floorColliders = new HashSet<Collider>();
            LoadRoads(network, out var newRoads);
            var result = CutOriginal(old, newRoads, out int cut, out int dropped);
            Validate(result);
            Debug.Log($"[MapExpansion] Pedestrian cuts: {old.Count} paths -> {result.Count}, {cut} cut lanes, {dropped} links dropped, {newRoads.Count} new roads.");
        }

        // Writes the original lanes for Tools/build_myeongji_lots.py, which keeps new buildings off them. The new lanes,
        // baked with the buildings in place, come after these in the network.
        public static void ExportOriginalPaths()
        {
            var scene = OpenScene();
            var walkers = scene.GetRootGameObjects().Single(g => g.name == "Pedestrians").GetComponent<PedestrianNetwork>();
            var points = new JArray();
            for (int p = 0; p < OriginalPathCount; p++)
                foreach (var point in walkers.Path(p).points) points.Add(new JArray(Math.Round(point.x, 2), Math.Round(point.z, 2)));
            File.WriteAllText("Docs/MapLayout/Myeongji/pedestrian_original_paths.json", new JObject { ["points"] = points }.ToString(Newtonsoft.Json.Formatting.None));
            Debug.Log($"[MapExpansion] Exported {points.Count} points of the first {OriginalPathCount} paths.");
        }

        // Read-only audit of the saved network.
        public static void Check()
        {
            var scene = OpenScene();
            var roots = scene.GetRootGameObjects();
            var network = roots.Single(g => g.name == "MyeongjiRoutes").GetComponent<RoadNetwork>();
            var walkers = roots.Single(g => g.name == "Pedestrians").GetComponent<PedestrianNetwork>();
            var paths = Enumerable.Range(0, walkers.PathCount).Select(walkers.Path).ToList();
            Validate(paths);
            LoadFloors(network);
            var roads = LoadRoads(network, out _);
            var traffic = roads.Where(r => r.HalfWidth >= TrafficHalfWidth).ToList();
            int offFloor = 0, onTraffic = 0, samples = 0;
            var blocked = new List<string>();
            var barriers = roots.FirstOrDefault(g => g.name == "ZoneBarriers");
            if (barriers != null) barriers.SetActive(false);
            Physics.SyncTransforms();
            foreach (var path in paths)
                foreach (var point in path.points)
                {
                    samples++;
                    if (!Raycast(new Vector2(point.x, point.z), out _, out _)) offFloor++;
                    if (!path.connector && path.halfWidth >= LaneHalfWidth && traffic.Any(r => r.Nearest(new Vector2(point.x, point.z), out _) < r.HalfWidth - RoadEdge)) onTraffic++;
                    int hits = Physics.OverlapCapsuleNonAlloc(point + Vector3.up * .75f, point + Vector3.up * 1.4f, ObstacleCheckRadius, Obstacles, ~0, QueryTriggerInteraction.Ignore);
                    for (int h = 0; h < hits; h++)
                        if (!floorColliders.Contains(Obstacles[h]) && Obstacles[h].attachedRigidbody == null) { blocked.Add($"{paths.IndexOf(path)}:{Obstacles[h].name}@{point:F1}"); break; }
                }
            var length = new Dictionary<string, float>();
            var zones = new SerializedObject(roots.Single(g => g.name == "DeliverySession").GetComponent<DeliverySession>()).FindProperty("zoneMap").objectReferenceValue as ZoneMap;
            foreach (var path in paths.Where(p => !p.connector))
            {
                string zone = zones != null ? zones.ZoneAt(path.points[path.points.Length / 2]) ?? "none" : "any";
                length[zone] = (length.TryGetValue(zone, out float value) ? value : 0f) + path.length * 0.5f;
            }
            if (barriers != null) barriers.SetActive(true);
            string byZone = string.Join(", ", length.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value:F0} m"));
            Debug.Log($"[MapExpansion] Pedestrian check: {paths.Count} paths ({paths.Count(p => p.connector)} links), {samples} points, {offFloor} off the floor, {onTraffic} deep in a traffic road, {blocked.Count} inside a solid; sidewalks {byZone}.");
            foreach (var group in blocked.GroupBy(x => x.Substring(0, x.IndexOf('@'))))
                Debug.Log($"[MapExpansion] Path in a solid {group.Key}: {group.Count()} points, {group.First().Substring(group.First().IndexOf('@'))} to {group.Last().Substring(group.Last().IndexOf('@'))}");
            if (offFloor > 0 || onTraffic > 0 || blocked.Count > 0) throw new InvalidOperationException("[MapExpansion] Pedestrian check failed.");
        }

        private static Scene OpenScene()
        {
            if (EditorApplication.isPlaying || EditorApplication.isCompiling) throw new InvalidOperationException("Edit mode required.");
            return EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
        }

        private static void LoadFloors(RoadNetwork network)
        {
            floors = new List<Floor>();
            floorColliders = new HashSet<Collider>();
            var array = new SerializedObject(network).FindProperty("ground");
            for (int i = 0; i < array.arraySize; i++)
            {
                if (!(array.GetArrayElementAtIndex(i).objectReferenceValue is MeshCollider collider) || collider.sharedMesh == null) continue;
                var mesh = collider.sharedMesh;
                var ends = new int[mesh.subMeshCount];
                int sum = 0;
                for (int s = 0; s < ends.Length; s++) { sum += (int)mesh.GetIndexCount(s) / 3; ends[s] = sum; }
                floors.Add(new Floor { collider = collider, renderer = collider.GetComponent<Renderer>(), submeshEnds = ends });
                floorColliders.Add(collider);
            }
            if (floors.Count == 0) throw new InvalidOperationException("RoadNetwork.ground is empty.");
        }

        private static List<RoadPath> LoadRoads(RoadNetwork network, out List<RoadPath> newRoads)
        {
            var layout = JObject.Parse(File.ReadAllText("Docs/MapLayout/Myeongji/layout_expanded.json"));
            var ids = new HashSet<string>(((JArray)layout["roads"]).Skip((int)layout["existingRoadCount"]).Select(r => (string)r["id"]));
            var method = typeof(RoadPath).GetMethod("FromSurface", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var roads = network.Roads.Select(r => (RoadPath)method.Invoke(null, new object[] { r })).ToList();
            foreach (var road in roads) typeof(RoadPath).GetProperty("SidewalkWidth").SetValue(road, 2.4f);
            newRoads = roads.Where(r => ids.Contains(r.Id)).ToList();
            if (newRoads.Count != ids.Count) throw new InvalidOperationException($"Found {newRoads.Count} of {ids.Count} new roads; apply the splines first.");
            return roads;
        }

        // Splits each original lane pair where a new road with traffic crosses it, and rewires what still connects.
        private static List<PedestrianPath> CutOriginal(List<PedestrianPath> old, List<RoadPath> newRoads, out int cutLanes, out int droppedLinks)
        {
            var traffic = newRoads.Where(r => r.HalfWidth >= TrafficHalfWidth).ToList();
            bool Crossed(Vector3 p) => traffic.Any(r => r.Near(new Rect(p.x, p.z, 0f, 0f), r.HalfWidth + RoadMargin) && r.Nearest(new Vector2(p.x, p.z), out _) < r.HalfWidth + RoadMargin);
            var result = new List<PedestrianPath>();
            var firstOf = new int[old.Count];
            var lastOf = new int[old.Count];
            for (int i = 0; i < old.Count; i++) firstOf[i] = lastOf[i] = -1;
            cutLanes = 0;
            for (int i = 0; i < old.Count; i++)
            {
                var lane = old[i];
                if (lane.connector || lane.reverse < i) continue;
                var pieces = new List<List<Vector3>>();
                var current = new List<Vector3>();
                foreach (var point in lane.points)
                {
                    if (Crossed(point)) { if (current.Count > 0) pieces.Add(current); current = new List<Vector3>(); }
                    else current.Add(Reproject(point, newRoads));
                }
                if (current.Count > 0) pieces.Add(current);
                bool whole = pieces.Count == 1 && pieces[0].Count == lane.points.Length;
                int before = pieces.Count;
                pieces = pieces.Where(p => whole || p.Count >= 4 && (p[p.Count - 1] - p[0]).sqrMagnitude >= MinimumLaneSpan * MinimumLaneSpan).ToList();
                if (!whole)
                {
                    cutLanes++;
                    Debug.Log($"[MapExpansion] Cut lane {i}: {lane.points[0]:F1} to {lane.points[lane.points.Length - 1]:F1}, {before} pieces, kept {pieces.Count}: " + string.Join(" | ", pieces.Select(p => $"{p[0]:F1}..{p[p.Count - 1]:F1}")));
                }
                int reverseLane = lane.reverse;
                for (int k = 0; k < pieces.Count; k++)
                {
                    int forward = result.Count, backward = forward + 1;
                    result.Add(MakePath(pieces[k].ToArray(), lane.halfWidth, backward, false));
                    result.Add(MakePath(pieces[k].AsEnumerable().Reverse().ToArray(), lane.halfWidth, forward, false));
                    if (k == 0) { firstOf[i] = forward; lastOf[reverseLane] = backward; }
                    if (k == pieces.Count - 1) { lastOf[i] = forward; firstOf[reverseLane] = backward; }
                }
            }
            droppedLinks = 0;
            var outgoing = result.Select(_ => new List<int>()).ToList();
            for (int i = 0; i < old.Count; i++)
            {
                var link = old[i];
                if (!link.connector) continue;
                int source = lastOf[link.reverse];
                var targets = link.next.Select(n => firstOf[n]).Where(n => n >= 0).ToArray();
                bool attached = source >= 0 && targets.Length > 0
                    && Vector3.Distance(result[source].points[result[source].points.Length - 1], link.points[0]) <= JoinTolerance
                    && targets.All(t => Vector3.Distance(link.points[link.points.Length - 1], result[t].points[0]) <= JoinTolerance);
                if (!attached || link.points.Any(Crossed)) { droppedLinks++; continue; }
                int index = result.Count;
                result.Add(new PedestrianPath { points = link.points, cumulative = link.cumulative, length = link.length, halfWidth = link.halfWidth, reverse = source, connector = true, next = targets });
                outgoing[source].Add(index);
                outgoing.Add(new List<int>());
            }
            for (int i = 0; i < old.Count; i++)
            {
                // A lane left without its own links keeps turning back at its end, as the original dead ends do.
                if (!old[i].connector && lastOf[i] >= 0) result[lastOf[i]].next = outgoing[lastOf[i]].ToArray();
            }
            return result;
        }

        // The ground drops where a narrow new road now runs under an original lane.
        private static Vector3 Reproject(Vector3 point, List<RoadPath> newRoads)
        {
            if (floors.Count == 0) return point;
            var flat = new Vector2(point.x, point.z);
            if (!newRoads.Any(r => r.Near(new Rect(flat.x, flat.y, 0f, 0f), r.HalfWidth + 1f) && r.Nearest(flat, out _) < r.HalfWidth + 1f)) return point;
            return Raycast(flat, out var hit, out _) ? hit + Vector3.up * FeetLift : point;
        }

        // Links between new lanes and from or to an original lane. Original-to-original links stay as they were.
        private static int LinkAcross(List<PedestrianPath> routes, int originalCount, List<RoadPath> roads, RoadNetwork ground)
        {
            int total = routes.Count, links = 0;
            var lanes = Enumerable.Range(0, total).Where(i => !routes[i].connector).ToList();
            foreach (int a in lanes)
            {
                var source = routes[a];
                var end = source.points[source.points.Length - 1];
                var next = new List<int>(source.next ?? Array.Empty<int>());
                foreach (int b in lanes)
                {
                    if (b == a || b == source.reverse || (a < originalCount && b < originalCount)) continue;
                    var destination = routes[b];
                    if ((destination.points[0] - end).sqrMagnitude > 4f * CornerReach * CornerReach) continue;
                    var points = Bridge(source, destination, roads, ground);
                    if (points == null) continue;
                    var path = MakePath(points, Mathf.Min(source.halfWidth, destination.halfWidth), a, true);
                    path.next = new[] { b };
                    next.Add(routes.Count);
                    routes.Add(path);
                    links++;
                }
                source.next = next.ToArray();
            }
            return links;
        }

        private static void BakeSide(RoadPath road, int side, List<RoadPath> roads, RoadNetwork ground, List<PedestrianPath> routes)
        {
            var fragment = new List<Vector3>();
            bool fragmentLoose = false;
            int count = Mathf.Max(1, Mathf.CeilToInt(road.Length / SampleStep));
            for (int i = 0; i <= count; i++)
            {
                float along = road.Length * i / count;
                var direction = road.DirectionAt(along);
                var outward = new Vector2(direction.y, -direction.x) * side;
                var kerb = road.PointAt(along);
                bool loose = false, found = Strip(kerb, outward, road.HalfWidth + StripStep, road, roads, false, out float middle);
                if (!found) found = loose = Strip(kerb, outward, road.HalfWidth - RoadEdge + StripStep, road, roads, true, out middle);
                var feet = Vector3.zero;
                bool clear = found && Walkable(kerb + outward * middle, roads, out feet, LaneClearance, LaneRoadMargin, loose);
                if (clear && fragment.Count > 0 && !ClearLine(fragment[fragment.Count - 1], feet, roads, LaneClearance, LaneRoadMargin, loose || fragmentLoose)) clear = false;
                fragmentLoose = loose;
                if (!clear) { AddFragment(fragment, LaneHalfWidth, routes); fragment.Clear(); }
                else fragment.Add(feet);
            }
            AddFragment(fragment, LaneHalfWidth, routes);
        }

        private static bool Strip(Vector2 kerb, Vector2 outward, float from, RoadPath road, List<RoadPath> roads, bool loose, out float middle)
        {
            float first = -1f, last = -1f;
            for (float offset = from; offset <= road.HalfWidth + StripSearch; offset += StripStep)
            {
                if (Walkable(kerb + outward * offset, roads, out _, LaneClearance, LaneRoadMargin, loose)) { if (first < 0f) first = offset; last = offset; }
                else if (first >= 0f) break;
            }
            middle = (first + last) * 0.5f;
            return first >= 0f;
        }

        private static void AddFragment(List<Vector3> points, float halfWidth, List<PedestrianPath> routes)
        {
            if (points.Count < 4 || (points[points.Count - 1] - points[0]).sqrMagnitude < MinimumLaneSpan * MinimumLaneSpan) return;
            int forward = routes.Count, reverse = forward + 1;
            routes.Add(MakePath(points.ToArray(), halfWidth, reverse, false));
            routes.Add(MakePath(points.AsEnumerable().Reverse().ToArray(), halfWidth, forward, false));
        }

        private static PedestrianPath MakePath(Vector3[] points, float halfWidth, int reverse, bool connector)
        {
            var cumulative = new float[points.Length];
            for (int i = 1; i < points.Length; i++) cumulative[i] = cumulative[i - 1] + Vector3.Distance(points[i], points[i - 1]);
            return new PedestrianPath { points = points, cumulative = cumulative, length = cumulative[cumulative.Length - 1], halfWidth = halfWidth, reverse = reverse, connector = connector };
        }

        private static List<PedestrianPath> JoinStraight(List<PedestrianPath> lanes, List<RoadPath> roads, RoadNetwork ground)
        {
            int count = lanes.Count;
            var successor = Enumerable.Repeat(-1, count).ToArray();
            var predecessor = Enumerable.Repeat(-1, count).ToArray();
            var bridges = new Vector3[count][];
            for (int a = 0; a < count; a++)
            {
                float best = float.PositiveInfinity;
                for (int b = 0; b < count; b++)
                {
                    if (b == a || b == lanes[a].reverse || predecessor[b] >= 0) continue;
                    float span = Vector3.Distance(lanes[a].points[lanes[a].points.Length - 1], lanes[b].points[0]);
                    if (span >= best || span > CornerReach) continue;
                    var points = Bridge(lanes[a], lanes[b], roads, ground, true);
                    if (points == null) continue;
                    best = span; successor[a] = b; bridges[a] = points;
                }
                if (successor[a] >= 0) predecessor[successor[a]] = a;
            }
            var used = new bool[count];
            var joined = new List<PedestrianPath>();
            void Chain(int head)
            {
                var points = new List<Vector3>(lanes[head].points);
                used[head] = used[lanes[head].reverse] = true;
                for (int current = head; successor[current] >= 0 && !used[successor[current]]; current = successor[current])
                {
                    var bridge = bridges[current];
                    points.AddRange(bridge.Skip(1).Take(bridge.Length - 2));
                    points.AddRange(lanes[successor[current]].points);
                    used[successor[current]] = used[lanes[successor[current]].reverse] = true;
                }
                AddFragment(points, lanes[head].halfWidth, joined);
            }
            for (int a = 0; a < count; a++) if (!used[a] && predecessor[a] < 0) Chain(a);
            for (int a = 0; a < count; a++) if (!used[a]) Chain(a);
            return joined;
        }

        private static Vector3[] Bridge(PedestrianPath source, PedestrianPath destination, List<RoadPath> roads, RoadNetwork ground, bool straightOnly = false)
        {
            var end = source.points[source.points.Length - 1];
            var start = destination.points[0];
            float span = Vector3.Distance(end, start);
            var outgoing = Heading(source.points.AsEnumerable().Reverse().ToArray(), -1f);
            var incoming = Heading(destination.points, 1f);
            float angle = Vector2.Angle(outgoing, incoming);
            if (straightOnly && (angle >= 30f || span >= 0.1f && Vector2.Angle(outgoing, Flat(start - end)) > 20f)) return null;
            if (span < 0.1f) return angle <= 150f ? new[] { end, end + new Vector3(outgoing.x, 0f, outgoing.y) * 0.05f, start } : null;
            if (span <= ConnectorReach && ClearLine(end, start, roads, LinkClearance, RoadMargin, true)) return Line(ground, end, start);
            Vector2 bend;
            if (angle < 30f)
            {
                if (span > CornerReach || Vector2.Angle(outgoing, Flat(start - end)) > 20f) return null;
                bend = (Flat(end) + Flat(start)) * 0.5f;
            }
            else if (angle <= 150f)
            {
                var from = Flat(end);
                var gap = Flat(start) - from;
                float cross = outgoing.x * incoming.y - outgoing.y * incoming.x;
                float ahead = (gap.x * incoming.y - gap.y * incoming.x) / cross;
                float before = (outgoing.x * gap.y - outgoing.y * gap.x) / cross;
                if (ahead < -0.5f || before < -0.5f || ahead > CornerReach || before > CornerReach) return null;
                bend = from + outgoing * Mathf.Max(0f, ahead);
            }
            else return null;
            foreach (var shift in Detours)
            {
                var flat = bend + shift;
                var point = new Vector3(flat.x, (end.y + start.y) * 0.5f, flat.y);
                if (!ClearLine(end, point, roads, LinkClearance, RoadMargin, true) || !ClearLine(point, start, roads, LinkClearance, RoadMargin, true)) continue;
                var leg = Line(ground, end, point);
                return leg.Take(leg.Length - 1).Concat(Line(ground, point, start)).ToArray();
            }
            return null;
        }

        private static readonly Vector2[] Detours = Enumerable.Range(-4, 9).SelectMany(x => Enumerable.Range(-4, 9).Select(z => new Vector2(x, z) * DetourStep))
            .Where(v => v.magnitude <= DetourReach + 0.001f).OrderBy(v => v.sqrMagnitude).ToArray();

        private static Vector2 Flat(Vector3 v) => new Vector2(v.x, v.z);

        private static Vector2 Heading(Vector3[] points, float sign)
        {
            int i = 1;
            while (i < points.Length - 1 && Vector3.Distance(points[0], points[i]) < 3f) i++;
            return (Flat(points[i] - points[0]) * sign).normalized;
        }

        private static Vector3[] Line(RoadNetwork ground, Vector3 from, Vector3 to)
        {
            int count = Mathf.Max(2, Mathf.CeilToInt(Vector3.Distance(from, to) / 0.4f) + 1);
            var points = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                var point = Vector3.Lerp(from, to, i / (float)(count - 1));
                points[i] = Raycast(Flat(point), out var hit, out _) ? hit + Vector3.up * FeetLift : point;
            }
            points[0] = from;
            points[count - 1] = to;
            return points;
        }

        private static bool ClearLine(Vector3 from, Vector3 to, List<RoadPath> roads, float clearance, float margin, bool loose)
        {
            int count = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(from, to) / 0.4f));
            for (int i = 0; i <= count; i++)
            {
                var point = Vector3.Lerp(from, to, i / (float)count);
                if (!Walkable(Flat(point), roads, out var feet, clearance, margin, loose) || Mathf.Abs(feet.y - point.y) > 0.6f) return false;
            }
            return true;
        }

        private static bool Raycast(Vector2 flat, out Vector3 point, out string surface)
        {
            point = Vector3.zero;
            surface = null;
            float best = float.PositiveInfinity;
            var ray = new Ray(new Vector3(flat.x, 25f, flat.y), Vector3.down);
            foreach (var floor in floors)
            {
                var bounds = floor.collider.bounds;
                if (flat.x < bounds.min.x || flat.x > bounds.max.x || flat.y < bounds.min.z || flat.y > bounds.max.z) continue;
                if (!floor.collider.Raycast(ray, out var hit, 40f) || hit.distance >= best) continue;
                int triangle = hit.triangleIndex;
                int sub = Array.FindIndex(floor.submeshEnds, last => triangle < last);
                if (sub < 0) continue;
                best = hit.distance;
                point = hit.point;
                surface = floor.renderer.sharedMaterials[sub].name;
            }
            return surface != null;
        }

        // Strict: paved sidewalk only, the margin clear of every carriageway. Loose: also the kerb, the open plot, the
        // outer RoadEdge of a road with traffic and the whole of a road without.
        private static bool Walkable(Vector2 flat, List<RoadPath> roads, out Vector3 feet, float clearance, float margin, bool loose)
        {
            feet = Vector3.zero;
            if (!Raycast(flat, out var hit, out string surface)) return false;
            if (!surface.Contains("Pavement") && !(loose && (surface.Contains("Plot") || surface.Contains("Curb") || surface.Contains("Asphalt")))) return false;
            feet = hit + Vector3.up * FeetLift;
            var at = new Rect(flat.x, flat.y, 0f, 0f);
            foreach (var road in roads)
            {
                if (loose && road.HalfWidth < TrafficHalfWidth) continue;
                float reach = loose ? road.HalfWidth - RoadEdge : road.HalfWidth + margin;
                if (road.Near(at, reach) && road.Nearest(flat, out _) < reach) return false;
            }
            int count = Physics.OverlapCapsuleNonAlloc(feet + Vector3.up * .75f, feet + Vector3.up * 1.4f, clearance, Obstacles, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++) if (!floorColliders.Contains(Obstacles[i]) && Obstacles[i].attachedRigidbody == null) return false;
            return count < Obstacles.Length;
        }

        private static void Validate(List<PedestrianPath> paths)
        {
            for (int p = 0; p < paths.Count; p++)
            {
                var path = paths[p];
                if (path.points.Length < 2 || path.length <= 0f || path.reverse < 0 || path.reverse >= paths.Count)
                    throw new InvalidOperationException("Invalid pedestrian path " + p);
                foreach (int next in path.next ?? Array.Empty<int>())
                    if (next < 0 || next >= paths.Count || Vector3.Distance(path.points[path.points.Length - 1], paths[next].points[0]) > JoinTolerance)
                        throw new InvalidOperationException("Disconnected pedestrian path " + p);
            }
        }
    }
}
