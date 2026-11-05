using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DeliveryRider.Editor
{
    // Each trigger, already running, against a rider who has lost the food. Runs the real hit code in an empty scene;
    // every case also proves the hit does land when strikes are allowed, so a silent no-op cannot pass.
    public static class HazardGateChecks
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private const string Prefabs = "Assets/_Game/Prefabs/Hazards/";
        private static RiderMotor rider;
        private static DeliverySession session;
        private static int hits;

        public static void Run()
        {
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                var player = new GameObject("Player");
                player.transform.position = new Vector3(1000f, 1f, 1000f);
                player.AddComponent<Rigidbody>().useGravity = false;
                player.AddComponent<CapsuleCollider>();
                rider = player.AddComponent<RiderMotor>();
                session = new GameObject("Session").AddComponent<DeliverySession>();
                Set(session, "player", player.transform);
                Set(rider, "session", session);
                Call(rider, "Awake");
                rider.HazardHit += _ => hits++;
                Physics.SyncTransforms();

                Case("melon", Melon);
                Case("tire", Tire);
                Case("duck", Duck);
                Case("muscleman", Muscleman);
                Chicken();
                FoodRule();
                MusclemanRetry();
                Debug.Log("HazardGateChecks PASS: all checks.");
                EditorApplication.Exit(0);
            }
            catch (Exception failure)
            {
                Debug.LogError("HazardGateChecks FAIL: " + failure);
                EditorApplication.Exit(1);
            }
        }

        private static void Case(string name, Action<bool> strike)
        {
            Reset();
            strike(false);
            if (hits != 0) throw new InvalidOperationException(name + " hit a rider without food");
            Debug.Log($"HazardGateChecks ok: {name} does not hit without food");
            Reset();
            strike(true);
            if (hits != 1) throw new InvalidOperationException($"{name} did not hit when allowed ({hits})");
            Debug.Log($"HazardGateChecks ok: {name} hits when allowed");
        }

        private static void Reset()
        {
            hits = 0;
            Set(rider, "lastHazardHitAt", float.NegativeInfinity);
            Set(rider, "lastHazard", null);
            Set(rider, "trafficRecoveryActive", false);
            Set(rider, "nextTrafficHitAt", 0f);
            Call(rider, "ResetReaction");
        }

        private static T Spawn<T>(string prefab, bool allowed) where T : Component
        {
            var made = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Prefabs + prefab + ".prefab"));
            PrefabUtility.UnpackPrefabInstance(made, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            made.transform.position = rider.transform.position;
            var slot = made.AddComponent<HazardSlot>();
            slot.Configure("test", HazardRole.Trigger, 1, Array.Empty<int>());
            slot.StrikeAllowed = allowed;
            var hazard = made.GetComponent<T>();
            Set(hazard, "slot", slot);
            Set(hazard, "rider", rider);
            if (Field(hazard, "session") != null) Set(hazard, "session", session);
            Physics.SyncTransforms();
            return hazard;
        }

        private static void Melon(bool allowed)
        {
            var hazard = Spawn<RollingMelonHazard>("MelonPile", allowed);
            SetEnum(hazard, "phase", "Rolling");
            var melon = hazard.GetComponentInChildren<RollingMelon>(true);
            melon.transform.position = rider.transform.position + Vector3.right;
            hazard.Contact(melon, rider);
            UnityEngine.Object.DestroyImmediate(hazard.gameObject);
        }

        private static void Tire(bool allowed)
        {
            var hazard = Spawn<RollingTireHazard>("RollingTire", allowed);
            Call(hazard, "CheckContact", rider.transform.position + Vector3.right * 0.5f, Vector3.left, 0.1f);
            UnityEngine.Object.DestroyImmediate(hazard.gameObject);
        }

        private static void Duck(bool allowed)
        {
            var hazard = Spawn<CrossingFlockHazard>("DuckFlock", allowed);
            Set(hazard, "localStart", Vector3.zero);
            Set(hazard, "localEnd", Vector3.right * 10f);
            Call(hazard, "Advance");
            UnityEngine.Object.DestroyImmediate(hazard.gameObject);
        }

        private static void Muscleman(bool allowed)
        {
            var hazard = Spawn<MusclemanHazard>("Muscleman", allowed);
            Call(hazard, "Shove", rider.GetComponent<Collider>(), Vector3.forward);
            UnityEngine.Object.DestroyImmediate(hazard.gameObject);
        }

        private static void Chicken()
        {
            float free = rider.Speed;
            void Prepare(ChaseFlockHazard h)
            {
                Set(h, "playerShape", rider.GetComponent<CapsuleCollider>());
                Call(h, "Awake");
                Set(h, "rider", rider);
                foreach (var bird in (Transform[])Field(h, "birds").GetValue(h))
                {
                    bird.position = rider.transform.position;
                    bird.gameObject.SetActive(true);
                }
            }
            Reset();
            var off = Spawn<ChaseFlockHazard>("Chickens", false);
            Prepare(off);
            Call(off, "Chase");
            if (rider.HasChickenAttachment(off) || rider.Speed != free) throw new InvalidOperationException("a bird attached to a rider without food");
            Debug.Log("HazardGateChecks ok: chicken does not attach without food");
            UnityEngine.Object.DestroyImmediate(off.gameObject);

            Reset();
            var on = Spawn<ChaseFlockHazard>("Chickens", true);
            Prepare(on);
            Call(on, "Chase");
            if (!rider.HasChickenAttachment(on) || rider.Speed >= free) throw new InvalidOperationException("a bird did not attach and slow the rider when allowed");
            Debug.Log("HazardGateChecks ok: chicken attaches and slows the rider when allowed");
            on.GetComponent<HazardSlot>().StrikeAllowed = false;
            Call(on, "Chase");
            if (rider.HasChickenAttachment(on) || rider.Speed != free || (bool)Field(on, "hasAttachedBirds").GetValue(on))
                throw new InvalidOperationException("attached birds stayed after the food was lost");
            Debug.Log("HazardGateChecks ok: losing the food removes attached birds and the slowdown at once");
            UnityEngine.Object.DestroyImmediate(on.gameObject);
        }

        // triggersRequireFood off must behave as before: only theft waits for the food.
        private static void FoodRule()
        {
            var planner = new GameObject("Planner").AddComponent<HazardPlanner>();
            Set(planner, "session", session);
            var kinds = new Dictionary<string, HazardRole> { { "manhole", HazardRole.Object }, { "duck", HazardRole.Trigger }, { "theft", HazardRole.Trigger } };
            bool Can(string type, bool carrying, bool requireFood)
            {
                Set(planner, "triggersRequireFood", requireFood);
                session.GetType().GetProperty("Stage").SetValue(session, carrying ? DeliveryStage.Carrying : DeliveryStage.Pickup);
                var slot = new GameObject(type).AddComponent<HazardSlot>();
                slot.Configure(type, kinds[type], 1, Array.Empty<int>());
                bool result = (bool)Call(planner, "CanStrike", slot);
                UnityEngine.Object.DestroyImmediate(slot.gameObject);
                return result;
            }
            var expected = new (string type, bool carrying, bool requireFood, bool can)[]
            {
                ("manhole", false, true, true), ("duck", false, true, false), ("duck", true, true, true), ("theft", false, true, false), ("theft", true, true, true),
                ("manhole", false, false, true), ("duck", false, false, true), ("duck", true, false, true), ("theft", false, false, false), ("theft", true, false, true),
            };
            foreach (var e in expected)
                if (Can(e.type, e.carrying, e.requireFood) != e.can) throw new InvalidOperationException($"{e.type} carrying={e.carrying} requireFood={e.requireFood} should be {e.can}");
            Debug.Log("HazardGateChecks ok: with triggersRequireFood off every trigger but theft ignores the food; on, all wait for it");
            UnityEngine.Object.DestroyImmediate(planner.gameObject);
        }

        // With no sidewalk route the search must wait, not run again on every physics step.
        private static void MusclemanRetry()
        {
            var hazard = Spawn<MusclemanHazard>("Muscleman", true);
            Set(hazard, "sidewalks", new GameObject("NoSidewalks").AddComponent<PedestrianNetwork>());
            Set(hazard, "playerShape", rider.GetComponent<CapsuleCollider>());
            Set(hazard, "volume", hazard.GetComponent<BoxCollider>());
            Set(hazard, "session", session);
            Set(hazard, "nextRouteTryAt", 0f);
            Set(hazard, "windSeconds", Vector2.zero);
            Physics.SyncTransforms();
            Call(hazard, "FixedUpdate");
            float waitUntil = (float)Field(hazard, "nextRouteTryAt").GetValue(hazard);
            if (waitUntil <= Time.time) throw new InvalidOperationException("a failed route search set no wait");
            if (!Field(hazard, "phase").GetValue(hazard).ToString().Equals("Ready")) throw new InvalidOperationException("a failed route search left the Ready phase");
            Set(hazard, "nextRouteTryAt", waitUntil + 100f);
            Call(hazard, "FixedUpdate");
            if ((float)Field(hazard, "nextRouteTryAt").GetValue(hazard) != waitUntil + 100f) throw new InvalidOperationException("the search ran during its wait");
            Debug.Log("HazardGateChecks ok: muscleman waits between failed route searches");
            UnityEngine.Object.DestroyImmediate(hazard.gameObject);
        }

        private static FieldInfo Field(object owner, string name) => owner.GetType().GetField(name, Any);
        private static void Set(object owner, string name, object value)
        {
            var field = Field(owner, name) ?? throw new MissingFieldException(owner.GetType().Name, name);
            field.SetValue(owner, value);
        }
        private static void SetEnum(object owner, string name, string value)
        {
            var field = Field(owner, name) ?? throw new MissingFieldException(owner.GetType().Name, name);
            field.SetValue(owner, Enum.Parse(field.FieldType, value));
        }
        private static object Call(object owner, string name, params object[] arguments)
        {
            var method = owner.GetType().GetMethod(name, Any) ?? throw new MissingMethodException(owner.GetType().Name, name);
            return method.Invoke(owner, arguments);
        }
    }
}
