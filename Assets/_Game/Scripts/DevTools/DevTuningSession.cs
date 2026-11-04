using System.Collections.Generic;
using UnityEngine;

namespace DeliveryRider
{
    // Plain values survive scene loads. Only assets are restored here; scene instances are throwaway copies.
    public static class DevTuningSession
    {
        private sealed class Edit
        {
            public UnityEngine.Object Source;
            public DevValue Field;
            public float Baseline, Current;
            public bool Pending;
        }

        private static readonly Dictionary<string, Dictionary<string, Edit>> targets = new Dictionary<string, Dictionary<string, Edit>>();
        private static readonly Dictionary<HazardSpawnSettings.Kind, int[]> originalDayCounts = new Dictionary<HazardSpawnSettings.Kind, int[]>();

        // Legacy one-value schedules repeat on every day. Expand only the runtime array for four independent controls.
        public static void EnsureFourDays(HazardSpawnSettings settings)
        {
            foreach (var kind in settings.Kinds)
            {
                if (kind.role != HazardRole.Object || kind.countPerDay.Length >= 4 || originalDayCounts.ContainsKey(kind)) continue;
                originalDayCounts[kind] = kind.countPerDay;
                var expanded = new int[4];
                for (int i = 0; i < expanded.Length; i++) expanded[i] = kind.Count(i + 1);
                kind.countPerDay = expanded;
            }
        }

        public static void MarkSaved(UnityEngine.Object source)
        {
            if (source is HazardSpawnSettings settings)
                foreach (var kind in settings.Kinds) originalDayCounts.Remove(kind);
        }

        public static string Key(string title, UnityEngine.Object source)
        {
            if (source is Component component)
            {
                string path = component.name;
                for (var parent = component.transform.parent; parent != null; parent = parent.parent) path = parent.name + "/" + path;
                int index = System.Array.IndexOf(component.GetComponents(component.GetType()), component);
                return $"{title}|{component.gameObject.scene.path}|{path}|{component.GetType().FullName}|{index}";
            }
            return $"{title}|{source.GetInstanceID()}";
        }

        public static bool TryGet(string key, string path, out float baseline, out float current, out bool pending)
        {
            if (targets.TryGetValue(key, out var values) && values.TryGetValue(path, out var edit))
            {
                baseline = edit.Baseline;
                current = edit.Current;
                pending = edit.Pending;
                return true;
            }
            baseline = current = 0;
            pending = false;
            return false;
        }

        public static void Keep(string key, UnityEngine.Object source, DevValue field, float baseline, float current, bool pending)
        {
            if (!targets.TryGetValue(key, out var values)) targets[key] = values = new Dictionary<string, Edit>();
            values[field.PropertyPath] = new Edit { Source = source, Field = field, Baseline = baseline, Current = current, Pending = pending };
        }

        public static void EndSession()
        {
            foreach (var values in targets.Values)
                foreach (var edit in values.Values)
                    if (edit.Pending && edit.Source is ScriptableObject && edit.Source != null)
                        edit.Field.Write(edit.Source, edit.Baseline);
            targets.Clear();
            foreach (var original in originalDayCounts) original.Key.countPerDay = original.Value;
            originalDayCounts.Clear();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void BeginSession()
        {
            EndSession();
            Application.quitting -= EndSession;
            Application.quitting += EndSession;
        }
    }
}
