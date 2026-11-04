using System;
using System.Text;
using UnityEngine;

namespace DeliveryRider
{
    // Observes the public session API; its totals live for one RunProgress, across daily scene reloads.
    public sealed class RunStats : MonoBehaviour
    {
        private static RunProgress run;
        private static int completed, late, inlineBuyDay;
        private static double deliverySeconds, elapsed, income;
        private static bool hadInline;
        private static float peakSpeed;

        private DeliverySession session;
        private RiderMotor rider;
        private DeliveryDaySettings settings;
        private int resultIndex, observedDay;
        private long lastIncome;
        private double dayStartedAt, todayElapsed, airborneSeconds;
        private double todayLength;
        private bool clockObserved;
        private int hazardHits;

        public int Completed => completed;
        public int Late => late;
        public double AverageSeconds => completed > 0 ? deliverySeconds / completed : 0;
        public double LateRatio => completed > 0 ? (double)late / completed : 0;
        public int InlineBuyDay => inlineBuyDay;
        public double Elapsed => elapsed;
        public double Income => income;
        public double TodayElapsed => todayElapsed;
        public int TodayHazardHits => hazardHits;
        public double AirborneRatio => todayElapsed > 0 ? airborneSeconds / todayElapsed : 0;
        public float PeakSpeed => peakSpeed;
        public double DayLength => settings != null ? settings.DurationSeconds : 300;
        // HTML credits each delivery immediately; Unity transfers today's income to Progress only at settlement.
        public double CashIncludingToday => run == null ? 0 : (double)run.Cash +
            (session != null && !session.IsSettled && !run.DayClosed && run.Day == observedDay ? session.Cash : 0);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            run = null;
            completed = late = inlineBuyDay = 0;
            deliverySeconds = elapsed = income = 0;
            hadInline = false;
            peakSpeed = 0;
        }

        public void Initialize(DeliverySession observed, RiderMotor motor, DeliveryDaySettings daySettings)
        {
            session = observed;
            rider = motor;
            settings = daySettings;
            todayLength = DayLength;
            if (rider != null) rider.HazardHit += OnHazardHit;
        }

        private void OnHazardHit(UnityEngine.Object source)
        {
            if (session != null && !session.IsSettled) hazardHits++;
        }

        private void LateUpdate() => Poll();

        private void OnDestroy()
        {
            Poll();
            if (rider != null) rider.HazardHit -= OnHazardHit;
        }

        public void Poll()
        {
            if (session == null || session.Progress == null) return;
            if (!ReferenceEquals(run, session.Progress))
            {
                Reset();
                run = session.Progress;
                hadInline = run.HasInline;
                resultIndex = 0;
                lastIncome = 0;
                todayElapsed = airborneSeconds = 0;
                clockObserved = false;
                hazardHits = 0;
                observedDay = run.Day;
            }
            if (observedDay == 0) observedDay = run.Day;
            var results = session.Results;
            while (resultIndex < results.Count)
            {
                var result = results[resultIndex++];
                completed++;
                if (result.WasLate) late++;
                deliverySeconds += result.DurationSeconds;
            }
            income += Math.Max(0, session.Cash - lastIncome);
            lastIncome = session.Cash;
            if (run.HasInline && !hadInline) inlineBuyDay = observedDay;
            hadInline = run.HasInline;

            if (session.HasDayStarted)
            {
                if (!clockObserved)
                {
                    // DayProgress stops at closing; tracking the start keeps overtime in the real elapsed time.
                    dayStartedAt = Time.timeAsDouble - (todayLength - session.DayRemainingSeconds);
                    clockObserved = true;
                }
                double now = session.IsSettled ? session.SettlementElapsedSeconds : Time.timeAsDouble - dayStartedAt;
                double delta = Math.Max(0, now - todayElapsed);
                elapsed += delta;
                todayElapsed += delta;
                if (rider != null && rider.IsAccidentAirborne && !session.IsPaused) airborneSeconds += delta;
            }
            if (rider != null) peakSpeed = Mathf.Max(peakSpeed, rider.HorizontalSpeed);
        }

        public RunPace Pace => RunStatsMath.Calculate(income, elapsed, DayLength,
            session != null && session.Economy != null ? session.Economy.LivingCost : 0,
            run != null ? run.Debt : 0, CashIncludingToday);

        public string Describe()
        {
            Poll();
            if (session == null || session.Progress == null) return "결과: 진행 정보 없음";
            var pace = Pace;
            var text = new StringBuilder();
            text.AppendLine($"이번 판: {completed}건 · 평균 {AverageSeconds:0.0}초 · 지각 {LateRatio:P0} · 진행 {pace.PlayedDays:0.00}일");
            text.AppendLine(inlineBuyDay > 0 ? $"인라인: {inlineBuyDay}일차 끝 구매" : hadInline ? "인라인: 시작 시 보유 (구매 일차 기록 없음)" : "인라인: 미구매");
            text.AppendLine($"오늘: {observedDay}일차 · {session.CompletedCount}건 · 수입 {session.Cash:N0}원 · {todayElapsed:0} / {todayLength:0}초");
            text.AppendLine($"하루 수입 페이스: {pace.DailyIncome:N0}원 · 생활비 뺀 순수입 {pace.DailyNet:N0}원");
            text.AppendLine($"누적 수입 {income:N0}원 · 실제 경과 {elapsed:0.0}초");
            text.AppendLine($"현금(오늘 수입 포함) {CashIncludingToday:N0}원 · 빚 {run.Debt:N0}원");
            text.AppendLine(pace.DailyNet > 0
                ? $"빚 상환 예상: 남은 {pace.Remaining:N0}원 → {pace.DaysLeft:0.0}일 더 · 총 {pace.TotalDays:0.0}일 · 약 {pace.TotalMinutes:0}분"
                : "빚 상환 예상: 이 페이스로는 못 갚음 (순수입 0 이하)");
            if (rider != null)
            {
                text.AppendLine($"현재 속도: {rider.HorizontalSpeed:0.0} m/s · {rider.HorizontalSpeed * 3.6f:0.0} km/h");
                text.AppendLine($"최고 속도(설정): {rider.Speed:0.0} m/s · 실측 최고 {peakSpeed:0.0} m/s · {(rider.HasInline ? "인라인" : "달리기")}");
            }
            text.Append($"함정: 오늘 {hazardHits}회 · 사고 체공 {airborneSeconds:0.0}초 ({AirborneRatio:P1})");
            return text.ToString();
        }
    }
}
