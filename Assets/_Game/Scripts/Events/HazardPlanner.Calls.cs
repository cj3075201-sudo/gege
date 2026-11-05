using System;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
using Unity.Profiling;
#endif

namespace DeliveryRider
{
    public sealed partial class HazardPlanner
    {
        [Header("HTML 콜별 추첨")]
        [SerializeField] private int[] activePerDay = { 5, 8, 10, 13, 13 };
        [SerializeField, Range(0f, 1f)] private float routeShare = .7f;
        [SerializeField, Min(0f)] private float routeRadius = 7f;
        [SerializeField, Range(0f, 1f)] private float typeGuarantee = .5f;
        [SerializeField, Range(0f, 1f)] private float stealChance = .5f;
        [SerializeField, Range(0f, 1f)] private float duckChance = .25f;
        [SerializeField, Min(0f)] private float visibilityHoldRadius = 25f;
        [Header("HTML 연쇄")]
        [SerializeField, Range(0f, 1f)] private float igniteChance = .8f;
        [SerializeField, Range(0f, 1f)] private float objectIgniteChance = .3f;
        [SerializeField, Min(0f)] private float igniteRadius = 40f;
        [Header("HTML 콤보와 오브젝트 간격")]
        [SerializeField] private bool combosEnabled = true;
        [SerializeField, Range(0f, 3f)] private float comboCountScale = 1f;
        [SerializeField, Min(0f)] private float objectMinimumSpacing = 5f;
        [SerializeField, Range(0f, 1f)] private float comboFirst = .35f;
        [Tooltip("Display the call's selected triggers before food pickup without allowing them to strike.")]
        [SerializeField] private bool showSelectedBeforePickup = true;
        [SerializeField,Range(0f,1f)] private float previewOpacity=.4f;
        [SerializeField,Min(0f)] private float previewFadeSeconds=.3f;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private static readonly ProfilerMarker RollSample = new ProfilerMarker("HazardPlanner.RollCall");
        public string LastRollAllocationUnit { get; private set; }
#endif
        private string allowedSteal;
        private DeliveryOrderOffer rolledOrder;
        private System.Random callRandom;
        private Camera visibilityCamera;
        private readonly List<HazardSlot> pendingSlots = new List<HazardSlot>();
        private readonly List<HazardSlot> callPool = new List<HazardSlot>();
        private readonly List<HazardSlot> nearPool = new List<HazardSlot>();
        private readonly Dictionary<string, int> callCounts = new Dictionary<string, int>();
        private readonly Dictionary<HazardSlot, float> rollKeys = new Dictionary<HazardSlot, float>();
        private readonly List<HazardSlot> group = new List<HazardSlot>();
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        public double LastRollMilliseconds { get; private set; }
        public long LastRollAllocatedBytes { get; private set; }
        public long LastRollHeapDeltaBytes { get; private set; }
#endif
        public int SelectedTriggers { get; private set; }
        public int NearSelectedTriggers { get; private set; }
        public int PendingSlotChanges => pendingSlots.Count;
        public int PlacedCombos { get; private set; }
        private int Route => rolledOrder != null && session.RouteTable != null
            ? rolledOrder.ShopIndex * session.RouteTable.DoorCount + rolledOrder.DoorIndex : -1;

        private bool NearRoute(HazardSlot slot) => candidates != null && candidates.RoutePaths.Count > 0
            ? candidates.RouteDistance(Route, slot.transform.position) <= routeRadius
            : slot.IsOnRoute(Route) && Mathf.Approximately(routeRadius, 7f);

        private void RefreshCall()
        {
            if (ReferenceEquals(rolledOrder, session.ActiveOrder)) return;
            rolledOrder = session.ActiveOrder;
            if (callRandom == null) callRandom = randomEachRun ? new System.Random() : new System.Random(seed);
            foreach (var slot in slots)
                if (slot != null && slot.Role == HazardRole.Trigger)
                {
                    slot.Selected = false; slot.Consumed = false; slot.NeedsCallReset = true;
                    if (!pendingSlots.Contains(slot)) pendingSlots.Add(slot);
                }
            SelectedTriggers = NearSelectedTriggers = 0;
            if (rolledOrder == null) return;
            RollCall();
        }

