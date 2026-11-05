using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DeliveryRider.Editor
{
    // Edit-mode batch checks: real accident entry points and spawning, with throwaway objects only.
    public static class DevToolsScaleChecks
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static void Run()
        {
            DevTuningSession.EndSession();
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            try
            {
                CheckFlight();
                CheckCounts();
                CheckTriggers();
                CheckDetectionDistances();
                CheckDayPersistence();
                Debug.Log("DevToolsScaleChecks PASS: all checks.");
            }
            finally
            {
                DevTuningSession.EndSession();
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        private static void CheckFlight()
        {
            var player = new GameObject("ScaleCheckPlayer");
            player.AddComponent<Rigidbody>().useGravity = false;
            player.AddComponent<CapsuleCollider>();
            var motor = player.AddComponent<RiderMotor>();
            var session = new GameObject("ScaleCheckSession").AddComponent<DeliverySession>();
            Set(session, "player", player.transform);
            Set(motor, "session", session);
            Call(motor, "Awake");
            Vector3 outgoing = default;
            motor.ReactionImpulse += velocity => outgoing = velocity;
            var incoming = new Vector3(3f, 5f, -7f);
            void Reset()
            {
                Set(motor, "lastHazardHitAt", float.NegativeInfinity);
                Set(motor, "trafficRecoveryActive", false);
                Set(motor, "nextTrafficHitAt", float.NegativeInfinity);
            }
            try
            {
                foreach (var scales in new[] { (1f, 1f), (2f, 1f), (1f, 2f), (4f, 4f) })
                {
                    Reset();
                    Set(motor, "hazardHeightScale", scales.Item1);
                    Set(motor, "hazardDistanceScale", scales.Item2);
                    Check(motor.TryHazardCrash(session, incoming, 1f), "hazard crash accepted");
                    Equal(new Vector3(incoming.x * scales.Item2, incoming.y * scales.Item1, incoming.z * scales.Item2), outgoing,
                        $"height {scales.Item1}, distance {scales.Item2}");
                    Reset();
                    Check(motor.TryTrafficCrash(incoming, 1f), "traffic crash accepted");
                    Equal(incoming, outgoing, "traffic velocity unchanged");
                    Set(motor, "nextRelaunchAt", float.NegativeInfinity);
                    Check(motor.TryTrafficRelaunch(incoming, 1f, 0f), "traffic relaunch accepted");
                    Equal(incoming, outgoing, "traffic relaunch unchanged");
                }
                Debug.Log("DevToolsScaleChecks PASS: unit launch, independent height/distance, traffic exclusion.");
            }
            finally { UnityEngine.Object.DestroyImmediate(player); UnityEngine.Object.DestroyImmediate(session.gameObject); }
        }

        private static void CheckCounts()
        {
            var host = new GameObject("ScaleCheckPlanner");
            host.SetActive(false);
            var planner = host.AddComponent<HazardPlanner>();
            var session = host.AddComponent<DeliverySession>();
            var roads = host.AddComponent<RoadNetwork>();
            var settings = ScriptableObject.CreateInstance<HazardSpawnSettings>();
            var candidates = ScriptableObject.CreateInstance<HazardCandidates>();
            var template = new GameObject("ScaleCheckTemplate");
            template.SetActive(false);
            var kinds = new[]
            {
                new HazardSpawnSettings.Kind { type = "pigeonflock", role = HazardRole.Object, prefab = template, countPerDay = new[] { 3 } },
                new HazardSpawnSettings.Kind { type = "manhole", role = HazardRole.Object, prefab = template, countPerDay = new[] { 4 } },
                new HazardSpawnSettings.Kind { type = "duck", role = HazardRole.Trigger, prefab = template, poolSize = 2 },
            };
            settings.Configure(kinds);
            var spots = new List<HazardCandidate>();
            for (int k = 0; k < kinds.Length; k++)
                for (int i = 0; i < 30; i++)
                    spots.Add(new HazardCandidate { type = kinds[k].type, spot = -1, position = new Vector3(i * 30f, 0f, k * 100f), rotation = Quaternion.identity });
            candidates.Configure(Array.Empty<HazardCandidates.Spot>(), spots.ToArray());
            Set(planner, "session", session); Set(planner, "ground", roads);
            Set(planner, "settings", settings); Set(planner, "candidates", candidates);
            Set(planner, "randomEachRun", false);
            try
            {
                Check(DevValue.Find(planner, "hazardCountScale", "count").Read(planner) == 1f, "default count multiplier is one");
                foreach (int seed in new[] { 0, 11, 73, 12345 })
                {
                    Set(planner, "seed", seed);
                    Set(planner, "hazardCountScale", 1f);
                    Call(planner, "Spawn");
                    // Baseline algorithm: same priority, candidates and Random calls as the pre-scale planner.
                    var random = new System.Random(seed);
                    var baseline = new List<Vector3>();
                    foreach (var kind in kinds)
                    {
                        var indices = Enumerable.Range(0, spots.Count).Where(i => spots[i].type == kind.type).ToList();
                        for (int i = indices.Count - 1; i > 0; i--)
                        { int j = random.Next(i + 1); (indices[i], indices[j]) = (indices[j], indices[i]); }
                        baseline.AddRange(indices.Take(kind.Count(1)).Select(i => spots[i].position));
                    }
                    Check(baseline.SequenceEqual(planner.Slots.Select(s => s.transform.position)), "scale 1 matches original seeded placement " + seed);
                    Set(planner, "hazardCountScale", 2f);
                    Set(planner, "hazardTriggerScale", 2f);
                    planner.Respawn();
                    Check(planner.Slots.All(slot => slot.TriggerScale == 2f), "trigger scale applied at spawn");
                    Set(planner, "hazardTriggerScale", 3f); Call(planner, "Update");
                    Check(planner.Slots.All(slot => slot.TriggerScale == 3f), "trigger scale applied to existing hazards");
                    Check(planner.Slots.Count == baseline.Count * 2, "count 2 doubles placements with room");
                    var requested = (IReadOnlyDictionary<string, int>)typeof(HazardPlanner).GetProperty("RequestedCounts").GetValue(planner);
                    foreach (var kind in kinds) Check(requested[kind.type] == kind.Count(1) * 2, "requested count doubled: " + kind.type);
                    Clear(planner);
                }
                Set(settings, "spacing", 700f); Set(settings, "sameTypeSpacing", 1000f);
                planner.Respawn();
                int totalRequested = planner.RequestedCounts.Values.Sum();
                Check(totalRequested > planner.Slots.Count, "insufficient candidates retain requested counts");
                for (int i = 0; i < planner.Slots.Count; i++)
                    for (int j = i + 1; j < planner.Slots.Count; j++)
                    {
                        var a = planner.Slots[i]; var b = planner.Slots[j];
                        float gap = a.Type == b.Type ? settings.SameTypeSpacing : settings.Spacing;
                        Check(Vector3.ProjectOnPlane(a.transform.position - b.transform.position, Vector3.up).sqrMagnitude >= gap * gap,
                            "scaled counts respect both spacing limits");
                    }
                var panel = host.AddComponent<DevTools>();
                Set(panel, "hazards", planner); Set(panel, "hazardSettings", settings);
                string summary = (string)Call(panel, "PlacedSummary");
                foreach (var kind in kinds)
                    Check(summary.Contains(planner.Slots.Count(slot => slot.Type == kind.type) + "/" + planner.RequestedCounts[kind.type]),
                        "F1 shows actual/requested for " + kind.type);
                // Fractional open-share must round before applying the new multiplier (3 * .5 rounds to 2, then 4).
                Check(HazardZoneFilter.ScaleCount(HazardZoneFilter.ScaleCount(3, 0.5f), 2f) == 4, "open-share rounding happens first");
                Debug.Log("DevToolsScaleChecks PASS: 4 seeds match original placement at 1; 2 doubles all requested/placed counts; Respawn/live trigger updates, spacing limits and F1 actual/requested.");
            }
            finally
            {
                Clear(planner);
                UnityEngine.Object.DestroyImmediate(host); UnityEngine.Object.DestroyImmediate(template);
                UnityEngine.Object.DestroyImmediate(settings); UnityEngine.Object.DestroyImmediate(candidates);
            }
        }

        private static void Clear(HazardPlanner planner)
        {
            foreach (var slot in planner.Slots.ToArray()) UnityEngine.Object.DestroyImmediate(slot.gameObject);
            ((List<HazardSlot>)planner.Slots).Clear();
        }

        private static void CheckTriggers()
        {
            foreach (string name in new[] { "Manhole", "Hydrant", "Muscleman", "PigeonFlock", "Chickens", "CelebrityCrowd", "PigeonSnatch", "RollingTire", "DuckFlock", "MelonPile", "SwingDoor" })
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/_Game/Prefabs/Hazards/{name}.prefab");
                string original = EditorJsonUtility.ToJson(prefab);
                var sourceParts = prefab.GetComponentsInChildren<MonoBehaviour>(true);
                var originalParts = sourceParts.Select(part => EditorJsonUtility.ToJson(part)).ToArray();
                var sourceBox = prefab.GetComponent<BoxCollider>();
                var sourceSphere = prefab.GetComponent<SphereCollider>();
                Vector3 boxSize = sourceBox != null ? sourceBox.size : Vector3.zero;
                float sphereRadius = sourceSphere != null ? sourceSphere.radius : 0f;
                var instance = UnityEngine.Object.Instantiate(prefab);
                var slot = instance.AddComponent<HazardSlot>();
                try
                {
                    foreach (float scale in new[] { 2f, 4f, 0.5f, 1f })
                    {
                        Call(slot, "SetTriggerScale", scale);
                        if (sourceBox != null)
                        {
                            Equal(boxSize * scale, instance.GetComponent<BoxCollider>().size, name + " instance box scaled without compounding");
                            Equal(boxSize, sourceBox.size, name + " prefab unchanged");
                        }
                        if (sourceSphere != null)
                        {
                            Check(instance.GetComponent<SphereCollider>().radius == sphereRadius * scale, name + " instance sphere scaled");
                            Check(sourceSphere.radius == sphereRadius, name + " prefab sphere unchanged");
                        }
                    }
                    Check(EditorJsonUtility.ToJson(prefab) == original, name + " source object unchanged");
                    for (int i = 0; i < sourceParts.Length; i++)
                        Check(EditorJsonUtility.ToJson(sourceParts[i]) == originalParts[i], name + " source component unchanged");
                    Check(!EditorUtility.IsDirty(prefab), name + " prefab not dirty");
                }
                finally { UnityEngine.Object.DestroyImmediate(instance); }
            }
            Debug.Log("DevToolsScaleChecks PASS: repeated instance trigger scaling and return to 1; source prefabs unchanged.");
        }

        private static void CheckDetectionDistances()
        {
            var player = new GameObject("DetectionPlayer");
            player.transform.position = new Vector3(1000f, 1f, 1000f);
            player.AddComponent<Rigidbody>().useGravity = false;
            player.AddComponent<CapsuleCollider>();
            var rider = player.AddComponent<RiderMotor>();
            var host = new GameObject("DetectionHost");
            var session = host.AddComponent<DeliverySession>();
            Set(session, "player", player.transform);
            Set(rider, "session", session);
            Call(rider, "Awake");
            typeof(DeliverySession).GetProperty("Stage").SetValue(session, DeliveryStage.Carrying);
            T Spawn<T>(string name) where T : Component
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/_Game/Prefabs/Hazards/{name}.prefab");
                var obj = UnityEngine.Object.Instantiate(prefab, host.transform);
                obj.transform.position = player.transform.position;
                var slot = obj.AddComponent<HazardSlot>();
                var hazard = obj.GetComponent<T>();
                Set(hazard, "slot", slot);
                Set(hazard, "rider", rider);
                if (typeof(T).GetField("session", Any) != null) Set(hazard, "session", session);
                return hazard;
            }
            void Reset()
            {
                Set(rider, "lastHazardHitAt", float.NegativeInfinity);
                Set(rider, "trafficRecoveryActive", false);
                Set(rider, "nextTrafficHitAt", float.NegativeInfinity);
            }
            try
            {
                var chicken = Spawn<ChaseFlockHazard>("Chickens");
                var point = player.transform.position + Vector3.right * 1.5f;
                foreach (float scale in new[] { 1f, 2f, 0.5f })
                {
                    chicken.GetComponent<HazardSlot>().SetTriggerScale(scale);
                    Check((bool)Call(chicken, "Near", point, 1f) == (scale == 2f), "chicken scaled proximity " + scale);
                }
                UnityEngine.Object.DestroyImmediate(chicken.gameObject);

                var pigeon = Spawn<PigeonSnatchHazard>("PigeonSnatch");
                Set(pigeon, "triggerRadius", 1f);
                ((Transform)typeof(PigeonSnatchHazard).GetField("perch", Any).GetValue(pigeon)).position = point;
                pigeon.GetComponent<HazardSlot>().SetTriggerScale(1f);
                Call(pigeon, "FixedUpdate");
                Check(typeof(PigeonSnatchHazard).GetField("phase", Any).GetValue(pigeon).ToString() == "Ready", "pigeon outside base trigger");
                pigeon.GetComponent<HazardSlot>().SetTriggerScale(2f);
                Call(pigeon, "FixedUpdate");
                Check(typeof(PigeonSnatchHazard).GetField("phase", Any).GetValue(pigeon).ToString() == "Warning", "pigeon inside enlarged trigger");
                UnityEngine.Object.DestroyImmediate(pigeon.gameObject);

                var tire = Spawn<RollingTireHazard>("RollingTire");
                Set(tire, "approachCenter", Vector3.zero); Set(tire, "approachSize", Vector3.one * 2f);
                tire.transform.position = point;
                foreach (float scale in new[] { 1f, 2f, 0.5f })
                {
                    tire.GetComponent<HazardSlot>().SetTriggerScale(scale);
                    Check((bool)Call(tire, "InApproach") == (scale == 2f), "tire approach scales " + scale);
                }
                // The normal and small tire share this component; changing the unscaled radius models both.
                foreach (float radius in new[] { 0.55f, 0.3f })
                    foreach (float scale in new[] { 1f, 2f, 0.5f })
                    {
                        Reset();
                        Set(tire, "radius", radius); Set(tire, "hitThisCycle", false);
                        tire.GetComponent<HazardSlot>().SetTriggerScale(scale);
                        Physics.SyncTransforms();
                        Call(tire, "CheckContact", player.transform.position + Vector3.right * (0.5f + radius * 1.5f), Vector3.zero, 0f);
                        Check(rider.IsRecovering == (scale == 2f), "tire rider radius scales " + radius + "/" + scale);
                    }
                UnityEngine.Object.DestroyImmediate(tire.gameObject);

                Reset();
                var duck = Spawn<CrossingFlockHazard>("DuckFlock");
                Set(duck, "localStart", Vector3.zero); Set(duck, "localEnd", Vector3.forward * 10f);
                Set(duck, "hitRadius", 1f);
                duck.transform.position = player.transform.position + Vector3.right * 1.5f;
                foreach (float scale in new[] { 1f, 2f, 0.5f })
                {
                    Reset(); Set(duck, "head", 0f); Set(duck, "nextHitAt", 0f);
                    duck.GetComponent<HazardSlot>().SetTriggerScale(scale);
                    Call(duck, "Advance");
                    Check(rider.IsRecovering == (scale == 2f), "duck hitRadius scales " + scale);
                }
                UnityEngine.Object.DestroyImmediate(duck.gameObject);

                Reset();
                var muscle = Spawn<MusclemanHazard>("Muscleman");
                Set(muscle, "runnerRadius", 0.75f);
                var from = player.transform.position + Vector3.right * 1.7f - Vector3.up;
                foreach (float scale in new[] { 1f, 2f, 0.5f })
                {
                    Reset();
                    ((HashSet<Rigidbody>)typeof(MusclemanHazard).GetField("struck", Any).GetValue(muscle)).Clear();
                    muscle.GetComponent<HazardSlot>().SetTriggerScale(scale);
                    Physics.SyncTransforms(); Call(muscle, "ShoveAlong", from, from, Vector3.forward);
                    Check(rider.IsRecovering == (scale == 2f), "muscle rider query scales " + scale);
                }
                UnityEngine.Object.DestroyImmediate(muscle.gameObject);
                Debug.Log("DevToolsScaleChecks PASS: live chicken/pigeon distance, normal/small tire approach and contact, duck hitRadius, muscle shove at 0.5/1/2.");
            }
            finally { UnityEngine.Object.DestroyImmediate(host); UnityEngine.Object.DestroyImmediate(player); }
        }

        private static void CheckDayPersistence()
        {
            DevTuningSession.EndSession();
            for (int day = 1; day <= 2; day++)
            {
                var owner = new GameObject("ScaleCheckDay");
                owner.SetActive(false);
                var motor = owner.AddComponent<RiderMotor>();
                var planner = owner.AddComponent<HazardPlanner>();
                try
                {
                    foreach (var field in new[] { "hazardHeightScale", "hazardDistanceScale", "hazardCountScale", "hazardTriggerScale" })
                    {
                        UnityEngine.Object source = field == "hazardHeightScale" || field == "hazardDistanceScale" ? motor : planner;
                        var values = DevValue.Discover(source);
                        var value = values.Single(v => v.PropertyPath == field);
                        Check(value.Label.Contains("배율"), "Korean label for " + field);
                        Check(value.Min == (field == "hazardTriggerScale" ? 0.5f : 0.25f), "minimum " + field);
                        Check(value.Max == (field == "hazardCountScale" ? 5f : 4f), "maximum " + field);
                        var target = new DevTarget("daily scales", source, () => new[] { source });
                        target.Add(new[] { value });
                        if (day == 1) { Check(target.Get(value) == 1f, "default one " + field); target.Set(value, 2f); }
                        else { Check(target.Get(value) == 2f, "day reload retains " + field); target.RevertAll(); Check(target.Get(value) == 1f, "revert baseline " + field); }
                    }
                }
                finally { UnityEngine.Object.DestroyImmediate(owner); }
            }
            Debug.Log("DevToolsScaleChecks PASS: four Korean/ranged F1 values survive daily component replacement and revert.");
        }

        private static void Set(object target, string field, object value)
        {
            var info = target.GetType().GetField(field, Any);
            if (info == null) throw new InvalidOperationException("Missing scale field: " + target.GetType().Name + "." + field);
            info.SetValue(target, value);
        }
        private static object Call(object target, string method, params object[] args) => target.GetType().GetMethod(method, Any).Invoke(target, args);
        private static void Check(bool pass, string message) { if (!pass) throw new InvalidOperationException(message); }
        private static void Equal(Vector3 expected, Vector3 actual, string message) => Check(expected.Equals(actual), $"{message}: {expected} != {actual}");
    }
}
