using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using DeliveryRider;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DeliveryRider.Editor
{
    // Batch order: BakeRoads -> bake/save expanded PedestrianNetwork -> BakePedestrians ->
    // link_myeongji_hazards.py -> ImportRoutes. Every write targets a NEW copy, never source settings or scenes.
    // Pedestrian candidates use the existing installers, which sample paths by array index. reverse/next must refer
    // to the SAME final PedestrianNetwork; rebaking/reordering that network requires repeating phase 2.
    // -hazardIncludeOldRoads true: the original roads get the same rules too, appended after the frozen 887 candidates.
    // Those keep their indices and values; a new candidate is skipped where one of its kind and side already stands.
    public static class MyeongjiHazardExpansionBake
    {
        private const string SourcePath = "Assets/_Game/Settings/MyeongjiHazardCandidates.asset";
        private const string ScenePath = "Assets/_Game/Scenes/FourDistrictPrototype_Myeongji.unity";
        private const string DataPath = "Docs/MapLayout/Myeongji/";
        private const int OriginalCandidates = 887, OriginalSpots = 306;
        private const float SidewalkReach = 3f, SamePlace = 1f;

        [Serializable]
        public sealed class BakeData
        {
            public HazardCandidates.Spot[] spots;
            public HazardCandidate[] candidates;
            public HazardCandidates.RoutePath[] routePaths;
        }

        private sealed class Context : IDisposable
        {
            public readonly Scene scene;
            public readonly JToken[] roads;
            public readonly JToken[] newRoads;
            public readonly List<Vector3> keep;
            public readonly Vector3 home;
            public readonly MeshCollider[] floors;
            public readonly Transform map;
            public readonly bool allowGeometry;
            public int meshChecks, geometryChecks, rejectedGround;
            private readonly GameObject[] barriers;
            private readonly bool[] active;

            public Context()
            {
                scene = EditorSceneManager.OpenScene(Arg("-hazardScene", ScenePath), OpenSceneMode.Single);
                var roots = scene.GetRootGameObjects();
                map = roots.Single(g => g.name == "MyeongjiMap").transform;
                var floorRoot = map.Find("Ground");
                floors = floorRoot == null ? Array.Empty<MeshCollider>() : floorRoot.GetComponentsInChildren<MeshCollider>();
                var layout = JObject.Parse(File.ReadAllText(DataPath + "layout_expanded.json"));
                roads = layout["roads"].ToArray();
                var old = JObject.Parse(File.ReadAllText(DataPath + "layout.json"))["roads"].ToArray();
                if ((int)layout["existingRoadCount"] != old.Length || roads.Length <= old.Length)
                    throw new InvalidOperationException("Invalid original-road prefix.");
                for (int i = 0; i < old.Length; i++)
                    if (!JToken.DeepEquals(old[i], roads[i])) throw new InvalidOperationException("Original road changed: " + i);
                newRoads = roads.Skip(old.Length).ToArray();
                var anchors = JArray.Parse(File.ReadAllText(DataPath + "anchors_expanded.json"));
                keep = anchors.Select(a => P3(a["position"])).ToList();
                home = P3(anchors.Single(a => (string)a["name"] == "C_Home")["position"]);
                string geometry = Arg("-hazardAllowGeometry", "false");
                if (geometry != "true" && geometry != "false") throw new ArgumentException("-hazardAllowGeometry must be true or false.");
                allowGeometry = geometry == "true";
                if (!allowGeometry)
                    foreach (var road in newRoads)
                    {
                        var a = P2(road["a"]); var delta = P2(road["b"]) - a;
                        for (float d = 5f; d < delta.magnitude - 5f; d += 4f)
                            if (!Ground(a + delta.normalized * d, out _))
                                throw new InvalidOperationException("Expanded floor missing on " + (string)road["id"] + "; install floor first, or explicitly use -hazardAllowGeometry true for a diagnostic copy.");
                    }
                barriers = roots.Where(g => g.name == "ZoneBarriers").ToArray();
                active = barriers.Select(g => g.activeSelf).ToArray();
                foreach (var barrier in barriers) barrier.SetActive(false);
                Physics.SyncTransforms();
            }

            public bool Ground(Vector3 p, out Vector3 feet)
            {
                feet = p;
                foreach (var floor in floors)
                    if (floor.enabled && floor.Raycast(new Ray(new Vector3(p.x, 25f, p.z), Vector3.down), out var hit, 40f))
                    { feet = hit.point; meshChecks++; return true; }
                // Explicit diagnostic fallback: carriageway plus the existing baker's 3 m roadside corridor.
                // This cannot prove walls, future buildings or future sidewalk meshes are clear.
                if (allowGeometry)
                {
                    var near = roads.OrderBy(r => Distance(p, r)).First();
                    if (Distance(p, near) <= (float)near["width"] * .5f + SidewalkReach)
                    {
                        feet.y = Distance(p, near) <= (float)near["width"] * .5f ? -.14f : 0f;
                        geometryChecks++; return true;
                    }
                }
                rejectedGround++; return false;
            }

            public bool Clear(Vector3 p, float radius = .65f, float height = 2f)
            {
                if (!Ground(p, out var feet)) return false;
                return !Physics.OverlapCapsule(feet + Vector3.up * (radius + .07f),
                    feet + Vector3.up * Mathf.Max(radius + .07f, height - radius), radius, ~0,
                    QueryTriggerInteraction.Ignore).Any(c => !floors.Contains(c) && c.attachedRigidbody == null);
            }

            public bool Away(Vector3 p, float clearance = 8f) => Flat(p - home).magnitude >= 11f
                && keep.All(k => Flat(p - k).magnitude >= clearance);
            public bool Junction(Vector3 p, JToken self) => roads.Any(r => r != self && Distance(p, r) < (float)r["width"] * .5f + 2.5f);
            public bool LineClear(Vector3 a, Vector3 b, float radius)
            {
                int n = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(a, b) / .6f));
                for (int i = 0; i <= n; i++) if (!Clear(Vector3.Lerp(a, b, i / (float)n), radius)) return false;
                return true;
            }
            public void Dispose()
            {
                for (int i = 0; i < barriers.Length; i++) barriers[i].SetActive(active[i]);
                Physics.SyncTransforms();
                // No scene is saved. Restore the serialized scene on disk, discarding this probe's edit-mode changes.
                EditorSceneManager.OpenScene(scene.path, OpenSceneMode.Single);
            }
        }

        public static void ExportSource()
        {
            var source = Source();
            WriteJson(Arg("-hazardJson"), new BakeData { spots = source.Spots.ToArray(), candidates = source.Candidates.ToArray() });
            Debug.Log("HAZARD_EXPORT source=asset spots=306 candidates=887 sha256=" + Hash(SourcePath));
        }

        public static void BakeRoads()
        {
            var source = Source();
            string output = OutputPath();
            byte[] sourceBefore = File.ReadAllBytes(SourcePath);
            var spots = source.Spots.Select(CloneSpot).ToList();
            var places = source.Candidates.ToList();
            using (var context = new Context())
            {
                var rider = context.scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<RiderMotor>(true)).Single();
                float pile = RollingMelonHazard.PileHalfExtent(rider.GetComponent<CapsuleCollider>().height * .3f);
                // MyeongjiEvents.Bake rules: 5 m ends, 4 m sampling, 2.5 m junction margin;
                // imported-map clearances take precedence over the procedural four-district installer's defaults.
                bool includeOld = IncludeOldRoads();
                int beforeOld = 0;
                foreach (var road in includeOld ? context.newRoads.Concat(context.roads.Except(context.newRoads)).ToArray() : context.newRoads)
                {
                    bool oldRoad = !context.newRoads.Contains(road);
                    if (oldRoad && beforeOld == 0) beforeOld = places.Count;
                    var a = P2(road["a"]); var b = P2(road["b"]);
                    var along = (b - a).normalized; var right = Vector3.Cross(Vector3.up, along);
                    float half = (float)road["width"] * .5f; bool alley = half < 3f;
                    var rotation = Quaternion.LookRotation(along);
                    for (float d = 5f; d < Vector3.Distance(a, b) - 5f; d += 2f)
                    {
                        var center = a + along * d;
                        if (!context.Ground(center, out center) || !context.Away(center) || context.Junction(center, road) || !context.Clear(center, .6f)) continue;
                        int spotsBefore = spots.Count, placesBefore = places.Count;
                        int spot = oldRoad ? spots.FindIndex(s => Flat(s.position - center).sqrMagnitude < SamePlace) : -1;
                        if (spot < 0) { spot = spots.Count; spots.Add(new HazardCandidates.Spot { position = center }); }
                        foreach (int side in new[] { -1, 1 })
                        {
                            void Add(string type, Vector3 p, Quaternion rot, bool flip = false)
                            {
                                if (!context.Away(p)) return;
                                if (oldRoad && places.Any(c => c.type == type && c.side == side && c.flip == flip && Flat(c.position - p).sqrMagnitude < SamePlace)) return;
                                places.Add(new HazardCandidate { type = type, spot = spot, position = p, rotation = rot,
                                    halfWidth = half, side = side, alley = alley, flip = flip });
                            }
                            if (!alley || side == 1)
                            {
                                var lid = center + (alley ? Vector3.zero : right * side * half * .3f);
                                if (context.Ground(lid, out lid) && context.Clear(lid, .98f) && !context.Junction(lid, road))
                                    Add("manhole", lid + Vector3.up * .008f, rotation);
                            }
                            var chicken = center + right * side * Mathf.Max(0, half - 1.25f);
                            if (context.Ground(chicken, out chicken) && context.Clear(chicken, 1.1f) && context.Away(chicken, 7f) && Flat(chicken - context.home).magnitude > 15f)
                                Add("chicken", chicken, rotation);
                            var melon = center + right * side * (half + pile + .15f);
                            if (context.Ground(melon, out melon) && context.Away(melon, 5f) && !context.Junction(melon, road)
                                && context.Clear(melon, pile + .1f) && context.LineClear(melon - along * 2f, melon + along * 2f, pile)) Add("melon", melon, rotation);
                            var hydrant = center + right * side * (Mathf.Max(.4f, half - 1.2f));
                            if (context.Ground(hydrant, out hydrant) && context.Clear(hydrant, .85f) && !context.Junction(hydrant, road))
                                Add("hydrant", hydrant, Quaternion.LookRotation(-right * side));
                            // Doors are retained as an existing type; new ones still require a real facade, even in geometry mode.
                            var outward = right * side;
                            var wall = Physics.RaycastAll(center + Vector3.up * 1.3f, outward, half + 6f, ~0, QueryTriggerInteraction.Ignore)
                                .Where(h => !context.floors.Contains(h.collider) && h.collider.attachedRigidbody == null
                                    && h.collider.transform.IsChildOf(context.map) && h.collider is MeshCollider).OrderBy(h => h.distance).ToArray();
                            if (wall.Length > 0)
                            {
                                var forward = side < 0 ? along : -along; var facing = Quaternion.LookRotation(forward);
                                var origin = wall[0].point - outward * .24f - forward * 1.2f;
                                if (context.Ground(origin, out origin) && context.Away(origin, 5f))
                                {
                                    bool clear = true;
                                    for (int ai = 0; ai <= 6 && clear; ai++) for (int ri = 1; ri <= 6 && clear; ri++)
                                    {
                                        float angle = ai * Mathf.PI / 12f, radius = ri * .4f;
                                        var p = origin + facing * new Vector3(Mathf.Sin(angle) * radius, 0, Mathf.Cos(angle) * radius);
                                        clear = context.Clear(p, .12f, 2.4f) && context.Away(p, 3.8f);
                                    }
                                    if (clear) Add("door", origin + Vector3.up * .03f, facing);
                                }
                            }
                            var tireStart = center + right * side * (half + (alley ? .5f : 2.5f));
                            var tireEnd = center - right * side * (half + 1f);
                            if (context.Away(tireStart, 5f) && context.Away(tireEnd, 5f) && context.LineClear(tireStart, tireEnd, .65f)) Add("tire", center, rotation);
                            var duckStart = center + right * side * (half + 2f); var duckEnd = center - right * side * (half + 2f);
                            if (context.Away(duckStart, 5f) && context.Away(duckEnd, 5f) && context.LineClear(duckStart, duckEnd, .5f)
                                && context.LineClear(duckStart - along * 1.5f, duckStart + along * 1.5f, .5f))
                            {
                                Add("duck", center, rotation);
                                // PigeonFlockInstaller copies the safe main-road duck places verbatim.
                                if (!alley && half >= 2f) Add("pigeonflock", center, rotation);
                            }
                            // HTML additions; all use the same ground, 8 m destination and junction checks.
                            if (side == 1)
                            {
                                if (context.Clear(center, .6f)) Add("falling", center, rotation);
                                if (context.LineClear(center - along * 2f, center + along * 2f, .6f)
                                    && context.Clear(center + right * Mathf.Min(1f, half - .6f), .35f)
                                    && context.Clear(center - right * Mathf.Min(1f, half - .6f), .35f)) Add("cat", center, rotation);
                                if (!alley && context.LineClear(center - along * 14f, center + along * 6f, .45f)
                                    && context.Clear(center - along * 14f - right * 1.4f, .45f)
                                    && context.Clear(center - along * 14f + right * 1.4f, .45f)) Add("clone", center, rotation);
                            }
                            var dog = center + right * side * Mathf.Max(.6f, half - .6f);
                            if (context.Ground(dog, out dog) && context.Clear(dog, .45f)
                                && context.LineClear(dog - along * 3f, dog + along * 3f, .45f)) Add("dog", dog, rotation);
                            var pole = center + right * side * (half + 1f);
                            if (!context.Ground(pole, out pole) || !context.Clear(pole, .5f) || !context.Away(pole, 5f)) continue;
                            foreach (bool flip in new[] { false, true })
                            {
                                bool clear = true; var direction = flip ? -along : along;
                                for (int i = 0; i < 16 && clear; i++)
                                {
                                    var p = center + direction * (4f + i * 3f);
                                    clear = context.roads.Any(r => Distance(p, r) <= (float)r["width"] * .5f)
                                        && context.Ground(p, out var feet)
                                        && !Physics.CheckSphere(feet + Vector3.up * 1.5f, .45f, ~0, QueryTriggerInteraction.Ignore);
                                }
                                if (clear) Add("theft", pole, rotation, flip);
                            }
                        }
                        if (oldRoad && spots.Count > spotsBefore && places.Count == placesBefore) spots.RemoveAt(spots.Count - 1);
                    }
                }
                if (includeOld) Debug.Log("HAZARD_OLD_ROADS added=" + (places.Count - beforeOld));
                SaveCopy(SourcePath, output, spots, places);
                Report(output, "roads", context, null);
            }
            if (!sourceBefore.SequenceEqual(File.ReadAllBytes(SourcePath))) throw new InvalidOperationException("Source asset bytes changed.");
        }

        public static void BakePedestrians()
        {
            string input = InputPath(), output = OutputPath();
            var data = Load(input); ValidatePrefix(data);
            using (var context = new Context())
            {
                var parts = context.scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Component>(true)).ToArray();
                var network = parts.OfType<PedestrianNetwork>().Single();
                ValidateNetwork(network);
                // Prevent running phase 2 against the old, local-only sidewalk graph.
                var covered = new HashSet<string>(); var zoneMap = MakeZoneMap();
                try
                {
                    for (int p = 0; p < network.PathCount; p++)
                        foreach (var point in network.Path(p).points)
                            if (OnNewRoad(context, point) && context.roads.Except(context.newRoads).All(r => Distance(point, r) > 12f))
                                covered.Add(zoneMap.ZoneAt(point) ?? "outside");
                }
                finally { UnityEngine.Object.DestroyImmediate(zoneMap); }
                if (new[] { "A", "B", "C", "D" }.Any(z => !covered.Contains(z)))
                    throw new InvalidOperationException("Bake/save the expanded PedestrianNetwork first (new-road sidewalks in all A/B/C/D required).");
                var session = parts.OfType<DeliverySession>().Single();
                var planner = new SerializedObject(parts.OfType<HazardPlanner>().Single());
                var ground = (RoadNetwork)planner.FindProperty("ground").objectReferenceValue;
                if (ground == null) throw new InvalidOperationException("Missing planner RoadNetwork.");
                var spots = data.Spots.Select(CloneSpot).ToList(); var places = data.Candidates.ToList();
                var extraSpots = new List<HazardCandidates.Spot>(); var extra = new List<HazardCandidate>();
                MusclemanInstaller.AppendCandidates(extraSpots, extra, session, network, ground, 6f, 6f);
                CelebrityCrowdInstaller.AppendCandidates(extraSpots, extra, session, network, ground);
                foreach (var candidate in extra)
                {
                    // A celebrity with no sidewalk within reach of FindCenter switches itself off at runtime: never add one.
                    if (candidate.type == "celebrity" && !CelebrityCrowdHazard.FindCenter(network, candidate.position, out _, out _)) continue;
                    bool onNew = OnNewRoad(context, candidate.position);
                    if (!onNew && !IncludeOldRoads()) continue;
                    float same = onNew ? .01f : SamePlace;
                    if (places.Any(c => c.type == candidate.type && (c.position - candidate.position).sqrMagnitude < same)) continue;
                    var c = candidate; c.spot = spots.Count; places.Add(c); spots.Add(CloneSpot(extraSpots[candidate.spot]));
                }
                if (!places.Skip(data.Candidates.Count).Any(c => c.type == "muscleman") || !places.Skip(data.Candidates.Count).Any(c => c.type == "celebrity"))
                    throw new InvalidOperationException("Expanded pedestrian bake produced no new muscleman or celebrity candidates; check sidewalks/corners.");
                // The frozen 887 stay as they are; report the ones that do nothing on this network.
                foreach (var c in places.Where(c => c.type == "celebrity" && !CelebrityCrowdHazard.FindCenter(network, c.position, out _, out _)))
                    Debug.Log($"HAZARD_CELEBRITY_OFF index={places.IndexOf(c)} position=({c.position.x:0.00},{c.position.z:0.00})");
                SaveCopy(input, output, spots, places);
                var networkJson = EditorJsonUtility.ToJson(network);
                Report(output, "pedestrians", context, HashBytes(Encoding.UTF8.GetBytes(networkJson)));
            }
        }

        public static void ImportRoutes()
        {
            string input = InputPath(), output = OutputPath(); var asset = Load(input);
            var linked = JsonUtility.FromJson<BakeData>(File.ReadAllText(Arg("-hazardJson")));
            if (linked == null || linked.candidates == null || linked.spots == null || linked.candidates.Length != asset.Candidates.Count || linked.spots.Length != asset.Spots.Count)
                throw new InvalidOperationException("Linked data dimensions changed.");
            for (int i = 0; i < linked.candidates.Length; i++)
                if (JsonUtility.ToJson(linked.candidates[i]) != JsonUtility.ToJson(asset.Candidates[i])) throw new InvalidOperationException("Linker changed candidate " + i);
            var routes = new HashSet<int>();
            for (int i = 0; i < linked.spots.Length; i++)
            {
                if (!linked.spots[i].position.Equals(asset.Spots[i].position)) throw new InvalidOperationException("Linker moved spot " + i);
                int previous = -1;
                foreach (int route in linked.spots[i].routes)
                {
                    if (route < 0 || route >= 384 || route <= previous) throw new InvalidOperationException("Invalid route list at spot " + i);
                    previous = route; routes.Add(route);
                }
            }
            if (routes.Count != 384) throw new InvalidOperationException("Expected all 384 routes, got " + routes.Count);
            SaveCopy(input, output, linked.spots.ToList(), linked.candidates.ToList());
            var routed = Load(output); routed.ConfigureRoutes(linked.routePaths); EditorUtility.SetDirty(routed); AssetDatabase.SaveAssetIfDirty(routed);
            Debug.Log("HAZARD_ROUTES verified=384 candidateFieldBytesAfterEolNormalization=true sourceSha256=" + Hash(SourcePath));
        }

        public static void ApplyHtmlRules()
        {
            var source = Load(InputPath()); ValidatePrefix(source);
            const string targetPath = "Assets/_Game/Settings/MyeongjiHazardCandidatesExpanded.asset";
            string guid = AssetDatabase.AssetPathToGUID(targetPath);
            var target = Load(targetPath);
            EditorUtility.CopySerialized(source, target);
            target.name = "MyeongjiHazardCandidatesExpanded";
            EditorUtility.SetDirty(target); AssetDatabase.SaveAssetIfDirty(target);
            if (AssetDatabase.AssetPathToGUID(targetPath) != guid) throw new InvalidOperationException("Candidate GUID changed");
            HtmlHazardSettingsInstaller.Apply();
            Debug.Log($"HTML_APPLY candidates={target.Candidates.Count} spots={target.Spots.Count} routes={target.RoutePaths.Count} guid={guid}; scene unchanged");
        }

        public static void RebakePlacementMetadata()
        {
            const string path="Assets/_Game/Settings/MyeongjiHazardCandidatesExpanded.asset";
            string guid=AssetDatabase.AssetPathToGUID(path),sourceHash=Hash(SourcePath);
            var asset=Load(path);ValidatePrefix(asset);
            string prefix=CandidateBlock(File.ReadAllText(path));
            var scene=EditorSceneManager.OpenScene("Assets/_Game/Scenes/FourDistrictPrototype_Myeongji.unity",OpenSceneMode.Single);
            var planner=UnityEngine.Object.FindFirstObjectByType<HazardPlanner>();
            var ground=(RoadNetwork)new SerializedObject(planner).FindProperty("ground").objectReferenceValue;
            var roads=ground.Roads;var sidewalks=UnityEngine.Object.FindFirstObjectByType<PedestrianNetwork>();
            var metadata=asset.Candidates.Select(c=>HazardCandidates.Classify(c,roads,sidewalks)).ToArray();
            asset.ConfigurePlacements(metadata);EditorUtility.SetDirty(asset);AssetDatabase.SaveAssetIfDirty(asset);
            ValidatePrefix(asset);
            if(guid!=AssetDatabase.AssetPathToGUID(path)||sourceHash!=Hash(SourcePath))throw new InvalidOperationException("GUID/source changed");
            // CandidateBlock now also reaches the appended metadata, so compare the original candidate block prefix.
            if(!CandidateBlock(File.ReadAllText(path)).StartsWith(prefix,StringComparison.Ordinal))throw new InvalidOperationException("Candidate records changed");
            Debug.Log($"REVIEW_METADATA_PASS placements={metadata.Length} invalid={metadata.Count(m=>!m.valid)} guid={guid} frozen887=true spots306=true sourceSha={sourceHash}");
        }

        private static void ValidateNetwork(PedestrianNetwork network)
        {
            if (network.PathCount == 0) throw new InvalidOperationException("No pedestrian paths.");
            for (int i = 0; i < network.PathCount; i++)
            {
                var path = network.Path(i);
                if (path.points == null || path.points.Length < 2 || path.cumulative.Length != path.points.Length || path.length <= 0
                    || path.reverse < 0 || path.reverse >= network.PathCount) throw new InvalidOperationException("Invalid pedestrian path " + i);
                if (!path.connector && network.Path(path.reverse).reverse != i) throw new InvalidOperationException("Invalid pedestrian reverse index " + i);
                foreach (int next in path.next)
                    if (next < 0 || next >= network.PathCount || Vector3.Distance(path.points[path.points.Length - 1], network.Path(next).points[0]) > .1f)
                        throw new InvalidOperationException("Invalid pedestrian next index " + i);
            }
        }

        private static void SaveCopy(string input, string output, List<HazardCandidates.Spot> spots, List<HazardCandidate> places)
        {
            if (places.Any(c => c.spot < 0 || c.spot >= spots.Count)) throw new InvalidOperationException("Invalid spot reference.");
            if (!AssetDatabase.CopyAsset(input, output)) throw new IOException("Cannot copy " + input + " to " + output);
            var copy = Load(output); copy.Configure(spots.ToArray(), places.ToArray());
            EditorUtility.SetDirty(copy); AssetDatabase.SaveAssetIfDirty(copy); ValidatePrefix(copy);
            // Verify Unity's actual serialized prefix, not a float-to-JSON approximation.
            string original = CandidateBlock(File.ReadAllText(SourcePath));
            string expanded = CandidateBlock(File.ReadAllText(output));
            if (!expanded.StartsWith(original, StringComparison.Ordinal)) throw new InvalidOperationException("Serialized 887-candidate field bytes differ (after EOL normalization).");
            Debug.Log("HAZARD_PREFIX candidateFieldBytesAfterEolNormalization=true originalSpots=true sourceSha256=" + Hash(SourcePath));
        }

        private static string CandidateBlock(string text, bool normalizeEol = true)
        {
            const string marker = "  candidates:";
            int start = text.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) throw new InvalidOperationException("Missing serialized candidates.");
            string block = text.Substring(start + marker.Length);
            return normalizeEol ? block.Replace("\r\n", "\n") : block;
        }

        private static void ValidatePrefix(HazardCandidates data)
        {
            var source = Source();
            if (data.Candidates.Count < OriginalCandidates || data.Spots.Count < OriginalSpots) throw new InvalidOperationException("Truncated original prefix.");
            for (int i = 0; i < OriginalCandidates; i++)
                if (!CandidateBytes(data.Candidates[i]).SequenceEqual(CandidateBytes(source.Candidates[i]))) throw new InvalidOperationException("Original candidate field bytes changed: " + i);
            for (int i = 0; i < OriginalSpots; i++)
                if (!data.Spots[i].position.Equals(source.Spots[i].position)) throw new InvalidOperationException("Original spot moved: " + i);
        }

        private static void Report(string output, string phase, Context context, string networkHash)
        {
            var data = Load(output); var zoneMap = MakeZoneMap();
            try
            {
                var counts = data.Candidates.GroupBy(c => zoneMap.ZoneAt(c.position) ?? "outside")
                    .ToDictionary(g => g.Key, g => g.GroupBy(c => c.type).ToDictionary(t => t.Key, t => t.Count()));
                int mesh = 0, geometry = 0, invalid = 0;
                foreach (var c in data.Candidates)
                {
                    int before = context.geometryChecks;
                    if (!context.Ground(c.position, out _)) invalid++;
                    else if (context.geometryChecks > before) geometry++; else mesh++;
                }
                if (invalid > 0) throw new InvalidOperationException("Candidates without floor/road geometry: " + invalid);
                WriteJson(Arg("-hazardJson"), new BakeData { spots = data.Spots.ToArray(), candidates = data.Candidates.ToArray() });
                WriteJson(Arg("-hazardReport"), new { phase, output, sourceSha256 = Hash(SourcePath),
                    originalCandidateBinaryFieldBytes = true, candidateFieldBytesAfterEolNormalization = true, rawDiskPrefixBytes = CandidateBlock(File.ReadAllText(output), false).StartsWith(CandidateBlock(File.ReadAllText(SourcePath), false), StringComparison.Ordinal),
                    serializationNote = "Source uses CRLF; Unity writes LF. Only line endings are normalized for prefix comparison. Source file bytes are never changed.", originalSpotPositions = true, spots = data.Spots.Count,
                    candidates = data.Candidates.Count, byZoneAndType = counts,
                    candidateGround = new { mesh, geometry, invalid },
                    groundQueries = new { context.meshChecks, context.geometryChecks, context.rejectedGround },
                    pedestrianNetworkSha256 = networkHash, allowGeometry = context.allowGeometry,
                    warning = geometry > 0 ? "Geometry-only locations require a final bake after expanded floor/buildings/sidewalks are installed." : null });
                Debug.Log("HAZARD_BAKE " + phase + " candidates=" + data.Candidates.Count + " spots=" + data.Spots.Count + " groundMesh=" + mesh + " groundGeometry=" + geometry);
            }
            finally { UnityEngine.Object.DestroyImmediate(zoneMap); }
        }

        private static ZoneMap MakeZoneMap()
        {
            var map = ScriptableObject.CreateInstance<ZoneMap>();
            var data = new SerializedObject(map); var array = data.FindProperty("zones");
            var zones = JObject.Parse(File.ReadAllText(DataPath + "zones.json"))["zones"].ToArray(); array.arraySize = zones.Length;
            for (int i = 0; i < zones.Length; i++)
            {
                var item = array.GetArrayElementAtIndex(i); item.FindPropertyRelative("id").stringValue = (string)zones[i]["id"];
                var polygon = item.FindPropertyRelative("polygon"); var points = zones[i]["polygon"].ToArray(); polygon.arraySize = points.Length;
                for (int j = 0; j < points.Length; j++) polygon.GetArrayElementAtIndex(j).vector2Value = new Vector2((float)points[j][0], (float)points[j][1]);
            }
            data.ApplyModifiedPropertiesWithoutUndo(); return map;
        }

        // Compare actual IEEE-754 field bits as well as Unity text; includes negative zero and all flags.
        private static byte[] CandidateBytes(HazardCandidate c)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(c.type ?? ""); writer.Write(c.spot);
                writer.Write(c.position.x); writer.Write(c.position.y); writer.Write(c.position.z);
                writer.Write(c.rotation.x); writer.Write(c.rotation.y); writer.Write(c.rotation.z); writer.Write(c.rotation.w);
                writer.Write(c.halfWidth); writer.Write(c.side); writer.Write(c.alley); writer.Write(c.flip);
                writer.Flush(); return stream.ToArray();
            }
        }

        private static HazardCandidates Source()
        {
            if (!Application.isBatchMode) throw new InvalidOperationException("Run hazard expansion entry points in batchmode.");
            var source = Load(SourcePath);
            if (source.Candidates.Count != OriginalCandidates || source.Spots.Count != OriginalSpots)
                throw new InvalidOperationException("Expected frozen source with 887 candidates / 306 spots.");
            return source;
        }
        private static HazardCandidates Load(string path) => AssetDatabase.LoadAssetAtPath<HazardCandidates>(path)
            ?? throw new InvalidOperationException("Missing HazardCandidates asset: " + path);
        private static HazardCandidates.Spot CloneSpot(HazardCandidates.Spot spot) => new HazardCandidates.Spot { position = spot.position, routes = spot.routes.ToArray() };
        private static string InputPath() { var p = Arg("-hazardInput"); if (p == SourcePath) throw new InvalidOperationException("Input must be an expansion copy."); return p; }
        private static string OutputPath()
        {
            string path = Arg("-hazardOutput");
            if (!path.StartsWith("Assets/", StringComparison.Ordinal) || !path.EndsWith(".asset", StringComparison.Ordinal)
                || path.Contains("..") || path.Contains('\\') || path.StartsWith("Assets/_Game/Settings/", StringComparison.OrdinalIgnoreCase)
                || File.Exists(path) || !Directory.Exists(Path.GetDirectoryName(path)))
                throw new InvalidOperationException("Use a new .asset in an existing Assets folder outside Settings (never overwrite an asset).");
            return path;
        }
        private static bool IncludeOldRoads()
        {
            string value = Arg("-hazardIncludeOldRoads", "false");
            if (value != "true" && value != "false") throw new ArgumentException("-hazardIncludeOldRoads must be true or false.");
            return value == "true";
        }
        private static bool OnNewRoad(Context c, Vector3 p) => c.newRoads.Any(r => Distance(p, r) <= (float)r["width"] * .5f + SidewalkReach)
            && !c.roads.Except(c.newRoads).Any(r => Distance(p, r) < (float)r["width"] * .5f);
        private static Vector3 P2(JToken p) => new Vector3((float)p[0], 0, (float)p[1]);
        private static Vector3 P3(JToken p) => new Vector3((float)p[0], (float)p[1], (float)p[2]);
        private static Vector3 Flat(Vector3 p) => new Vector3(p.x, 0, p.z);
        private static float Distance(Vector3 p, JToken road)
        {
            var a = P2(road["a"]); var delta = P2(road["b"]) - a;
            return (Flat(p) - a - delta * Mathf.Clamp01(Vector3.Dot(Flat(p) - a, delta) / Mathf.Max(.0001f, delta.sqrMagnitude))).magnitude;
        }
        private static string Arg(string name, string fallback = null)
        {
            var args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, name);
            if (i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal)) return args[i + 1];
            return fallback ?? throw new ArgumentException("Required argument: " + name);
        }
        private static void WriteJson(string path, object value)
        {
            string full = Path.GetFullPath(path); string root = Path.GetFullPath(".") + Path.DirectorySeparatorChar;
            // Reports/exports cannot overwrite any Unity asset, prefab, scene, settings or input layout.
            if (full.StartsWith(Path.Combine(root, "Assets") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(Path.Combine(root, "Docs") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(Path.Combine(root, "Temp") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Write exports/reports to Library or an external result folder.");
            Directory.CreateDirectory(Path.GetDirectoryName(full)); File.WriteAllText(full, value is BakeData bake ? JsonUtility.ToJson(bake, true) : JsonConvert.SerializeObject(value, Formatting.Indented));
        }
        private static string Hash(string path) => HashBytes(File.ReadAllBytes(path));
        private static string HashBytes(byte[] bytes) { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    }
}
