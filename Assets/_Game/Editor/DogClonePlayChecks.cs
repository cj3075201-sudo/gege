using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.Splines;
using Unity.Mathematics;

namespace DeliveryRider.Editor
{
    // Explicit batch Play probe. Real Update/FixedUpdate, PhysX, virtual input, throwaway scenes and no player saves.
    [InitializeOnLoad]
    public static class DogClonePlayChecks
    {
        private const string Pending = "DogClonePlayChecks.Pending", Exit = "DogClonePlayChecks.Exit";
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly string[] Cases = {
            "dog_no_food", "dog_bite_30m", "dog_avoid", "dog_punch", "dog_gate_chase", "dog_gate_dash",
            "dog_boundary", "dog_recover", "dog_trigger_scale", "dog_chain", "clone_no_food", "clone_catch", "clone_escape",
            "clone_avoid", "clone_gate", "clone_disable_stolen", "clone_boundary", "clone_decoy_punch", "clone_trigger_scale"
        };
        private static Keyboard keyboard;
        private static DeliverySession session;
        private static RiderMotor rider;
        private static Rigidbody body;
        private static HazardSlot slot;
        private static DogHazard dog;
        private static CloneRiderHazard clone;
        private static int scenario, hits;
        private static float started, changed, maximumSpeed, maximumZ, dashStart, dashEnd;
        private static bool stolen, action, sawActive;
        private static double deadline;
        private static readonly List<string> Failures = new List<string>();
        private static ZoneMap zoneMap;

        static DogClonePlayChecks() => EditorApplication.playModeStateChanged += OnMode;

        public static void Run()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            SessionState.SetBool(Pending, true); SessionState.SetInt(Exit, 1);
            EditorApplication.isPlaying = true;
        }

        private static void OnMode(PlayModeStateChange mode)
        {
            if (!SessionState.GetBool(Pending, false)) return;
            if (mode == PlayModeStateChange.EnteredPlayMode)
            {
                try
                {
                    Time.timeScale = 10f; Application.runInBackground = true;
                    InputSystem.settings = UnityEngine.Object.Instantiate(InputSystem.settings);
                    InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
                    InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
                    keyboard = InputSystem.AddDevice<Keyboard>("DogCloneProbeKeyboard");
                    scenario = 0; Failures.Clear(); Build(); EditorApplication.update += Observe;
                }
                catch (Exception error) { Finish(error); }
            }
            if (mode == PlayModeStateChange.EnteredEditMode)
            { SessionState.SetBool(Pending, false); EditorApplication.Exit(SessionState.GetInt(Exit, 1)); }
        }

