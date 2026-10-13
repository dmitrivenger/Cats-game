using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// Daily mini-game "three bowls" (opened from the icon in the corner of the level map), once per calendar day.
//  * Three upside-down bowls stand on a mat. One lifts to show the golden fish, then the bowls are
//    shuffled (slowly enough to follow) and the player picks one.
//  * Under the right bowl: BigPrize coins. Under one other: SmallPrize coins. The third is empty: a little
//    kitten sits there crying (nothing to take). The shuffle gets longer, faster and trickier (three-way
//    rotations, feints) as the streak of days grows. The player
//    gets what is under the bowl they picked; then the other bowls lift too, so the result is honest.
//  * Days in a row are shown as seven pips next to the flame-cat; the 7th day also gives a power-up.
// Built from code on the popup canvas, like every other window. Runs on unscaled time.
public class DailyBowls : MonoBehaviour
{
    public const int BigPrize = 10, SmallPrize = 2;   // 20% less again than 12 / 2 (the small one can't go lower)
    // the shuffle grows with the streak day (1..7): more moves, faster at the end, tricks from day 2
    int Swaps => 8 + day;
    const float SwapSlow = 0.42f;
    float SwapFast => Mathf.Max(0.17f, 0.3f - 0.019f * day);
    const float BowlW = 280f, BowlH = BowlW * 448f / 512f, Gap = 315f;
    const float MatY = -120f, BowlY = MatY + 78f, Lift = 235f;

    RectTransform root, bowlLayer, prizeLayer;
    Action onClose;
    readonly RectTransform[] bowls = new RectTransform[3];
    readonly int[] slotOf = { 0, 1, 2 };                  // bowl index -> slot (0 left, 1 middle, 2 right)
    readonly bool[] lifted = new bool[3];
    int goldBowl, emptyBowl, picked = -1;
    bool secondTry; int firstDay;                       // a second round bought with a video (once, after a miss)
    readonly List<GameObject> revealBits = new List<GameObject>();
    bool canPick, idle;
    Text title, hint;
    Button closeBtn;
    readonly List<Image> pips = new List<Image>();
    RectTransform todayPip, flame;
    int day;
    readonly List<(RectTransform t, Vector2 v, float spin)> bits = new List<(RectTransform, Vector2, float)>();
    Image rays;

    static float SlotX(int slot) => (slot - 1) * Gap;

    public static DailyBowls Open(RectTransform parent, Action onClose)
    {
        var rt = UIKit.Rect("DailyBowls", parent); UIKit.Stretch(rt);
        var d = rt.gameObject.AddComponent<DailyBowls>();
        d.root = rt; d.onClose = onClose;
        d.Build();
        return d;
    }

