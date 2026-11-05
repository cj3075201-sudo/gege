using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DeliveryRider.Editor
{
    public static class TrafficZoneChecks
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        public static void Run()
        {
            CheckSynthetic();
            CheckMapDays();
            Debug.Log("TrafficZoneChecks PASS: all checks (actual spawning/selection + graph movement; not Play).");
        }

        private static void CheckSynthetic()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var host = new GameObject("TrafficFixture");
            var network = host.AddComponent<TrafficNetwork>();
            var values = DevValue.Discover(network);
            for (int i = 0; i < 5; i++)
                Check(values.Exists(v => v.PropertyPath == $"vehicleCountByDay.Array.data[{i}]"), "F1 daily count " + (i + 1));
            Check(DevLabels.Find(nameof(TrafficNetwork), "vehicleCountByDay") != null, "Korean daily count label");
            var counts = Get<int[]>(network, "vehicleCountByDay");
            Check(counts.Length == 5 && counts[0] == 30 && counts[1] == 40 && counts[2] == 50 && counts[3] == 60 && counts[4] == 60, "30/40/50/60/60 defaults");
            var map = ScriptableObject.CreateInstance<ZoneMap>();
            Set(map, "zones", new[] { Zone("C", 0f), Zone("A", 100f) });
            var session = new GameObject("Session").AddComponent<DeliverySession>();
            Set(session, "zoneMap", map); Set(session, "openZones", new[] { "C" });
            Set(session, "<Progress>k__BackingField", new RunProgress(1, 0));
            var prefab = AssetDatabase.LoadAssetAtPath<TrafficCar>("Assets/_Game/Prefabs/Traffic/Car.prefab");
            var paths = new[]
            {
                Lane(10f, new[] { 0, 1, 2 }), // safe loop, closed branch, open dead end
                Lane(150f, new[] { 1 }),
                Lane(60f, new[] { 1 })
            };
            network.Configure(session, paths, new[] { new TrafficJunctionData() },
                new[] { new TrafficNetwork.VehicleKind { prefab = prefab, share = 1f } });
            Call(network, "Awake"); Call(network, "RefreshOpenPaths");
            Check((bool)Call(network, "IsPathOpen", 0), "open loop retained");
            Check(!(bool)Call(network, "IsPathOpen", 1), "closed path excluded");
            Check(!(bool)Call(network, "IsPathOpen", 2), "open dead end pruned before a car enters it");
            var random = new System.Random(7);
            for (int i = 0; i < 1000; i++) Check(network.ChooseNext(0, random) == 0, "never chooses closed/dead branch");
            var crossing = Lane(10f, new[] { 0 });
            crossing.points = new[] { new Vector3(10f, 0f, 50f), new Vector3(150f, 0f, 50f), new Vector3(20f, 0f, 50f) };
            crossing.cumulative = new[] { 0f, 140f, 270f }; crossing.length = 270f;
            network.Configure(session, new[] { crossing }, Array.Empty<TrafficJunctionData>(), Array.Empty<TrafficNetwork.VehicleKind>());
            Call(network, "RefreshOpenPaths");
            Check(!(bool)Call(network, "IsPathOpen", 0), "closed middle rejects path with open endpoints");
            // All old configurations use the original vehicleCount and do not prune the old graph.
            network.Configure(null, paths, Array.Empty<TrafficJunctionData>(), new[] { new TrafficNetwork.VehicleKind { prefab = prefab, share = 1f } });
            Set(network, "vehicleCount", 7); Set(network, "randomEachRun", false);
            Call(network, "Awake"); Call(network, "Spawn");
            Check(network.Cars.Count == 7, "no session: legacy fleet count");
            Check((bool)Call(network, "IsPathOpen", 2), "legacy dead-end behavior unchanged");
            // Zero weights in a legacy scene still return the first successor, as before.
            paths[0].nextWeights = new[] { 0f, 0f, 0f };
            Check(network.ChooseNext(0, new System.Random(7)) == 0, "legacy zero-weight selection unchanged");
            paths[0].nextWeights = Array.Empty<float>();
            var lead = network.Cars[0];
            foreach (var car in network.Cars) if (car != lead) UnityEngine.Object.DestroyImmediate(car.gameObject);
            var signal = new GameObject("TestSignal").AddComponent<PedestrianCrossingSignal>();
            Set(network, "signalStops", new[] { new TrafficSignalStop { path = lead.PathIndex, distance = lead.Distance + 10f, signal = signal } });
            var phase = typeof(PedestrianCrossingSignal).GetField("phase", Any);
            phase.SetValue(signal, Enum.Parse(phase.FieldType, "Walk"));
            float stoppedGap = network.GapAhead(lead, 25f);
            Check(stoppedGap <= 10f && stoppedGap > 0f, "red signal contributes stop line");
            phase.SetValue(signal, Enum.Parse(phase.FieldType, "Drive"));
            Check(network.GapAhead(lead, 25f) > stoppedGap, "green signal releases stop line");
            Set(network, "signalStops", Array.Empty<TrafficSignalStop>());
            UnityEngine.Object.DestroyImmediate(lead.gameObject);
            Set(session, "zoneMap", null);
            network.Configure(session, paths, Array.Empty<TrafficJunctionData>(), new[] { new TrafficNetwork.VehicleKind { prefab = prefab, share = 1f } });
            Call(network, "Awake");
            ((List<TrafficCar>)Get<object>(network, "cars")).Clear();
            Call(network, "Spawn");
            Check(network.Cars.Count == 7, "no zone map: legacy fleet count");
            UnityEngine.Object.DestroyImmediate(map);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        private static void CheckMapDays()
        {
            EditorSceneManager.OpenScene("Assets/_Game/Scenes/FourDistrictPrototype_Myeongji.unity", OpenSceneMode.Single);
            var network = UnityEngine.Object.FindFirstObjectByType<TrafficNetwork>();
            var session = network.Session;
            Set(network, "randomEachRun", false);
            Set(session, "player", null); // no rider obstacle; this is the requested graph simulation.
            for (int day = 1; day <= 5; day++)
            {
                var old = new List<TrafficCar>(network.Cars);
                foreach (var car in old) if (car != null) UnityEngine.Object.DestroyImmediate(car.gameObject);
                Get<List<TrafficCar>>(network, "cars").Clear();
                Set(session, "<Progress>k__BackingField", new RunProgress(day, 0));
                Set(session, "openZones", new List<string>(new[] { "C", "A", "B", "D" }).GetRange(0, Mathf.Min(day, 4)));
                Call(network, "Awake"); Call(network, "Spawn");
                int expected = new[] { 30, 40, 50, 60, 60 }[day - 1];
                Check(network.Cars.Count == expected, $"day {day} fleet {network.Cars.Count}/{expected}");
                var filter = new HazardZoneFilter(session.ZoneMap, session.IsZoneOpen);
                int closed = 0, stopped = 0, transitions = 0;
                // One simulated hour, 1-second steps, using the actual TrafficCar.Advance/ChooseNext.
                foreach (var car in network.Cars)
                {
                    int initial = car.PathIndex; float along = car.Distance; int moved = 0;
                    for (int second = 0; second < 3600; second++)
                    {
                        int previous = car.PathIndex;
                        Call(car, "Advance", 9f);
                        var path = network.Path(car.PathIndex);
                        network.Sample(car.PathIndex, car.Distance, out var position, out _);
                        if (!filter.PointOpen(position)) closed++;
                        if (car.PathIndex != previous) transitions++;
                        if (car.PathIndex != initial || Mathf.Abs(car.Distance - along) > 0.01f) moved++;
                        initial = car.PathIndex; along = car.Distance;
                    }
                    if (moved == 0 || car.NextIndex < 0) stopped++;
                }
                Check(closed == 0, $"day {day}: closed path occupancy {closed}");
                Check(stopped == 0, $"day {day}: permanently stopped cars {stopped}");
                Check(transitions > 0, "cars actually cross paths");
                // Existing reservation rules still arbitrate competing connector IDs and release on disable.
                var first = network.Cars[0]; var secondCar = network.Cars[1];
                foreach (var car in network.Cars)
                    for (int junction = 0; junction < network.JunctionCount; junction++) network.LeaveJunction(junction, car);
                Check(network.TryEnterJunction(0, first, 100), "first junction entry");
                Check(!network.TryEnterJunction(0, secondCar, 101), "conflicting connector waits");
                network.LeaveJunction(0, first);
                Check(network.TryEnterJunction(0, secondCar, 101), "waiting car enters after release");
                network.LeaveJunction(0, secondCar);
                Check(network.TryEnterJunction(0, first, 100), "entry before car disable");
                secondCar.gameObject.SetActive(false);
                first.gameObject.SetActive(false);
                Check(network.TryEnterJunction(0, network.Cars[2], 101), "disable releases reservation");
                network.LeaveJunction(0, network.Cars[2]);
                Debug.Log($"TRAFFIC day={day} fleet={network.Cars.Count}/{expected} closed={closed} stopped={stopped} transitions={transitions} duration=3600s graph");
            }
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        private static ZoneMap.Zone Zone(string id, float x) => new ZoneMap.Zone { id = id, polygon = new[] { new Vector2(x, 0f), new Vector2(x + 100f, 0f), new Vector2(x + 100f, 100f), new Vector2(x, 100f) } };
        private static TrafficPath Lane(float x, int[] next) => new TrafficPath { points = new[] { new Vector3(x, 0f, 10f), new Vector3(x, 0f, 90f) }, cumulative = new[] { 0f, 80f }, length = 80f, next = next };
        private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, Any).GetValue(target);
        private static void Set(object target, string name, object value) => target.GetType().GetField(name, Any).SetValue(target, value);
        private static object Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Any).Invoke(target, args);
        private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("TrafficZoneChecks: " + message); }
    }
}
