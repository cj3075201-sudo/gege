using System;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;

namespace DeliveryRider.Editor
{
    // Explicitly requested batch Play probe. Uses a disposable scene, real physics, FixedUpdate and keyboard input.
    [InitializeOnLoad]
    public static class FallingObjectPlayChecks
    {
        private const string Pending = "FallingObjectPlayChecks.Pending";
        private const string ExitCode = "FallingObjectPlayChecks.ExitCode";
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static Keyboard keyboard;
        private static DeliverySession session;
        private static RiderMotor motor;
        private static Rigidbody body;
        private static FallingObjectHazard hazard;
        private static HazardSlot slot;
        private static ZoneMap map;
        private static Camera camera;
        private static int scenario, hits;
        private static float started, hitAt, recoveredAt, maximumY;
        private static double deadline;
        private static bool acted, airborne, captured, locked;
        private static Vector3 lockedTarget;
        private static Transform pickupPoint;
        private static string playError;
        private static HazardSpawnSettings spawnSettings;
        private static HazardCandidates candidates;
        private static bool foodSpawned, foodLost, foodRecovered;

        static FallingObjectPlayChecks() => EditorApplication.playModeStateChanged += OnMode;

        public static void Run()
        {
            FallingObjectChecks.Run();
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
                    Time.timeScale = 3f;
                    Application.runInBackground = true;
                    InputSystem.settings = UnityEngine.Object.Instantiate(InputSystem.settings);
                    InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
                    InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
                    keyboard = InputSystem.AddDevice<Keyboard>("FallingProbeKeyboard");
                    scenario = 0;
                    Application.logMessageReceived += OnLog;
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
            InputSystem.QueueStateEvent(keyboard, new KeyboardState());
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects()) UnityEngine.Object.DestroyImmediate(root);
            if (map != null) UnityEngine.Object.DestroyImmediate(map);
            if (spawnSettings != null) UnityEngine.Object.DestroyImmediate(spawnSettings);
            if (candidates != null) UnityEngine.Object.DestroyImmediate(candidates);
            hits = 0; hitAt = recoveredAt = -1f; maximumY = 0f;
            playError = null;
            acted = airborne = captured = locked = false;
            foodSpawned = foodLost = foodRecovered = false;
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Probe floor";
            floor.transform.position = new Vector3(0f, -.5f, 0f);
            floor.transform.localScale = new Vector3(100f, 1f, 100f);
            floor.GetComponent<Renderer>().sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/_Game/Art/Materials/Sidewalk.mat");
            var host = new GameObject("InactiveSession"); host.SetActive(false);
            pickupPoint = new GameObject("DistantPickup").transform;
            pickupPoint.SetParent(host.transform); pickupPoint.position = Vector3.one * 40f;
            session = host.AddComponent<DeliverySession>();
            var bag = new GameObject("FoodBag"); bag.transform.SetParent(host.transform);
            Set(session, "foodBag", bag);
            var ground = host.AddComponent<RoadNetwork>();
            ground.Configure(new[] { floor.GetComponent<Collider>() });
            var player = new GameObject("ProbeRider"); player.SetActive(false);
            player.transform.position = new Vector3(0f, .02f, 0f);
            body = player.AddComponent<Rigidbody>(); body.mass = 75f; body.constraints = RigidbodyConstraints.FreezeRotation;
            var capsule = player.AddComponent<CapsuleCollider>(); capsule.center = Vector3.up * .9f; capsule.height = 1.8f; capsule.radius = .5f;
            motor = player.AddComponent<RiderMotor>();
            var view = new GameObject("Input view").transform; view.rotation = Quaternion.LookRotation(Vector3.right);
            Set(session, "player", player.transform); Set(session, "rider", motor);
            Set(motor, "session", session); Set(motor, "view", view); Set(motor, "speed", 6f);
            Property(session, "Stage", scenario == 2 ? DeliveryStage.Available : DeliveryStage.Carrying);
            Property(session, "ActiveOrder", Order(1));
            var visual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            visual.transform.SetParent(player.transform, false); visual.transform.localPosition = Vector3.up * .9f;
            visual.transform.localScale = new Vector3(1f, .9f, 1f);
            UnityEngine.Object.DestroyImmediate(visual.GetComponent<Collider>());
            visual.GetComponent<Renderer>().sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/_Game/Art/Materials/RiderCoat.mat");
            var pose = player.AddComponent<RiderReactionVisual>(); Set(pose, "motor", motor); Set(pose, "visualRoot", visual.transform);
            player.SetActive(true);
            motor.HazardHit += _ => { hits++; hitAt = Time.time; };
            motor.ReactionChanged += state =>
            {
                if (state == RiderReactionState.Airborne) airborne = true;
                if (hitAt >= 0f && state == RiderReactionState.Normal) recoveredAt = Time.time;
            };
            var light = new GameObject("Sun").AddComponent<Light>(); light.type = LightType.Directional; light.intensity = 1.4f;
            light.transform.rotation = Quaternion.Euler(45f, -30f, 0f);
            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
            RenderSettings.ambientLight = Color.white * .7f;
            camera = new GameObject("Probe camera", typeof(Camera)).GetComponent<Camera>(); camera.tag = "MainCamera";
            camera.transform.position = new Vector3(10f, 10f, -14f); camera.transform.LookAt(new Vector3(0f, 2.8f, 0f));
            camera.backgroundColor = new Color(.65f, .78f, .9f); camera.clearFlags = CameraClearFlags.SolidColor;
            camera.fieldOfView = 45f;
            var staging = new GameObject("Staging"); staging.SetActive(false);
            var instance = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(FallingObjectChecks.PrefabPath), staging.transform);
            hazard = instance.GetComponent<FallingObjectHazard>();
            slot = instance.AddComponent<HazardSlot>(); slot.Configure("falling", HazardRole.Trigger, 1, Array.Empty<int>());
            slot.StrikeAllowed = scenario != 3 && HazardPlanner.DeliveryInProgress(session);
            if (scenario == 5 || scenario == 6)
            {
                map = ScriptableObject.CreateInstance<ZoneMap>();
                Set(map, "zones", new[] { Zone("C", -50f, 0f), Zone("A", 0f, 50f) });
                Set(session, "zoneMap", map); Set(session, "openZones", new[] { "C" });
                instance.transform.position = new Vector3(-4f, 0f, 0f);
                body.position = new Vector3(scenario == 5 ? 2f : -2f, .02f, 0f);
                var wall = GameObject.CreatePrimitive(PrimitiveType.Cube); wall.name = "Closed-zone wall";
                wall.transform.position = new Vector3(0f, 1.5f, 0f); wall.transform.localScale = new Vector3(.6f, 3f, 30f);
                if (scenario == 6) body.linearVelocity = Vector3.right * 6f;
            }
            if (scenario == 10) { slot.SetTriggerScale(2f); body.position = new Vector3(12f, .02f, 0f); }
            hazard.Place(session, ground, new HazardCandidate());
            instance.transform.SetParent(null, true);
            UnityEngine.Object.DestroyImmediate(staging);
            if (scenario == 11)
            {
                var plannerObject = new GameObject("Real food-drop planner"); plannerObject.SetActive(false);
                var planner = plannerObject.AddComponent<HazardPlanner>();
                spawnSettings = ScriptableObject.CreateInstance<HazardSpawnSettings>();
                candidates = ScriptableObject.CreateInstance<HazardCandidates>();
                Set(planner, "session", session); Set(planner, "rider", motor); Set(planner, "ground", ground);
                Set(planner, "settings", spawnSettings); Set(planner, "candidates", candidates);
                Set(planner, "foodDropChance", 1f); Set(planner, "foodSpreadSpeed", 0f);
                session.Changed += () =>
                {
                    if (session.Stage == DeliveryStage.FoodLost) foodLost = true;
                    if (foodLost && session.Stage == DeliveryStage.Carrying) foodRecovered = true;
                };
                plannerObject.SetActive(true);
            }
            Physics.SyncTransforms();
            started = Time.time;
            deadline = EditorApplication.timeSinceStartup + 45d;
        }

        private static void Observe()
        {
            try
            {
                float age = Time.time - started;
                Check(playError == null, "unexpected Unity error: " + playError);
                if (EditorApplication.timeSinceStartup > deadline) throw new TimeoutException("Falling Play scenario " + scenario);
                maximumY = Mathf.Max(maximumY, body.position.y);
                if (scenario == 11 && UnityEngine.Object.FindFirstObjectByType<DroppedFood>() != null) foodSpawned = true;
                float elapsed = (float)Get(hazard, "elapsed");
                if (scenario == 0 && !captured && age >= .4f && age < 1.8f)
                { Capture("warning"); captured = true; }
                if (scenario == 0 && hitAt >= 0f && age - (hitAt - started) > .3f && !acted)
                { Capture("stun"); acted = true; }
                if (scenario == 1 && !acted && elapsed >= 1.32f)
                {
                    lockedTarget = (Vector3)Get(hazard, "target"); locked = true; acted = true;
                    InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.W));
                }
                if (scenario == 1 && locked && elapsed > 1.35f && elapsed < 1.9f)
                    Check(Vector3.Distance(lockedTarget, (Vector3)Get(hazard, "target")) < .001f, "target remains locked after 1.3 s");
                if (!acted && age >= .6f)
                {
                    if (scenario == 4) { Set(session, "mapOpen", true); acted = true; }
                    if (scenario == 7) { hazard.gameObject.SetActive(false); acted = true; CheckHidden(); }
                    if (scenario == 8) { Property(session, "ActiveOrder", Order(2)); acted = true; }
                    if (scenario == 9) { Property(session, "Stage", DeliveryStage.FoodLost); slot.StrikeAllowed = true; acted = true; }
                    if (scenario == 10)
                    {
                        // Move after target lock, still inside the scaled impact radius (outside the 1.7 m base).
                        if (elapsed >= 1.32f) { body.position += Vector3.forward * 2.5f; Physics.SyncTransforms(); acted = true; }
                    }
                    if (scenario == 12 && hitAt >= 0f && age >= 2.5f)
                    {
                        Check(motor.TryHazardSlip(camera, Vector3.right, 1.5f), "another hazard can replace the stun with a slip");
                        acted = true;
                    }
                }
                if (scenario == 12 && acted && age > 2.8f && age < 3.2f)
                    Check(motor.IsSlipping && motor.HorizontalSpeed > 3f, "replacement slip must move instead of retaining the old stun");
                if (scenario == 7 && acted && age >= 1f && !hazard.gameObject.activeSelf) hazard.gameObject.SetActive(true);
                if (scenario == 6 && age > .05f && age < .6f)
                {
                    var target = (Vector3)Get(hazard, "target");
                    Check(target.x <= -1f, "prediction respects 1 m closed-wall margin");
                }
                float end = scenario == 0 ? 6f : scenario == 8 ? 5.7f : 4.9f;
                if (age < end) return;
                bool shouldHit = scenario == 0 || scenario == 6 || scenario == 8 || scenario == 9 || scenario == 10 || scenario == 11;
                int expectedHits = scenario == 12 ? 2 : shouldHit ? 1 : 0;
                Check(hits == expectedHits, "hit count expected " + expectedHits + ", got " + hits);
                if (shouldHit)
                {
                    Check(recoveredAt > hitAt, "recovers after ground stun");
                    Check(Mathf.Abs(recoveredAt - hitAt - 2.6f) <= .041f, "stun duration=" + (recoveredAt - hitAt));
                    Check(!airborne && maximumY <= .08f, "no upward launch, peak y=" + maximumY);
                }
                if (scenario == 1) Check(acted && body.position.x > 3f, "real W input leaves the target");
                if (scenario == 8) Check(hitAt - started >= 2.45f, "new call restarts the warning instead of retaining the old timer");
                if (scenario == 11) Check(foodSpawned && foodLost && foodRecovered, "real HazardHit -> planner -> dropped food -> recovery");
                CheckHidden();
                Debug.Log($"FALLING_PLAY scenario={scenario} PASS hits={hits} stun={recoveredAt - hitAt:0.000}s peakY={maximumY:0.000} end={body.position}");
                if (++scenario <= 12) { Build(); return; }
                Debug.Log("FALLING_PLAY_PASS: 13 actual Play scenarios; hit, evade, stun recovery, no food, gate, map, closed wall, disable, new call, food loss, scale, real food drop/recovery, replacement slip.");
                Finish(null);
            }
            catch (Exception error) { Finish(error); }
        }

        private static void CheckHidden()
        {
            foreach (string name in new[] { "aimMarker", "fallingObject" })
                Check(!((Transform)Get(hazard, name)).gameObject.activeSelf, name + " cleaned up");
            Check(!((TMPro.TMP_Text)Get(hazard, "cue")).gameObject.activeSelf, "cue cleaned up");
            foreach (var fragment in (Transform[])Get(hazard, "fragments")) Check(!fragment.gameObject.activeSelf, "fragment cleaned up");
        }

        private static void Capture(string label)
        {
            var texture = new RenderTexture(1280, 900, 24);
            var previous = RenderTexture.active;
            var pixels = new Texture2D(1280, 900, TextureFormat.RGB24, false);
            try
            {
                camera.targetTexture = texture; camera.Render(); RenderTexture.active = texture;
                pixels.ReadPixels(new Rect(0, 0, 1280, 900), 0, 0); pixels.Apply();
                string path = "C:/UnityWork/ge/공유/결과/codex_falling_" + label + ".png";
                File.WriteAllBytes(path, pixels.EncodeToPNG());
                Debug.Log("FALLING_SCREENSHOT " + path);
            }
            finally { camera.targetTexture = null; RenderTexture.active = previous; UnityEngine.Object.DestroyImmediate(pixels); UnityEngine.Object.DestroyImmediate(texture); }
        }

        private static ZoneMap.Zone Zone(string id, float min, float max) => new ZoneMap.Zone
        { id = id, polygon = new[] { new Vector2(min, -50), new Vector2(max, -50), new Vector2(max, 50), new Vector2(min, 50) } };
        private static DeliveryOrderOffer Order(int number) => new DeliveryOrderOffer(number, pickupPoint, null, pickupPoint, "", "", "", 0, 0, 100f, 100f, "", "C", "C", 0, 0);
        private static void OnLog(string message, string stack, LogType type)
        {
            // Unity's optional Search index may fail during editor startup before the disposable scene runs.
            // Keep that exception in the log and report it separately; all gameplay/rendering errors still fail.
            if (stack.Contains("UnityEditor.Search.SearchDatabase")) return;
            if (type == LogType.Exception || type == LogType.Error || type == LogType.Assert) playError = message + " " + stack;
        }
        private static void Property(object owner, string name, object value) => owner.GetType().GetProperty(name, Any).SetValue(owner, value);
        private static object Get(object owner, string name) => owner.GetType().GetField(name, Any).GetValue(owner);
        private static void Set(object owner, string name, object value) => owner.GetType().GetField(name, Any).SetValue(owner, value);
        private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("FALLING_PLAY scenario " + scenario + ": " + message); }

        private static void Finish(Exception error)
        {
            EditorApplication.update -= Observe;
            Application.logMessageReceived -= OnLog;
            if (error != null) Debug.LogException(error);
            SessionState.SetInt(ExitCode, error == null ? 0 : 1);
            if (keyboard != null && keyboard.added) InputSystem.RemoveDevice(keyboard);
            Time.timeScale = 1f;
            EditorApplication.isPlaying = false;
        }
    }
}
