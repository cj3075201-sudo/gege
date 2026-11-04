using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Unity.Mathematics;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Splines;
using UnityEngine.UI;

namespace DeliveryRider.Editor
{
    // Applies the widened Myeongji map (Docs/MapLayout/Myeongji/*_expanded.json, zones.json from
    // Tools/build_myeongji_expansion.py) to the Myeongji scene. Each step can be run again: it skips what is already
    // there and fails loudly on a mismatch. Run in batch mode with -executeMethod, one step at a time.
    public static class MyeongjiExpansionInstaller
    {
        private const string ScenePath = "Assets/_Game/Scenes/FourDistrictPrototype_Myeongji.unity";
        private const string DataFolder = "Docs/MapLayout/Myeongji";
        private const string RouteTablePath = "Assets/_Game/Settings/MyeongjiRouteTable.asset";
        private const string MapImagePath = "Assets/_Game/Art/Myeongji/Map/MyeongjiMap.png";
        private const string ZoneMapPath = "Assets/_Game/Settings/MyeongjiZoneMap.asset";
        private const float RoadSurfaceHeight = -0.14f;
        private const float AnchorTolerance = 0.02f;
        // New roads carry this width and up as main roads, as the original map did.
        private const float MainRoadWidth = 5f;

        public static void ApplySplines()
        {
            var scene = OpenScene();
            var layout = ReadJson("layout_expanded.json");
            int existing = (int)layout["existingRoadCount"];
            var roads = (JArray)layout["roads"];
            var routes = Root(scene, "MyeongjiRoutes");
            int added = 0, kept = 0;
            for (int i = 0; i < roads.Count; i++)
            {
                var road = roads[i];
                string id = (string)road["id"];
                var found = routes.transform.Find(id);
                if (i < existing)
                {
                    if (found == null) throw new InvalidOperationException("Original road missing: " + id);
                    kept++;
                    continue;
                }
                if (found != null) { kept++; continue; }
                AddRoad(routes.transform, id, Flat(road["a"]), Flat(road["b"]), (float)road["width"]);
                added++;
            }
            int total = routes.GetComponentsInChildren<RoadSurface>(true).Length;
            if (total != roads.Count) throw new InvalidOperationException($"Expected {roads.Count} roads, found {total}.");
            Save(scene);
            Debug.Log($"[MapExpansion] Splines: added {added}, kept {kept}, total {total}.");
        }

        private static void AddRoad(Transform parent, string id, Vector2 a, Vector2 b, float width)
        {
            var go = new GameObject(id);
            go.transform.SetParent(parent, false);
            var container = go.AddComponent<SplineContainer>();
            container.Spline = new Spline();
            foreach (var point in new[] { a, b })
                container.Spline.Add(new BezierKnot(new float3(point.x, RoadSurfaceHeight, point.y)), TangentMode.Linear);
            var surface = go.AddComponent<RoadSurface>();
            surface.Configure(width >= MainRoadWidth ? RoadKind.Main : RoadKind.Alley, width, 0);
            // Lookup only, like the original routes: the road you see is part of the ground mesh.
            surface.enabled = false;
            go.GetComponent<MeshRenderer>().enabled = false;
        }

        public static void ApplyAnchors()
        {
            var scene = OpenScene();
            var locations = Root(scene, "MyeongjiLocations").transform;
            var anchors = (JArray)ReadJson("anchors_expanded.json");
            int moved = 0, kept = 0;
            foreach (var anchor in anchors)
            {
                string name = (string)anchor["name"];
                var point = locations.Find(name) ?? throw new InvalidOperationException("Location missing: " + name);
                var position = (JArray)anchor["position"];
                var target = new Vector3((float)position[0], (float)position[1], (float)position[2]);
                if (new Vector2(point.position.x - target.x, point.position.z - target.z).magnitude <= AnchorTolerance) { kept++; continue; }
                if ((string)anchor["type"] == "home") throw new InvalidOperationException("The home must not move.");
                var forward = (JArray)anchor["forward"];
                point.SetPositionAndRotation(target, Quaternion.LookRotation(new Vector3((float)forward[0], 0f, (float)forward[2])));
                moved++;
            }
            CheckSessionPoints(scene, anchors);
            Save(scene);
            Debug.Log($"[MapExpansion] Anchors: moved {moved}, kept {kept}.");
        }

