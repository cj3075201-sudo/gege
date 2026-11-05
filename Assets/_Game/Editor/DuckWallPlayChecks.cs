using System;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using static DeliveryRider.Editor.DuckWallChecks;

namespace DeliveryRider.Editor
{
    // Explicitly requested Play probe. Real prefab, RiderMotor, gravity, recovery and virtual keyboard in throwaway corridors.
    [InitializeOnLoad]
    public static class DuckWallPlayChecks
    {
        private const string Pending = "DuckWallPlayChecks.Pending";
        private const string ExitCode = "DuckWallPlayChecks.ExitCode";
        private static readonly string[] Modes = { "run", "jump", "inline-jump", "sidewalk", "wait", "repeat", "high-flight", "no-food", "lose-food" };
        private static Keyboard keyboard;
        private static DeliverySession probeSession;
        private static RiderMotor motor;
        private static Rigidbody body;
        private static CrossingFlockHazard ducks;
        private static HazardSlot slot;
        private static int scenario, hits;
        private static float started, width, openedAt, secondHitAt, jumpPeak, farthest, highCrossHeight, hitHeight, maximumFlightHeight;
        private static int fixedSamples;
        private static double deadline;
        private static bool jumpSent, waitingReleased, launchSent, lostFood, repeatReturning;
        private static float repeatSide;

        static DuckWallPlayChecks() => EditorApplication.playModeStateChanged += OnMode;

        public static void Run()
        {
            SessionState.SetInt("DuckWallPlayChecks.Start", 0);
            StartProbe();
        }

        public static void RunHighFlight()
        {
            SessionState.SetInt("DuckWallPlayChecks.Start", 6);
            StartProbe();
        }

        private static void StartProbe()
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
                    keyboard = InputSystem.AddDevice<Keyboard>("DuckProbeKeyboard");
                    scenario = SessionState.GetInt("DuckWallPlayChecks.Start", 0);
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

        private static string Mode => Modes[scenario % Modes.Length];

        private static void Keys(params Key[] keys) => InputSystem.QueueStateEvent(keyboard, new KeyboardState(keys));

