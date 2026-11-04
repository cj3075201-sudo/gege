using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DeliveryRider.Editor
{
    // Zone walls and barricades. Walls follow every shared zone border, because walkers can cross the open plots between
    // buildings; barricades only mark where a road meets a wall. The barrier layer collides with nothing except the rider,
    // whose capsule asks for it, so cars and walkers keep moving through closed zones.
    public static partial class MyeongjiExpansionInstaller
    {
        private const string BarrierRootName = "ZoneBarriers";
        private const string BarrierLayerName = "ZoneBarrier";
        private const string PedestrianPrefabPath = "Assets/_Game/Prefabs/Traffic/Pedestrian.prefab";
        private const string BarricadeBarMaterial = "Assets/_Game/Art/Myeongji/Materials/ST_Paint_Yellow.mat";
        private const string BarricadePostMaterial = "Assets/_Game/Art/Myeongji/Materials/ST_Pole.mat";
        private const float WallHeight = 8f, WallThickness = 0.6f, WallBottom = -0.5f;
        private const float BarricadeHeight = 1.1f, BarThickness = 0.12f, PostThickness = 0.14f, BarricadeBase = -0.14f;

        public static void ApplyBarriers()
        {
            var scene = OpenScene();
            int layer = EnsureBarrierLayer();
            ExcludeFromPedestrianObstacles(layer);
            Root(scene, "Player").GetComponent<CapsuleCollider>().includeLayers |= 1 << layer;
            foreach (var old in scene.GetRootGameObjects().Where(g => g.name == BarrierRootName).ToArray()) UnityEngine.Object.DestroyImmediate(old);
            var zones = ReadJson("zones.json");
            var session = Root(scene, "DeliverySession").GetComponent<DeliverySession>();
            var bar = AssetDatabase.LoadAssetAtPath<Material>(BarricadeBarMaterial) ?? throw new InvalidOperationException("Missing " + BarricadeBarMaterial);
            var post = AssetDatabase.LoadAssetAtPath<Material>(BarricadePostMaterial) ?? throw new InvalidOperationException("Missing " + BarricadePostMaterial);
            var root = new GameObject(BarrierRootName);
            var groups = new Dictionary<string, Transform>();
            Transform Group(JToken pair)
            {
                string key = (string)pair[0] + (string)pair[1];
                if (groups.TryGetValue(key, out var found)) return found;
                var go = new GameObject("Barrier_" + key);
                go.transform.SetParent(root.transform, false);
                var barrier = go.AddComponent<ZoneBarrier>();
                var data = new SerializedObject(barrier);
                data.FindProperty("session").objectReferenceValue = session;
                var required = data.FindProperty("requiredZones");
                required.arraySize = 2;
                required.GetArrayElementAtIndex(0).stringValue = (string)pair[0];
                required.GetArrayElementAtIndex(1).stringValue = (string)pair[1];
                data.ApplyModifiedPropertiesWithoutUndo();
                return groups[key] = go.transform;
            }
            int walls = 0, barricades = 0;
            foreach (var border in (JArray)zones["borders"]) { AddWall(Group(border["zones"]), Flat(border["from"]), Flat(border["to"]), layer, walls++); }
            foreach (var spot in (JArray)zones["barriers"]) { AddBarricade(Group(spot["zones"]), (string)spot["road"], Flat(spot["position"]), Flat(spot["roadDirection"]), (float)spot["span"], bar, post); barricades++; }
            Save(scene);
            AssetDatabase.SaveAssets();
            Debug.Log($"[MapExpansion] Barriers: {groups.Count} groups, {walls} walls, {barricades} barricades, layer {layer}.");
        }

        private static int EnsureBarrierLayer()
        {
            var manager = new SerializedObject(AssetDatabase.LoadMainAssetAtPath("ProjectSettings/TagManager.asset"));
            var layers = manager.FindProperty("layers");
            int layer = -1;
            for (int i = 8; i < layers.arraySize && layer < 0; i++) if (layers.GetArrayElementAtIndex(i).stringValue == BarrierLayerName) layer = i;
            for (int i = 8; i < layers.arraySize && layer < 0; i++)
                if (string.IsNullOrEmpty(layers.GetArrayElementAtIndex(i).stringValue))
                {
                    layers.GetArrayElementAtIndex(i).stringValue = BarrierLayerName;
                    manager.ApplyModifiedPropertiesWithoutUndo();
                    layer = i;
                }
            if (layer < 0) throw new InvalidOperationException("No free layer.");
            for (int i = 0; i < 32; i++) Physics.IgnoreLayerCollision(layer, i, true);
            EditorUtility.SetDirty(AssetDatabase.LoadMainAssetAtPath("ProjectSettings/DynamicsManager.asset"));
            return layer;
        }

        // Walkers steer by casting against their obstacle mask, so without this a closed-zone wall would stop them.
        private static void ExcludeFromPedestrianObstacles(int layer)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PedestrianPrefabPath) ?? throw new InvalidOperationException("Missing " + PedestrianPrefabPath);
            var data = new SerializedObject(prefab.GetComponent<TrafficPedestrian>());
            var mask = data.FindProperty("obstacleMask");
            mask.intValue &= ~(1 << layer);
            data.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(prefab);
        }

        private static void AddWall(Transform parent, Vector2 a, Vector2 b, int layer, int index)
        {
            var wall = new GameObject("Wall_" + index) { layer = layer };
            wall.transform.SetParent(parent, false);
            var middle = (a + b) * 0.5f;
            var along = b - a;
            wall.transform.SetPositionAndRotation(new Vector3(middle.x, WallBottom + WallHeight * 0.5f, middle.y), Quaternion.LookRotation(new Vector3(along.x, 0f, along.y)));
            wall.AddComponent<BoxCollider>().size = new Vector3(WallThickness, WallHeight, along.magnitude);
        }

        private static void AddBarricade(Transform parent, string road, Vector2 position, Vector2 direction, float span, Material bar, Material post)
        {
            var barricade = new GameObject("Barricade_" + road);
            barricade.transform.SetParent(parent, false);
            barricade.transform.SetPositionAndRotation(new Vector3(position.x, BarricadeBase, position.y), Quaternion.LookRotation(new Vector3(direction.x, 0f, direction.y)));
            foreach (float height in new[] { BarricadeHeight * 0.55f, BarricadeHeight * 0.9f })
                AddPart(barricade.transform, "Bar", new Vector3(0f, height, 0f), new Vector3(span, BarThickness, BarThickness), bar);
            foreach (float x in new[] { -span * 0.5f, 0f, span * 0.5f })
                AddPart(barricade.transform, "Post", new Vector3(x, BarricadeHeight * 0.5f, 0f), new Vector3(PostThickness, BarricadeHeight, PostThickness), post);
        }

        private static void AddPart(Transform parent, string name, Vector3 local, Vector3 size, Material material)
        {
            var part = GameObject.CreatePrimitive(PrimitiveType.Cube);
            part.name = name;
            UnityEngine.Object.DestroyImmediate(part.GetComponent<Collider>());
            part.transform.SetParent(parent, false);
            part.transform.SetLocalPositionAndRotation(local, Quaternion.identity);
            part.transform.localScale = size;
            part.GetComponent<MeshRenderer>().sharedMaterial = material;
        }

        // Pushes throw-away capsules at a real wall: only the one built like the rider may stop.
        public static void CheckBarriers()
        {
            var scene = OpenScene();
            int layer = LayerMask.NameToLayer(BarrierLayerName);
            if (layer < 0) throw new InvalidOperationException("Barrier layer missing.");
            var failures = new List<string>();
            for (int i = 0; i < 32; i++) if (!Physics.GetIgnoreLayerCollision(layer, i)) failures.Add("layer collides with " + i);
            var player = Root(scene, "Player").GetComponent<CapsuleCollider>();
            if ((player.includeLayers.value & (1 << layer)) == 0) failures.Add("rider ignores the barrier layer");
            var walker = AssetDatabase.LoadAssetAtPath<GameObject>(PedestrianPrefabPath).GetComponent<TrafficPedestrian>();
            if ((new SerializedObject(walker).FindProperty("obstacleMask").intValue & (1 << layer)) != 0) failures.Add("walkers treat the barrier as an obstacle");
            var zones = ReadJson("zones.json");
            var root = Root(scene, BarrierRootName);
            var barriers = root.GetComponentsInChildren<ZoneBarrier>(true);
            var walls = root.GetComponentsInChildren<BoxCollider>(true);
            int borders = ((JArray)zones["borders"]).Count, spots = ((JArray)zones["barriers"]).Count;
            if (walls.Length != borders) failures.Add($"walls {walls.Length}/{borders}");
            if (walls.Any(w => w.gameObject.layer != layer)) failures.Add("wall on another layer");
            if (root.GetComponentsInChildren<Transform>(true).Count(t => t.name.StartsWith("Barricade_")) != spots) failures.Add("barricade count");
            foreach (var barrier in barriers)
            {
                var required = new SerializedObject(barrier).FindProperty("requiredZones");
                string pair = barrier.name.Substring("Barrier_".Length);
                if (required.arraySize != 2 || required.GetArrayElementAtIndex(0).stringValue != pair.Substring(0, 1) || required.GetArrayElementAtIndex(1).stringValue != pair.Substring(1, 1))
                    failures.Add("required zones " + barrier.name);
            }
            float total = walls.Sum(w => w.size.z);
            var mode = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;
            float rider = Push(layer, true), other = Push(layer, false);
            Physics.simulationMode = mode;
            const float wallX = 31f;
            if (rider > wallX - 0.2f) failures.Add($"rider-like capsule reached x={rider:F2}");
            if (other < wallX + 1f) failures.Add($"walker-like capsule stopped at x={other:F2}");
            if (failures.Count > 0) throw new InvalidOperationException("[MapExpansion] Barrier check failed: " + string.Join("; ", failures));
            Debug.Log($"[MapExpansion] Barrier check passed: {barriers.Length} groups, {walls.Length} walls {total:F0} m, {spots} barricades; rider-like stops at x={rider:F2}, walker-like reaches x={other:F2}.");
        }

        private static float Push(int barrierLayer, bool likeRider)
        {
            var capsule = new GameObject("BarrierProbe");
            capsule.transform.position = new Vector3(27f, 3f, 25f);
            var collider = capsule.AddComponent<CapsuleCollider>();
            collider.radius = 0.4f;
            collider.height = 1.8f;
            if (likeRider) collider.includeLayers = 1 << barrierLayer;
            var body = capsule.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.constraints = RigidbodyConstraints.FreezeRotation;
            body.linearVelocity = new Vector3(8f, 0f, 0f);
            Physics.SyncTransforms();
            for (int i = 0; i < 100; i++) Physics.Simulate(0.02f);
            float x = body.position.x;
            UnityEngine.Object.DestroyImmediate(capsule);
            return x;
        }
    }
}
