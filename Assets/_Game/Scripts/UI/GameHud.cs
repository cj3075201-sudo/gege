using System;
using TMPro;
using UnityEngine;

namespace DeliveryRider
{
    public sealed class GameHud : MonoBehaviour
    {
        [SerializeField] private DeliverySession session;
        [SerializeField] private TMP_Text objective;
        [SerializeField] private TMP_Text distance;
        [SerializeField] private TMP_Text prompt;
        [SerializeField] private TMP_Text cardTitle;
        [SerializeField] private TMP_Text cardBody;
        [SerializeField] private TMP_Text actionLabel;
        [SerializeField] private GameObject orderCard;
        [SerializeField] private GameObject pausePanel;
        [SerializeField] private UnityEngine.UI.Button accept;
        [SerializeField] private UnityEngine.UI.Button resume;
        [SerializeField] private UnityEngine.UI.Button title;
        [SerializeField] private UnityEngine.UI.Button[] candidateButtons = System.Array.Empty<UnityEngine.UI.Button>();
        [Header("Call list (optional; Apply HUD Style builds it)")]
        [Tooltip("One order card per call candidate, shown instead of the detail text while the call is open.")]
        [SerializeField] private UnityEngine.UI.Button[] callCards = System.Array.Empty<UnityEngine.UI.Button>();
        [Tooltip("Holds the detail text; hidden while the call cards show the candidates.")]
        [SerializeField] private GameObject detailCard;
        private static readonly Color SelectedCall = new Color32(214, 243, 238, 255);
        private static readonly Color IdleCall = new Color32(243, 245, 244, 255);
        private TMP_Text[] candidateLabels;
        private TMP_Text[] callCardTexts;
        private int lastDistance = -1;

        private void Awake()
        {
            accept.onClick.AddListener(session.AcceptOrder);
            resume.onClick.AddListener(session.TogglePause);

            // 기존 코드: title.onClick.AddListener(session.BackToTitle);
            // 변경된 코드: 일시정지(시간 정지)를 풀고 SceneController를 통해 타이틀 씬으로 이동
            title.onClick.AddListener(() =>
            {
                Time.timeScale = 1f; // 일시정지 상태에서 나갈 경우를 대비해 시간 흐름 정상화
                SceneController.Instance.NextScene(ESceneType.Title);
            });

            candidateLabels = new TMP_Text[candidateButtons.Length];
            for (int i = 0; i < candidateButtons.Length; i++)
            {
                int slot = i;
                candidateLabels[i] = candidateButtons[i].GetComponentInChildren<TMP_Text>(true);
                candidateButtons[i].onClick.AddListener(() => session.SelectCandidate(slot));
            }
            callCardTexts = new TMP_Text[callCards.Length];
            for (int i = 0; i < callCards.Length; i++)
            {
                int slot = i;
                callCardTexts[i] = callCards[i].GetComponentInChildren<TMP_Text>(true);
                callCards[i].onClick.AddListener(() => session.SelectCandidate(slot));
            }
        }
        private void OnEnable() { session.Changed += Refresh; Refresh(); }
        private void OnDisable() => session.Changed -= Refresh;

