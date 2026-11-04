using System;

namespace DeliveryRider
{
    public readonly struct RunPace
    {
        public readonly double DailyIncome, DailyNet, Remaining, DaysLeft, PlayedDays, TotalDays, TotalMinutes;
        public RunPace(double dailyIncome, double dailyNet, double remaining, double daysLeft, double playedDays, double totalMinutes)
        {
            DailyIncome = dailyIncome; DailyNet = dailyNet; Remaining = remaining;
            DaysLeft = daysLeft; PlayedDays = playedDays; TotalDays = playedDays + daysLeft; TotalMinutes = totalMinutes;
        }
    }

    public static class RunStatsMath
    {
        public static RunPace Calculate(double income, double elapsed, double dayLength, double livingCost, double debt, double cash)
        {
            elapsed = Math.Max(0, elapsed);
            dayLength = Math.Max(1, dayLength);
            double dailyIncome = Math.Max(0, income) / Math.Max(1, elapsed) * dayLength;
            double dailyNet = dailyIncome - Math.Max(0, livingCost);
            double remaining = Math.Max(0, debt - cash);
            double daysLeft = dailyNet > 0 ? remaining / dailyNet : double.PositiveInfinity;
            return new RunPace(dailyIncome, dailyNet, remaining, daysLeft, elapsed / dayLength,
                (elapsed + daysLeft * dayLength) / 60);
        }
    }
}
