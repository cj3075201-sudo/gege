using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DeliveryRider.Editor
{
    public static class ReviewCorrectionInstaller
    {
        public static void ApplyFiveDays()
        {
            var settings=AssetDatabase.LoadAssetAtPath<HazardSpawnSettings>("Assets/_Game/Settings/MyeongjiHazardSpawnSettings.asset");
            DevTuningSession.EnsureFiveDays(settings);DevTuningSession.MarkSaved(settings);
            EditorUtility.SetDirty(settings);AssetDatabase.SaveAssetIfDirty(settings);DevTuningSession.EndSession();
            var scene=EditorSceneManager.OpenScene(MeasureRunner.ScenePath,OpenSceneMode.Single);
            Extend(UnityEngine.Object.FindFirstObjectByType<HazardPlanner>(),"activePerDay");
            Extend(UnityEngine.Object.FindFirstObjectByType<TrafficNetwork>(),"vehicleCountByDay");
            EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);
            Debug.Log("REVIEW_INSTALL five days, day5 copies day4; day6+ capped at5");
        }
        public static void ApplyFoodGravity()
        {
            var scene=EditorSceneManager.OpenScene(MeasureRunner.ScenePath,OpenSceneMode.Single);
            var so=new SerializedObject(UnityEngine.Object.FindFirstObjectByType<HazardPlanner>());
            so.FindProperty("foodExtraUp").floatValue=5f;so.ApplyModifiedPropertiesWithoutUndo();
            EditorSceneManager.MarkSceneDirty(scene);EditorSceneManager.SaveScene(scene);
            Debug.Log("REVIEW_INSTALL food uses accident gravity and HTML extra-up5");
        }
        private static void Extend(UnityEngine.Object target,string name)
        {
            var so=new SerializedObject(target);var field=so.FindProperty(name);
            if(field==null||field.arraySize==0)throw new InvalidOperationException("Missing schedule "+name);
            int last=field.GetArrayElementAtIndex(Mathf.Min(3,field.arraySize-1)).intValue;
            if(field.arraySize<5){field.arraySize=5;field.GetArrayElementAtIndex(4).intValue=last;}
            so.ApplyModifiedPropertiesWithoutUndo();EditorUtility.SetDirty(target);
        }
    }
}