        private void RollCall()
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            using var sample = RollSample.Auto();
            using var allocation = ProfilerRecorder.StartNew(ProfilerCategory.Memory,"GC.Alloc",1,
                ProfilerRecorderOptions.StartImmediately|ProfilerRecorderOptions.SumAllSamplesInFrame|ProfilerRecorderOptions.CollectOnlyOnCurrentThread);
            long heapBefore = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
            var timer = System.Diagnostics.Stopwatch.StartNew();
#endif
            callPool.Clear(); nearPool.Clear(); callCounts.Clear(); rollKeys.Clear();
            foreach (var slot in slots)
            {
                if (slot.Role != HazardRole.Trigger || slot.Type == "celebrity" || slot.UnlockDay > day) continue;
                callPool.Add(slot);
                rollKeys[slot] = (float)callRandom.NextDouble() - (slot.Combo >= 0 ? comboFirst : 0f);
            }
            callPool.Sort((a, b) => rollKeys[a].CompareTo(rollKeys[b]));
            foreach (var slot in callPool) if (NearRoute(slot)) nearPool.Add(slot);
            var types = new HashSet<string>(); foreach (var slot in callPool) types.Add(slot.Type);
            int want = activePerDay.Length == 0 ? 0 : activePerDay[Mathf.Clamp(day - 1, 0, Mathf.Min(4, activePerDay.Length - 1))];
            want = Mathf.Min(want, callPool.Count);
            int cap = types.Count >= 4 ? Mathf.Max(2, Mathf.CeilToInt(want * .25f)) : want;
            string steal = callRandom.NextDouble() < stealChance ? (callRandom.Next(2) == 0 ? "theft" : "clone") : "";
            allowedSteal = steal;
            bool duck = callRandom.NextDouble() < duckChance;
            HazardSlot duckSlot = duck ? nearPool.Find(s => s.Type == "duck") : null;
            // User override: a successful duck roll selects ONE on-route slot, outside grouped activation.
            if (duckSlot != null && CallRoom(duckSlot)) Select(duckSlot);
            bool Eligible(HazardSlot s) => !s.Selected && (s.Type != "duck")
                && ((s.Type != "theft" && s.Type != "clone") || s.Type == steal && !callCounts.ContainsKey(steal));
            bool TurnOn(HazardSlot candidate)
            {
                group.Clear();
                if (candidate.Combo < 0) group.Add(candidate);
                else foreach (var other in callPool) if (other.Combo == candidate.Combo && Eligible(other)) group.Add(other);
                if (group.Count == 0 || SelectedTriggers + group.Count > want) return false;
                var additions = new Dictionary<string, int>();
                foreach (var member in group)
                {
                    if (!Eligible(member) || !CallRoom(member, group)) return false;
                    additions[member.Type] = additions.TryGetValue(member.Type, out int n) ? n + 1 : 1;
                }
                foreach (var entry in additions)
                    if ((callCounts.TryGetValue(entry.Key, out int n) ? n : 0) + entry.Value > cap
                        || (entry.Key == "theft" || entry.Key == "clone") && entry.Value > 1) return false;
                foreach (var member in group) Select(member);
                return true;
            }
            var tried = new HashSet<string>();
            foreach (var slot in nearPool)
                if (SelectedTriggers < want && tried.Add(slot.Type) && Eligible(slot) && ((slot.Type == "theft" || slot.Type == "clone") || callRandom.NextDouble() < typeGuarantee)) TurnOn(slot);
            int routeWant = Mathf.RoundToInt(want * routeShare);
            foreach (var slot in nearPool) if (NearSelectedTriggers < routeWant && Eligible(slot)) TurnOn(slot);
            foreach (var slot in callPool) if (SelectedTriggers < want && Eligible(slot)) TurnOn(slot);
            // Whole groups can leave one or two spare seats: fill only with standalone slots, preserving group integrity.
            if (SelectedTriggers < want)
                foreach (var slot in callPool)
                    if (SelectedTriggers < want && slot.Combo < 0 && Eligible(slot)) TurnOn(slot);
            ImproveRouteCoverage(want,cap,steal);
            // rollSpec: this project implements celebrity; the HTML budget 6/9/12/16 easily covers its 5-slot pool.
            var special = new List<HazardSlot>();
            foreach (var slot in slots) if (slot.Type == "celebrity" && slot.UnlockDay <= day) special.Add(slot);
            foreach (var slot in special) rollKeys[slot] = SpecDistance(slot);
            special.Sort((a,b) => rollKeys[a].CompareTo(rollKeys[b]));
            int specialWant = new[] {6,9,12,16}[Mathf.Clamp(day - 1, 0, 3)];
            for (int i = 0; i < special.Count && i < specialWant; i++){special[i].Selected = true;special[i].StrikeAllowed=CanStrike(special[i])&&!special[i].NeedsCallReset;}
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            timer.Stop(); LastRollMilliseconds = timer.Elapsed.TotalMilliseconds;
            LastRollAllocationUnit = allocation.Valid ? allocation.UnitType.ToString() : "Unavailable";
            LastRollAllocatedBytes = LastRollAllocationUnit == "Bytes" ? allocation.CurrentValue : -1;
            LastRollHeapDeltaBytes = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() - heapBefore;
#endif
        }

