using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Splines;

namespace DeliveryRider.Editor
{
    // JSON is authoritative: it can be baked before the expanded scene splines are installed.
    // Reuse RoadPath's linear-spline sampling so the original lane/connector rules stay intact.
    public static class MyeongjiTrafficBake
    {
        private const string DefaultLayout = "Docs/MapLayout/Myeongji/layout_expanded.json";
        private const float TrafficHalfWidth = 3f; // Same as the original baker: full width >= 6 m.
        private const float RoadHeight = -0.14f;
        private const float PathLift = 0.02f;
        private const float EndpointTolerance = 0.001f;
        private const float EndpointReach = 4f;
        private const float MergeRadius = 3f;
        private const float TouchReach = 3f;
        private const float StopBack = 3.5f;
        private const float MinimumEdge = 4f;
        private const float StraightLimit = 14f, TurnLimit = 7f, UTurnLimit = 5f;

        private sealed class Node
        {
            public Vector2 center;
            public bool deadEnd;
            public readonly List<(int road, float along)> touches = new List<(int, float)>();
        }
        private sealed class Lane
        {
            public int edge, startNode, endNode, path;
            public List<Vector2> flat;
        }

        // Unity -batchmode -quit -executeMethod DeliveryRider.Editor.MyeongjiTrafficBake.Batch
        // -trafficScene Assets/...unity [-trafficLayout Docs/...json] [-trafficLots ...json]
        // [-trafficReport Library/traffic-bake.json]. Only the explicitly named scene is saved.
        public static void Batch()
        {
            string reportPath = FullPath(Argument("-trafficReport", "Library/MyeongjiTrafficBakeReport.json"));
            JObject report = null;
            try
            {
                string scenePath = Argument("-trafficScene");
                if (string.IsNullOrEmpty(scenePath) || !scenePath.StartsWith("Assets/", StringComparison.Ordinal)
                    || !scenePath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("-trafficScene must name an existing Assets/...unity scene.");
                var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                report = Bake(scene, Argument("-trafficLayout", DefaultLayout), Argument("-trafficLots"));
                if ((string)report["status"] != "ok") throw new InvalidOperationException("Traffic validation failed; see " + reportPath);
                if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Could not save " + scenePath);
                report["savedScene"] = scenePath;
                Debug.Log("TRAFFIC_BAKE_OK: " + report["pathCount"] + " paths, " + report["junctionCount"] + " junctions. Report: " + reportPath);
            }
            catch (Exception exception)
            {
                if (report == null) report = new JObject();
                report["status"] = "failed";
                report["error"] = exception.Message;
                throw;
            }
            finally
            {
                if (report != null) WriteReport(reportPath, report);
            }
        }

        // Compatibility entry point for an editor caller; no automatic scene/asset backups.
        public static string Apply()
        {
            var scene = SceneManager.GetActiveScene();
            if (scene.isDirty) throw new InvalidOperationException("Save the target scene first.");
            var report = Bake(scene, DefaultLayout);
            if ((string)report["status"] != "ok") throw new InvalidOperationException(report.ToString());
            if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Could not save the target scene.");
            return "Saved " + report["pathCount"] + " paths, " + report["junctionCount"] + " junctions.";
        }

        // Validates everything before replacing serialized data. Callers decide when/where to save.
        public static JObject Bake(Scene scene, string layoutPath, string lotsPath = null)
        {
            if (EditorApplication.isPlaying || EditorApplication.isCompiling || !scene.IsValid() || !scene.isLoaded)
                throw new InvalidOperationException("A loaded scene in edit mode is required.");
            var layout = JObject.Parse(File.ReadAllText(FullPath(layoutPath)));
            var definitions = (JArray)layout["roads"] ?? throw new InvalidDataException("layout.roads is missing.");
            var roads = ReadRoads(definitions);
            var roots = scene.GetRootGameObjects();
            var networks = roots.SelectMany(r => r.GetComponentsInChildren<TrafficNetwork>(true)).ToArray();
            if (networks.Length > 1) throw new InvalidOperationException("Target scene must have at most one TrafficNetwork.");
            var network = networks.SingleOrDefault();
            var ground = roots.Where(r => r.name == "MyeongjiRoutes").Select(r => r.GetComponent<RoadNetwork>()).FirstOrDefault();
            var nodes = FindJunctions(roads);
            var edges = SplitEdges(roads, nodes);
            var paths = new List<TrafficPath>();
            var lanes = new List<Lane>();
            for (int e = 0; e < edges.Count; e++)
            {
                var edge = edges[e];
                lanes.Add(MakeLane(roads[edge.road], edge.a0, edge.a1, e, edge.n0, edge.n1, ground, paths));
                lanes.Add(MakeLane(roads[edge.road], edge.a1, edge.a0, e, edge.n1, edge.n0, ground, paths));
            }
            BuildConnectors(nodes, lanes, ground, paths);
            var report = Validate(paths, nodes, lanes);
            report["layout"] = FullPath(layoutPath);
            report["roadCount"] = definitions.Count;
            report["drivableRoadCount"] = roads.Count;
            report["excludedRoadIds"] = new JArray(definitions.Where(r => (float)r["width"] * 0.5f < TrafficHalfWidth).Select(r => (string)r["id"]));
            report["minimumFullRoadWidth"] = TrafficHalfWidth * 2f;
            report["lots"] = string.IsNullOrEmpty(lotsPath) ? "not supplied; scene colliders only" : FullPath(lotsPath);
            var errors = (JArray)report["errors"];
            var stops = RebindSignalStops(scene, network, paths, errors, out var signalReport);
            report["signals"] = signalReport;
            report["signalStopCount"] = stops.Count;
            report["clearance"] = CheckClearance(scene, paths, ground, lotsPath);
            if ((int)report["clearance"]["hitCount"] > 0) errors.Add("Vehicle swept footprint intersects solid scenery.");
            report["status"] = errors.Count == 0 ? "ok" : "failed";
            if (errors.Count > 0) return report;

            if (network == null)
            {
                var go = new GameObject("Traffic");
                SceneManager.MoveGameObjectToScene(go, scene);
                network = go.AddComponent<TrafficNetwork>();
            }
            network.Configure(network.Session, paths.ToArray(), nodes.Select(n => new TrafficJunctionData { center = OnGround(n.center, ground) }).ToArray(), network.Kinds.ToArray());
            var serialized = new SerializedObject(network);
            var array = serialized.FindProperty("signalStops");
            array.arraySize = stops.Count;
            for (int i = 0; i < stops.Count; i++)
            {
                var item = array.GetArrayElementAtIndex(i);
                item.FindPropertyRelative("path").intValue = stops[i].path;
                item.FindPropertyRelative("distance").floatValue = stops[i].distance;
                item.FindPropertyRelative("signal").objectReferenceValue = stops[i].signal;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(network);
            EditorSceneManager.MarkSceneDirty(scene);
            return report;
        }

        private static List<RoadPath> ReadRoads(JArray definitions)
        {
            var ids = new HashSet<string>();
            var preview = EditorSceneManager.NewPreviewScene();
            try
            {
                var root = new GameObject("TrafficBakeRoadInput");
                SceneManager.MoveGameObjectToScene(root, preview);
                root.AddComponent<RoadNetwork>().enabled = false;
                foreach (var road in definitions)
                {
                    string id = (string)road["id"];
                    float width = (float)road["width"];
                    if (string.IsNullOrWhiteSpace(id) || !ids.Add(id) || !Finite(width) || width <= 0f)
                        throw new InvalidDataException("Road IDs must be unique and widths positive: " + id);
                    var a = Point((JArray)road["a"]);
                    var b = Point((JArray)road["b"]);
                    if (Vector3.Distance(a, b) < 0.001f) throw new InvalidDataException("Zero length road: " + id);
                    if (width * 0.5f < TrafficHalfWidth) continue;
                    var go = new GameObject(id);
                    go.transform.SetParent(root.transform, false);
                    var container = go.AddComponent<SplineContainer>();
                    container.Spline = new Spline();
                    container.Spline.Add(new BezierKnot(new float3(a.x, a.y, a.z)), TangentMode.Linear);
                    container.Spline.Add(new BezierKnot(new float3(b.x, b.y, b.z)), TangentMode.Linear);
                    var surface = go.AddComponent<RoadSurface>();
                    surface.Configure(RoadKind.Main, width, 0f);
                    surface.enabled = false;
                }
                var roads = RoadPath.FromScene(preview);
                if (roads.Count == 0) throw new InvalidDataException("No roads satisfy the 6 m traffic width rule.");
                return roads;
            }
            finally { EditorSceneManager.ClosePreviewScene(preview); }
        }

        private static Vector3 Point(JArray value)
        {
            if (value == null || value.Count != 2) throw new InvalidDataException("Expected an [x,z] point.");
            float x = (float)value[0], z = (float)value[1];
            if (!Finite(x) || !Finite(z)) throw new InvalidDataException("Road coordinate is not finite.");
            return new Vector3(x, RoadHeight, z);
        }

        private static Vector3 OnGround(Vector2 point, RoadNetwork ground)
        {
            var p = new Vector3(point.x, RoadHeight, point.y);
            return ground == null ? p + Vector3.up * PathLift : ground.OnGround(p, PathLift);
        }

        private static string FullPath(string path) => System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(Application.dataPath, "..", path));
        private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        private static string Argument(string name, string fallback = null)
        {
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, name);
            if (index < 0) return fallback;
            if (index + 1 >= args.Length || args[index + 1].StartsWith("-", StringComparison.Ordinal)) throw new ArgumentException("Missing value for " + name);
            return args[index + 1];
        }
        private static void WriteReport(string path, JObject report)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            File.WriteAllText(path, report.ToString());
        }

