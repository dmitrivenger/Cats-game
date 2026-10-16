using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// Daily login gift: opens by itself on the level map once a calendar day (Progress.GiftReady).
//  * Seven tiles: days 1-6 are gift boxes (coins or a power-up), day 7 is the big chest. Days already
//    taken carry the green "claimed" stamp, today's tile glows and bobs, the days ahead wait.
//  * "Take": today's box shakes, pops open with a flash, its coins fly up to the balance (or the
//    power-up icon flies up), the stamp slams onto the tile, and the window closes.
//  * "x2 for a video": the same reward twice (coins doubled, a power-up given twice).
//  * Missing a day starts the row again from day 1 (Progress.GiftDay).
// Built from code on the popup canvas, like every other window. Runs on unscaled time.
public class DailyGift : MonoBehaviour
{
    // what each day gives: coins, or a power-up (coins = 0); day 7 gives both
    static readonly (int coins, PowerUpType? pu, string art)[] Rewards =
    {
        (10, null, "daily_gift_1"),
        (15, null, "daily_gift_1"),
        (0, PowerUpType.Paw, "daily_gift_2"),
        (20, null, "daily_gift_2"),
        (0, PowerUpType.Swap, "daily_gift_3"),
        (25, null, "daily_gift_3"),
        (40, PowerUpType.MegaBomb, "daily_chest"),
    };

    RectTransform root, window;
    readonly RectTransform[] tiles = new RectTransform[7];
    readonly RectTransform[] icons = new RectTransform[7];
    Action onClose;
    int day;
    bool busy;
    Button takeBtn, adBtn;

    public static DailyGift Open(RectTransform parent, Action onClose)
    {
        var rt = UIKit.Rect("DailyGift", parent); UIKit.Stretch(rt);
        var d = rt.gameObject.AddComponent<DailyGift>();
        d.root = rt; d.onClose = onClose;
        d.Build();
        return d;
    }

    void Build()
    {
        day = Progress.GiftDay;
        var dim = root.gameObject.AddComponent<Image>(); dim.color = new Color(0.05f, 0.05f, 0.15f, 0.7f);
        const float W = 920f, H = 1500f;
        window = UIKit.Place(UIKit.Rect("window", root), new Vector2(0.5f, 0.5f), new Vector2(0, 20), new Vector2(W, H));
        var art = GameArt.I.S("ui_popup_panel");
        if (art != null)
        {
            var i = UIKit.Image(window, "ui_popup_panel", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(W, H), false);
            i.type = Image.Type.Sliced; i.pixelsPerUnitMultiplier = art.rect.width / W; i.raycastTarget = true;
        }
        else UIKit.Panel(window, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(W, H), UIKit.Sky, 60).raycastTarget = true;
        UIKit.Label(window, Loc.T("Gift.Title"), 62, Color.white, new Vector2(0.5f, 1f), new Vector2(0, art != null ? -255 : -60), new Vector2(760, 90));
        UIKit.Label(window, Loc.T("Gift.Sub"), 32, new Color(1f, 0.95f, 0.8f), new Vector2(0.5f, 1f), new Vector2(0, art != null ? -325 : -130), new Vector2(760, 60));

        // days 1-3, 4-6 in two rows, day 7 wide underneath
        const float tw = 220f, th = 250f, gx = 240f;
        for (int i = 0; i < 6; i++)
        {
            int row = i / 3, col = i % 3;
            Tile(i, new Vector2((col - 1) * gx, 240f - row * 275f), new Vector2(tw, th));
        }
        Tile(6, new Vector2(0, -330f), new Vector2(tw * 2f + gx - tw + 220f, th + 20f));

        takeBtn = UIKit.TextButton(window, Loc.T("Gift.Take"), new Vector2(0.5f, 0f), new Vector2(Ads.CanReward ? -170 : 0, 250), new Vector2(300, 104), new Color(0.3f, 0.8f, 0.4f), () => Take(false));
        takeBtn.name = "btn_gift_take";
        if (Ads.CanReward)
        {
            adBtn = UIKit.AdButton(window, Loc.T("Gift.Double"), new Vector2(0.5f, 0f), new Vector2(170, 250), new Vector2(300, 104), () =>
            {
                if (busy) return;
                Ads.ShowRewarded(ok => { if (ok) Take(true); });
            });
            adBtn.name = "btn_gift_x2";
        }
        StartCoroutine(UIKit.PopIn(window, 0.35f));
        AudioManager.Play("popup", 0.6f);
        StartCoroutine(Idle());
    }

