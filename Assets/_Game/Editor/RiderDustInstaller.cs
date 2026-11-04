using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DeliveryRider.Editor
{
    // Adds the rider's walking dust (RiderDust) to the open game scene: small cloud meshes on the cloud material. Safe
    // to run repeatedly; tuned values stay.
    public static class RiderDustInstaller
    {
        private const string CloudMaterialPath = "Assets/_Game/Art/Materials/Cloud.mat";

        [MenuItem("Delivery Rider/Apply Rider Dust")]
        public static void Apply()
        {
            var scene = GameScenes.RequireActive();
            var rider = UnityEngine.Object.FindFirstObjectByType<RiderMotor>(FindObjectsInactive.Include);
            var material = AssetDatabase.LoadAssetAtPath<Material>(CloudMaterialPath);
            if (rider == null || material == null) throw new InvalidOperationException("The rider and the cloud material (Apply Day Sky) are required.");
            var shapes = CloudShapes.LoadOrBuildDust();

            var dust = UnityEngine.Object.FindFirstObjectByType<RiderDust>(FindObjectsInactive.Include);
            if (dust == null)
            {
                var host = new GameObject("RiderDust");
                Undo.RegisterCreatedObjectUndo(host, "Add rider dust");
                dust = host.AddComponent<RiderDust>();
            }
            var data = new SerializedObject(dust);
            data.FindProperty("rider").objectReferenceValue = rider;
            data.FindProperty("material").objectReferenceValue = material;
            var shapesProperty = data.FindProperty("shapes");
            shapesProperty.arraySize = shapes.Length;
            for (int i = 0; i < shapes.Length; i++) shapesProperty.GetArrayElementAtIndex(i).objectReferenceValue = shapes[i];
            data.ApplyModifiedProperties();

            var tools = UnityEngine.Object.FindFirstObjectByType<DevTools>(FindObjectsInactive.Include);
            if (tools != null)
            {
                var toolData = new SerializedObject(tools);
                toolData.FindProperty("riderDust").objectReferenceValue = dust;
                toolData.ApplyModifiedProperties();
            }

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene)) throw new InvalidOperationException("Could not save scene.");
            Debug.Log($"Rider dust applied in {scene.name}: {shapes.Length} puff shapes on {material.name}.");
        }
    }
}
