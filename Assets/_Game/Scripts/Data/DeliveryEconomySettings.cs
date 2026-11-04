using System;
using UnityEngine;

namespace DeliveryRider
{
    // Trial copy: HTML v5.5 zone unlock and distance-pricing values.
    [CreateAssetMenu(menuName = "Delivery Rider/Economy Settings", fileName = "EconomySettings")]
    public sealed class DeliveryEconomySettings : ScriptableObject
    {
        [Serializable]
        public struct RewardTier
        {
            [Min(0)] public float maximumDistance;
            [Min(0)] public int reward;
            public string label;
        }

        [Header("Time limit = main-road distance / run speed x ratio + padding, rounded up")]
        [SerializeField, Min(0.1f)] private float runSpeed = 6f;
        [SerializeField, Min(0.1f)] private float limitRatio = 1.5f;
        [SerializeField, Min(0f)] private float limitPadding = 25f;
        [SerializeField, Min(1f)] private float limitRounding = 5f;
        [Header("Rewards")]
        [SerializeField, Min(0f)] private float minimumDistance = 300f;
        [SerializeField] private bool useDistanceReward = true;
        [SerializeField, Min(0)] private int rewardBase = 1500;
        [SerializeField, Min(0f)] private float rewardPerMeter = 8f;
        [Header("Zone unlocks (day 1 onward; last value repeats)")]
        [SerializeField] private string[] zoneOrder = { "C", "A", "B", "D" };
        [SerializeField, Min(1)] private int[] openCountByDay = { 1, 2, 3, 4 };
        [SerializeField, Min(0f)] private float[] minimumDistanceByOpenCount = { 120f, 220f, 300f, 300f };
        [SerializeField, Min(0f)] private float reuseExclusionDistance = 20f;
        [Header("Legacy reward tiers (also used as distance labels)")]
        [SerializeField] private RewardTier[] rewardTiers =
        {
            new RewardTier { maximumDistance = 350f, reward = 3000, label = "가까움" },
            new RewardTier { maximumDistance = 470f, reward = 4500, label = "중간" },
            new RewardTier { maximumDistance = 100000f, reward = 5500, label = "멀리" },
        };
        [SerializeField, Range(0f, 1f)] private float lateRatio = 0.6f;
        [Header("Run economy")]
        [SerializeField, Min(0)] private int startingDebt = 100000;
        [SerializeField, Min(0)] private int livingCost = 5000;
        [SerializeField] private bool addShortfallToDebt;
        [SerializeField, Min(0)] private int inlinePrice = 8000;
        [SerializeField, Min(1)] private int inlineSaleDay = 1;
        [SerializeField, Min(1f)] private float inlineSpeedMultiplier = 1.6f;
        [Tooltip("Lower than 1 gives the inline skates some inertia.")]
        [SerializeField, Range(0.1f, 1f)] private float inlineAccelerationMultiplier = 0.6f;
        [Header("Flavour text")]
        [SerializeField] private string[] customerNames = { "민지", "서준", "하은", "도윤", "지아", "시우", "유나", "준호", "수아", "현우" };
        [SerializeField] private string[] foodNames = { "떡볶이", "치킨", "짜장면", "김밥", "곱창", "햄버거", "피자", "국밥" };

        public float MinimumDistance => minimumDistance;
        public int StartingDebt => startingDebt;
        public int LivingCost => livingCost;
        public bool AddShortfallToDebt => addShortfallToDebt;
        public float ReuseExclusionDistance => reuseExclusionDistance;
        public int InlinePrice => inlinePrice;
        public int InlineSaleDay => inlineSaleDay;
        public float InlineSpeedMultiplier => inlineSpeedMultiplier;
        public float InlineAccelerationMultiplier => inlineAccelerationMultiplier;

        public System.Collections.Generic.List<string> ZonesForDay(int day)
            => DeliveryZoneRules.OpenZonesForDay(day, zoneOrder, openCountByDay);

        public float MinimumDistanceForOpenCount(int count)
            => minimumDistanceByOpenCount == null || minimumDistanceByOpenCount.Length == 0 ? minimumDistance
                : minimumDistanceByOpenCount[Mathf.Clamp(count - 1, 0, minimumDistanceByOpenCount.Length - 1)];

        public int Reward(float distance) => useDistanceReward
            ? Mathf.FloorToInt((rewardBase + rewardPerMeter * Mathf.Max(0f, distance)) / 100f + 0.5f) * 100
            : Tier(distance).reward;

        public float TimeLimit(float distance)
        {
            float raw = distance / runSpeed * limitRatio + limitPadding;
            return Mathf.Ceil(raw / limitRounding) * limitRounding;
        }

        public RewardTier Tier(float distance)
        {
            foreach (var tier in rewardTiers)
                if (distance <= tier.maximumDistance) return tier;
            return rewardTiers.Length > 0 ? rewardTiers[rewardTiers.Length - 1] : default;
        }

        // HTML rounds the distance reward to hundreds, then applies lateness to whole won.
        public int LateReward(int onTimeReward) => Mathf.FloorToInt(onTimeReward * lateRatio + 0.5f);
        public string RandomCustomer() => customerNames.Length == 0 ? "손님" : customerNames[UnityEngine.Random.Range(0, customerNames.Length)];
        public string RandomFood() => foodNames.Length == 0 ? "음식" : foodNames[UnityEngine.Random.Range(0, foodNames.Length)];
    }
}
