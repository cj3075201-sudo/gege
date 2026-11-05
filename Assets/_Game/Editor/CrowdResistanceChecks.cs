using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DeliveryRider.Editor
{
    // Actual movement methods + PhysX in an empty edit-mode scene. Never saves the fixture.
    public static class CrowdResistanceChecks
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const float Step = 0.02f;

        public static void RunBaseline()
        {
            foreach (float speed in new[] { 6f, 9.6f }) Simulate(speed, false);
        }

        public static void Run()
        {
            CheckTuningAndContact();
            foreach (float speed in new[] { 6f, 9.6f }) Simulate(speed, true);
            Debug.Log("CrowdResistanceChecks PASS: all checks (edit-mode methods + PhysX, not Play).");
        }

        private static void CheckTuningAndContact()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var hazard = new GameObject("CrowdSettings").AddComponent<CelebrityCrowdHazard>();
            var fields = DevValue.Discover(hazard);
            foreach (string name in new[] { "fanKnockMinimumSpeed", "fanContactSpeedShare", "riderMinimumProgressSpeed", "fanContactCooldown" })
            {
                Check(fields.Exists(v => v.PropertyPath == name), "F1 lists " + name);
                Check(DevLabels.Find(nameof(CelebrityCrowdHazard), name) != null, "Korean name for " + name);
            }
            Equal(8.5f, Get<float>(hazard, "fanKnockMinimumSpeed"), "fan knock threshold");
            Equal(0.74f, Get<float>(hazard, "fanContactSpeedShare"), "HTML contact share");
            Equal(1.5f, Get<float>(hazard, "riderMinimumProgressSpeed"), "escape floor");
            var network = new GameObject("Walkers").AddComponent<PedestrianNetwork>();
            var prefab = AssetDatabase.LoadAssetAtPath<TrafficPedestrian>("Assets/_Game/Prefabs/Traffic/Pedestrian.prefab");
            network.Configure(null, new[] { Route() }, prefab, Array.Empty<Material>());
            var fan = UnityEngine.Object.Instantiate(prefab);
            Call(fan, "Awake");
            fan.InitializeCrowd(network, 0, 0f, 7, null, hazard.transform, Vector3.zero, true, 3.75f, 1.35f, 3f);
            Call(fan, "ConfigureCrowdContact", hazard);
            var player = new GameObject("ContactRider");
            player.AddComponent<Rigidbody>().useGravity = false;
            player.AddComponent<CapsuleCollider>();
            var rider = player.AddComponent<RiderMotor>();
            var session = new GameObject("ContactSession").AddComponent<DeliverySession>();
            Set(session, "player", player.transform); Set(rider, "session", session); Set(rider, "speed", 6f);
            Call(rider, "Awake");
            var rb = player.GetComponent<Rigidbody>();
            rb.linearVelocity = Vector3.forward * 6f;
            Check(fan.TryCrowdContact(rider, rb.linearVelocity), "fan contact handled");
            Equal(4.44f, rider.HorizontalSpeed, "one contact applies HTML share");
            fan.TryCrowdContact(rider, rb.linearVelocity);
            Equal(4.44f, rider.HorizontalSpeed, "same fan does not multiply each frame");
            for (int i = 0; i < 32; i++) rider.ApplyCrowdContact(0.74f, 1.5f, 0.35f);
            Equal(1.5f, rider.HorizontalSpeed, "many contacts retain minimum progress");
            Set(hazard, "fanKnockMinimumSpeed", 10f);
            Check(!fan.TryBodyKnock(Vector3.forward * 9.6f), "F1 reaches existing fans immediately");
            Set(hazard, "fanKnockMinimumSpeed", 8.5f);
            Check(!fan.TryBodyKnock(Vector3.forward * 6f), "running does not knock a fan");
            Check(fan.TryBodyKnock(Vector3.forward * 9.6f), "inline knocks a fan");
            Check(Get<bool>(fan, "crowdChain"), "fan flight carries crowd origin, preventing rider feedback");
            var slot = hazard.gameObject.AddComponent<HazardSlot>();
            slot.Configure("celebrity", HazardRole.Trigger, 1, Array.Empty<int>());
            slot.StrikeAllowed = false;
            Set(hazard, "slot", slot); Set(hazard, "session", session); Set(hazard, "network", network);
            Call(hazard, "Update");
            Check(!Get<bool>(hazard, "appeared"), "no food: crowd does not appear");
            var walker = UnityEngine.Object.Instantiate(prefab);
            Call(walker, "Awake");
            walker.Initialize(network, 0, 0f, 9, null);
            Check(walker.TryBodyKnock(Vector3.forward * 6f), "ordinary walker retains 4 m/s threshold");
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        private static PedestrianPath Route() => new PedestrianPath
        {
            points = new[] { new Vector3(-30f, 0f, 0f), new Vector3(30f, 0f, 0f) },
            cumulative = new[] { 0f, 60f }, length = 60f, next = new[] { 0 }
        };

        private static void Simulate(float speed, bool verify)
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var previous = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;
            try
            {
                var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
                floor.transform.position = new Vector3(0f, -0.5f, 0f);
                floor.transform.localScale = new Vector3(60f, 1f, 60f);
                var player = new GameObject("Rider");
                player.transform.position = new Vector3(-6f, 0.02f, 0f);
                var body = player.AddComponent<Rigidbody>();
                body.mass = 75f; // Current scene rider mass.
                body.constraints = RigidbodyConstraints.FreezeRotation;
                var shape = player.AddComponent<CapsuleCollider>();
                shape.center = Vector3.up * 0.9f; shape.height = 1.8f; shape.radius = 0.5f;
                var rider = player.AddComponent<RiderMotor>();
                var session = new GameObject("Session").AddComponent<DeliverySession>();
                Set(session, "player", player.transform);
                Set(rider, "session", session); Set(rider, "speed", speed);
                var view = new GameObject("View").transform;
                view.rotation = Quaternion.LookRotation(Vector3.right);
                Set(rider, "view", view);
                Call(rider, "Awake");
                Set(rider, "spawn", player.transform.position);
                var network = new GameObject("Fans").AddComponent<PedestrianNetwork>();
                var prefab = AssetDatabase.LoadAssetAtPath<TrafficPedestrian>("Assets/_Game/Prefabs/Traffic/Pedestrian.prefab");
                network.Configure(null, new[] { Route() }, prefab, Array.Empty<Material>());
                var hazard = new GameObject("Celebrity").AddComponent<CelebrityCrowdHazard>();
                var fans = new List<TrafficPedestrian>();
                // Six waiting + 26 incoming fans, already gathered at the same 2 m ring, deterministic seeds.
                for (int i = 0; i < 32; i++)
                {
                    float angle = i * Mathf.PI * 2f / 32f;
                    var point = new Vector3(Mathf.Cos(angle), 0.02f, Mathf.Sin(angle)) * 2f;
                    var fan = UnityEngine.Object.Instantiate(prefab);
                    Call(fan, "Awake");
                    fan.InitializeCrowd(network, 0, 0f, i + 13, null, hazard.transform, point, true, 3.75f, 1.35f, 3f);
                    var configure = fan.GetType().GetMethod("ConfigureCrowdContact", Any);
                    configure?.Invoke(fan, new object[] { hazard });
                    fans.Add(fan);
                }
                Physics.SyncTransforms();
                body.linearVelocity = Vector3.right * speed;
                var knocked = new HashSet<TrafficPedestrian>();
                float time = 0f, minimum = speed;
                // Edit-mode Time.time does not advance with Physics.Simulate. Advance only cooldown deadlines;
                // all movement, fan forces, contact sweep and physics below are the real production methods.
                for (int frame = 0; frame < 2000 && body.position.x < 6f; frame++)
                {
                    foreach (var fan in fans)
                    {
                        ShiftDeadline(fan, "nextCrowdSlowAt");
                        ShiftDeadline(fan, "crowdPushUntil");
                        Call(fan, "FixedUpdate");
                    }
                    ShiftDeadline(rider, "crowdSlowUntil");
                    Set(rider, "movement", Vector2.up);
                    Call(rider, "FixedUpdate");
                    foreach (var fan in fans) if (Get<bool>(fan, "flying")) knocked.Add(fan);
                    Physics.Simulate(Step);
                    time += Step;
                    if (Mathf.Abs(body.position.x) < 3f) minimum = Mathf.Min(minimum, rider.HorizontalSpeed);
                }
                bool trapped = body.position.x < 6f;
                Debug.Log($"CROWD {(verify ? "after" : "before")} speed={speed:0.0} time={time:0.00}s knocked={knocked.Count}/32 trapped={(trapped ? 1 : 0)} minimumSolvedSpeed={minimum:0.000} end={body.position}");
                if (verify)
                {
                    Check(!trapped, "crowd traversal finishes at " + speed);
                    if (speed < 8.5f) Check(knocked.Count == 0, "no running fan knockdowns");
                    else Check(knocked.Count > 0, "inline knocks at least one fan");
                    Check(minimum < speed * 0.9f, "crowd slows rider");
                }
            }
            finally
            {
                Physics.simulationMode = previous;
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
        }

        private static void ShiftDeadline(object target, string name)
        {
            var field = target.GetType().GetField(name, Any);
            if (field != null) field.SetValue(target, (float)field.GetValue(target) - Step);
        }
        private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, Any).GetValue(target);
        private static void Set(object target, string name, object value) => target.GetType().GetField(name, Any).SetValue(target, value);
        private static object Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Any).Invoke(target, args);
        private static void Equal(float expected, float actual, string message) => Check(Mathf.Abs(expected - actual) < 0.001f, message);
        private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("CrowdResistanceChecks: " + message); }
    }
}
