using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DeliveryRider.Editor
{
    // Batch checks for open-road placement; no scene or asset is touched.
    public static class HazardZoneChecks
    {
        public static void Run()
        {
            ZoneMap map = null;
            try
            {
                map = ScriptableObject.CreateInstance<ZoneMap>();
                var data = new SerializedObject(map);
                var list = data.FindProperty("zones");
                list.arraySize = 2;
                SetZone(list.GetArrayElementAtIndex(0), "C", 0f, 100f);
                SetZone(list.GetArrayElementAtIndex(1), "A", 100f, 200f);
                data.ApplyModifiedPropertiesWithoutUndo();

                var none = new HazardZoneFilter(null, _ => false);
                Check(none.PointOpen(new Vector3(150f, 0f, 50f)), "no map: everything open");
                Check(Mathf.Approximately(none.OpenRoadShare(null), 1f), "no map: share 1");

                var onlyC = new HazardZoneFilter(map, zone => zone == "C");
                float clearance = HazardZoneFilter.WallClearance;
                Check(clearance >= 0.3f + 0.5f, "clearance covers half a wall and the rider's radius");
                Check(onlyC.PointOpen(new Vector3(50f, 0f, 50f)), "inside an open zone");
                Check(!onlyC.PointOpen(new Vector3(150f, 0f, 50f)), "inside a closed zone");
                Check(!onlyC.PointOpen(new Vector3(100f, 0f, 50f)), "on a border with a closed side, though ZoneAt reports the open zone");
                Check(map.ZoneAt(new Vector3(100f, 0f, 50f)) == "C", "that border point really is reported as the open zone");
                Check(!onlyC.PointOpen(new Vector3(100f + 0.01f, 0f, 50f)), "just past the wall");
                Check(!onlyC.PointOpen(new Vector3(103f, 0f, 50f)), "a few metres past the wall, where a side probe would see the open zone");
                Check(!onlyC.PointOpen(new Vector3(100f - clearance + 0.01f, 0f, 50f)), "open side but touching the wall");
                Check(onlyC.PointOpen(new Vector3(100f - clearance - 0.01f, 0f, 50f)), "open side clear of the wall");
                Check(new HazardZoneFilter(map, _ => true).PointOpen(new Vector3(100f, 0f, 50f)), "border of two open zones has no wall");
                Check(new HazardZoneFilter(map, zone => zone == "A").PointOpen(new Vector3(103f, 0f, 50f)), "same point when only the far side is open");
                Check(!new HazardZoneFilter(map, zone => zone == "A").PointOpen(new Vector3(100f, 0f, 50f)), "border point is closed from either side");
                Check(onlyC.PointOpen(new Vector3(500f, 0f, 500f)), "outside every zone");

                float open = 0f, total = 0f;
                onlyC.AddRoad(ref open, ref total, 200f, t => new Vector3(200f * t, 0f, 50f));
                Check(Mathf.Abs(open / total - 0.5f) < 0.02f, $"half an alley line is open, got {open / total:0.000}");

                // A pole beside a road running +x at z=50; forward along the road, straight at the closed zone at x=100.
                var pole = new HazardCandidate { type = "theft", position = new Vector3(40f, 0f, 50f), rotation = Quaternion.LookRotation(Vector3.right), halfWidth = 3f, side = 1 };
                Check(Vector3.Distance(PigeonSnatchHazard.EscapePoint(pole, 0), new Vector3(44f, 0f, 54f)) < 0.001f, "escape route starts on the road ahead of the pole");
                Check(Vector3.Distance(PigeonSnatchHazard.EscapePoint(pole, 15), new Vector3(89f, 0f, 54f)) < 0.001f, "escape route is 16 points, 3 m apart");
                Check(PigeonSnatchHazard.OpenEscapePoints(pole, onlyC, 16) == 16, "a route that stays in the open zone is kept whole");
                pole.position = new Vector3(70f, 0f, 50f);
                int kept = PigeonSnatchHazard.OpenEscapePoints(pole, onlyC, 16);
                Check(kept > 0 && kept < 16 && onlyC.PointOpen(PigeonSnatchHazard.EscapePoint(pole, kept - 1)) && !onlyC.PointOpen(PigeonSnatchHazard.EscapePoint(pole, kept)), $"a route is cut right before its first closed point ({kept} points)");
                pole.flip = true;
                Check(PigeonSnatchHazard.OpenEscapePoints(pole, onlyC, 16) == 16, "the same pole flying the other way stays whole");
                pole.flip = false;
                Check(PigeonSnatchHazard.OpenEscapePoints(pole, none, 16) == 16, "no map: the route is never cut");

                Check(HazardZoneFilter.ScaleCount(6, 1f) == 6, "whole map keeps the count");
                Check(HazardZoneFilter.ScaleCount(6, 0.25f) == 2, "6 x 0.25 rounds to 2");
                Check(HazardZoneFilter.ScaleCount(1, 0.25f) == 0, "1 x 0.25 rounds to 0");
                Check(HazardZoneFilter.ScaleCount(9, 0.5f) == 5, "9 x 0.5 rounds up to 5");

                Check(DevLabels.Find("HazardPlanner", "triggersRequireFood") != null, "new toggle has a Korean name");

                var centre = new Vector3(50f, 0f, 50f);
                var closedCentre = new Vector3(150f, 0f, 50f);
                Check(!none.LandingClosed(closedCentre, true), "no map: no landing is closed");
                Check(!onlyC.LandingClosed(centre, true), "landing on an open road");
                Check(onlyC.LandingClosed(closedCentre, true), "landing on a closed road");
                Check(onlyC.LandingClosed(new Vector3(103f, 0f, 50f), true), "landing on a road just past the wall");
                Check(onlyC.LandingClosed(new Vector3(100f, 0f, 50f), true), "landing on the border itself");
                Check(!onlyC.LandingClosed(new Vector3(100f - 0.8f, 0f, 50f), true), "standing against the wall on the open side is not a closed landing");
                Check(HazardZoneFilter.OnWallReach >= 0.3f && HazardZoneFilter.OnWallReach < 0.8f, "on-wall reach covers the wall's top and not a rider beside it");
                Check(onlyC.LandingClosed(new Vector3(100f - 0.29f, 0f, 50f), true), "standing on top of the wall, open half");
                Check(onlyC.LandingClosed(new Vector3(100f + 0.29f, 0f, 50f), true), "standing on top of the wall, closed half");
                Check(!new HazardZoneFilter(map, _ => true).LandingClosed(new Vector3(100f, 0f, 50f), true), "no wall between two open zones");
                Check(!onlyC.LandingClosed(centre, false), "off road inside an open zone");
                Check(onlyC.LandingClosed(closedCentre, false), "off road inside a closed zone");
                Check(onlyC.LandingClosed(new Vector3(500f, 0f, 500f), false), "off road outside every zone");
                Check(!onlyC.LandingClosed(new Vector3(500f, 0f, 500f), true), "road outside every zone");

                Check(Mathf.Approximately(RiderMotor.ZoneHopDuration(2f, 13f, 0.55f, 1.5f), 0.55f), "short hop uses the shortest time");
                Check(Mathf.Approximately(RiderMotor.ZoneHopDuration(13f, 13f, 0.55f, 1.5f), 1f), "hop time follows distance");
                Check(Mathf.Approximately(RiderMotor.ZoneHopDuration(100f, 13f, 0.55f, 1.5f), 1.5f), "long hop uses the longest time");
                var hopFrom = new Vector3(0f, 2f, 0f);
                var hopTo = new Vector3(10f, 0f, 0f);
                Check(RiderMotor.ZoneHopPoint(hopFrom, hopTo, 0f, 3f) == hopFrom, "hop starts where the rider lies");
                Check(Vector3.Distance(RiderMotor.ZoneHopPoint(hopFrom, hopTo, 1f, 3f), hopTo) < 0.0001f, "hop ends on the road");
                Check(Vector3.Distance(RiderMotor.ZoneHopPoint(hopFrom, hopTo, 0.5f, 3f), new Vector3(5f, 4f, 0f)) < 0.0001f, "hop peaks halfway");
                CheckBarrierMasks();
                Debug.Log("HazardZoneChecks PASS: all checks.");
                EditorApplication.Exit(0);
            }
            catch (Exception failure)
            {
                Debug.LogError("HazardZoneChecks FAIL: " + failure.Message);
                EditorApplication.Exit(1);
            }
            finally
            {
                if (map != null) UnityEngine.Object.DestroyImmediate(map);
            }
        }

        private static void CheckBarrierMasks()
        {
            int layer = LayerMask.NameToLayer(HazardPhysics.BarrierLayerName);
            Check(layer >= 0, "barrier layer exists");
            int bit = 1 << layer;
            Check((HazardPhysics.Everything & bit) == 0, "hazard queries leave out the barrier layer");
            Check((HazardPhysics.Everything | bit) == ~0, "hazard queries keep every other layer");
            Check((PrefabMask("Assets/_Game/Prefabs/Hazards/RollingTire.prefab", typeof(RollingTireHazard), "collisionMask") & bit) == 0, "tire ignores barriers");
            Check((PrefabMask("Assets/_Game/Prefabs/Traffic/Pedestrian.prefab", typeof(TrafficPedestrian), "obstacleMask") & bit) == 0, "walkers ignore barriers");

            // A real wall on the barrier layer: seen by a plain query, not by the hazard checks.
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            try
            {
                wall.layer = layer;
                wall.transform.position = new Vector3(5000f, 1f, 5000f);
                wall.transform.localScale = new Vector3(1f, 8f, 20f);
                Physics.SyncTransforms();
                var inside = new Vector3(5000f, 0f, 5000f);
                Check(Physics.CheckCapsule(inside + Vector3.up * 0.55f, inside + Vector3.up * 1.45f, 0.3f, ~0, QueryTriggerInteraction.Ignore), "plain query sees the test wall");
                Check(CelebrityCrowdHazard.Clear(inside), "fan spawn check ignores the wall");
                Check(CelebrityCrowdHazard.HasDirectApproach(new Vector3(4990f, 0f, 5000f), new Vector3(5010f, 0f, 5000f)), "fan path check ignores the wall");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(wall);
                Physics.SyncTransforms();
            }

            var sources = new System.Collections.Generic.List<string>(Directory.GetFiles("Assets/_Game/Scripts/Events", "*.cs", SearchOption.TopDirectoryOnly));
            sources.Add("Assets/_Game/Scripts/NPC/PedestrianNetwork.cs");
            sources.Add("Assets/_Game/Scripts/Player/RiderSafeRecovery.cs");
            foreach (var source in sources)
                Check(!File.ReadAllText(source).Contains(", ~0,"), "no everything-mask query in " + Path.GetFileName(source));
        }

        // FindReturn with its shortcuts against the plain scan it replaced: same answer from the same points, on the real network.
        public static void RunWalkerReturn()
        {
            try
            {
                var scene = EditorSceneManager.OpenScene("Assets/_Game/Scenes/FourDistrictPrototype_Myeongji.unity", OpenSceneMode.Single);
                var network = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<PedestrianNetwork>(true)).Single();
                var walker = UnityEngine.Object.Instantiate(network.Prefab, new Vector3(9000f, 0f, 9000f), Quaternion.identity);
                Physics.SyncTransforms();
                int total = 0;
                for (int p = 0; p < network.PathCount; p++) if (!network.Path(p).connector) total += network.Path(p).points.Length - 1;
                const float laneOffset = 0.35f;
                bool Plain(Vector3 from, out int pathIndex, out float distance, out Vector3 feet)
                {
                    pathIndex = -1; distance = 0f; feet = from;
                    float best = float.PositiveInfinity;
                    for (int p = 0; p < network.PathCount; p++)
                    {
                        var path = network.Path(p);
                        if (path.connector) continue;
                        for (int i = 0; i < path.points.Length - 1; i++)
                        {
                            var a = path.points[i];
                            var ab = path.points[i + 1] - a;
                            float t = Mathf.Clamp01(Vector3.Dot(from - a, ab) / Mathf.Max(0.001f, ab.sqrMagnitude));
                            float along = Mathf.Lerp(path.cumulative[i], path.cumulative[i + 1], t);
                            network.Sample(p, along, out var position, out var direction);
                            position += Vector3.Cross(Vector3.up, direction) * Mathf.Min(laneOffset, path.halfWidth - 0.35f);
                            float squared = (position - from).sqrMagnitude;
                            if (squared >= best || !network.Free(position, walker) || !walker.HasRoom(position)) continue;
                            best = squared; pathIndex = p; distance = along; feet = position;
                        }
                    }
                    return pathIndex >= 0;
                }
                var random = new System.Random(4153);
                int same = 0, runs = 1500;
                long checkedSum = 0, sampledSum = 0, plainTicks = 0, fastTicks = 0;
                int checkedMost = 0, sampledMost = 0;
                var watch = new System.Diagnostics.Stopwatch();
                for (int n = 0; n < runs; n++)
                {
                    var from = new Vector3(-100f + (float)random.NextDouble() * 310f, (float)random.NextDouble() * 3f, -160f + (float)random.NextDouble() * 255f);
                    watch.Restart();
                    bool plain = Plain(from, out int plainPath, out float plainAlong, out var plainFeet);
                    plainTicks += watch.ElapsedTicks;
                    watch.Restart();
                    bool fast = network.FindReturn(from, walker, out int fastPath, out float fastAlong, out var fastFeet);
                    fastTicks += watch.ElapsedTicks;
                    if (plain != fast || plainPath != fastPath || plainAlong != fastAlong || plainFeet != fastFeet)
                        throw new InvalidOperationException($"from {from}: plain {plainPath}/{plainAlong}/{plainFeet}, fast {fastPath}/{fastAlong}/{fastFeet}");
                    same++;
                    checkedSum += network.ReturnSegmentsChecked; sampledSum += network.ReturnSpotsSampled;
                    checkedMost = Mathf.Max(checkedMost, network.ReturnSegmentsChecked); sampledMost = Mathf.Max(sampledMost, network.ReturnSpotsSampled);
                }
                double ms = 1000d / System.Diagnostics.Stopwatch.Frequency / runs;
                Debug.Log($"HazardZoneChecks ok: FindReturn same as the plain scan at {same}/{runs} points; lane segments {total}; per call segments looked at {checkedSum / runs} (most {checkedMost}), spots sampled {sampledSum / runs} (most {sampledMost}); plain {plainTicks * ms:0.00} ms, now {fastTicks * ms:0.00} ms per call");
                Debug.Log("HazardZoneChecks PASS: walker return.");
                EditorApplication.Exit(0);
            }
            catch (Exception failure)
            {
                Debug.LogError("HazardZoneChecks FAIL: " + failure.Message);
                EditorApplication.Exit(1);
            }
        }

        // The tuning copy must not share a save folder with the team project.
        public static void PrintDataPath()
        {
            string path = Application.persistentDataPath, team = "CapstoneGameEngine_Team_3_BaeminRider";
            Debug.Log($"HazardZoneChecks DATAPATH product={PlayerSettings.productName} company={PlayerSettings.companyName} path={path} saveExists={File.Exists(Path.Combine(path, "save.json"))}");
            string teamFolder = Path.Combine(Path.GetDirectoryName(path), team);
            Debug.Log($"HazardZoneChecks TEAMPATH path={teamFolder} folderExists={Directory.Exists(teamFolder)} saveExists={File.Exists(Path.Combine(teamFolder, "save.json"))}");
            bool separate = !string.Equals(Path.GetFileName(path), team, StringComparison.OrdinalIgnoreCase);
            Debug.Log("HazardZoneChecks " + (separate ? "PASS" : "FAIL") + ": data path is separate from the team project.");
            EditorApplication.Exit(separate ? 0 : 1);
        }

        // On the real scene, day by day with the same walls ZoneBarrier would switch on: recovery and dropped food never
        // pick a point inside or behind a closed wall, and each wall really stands between the zones it separates.
        public static void RunBarrierAudit()
        {
            try
            {
                var scene = EditorSceneManager.OpenScene("Assets/_Game/Scenes/FourDistrictPrototype_Myeongji.unity", OpenSceneMode.Single);
                var roots = scene.GetRootGameObjects();
                var player = roots.Single(g => g.name == "Player");
                var recovery = player.GetComponent<RiderSafeRecovery>();
                var capsule = player.GetComponent<CapsuleCollider>();
                var session = roots.Single(g => g.name == "DeliverySession").GetComponent<DeliverySession>();
                var map = new SerializedObject(session).FindProperty("zoneMap").objectReferenceValue as ZoneMap;
                if (map == null) throw new InvalidOperationException("scene has no zone map");
                var barriers = roots.Single(g => g.name == "ZoneBarriers").GetComponentsInChildren<ZoneBarrier>(true);
                Physics.SyncTransforms();
                int layerMask = 1 << LayerMask.NameToLayer(HazardPhysics.BarrierLayerName);
                var required = barriers.ToDictionary(b => b, b => Enumerable.Range(0, new SerializedObject(b).FindProperty("requiredZones").arraySize)
                    .Select(i => new SerializedObject(b).FindProperty("requiredZones").GetArrayElementAtIndex(i).stringValue).ToArray());

                // Each wall has exactly its two zones on its two sides, and each barricade sits on such a border.
                int wallSamples = 0, walls = 0, barricades = 0;
                var sources = new List<Vector3>();
                var onTop = new List<(Vector3 point, ZoneBarrier barrier)>();
                foreach (var barrier in barriers)
                {
                    foreach (var wall in barrier.GetComponentsInChildren<BoxCollider>(true))
                    {
                        walls++;
                        float half = wall.size.z * 0.5f;
                        for (float d = -half + 1.5f; d <= half - 1.5f; d += 2f)
                        {
                            var at = wall.transform.position + wall.transform.forward * d;
                            var side = wall.transform.right * (wall.size.x * 0.5f + 0.05f);
                            var pair = new[] { map.ZoneAt(at + side), map.ZoneAt(at - side) };
                            if (!pair.OrderBy(z => z).SequenceEqual(required[barrier].OrderBy(z => z)))
                                throw new InvalidOperationException($"wall {wall.name} at {at} has {string.Join("/", pair)} on its sides, expected {string.Join("/", required[barrier])}");
                            wallSamples++;
                            sources.Add(at);
                            foreach (float out_ in new[] { 2f, 6f, 12f }) { sources.Add(at + wall.transform.right * out_); sources.Add(at - wall.transform.right * out_); }
                        }
                        // Wall ends, where a jump can round the wall, and its top, where a launch can land.
                        foreach (float end in new[] { -half, half })
                        {
                            var tip = wall.transform.position + wall.transform.forward * end;
                            foreach (float past in new[] { 0f, 1f, 2.6f, 5.2f })
                                foreach (float across in new[] { -5.2f, -2.6f, -0.9f, 0f, 0.9f, 2.6f, 5.2f })
                                    sources.Add(tip + wall.transform.forward * (Mathf.Sign(end) * past) + wall.transform.right * across);
                        }
                        for (float d = -half; d <= half; d += 2f)
                            foreach (float across in new[] { -0.29f, 0f, 0.29f })
                                onTop.Add((wall.transform.position + wall.transform.forward * d + wall.transform.right * across, barrier));
                    }
                    foreach (Transform child in barrier.transform)
                    {
                        if (!child.name.StartsWith("Barricade_")) continue;
                        barricades++;
                        var side = child.forward * 0.4f;
                        var pair = new[] { map.ZoneAt(child.position + side), map.ZoneAt(child.position - side) };
                        if (!pair.OrderBy(z => z).SequenceEqual(required[barrier].OrderBy(z => z)))
                            throw new InvalidOperationException($"barricade {child.name} at {child.position} has {string.Join("/", pair)}, expected {string.Join("/", required[barrier])}");
                        sources.Add(child.position);
                    }
                }
                Check(walls == 8 && barricades == 15, $"found {walls} walls and {barricades} barricades");
                Debug.Log($"HazardZoneChecks ok: {wallSamples} wall samples and {barricades} barricades have the zones they separate on their two sides");

                for (float x = -100f; x <= 210f; x += 5f)
                    for (float z = -160f; z <= 95f; z += 5f) sources.Add(new Vector3(x, 0f, z));
                sources.Add(new Vector3(-84f, 0f, -44.19f));
                // Ballistic landing locations for 1/2/4 launch scales, including well beyond the map.
                foreach (float scale in new[] { 1f, 2f, 4f })
                    foreach (var direction in new[] { Vector3.forward, Vector3.back, Vector3.left, Vector3.right })
                    {
                        float flightSeconds = 2f * (14f * scale) / Mathf.Max(0.01f, -Physics.gravity.y);
                        sources.Add(player.transform.position + direction * (24f * scale * flightSeconds));
                    }

                var far = new Vector3(4000f, 0f, 4000f);
                float radius = capsule.radius * 0.98f, reach = Mathf.Max(0f, capsule.height * 0.5f - capsule.radius);
                bool Stuck(Vector3 body)
                {
                    var middle = body + player.transform.TransformVector(capsule.center);
                    return Physics.CheckCapsule(middle - Vector3.up * reach, middle + Vector3.up * reach, radius, layerMask, QueryTriggerInteraction.Ignore);
                }
                var days = new[] { new[] { "C" }, new[] { "C", "A" }, new[] { "C", "A", "B" }, new[] { "C", "A", "B", "D" } };
                for (int d = 0; d < days.Length; d++)
                {
                    var open = days[d];
                    foreach (var barrier in barriers)
                    {
                        bool closed = required[barrier].Any(z => !open.Contains(z));
                        foreach (var wall in barrier.GetComponentsInChildren<Collider>(true)) wall.enabled = closed;
                    }
                    Physics.SyncTransforms();
                    var filter = new HazardZoneFilter(map, z => open.Contains(z));
                    int picks = 0, hops = 0;
                    void Verify(string what, Vector3 from, Vector3 picked)
                    {
                        picks++;
                        string zone = map.ZoneAt(picked);
                        if (picked == far) throw new InvalidOperationException($"day {d + 1}: {what} from {from} found nothing");
                        if (zone == null || !open.Contains(zone)) throw new InvalidOperationException($"day {d + 1}: {what} from {from} chose {picked} in zone {zone}");
                        if (!filter.PointOpen(picked)) throw new InvalidOperationException($"day {d + 1}: {what} from {from} chose {picked} against a closed wall");
                        if (Stuck(picked)) throw new InvalidOperationException($"day {d + 1}: {what} from {from} chose {picked}, where the rider overlaps a wall");
                        if (!recovery.IsSafe(picked)) throw new InvalidOperationException($"day {d + 1}: {what} from {from} chose {picked}, which is not a safe place");
                    }
                    foreach (var from in sources)
                    {
                        Verify("restore", from, recovery.FindSafePosition(from, far, filter));
                        Verify("food", from, HazardPlanner.FoodRestingPoint(recovery, filter, from, far));
                        if (recovery.LandedClosed(from, filter))
                        {
                            hops++;
                            if (!recovery.TryFindOpenRoad(from, filter, out var hop)) throw new InvalidOperationException($"day {d + 1}: no open road from {from}");
                            Verify("hop", from, hop);
                            if (recovery.LandedClosed(hop, filter)) throw new InvalidOperationException($"day {d + 1}: hop from {from} ends at {hop}, again a closed landing");
                        }
                    }
                    // A rider standing on top of a wall that is up this day is a closed landing and hops to an open road.
                    int topChecks = 0;
                    foreach (var (point, barrier) in onTop)
                    {
                        if (required[barrier].All(z => open.Contains(z))) continue;
                        topChecks++;
                        if (!recovery.LandedClosed(point, filter)) throw new InvalidOperationException($"day {d + 1}: standing on the wall at {point} is not a closed landing");
                        if (!recovery.TryFindOpenRoad(point, filter, out var off)) throw new InvalidOperationException($"day {d + 1}: no open road from the wall at {point}");
                        Verify("hop from wall top", point, off);
                    }
                    Debug.Log($"HazardZoneChecks ok: day {d + 1}: {topChecks} points on top of closed walls all count as closed landings");
                    Debug.Log($"HazardZoneChecks ok: day {d + 1} open {string.Concat(open)}: {sources.Count} sources, {picks} picked points, {hops} closed landings, none in or behind a wall");
                }

                // A pigeon on an open pole never leads the rider past a wall, and keeps a long enough flight.
                var theftAsset = (HazardCandidates)new SerializedObject(roots.SelectMany(g => g.GetComponentsInChildren<HazardPlanner>(true)).Single()).FindProperty("candidates").objectReferenceValue;
                for (int d = 0; d < days.Length; d++)
                {
                    var open = days[d];
                    var filter = new HazardZoneFilter(map, z => open.Contains(z));
                    int used = 0, dropped = 0, shortened = 0;
                    foreach (var candidate in theftAsset.Candidates)
                    {
                        if (candidate.type != "theft" || !filter.PointOpen(candidate.position) || !filter.PointOpen(theftAsset.Spots[candidate.spot].position)) continue;
                        int kept = PigeonSnatchHazard.OpenEscapePoints(candidate, filter, 16);
                        if (kept < PigeonSnatchHazard.MinimumEscapePoints) { dropped++; continue; }
                        used++;
                        if (kept < 16) shortened++;
                        for (int i = 0; i < kept; i++)
                            if (!open.Contains(map.ZoneAt(PigeonSnatchHazard.EscapePoint(candidate, i)))) throw new InvalidOperationException($"day {d + 1}: pigeon route from {candidate.position} enters a closed zone at point {i}");
                    }
                    Debug.Log($"HazardZoneChecks ok: day {d + 1}: pigeon places {used} used ({shortened} with a shortened flight), {dropped} dropped as too short");
                }

                // Startup cost of the planner's zone work on the real candidates, per day. Instantiating hazards is not included.
                var plannerData = new SerializedObject(roots.SelectMany(g => g.GetComponentsInChildren<HazardPlanner>(true)).Single());
                var stored = (HazardCandidates)plannerData.FindProperty("candidates").objectReferenceValue;
                var settings = (HazardSpawnSettings)plannerData.FindProperty("settings").objectReferenceValue;
                var roadNetwork = (RoadNetwork)plannerData.FindProperty("ground").objectReferenceValue;
                for (int d = 0; d < days.Length; d++)
                {
                    var open = days[d];
                    var filter = new HazardZoneFilter(map, z => open.Contains(z));
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    float share = filter.OpenRoadShare(roadNetwork.Roads);
                    long shareMs = watch.ElapsedMilliseconds;
                    int compared = 0, kept = 0;
                    foreach (var kind in settings.Kinds)
                        foreach (var candidate in stored.Candidates)
                        {
                            compared++;
                            if (candidate.type == kind.type && filter.PointOpen(candidate.position) && filter.PointOpen(stored.Spots[candidate.spot].position)) kept++;
                        }
                    Debug.Log($"HazardZoneChecks ok: day {d + 1} startup filter {watch.ElapsedMilliseconds} ms (road share {shareMs} ms, share {share:0.000}), {stored.Candidates.Count} candidates, {compared} type comparisons, {kept} open");
                }

                // The checks must be able to fail: a point on a closed wall, and the old defect, are caught.
                var dayOne = new HazardZoneFilter(map, z => z == "C");
                foreach (var barrier in barriers)
                {
                    bool closed = required[barrier].Any(z => z != "C");
                    foreach (var wall in barrier.GetComponentsInChildren<Collider>(true)) wall.enabled = closed;
                }
                Physics.SyncTransforms();
                var onWall = barriers.Where(b => required[b].Contains("A") && required[b].Contains("C")).SelectMany(b => b.GetComponentsInChildren<BoxCollider>(true)).First().transform.position;
                onWall.y = 0.2f;
                Check(Stuck(onWall), "a rider placed on a closed wall counts as stuck");
                Check(!dayOne.PointOpen(onWall), "a point on a closed wall is not open");
                Check(!dayOne.PointOpen(new Vector3(-84f, 0f, -44.19f)), "day 1: the old recovery point behind the A wall is closed");
                Check(recovery.LandedClosed(new Vector3(-84f, 0f, -44.19f), dayOne), "day 1: resting at the old recovery point is a closed landing");
                Check(!recovery.LandedClosed(player.transform.position, dayOne) && dayOne.PointOpen(player.transform.position), "day 1: the rider's start is open");
                CheckStandingReturn(player, session, recovery, new Vector3(-30f, 0f, 0f));
                Debug.Log("HazardZoneChecks PASS: barrier audit.");
                EditorApplication.Exit(0);
            }
            catch (Exception failure)
            {
                Debug.LogError("HazardZoneChecks FAIL: " + failure.Message);
                EditorApplication.Exit(1);
            }
        }

        // The real motor, not just the rule: standing in a closed zone starts a hop, in the air it does not, and food stays.
        private static void CheckStandingReturn(GameObject player, DeliverySession session, RiderSafeRecovery recovery, Vector3 closed)
        {
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var motor = player.GetComponent<RiderMotor>();
            void Set(string field, object value) => typeof(RiderMotor).GetField(field, any).SetValue(motor, value);
            object Get(string field) => typeof(RiderMotor).GetField(field, any).GetValue(motor);
            object Call(string method) => typeof(RiderMotor).GetMethod(method, any).Invoke(motor, null);
            typeof(DeliverySession).GetField("openZones", any).SetValue(session, new List<string> { "C" });
            typeof(DeliverySession).GetProperty("Stage").SetValue(session, DeliveryStage.Carrying);
            typeof(RiderSafeRecovery).GetMethod("Awake", any).Invoke(recovery, null);
            Call("Awake");
            Set("hazardHeightScale", 4f);
            Set("hazardDistanceScale", 4f);
            Check(motor.TryHazardCrash(session, new Vector3(24f, 14f, 24f), 1f), "maximum-scale hazard launch accepted before landing checks");
            Check(motor.ContactVelocity == new Vector3(96f, 56f, 96f), "maximum-scale launch reaches the real motor");
            var body = player.GetComponent<Rigidbody>();
            player.transform.position = closed;
            body.position = closed;
            Physics.SyncTransforms();
            Check(!(bool)typeof(RiderMotor).GetProperty("SpawnAllowedHere", any).GetValue(motor), "a closed-zone position is never saved as the recovery spot");

            Set("groundedForHazards", false);
            Set("nextZoneCheckAt", 0f);
            Check(!(bool)Call("TryReturnFromClosedZone") && !(bool)Get("zoneHopping"), "crossing a closed zone in the air starts no hop");

            Set("groundedForHazards", true);
            Set("nextZoneCheckAt", 0f);
            Check((bool)Call("TryReturnFromClosedZone") && (bool)Get("zoneHopping"), "standing in a closed zone starts a hop");
            var target = (Vector3)Get("zoneHopTo");
            Check(session.ZoneMap.ZoneAt(target) == "C", "the hop goes to the open zone");
            Check(session.Stage == DeliveryStage.Carrying, "the hop leaves the food alone");

            Set("nextZoneCheckAt", Time.time + 1f);
            Call("CancelZoneHop");
            Check(!(bool)Call("TryReturnFromClosedZone"), "between checks nothing runs");
            Set("nextZoneCheckAt", 0f);
            typeof(DeliverySession).GetField("openZones", any).SetValue(session, new List<string>());
            motor.GetType().GetField("openRoads", any).SetValue(motor, null);
            Check(!(bool)Call("TryReturnFromClosedZone") && (float)Get("nextZoneCheckAt") > Time.time, "no open road: no hop, retried at the next check");
        }

        private static int PrefabMask(string path, Type component, string field)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var part = prefab != null ? prefab.GetComponentInChildren(component, true) : null;
            if (part == null) throw new InvalidOperationException("missing " + component.Name + " in " + path);
            return new SerializedObject(part).FindProperty(field).intValue;
        }

        private static void SetZone(SerializedProperty zone, string id, float minimumX, float maximumX)
        {
            zone.FindPropertyRelative("id").stringValue = id;
            var polygon = zone.FindPropertyRelative("polygon");
            polygon.arraySize = 4;
            polygon.GetArrayElementAtIndex(0).vector2Value = new Vector2(minimumX, 0f);
            polygon.GetArrayElementAtIndex(1).vector2Value = new Vector2(maximumX, 0f);
            polygon.GetArrayElementAtIndex(2).vector2Value = new Vector2(maximumX, 100f);
            polygon.GetArrayElementAtIndex(3).vector2Value = new Vector2(minimumX, 100f);
        }

        private static void Check(bool passed, string name)
        {
            if (!passed) throw new InvalidOperationException(name);
            Debug.Log("HazardZoneChecks ok: " + name);
        }
    }
}
