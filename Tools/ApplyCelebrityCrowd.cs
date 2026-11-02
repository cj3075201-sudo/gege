using System;
using System.Linq;
using DeliveryRider;
using DeliveryRider.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

public static class ApplyCelebrityCrowd
{
    public static string UpdateTuning()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Use edit mode.");
        var root = PrefabUtility.LoadPrefabContents(CelebrityCrowdInstaller.PrefabPath);
        try
        {
            var data = new SerializedObject(root.GetComponent<CelebrityCrowdHazard>());
            data.FindProperty("spawnRadius").floatValue = 15f;
            data.FindProperty("fanMassMultiplier").floatValue = 3f;
            data.ApplyModifiedPropertiesWithoutUndo();
            PrefabUtility.SaveAsPrefabAsset(root, CelebrityCrowdInstaller.PrefabPath);
        }
        finally { PrefabUtility.UnloadPrefabContents(root); }
        return Apply();
    }

    public static string Apply()
    {
        if (EditorApplication.isPlaying) throw new InvalidOperationException("Use edit mode.");
        const string path = "Assets/_Game/Scenes/FourDistrictPrototype_Myeongji.unity";
        var previous = SceneManager.GetActiveScene();
        var scene = SceneManager.GetSceneByPath(path);
        bool opened = !scene.isLoaded;
        if (opened) scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
        SceneManager.SetActiveScene(scene);
        try { return CelebrityCrowdInstaller.ApplyToScene(scene); }
        finally
        {
            if (previous.IsValid() && previous.isLoaded) SceneManager.SetActiveScene(previous);
            if (opened) EditorSceneManager.CloseScene(scene, true);
        }
    }

    public static string Review()
    {
        var settings = AssetDatabase.LoadAssetAtPath<HazardSpawnSettings>("Assets/_Game/Settings/MyeongjiHazardSpawnSettings.asset");
        var candidates = AssetDatabase.LoadAssetAtPath<HazardCandidates>("Assets/_Game/Settings/MyeongjiHazardCandidates.asset");
        var kind = settings.Kinds.Single(k => k.type == "celebrity");
        var hazard = kind.prefab.GetComponent<CelebrityCrowdHazard>();
        var data = new SerializedObject(hazard);
        if (data.FindProperty("celebrityCoat").objectReferenceValue == null || data.FindProperty("glowMaterial").objectReferenceValue == null)
            throw new InvalidOperationException("Missing crowd material references.");
        return $"Saved assets: {candidates.Candidates.Count(c => c.type == "celebrity")} crowd candidates; spots {candidates.Spots.Count}; count {kind.Count(1)}; unlock day {kind.unlockDay}; waiting {data.FindProperty("waitingFans").intValue}, incoming {data.FindProperty("incomingFans").intValue}; materials wired; playMode={EditorApplication.isPlaying}.";
    }
}
