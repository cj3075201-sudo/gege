using System;
using UnityEngine;

namespace DeliveryRider
{
    // Keep this component active. Toggle only its geometry, so it can open on a later day.
    public sealed class ZoneBarrier : MonoBehaviour
    {
        [SerializeField] private DeliverySession session;
        [SerializeField] private string[] requiredZones = Array.Empty<string>();
        [Tooltip("Optional child containing visuals and player-only collision geometry. Never assign a parent of this component.")]
        [SerializeField] private GameObject barrierRoot;
        private Collider[] colliders;
        private Renderer[] renderers;

        private void Awake()
        {
            colliders = GetComponentsInChildren<Collider>(true);
            renderers = GetComponentsInChildren<Renderer>(true);
            if (session == null) session = FindFirstObjectByType<DeliverySession>();
        }

        private void Start() => Refresh();

        public void Refresh()
        {
            if (session == null) return;
            bool closed = false;
            foreach (string zone in requiredZones)
                if (!session.IsZoneOpen(zone)) { closed = true; break; }
            if (barrierRoot != null && !transform.IsChildOf(barrierRoot.transform)) barrierRoot.SetActive(closed);
            else
            {
                foreach (var collider in colliders) collider.enabled = closed;
                foreach (var renderer in renderers) renderer.enabled = closed;
            }
        }
    }
}