        private static JObject Validate(List<TrafficPath> paths, List<Node> nodes, List<Lane> lanes)
        {
            var errors = new JArray();
            int badIndices = 0, emptyNext = 0, endpointMismatches = 0, checkedLinks = 0;
            float maxEndpointError = 0f;
            for (int p = 0; p < paths.Count; p++)
            {
                var path = paths[p];
                if (path.points.Length < 2 || path.cumulative.Length != path.points.Length || !Finite(path.length) || path.length <= 0f
                    || path.junction < -1 || path.junction >= nodes.Count || path.nextWeights.Length != path.next.Length)
                    errors.Add("Invalid path data: " + p);
                for (int i = 0; i < path.points.Length; i++)
                    if (!Finite(path.points[i].x) || !Finite(path.points[i].y) || !Finite(path.points[i].z) || !Finite(path.cumulative[i])
                        || (i > 0 && path.cumulative[i] <= path.cumulative[i - 1])) errors.Add("Invalid point/span: " + p + ":" + i);
                if (path.next.Length == 0) emptyNext++;
                foreach (int next in path.next)
                {
                    if (next < 0 || next >= paths.Count) { badIndices++; continue; }
                    float distance = Vector3.Distance(path.points[path.points.Length - 1], paths[next].points[0]);
                    maxEndpointError = Mathf.Max(maxEndpointError, distance);
                    if (distance > EndpointTolerance) endpointMismatches++;
                    checkedLinks++;
                }
                if (path.nextWeights.Any(w => !Finite(w) || w <= 0f)) errors.Add("Invalid next weight: " + p);
            }
            int reachable = badIndices == 0 ? Reachable(paths) : 0;
            // Both directions from path zero must cover the graph; one-way reachability alone can hide a sink.
            int canReturn = 0;
            if (paths.Count > 0 && badIndices == 0)
            {
                var previous = Enumerable.Range(0, paths.Count).Select(_ => new List<int>()).ToArray();
                for (int p = 0; p < paths.Count; p++) foreach (int next in paths[p].next) previous[next].Add(p);
                var seen = new HashSet<int> { 0 };
                var queue = new Queue<int>();
                queue.Enqueue(0);
                while (queue.Count > 0) foreach (int before in previous[queue.Dequeue()]) if (seen.Add(before)) queue.Enqueue(before);
                canReturn = seen.Count;
            }
            var junctionDetails = new JArray();
            int uTurns = 0, missingUTurns = 0;
            for (int n = 0; n < nodes.Count; n++)
            {
                var incoming = lanes.Where(l => l.endNode == n).ToArray();
                int connectors = paths.Count(p => p.junction == n);
                junctionDetails.Add(new JObject
                {
                    ["index"] = n, ["x"] = nodes[n].center.x, ["z"] = nodes[n].center.y,
                    ["deadEnd"] = nodes[n].deadEnd, ["approaches"] = incoming.Length, ["connectors"] = connectors
                });
                if (!nodes[n].deadEnd) continue;
                foreach (var lane in incoming)
                {
                    var back = lanes.Single(l => l.edge == lane.edge && l.startNode == n);
                    bool found = paths[lane.path].next.Any(c => c >= 0 && c < paths.Count && paths[c].next.Contains(back.path));
                    if (found) uTurns++; else missingUTurns++;
                }
            }
            if (paths.Count == 0) errors.Add("No vehicle paths generated.");
            if (badIndices > 0) errors.Add("Out-of-range next indices: " + badIndices);
            if (emptyNext > 0) errors.Add("Paths with no next: " + emptyNext);
            if (endpointMismatches > 0) errors.Add("Endpoint mismatches: " + endpointMismatches);
            if (reachable != paths.Count || canReturn != paths.Count) errors.Add("Traffic graph is not strongly connected.");
            if (missingUTurns > 0) errors.Add("Missing dead-end U-turns: " + missingUTurns);
            return new JObject
            {
                ["pathCount"] = paths.Count, ["laneCount"] = lanes.Count, ["connectorCount"] = paths.Count - lanes.Count,
                ["junctionCount"] = nodes.Count, ["intersectionCount"] = nodes.Count(n => !n.deadEnd),
                ["deadEndCount"] = nodes.Count(n => n.deadEnd), ["deadEndUTurns"] = uTurns, ["missingDeadEndUTurns"] = missingUTurns,
                ["nextLinksChecked"] = checkedLinks, ["outOfRangeNext"] = badIndices, ["emptyNextCount"] = emptyNext,
                ["endpointMismatchCount"] = endpointMismatches, ["maxEndpointErrorMeters"] = maxEndpointError,
                ["reachableCount"] = reachable, ["canReturnCount"] = canReturn, ["junctions"] = junctionDetails, ["errors"] = errors
            };
        }

