using System;
using UnityEngine;

public enum PowerUpType { Paw, MegaBomb, ColorBlast, ExtraMoves, Swap }

// Persistent player progress - Marbly's PlayerPrefs pattern (BalanceManager / PowerUpInventory /
// SaveVersionGate) folded into one static class: current level, fish-coin balance, power-up
// charges, and a one-time wipe when SAVE_VERSION is bumped for a test build.
public static class Progress
{
    const int SAVE_VERSION = 1;
    const string P = "Kuzya.";

    public static event Action OnBalanceChanged;
    public static event Action<PowerUpType> OnPowerUpChanged;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Gate()
    {
        if (PlayerPrefs.GetInt(P + "SaveVersion", 0) == SAVE_VERSION)
        {
            // stars arrived later than the first builds: a player who is already on the road gets
            // one star for every level passed, and no pile of old chests
            int total = StarsTotal, spent = ChestSpent, gate = Worlds.Open;
            return;
        }
        ResetAll();
        PlayerPrefs.SetInt(P + "SaveVersion", SAVE_VERSION);
        // first-launch gift so every power-up can be tried right away
        foreach (PowerUpType t in Enum.GetValues(typeof(PowerUpType))) PlayerPrefs.SetInt(Key(t), 1);
        PlayerPrefs.Save();
    }

    public static void ResetAll()
    {
        PlayerPrefs.DeleteKey(P + "Level");
        PlayerPrefs.DeleteKey(P + "Balance");
        foreach (PowerUpType t in Enum.GetValues(typeof(PowerUpType))) PlayerPrefs.DeleteKey(Key(t));
        for (int i = 1; i < 500; i++) PlayerPrefs.DeleteKey(LevelGen.MercyKey(i));
        for (int i = 1; i < 2000; i++) PlayerPrefs.DeleteKey(StarKey(i));
        foreach (var k in new[] { "StarsTotal", "ChestStars", "Chest", "Daily.Last", "Daily.LastN", "Daily.Streak", "Daily.Weeks", "GateOpen", "WinStreak", "Tut.1", "Tut.2", "Tut.3" }) PlayerPrefs.DeleteKey(P + k);
    }

    public static int Level
    {
        get => Mathf.Max(1, PlayerPrefs.GetInt(P + "Level", 1));
        set { PlayerPrefs.SetInt(P + "Level", Mathf.Max(1, value)); PlayerPrefs.Save(); }
    }

    // ---- Balance (fish coins) - earned from big cascades, spent on power-ups ----
    // wins in a row on new levels (0..3); three in a row = a free special cat at the start of the next level
    public static int WinStreak
    {
        get => PlayerPrefs.GetInt(P + "WinStreak", 0);
        set { PlayerPrefs.SetInt(P + "WinStreak", Mathf.Clamp(value, 0, 3)); PlayerPrefs.Save(); }
    }

    public static int Balance => PlayerPrefs.GetInt(P + "Balance", 0);

    public static void AddBalance(int amount)
    {
        if (amount <= 0) return;
        PlayerPrefs.SetInt(P + "Balance", Balance + amount);
        PlayerPrefs.Save();
        OnBalanceChanged?.Invoke();
    }

    public static bool TrySpend(int amount)
    {
        if (amount <= 0) return true;
        if (Balance < amount) return false;
        PlayerPrefs.SetInt(P + "Balance", Balance - amount);
        PlayerPrefs.Save();
        OnBalanceChanged?.Invoke();
        return true;
    }

    // ---- Stars: every won level keeps its best result, 1-3 stars. Each star counts once; replaying a
    // level can only add the missing ones. A level passed before stars existed counts as 1 star. ----
    static string StarKey(int level) => P + "Stars." + level;
    public static int Stars(int level)
    {
        int s = PlayerPrefs.GetInt(StarKey(level), -1);
        return s >= 0 ? Mathf.Clamp(s, 0, 3) : (level < Level ? 1 : 0);
    }
    public static int StarsTotal
    {
        get
        {
            if (!PlayerPrefs.HasKey(P + "StarsTotal")) { PlayerPrefs.SetInt(P + "StarsTotal", Level - 1); PlayerPrefs.Save(); }
            return PlayerPrefs.GetInt(P + "StarsTotal", 0);
        }
    }
    // records a win with this many stars; returns how many NEW stars it brought (0 when the level already had as many).
    // Call it before Level moves on.
    public static int AddStars(int level, int stars)
    {
        int total = StarsTotal, spent = ChestSpent;      // make sure both are set up before anything changes
        stars = Mathf.Clamp(stars, 1, 3);
        int had = Stars(level);
        if (stars <= had) { if (!PlayerPrefs.HasKey(StarKey(level))) { PlayerPrefs.SetInt(StarKey(level), had); PlayerPrefs.Save(); } return 0; }
        PlayerPrefs.SetInt(StarKey(level), stars);
        PlayerPrefs.SetInt(P + "StarsTotal", total + stars - had);
        PlayerPrefs.Save();
        return stars - had;
    }

