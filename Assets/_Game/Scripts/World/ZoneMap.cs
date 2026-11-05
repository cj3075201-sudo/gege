using System;
using UnityEngine;

namespace DeliveryRider
{
    [CreateAssetMenu(menuName = "Delivery Rider/Zone Map", fileName = "ZoneMap")]
    public sealed class ZoneMap : ScriptableObject
    {
        [Serializable]
        public struct Zone
        {
            public string id;
            [Tooltip("World xz vertices, clockwise or counterclockwise.")]
            public Vector2[] polygon;
        }

        [SerializeField] private Zone[] zones = Array.Empty<Zone>();
        public System.Collections.Generic.IReadOnlyList<Zone> Zones => zones;

        public string ZoneAt(Vector3 worldPosition)
        {
            var point = new Vector2(worldPosition.x, worldPosition.z);
            if (float.IsNaN(point.x) || float.IsInfinity(point.x) || float.IsNaN(point.y) || float.IsInfinity(point.y)) return null;
            foreach (var zone in zones)
            {
                var polygon = zone.polygon;
                if (string.IsNullOrEmpty(zone.id) || polygon == null || polygon.Length < 3) continue;
                bool inside = false;
                for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
                {
                    Vector2 a = polygon[j], b = polygon[i], edge = b - a, offset = point - a;
                    float cross = edge.x * offset.y - edge.y * offset.x;
                    if (edge.sqrMagnitude > 0f && Mathf.Abs(cross) < 0.0001f
                        && Vector2.Dot(offset, edge) >= 0f && Vector2.Dot(offset, edge) <= edge.sqrMagnitude) return zone.id;
                    if ((a.y > point.y) != (b.y > point.y)
                        && point.x < (b.x - a.x) * (point.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
                }
                if (inside) return zone.id;
            }
            return null;
        }
    }
}
