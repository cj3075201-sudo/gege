using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DeliveryRider.Editor
{
    public static class DogCloneChecks
    {
        public static void Run()
        {
            foreach (string name in new[] { "Dog", "CloneRider" })
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/_Game/Prefabs/Hazards/" + name + ".prefab");
                Check(prefab != null, name + " prefab exists");
                var type = typeof(RiderMotor).Assembly.GetType("DeliveryRider." + name + "Hazard");
                Check(type != null && typeof(IPlacedHazard).IsAssignableFrom(type), name + " supports planner placement");
                var hazard = prefab.GetComponent(type);
                Check(hazard != null, name + " prefab is wired");
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic))
                {
                    if (!field.IsDefined(typeof(SerializeField)) || field.FieldType != typeof(float)) continue;
                    Check(DevValue.Discover(hazard).Exists(v => v.PropertyPath == field.Name), "F1 discovers " + field.Name);
                    Check(DevLabels.Find(type.Name, field.Name) != null, "Korean label: " + type.Name + "." + field.Name);
                }
                foreach (var renderer in prefab.GetComponentsInChildren<Renderer>(true))
                    Check(renderer.sharedMaterial != null && AssetDatabase.Contains(renderer.sharedMaterial), "existing material: " + renderer.name);
            }
            Debug.Log("DogCloneChecks PASS: prefab wiring, planner placement, serialized F1 values and Korean labels.");
        }

        internal static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("DogCloneChecks: " + message);
        }
    }
}