    // ---- Treasure chests: one for every 15 stars. ChestSpent = stars already "paid" for opened chests. ----
    public const int ChestStars = 15;
    public static int ChestSpent
    {
        get
        {
            if (!PlayerPrefs.HasKey(P + "ChestStars")) { PlayerPrefs.SetInt(P + "ChestStars", StarsTotal / ChestStars * ChestStars); PlayerPrefs.Save(); }
            return PlayerPrefs.GetInt(P + "ChestStars", 0);
        }
    }
    // stars collected towards the next chest, 0..ChestStars
    public static int ChestProgress => Mathf.Clamp(StarsTotal - ChestSpent, 0, ChestStars);
    // the number of the chest waiting to be opened (1, 2, 3...), or 0 when none is ready
    public static int PendingChest => StarsTotal - ChestSpent >= ChestStars ? ChestSpent / ChestStars + 1 : 0;
    public static void OpenChest()
    {
        if (PendingChest == 0) return;
        PlayerPrefs.SetInt(P + "ChestStars", ChestSpent + ChestStars); PlayerPrefs.Save();
    }

    // ---- Daily "three bowls": one go per calendar day. Days in a row are counted up to 7; the 7th day
    // also gives a power-up (they take turns week after week), then the count starts again. ----
    public const int DailyDays = 7;
    // days are plain day numbers (independent of the phone's language and calendar)
    static int DayNum(DateTime d) => (int)(d.Date.Ticks / TimeSpan.TicksPerDay);
    static int Today => DayNum(DateTime.Now);
    // the day of the last go; older saves kept it as "yyyyMMdd" text
    static int LastDay
    {
        get
        {
            int n = PlayerPrefs.GetInt(P + "Daily.LastN", -1);
            if (n >= 0) return n;
            string s = PlayerPrefs.GetString(P + "Daily.Last", "");
            return DateTime.TryParseExact(s, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
                                          System.Globalization.DateTimeStyles.None, out var d) ? DayNum(d) : -1;
        }
    }
    // ready only on a LATER day than the last go: turning the phone's clock back gives no extra goes
    public static bool DailyReady => Today > LastDay;
    // days played in a row so far (0 when the row is broken)
    public static int DailyStreak
    {
        get
        {
            int last = LastDay, today = Today;
            return last == today || last == today - 1 ? Mathf.Clamp(PlayerPrefs.GetInt(P + "Daily.Streak", 0), 0, DailyDays) : 0;
        }
    }
    // which day of the row today's go is: 1..7
    public static int DailyDay { get { int s = DailyStreak; return s >= DailyDays ? 1 : s + 1; } }
    public static PowerUpType DailyBooster => (PowerUpType)(PlayerPrefs.GetInt(P + "Daily.Weeks", 0) % 4);
    // marks today's go as used; returns the day of the row (7 = the power-up day, already added)
    public static int DailyPlay()
    {
        int day = DailyDay;
        PlayerPrefs.SetInt(P + "Daily.LastN", Today);
        PlayerPrefs.SetInt(P + "Daily.Streak", day);
        if (day >= DailyDays)
        {
            var t = DailyBooster;
            PlayerPrefs.SetInt(P + "Daily.Weeks", PlayerPrefs.GetInt(P + "Daily.Weeks", 0) + 1);
            AddCharge(t);
        }
        PlayerPrefs.Save();
        return day;
    }

    public static void AddCharge(PowerUpType t)
    {
        if (Charges(t) >= MaxCharges) return;
        PlayerPrefs.SetInt(Key(t), Charges(t) + 1);
        PlayerPrefs.Save();
        OnPowerUpChanged?.Invoke(t);
    }

    // ---- Power-ups (Marbly PowerUpInventory: buy adds 1 charge, max 9) ----
    public const int MaxCharges = 9;
    static string Key(PowerUpType t) => P + "PowerUp." + t;

    public static int Price(PowerUpType t)
    {
        switch (t)
        {
            case PowerUpType.Paw: return 50;
            case PowerUpType.MegaBomb: return 80;
            case PowerUpType.ColorBlast: return 70;
            case PowerUpType.Swap: return 60;
            default: return 60;
        }
    }

    public static int Charges(PowerUpType t) => Mathf.Clamp(PlayerPrefs.GetInt(Key(t), 0), 0, MaxCharges);

    public static bool Buy(PowerUpType t)
    {
        if (Charges(t) >= MaxCharges) return false;
        if (!TrySpend(Price(t))) return false;
        PlayerPrefs.SetInt(Key(t), Charges(t) + 1);
        PlayerPrefs.Save();
        OnPowerUpChanged?.Invoke(t);
        return true;
    }

    public static bool Consume(PowerUpType t)
    {
        int c = Charges(t);
        if (c <= 0) return false;
        PlayerPrefs.SetInt(Key(t), c - 1);
        PlayerPrefs.Save();
        OnPowerUpChanged?.Invoke(t);
        return true;
    }
}