        private float SpecDistance(HazardSlot slot) => candidates != null
            ? candidates.RouteDistance(Route, slot.transform.position, true) + (float)callRandom.NextDouble() * 4f : 0f;

        // Keep complete bundles while replacing off-route draws where the strict count/cap/spacing rules permit it.
        private void ImproveRouteCoverage(int want,int cap,string steal)
        {
            int target=Mathf.RoundToInt(want*routeShare);
            if(NearSelectedTriggers>=target)return;
            var visited=new HashSet<int>();var units=new List<List<HazardSlot>>();
            foreach(var slot in callPool)
            {
                if(!slot.Selected||slot.Type=="duck")continue;
                int key=slot.Combo>=0?slot.Combo:slot.GetInstanceID();if(!visited.Add(key))continue;
                var unit=new List<HazardSlot>();
                foreach(var other in callPool)if(other.Selected&&(other==slot||slot.Combo>=0&&other.Combo==slot.Combo))unit.Add(other);
                if(unit.Exists(s=>!NearRoute(s)))units.Add(unit);
            }
            foreach(var unit in units)
            {
                if(NearSelectedTriggers>=target)break;
                int oldNear=unit.FindAll(NearRoute).Count;
                foreach(var old in unit)Unselect(old);
                var added=new List<HazardSlot>();
                foreach(var candidate in nearPool)
                {
                    if(added.Count>=unit.Count)break;
                    if(candidate.Selected||candidate.Combo>=0||candidate.Type=="duck"||!CallRoom(candidate))continue;
                    if(callCounts.TryGetValue(candidate.Type,out int count)&&count>=cap)continue;
                    if((candidate.Type=="theft"||candidate.Type=="clone")&&(candidate.Type!=steal||callCounts.ContainsKey("theft")||callCounts.ContainsKey("clone")))continue;
                    Select(candidate);added.Add(candidate);
                }
                if(added.Count==unit.Count&&added.Count>oldNear)continue;
                foreach(var item in added)Unselect(item);
                foreach(var old in unit)Select(old);
            }
        }
        private void Unselect(HazardSlot slot)
        {
            if(!slot.Selected)return;
            slot.Selected=false;slot.StrikeAllowed=false;SelectedTriggers--;if(NearRoute(slot))NearSelectedTriggers--;
            if(callCounts.TryGetValue(slot.Type,out int count)){if(count<=1)callCounts.Remove(slot.Type);else callCounts[slot.Type]=count-1;}
        }

        private bool CallRoom(HazardSlot candidate, List<HazardSlot> members = null)
        {
            foreach(var other in slots)
            {
                if(other == candidate || other.Consumed || other.Role == HazardRole.Trigger && !other.Selected) continue;
                float gap = candidate.Combo >= 0 && other.Combo == candidate.Combo ? 2.9f : 9f;
                if(Vector3.ProjectOnPlane(candidate.transform.position-other.transform.position,Vector3.up).sqrMagnitude < gap*gap) return false;
            }
            if(members!=null)foreach(var other in members)
            {
                if(other==candidate)continue;
                if(Vector3.ProjectOnPlane(candidate.transform.position-other.transform.position,Vector3.up).sqrMagnitude < 2.9f*2.9f)return false;
            }
            return true;
        }

        private void Select(HazardSlot slot)
        {
            if (slot.Selected) return;
            slot.Selected = true; slot.StrikeAllowed=CanStrike(slot)&&!slot.NeedsCallReset&&!slot.Consumed; SelectedTriggers++;
            if (NearRoute(slot)) NearSelectedTriggers++;
            callCounts[slot.Type] = callCounts.TryGetValue(slot.Type, out int n) ? n + 1 : 1;
            if (!pendingSlots.Contains(slot)) pendingSlots.Add(slot);
        }

        private bool DesiredVisible(HazardSlot slot) => slot.Selected && !slot.Consumed && CanStrike(slot);

