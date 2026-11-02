using UnityEngine;

namespace DeliveryRider
{
    // One scene UI listens to the session. Hazard prefabs never hold a reference into the scene Canvas.
    [RequireComponent(typeof(CanvasGroup))]
    public sealed class PigeonFlockOverlay : MonoBehaviour
    {
        [SerializeField] private DeliverySession session;
        [SerializeField] private Sprite icon;
        [SerializeField, Min(1)] private int iconCount = 48;
        [SerializeField] private Vector2 heightShare = new Vector2(0.45f, 0.65f);
        [SerializeField] private Vector2 speedShare = new Vector2(0.245f, 0.49f);
        private RectTransform area;
        private CanvasGroup group;
        private RectTransform[] icons;
        private Vector2[] positions;
        private float[] directions, speeds, sizes, phases;
        private float remaining;

        public void Configure(DeliverySession owner, Sprite sprite) { session = owner; icon = sprite; }

        private void Awake()
        {
            area = (RectTransform)transform;
            group = GetComponent<CanvasGroup>();
            group.alpha = 0f;
            group.blocksRaycasts = group.interactable = false;
            icons = new RectTransform[iconCount];
            positions = new Vector2[iconCount]; directions = new float[iconCount];
            speeds = new float[iconCount]; sizes = new float[iconCount]; phases = new float[iconCount];
            for (int i = 0; i < iconCount; i++)
            {
                var obj = new GameObject($"Pigeon_{i:00}", typeof(RectTransform), typeof(UnityEngine.UI.Image));
                var rect = (RectTransform)obj.transform;
                rect.SetParent(transform, false);
                rect.anchorMin = rect.anchorMax = Vector2.one * 0.5f;
                var picture = obj.GetComponent<UnityEngine.UI.Image>();
                picture.sprite = icon;
                picture.preserveAspect = true;
                picture.raycastTarget = false;
                picture.enabled = false;
                icons[i] = rect;
            }
        }

        private void OnEnable() { if (session != null) session.PigeonFlockObscured += Show; }
        private void OnDisable()
        {
            if (session != null) session.PigeonFlockObscured -= Show;
            remaining = 0f;
            if (group != null) group.alpha = 0f;
        }

        private void Show(float seconds)
        {
            if (remaining > 0f || icon == null) return;
            remaining = seconds;
            int columns = Mathf.CeilToInt(Mathf.Sqrt(iconCount * 1.5f));
            int rows = Mathf.CeilToInt(iconCount / (float)columns);
            for (int i = 0; i < icons.Length; i++)
            {
                positions[i] = new Vector2((i % columns + Random.Range(0.1f, 0.9f)) / columns,
                    (i / columns + Random.Range(0.1f, 0.9f)) / rows);
                directions[i] = i % 2 == 0 ? 1f : -1f;
                speeds[i] = Random.Range(speedShare.x, speedShare.y);
                sizes[i] = Random.Range(heightShare.x, heightShare.y);
                phases[i] = Random.Range(0f, Mathf.PI * 2f);
                icons[i].localScale = new Vector3(directions[i], 1f, 1f);
                icons[i].localRotation = Quaternion.Euler(0f, 0f, Random.Range(-15f, 15f));
                icons[i].GetComponent<UnityEngine.UI.Image>().enabled = true;
            }
            Render(0f);
            group.alpha = 1f;
        }

        private void Update()
        {
            if (remaining <= 0f) return;
            if (session.IsSettled) remaining = 0f;
            else remaining = Mathf.Max(0f, remaining - Time.deltaTime);
            bool menu = session.IsPaused || session.IsMapOpen || session.IsCallOpen;
            group.alpha = remaining > 0f && !menu ? Mathf.Min(1f, remaining / 0.15f) : 0f;
            if (remaining > 0f) Render(Time.deltaTime);
            else foreach (var rect in icons) rect.GetComponent<UnityEngine.UI.Image>().enabled = false;
        }

        private void Render(float delta)
        {
            var size = area.rect.size;
            for (int i = 0; i < icons.Length; i++)
            {
                float extent = size.y * sizes[i];
                float padding = extent / Mathf.Max(1f, size.x) * 0.6f;
                positions[i].x += directions[i] * speeds[i] * delta;
                if (positions[i].x > 1f + padding) positions[i].x = -padding;
                else if (positions[i].x < -padding) positions[i].x = 1f + padding;
                icons[i].sizeDelta = Vector2.one * extent;
                icons[i].anchoredPosition = new Vector2((positions[i].x - 0.5f) * size.x,
                    (positions[i].y - 0.5f + Mathf.Sin(Time.time * 5f + phases[i]) * 0.025f) * size.y);
            }
        }
    }
}