    void Build()
    {
        var dim = root.gameObject.AddComponent<Image>(); dim.color = new Color(0.1f, 0.09f, 0.26f, 1f);      // its own little stage: nothing of the map shows through
        day = Progress.DailyDay;
        goldBowl = UnityEngine.Random.Range(0, 3);
        emptyBowl = (goldBowl + UnityEngine.Random.Range(1, 3)) % 3;

        if (GameArt.I.Has("res_win_rays"))
        {
            rays = UIKit.Image(root, "res_win_rays", new Vector2(0.5f, 0.5f), new Vector2(0, MatY + 120), Vector2.one * 1500f);
            rays.color = new Color(1f, 0.82f, 0.4f, 0f);
        }
        title = UIKit.Label(root, Loc.T("Daily.Title"), 88, new Color(1f, 0.85f, 0.35f), new Vector2(0.5f, 0.5f), new Vector2(0, 700), new Vector2(980, 120));
        BuildStreak(540f);

        UIKit.Image(root, "daily_mat", new Vector2(0.5f, 0.5f), new Vector2(0, MatY), new Vector2(1040, 1040 * 308f / 1024f));
        prizeLayer = UIKit.Rect("prizes", root); UIKit.Place(prizeLayer, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        bowlLayer = UIKit.Rect("bowls", root); UIKit.Place(bowlLayer, new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);
        for (int i = 0; i < 3; i++)
        {
            int b = i;
            var img = UIKit.Image(bowlLayer, "daily_bowl", new Vector2(0.5f, 0.5f), new Vector2(SlotX(i), BowlY), new Vector2(BowlW, BowlH));
            img.raycastTarget = true; img.name = "btn_bowl_" + i;
            var btn = img.gameObject.AddComponent<Button>(); btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(() => Pick(b));
            bowls[i] = img.rectTransform;
        }
        hint = UIKit.Label(root, Loc.T("Daily.Watch"), 58, Color.white, new Vector2(0.5f, 0.5f), new Vector2(0, -420), new Vector2(1000, 90));

        closeBtn = UIKit.ArtButton(root, "ui_close", new Vector2(1f, 1f), new Vector2(-90, -130), 130, Close, "X");
        StartCoroutine(Run());
    }

    // flame-cat + seven pips: days already played are gold, today's pip beats, the 7th carries the power-up
    void BuildStreak(float y)
    {
        var row = UIKit.Place(UIKit.Rect("streak", root), new Vector2(0.5f, 0.5f), new Vector2(0, y), new Vector2(1000, 150));
        flame = UIKit.Image(row, "streak_flame", new Vector2(0.5f, 0.5f), new Vector2(-405, 6), new Vector2(150, 150)).rectTransform;
        for (int i = 1; i <= Progress.DailyDays; i++)
        {
            bool last = i == Progress.DailyDays;
            float size = last ? 118f : 78f;
            float x = last ? 372f : -280f + (i - 1) * 96f;
            var rim = UIKit.Panel(row, new Vector2(0.5f, 0.5f), new Vector2(x, 0), new Vector2(size, size), UIKit.Navy, Mathf.RoundToInt(size / 2));
            var face = UIKit.Panel(rim.transform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(size - 12, size - 12), PipColor(i < day), Mathf.RoundToInt((size - 12) / 2));
            pips.Add(face);
            if (last)
            {
                var t = Progress.DailyBooster;
                string pk = GameController.PuKey(t);
                UIKit.Image(rim.transform, pk, new Vector2(0.5f, 0.5f), new Vector2(0, 2), new Vector2(92, 92));
            }
            else UIKit.Label(rim.transform, i.ToString(), 40, Color.white, new Vector2(0.5f, 0.5f), new Vector2(0, 1), new Vector2(size, size), true);
            if (i == day) todayPip = rim.rectTransform;
        }
    }

    static Color PipColor(bool done) => done ? new Color(1f, 0.72f, 0.2f) : new Color(0.3f, 0.38f, 0.66f);

    // ------------------------------------------------------------------ the show
    IEnumerator Run()
    {
        foreach (var b in bowls) b.localScale = Vector3.zero;
        for (int i = 0; i < 3; i++) { StartCoroutine(PopIn(bowls[i], 0.32f)); AudioManager.Play("Pop", 0.35f); yield return new WaitForSecondsRealtime(0.11f); }
        yield return new WaitForSecondsRealtime(0.45f);

        // show the golden fish under one bowl
        var fish = Prize(goldBowl, 1f);
        yield return LiftBowl(goldBowl, true, 0.32f);
        AudioManager.Play("balance", 0.6f);
        yield return Tween(0.9f, k => { if (fish != null) fish.localScale = Vector3.one * (1f + 0.08f * Mathf.Sin(k * Mathf.PI * 3f)); });
        yield return LiftBowl(goldBowl, false, 0.24f);
        if (fish != null) Destroy(fish.gameObject);
        // once the bowls start moving the window can't be closed: no closing and reopening for a new shuffle
        if (closeBtn != null) closeBtn.gameObject.SetActive(false);
        yield return new WaitForSecondsRealtime(0.3f);

        // shuffle: mostly two bowls trade places (one passing in front of the other); from day 2 a bowl
        // sometimes fakes a move, from day 3 all three sometimes rotate at once
        int lastA = -1, lastB = -1, n = Swaps;
        for (int s = 0; s < n; s++)
        {
            float dur = Mathf.Lerp(SwapSlow, SwapFast, s / (float)(n - 1));
            AudioManager.Play("bowl_shuffle", 0.7f);
            float r = UnityEngine.Random.value;
            if (day >= 3 && s > 0 && r < 0.22f)
            {
                yield return Rotate(UnityEngine.Random.value < 0.5f ? 1 : -1, dur * 1.25f);
                lastA = lastB = -1;
            }
            else
            {
                int a, b;
                do { a = UnityEngine.Random.Range(0, 3); b = UnityEngine.Random.Range(0, 3); }
                while (a == b || (Mathf.Min(a, b) == lastA && Mathf.Max(a, b) == lastB));
                if (day >= 2 && r > 0.82f) { yield return Feint(a, b, dur * 0.7f); a = b == 0 ? 1 : 0; if (a == b) a = 2; }
                lastA = Mathf.Min(a, b); lastB = Mathf.Max(a, b);
                yield return Swap(a, b, dur);
            }
            yield return new WaitForSecondsRealtime(0.04f);
        }

        hint.text = Loc.T("Daily.Pick");
        StartCoroutine(Punch(hint.transform));
        canPick = true; idle = true;
    }

    // a and b are bowl indices
    IEnumerator Swap(int a, int b, float dur)
    {
        float xa = SlotX(slotOf[a]), xb = SlotX(slotOf[b]);
        float arc = 62f + 14f * Mathf.Abs(slotOf[a] - slotOf[b]);
        bowls[a].SetAsLastSibling();                      // a passes in front (down the screen), b behind
        yield return Tween(dur, k =>
        {
            float e = Kit.EaseInOut(k), w = Mathf.Sin(k * Mathf.PI);
            bowls[a].anchoredPosition = new Vector2(Mathf.Lerp(xa, xb, e), BowlY - arc * w);
            bowls[b].anchoredPosition = new Vector2(Mathf.Lerp(xb, xa, e), BowlY + arc * 0.7f * w);
            bowls[a].localScale = Vector3.one * (1f + 0.07f * w);
            bowls[b].localScale = Vector3.one * (1f - 0.07f * w);
        });
        int t = slotOf[a]; slotOf[a] = slotOf[b]; slotOf[b] = t;
    }

    // all three bowls shift one place round (dir +1 = to the right); the one at the end swoops across in front
    IEnumerator Rotate(int dir, float dur)
    {
        var from = new float[3]; var to = new int[3];
        for (int i = 0; i < 3; i++) { from[i] = SlotX(slotOf[i]); to[i] = (slotOf[i] + dir + 3) % 3; }
        int wrap = -1; for (int i = 0; i < 3; i++) if (Mathf.Abs(to[i] - slotOf[i]) == 2) wrap = i;
        if (wrap >= 0) bowls[wrap].SetAsLastSibling();
        yield return Tween(dur, k =>
        {
            float e = Kit.EaseInOut(k), w = Mathf.Sin(k * Mathf.PI);
            for (int i = 0; i < 3; i++)
            {
                bool wr = i == wrap;
                bowls[i].anchoredPosition = new Vector2(Mathf.Lerp(from[i], SlotX(to[i]), e), BowlY + (wr ? -95f : 40f) * w);
                bowls[i].localScale = Vector3.one * (1f + (wr ? 0.08f : -0.04f) * w);
            }
        });
        for (int i = 0; i < 3; i++) slotOf[i] = to[i];
    }

    // bowl a starts towards b, stops halfway and slides back: nothing changes places
    IEnumerator Feint(int a, int b, float dur)
    {
        float xa = SlotX(slotOf[a]), xb = SlotX(slotOf[b]);
        bowls[a].SetAsLastSibling();
        yield return Tween(dur, k =>
        {
            float w = Mathf.Sin(k * Mathf.PI);
            bowls[a].anchoredPosition = new Vector2(Mathf.Lerp(xa, xb, 0.42f * w), BowlY - 40f * w);
            bowls[a].localScale = Vector3.one * (1f + 0.05f * w);
            bowls[b].localRotation = Quaternion.Euler(0, 0, 6f * w * Mathf.Sin(k * 30f));
        });
        bowls[a].anchoredPosition = new Vector2(xa, BowlY); bowls[a].localScale = Vector3.one; bowls[b].localRotation = Quaternion.identity;
    }

    void Pick(int b)
    {
        if (!canPick) return;
        canPick = false; idle = false; picked = b;
        if (closeBtn != null) closeBtn.gameObject.SetActive(false);
        hint.gameObject.SetActive(false);
        StartCoroutine(Reveal());
    }

    IEnumerator Reveal()
    {
        bool win = picked == goldBowl, empty = picked == emptyBowl;
        int prize = win ? BigPrize : empty ? 0 : SmallPrize;
        // the reward is given the moment the bowl is chosen, so closing the game mid-animation loses nothing
        int playedDay = secondTry ? firstDay : Progress.DailyPlay();     // the day is counted only once
        bool booster = !secondTry && playedDay >= Progress.DailyDays;
        if (!secondTry) firstDay = playedDay;
        Progress.AddBalance(prize);

        foreach (var bw in bowls) { bw.localRotation = Quaternion.identity; bw.localScale = Vector3.one; }
        AudioManager.Play("chest_tap", 0.7f);
        yield return Tween(0.22f, k => bowls[picked].localRotation = Quaternion.Euler(0, 0, 9f * Mathf.Sin(k * Mathf.PI * 4f) * (1f - k)));
        var mine = Prize(picked, 1f);
        yield return LiftBowl(picked, true, 0.3f);
        AudioManager.Play(win ? "chest_open" : empty ? "no_win" : "balance", win ? 0.9f : 0.6f);
        title.text = Loc.T(win ? "Daily.Win" : empty ? "Daily.Empty" : "Daily.Miss");
        if (empty) title.fontSize = 62;                    // the longer line has to fit the width
        StartCoroutine(Punch(title.transform));
        if (!empty) { var mineLbl = PrizeLabel(picked, prize, true); revealBits.Add(mineLbl.gameObject); StartCoroutine(PopIn(mineLbl, 0.3f)); }
        if (win) Confetti();

        // today's pip turns gold
        if (!secondTry)
        {
            if (playedDay - 1 < pips.Count) pips[playedDay - 1].color = PipColor(true);
            if (todayPip != null) StartCoroutine(Punch(todayPip));
            if (flame != null) StartCoroutine(Punch(flame));
        }

        yield return new WaitForSecondsRealtime(0.75f);
        // ...and the other two bowls show what they were hiding
        for (int i = 0; i < 3; i++)
        {
            if (i == picked) continue;
            var p = Prize(i, 0.8f);
            StartCoroutine(LiftBowl(i, true, 0.28f));
            if (i == emptyBowl) continue;
            var lbl = PrizeLabel(i, i == goldBowl ? BigPrize : SmallPrize, false);
            revealBits.Add(lbl.gameObject);
            StartCoroutine(PopIn(lbl, 0.3f));
        }
        AudioManager.Play("Pop", 0.4f);
        yield return new WaitForSecondsRealtime(0.55f);

        // what the player takes home
        var row = UIKit.Place(UIKit.Rect("reward", root), new Vector2(0.5f, 0.5f), new Vector2(0, -455), new Vector2(800, 150));
        float cx = booster ? -230f : -95f;
        UIKit.Image(row, "ui_balance", new Vector2(0.5f, 0.5f), new Vector2(cx, 0), new Vector2(112, 112));
        UIKit.Label(row, "+" + prize, 84, new Color(1f, 0.9f, 0.4f), new Vector2(0.5f, 0.5f), new Vector2(cx + 185, 0), new Vector2(220, 110), true, TextAnchor.MiddleLeft);
        if (booster)
        {
            var t = (PowerUpType)(((int)Progress.DailyBooster + 3) % 4);      // the one just given (the counter already moved on)
            string pk = GameController.PuKey(t);
            var pim = UIKit.Image(row, pk, new Vector2(0.5f, 0.5f), new Vector2(220, 0), new Vector2(150, 150));
            UIKit.Label(pim.transform, "+1", 48, Color.white, new Vector2(1f, 0f), new Vector2(-10, 10), new Vector2(80, 60), true);
        }
        revealBits.Add(row.gameObject);
        StartCoroutine(PopIn(row, 0.35f));
        AudioManager.Play("balance", 0.6f);
        var note = UIKit.Label(root, Loc.T(booster ? "Daily.Day7" : "Daily.Tomorrow"), 46, new Color(0.8f, 0.86f, 1f), new Vector2(0.5f, 0.5f), new Vector2(0, -575), new Vector2(1000, 70));
        revealBits.Add(note.gameObject);
        StartCoroutine(PopIn(note.transform, 0.3f));
        yield return new WaitForSecondsRealtime(0.35f);
        // missed the golden fish: one more round for a video (once a day)
        bool retry = !secondTry && !win && Ads.CanReward;
        var take = UIKit.TextButton(root, Loc.T("Take"), new Vector2(0.5f, 0.5f), new Vector2(retry ? -235 : 0, -720), new Vector2(retry ? 420 : 460, 130), new Color(0.3f, 0.8f, 0.4f), Close);
        take.name = "btn_daily_take";
        revealBits.Add(take.gameObject);
        StartCoroutine(PopIn(take.transform, 0.3f));
        if (retry)
        {
            var again = UIKit.AdButton(root, Loc.T("Ads.Retry"), new Vector2(0.5f, 0.5f), new Vector2(235, -720), new Vector2(420, 130), () =>
                Ads.ShowRewarded(ok => { if (ok) StartCoroutine(Again()); }));
            again.name = "btn_daily_retry";
            revealBits.Add(again.gameObject);
            StartCoroutine(PopIn(again.transform, 0.3f));
        }
    }

    // the second round: everything shown is cleared, the bowls come down, a new hiding place, a new shuffle
    IEnumerator Again()
    {
        secondTry = true; canPick = false; idle = false;
        foreach (var g in revealBits) if (g != null) Destroy(g);
        revealBits.Clear();
        for (int i = 0; i < 3; i++) StartCoroutine(LiftBowl(i, false, 0.24f));
        yield return new WaitForSecondsRealtime(0.45f);
        foreach (Transform t in prizeLayer) Destroy(t.gameObject);
        picked = -1;
        goldBowl = UnityEngine.Random.Range(0, 3);
        emptyBowl = (goldBowl + UnityEngine.Random.Range(1, 3)) % 3;
        title.text = Loc.T("Daily.Title"); title.fontSize = 88;
        hint.gameObject.SetActive(true); hint.text = Loc.T("Daily.Watch");
        StartCoroutine(Run());
    }

    // what lies under a bowl: the golden fish, the small coins, or the crying kitten. It sits on the mat at the bowl's slot.
    RectTransform Prize(int bowl, float alpha)
    {
        bool gold = bowl == goldBowl;
        if (bowl == emptyBowl && GameArt.I.Has("daily_kitten"))
        {
            var k = UIKit.Image(prizeLayer, "daily_kitten", new Vector2(0.5f, 0.5f), new Vector2(SlotX(slotOf[bowl]), BowlY - 20f), new Vector2(205, 205));
            k.color = new Color(1f, 1f, 1f, Mathf.Max(alpha, 0.95f));
            StartCoroutine(Cry(k.rectTransform));
            return k.rectTransform;
        }
        float size = gold ? 200f : 170f;
        var img = UIKit.Image(prizeLayer, gold ? "daily_fish" : (GameArt.I.Has("daily_small") ? "daily_small" : "ui_balance"), new Vector2(0.5f, 0.5f),
                              new Vector2(SlotX(slotOf[bowl]), BowlY - 42f), new Vector2(size, size));
        img.color = new Color(1f, 1f, 1f, alpha);
        return img.rectTransform;
    }

    // the kitten sobs (little shudders) and tears roll down both cheeks and drip off
    IEnumerator Cry(RectTransform kitten)
    {
        // eyes on the art (share of its width / height from the top-left); the image is 424x512, fitted in 205
        Vector2 size = new Vector2(205f * 424f / 512f, 205f);
        Vector2[] eyes = { new Vector2(0.318f, 0.40f), new Vector2(0.658f, 0.40f) };
        float next = 0f, t = 0f;
        var drops = new List<(RectTransform r, Image i, float age, float vy)>();
        while (kitten != null)
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, 1f / 20f); t += dt;
            float sob = Mathf.Pow(Mathf.Max(0f, Mathf.Sin(t * 5.5f)), 8f);
            kitten.localScale = new Vector3(1f + 0.03f * sob, 1f - 0.04f * sob, 1f);
            if (t >= next)
            {
                next = t + UnityEngine.Random.Range(0.18f, 0.32f);
                var e = eyes[UnityEngine.Random.Range(0, 2)];
                var d = UIKit.Image(prizeLayer, null, new Vector2(0.5f, 0.5f),
                    kitten.anchoredPosition + new Vector2((e.x - 0.5f) * size.x, (0.5f - e.y) * size.y), new Vector2(16, 22));
                d.sprite = Kit.SoftCircle(64); d.color = new Color(0.6f, 0.85f, 1f, 0.95f);
                drops.Add((d.rectTransform, d, 0f, 0f));
            }
            for (int i = drops.Count - 1; i >= 0; i--)
            {
                var dr = drops[i]; dr.age += dt; dr.vy += 420f * dt;
                if (dr.r == null) { drops.RemoveAt(i); continue; }
                dr.r.anchoredPosition += new Vector2(0, -dr.vy * dt);
                float a = dr.age < 0.1f ? dr.age / 0.1f : Mathf.Clamp01(1f - (dr.age - 0.5f) / 0.3f);
                dr.i.color = new Color(0.6f, 0.85f, 1f, 0.95f * a);
                if (dr.age > 0.8f) { Destroy(dr.r.gameObject); drops.RemoveAt(i); } else drops[i] = dr;
            }
            yield return null;
        }
        foreach (var dr in drops) if (dr.r != null) Destroy(dr.r.gameObject);
    }

    RectTransform PrizeLabel(int bowl, int amount, bool mine)
    {
        var l = UIKit.Label(root, "+" + amount, mine ? 70 : 50, mine ? new Color(1f, 0.9f, 0.4f) : new Color(0.82f, 0.86f, 1f),
                            new Vector2(0.5f, 0.5f), new Vector2(SlotX(slotOf[bowl]), MatY - 190f), new Vector2(240, 90), true);
        return (RectTransform)l.transform;
    }

    IEnumerator LiftBowl(int b, bool up, float dur)
    {
        lifted[b] = up;
        float x = SlotX(slotOf[b]);
        float from = bowls[b].anchoredPosition.y, to = BowlY + (up ? Lift : 0f);
        float tilt = (slotOf[b] - 1) * -7f + (slotOf[b] == 1 ? 6f : 0f);
        yield return Tween(dur, k =>
        {
            float e = up ? Kit.EaseOutBack(k) : Kit.EaseInCubic(k);
            bowls[b].anchoredPosition = new Vector2(x, Mathf.LerpUnclamped(from, to, e));
            bowls[b].localRotation = Quaternion.Euler(0, 0, tilt * (up ? Mathf.Clamp01(k) : 1f - k));
        });
        if (!up)
        {   // a little squash as it lands
            AudioManager.Play("Hit", 0.3f);
            yield return Tween(0.14f, k => bowls[b].localScale = new Vector3(1f + 0.08f * Mathf.Sin(k * Mathf.PI), 1f - 0.1f * Mathf.Sin(k * Mathf.PI), 1f));
        }
    }

    void Confetti()
    {
        Vector2 at = new Vector2(SlotX(slotOf[picked]), BowlY);
        for (int i = 0; i < 46; i++)
        {
            bool coin = i % 4 == 0;
            string key = coin ? "ui_balance" : "res_confetti_" + (1 + i % 8);
            if (!GameArt.I.Has(key)) continue;
            var im = UIKit.Image(root, key, new Vector2(0.5f, 0.5f), at + new Vector2(UnityEngine.Random.Range(-60f, 60f), 0), Vector2.one * (coin ? 64f : UnityEngine.Random.Range(36f, 56f)));
            if (!coin) im.color = Color.HSVToRGB(UnityEngine.Random.value, 0.55f, 1f);
            bits.Add((im.rectTransform, new Vector2(UnityEngine.Random.Range(-850f, 850f), UnityEngine.Random.Range(1100f, 2000f)), UnityEngine.Random.Range(-400f, 400f)));
        }
    }

    void Update()
    {
        float dt = Mathf.Min(Time.unscaledDeltaTime, 1f / 20f), t = Time.unscaledTime;
        if (idle)
            for (int i = 0; i < 3; i++)
            {   // "pick me": the bowls hop a little, one after another
                float w = Mathf.Pow(Mathf.Max(0f, Mathf.Sin(t * 3.2f - slotOf[i] * 0.9f)), 6f);
                bowls[i].anchoredPosition = new Vector2(SlotX(slotOf[i]), BowlY + 22f * w);
                bowls[i].localRotation = Quaternion.Euler(0, 0, 3f * w * Mathf.Sin(t * 20f));
            }
        if (todayPip != null && picked < 0) todayPip.localScale = Vector3.one * (1f + 0.14f * Mathf.Pow(Mathf.Abs(Mathf.Sin(t * 2.6f)), 3f));
        if (flame != null) flame.localRotation = Quaternion.Euler(0, 0, 4f * Mathf.Sin(t * 2.2f));
        if (rays != null)
        {
            rays.rectTransform.localRotation = Quaternion.Euler(0, 0, -t * 9f);
            float target = picked >= 0 && picked == goldBowl ? 0.3f : 0.07f;
            var c = rays.color; c.a = Mathf.MoveTowards(c.a, target, dt * 0.8f); rays.color = c;
        }
        float floor = -root.rect.height * 0.6f;
        for (int i = bits.Count - 1; i >= 0; i--)
        {
            var b = bits[i];
            if (b.t == null) { bits.RemoveAt(i); continue; }
            b.v.y -= 2600f * dt; b.v.x *= 1f / (1f + 0.8f * dt);
            b.t.anchoredPosition += b.v * dt;
            b.t.localRotation = Quaternion.Euler(0, 0, b.t.localEulerAngles.z + b.spin * dt);
            bits[i] = b;
            if (b.t.anchoredPosition.y < floor) { Destroy(b.t.gameObject); bits.RemoveAt(i); }
        }
    }

    void Close()
    {
        onClose?.Invoke();
        Destroy(gameObject);
    }

    // ---- small tween helpers on unscaled time (the game may be paused / slowed behind the window)
    static IEnumerator Tween(float dur, Action<float> step)
    {
        float t = 0f;
        while (t < dur) { t += Mathf.Min(Time.unscaledDeltaTime, 1f / 20f); step(Mathf.Clamp01(t / dur)); yield return null; }
        step(1f);
    }
    static IEnumerator PopIn(Transform tr, float dur)
    {
        tr.localScale = Vector3.zero;
        yield return Tween(dur, k => { if (tr != null) tr.localScale = Vector3.one * Kit.EaseOutBack(k); });
    }
    static IEnumerator Punch(Transform tr)
    {
        yield return Tween(0.25f, k => { if (tr != null) tr.localScale = Vector3.one * (1f + 0.22f * Mathf.Sin(k * Mathf.PI)); });
    }
}
