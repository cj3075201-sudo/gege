using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DeliveryRider.Editor
{
    public static class PigeonFlockInstaller
    {
        public const string IconPath = "Assets/_Game/Art/UI/PigeonFlockIcon.png";
        public const string PrefabPath = "Assets/_Game/Prefabs/Hazards/PigeonFlock.prefab";

        [MenuItem("Delivery Rider/Apply Pigeon Flock")]
        public static void Apply() => Debug.Log(ApplyToScene(SceneManager.GetActiveScene()));

        public static void ImportIcon(string source)
        {
            if (!File.Exists(IconPath)) File.Copy(source, IconPath);
            AssetDatabase.ImportAsset(IconPath, ImportAssetOptions.ForceSynchronousImport);
            var importer = (TextureImporter)AssetImporter.GetAtPath(IconPath);
            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.alphaIsTransparency = true;
            importer.mipmapEnabled = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.maxTextureSize = 2048;
            importer.filterMode = FilterMode.Bilinear;
            importer.SaveAndReimport();
        }

        public static string ApplyToScene(Scene scene)
        {
            if (EditorApplication.isPlaying || EditorApplication.isCompiling) throw new InvalidOperationException("Use edit mode after compilation.");
            GameScenes.RequireActive();
            var parts = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Component>(true)).ToArray();
            var session = parts.OfType<DeliverySession>().Single();
            var planner = parts.OfType<HazardPlanner>().Single();
            var data = new SerializedObject(planner);
            var settings = (HazardSpawnSettings)data.FindProperty("settings").objectReferenceValue;
            var candidates = (HazardCandidates)data.FindProperty("candidates").objectReferenceValue;
            var icon = AssetDatabase.LoadAssetAtPath<Sprite>(IconPath);
            if (icon == null) throw new InvalidOperationException("Import the approved pigeon UI icon first.");
            var prefab = CreatePrefab();
            var places = candidates.Candidates.Where(c => c.type != "pigeonflock").ToList();
            // Main-road duck candidates already have room for an across-road encounter.
            var bases = places.Where(c => c.type == "duck" && !c.alley && c.halfWidth >= 2f).ToArray();
            if (bases.Length == 0) throw new InvalidOperationException("No suitable main-road flock candidates.");
            foreach (var spot in bases) { var copy = spot; copy.type = "pigeonflock"; places.Add(copy); }
            candidates.Configure(candidates.Spots.ToArray(), places.ToArray());
            EditorUtility.SetDirty(candidates);
            var kinds = settings.Kinds.ToList();
            var kind = kinds.FirstOrDefault(k => k.type == "pigeonflock");
            if (kind == null)
            {
                kind = new HazardSpawnSettings.Kind { type = "pigeonflock", role = HazardRole.Trigger, unlockDay = 2, poolSize = 3 };
                kinds.Add(kind);
            }
            kind.prefab = prefab;
            settings.Configure(kinds.ToArray());
            EditorUtility.SetDirty(settings);
            InstallOverlay(scene, session, icon);
            AssetDatabase.SaveAssets();
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("Could not save pigeon flock UI wiring.");
            return $"Pigeon flock: {bases.Length} candidates; day {kind.unlockDay}, pool {kind.poolSize}; eight 3D birds, invisible sphere trigger; UI icon and session event wired; saved {scene.path}. No Play Mode.";
        }

        private static void InstallOverlay(Scene scene, DeliverySession session, Sprite icon)
        {
            var root = scene.GetRootGameObjects().SingleOrDefault(r => r.name == "PigeonFlockUI");
            if (root == null) root = new GameObject("PigeonFlockUI", typeof(RectTransform), typeof(Canvas), typeof(UnityEngine.UI.CanvasScaler));
            var canvas = root.GetComponent<Canvas>();
            if (canvas == null) throw new InvalidOperationException("PigeonFlockUI exists but is not the overlay Canvas.");
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;
            var scaler = root.GetComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            var child = root.transform.Find("Birds");
            if (child == null)
            {
                child = new GameObject("Birds", typeof(RectTransform)).transform;
                child.SetParent(root.transform, false);
            }
            var rect = (RectTransform)child;
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
            var overlay = child.GetComponent<PigeonFlockOverlay>() ?? child.gameObject.AddComponent<PigeonFlockOverlay>();
            overlay.Configure(session, icon);
            var group = child.GetComponent<CanvasGroup>();
            group.alpha = 0f; group.blocksRaycasts = group.interactable = false;
            EditorUtility.SetDirty(overlay);
        }

        private static GameObject CreatePrefab()
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (existing != null) return existing;
            var source = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Game/Prefabs/Traffic/Pedestrian.prefab");
            var coat = (Renderer)new SerializedObject(source.GetComponent<TrafficPedestrian>()).FindProperty("coat").objectReferenceValue;
            var gray = Material("PigeonFlockGray", coat.sharedMaterial, new Color(0.48f, 0.51f, 0.58f));
            var light = Material("PigeonFlockWing", coat.sharedMaterial, new Color(0.7f, 0.72f, 0.77f));
            var teal = Material("PigeonFlockNeck", coat.sharedMaterial, new Color(0.25f, 0.43f, 0.43f));
            var beak = Material("PigeonFlockBeak", coat.sharedMaterial, new Color(0.78f, 0.76f, 0.7f));
            var dark = Material("PigeonFlockEye", coat.sharedMaterial, new Color(0.08f, 0.09f, 0.1f));
            var root = new GameObject("PigeonFlock");
            try
            {
                var sphere = root.AddComponent<SphereCollider>();
                sphere.isTrigger = true; sphere.center = new Vector3(0f, 1.7f, 0f); sphere.radius = 2.25f;
                var body = root.AddComponent<Rigidbody>(); body.isKinematic = true; body.useGravity = false;
                var birds = new Transform[8]; var left = new Transform[8]; var right = new Transform[8];
                for (int i = 0; i < birds.Length; i++)
                {
                    var bird = new GameObject($"Pigeon_{i:00}").transform;
                    bird.SetParent(root.transform, false);
                    birds[i] = bird;
                    float phase = i * Mathf.PI * 0.25f;
                    var plane = Quaternion.Euler(25f + i * 23f, i * 47f, i * 31f);
                    float radius = 1.45f * (0.65f + 0.35f * (i % 3) / 2f);
                    bird.localPosition = sphere.center + plane * new Vector3(Mathf.Cos(phase), Mathf.Sin(phase), 0f) * radius;
                    bird.localRotation = PigeonFlockHazard.UprightFlightRotation(plane * new Vector3(-Mathf.Sin(phase), Mathf.Cos(phase), 0f));
                    Cube("Body", bird, Vector3.zero, new Vector3(0.28f, 0.24f, 0.45f), gray);
                    Cube("Neck", bird, new Vector3(0f, 0.09f, 0.15f), new Vector3(0.2f, 0.19f, 0.16f), teal);
                    Cube("Head", bird, new Vector3(0f, 0.2f, 0.25f), Vector3.one * 0.2f, gray);
                    Cube("Beak", bird, new Vector3(0f, 0.19f, 0.39f), new Vector3(0.07f, 0.05f, 0.12f), beak);
                    Cube("Tail", bird, new Vector3(0f, -0.01f, -0.32f), new Vector3(0.22f, 0.04f, 0.22f), gray);
                    Cube("LeftEye", bird, new Vector3(-0.104f, 0.22f, 0.28f), Vector3.one * 0.03f, dark);
                    Cube("RightEye", bird, new Vector3(0.104f, 0.22f, 0.28f), Vector3.one * 0.03f, dark);
                    for (int side = -1; side <= 1; side += 2)
                    {
                        var pivot = new GameObject(side < 0 ? "LeftWing" : "RightWing").transform;
                        pivot.SetParent(bird, false); pivot.localPosition = new Vector3(side * 0.1f, 0.06f, 0f);
                        Cube("Wing", pivot, new Vector3(side * 0.18f, 0f, -0.03f), new Vector3(0.4f, 0.035f, 0.27f), light);
                        if (side < 0) left[i] = pivot; else right[i] = pivot;
                    }
                }
                var hazard = root.AddComponent<PigeonFlockHazard>();
                var data = new SerializedObject(hazard);
                SetArray(data.FindProperty("birds"), birds); SetArray(data.FindProperty("leftWings"), left); SetArray(data.FindProperty("rightWings"), right);
                data.ApplyModifiedPropertiesWithoutUndo();
                return PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        private static void SetArray(SerializedProperty property, Transform[] values)
        { property.arraySize = values.Length; for (int i = 0; i < values.Length; i++) property.GetArrayElementAtIndex(i).objectReferenceValue = values[i]; }

        private static void Cube(string name, Transform parent, Vector3 position, Vector3 scale, Material material)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name; cube.transform.SetParent(parent, false);
            cube.transform.localPosition = position; cube.transform.localScale = scale;
            cube.GetComponent<Renderer>().sharedMaterial = material;
            UnityEngine.Object.DestroyImmediate(cube.GetComponent<Collider>());
        }

        private static Material Material(string name, Material source, Color color)
        {
            string path = $"Assets/_Game/Art/Materials/{name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null) return material;
            material = new Material(source) { name = name, color = color };
            AssetDatabase.CreateAsset(material, path);
            return material;
        }
    }
}