        private void ApplyPendingSlots()
        {
            // Delivery stage changes affect visibility as well as the shared gate. Active is never used as authority.
            foreach (var slot in slots)
            {
                if (slot == null || slot.Role == HazardRole.Object) continue;
                slot.TickGhostFade();
                bool active = DesiredVisible(slot);
                slot.StrikeAllowed = active;
                bool preview=slot.Selected&&!slot.Consumed&&showSelectedBeforePickup
                    &&session.ActiveOrder!=null&&session.Stage==DeliveryStage.Pickup;
                if(preview)
                {
                    pendingSlots.Remove(slot);slot.NeedsCallReset=false;
                    slot.SetPreview(previewOpacity);
                    continue;
                }
                if(slot.IsPreviewVisible)
                {
                    pendingSlots.Remove(slot);slot.NeedsCallReset=false;
                    if(active)slot.ActivateFromPreview(previewFadeSeconds);
                    else slot.SetRuntimeActive(false);
                    continue;
                }
                if((session.ActiveOrder==null||session.Stage==DeliveryStage.Complete)&&!active&&slot.RuntimeActive)
                {
                    pendingSlots.Remove(slot);slot.NeedsCallReset=false;slot.SetRuntimeActive(false);
                    continue;
                }
                slot.StrikeAllowed=active&&!slot.NeedsCallReset;
                if ((slot.RuntimeActive != active || slot.NeedsCallReset) && !pendingSlots.Contains(slot)) pendingSlots.Add(slot);
            }
            if (visibilityCamera == null) visibilityCamera = Camera.main;
            for (int i = pendingSlots.Count - 1; i >= 0; i--)
            {
                var slot = pendingSlots[i];
                if (slot == null) { pendingSlots.RemoveAt(i); continue; }
                bool active = DesiredVisible(slot);
                if (slot.RuntimeActive == active && !slot.NeedsCallReset) { pendingSlots.RemoveAt(i); continue; }
                if (rider != null && (slot.transform.position - rider.transform.position).sqrMagnitude < visibilityHoldRadius * visibilityHoldRadius)
                {
                    var viewport = visibilityCamera != null ? visibilityCamera.WorldToViewportPoint(slot.transform.position + Vector3.up) : Vector3.one * .5f;
                    if (visibilityCamera == null || viewport.z > 0f && viewport.x >= -.1f && viewport.x <= 1.1f && viewport.y >= -.1f && viewport.y <= 1.1f)
                    { if(!active||slot.NeedsCallReset)slot.PauseKeepingVisuals(); continue; }
                }
                // Teardown may change food ownership and synchronously reenter through session.Changed.
                // Remove the queue entry and finish its state before invoking component callbacks.
                var order=rolledOrder;
                bool reset=slot.NeedsCallReset;slot.NeedsCallReset=false;
                pendingSlots.RemoveAt(i);
                slot.StrikeAllowed = slot.Selected && CanStrike(slot) && !slot.Consumed;
                slot.SetRuntimeActive(active,reset);
                if(!ReferenceEquals(order,rolledOrder))return;
                i=Mathf.Min(i,pendingSlots.Count);
            }
        }

        private void Ignite(UnityEngine.Object source)
        {
            if (!DeliveryInProgress(session) || !(source is Component component)) return;
            var sourceSlot = component.GetComponentInParent<HazardSlot>(true);
            if (sourceSlot == null || callRandom == null || callRandom.NextDouble() >= (sourceSlot.Role == HazardRole.Object ? objectIgniteChance : igniteChance)) return;
            HazardSlot closest = null; float best = igniteRadius * igniteRadius;
            foreach (var slot in slots)
            {
                if (slot.Role != HazardRole.Trigger || slot.Type == "celebrity" || slot.Type == "duck" || slot.Selected || slot.Consumed || slot.UnlockDay > day) continue;
                if ((slot.Type == "theft" || slot.Type == "clone") && (slot.Type != allowedSteal
                    || callCounts.ContainsKey("theft") || callCounts.ContainsKey("clone"))) continue;
                float distance = (slot.transform.position - sourceSlot.transform.position).sqrMagnitude;
                if (distance < best && CallRoom(slot)) { best = distance; closest = slot; }
            }
            if (closest != null) { Select(closest); closest.StrikeAllowed = CanStrike(closest); }
        }

        public bool TryConsumeSlot(Vector3 position, float radius, HazardSlot except)
        {
            HazardSlot closest = null; float best = radius * radius;
            foreach (var slot in slots)
            {
                if (slot == except || slot.Consumed || slot.Role != HazardRole.Trigger || !slot.Selected || !slot.StrikeAllowed) continue;
                float distance = (slot.transform.position - position).sqrMagnitude;
                if (distance < best) { best = distance; closest = slot; }
            }
            if (closest == null) return false;
            closest.Consumed = true; closest.StrikeAllowed = false;
            if (!pendingSlots.Contains(closest)) pendingSlots.Add(closest);
            return true;
        }
    }
}
