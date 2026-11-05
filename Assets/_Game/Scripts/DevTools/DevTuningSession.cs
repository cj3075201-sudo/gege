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
        private sealed class Schedule
        {
            public int[] Counts, Main, Alley;
        }
        private static readonly Dictionary<HazardSpawnSettings.Kind, Schedule> originalDayCounts = new Dictionary<HazardSpawnSettings.Kind, Schedule>();

        // Runtime expansion is reversible unless the editor explicitly saves this asset.
        public static void EnsureFiveDays(HazardSpawnSettings settings)
        {
            foreach (var kind in settings.Kinds)
            {
                bool expand = kind.role == HazardRole.Object ? kind.countPerDay.Length < 5
                    : kind.mainCountPerDay.Length < 5 || kind.alleyCountPerDay.Length < 5;
                if (!expand) continue;
                if (!originalDayCounts.ContainsKey(kind)) originalDayCounts[kind] = new Schedule
                    { Counts=kind.countPerDay, Main=kind.mainCountPerDay, Alley=kind.alleyCountPerDay };
                var counts=new int[5];var main=new int[5];var alley=new int[5];
                for(int i=0;i<5;i++){counts[i]=kind.Count(i+1);main[i]=kind.MainCount(i+1);alley[i]=kind.AlleyCount(i+1);}
                if(kind.role==HazardRole.Object)kind.countPerDay=counts;
                else{kind.mainCountPerDay=main;kind.alleyCountPerDay=alley;}
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
            foreach (var original in originalDayCounts)
            {
                original.Key.countPerDay=original.Value.Counts;
                original.Key.mainCountPerDay=original.Value.Main;
                original.Key.alleyCountPerDay=original.Value.Alley;
            }
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
