using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DeliveryRider.Editor
{
    public static class FallingObjectChecks
    {
        public const string PrefabPath = "Assets/_Game/Prefabs/Hazards/FallingObject.prefab";
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static void Run()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Check(prefab != null, "falling prefab exists");
            var hazard = prefab.GetComponent("FallingObjectHazard");
            Check(hazard is IPlacedHazard, "prefab implements placement");
            foreach (var pair in new[] { ("triggerRadius", 9f), ("initialLeadSeconds", .9f), ("trackingLeadSeconds", .6f),
                ("trackingSeconds", 1.3f), ("impactSeconds", 1.9f), ("hitRadius", 1.7f), ("stunSeconds", 2.6f) })
            {
                float value = (float)hazard.GetType().GetField(pair.Item1, Any).GetValue(hazard);
                Check(Mathf.Abs(value - pair.Item2) < .0001f, pair.Item1 + " matches HTML");
                Check(DevValue.Discover(hazard).Exists(v => v.PropertyPath == pair.Item1), pair.Item1 + " appears in F1");
                Check(DevLabels.Find("FallingObjectHazard", pair.Item1) != null, pair.Item1 + " has Korean label");
            }
            foreach (string name in new[] { "crow", "fallingObject", "aimMarker", "cue" })
                Check(hazard.GetType().GetField(name, Any).GetValue(hazard) is UnityEngine.Object, name + " is connected");
            Check(prefab.GetComponentsInChildren<Collider>(true).Length == 0, "visuals cannot block the rider");
            Debug.Log("FallingObjectChecks PASS: prefab, HTML values, F1 fields and visual references.");
        }

        public static void RunCore()
        {
            Run();
            DevToolsStatsChecks.Run();
            ZoneRulesVerification.Run();
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("FallingObjectChecks FAIL: " + message);
        }
    }
}
