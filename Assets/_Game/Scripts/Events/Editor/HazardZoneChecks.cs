using System;
using System.IO;
using UnityEditor;
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
                Check(none.RoadOpen(new Vector3(150f, 0f, 50f), 4f, false), "no map: everything open");
                Check(Mathf.Approximately(none.OpenRoadShare(null), 1f), "no map: share 1");

                var onlyC = new HazardZoneFilter(map, zone => zone == "C");
                Check(onlyC.RoadOpen(new Vector3(50f, 0f, 50f), 4f, false), "main road inside open zone");
                Check(!onlyC.RoadOpen(new Vector3(150f, 0f, 50f), 4f, false), "main road inside closed zone");
                Check(onlyC.RoadOpen(new Vector3(100f, 0f, 50f), 4f, false), "main road on the border is open");
                Check(onlyC.RoadOpen(new Vector3(50f, 0f, 50f), 2.25f, true), "alley in open zone");
                Check(!onlyC.RoadOpen(new Vector3(105f, 0f, 50f), 2.25f, true), "alley in closed zone beside the border");
                Check(onlyC.RoadOpen(new Vector3(500f, 0f, 500f), 4f, false), "main road outside every zone");
                Check(onlyC.RoadOpen(new Vector3(500f, 0f, 500f), 2.25f, true), "alley outside every zone");

                float open = 0f, total = 0f;
                onlyC.AddRoad(ref open, ref total, 200f, t => new Vector3(200f * t, 0f, 50f), 2.25f, true);
                Check(Mathf.Abs(open / total - 0.5f) < 0.02f, $"half an alley line is open, got {open / total:0.000}");

                Check(HazardZoneFilter.ScaleCount(6, 1f) == 6, "whole map keeps the count");
                Check(HazardZoneFilter.ScaleCount(6, 0.25f) == 2, "6 x 0.25 rounds to 2");
                Check(HazardZoneFilter.ScaleCount(1, 0.25f) == 0, "1 x 0.25 rounds to 0");
                Check(HazardZoneFilter.ScaleCount(9, 0.5f) == 5, "9 x 0.5 rounds up to 5");

                Check(DevLabels.Find("HazardPlanner", "triggersRequireFood") != null, "new toggle has a Korean name");

                var centre = new Vector3(50f, 0f, 50f);
                var closedCentre = new Vector3(150f, 0f, 50f);
                Check(!none.LandingClosed(closedCentre, true, closedCentre, 4f, false), "no map: no landing is closed");
                Check(!onlyC.LandingClosed(centre, true, centre, 4f, false), "landing on an open road");
                Check(onlyC.LandingClosed(closedCentre, true, closedCentre, 4f, false), "landing on a closed road");
                Check(!onlyC.LandingClosed(new Vector3(103f, 0f, 50f), true, new Vector3(100f, 0f, 50f), 4f, false), "closed side of an open border road");
                Check(onlyC.LandingClosed(new Vector3(105f, 0f, 50f), true, new Vector3(105f, 0f, 50f), 2.25f, true), "landing in a closed alley");
                Check(!onlyC.LandingClosed(centre, false, centre, 0f, false), "off road inside an open zone");
                Check(onlyC.LandingClosed(closedCentre, false, closedCentre, 0f, false), "off road inside a closed zone");
                Check(onlyC.LandingClosed(new Vector3(500f, 0f, 500f), false, Vector3.zero, 0f, false), "off road outside every zone");
                Check(!onlyC.LandingClosed(new Vector3(500f, 0f, 500f), true, new Vector3(500f, 0f, 500f), 4f, false), "road outside every zone");

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
