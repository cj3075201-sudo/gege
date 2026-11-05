using System;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

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

        internal static HazardCandidate[] Places() => AssetDatabase.LoadAssetAtPath<HazardCandidates>(
            "Assets/_Game/Settings/MyeongjiHazardCandidates.asset").Candidates.ToArray();

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
            var table = new StringBuilder("|후보|차도 폭|시작 X/Z (경계)|끝 X/Z (경계)|횡단 폭|마릿수|전체 막힘(최소)|완전히 열림|유효|\n|---|---:|---|---|---:|---:|---:|---:|---|\n");
            int valid = 0, buried = 0;
            foreach (var place in Places().Where(c => c.type == "duck"))
            {
                var hazard = Make(network, place);
                var a = hazard.transform.TransformPoint(Get<Vector3>(hazard, "localStart"));
                var b = hazard.transform.TransformPoint(Get<Vector3>(hazard, "localEnd"));
                int count = Get<int>(hazard, "activeBirds");
                float gap = Get<float>(hazard, "effectiveSpacing"), speed = Get<float>(hazard, "walkSpeed");
                bool usable = Get<bool>(hazard, "placementValid");
                if (usable)
                {
                    valid++;
                    Physics.SyncTransforms();
                    foreach (var bird in Get<Transform[]>(hazard, "birds").Where(t => t.gameObject.activeSelf))
                    {
                        foreach (var visual in bird.GetComponentsInChildren<Renderer>())
                        {
                            var bounds = visual.localBounds;
                            var overlaps = Physics.OverlapBox(visual.transform.TransformPoint(bounds.center), Vector3.Scale(bounds.extents, visual.transform.lossyScale) * .98f,
                                visual.transform.rotation, ~(1 << 8), QueryTriggerInteraction.Ignore)
                                .Where(c => c.attachedRigidbody == null && c.bounds.size.y > 1.25f && !c.transform.IsChildOf(hazard.transform)).ToArray();
                            if (overlaps.Length > 0)
                            { buried++; Debug.LogError($"DUCK_BURIED spot={place.spot} bird={bird.position} part={visual.name} blockers={string.Join(";", overlaps.Select(c => c.name + " " + c.bounds))}"); }
                        }
                    }
                }
                float width = Vector3.Distance(a, b);
                Check(width > place.halfWidth * 2f + 1f, $"spot {place.spot}: crossing goes beyond carriageway and kerbs ({width:0.00}m)");
                table.AppendLine($"|{place.spot}/{place.side}|{place.halfWidth * 2f:0.0}|{a.x:0.00}/{a.z:0.00} ({EdgeLabel(a, (a - b).normalized)})|{b.x:0.00}/{b.z:0.00} ({EdgeLabel(b, (b - a).normalized)})|{width:0.00}|{count}|{((count - 1) * gap - width) / speed:0.00}초|{count * gap / speed:0.00}초|{usable}|");
                Debug.Log($"DUCK_MAP spot={place.spot} side={place.side} width={width:0.00} birds={count} start={a:F2} end={b:F2} usable={usable}");
                UnityEngine.Object.DestroyImmediate(hazard.transform.parent.gameObject);
            }
            Directory.CreateDirectory(Output);
            File.WriteAllText(Output + "codex_duck_corridors.md", table.ToString());
            Check(valid > 0, "real map has supported corridors");
            Check(buried == 0, "real map buried ducks = " + buried);
            Debug.Log($"DUCK_MAP_PASS valid={valid} buried={buried}");
        }

        private static string EdgeLabel(Vector3 point, Vector3 direction)
        {
            var wall = Physics.RaycastAll(point + Vector3.up * .25f, direction, 2f, ~(1 << 8), QueryTriggerInteraction.Ignore)
                .Where(h => h.rigidbody == null && h.normal.y < .7f).OrderBy(h => h.distance).FirstOrDefault();
            return wall.collider != null ? wall.collider.name : "바닥 끝/높이 경계";
        }
    }
}
