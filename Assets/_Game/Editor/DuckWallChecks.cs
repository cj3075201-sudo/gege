using System;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;

namespace DeliveryRider.Editor
{
    public static class DuckWallChecks
    {
        internal const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        internal const string Prefab = "Assets/_Game/Prefabs/Hazards/DuckFlock.prefab";
        internal static void Set(object target, string field, object value) => target.GetType().GetField(field, Any).SetValue(target, value);
        internal static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, Any).GetValue(target);
        internal static object Call(object target, string method) => target.GetType().GetMethod(method, Any).Invoke(target, null);
        internal static void Check(bool pass, string description)
        {
            if (!pass) throw new InvalidOperationException(description);
            Debug.Log("DUCK_CHECK " + description);
        }

        public static void RunWidthLimits()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.transform.position = Vector3.down * .5f;
            floor.transform.localScale = new Vector3(120f, 1f, 120f);
            var host = new GameObject("InactiveWidthFixture"); host.SetActive(false);
            var network = host.AddComponent<RoadNetwork>(); network.Configure(new[] { floor.GetComponent<Collider>() });
            Physics.SyncTransforms();
            var spot = new HazardCandidate { type = "duck", halfWidth = 4f, side = 1 };
            var hazard = Make(network, spot);
            Vector3 start = Get<Vector3>(hazard, "localStart"), end = Get<Vector3>(hazard, "localEnd");
            Debug.Log($"DUCK_WIDTH start={start:F3} end={end:F3}");
            Check(Mathf.Abs(start.x - 12f) < .02f && Mathf.Abs(end.x + 12f) < .02f,
                "open space stops exactly at road centre +/-12m");
            Check(Get<bool>(hazard, "placementValid"), "open ends allow lateral detours without suppressing the flock");
            Check(Get<int>(hazard, "activeBirds") <= 22, "24m crossing never needs more than 22 default ducks");
            Check(Get<int>(hazard, "activeBirds") * Get<float>(hazard, "effectiveSpacing") / Get<float>(hazard, "walkSpeed") <= 25f,
                "24m crossing completely clears within 25 seconds");
            Check(DevValue.Discover(hazard).Exists(v => v.PropertyPath == "maximumCrossingWidth"), "F1 discovers maximumCrossingWidth");
            Check(DevLabels.Find(nameof(CrossingFlockHazard), "maximumCrossingWidth") != null, "F1 Korean maximum crossing width");

