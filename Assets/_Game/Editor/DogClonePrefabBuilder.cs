using System;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DeliveryRider.Editor
{
    // Explicit prefab-only builder. Never registers kinds, candidates or changes a saved game scene.
    public static class DogClonePrefabBuilder
    {
        public const string Folder = "Assets/_Game/Prefabs/Hazards/";

        [MenuItem("Delivery Rider/Build Dog and Clone Prefabs")]
        public static void Run()
        {
            BuildDog(); BuildClone(); AssetDatabase.SaveAssets();
            DogCloneChecks.Run();
            Screenshot();
            Debug.Log("DOG_CLONE_PREFABS_PASS dog=" + AssetDatabase.AssetPathToGUID(Folder + "Dog.prefab")
                + " clone=" + AssetDatabase.AssetPathToGUID(Folder + "CloneRider.prefab"));
        }

        private static void BuildDog()
        {
            var root = new GameObject("Dog");
            try
            {
                var dog = Group("DogBody", root.transform); dog.localScale = Vector3.one * 1.35f;
                Material brown = Mat("Materials/Wood"), cream = Mat("Materials/Skin"), dark = Mat("Materials/PigeonFlockEye");
                Cube("Body", dog, new Vector3(0f, 0.5f, 0f), new Vector3(0.36f, 0.38f, 0.85f), brown);
                Cube("Head", dog, new Vector3(0f, 0.72f, 0.52f), new Vector3(0.32f, 0.32f, 0.34f), brown);
                Cube("Muzzle", dog, new Vector3(0f, 0.66f, 0.74f), new Vector3(0.2f, 0.16f, 0.2f), cream);
                Cube("Nose", dog, new Vector3(0f, 0.7f, 0.85f), new Vector3(0.08f, 0.07f, 0.06f), dark);
                Cube("Collar", dog, new Vector3(0f, 0.62f, 0.36f), new Vector3(0.38f, 0.08f, 0.1f), Mat("FourDistrict/HydrantRed"));
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    Cube("Ear", dog, new Vector3(sign * 0.13f, 0.94f, 0.44f), new Vector3(0.1f, 0.2f, 0.08f), brown);
                    Cube("Eye", dog, new Vector3(sign * 0.164f, 0.79f, 0.61f), new Vector3(0.025f, 0.045f, 0.045f), dark);
                }
                var tail = Group("Tail", dog); tail.localPosition = new Vector3(0f, 0.64f, -0.42f);
                Cube("TailTip", tail, Vector3.up * 0.18f, new Vector3(0.08f, 0.36f, 0.08f), brown);
                var legs = new Transform[4];
                for (int i = 0; i < legs.Length; i++)
                {
                    legs[i] = Group("Leg_" + i, dog);
                    legs[i].localPosition = new Vector3(i % 2 == 0 ? -0.12f : 0.12f, 0.36f, i < 2 ? 0.3f : -0.3f);
                    Cube("LowerLeg", legs[i], Vector3.down * 0.18f, new Vector3(0.11f, 0.36f, 0.11f), brown);
                }
                var shape = dog.gameObject.AddComponent<BoxCollider>(); shape.center = new Vector3(0f, 0.55f, 0.1f); shape.size = new Vector3(0.4f, 0.85f, 1.35f);
                dog.gameObject.AddComponent<Rigidbody>().isKinematic = true;
                var hazard = root.AddComponent<DogHazard>();
                var data = new SerializedObject(hazard);
                Ref(data, "dog", dog); Ref(data, "tail", tail); Ref(data, "callout", Callout(root.transform));
                var list = data.FindProperty("legs"); list.arraySize = legs.Length;
                for (int i = 0; i < legs.Length; i++) list.GetArrayElementAtIndex(i).objectReferenceValue = legs[i];
                data.ApplyModifiedPropertiesWithoutUndo();
                dog.gameObject.SetActive(false);
                PrefabUtility.SaveAsPrefabAsset(root, Folder + "Dog.prefab");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        private static void BuildClone()
        {
            var root = new GameObject("CloneRider");
            try
            {
                var hazard = root.AddComponent<CloneRiderHazard>();
                var data = new SerializedObject(hazard);
                var list = data.FindProperty("clones"); list.arraySize = 3;
                for (int i = 0; i < 3; i++)
                {
                    var actor = Group("Clone_" + i, root.transform); actor.localPosition = Vector3.right * ((i - 1) * 1.4f);
                    var shape = actor.gameObject.AddComponent<CapsuleCollider>(); shape.center = Vector3.up * 0.9f; shape.height = 1.8f; shape.radius = 0.35f;
                    var body = actor.gameObject.AddComponent<Rigidbody>(); body.mass = 65f; body.constraints = RigidbodyConstraints.FreezeRotation; body.isKinematic = true;
                    var clone = actor.gameObject.AddComponent<FoodThief>();
                    Material skin = Mat("Materials/Skin"), purple = Mat("FourDistrict/Building_D"), dark = Mat("Materials/SignBoard");
                    Cube("Shirt", actor, new Vector3(0f, 1.1f, 0f), new Vector3(0.55f, 0.65f, 0.3f), purple);
                    Cube("Head", actor, new Vector3(0f, 1.65f, 0f), new Vector3(0.38f, 0.4f, 0.36f), skin);
                    Cube("Cap", actor, new Vector3(0f, 1.89f, 0.04f), new Vector3(0.44f, 0.12f, 0.5f), dark);
                    for (int side = -1; side <= 1; side += 2)
                    {
                        Cube("Arm", actor, new Vector3(side * 0.36f, 1.05f, 0f), new Vector3(0.15f, 0.65f, 0.18f), purple);
                        Cube("Leg", actor, new Vector3(side * 0.17f, 0.4f, 0f), new Vector3(0.2f, 0.8f, 0.22f), dark);
                        Cube("Shoe", actor, new Vector3(side * 0.17f, 0.06f, 0.07f), new Vector3(0.23f, 0.12f, 0.4f), dark);
                        Cube("Eye", actor, new Vector3(side * 0.09f, 1.7f, 0.185f), new Vector3(0.04f, 0.05f, 0.02f), dark);
                    }
                    Cube("StolenFood", actor, new Vector3(0f, 1.1f, -0.35f), new Vector3(0.48f, 0.5f, 0.45f), Mat("FourDistrict/StolenFood")).gameObject.SetActive(false);
                    list.GetArrayElementAtIndex(i).objectReferenceValue = clone;
                    actor.gameObject.SetActive(false);
                }
                Ref(data, "callout", Callout(root.transform));
                data.ApplyModifiedPropertiesWithoutUndo();
                PrefabUtility.SaveAsPrefabAsset(root, Folder + "CloneRider.prefab");
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        public static void Screenshot()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var dog = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Folder + "Dog.prefab"));
            dog.transform.position = new Vector3(-2.8f, 0f, 0f); dog.transform.rotation = Quaternion.Euler(0f, -50f, 0f);
            dog.transform.Find("DogBody").gameObject.SetActive(true);
            var clones = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Folder + "CloneRider.prefab"));
            clones.transform.position = new Vector3(1.5f, 0f, 0f);
            for (int i = 0; i < 3; i++) clones.transform.Find("Clone_" + i).gameObject.SetActive(true);
            clones.transform.Find("Clone_1/StolenFood").gameObject.SetActive(true);
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube); floor.transform.position = Vector3.down * 0.1f;
            floor.transform.localScale = new Vector3(20f, 0.2f, 15f); floor.GetComponent<Renderer>().sharedMaterial = Mat("Materials/Sidewalk");
            var light = new GameObject("PreviewLight").AddComponent<Light>(); light.type = LightType.Directional; light.intensity = 1.4f;
            light.transform.rotation = Quaternion.Euler(40f, -25f, 0f);
            var camera = new GameObject("PreviewCamera").AddComponent<Camera>();
            camera.transform.position = new Vector3(7f, 5f, 10f); camera.transform.LookAt(new Vector3(0f, 0.9f, 0f));
            camera.orthographic = true; camera.orthographicSize = 3.4f; camera.backgroundColor = new Color(0.76f, 0.84f, 0.9f); camera.clearFlags = CameraClearFlags.SolidColor;
            var target = new RenderTexture(1600, 900, 24);
            var pixels = new Texture2D(1600, 900, TextureFormat.RGB24, false);
            bool batching = UnityEngine.Rendering.GraphicsSettings.useScriptableRenderPipelineBatching;
            try
            {
                UnityEngine.Rendering.GraphicsSettings.useScriptableRenderPipelineBatching = false;
                camera.targetTexture = target; camera.Render(); RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0, 0, 1600, 900), 0, 0); pixels.Apply();
                string path = Path.GetFullPath(Path.Combine(Application.dataPath, "../../공유/결과/codex_개복제_프리팹.png"));
                File.WriteAllBytes(path, pixels.EncodeToPNG());
                Debug.Log("DOG_CLONE_SCREENSHOT " + path);
            }
            finally
            {
                camera.targetTexture = null; RenderTexture.active = null;
                UnityEngine.Rendering.GraphicsSettings.useScriptableRenderPipelineBatching = batching;
                UnityEngine.Object.DestroyImmediate(target); UnityEngine.Object.DestroyImmediate(pixels);
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        private static Material Mat(string name)
            => AssetDatabase.LoadAssetAtPath<Material>("Assets/_Game/Art/" + name + ".mat") ?? throw new InvalidOperationException("Existing material missing: " + name);
        private static Transform Group(string name, Transform parent)
        { var child = new GameObject(name).transform; child.SetParent(parent, false); return child; }
        private static Transform Cube(string name, Transform parent, Vector3 point, Vector3 size, Material material)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube); cube.name = name;
            cube.transform.SetParent(parent, false); cube.transform.localPosition = point; cube.transform.localScale = size;
            cube.GetComponent<Renderer>().sharedMaterial = material;
            UnityEngine.Object.DestroyImmediate(cube.GetComponent<Collider>());
            return cube.transform;
        }
        private static TextMesh Callout(Transform parent)
        {
            var text = Group("Callout", parent).gameObject.AddComponent<TextMesh>();
            text.text = "!"; text.fontSize = 80; text.characterSize = 0.04f;
            text.anchor = TextAnchor.MiddleCenter; text.color = Color.red; text.gameObject.SetActive(false);
            return text;
        }
        private static void Ref(SerializedObject data, string field, UnityEngine.Object value)
            => data.FindProperty(field).objectReferenceValue = value;
    }
}
