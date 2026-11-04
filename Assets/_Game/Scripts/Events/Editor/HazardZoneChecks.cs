using System;
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
