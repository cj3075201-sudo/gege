using System;
using System.Collections.Generic;

namespace DeliveryRider
{
    // A value the panel changed, for the editor save: the field at PropertyPath of Source (a scene component, a
    // settings asset or a prefab's component) becomes Value (ints and bools as numbers).
    public readonly struct DevChange
    {
        public readonly UnityEngine.Object Source;
        public readonly string PropertyPath;
        public readonly float Value;

        public DevChange(UnityEngine.Object source, string propertyPath, float value)
        {
            Source = source;
            PropertyPath = propertyPath;
            Value = value;
        }
    }

    // Live objects sharing one set of tuned values: a scene component, a settings asset, or every spawned copy of one
    // prefab part. Source is what an editor save writes. Only values changed in the panel are saved or reverted, so
    // values the game itself moves at runtime are left alone.
    public sealed class DevTarget
    {
        private readonly Func<IEnumerable<UnityEngine.Object>> find;
        private readonly List<UnityEngine.Object> live = new List<UnityEngine.Object>();
        private readonly Dictionary<DevValue, float> baseline = new Dictionary<DevValue, float>();
        private readonly Dictionary<DevValue, float> changes = new Dictionary<DevValue, float>();
        private readonly string sessionKey;

        public DevTarget(string title, UnityEngine.Object source, Func<IEnumerable<UnityEngine.Object>> find)
        {
            Title = title;
            Source = source;
            sessionKey = DevTuningSession.Key(title, source);
            this.find = find;
            Refresh();
        }

        public string Title { get; }
        public UnityEngine.Object Source { get; }
        public List<DevValue> Values { get; } = new List<DevValue>();
        public bool Expanded { get; set; }
        public int ChangeCount => changes.Count;

        public void Add(IEnumerable<DevValue> values)
        {
            var sample = Sample;
            foreach (var value in values)
            {
                Values.Add(value);
                baseline[value] = value.Read(sample);
                if (DevTuningSession.TryGet(sessionKey, value.PropertyPath, out float original, out float current, out bool pending))
                {
                    baseline[value] = original;
                    Write(value, current);
                    if (pending) changes[value] = current;
                }
            }
        }

        // Live copies first, so the panel shows what the game uses right now.
        public float Get(DevValue value)
        {
            foreach (var target in live) if (target != null) return value.Read(target);
            return changes.TryGetValue(value, out float raw) ? raw : value.Read(Source);
        }

        public bool IsChanged(DevValue value) => changes.ContainsKey(value);

        public void Set(DevValue value, float raw)
        {
            changes[value] = raw;
            DevTuningSession.Keep(sessionKey, Source, value, baseline[value], raw, true);
            Write(value, raw);
        }

        public void Revert(DevValue value)
        {
            if (!changes.Remove(value)) return;
            Write(value, baseline[value]);
            DevTuningSession.Keep(sessionKey, Source, value, baseline[value], baseline[value], false);
        }

        public void RevertAll()
        {
            foreach (var value in new List<DevValue>(changes.Keys)) Revert(value);
        }

        public void Refresh()
        {
            live.Clear();
            foreach (var target in find()) if (target != null) live.Add(target);
        }

        public void ClearLive() => live.Clear();

        // A freshly spawned copy takes this session's changes before it wakes.
        public void ApplyTo(UnityEngine.Object instance)
        {
            live.Add(instance);
            foreach (var change in changes) change.Key.Write(instance, change.Value);
        }

        public void ReapplyChanges()
        {
            foreach (var change in changes) Write(change.Key, change.Value);
        }

        public IEnumerable<(DevValue value, float before, float after)> Edits
        {
            get { foreach (var change in changes) yield return (change.Key, baseline[change.Key], change.Value); }
        }

        public void CollectChanges(List<DevChange> into)
        {
            foreach (var change in changes) into.Add(new DevChange(Source, change.Key.PropertyPath, change.Value));
        }

        // Saved values become the new baseline, so ending the scene keeps them.
        public void MarkSaved()
        {
            if (changes.Count > 0) DevTuningSession.MarkSaved(Source);
            foreach (var change in changes)
            {
                baseline[change.Key] = change.Value;
                DevTuningSession.Keep(sessionKey, Source, change.Key, change.Value, change.Value, false);
            }
            changes.Clear();
        }

        private UnityEngine.Object Sample
        {
            get
            {
                foreach (var target in live) if (target != null) return target;
                return Source;
            }
        }

        private void Write(DevValue value, float raw)
        {
            foreach (var target in live) if (target != null) value.Write(target, raw);
        }
    }
}
