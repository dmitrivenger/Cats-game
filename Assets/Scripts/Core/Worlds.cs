using System.Collections.Generic;
using UnityEngine;

// The road is cut into worlds (garden, kitchen, rooftop...). There are ArtCount sets of pictures.
// The first time round the player walks through all of them in order, each in a full day: a soft morning at
// its first levels, daylight, a golden evening before the next gate. After that the road never ends: every
// next world is picked at random (never one of the last few again) and gets its own time of day - a whole
// morning, day, evening or night world. The picks are fixed (seeded by the world's number), so the map is
// the same every time it is opened.
// To walk through the gate into the next world the player needs a share of the stars of the world they
// are leaving. Worlds already entered stay open for good.
public static class Worlds
{
    public const int ArtCount = 12;
    public const int Count = ArtCount;                                    // kept for older callers
    // lengths of the first round (levels per world); every world after it is RandomLen long
    static readonly int[] Len = { 20, 25, 30, 35, 40, 45, 50, 55, 60, 60, 60, 60 };
    const int RandomLen = 60;
    static readonly int[] Start;                                          // levels before each world of the first round
    static readonly int FirstRound;                                       // levels in the first round
    static Worlds()
    {
        Start = new int[ArtCount]; int s = 0;
        for (int i = 0; i < ArtCount; i++) { Start[i] = s; s += Len[i]; }
        FirstRound = s;
    }

    public enum Mood { Day, Morning, Evening, Night }

    // 0-based and ever growing
    public static int Of(int level)
    {
        int l = Mathf.Max(1, level) - 1;
        if (l >= FirstRound) return ArtCount + (l - FirstRound) / RandomLen;
        int i = ArtCount - 1;
        while (Start[i] > l) i--;
        return i;
    }
    public static int Length(int world) => world < ArtCount ? Len[Mathf.Max(0, world)] : RandomLen;
    public static int First(int world) => world < ArtCount ? Start[Mathf.Max(0, world)] + 1 : FirstRound + (world - ArtCount) * RandomLen + 1;
    public static int Last(int world) => First(world) + Length(world) - 1;

    // ---- which pictures and which time of day (fixed per world, picked once and remembered)
    static readonly List<int> arts = new List<int>();
    static readonly List<Mood> times = new List<Mood>();
    static void Fill(int world)
    {
        while (arts.Count <= world)
        {
            int w = arts.Count;
            if (w < ArtCount) { arts.Add(w); times.Add(Mood.Day); continue; }
            uint seed = Hash((uint)w * 2654435761u + 0x9E3779B9u);
            int a;
            do { seed = Hash(seed); a = (int)(seed % ArtCount); }
            while (Recent(w, a, 4));                                       // never one of the last four worlds again
            arts.Add(a);
            // mostly daylight; a night never right after a night
            seed = Hash(seed); double r = (seed & 0xFFFFFF) / (double)0x1000000;
            Mood t = r < 0.34 ? Mood.Day : r < 0.56 ? Mood.Evening : r < 0.78 ? Mood.Morning : Mood.Night;
            if (t == Mood.Night && times[w - 1] == Mood.Night) t = Mood.Evening;
            times.Add(t);
        }
    }
    // a well-mixing integer hash (neighbouring worlds get unrelated picks)
    static uint Hash(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352du; x ^= x >> 15; x *= 0x846ca68bu; x ^= x >> 16;
        return x;
    }
    static bool Recent(int w, int a, int back)
    {
        for (int i = Mathf.Max(0, w - back); i < w; i++) if (arts[i] == a) return true;
        return false;
    }
    public static int Art(int world) { world = Mathf.Max(0, world); Fill(world); return arts[world]; }
    public static Mood TimeOf(int world) { world = Mathf.Max(0, world); Fill(world); return times[world]; }
    public static bool Night(int world) => TimeOf(world) == Mood.Night;
    // where in its day a world is at progress p (0 its first level, 1 its last): 0 morning ... 1 evening
    public static float DayPart(int world, float p)
    {
        switch (TimeOf(world))
        {
            case Mood.Morning: return 0f;
            case Mood.Evening: return 1f;
            case Mood.Day: return world < ArtCount ? p : 0.45f;            // the first round shows the whole day
            default: return 0.45f;
        }
    }
    public static string Name(int world) => Loc.T((Night(world) ? "World.N." : "World.") + (Art(world) + 1));

    // ---- star gates ----
    // share of a world's stars needed to leave it: 60% for the garden, then 65, 70, and 75% from the fourth world on
    public static float Share(int world) => Mathf.Min(0.75f, 0.60f + 0.05f * world);
    public static int StarsMax(int world) => Length(world) * 3;
    public static int StarsNeed(int world) => Mathf.CeilToInt(StarsMax(world) * Share(world));
    public static int Stars(int world)
    {
        int s = 0;
        for (int l = First(world), e = Last(world); l <= e; l++) s += Progress.Stars(l);
        return s;
    }

    // the furthest world the player may play in. A player who was already further when gates arrived keeps that.
    const string Key = "Kuzya.GateOpen";
    public static int Open
    {
        get
        {
            if (!PlayerPrefs.HasKey(Key)) { PlayerPrefs.SetInt(Key, Of(Progress.Level)); PlayerPrefs.Save(); }
            return PlayerPrefs.GetInt(Key, 0);
        }
        set { PlayerPrefs.SetInt(Key, value); PlayerPrefs.Save(); }
    }
    public static bool Locked(int level) => Of(level) > Open;
    // the level the big "Play" button starts: the next unplayed one, or - at a locked gate - the last one before it
    public static int Playable => Locked(Progress.Level) ? Last(Open) : Progress.Level;
}
