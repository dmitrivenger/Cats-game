using System;
using UnityEngine;

public enum PowerUpType { Paw, MegaBomb, ColorBlast, ExtraMoves }

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
        if (PlayerPrefs.GetInt(P + "SaveVersion", 0) == SAVE_VERSION) return;
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
    }

    public static int Level
    {
        get => Mathf.Max(1, PlayerPrefs.GetInt(P + "Level", 1));
        set { PlayerPrefs.SetInt(P + "Level", Mathf.Max(1, value)); PlayerPrefs.Save(); }
    }

    // ---- Balance (fish coins) - earned from big cascades, spent on power-ups ----
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

    // ---- Treasure chests: one after every 5th level. ChestOpened = the last chest level taken. A player
    // who already was past some chests when this arrived starts from there (no pile of old chests). ----
    public const int ChestEvery = 5;
    public static int ChestOpened
    {
        get
        {
            if (!PlayerPrefs.HasKey(P + "Chest")) { PlayerPrefs.SetInt(P + "Chest", (Level - 1) / ChestEvery * ChestEvery); PlayerPrefs.Save(); }
            return PlayerPrefs.GetInt(P + "Chest", 0);
        }
        set { PlayerPrefs.SetInt(P + "Chest", value); PlayerPrefs.Save(); }
    }
    // the next chest waiting to be opened (its level), or 0
    public static int PendingChest { get { int c = ChestOpened + ChestEvery; return c <= Level - 1 ? c : 0; } }

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
