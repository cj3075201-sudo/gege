using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DeliveryRider.Editor
{
    // Time-of-day sky (Docs/하늘_시간대_구현계획.md, steps 1-6): DaySky on the scene's directional light moves the sun with
    // the work day and drives sunlight, ambient light, fog, the sky shader and the cloud shadow cookie from
    // DaySkySettings. The game camera clears to the Sky skybox, and the light is the scene's sun with soft shadows.
    // Flat parts lying on walls or the ground (windows, road surfaces, markers) do not cast shadows: they would not
    // show and only cost shadow draws.
    // A CloudField ("Clouds") keeps the near soft clouds around the rider. Safe to run repeatedly (run again after
    // rebuilding roads or buildings); the settings asset, materials and cloud shapes are created once and then left
    // to tuning.
    public static class DaySkyInstaller
    {
        private const string SettingsPath = "Assets/_Game/Settings/DaySkySettings.asset";
        private const string SkyMaterialPath = "Assets/_Game/Art/Materials/Sky.mat";
        private const string SkyShader = "DeliveryRider/Sky";
        private const string CloudMaterialPath = "Assets/_Game/Art/Materials/Cloud.mat";
        private const string CloudShader = "DeliveryRider/Cloud";
        private const string CloudShadowShader = "Hidden/DeliveryRider/CloudShadow";
        // Four-district map, then the Myeongji map.
        private static readonly string[] FlatMaterials =
        {
            "Windows", "RoadPaint", "RoadMarking", "RoadAsphalt", "RoadSidewalk", "RoadAlley", "Asphalt", "Sidewalk", "DoorMarker", "ShopMarker", "MapFloor",
            "ST_Asphalt", "ST_Pavement", "ST_Plot", "ST_Paint_White", "ST_Paint_Yellow", "PickupPoint",
        };

        [MenuItem("Delivery Rider/Apply Day Sky")]
        public static void Apply()
        {
            if (EditorApplication.isPlaying || EditorApplication.isCompiling) throw new InvalidOperationException("Use edit mode after compilation.");
            var scene = GameScenes.RequireActive();
            var session = UnityEngine.Object.FindFirstObjectByType<DeliverySession>(FindObjectsInactive.Include);
            var follow = UnityEngine.Object.FindFirstObjectByType<ThirdPersonCamera>(FindObjectsInactive.Include);
            var sun = RenderSettings.sun != null ? RenderSettings.sun
                : UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault(l => l.type == LightType.Directional);
            if (session == null || follow == null || sun == null) throw new InvalidOperationException("The delivery session, follow camera and a directional light are required.");

            const string myeongjiSettingsPath = "Assets/_Game/Settings/MyeongjiDaySkySettings.asset";
            string activeSettingsPath = scene.path.EndsWith("FourDistrictPrototype_Myeongji.unity")
                && AssetDatabase.LoadAssetAtPath<DaySkySettings>(myeongjiSettingsPath) != null ? myeongjiSettingsPath : SettingsPath;
            var settings = AssetDatabase.LoadAssetAtPath<DaySkySettings>(activeSettingsPath);
            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<DaySkySettings>();
                AssetDatabase.CreateAsset(settings, SettingsPath);
                AssetDatabase.SaveAssetIfDirty(settings);
            }
            var skybox = LoadOrCreateMaterial(SkyMaterialPath, SkyShader, "Sky");
            var cloudMaterial = LoadOrCreateMaterial(CloudMaterialPath, CloudShader, "Cloud");
            var cloudShapes = CloudShapes.LoadOrBuild();
            var cloudShadowShader = Shader.Find(CloudShadowShader);
            if (cloudShadowShader == null) throw new InvalidOperationException(CloudShadowShader + " is missing.");

            var sky = sun.GetComponent<DaySky>();
            if (sky == null) sky = Undo.AddComponent<DaySky>(sun.gameObject);
            var data = new SerializedObject(sky);
            data.FindProperty("session").objectReferenceValue = session;
            data.FindProperty("sun").objectReferenceValue = sun;
            data.FindProperty("settings").objectReferenceValue = settings;
            data.FindProperty("skybox").objectReferenceValue = skybox;
            data.FindProperty("cloudShadowShader").objectReferenceValue = cloudShadowShader;
            data.ApplyModifiedProperties();
            RenderSettings.sun = sun;
            RenderSettings.skybox = skybox;
            var view = follow.GetComponent<Camera>();
            Undo.RecordObject(view, "Clear to the sky");
            view.clearFlags = CameraClearFlags.Skybox;
            Undo.RecordObject(sun, "Sun shadows");
            sun.shadows = LightShadows.Soft;
            int flat = 0;
            foreach (var renderer in UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var materials = renderer.sharedMaterials;
                if (materials.Length == 0 || materials.Any(m => m == null || Array.IndexOf(FlatMaterials, m.name) < 0)
                    || renderer.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.Off) continue;
                Undo.RecordObject(renderer, "Flat parts cast no shadows");
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                flat++;
            }

            var field = UnityEngine.Object.FindFirstObjectByType<CloudField>(FindObjectsInactive.Include);
            if (field == null)
            {
                var host = new GameObject("Clouds");
                Undo.RegisterCreatedObjectUndo(host, "Add near clouds");
                field = host.AddComponent<CloudField>();
            }
            var fieldData = new SerializedObject(field);
            fieldData.FindProperty("settings").objectReferenceValue = settings;
            fieldData.FindProperty("center").objectReferenceValue = new SerializedObject(follow).FindProperty("target").objectReferenceValue;
            fieldData.FindProperty("material").objectReferenceValue = cloudMaterial;
            var shapesProperty = fieldData.FindProperty("shapes");
            shapesProperty.arraySize = cloudShapes.Length;
            for (int i = 0; i < cloudShapes.Length; i++) shapesProperty.GetArrayElementAtIndex(i).objectReferenceValue = cloudShapes[i];
            fieldData.ApplyModifiedProperties();

            var tools = UnityEngine.Object.FindFirstObjectByType<DevTools>(FindObjectsInactive.Include);
            if (tools != null)
            {
                var toolData = new SerializedObject(tools);
                toolData.FindProperty("daySky").objectReferenceValue = sky;
                toolData.ApplyModifiedProperties();
            }

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("Could not save scene.");
            Debug.Log($"Day sky applied on {sun.name}: the sun follows the work day with soft shadows ({flat} more flat parts cast none), "
                + $"the camera clears to the Sky skybox and {cloudShapes.Length} cloud shapes feed the near clouds. Play Mode not started.");
        }

        private static Material LoadOrCreateMaterial(string path, string shaderName, string name)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null) return material;
            var shader = Shader.Find(shaderName);
            if (shader == null) throw new InvalidOperationException(shaderName + " is missing.");
            material = new Material(shader) { name = name };
            AssetDatabase.CreateAsset(material, path);
            AssetDatabase.SaveAssetIfDirty(material);
            return material;
        }
    }
}
