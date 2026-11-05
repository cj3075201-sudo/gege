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
                Check(!onlyC.LandingClosed(new Vector3(99.5f, 0f, 50f), true), "resting against the wall on the open side is not a closed landing");
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
                    Debug.Log($"HazardZoneChecks ok: day {d + 1} open {string.Concat(open)}: {sources.Count} sources, {picks} picked points, {hops} closed landings, none in or behind a wall");
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
                Debug.Log("HazardZoneChecks PASS: barrier audit.");
                EditorApplication.Exit(0);
            }
            catch (Exception failure)
            {
                Debug.LogError("HazardZoneChecks FAIL: " + failure.Message);
                EditorApplication.Exit(1);
            }
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