        private void Refresh()
        {
            pausePanel.SetActive(session.IsPaused);
            orderCard.SetActive(!session.IsPaused && session.IsMapOpen);
            string orderTitle = session.ActiveOrder == null ? null : $"진행 중 주문 #{session.ActiveOrder.Number:000}";
            switch (session.Stage)
            {
                case DeliveryStage.Available:
                    objective.text = session.HasDayStarted ? "1  콜 확인" : "집을 나서면 하루가 시작됩니다   ·   1  콜 확인";
                    cardTitle.text = "지도   ·   1  콜 확인";
                    break;
                case DeliveryStage.Pickup:
                    objective.text = $"01  {session.ActiveOrder.RestaurantName}에서 음식 받기";
                    cardTitle.text = orderTitle; break;
                case DeliveryStage.Carrying:
                    objective.text = $"02  {session.ActiveOrder.CustomerName}을(를) 주먹으로 쳐서 전달";
                    cardTitle.text = orderTitle; break;
                case DeliveryStage.Complete:
                    objective.text = "배달 완료!   ·   1  다음 콜 확인";
                    cardTitle.text = session.LastResult == null ? "배달 완료!" :
                        $"배달 완료 · {(session.LastResult.WasLate ? "지각" : "정시")} +{session.LastResult.Reward:N0}원";
                    break;
                case DeliveryStage.FoodLost:
                    objective.text = session.IsChasingFood ? "도둑을 주먹으로 쳐서 음식을 회수하세요" : $"{session.ActiveOrder.RestaurantName}에서 음식 다시 받기";
                    cardTitle.text = session.IsChasingFood ? "음식 도난 · 도둑 추격" : "음식 분실 · 음식점 재수령";
                    break;
            }
            if (session.IsCallOpen) cardTitle.text = callCards.Length > 0 ? $"새 콜 {session.Candidates.Count}건" : "콜 확인   ·   1 / 2  수락";
            if (session.DayPhase == WorkDayPhase.Overtime) cardTitle.text = "영업 종료 · 현재 배달을 마무리하세요";
            if (session.IsSettled) objective.text = "오늘의 영업이 종료되었습니다";
            RefreshCandidates();
            distance.text = session.ActiveOrder != null ? "목적지로 이동하세요" : $"오늘 완료 {session.CompletedCount}건";
            lastDistance = -1;
            UpdatePrompt();
        }

        private void RefreshCandidates()
        {
            for (int i = 0; i < candidateButtons.Length; i++)
            {
                bool exists = session.IsCallOpen && i < session.Candidates.Count;
                candidateButtons[i].gameObject.SetActive(exists);
                if (!exists) continue;
                var offer = session.Candidates[i];
                bool selected = offer == session.SelectedOrder;
                candidateLabels[i].text = $"{i + 1}  #{offer.Number:000}";
                candidateButtons[i].interactable = session.CanChooseOrder;
                candidateButtons[i].GetComponent<UnityEngine.UI.Image>().color = selected
                    ? new Color32(40, 197, 177, 255) : new Color32(221, 232, 224, 255);
            }
            for (int i = 0; i < callCards.Length; i++)
            {
                bool exists = session.IsCallOpen && i < session.Candidates.Count;
                callCards[i].gameObject.SetActive(exists);
                if (!exists) continue;
                var offer = session.Candidates[i];
                callCardTexts[i].text = CallSummary(i, offer);
                callCards[i].interactable = session.CanChooseOrder;
                callCards[i].GetComponent<UnityEngine.UI.Image>().color = offer == session.SelectedOrder ? SelectedCall : IdleCall;
            }
            if (detailCard != null) detailCard.SetActive(!session.IsCallOpen || callCards.Length == 0);
            accept.gameObject.SetActive(session.IsCallOpen);
            accept.interactable = session.CanChooseOrder && session.SelectedOrder != null;
            var selectedOrder = session.ActiveOrder ?? (session.IsCallOpen ? session.SelectedOrder : null);
            if (selectedOrder == null)
            {
                cardBody.text = session.CanOpenCall ? "1 키를 누르면 콜 화면이 열립니다.\n지도를 보는 동안에도 시간은 흐릅니다." : "지도를 보는 동안에도 시간은 흐릅니다.";
                actionLabel.text = "주문 없음";
                return;
            }
            var data = selectedOrder;
            string clock = session.ActiveOrder == null || !session.HasPickedUp ? "음식을 받는 순간 시작" : "진행 중";
            string from = data.PickupZone == null ? data.RestaurantName : $"{data.RestaurantName}  ({ZoneLabel(data.PickupZone)})";
            string to = data.DestinationZone == null ? data.CustomerName : $"{data.CustomerName}  ({ZoneLabel(data.DestinationZone)})";
            string distanceLine = data.Distance > 0f ? $"큰길 기준 {data.Distance:0}m · {data.TierLabel}\n" : "";
            cardBody.text = $"주문 #{selectedOrder.Number:000}  ·  {data.FoodName}\n"
                + $"수령  {from}\n전달  {to}\n{distanceLine}\n"
                + $"정시 {data.OnTimeReward:N0}원 / 지각 {data.LateReward:N0}원\n"
                + $"제한 시간 {data.TimeLimitSeconds:0}초 ({clock})\n<size=14>늦어도 배달 가능 · 콜 화면에서는 시간이 멈춤</size>";
            actionLabel.text = session.ActiveOrder == null ? $"#{selectedOrder.Number:000} 주문 수락" : "배달 진행 중 · 추가 수락 불가";
        }

