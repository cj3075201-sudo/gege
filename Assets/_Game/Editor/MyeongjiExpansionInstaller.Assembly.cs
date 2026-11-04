using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace DeliveryRider.Editor
{
    // Puts the new ground, buildings and lamps into the scene. Buildings are clones of the 52 map buildings with their
    // meshes, colliders and materials, placed from lots_expanded.json and furniture_expanded.json (Tools/build_myeongji_lots.py).
    public static partial class MyeongjiExpansionInstaller
    {
        private const string MapPrefabPath = "Assets/_Game/Prefabs/Myeongji/MyeongjiMap.prefab";
        private const string ExpansionPrefabPath = "Assets/_Game/Prefabs/Myeongji/MyeongjiMapExpansion.prefab";
        private const string GroundMeshPath = "Assets/_Game/Art/Myeongji/Geometry/Ground_Expanded.asset";
        private const string MarkingsMeshPath = "Assets/_Game/Art/Myeongji/Geometry/Ground_ExpandedMarkings.asset";
        private const string MaterialFolder = "Assets/_Game/Art/Myeongji/Materials/";
        private const string ExpansionRootName = "MyeongjiMapExpansion";
        private const string MarkingsName = "ExpandedMarkings";
        private const string SourceLamp = "StreetLamp_02";
        // Original lamps standing where new roads now run: their new base, on the sidewalk beside that road.
        private static readonly Dictionary<string, Vector2> MovedLamps = new Dictionary<string, Vector2>
        {
            { "StreetLamp_01", new Vector2(-5.5f, 4.72f) },
            { "StreetLamp_07", new Vector2(122.2f, 4.72f) },
        };
        // New lamps whose planned spot falls inside a building's real outline (the plan used nominal sizes): moved along the
        // road, same offset from the kerb. Keyed by the order in furniture_expanded.json, starting at 1.
        private static readonly Dictionary<int, Vector2> CorrectedLamps = new Dictionary<int, Vector2>
        {
            { 62, new Vector2(54.5f, -137.4f) },
        };
        // Wall colours per zone, from Codex lots report; each clone takes the next one in turn.
        private static readonly Dictionary<string, string[]> ZoneWalls = new Dictionary<string, string[]>
        {
            { "A", new[] { "DDD8C9", "D5C9AF", "B3816D" } },
            { "B", new[] { "8FA6B0", "A6AAA5", "DDD8C9" } },
            { "C", new[] { "A9B5A1", "D5C9AF", "DDD8C9" } },
            { "D", new[] { "B3816D", "A6AAA5", "8FA6B0" } },
        };
        // Lots left out after checking them in the scene, with the reason in the report.
        private static readonly HashSet<string> ExcludedLots = new HashSet<string>();

        public static void ApplyGround()
        {
            var ground = AssetDatabase.LoadAssetAtPath<Mesh>(GroundMeshPath) ?? throw new InvalidOperationException("Missing " + GroundMeshPath);
            var markings = AssetDatabase.LoadAssetAtPath<Mesh>(MarkingsMeshPath) ?? throw new InvalidOperationException("Missing " + MarkingsMeshPath);
            var white = AssetDatabase.LoadAssetAtPath<Material>(MaterialFolder + "ST_Paint_White.mat") ?? throw new InvalidOperationException("Missing white paint.");
            var yellow = AssetDatabase.LoadAssetAtPath<Material>(MaterialFolder + "ST_Paint_Yellow.mat") ?? throw new InvalidOperationException("Missing yellow paint.");
            var contents = PrefabUtility.LoadPrefabContents(MapPrefabPath);
            try
            {
                UseGround(contents.transform, ground);
                MoveLamps(contents.transform);
                if (contents.transform.Find(MarkingsName) == null)
                {
                    var paint = new GameObject(MarkingsName);
                    paint.transform.SetParent(contents.transform, false);
                    paint.AddComponent<MeshFilter>().sharedMesh = markings;
                    var renderer = paint.AddComponent<MeshRenderer>();
                    renderer.sharedMaterials = new[] { white, yellow };
                    renderer.shadowCastingMode = ShadowCastingMode.Off;
                }
                PrefabUtility.SaveAsPrefabAsset(contents, MapPrefabPath, out bool saved);
                if (!saved) throw new InvalidOperationException("Could not save " + MapPrefabPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
            // The scene instance carries its own overrides of these same values, so they follow by hand.
            var scene = OpenScene();
            var map = Root(scene, "MyeongjiMap").transform;
            UseGround(map, ground);
            MoveLamps(map);
            Save(scene);
            var floor = map.Find("Ground").GetComponentInChildren<MeshCollider>();
            Debug.Log($"[MapExpansion] Ground: {floor.sharedMesh.name}, bounds {floor.bounds.min} to {floor.bounds.max}; paint {markings.name}; lamps moved {MovedLamps.Count}.");
        }

        private static void UseGround(Transform map, Mesh mesh)
        {
            var ground = map.Find("Ground") ?? throw new InvalidOperationException("Ground missing.");
            var filter = ground.GetComponentInChildren<MeshFilter>();
            var collider = ground.GetComponentInChildren<MeshCollider>();
            var renderer = ground.GetComponentInChildren<MeshRenderer>();
            if (renderer.sharedMaterials.Length != mesh.subMeshCount) throw new InvalidOperationException($"Ground has {renderer.sharedMaterials.Length} materials, the mesh {mesh.subMeshCount} slots.");
            filter.sharedMesh = mesh;
            collider.sharedMesh = mesh;
        }

        // The lamp meshes are baked in map space, so a move is the same world offset for the model and its post collider.
        private static void MoveLamps(Transform map)
        {
            var furniture = map.Find("Furniture");
            foreach (var move in MovedLamps)
            {
                var post = furniture.Find(move.Key + "_PostCollider") ?? throw new InvalidOperationException("Missing post of " + move.Key);
                var model = furniture.Cast<Transform>().Single(t => t.name.StartsWith(move.Key + "."));
                var delta = new Vector3(move.Value.x - post.position.x, 0f, move.Value.y - post.position.z);
                post.position += delta;
                model.position += delta;
            }
        }

        public static void ApplyExpansionObjects()
        {
            var scene = OpenScene();
            var map = Root(scene, "MyeongjiMap").transform;
            foreach (var old in scene.GetRootGameObjects().Where(g => g.name == ExpansionRootName).ToArray()) UnityEngine.Object.DestroyImmediate(old);
            var root = new GameObject(ExpansionRootName);
            var layout = ReadJson("layout.json");
            var sources = ((JArray)layout["lots"]).ToDictionary(l => (string)l[0], l => (Heading: (float)l[7], Building: FindBuilding(map, (string)l[0])));
            var used = new Dictionary<string, int>();
            int placed = 0, skipped = 0;
            foreach (var lot in (JArray)ReadJson("lots_expanded.json")["lots"])
            {
                if (ExcludedLots.Contains((string)lot["id"])) { skipped++; continue; }
                var zone = (string)lot["zone"];
                var group = root.transform.Find("Buildings_" + zone);
                if (group == null)
                {
                    group = new GameObject("Buildings_" + zone).transform;
                    group.SetParent(root.transform, false);
                }
                var source = sources[(string)lot["sourceBuildingId"]];
                var clone = UnityEngine.Object.Instantiate(source.Building.gameObject, group, true);
                clone.name = (string)lot["id"];
                var center = BoundsOf(source.Building.gameObject).center;
                var yaw = Quaternion.Euler(0f, -((float)lot["rotationDeg"] - source.Heading), 0f);
                var target = new Vector3((float)lot["x"], center.y, (float)lot["z"]);
                clone.transform.SetPositionAndRotation(target + yaw * (source.Building.position - center), yaw * source.Building.rotation);
                used[zone] = used.TryGetValue(zone, out int count) ? count + 1 : 1;
                PaintWall(clone, ZoneWalls[zone][used[zone] % ZoneWalls[zone].Length]);
                // Clones share their meshes; static batching would copy every one of them into the build.
                GameObjectUtility.SetStaticEditorFlags(clone, GameObjectUtility.GetStaticEditorFlags(clone) & ~StaticEditorFlags.BatchingStatic);
                placed++;
            }
            int lamps = AddLamps(map, root.transform);
            Physics.SyncTransforms();
            PrefabUtility.SaveAsPrefabAssetAndConnect(root, ExpansionPrefabPath, InteractionMode.AutomatedAction);
            Save(scene);
            Debug.Log($"[MapExpansion] Expansion: {placed} buildings, {skipped} left out, {lamps} lamps.");
        }

        private static Transform FindBuilding(Transform map, string id)
        {
            var found = new List<Transform>();
            foreach (Transform group in map)
                if (group.name.StartsWith("Buildings"))
                    foreach (Transform building in group)
                        if (building.name.Contains("_" + id + ".")) found.Add(building);
            if (found.Count != 1) throw new InvalidOperationException($"Building {id}: found {found.Count}.");
            return found[0];
        }

        private static Bounds BoundsOf(GameObject building)
        {
            var renderers = building.GetComponentsInChildren<Renderer>(true);
            var bounds = renderers[0].bounds;
            foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
            return bounds;
        }

        // Only this instance's first wall slot changes; the shared materials stay as they are.
        private static void PaintWall(GameObject building, string color)
        {
            var renderer = building.GetComponent<Renderer>();
            var materials = renderer.sharedMaterials;
            for (int i = 0; i < materials.Length; i++)
            {
                string family = materials[i] == null ? null : materials[i].name.StartsWith("KIT_DistrictWall_") ? "KIT_DistrictWall_" : materials[i].name.StartsWith("KIT_Wall_") ? "KIT_Wall_" : null;
                if (family == null) continue;
                materials[i] = AssetDatabase.LoadAssetAtPath<Material>(MaterialFolder + family + color + ".mat") ?? throw new InvalidOperationException($"Missing {family}{color}");
                renderer.sharedMaterials = materials;
                return;
            }
        }

        private static int AddLamps(Transform map, Transform root)
        {
            var furniture = map.Find("Furniture");
            var post = furniture.Find(SourceLamp + "_PostCollider") ?? throw new InvalidOperationException("Missing source lamp post.");
            var model = furniture.Cast<Transform>().Single(t => t.name.StartsWith(SourceLamp + "."));
            var group = new GameObject("Lamps").transform;
            group.SetParent(root, false);
            // The source lamp's arm points at the road (-Z, heading 0) from its post at y = 0.
            var pivot = new Vector3(post.position.x, 0f, post.position.z);
            var lamps = (JArray)ReadJson("furniture_expanded.json")["lamps"];
            int index = 0;
            foreach (var lamp in lamps)
            {
                index++;
                var yaw = Quaternion.Euler(0f, -(float)lamp["rotationDeg"], 0f);
                var at = new Vector3((float)lamp["x"], 0f, (float)lamp["z"]);
                if (CorrectedLamps.TryGetValue(index, out var corrected)) at = new Vector3(corrected.x, 0f, corrected.y);
                var clone = UnityEngine.Object.Instantiate(model.gameObject, group, true);
                clone.name = $"ExpandedLamp_{index:000}";
                clone.transform.SetPositionAndRotation(at + yaw * (model.position - pivot), yaw * model.rotation);
                var box = UnityEngine.Object.Instantiate(post.gameObject, group, true);
                box.name = clone.name + "_PostCollider";
                box.transform.SetPositionAndRotation(new Vector3(at.x, post.position.y, at.z), yaw * post.rotation);
            }
            return lamps.Count;
        }

        // Read-only: compares what stands in the scene with the plan. Hard failures throw; the rest is logged for the report.
        public static void CheckExpansion()
        {
            var scene = OpenScene();
            Physics.SyncTransforms();
            var barriers = Root(scene, BarrierRootName);
            barriers.SetActive(false);
            var map = Root(scene, "MyeongjiMap").transform;
            var root = Root(scene, ExpansionRootName).transform;
            var floor = map.Find("Ground").GetComponentInChildren<MeshCollider>();
            var failures = new List<string>();
            var notes = new List<string>();
            if (floor.sharedMesh.name != "Ground_Expanded") failures.Add("floor is " + floor.sharedMesh.name);
            var lots = ((JArray)ReadJson("lots_expanded.json")["lots"]).Where(l => !ExcludedLots.Contains((string)l["id"])).ToList();
            var anchors = ((JArray)ReadJson("anchors_expanded.json")).ToDictionary(a => (string)a["name"]);
            var locations = Root(scene, "MyeongjiLocations").transform;
            var clones = new Dictionary<string, (Bounds bounds, JToken lot)>();
            float worst = 0f;
            foreach (var lot in lots)
            {
                var clone = root.Cast<Transform>().Where(t => t.name.StartsWith("Buildings_")).Select(g => g.Find((string)lot["id"])).FirstOrDefault(t => t != null);
                if (clone == null) { failures.Add("no building " + lot["id"]); continue; }
                var bounds = BoundsOf(clone.gameObject);
                clones[(string)lot["id"]] = (bounds, lot);
                float offset = Vector2.Distance(new Vector2(bounds.center.x, bounds.center.z), new Vector2((float)lot["x"], (float)lot["z"]));
                worst = Mathf.Max(worst, offset);
                if (offset > 0.05f) failures.Add($"{lot["id"]} centre off by {offset:F2}");
                foreach (var corner in Corners(bounds))
                    if (!floor.Raycast(new Ray(new Vector3(corner.x, 30f, corner.y), Vector3.down), out _, 60f)) { notes.Add($"{lot["id"]} corner off the floor at {corner}"); break; }
            }
            // Real outlines, old and new, against each other.
            var outlines = clones.Select(c => (id: c.Key, bounds: c.Value.bounds)).ToList();
            foreach (Transform group in map)
                if (group.name.StartsWith("Buildings"))
                    foreach (Transform building in group) outlines.Add((building.name, BoundsOf(building.gameObject)));
            int overlaps = 0;
            for (int a = 0; a < outlines.Count; a++)
                for (int b = a + 1; b < outlines.Count; b++)
                {
                    float x = Mathf.Min(outlines[a].bounds.max.x, outlines[b].bounds.max.x) - Mathf.Max(outlines[a].bounds.min.x, outlines[b].bounds.min.x);
                    float z = Mathf.Min(outlines[a].bounds.max.z, outlines[b].bounds.max.z) - Mathf.Max(outlines[a].bounds.min.z, outlines[b].bounds.min.z);
                    if (x <= 0.01f || z <= 0.01f) continue;
                    if (!clones.ContainsKey(outlines[a].id) && !clones.ContainsKey(outlines[b].id)) continue; // original neighbours stay as they were
                    overlaps++;
                    notes.Add($"overlap {outlines[a].id} / {outlines[b].id}: {x:F2} x {z:F2} m");
                }
            // Roads: real outlines must stay off every carriageway.
            var network = Root(scene, "MyeongjiRoutes").GetComponent<RoadNetwork>();
            var roads = network.Roads.ToList();
            int onRoad = 0;
            foreach (var clone in clones)
                foreach (var road in roads)
                {
                    int samples = Mathf.Max(2, Mathf.CeilToInt(road.Container.CalculateLength()));
                    for (int i = 0; i <= samples; i++)
                    {
                        Vector3 p = road.Container.EvaluatePosition(i / (float)samples);
                        var b = clone.Value.bounds;
                        float dx = Mathf.Max(b.min.x - p.x, 0f, p.x - b.max.x), dz = Mathf.Max(b.min.z - p.z, 0f, p.z - b.max.z);
                        if (dx * dx + dz * dz < road.HalfWidth * road.HalfWidth) { onRoad++; notes.Add($"{clone.Key} on road {road.name}"); i = samples; break; }
                    }
                }
            // Every planned anchor must have its building in front of it, facing it, with a free spot to stand.
            float minGap = float.PositiveInfinity, maxGap = 0f;
            int faced = 0;
            foreach (var clone in clones.Where(c => c.Value.lot["facesAnchor"].Type == JTokenType.String))
            {
                var anchor = anchors[(string)clone.Value.lot["facesAnchor"]];
                var position = locations.Find((string)anchor["name"]).position;
                float heading = (float)clone.Value.lot["rotationDeg"] * Mathf.Deg2Rad;
                var front = new Vector2(Mathf.Sin(heading), -Mathf.Cos(heading));
                var forward = (JArray)anchor["forward"];
                // The anchor faces the road, the same way the front does.
                if (Vector2.Dot(front, new Vector2((float)forward[0], (float)forward[2])) < 0.99f) failures.Add($"{clone.Key} does not face {anchor["name"]}");
                var b = clone.Value.bounds;
                float gap = front.x > 0.5f ? position.x - b.max.x : front.x < -0.5f ? b.min.x - position.x : front.y > 0.5f ? position.z - b.max.z : b.min.z - position.z;
                float lateral = Mathf.Abs(front.x) > 0.5f ? Mathf.Abs(position.z - b.center.z) - b.extents.z : Mathf.Abs(position.x - b.center.x) - b.extents.x;
                if (gap < 0f) failures.Add($"{anchor["name"]} is {-gap:F2} m inside {clone.Key}");
                if (lateral > 0.01f) notes.Add($"{anchor["name"]} {lateral:F2} m beyond the front of {clone.Key}");
                minGap = Mathf.Min(minGap, gap);
                maxGap = Mathf.Max(maxGap, gap);
                faced++;
            }
            int blocked = 0;
            foreach (var anchor in anchors.Values)
            {
                var point = locations.Find((string)anchor["name"]).position;
                if (!floor.Raycast(new Ray(new Vector3(point.x, 30f, point.z), Vector3.down), out var hit, 60f)) { failures.Add($"{anchor["name"]} has no floor"); continue; }
                var surface = SurfaceName(floor, hit.triangleIndex);
                if (!surface.Contains("Pavement") && !surface.Contains("Plot")) notes.Add($"{anchor["name"]} stands on {surface}");
                var solid = Physics.OverlapCapsule(hit.point + Vector3.up * 0.5f, hit.point + Vector3.up * 1.5f, 0.35f, ~0, QueryTriggerInteraction.Ignore)
                    .Where(c => c != floor && c.attachedRigidbody == null).Select(c => c.name).ToArray();
                if (solid.Length > 0) { blocked++; notes.Add($"{anchor["name"]} blocked by {string.Join(",", solid)}"); }
            }
            // Every lot's planned access line must be clear of real solids.
            int noAccess = 0;
            foreach (var path in (JArray)ReadJson("lots_expanded.json")["accessPaths"])
            {
                if (!clones.TryGetValue((string)path["lotId"], out var clone)) continue;
                var from = new Vector2((float)path["start"][0], (float)path["start"][1]);
                var to = new Vector2((float)path["end"][0], (float)path["end"][1]);
                var building = root.Cast<Transform>().Select(g => g.Find((string)path["lotId"])).First(t => t != null).GetComponent<Collider>();
                int steps = Mathf.Max(2, Mathf.CeilToInt(Vector2.Distance(from, to) * 2f));
                for (int i = 0; i <= steps; i++)
                {
                    var flat = Vector2.Lerp(from, to, i / (float)steps);
                    var feet = new Vector3(flat.x, 0f, flat.y);
                    var solid = Physics.OverlapCapsule(feet + Vector3.up * 0.5f, feet + Vector3.up * 1.5f, 0.3f, ~0, QueryTriggerInteraction.Ignore)
                        .Where(c => c != floor && c != building && c.attachedRigidbody == null).Select(c => c.name).ToArray();
                    if (solid.Length == 0) continue;
                    noAccess++;
                    notes.Add($"{path["lotId"]} access blocked by {string.Join(",", solid)} at {flat}");
                    break;
                }
            }
            int lampsOnRoad = 0, lampCount = 0, lampsInBuilding = 0;
            foreach (var post in root.GetComponentsInChildren<BoxCollider>(true))
            {
                lampCount++;
                var flat = new Vector3(post.transform.position.x, 0f, post.transform.position.z);
                if (roads.Any(r => r.DistanceToCentre(flat) < r.HalfWidth)) { lampsOnRoad++; notes.Add($"{post.name} on a road"); }
                if (outlines.Any(o => o.bounds.min.x < flat.x + 0.2f && o.bounds.max.x > flat.x - 0.2f && o.bounds.min.z < flat.z + 0.2f && o.bounds.max.z > flat.z - 0.2f)) { lampsInBuilding++; notes.Add($"{post.name} inside a building"); }
            }
            foreach (var move in MovedLamps)
            {
                var post = map.Find("Furniture/" + move.Key + "_PostCollider");
                if (Vector2.Distance(new Vector2(post.position.x, post.position.z), move.Value) > 0.01f) failures.Add(move.Key + " not moved");
                var flat = new Vector3(post.position.x, 0f, post.position.z);
                if (roads.Any(r => r.DistanceToCentre(flat) < r.HalfWidth)) failures.Add(move.Key + " still on a road");
            }
            barriers.SetActive(true);
            foreach (var note in notes) Debug.Log("[MapExpansion] Note: " + note);
            Debug.Log($"[MapExpansion] Expansion check: {clones.Count} buildings, centre off by at most {worst:F3} m, {overlaps} overlaps, {onRoad} on roads, {faced} anchors faced (front gap {minGap:F2}..{maxGap:F2} m), {blocked} anchors blocked, {noAccess} access lines blocked, {lampCount} lamp posts ({lampsOnRoad} on roads, {lampsInBuilding} in buildings).");
            if (failures.Count > 0) throw new InvalidOperationException("[MapExpansion] Expansion check failed: " + string.Join("; ", failures));
        }

        private static IEnumerable<Vector2> Corners(Bounds b)
        {
            yield return new Vector2(b.min.x, b.min.z);
            yield return new Vector2(b.min.x, b.max.z);
            yield return new Vector2(b.max.x, b.min.z);
            yield return new Vector2(b.max.x, b.max.z);
        }

        private static string SurfaceName(MeshCollider floor, int triangle)
        {
            var mesh = floor.sharedMesh;
            int sum = 0;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                sum += (int)mesh.GetIndexCount(s) / 3;
                if (triangle < sum) return floor.GetComponent<Renderer>().sharedMaterials[s].name;
            }
            return "none";
        }

        // Editor renders for the report: no Play mode and nothing saved. Fog is off so the far blocks read.
        public static void RenderViews()
        {
            var scene = OpenScene();
            string folder = "C:/UnityWork/ge/공유/결과/맵조립/";
            Directory.CreateDirectory(folder);
            bool fog = RenderSettings.fog;
            RenderSettings.fog = false;
            var host = new GameObject("ReviewCamera");
            var camera = host.AddComponent<Camera>();
            camera.farClipPlane = 900f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.8f, 0.86f, 0.86f);
            void Shot(string name, Vector3 from, Vector3 at, bool top = false)
            {
                camera.orthographic = top;
                camera.orthographicSize = 125f;
                camera.fieldOfView = 50f;
                host.transform.position = from;
                host.transform.LookAt(at);
                var texture = new RenderTexture(1920, 1200, 24);
                camera.targetTexture = texture;
                camera.Render();
                RenderTexture.active = texture;
                var image = new Texture2D(1920, 1200, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, 1920, 1200), 0, 0);
                image.Apply();
                File.WriteAllBytes(folder + name + ".png", image.EncodeToPNG());
                RenderTexture.active = null;
                camera.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(texture);
                UnityEngine.Object.DestroyImmediate(image);
            }
            Shot("부감", new Vector3(55f, 300f, -32.5f), new Vector3(55f, 0f, -32.4f), true);
            Shot("A구역_북서", new Vector3(-120f, 70f, 110f), new Vector3(-20f, 0f, 20f));
            Shot("B구역_북동", new Vector3(230f, 70f, 110f), new Vector3(110f, 0f, 30f));
            Shot("C구역_남서", new Vector3(-120f, 70f, -180f), new Vector3(-20f, 0f, -100f));
            Shot("D구역_남동", new Vector3(230f, 70f, -180f), new Vector3(130f, 0f, -90f));
            Shot("이음부_큰길_서쪽", new Vector3(-30f, 14f, 22f), new Vector3(6f, 1f, -2f));
            Shot("이음부_남쪽", new Vector3(30f, 16f, -125f), new Vector3(30f, 0f, -96f));
            Shot("거리_A구역", new Vector3(-70f, 2f, -2f), new Vector3(-30f, 3f, 0f));
            Shot("거리_D구역", new Vector3(155f, 2f, -60f), new Vector3(155f, 3f, -95f));
            Shot("거리_B구역", new Vector3(30f, 2f, 49f), new Vector3(70f, 3f, 48f));
            Shot("거리_C구역", new Vector3(-30f, 2f, -142f), new Vector3(10f, 3f, -141f));
            UnityEngine.Object.DestroyImmediate(host);
            RenderSettings.fog = fog;
            Debug.Log("[MapExpansion] Views rendered to " + folder);
        }
    }
}
