using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Splines;

namespace DeliveryRider
{
    // Owns the four-district road splines (one RoadSurface child per road) and the terrain they sit on.
    // In the editor, moving a knot rebuilds that road's mesh; use "Rebuild All Roads" after large edits so
    // centre dashes of neighbouring roads also respect the new junctions.
    // At runtime nothing is rebuilt; the meshes are saved with the scene.
    [ExecuteAlways]
    public sealed class RoadNetwork : MonoBehaviour
    {
        [Tooltip("Terrain colliders the road surfaces are projected onto.")]
        [SerializeField] private Collider[] ground = System.Array.Empty<Collider>();
        private readonly List<RoadSurface> roads = new List<RoadSurface>();
#if UNITY_EDITOR
        private readonly HashSet<RoadSurface> dirty = new HashSet<RoadSurface>();
#endif

        private bool roadsCollected;
        public IReadOnlyList<RoadSurface> Roads { get { if(!roadsCollected)Collect(); return roads; } }
        private void OnTransformChildrenChanged() => roadsCollected=false;

        public void Configure(Collider[] groundColliders) => ground = groundColliders;

        [ContextMenu("Rebuild All Roads")]
        public void RebuildAll()
        {
            Collect();
            // Footprints first: centre dashes of each road test against every other road's footprint.
            foreach (var road in roads) road.UpdateFootprint();
            foreach (var road in roads) road.Rebuild(this);
#if UNITY_EDITOR
            MarkSceneDirty();
#endif
        }

        // Point on the terrain below p, lifted by `lift`. Only the terrain chunk under p is ray-cast.
        public Vector3 OnGround(Vector3 p, float lift)
        {
            var ray = new Ray(new Vector3(p.x, p.y + 50f, p.z), Vector3.down);
            foreach (var collider in ground)
            {
                if (collider == null) continue;
                var bounds = collider.bounds;
                if (p.x < bounds.min.x || p.x > bounds.max.x || p.z < bounds.min.z || p.z > bounds.max.z) continue;
                if (collider.Raycast(ray, out var hit, 200f)) { p.y = hit.point.y; break; }
            }
            p.y += lift;
            return p;
        }

        // True when a road end lies on another road (a corner or T-junction rather than an open end).
        public bool TouchesOtherRoad(RoadSurface self, Vector3 world)
        {
            foreach (var road in roads)
            {
                if (road == self || !road.FootprintContains(world, 1f)) continue;
                if (road.DistanceToCentre(world) < road.HalfWidth + 1f) return true;
            }
            return false;
        }

        public bool InsideOtherCarriageway(RoadSurface self, Vector3 world, float margin)
        {
            foreach (var road in roads)
            {
                if (road == self || road.Kind != RoadKind.Main || !road.FootprintContains(world, margin)) continue;
                if (road.DistanceToCentre(world) < road.HalfWidth + margin) return true;
            }
            return false;
        }

        private void Collect()
        {
            roads.Clear();
            GetComponentsInChildren(true, roads);
            foreach(var road in roads)if(road.Footprint.size==Vector3.zero)road.UpdateFootprint();
            roadsCollected=true;
        }

        private void OnEnable()
        {
            Collect();
#if UNITY_EDITOR
            if (!Application.isPlaying) Spline.Changed += OnSplineChanged;
#endif
        }

#if UNITY_EDITOR
        private void OnDisable() => Spline.Changed -= OnSplineChanged;

        private void OnSplineChanged(Spline spline, int knot, SplineModification modification)
        {
            Collect();
            foreach (var road in roads)
                if (road.Container.Spline == spline) { dirty.Add(road); return; }
        }

        private void Update()
        {
            if (Application.isPlaying || dirty.Count == 0) return;
            foreach (var road in roads) if (road.Footprint.size == Vector3.zero) road.UpdateFootprint();
            bool rebuilt = false;
            foreach (var road in dirty)
            {
                if (road == null || !road.ShapeChanged()) continue;
                road.UpdateFootprint();
                road.Rebuild(this);
                rebuilt = true;
            }
            dirty.Clear();
            if (rebuilt) MarkSceneDirty();
        }

        // Road meshes live in the scene, so a rebuild must mark it for saving.
        private void MarkSceneDirty()
        {
            if (!Application.isPlaying) UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
        }
#endif
    }
}
