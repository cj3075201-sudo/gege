using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DeliveryRider.Editor
{
    // Editor side of the developer tools' save button. Settings assets and prefabs are written at once. The scene in
    // Play Mode is a throwaway copy, so scene values wait in SessionState until Play Mode ends; they are then written to
    // the open scene, which is saved.
    [InitializeOnLoad]
    public static class DevToolsSaver
    {
        private const string PendingKey = "DeliveryRider.DevTools.PendingSceneValues";

        [Serializable]
        private sealed class SceneValue
        {
            public string scene, objectPath, componentType, propertyPath;
            public int componentIndex;
            public float value;
        }

        [Serializable]
        private sealed class Pending
        {
            public List<SceneValue> values = new List<SceneValue>();
        }

        static DevToolsSaver()
        {
            DevTools.Saver = Save;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload += DevTuningSession.EndSession;
        }

        private static string Save(IReadOnlyList<DevChange> changes)
        {
            var pending = Load();
            var prefabs = new HashSet<GameObject>();
            int assets = 0, sceneValues = 0;
            foreach (var change in changes)
            {
                if (change.Source is Component component && component.gameObject.scene.IsValid())
                {
                    var entry = new SceneValue
                    {
                        scene = component.gameObject.scene.path,
                        objectPath = HierarchyPath(component.transform),
                        componentType = component.GetType().AssemblyQualifiedName,
                        componentIndex = Array.IndexOf(component.GetComponents(component.GetType()), component),
                        propertyPath = change.PropertyPath,
                        value = change.Value,
                    };
                    pending.values.RemoveAll(v => v.scene == entry.scene && v.objectPath == entry.objectPath && v.componentType == entry.componentType
                        && v.componentIndex == entry.componentIndex && v.propertyPath == entry.propertyPath);
                    pending.values.Add(entry);
                    sceneValues++;
                    continue;
                }
                // Settings assets already hold the value in memory; prefab components get it here.
                var data = new SerializedObject(change.Source);
                if (!Assign(data.FindProperty(change.PropertyPath), change.Value))
                    throw new InvalidOperationException($"{change.Source.name}: {change.PropertyPath} cannot be saved.");
                data.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(change.Source);
                if (change.Source is Component part) prefabs.Add(part.transform.root.gameObject);
                else AssetDatabase.SaveAssetIfDirty(change.Source);
                assets++;
            }
            foreach (var prefab in prefabs) PrefabUtility.SavePrefabAsset(prefab);
            SessionState.SetString(PendingKey, JsonUtility.ToJson(pending));
            return $"저장했습니다. 설정·프리팹 값 {assets}개는 바로 기록했고, 씬 값 {sceneValues}개는 Play를 끝내면 씬에 기록합니다.";
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode) DevTuningSession.EndSession();
            if (state != PlayModeStateChange.EnteredEditMode) return;
            var pending = Load();
            if (pending.values.Count == 0) return;
            SessionState.EraseString(PendingKey);
            var scenes = new HashSet<Scene>();
            int written = 0;
            foreach (var entry in pending.values)
            {
                var component = FindComponent(entry);
                var data = component != null ? new SerializedObject(component) : null;
                if (data == null || !Assign(data.FindProperty(entry.propertyPath), entry.value))
                {
                    Debug.LogWarning($"Dev tools: {entry.objectPath} {entry.propertyPath} was not found in the scene and is not saved.");
                    continue;
                }
                data.ApplyModifiedProperties();
                scenes.Add(component.gameObject.scene);
                written++;
            }
            foreach (var scene in scenes)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }
            Debug.Log($"Dev tools: {written} scene values saved after Play Mode.");
        }

        private static Component FindComponent(SceneValue entry)
        {
            var scene = SceneManager.GetSceneByPath(entry.scene);
            var type = Type.GetType(entry.componentType);
            if (!scene.isLoaded || type == null) return null;
            int slash = entry.objectPath.IndexOf('/');
            string rootName = slash < 0 ? entry.objectPath : entry.objectPath.Substring(0, slash);
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name != rootName) continue;
                var holder = slash < 0 ? root.transform : root.transform.Find(entry.objectPath.Substring(slash + 1));
                if (holder == null) continue;
                var parts = holder.GetComponents(type);
                if (entry.componentIndex >= 0 && entry.componentIndex < parts.Length) return parts[entry.componentIndex];
            }
            return null;
        }

        private static string HierarchyPath(Transform transform)
        {
            string path = transform.name;
            for (var t = transform.parent; t != null; t = t.parent) path = t.name + "/" + path;
            return path;
        }

        private static bool Assign(SerializedProperty property, float value)
        {
            if (property == null) return false;
            switch (property.propertyType)
            {
                case SerializedPropertyType.Float: property.floatValue = value; return true;
                case SerializedPropertyType.Integer: property.intValue = Mathf.RoundToInt(value); return true;
                case SerializedPropertyType.Boolean: property.boolValue = value >= 0.5f; return true;
                default: return false;
            }
        }

        private static Pending Load()
        {
            string json = SessionState.GetString(PendingKey, "");
            return json.Length > 0 ? JsonUtility.FromJson<Pending>(json) : new Pending();
        }
    }
}