    void Tile(int i, Vector2 pos, Vector2 size)
    {
        int d = i + 1;
        bool taken = d < day, today = d == day;
        var rim = UIKit.Panel(window, new Vector2(0.5f, 0.5f), pos, size, today ? new Color(1f, 0.82f, 0.3f) : UIKit.Navy, 36);
        var face = UIKit.Panel(rim.transform, new Vector2(0.5f, 0.5f), Vector2.zero, size - new Vector2(12, 12),
                               today ? new Color(1f, 0.93f, 0.7f) : taken ? new Color(0.62f, 0.72f, 0.86f) : new Color(0.82f, 0.88f, 1f), 30);
        tiles[i] = rim.rectTransform;
        UIKit.Label(rim.transform, Loc.T("Gift.Day", d), 30, today ? new Color(0.6f, 0.3f, 0.1f) : Color.white, new Vector2(0.5f, 1f), new Vector2(0, -26), new Vector2(size.x, 44));
        var r = Rewards[i];
        bool big = i == 6;
        float isz = big ? 190f : 128f;
        var icon = UIKit.Image(rim.transform, r.art, new Vector2(0.5f, 0.5f), new Vector2(big ? -130f : 0f, big ? -6f : 8f), new Vector2(isz, isz));
        icons[i] = icon.rectTransform;
        // the reward under the box: "+15" with a coin, and / or the power-up icon
        float ry = big ? -10f : -size.y * 0.5f + 34f;
        float rx = big ? 120f : 0f;
        if (r.coins > 0 && r.pu.HasValue)
        {
            UIKit.Image(rim.transform, "ui_balance", new Vector2(0.5f, 0.5f), new Vector2(rx - 80, ry + 34), new Vector2(54, 54));
            UIKit.Label(rim.transform, "+" + r.coins, 40, Color.white, new Vector2(0.5f, 0.5f), new Vector2(rx + 10, ry + 34), new Vector2(120, 50), true);
            UIKit.Image(rim.transform, GameController.PuKey(r.pu.Value), new Vector2(0.5f, 0.5f), new Vector2(rx - 20, ry - 40), new Vector2(76, 76));
        }
        else if (r.coins > 0)
        {
            UIKit.Image(rim.transform, "ui_balance", new Vector2(0.5f, 0.5f), new Vector2(rx - 40, ry), new Vector2(44, 44));
            UIKit.Label(rim.transform, "+" + r.coins, 36, Color.white, new Vector2(0.5f, 0.5f), new Vector2(rx + 18, ry), new Vector2(100, 46), true);
        }
        else if (r.pu.HasValue)
            UIKit.Image(rim.transform, GameController.PuKey(r.pu.Value), new Vector2(0.5f, 0.5f), new Vector2(rx, ry + 4), new Vector2(64, 64));
        if (taken)
        {
            icon.color = new Color(1f, 1f, 1f, 0.55f);
            UIKit.Image(rim.transform, "daily_claimed", new Vector2(0.5f, 0.5f), new Vector2(big ? -130f : 0f, 8f), new Vector2(110, 110));
        }
        if (today)
        {
            var glow = UIKit.Place(UIKit.Rect("glow", rim.transform), new Vector2(0.5f, 0.5f), icon.rectTransform.anchoredPosition, new Vector2(10, 10)).gameObject.AddComponent<PickGlow>();
            glow.Build(new Color(1f, 0.85f, 0.35f)); glow.transform.localScale = Vector3.one * (big ? 0.9f : 0.65f); glow.on = true;
            glow.transform.SetSiblingIndex(icon.transform.GetSiblingIndex());
        }
    }

