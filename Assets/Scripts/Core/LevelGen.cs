using System.Collections.Generic;
using UnityEngine;

public enum GoalType { Score, Color, Box, Yarn, Mint, Vase, Fish, Foam }

public class LevelGoal
{
    public GoalType type;
    public int target;      // Score/Color: amount to reach. Blockers/Fish: how many to clear/deliver.
    public int color = -1;  // Color goal only
    public int progress;    // counts UP to target for every type
    public bool Done => progress >= target;
    public int Remaining => Mathf.Max(0, target - progress);
}

public class LevelDef
{
    public int level;
    public int moves;
    public int mercyMoves;               // part of `moves` that is a gift after losses (does not count towards stars)
    public int boxes, yarns, mints, vases, fish;
    public int foam, foamThick;
    public int milk;                     // milk buckets (a bonus, not a goal): tipped over they spill coin puddles
    public bool mouse;                   // mouse thief rides the cats and steals coins
    public int theme;
    public bool rest;
    public string shape = "Rect";
    public bool[,] mask;                 // [x, y], y = 0 is the bottom row; false = no cell there
    public int LiveCells { get { int n = 0; foreach (var b in mask) if (b) n++; return n; } }
    public List<LevelGoal> goals = new List<LevelGoal>();
    public int BlockerTotal => boxes + yarns + mints + vases;
}

// Port of Marbly's LevelGenerator: one ever-increasing level index -> deterministic level
// (same level always rebuilds identically on Retry), sqrt-flattened difficulty growth, rest
// levels, adaptive mercy after consecutive losses, per-blocker cost on the targets.
// New for Kuzya: the blocker cast (Box / Yarn / Vase / Fish / Mint), introduced one at a time.
public static class LevelGen
{
    // Global difficulty knob: goals and blocker counts are multiplied by this (1 = the original tuning;
    // 1.25 = everything asks for 25% more with the same number of moves).
    public const float Difficulty = 1.25f;
    // ...and the move budget is multiplied by this (1 = original). Goals alone barely bite because a
    // decent player finishes with ~12 moves spare, so "harder" also means fewer moves.
    public const float MovesFactor = 0.76f;      // about 10% fewer moves than the 0.84 before (25 -> 19, 23 -> 17)

    public const int Cols = 7;
    public const int Rows = 8;
    public const int Colors = 5;

    // When each mechanic first appears (easiest -> hardest, as agreed).
    public const int BoxUnlock = 3, YarnUnlock = 5, VaseUnlock = 8, FishUnlock = 11, MintUnlock = 14, FoamUnlock = 16, MilkUnlock = 12, MouseUnlock = 26;
    // "events" (milk bucket / mouse thief): at most one per level, on every 3rd level; from MouseUnlock
    // they take turns. The mouse never comes on a level that already has three tasks.
    public static bool IsEventLevel(int level) => level >= MilkUnlock && level % 3 == 0;

    public static bool IsRestLevel(int level) => level >= 7 && level % 5 == 0;

    // ---- mercy (Marbly: rubber-banding after losses on the same level) ----
    public static string MercyKey(int level) => "Kuzya.Losses." + level;
    public static int LossStreak(int level) => PlayerPrefs.GetInt(MercyKey(level), 0);
    public static void RegisterLoss(int level) { PlayerPrefs.SetInt(MercyKey(level), LossStreak(level) + 1); PlayerPrefs.Save(); }
    public static void RegisterWin(int level) { PlayerPrefs.DeleteKey(MercyKey(level)); PlayerPrefs.Save(); }
    static float MercyTargetMult(int level) => Mathf.Max(0.75f, 1f - 0.05f * LossStreak(level));
    static float MercyBlockerMult(int level) => Mathf.Max(0.5f, 1f - 0.15f * LossStreak(level));
    // +1 move every 2 losses (up to +2), then +1 for every loss after the 4th: at most +5
    static int MercyExtraMoves(int level) { int s = LossStreak(level); return Mathf.Min(2, s / 2) + Mathf.Clamp(s - 4, 0, 3); }

    // Casual target: ~45-50% first-attempt win from level 5 (Marbly's calibration point = 1.0),
    // with an easy onboarding ramp. Marbly itself was tuned harder (20-25%); lower these to
    // make it harder.
    static float WinRateMult(int level)
    {
        if (IsRestLevel(level)) return 0.85f;
        switch (level) { case 1: return 0.55f; case 2: return 0.65f; case 3: return 0.75f; case 4: return 0.85f; }
        return 1.0f;
    }

