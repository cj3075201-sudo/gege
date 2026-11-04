using UnityEngine;
using UnityEngine.UI;

namespace DeliveryRider
{
    public sealed partial class DeliveryMapView
    {
        [Header("Closed zones (optional session ZoneMap)")]
        [SerializeField, Range(32, 512)] private int zoneMaskResolution = 128;
        [SerializeField] private Color closedZoneShade = new Color32(38, 46, 53, 170);
        private RawImage closedZones;
        private Texture2D zoneMask;
        private ZoneMap displayedZoneMap;
        private int displayedZoneDay = -1;
        private Vector2 displayedWorldMinimum;
        private Vector2 displayedWorldSize;

        private void RefreshClosedZones()
        {
            var zones = session.ZoneMap;
            if (zones == null)
            {
                if (closedZones != null) closedZones.gameObject.SetActive(false);
                return;
            }
            if (closedZones == null)
            {
                var overlay = new GameObject("ClosedZones", typeof(RectTransform), typeof(RawImage));
                closedZones = overlay.GetComponent<RawImage>();
                var rect = closedZones.rectTransform;
                rect.SetParent(mapArea, false);
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = rect.offsetMax = Vector2.zero;
                closedZones.raycastTarget = false;
                // Shade the map art while keeping route lines and every live marker above it.
                int index = mapArea.childCount - 1;
                IncludeSibling(playerMarker, ref index);
                IncludeSibling(activeCustomerMarker, ref index);
                IncludeSibling(thiefMarker, ref index);
                IncludeSibling(pickupConnection, ref index);
                IncludeSibling(deliveryConnection, ref index);
                foreach (var marker in candidateMarkers) IncludeSibling(marker, ref index);
                foreach (var tether in candidateTethers) IncludeSibling(tether, ref index);
                foreach (var marker in pickupMarkers) if (marker != null) IncludeSibling(marker.rectTransform, ref index);
                rect.SetSiblingIndex(index);
            }
            closedZones.gameObject.SetActive(true);
            closedZones.color = closedZoneShade;
            int resolution = Mathf.Clamp(zoneMaskResolution, 32, 512);
            if (displayedZoneMap == zones && displayedZoneDay == session.Progress.Day && zoneMask != null
                && zoneMask.width == resolution && displayedWorldMinimum == worldMinimum && displayedWorldSize == worldSize) return;
            if (zoneMask != null) Destroy(zoneMask);
            zoneMask = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false)
            {
                name = "ClosedZoneMask", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear
            };
            var pixels = new Color32[resolution * resolution];
            for (int y = 0; y < resolution; y++)
                for (int x = 0; x < resolution; x++)
                {
                    var world = new Vector3(worldMinimum.x + worldSize.x * (x + 0.5f) / resolution, 0f,
                        worldMinimum.y + worldSize.y * (y + 0.5f) / resolution);
                    string zone = zones.ZoneAt(world);
                    pixels[y * resolution + x] = new Color32(255, 255, 255, (byte)(zone != null && !session.IsZoneOpen(zone) ? 255 : 0));
                }
            zoneMask.SetPixels32(pixels);
            zoneMask.Apply(false, true);
            closedZones.texture = zoneMask;
            displayedZoneMap = zones;
            displayedZoneDay = session.Progress.Day;
            displayedWorldMinimum = worldMinimum;
            displayedWorldSize = worldSize;
        }

        private void IncludeSibling(RectTransform rect, ref int index)
        {
            if (rect != null && rect.parent == mapArea) index = Mathf.Min(index, rect.GetSiblingIndex());
        }

        private void OnDestroy()
        {
            if (zoneMask != null) Destroy(zoneMask);
            if (closedZones != null) Destroy(closedZones.gameObject);
        }
    }
}