    // today's box bobs and wiggles now and then, the tile beats softly
    IEnumerator Idle()
    {
        var ic = icons[day - 1]; var tile = tiles[day - 1];
        Vector2 basePos = ic.anchoredPosition;
        float t = 0f;
        while (!busy && ic != null)
        {
            t += Time.unscaledDeltaTime;
            ic.anchoredPosition = basePos + new Vector2(0, 8f * Mathf.Sin(t * 3f));
            float k = t % 2.4f;
            ic.localRotation = Quaternion.Euler(0, 0, k < 0.5f ? 8f * Mathf.Sin(k * 38f) * (1f - k / 0.5f) : 0f);
            float s = 1f + 0.03f * Mathf.Sin(t * 4f);
            tile.localScale = new Vector3(s, s, 1f);
            yield return null;
        }
    }

    void Take(bool doubled)
    {
        if (busy) return;
        busy = true;
        if (takeBtn != null) takeBtn.interactable = false;
        if (adBtn != null) adBtn.gameObject.SetActive(false);
        int d = Progress.GiftClaim();
        var r = Rewards[d - 1];
        int mult = doubled ? 2 : 1;
        if (r.coins > 0) Progress.AddBalance(r.coins * mult);
        if (r.pu.HasValue) for (int i = 0; i < mult; i++) Progress.AddCharge(r.pu.Value);
        StartCoroutine(Open(d - 1, r.coins * mult, r.pu, mult));
    }

    IEnumerator Open(int i, int coins, PowerUpType? pu, int mult)
    {
        var ic = icons[i]; var tile = tiles[i];
        tile.localScale = Vector3.one;
        // the box shakes harder and harder...
        yield return Tween(0.6f, k =>
        {
            ic.localRotation = Quaternion.Euler(0, 0, (6f + 14f * k) * Mathf.Sin(k * 60f));
            float s = 1f + 0.12f * k; ic.localScale = new Vector3(s, s, 1f);
        });
        // ...and pops: a flash, the box swells and fades, coins and stars fly out
        AudioManager.Play("chest_open", 0.8f);
        Vector3 at = ic.position;
        var flash = UIKit.Image(window, "res_win_rays", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(520, 520));
        flash.transform.position = at; flash.color = new Color(1f, 0.9f, 0.5f, 0.9f);
        StartCoroutine(Tween(0.7f, k =>
        {
            if (flash == null) return;
            flash.rectTransform.localRotation = Quaternion.Euler(0, 0, k * 60f);
            flash.rectTransform.localScale = Vector3.one * (0.5f + 0.9f * k);
            flash.color = new Color(1f, 0.9f, 0.5f, 0.9f * (1f - k));
        }));
        var icImg = ic.GetComponent<Image>();
        StartCoroutine(Tween(0.35f, k =>
        {
            float s = 1.12f + 0.5f * k; ic.localScale = new Vector3(s, s, 1f); ic.localRotation = Quaternion.identity;
            if (icImg != null) icImg.color = new Color(1f, 1f, 1f, 1f - k);
        }));
        if (coins > 0) StartCoroutine(CoinsUp(at, Mathf.Min(coins, 24)));
        if (pu.HasValue) for (int m = 0; m < mult; m++) StartCoroutine(PowerUpUp(at, pu.Value, m * 0.25f));
        yield return Wait(0.55f);
        // the stamp slams onto the tile
        var stamp = UIKit.Image(tile, "daily_claimed", new Vector2(0.5f, 0.5f), ic.anchoredPosition, new Vector2(120, 120));
        AudioManager.Play("Hit", 0.6f);
        yield return Tween(0.25f, k => { float s = Mathf.Lerp(2.2f, 1f, Kit.EaseOutBack(k)); stamp.rectTransform.localScale = new Vector3(s, s, 1f); stamp.color = new Color(1, 1, 1, Mathf.Clamp01(k * 3f)); });
        var label = UIKit.Label(window, mult > 1 ? Loc.T("Gift.ThanksX2") : Loc.T("Gift.Thanks"), 50, new Color(1f, 0.88f, 0.35f), new Vector2(0.5f, 0f), new Vector2(0, 260), new Vector2(760, 90));
        if (takeBtn != null) takeBtn.gameObject.SetActive(false);
        StartCoroutine(UIKit.PopIn(label.transform, 0.3f));
        yield return Wait(1.3f);
        yield return Tween(0.25f, k => { window.localScale = Vector3.one * (1f - 0.3f * k); });
        Destroy(root.gameObject);
        onClose?.Invoke();
    }

