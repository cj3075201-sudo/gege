using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DeliveryRider.Editor
{
    public static class CelebrityCrowdInstaller
    {
        public const string PrefabPath = "Assets/_Game/Prefabs/Hazards/CelebrityCrowd.prefab";

        [MenuItem("Delivery Rider/Apply Celebrity Crowd")]
        public static void Apply() => Debug.Log(ApplyToScene(SceneManager.GetActiveScene()));

        public static string ApplyToScene(Scene scene)
        {
            if (EditorApplication.isPlaying || EditorApplication.isCompiling) throw new InvalidOperationException("Use edit mode after compilation.");
            GameScenes.RequireActive();
            var components = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Component>(true)).ToArray();
            var planner = components.OfType<HazardPlanner>().Single();
            var network = components.OfType<PedestrianNetwork>().Single();
            var session = components.OfType<DeliverySession>().Single();
            var data = new SerializedObject(planner);
            var settings = (HazardSpawnSettings)data.FindProperty("settings").objectReferenceValue;
            var candidates = (HazardCandidates)data.FindProperty("candidates").objectReferenceValue;
            var roads = (RoadNetwork)data.FindProperty("ground").objectReferenceValue;
            var places = candidates.Candidates.Where(c => c.type != "celebrity").ToList();
            int prefix = places.Count == 0 ? 0 : places.Max(c => c.spot) + 1;
            var spots = candidates.Spots.Take(prefix).ToList();
            int count = AppendCandidates(spots, places, session, network, roads);
            if (count == 0) throw new InvalidOperationException("No clear gathering sites for six fans. Existing assets were preserved.");
            candidates.Configure(spots.ToArray(), places.ToArray());
            EditorUtility.SetDirty(candidates);
            var kinds = settings.Kinds.ToList();
            var kind = kinds.FirstOrDefault(k => k.type == "celebrity");
            if (kind == null)
            {
                kind = new HazardSpawnSettings.Kind { type = "celebrity", role = HazardRole.Object, unlockDay = 1, countPerDay = new[] { 1 } };
                // Reserve this one gathering before the more numerous object hazards fill the map.
                kinds.Insert(0, kind);
            }
            kind.prefab = CreatePrefab(network);
            settings.Configure(kinds.ToArray());
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
            return $"Celebrity crowd: {count} candidates in {candidates.name}; daily count {kind.Count(1)}; 6 waiting + 26 incoming, radius 10/15 m, speed x3.75, mass x3, resistance x2. Play Mode not started.";
        }

        public static int AppendCandidates(List<HazardCandidates.Spot> spots, List<HazardCandidate> places,
            DeliverySession session, PedestrianNetwork network, RoadNetwork ground)
        {
            Physics.SyncTransforms();
            int count = 0;
            var keepOut = new List<Vector3> { session.Player.position };
            var sessionData = new SerializedObject(session);
            foreach (var field in new[] { "shopPoints", "doorPoints" })
            {
                var array = sessionData.FindProperty(field);
                for (int i = 0; i < array.arraySize; i++)
                    if (array.GetArrayElementAtIndex(i).objectReferenceValue is Transform t) keepOut.Add(t.position);
            }
            for (int p = 0; p < network.PathCount; p++)
            {
                var lane = network.Path(p);
                if (lane.connector || p > lane.reverse) continue;
                for (float along = 3f; along < lane.length - 3f; along += 6f)
                {
                    network.Sample(p, along, out var center, out var forward);
                    var rotation = Quaternion.LookRotation(forward);
                    if ((session.Player.position - center).sqrMagnitude < 100f
                        || keepOut.Skip(1).Any(k => Vector3.ProjectOnPlane(k - center, Vector3.up).sqrMagnitude < 36f)
                        || !CelebrityCrowdHazard.Clear(center)) continue;
                    bool clear = true;
                    for (int i = 0; i < 6; i++)
                    {
                        var feet = ground.OnGround(CelebrityCrowdHazard.WaitingSpot(center, rotation, i), 0f);
                        if (Mathf.Abs(feet.y - center.y) > 0.5f || !CelebrityCrowdHazard.Clear(feet)) { clear = false; break; }
                    }
                    if (!clear) continue;
                    // Match runtime radial spawning, including a clear straight approach past low kerbs.
                    var waveSpots = new List<Vector3>();
                    for (float radius = 4f; radius <= 15f && waveSpots.Count < 40; radius += 2f)
                    {
                        for (int direction = 0; direction < 32; direction++)
                        {
                            float angle = direction * Mathf.PI / 16f;
                            var feet = ground.OnGround(center + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius, 0f);
                            if (Mathf.Abs(feet.y - center.y) > 0.5f || !CelebrityCrowdHazard.Clear(feet)
                                || !CelebrityCrowdHazard.HasDirectApproach(feet, center)
                                || waveSpots.Any(v => (v - feet).sqrMagnitude < 0.85f * 0.85f)) continue;
                            waveSpots.Add(feet);
                        }
                    }
                    if (waveSpots.Count < 32) continue;
                    places.Add(new HazardCandidate { type = "celebrity", spot = spots.Count, position = center,
                        rotation = rotation, halfWidth = lane.halfWidth });
                    spots.Add(new HazardCandidates.Spot { position = center });
                    count++;
                }
            }
            return count;
        }

        public static GameObject CreatePrefab(PedestrianNetwork network)
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            if (existing != null) return existing;
            var source = new SerializedObject(network.Prefab).FindProperty("coat").objectReferenceValue as Renderer;
            var yellow = CreateMaterial("CelebrityYellow", source.sharedMaterial, new Color(1f, 0.85f, 0.04f), false);
            var glow = CreateMaterial("FanGlowStick", source.sharedMaterial, new Color(0.2f, 1f, 0.45f), true);
            var root = new GameObject("CelebrityCrowd");
            try
            {
                var hazard = root.AddComponent<CelebrityCrowdHazard>();
                var data = new SerializedObject(hazard);
                data.FindProperty("celebrityCoat").objectReferenceValue = yellow;
                data.FindProperty("glowMaterial").objectReferenceValue = glow;
                data.ApplyModifiedPropertiesWithoutUndo();
                return PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }

        private static Material CreateMaterial(string name, Material source, Color color, bool emission)
        {
            string path = $"Assets/_Game/Art/Materials/{name}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material != null) return material;
            material = new Material(source) { name = name, color = color };
            if (emission && material.HasProperty("_EmissionColor"))
            { material.EnableKeyword("_EMISSION"); material.SetColor("_EmissionColor", color * 3f); }
            AssetDatabase.CreateAsset(material, path);
            return material;
        }
    }
}
