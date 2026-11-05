using System;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DeliveryRider.Editor
{
    public static class FallingObjectInstaller
    {
        [MenuItem("Delivery Rider/Falling Object/Build Prefab")]
        public static void Build()
        {
            if (EditorApplication.isPlaying) throw new InvalidOperationException("Stop Play Mode before building the prefab.");
            var preview = EditorSceneManager.NewPreviewScene();
            var root = new GameObject("FallingObject");
            SceneManager.MoveGameObjectToScene(root, preview);
            root.SetActive(false);
            try
            {
                var dark = Material("Materials/PigeonFlockEye");
                var beak = Material("FourDistrict/BeakOrange");
                var brown = Material("Materials/House0");
                var green = Material("Materials/Leaves");
                var warning = Material("FourDistrict/HazardWarning");
                var crow = new GameObject("Crow").transform;
                crow.SetParent(root.transform, false);
                crow.localPosition = Vector3.up * 6f;
                Shape(crow, "Body", PrimitiveType.Sphere, Vector3.zero, new Vector3(.65f, .45f, .85f), dark);
                Shape(crow, "Head", PrimitiveType.Sphere, new Vector3(0f, .26f, .35f), Vector3.one * .38f, dark);
                Shape(crow, "Beak", PrimitiveType.Cube, new Vector3(0f, .22f, .6f), new Vector3(.14f, .12f, .35f), beak);
                Shape(crow, "LeftWing", PrimitiveType.Sphere, new Vector3(-.48f, .05f, -.05f), new Vector3(.7f, .1f, .5f), dark);
                Shape(crow, "RightWing", PrimitiveType.Sphere, new Vector3(.48f, .05f, -.05f), new Vector3(.7f, .1f, .5f), dark);
                var falling = new GameObject("FallingPot").transform;
                falling.SetParent(root.transform, false);
                Shape(falling, "Pot", PrimitiveType.Cylinder, Vector3.zero, new Vector3(.55f, .22f, .55f), brown);
                Shape(falling, "Plant", PrimitiveType.Sphere, Vector3.up * .3f, new Vector3(.6f, .35f, .6f), green);
                var marker = Shape(root.transform, "AimMarker", PrimitiveType.Cylinder, Vector3.up * .025f, new Vector3(3.4f, .02f, 3.4f), warning);
                var cueObject = new GameObject("Cue", typeof(TextMeshPro));
                cueObject.transform.SetParent(root.transform, false);
                var cue = cueObject.GetComponent<TextMeshPro>();
                cue.font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/_Game/Art/Fonts/PrototypeKoreanRegular.asset");
                cue.fontSize = 4f;
                cue.alignment = TextAlignmentOptions.Center;
                cue.color = new Color(1f, .8f, .15f);
                cue.text = "까악!";
                cue.rectTransform.sizeDelta = new Vector2(5f, 2f);
                var fragments = new Transform[12];
                for (int i = 0; i < fragments.Length; i++)
                    fragments[i] = Shape(root.transform, "Fragment_" + i, PrimitiveType.Cube, Vector3.zero,
                        Vector3.one * .14f, i % 2 == 0 ? brown : green);
                var hazard = root.AddComponent<FallingObjectHazard>();
                var data = new SerializedObject(hazard);
                data.FindProperty("crow").objectReferenceValue = crow;
                data.FindProperty("fallingObject").objectReferenceValue = falling;
                data.FindProperty("aimMarker").objectReferenceValue = marker;
                data.FindProperty("cue").objectReferenceValue = cue;
                var array = data.FindProperty("fragments");
                array.arraySize = fragments.Length;
                for (int i = 0; i < fragments.Length; i++) array.GetArrayElementAtIndex(i).objectReferenceValue = fragments[i];
                data.ApplyModifiedPropertiesWithoutUndo();
                marker.gameObject.SetActive(false);
                falling.gameObject.SetActive(false);
                cueObject.SetActive(false);
                foreach (var fragment in fragments) fragment.gameObject.SetActive(false);
                root.SetActive(true);
                PrefabUtility.SaveAsPrefabAsset(root, FallingObjectChecks.PrefabPath);
                AssetDatabase.SaveAssets();
                Debug.Log("FallingObjectInstaller: built " + FallingObjectChecks.PrefabPath + " GUID=" + AssetDatabase.AssetPathToGUID(FallingObjectChecks.PrefabPath));
            }
            finally { EditorSceneManager.ClosePreviewScene(preview); }
        }

        [MenuItem("Delivery Rider/Falling Object/Place Test Near Rider")]
        public static void PlaceTest()
        {
            var session = UnityEngine.Object.FindFirstObjectByType<DeliverySession>();
            var network = UnityEngine.Object.FindFirstObjectByType<RoadNetwork>();
            if (session == null || session.Player == null || network == null) throw new InvalidOperationException("Open the game scene first.");
            var existing = GameObject.Find("FallingObject_Test");
            if (existing != null) { Selection.activeGameObject = existing; return; }
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(FallingObjectChecks.PrefabPath);
            if (prefab == null) throw new InvalidOperationException("Run Build Prefab first.");
            var staging = new GameObject("FallingObject staging");
            staging.SetActive(false);
            var instance = UnityEngine.Object.Instantiate(prefab, staging.transform);
            instance.name = "FallingObject_Test";
            instance.transform.position = network.OnGround(session.Player.position + session.Player.forward * 4f, 0f);
            instance.GetComponent<FallingObjectHazard>().Place(session, network, new HazardCandidate());
            instance.AddComponent<HazardSlot>().Configure("falling", HazardRole.Trigger, 1, Array.Empty<int>());
            instance.transform.SetParent(null, true);
            UnityEngine.Object.DestroyImmediate(staging);
            if (!EditorApplication.isPlaying) Undo.RegisterCreatedObjectUndo(instance, "Place test falling hazard");
            Selection.activeGameObject = instance;
        }

        private static Material Material(string name)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>("Assets/_Game/Art/" + name + ".mat");
            if (material == null) throw new InvalidOperationException("Missing existing material: " + name);
            return material;
        }

        private static Transform Shape(Transform parent, string name, PrimitiveType type, Vector3 position, Vector3 scale, Material material)
        {
            var shape = GameObject.CreatePrimitive(type);
            shape.name = name;
            shape.transform.SetParent(parent, false);
            shape.transform.localPosition = position;
            shape.transform.localScale = scale;
            UnityEngine.Object.DestroyImmediate(shape.GetComponent<Collider>());
            shape.GetComponent<Renderer>().sharedMaterial = material;
            return shape.transform;
        }
    }
}
