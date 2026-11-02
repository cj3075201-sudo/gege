using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DeliveryRider
{
    public enum DeliveryStage { Available, Pickup, Carrying, Complete, FoodLost }
    public enum WorkDayPhase { Working, Overtime, Settlement }

    public sealed class DeliverySession : MonoBehaviour
    {
        [SerializeField] private Transform player;
        [SerializeField, Range(1, 3)] private int candidateCount = 2;
        [Tooltip("The day clock starts once the player walks this far from the start point. 0 starts it immediately.")]
        [SerializeField, Min(0f)] private float homeLeaveDistance = 6f;
        [SerializeField] private GameObject foodBag;
        [SerializeField] private float pickupRange = 2.8f;
        [SerializeField] private DeliveryDaySettings daySettings;
        [Header("Calls")]
        [SerializeField] private DeliveryRouteTable routeTable;
        [SerializeField] private DeliveryEconomySettings economy;
        [Tooltip("Pickup points in route table shop order.")]
        [SerializeField] private Transform[] shopPoints = Array.Empty<Transform>();
        [Tooltip("Recipient stand points in route table door order.")]
        [SerializeField] private Transform[] doorPoints = Array.Empty<Transform>();
        [SerializeField] private DeliveryResident recipient;
        [SerializeField, Min(0f)] private float recipientHeight = 1.08f;
        [SerializeField] private string startZone = "C";
        public event Action Changed;
        public event Action<float> PigeonFlockObscured;
        private float pigeonFlockObscuredUntil;

        public bool TryObscureWithPigeons(float seconds)
        {
            if (IsPaused || IsMapOpen || IsSettled || Time.time < pigeonFlockObscuredUntil) return false;
            float duration = Mathf.Max(0.1f, seconds);
            pigeonFlockObscuredUntil = Time.time + duration;
            PigeonFlockObscured?.Invoke(duration);
            return true;
        }
        public DeliveryStage Stage { get; private set; }
        public bool IsPaused { get; private set; }
        // The call screen shares the map panel; IsMapOpen means that panel is shown in either mode.
        public bool IsMapOpen => mapOpen || IsCallOpen;
        public bool IsCallOpen { get; private set; }
        private bool mapOpen;
        public Transform Player => player;
        public int CompletedCount { get; private set; }
        public WorkDayPhase DayPhase { get; private set; }
        // Today's earnings; carried cash and debt live in Progress.
        public long Cash { get; private set; }
        public RunProgress Progress { get; private set; }
        public DeliveryEconomySettings Economy => economy;
        public int OnTimeCount { get; private set; }
        public int LateCount => CompletedCount - OnTimeCount;
        public DeliveryResult LastResult { get; private set; }
        public double SettlementElapsedSeconds { get; private set; }
        private readonly List<DeliveryResult> results = new List<DeliveryResult>();
        public IReadOnlyList<DeliveryResult> Results => results;
        private double dayStartedAt;
        private Vector3 homePosition;
        public bool HasDayStarted { get; private set; }
        private double dayDuration;
        private double pickedUpTime;
        private double orderDeadline;
        private double activeTimeLimit;
        // The delivery clock starts at the first pickup; re-pickups after theft keep it running.
        public bool HasPickedUp { get; private set; }
        private int activeOnTimeReward;
        private int activeLateReward;
        private bool leavingScene;
        public bool IsSettled => DayPhase == WorkDayPhase.Settlement;
        public bool IsDayClosed => DayPhase != WorkDayPhase.Working || HasDayStarted && Time.timeAsDouble >= dayStartedAt + dayDuration;
        public double DayRemainingSeconds => HasDayStarted ? Math.Max(0d, dayStartedAt + dayDuration - Time.timeAsDouble) : dayDuration;
        public double DayProgress => dayDuration <= 0d ? 1d : 1d - DayRemainingSeconds / dayDuration;
        public double OrderRemainingSeconds => ActiveOrder == null ? 0d : HasPickedUp ? Math.Max(0d, orderDeadline - Time.timeAsDouble) : activeTimeLimit;
        public double OrderOverdueSeconds => ActiveOrder == null || !HasPickedUp ? 0d : Math.Max(0d, Time.timeAsDouble - orderDeadline);
        public bool IsOrderLate => ActiveOrder != null && HasPickedUp && Time.timeAsDouble > orderDeadline;
        private readonly List<DeliveryOrderOffer> candidates = new List<DeliveryOrderOffer>();
        private int nextOrderNumber = 1;
        public IReadOnlyList<DeliveryOrderOffer> Candidates => candidates;
        public DeliveryOrderOffer SelectedOrder { get; private set; }
        public DeliveryOrderOffer ActiveOrder { get; private set; }
        public DeliveryOrderOffer LastCompletedOrder { get; private set; }
        public float AcceptedAt { get; private set; }
        public bool CanOpenCall => !IsPaused && !IsSettled && !IsDayClosed && ActiveOrder == null && (Stage == DeliveryStage.Available || Stage == DeliveryStage.Complete);
        public bool CanChooseOrder => IsCallOpen && CanOpenCall;
        private RiderMotor rider;
        // Calls come from the zone where the last delivery ended and never send the rider straight back.
        public string CurrentZone { get; private set; }
        private string previousZone;
        private readonly HashSet<int> usedDoors = new HashSet<int>();
        private readonly List<string> zonePool = new List<string>(4);
        private readonly List<int> doorPool = new List<int>(32);
        private readonly List<int> shopPool = new List<int>(4);
        public DeliveryRouteTable RouteTable => routeTable;
        private bool UsesGeneratedCalls => routeTable != null && routeTable.IsValid && economy != null && recipient != null
            && shopPoints.Length == routeTable.ShopCount && doorPoints.Length == routeTable.DoorCount;
        private Transform recoveryTarget;
        private bool awaitingThief;
        public bool IsRecoveringFood => Stage == DeliveryStage.FoodLost;
        public bool IsChasingFood => IsRecoveringFood && recoveryTarget != null;
        public bool CanMove => !IsPaused && !IsMapOpen && !IsSettled && (rider == null || !rider.IsRecovering);
        public Transform Target => ActiveOrder == null ? null : IsChasingFood ? recoveryTarget :
            Stage == DeliveryStage.Pickup || IsRecoveringFood ? ActiveOrder.PickupPoint : ActiveOrder.Recipient.transform;
        public float TargetDistance => Target == null ? 0f : Vector3.Distance(player.position, Target.position);
        public bool CanPickup => (Stage == DeliveryStage.Pickup || (IsRecoveringFood && !IsChasingFood)) && CanMove && ActiveOrder != null
            && Vector3.Distance(player.position, ActiveOrder.PickupPoint.position) <= pickupRange;

        private void Awake()
        {
            rider = player.GetComponent<RiderMotor>();
            var controller = SceneController.Instance;
            Progress = controller != null ? controller.Progress : null;
            if (Progress == null)
            {
                // Playing the game scene directly starts a fresh day-1 run.
                Progress = new RunProgress(1, economy != null ? economy.StartingDebt : 0);
                if (controller != null) controller.Progress = Progress;
            }
            if (Progress.HasInline && economy != null && rider != null)
                rider.EquipInline(economy.InlineSpeedMultiplier, economy.InlineAccelerationMultiplier);
            homePosition = player.position;
            CurrentZone = startZone;
            dayDuration = daySettings != null ? daySettings.DurationSeconds : 300d;
            if (homeLeaveDistance <= 0f) StartDay();
            FillCandidates(); SelectedOrder = candidates.Count > 0 ? candidates[0] : null;
        }
        private void Start() { ApplyTimeScale(); Notify(); }
        private void Update()
        {
            if (!HasDayStarted && (player.position - homePosition).sqrMagnitude >= homeLeaveDistance * homeLeaveDistance) StartDay();
            RefreshDayPhase();
            if (IsRecoveringFood && awaitingThief && (recoveryTarget == null || !recoveryTarget.gameObject.activeInHierarchy))
                RequireReplacement(recoveryTarget);
        }

        public bool TryLoseFood(Transform thief)
        {
            RefreshDayPhase();
            if (!CanMove || ActiveOrder == null || Stage != DeliveryStage.Carrying) return false;
            recoveryTarget = thief;
            awaitingThief = thief != null;
            Stage = DeliveryStage.FoodLost;
            Notify();
            return true;
        }

        public bool TryRecoverFood(Transform thief)
        {
            if (!CanMove || !IsChasingFood || recoveryTarget != thief) return false;
            recoveryTarget = null;
            awaitingThief = false;
            Stage = DeliveryStage.Carrying;
            Notify();
            return true;
        }

        public void RequireReplacement(Transform thief)
        {
            if (!IsRecoveringFood || recoveryTarget != thief) return;
            recoveryTarget = null;
            awaitingThief = false;
            Notify();
        }

        private void StartDay()
        {
            HasDayStarted = true;
            dayStartedAt = Time.timeAsDouble;
            Notify();
        }

        // Called by both Update and entry points, preventing frame-order races at closing time.
        private void RefreshDayPhase()
        {
            if (DayPhase != WorkDayPhase.Working || !IsDayClosed) return;
            candidates.Clear();
            SelectedOrder = null;
            if (ActiveOrder != null) DayPhase = WorkDayPhase.Overtime;
            else EnterSettlement();
            Notify();
        }

        private void EnterSettlement()
        {
            if (IsSettled) return;
            DayPhase = WorkDayPhase.Settlement;
            SettlementElapsedSeconds = HasDayStarted ? Time.timeAsDouble - dayStartedAt : 0d;
            Progress.CloseDay(Cash, CompletedCount, economy != null ? economy.LivingCost : 0);
            IsPaused = false;
            IsCallOpen = false;
            mapOpen = false;
            candidates.Clear();
            SelectedOrder = null;
            ApplyTimeScale();
        }

        public void SelectCandidate(int index)
        {
            RefreshDayPhase();
            if (!CanChooseOrder || index < 0 || index >= candidates.Count) return;
            SelectedOrder = candidates[index];
            Notify();
        }

        public void AcceptOrder() => TryAcceptOrder(SelectedOrder);

        public bool TryAcceptOrder(DeliveryOrderOffer offer)
        {
            RefreshDayPhase();
            if (!CanChooseOrder || offer == null || !offer.IsValid || !candidates.Contains(offer)) return false;
            ActiveOrder = offer;
            // The offers that were not taken are dropped, so the next call screen is built in the zone where this
            // delivery ends rather than offering a stale call from the old zone.
            candidates.Clear();
            SelectedOrder = null;
            AcceptedAt = Time.time;
            HasPickedUp = false;
            activeTimeLimit = Math.Max(1d, offer.TimeLimitSeconds);
            activeOnTimeReward = Mathf.Max(0, offer.OnTimeReward);
            activeLateReward = Mathf.Max(0, offer.LateReward);
            offer.Recipient.AssignHome(offer.DestinationPoint.position + Vector3.up * recipientHeight, offer.DestinationPoint.rotation);
            Stage = DeliveryStage.Pickup;
            IsCallOpen = false;
            mapOpen = false;
            ApplyTimeScale();
            Notify();
            return true;
        }

        public bool TryPickup()
        {
            RefreshDayPhase();
            if (!CanPickup) return false;
            recoveryTarget = null;
            awaitingThief = false;
            if (!HasPickedUp)
            {
                HasPickedUp = true;
                pickedUpTime = Time.timeAsDouble;
                orderDeadline = pickedUpTime + activeTimeLimit;
            }
            Stage = DeliveryStage.Carrying;
            Notify();
            return true;
        }

        public bool TryDeliver(DeliveryResident recipient)
        {
            RefreshDayPhase();
            if (!CanMove || Stage != DeliveryStage.Carrying || ActiveOrder == null || recipient != ActiveOrder.Recipient) return false;
            bool late = IsOrderLate;
            int reward = late ? activeLateReward : activeOnTimeReward;
            var result = new DeliveryResult(ActiveOrder, Time.timeAsDouble - pickedUpTime, late, reward);
            LastCompletedOrder = ActiveOrder;
            if (ActiveOrder.DoorIndex >= 0)
            {
                usedDoors.Add(ActiveOrder.DoorIndex);
                previousZone = ActiveOrder.PickupZone;
                CurrentZone = ActiveOrder.DestinationZone;
            }
            ActiveOrder = null;
            Stage = DeliveryStage.Complete;
            CompletedCount++;
            if (!late) OnTimeCount++;
            LastResult = result;
            results.Add(result);
            Cash += reward;
            if (IsDayClosed) EnterSettlement();
            else
            {
                // The call screen is opened manually so the flying recipient stays in view.
                FillCandidates();
                SelectedOrder = candidates.Count > 0 ? candidates[0] : null;
            }
            Notify();
            return true;
        }

        private void FillCandidates()
        {
            if (IsDayClosed) return;
            if (!UsesGeneratedCalls)
            {
                Debug.LogError("DeliverySession needs a route table, economy settings, recipient and matching shop/door points.", this);
                return;
            }
            FillGeneratedCandidates();
        }

        // Based on the v5.2 trial: one call per other zone (not the zone the rider just came from), doors not reused
        // within the day, main-road distance pricing. Each call picks up at a different shop of the current zone,
        // nearest first, so the call screen offers distinct shops.
        private void FillGeneratedCandidates()
        {
            if (candidates.Count > 0) return;
            ShopsByDistance(CurrentZone);
            if (shopPool.Count == 0) return;
            zonePool.Clear();
            for (int i = 0; i < routeTable.DoorCount; i++)
            {
                string zone = routeTable.DoorZone(i);
                if (zone != CurrentZone && zone != previousZone && !zonePool.Contains(zone)) zonePool.Add(zone);
            }
            while (zonePool.Count > candidateCount) zonePool.RemoveAt(UnityEngine.Random.Range(0, zonePool.Count));
            for (int z = 0; z < zonePool.Count; z++)
            {
                string zone = zonePool[z];
                int shop = shopPool[z % shopPool.Count];
                int door = PickDoor(shop, zone);
                if (door < 0) continue;
                float distance = routeTable.ShopToDoor(shop, door);
                var tier = economy.Tier(distance);
                candidates.Add(new DeliveryOrderOffer(nextOrderNumber++, shopPoints[shop], recipient, doorPoints[door],
                    routeTable.ShopName(shop), economy.RandomCustomer(), economy.RandomFood(), tier.reward, economy.LateReward(tier.reward),
                    economy.TimeLimit(distance), distance, tier.label, routeTable.ShopZone(shop), zone, shop, door));
            }
        }

        // The zone's shops, nearest to the rider first.
        private void ShopsByDistance(string zone)
        {
            shopPool.Clear();
            for (int i = 0; i < routeTable.ShopCount; i++)
                if (routeTable.ShopZone(i) == zone && shopPoints[i] != null) shopPool.Add(i);
            var from = player.position;
            shopPool.Sort((a, b) => (shopPoints[a].position - from).sqrMagnitude.CompareTo((shopPoints[b].position - from).sqrMagnitude));
        }

        private int PickDoor(int shop, string zone)
        {
            doorPool.Clear();
            for (int pass = 0; pass < 2 && doorPool.Count == 0; pass++)
                for (int i = 0; i < routeTable.DoorCount; i++)
                    if (routeTable.DoorZone(i) == zone && doorPoints[i] != null && (pass == 1 || !usedDoors.Contains(i))) doorPool.Add(i);
            if (doorPool.Count == 0) return -1;
            doorPool.RemoveAll(i => routeTable.ShopToDoor(shop, i) < economy.MinimumDistance);
            if (doorPool.Count == 0)
            {
                // Every door is too close: fall back to the two farthest, as the trial build does.
                for (int i = 0; i < routeTable.DoorCount; i++)
                    if (routeTable.DoorZone(i) == zone && doorPoints[i] != null) doorPool.Add(i);
                doorPool.Sort((a, c) => routeTable.ShopToDoor(shop, c).CompareTo(routeTable.ShopToDoor(shop, a)));
                if (doorPool.Count > 2) doorPool.RemoveRange(2, doorPool.Count - 2);
            }
            return doorPool[UnityEngine.Random.Range(0, doorPool.Count)];
        }

        public void TogglePause()
        {
            RefreshDayPhase();
            if (IsSettled) return;
            IsPaused = !IsPaused;
            ApplyTimeScale();
            Notify();
        }

        // The map keeps the day running; only the call screen stops time.
        public void ToggleMap()
        {
            RefreshDayPhase();
            if (IsPaused || IsSettled) return;
            if (IsCallOpen) { CloseCall(); return; }
            mapOpen = !mapOpen;
            Notify();
        }

        public bool OpenCall()
        {
            RefreshDayPhase();
            if (IsCallOpen || !CanOpenCall) return false;
            if (candidates.Count == 0) FillCandidates();
            if (candidates.Count == 0) return false;
            if (SelectedOrder == null || !candidates.Contains(SelectedOrder)) SelectedOrder = candidates[0];
            IsCallOpen = true;
            mapOpen = false;
            ApplyTimeScale();
            Notify();
            return true;
        }

        public void CloseCall()
        {
            if (!IsCallOpen) return;
            IsCallOpen = false;
            ApplyTimeScale();
            Notify();
        }

        // Key 1 opens the call screen; inside it, keys 1 and 2 accept that slot.
        public void HandleCallKey(int slot)
        {
            if (IsPaused || IsSettled) return;
            if (!IsCallOpen) { if (slot == 0) OpenCall(); return; }
            if (slot >= 0 && slot < candidates.Count) TryAcceptOrder(candidates[slot]);
        }

        public void HandleEscape()
        {
            if (IsPaused) TogglePause();
            else if (IsCallOpen) CloseCall();
            else if (mapOpen) ToggleMap();
            else TogglePause();
        }

        private void ApplyTimeScale() => Time.timeScale = IsPaused || IsCallOpen || IsSettled ? 0f : 1f;


        // Settlement actions. RunProgress applies the money rules; the session is the only caller.
        public bool CanBuyInline => economy != null && Progress.CanBuyInline(economy.InlinePrice, economy.InlineSaleDay);

        public bool TryBuyInline()
        {
            if (!IsSettled || !CanBuyInline || !Progress.TryBuyInline(economy.InlinePrice, economy.InlineSaleDay)) return false;
            Notify();
            return true;
        }

        public bool TryRepayDebt()
        {
            if (!IsSettled || !Progress.TryRepay()) return false;
            RunProgress.DeleteSave();
            Notify();
            return true;
        }

        // Closing the settlement screen always saves the start of the next day, whether the player continues or quits.
        public void ContinueToNextDay() => LeaveSettlement(ESceneType.GameScene);
        public void SaveAndLeaveToTitle() => LeaveSettlement(ESceneType.Title);

        private void LeaveSettlement(ESceneType destination)
        {
            if (!IsSettled || leavingScene) return;
            leavingScene = true;
            if (!Progress.IsEnded)
            {
                Progress.StartNextDay();
                Progress.Save();
            }
            else if (SceneController.Instance != null) SceneController.Instance.Progress = null;
            // 정산 화면에서 멈춘 시간을 다시 흐르게 한 뒤 전역 매니저에게 씬 전환을 맡긴다.
            Time.timeScale = 1f;
            SceneController.Instance.NextScene(Progress.IsEnded ? ESceneType.Title : destination);
        }

        private void Notify()
        {
            foodBag.SetActive(Stage == DeliveryStage.Carrying);
            bool canLook = !IsPaused && !IsMapOpen && !IsSettled;
            Cursor.lockState = canLook ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !canLook;
            Changed?.Invoke();
        }

        private void OnDestroy()
        {
            Time.timeScale = 1f;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }
    }
}
