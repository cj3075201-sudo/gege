using System;
using DeliveryRider;
using DeliveryRider.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

public static class ApplyMusclemanDistance
{
    public static string UpdateLifetime()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Use edit mode.");
        var root = PrefabUtility.LoadPrefabContents(MusclemanInstaller.PrefabPath);
        try
        {
            var fields = new SerializedObject(root.GetComponent<MusclemanHazard>());
            fields.FindProperty("disappearAfterTurn").floatValue = 1f;
            fields.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.SaveAsPrefabAsset(root, MusclemanInstaller.PrefabPath);
            return "Saved Muscleman disappearAfterTurn=1 second. No Play Mode.";
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
    }

    public static string Apply()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Use edit mode.");
        var root = PrefabUtility.LoadPrefabContents(MusclemanInstaller.PrefabPath);
        try
        {
            var fields = new SerializedObject(root.GetComponent<MusclemanHazard>());
            fields.FindProperty("spawnBehindDistance").floatValue = 10f;
            fields.FindProperty("minimumStraightBehind").floatValue = 10f;
            fields.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.SaveAsPrefabAsset(root, MusclemanInstaller.PrefabPath);
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
        const string path = "Assets/_Game/Scenes/FourDistrictPrototype_Myeongji.unity";
        var previous = SceneManager.GetActiveScene();
        var scene = SceneManager.GetSceneByPath(path);
        bool opened = !scene.isLoaded;
        if (opened) scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
        SceneManager.SetActiveScene(scene);
        try { return "Saved behind distance 10m; " + MusclemanInstaller.ApplyToScene(scene); }
        finally
        {
            if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
            if (opened) EditorSceneManager.CloseScene(scene, true);
        }
    }
}