    static int RampedCount(int level, int salt, int unlock, float baseMin, float baseMax, float growth)
    {
        if (level < unlock) return 0;
        var rng = new System.Random(level * 7919 + salt);
        float g = growth * Mathf.Sqrt(level - unlock);
        int min = Mathf.RoundToInt(baseMin + g), max = Mathf.Max(min, Mathf.RoundToInt(baseMax + g));
        int since = level - unlock;
        if (since < 3) { max = Mathf.Min(max, 2 + since); min = Mathf.Min(min, max); }
        return rng.Next(min, max + 1);
    }


    // ---- board shapes (7 x 8, rows written top to bottom; X = cell, . = empty) ----
    static readonly (string name, string[] rows)[] Shapes =
    {
        ("Rect",      new[] { "XXXXXXX", "XXXXXXX", "XXXXXXX", "XXXXXXX", "XXXXXXX", "XXXXXXX", "XXXXXXX", "XXXXXXX" }),
        ("Heart",     new[] { ".XX.XX.", "XXXXXXX", "XXXXXXX", "XXXXXXX", ".XXXXX.", ".XXXXX.", "..XXX..", "...X..." }),
        ("CatHead",   new[] { "XX...XX", "XXX.XXX", "XXXXXXX", "XXXXXXX", "XXXXXXX", "XXXXXXX", ".XXXXX.", "..XXX.." }),
        ("Paw",       new[] { "XX.X.XX", "XX.X.XX", "XXXXXXX", "XXXXXXX", ".XXXXX.", ".XXXXX.", "..XXX..", "......." }),
        ("Fish",      new[] { "..XXX..", ".XXXXXX", "XXXXXXX", "XXXXXX.", "XXXXXXX", ".XXXXXX", "..XXX..", "......." }),
        ("Diamond",   new[] { "...X...", "..XXX..", ".XXXXX.", "XXXXXXX", "XXXXXXX", ".XXXXX.", "..XXX..", "...X..." }),
        ("Hourglass", new[] { "XXXXXXX", "XXXXXXX", ".XXXXX.", "..XXX..", "..XXX..", ".XXXXX.", "XXXXXXX", "XXXXXXX" }),
        ("Cross",     new[] { "..XXX..", "..XXX..", "XXXXXXX", "XXXXXXX", "XXXXXXX", "XXXXXXX", "..XXX..", "..XXX.." }),
        ("Donut",     new[] { "XXXXXXX", "XXXXXXX", "XX...XX", "XX...XX", "XX...XX", "XXXXXXX", "XXXXXXX", "XXXXXXX" }),
    };

    // Levels 1-4 and rest levels: plain rectangle. Otherwise a fixed per-level shape (same on
    // retry); consecutive levels always differ (step 7 or 8 mod 9 is never 0).
    public static int ShapeIndexFor(int level)
    {
        if (level <= 4 || IsRestLevel(level)) return 0;
        return (level * 7 + level / 3) % Shapes.Length;
    }

    public static bool[,] MaskFor(int idx, out string name)
    {
        var sh = Shapes[idx];
        name = sh.name;
        var m = new bool[Cols, Rows];
        for (int r = 0; r < Rows; r++)
            for (int x = 0; x < Cols; x++)
                m[x, Rows - 1 - r] = sh.rows[r][x] == 'X';
        return m;
    }

    // ---- stars: how many moves were left at the win (see StarsFor).
    // Moves given as mercy after losses are not counted, so a level can't be "three-starred" by losing first.
    public const int Star3Moves = 6, Star2Moves = 3;
    public static int StarsFor(LevelDef d, int movesLeft)
    {
        int clean = movesLeft - d.mercyMoves;
        // fixed thresholds: 6 or more moves left -> 3 stars, 3-5 -> 2 stars, 0-2 -> 1 star
        // (moves given after losses don't count)
        if (clean >= Star3Moves) return 3;
        if (clean >= Star2Moves) return 2;
        return 1;
    }

    // two colour tasks at once: each asks for this share of what a single colour task would
    public const float TwoColorShare = 0.72f;