        // The route table is indexed by these arrays, so each slot must hold the anchor of the same type and index.
        private static void CheckSessionPoints(Scene scene, JArray anchors)
        {
            var session = new SerializedObject(Root(scene, "DeliverySession").GetComponent<DeliverySession>());
            foreach (string kind in new[] { "shop", "door" })
            {
                var array = session.FindProperty(kind + "Points");
                var expected = anchors.Where(a => (string)a["type"] == kind).OrderBy(a => (int)a["index"]).ToArray();
                if (array.arraySize != expected.Length) throw new InvalidOperationException($"{kind}Points holds {array.arraySize}, expected {expected.Length}.");
                for (int i = 0; i < expected.Length; i++)
                {
                    var point = array.GetArrayElementAtIndex(i).objectReferenceValue as Transform;
                    if (point == null || point.name != (string)expected[i]["name"])
                        throw new InvalidOperationException($"{kind}Points[{i}] is {(point == null ? "empty" : point.name)}, expected {expected[i]["name"]}.");
                }
            }
        }

        public static void ApplyRouteTable()
        {
            var table = AssetDatabase.LoadAssetAtPath<DeliveryRouteTable>(RouteTablePath) ?? throw new InvalidOperationException("Route table missing.");
            JsonUtility.FromJsonOverwrite(File.ReadAllText(DataFolder + "/route_table_expanded.json"), table);
            if (!table.IsValid || table.ShopCount != 12 || table.DoorCount != 32) throw new InvalidOperationException("Route table does not match 12 shops and 32 doors.");
            EditorUtility.SetDirty(table);
            AssetDatabase.SaveAssets();
            Debug.Log($"[MapExpansion] Route table: {table.ShopCount} shops, {table.DoorCount} doors.");
        }

        // Copies the baked picture over the one the map already shows, keeping its import settings.
        public static void ApplyMap()
        {
            var scene = OpenScene();
            var bounds = ReadJson("zones.json")["bounds"];
            var minimum = Flat(bounds["minimum"]);
            var size = Flat(bounds["size"]);
            File.Copy(DataFolder + "/MyeongjiMap_Expanded.png", MapImagePath, true);
            AssetDatabase.ImportAsset(MapImagePath, ImportAssetOptions.ForceSynchronousImport);
            var map = Root(scene, "UI").GetComponentInChildren<DeliveryMapView>(true) ?? throw new InvalidOperationException("Map view missing.");
            var view = new SerializedObject(map);
            view.FindProperty("worldMinimum").vector2Value = minimum;
            view.FindProperty("worldSize").vector2Value = size;
            view.ApplyModifiedPropertiesWithoutUndo();
            var area = (RectTransform)view.FindProperty("mapArea").objectReferenceValue;
            var fitter = area.GetComponent<AspectRatioFitter>();
            if (fitter != null) fitter.aspectRatio = size.x / size.y;
            var picture = area.Find("MapGeometryBackground")?.GetComponent<RawImage>();
            if (picture == null || AssetDatabase.GetAssetPath(picture.texture) != MapImagePath) throw new InvalidOperationException("Map background does not show " + MapImagePath);
            Save(scene);
            Debug.Log($"[MapExpansion] Map: minimum {minimum}, size {size}.");
        }

        public static void ApplyZoneMap()
        {
            var scene = OpenScene();
            var zones = (JArray)ReadJson("zones.json")["zones"];
            var asset = AssetDatabase.LoadAssetAtPath<ZoneMap>(ZoneMapPath);
            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance<ZoneMap>();
                AssetDatabase.CreateAsset(asset, ZoneMapPath);
            }
            var data = new SerializedObject(asset);
            var array = data.FindProperty("zones");
            array.arraySize = zones.Count;
            for (int i = 0; i < zones.Count; i++)
            {
                var zone = array.GetArrayElementAtIndex(i);
                zone.FindPropertyRelative("id").stringValue = (string)zones[i]["id"];
                var polygon = (JArray)zones[i]["polygon"];
                var points = zone.FindPropertyRelative("polygon");
                points.arraySize = polygon.Count;
                for (int p = 0; p < polygon.Count; p++) points.GetArrayElementAtIndex(p).vector2Value = Flat(polygon[p]);
            }
            data.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            var session = new SerializedObject(Root(scene, "DeliverySession").GetComponent<DeliverySession>());
            session.FindProperty("zoneMap").objectReferenceValue = asset;
            session.ApplyModifiedPropertiesWithoutUndo();
            CheckAnchorsInZones(scene, asset);
            Save(scene);
            Debug.Log($"[MapExpansion] Zone map: {zones.Count} zones, connected to the session.");
        }

