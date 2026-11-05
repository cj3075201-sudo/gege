using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DeliveryRider.Editor
{
    public static class HtmlHazardSettingsInstaller
    {
        private static readonly Dictionary<string, string> Types = new Dictionary<string, string>
        {
            { "맨", "manhole" }, { "소", "hydrant" }, { "근", "muscleman" }, { "오", "duck" }, { "닭", "chicken" },
            { "타", "tire" }, { "수", "melon" }, { "낙", "falling" }, { "개", "dog" }, { "고", "cat" },
            { "비", "theft" }, { "떼", "pigeonflock" }, { "라", "clone" }, { "B", "B" }
        };
        public static void Apply()
        {
            var rules = JObject.Parse(File.ReadAllText("Docs/MapLayout/Myeongji/html_hazard_rules.json"));
            var settings = AssetDatabase.LoadAssetAtPath<HazardSpawnSettings>("Assets/_Game/Settings/MyeongjiHazardSpawnSettings.asset");
            var kinds = settings.Kinds.ToList();
            foreach (var spec in new[] { ("falling", "FallingObject", 1, 40), ("cat", "CatBoxer", 2, 31), ("dog", "Dog", 2, 29), ("clone", "CloneRider", 2, 7) })
            {
                var kind = kinds.FirstOrDefault(k => k.type == spec.Item1);
                if (kind == null) { kind = new HazardSpawnSettings.Kind { type = spec.Item1 }; kinds.Add(kind); }
                kind.prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/_Game/Prefabs/Hazards/{spec.Item2}.prefab");
                if (kind.prefab == null) throw new InvalidOperationException("Missing prefab " + spec.Item2);
                kind.role = HazardRole.Trigger; kind.unlockDay = spec.Item3; kind.poolSize = spec.Item4;
            }
            foreach (var kind in kinds)
            {
                string code = Types.FirstOrDefault(p => p.Value == kind.type).Key;
                if (kind.role == HazardRole.Trigger && code != null)
                {
                    kind.mainPoolSize = (int?)rules["main"][code] ?? 0;
                    kind.alleyPoolSize = (int?)rules["alley"][code] ?? 0;
                    kind.poolSize = kind.mainPoolSize + kind.alleyPoolSize;
                }
                if (kind.type == "muscleman" || kind.type == "duck" || kind.type == "chicken" || kind.type == "celebrity" || kind.type == "falling") kind.unlockDay = 1;
                if (new[] { "tire", "melon", "theft", "pigeonflock", "dog", "clone", "cat" }.Contains(kind.type)) kind.unlockDay = 2;
                if (kind.type == "manhole") { kind.countPerDay = new[] {40,54,70,84}; kind.objectAlleyShare = 29f / 53f; }
                if (kind.type == "hydrant") { kind.countPerDay = new[] {38,38,38,38}; kind.objectAlleyShare = 0f; }
                // The zone prototype removed watermelons from layout; keep the existing Unity trigger as requested.
                if (kind.type == "melon") { kind.mainPoolSize = 15; kind.alleyPoolSize = 0; kind.poolSize = 15; }
            }
            settings.Configure(kinds.ToArray());
            var combos = new List<HazardSpawnSettings.ComboTemplate>();
            foreach (string collection in new[] { "TPL", "ATPL" })
                foreach (var row in rules[collection])
                    combos.Add(new HazardSpawnSettings.ComboTemplate
                    {
                        name = (string)row[0], count = (int)row[1], alley = collection == "ATPL",
                        members = row[2].Where(m => Types.ContainsKey((string)m[0])).Select(m => new HazardSpawnSettings.ComboMember
                        { type = Types[(string)m[0]], along = (float)m[1] * 3f, side = (int)m[2], lateralFraction = m.Count() > 3 ? (float)m[3] : -1f }).ToArray()
                    });
            foreach (var row in rules["MAPC"])
            {
                float center = Mathf.Ceil(row["mem"].Max(m => (float)m[1]) / 2f);
                var members = row["mem"].Where(m => Types.ContainsKey((string)m[0])).Select((m,i) => new HazardSpawnSettings.ComboMember
                    { type = Types[(string)m[0]], along = ((float)m[1] - center) * 3f, side = i % 2 == 0 ? 1 : -1 }).ToArray();
                if (members.Length > 1) combos.Add(new HazardSpawnSettings.ComboTemplate {name = "맵 연쇄 " + (string)row["ch"], count = 1, members = members});
            }
            settings.ConfigureCombos(combos.ToArray());
            var anchors = JArray.Parse(File.ReadAllText("Docs/MapLayout/Myeongji/anchors_expanded.json"));
            settings.ConfigureKeepouts(anchors.Select(a => new Vector3((float)a["position"][0], (float)a["position"][1], (float)a["position"][2])).ToArray());
            var so = new SerializedObject(settings); so.FindProperty("spacing").floatValue = 9f; so.FindProperty("sameTypeSpacing").floatValue = 9f; so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(settings); AssetDatabase.SaveAssetIfDirty(settings);
            Ground("Manhole", 2.6f); Ground("Hydrant", 2.6f);
            Tune("Chickens", ("chaseSeconds",7f), ("attachmentSeconds",2.5f), ("extraAttachmentSeconds",1.5f), ("contactRadius", .9f), ("chaseSpeed",6.5f));
            Tune("CelebrityCrowd", ("triggerRadius",16f));
            Tune("PigeonSnatch", ("triggerRadius",8f));
            Tune("Muscleman", ("spawnBehindDistance",12f), ("speedMultiplier",11.5f/6f), ("disappearAfterTurn",2.5f), ("runnerRadius",1.15f));
            Tune("RollingTire", ("initialSpeed",12f), ("maximumSpeed",12f), ("rearmSeconds",10f));
            Debug.Log("HTML_SETTINGS kinds=" + kinds.Count + " comboTemplates=" + combos.Count + " main/alley pools and object day counts applied");
        }
        public static void ApplyFlightC()
        {
            Tune("Manhole",("upwardImpulse",24f)); Tune("Hydrant",("upwardImpulse",30f));
            Tune("DuckFlock",("bounceLift",15.6f)); Tune("Muscleman",("shoveLift",6.5f));
            Tune("RollingTire",("upwardImpulse",13f),("smallUpwardImpulse",6.75f)); Tune("MelonPile",("hitLift",6.75f));
            Tune("CatBoxer",("jabUp",5.5f),("hookUp",8f),("chaseUp",7f)); Tune("Dog",("bounceLift",9f));
            string path="Assets/_Game/Prefabs/Hazards/CatBoxer.prefab";
            var cat=PrefabUtility.LoadPrefabContents(path);
            try { var data=new SerializedObject(cat.GetComponent<CatBoxerHazard>());data.FindProperty("upperUp").vector2Value=new Vector2(22f,25f);data.FindProperty("lossUp").vector2Value=new Vector2(24f,27f);data.ApplyModifiedPropertiesWithoutUndo();PrefabUtility.SaveAsPrefabAsset(cat,path); }
            finally{PrefabUtility.UnloadPrefabContents(cat);}
            var scene=EditorSceneManager.OpenScene("Assets/_Game/Scenes/FourDistrictPrototype_Myeongji.unity",OpenSceneMode.Single);
            var motor=scene.GetRootGameObjects().SelectMany(r=>r.GetComponentsInChildren<RiderMotor>(true)).Single();
            var so=new SerializedObject(motor);so.FindProperty("airGravityScale").floatValue=2.24f;so.FindProperty("minimumBounceSpeed").floatValue=5f;so.FindProperty("maximumFloorBounces").intValue=4;so.ApplyModifiedPropertiesWithoutUndo();
            EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);
            Debug.Log("HTML_FLIGHT_C_APPLIED gravity=2.24 bounceThreshold=5 bounces=4; upward values restored; scene rider fields only");
        }

        public static void FinalizeArtifacts()
        {
            var values=new Dictionary<string,object>();
            foreach(var guid in AssetDatabase.FindAssets("t:Prefab",new[]{"Assets/_Game/Prefabs/Hazards"}))
            {
                var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                foreach(var part in prefab.GetComponents<MonoBehaviour>())
                {
                    if(!(part is IPlacedHazard))continue;
                    var fields=new Dictionary<string,object>();
                    foreach(var field in part.GetType().GetFields(System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Public))
                    {
                        if(!Attribute.IsDefined(field,typeof(SerializeField)))continue;
                        object value=field.GetValue(part);
                        if(value is float||value is int||value is bool)fields[field.Name]=value;
                        else if(value is Vector2 v)fields[field.Name]=new[]{v.x,v.y};
                    }
                    values[part.GetType().Name]=fields;
                }
            }
            File.WriteAllText("Library/html_final_values.json",Newtonsoft.Json.JsonConvert.SerializeObject(values,Newtonsoft.Json.Formatting.Indented));
            CleanupProbeAssets();
        }

        public static void CleanupProbeAssets()
        {
            foreach(string name in new[]{"HtmlRoadsProbe","HtmlPedestrianProbe","HtmlRoutedProbe","HtmlRoadsDenseProbe","HtmlPedestrianDenseProbe","HtmlRoutedDenseProbe"})
            {string path="Assets/"+name+".asset";if(AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path)!=null&&!AssetDatabase.DeleteAsset(path))throw new InvalidOperationException("Cannot remove generated probe "+path);}
            AssetDatabase.SaveAssets();Debug.Log("HTML_CLEANUP temporary bake copies removed through Unity API");
        }

        private static void Ground(string name, float diameter)
        {
            string path = "Assets/_Game/Prefabs/Hazards/" + name + ".prefab";
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var shape = root.GetComponent<BoxCollider>();
                var size = shape.size; size.x = Mathf.Max(size.x, diameter / Mathf.Abs(root.transform.localScale.x)); size.z = Mathf.Max(size.z, diameter / Mathf.Abs(root.transform.localScale.z)); shape.size = size;
                var data = new SerializedObject(root.GetComponent<GroundContactHazard>());
                data.FindProperty("objectRearmSeconds").floatValue = 10f; data.FindProperty("landingRadius").floatValue = 2.6f; data.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root,path);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
        private static void Tune(string name, params (string field, float value)[] fields)
        {
            string path = "Assets/_Game/Prefabs/Hazards/" + name + ".prefab";
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null) throw new InvalidOperationException("Missing tuning prefab " + path);
            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                foreach (var component in root.GetComponents<MonoBehaviour>())
                {
                    var data = new SerializedObject(component);
                    foreach (var field in fields) { var property = data.FindProperty(field.field); if (property != null) property.floatValue = field.value; }
                    data.ApplyModifiedPropertiesWithoutUndo();
                }
                PrefabUtility.SaveAsPrefabAsset(root,path);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }
    }
}
