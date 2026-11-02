using System;
using System.Linq;
using DeliveryRider;
using DeliveryRider.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

public static class ApplyPigeonFlock
{
    public static string UpdateVisuals()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Use edit mode.");
        var root = PrefabUtility.LoadPrefabContents(PigeonFlockInstaller.PrefabPath);
        try
        {
            var birds = new SerializedObject(root.GetComponent<PigeonFlockHazard>()).FindProperty("birds");
            for (int i = 0; i < birds.arraySize; i++)
            {
                float phase = i * UnityEngine.Mathf.PI * 0.25f;
                var plane = UnityEngine.Quaternion.Euler(25f + i * 23f, i * 47f, i * 31f);
                var tangent = plane * new UnityEngine.Vector3(-UnityEngine.Mathf.Sin(phase), UnityEngine.Mathf.Cos(phase), 0f);
                ((UnityEngine.Transform)birds.GetArrayElementAtIndex(i).objectReferenceValue).localRotation = PigeonFlockHazard.UprightFlightRotation(tangent);
            }
            PrefabUtility.SaveAsPrefabAsset(root, PigeonFlockInstaller.PrefabPath);
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
        const string path = "Assets/_Game/Scenes/FourDistrictPrototype_Myeongji.unity";
        var scene = SceneManager.GetSceneByPath(path);
        bool opened = !scene.isLoaded;
        if (opened) scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
        try
        {
            var overlay = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<PigeonFlockOverlay>(true)).Single();
            var fields = new SerializedObject(overlay);
            fields.FindProperty("iconCount").intValue = 48;
            fields.FindProperty("speedShare").vector2Value = new UnityEngine.Vector2(0.245f, 0.49f);
            fields.ApplyModifiedPropertiesWithoutUndo();
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("Could not save overlay tuning.");
            return "Saved: upright 3D birds; UI icons 48; UI speed 0.245–0.49 screen widths/s (x0.7). No Play Mode.";
        }
        finally { if (opened) EditorSceneManager.CloseScene(scene, true); }
    }

    public static string Review()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<UnityEngine.GameObject>(PigeonFlockInstaller.PrefabPath);
        var hazard = prefab.GetComponent<PigeonFlockHazard>();
        var data = new SerializedObject(hazard);
        var sphere = prefab.GetComponent<UnityEngine.SphereCollider>();
        var importer = (TextureImporter)AssetImporter.GetAtPath(PigeonFlockInstaller.IconPath);
        int count = data.FindProperty("birds").arraySize;
        if (count != 8 || !sphere.isTrigger || prefab.GetComponentsInChildren<UnityEngine.Collider>(true).Length != 1
            || !importer.DoesSourceTextureHaveAlpha()) throw new InvalidOperationException("Invalid flock prefab or icon alpha.");
        var settings = AssetDatabase.LoadAssetAtPath<HazardSpawnSettings>("Assets/_Game/Settings/MyeongjiHazardSpawnSettings.asset");
        var kind = settings.Kinds.Single(k => k.type == "pigeonflock");
        if (kind.prefab != prefab) throw new InvalidOperationException("Flock prefab reference missing.");
        const string scenePath = "Assets/_Game/Scenes/FourDistrictPrototype_Myeongji.unity";
        var scene = SceneManager.GetSceneByPath(scenePath);
        bool opened = !scene.isLoaded;
        if (opened) scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
        try
        {
            var ui = scene.GetRootGameObjects().Single(r => r.name == "PigeonFlockUI").GetComponentInChildren<PigeonFlockOverlay>(true);
            var fields = new SerializedObject(ui);
            if (fields.FindProperty("session").objectReferenceValue == null || fields.FindProperty("icon").objectReferenceValue == null)
                throw new InvalidOperationException("UI references missing.");
            return $"Verified saved assets: birds={count}; only collider=invisible sphere; icon alpha=true; session/UI references valid; icons={fields.FindProperty("iconCount").intValue}; duration={data.FindProperty("obscureSeconds").floatValue}; unlockDay={kind.unlockDay}; pool={kind.poolSize}; playMode={EditorApplication.isPlaying}.";
        }
        finally { if (opened) EditorSceneManager.CloseScene(scene, true); }
    }

    public static string Apply()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Use edit mode.");
        PigeonFlockInstaller.ImportIcon(@"C:\Users\우성\.codex\visualizations\2026\11\02\01a19cee-ff54-7c81-8588-e11e3ef85bf2\pigeon-ui-icon-lowpoly.png");
        const string path = "Assets/_Game/Scenes/FourDistrictPrototype_Myeongji.unity";
        var previous = SceneManager.GetActiveScene();
        var scene = SceneManager.GetSceneByPath(path);
        bool opened = !scene.isLoaded;
        if (opened) scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
        SceneManager.SetActiveScene(scene);
        try { return PigeonFlockInstaller.ApplyToScene(scene); }
        finally
        {
            if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
            if (opened) EditorSceneManager.CloseScene(scene, true);
        }
    }
}