            AddRoad(network, "Source", new Vector3(0, 0, -40), new Vector3(0, 0, 40), 8f);
            var parallel = AddRoad(network, "Neighbour", new Vector3(8, 0, -30), new Vector3(8, 0, 30), 4f);
            hazard.Place(null, network, spot);
            start = Get<Vector3>(hazard, "localStart"); end = Get<Vector3>(hazard, "localEnd");
            Check(start.x >= 4f && start.x <= 5.51f && end.x <= -11.99f,
                "crossing stops before a neighbouring street while covering its own carriageway");
            UnityEngine.Object.DestroyImmediate(parallel.gameObject);
            AddRoad(network, "JunctionArm", new Vector3(4, 0, 0), new Vector3(45, 0, 0), 4f);
            hazard.Place(null, network, spot);
            Check(Get<Vector3>(hazard, "localStart").x <= 4.51f && Get<Vector3>(hazard, "localEnd").x <= -11.99f,
                "flock cannot follow the length of a junction arm");
            Set(hazard, "maximumCrossingWidth", 16f);
            hazard.Place(null, network, spot);
            Check(Get<Vector3>(hazard, "localEnd").x >= -8.01f, "F1 width changes recalculate endpoints and count");
            Debug.Log("DUCK_WIDTH_PASS");
        }

        private static RoadSurface AddRoad(RoadNetwork network, string name, Vector3 start, Vector3 end, float width)
        {
            var root = new GameObject(name); root.transform.SetParent(network.transform, false);
            var container = root.AddComponent<SplineContainer>();
            container.Spline = new Spline();
            container.Spline.Add(new BezierKnot((float3)start), TangentMode.Linear);
            container.Spline.Add(new BezierKnot((float3)end), TangentMode.Linear);
            var road = root.AddComponent<RoadSurface>(); road.Configure(RoadKind.Main, width, 3f);
            return road;
        }

        public static void Run()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.transform.position = Vector3.down * .64f;
            floor.transform.localScale = new Vector3(30f, 1f, 100f);
            foreach (float side in new[] { -1f, 1f })
            {
                var sidewalk = GameObject.CreatePrimitive(PrimitiveType.Cube);
                sidewalk.name = "RaisedSidewalk";
                sidewalk.transform.position = new Vector3(side * 10f, -.5f, 0f);
                sidewalk.transform.localScale = new Vector3(10f, 1f, 100f);
                var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
                wall.name = "Building";
                wall.transform.position = new Vector3(side * 15.5f, 4f, 0f);
                wall.transform.localScale = new Vector3(1f, 8f, 100f);
            }
            var host = new GameObject("Fixture"); host.SetActive(false);
            var network = host.AddComponent<RoadNetwork>();
            network.Configure(new[] { floor.GetComponent<Collider>() });
            var hazard = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Prefab)).GetComponent<CrossingFlockHazard>();
            Set(hazard, "maximumCrossingWidth", 32f); // Keep the original wall/kerb regression inside the explicit width budget.
            Physics.SyncTransforms();
            hazard.Place(null, network, new HazardCandidate { halfWidth = 5f, side = 1 });
            Vector3 start = Get<Vector3>(hazard, "localStart"), end = Get<Vector3>(hazard, "localEnd");
            Check(start.x > 14f && start.x < 15f && end.x < -14f && end.x > -15f, "crossing reaches both building walls beyond sidewalks");
            float span = Vector3.Distance(start, end);
            float row = (Get<Transform[]>(hazard, "birds").Length - 1) * Get<float>(hazard, "spacing");
            Check(row >= span + 6f * 1.5f, "runtime flock covers corridor plus six seconds of walking");
            Physics.SyncTransforms();
            Check(!Get<Transform[]>(hazard, "birds").Where(t => t.gameObject.activeSelf)
                .SelectMany(t => t.GetComponentsInChildren<Renderer>()).Any(r =>
                    Physics.OverlapBox(r.bounds.center, r.bounds.extents * .98f, Quaternion.identity, ~(1 << 8), QueryTriggerInteraction.Ignore)
                    .Any(c => c.name == "Building")), "whole duck visuals including beak remain outside walls");
            Check(AssetDatabase.LoadAssetAtPath<GameObject>(Prefab).GetComponentsInChildren<Transform>(true).Count(t => t.name.StartsWith("Duck_")) == 5, "source prefab keeps five bird templates");
            var barrier = GameObject.CreatePrimitive(PrimitiveType.Cube);
            barrier.layer = 8;
            barrier.transform.position = new Vector3(3f, 2f, 0f);
            barrier.transform.localScale = new Vector3(1f, 4f, 10f);
            Physics.SyncTransforms();
            hazard.Place(null, network, new HazardCandidate { halfWidth = 5f, side = 1 });
            Check(Vector3.Distance(start, Get<Vector3>(hazard, "localStart")) < .01f, "layer 8 never shortens crossing");
            var slot = hazard.gameObject.AddComponent<HazardSlot>();
            Set(hazard, "slot", slot);
            foreach (float scale in new[] { .5f, 1f, 2f, 4f })
            {
                slot.SetTriggerScale(scale);
                Call(hazard, "EnsureFlock");
                Check(Get<float>(hazard, "effectiveSpacing") <= 2f * .85f * scale, "no gap at trigger scale " + scale);
            }
            foreach (string name in new[] { "blockingSeconds", "jumpReactionRange", "highFlightPassHeight", "repeatHitDelay" })
            {
                Check(DevValue.Discover(hazard).Exists(v => v.PropertyPath == name), "F1 discovers " + name);
                Check(DevLabels.Find(nameof(CrossingFlockHazard), name) != null, "F1 Korean label " + name);
            }
            CheckContactHeight(hazard, slot);
            Debug.Log("DUCK_CHECK_PASS");
        }

        private static void CheckContactHeight(CrossingFlockHazard hazard, HazardSlot slot)
        {
            var player = new GameObject("ContactRider");
            var body = player.AddComponent<Rigidbody>(); body.useGravity = false;
            var shape = player.AddComponent<CapsuleCollider>(); shape.center = Vector3.up * .9f;
            shape.height = 1.8f; shape.radius = .5f;
            var rider = player.AddComponent<RiderMotor>();
            var owner = new GameObject("ContactSession").AddComponent<DeliverySession>();
            Set(owner, "player", player.transform); Set(rider, "session", owner); Call(rider, "Awake");
            Set(hazard, "rider", rider); Set(hazard, "session", owner);
            foreach (var test in new[] { (.5f, .91f, true), (1f, .91f, true), (1f, 3f, false), (2f, 3f, true) })
            {
                Call(rider, "ResetReaction");
                Set(rider, "lastHazardHitAt", float.NegativeInfinity); Set(rider, "lastHazard", null);
                Set(rider, "nextTrafficHitAt", 0f); Set(hazard, "nextHitAt", 0f);
                player.transform.position = Vector3.up * test.Item2; body.position = player.transform.position;
                body.linearVelocity = Vector3.forward * 6f;
                slot.SetTriggerScale(test.Item1);
                Set(hazard, "head", Vector3.Distance(Get<Vector3>(hazard, "localStart"), Get<Vector3>(hazard, "localEnd")));
                Call(hazard, "Advance");
                Check(rider.IsRecovering == test.Item3, $"contact height scale={test.Item1}, feet={test.Item2}, expectedHit={test.Item3}");
            }
        }

        internal const string MapScene = "Assets/_Game/Scenes/FourDistrictPrototype_Myeongji.unity";
        internal const string Output = "C:/UnityWork/ge/공유/결과/";

        internal static RoadNetwork OpenMap()
        {
            var scene = EditorSceneManager.OpenScene(MapScene, OpenSceneMode.Single);
            var roots = scene.GetRootGameObjects();
            foreach (var part in roots.SelectMany(r => r.GetComponentsInChildren<MonoBehaviour>(true))) part.enabled = false;
            foreach (var body in roots.SelectMany(r => r.GetComponentsInChildren<Rigidbody>(true))) body.gameObject.SetActive(false);
            Physics.SyncTransforms();
            return roots.SelectMany(r => r.GetComponentsInChildren<HazardPlanner>(true))
                .Select(p => new SerializedObject(p).FindProperty("ground").objectReferenceValue as RoadNetwork).First(n => n != null);
        }

        internal const string CandidatesPath = "Assets/_Game/Settings/MyeongjiHazardCandidatesExpanded.asset";
        internal static HazardCandidate[] Places() => AssetDatabase.LoadAssetAtPath<HazardCandidates>(CandidatesPath).Candidates.ToArray();

        internal static CrossingFlockHazard Make(RoadNetwork network, HazardCandidate candidate, DeliverySession session = null)
        {
            var staging = new GameObject("InactiveDuckStaging"); staging.SetActive(false);
            var obj = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(Prefab), candidate.position, candidate.rotation, staging.transform);
            var hazard = obj.GetComponent<CrossingFlockHazard>();
            hazard.Place(session, network, candidate);
            return hazard;
        }

        public static void RunMapAudit()
        {
            var network = OpenMap();
            var rows = new System.Collections.Generic.List<AuditRow>();
            var candidates = Places();
            int expected = candidates.Count(c => c.type == "duck");
            var issues = new System.Collections.Generic.List<string>();
            var table = new StringBuilder("|원본 인덱스 / 후보|차도 폭|시작 X/Z|끝 X/Z|횡단 폭|마릿수|전체 막힘(최소)|완전히 열림|유효/차도 덮음/겹침|\n|---|---:|---|---|---:|---:|---:|---:|---|\n");
            for (int index = 0; index < candidates.Length; index++)
            {
                var place = candidates[index];
                if (place.type != "duck") continue;
                var hazard = Make(network, place);
                var a = hazard.transform.TransformPoint(Get<Vector3>(hazard, "localStart"));
                var b = hazard.transform.TransformPoint(Get<Vector3>(hazard, "localEnd"));
                int count = Get<int>(hazard, "activeBirds");
                float gap = Get<float>(hazard, "effectiveSpacing"), speed = Get<float>(hazard, "walkSpeed");
                var across = Vector3.ProjectOnPlane(hazard.transform.right, Vector3.up).normalized;
                float first = Vector3.Dot(a - place.position, across), last = Vector3.Dot(b - place.position, across);
                float radius = Get<float>(hazard, "hitRadius");
                float span = Get<float>(hazard, "configuredSpan");
                var row = new AuditRow
                {
                    index = index, spot = place.spot, side = place.side, position = place.position, start = a, end = b,
                    carriagewayWidth = place.halfWidth * 2f, width = Vector3.ProjectOnPlane(a - b, Vector3.up).magnitude,
                    birds = count, totalWait = count * gap / speed, fullBlock = ((count - 1) * gap - span) / speed,
                    valid = Get<bool>(hazard, "placementValid"),
                    coversCarriageway = Mathf.Min(first, last) - radius <= -place.halfWidth + .01f
                        && Mathf.Max(first, last) + radius >= place.halfWidth - .01f && gap <= radius * 2f
                };
                if (row.valid)
                {
                    // Every .2m phase of the moving queue. Remaining motion repeats these positions as birds follow.
                    int phases = Mathf.Max(1, Mathf.CeilToInt(gap / .2f));
                    for (int phase = 0; phase < phases; phase++)
                    {
                        Set(hazard, "head", span + gap * phase / phases);
                        typeof(CrossingFlockHazard).GetMethod("PositionBirds", Any).Invoke(hazard, new object[] { false });
                        foreach (var bird in Get<Transform[]>(hazard, "birds").Where(t => t.gameObject.activeSelf))
                        {
                            foreach (var visual in bird.GetComponentsInChildren<Renderer>(true))
                            {
                                row.testedParts++;
                                var bounds = visual.localBounds;
                                float jump = Get<float>(hazard, "duckJumpHeight") * .5f;
                                var extent = Vector3.Scale(bounds.extents, visual.transform.lossyScale) * .98f;
                                extent.y += jump; // Full swept jump envelope, including all intermediate jump heights.
                                var overlaps = Physics.OverlapBox(visual.transform.TransformPoint(bounds.center) + Vector3.up * jump, extent,
                                    visual.transform.rotation, ~(1 << 8), QueryTriggerInteraction.Ignore)
                                    .Where(c => c.attachedRigidbody == null && !c.transform.IsChildOf(hazard.transform)).ToArray();
                                if (overlaps.Length == 0) continue;
                                row.buried++;
                                if (issues.Count < 40) issues.Add($"index={index} spot={place.spot}/{place.side} bird={bird.position} part={visual.name} blockers={string.Join(";", overlaps.Select(c => c.name))}");
                            }
                        }
                    }
                }
                rows.Add(row);
                table.AppendLine($"|{index} / {place.spot}/{place.side}|{row.carriagewayWidth:0.0}|{a.x:0.00}/{a.z:0.00}|{b.x:0.00}/{b.z:0.00}|{row.width:0.00}|{count}|{row.fullBlock:0.00}초|{row.totalWait:0.00}초|{row.valid}/{row.coversCarriageway}/{row.buried}|");
                if (!row.valid || !row.coversCarriageway || row.buried > 0) Debug.LogWarning($"DUCK_MAP_ISSUE index={index} spot={place.spot}/{place.side} valid={row.valid} coverage={row.coversCarriageway} buried={row.buried}");
                if (rows.Count % 64 == 0) Debug.Log($"DUCK_MAP_PROGRESS {rows.Count}/{expected}");
                UnityEngine.Object.DestroyImmediate(hazard.transform.parent.gameObject);
            }
            Directory.CreateDirectory(Output);
            File.WriteAllText(Output + "codex_duck_corridors.md", table.ToString());
            var summary = new AuditSummary
            {
                candidateAsset = CandidatesPath, scene = MapScene, candidates = rows.Count, roads = network.Roads.Count,
                valid = rows.Count(r => r.valid), covered = rows.Count(r => r.coversCarriageway), buried = rows.Sum(r => r.buried),
                testedParts = rows.Sum(r => r.testedParts), width = Distribution.Of(rows.Select(r => r.width)),
                birds = Distribution.Of(rows.Select(r => (float)r.birds)), wait = Distribution.Of(rows.Select(r => r.totalWait)),
                rows = rows.ToArray(), issues = issues.ToArray()
            };
            File.WriteAllText(Output + "codex_duck_expanded_audit.json", JsonUtility.ToJson(summary, true));
            Debug.Log($"DUCK_MAP_SUMMARY candidates={summary.candidates} valid={summary.valid} covered={summary.covered} buried={summary.buried} parts={summary.testedParts} roads={summary.roads} width={summary.width} birds={summary.birds} wait={summary.wait}");
            foreach (string issue in issues) Debug.LogWarning("DUCK_BURIED " + issue);
            Check(rows.Count == expected && expected >= 700, "audit includes every expanded duck candidate");
            Check(summary.valid == expected, "every expanded placement is supported");
            Check(summary.covered == expected, "every expanded carriageway is covered");
            Check(summary.buried == 0 && summary.testedParts > 0, "expanded standing/jumping full-body overlap = 0");
            Check(summary.width.max <= 24.01f, "every expanded horizontal crossing <=24m");
            Check(summary.wait.max <= 30f, "every expanded crossing completely clears within 30 seconds");
            Debug.Log($"DUCK_MAP_PASS expanded={expected} valid={summary.valid} covered={summary.covered} buried={summary.buried}");
        }

        [Serializable]
        private sealed class AuditRow
        {
            public int index, spot, side, birds, buried, testedParts;
            public float carriagewayWidth, width, totalWait, fullBlock;
            public bool valid, coversCarriageway;
            public Vector3 position, start, end;
        }

        [Serializable]
        private sealed class AuditSummary
        {
            public string candidateAsset, scene;
            public int candidates, roads, valid, covered, buried, testedParts;
            public Distribution width, birds, wait;
            public AuditRow[] rows;
            public string[] issues;
        }

        [Serializable]
        private sealed class Distribution
        {
            public float min, median, max;
            public static Distribution Of(System.Collections.Generic.IEnumerable<float> input)
            {
                var sorted = input.OrderBy(x => x).ToArray();
                return new Distribution { min = sorted.First(), max = sorted.Last(), median = (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) * .5f };
            }
            public override string ToString() => $"{min:0.00}/{median:0.00}/{max:0.00}";
        }
    }
}