        private static void CheckAnchorsInZones(Scene scene, ZoneMap map)
        {
            var locations = Root(scene, "MyeongjiLocations").transform;
            var wrong = new List<string>();
            foreach (Transform point in locations)
            {
                string zone = point.name.Substring(0, 1);
                if (map.ZoneAt(point.position) != zone) wrong.Add($"{point.name} in {map.ZoneAt(point.position) ?? "none"}");
            }
            if (wrong.Count > 0) throw new InvalidOperationException("Anchors outside their zone: " + string.Join(", ", wrong));
        }

        // Read-only report of what the steps above should have left in the scene.
        public static void Check()
        {
            var scene = OpenScene();
            var failures = new List<string>();
            var layout = ReadJson("layout_expanded.json");
            var routes = Root(scene, "MyeongjiRoutes");
            var surfaces = routes.GetComponentsInChildren<RoadSurface>(true);
            if (surfaces.Length != ((JArray)layout["roads"]).Count) failures.Add($"roads {surfaces.Length}");
            foreach (var road in (JArray)layout["roads"])
            {
                var found = routes.transform.Find((string)road["id"]);
                if (found == null) { failures.Add("no road " + road["id"]); continue; }
                var surface = found.GetComponent<RoadSurface>();
                var a = Flat(road["a"]);
                var b = Flat(road["b"]);
                Vector3 start = surface.Container.EvaluatePosition(0f), end = surface.Container.EvaluatePosition(1f);
                bool original = Vector2.Distance(new Vector2(start.x, start.z), a) < 0.05f && Vector2.Distance(new Vector2(end.x, end.z), b) < 0.05f;
                if (!original) failures.Add("road ends differ " + road["id"]);
                if (Mathf.Abs(surface.HalfWidth * 2f - (float)road["width"]) > 0.01f) failures.Add("road width " + road["id"]);
                if (surface.enabled || found.GetComponent<MeshRenderer>().enabled) failures.Add("road shows its own mesh " + road["id"]);
            }
            var anchors = (JArray)ReadJson("anchors_expanded.json");
            var locations = Root(scene, "MyeongjiLocations").transform;
            foreach (var anchor in anchors)
            {
                var point = locations.Find((string)anchor["name"]);
                var position = (JArray)anchor["position"];
                if (point == null || new Vector2(point.position.x - (float)position[0], point.position.z - (float)position[2]).magnitude > AnchorTolerance)
                    failures.Add("anchor " + anchor["name"]);
            }
            CheckSessionPoints(scene, anchors);
            var session = new SerializedObject(Root(scene, "DeliverySession").GetComponent<DeliverySession>());
            var table = session.FindProperty("routeTable").objectReferenceValue as DeliveryRouteTable;
            if (table == null || AssetDatabase.GetAssetPath(table) != RouteTablePath || !table.IsValid) failures.Add("route table");
            else
            {
                var json = JObject.Parse(File.ReadAllText(DataFolder + "/route_table_expanded.json"));
                var distances = (JArray)json["shopToDoor"];
                for (int s = 0; s < table.ShopCount; s++)
                    for (int d = 0; d < table.DoorCount; d++)
                        if (Mathf.Abs(table.ShopToDoor(s, d) - (float)distances[s * table.DoorCount + d]) > 0.01f) { failures.Add($"route {s}->{d}"); s = table.ShopCount; break; }
            }
            var zoneMap = session.FindProperty("zoneMap").objectReferenceValue as ZoneMap;
            if (zoneMap == null) failures.Add("session has no zone map");
            else CheckAnchorsInZones(scene, zoneMap);
            int missing = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<Component>(true)).Count(c => c == null);
            if (missing > 0) failures.Add("missing scripts " + missing);
            if (failures.Count > 0) throw new InvalidOperationException("[MapExpansion] Check failed: " + string.Join("; ", failures));
            Debug.Log($"[MapExpansion] Check passed: {surfaces.Length} roads, {anchors.Count} anchors, missing scripts 0.");
        }

        private static Scene OpenScene()
        {
            if (EditorApplication.isPlaying || EditorApplication.isCompiling) throw new InvalidOperationException("Edit mode required.");
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (!scene.IsValid()) throw new InvalidOperationException("Could not open " + ScenePath);
            return scene;
        }

        private static void Save(Scene scene)
        {
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("Could not save the scene.");
            AssetDatabase.SaveAssets();
        }

        private static GameObject Root(Scene scene, string name) =>
            scene.GetRootGameObjects().SingleOrDefault(g => g.name == name) ?? throw new InvalidOperationException("Scene root missing: " + name);

        private static JToken ReadJson(string name) => JToken.Parse(File.ReadAllText(DataFolder + "/" + name));

        private static Vector2 Flat(JToken pair) => new Vector2((float)pair[0], (float)pair[1]);
    }
}
