#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DeliveryRider.Editor
{
    // Batch entry point; exercises real rules without entering Play mode or touching player saves.
    public static class ZoneRulesVerification
    {
        private static int assertions;
        private static int failures;
        private static readonly string[] Order = { "C", "A", "B", "D" };

        public static void Run()
        {
            assertions = failures = 0;
            CheckGroup("economy", VerifyEconomy);
            CheckGroup("zone/call table", VerifyZones);
            CheckGroup("door eligibility", VerifyDoors);
            CheckGroup("settlement", VerifySettlement);
            CheckGroup("polygon", VerifyPolygon);
            CheckGroup("session and barrier wiring", VerifySession);
            CheckGroup("saved assets", VerifyAssets);
            if (failures > 0) throw new Exception($"ZONE_RULES_FAIL: {failures} groups, {assertions} assertions");
            Debug.Log($"ZONE_RULES_PASS: {assertions} assertions; days 1-6 x current A/B/C/D x previous null/A/B/C/D");
        }

        private static void CheckGroup(string name, Action verify)
        {
            try { verify(); Debug.Log("ZONE_GROUP_PASS: " + name); }
            catch (Exception error) { failures++; Debug.Log("ZONE_GROUP_FAIL: " + name + " -> " + error.GetBaseException().Message); }
        }

        private static void VerifyEconomy()
        {
            var economy = ScriptableObject.CreateInstance<DeliveryEconomySettings>();
            try
            {
                var reward = RequiredMethod(typeof(DeliveryEconomySettings), "Reward");
                Assert((int)reward.Invoke(economy, new object[] { 200f }) == 3100, "200m pays 3100");
                Assert((int)reward.Invoke(economy, new object[] { 400f }) == 4700, "400m pays 4700");
                Assert((int)reward.Invoke(economy, new object[] { 218.75f }) == 3300, "half hundreds round up as in HTML");
                Assert(economy.LateReward(3100) == 1860 && economy.LateReward(4700) == 2820, "late pays exactly 60 percent");
                Assert(economy.TimeLimit(200) == 75 && economy.TimeLimit(401) == 130, "time limit rounds up to 5s");
                var serialized = new SerializedObject(economy);
                serialized.FindProperty("useDistanceReward").boolValue = false;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                Assert((int)reward.Invoke(economy, new object[] { 400f }) == 4500, "disabled formula restores reward tiers");
            }
            finally { UnityEngine.Object.DestroyImmediate(economy); }
        }

        private static void VerifyZones()
        {
            Type rules = typeof(DeliverySession).Assembly.GetType("DeliveryRider.DeliveryZoneRules");
            Assert(rules != null, "DeliveryZoneRules API exists");
            var open = RequiredMethod(rules, "OpenZonesForDay");
            var candidate = RequiredMethod(rules, "CandidateZones");
            string[] expectedOpen = { "C", "CA", "CAB", "CABD", "CABD", "CABD" };
            string[] currentZones = { "A", "B", "C", "D" };
            string[] previousZones = { null, "A", "B", "C", "D" };
            for (int day = 1; day <= 6; day++)
            {
                var opened = (List<string>)open.Invoke(null, new object[] { day, Order, new[] { 1, 2, 3, 4 } });
                Assert(string.Concat(opened) == expectedOpen[day - 1], "open zones day " + day);
                foreach (string current in currentZones)
                {
                    foreach (string previous in previousZones)
                    {
                        var rng = new System.Random(day * 31 + current[0] + (previous == null ? 0 : previous[0]));
                        var zones = (List<string>)candidate.Invoke(null, new object[] { opened, current, previous, 2, new Func<int, int>(rng.Next) });
                        Assert(zones.Count == 2 && zones.All(opened.Contains), "two open destinations");
                        if (opened.Count == 1) Assert(zones.SequenceEqual(new[] { "C", "C" }), "one zone duplicates zone, not door");
                        else Assert(zones.Distinct().Count() == 2, "distinct destination zones when available");
                        int preferred = opened.Count(z => z != current && z != previous);
                        if (preferred >= 2) Assert(zones.All(z => z != current && z != previous), "exclude current and previous when possible");
                        if (opened.Count == 2 && opened.Contains(current))
                            Assert(zones[0] != current && zones[1] == current, "other zone before current");
                        if (previous == null) Debug.Log($"ZONE_TABLE day={day} current={current} open={string.Join(",", opened)} calls={string.Join(",", zones)}");
                    }
                }
            }
            var empty = (List<string>)open.Invoke(null, new object[] { 1, Array.Empty<string>(), Array.Empty<int>() });
            Assert(empty.Count == 0, "empty configuration is safe");
            var lateDay = (List<string>)open.Invoke(null, new object[] { int.MaxValue, Order, new[] { 1, 2, 3, 4 } });
            Assert(string.Concat(lateDay) == "CABD", "days above configured range clamp");
        }

        private static void VerifyDoors()
        {
            Type rules = typeof(DeliverySession).Assembly.GetType("DeliveryRider.DeliveryZoneRules");
            Assert(rules != null, "door rules API exists");
            var method = RequiredMethod(rules, "EligibleDoors");
            float[] distances = { 300, 110, 100, 90, float.PositiveInfinity, float.NaN, -1 };
            float[] fromPlayer = { 60, 20, 21, 19, 100, 100, 100 };
            var used = new HashSet<int> { 0 };
            var offered = new HashSet<int>();
            Func<List<int>> eligible = () => (List<int>)method.Invoke(null, new object[] {
                new[] { 0, 1, 2, 3, 4, 5, 6 }, used, offered,
                new Func<int, float>(i => distances[i]), new Func<int, float>(i => fromPlayer[i]), 120f, 20f });
            Assert(eligible().SequenceEqual(new[] { 1, 2 }), "minimum-distance fallback keeps used house excluded and picks farthest two");
            offered.Add(1);
            Assert(eligible().SequenceEqual(new[] { 2, 3 }), "second offer never repeats first house");
            used.UnionWith(new[] { 1, 2, 3 });
            Assert(eligible().SequenceEqual(new[] { 0 }), "exhausted houses may repeat beyond 20m");
            offered.Add(0);
            Assert(eligible().SequenceEqual(new[] { 2 }), "reused house must be strictly beyond 20m and not already offered");
            offered.Add(2);
            Assert(eligible().Count == 0, "no eligible houses returns empty safely");
        }

        private static void VerifySettlement()
        {
            var close = typeof(RunProgress).GetMethod("CloseDay", new[] { typeof(long), typeof(int), typeof(long), typeof(bool) });
            Assert(close != null, "settlement supports shortfall switch");
            foreach (bool addDebt in new[] { false, true })
            {
                var run = new RunProgress(1, 100000);
                close.Invoke(run, new object[] { 2000L, 1, 5000L, addDebt });
                Assert(run.Cash == 0 && run.Debt == (addDebt ? 103000 : 100000), "shortfall mode " + addDebt);
                close.Invoke(run, new object[] { 2000L, 1, 5000L, addDebt });
                Assert(run.TotalDeliveries == 1, "settlement applied once");
            }
            var funded = new RunProgress(1, 100000);
            close.Invoke(funded, new object[] { 13000L, 1, 5000L, false });
            Assert(funded.TryBuyInline(8000, 1) && funded.Cash == 0, "inline can be bought after day 1 living cost");
        }

        private static void VerifyPolygon()
        {
            Type type = typeof(DeliverySession).Assembly.GetType("DeliveryRider.ZoneMap");
            Assert(type != null, "ZoneMap API exists");
            var map = ScriptableObject.CreateInstance(type);
            try
            {
                JsonUtility.FromJsonOverwrite("{\"zones\":[{\"id\":\"C\",\"polygon\":[{\"x\":0,\"y\":0},{\"x\":10,\"y\":0},{\"x\":10,\"y\":10},{\"x\":0,\"y\":10}]}]}", map);
                var at = RequiredMethod(type, "ZoneAt");
                Assert((string)at.Invoke(map, new object[] { new Vector3(5, 100, 5) }) == "C", "polygon uses xz independent of height; actual data=" + EditorJsonUtility.ToJson(map));
                Assert((string)at.Invoke(map, new object[] { new Vector3(0, 0, 5) }) == "C", "polygon edge belongs to zone");
                Assert(at.Invoke(map, new object[] { new Vector3(11, 0, 5) }) == null, "outside polygon has no zone");
            }
            finally { UnityEngine.Object.DestroyImmediate(map); }
        }

        private static void VerifyAssets()
        {
            EditorSceneManager.OpenScene("Assets/_Game/Scenes/TitleScene.unity", OpenSceneMode.Single);
            // Opening a scene unloads assets referenced only by this method's local variables.
            var economy = AssetDatabase.LoadAssetAtPath<DeliveryEconomySettings>("Assets/_Game/Settings/EconomySettings.asset");
            var title = UnityEngine.Object.FindFirstObjectByType<TitleUI>(FindObjectsInactive.Include);
            Assert(economy != null && title != null, "saved title and economy exist");
            Assert(new SerializedObject(title).FindProperty("economy").objectReferenceValue == economy, "saved title uses economy asset");
            Assert(economy.StartingDebt == 95000 && economy.LivingCost == 5000 && economy.InlinePrice == 8000
                && economy.InlineSaleDay == 2 && Mathf.Approximately(economy.InlineSpeedMultiplier, 1.6f), "saved run economy matches trial");
            Assert(economy.LateReward(3100) == 1860 && economy.TimeLimit(200) == 70, "saved time/late values match trial");
            var reward = RequiredMethod(typeof(DeliveryEconomySettings), "Reward");
            Assert((int)reward.Invoke(economy, new object[] { 200f }) == 3100 && (int)reward.Invoke(economy, new object[] { 400f }) == 4700,
                "saved distance reward enabled");
            var shortfall = new SerializedObject(economy).FindProperty("addShortfallToDebt");
            Assert(shortfall != null && !shortfall.boolValue, "saved shortfall switch defaults off");
        }

        private static void VerifySession()
        {
            var fixture = new GameObject("ZoneRulesFixture");
            fixture.SetActive(false);
            var economy = ScriptableObject.CreateInstance<DeliveryEconomySettings>();
            var routes = ScriptableObject.CreateInstance<DeliveryRouteTable>();
            var randomState = UnityEngine.Random.state;
            try
            {
                JsonUtility.FromJsonOverwrite("{\"shopNames\":[\"C far\",\"A closed\",\"C near\"],\"shopZones\":[\"C\",\"A\",\"C\"],\"doorZones\":[\"C\",\"C\",\"A\",\"A\",\"D\"],\"shopToDoor\":[300,330,350,360,400,10,20,120,220,310,150,250,300,320,400],\"doorToDoor\":[0,1,2,3,4,1,0,1,2,3,2,1,0,1,2,3,2,1,0,1,4,3,2,1,0]}", routes);
                var session = fixture.AddComponent<DeliverySession>();
                var player = Child(fixture.transform, "Player", Vector3.zero);
                var recipient = Child(fixture.transform, "Recipient", Vector3.zero).gameObject.AddComponent<DeliveryResident>();
                var shops = new[] { Child(fixture.transform, "Far", Vector3.right * 100),
                    Child(fixture.transform, "Closed", Vector3.right), Child(fixture.transform, "Near", Vector3.right * 5) };
                var doors = new Transform[5];
                for (int i = 0; i < doors.Length; i++) doors[i] = Child(fixture.transform, "Door" + i, Vector3.forward * (30 + 30 * i));
                SetField(session, "player", player); SetField(session, "economy", economy); SetField(session, "routeTable", routes);
                SetField(session, "shopPoints", shops); SetField(session, "doorPoints", doors); SetField(session, "recipient", recipient);
                SetField(session, "foodBag", Child(fixture.transform, "Food", Vector3.zero).gameObject);
                InvokePrivate(session, "Awake");
                Assert(session.ZoneMap == null && session.OpenZones.SequenceEqual(new[] { "C" }), "null ZoneMap supports day-1 startup");
                Assert(session.IsZoneOpen("C") && !session.IsZoneOpen("A"), "public zone contract");
                Assert(session.Candidates.Count == 2 && session.Candidates.All(c => c.ShopIndex == 2 && c.DestinationZone == "C"),
                    "both calls use closest current-zone shop, ignoring closer closed shop; arbitrary 3-shop/5-door table");
                Assert(session.Candidates[0].DoorIndex != session.Candidates[1].DoorIndex, "session offers different houses");
                foreach (var offer in session.Candidates)
                    Assert(offer.OnTimeReward == (offer.Distance == 150 ? 2700 : 3500), "session uses distance formula");
                typeof(DeliverySession).GetProperty("CurrentZone").SetValue(session, "D");
                ((List<DeliveryOrderOffer>)typeof(DeliverySession).GetField("candidates", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(session)).Clear();
                InvokePrivate(session, "FillGeneratedCandidates");
                Assert(session.Candidates.Count == 2 && session.Candidates.All(c => c.ShopIndex == 2), "closed current zone falls back to closest open shop");
                var barrier = fixture.AddComponent<ZoneBarrier>();
                var geometry = Child(fixture.transform, "Barrier", Vector3.zero).gameObject;
                SetField(barrier, "session", session); SetField(barrier, "barrierRoot", geometry);
                SetField(barrier, "requiredZones", new[] { "C", "A" });
                InvokePrivate(barrier, "Awake"); barrier.Refresh();
                Assert(geometry.activeSelf, "one closed required zone keeps barrier on");
                SetField(barrier, "requiredZones", new[] { "C" }); barrier.Refresh();
                Assert(!geometry.activeSelf && barrier.enabled, "all required zones open hides geometry and retains component");
            }
            finally
            {
                UnityEngine.Random.state = randomState;
                UnityEngine.Object.DestroyImmediate(fixture);
                UnityEngine.Object.DestroyImmediate(economy);
                UnityEngine.Object.DestroyImmediate(routes);
            }
        }

        private static Transform Child(Transform parent, string name, Vector3 position)
        {
            var child = new GameObject(name).transform;
            child.SetParent(parent, false); child.position = position;
            return child;
        }
        private static void SetField(object target, string name, object value)
            => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
        private static void InvokePrivate(object target, string name)
            => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, null);

        public static void ConfigureTrialAssets()
        {
            var economy = AssetDatabase.LoadAssetAtPath<DeliveryEconomySettings>("Assets/_Game/Settings/EconomySettings.asset");
            Assert(economy != null, "economy asset exists");
            var data = new SerializedObject(economy);
            SetInt(data, "startingDebt", 100000); SetInt(data, "livingCost", 5000);
            SetInt(data, "inlinePrice", 8000); SetInt(data, "inlineSaleDay", 1);
            SetInt(data, "rewardBase", 1500);
            SetFloat(data, "rewardPerMeter", 8); SetFloat(data, "inlineSpeedMultiplier", 1.6f);
            SetFloat(data, "lateRatio", 0.6f); SetFloat(data, "runSpeed", 6);
            SetFloat(data, "limitRatio", 1.5f); SetFloat(data, "limitPadding", 25); SetFloat(data, "limitRounding", 5);
            data.FindProperty("useDistanceReward").boolValue = true;
            data.FindProperty("addShortfallToDebt").boolValue = false;
            SetArray(data, "zoneOrder", Order);
            SetArray(data, "openCountByDay", new[] { 1, 2, 3, 4 });
            var minimum = data.FindProperty("minimumDistanceByOpenCount");
            minimum.arraySize = 4;
            float[] distances = { 120, 220, 300, 300 };
            for (int i = 0; i < distances.Length; i++) minimum.GetArrayElementAtIndex(i).floatValue = distances[i];
            data.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(economy);
            AssetDatabase.SaveAssets();
            var scene = EditorSceneManager.OpenScene("Assets/_Game/Scenes/TitleScene.unity", OpenSceneMode.Single);
            economy = AssetDatabase.LoadAssetAtPath<DeliveryEconomySettings>("Assets/_Game/Settings/EconomySettings.asset");
            Assert(economy != null, "reload economy after scene asset unloading");
            var title = UnityEngine.Object.FindFirstObjectByType<TitleUI>(FindObjectsInactive.Include);
            Assert(title != null, "title UI exists");
            var titleData = new SerializedObject(title);
            titleData.FindProperty("economy").objectReferenceValue = economy;
            titleData.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(title);
            Assert(new SerializedObject(title).FindProperty("economy").objectReferenceValue == economy,
                "title economy assigned in memory; title=" + title.gameObject.scene.path + "; economy=" + AssetDatabase.GetAssetPath(economy));
            EditorSceneManager.MarkSceneDirty(scene);
            Assert(EditorSceneManager.SaveScene(scene), "title scene saved");
            EditorSceneManager.OpenScene(scene.path, OpenSceneMode.Single);
            title = UnityEngine.Object.FindFirstObjectByType<TitleUI>(FindObjectsInactive.Include);
            Assert(new SerializedObject(title).FindProperty("economy").objectReferenceValue == economy, "title economy survives scene reopen");
            Debug.Log("ZONE_TRIAL_ASSETS_CONFIGURED: title debt=100000, inline day=1, distance reward on, shortfall debt off");
            Run();
        }

        private static void SetInt(SerializedObject data, string name, int value) => data.FindProperty(name).intValue = value;
        private static void SetFloat(SerializedObject data, string name, float value) => data.FindProperty(name).floatValue = value;
        private static void SetArray(SerializedObject data, string name, string[] values)
        {
            var property = data.FindProperty(name); property.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++) property.GetArrayElementAtIndex(i).stringValue = values[i];
        }
        private static void SetArray(SerializedObject data, string name, int[] values)
        {
            var property = data.FindProperty(name); property.arraySize = values.Length;
            for (int i = 0; i < values.Length; i++) property.GetArrayElementAtIndex(i).intValue = values[i];
        }
        private static MethodInfo RequiredMethod(Type type, string name)
        {
            var method = type.GetMethod(name, BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public);
            Assert(method != null, name + " API exists");
            return method;
        }
        private static void Assert(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new Exception("FAIL: " + message);
        }
    }
}
#endif