        private static void Build()
        {
            Keys();
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects()) UnityEngine.Object.DestroyImmediate(root);
            width = scenario < Modes.Length ? 22f : 5f;
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "SupportedFloor";
            floor.transform.position = Vector3.down * .64f;
            floor.transform.localScale = new Vector3(width + 2f, 1f, 240f);
            foreach (float side in new[] { -1f, 1f })
            {
                float roadHalf = width < 10f ? 1f : 4f;
                float sidewalkWidth = width / 2f - roadHalf;
                var sidewalk = GameObject.CreatePrimitive(PrimitiveType.Cube);
                sidewalk.name = "RaisedSidewalk";
                sidewalk.transform.position = new Vector3(side * (roadHalf + sidewalkWidth / 2f), -.5f, 0f);
                sidewalk.transform.localScale = new Vector3(sidewalkWidth, 1f, 240f);
                var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                wall.name = "BuildingWall";
                wall.transform.position = new Vector3(side * (width / 2f + .5f), 4f, 0f);
                wall.transform.localScale = new Vector3(1f, 8f, 240f);
            }
            var host = new GameObject("InactiveServices"); host.SetActive(false);
            var session = host.AddComponent<DeliverySession>(); probeSession=session;
            typeof(DeliverySession).GetProperty("Stage").SetValue(session, Mode=="no-food"?DeliveryStage.Pickup:DeliveryStage.Carrying);
            var network = host.AddComponent<RoadNetwork>();
            network.Configure(new[] { floor.GetComponent<Collider>() });
            var player = new GameObject("ProbeRider"); player.SetActive(false);
            player.transform.position = new Vector3(Mode == "sidewalk" ? width / 2f - .7f : 0f, .02f,
                Mode == "wait" || Mode == "repeat" ? -2f : -6f);
            body = player.AddComponent<Rigidbody>();
            body.mass = 75f; body.constraints = RigidbodyConstraints.FreezeRotation;
            var capsule = player.AddComponent<CapsuleCollider>();
            capsule.center = Vector3.up * .9f; capsule.height = 1.8f; capsule.radius = .5f;
            motor = player.AddComponent<RiderMotor>();
            player.AddComponent<DuckWallPlayFixedSample>();
            var view = new GameObject("View").transform;
            Set(session, "player", player.transform);
            Set(motor, "session", session); Set(motor, "view", view);
            Set(motor, "speed", 6f); Set(motor, "jumpSpeed", 4.24f);
            Set(motor, "groundMask", (LayerMask)1); Set(motor, "followSlopes", true);
            player.SetActive(true);
            if (Mode == "inline-jump") motor.EquipInline(1.6f, 1f);
            Physics.SyncTransforms();
            ducks = Make(network, new HazardCandidate { type = "duck", halfWidth = width < 10f ? 1f : 4f, side = 1 }, session);
            slot = ducks.gameObject.AddComponent<HazardSlot>();
            slot.Configure("duck", HazardRole.Trigger, 1, Array.Empty<int>());
            slot.StrikeAllowed = Mode != "no-food";
            ducks.transform.SetParent(null, true);
            ducks.gameObject.SetActive(true);
            motor.HazardHit += source =>
            {
                if (source != ducks) return;
                hits++;
                hitHeight = body.position.y;
                if (hits == 2) secondHitAt = Time.time - started;
                Check(body.linearVelocity.z < 1f, "contact launch replaces incoming movement");
            };
            hits = 0; jumpSent = waitingReleased = launchSent = lostFood = repeatReturning = false; repeatSide=0f;
            started = Time.time; openedAt = secondHitAt = jumpPeak = farthest = 0f;
            highCrossHeight = hitHeight = 0f;
            fixedSamples = 0; maximumFlightHeight = 0f;
            deadline = EditorApplication.timeSinceStartup + 45d;
            Physics.SyncTransforms();
            Keys(Mode == "wait" || Mode == "high-flight" ? Array.Empty<Key>() : new[] { Key.W });
            if (Mode == "high-flight")
            {
                // Launch at a deterministic fixture boundary, before any simulation steps can move the rider.
                motor.SetInputBlocked(true);
                Check(motor.TryHazardCrash(motor, new Vector3(0f, 15.6f, 6f), .8f), "high-launch fixture accepted by real motor");
                launchSent = true;
            }
        }