    public static LevelDef Build(int level)
    {
        var d = new LevelDef { level = level, rest = IsRestLevel(level) };
        var rng = new System.Random(level * 104729 + 17);
        d.theme = (level - 1) % 6;
        // a gentle ramp: 25 moves early on, 23 from level 15 (fish levels get extra moves below)
        d.mercyMoves = MercyExtraMoves(level);
        d.moves = Mathf.RoundToInt((level < 15 ? 25 : 23) * MovesFactor) + d.mercyMoves;
        d.mask = MaskFor(ShapeIndexFor(level), out d.shape);
        float area = d.LiveCells / (float)(Cols * Rows);   // smaller board -> fewer blockers, lower targets

        // The newest unlocked mechanic always features on its unlock level; others roll in.
        d.boxes = RampedCount(level, 11, BoxUnlock, 3, 5, 1.2f);
        d.yarns = RampedCount(level, 23, YarnUnlock, 2, 4, 1.0f);
        d.vases = RampedCount(level, 37, VaseUnlock, 2, 4, 1.0f);
        d.mints = RampedCount(level, 41, MintUnlock, 1, 2, 0.5f);
        d.foam = Mathf.RoundToInt(RampedCount(level, 53, FoamUnlock, 5, 8, 1.6f) * Difficulty);
        d.boxes = Mathf.RoundToInt(d.boxes * Difficulty); d.yarns = Mathf.RoundToInt(d.yarns * Difficulty);
        d.vases = Mathf.RoundToInt(d.vases * Difficulty); d.mints = Mathf.RoundToInt(d.mints * Difficulty);
        d.fish = level >= FishUnlock ? Mathf.Min(3, 1 + (level - FishUnlock) / 8) : 0;   // fish are the slowest goal: grow slowly, max 3

        // Variety cap: at most 2 blocker types before level 15, 3 after. Keep the newest.
        var types = new List<(GoalType t, int unlock)>();
        if (d.boxes > 0) types.Add((GoalType.Box, BoxUnlock));
        if (d.yarns > 0) types.Add((GoalType.Yarn, YarnUnlock));
        if (d.vases > 0) types.Add((GoalType.Vase, VaseUnlock));
        if (d.fish > 0) types.Add((GoalType.Fish, FishUnlock));
        if (d.mints > 0) types.Add((GoalType.Mint, MintUnlock));
        if (d.foam > 0) types.Add((GoalType.Foam, FoamUnlock));
        // a mouse level keeps to two tasks (catching the mouse is the third thing to think about)
        bool mouseLevel = IsEventLevel(level) && level >= MouseUnlock && (level / 3) % 2 == 1 && !d.rest;
        int cap = level >= 20 && !mouseLevel ? 3 : 2;   // the HUD has three goal medallions; three blocker kinds only from level 20
        // newest unlocked first, then a deterministic shuffle of the rest
        types.Sort((a, b) => b.unlock.CompareTo(a.unlock));
        var keep = new HashSet<GoalType>();
        // a newly unlocked mechanic is guaranteed for its first 3 levels, then everything rotates
        bool fresh = types.Count > 0 && level - types[0].unlock < 3;
        if (fresh) keep.Add(types[0].t);
        var restTypes = fresh ? types.GetRange(1, types.Count - 1) : new List<(GoalType t, int unlock)>(types);
        for (int i = restTypes.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); var tmp = restTypes[i]; restTypes[i] = restTypes[j]; restTypes[j] = tmp; }
        foreach (var t in restTypes) if (keep.Count < cap) keep.Add(t.t);
        if (!keep.Contains(GoalType.Box)) d.boxes = 0;
        if (!keep.Contains(GoalType.Yarn)) d.yarns = 0;
        if (!keep.Contains(GoalType.Vase)) d.vases = 0;
        if (!keep.Contains(GoalType.Fish)) d.fish = 0;
        if (!keep.Contains(GoalType.Mint)) d.mints = 0;
        if (!keep.Contains(GoalType.Foam)) d.foam = 0;
        if (keep.Count >= 3)
        {   // three different tasks at once: each one is a bit smaller so the level stays fair
            d.boxes = Mathf.RoundToInt(d.boxes * 0.7f); d.yarns = Mathf.RoundToInt(d.yarns * 0.7f);
            d.vases = Mathf.RoundToInt(d.vases * 0.7f); d.mints = Mathf.RoundToInt(d.mints * 0.7f);
            d.foam = Mathf.RoundToInt(d.foam * 0.7f); d.fish = Mathf.Max(d.fish > 0 ? 1 : 0, d.fish - 1);
        }

        if (d.rest) { d.boxes = Mathf.Min(d.boxes, 2); d.yarns = d.vases = d.mints = 0; d.fish = Mathf.Min(d.fish, 1); d.foam = Mathf.Min(d.foam, 4); }
        d.foam = Mathf.Min(d.foam, Mathf.RoundToInt(d.LiveCells * 0.4f));
        d.foamThick = 0;   // all foam pops with a single hit (no thick 2-layer foam)

