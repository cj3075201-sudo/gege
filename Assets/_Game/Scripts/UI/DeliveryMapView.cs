using TMPro;
using UnityEngine;

namespace DeliveryRider
{
    public sealed partial class DeliveryMapView : MonoBehaviour
    {
        [SerializeField] private DeliverySession session;
        [SerializeField] private RectTransform mapArea;
        [SerializeField] private Vector2 worldMinimum = new Vector2(-29, -26);
        [SerializeField] private Vector2 worldSize = new Vector2(58, 58);
        [SerializeField] private RectTransform playerMarker;
        [SerializeField] private RectTransform[] candidateMarkers;
        [SerializeField] private RectTransform[] candidateTethers;
        [SerializeField] private UnityEngine.UI.Image[] pickupMarkers;
        [SerializeField] private RectTransform activeCustomerMarker;
        [SerializeField] private TMP_Text activeCustomerLabel;
        [SerializeField] private RectTransform pickupConnection;
        [SerializeField] private RectTransform deliveryConnection;
        [SerializeField] private UnityEngine.UI.Button closeButton;
        [SerializeField] private RectTransform thiefMarker;
        private static readonly Color Gold = new Color32(240, 170, 45, 255);
        private static readonly Color Teal = new Color32(40, 197, 177, 255);
        private UnityEngine.UI.Image activeCustomerImage;
        private int displayedActiveNumber = -1;
        private TMP_Text[] pickupLabels;
        private readonly Transform[] shownShops = new Transform[3];

        private void Awake()
        {
            closeButton.onClick.AddListener(session.ToggleMap);
            activeCustomerImage = activeCustomerMarker.GetComponent<UnityEngine.UI.Image>();
            pickupLabels = new TMP_Text[pickupMarkers.Length];
            for (int i = 0; i < pickupMarkers.Length; i++) pickupLabels[i] = pickupMarkers[i].GetComponentInChildren<TMP_Text>(true);
        }

        private void LateUpdate()
        {
            if (!session.IsMapOpen || session.IsPaused) return;
            RefreshClosedZones();
            playerMarker.anchoredPosition = Point(session.Player.position);
            playerMarker.localRotation = Quaternion.Euler(0, 0, -session.Player.eulerAngles.y);
            for (int i = 0; i < candidateMarkers.Length; i++)
            {
                bool exists = session.IsCallOpen && i < session.Candidates.Count;
                candidateTethers[i].gameObject.SetActive(exists);
                if (!exists) continue;
                var customer = session.Candidates[i].DestinationPoint;
                int count = 0, index = 0;
                for (int j = 0; j < session.Candidates.Count; j++)
                    if (session.Candidates[j].DestinationPoint == customer) { if (j < i) index++; count++; }
                var actual = Point(customer.position);
                var displayed = Clamp(actual + Vector2.right * ((index - (count - 1) * 0.5f) * 66f), 35f);
                candidateMarkers[i].anchoredPosition = displayed;
                Line(candidateTethers[i], actual, displayed);
            }
            var shown = session.ActiveOrder ?? (session.IsCallOpen ? session.SelectedOrder : null);
            ShowCallShops(shown);
            bool active = session.ActiveOrder != null;
            if (thiefMarker != null)
            {
                thiefMarker.gameObject.SetActive(session.IsChasingFood);
                if (session.IsChasingFood) thiefMarker.anchoredPosition = Clamp(Point(session.Target.position), 35f);
            }
            activeCustomerMarker.gameObject.SetActive(active);
            if (active)
            {
                activeCustomerMarker.anchoredPosition = Clamp(Point(session.ActiveOrder.Recipient.transform.position), 35f);
                if (displayedActiveNumber != session.ActiveOrder.Number)
                {
                    displayedActiveNumber = session.ActiveOrder.Number;
                    activeCustomerLabel.text = "#" + displayedActiveNumber.ToString("000");
                }
                activeCustomerImage.color = session.Stage == DeliveryStage.Carrying ? Teal : new Color32(191, 225, 211, 255);
            }
            pickupConnection.gameObject.SetActive(shown != null && session.Stage != DeliveryStage.Carrying);
            deliveryConnection.gameObject.SetActive(shown != null);
            if (session.IsRecoveringFood)
            {
                pickupConnection.gameObject.SetActive(true);
                deliveryConnection.gameObject.SetActive(false);
                Line(pickupConnection, Point(session.Player.position), Point(session.Target.position));
                return;
            }
            if (shown == null) return;
            var player = Point(session.Player.position);
            var pickup = Point(shown.PickupPoint.position);
            var destination = Point(session.ActiveOrder == shown ? shown.Recipient.transform.position : shown.DestinationPoint.position);
            if (session.Stage != DeliveryStage.Carrying) Line(pickupConnection, player, pickup);
            Line(deliveryConnection, session.Stage == DeliveryStage.Carrying ? player : pickup, destination);
        }

        // Only the shops involved in the open calls or the active order are drawn.
        private void ShowCallShops(DeliveryOrderOffer shown)
        {
            int count = 0;
            if (session.ActiveOrder != null) shownShops[count++] = session.ActiveOrder.PickupPoint;
            else if (session.IsCallOpen)
                foreach (var offer in session.Candidates)
                    if (count < shownShops.Length && System.Array.IndexOf(shownShops, offer.PickupPoint, 0, count) < 0) shownShops[count++] = offer.PickupPoint;
            for (int i = 0; i < pickupMarkers.Length; i++)
            {
                bool visible = i < count;
                if (pickupMarkers[i].gameObject.activeSelf != visible) pickupMarkers[i].gameObject.SetActive(visible);
                if (!visible) continue;
                pickupMarkers[i].rectTransform.anchoredPosition = Point(shownShops[i].position);
                bool selected = shown != null && shown.PickupPoint == shownShops[i];
                pickupMarkers[i].color = selected ? Gold : new Color32(216, 210, 188, 255);
                pickupMarkers[i].rectTransform.localScale = Vector3.one * (selected ? 1.12f : 1f);
                string name = NameOf(shownShops[i]);
                if (pickupLabels[i] != null && pickupLabels[i].text != name) pickupLabels[i].text = name;
            }
        }

        private string NameOf(Transform shop)
        {
            if (session.ActiveOrder != null && session.ActiveOrder.PickupPoint == shop) return session.ActiveOrder.RestaurantName;
            foreach (var offer in session.Candidates)
                if (offer.PickupPoint == shop) return offer.RestaurantName;
            return shop.name;
        }

        private Vector2 Point(Vector3 world)
        {
            var normalized = new Vector2((world.x - worldMinimum.x) / worldSize.x, (world.z - worldMinimum.y) / worldSize.y);
            return Clamp(Vector2.Scale(normalized - Vector2.one * 0.5f, mapArea.rect.size), 12f);
        }
        private Vector2 Clamp(Vector2 point, float margin)
        {
            var half = mapArea.rect.size * 0.5f - Vector2.one * margin;
            return new Vector2(Mathf.Clamp(point.x, -Mathf.Max(0, half.x), Mathf.Max(0, half.x)), Mathf.Clamp(point.y, -Mathf.Max(0, half.y), Mathf.Max(0, half.y)));
        }
        private static void Line(RectTransform rect, Vector2 from, Vector2 to)
        {
            var direction = to - from;
            rect.anchoredPosition = from;
            rect.sizeDelta = new Vector2(direction.magnitude, 2f);
            rect.localRotation = Quaternion.Euler(0, 0, Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg);
        }
    }
}
