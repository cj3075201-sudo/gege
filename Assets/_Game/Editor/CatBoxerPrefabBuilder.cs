using System;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace DeliveryRider.Editor
{
    public static class CatBoxerPrefabBuilder
    {
        public const string PrefabPath = "Assets/_Game/Prefabs/Hazards/CatBoxer.prefab";
        private const string Output = "C:/UnityWork/ge/공유/결과/";
        public static void Run()
        {
            try
            {
                Build();
                Render();
                Debug.Log("CAT_PREFAB_PASS guid=" + AssetDatabase.AssetPathToGUID(PrefabPath));
                EditorApplication.Exit(0);
            }
            catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
        }

        [MenuItem("Delivery Rider/Cat Boxer/Rebuild Prefab")]
        public static void Build()
        {
            var root = new GameObject("CatBoxer");
            try
            {
                var cat = root.AddComponent<CatBoxerHazard>();
                var actor = new GameObject("Cat"); actor.transform.SetParent(root.transform, false);
                var view = actor.AddComponent<CatBoxerView>();
                var collider = actor.AddComponent<CapsuleCollider>();
                collider.center = Vector3.up; collider.height = 2f; collider.radius = 0.42f;
                var body = actor.AddComponent<Rigidbody>(); body.isKinematic = true; body.useGravity = false;
                var model = new GameObject("Model").transform; model.SetParent(actor.transform, false); model.localScale = Vector3.one * 1.45f;
                var orange = Material("Assets/_Game/Art/FourDistrict/BeakOrange.mat");
                var cream = Material("Assets/_Game/Art/FourDistrict/ChickenWhite.mat");
                var red = Material("Assets/_Game/Art/FourDistrict/ChickenComb.mat");
                var dark = Material("Assets/_Game/Art/FourDistrict/ManholeIron.mat");
                Primitive(model, "Torso", PrimitiveType.Cube, orange, new Vector3(0f, .62f, 0f), new Vector3(.36f, .62f, .3f));
                Primitive(model, "Chest", PrimitiveType.Cube, cream, new Vector3(0f, .6f, .16f), new Vector3(.24f, .36f, .05f));
                Primitive(model, "Head", PrimitiveType.Sphere, orange, new Vector3(0f, 1.12f, 0f), new Vector3(.44f, .38f, .42f));
                Transform Ear(string name, float x)
                {
                    var pivot = new GameObject(name).transform; pivot.SetParent(model, false); pivot.localPosition = new Vector3(x, 1.31f, 0f);
                    var shape = Primitive(pivot, "EarTip", PrimitiveType.Cube, orange, Vector3.zero, new Vector3(.17f, .17f, .1f));
                    shape.localRotation = Quaternion.Euler(0f, 0f, 45f);
                    return pivot;
                }
                var earL = Ear("EarL", -.15f); var earR = Ear("EarR", .15f);
                for (int i = -1; i <= 1; i += 2)
                {
                    Primitive(model, "Eye" + i, PrimitiveType.Cube, dark, new Vector3(i * .1f, 1.16f, .2f), new Vector3(.08f, .05f, .04f));
                    Primitive(model, "Leg" + i, PrimitiveType.Cube, orange, new Vector3(i * .11f, .16f, 0f), new Vector3(.14f, .32f, .14f));
                }
                Primitive(model, "Brow", PrimitiveType.Cube, dark, new Vector3(0f, 1.24f, .2f), new Vector3(.34f, .03f, .04f));
                var tail = Primitive(model, "Tail", PrimitiveType.Cube, orange, new Vector3(0f, .63f, -.27f), new Vector3(.08f, .6f, .08f)); tail.localRotation = Quaternion.Euler(-28f, 0f, 0f);
                Transform Arm(string name, float x)
                {
                    var pivot = new GameObject(name).transform; pivot.SetParent(model, false); pivot.localPosition = new Vector3(x, .84f, 0f);
                    Primitive(pivot, "Arm", PrimitiveType.Cube, orange, new Vector3(0f, -.17f, 0f), new Vector3(.1f, .34f, .1f));
                    Primitive(pivot, "Glove", PrimitiveType.Sphere, red, new Vector3(0f, -.4f, 0f), Vector3.one * .26f);
                    return pivot;
                }
                var left = Arm("ArmL", -.24f); var right = Arm("ArmR", .24f);
                var ring = Primitive(actor.transform, "AttackTell", PrimitiveType.Cylinder, Material("Assets/_Game/Art/FourDistrict/HazardWarning.mat"), Vector3.up * .04f, new Vector3(2.6f, .015f, 2.6f));
                ring.gameObject.SetActive(false);
                var captionRoot = new GameObject("Caption"); captionRoot.transform.SetParent(actor.transform, false); captionRoot.transform.localPosition = Vector3.up * 2.5f;
                var caption = captionRoot.AddComponent<TextMeshPro>();
                caption.font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>("Assets/_Game/Art/Fonts/PrototypeKoreanRegular.asset");
                caption.text = "고양이 복서"; caption.fontSize = 3f; caption.alignment = TextAlignmentOptions.Center; caption.color = Color.black;
                caption.rectTransform.sizeDelta = new Vector2(7f, 1f);
                Set(view, "model", model); Set(view, "leftArm", left); Set(view, "rightArm", right); Set(view, "leftEar", earL); Set(view, "rightEar", earR);
                Set(view, "tellRing", ring); Set(view, "caption", caption); Set(cat, "presentation", view);
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
                AssetDatabase.SaveAssets();
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        private static Material Material(string path) => AssetDatabase.LoadAssetAtPath<Material>(path) ?? throw new InvalidOperationException("Existing material missing: " + path);
        private static Transform Primitive(Transform parent, string name, PrimitiveType type, Material material, Vector3 position, Vector3 scale)
        {
            var go = GameObject.CreatePrimitive(type); go.name = name; go.transform.SetParent(parent, false);
            go.transform.localPosition = position; go.transform.localScale = scale;
            go.GetComponent<Renderer>().sharedMaterial = material;
            UnityEngine.Object.DestroyImmediate(go.GetComponent<Collider>());
            return go.transform;
        }
        private static void Set(UnityEngine.Object owner, string field, UnityEngine.Object value)
        {
            var so = new SerializedObject(owner); so.FindProperty(field).objectReferenceValue = value; so.ApplyModifiedPropertiesWithoutUndo();
        }

        public static void Render()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var root = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath));
            var actor = root.GetComponentInChildren<CatBoxerView>(true);
            var camera = new GameObject("CatPreviewCamera").AddComponent<Camera>();
            camera.transform.position = new Vector3(4f, 3.2f, 5f); camera.transform.LookAt(new Vector3(0f, 1.1f, 0f));
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.white;
            camera.orthographic = true; camera.orthographicSize = 2f;
            var light = new GameObject("Key").AddComponent<Light>(); light.type = LightType.Directional; light.intensity = 1.5f; light.transform.rotation = Quaternion.Euler(40f, -25f, 0f);
            RenderSettings.ambientMode = AmbientMode.Flat; RenderSettings.ambientLight = Color.gray;
            var target = new RenderTexture(960, 960, 24); camera.targetTexture = target;
            var image = new Texture2D(960, 960, TextureFormat.RGB24, false);
            try
            {
                var cat = root.GetComponent<CatBoxerHazard>(); actor.Show(cat, camera.transform.position, 0f, 0f, 1.2f);
                camera.Render(); RenderTexture.active = target; image.ReadPixels(new Rect(0, 0, 960, 960), 0, 0); image.Apply();
                Directory.CreateDirectory(Output); File.WriteAllBytes(Output + "codex_cat_prefab.png", image.EncodeToPNG());
                Debug.Log("CAT_PREVIEW_SAVED: " + Output + "codex_cat_prefab.png");
            }
            finally { RenderTexture.active = null; camera.targetTexture = null; target.Release(); UnityEngine.Object.DestroyImmediate(target); UnityEngine.Object.DestroyImmediate(image); }
        }
    }
}