        // mercy + hard density cap (never more than ~35% of the live cells covered)
        float m = MercyBlockerMult(level) * Mathf.Lerp(0.6f, 1f, area);
        d.boxes = Mathf.RoundToInt(d.boxes * m); d.yarns = Mathf.RoundToInt(d.yarns * m);
        d.vases = Mathf.RoundToInt(d.vases * m); d.mints = Mathf.Max(d.mints > 0 ? 1 : 0, Mathf.RoundToInt(d.mints * m));
        // mercy also softens foam and fish: less foam each loss, one fish fewer after 3 losses
        if (d.foam > 0) d.foam = Mathf.Max(2, Mathf.RoundToInt(d.foam * MercyBlockerMult(level)));
        if (d.fish > 1 && LossStreak(level) >= 3) { d.fish--; d.moves += 3; d.mercyMoves += 3; }   // a gift: doesn't count towards stars   // keep that fish's moves: the level must not get shorter
        int maxTotal = Mathf.RoundToInt(d.LiveCells * 0.35f);
        while (d.BlockerTotal > maxTotal)
        {
            if (d.boxes >= d.yarns && d.boxes >= d.vases && d.boxes > 0) d.boxes--;
            else if (d.vases >= d.yarns && d.vases > 0) d.vases--;
            else if (d.yarns > 0) d.yarns--;
            else d.mints--;
        }

        // ---- goals ----
        if (d.boxes > 0) d.goals.Add(new LevelGoal { type = GoalType.Box, target = d.boxes });
        if (d.yarns > 0) d.goals.Add(new LevelGoal { type = GoalType.Yarn, target = d.yarns });
        if (d.vases > 0) d.goals.Add(new LevelGoal { type = GoalType.Vase, target = d.vases });
        if (d.fish > 0) d.goals.Add(new LevelGoal { type = GoalType.Fish, target = d.fish });
        if (d.mints > 0) d.goals.Add(new LevelGoal { type = GoalType.Mint, target = d.mints });
        if (d.foam > 0) d.goals.Add(new LevelGoal { type = GoalType.Foam, target = d.foam });

        d.moves += 3 * d.fish;   // each fish has to travel the whole board: give it time
        if (d.foam > 0) d.moves += 2;   // foam is cleared cell by cell

        float scale = Difficulty * WinRateMult(level) * MercyTargetMult(level) * Mathf.Lerp(0.55f, 1f, area);
        float blockerCost = 1f - 0.018f * d.BlockerTotal - 0.05f * d.fish - 0.01f * d.foam;
        // Colour tasks fill the level up when there are few blockers (there are no score tasks: the player
        // can't steer a score, but "collect 18 ginger cats" is something to play for).
        // No blockers at all -> one or two colours; one blocker kind -> sometimes one colour.
        int baseColor = Mathf.RoundToInt(29.5f * 0.62f * blockerCost * scale);
        bool wantColor = level == 2 || (level > 2 && d.goals.Count < 2 && rng.NextDouble() < 0.5);
        bool second = rng.NextDouble() < 0.5;
        if (d.goals.Count == 0)
        {
            bool two = level > 2 && (!wantColor || second);     // the levels that used to have a score task
            int c1 = (level - 1) % Colors;
            float share = two ? TwoColorShare : level == 1 ? 0.75f : 1f;      // the very first level is a warm-up
            d.goals.Add(new LevelGoal { type = GoalType.Color, target = Mathf.Max(two ? 6 : 8, Mathf.RoundToInt(baseColor * share)), color = c1 });
            if (two) d.goals.Add(new LevelGoal { type = GoalType.Color, target = Mathf.Max(6, Mathf.RoundToInt(baseColor * share)), color = (c1 + 2) % Colors });
        }
        else if (d.goals.Count < 2 && (wantColor || (d.goals.Count == 1 && second)))
            d.goals.Add(new LevelGoal { type = GoalType.Color, target = Mathf.Max(8, Mathf.RoundToInt(baseColor * (wantColor ? 1f : 0.75f))), color = (level - 1) % Colors });

        // ---- event: milk bucket or mouse thief (never both) ----
        if (IsEventLevel(level))
        {
            if (mouseLevel && d.goals.Count < 3) d.mouse = true;
            else { var mr = new System.Random(level * 3571 + 7); d.milk = d.rest ? 1 + mr.Next(2) : 1; }
        }
        return d;
    }
}
