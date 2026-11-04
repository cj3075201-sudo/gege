using System;
using System.IO;
using UnityEngine;

namespace DeliveryRider
{
    // Progress that survives scene reloads and is saved when a day is closed.
    // Day-only values (today's income, deliveries) stay in DeliverySession.
    public sealed class RunProgress
    {
        public int Day { get; private set; }
        public long Cash { get; private set; }
        public long Debt { get; private set; }
        public bool HasInline { get; private set; }
        public int TotalDeliveries { get; private set; }
        public bool IsEnded { get; private set; }

        // Settlement of the current day, applied at most once per day.
        public bool DayClosed { get; private set; }
        public long LivingCostPaid { get; private set; }
        public long DebtAddedToday { get; private set; }

        public RunProgress(int day, long debt)
        {
            Day = Mathf.Max(1, day);
            Debt = Math.Max(0, debt);
        }

        // HTML discards the shortfall; the switch can restore the original debt rule.
        public void CloseDay(long income, int deliveries, long livingCost, bool addShortfallToDebt = false)
        {
            if (DayClosed || IsEnded) return;
            DayClosed = true;
            Cash += Math.Max(0, income);
            TotalDeliveries += Math.Max(0, deliveries);
            livingCost = Math.Max(0, livingCost);
            LivingCostPaid = Math.Min(Cash, livingCost);
            DebtAddedToday = addShortfallToDebt ? livingCost - LivingCostPaid : 0;
            Cash -= LivingCostPaid;
            Debt += DebtAddedToday;
        }

        public bool CanBuyInline(int price, int saleDay) => DayClosed && !IsEnded && !HasInline && Day >= saleDay && Cash >= price;

        public bool TryBuyInline(int price, int saleDay)
        {
            if (!CanBuyInline(price, saleDay)) return false;
            Cash -= price;
            HasInline = true;
            return true;
        }

        // Debt is repaid only in full, which ends the run.
        public bool CanRepay => DayClosed && !IsEnded && Debt > 0 && Cash >= Debt;

        public bool TryRepay()
        {
            if (!CanRepay) return false;
            Cash -= Debt;
            Debt = 0;
            IsEnded = true;
            return true;
        }

        public void StartNextDay()
        {
            if (!DayClosed || IsEnded) return;
            Day++;
            DayClosed = false;
            LivingCostPaid = DebtAddedToday = 0;
        }

        [Serializable]
        private sealed class SaveData
        {
            public int version = SaveVersion;
            public int day;
            public long cash;
            public long debt;
            public bool hasInline;
            public int totalDeliveries;
        }

        private const int SaveVersion = 1;
        private static string SavePath => Path.Combine(Application.persistentDataPath, "save.json");
        public static bool HasSave => File.Exists(SavePath);

        // Saves the start of the next day; call after StartNextDay.
        public void Save()
        {
            var data = new SaveData { day = Day, cash = Cash, debt = Debt, hasInline = HasInline, totalDeliveries = TotalDeliveries };
            try { File.WriteAllText(SavePath, JsonUtility.ToJson(data)); }
            catch (Exception exception) { Debug.LogWarning("Save failed: " + exception.Message); }
        }

        public static RunProgress Load()
        {
            try
            {
                if (!HasSave) return null;
                var data = JsonUtility.FromJson<SaveData>(File.ReadAllText(SavePath));
                if (data == null || data.version != SaveVersion || data.day < 1) return null;
                return new RunProgress(data.day, data.debt) { Cash = Math.Max(0, data.cash), HasInline = data.hasInline, TotalDeliveries = data.totalDeliveries };
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Save could not be read, starting fresh: " + exception.Message);
                return null;
            }
        }

        public static void DeleteSave()
        {
            try { if (HasSave) File.Delete(SavePath); }
            catch (Exception exception) { Debug.LogWarning("Save could not be deleted: " + exception.Message); }
        }
    }
}
