using System;
using UnityEngine;

namespace DeliveryRider.Editor
{
    public static class DevToolsStatsChecks
    {
        public static void Run()
        {
            CheckMath();
            RunTuning();
            Debug.Log("DevToolsStatsChecks PASS: all checks.");
        }

        private static void CheckMath()
        {
            var pace = RunStatsMath.Calculate(12000, 300, 600, 5000, 100000, 0);
            Equal(24000, pace.DailyIncome, "daily income");
            Equal(19000, pace.DailyNet, "daily net");
            Equal(100000d / 19000, pace.DaysLeft, "days left");
            Equal(0.5 + 100000d / 19000, pace.TotalDays, "total days");
            Equal((300 + 100000d / 19000 * 600) / 60, pace.TotalMinutes, "total minutes");
            Equal(88000, RunStatsMath.Calculate(12000, 300, 600, 5000, 100000, 12000).Remaining, "cash deduction");
            var zero = RunStatsMath.Calculate(0, 0, 600, 5000, 100000, 0);
            if (!double.IsPositiveInfinity(zero.DaysLeft)) throw new Exception("zero net must not repay");
            Equal(0, zero.PlayedDays, "zero elapsed");
            Equal(0, RunStatsMath.Calculate(12000, 300, 600, 5000, 100000, 120000).DaysLeft, "excess cash");
            if (!double.IsPositiveInfinity(RunStatsMath.Calculate(2500, 300, 600, 5000, 100000, 0).DaysLeft))
                throw new Exception("exactly zero daily net must not repay");
            Debug.Log("DevToolsStatsChecks PASS: HTML pace and repayment, zero elapsed/net, excess cash.");
        }

        public static void RunTuning()
        {
            DevTuningSession.EndSession();
            // A new scene component with the same identity must receive even unsaved edits.
            GameObject owner = new GameObject("DevToolsStatsCheckPlanner");
            try
            {
                var source = owner.AddComponent<HazardPlanner>();
                var value = DevValue.Find(source, "seed", "seed");
                var target = new DevTarget("test planner", source, () => new UnityEngine.Object[] { source });
                target.Add(new[] { value });
                target.Set(value, 73);
                UnityEngine.Object.DestroyImmediate(owner);
                owner = new GameObject("DevToolsStatsCheckPlanner");
                var replacement = owner.AddComponent<HazardPlanner>();
                var nextValue = DevValue.Find(replacement, "seed", "seed");
                var next = new DevTarget("test planner", replacement, () => new UnityEngine.Object[] { replacement });
                next.Add(new[] { nextValue });
                Equal(73, next.Get(nextValue), "scene reload retains unsaved seed");
                Equal(1, next.ChangeCount, "scene reload preserves pending save");
                var pending = new System.Collections.Generic.List<DevChange>();
                next.CollectChanges(pending);
                if (pending.Count != 1 || pending[0].Source != replacement || pending[0].PropertyPath != "seed" || pending[0].Value != 73)
                    throw new Exception("reloaded changes must use the replacement source and original Saver field path");
                next.RevertAll();
                Equal(11, next.Get(nextValue), "scene reload reverts to original seed");
                next.Set(nextValue, 37);
                next.MarkSaved();
                next.Set(nextValue, 52);
                next.RevertAll();
                var savedCopy = new GameObject("DevToolsStatsCheckPlanner");
                try
                {
                    var savedSource = savedCopy.AddComponent<HazardPlanner>();
                    var savedValue = DevValue.Find(savedSource, "seed", "seed");
                    var savedTarget = new DevTarget("test planner", savedSource, () => new UnityEngine.Object[] { savedSource });
                    savedTarget.Add(new[] { savedValue });
                    Equal(37, savedTarget.Get(savedValue), "saved scene value survives reload after revert");
                    Equal(0, savedTarget.ChangeCount, "saved scene value not pending");
                }
                finally { UnityEngine.Object.DestroyImmediate(savedCopy); }
            }
            finally { if (owner != null) UnityEngine.Object.DestroyImmediate(owner); }

            var settings = ScriptableObject.CreateInstance<DeliveryDaySettings>();
            var duration = DevValue.Find(settings, "durationSeconds", "duration");
            float original = duration.Read(settings);
            try
            {
                var first = new DevTarget("test day", settings, () => new UnityEngine.Object[] { settings });
                first.Add(new[] { duration });
                first.Set(duration, 900);
                var nextValue = DevValue.Find(settings, "durationSeconds", "duration");
                var next = new DevTarget("test day", settings, () => new UnityEngine.Object[] { settings });
                next.Add(new[] { nextValue });
                next.RevertAll();
                Equal(original, nextValue.Read(settings), "shared asset original survives target recreation");
                next.Set(nextValue, 700);
                next.MarkSaved();
                next.Set(nextValue, 800);
                DevTuningSession.EndSession();
                Equal(700, nextValue.Read(settings), "saved value becomes restore baseline");
            }
            finally
            {
                duration.Write(settings, original);
                UnityEngine.Object.DestroyImmediate(settings);
            }
            var hazardSettings = ScriptableObject.CreateInstance<HazardSpawnSettings>();
            var kind = new HazardSpawnSettings.Kind { role = HazardRole.Object, countPerDay = new[] { 5 } };
            hazardSettings.Configure(new[] { kind });
            try
            {
                DevTuningSession.EnsureFourDays(hazardSettings);
                Equal(4, kind.countPerDay.Length, "four independently editable days");
                Equal(5, kind.Count(4), "fourth day keeps original repeated value");
                kind.countPerDay[3] = 8;
                Equal(5, kind.Count(1), "fourth day edit does not change first day");
                var untouched = new DevTarget("untouched schedule", hazardSettings, () => new UnityEngine.Object[] { hazardSettings });
                untouched.Add(new[] { DevValue.Find(hazardSettings, "spacing", "spacing") });
                untouched.MarkSaved();
                DevTuningSession.EndSession();
                Equal(1, kind.countPerDay.Length, "unsaved schedule shape restored");
                Equal(5, kind.Count(4), "unsaved schedule values restored");
                DevTuningSession.EnsureFourDays(hazardSettings);
                kind.countPerDay[3] = 9;
                DevTuningSession.MarkSaved(hazardSettings);
                DevTuningSession.EndSession();
                Equal(4, kind.countPerDay.Length, "saved schedule shape kept");
                Equal(9, kind.Count(4), "saved fourth day kept");
            }
            finally { UnityEngine.Object.DestroyImmediate(hazardSettings); DevTuningSession.EndSession(); }
            Debug.Log("DevToolsStatsChecks PASS: scene recreation, shared asset restore, saved baseline.");
        }

        private static void Equal(double expected, double actual, string name)
        {
            if (double.IsNaN(actual) || Math.Abs(expected - actual) > 0.000001)
                throw new Exception($"{name}: expected {expected}, got {actual}");
        }
    }
}