    // coins burst out of the box and fly up to the balance at the top of the map
    IEnumerator CoinsUp(Vector3 from, int n)
    {
        var target = new Vector3(Screen.width * 0.5f, Screen.height * 0.93f, 0);
        var cnv = root.GetComponentInParent<Canvas>();
        if (cnv != null && cnv.renderMode != RenderMode.ScreenSpaceOverlay && cnv.worldCamera != null)
            target = cnv.worldCamera.ScreenToWorldPoint(new Vector3(target.x, target.y, cnv.planeDistance));
        for (int i = 0; i < n; i++)
        {
            var c = UIKit.Image(root, "ui_balance", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(70, 70));
            c.transform.position = from;
            Vector3 mid = from + new Vector3(UnityEngine.Random.Range(-1f, 1f), UnityEngine.Random.Range(0.3f, 1.2f), 0) * 220f * root.lossyScale.x;
            StartCoroutine(Fly(c.rectTransform, from, mid, target, 0.75f, i == n - 1));
            yield return Wait(0.045f);
        }
    }

    IEnumerator PowerUpUp(Vector3 from, PowerUpType pu, float delay)
    {
        if (delay > 0f) yield return Wait(delay);
        var p = UIKit.Image(root, GameController.PuKey(pu), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(150, 150));
        p.transform.position = from;
        AudioManager.Play("purchase", 0.6f);
        Vector3 a = from;
        yield return Tween(0.9f, k =>
        {
            float s = k < 0.25f ? Kit.EaseOutBack(k / 0.25f) * 1.3f : Mathf.Lerp(1.3f, 0.6f, (k - 0.25f) / 0.75f);
            p.rectTransform.localScale = new Vector3(s, s, 1f);
            p.transform.position = a + new Vector3(0, 260f * root.lossyScale.y * Kit.EaseOutCubic(k), 0);
            p.color = new Color(1, 1, 1, k < 0.75f ? 1f : 1f - (k - 0.75f) / 0.25f);
        });
        Destroy(p.gameObject);
    }

    IEnumerator Fly(RectTransform c, Vector3 a, Vector3 mid, Vector3 b, float dur, bool last)
    {
        yield return Tween(dur, k =>
        {
            if (c == null) return;
            float e = Kit.EaseInOut(k);
            c.position = Vector3.Lerp(Vector3.Lerp(a, mid, e), Vector3.Lerp(mid, b, e), e);
            float s = Mathf.Lerp(1f, 0.6f, k); c.localScale = new Vector3(s, s, 1f);
        });
        if (c != null) Destroy(c.gameObject);
        AudioManager.Play("balance", last ? 0.6f : 0.3f);
    }

    static IEnumerator Wait(float t) { float a = 0f; while (a < t) { a += Time.unscaledDeltaTime; yield return null; } }

    static IEnumerator Tween(float dur, Action<float> f)
    {
        float a = 0f;
        while (a < dur) { a += Time.unscaledDeltaTime; f(Mathf.Clamp01(a / dur)); yield return null; }
        f(1f);
    }
}