        private static void Observe()
        {
            try
            {
                float elapsed = Time.time - started;
                bool crossing = Get<object>(ducks, "phase").ToString() == "Crossing";
                bool spent = Get<object>(ducks, "phase").ToString() == "Spent";
                if (EditorApplication.timeSinceStartup > deadline || elapsed > 75f)
                    throw new InvalidOperationException($"trapped/timeout: {Mode} width={width} rider={body.position} hits={hits}");
                farthest = Mathf.Max(farthest, -body.position.z);
                if (Mode.Contains("jump") && !jumpSent && body.position.z > -3.8f && motor.IsGroundedForHazards)
                {
                    Keys(Key.W, Key.Space); jumpSent = true;
                }
                foreach (var bird in Get<Transform[]>(ducks, "birds"))
                    if (bird.gameObject.activeSelf) jumpPeak = Mathf.Max(jumpPeak, bird.position.y);
                if ((Mode == "run" || Mode == "jump" || Mode == "inline-jump" || Mode == "sidewalk") && hits > 0)
                {
                    if (Mode.Contains("jump") && jumpPeak < .02f) return;
                    if (Mode.Contains("jump")) Check(jumpSent && hitHeight > .1f, "ordinary jump is hit while airborne");
                    Check(body.position.z < 1f, "normal movement never gets through the row");
                    Pass(); return;
                }
                if (Mode == "wait")
                {
                    if (hits != 0) throw new InvalidOperationException("waiting rider was hit");
                    if (spent && !waitingReleased)
                    {
                        openedAt = elapsed; waitingReleased = true; Keys(Key.W);
                        float expected = Get<int>(ducks, "activeBirds") * Get<float>(ducks, "effectiveSpacing") / Get<float>(ducks, "walkSpeed");
                        Check(Mathf.Abs(openedAt - expected) < .5f, "measured opening matches queue duration");
                    }
                    if (waitingReleased && body.position.z > 3f) { Pass(); return; }
                }
                if (Mode == "repeat")
                {
                    if(hits>0&&!motor.IsRecovering)
                    {
                        if(repeatSide==0f)repeatSide=body.position.z>=0f?1f:-1f;
                        if(!repeatReturning)
                        {
                            if(Mathf.Abs(body.position.z)<12f)Keys(repeatSide>0f?Key.W:Key.S);else Keys();
                            if(!crossing&&Mathf.Abs(body.position.z)>9f)repeatReturning=true;
                        }
                        else Keys(body.position.z>0f?Key.S:Key.W);
                    }
                    if (hits >= 2) { Check(secondHitAt > 1f, "rider returns after recovery and is hit twice"); Pass(); return; }
                }
                if (Mode == "high-flight")
                {
                    if (hits != 0) throw new InvalidOperationException($"high flight hit at {body.position}, elapsed={elapsed:0.00}, launched={launchSent}");
                    if (body.position.z > 2f)
                    {
                        Check(hits == 0 && highCrossHeight > 2.2f,
                            $"high hazard flight clears row (cross={highCrossHeight:0.00}, max={maximumFlightHeight:0.00}, samples={fixedSamples}, p={body.position:F2}, v={body.linearVelocity:F2}, state={motor.ReactionState})");
                        Pass(); return;
                    }
                }
                if (Mode == "lose-food" && crossing && body.position.z > -3f && !lostFood)
                {
                    typeof(DeliverySession).GetProperty("Stage").SetValue(probeSession,DeliveryStage.FoodLost);
                    slot.StrikeAllowed = HazardPlanner.DeliveryInProgress(probeSession); lostFood = true;
                }
                if(Mode=="lose-food"&&lostFood&&hits>0){Check(slot.StrikeAllowed,"food recovery remains armed");Pass();return;}
                if (Mode == "no-food" && body.position.z > 3f)
                {
                    Check(hits == 0, "disabled strike gate permits passage"); Pass(); return;
                }
                if (body.position.z > 2f && hits == 0 && Mode != "wait" && Mode != "high-flight" && Mode != "no-food" && Mode != "lose-food")
                    throw new InvalidOperationException($"escaped duck row: {Mode} width={width} jump={jumpSent}");
            }
            catch (Exception error) { Finish(error); }
        }

        private static void Pass()
        {
            Debug.Log($"DUCK_PLAY width={width:0.0} mode={Mode} elapsed={Time.time - started:0.00}s hits={hits} duckJump={jumpPeak:0.00} hitHeight={hitHeight:0.00} highCross={highCrossHeight:0.00} opening={openedAt:0.00}s secondHit={secondHitAt:0.00}s farthest={farthest:0.00}m trapped=0");
            if (++scenario < Modes.Length * 2) { Build(); return; }
            Debug.Log("DUCK_PLAY_PASS: 18 actual Play scenarios; wide road and narrow alley, real motor and prefab.");
            Finish(null);
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

        internal static void SampleFixed()
        {
            if (body == null || ducks == null) return;
            fixedSamples++;
            maximumFlightHeight = Mathf.Max(maximumFlightHeight, body.position.y);
            if (Mode == "high-flight" && Mathf.Abs(body.position.z) <= .85f)
                highCrossHeight = Mathf.Max(highCrossHeight, body.position.y);
            foreach (var bird in Get<Transform[]>(ducks, "birds"))
                if (bird.gameObject.activeSelf) jumpPeak = Mathf.Max(jumpPeak, bird.position.y);
        }
    }

    [DefaultExecutionOrder(10000)]
    public sealed class DuckWallPlayFixedSample : MonoBehaviour
    {
        private void FixedUpdate() => DuckWallPlayChecks.SampleFixed();
    }
}