        // One call as a delivery-app order card: the key that accepts it, shop to customer, then the job and the pay.
        private static string CallSummary(int slot, DeliveryOrderOffer offer)
        {
            string distance = offer.Distance > 0f ? $" · {offer.Distance:0}m {offer.TierLabel}" : "";
            return $"<mark=#1D2B3326 padding=\"8,8,3,3\"><b>{slot + 1}</b></mark>  <b>{offer.RestaurantName}</b>  →  <b>{offer.CustomerName}</b>\n"
                + $"<size=82%><color=#5B6B70>{offer.FoodName}{distance} · 제한 {offer.TimeLimitSeconds:0}초</color></size>\n"
                + $"<size=92%>정시 <b>{offer.OnTimeReward:N0}원</b>   <color=#5B6B70>지각 {offer.LateReward:N0}원</color></size>";
        }

        private string ZoneLabel(string zone)
        {
            string name = session.RouteTable != null ? session.RouteTable.ZoneName(zone) : null;
            return string.IsNullOrEmpty(name) || name == zone ? zone
                : name.StartsWith(zone + " ", StringComparison.Ordinal) ? name : $"{zone} {name}";
        }

        private void Update()
        {
            if (session.ActiveOrder == null || session.IsPaused) return;
            int meters = Mathf.CeilToInt(session.TargetDistance);
            if (meters != lastDistance) { distance.text = $"목적지까지  {meters} m"; lastDistance = meters; }
        }

        private void UpdatePrompt()
        {
            string common = $"{Key("M")} 지도   {Key("Esc")} 일시정지";
            prompt.text = session.IsSettled ? "오늘의 배달 결과를 확인하세요"
                : session.IsPaused ? $"{Key("Esc")} 계속하기"
                : session.IsCallOpen ? $"{Key("1")} {Key("2")} 콜 수락   ·   클릭 자세히 보기   ·   {Key("Esc")} 닫기"
                : session.IsMapOpen ? $"{Key("M")} {Key("Esc")} 지도 닫기   ·   지도를 보는 동안에도 시간이 흐릅니다"
                : session.IsChasingFood ? $"{Key("F")} 음식을 가져간 도둑을 쳐서 회수   ·   {common}"
                : session.IsRecoveringFood ? $"음식점에 가면 자동으로 다시 받습니다   ·   {common}"
                : session.Stage == DeliveryStage.Carrying ? $"{Key("F")} {session.ActiveOrder.CustomerName} 쳐서 전달   {Key("Space")} 점프   ·   {common}"
                : session.Stage == DeliveryStage.Pickup ? $"음식점에 가면 자동으로 수령합니다   ·   {common}"
                : session.CanOpenCall ? $"{Key("1")} 콜 확인   ·   {common}"
                : $"{Key("WASD")} 이동   {Key("F")} 주먹   {Key("Space")} 점프   ·   {common}";
        }

        // A key drawn as a small cap behind its letter.
        private static string Key(string key) => $"<mark=#FFFFFF33 padding=\"8,8,3,3\"><b>{key}</b></mark>";
    }
}