        // PedestrianCrossingSignal currently uses world-axis X/Z crossing bounds, as did MyeongjiSignals.
        // Recompute geometry intersections instead of keeping any old path index or along-path distance.
        private static List<TrafficSignalStop> RebindSignalStops(Scene scene, TrafficNetwork network, List<TrafficPath> paths,
            JArray errors, out JArray details)
        {
            var stops = new List<TrafficSignalStop>();
            details = new JArray();
            foreach (var signal in scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<PedestrianCrossingSignal>(true)))
            {
                var serialized = new SerializedObject(signal);
                if (serialized.FindProperty("traffic").objectReferenceValue != network) continue;
                var half = serialized.FindProperty("crossingHalfSize").vector2Value;
                int before = stops.Count;
                for (int p = 0; p < paths.Count; p++)
                {
                    var path = paths[p];
                    for (int k = 1; k < path.points.Length; k++)
                    {
                        var a = path.points[k - 1];
                        var b = path.points[k];
                        float dx = b.x - a.x;
                        if (Mathf.Abs(dx) < 0.0001f) continue;
                        float line = signal.transform.position.x - Mathf.Sign(dx) * (half.x + 1.05f);
                        float t = (line - a.x) / dx;
                        if (t < 0f || t > 1f) continue;
                        var hit = Vector3.Lerp(a, b, t);
                        if (Mathf.Abs(hit.z - signal.transform.position.z) > half.y - 0.45f) continue;
                        float along = Mathf.Lerp(path.cumulative[k - 1], path.cumulative[k], t);
                        if (stops.Any(s => s.path == p && s.signal == signal && Mathf.Abs(s.distance - along) < 0.1f)) continue;
                        stops.Add(new TrafficSignalStop { path = p, distance = along, signal = signal });
                    }
                }
                int count = stops.Count - before;
                if (count < 2) errors.Add("Signal has fewer than two approaches: " + signal.name);
                details.Add(new JObject { ["name"] = signal.name, ["stopCount"] = count });
            }
            return stops;
        }

        private static JObject CheckClearance(Scene scene, List<TrafficPath> paths, RoadNetwork ground, string lotsPath)
        {
            var barriers = scene.GetRootGameObjects().Where(r => r.name == "ZoneBarriers").ToArray();
            var active = barriers.Select(r => r.activeSelf).ToArray();
            GameObject proxies = null;
            var hits = new HashSet<string>();
            int samples = 0, proxyCount = 0;
            var groundColliders = new HashSet<Collider>();
            if (ground != null)
            {
                var property = new SerializedObject(ground).FindProperty("ground");
                for (int i = 0; i < property.arraySize; i++)
                    if (property.GetArrayElementAtIndex(i).objectReferenceValue is Collider collider) groundColliders.Add(collider);
            }
            var groundRoot = scene.GetRootGameObjects().FirstOrDefault(r => r.name == "MyeongjiMap")?.transform.Find("Ground");
            try
            {
                foreach (var barrier in barriers) barrier.SetActive(false);
                if (!string.IsNullOrEmpty(lotsPath))
                {
                    proxies = new GameObject("TrafficBakeLotClearance");
                    proxies.hideFlags = HideFlags.HideAndDontSave;
                    SceneManager.MoveGameObjectToScene(proxies, scene);
                    var json = JToken.Parse(File.ReadAllText(FullPath(lotsPath)));
                    var lots = json is JArray list ? list : json["lots"] as JArray;
                    if (lots == null) throw new InvalidDataException("Lots input must be an array or an object containing lots[].");
                    foreach (var lot in lots)
                    {
                        // Original layout rows contain world-aligned bounding widths/depths.
                        // Object rows contain local footprint dimensions and optional rotationDeg.
                        bool row = lot is JArray;
                        string id = row ? (string)lot[0] : (string)lot["id"];
                        float x = row ? (float)lot[2] : (float)lot["x"];
                        float z = row ? (float)lot[3] : (float)lot["z"];
                        float width = row ? (float)lot[4] : (float)lot["width"];
                        float depth = row ? (float)lot[5] : (float)lot["depth"];
                        float rotation = row ? 0f : (float?)lot["rotationDeg"] ?? 0f;
                        if (!Finite(x) || !Finite(z) || !Finite(width) || !Finite(depth) || !Finite(rotation) || width <= 0f || depth <= 0f)
                            throw new InvalidDataException("Invalid lot footprint: " + id);
                        var go = new GameObject("Lot_" + id);
                        go.transform.SetParent(proxies.transform, false);
                        go.transform.SetPositionAndRotation(new Vector3(x, 2f, z), Quaternion.Euler(0f, rotation, 0f));
                        go.AddComponent<BoxCollider>().size = new Vector3(width, 4f, depth);
                        proxyCount++;
                    }
                }
                Physics.SyncTransforms();
                for (int p = 0; p < paths.Count; p++)
                    for (int k = 1; k < paths[p].points.Length; k++)
                    {
                        var a = paths[p].points[k - 1];
                        var b = paths[p].points[k];
                        if ((b - a).sqrMagnitude < 1e-10f) continue;
                        var rotation = Quaternion.LookRotation(b - a);
                        // Original largest retained car: 2.04 m wide, 5 m long, at 1.2 m height.
                        foreach (var collider in Physics.OverlapBox((a + b) * 0.5f + Vector3.up * 1.2f,
                            new Vector3(1.02f, 0.85f, 2.5f), rotation, ~0, QueryTriggerInteraction.Ignore))
                        {
                            if (collider.gameObject.scene != scene || collider.attachedRigidbody != null || groundColliders.Contains(collider)
                                || (groundRoot != null && collider.transform.IsChildOf(groundRoot))) continue;
                            hits.Add(p + ":" + Hierarchy(collider.transform));
                        }
                        samples++;
                    }
            }
            finally
            {
                if (proxies != null) UnityEngine.Object.DestroyImmediate(proxies);
                for (int i = 0; i < barriers.Length; i++) barriers[i].SetActive(active[i]);
                Physics.SyncTransforms();
            }
            return new JObject
            {
                ["sampleCount"] = samples, ["hitCount"] = hits.Count, ["hits"] = new JArray(hits.OrderBy(h => h, StringComparer.Ordinal)),
                ["lotProxyCount"] = proxyCount, ["zoneBarrierRootsTemporarilyDisabled"] = barriers.Length
            };
        }

        private static string Hierarchy(Transform transform)
        {
            string path = transform.name;
            while (transform.parent != null) { transform = transform.parent; path = transform.name + "/" + path; }
            return path;
        }

        private static List<Node> FindJunctions(List<RoadPath> roads)
        {
            var candidates = new List<Vector2>();
            for (int a = 0; a < roads.Count; a++)
            {
                foreach (var end in new[] { roads[a].Points[0], roads[a].Points[roads[a].Points.Length - 1] })
                    for (int b = 0; b < roads.Count; b++)
                    {
                        if (b == a) continue;
                        if (roads[b].Nearest(end, out float along) < roads[b].HalfWidth + EndpointReach) candidates.Add(roads[b].PointAt(along));
                    }
                for (int b = a + 1; b < roads.Count; b++) candidates.AddRange(Crossings(roads[a], roads[b]));
            }
            var nodes = new List<Node>();
            var counts = new List<int>();
            foreach (var c in candidates)
            {
                int hit = nodes.FindIndex(n => Vector2.Distance(n.center, c) < MergeRadius);
                if (hit < 0) { nodes.Add(new Node { center = c }); counts.Add(1); continue; }
                counts[hit]++;
                nodes[hit].center += (c - nodes[hit].center) / counts[hit];
            }
            foreach (var node in nodes)
                for (int r = 0; r < roads.Count; r++)
                    if (roads[r].Nearest(node.center, out float along) < roads[r].HalfWidth + TouchReach) node.touches.Add((r, along));
            return nodes.Where(n => n.touches.Select(t => t.road).Distinct().Count() >= 2).ToList();
        }

        private static IEnumerable<Vector2> Crossings(RoadPath a, RoadPath b)
        {
            if (!a.Near(b.Bounds, 1f)) yield break;
            for (int i = 1; i < a.Points.Length; i++)
                for (int j = 1; j < b.Points.Length; j++)
                    if (Intersect(a.Points[i - 1], a.Points[i], b.Points[j - 1], b.Points[j], out var p)) yield return p;
        }

        // Splits each road into edges between the junctions it passes; open road ends become dead-end nodes (U-turns).
        private static List<(int road, float a0, float a1, int n0, int n1)> SplitEdges(List<RoadPath> roads, List<Node> nodes)
        {
            var edges = new List<(int, float, float, int, int)>();
            for (int r = 0; r < roads.Count; r++)
            {
                var road = roads[r];
                var events = new List<(float along, int node, float cut)>();
                for (int n = 0; n < nodes.Count; n++)
                {
                    if (nodes[n].deadEnd) continue;
                    foreach (var touch in nodes[n].touches)
                    {
                        if (touch.road != r) continue;
                        float other = nodes[n].touches.Where(t => t.road != r).Select(t => roads[t.road].HalfWidth).DefaultIfEmpty(0f).Max();
                        events.Add((touch.along, n, other + StopBack));
                        break;
                    }
                }
                events.Sort((x, y) => x.along.CompareTo(y.along));
                float start = 0f;
                int startNode = -1;
                int index = 0;
                if (events.Count > 0 && events[0].along - events[0].cut <= 1.5f)
                {
                    start = events[0].along + events[0].cut;
                    startNode = events[0].node;
                    index = 1;
                }
                else { start = road.Id == "main" ? 4f : 0f; startNode = DeadEnd(nodes, road, start); }
                for (; index < events.Count; index++)
                {
                    var e = events[index];
                    float end = e.along - e.cut;
                    if (end - start >= MinimumEdge) edges.Add((r, start, end, startNode, e.node));
                    start = e.along + e.cut;
                    startNode = e.node;
                }
                if (road.Length - start >= MinimumEdge) edges.Add((r, start, road.Length, startNode, DeadEnd(nodes, road, road.Length)));
            }
            return edges;
        }

        private static int DeadEnd(List<Node> nodes, RoadPath road, float along)
        {
            nodes.Add(new Node { center = road.PointAt(along), deadEnd = true });
            return nodes.Count - 1;
        }

        // Right-hand traffic: each lane runs half a carriageway to the right of the road centre line.
        private static Lane MakeLane(RoadPath road, float from, float to, int edge, int startNode, int endNode, RoadNetwork ground, List<TrafficPath> paths)
        {
            float offset = road.HalfWidth * 0.5f;
            int samples = Mathf.Max(2, Mathf.CeilToInt(Mathf.Abs(to - from)) + 1);
            var flat = new List<Vector2>(samples);
            for (int i = 0; i < samples; i++)
            {
                float along = Mathf.Lerp(from, to, i / (float)(samples - 1));
                var dir = road.DirectionAt(along) * Mathf.Sign(to - from);
                flat.Add(road.PointAt(along) + new Vector2(dir.y, -dir.x) * offset);
            }
            var lane = new Lane { edge = edge, startNode = startNode, endNode = endNode, path = paths.Count, flat = flat };
            paths.Add(Path(flat, ground, -1, 100f));
            return lane;
        }

        private static int BuildConnectors(List<Node> nodes, List<Lane> lanes, RoadNetwork ground, List<TrafficPath> paths)
        {
            int count = 0;
            for (int n = 0; n < nodes.Count; n++)
            {
                var incoming = lanes.Where(l => l.endNode == n).ToList();
                var outgoing = lanes.Where(l => l.startNode == n).ToList();
                foreach (var inLane in incoming)
                {
                    var next = new List<int>();
                    var weights = new List<float>();
                    var inDir = (inLane.flat[inLane.flat.Count - 1] - inLane.flat[inLane.flat.Count - 2]).normalized;
                    foreach (var outLane in outgoing)
                    {
                        bool uTurn = outLane.edge == inLane.edge;
                        if (uTurn && !nodes[n].deadEnd) continue;
                        var outDir = (outLane.flat[1] - outLane.flat[0]).normalized;
                        float angle = Vector2.SignedAngle(inDir, outDir);
                        if (!uTurn && Mathf.Abs(angle) > 150f) continue;
                        float limit = uTurn ? UTurnLimit : Mathf.Abs(angle) < 30f ? StraightLimit : TurnLimit;
                        next.Add(AddConnector(inLane, outLane, inDir, outDir, n, limit, ground, paths));
                        weights.Add(!uTurn && Mathf.Abs(angle) < 30f ? 3f : 1f);
                        count++;
                    }
                    if (next.Count == 0)
                    {
                        // A junction with no way on: turn back rather than stop forever.
                        var back = outgoing.FirstOrDefault(l => l.edge == inLane.edge);
                        if (back != null)
                        {
                            next.Add(AddConnector(inLane, back, inDir, (back.flat[1] - back.flat[0]).normalized, n, UTurnLimit, ground, paths));
                            weights.Add(1f);
                            count++;
                        }
                    }
                    paths[inLane.path].next = next.ToArray();
                    paths[inLane.path].nextWeights = weights.ToArray();
                }
            }
            return count;
        }

        private static int AddConnector(Lane from, Lane to, Vector2 inDir, Vector2 outDir, int junction, float limit, RoadNetwork ground, List<TrafficPath> paths)
        {
            var p0 = from.flat[from.flat.Count - 1];
            var p3 = to.flat[0];
            // Handle length relative to the chord: ~quarter-circle for turns, ~semicircle for U-turns. A fixed
            // minimum made short right turns loop back on themselves.
            float reach = Vector2.Distance(p0, p3) * (from.edge == to.edge ? 0.67f : 0.65f);
            var p1 = p0 + inDir * reach;
            var p2 = p3 - outDir * reach;
            float approx = Vector2.Distance(p0, p1) + Vector2.Distance(p1, p2) + Vector2.Distance(p2, p3);
            int samples = Mathf.Max(4, Mathf.CeilToInt(approx));
            var flat = new List<Vector2>(samples + 1);
            for (int i = 0; i <= samples; i++)
            {
                float t = i / (float)samples, u = 1f - t;
                flat.Add(u * u * u * p0 + 3f * u * u * t * p1 + 3f * u * t * t * p2 + t * t * t * p3);
            }
            var path = Path(flat, ground, junction, limit);
            path.next = new[] { to.path };
            path.nextWeights = new[] { 1f };
            paths.Add(path);
            return paths.Count - 1;
        }

        private static TrafficPath Path(List<Vector2> flat, RoadNetwork ground, int junction, float limit)
        {
            var points = flat.Select(p => OnGround(p, ground)).ToArray();
            var cumulative = new float[points.Length];
            for (int i = 1; i < points.Length; i++) cumulative[i] = cumulative[i - 1] + Vector3.Distance(points[i - 1], points[i]);
            return new TrafficPath { points = points, cumulative = cumulative, length = cumulative[points.Length - 1], junction = junction, speedLimit = limit };
        }

        private static int Reachable(List<TrafficPath> paths)
        {
            if (paths.Count == 0) return 0;
            var seen = new HashSet<int> { 0 };
            var queue = new Queue<int>();
            queue.Enqueue(0);
            while (queue.Count > 0)
                foreach (int next in paths[queue.Dequeue()].next)
                    if (seen.Add(next)) queue.Enqueue(next);
            return seen.Count;
        }

        private static bool Intersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d, out Vector2 point)
        {
            point = default;
            var r = b - a;
            var s = d - c;
            float denominator = r.x * s.y - r.y * s.x;
            if (Mathf.Abs(denominator) < 1e-6f) return false;
            float t = ((c.x - a.x) * s.y - (c.y - a.y) * s.x) / denominator;
            float u = ((c.x - a.x) * r.y - (c.y - a.y) * r.x) / denominator;
            if (t < 0f || t > 1f || u < 0f || u > 1f) return false;
            point = a + r * t;
            return true;
        }

    }
}
