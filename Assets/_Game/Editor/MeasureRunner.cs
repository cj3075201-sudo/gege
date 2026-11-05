using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DeliveryRider.Editor
{
    // Play-mode measurement in batchmode, no game code changed. Usage (no -quit):
    //   -executeMethod DeliveryRider.Editor.MeasureRunner.Begin -measurePlan drive|flight|clean -measureOut <file.jsonl>
    //   [-measureSeeds 10] [-measureDays 1,2,3,4] [-measureLanes lane,center] [-measureSeconds 300] [-measureDay1Seconds 900]
    //   [-measureTweak manhole=2,hydrant=2,chicken=5,muscleman=2] [-measureTrials 10]
    [InitializeOnLoad]
    public static class MeasureRunner
    {
        private const string ActiveKey = "DeliveryRider.Measure.Active";
        public const string ScenePath = "Assets/_Game/Scenes/FourDistrictPrototype_Myeongji.unity";

        static MeasureRunner()
        {
            if (!SessionState.GetBool(ActiveKey, false)) return;
            EditorApplication.update += StartBotWhenPlaying;
        }

        public static void Begin()
        {
            SessionState.SetBool(ActiveKey, true);
            EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            EditorApplication.EnterPlaymode();
        }

        private static void StartBotWhenPlaying()
        {
            if (!EditorApplication.isPlaying) return;
            EditorApplication.update -= StartBotWhenPlaying;
            var host = new GameObject("MeasureBot");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<MeasureBot>();
        }

        // What blocks the straight walk from where the bot stood to a pickup point.
        public static void ProbeBlocked()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Physics.SyncTransforms();
            var anchors = JArray.Parse(File.ReadAllText("Docs/MapLayout/Myeongji/anchors_expanded.json"));
            var session = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<DeliverySession>(true)).Single();
            var so = new SerializedObject(session);
            var shops = so.FindProperty("shopPoints");
            var doors = so.FindProperty("doorPoints");
            foreach (var property in new[] { shops, doors })
                for (int i = 0; i < property.arraySize; i++)
                {
                    var t = (Transform)property.GetArrayElementAtIndex(i).objectReferenceValue;
                    var a = anchors.First(x => (string)x["name"] == t.name);
                    var road = new Vector3((float)a["roadPoint"][0], t.position.y + 0.9f, (float)a["roadPoint"][2]);
                    var goal = t.position + Vector3.up * 0.9f;
                    var delta = goal - road;
                    var hits = Physics.CapsuleCastAll(road + Vector3.up * 0.5f, road - Vector3.up * 0.4f, 0.48f, delta.normalized, delta.magnitude, 1, QueryTriggerInteraction.Ignore);
                    var blockers = hits.Where(h => h.rigidbody == null && h.normal.y < 0.7f).Select(h => $"{h.collider.name}@{h.distance:0.0}").Distinct().ToArray();
                    Debug.Log($"MEASURE probe {t.name} at {t.position} roadPoint {road} dist {delta.magnitude:0.0} blockers [{string.Join(",", blockers)}]");
                }
            var from = new Vector3(8f, 0.86f, -141.15f);
            foreach (var hit in Physics.OverlapSphere(from + Vector3.forward * 0.6f, 0.6f, ~0, QueryTriggerInteraction.Ignore))
                Debug.Log($"MEASURE probe near (8,-141) {hit.name} layer {hit.gameObject.layer} {hit.GetType().Name} bounds {hit.bounds.min} {hit.bounds.max}");
            EditorApplication.Exit(0);
        }

        public static string Arg(string name, string fallback)
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
        }
    }

    [DefaultExecutionOrder(-1000)]
    public sealed class MeasureBot : MonoBehaviour
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private const float Step = 0.02f, SeenRange = 25f;
        private sealed class Config { public int day, seed; public string lane, plan; public float seconds; }
        private sealed class SlotWatch { public HazardSlot slot; public Component part; public FieldInfo phase, flag; public string last; public bool on; public bool lastFlag; public bool seen; }

        private string outPath;
        private DeliverySession session;
        private RiderMotor rider;
        private Rigidbody body;
        private HazardPlanner planner;
        private Transform proxy;
        private HazardZoneFilter open;
        private Vector3[] nodes;
        private List<(int to, float length)>[] links;
        private float[,] widths;
        private bool[] nodeOpen;
        private readonly List<SlotWatch> watches = new List<SlotWatch>();
        private TrafficCar[] cars = Array.Empty<TrafficCar>();
        private FieldInfo movementField, zoneHopField, floorBouncesField;

        private Config config;
        private bool driving, done;
        private float elapsed, carried, moved;
        private JObject counts;
        private readonly List<Vector3> path = new List<Vector3>();
        private int pathIndex;
        private Transform pathTarget;
        private float replanAt, stuckTimer;
        private Vector3 stuckFrom;
        private int stuckFixes, calls, stepCount;
        private DeliveryOrderOffer lastOrder;
        private RiderReactionState lastState;
        private bool pendingCrash;
        private readonly List<JObject> flights = new List<JObject>();
        private Flight flight;
        private sealed class Flight { public string type; public Vector3 from; public float y0, top, time, firstTime = -1f, firstDistance; public Vector3 impulse; public int impacts, hits; public int bounces; public readonly List<string> trace = new List<string>(); public string firstState; public float lowest = float.PositiveInfinity; public Vector3 lowestAt; }

        private void Start() => StartCoroutine(Main());

        private IEnumerator Main()
        {
            outPath = MeasureRunner.Arg("-measureOut", "measure.jsonl");
            string plan = MeasureRunner.Arg("-measurePlan", "drive");
            Application.runInBackground = true;
            Time.captureDeltaTime = Step;
            var controllerHost = new GameObject("MeasureController");
            DontDestroyOnLoad(controllerHost);
            var controller = SceneController.Instance != null ? SceneController.Instance : controllerHost.AddComponent<SceneController>();
            LoadGraph();
            IEnumerator body2 = plan == "gym" ? GymPlan(controller) : plan == "flight" ? FlightPlan(controller) : plan == "probe" ? ProbePlan(controller) : DrivePlan(controller, plan);
            while (true)
            {
                object current;
                try { if (!body2.MoveNext()) break; current = body2.Current; }
                catch (Exception failure) { Debug.LogError("MEASURE FAIL " + failure); EditorApplication.Exit(1); yield break; }
                yield return current;
            }
            Debug.Log("MEASURE DONE");
            EditorApplication.Exit(0);
        }

        private IEnumerator GymPlan(SceneController controller)
        {
            config=new Config{day=1,seed=1,lane="carriageway",plan="gym",seconds=30};
            yield return Load(controller,1,1);Prepare();
            foreach(var name in new[]{"Traffic","Pedestrians"})foreach(var root in SceneManager.GetActiveScene().GetRootGameObjects())if(root.name==name)root.SetActive(false);
            session.HandleCallKey(0);session.HandleCallKey(0);
            var pickup=session.ActiveOrder.PickupPoint.position;body.position=pickup;rider.transform.position=pickup;Physics.SyncTransforms();session.TryPickup();
            planner.enabled=false;
            foreach(var slot in planner.Slots)slot.SetRuntimeActive(false);
            var ground=(RoadNetwork)typeof(HazardPlanner).GetField("ground",Any).GetValue(planner);
            int trials=0,hitTrials=0;
            foreach(var site in planner.Slots.Where(s=>s.Type=="muscleman"))
            {
                Vector3 center=site.transform.position,forward=site.transform.forward;float best=float.PositiveInfinity,length=0f;
                for(int a=0;a<nodes.Length;a++)foreach(var edge in links[a])
                {
                    var delta=nodes[edge.to]-nodes[a];var flat=Flat(delta);float t=Mathf.Clamp01(Vector3.Dot(Flat(site.transform.position-nodes[a]),flat)/Mathf.Max(.001f,flat.sqrMagnitude));
                    var point=Vector3.Lerp(nodes[a],nodes[edge.to],t);float distance=Flat(site.transform.position-point).sqrMagnitude;
                    if(distance>=best)continue;best=distance;center=point;forward=flat.normalized;length=flat.magnitude;
                }
                if(length<45f)continue;
                trials++;site.SetRuntimeActive(true,true);site.StrikeAllowed=true;
                typeof(RiderMotor).GetMethod("ResetReaction",Any).Invoke(rider,null);typeof(RiderMotor).GetField("lastHazardHitAt",Any).SetValue(rider,float.NegativeInfinity);typeof(RiderMotor).GetField("lastHazard",Any).SetValue(rider,null);
                var start=ground.OnGround(center-forward*6f,1.05f);body.position=start;rider.transform.position=start;body.linearVelocity=Vector3.zero;Physics.SyncTransforms();
                var part=site.GetComponent<MusclemanHazard>();var field=typeof(MusclemanHazard).GetField("phase",Any);string previous="";float clock=0;bool hit=false;
                void Hit(UnityEngine.Object source){if(source==part)hit=true;}
                rider.HazardHit+=Hit;
                trialDrive=()=>{clock+=Step;Track();Head(forward);string state=field.GetValue(part).ToString();if(state!=previous){Debug.Log($"GYM_PROBE state={state} t={clock:F2} player={body.position} at={site.transform.position} active={site.RuntimeActive} enabled={part.enabled}");previous=state;}};
                while(clock<12f&&!hit)yield return null;
                trialDrive=null;rider.HazardHit-=Hit;site.SetRuntimeActive(false);if(hit)hitTrials++;
                Debug.Log($"GYM_PROBE trial={trials} hit={hit}");
                if(trials>=3)break;
            }
            if(trials==0||hitTrials==0)throw new InvalidOperationException("carriageway gym never hits: "+hitTrials+"/"+trials);
            Debug.Log($"GYM_PROBE_PASS carriageway hits={hitTrials}/{trials}");
        }

        // Walks at C_Shop_09 from several sides and lists what the capsule touches when it stops.
        private IEnumerator ProbePlan(SceneController controller)
        {
            config = new Config { day = 1, seed = 1, lane = "center", plan = "probe", seconds = 1e9f };
            yield return Load(controller, 1, 1);
            Prepare();
            typeof(RiderMotor).GetField("nextZoneCheckAt", Any).SetValue(rider, 1e9f);
            RemoveEverything();
            var goal = new Vector3(8.14f, 0f, -136.75f);
            var capsule = rider.GetComponent<CapsuleCollider>();
            var network = (RoadNetwork)typeof(HazardPlanner).GetField("ground", Any).GetValue(planner);
            foreach (var from in new[] { new Vector3(8f, 0f, -146f), new Vector3(0f, 0f, -141f), new Vector3(16f, 0f, -141f), new Vector3(3f, 0f, -146f), new Vector3(14f, 0f, -146f) })
            {
                var start = network.OnGround(from, 1.05f);
                body.position = start; rider.transform.position = start; body.linearVelocity = Vector3.zero;
                Physics.SyncTransforms();
                float clock = 0f;
                trialDrive = () => { clock += Step; if (Flat(goal - body.position).magnitude > 1.5f) Head(goal - body.position); else Stop(); };
                while (clock < 5f) yield return null;
                var bounds = capsule.bounds;
                var near = Physics.OverlapCapsule(bounds.center + Vector3.up * 0.4f, bounds.center - Vector3.up * 0.4f, 0.62f, ~0, QueryTriggerInteraction.Ignore)
                    .Where(c => c.attachedRigidbody != body).Select(c => $"{c.name}(layer {c.gameObject.layer},{c.GetType().Name},y{c.bounds.min.y:0.00}..{c.bounds.max.y:0.00})").ToArray();
                var ground = network.OnGround(body.position, 0f);
                Debug.Log($"MEASURE probe from {from} ended {body.position} left {Flat(goal - body.position).magnitude:0.0}m speed {body.linearVelocity.magnitude:0.0} groundY {ground.y:0.00} touching [{string.Join(", ", near)}]");
                trialDrive = null;
            }
        }

        private IEnumerator DrivePlan(SceneController controller, string plan)
        {
            int seeds = int.Parse(MeasureRunner.Arg("-measureSeeds", "10"));
            var days = MeasureRunner.Arg("-measureDays", "1,2,3,4").Split(',').Select(int.Parse).ToArray();
            var lanes = MeasureRunner.Arg("-measureLanes", "lane,center").Split(',');
            float seconds = float.Parse(MeasureRunner.Arg("-measureSeconds", "300"));
            float firstDay = float.Parse(MeasureRunner.Arg("-measureDay1Seconds", "900"));
            foreach (int day in days)
                foreach (string lane in lanes)
                    for (int seed = 1; seed <= seeds; seed++)
                    {
                        config = new Config { day = day, seed = seed, lane = lane, plan = plan, seconds = day == 1 ? firstDay : seconds };
                        yield return Load(controller, day, seed);
                        Prepare();
                        if (plan == "clean") RemoveEverything();
                        ApplyTweak();
                        driving = true; done = false;
                        float started = Time.realtimeSinceStartup;
                        while (!done) yield return null;
                        driving = false;
                        Write(Time.realtimeSinceStartup - started);
                    }
        }

        private IEnumerator Load(SceneController controller, int day, int seed)
        {
            Time.timeScale = 1f;
            controller.Progress = new RunProgress(day, 100000);
            UnityEngine.Random.InitState(seed * 7919);
            void Seeded(Scene scene, LoadSceneMode mode)
            {
                SceneManager.sceneLoaded -= Seeded;
                foreach (var found in FindObjectsByType<HazardPlanner>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    typeof(HazardPlanner).GetField("randomEachRun", Any).SetValue(found, false);
                    typeof(HazardPlanner).GetField("seed", Any).SetValue(found, seed * 1009 + day);
                }
            }
            SceneManager.sceneLoaded += Seeded;
            var loading = EditorSceneManager.LoadSceneAsyncInPlayMode(MeasureRunner.ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
            while (!loading.isDone) yield return null;
            yield return null;
            yield return null;
        }

        private void Prepare()
        {
            session = FindFirstObjectByType<DeliverySession>();
            rider = session.Player.GetComponent<RiderMotor>();
            body = rider.GetComponent<Rigidbody>();
            planner = FindFirstObjectByType<HazardPlanner>();
            foreach (var camera in FindObjectsByType<Camera>(FindObjectsSortMode.None)) camera.enabled = false;
            var proxyHost = new GameObject("MeasureView");
            proxy = proxyHost.transform;
            typeof(RiderMotor).GetField("view", Any).SetValue(rider, proxy);
            movementField = typeof(RiderMotor).GetField("movement", Any);
            zoneHopField = typeof(RiderMotor).GetField("zoneHopping", Any);
            floorBouncesField = typeof(RiderMotor).GetField("floorBounces", Any);
            open = new HazardZoneFilter(session.ZoneMap, session.IsZoneOpen);
            nodeOpen = nodes.Select(n => open.PointOpen(n)).ToArray();
            cars = FindObjectsByType<TrafficCar>(FindObjectsSortMode.None);

            counts = new JObject { ["placed"] = new JObject(), ["seen"] = new JObject(), ["triggered"] = new JObject(), ["hits"] = new JObject(), ["other"] = new JObject() };
            watches.Clear();
            foreach (var slot in planner.Slots)
            {
                if (slot == null) continue;
                Add("placed", slot.Type);
                var watch = new SlotWatch { slot = slot };
                Component part = slot.GetComponent<GroundContactHazard>();
                if (part == null) part = slot.GetComponent<CrossingFlockHazard>();
                if (part == null) part = slot.GetComponent<ChaseFlockHazard>();
                if (part == null) part = slot.GetComponent<RollingTireHazard>();
                if (part == null) part = slot.GetComponent<RollingMelonHazard>();
                if (part == null) part = slot.GetComponent<MusclemanHazard>();
                if (part == null) part = slot.GetComponent<PigeonSnatchHazard>();
                if (part == null) part = slot.GetComponent<CelebrityCrowdHazard>();
                if (part != null)
                {
                    watch.part = part;
                    watch.phase = part.GetType().GetField("phase", Any);
                    watch.flag = part.GetType().GetField(part is ChaseFlockHazard ? "hasAttachedBirds" : part is CelebrityCrowdHazard ? "appeared" : "__none", Any);
                    watch.last = watch.phase?.GetValue(part).ToString();
                }
                watches.Add(watch);
            }
            rider.HazardHit += OnHazardHit;
            rider.ReactionChanged += OnReaction;
            rider.ContactImpact += OnImpact;
            session.PigeonFlockObscured += OnPigeons;

            lastPosition = body.position; routeMeters = 0f; dodgeUntil = 0f;
            elapsed = carried = moved = 0f; path.Clear(); pathTarget = null; replanAt = 0f; stuckTimer = 0f; stuckFrom = body.position;
            stuckFixes = calls = stepCount = 0; lastOrder = null; lastState = RiderReactionState.Normal; pendingCrash = false; flight = null; flights.Clear();
        }

        private void RemoveEverything()
        {
            foreach (var slot in planner.Slots) if (slot != null) slot.gameObject.SetActive(false);
            foreach (var name in new[] { "Traffic", "Pedestrians" })
                foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
                    if (root.name == name) root.SetActive(false);
        }

        // What-if sizes for the recommendation: multiply a kind's touch size. Runs only on the measurement copy in memory.
        private void ApplyTweak()
        {
            string tweak = MeasureRunner.Arg("-measureTweak", "");
            if (tweak.Length == 0) return;
            var scale = tweak.Split(',').Select(p => p.Split('=')).ToDictionary(p => p[0], p => float.Parse(p[1]));
            foreach (var slot in planner.Slots)
            {
                if (slot == null || !scale.TryGetValue(slot.Type, out float k)) continue;
                var box = slot.GetComponent<BoxCollider>();
                if (box != null && (slot.Type == "manhole" || slot.Type == "hydrant" || slot.Type == "muscleman")) box.size = new Vector3(box.size.x * k, box.size.y, box.size.z * k);
                if (slot.Type == "chicken")
                {
                    var hazard = slot.GetComponent<ChaseFlockHazard>();
                    var field = typeof(ChaseFlockHazard).GetField("triggerRadius", Any);
                    field.SetValue(hazard, k);
                }
            }
            Physics.SyncTransforms();
        }

        // Settlement stops the clock (timeScale 0), so physics steps never come to notice it.
        private void Update()
        {
            if (driving && !done && session != null && session.IsSettled) done = true;
        }

        private void FixedUpdate()
        {
            if (trialDrive != null) { trialDrive(); return; }
            if (!driving || done) return;
            stepCount++;
            elapsed += Step;
            if (stepCount % 3000 == 0)
                Debug.Log($"MEASURE beat {config.day}/{config.lane}/{config.seed} t{elapsed:0} real{Time.realtimeSinceStartup:0} stage {session.Stage} state {rider.ReactionState} at {body.position} target {(session.Target != null ? session.Target.name + session.Target.position : "-")} path {pathIndex}/{path.Count} {DescribeStuck()}");
            if (session.IsSettled || elapsed >= config.seconds) { done = true; return; }
            Track();
            var now = body.position;
            float travelled = new Vector2(now.x - lastPosition.x, now.z - lastPosition.z).magnitude;
            if (travelled < 2f)
            {
                moved += travelled;
                if (session.Stage == DeliveryStage.Carrying || session.Stage == DeliveryStage.FoodLost) carried += travelled;
            }
            lastPosition = now;
            if (session.ActiveOrder != lastOrder)
            {
                lastOrder = session.ActiveOrder;
                foreach (var w in watches) w.seen = false;
                if (lastOrder != null) { calls++; routeMeters += lastOrder.Distance; }
                pathTarget = null;
            }
            if (session.ActiveOrder == null)
            {
                Stop();
                if (session.CanOpenCall) { session.HandleCallKey(0); session.HandleCallKey(0); }
                return;
            }
            Drive();
        }
        private Vector3 lastPosition, dodgeDirection;
        private float dodgeUntil;
        private float routeMeters;
        private Action trialDrive;

        private string DescribeStuck()
        {
            var bag = (JObject)counts["other"];
            return $"stuck {(int)(bag["stuck"] ?? 0)} calls {calls} done {session.CompletedCount} mapOpen {session.IsMapOpen} canMove {session.CanMove}";
        }

        private void Stop() => movementField.SetValue(rider, Vector2.zero);

        private void Head(Vector3 direction)
        {
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.0001f) { Stop(); return; }
            proxy.rotation = Quaternion.LookRotation(direction);
            movementField.SetValue(rider, Vector2.up);
        }

        private void Drive()
        {
            var target = session.Target;
            if (target == null) { Stop(); return; }
            if (rider.IsRecovering) { Stop(); replanAt = 0f; stuckTimer = 0f; stuckFrom = body.position; return; }
            var position = body.position;
            float toTarget = Flat(target.position - position).magnitude;
            bool punchable = session.Stage == DeliveryStage.Carrying || session.IsChasingFood && target.GetComponent<FoodThief>() != null;
            if (punchable && toTarget < 2f)
            {
                proxy.rotation = Quaternion.LookRotation(Flat(target.position - position));
                Stop();
                typeof(RiderMotor).GetMethod("TryPunch", Any).Invoke(rider, null);
                return;
            }
            if (target != pathTarget || elapsed >= replanAt) Plan(target);
            while (pathIndex < path.Count - 1 && Flat(path[pathIndex] - position).magnitude < 2.2f) pathIndex++;
            if (elapsed < dodgeUntil) Head(dodgeDirection); else Head(path[pathIndex] - position);
            stuckTimer += Step;
            if (stuckTimer >= 2f)
            {
                if (Flat(position - stuckFrom).magnitude < 0.8f)
                {
                    stuckFixes++;
                    if (stuckFixes % 20 == 5)
                    {
                        var bounds = rider.GetComponent<CapsuleCollider>().bounds;
                        var near = Physics.OverlapCapsule(bounds.center + Vector3.up * 0.4f, bounds.center - Vector3.up * 0.4f, 0.75f, ~0, QueryTriggerInteraction.Ignore)
                            .Where(c => c.attachedRigidbody != body).Select(c => $"{c.name}<{(c.attachedRigidbody != null ? c.attachedRigidbody.name : "static")}>(layer {c.gameObject.layer},trigger {c.isTrigger})").ToArray();
                        Debug.Log($"MEASURE stuck at {position} target {target.name}{target.position} next {path[pathIndex]} index {pathIndex}/{path.Count} velocity {body.linearVelocity} groundedForHazards {rider.IsGroundedForHazards} touching [{string.Join(", ", near)}]");
                    }
                    Add("other", "stuck");
                    // A person steps round what blocks them, such as a car waiting for them to move.
                    dodgeDirection = Vector3.Cross(Vector3.up, Flat(path[pathIndex] - position)) * (stuckFixes % 2 == 0 ? 1f : -1f);
                    dodgeUntil = elapsed + 1.2f;
                    if (stuckFixes % 3 == 0) typeof(RiderMotor).GetMethod("RestoreSafePosition", Any).Invoke(rider, null);
                    replanAt = 0f;
                }
                stuckTimer = 0f;
                stuckFrom = position;
            }
        }

        private void Plan(Transform target)
        {
            pathTarget = target;
            replanAt = float.PositiveInfinity;
            path.Clear();
            pathIndex = 0;
            int start = Nearest(body.position), goal = Nearest(target.position);
            var route = Dijkstra(start, goal);
            // Already past the nearest node on the way to the second: do not walk back to it.
            if (route.Count >= 2)
            {
                var along = Flat(nodes[route[1]] - nodes[route[0]]);
                float t = Vector3.Dot(Flat(body.position - nodes[route[0]]), along) / Mathf.Max(0.001f, along.sqrMagnitude);
                if (t > 0f && t < 1f) route.RemoveAt(0);
            }
            for (int i = 0; i < route.Count; i++)
            {
                var point = nodes[route[i]];
                if (config.lane == "lane")
                {
                    var side = Vector3.zero;
                    if (i > 0) side += LaneShift(route[i - 1], route[i]);
                    if (i < route.Count - 1) side += LaneShift(route[i], route[i + 1]);
                    if (i > 0 && i < route.Count - 1) side *= 0.5f;
                    point += side;
                }
                path.Add(point);
            }
            path.Add(target.position);
        }

        private Vector3 LaneShift(int a, int b)
        {
            var along = Flat(nodes[b] - nodes[a]);
            if (along.sqrMagnitude < 0.001f) return Vector3.zero;
            return Vector3.Cross(Vector3.up, along.normalized) * (widths[a, b] * 0.25f);
        }

        private int Nearest(Vector3 position)
        {
            int best = -1;
            float bestDistance = float.PositiveInfinity;
            for (int i = 0; i < nodes.Length; i++)
            {
                if (!nodeOpen[i]) continue;
                float d = Flat(nodes[i] - position).sqrMagnitude;
                if (d < bestDistance) { bestDistance = d; best = i; }
            }
            return best;
        }

        private List<int> Dijkstra(int start, int goal)
        {
            int n = nodes.Length;
            var distance = Enumerable.Repeat(float.PositiveInfinity, n).ToArray();
            var previous = Enumerable.Repeat(-1, n).ToArray();
            var finished = new bool[n];
            distance[start] = 0f;
            for (int round = 0; round < n; round++)
            {
                int u = -1;
                for (int i = 0; i < n; i++) if (!finished[i] && (u < 0 || distance[i] < distance[u])) u = i;
                if (u < 0 || float.IsInfinity(distance[u]) || u == goal) break;
                finished[u] = true;
                foreach (var (to, length) in links[u])
                {
                    if (!nodeOpen[to]) continue;
                    if (distance[u] + length < distance[to]) { distance[to] = distance[u] + length; previous[to] = u; }
                }
            }
            var route = new List<int>();
            for (int v = goal; v >= 0; v = previous[v]) route.Add(v);
            route.Reverse();
            return route;
        }

        private void LoadGraph()
        {
            var graph = JObject.Parse(File.ReadAllText("Docs/MapLayout/Myeongji/road_graph_expanded.json"));
            var roads = JObject.Parse(File.ReadAllText("Docs/MapLayout/Myeongji/layout_expanded.json"))["roads"].ToArray();
            nodes = graph["nodes"].Select(p => new Vector3((float)p[0], 0f, (float)p[1])).ToArray();
            links = nodes.Select(_ => new List<(int, float)>()).ToArray();
            widths = new float[nodes.Length, nodes.Length];
            foreach (var edge in graph["edges"])
            {
                int a = (int)edge[0], b = (int)edge[1];
                float length = (float)edge[2];
                links[a].Add((b, length));
                links[b].Add((a, length));
                var middle = (nodes[a] + nodes[b]) * 0.5f;
                float best = float.PositiveInfinity, width = 6f;
                foreach (var road in roads)
                {
                    var from = new Vector3((float)road["a"][0], 0f, (float)road["a"][1]);
                    var delta = new Vector3((float)road["b"][0], 0f, (float)road["b"][1]) - from;
                    float t = Mathf.Clamp01(Vector3.Dot(middle - from, delta) / Mathf.Max(0.001f, delta.sqrMagnitude));
                    float d = (middle - from - delta * t).sqrMagnitude;
                    if (d < best) { best = d; width = (float)road["width"]; }
                }
                widths[a, b] = widths[b, a] = width;
            }
        }

        private void Track()
        {
            var position = body.position;
            if (pendingCrash)
            {
                pendingCrash = false;
                bool hopping = (bool)zoneHopField.GetValue(rider);
                if (hopping) Add("other", "zoneHop");
                else
                {
                    bool car = cars.Any(c => c != null && Flat(c.transform.position - position).magnitude < 6f);
                    Add("other", car ? "carCrash" : "walkerCrash");
                    BeginFlight(car ? "car" : "walker");
                }
            }
            lastState = rider.ReactionState;
            TrackFlight();
            if (stepCount % 5 != 0) return;
            bool carrying = session.Stage == DeliveryStage.Carrying || session.Stage == DeliveryStage.FoodLost;
            foreach (var w in watches)
            {
                if (w.slot == null || !w.slot.gameObject.activeInHierarchy || !w.slot.RuntimeActive) continue;
                float d = Flat(w.slot.transform.position - position).magnitude;
                bool counts_ = w.slot.Role == HazardRole.Object || carrying;
                if (!w.seen && counts_ && d < SeenRange) { w.seen = true; Add("seen", w.slot.Type); }
                if (d > 70f || w.part == null) continue;
                if (w.phase != null)
                {
                    string phase = w.phase.GetValue(w.part).ToString();
                    if (phase != w.last)
                    {
                        bool fired = phase == "Active" || phase == "Crossing" || phase == "Chasing" || phase == "Warning" || phase == "Rolling" && w.last == "Ready" || phase == "Running";
                        if (fired) Add("triggered", w.slot.Type);
                        if (phase == "Flying") Add("hits", w.slot.Type);
                        w.last = phase;
                    }
                }
                if (w.flag != null)
                {
                    bool flag = (bool)w.flag.GetValue(w.part);
                    if (flag && !w.lastFlag)
                    {
                        if (w.part is CelebrityCrowdHazard) Add("triggered", w.slot.Type);
                        else Add("hits", w.slot.Type);
                    }
                    w.lastFlag = flag;
                }
            }
        }

        private void OnHazardHit(UnityEngine.Object source)
        {
            pendingCrash = false;
            var component = source as Component;
            var slot = component != null ? component.GetComponentInParent<HazardSlot>(true) : null;
            string type = slot != null ? slot.Type : source != null ? source.GetType().Name : "?";
            if (driving || trialDrive != null) Add("hits", type);
            BeginFlight(type);
        }

        private void OnReaction(RiderReactionState state)
        {
            if (state == RiderReactionState.Airborne && lastState == RiderReactionState.Normal) pendingCrash = true;
        }

        private void OnImpact(float strength) { if (flight != null) flight.impacts++; }
        private void OnPigeons(float seconds) { Add("hits", "pigeonflock"); Add("triggered", "pigeonflock"); }

        private void BeginFlight(string type)
        {
            if (flight != null) { flight.hits++; if (flight.firstTime < 0f) EndFlight(true); else EndFlight(true); }
            var position = body.position;
            flight = new Flight { type = type, from = position, y0 = position.y, top = position.y, impulse = rider.ContactVelocity };
        }

        private void TrackFlight()
        {
            if (flight == null) return;
            var position = body.position;
            flight.time += Step;
            flight.top = Mathf.Max(flight.top, position.y);
            if (position.y < flight.lowest) { flight.lowest = position.y; flight.lowestAt = position; }
            if (Mathf.Round(flight.time / Step) % 15 == 0 && flight.trace.Count < 30)
                flight.trace.Add("");
            int bounces = (int)floorBouncesField.GetValue(rider);
            flight.bounces = Mathf.Max(flight.bounces, bounces);
            if (flight.firstTime < 0f && flight.time > 0.06f && (bounces > 0 || rider.ReactionState == RiderReactionState.Rolling || rider.ReactionState == RiderReactionState.Normal))
            {
                flight.firstTime = flight.time;
                flight.firstState = rider.ReactionState.ToString() + " vy" + body.linearVelocity.y.ToString("0.0");
                flight.firstDistance = Flat(position - flight.from).magnitude;
            }
            if (rider.ReactionState == RiderReactionState.Normal && flight.time > 0.1f) EndFlight(false);
        }

        private void EndFlight(bool interrupted)
        {
            var position = body.position;
            flights.Add(new JObject
            {
                ["type"] = flight.type,
                ["vh"] = Round(Flat(flight.impulse).magnitude),
                ["vz"] = Round(flight.impulse.y),
                ["air"] = Round(flight.firstTime),
                ["dist1"] = Round(flight.firstDistance),
                ["top"] = Round(flight.top - flight.y0),
                ["total"] = Round(Flat(position - flight.from).magnitude),
                ["dur"] = Round(flight.time),
                ["bounces"] = flight.bounces,
                ["impacts"] = flight.impacts,
                ["interrupted"] = interrupted,
                ["belowFloor"] = flight.lowest < flight.y0 - 1.5f,
                ["lowestX"] = Round(flight.lowestAt.x),
                ["lowestY"] = Round(flight.lowestAt.y),
                ["lowestZ"] = Round(flight.lowestAt.z),
                ["firstState"] = flight.firstState,
            });
            flight = null;
        }

        private static double Round(float value) => Math.Round(value, 2);
        private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        private void Add(string group, string key)
        {
            var bag = (JObject)counts[group];
            bag[key] = (int)(bag[key] ?? 0) + 1;
        }

        private void Write(float realSeconds)
        {
            rider.HazardHit -= OnHazardHit;
            rider.ReactionChanged -= OnReaction;
            rider.ContactImpact -= OnImpact;
            session.PigeonFlockObscured -= OnPigeons;
            var results = session.Results;
            var line = new JObject
            {
                ["plan"] = config.plan,
                ["day"] = config.day,
                ["seed"] = config.seed,
                ["lane"] = config.lane,
                ["tweak"] = MeasureRunner.Arg("-measureTweak", ""),
                ["gameSeconds"] = Round(elapsed),
                ["realSeconds"] = Round(realSeconds),
                ["settled"] = session.IsSettled,
                ["calls"] = calls,
                ["completed"] = session.CompletedCount,
                ["late"] = session.LateCount,
                ["cash"] = session.Cash,
                ["carriedMeters"] = Round(carried),
                ["movedMeters"] = Round(moved),
                ["deliverySeconds"] = Round((float)results.Sum(r => r.DurationSeconds)),
                ["routeMeters"] = Round(routeMeters),
                ["share"] = Round(session.ZoneMap != null ? new HazardZoneFilter(session.ZoneMap, session.IsZoneOpen).OpenRoadShare(FindRoads()) : 1f),
                ["counts"] = counts,
                ["flights"] = new JArray(flights),
            };
            File.AppendAllText(outPath, line.ToString(Newtonsoft.Json.Formatting.None) + "\n");
            Debug.Log($"MEASURE run {config.plan} day{config.day} {config.lane} seed{config.seed}: {calls} calls, {carried:0} m carried, {elapsed:0} s game in {realSeconds:0} s real");
        }

        private IReadOnlyList<RoadSurface> FindRoads()
            => ((RoadNetwork)typeof(HazardPlanner).GetField("ground", Any).GetValue(planner)).Roads;

        // ---- Flight trials: hit real hazards on open road, one kind and site at a time, no traffic.
        private IEnumerator FlightPlan(SceneController controller)
        {
            int trials = int.Parse(MeasureRunner.Arg("-measureTrials", "10"));
            foreach (bool buildings in new[] { true, false })
            {
                config = new Config { day = 4, seed = buildings ? 1 : 2, lane = buildings ? "buildings" : "noBuildings", plan = "flight", seconds = 1e9f };
                yield return Load(controller, 4, config.seed);
                Prepare();
                typeof(DeliverySession).GetField("dayDuration", Any).SetValue(session, 1e9d);
                foreach (var name in new[] { "Traffic", "Pedestrians" })
                    foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
                        if (root.name == name) root.SetActive(false);
                if (!buildings) RemoveBuildingColliders();
                if (MeasureRunner.Arg("-measureWhatIf", "") == "htmlArc") ApplyHtmlArc();
                planner.enabled=false;
                foreach(var tireSlot in planner.Slots.Where(s=>s.Type=="tire"))typeof(RollingTireHazard).GetField("largeChance",Any).SetValue(tireSlot.GetComponent<RollingTireHazard>(),1f);
                var slots = planner.Slots.Where(s => s != null).ToList();
                foreach (string type in new[] { "manhole", "hydrant", "duck", "muscleman", "tire", "melon" })
                {
                    var sites = slots.Where(s => s.Type == type).ToList();
                    int made = 0, tried = 0;
                    foreach (var site in sites)
                    {
                        if (made >= trials) break;
                        tried++;
                        foreach (var other in slots) other.SetRuntimeActive(other == site,other == site);
                        foreach (var w in watches) w.seen = false;
                        yield return Trial(type, site);
                        if (lastTrialHit) made++;
                        site.SetRuntimeActive(false);
                    }
                    Debug.Log($"MEASURE flight {config.lane} {type}: {made} hits in {tried} sites");
                }
                elapsed = 1f;
                Write(0f);
            }
        }

        private bool lastTrialHit;

        // What-if on a local, uncommitted patch of RiderMotor.Reaction (accident gravity scale and landing that waits for the
        // bounce): HTML's own upward speeds and bounce threshold on the live objects. Nothing is saved.
        private void ApplyHtmlArc()
        {
            var scale = typeof(RiderMotor).GetField("airGravityScale", Any) ?? throw new MissingFieldException("airGravityScale needs the local patch");
            scale.SetValue(rider, 22f / 9.81f);
            typeof(RiderMotor).GetField("minimumBounceSpeed", Any).SetValue(rider, 5f);
            typeof(RiderMotor).GetField("maximumFloorBounces", Any).SetValue(rider, 4);
            foreach (var slot in planner.Slots)
            {
                if (slot == null) continue;
                void Up(Component part, string field, float value) => part.GetType().GetField(field, Any).SetValue(part, value);
                if (slot.Type == "manhole") Up(slot.GetComponent<GroundContactHazard>(), "upwardImpulse", 24f);
                if (slot.Type == "hydrant") Up(slot.GetComponent<GroundContactHazard>(), "upwardImpulse", 30f);
                if (slot.Type == "duck") Up(slot.GetComponent<CrossingFlockHazard>(), "bounceLift", 15.6f);
                if (slot.Type == "muscleman") Up(slot.GetComponent<MusclemanHazard>(), "shoveLift", 6.5f);
                if (slot.Type == "tire") Up(slot.GetComponent<RollingTireHazard>(), "upwardImpulse", 13f);
                if (slot.Type == "melon") Up(slot.GetComponent<RollingMelonHazard>(), "hitLift", 6.75f);
            }
        }

        private void RemoveBuildingColliders()
        {
            var ground = (Collider[])typeof(RoadNetwork).GetField("ground", Any).GetValue(typeof(HazardPlanner).GetField("ground", Any).GetValue(planner));
            foreach (var collider in FindObjectsByType<Collider>(FindObjectsSortMode.None))
                if (collider.attachedRigidbody == null && !ground.Contains(collider) && !collider.isTrigger) collider.enabled = false;
            Physics.SyncTransforms();
        }

        private IEnumerator Trial(string type, HazardSlot site)
        {
            var transformSite = site.transform;
            var forward = Flat(transformSite.forward).normalized;
            Vector3 start, aim;
            switch (type)
            {
                case "hydrant": { var along = Vector3.Cross(Vector3.up, forward); start = transformSite.position - along * 12f; aim = transformSite.position; break; }
                case "duck": start = transformSite.position - forward * 14f; aim = transformSite.position; break;
                case "muscleman": start = transformSite.position; aim = transformSite.position; break;
                case "tire": start = transformSite.position + forward * 20f; aim = transformSite.position; break;
                case "melon": start = transformSite.position + forward * 18f; aim = transformSite.position; break;
                default: start = transformSite.position - forward * 12f; aim = transformSite.position; break;
            }
            var network = (RoadNetwork)typeof(HazardPlanner).GetField("ground", Any).GetValue(planner);
            start = network.OnGround(start, 1.05f);
            typeof(RiderMotor).GetMethod("ResetReaction", Any).Invoke(rider, null);
            typeof(RiderMotor).GetField("lastHazardHitAt", Any).SetValue(rider, float.NegativeInfinity);
            typeof(RiderMotor).GetField("lastHazard", Any).SetValue(rider, null);
            typeof(RiderMotor).GetField("nextTrafficHitAt", Any).SetValue(rider, 0f);
            typeof(RiderMotor).GetField("nextZoneCheckAt", Any).SetValue(rider, 1e9f);
            body.position = start;
            rider.transform.position = start;
            body.linearVelocity = body.angularVelocity = Vector3.zero;
            Physics.SyncTransforms();
            site.Selected = true; site.SetRuntimeActive(true,true); site.StrikeAllowed = true;
            flight = null;
            // Exercise the production component reset above; do not force private phases back to Ready.
            int flightsBefore = flights.Count;
            bool hit = false;
            float clock = 0f, afterHit = 0f;
            var birds = site.GetComponent<CrossingFlockHazard>();
            var runnerField = typeof(MusclemanHazard).GetField("runner", Any);
            lastPosition = body.position;
            trialDrive = () =>
            {
                site.StrikeAllowed = true;
                clock += Step;
                Track();
                if (flight != null || rider.IsRecovering) { hit = true; afterHit += Step; Stop(); return; }
                var position = body.position;
                if (type == "muscleman")
                {
                    var runner = (Transform)runnerField.GetValue(site.GetComponent<MusclemanHazard>());
                    if (runner != null && runner.gameObject.activeSelf) Head(runner.forward); else Stop();
                }
                else if (type == "duck")
                {
                    var flock = (Transform[])typeof(CrossingFlockHazard).GetField("birds", Any).GetValue(birds);
                    var near = flock.Where(b => b != null && b.gameObject.activeSelf).OrderBy(b => Flat(b.position - position).sqrMagnitude).FirstOrDefault();
                    Head((near != null ? near.position : aim) - position);
                }
                else if (type == "tire" || type == "melon" ) { if (Flat(aim - position).magnitude > 3f) Head(aim - position); else Stop(); }
                else Head(aim - position);
            };
            while (clock < 12f && !(hit && flight == null && rider.ReactionState == RiderReactionState.Normal) && afterHit < 12f) yield return null;
            trialDrive = null;
            if (flight != null) EndFlight(true);
            lastTrialHit = hit && flights.Count > flightsBefore;
            typeof(RiderMotor).GetMethod("ResetReaction", Any).Invoke(rider, null);
        }
    }
}
