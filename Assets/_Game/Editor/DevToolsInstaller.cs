using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DeliveryRider.Editor
{
    // Adds the F1 developer tools to a game scene (GameScenes) and wires them to the scene's systems. Safe to run repeatedly.
    public static class DevToolsInstaller
    {
        private const string FontPath = "Assets/_Game/Art/Fonts/NotoSansKR-Regular.otf";

        [MenuItem("Delivery Rider/Apply Dev Tools")]
        public static void Apply()
        {
            if (EditorApplication.isPlaying || EditorApplication.isCompiling) throw new InvalidOperationException("Use edit mode after compilation.");
            var scene = GameScenes.RequireActive();
            var session = UnityEngine.Object.FindFirstObjectByType<DeliverySession>(FindObjectsInactive.Include);
            var rider = UnityEngine.Object.FindFirstObjectByType<RiderMotor>(FindObjectsInactive.Include);
            var follow = UnityEngine.Object.FindFirstObjectByType<ThirdPersonCamera>(FindObjectsInactive.Include);
            var planner = UnityEngine.Object.FindFirstObjectByType<HazardPlanner>(FindObjectsInactive.Include);
            if (session == null || rider == null || follow == null || planner == null)
                throw new InvalidOperationException("Session, rider, follow camera and hazard planner are required.");
            var font = AssetDatabase.LoadAssetAtPath<Font>(FontPath);
            if (font == null) throw new InvalidOperationException(FontPath + " is missing.");

            var tools = UnityEngine.Object.FindFirstObjectByType<DevTools>(FindObjectsInactive.Include);
            if (tools == null)
            {
                var host = new GameObject("DevTools");
                Undo.RegisterCreatedObjectUndo(host, "Add developer tools");
                tools = host.AddComponent<DevTools>();
            }
            var data = new SerializedObject(tools);
            data.FindProperty("session").objectReferenceValue = session;
            data.FindProperty("rider").objectReferenceValue = rider;
            data.FindProperty("riderVisual").objectReferenceValue = rider.GetComponent<RiderReactionVisual>();
            data.FindProperty("safeRecovery").objectReferenceValue = rider.GetComponent<RiderSafeRecovery>();
            data.FindProperty("riderDust").objectReferenceValue = UnityEngine.Object.FindFirstObjectByType<RiderDust>(FindObjectsInactive.Include);
            data.FindProperty("followCamera").objectReferenceValue = follow;
            data.FindProperty("curvedWorld").objectReferenceValue = follow.GetComponent<CurvedWorld>();
            data.FindProperty("hazards").objectReferenceValue = planner;
            data.FindProperty("hazardSettings").objectReferenceValue = new SerializedObject(planner).FindProperty("settings").objectReferenceValue;
            data.FindProperty("traffic").objectReferenceValue = UnityEngine.Object.FindFirstObjectByType<TrafficNetwork>(FindObjectsInactive.Include);
            data.FindProperty("pedestrians").objectReferenceValue = UnityEngine.Object.FindFirstObjectByType<PedestrianNetwork>(FindObjectsInactive.Include);
            data.FindProperty("daySettings").objectReferenceValue = new SerializedObject(session).FindProperty("daySettings").objectReferenceValue;
            data.FindProperty("daySky").objectReferenceValue = UnityEngine.Object.FindFirstObjectByType<DaySky>(FindObjectsInactive.Include);
            data.FindProperty("font").objectReferenceValue = font;
            data.ApplyModifiedProperties();
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("Could not save scene.");
            Debug.Log("Dev tools applied: press F1 in Play Mode (editor and development builds). Play Mode not started.");
        }
    }
}
