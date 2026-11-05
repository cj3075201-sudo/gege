using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DeliveryRider.Editor
{
    public static class CatBoxerChecks
    {
        public static void Run()
        {
            try
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(CatBoxerPrefabBuilder.PrefabPath);
                Check(prefab != null, "prefab exists");
                var cat = prefab.GetComponent<CatBoxerHazard>();
                Check(cat != null && cat is IPlacedHazard && cat is IPunchable, "planner and existing rider punch contracts");
                var view = prefab.GetComponentInChildren<CatBoxerView>(true);
                Check(view != null && view.GetComponent<Collider>() != null && !view.GetComponent<Collider>().isTrigger, "solid punch query volume");
                Check(view.GetComponent<Rigidbody>().isKinematic, "query volume cannot pin a rider");
                Check(prefab.GetComponentsInChildren<Renderer>(true).All(r => r.sharedMaterials.All(m => m != null && AssetDatabase.Contains(m))), "only existing persistent materials");
                var fields = DevValue.Discover(cat);
                Check(fields.Count >= 55, "F1 automatic numeric discovery: " + fields.Count);
                Check(fields.All(v => !string.IsNullOrEmpty(v.Label) && v.Label.Any(c => c >= 0xAC00 && c <= 0xD7A3)), "every F1 value has Korean label");
                Equal(cat, "unlockDay", 2f); Equal(cat, "catHealth", 3f); Equal(cat, "lossHits", 3f);
                Equal(cat, "triggerRadius", 5f); Equal(cat, "boutSeconds", 22f); Equal(cat, "missOpenSeconds", 1.1f);
                Equal(cat, "jabTellSeconds", .55f); Equal(cat, "hookTellSeconds", .7f); Equal(cat, "upperTellSeconds", .85f);
                foreach (var pair in new[] { ("jabUp", 5.5f), ("hookUp", 8f), ("chaseUp", 7f) })
                {
                    float converted = (float)typeof(CatBoxerHazard).GetField(pair.Item1, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(cat);
                    Check(Mathf.Abs(converted - pair.Item2) < .00001f, pair.Item1 + " uses HTML upward speed with accident gravity 2.24");
                }
                var slotHost = new GameObject("CatScaleProbe");
                try
                {
                    var slot = slotHost.AddComponent<HazardSlot>(); slot.SetTriggerScale(2f);
                    Check(slot.TriggerScale == 2f, "shared numeric trigger scale contract");
                }
                finally { UnityEngine.Object.DestroyImmediate(slotHost); }
                Check(DevLabels.HazardType("cat") == "고양이 복서", "F1 hazard name");
                Debug.Log("CAT_CHECKS_PASS: defaults, gravity, prefab references, punch/placement contracts, Korean F1 discovery and scale.");
                EditorApplication.Exit(0);
            }
            catch (Exception error)
            {
                Debug.LogError("CAT_CHECKS_FAIL: " + error);
                EditorApplication.Exit(1);
            }
        }
        private static void Equal(object owner, string name, float expected)
        {
            float actual = Convert.ToSingle(owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner));
            Check(Mathf.Abs(actual - expected) < .00001f, name + "=" + actual);
        }
        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            Debug.Log("CAT_CHECK ok: " + message);
        }
    }
}
