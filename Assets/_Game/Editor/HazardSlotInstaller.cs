using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DeliveryRider.Editor
{
    // Hazard spawning: builds the hazard prefabs, bakes every candidate place for every type from the scene's road
    // splines into Settings/HazardCandidates.asset, creates Settings/HazardSpawnSettings.asset once (counts are kept
    // afterwards) and wires the scene's HazardPlanner, which spawns random candidates each run.
    // Re-run after editing roads or buildings. Door and tire prefabs are authored assets and only loaded;
    // the other prefabs are regenerated on every run.
    public static class HazardSlotInstaller
    {
        private const string RootName = "Hazards";
        private const string OldRootName = "HazardSlots";
        private const string PrefabFolder = "Assets/_Game/Prefabs/Hazards/";
        private const string DoorPrefabPath = PrefabFolder + "SwingDoor.prefab";
        private const string TirePrefabPath = PrefabFolder + "RollingTire.prefab";
        private const string SettingsPath = "Assets/_Game/Settings/HazardSpawnSettings.asset";
        private const string CandidatesPath = "Assets/_Game/Settings/HazardCandidates.asset";
        private const string MaterialFolder = "Assets/_Game/Art/FourDistrict/";
        // Candidate rules (test values): a road spot every SpotSpacing metres, none near road ends, junctions,
        // shops, delivery doors or home; crossing hazards need a straight stretch.
        private const float SpotSpacing = 5f, EndMargin = 6f, JunctionMargin = 2f, KeepOut = 10f;
        private const float StraightReach = 6f, StraightDegrees = 12f, RouteReach = 7f, GraphStep = 2f, GraphLink = 2.5f;

        private sealed class HazardPrefabs
        {
            public GameObject manhole, hydrant, melon, door, tire, ducks, chickens, pigeon, muscleman, celebrity;
        }

        private sealed class Bake
        {
            public List<RoadPath> roads;
            public DistrictLayout.Layout layout;
            public HashSet<Collider> terrain;
            public List<Vector3> keepOut;
            public float melonHalfExtent;
            public readonly List<HazardCandidates.Spot> spots = new List<HazardCandidates.Spot>();
            public readonly List<HazardCandidate> candidates = new List<HazardCandidate>();
        }

        private sealed class PathTree
        {
            public float[] cost;
            public int[] previous;
        }

        [MenuItem("Delivery Rider/Apply Hazard Spawning")]
        public static void Apply()
        {
            if (EditorApplication.isPlaying || EditorApplication.isCompiling) throw new InvalidOperationException("Stop Play Mode and finish compilation first.");
            for (int i = 0; i < SceneManager.sceneCount; i++)
                if (SceneManager.GetSceneAt(i).isDirty) throw new InvalidOperationException("Save scene changes first.");
            var scene = EditorSceneManager.OpenScene(DistrictLayout.ScenePath, OpenSceneMode.Single);
            var roots = scene.GetRootGameObjects();
            var components = roots.SelectMany(g => g.GetComponentsInChildren<Component>(true)).ToArray();
            var session = components.OfType<DeliverySession>().Single();
            var rider = components.OfType<RiderMotor>().Single();
            var network = components.OfType<RoadNetwork>().Single();
            var prefabs = CreatePrefabs(session);
            var settings = SpawnSettings(prefabs);

            // Old fixed slots and an earlier planner root go first, so they never block candidate checks.
            foreach (var old in roots.Where(g => g.name == OldRootName || g.name == RootName)) UnityEngine.Object.DestroyImmediate(old);

            var sessionData = new SerializedObject(session);
            var shops = References<Transform>(sessionData.FindProperty("shopPoints"));
            var doors = References<Transform>(sessionData.FindProperty("doorPoints"));
            var bake = new Bake
            {
                roads = RoadPath.FromScene(scene),
                layout = DistrictLayout.ReadLayout(),
                terrain = new HashSet<Collider>(References<Collider>(new SerializedObject(network).FindProperty("ground"))),
                keepOut = shops.Concat(doors).Select(t => t.position).Append(rider.transform.position).ToList(),
                melonHalfExtent = RollingMelonHazard.PileHalfExtent(rider.GetComponent<CapsuleCollider>().height * Mathf.Abs(rider.transform.lossyScale.y) * 0.3f),
            };
            foreach (var road in bake.roads) BakeRoad(bake, road);
            AssignRoutes(bake, shops, doors);
            MusclemanInstaller.AppendCandidates(bake.spots, bake.candidates, session, components.OfType<PedestrianNetwork>().Single(), network);
            CelebrityCrowdInstaller.AppendCandidates(bake.spots, bake.candidates, session, components.OfType<PedestrianNetwork>().Single(), network);

            var asset = AssetDatabase.LoadAssetAtPath<HazardCandidates>(CandidatesPath);
            if (asset == null) { asset = ScriptableObject.CreateInstance<HazardCandidates>(); AssetDatabase.CreateAsset(asset, CandidatesPath); }
            asset.Configure(bake.spots.ToArray(), bake.candidates.ToArray());
            EditorUtility.SetDirty(asset);

            var root = new GameObject(RootName);
            var planner = root.AddComponent<HazardPlanner>();
            var data = new SerializedObject(planner);
            data.FindProperty("session").objectReferenceValue = session;
            data.FindProperty("rider").objectReferenceValue = rider;
            data.FindProperty("ground").objectReferenceValue = network;
            data.FindProperty("settings").objectReferenceValue = settings;
            data.FindProperty("candidates").objectReferenceValue = asset;
            data.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.MarkSceneDirty(scene);
            AssetDatabase.SaveAssets();
            if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("Could not save the hazard planner.");
            string byType = string.Join(", ", bake.candidates.GroupBy(c => c.type).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()}"));
            Debug.Log($"Hazard spawning: {bake.spots.Count} road spots ({bake.spots.Count(s => s.routes.Length == 0)} on no call route), "
                + $"{bake.candidates.Count} candidates ({byType}). Play Mode not started.");
        }

        // Created once with the agreed starting counts; later runs only fill in missing prefabs and types.
        private static HazardSpawnSettings SpawnSettings(HazardPrefabs prefabs)
        {
            var defaults = new[]
            {
                Kind("celebrity", prefabs.celebrity, HazardRole.Object, 1, 1),
                Kind("manhole", prefabs.manhole, HazardRole.Object, 1, 6, 8, 10, 12),
                Kind("hydrant", prefabs.hydrant, HazardRole.Object, 1, 4, 5, 6, 6),
                Kind("melon", prefabs.melon, HazardRole.Object, 1, 4, 5, 6, 8),
                Kind("door", prefabs.door, HazardRole.Object, 1, 3, 4, 5, 6),
                Kind("muscleman", prefabs.muscleman, HazardRole.Object, 1, 8, 8, 8, 8),
                Kind("duck", prefabs.ducks, HazardRole.Trigger, 1),
                Kind("chicken", prefabs.chickens, HazardRole.Trigger, 1),
                Kind("tire", prefabs.tire, HazardRole.Trigger, 2),
                Kind("theft", prefabs.pigeon, HazardRole.Trigger, 2),
            };
            var settings = AssetDatabase.LoadAssetAtPath<HazardSpawnSettings>(SettingsPath);
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<HazardSpawnSettings>();
                settings.Configure(defaults);
                AssetDatabase.CreateAsset(settings, SettingsPath);
                return settings;
            }
            var kinds = settings.Kinds.ToList();
            foreach (var fallback in defaults)
            {
                var kind = kinds.FirstOrDefault(k => k.type == fallback.type);
                if (kind == null) kinds.Add(fallback);
                else if (kind.prefab == null) kind.prefab = fallback.prefab;
            }
            settings.Configure(kinds.ToArray());
            EditorUtility.SetDirty(settings);
            return settings;
        }

        private static HazardSpawnSettings.Kind Kind(string type, GameObject prefab, HazardRole role, int unlockDay, params int[] countPerDay) =>
            new HazardSpawnSettings.Kind { type = type, prefab = prefab, role = role, unlockDay = unlockDay, countPerDay = countPerDay, poolSize = 10 };

        private static T[] References<T>(SerializedProperty array) where T : UnityEngine.Object
        {
            var values = new List<T>();
            for (int i = 0; i < array.arraySize; i++)
                if (array.GetArrayElementAtIndex(i).objectReferenceValue is T value) values.Add(value);
            return values.ToArray();
        }

        // ---- candidates ----

        private static void BakeRoad(Bake bake, RoadPath road)
        {
            for (float d = EndMargin; d <= road.Length - EndMargin; d += SpotSpacing)
            {
                var flat = road.PointAt(d);
                if (InJunction(bake, road, flat) || bake.keepOut.Any(p => (new Vector2(p.x, p.z) - flat).sqrMagnitude < KeepOut * KeepOut)) continue;
                var direction = road.DirectionAt(d);
                var along = new Vector3(direction.x, 0f, direction.y);
                var right = Vector3.Cross(Vector3.up, along);
                var center = new Vector3(flat.x, 0f, flat.y);
                float half = road.HalfWidth;
                bool straight = Vector2.Dot(road.DirectionAt(d - StraightReach), road.DirectionAt(d + StraightReach)) >= Mathf.Cos(StraightDegrees * Mathf.Deg2Rad);
                int spot = bake.spots.Count;
                bake.spots.Add(new HazardCandidates.Spot { position = Ground(bake.layout, center) });

                foreach (int side in new[] { -1, 1 })
                {
                    // A manhole lies on the drawn road surface (asphalt or alley paving sits above the terrain). On main
                    // roads it sits in the middle of a lane, clear of the centre dashes; an alley has one lane down its middle.
                    if (!road.Alley || side > 0)
                    {
                        var lid = road.Alley ? center : center + right * side * half * 0.5f;
                        float surface = road.Alley ? RoadSurface.AlleyLift : RoadSurface.AsphaltLift;
                        Add(bake, "manhole", spot, road, Ground(bake.layout, lid) + Vector3.up * (surface + 0.005f), Tilted(bake.layout, lid, along), side);
                    }
                    Add(bake, "chicken", spot, road, Ground(bake.layout, center + right * side * Mathf.Max(0f, half - 0.8f)), Quaternion.LookRotation(along), side);
                    // Keep the entire resting pile on the sidewalk, or just inside the alley edge.
                    float pileHalf = bake.melonHalfExtent;
                    bool fits = road.Alley ? half >= pileHalf + 0.05f : road.SidewalkWidth >= pileHalf * 2f + 0.1f;
                    float melonOffset = road.Alley ? half - pileHalf - 0.05f : half + pileHalf + 0.05f;
                    var melon = Ground(bake.layout, center + right * side * melonOffset);
                    if (fits && ClearPile(bake, melon, along, pileHalf) && !InJunction(bake, road, new Vector2(melon.x, melon.z)))
                        Add(bake, "melon", spot, road, melon, Quaternion.LookRotation(along), side);
                    // A door needs a wall right behind it at the building line (alleys: the alley edge).
                    var door = Ground(bake.layout, center + right * side * (road.Alley ? half : half + 3f)) + Vector3.up * 0.03f;
                    if (Wall(bake, door, right * side))
                        Add(bake, "door", spot, road, door, Quaternion.LookRotation(side < 0 ? along : -along), side);
                    if (road.Alley) continue;

                    var hydrant = Ground(bake.layout, center + right * side * (half + 0.6f));
                    if (Clear(bake, hydrant) && !InJunction(bake, road, new Vector2(hydrant.x, hydrant.z)))
                        Add(bake, "hydrant", spot, road, hydrant, Quaternion.LookRotation(-right * side), side);
                    if (straight)
                    {
                        if (Clear(bake, Ground(bake.layout, center + right * side * (half + 2.5f))))
                            Add(bake, "tire", spot, road, Ground(bake.layout, center), Quaternion.LookRotation(along), side);
                        Add(bake, "duck", spot, road, Ground(bake.layout, center), Quaternion.LookRotation(along), side);
                    }
                    // The pigeon flies off straight along the road; every escape point must stay over a road or sidewalk.
                    var pole = Ground(bake.layout, center + right * side * (half + 1f));
                    if (!Clear(bake, pole)) continue;
                    foreach (bool flip in new[] { false, true })
                    {
                        var away = flip ? -along : along;
                        bool open = true;
                        for (int i = 0; i < 16 && open; i++)
                        {
                            var point = center + away * (4f + i * 3f);
                            open = OnAnyRoad(bake, new Vector2(point.x, point.z))
                                && !Physics.CheckSphere(Ground(bake.layout, point) + Vector3.up * 1.5f, 0.4f, ~0, QueryTriggerInteraction.Ignore);
                        }
                        if (open) Add(bake, "theft", spot, road, pole, Quaternion.LookRotation(along), side, flip);
                    }
                }
            }
        }

        private static void Add(Bake bake, string type, int spot, RoadPath road, Vector3 position, Quaternion rotation, int side, bool flip = false) =>
            bake.candidates.Add(new HazardCandidate { type = type, spot = spot, position = position, rotation = rotation, halfWidth = road.HalfWidth, side = side, alley = road.Alley, flip = flip });

        // A manhole lies flat on the terrain, tilted to its slope.
        private static Quaternion Tilted(DistrictLayout.Layout layout, Vector3 p, Vector3 forward)
        {
            float dx = DistrictLayout.Height(layout, p.x + 0.5f, p.z) - DistrictLayout.Height(layout, p.x - 0.5f, p.z);
            float dz = DistrictLayout.Height(layout, p.x, p.z + 0.5f) - DistrictLayout.Height(layout, p.x, p.z - 0.5f);
            var normal = new Vector3(-dx, 1f, -dz).normalized;
            return Quaternion.LookRotation(Vector3.ProjectOnPlane(forward, normal), normal);
        }

        // Inside another road's carriageway (a junction or crossing), measured from that road's centre line.
        private static bool InJunction(Bake bake, RoadPath self, Vector2 flat)
        {
            foreach (var road in bake.roads)
            {
                if (road == self || !road.Near(new Rect(flat, Vector2.zero), road.HalfWidth + JunctionMargin)) continue;
                if (road.Nearest(flat, out _) < road.HalfWidth + JunctionMargin) return true;
            }
            return false;
        }

        private static bool OnAnyRoad(Bake bake, Vector2 flat)
        {
            foreach (var road in bake.roads)
            {
                float reach = road.HalfWidth + road.SidewalkWidth;
                if (road.Near(new Rect(flat, Vector2.zero), reach) && road.Nearest(flat, out _) <= reach) return true;
            }
            return false;
        }

        // Nothing solid (other than the terrain) where a sidewalk hazard would stand.
        private static bool Clear(Bake bake, Vector3 feet)
        {
            var hits = Physics.OverlapCapsule(feet + Vector3.up * 0.5f, feet + Vector3.up * 1.5f, 0.5f, ~0, QueryTriggerInteraction.Ignore);
            return hits.All(h => bake.terrain.Contains(h));
        }

        private static bool ClearPile(Bake bake, Vector3 feet, Vector3 along, float halfExtent)
        {
            var hits = Physics.OverlapBox(feet + Vector3.up * 1.2f, new Vector3(halfExtent, 1.1f, halfExtent), Quaternion.LookRotation(along), ~0, QueryTriggerInteraction.Ignore);
            return hits.All(h => bake.terrain.Contains(h));
        }

        private static bool Wall(Bake bake, Vector3 feet, Vector3 outward)
        {
            var origin = feet + Vector3.up * 1.2f - outward * 0.3f;
            foreach (var hit in Physics.RaycastAll(origin, outward, 2.5f, ~0, QueryTriggerInteraction.Ignore))
                if (!bake.terrain.Contains(hit.collider) && hit.rigidbody == null) return true;
            return false;
        }

        // ---- call routes per spot ----
        // A road graph sampled from the splines (nodes every GraphStep metres, linked across junctions) gives the full and
        // the main-road shortest path of every shop -> door call; a spot is on a call when either path passes within RouteReach.
        private static void AssignRoutes(Bake bake, Transform[] shops, Transform[] doors)
        {
            var nodes = new List<Vector2>();
            var alley = new List<bool>();
            var owner = new List<int>();
            var links = new List<List<(int to, float cost)>>();
            for (int r = 0; r < bake.roads.Count; r++)
            {
                var road = bake.roads[r];
                int count = Mathf.Max(1, Mathf.CeilToInt(road.Length / GraphStep));
                for (int i = 0; i <= count; i++)
                {
                    nodes.Add(road.PointAt(road.Length * i / count));
                    alley.Add(road.Alley);
                    owner.Add(r);
                    links.Add(new List<(int, float)>());
                    if (i > 0) Link(links, nodes, nodes.Count - 2, nodes.Count - 1);
                }
            }
            var grid = Grid(nodes, GraphLink);
            for (int a = 0; a < nodes.Count; a++)
                foreach (int b in Around(grid, nodes[a], GraphLink))
                    if (b > a && owner[a] != owner[b] && (nodes[a] - nodes[b]).sqrMagnitude <= GraphLink * GraphLink) Link(links, nodes, a, b);

            var spotPoints = bake.spots.Select(s => new Vector2(s.position.x, s.position.z)).ToList();
            var spotGrid = Grid(spotPoints, RouteReach);
            var spotsNear = new List<int>[nodes.Count];
            for (int n = 0; n < nodes.Count; n++)
                spotsNear[n] = Around(spotGrid, nodes[n], RouteReach).Where(s => (spotPoints[s] - nodes[n]).sqrMagnitude <= RouteReach * RouteReach).ToList();

            var routes = bake.spots.Select(_ => new SortedSet<int>()).ToArray();
            for (int shop = 0; shop < shops.Length; shop++)
            {
                foreach (bool mainOnly in new[] { false, true })
                {
                    var tree = ShortestPaths(links, alley, Nearest(nodes, alley, shops[shop].position, mainOnly), mainOnly);
                    if (tree == null) continue;
                    for (int door = 0; door < doors.Length; door++)
                    {
                        int target = Nearest(nodes, alley, doors[door].position, mainOnly);
                        if (target < 0 || float.IsPositiveInfinity(tree.cost[target])) continue;
                        int route = shop * doors.Length + door;
                        for (int n = target; n >= 0; n = tree.previous[n])
                            foreach (int s in spotsNear[n]) routes[s].Add(route);
                    }
                }
            }
            for (int s = 0; s < bake.spots.Count; s++) bake.spots[s].routes = routes[s].ToArray();
        }

        private static void Link(List<List<(int to, float cost)>> links, List<Vector2> nodes, int a, int b)
        {
            float cost = Vector2.Distance(nodes[a], nodes[b]);
            links[a].Add((b, cost));
            links[b].Add((a, cost));
        }

        private static int Nearest(List<Vector2> nodes, List<bool> alley, Vector3 position, bool mainOnly)
        {
            var flat = new Vector2(position.x, position.z);
            int best = -1;
            float bestDistance = float.PositiveInfinity;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (mainOnly && alley[i]) continue;
                float d = (nodes[i] - flat).sqrMagnitude;
                if (d < bestDistance) { bestDistance = d; best = i; }
            }
            return best;
        }

        // Dijkstra over the road graph; main-only searches skip alley nodes.
        private static PathTree ShortestPaths(List<List<(int to, float cost)>> links, List<bool> alley, int start, bool mainOnly)
        {
            if (start < 0) return null;
            var tree = new PathTree { cost = Enumerable.Repeat(float.PositiveInfinity, links.Count).ToArray(), previous = Enumerable.Repeat(-1, links.Count).ToArray() };
            var open = new SortedSet<(float cost, int node)>();
            tree.cost[start] = 0f;
            open.Add((0f, start));
            while (open.Count > 0)
            {
                var (c, u) = open.Min;
                open.Remove(open.Min);
                foreach (var (v, w) in links[u])
                {
                    if (mainOnly && alley[v] || c + w >= tree.cost[v]) continue;
                    if (!float.IsPositiveInfinity(tree.cost[v])) open.Remove((tree.cost[v], v));
                    tree.cost[v] = c + w;
                    tree.previous[v] = u;
                    open.Add((tree.cost[v], v));
                }
            }
            return tree;
        }

        private static Dictionary<Vector2Int, List<int>> Grid(List<Vector2> points, float cell)
        {
            var grid = new Dictionary<Vector2Int, List<int>>();
            for (int i = 0; i < points.Count; i++)
            {
                var key = Vector2Int.FloorToInt(points[i] / cell);
                if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<int>();
                list.Add(i);
            }
            return grid;
        }

        private static IEnumerable<int> Around(Dictionary<Vector2Int, List<int>> grid, Vector2 point, float cell)
        {
            var key = Vector2Int.FloorToInt(point / cell);
            for (int x = -1; x <= 1; x++)
                for (int y = -1; y <= 1; y++)
                    if (grid.TryGetValue(key + new Vector2Int(x, y), out var list))
                        foreach (int i in list) yield return i;
        }

        // ---- prefab templates ----

        private static HazardPrefabs CreatePrefabs(DeliverySession session)
        {
            var prefabs = new HazardPrefabs
            {
                door = AssetDatabase.LoadAssetAtPath<GameObject>(DoorPrefabPath),
                tire = AssetDatabase.LoadAssetAtPath<GameObject>(TirePrefabPath),
            };
            if (prefabs.door == null || prefabs.door.GetComponent<SwingDoorHazard>() == null
                || prefabs.tire == null || prefabs.tire.GetComponent<RollingTireHazard>() == null)
                throw new InvalidOperationException("Door and tire prefabs are required in Prefabs/Hazards.");
            prefabs.manhole = ManholePrefab();
            prefabs.hydrant = HydrantPrefab();
            prefabs.melon = RollingMelonInstaller.CreatePrefab(session, PrefabFolder + "MelonPile.prefab").gameObject;
            prefabs.ducks = DuckPrefab();
            prefabs.chickens = ChickenPrefab();
            prefabs.pigeon = PigeonPrefab();
            prefabs.muscleman = MusclemanInstaller.CreatePrefab(session);
            prefabs.celebrity = CelebrityCrowdInstaller.CreatePrefab(UnityEngine.Object.FindFirstObjectByType<PedestrianNetwork>());
            return prefabs;
        }

        private static GameObject SavePrefab(GameObject root)
        {
            string path = PrefabFolder + root.name + ".prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
            UnityEngine.Object.DestroyImmediate(root);
            if (prefab == null) throw new InvalidOperationException("Could not save " + path);
            return prefab;
        }

        // Manhole: a trigger volume with a flat lid, a warning ring and a burst effect.
        private static GameObject ManholePrefab()
        {
            var obj = new GameObject("Manhole");
            var volume = obj.AddComponent<BoxCollider>();
            volume.isTrigger = true;
            volume.center = new Vector3(0f, 0.65f, 0f);
            volume.size = new Vector3(1.8f, 1.5f, 1.8f);
            var hazard = obj.AddComponent<GroundContactHazard>();
            Child(Shape("Lid", PrimitiveType.Cylinder, new Vector3(1.7f, 0.035f, 1.7f), Material("ManholeIron", new Color(0.18f, 0.22f, 0.25f)), obj.transform), new Vector3(0f, 0.035f, 0f));
            var lamp = Shape("Warning", PrimitiveType.Cylinder, new Vector3(1.9f, 0.02f, 1.9f), Material("HazardWarning", new Color(1f, 0.65f, 0.08f)), obj.transform);
            Child(lamp, new Vector3(0f, 0.09f, 0f));
            lamp.GetComponent<Renderer>().enabled = false;
            var steam = Shape("Burst", PrimitiveType.Cylinder, new Vector3(0.65f, 0.75f, 0.65f), Material("Steam", new Color(0.85f, 0.95f, 1f)), obj.transform);
            Child(steam, new Vector3(0f, 0.8f, 0f));
            steam.SetActive(false);
            var data = new SerializedObject(hazard);
            data.FindProperty("kind").enumValueIndex = (int)GroundHazardKind.Manhole;
            data.FindProperty("warningLamp").objectReferenceValue = lamp.GetComponent<Renderer>();
            data.FindProperty("effect").objectReferenceValue = steam;
            data.ApplyModifiedPropertiesWithoutUndo();
            return SavePrefab(obj);
        }

        // Hydrant: stands on the sidewalk edge; touching it bursts at once and throws the rider high into the road.
        private static GameObject HydrantPrefab()
        {
            var obj = new GameObject("Hydrant");
            var volume = obj.AddComponent<BoxCollider>();
            volume.isTrigger = true;
            volume.center = new Vector3(0f, 1f, 0f);
            volume.size = new Vector3(1.6f, 2.2f, 1.6f);
            var red = Material("HydrantRed", new Color(0.8f, 0.12f, 0.1f));
            Child(Shape("Post", PrimitiveType.Cylinder, new Vector3(0.45f, 0.5f, 0.45f), red, obj.transform), new Vector3(0f, 0.5f, 0f));
            Child(Shape("Cap", PrimitiveType.Sphere, new Vector3(0.5f, 0.3f, 0.5f), red, obj.transform), new Vector3(0f, 1.02f, 0f));
            var lamp = Shape("Warning", PrimitiveType.Cylinder, new Vector3(1.6f, 0.02f, 1.6f), Material("HazardWarning", new Color(1f, 0.65f, 0.08f)), obj.transform);
            Child(lamp, new Vector3(0f, 0.05f, 0f));
            lamp.GetComponent<Renderer>().enabled = false;
            var water = Shape("Burst", PrimitiveType.Cylinder, new Vector3(0.7f, 2.5f, 0.7f), Material("HydrantWater", new Color(0.55f, 0.8f, 1f)), obj.transform);
            Child(water, new Vector3(0f, 3.2f, 0f));
            water.SetActive(false);
            var hazard = obj.AddComponent<GroundContactHazard>();
            var data = new SerializedObject(hazard);
            data.FindProperty("kind").enumValueIndex = (int)GroundHazardKind.Hydrant;
            data.FindProperty("warningLamp").objectReferenceValue = lamp.GetComponent<Renderer>();
            data.FindProperty("effect").objectReferenceValue = water;
            data.FindProperty("activeSeconds").floatValue = 0.8f;
            data.FindProperty("forwardImpulse").floatValue = 2f;
            data.FindProperty("upwardImpulse").floatValue = 11f;
            data.ApplyModifiedPropertiesWithoutUndo();
            return SavePrefab(obj);
        }

        // Duck flock: five hidden ducks; each slot sets where they cross.
        private static GameObject DuckPrefab()
        {
            var obj = new GameObject("DuckFlock");
            var yellow = Material("DuckYellow", new Color(0.98f, 0.85f, 0.25f));
            var orange = Material("BeakOrange", new Color(0.95f, 0.5f, 0.1f));
            var birds = new Transform[5];
            for (int i = 0; i < birds.Length; i++)
            {
                var bird = new GameObject("Duck_" + i).transform;
                bird.SetParent(obj.transform, false);
                Child(Shape("Body", PrimitiveType.Sphere, new Vector3(0.45f, 0.35f, 0.6f), yellow, bird), new Vector3(0f, 0.22f, 0f));
                Child(Shape("Head", PrimitiveType.Sphere, new Vector3(0.25f, 0.25f, 0.25f), yellow, bird), new Vector3(0f, 0.48f, 0.22f));
                Child(Shape("Beak", PrimitiveType.Cube, new Vector3(0.1f, 0.05f, 0.14f), orange, bird), new Vector3(0f, 0.46f, 0.38f));
                bird.gameObject.SetActive(false);
                birds[i] = bird;
            }
            var hazard = obj.AddComponent<CrossingFlockHazard>();
            var data = new SerializedObject(hazard);
            SetArray(data.FindProperty("birds"), birds);
            data.FindProperty("localStart").vector3Value = new Vector3(6f, 0f, 0f);
            data.FindProperty("localEnd").vector3Value = new Vector3(-6f, 0f, 0f);
            data.FindProperty("approachSize").vector3Value = new Vector3(12f, 3f, 16f);
            data.ApplyModifiedPropertiesWithoutUndo();
            return SavePrefab(obj);
        }

        // Chickens: four chickens on the road edge; brushing past them sets the flock chasing.
        private static GameObject ChickenPrefab()
        {
            var obj = new GameObject("Chickens");
            var white = Material("ChickenWhite", new Color(0.95f, 0.95f, 0.92f));
            var red = Material("ChickenComb", new Color(0.85f, 0.1f, 0.1f));
            var birds = new Transform[4];
            for (int i = 0; i < birds.Length; i++)
            {
                var bird = new GameObject("Chicken_" + i).transform;
                bird.SetParent(obj.transform, false);
                float angle = i * Mathf.PI * 0.5f;
                bird.localPosition = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * 0.7f;
                bird.localRotation = Quaternion.Euler(0f, i * 90f + 45f, 0f);
                Child(Shape("Body", PrimitiveType.Sphere, new Vector3(0.4f, 0.4f, 0.5f), white, bird), new Vector3(0f, 0.3f, 0f));
                Child(Shape("Head", PrimitiveType.Sphere, new Vector3(0.22f, 0.24f, 0.22f), white, bird), new Vector3(0f, 0.58f, 0.18f));
                Child(Shape("Comb", PrimitiveType.Cube, new Vector3(0.05f, 0.1f, 0.12f), red, bird), new Vector3(0f, 0.72f, 0.18f));
                birds[i] = bird;
            }
            var hazard = obj.AddComponent<ChaseFlockHazard>();
            var data = new SerializedObject(hazard);
            SetArray(data.FindProperty("birds"), birds);
            data.ApplyModifiedPropertiesWithoutUndo();
            return SavePrefab(obj);
        }

        // Pigeon snatch: pole, perch, the pigeon (a FoodThief) and 16 escape points; each slot lays out the route.
        private static GameObject PigeonPrefab()
        {
            var obj = new GameObject("PigeonSnatch");
            var gray = Material("PigeonGray", new Color(0.55f, 0.58f, 0.65f));
            Child(Shape("Pole", PrimitiveType.Cylinder, new Vector3(0.12f, 1.25f, 0.12f), Material("PolePaint", new Color(0.3f, 0.3f, 0.32f)), obj.transform), new Vector3(0f, 1.25f, 0f));
            var perch = new GameObject("Perch").transform;
            perch.SetParent(obj.transform, false);
            perch.localPosition = Vector3.up * 2.75f;

            var pigeon = new GameObject("Pigeon");
            pigeon.transform.SetParent(obj.transform, false);
            pigeon.transform.localPosition = perch.localPosition;
            Child(Shape("Body", PrimitiveType.Sphere, new Vector3(0.35f, 0.3f, 0.45f), gray, pigeon.transform), Vector3.zero);
            Child(Shape("Head", PrimitiveType.Sphere, new Vector3(0.18f, 0.18f, 0.18f), gray, pigeon.transform), new Vector3(0f, 0.15f, 0.2f));
            var bag = Shape("StolenFood", PrimitiveType.Cube, new Vector3(0.3f, 0.22f, 0.3f), Material("StolenFood", new Color(0.95f, 0.6f, 0.2f)), pigeon.transform);
            Child(bag, new Vector3(0f, -0.25f, 0f));
            bag.SetActive(false);
            var body = pigeon.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.mass = 0.3f;
            body.constraints = RigidbodyConstraints.FreezeRotation;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            pigeon.AddComponent<SphereCollider>().radius = 0.35f;
            var thief = pigeon.AddComponent<FoodThief>();

            var lamp = Shape("Warning", PrimitiveType.Sphere, Vector3.one * 0.35f, Material("HazardWarning", new Color(1f, 0.65f, 0.08f)), obj.transform);
            Child(lamp, new Vector3(0f, 3.3f, 0f));
            lamp.GetComponent<Renderer>().enabled = false;

            var routeRoot = new GameObject("EscapeRoute").transform;
            routeRoot.SetParent(obj.transform, false);
            var route = new Transform[16];
            for (int i = 0; i < route.Length; i++)
            {
                var point = new GameObject("Point_" + i).transform;
                point.SetParent(routeRoot, false);
                point.localPosition = new Vector3(0f, 1.5f, 4f + i * 3f);
                route[i] = point;
            }

            var hazard = obj.AddComponent<PigeonSnatchHazard>();
            var data = new SerializedObject(hazard);
            data.FindProperty("pigeon").objectReferenceValue = thief;
            data.FindProperty("perch").objectReferenceValue = perch;
            SetArray(data.FindProperty("escapeRoute"), route);
            data.FindProperty("warningLamp").objectReferenceValue = lamp.GetComponent<Renderer>();
            data.ApplyModifiedPropertiesWithoutUndo();
            return SavePrefab(obj);
        }

        private static void Child(GameObject shape, Vector3 localPosition)
        {
            shape.transform.localPosition = localPosition;
            shape.transform.localRotation = Quaternion.identity;
        }

        private static void SetArray(SerializedProperty property, UnityEngine.Object[] values)
        {
            property.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++) property.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
        }

        // Visual-only primitive (collider removed).
        private static GameObject Shape(string name, PrimitiveType type, Vector3 scale, Material material, Transform parent)
        {
            var obj = GameObject.CreatePrimitive(type);
            obj.name = name;
            obj.transform.SetParent(parent, false);
            obj.transform.localScale = scale;
            obj.GetComponent<Renderer>().sharedMaterial = material;
            UnityEngine.Object.DestroyImmediate(obj.GetComponent<Collider>());
            return obj;
        }

        private static Material Material(string name, Color color)
        {
            string path = MaterialFolder + name + ".mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null) return material;
            material = new Material(Shader.Find(CurvedWorldInstaller.LitShader)) { name = name, color = color };
            material.SetFloat("_Smoothness", 0.1f);
            AssetDatabase.CreateAsset(material, path);
            return material;
        }

        private static Vector3 Ground(DistrictLayout.Layout layout, Vector3 p)
        {
            p.y = DistrictLayout.Height(layout, p.x, p.z);
            return p;
        }
    }
}
