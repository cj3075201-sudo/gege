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
    // Explicitly requested Play probe, following CrowdPlayChecks: no saved scenes, settings, progress or input assets.
    [InitializeOnLoad]
    public static class CatBoxerPlayChecks
    {
        private const string Pending = "CatBoxerPlayChecks.Pending", ExitCode = "CatBoxerPlayChecks.ExitCode";
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private static Keyboard keyboard;
        private static DeliverySession session;
        private static RiderMotor motor;
        private static Rigidbody rider;
        private static CatBoxerHazard cat;
        private static HazardSlot slot;
        private static GameObject catRoot;
        private static CatBoxerView view;
        private static Transform cameraView;
        private static int scenario, step, observedHits, punches;
        private static float started, stepStarted, lastPunch, minimumX;
        private static double deadline;
        private static Vector3 launch;
        private static ZoneMap zones;
        private static DroppedFood food;
        private static readonly string[] Names = { "no-food", "day-one", "duel-win-pal", "jab-keeps-food", "hook-jump-miss", "upper-step-back-miss", "loss-toward-target", "closed-zone", "reroll-cleanup", "scale", "escape-chase", "untrapped", "food-recovery", "pause-map-settlement" };
        static CatBoxerPlayChecks() => EditorApplication.playModeStateChanged += OnMode;
        public static void Run()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            SessionState.SetBool(Pending, true); SessionState.SetInt(ExitCode, 1); EditorApplication.isPlaying = true;
        }
        private static void OnMode(PlayModeStateChange state)
        {
            if (!SessionState.GetBool(Pending, false)) return;
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                try
                {
                    Application.runInBackground = true; Time.timeScale = 3f;
                    InputSystem.settings = UnityEngine.Object.Instantiate(InputSystem.settings);
                    InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
                    InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
                    keyboard = InputSystem.AddDevice<Keyboard>("CatProbeKeyboard"); scenario = 0; Build(); EditorApplication.update += Observe;
                }
                catch (Exception error) { Finish(error); }
            }
            if (state == PlayModeStateChange.EnteredEditMode)
            {
                SessionState.SetBool(Pending, false); EditorApplication.Exit(SessionState.GetInt(ExitCode, 1));
            }
        }
        private static void Build()
        {
            InputSystem.QueueStateEvent(keyboard, new KeyboardState());
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects()) UnityEngine.Object.DestroyImmediate(root);
            if (zones != null) UnityEngine.Object.DestroyImmediate(zones);
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube); floor.name = "TemporaryProbeFloor";
            floor.transform.position = new Vector3(0f, -.5f, 0f); floor.transform.localScale = new Vector3(180f, 1f, 180f);
            var services = new GameObject("InactiveProbeServices"); services.SetActive(false);
            session = services.AddComponent<DeliverySession>();
            var ground = services.AddComponent<RoadNetwork>(); ground.Configure(new[] { floor.GetComponent<Collider>() });
            var player = new GameObject("Rider"); player.SetActive(false); player.transform.position = new Vector3(0f, .02f, 2f); player.transform.rotation = Quaternion.LookRotation(Vector3.back);
            rider = player.AddComponent<Rigidbody>(); rider.mass = 75f; rider.constraints = RigidbodyConstraints.FreezeRotation;
            var capsule = player.AddComponent<CapsuleCollider>(); capsule.center = Vector3.up * .9f; capsule.height = 1.8f; capsule.radius = .5f;
            motor = player.AddComponent<RiderMotor>();
            cameraView = new GameObject("InputView").transform; cameraView.rotation = Quaternion.LookRotation(Vector3.back);
            Set(session, "player", player.transform); Set(session, "foodBag", new GameObject("FoodBag")); Set(session, "rider", motor);
            Property(session, "Progress", new RunProgress(scenario == 1 ? 1 : 2, 100000));
            Property(session, "Stage", scenario == 0 ? DeliveryStage.Pickup : DeliveryStage.Carrying);
            Set(session, "openZones", new List<string> { "C" });
            var pickup = new GameObject("Pickup").transform; pickup.position = Vector3.left * 40f;
            var destination = new GameObject("Destination").transform; destination.position = Vector3.right * 40f;
            var recipient = new GameObject("InactiveRecipient"); recipient.SetActive(false);
            var resident = recipient.AddComponent<DeliveryResident>();
            Property(session, "ActiveOrder", new DeliveryOrderOffer(17, pickup, resident, destination, "test", "test", "test", 3000, 1800, 300f, 40f, "test", "C", "C", 0, 0));
            resident.transform.position = destination.position;
            Set(motor, "session", session); Set(motor, "view", cameraView); Set(motor, "speed", 6f);
            player.SetActive(true); observedHits = punches = step = 0; launch = Vector3.zero;
            motor.ReactionImpulse += v => { launch = v; observedHits++; };
            motor.Punched += () => punches++;
            var staging = new GameObject("InactiveCatStaging"); staging.SetActive(false);
            catRoot = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(CatBoxerPrefabBuilder.PrefabPath), staging.transform);
            catRoot.name = "CatBoxerProbe"; slot = catRoot.AddComponent<HazardSlot>(); slot.Configure("cat", HazardRole.Trigger, 2, Array.Empty<int>());
            if (scenario == 7)
            {
                zones = ScriptableObject.CreateInstance<ZoneMap>();
                Set(zones, "zones", new[] { new ZoneMap.Zone { id = "A", polygon = new[] { new Vector2(-20f,-20f), new Vector2(20f,-20f), new Vector2(20f,20f), new Vector2(-20f,20f) } } });
                Set(session, "zoneMap", zones);
            }
            cat = catRoot.GetComponent<CatBoxerHazard>(); cat.Place(session, ground, new HazardCandidate { type = "cat", position = Vector3.zero, rotation = Quaternion.identity, halfWidth = 3f, side = 1 });
            view = cat.GetComponentInChildren<CatBoxerView>(true);
            if (scenario == 9) { rider.position = new Vector3(0f, .02f, 7f); slot.SetTriggerScale(2f); }
            if (scenario == 11) { rider.position = new Vector3(0f, .02f, 4f); Set(cat, "boutSeconds", 1f); }
            catRoot.transform.SetParent(null, true); UnityEngine.Object.Destroy(staging);
            Physics.SyncTransforms();
            started = stepStarted = Time.time; lastPunch = float.NegativeInfinity; minimumX = rider.position.x;
            deadline = EditorApplication.timeSinceStartup + 55d;
        }
        private static void Observe()
        {
            try
            {
                if (EditorApplication.timeSinceStartup > deadline || Time.time - started > 90f) throw new InvalidOperationException("timeout: " + Names[scenario] + " step=" + step + " hp=" + cat.Health + " punches=" + punches + " state=" + cat.State + " phase=" + cat.Phase + " rider=" + rider.position);
                float age = Time.time - started, since = Time.time - stepStarted;
                if (age < .15f) return;
                switch (scenario)
                {
                    case 0:
                    case 1:
                        Check(cat.State == CatBoxerHazard.Mode.Spent && !view.gameObject.activeSelf && observedHits == 0, "no appearance/hit before food or day two");
                        if (age > .6f) Pass();
                        break;
                    case 2: WinAndPal(since); break;
                    case 3:
                        if (step == 0 && Bout()) { Tell(CatBoxerHazard.Attack.Jab, 1f); Next(); }
                        else if (step == 1 && observedHits > 0)
                        {
                            Check(cat.TakenHits == 1 && session.Stage == DeliveryStage.Carrying && cat.KeepsFoodOnCurrentHit, "jab registered once and retained food");
                            Check(Mathf.Abs(launch.z - 11f) < .01f && Mathf.Abs(launch.y - 2.4525f) < .01f, "converted jab velocity"); Pass();
                        }
                        break;
                    case 4:
                        if (step == 0 && Bout()) { Tell(CatBoxerHazard.Attack.Hook, 1.5f); rider.position += Vector3.up; rider.useGravity = false; Next(); }
                        else if (step == 1 && cat.Phase == CatBoxerHazard.Beat.Open) { Check(observedHits == 0 && cat.TakenHits == 0, "jump clears low hook"); Pass(); }
                        break;
                    case 5:
                        if (step == 0 && Bout()) { Tell(CatBoxerHazard.Attack.Uppercut, 2f); Next(); }
                        else if (step == 1 && cat.Phase == CatBoxerHazard.Beat.Open) { Check(observedHits == 0, "backstep clears uppercut"); Pass(); }
                        break;
                    case 6: Lose(since); break;
                    case 7:
                        Check(cat.State == CatBoxerHazard.Mode.Spent && !view.gameObject.activeSelf && observedHits == 0, "closed-zone placement suppressed");
                        if (age > .6f) Pass(); break;
                    case 8:
                        if (step == 0 && Bout()) { slot.StrikeAllowed = false; Next(); }
                        else if (step == 1 && since > .15f)
                        {
                            Check(cat.State == CatBoxerHazard.Mode.Spent && !view.gameObject.activeSelf && !cat.ReceivePunch(Vector3.back), "reroll off tears down and rejects punch"); slot.StrikeAllowed = true; Next();
                        }
                        else if (step == 2 && Bout())
                        {
                            Check(cat.Health == 3 && cat.TakenHits == 0, "reroll on starts clean"); catRoot.SetActive(false); Check(!view.gameObject.activeSelf, "disable clears visuals"); Pass();
                        }
                        break;
                    case 9:
                        if (Bout()) { Check(Vector3.Distance(rider.position, cat.Position) > 5f && slot.TriggerScale == 2f, "trigger scale starts at seven metres"); Pass(); }
                        break;
                    case 10: Escape(since); break;
                    case 11:
                        if (step == 0 && Bout()) { cameraView.rotation = Quaternion.LookRotation(Vector3.back); InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.W)); Next(); }
                        else if (step == 1 && rider.position.z < -4f) { Check(!motor.IsRecovering, "walked through boxer without being trapped"); Debug.Log("CAT_PLAY trapped=0 actual virtual W path=" + rider.position); Pass(); }
                        break;
                    case 12: FoodRecovery(since); break;
                    case 13: PauseMapSettlement(since); break;
                }
            }
            catch (Exception error) { Finish(error); }
        }
        private static void WinAndPal(float since)
        {
            if (step == 0 && Bout())
            {
                Check(!cat.ReceivePunch(Vector3.back) && cat.Health == 3, "guard blocks punch outside open window"); Tell(CatBoxerHazard.Attack.Jab, 1f);
                rider.position += Vector3.right * 1.3f; Next();
            }
            else if (step == 1 && cat.Phase == CatBoxerHazard.Beat.Open)
            {
                Check(observedHits == 0, "side step avoids aimed jab");
                Stand(1f); AimPunch(); Next();
            }
            else if (step == 2)
            {
                Release();
                if (cat.Health == 2 && Time.time - lastPunch > .1f) { Check(punches > 0, "existing RiderMotor virtual F punch landed"); Next(); }
            }
            else if (step == 3 && since > .45f)
            {
                Tell(CatBoxerHazard.Attack.Uppercut, 2f); Next();
            }
            else if (step == 4 && cat.Phase == CatBoxerHazard.Beat.Open) { Stand(1f); AimPunch(); Next(); }
            else if (step == 5) { Release(); if (cat.Health == 1 && Time.time - lastPunch > .1f) Next(); }
            else if (step == 6 && since > .45f) { Tell(CatBoxerHazard.Attack.Uppercut, 2f); Next(); }
            else if (step == 7 && cat.Phase == CatBoxerHazard.Beat.Open) { Stand(1f); AimPunch(); Next(); }
            else if (step == 8)
            {
                Release();
                if (cat.State == CatBoxerHazard.Mode.Knockout && Time.time - lastPunch > .1f) { Check(cat.Health == 0 && punches >= 3, "three rider punches produce KO"); Next(); }
            }
            else if (step == 9 && cat.State == CatBoxerHazard.Mode.Pal) { rider.position = new Vector3(15f, .02f, 0f); Next(); }
            else if (step == 10 && since > 2f)
            {
                Check(Vector3.ProjectOnPlane(cat.Position - rider.position, Vector3.up).magnitude < 3f, "victorious cat follows rider");
                Property(session, "Stage", DeliveryStage.Complete); Next();
            }
            else if (step == 11 && since > .15f) { Check(!view.gameObject.activeSelf && cat.State == CatBoxerHazard.Mode.Spent, "delivery end dismisses friend"); Pass(); }
        }
        private static void Lose(float since)
        {
            if (step == 0 && Bout()) { Tell(CatBoxerHazard.Attack.Jab, 1f); Next(); }
            else if (step == 1 && cat.TakenHits == 1) { Recover(); Tell(CatBoxerHazard.Attack.Hook, 1f); Next(); }
            else if (step == 2 && cat.TakenHits == 2)
            {
                Check(Mathf.Abs(launch.x) > 14f && Mathf.Abs(launch.y - 3.567273f) < .01f, "hook launches sideways"); Recover(); Tell(CatBoxerHazard.Attack.Uppercut, 1f); Next();
            }
            else if (step == 3 && cat.State == CatBoxerHazard.Mode.Leaving)
            {
                Check(cat.TakenHits == 3 && launch.x > 14f && launch.y >= 10.70f && launch.y <= 12.05f, "third hit launches toward destination with converted loss height");
                Next();
            }
            else if (step == 4 && cat.State == CatBoxerHazard.Mode.Spent) { Check(!view.gameObject.activeSelf, "losing cat leaves"); Pass(); }
        }
        private static void Escape(float since)
        {
            if (step == 0 && Bout())
            {
                rider.position = new Vector3(0f, .02f, 10f); cameraView.rotation = Quaternion.LookRotation(Vector3.forward);
                InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.W)); Next();
            }
            else if (step == 1 && cat.Phase == CatBoxerHazard.Beat.Chase) { Release(); Next(); }
            else if (step == 2 && cat.State == CatBoxerHazard.Mode.Leaving)
            {
                Check(observedHits == 1 && launch.z > 14f && Mathf.Abs(launch.y - 3.121364f) < .01f, "running away triggers forward chase punch"); Pass();
            }
        }
        private static void FoodRecovery(float since)
        {
            if (step == 0 && Bout())
            {
                // Force the common drop decision to 100%; the actual drop/ownership/collection code stays unchanged.
                var host = new GameObject("InactiveDropPlanner"); host.SetActive(false); var planner = host.AddComponent<HazardPlanner>();
                Set(planner, "session", session); Set(planner, "rider", motor); Set(planner, "ground", UnityEngine.Object.FindFirstObjectByType<RoadNetwork>(FindObjectsInactive.Include));
                Set(planner, "settings", ScriptableObject.CreateInstance<HazardSpawnSettings>()); Set(planner, "foodDropChance", 1f);
                var drop = (Action<UnityEngine.Object>)Delegate.CreateDelegate(typeof(Action<UnityEngine.Object>), planner, typeof(HazardPlanner).GetMethod("OnRiderHazardHit", Any));
                motor.HazardHit += drop;
                Tell(CatBoxerHazard.Attack.Hook, 1f); Next();
            }
            else if (step == 1 && observedHits > 0)
            {
                food = UnityEngine.Object.FindFirstObjectByType<DroppedFood>();
                Check(food != null, "common food drop path spawned arc"); Next();
            }
            else if (step == 2 && session.Stage == DeliveryStage.FoodLost) Next();
            else if (step == 3 && since > .2f)
            {
                Check(!view.gameObject.activeSelf, "food loss ends duel"); Recover(); Next();
            }
            else if (step == 4 && since > 2f)
            {
                rider.position = food.transform.position; Physics.SyncTransforms(); Next();
            }
            else if (step == 5 && session.Stage == DeliveryStage.Carrying) { Check(!session.IsChasingFood, "walking over dropped food recovers order"); Pass(); }
        }
        private static void PauseMapSettlement(float since)
        {
            if (step == 0 && Bout()) { Set(cat, "remaining", .7f); Property(session, "IsPaused", true); Next(); }
            else if (step == 1 && since > .3f)
            {
                Check(Mathf.Abs((float)Get(cat, "remaining") - .7f) < .001f, "pause stops duel clock");
                Property(session, "IsPaused", false); Set(session, "mapOpen", true); Next();
            }
            else if (step == 2 && since > .3f)
            {
                Check(Mathf.Abs((float)Get(cat, "remaining") - .7f) < .001f, "map prevents attack updates");
                Set(session, "mapOpen", false); Property(session, "DayPhase", WorkDayPhase.Settlement); Next();
            }
            else if (step == 3 && since > .1f) { Check(cat.State == CatBoxerHazard.Mode.Spent && !view.gameObject.activeSelf, "settlement clears duel"); Pass(); }
        }
        private static bool Bout() => cat.State == CatBoxerHazard.Mode.Bout;
        private static void Tell(CatBoxerHazard.Attack attack, float distance)
        {
            Stand(distance); Property(cat, "Phase", CatBoxerHazard.Beat.Tell); Property(cat, "CurrentAttack", attack);
            Set(cat, "aim", Vector3.forward); Set(cat, "remaining", .25f);
        }
        private static void Stand(float distance)
        {
            rider.position = cat.Position + Vector3.forward * distance + Vector3.up * .02f; rider.linearVelocity = Vector3.zero; rider.useGravity = true;
            cameraView.rotation = Quaternion.LookRotation(cat.Position - rider.position); Physics.SyncTransforms();
        }
        private static void AimPunch() { InputSystem.QueueStateEvent(keyboard, new KeyboardState(Key.F)); lastPunch = Time.time; }
        private static void Release() { if (Time.time - lastPunch > .05f) InputSystem.QueueStateEvent(keyboard, new KeyboardState()); }
        private static void Recover()
        {
            Call(motor, "ResetReaction"); Set(motor, "trafficRecoveryActive", false); Set(motor, "nextTrafficHitAt", 0f);
            Set(motor, "lastHazardHitAt", float.NegativeInfinity); Set(motor, "lastHazard", null); rider.linearVelocity = Vector3.zero;
        }
        private static void Next() { step++; stepStarted = Time.time; }
        private static void Pass()
        {
            Debug.Log("CAT_PLAY_PASS " + Names[scenario] + " hits=" + observedHits + " punches=" + punches + " trapped=0");
            if (++scenario == Names.Length) { Debug.Log("CAT_PLAY_ALL_PASS: " + Names.Length + " actual Play scenarios."); Finish(null); }
            else Build();
        }
        private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(Names[scenario] + ": " + message); }
        private static void Finish(Exception error)
        {
            EditorApplication.update -= Observe; Release();
            if (error != null) Debug.LogException(error);
            SessionState.SetInt(ExitCode, error == null ? 0 : 1);
            if (keyboard != null && keyboard.added) InputSystem.RemoveDevice(keyboard);
            Time.timeScale = 1f; EditorApplication.isPlaying = false;
        }
        private static object Get(object owner, string name) => owner.GetType().GetField(name, Any).GetValue(owner);
        private static void Set(object owner, string name, object value) => owner.GetType().GetField(name, Any).SetValue(owner, value);
        private static void Property(object owner, string name, object value) => owner.GetType().GetProperty(name, Any).SetValue(owner, value);
        private static object Call(object owner, string name, params object[] args) => owner.GetType().GetMethod(name, Any).Invoke(owner, args);
    }
}
