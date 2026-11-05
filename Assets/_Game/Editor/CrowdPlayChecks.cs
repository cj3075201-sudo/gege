using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;

namespace DeliveryRider.Editor
{
    // Optional explicit Play probe: a throwaway flat scene and virtual W input, never the saved game.
    [InitializeOnLoad]
    public static class CrowdPlayChecks
    {
        private const string Pending = "CrowdPlayChecks.Pending";
        private const string ExitCode = "CrowdPlayChecks.ExitCode";
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static readonly List<TrafficPedestrian> Fans = new List<TrafficPedestrian>();
        private static readonly HashSet<TrafficPedestrian> Knocked = new HashSet<TrafficPedestrian>();
        private static Keyboard keyboard;
        private static Rigidbody rider;
        private static int scenario;
        private static float started, minimum;
        private static double deadline;

        static CrowdPlayChecks() => EditorApplication.playModeStateChanged += OnMode;

        public static void Run()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            SessionState.SetBool(Pending, true);
            SessionState.SetInt(ExitCode, 1);
            EditorApplication.isPlaying = true;
        }

        private static void OnMode(PlayModeStateChange state)
        {
            if (!SessionState.GetBool(Pending, false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                try
                {
                    Time.timeScale = 10f;
                    Application.runInBackground = true;
                    InputSystem.settings = UnityEngine.Object.Instantiate(InputSystem.settings);
                    InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
                    InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
                    keyboard = InputSystem.AddDevice<Keyboard>("CrowdProbeKeyboard");
                    scenario = 0;
                    Build();
                    EditorApplication.update += Observe;
                }
                catch (Exception error) { Finish(error); }
            }
            if (state == PlayModeStateChange.EnteredEditMode)
            {
                SessionState.SetBool(Pending, false);
                EditorApplication.Exit(SessionState.GetInt(ExitCode, 1));
            }
        }

        private static void Build()
        {
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects()) UnityEngine.Object.DestroyImmediate(root);
            Fans.Clear(); Knocked.Clear();
            bool changed = scenario >= 2;
            float speed = scenario % 2 == 0 ? 6f : 9.6f;
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.transform.position = new Vector3(0f, -0.5f, 0f); floor.transform.localScale = new Vector3(60f, 1f, 60f);
            var host = new GameObject("InactiveFixtureServices"); host.SetActive(false);
            var session = host.AddComponent<DeliverySession>();
            var network = host.AddComponent<PedestrianNetwork>();
            var settings = host.AddComponent<CelebrityCrowdHazard>();
            var prefab = AssetDatabase.LoadAssetAtPath<TrafficPedestrian>("Assets/_Game/Prefabs/Traffic/Pedestrian.prefab");
            network.Configure(null, new[] { new PedestrianPath { points = new[] { Vector3.left * 30f, Vector3.right * 30f }, cumulative = new[] { 0f, 60f }, length = 60f, next = new[] { 0 } } }, prefab, Array.Empty<Material>());
            var player = new GameObject("Rider"); player.SetActive(false);
            player.transform.position = new Vector3(-6f, 0.02f, 0f);
            rider = player.AddComponent<Rigidbody>(); rider.mass = 75f; rider.constraints = RigidbodyConstraints.FreezeRotation;
            var shape = player.AddComponent<CapsuleCollider>(); shape.center = Vector3.up * 0.9f; shape.height = 1.8f; shape.radius = 0.5f;
            var motor = player.AddComponent<RiderMotor>();
            var view = new GameObject("View").transform; view.rotation = Quaternion.LookRotation(Vector3.right);
            Set(session, "player", player.transform); Set(motor, "session", session); Set(motor, "view", view); Set(motor, "speed", speed);
            player.SetActive(true);
            for (int i = 0; i < 32; i++)
            {
                float angle = i * Mathf.PI * 2f / 32f;
                var point = new Vector3(Mathf.Cos(angle) * 2f, 0.02f, Mathf.Sin(angle) * 2f);
                var fan = UnityEngine.Object.Instantiate(prefab);
                fan.InitializeCrowd(network, 0, 0f, i + 13, null, host.transform, point, true, 3.75f, 1.35f, 3f);
                if (changed) fan.ConfigureCrowdContact(settings);
                Fans.Add(fan);
            }
            Physics.SyncTransforms(); rider.linearVelocity = Vector3.right * speed;
            InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.W));
            started = Time.time; minimum = speed; deadline = EditorApplication.timeSinceStartup + 45d;
        }

        private static void Observe()
        {
            try
            {
                foreach (var fan in Fans) if ((bool)fan.GetType().GetField("flying", Any).GetValue(fan)) Knocked.Add(fan);
                if (Mathf.Abs(rider.position.x) < 3f) minimum = Mathf.Min(minimum, Vector3.ProjectOnPlane(rider.linearVelocity, Vector3.up).magnitude);
                bool trapped = Time.time - started >= 40f || EditorApplication.timeSinceStartup > deadline;
                if (!trapped && rider.position.x < 6f) return;
                bool changed = scenario >= 2;
                float speed = scenario % 2 == 0 ? 6f : 9.6f;
                Debug.Log($"CROWD_PLAY {(changed ? "after" : "before")} speed={speed:0.0} time={Time.time - started:0.00}s knocked={Knocked.Count}/32 trapped={(trapped ? 1 : 0)} minimumSolvedSpeed={minimum:0.000} end={rider.position}");
                if (trapped || minimum < 1.5f || speed >= 8.5f && Knocked.Count == 0
                    || changed && speed < 8.5f && Knocked.Count > 0 || !changed && Knocked.Count == 0)
                    throw new InvalidOperationException("Play crowd escape/knock/minimum-speed regression");
                if (++scenario < 4) { Build(); return; }
                Debug.Log("CROWD_PLAY_PASS: four scenarios using actual FixedUpdate, collision callbacks and virtual W input.");
                Finish(null);
            }
            catch (Exception error) { Finish(error); }
        }

        private static void Finish(Exception error)
        {
            EditorApplication.update -= Observe;
            if (error != null) Debug.LogException(error);
            SessionState.SetInt(ExitCode, error == null ? 0 : 1);
            if (keyboard != null && keyboard.added) InputSystem.RemoveDevice(keyboard);
            Time.timeScale = 1f;
            EditorApplication.isPlaying = false;
        }
        private static void Set(object owner, string name, object value) => owner.GetType().GetField(name, Any).SetValue(owner, value);
    }
}