        private static void Build()
        {
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects()) UnityEngine.Object.DestroyImmediate(root);
            if (zoneMap != null) UnityEngine.Object.DestroyImmediate(zoneMap);
            Keys();
            string name = Cases[scenario];
            hits = 0; stolen = action = sawActive = false;
            maximumSpeed = maximumZ = 0f; dashStart = dashEnd = 0f; changed = 0f;
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Ground"; floor.transform.position = new Vector3(0f, -0.5f, 50f); floor.transform.localScale = new Vector3(200f, 1f, 600f);
            var network = new GameObject("RoadNetwork").AddComponent<RoadNetwork>();
            network.Configure(new[] { floor.GetComponent<Collider>() });
            var lane = new GameObject("BakedRoad"); lane.transform.SetParent(network.transform);
            var spline = lane.AddComponent<SplineContainer>();
            spline.Spline.Add(new BezierKnot(new float3(0f, 0f, -250f)));
            spline.Spline.Add(new BezierKnot(new float3(0f, 0f, 300f)));
            lane.AddComponent<RoadSurface>().Configure(RoadKind.Main, 12f, 3f);
            var host = new GameObject("InactiveServices"); host.SetActive(false);
            session = host.AddComponent<DeliverySession>();
            var bag = new GameObject("FoodBag"); Set(session, "foodBag", bag);
            var player = new GameObject("Rider"); player.SetActive(false); player.transform.position = new Vector3(0f, 0.02f, 0f);
            body = player.AddComponent<Rigidbody>(); body.mass = 75f; body.constraints = RigidbodyConstraints.FreezeRotation;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            var shape = player.AddComponent<CapsuleCollider>(); shape.center = Vector3.up * 0.9f; shape.height = 1.8f; shape.radius = 0.5f;
            rider = player.AddComponent<RiderMotor>();
            var view = new GameObject("View").transform; view.forward = name == "dog_punch" ? Vector3.back : Vector3.forward;
            Set(rider, "session", session); Set(rider, "view", view); Set(rider, "speed", name == "dog_avoid" || name == "clone_avoid" ? 9.6f : 6f);
            Set(session, "player", player.transform); Set(session, "rider", rider);
            var pickup = new GameObject("Pickup").transform; pickup.position = new Vector3(-50f, 0f, -50f);
            var recipient = host.AddComponent<DeliveryResident>();
            var offer = new DeliveryOrderOffer(1, pickup, recipient, pickup, "shop", "recipient", "food", 3000, 1800, 300f, 100f, "near", "C", "A", 0, 0);
            Property(session, "ActiveOrder", offer); Property(session, "HasPickedUp", true); Set(session, "orderDeadline", 100000d);
            Property(session, "Stage", name.EndsWith("no_food") ? DeliveryStage.Pickup : DeliveryStage.Carrying);
            if (name.EndsWith("boundary"))
            {
                zoneMap = ScriptableObject.CreateInstance<ZoneMap>();
                Set(zoneMap, "zones", new[] { new ZoneMap.Zone { id = "B", polygon = new[] { new Vector2(-100, 4), new Vector2(100, 4), new Vector2(100, 250), new Vector2(-100, 250) } } });
                Set(session, "zoneMap", zoneMap); Set(session, "openZones", new[] { "C", "A" });
                var wall = GameObject.CreatePrimitive(PrimitiveType.Cube); wall.name = "ClosedZoneBarrier";
                wall.transform.position = new Vector3(0f, 2f, 4f); wall.transform.localScale = new Vector3(100f, 4f, 0.6f);
                int layer = LayerMask.NameToLayer("ZoneBarrier"); if (layer >= 0) wall.layer = layer;
            }
            var staging = new GameObject("Staging"); staging.SetActive(false);
            bool isDog = name.StartsWith("dog");
            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(DogClonePrefabBuilder.Folder + (isDog ? "Dog" : "CloneRider") + ".prefab");
            var instance = UnityEngine.Object.Instantiate(asset, staging.transform);
            instance.transform.position = isDog ? new Vector3(0f, 0.02f, name == "dog_trigger_scale" ? -20f : -6f) : new Vector3(0f, 0.02f, name == "clone_trigger_scale" ? -15f : -2f);
            slot = instance.AddComponent<HazardSlot>(); slot.Configure(isDog ? "dog" : "clone", HazardRole.Trigger, 2, Array.Empty<int>()); slot.StrikeAllowed = HazardPlanner.DeliveryInProgress(session);
            slot.StrikeAllowed = !name.EndsWith("no_food");
            if (name.EndsWith("trigger_scale")) slot.SetTriggerScale(2f);
            foreach (var hazard in instance.GetComponents<IPlacedHazard>()) hazard.Place(session, network, new HazardCandidate());
            dog = instance.GetComponent<DogHazard>(); clone = instance.GetComponent<CloneRiderHazard>();
            rider.HazardHit += _ => { hits++; if (dog != null) dashStart = body.position.z; };
            player.SetActive(true); instance.transform.SetParent(null, true); UnityEngine.Object.DestroyImmediate(staging);
            Physics.SyncTransforms();
            started = Time.time; deadline = EditorApplication.timeSinceStartup + 50d;
            if (name == "dog_avoid" || name == "clone_avoid") Keys(Key.W);
        }

        private static void Observe()
        {
            try
            {
                if (EditorApplication.timeSinceStartup > deadline) throw new InvalidOperationException("probe deadline");
                float t = Time.time - started;
                string name = Cases[scenario];
                maximumSpeed = Mathf.Max(maximumSpeed, rider.HorizontalSpeed); maximumZ = Mathf.Max(maximumZ, body.position.z);
                string phase = dog != null ? Get<object>(dog, "phase").ToString() : Get<object>(clone, "phase").ToString();
                if (phase == "Chasing" || phase == "Dashing" || phase == "Approaching" || phase == "Escaping") sawActive = true;
                if (session.IsChasingFood) stolen = true;
                bool done = false;
                switch (name)
                {
                    case "dog_no_food":
                        if (t > 2f) { Check(!sawActive && hits == 0 && !dog.transform.Find("DogBody").gameObject.activeSelf, "hidden without food"); done = true; } break;
                    case "dog_bite_30m":
                        if (hits > 0 && phase != "Dashing")
                        {
                            dashEnd = body.position.z;
                            Check(hits == 1 && maximumSpeed >= 9.1f && dashEnd - dashStart > 27f && dashEnd - dashStart < 33f && rider.Speed == 6f, "30m dash and base speed restored");
                            done = true;
                        }
                        else if (t > 9f) throw new InvalidOperationException("dog never finished a bite/dash");
                        break;
                    case "dog_avoid":
                        if (t > 10f) { Check(sawActive && hits == 0 && phase == "Spent", "inline outruns dog"); done = true; } break;
                    case "dog_punch":
                        if (!action && phase == "Chasing" && Vector3.Distance(body.position, dog.transform.Find("DogBody").position) < 2f) { Keys(Key.F); action = true; changed = Time.time; }
                        if (action && Time.time - changed > 0.3f) { Keys(); Check(hits == 0 && phase == "Spent", "real F punch stops dog"); done = true; }
                        else if (t > 3f) throw new InvalidOperationException("dog punch never happened");
                        break;
                    case "dog_gate_chase": case "dog_gate_dash":
                        if (!action && phase == (name.EndsWith("chase") ? "Chasing" : "Dashing")) { slot.StrikeAllowed = false; changed = Time.time; action = true; }
                        if (action && Time.time - changed > 0.2f) { Check(phase == "Spent" && !rider.IsSlipping && rider.Speed == 6f, "gate clears owned pursuit/dash"); done = true; }
                        break;
                    case "dog_boundary":
                        if (t > 5f) { Check(hits == 1 && maximumZ < 3.2f && !rider.IsRecovering && !rider.IsSlipping, "wall blocks dash and recovery finishes"); done = true; } break;
                    case "dog_recover":
                        if (!action && phase == "Dashing") { Keys(Key.R); changed = Time.time; action = true; }
                        if (action && Time.time - changed > 0.2f) { Keys(); Check(!rider.IsSlipping && phase == "Spent", "safe recovery cancels dash"); done = true; } break;
                    case "dog_trigger_scale":
                        if (t > 0.3f) { Check(sawActive, "2x dog trigger sees 20m"); done = true; } break;
                    case "dog_chain":
                        if (!action && phase == "Dashing" && t > 1.2f)
                        { Check(rider.TryHazardSlip(session, Vector3.right, 2f), "another hazard accepts a chained slip"); action = true; changed = Time.time; }
                        if (action && Time.time - changed > 0.1f)
                        { Check(phase == "Spent" && rider.IsSlipping && rider.HorizontalSpeed <= 8.1f, "dog yields controls to the next incident"); done = true; }
                        break;
                    case "clone_no_food":
                        if (t > 2f) { Check(!sawActive && !stolen && LiveClones() == 0, "hidden without food"); done = true; } break;
                    case "clone_catch":
                        if (stolen && !rider.IsRecovering && !action)
                        { body.position = session.Target.position - Vector3.forward * 1.5f; body.linearVelocity = Vector3.zero; Keys(Key.F); action = true; changed = Time.time; }
                        if (action && Time.time - changed > 0.3f) { Keys(); Check(session.Stage == DeliveryStage.Carrying && !session.IsChasingFood && LiveClones() == 0, "real punch restores food"); done = true; }
                        break;
                    case "clone_escape":
                        if (stolen && !session.IsChasingFood && !action)
                        {
                            Check(session.Stage == DeliveryStage.FoodLost && session.Target == session.ActiveOrder.PickupPoint && !rider.IsRecovering, "escape points to shop");
                            body.position = session.ActiveOrder.PickupPoint.position;
                            action = true; changed = Time.time;
                        }
                        if (action && Time.time - changed > 0.2f)
                        { Check(session.Stage == DeliveryStage.Carrying, "entering shop range restores food"); done = true; }
                        break;
                    case "clone_avoid":
                        if (t > 7f) { Check(sawActive && !stolen && phase == "Spent", "inline outruns clones"); done = true; } break;
                    case "clone_gate":
                        if (!action && phase == "Approaching") { slot.StrikeAllowed = false; action = true; changed = Time.time; }
                        if (action && Time.time - changed > 0.2f) { Check(!stolen && LiveClones() == 0 && phase == "Spent", "gate clears three approaches"); done = true; } break;
                    case "clone_disable_stolen":
                        if (stolen && !action) { clone.gameObject.SetActive(false); action = true; changed = Time.time; }
                        if (action && Time.time - changed > 2.5f) { Check(!session.IsChasingFood && session.Stage == DeliveryStage.FoodLost && LiveClones() == 0 && !rider.IsRecovering, "despawn preserves replacement path"); done = true; } break;
                    case "clone_boundary":
                        if (stolen && t > 5f)
                        {
                            foreach (var actor in clone.GetComponentsInChildren<FoodThief>(true)) Check(actor.transform.position.z < 3.1f, "thief stays before closed border");
                            Check(!rider.IsRecovering, "theft knockdown recovered"); done = true;
                        }
                        break;
                    case "clone_decoy_punch":
                        if (!action && phase == "Approaching")
                        {
                            var actor = clone.GetComponentsInChildren<FoodThief>(true)[0];
                            body.position = actor.transform.position - Vector3.forward * 1.5f; body.linearVelocity = Vector3.zero;
                            Keys(Key.F); action = true; changed = Time.time;
                        }
                        if (action && Time.time - changed > 0.15f) { Keys(); Check(LiveClones() < 3 && !stolen, "decoy punch removes one without food loss"); done = true; } break;
                    case "clone_trigger_scale":
                        if (t > 0.3f)
                        {
                            Check(sawActive && LiveClones() == 3, "2x clone trigger sees 15m");
                            foreach (var actor in clone.GetComponentsInChildren<FoodThief>(true))
                                Check(body.position.z - actor.transform.position.z > 10f, "clones actually spawn 14m behind before closing");
                            done = true;
                        } break;
                }
                if (!done && t < 43f) return;
                if (!done) throw new InvalidOperationException("scenario stalled at phase=" + phase + " stage=" + session.Stage);
                Debug.Log($"DOG_CLONE_PLAY_PASS {name} time={t:0.00}s hits={hits} stolen={stolen} maxSpeed={maximumSpeed:0.00} maxZ={maximumZ:0.00} phase={phase} trapped=0");
                Next();
            }
            catch (Exception error)
            {
                Failures.Add(Cases[scenario] + ": " + error.Message);
                Debug.LogError("DOG_CLONE_PLAY_FAIL " + Cases[scenario] + " t=" + (Time.time - started) + " active=" + sawActive
                    + " stolen=" + stolen + " speed=" + maximumSpeed + " phase=" + (dog != null ? Get<object>(dog, "phase") : Get<object>(clone, "phase")) + " " + error);
                Next();
            }
        }

        private static void Next()
        {
            if (++scenario < Cases.Length) { Build(); return; }
            if (Failures.Count > 0) Finish(new InvalidOperationException(string.Join("; ", Failures)));
            else { Debug.Log("DOG_CLONE_PLAY_ALL_PASS: " + Cases.Length + " real Play scenarios; trapped=0."); Finish(null); }
        }
        private static int LiveClones()
        { int count = 0; foreach (var actor in clone.GetComponentsInChildren<FoodThief>(true)) if (actor.gameObject.activeInHierarchy) count++; return count; }
        private static void Finish(Exception error)
        {
            EditorApplication.update -= Observe; if (error != null) Debug.LogException(error);
            SessionState.SetInt(Exit, error == null ? 0 : 1);
            if (keyboard != null && keyboard.added) InputSystem.RemoveDevice(keyboard);
            Time.timeScale = 1f; EditorApplication.isPlaying = false;
        }
        private static void Keys(params Key[] keys) => InputSystem.QueueStateEvent(keyboard, new KeyboardState(keys));
        private static void Check(bool condition, string message) => DogCloneChecks.Check(condition, Cases[scenario] + ": " + message);
        private static T Get<T>(object owner, string field) => (T)owner.GetType().GetField(field, Any).GetValue(owner);
        private static void Set(object owner, string field, object value) => owner.GetType().GetField(field, Any).SetValue(owner, value);
        private static void Property(object owner, string property, object value)
        {
            owner.GetType().GetProperty(property, Any).SetValue(owner, value);
            if (ReferenceEquals(owner, session) && property == "Stage" && slot != null) slot.StrikeAllowed = HazardPlanner.DeliveryInProgress(session);
        }
    }
}
