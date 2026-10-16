using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Infinite level map ("дорожка", Candy Crush style), built from code on the popup canvas.
//  * The road winds upward forever: x = Wave(y). Level 1 at the bottom, one level every Spacing units.
//  * Only the levels near the screen exist as objects (built/destroyed while scrolling), so level 1000
//    costs the same as level 10.
//  * Road = overlapping rounded discs (outline layer + cream layer) with paw-print steps along it.
//  * Nodes: done (pink) / current (mint, bigger, pulsing, cat marker above) / locked (grey + padlock).
//  * Background colour glides between the 6 world themes, one chapter every ChapterLen levels;
//    the game's pattern tile scrolls on top with a little parallax.
//  * Decorations (16 props + flowers) are placed pseudo-randomly but always the same for a level.
public class LevelMap : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler, IPointerDownHandler
{
    public const float Spacing = 260f;      // map units between two levels
    const float Road = 150f, Outline = 14f, DiscGap = 24f, PawGap = 64f;
    const float NodeSize = 170f, CurrentSize = 205f;
    public const int ChapterLen = 10;
    const int LockedAhead = 12;             // how many locked levels are shown above the current one

    RectTransform root, content, roadBack, roadFront, decorLayer, nodeLayer, markerRt;
    RawImage bg, pattern;
    Texture2D gradTex;
    Transform currentPulse;

    // ---- the view: the lower part of the screen shows the map flat, as it is. Above the "join" line the
    // ground runs away in a gentle perspective towards a horizon line (things get smaller and the road
    // narrows towards the middle), and a 2D sky with drifting clouds covers everything beyond it, so the
    // road seems to go on behind the horizon. Every element keeps its flat map position and is
    // re-projected each frame.
    const float SkyShare = 0.30f;       // the sky's share of the screen height
    const float Depth = 1000f;          // how gently the ground runs away (bigger = flatter)
    const float Ease = 900f;            // the bend into the perspective is spread over this much map, so the ground
                                        // rolls over like a big wheel instead of folding at a line
    const float Vanish = 600f;          // the vanishing point hides this far above the horizon line, so the road
                                        // slips under the sky while it is still clearly visible, as over a hill
    float FarCut => XOfY(TabTotal - Vanish) + 200f;   // how far past the join line things are still built (just past the horizon)
    readonly Dictionary<RectTransform, Vector3> flat = new Dictionary<RectTransform, Vector3>();   // x, y, base scale
    RawImage sky, haze; Texture2D skyTex, hazeTex; RectTransform skyRt, cloudLayer;
    readonly List<(RectTransform rt, float speed, float k)> clouds = new List<(RectTransform, float, float)>();
    readonly List<(Image img, float phase)> skyStars = new List<(Image, float)>();
    Color skyTop, skyBottom; float nightK; bool skySet;
    static Sprite cloudSprite;
    float HorizonY => ViewH * (0.5f - SkyShare);     // from the screen centre
    float JoinY => HorizonY + Vanish - TabTotal;    // below this the map is flat
    Text balanceText; int shownBalance = -1;
    Action onSettings;
    readonly Dictionary<int, List<GameObject>> built = new Dictionary<int, List<GameObject>>();
    readonly List<RectTransform> swaying = new List<RectTransform>();
    readonly List<RectTransform> pulsing = new List<RectTransform>();
    int current; Action<int> onPick;
    public Action onChest;               // tap on the stars / chest chip under the balance
    public Action onDaily;               // tap on the daily "three bowls" icon
    public Action<string> onToast;       // a short message for the player
    public Action onGateOpened;          // a star gate has just opened: the owner rebuilds the map
    // star gates
    readonly List<(Image img, float phase)> twinkle = new List<(Image, float)>();     // fireflies of the night worlds
    readonly List<Image> glows = new List<Image>();                                  // rings under levels that still miss stars
    int glowWorld = -1; float glowUntil; bool gateBusy;
    int newStars, starLevel;             // stars that fly in when the map opens after a win
    RectTransform gateChip; Text gateChipText; int gateChipShown;   // "stars for the next gate" under the top bar
    static readonly Color NightTint = new Color(0.46f, 0.52f, 0.86f), NightProp = new Color(0.74f, 0.78f, 1f);
    Text starsText; RectTransform starsChest, dailyBtn; Image dailyImg;
    int hopFrom; float hopAge;      // after a win the marker jumps from the old level to the new one
    float scroll, velocity; bool dragging;
    float lastDragTime;
    // "back to my level": a round button that shows up once the current level scrolls off screen,
    // and a big "Играть" button that starts the current level straight away (no searching at all)
    RectTransform findBtn, findArrowImg; Text findArrow; float findShow; float? autoScroll;

    // ---- worlds (see Worlds.cs): every so many levels a new place (garden, kitchen, roof...). Between two worlds the road
    // is longer by GateGap and a gate with the world's name stands there. A world that has no art yet
    // keeps the plain look (colour gradient + paw pattern + the general decorations).
    public const int WorldCount = Worlds.Count;
    const float GateGap = 360f, GateH = 560f;
    static readonly (Color fog, Color road, Color rim)[] WorldLook =
    {
        (new Color(0.38f, 0.68f, 0.26f), new Color(1f, 0.94f, 0.8f), new Color(0.66f, 0.5f, 0.3f)),      // garden
        (new Color(0.86f, 0.9f, 0.96f), new Color(1f, 0.96f, 0.86f), new Color(0.8f, 0.6f, 0.45f)),      // kitchen
        (new Color(0.9f, 0.52f, 0.38f), new Color(1f, 0.95f, 0.85f), new Color(0.55f, 0.33f, 0.28f)),    // rooftop
        (new Color(0.96f, 0.86f, 0.6f), new Color(1f, 0.98f, 0.9f), new Color(0.82f, 0.66f, 0.42f)),     // beach
        (new Color(0.9f, 0.63f, 0.42f), new Color(1f, 0.95f, 0.84f), new Color(0.5f, 0.32f, 0.2f)),       // attic
        (new Color(0.9f, 0.95f, 1f), new Color(0.86f, 0.93f, 1f), new Color(0.55f, 0.7f, 0.88f)),        // winter yard
        (new Color(0.6f, 0.67f, 0.71f), new Color(1f, 0.95f, 0.84f), new Color(0.4f, 0.42f, 0.5f)),       // pier
        (new Color(1f, 0.82f, 0.86f), new Color(1f, 0.97f, 0.9f), new Color(0.85f, 0.55f, 0.6f)),        // cat café
        (new Color(0.7f, 0.8f, 0.9f), new Color(1f, 0.96f, 0.86f), new Color(0.72f, 0.52f, 0.38f)),      // toy room
        (new Color(0.56f, 0.64f, 0.3f), new Color(1f, 0.94f, 0.8f), new Color(0.58f, 0.36f, 0.2f)),      // autumn park
        (new Color(0.76f, 0.9f, 0.9f), new Color(1f, 0.98f, 0.94f), new Color(0.42f, 0.62f, 0.68f)),     // bathroom
        (new Color(0.94f, 0.88f, 0.88f), new Color(0.93f, 0.84f, 0.7f), new Color(0.6f, 0.34f, 0.3f)),   // japanese garden
    };
    // the blank board on each gate picture: centre x, centre y (from the top), width, height - as shares of the picture
    static readonly Vector4[] GatePlate =
    {
        new Vector4(0.5f, 0.161f, 0.36f, 0.13f),     // garden
        new Vector4(0.502f, 0.292f, 0.436f, 0.198f), // kitchen
        new Vector4(0.484f, 0.167f, 0.41f, 0.153f),  // rooftop
        new Vector4(0.482f, 0.205f, 0.4f, 0.151f),   // beach
        new Vector4(0.501f, 0.14f, 0.43f, 0.267f),   // attic
        new Vector4(0.484f, 0.118f, 0.488f, 0.219f), // winter yard
        new Vector4(0.48f, 0.29f, 0.462f, 0.173f),   // pier
        new Vector4(0.5f, 0.231f, 0.424f, 0.257f),   // cat café
        new Vector4(0.5f, 0.355f, 0.44f, 0.19f),     // toy room
        new Vector4(0.5f, 0.393f, 0.46f, 0.21f),     // autumn park
        new Vector4(0.501f, 0.25f, 0.37f, 0.24f),    // bathroom
        new Vector4(0.5f, 0.27f, 0.46f, 0.2f),       // japanese garden
    };
    // how many map units one repeat of the ground picture covers
    static readonly float[] FloorTile = { 620f, 640f, 700f, 1150f, 1150f, 950f, 1150f, 640f, 700f, 900f, 900f, 1000f };
    // the free passage between the posts, as a share of each gate picture's width
    static readonly float[] GateOpen = { 0.363f, 0.385f, 0.518f, 0.46f, 0.62f, 0.54f, 0.572f, 0.456f, 0.28f, 0.5f, 0.36f, 0.42f };
    public static int WorldOf(int level) => Worlds.Of(level);                                             // 0-based, keeps growing
    static string WKey(int world, string what) => "world_" + (Worlds.Art(world) + 1) + "_" + what;
    static bool WorldHasArt(int world) => GameArt.I.Has(WKey(world, "floor"));
    static int CountKeys(int world, string what) { int n = 0; while (GameArt.I.Has(WKey(world, what + "_" + (n + 1)))) n++; return n; }
    // map height of a world's first level
    static float WorldY(int world) => (Worlds.First(world) - 1) * Spacing + world * GateGap;
    // the gate INTO a world stands in the middle of the long stretch before its first level
    static float GateY(int world) => WorldY(world) - (Spacing + GateGap) * 0.5f;
    // the world whose stretch of road (from its first level up to the next world's first level) holds this height
    static int WorldAtY(float y)
    {
        if (y <= 0f) return 0;
        int w = Worlds.Of(Mathf.Max(1, Mathf.FloorToInt(y / Spacing) + 1));     // a guess from above (gates only add height)
        while (w > 0 && WorldY(w) > y) w--;
        while (WorldY(w + 1) <= y) w++;
        return w;
    }
    // the gate nearest to this height
    static float NearestGateY(float y)
    {
        int w = WorldAtY(y);
        float a = GateY(w), b = GateY(w + 1);
        return Mathf.Abs(y - a) < Mathf.Abs(y - b) ? a : b;
    }
    // fractional 0-based level index at a map height (the inverse of YOf)
    static float LevelAt(float y)
    {
        if (y <= 0f) return y / Spacing;
        int w = WorldAtY(y);
        return Worlds.First(w) - 1 + Mathf.Min((y - WorldY(w)) / Spacing, Worlds.Length(w) - 0.001f);
    }
    RawImage floorA, floorB;

    public static float YOf(int level) => (level - 1) * Spacing + WorldOf(level) * GateGap;
    static float RawWave(float y) { float t = y / Spacing; return 250f * Mathf.Sin(t * 0.85f) + 45f * Mathf.Sin(t * 2.3f + 1f); }
    // the road's x at a map height. Around a gate it runs straight (so it passes cleanly between the posts)
    // and bends back into its usual wave above and below.
    public static float Wave(float y)
    {
        float gy = NearestGateY(y);
        float d = Mathf.Abs(y - gy);
        if (d >= GateStraight + GateBlend) return RawWave(y);
        return Mathf.Lerp(RawWave(gy), RawWave(y), Mathf.SmoothStep(0f, 1f, (d - GateStraight) / GateBlend));
    }
    const float GateStraight = 270f, GateBlend = 260f;
    // decorations keep this far (up and down the road) from a gate, so the gate stands in the open
    const float GateClear = 400f;
    static bool NearGate(float y) => Mathf.Abs(y - NearestGateY(y)) < GateClear;
    static Vector2 P(float y) => new Vector2(Wave(y), y);

    // newStars / starLevel: the map opens right after a win that brought this many NEW stars on that level;
    // they fly from the middle of the screen into the counters
    public static LevelMap Open(RectTransform parent, int currentLevel, Action<int> onPick, Action onClose, int hopFrom = 0, Action onSettings = null, int newStars = 0, int starLevel = 0)
    {
        var rt = UIKit.Rect("LevelMap", parent); UIKit.Stretch(rt);
        var m = rt.gameObject.AddComponent<LevelMap>();
        m.hopFrom = hopFrom > 0 && hopFrom < currentLevel ? hopFrom : 0;
        m.newStars = Mathf.Clamp(newStars, 0, 3); m.starLevel = starLevel;
        m.onSettings = onSettings;
        m.Build(rt, currentLevel, onPick, onClose);
        return m;
    }

    void Build(RectTransform rt, int currentLevel, Action<int> pick, Action onClose)
    {
        root = rt; current = Mathf.Max(1, currentLevel); onPick = pick;

        gradTex = Kit.Gradient(Background.Themes[0].bottom, Background.Themes[0].top);
        bg = new GameObject("bg", typeof(RectTransform)).AddComponent<RawImage>();
        bg.transform.SetParent(root, false); UIKit.Stretch(bg.rectTransform);
        bg.texture = gradTex; bg.raycastTarget = true;       // catches the drags

        var ps = GameArt.I.S("bg_pattern");
        if (ps != null)
        {
            pattern = new GameObject("pattern", typeof(RectTransform)).AddComponent<RawImage>();
            pattern.transform.SetParent(root, false); UIKit.Stretch(pattern.rectTransform);
            ps.texture.wrapMode = TextureWrapMode.Repeat;
            pattern.texture = ps.texture; pattern.color = new Color(1, 1, 1, 0.18f); pattern.raycastTarget = false;
        }

        floorA = FloorLayer("floorA"); floorB = FloorLayer("floorB");

        content = UIKit.Rect("content", root);
        content.anchorMin = content.anchorMax = new Vector2(0.5f, 0.5f); content.sizeDelta = Vector2.zero;
        roadBack = Layer("roadBack"); roadFront = Layer("roadFront"); decorLayer = Layer("decor"); nodeLayer = Layer("nodes");

        BuildSky();
        if (onClose != null)
            UIKit.ArtButton(root, "ui_close", new Vector2(1f, 1f), new Vector2(-90, -130), 130, () => onClose(), "X");
        if (onSettings != null)
            UIKit.ArtButton(root, "ui_sett", new Vector2(0f, 1f), new Vector2(90, -130), 130, () => onSettings(), "⚙");
        var chip = UIKit.Panel(root, new Vector2(0.5f, 1f), new Vector2(0, -130), new Vector2(300, 96), new Color(0.13f, 0.18f, 0.36f, 0.88f), 48);
        UIKit.Panel(chip.transform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(288, 84), new Color(1f, 0.8f, 0.3f, 0.35f), 42);
        UIKit.Image(chip.transform, "ui_balance", new Vector2(0f, 0.5f), new Vector2(58, 0), new Vector2(74, 74));
        balanceText = UIKit.Label(chip.transform, Progress.Balance.ToString(), 54, Color.white, new Vector2(0.5f, 0.5f), new Vector2(34, 0), new Vector2(190, 80), true);

        BuildStarsChip();
        BuildPlayButtons();
        BuildDaily();
        scroll = HomeScroll;
        BuildHero();                 // current level a little below the middle of the screen
        Refresh(true);
        if (newStars > 0) StartCoroutine(FlyStars());
        if (Worlds.Locked(current)) StartCoroutine(GateAhead());
    }

    RawImage FloorLayer(string n)
    {
        var img = new GameObject(n, typeof(RectTransform)).AddComponent<CurvedImage>();
        img.transform.SetParent(root, false); UIKit.Stretch(img.rectTransform);
        img.raycastTarget = false; img.color = new Color(1, 1, 1, 0);
        return img;
    }

    RectTransform Layer(string n)
    {
        var l = UIKit.Rect(n, content);
        l.anchorMin = l.anchorMax = new Vector2(0.5f, 0.5f); l.sizeDelta = Vector2.zero;
        return l;
    }

    // where the map rests: the current level a little below the middle; in front of a closed gate - the gate itself
    float HomeScroll => Worlds.Locked(current) ? GateY(Worlds.Open + 1) + 200f : YOf(current) + 300f;

    // under the balance: stars collected towards the next chest ("7/10") and the chest itself.
    // When the chest is ready it beats like a heart; a tap opens it.
    void BuildStarsChip()
    {
        var chip = UIKit.Panel(root, new Vector2(0.5f, 1f), new Vector2(-190, -250), new Vector2(430, 96), new Color(0.13f, 0.18f, 0.36f, 0.88f), 48);
        chip.raycastTarget = true; chip.name = "btn_map_stars";
        starsChip = chip.rectTransform;
        UIKit.Panel(chip.transform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(418, 84), new Color(1f, 0.8f, 0.3f, 0.35f), 42);
        UIKit.Image(chip.transform, "ui_star", new Vector2(0f, 0.5f), new Vector2(56, 1), new Vector2(67, 67));
        starsText = UIKit.Label(chip.transform, "", 52, Color.white, new Vector2(0.5f, 0.5f), new Vector2(-22, 0), new Vector2(190, 80), true);
        starsChest = UIKit.Image(chip.transform, "map_chest", new Vector2(1f, 0.5f), new Vector2(-62, 2), new Vector2(90, 90)).rectTransform;
        var b = chip.gameObject.AddComponent<Button>(); b.targetGraphic = chip; b.transition = Selectable.Transition.None;
        b.onClick.AddListener(() => { AudioManager.PlayClick(); onChest?.Invoke(); });
        chestShown = Progress.ChestProgress;
        RefreshStars();
        BuildGateChip();
    }

    // next to it: the padlock and how many stars the player has of those the next gate asks for.
    // A tap shows which levels can still give stars.
    void BuildGateChip()
    {
        int from = Worlds.Open;
        var chip = UIKit.Panel(root, new Vector2(0.5f, 1f), new Vector2(245, -250), new Vector2(380, 96), new Color(0.13f, 0.18f, 0.36f, 0.88f), 48);
        chip.raycastTarget = true; chip.name = "btn_map_gate";
        UIKit.Panel(chip.transform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(368, 84), new Color(1f, 0.8f, 0.3f, 0.35f), 42);
        UIKit.Image(chip.transform, "ui_lock", new Vector2(0f, 0.5f), new Vector2(54, 2), new Vector2(76, 76));
        gateChipShown = Worlds.Stars(from);
        gateChipText = UIKit.Label(chip.transform, "", 48, Color.white, new Vector2(0.5f, 0.5f), new Vector2(34, 0), new Vector2(260, 80), true);
        var b = chip.gameObject.AddComponent<Button>(); b.targetGraphic = chip; b.transition = Selectable.Transition.None;
        b.onClick.AddListener(() => { AudioManager.PlayClick(); GateHint(Worlds.Open + 1); });
        gateChip = chip.rectTransform;
        // the stars that are about to fly in are not counted yet: each one ticks its counter as it lands
        if (newStars > 0)
        {
            chestShown = Mathf.Max(0, chestShown - newStars);
            if (Worlds.Of(starLevel) == from) gateChipShown = Mathf.Max(0, gateChipShown - newStars);
        }
        RefreshStars();
    }

    RectTransform starsChip; int chestShown; bool starsFlying;

    public void RefreshStars()
    {
        if (!starsFlying && newStars == 0) { chestShown = Progress.ChestProgress; gateChipShown = Worlds.Stars(Worlds.Open); }
        if (starsText != null) starsText.text = chestShown + "/" + Progress.ChestStars;
        if (gateChipText != null)
        {
            int need = Worlds.StarsNeed(Worlds.Open);
            gateChipText.text = gateChipShown + "/" + need;
            gateChipText.color = gateChipShown >= need ? new Color(0.6f, 1f, 0.55f) : Color.white;
        }
    }

    // New stars of the win just made fly from the middle of the screen: each one lands in the chest counter,
    // and - when the level belongs to the world the player is working through - a twin lands in the gate counter.
    IEnumerator FlyStars()
    {
        starsFlying = true;
        bool toGate = Worlds.Of(starLevel) == Worlds.Open && gateChip != null;
        int n = newStars; newStars = 0;
        yield return new WaitForSecondsRealtime(0.35f);
        for (int i = 0; i < n; i++)
        {
            StartCoroutine(FlyStar(new Vector2((i - (n - 1) * 0.5f) * 150f, 120f), starsChip, false));
            if (toGate) StartCoroutine(FlyStar(new Vector2((i - (n - 1) * 0.5f) * 150f, 120f), gateChip, true));
            yield return new WaitForSecondsRealtime(0.22f);
        }
        yield return new WaitForSecondsRealtime(0.85f);
        starsFlying = false;
        RefreshStars();
    }

    IEnumerator FlyStar(Vector2 from, RectTransform chip, bool gate)
    {
        var img = UIKit.Image(root, "ui_star", new Vector2(0.5f, 0.5f), from, new Vector2(130, 130));
        var rt = img.rectTransform;
        yield return TweenU(0.22f, k => rt.localScale = Vector3.one * Kit.EaseOutBack(k));
        Vector3 a = rt.position, b = chip.position; b.x -= chip.rect.width * 0.5f * chip.lossyScale.x * 0.72f;   // the icon at the chip's left end
        Vector3 mid = (a + b) * 0.5f + new Vector3(gate ? 1.2f : -1.2f, 0.6f, 0f);
        yield return TweenU(0.55f, k =>
        {
            float e = Kit.EaseInOut(k);
            rt.position = Vector3.Lerp(Vector3.Lerp(a, mid, e), Vector3.Lerp(mid, b, e), e);
            rt.localScale = Vector3.one * Mathf.Lerp(1f, 0.5f, e);
            rt.localRotation = Quaternion.Euler(0, 0, 360f * e);
        });
        Destroy(img.gameObject);
        if (gate) gateChipShown++; else chestShown = Mathf.Min(Progress.ChestStars, chestShown + 1);
        RefreshStars();
        AudioManager.Play("balance", 0.5f);
        StartCoroutine(TweenU(0.25f, k => { if (chip != null) chip.localScale = Vector3.one * (1f + 0.14f * Mathf.Sin(k * Mathf.PI)); }));
    }

    // daily "three bowls" in the bottom-left corner: bright and beating while today's go is unused, grey after it
    void BuildDaily()
    {
        if (!GameArt.I.Has("daily_icon")) return;
        var b = UIKit.ArtButton(root, "daily_icon", new Vector2(0f, 0f), new Vector2(125, 235), 190, () => onDaily?.Invoke());
        b.name = "btn_map_daily";
        dailyBtn = (RectTransform)b.transform; dailyImg = b.GetComponent<Image>();
        Destroy(b.GetComponent<Squish>());           // the icon is animated from Update
        RefreshDaily();
    }

    public void RefreshDaily()
    {
        if (dailyImg == null) return;
        bool ready = Progress.DailyReady;
        var s = GameArt.I.S(ready || !GameArt.I.Has("daily_icon_done") ? "daily_icon" : "daily_icon_done");
        if (s != null) dailyImg.sprite = s;
    }

    void PlayCurrent()
    {
        if (gateBusy) return;
        if (Worlds.Locked(current)) { GateHint(Worlds.Open + 1); return; }
        onPick?.Invoke(current);
    }

    void BuildPlayButtons()
    {
        // big "Играть" at the bottom: plays the current level
        var normal = GameArt.I.S(Loc.Art("ui_play_big"));
        if (normal != null)
        {
            float w = 520f, h = w * normal.rect.height / normal.rect.width;
            var img = UIKit.Image(root, Loc.Art("ui_play_big"), new Vector2(0.5f, 0f), new Vector2(0, 120 + h * 0.5f), new Vector2(w, h));
            img.raycastTarget = true; img.name = "btn_map_play";
            var b = img.gameObject.AddComponent<Button>(); b.targetGraphic = img;
            var pressed = GameArt.I.S(Loc.Art("ui_play_big_pressed"));
            if (pressed != null) { b.transition = Selectable.Transition.SpriteSwap; b.spriteState = new SpriteState { pressedSprite = pressed, highlightedSprite = normal, selectedSprite = normal }; }
            else { b.transition = Selectable.Transition.None; img.gameObject.AddComponent<Squish>(); }
            b.onClick.AddListener(() => { AudioManager.PlayClick(); PlayCurrent(); });
        }
        else UIKit.TextButton(root, Loc.T("Play"), new Vector2(0.5f, 0f), new Vector2(0, 200), new Vector2(480, 140), new Color(0.3f, 0.8f, 0.4f), PlayCurrent);

        // round "back to my level" button, bottom-right: the current level's number on a green badge and an
        // arrow that shows which way that level lies
        var ringRt = UIKit.Place(UIKit.Rect("btn_map_find", root), new Vector2(1f, 0f), new Vector2(-120, 240), new Vector2(170, 170));
        var ring = ringRt.gameObject.AddComponent<Image>();
        bool findArt = GameArt.I.Has("ui_find_btn");
        ring.sprite = findArt ? GameArt.I.S("ui_find_btn") : Kit.RoundedRect(128, 63);
        ring.preserveAspect = true; ring.raycastTarget = true;
        if (!findArt) ring.color = new Color(0.25f, 0.8f, 0.35f);
        UIKit.Label(ring.transform, current.ToString(), current >= 100 ? 46 : 58, Color.white, new Vector2(0.5f, 0.5f), new Vector2(0, -12), new Vector2(150, 80), true);
        if (GameArt.I.Has("ui_find_arrow")) findArrowImg = UIKit.Image(ring.transform, "ui_find_arrow", new Vector2(0.5f, 0.5f), new Vector2(0, -112), new Vector2(64, 64)).rectTransform;
        else findArrow = UIKit.Label(ring.transform, "▼", 44, Color.white, new Vector2(0.5f, 0f), new Vector2(0, -30), new Vector2(80, 50));
        var fb = ring.gameObject.AddComponent<Button>(); fb.targetGraphic = ring; fb.transition = Selectable.Transition.None;
        ring.gameObject.AddComponent<Squish>();
        fb.onClick.AddListener(() => { AudioManager.PlayClick(); velocity = 0f; autoScroll = HomeScroll; });
        findBtn = ring.rectTransform;
        findBtn.localScale = Vector3.zero;
    }

    float MinScroll => YOf(1) + ViewH * 0.5f - 660f;      // low enough to see the first gate
    float MaxScroll => Mathf.Max(MinScroll, YOf(current + LockedAhead) - ViewH * 0.25f);
    float ViewH => root.rect.height > 1 ? root.rect.height : 1920f;

    // ---------------------------------------------------------------- input
    public void OnPointerDown(PointerEventData e) { velocity = 0f; autoScroll = null; }
    public void OnBeginDrag(PointerEventData e) { if (gateBusy) return; dragging = true; velocity = 0f; }
    public void OnDrag(PointerEventData e)
    {
        if (gateBusy || !dragging) return;
        float dy = e.delta.y * UnitsPerPixel();
        float s = scroll - dy;
        // rubber band past the ends
        if (s < MinScroll || s > MaxScroll) s = scroll - dy * 0.35f;
        float dt = Mathf.Max(0.001f, Time.unscaledTime - lastDragTime);
        velocity = Mathf.Lerp(velocity, (s - scroll) / dt, 0.4f);
        lastDragTime = Time.unscaledTime;
        scroll = s;
    }
    public void OnEndDrag(PointerEventData e) { dragging = false; if (Time.unscaledTime - lastDragTime > 0.08f) velocity = 0f; }

    float UnitsPerPixel()
    {
        var cam = Camera.main;
        // world-space canvas glued to the camera: canvas units per screen pixel
        return cam != null && cam.pixelHeight > 0 ? root.rect.height / cam.pixelHeight : 1f;
    }

    void Update()
    {
        float dt = Time.unscaledDeltaTime;
        if (autoScroll.HasValue && !dragging)
        {
            // glide back to the current level (fast over long distances, soft landing)
            scroll = Mathf.Lerp(scroll, autoScroll.Value, 1f - Mathf.Exp(-6f * dt));
            if (Mathf.Abs(scroll - autoScroll.Value) < 2f) { scroll = autoScroll.Value; autoScroll = null; }
        }
        else if (!dragging)
        {
            scroll += velocity * dt;
            velocity *= Mathf.Exp(-3.5f * dt);
            if (scroll < MinScroll) { scroll = Mathf.Lerp(scroll, MinScroll, 1f - Mathf.Exp(-12f * dt)); velocity = 0f; }
            if (scroll > MaxScroll) { scroll = Mathf.Lerp(scroll, MaxScroll, 1f - Mathf.Exp(-12f * dt)); velocity = 0f; }
        }
        Refresh(false);
        UpdateClouds(dt);

        float t = Time.unscaledTime;
        if (findBtn != null)
        {
            // visible only while the current level is off screen; the arrow points to where it is
            float off = HomeScroll - scroll;
            bool show = Mathf.Abs(off) > ViewH * 0.4f && !autoScroll.HasValue;
            findShow = Mathf.MoveTowards(findShow, show ? 1f : 0f, dt * 5f);
            float bob = 1f + 0.05f * Mathf.Sin(t * 5f);
            findBtn.localScale = Vector3.one * Kit.EaseOutBack(findShow) * (findShow >= 1f ? bob : 1f);
            if (findArrow != null) findArrow.text = off > 0 ? "▲" : "▼";
            if (findArrowImg != null)
            {   // the picture points up; below the badge when the level is further down the map, above it when it is up
                bool up = off > 0;
                findArrowImg.anchoredPosition = new Vector2(0, up ? 118f : -112f);
                findArrowImg.localRotation = Quaternion.Euler(0, 0, up ? 0f : 180f);
            }
        }
        float heart = Mathf.Pow(Mathf.Abs(Mathf.Sin(t * 2.6f)), 3f);
        // saved state is read a few times a second, not every frame
        if (t >= nextStateCheck)
        {
            nextStateCheck = t + 0.3f;
            chestReady = Progress.PendingChest > 0; dailyReady = Progress.DailyReady;
            if (balanceText != null && shownBalance != Progress.Balance) { shownBalance = Progress.Balance; balanceText.text = shownBalance.ToString(); }
        }
        if (starsChest != null)
        {
            bool ready = chestReady;
            starsChest.localScale = Vector3.one * (ready ? 1f + 0.22f * heart : 1f);
            starsChest.localRotation = Quaternion.Euler(0, 0, ready ? 7f * Mathf.Sin(t * 1.7f) : 0f);
            if (Time.frameCount % 20 == 0 && !starsFlying) RefreshStars();
        }
        if (dailyBtn != null)
        {
            bool ready = dailyReady;
            dailyBtn.localScale = Vector3.one * (ready ? 1f + 0.12f * heart : 0.9f);
            dailyBtn.localRotation = Quaternion.Euler(0, 0, ready ? 5f * Mathf.Sin(t * 1.9f) : 0f);
        }
        if (currentPulse != null) { float s = 1f + 0.06f * Mathf.Sin(t * 4f); currentPulse.localScale = new Vector3(s, s, 1f); }
        if (heroRt != null) UpdateHero(Time.unscaledDeltaTime, t);
        else if (markerRt == null && hopFrom > 0)
        {   // no hero art: after a win the new current level simply pops
            hopAge += Time.unscaledDeltaTime;
            if (hopAge > 0.6f) { hopFrom = 0; if (currentPulse != null) StartCoroutine(PopNode(currentPulse)); AudioManager.Play("balance", 0.6f); }
        }
        if (markerRt != null)
        {
            // beside the current node, on the side away from the road's bend, so it never covers the next level
            Vector2 target = MarkerSpot(current, t);
            if (hopFrom > 0)
            {
                hopAge += Time.unscaledDeltaTime;
                float k = Mathf.Clamp01((hopAge - 0.45f) / 0.6f);           // short pause, then the jump
                Vector2 from = MarkerSpot(hopFrom, 0f);
                Vector2 p = Vector2.Lerp(from, target, Kit.EaseInOut(k)) + new Vector2(0, 170f * Mathf.Sin(k * Mathf.PI));
                float sq = k > 0.95f ? 1f + 0.15f * Mathf.Sin((k - 0.95f) / 0.05f * Mathf.PI) : 1f;
                Project(markerRt, p, 1f, new Vector2(sq, 2f - sq));
                if (k >= 1f) { hopFrom = 0; if (currentPulse != null) StartCoroutine(PopNode(currentPulse)); AudioManager.Play("balance", 0.6f); }
            }
            else Project(markerRt, target, 1f, Vector2.one);
            float side = P(YOf(current)).x > 0 ? -1f : 1f;
            markerRt.localRotation = Quaternion.Euler(0, 0, -side * 8f + 3f * Mathf.Sin(t * 2f));
        }
        for (int i = swaying.Count - 1; i >= 0; i--)
        {
            if (swaying[i] == null) { swaying.RemoveAt(i); continue; }
            swaying[i].localRotation = Quaternion.Euler(0, 0, 5f * Mathf.Sin(t * 1.6f + i * 1.3f));
        }
        for (int i = twinkle.Count - 1; i >= 0; i--)
        {
            var tw = twinkle[i];
            if (tw.img == null) { twinkle.RemoveAt(i); continue; }
            float a = 0.25f + 0.75f * Mathf.Pow(Mathf.Abs(Mathf.Sin(t * 1.3f + tw.phase)), 2f);
            tw.img.color = new Color(1f, 0.95f, 0.45f, a);
        }
        for (int i = glows.Count - 1; i >= 0; i--)
        {
            var g = glows[i];
            if (g == null) { glows.RemoveAt(i); continue; }
            float left = glowUntil - Time.unscaledTime;
            if (left <= 0f) { Destroy(g.gameObject); glows.RemoveAt(i); continue; }
            float a = Mathf.Min(1f, left) * (0.55f + 0.45f * Mathf.Sin(t * 5f));
            g.color = new Color(1f, 0.85f, 0.3f, a);
            g.rectTransform.localScale = Vector3.one * (1f + 0.08f * Mathf.Sin(t * 5f));
        }
        for (int i = pulsing.Count - 1; i >= 0; i--)
        {
            if (pulsing[i] == null) { pulsing.RemoveAt(i); continue; }
            float beat = Mathf.Pow(Mathf.Abs(Mathf.Sin(t * 2.6f)), 3f);   // a heartbeat-like swell
            pulsing[i].localScale = Vector3.one * (1f + 0.16f * beat);
        }
    }

    // ---------------------------------------------------------------- projection
    // below the join line: flat. Above it: a perspective of flat ground - the farther, the smaller, shrinking
    // towards the horizon. The perspective doesn't switch on at the join line: it grows in smoothly over the
    // first Ease units of map (x = map distance past the join, F = its "eased" distance), so the road bends
    // over like a wheel. Scale r = Depth / (Depth + F); the screen height is the integral of r^2 (ground
    // seen at a slant is squeezed by r^2), kept in a table.
    const float TabStep = 4f, TabMax = 8000f;
    static float[] tabY; static float tabTotal;
    static float EaseF(float x) => x < Ease ? x * x / (2f * Ease) : x - Ease * 0.5f;
    static float ScaleAt(float x) => Depth / (Depth + EaseF(Mathf.Max(0f, x)));
    static float TabTotal { get { BuildTab(); return tabTotal; } }
    static void BuildTab()
    {
        if (tabY != null) return;
        int n = (int)(TabMax / TabStep) + 1;
        var t = new float[n]; float acc = 0f;
        for (int i = 1; i < n; i++)
        {
            float a = ScaleAt((i - 1) * TabStep), b = ScaleAt(i * TabStep);
            acc += TabStep * 0.5f * (a * a + b * b);
            t[i] = acc;
        }
        tabTotal = acc + Depth * ScaleAt(TabMax);     // the rest, out to infinity, in closed form
        tabY = t;
    }
    // screen height above the join line for a map distance x past it, and back
    static float YOfX(float x)
    {
        BuildTab();
        if (x <= 0f) return x;
        if (x >= TabMax) return tabTotal - Depth * ScaleAt(x);
        float f = x / TabStep; int i = (int)f;
        return Mathf.Lerp(tabY[i], tabY[Mathf.Min(i + 1, tabY.Length - 1)], f - i);
    }
    static float XOfY(float y)
    {
        BuildTab();
        if (y <= 0f) return y;
        int last = tabY.Length - 1;
        if (y >= tabY[last])
        {
            float r = Mathf.Max(0.02f, (tabTotal - y) / Depth);
            return Depth / r - Depth + Ease * 0.5f;
        }
        int lo = 0, hi = last;
        while (hi - lo > 1) { int m = (lo + hi) >> 1; if (tabY[m] <= y) lo = m; else hi = m; }
        float k = Mathf.InverseLerp(tabY[lo], tabY[hi], y);
        return (lo + k) * TabStep;
    }

    Vector3 Drum(Vector2 p)
    {
        float d = p.y - scroll, j = JoinY;
        if (d <= j) return new Vector3(p.x, scroll + d, 1f);
        float x = d - j, r = ScaleAt(x);
        return new Vector3(p.x * r, scroll + j + YOfX(x), r);
    }

    // the inverse: a point on the screen (relative to its centre) -> where it lies on the flat map
    Vector2 FlatAt(Vector2 screen)
    {
        float j = JoinY;
        if (screen.y <= j) return new Vector2(screen.x, scroll + screen.y);
        float x = XOfY(screen.y - j);
        float maxX = Depth * 9f + Ease * 0.5f;                     // r = 0.1: above the horizon, hidden by the sky anyway
        x = Mathf.Min(x, maxX);
        return new Vector2(screen.x / ScaleAt(x), scroll + j + x);
    }

    void Project(RectTransform rt, Vector2 flatPos, float baseScale, Vector2 squash)
    {
        var w = Drum(flatPos);
        rt.anchoredPosition = new Vector2(w.x, w.y);
        rt.localScale = new Vector3(baseScale * w.z * squash.x, baseScale * w.z * squash.y, 1f);
    }

    void ApplyDrum()
    {
        List<RectTransform> dead = null;
        foreach (var kv in flat)
        {
            if (kv.Key == null) { (dead ??= new List<RectTransform>()).Add(kv.Key); continue; }
            Project(kv.Key, new Vector2(kv.Value.x, kv.Value.y), kv.Value.z, Vector2.one);
        }
        if (dead != null) foreach (var k in dead) flat.Remove(k);
    }

    // ---------------------------------------------------------------- sky
    // The top 30% of the screen: a sky gradient whose colours come from code (morning / day / evening
    // along a world, dark blue in night worlds), clouds drifting across it, stars at night. A soft haze
    // in the sky's colour just under the horizon line lets the far road melt into the distance.
    void BuildSky()
    {
        hazeTex = new Texture2D(1, 64, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        haze = new GameObject("haze", typeof(RectTransform)).AddComponent<RawImage>();
        haze.transform.SetParent(root, false);
        var hr = haze.rectTransform;
        hr.anchorMin = new Vector2(0f, 1f); hr.anchorMax = new Vector2(1f, 1f); hr.pivot = new Vector2(0.5f, 1f);
        haze.texture = hazeTex; haze.raycastTarget = false;

        skyTex = Kit.Gradient(Color.white, Color.white);
        sky = new GameObject("sky", typeof(RectTransform)).AddComponent<RawImage>();
        sky.transform.SetParent(root, false);
        skyRt = sky.rectTransform;
        skyRt.anchorMin = new Vector2(0f, 1f); skyRt.anchorMax = new Vector2(1f, 1f); skyRt.pivot = new Vector2(0.5f, 1f);
        sky.texture = skyTex; sky.raycastTarget = false;
        sky.gameObject.AddComponent<RectMask2D>();          // clouds never spill onto the ground
        LayoutSky();

        // night stars (invisible by day)
        var rng = new System.Random(77);
        for (int i = 0; i < 26; i++)
        {
            float s = 8f + (float)rng.NextDouble() * 10f;
            var rt = UIKit.Place(UIKit.Rect("star", skyRt), new Vector2((float)rng.NextDouble(), 0.25f + 0.75f * (float)rng.NextDouble()), Vector2.zero, new Vector2(s, s));
            var im = rt.gameObject.AddComponent<Image>(); im.sprite = Kit.SoftCircle(32); im.raycastTarget = false; im.color = new Color(1, 1, 1, 0);
            skyStars.Add((im, (float)rng.NextDouble() * 6.28f));
        }

        cloudLayer = UIKit.Rect("clouds", skyRt); UIKit.Stretch(cloudLayer);
        int nArt = 0; while (GameArt.I.Has("map_cloud_" + (nArt + 1))) nArt++;
        float W = Mathf.Max(1080f, root.rect.width);
        for (int i = 0; i < 7; i++)
        {
            float k = i / 6f;                                    // 0 = far, low and slow; 1 = near, high and faster
            var sp = nArt > 0 ? GameArt.I.S("map_cloud_" + (1 + i * nArt / 7)) : CloudSprite();   // art goes small -> big, far -> near
            float w = Mathf.Lerp(200f, 430f, k) * (0.85f + 0.3f * (float)rng.NextDouble());
            float h = w * sp.rect.height / sp.rect.width;
            var rt = UIKit.Place(UIKit.Rect("cloud", cloudLayer), new Vector2(0.5f, 0f),
                                 new Vector2(((float)rng.NextDouble() - 0.5f) * (W + w), 0f), new Vector2(w, h));
            var im = rt.gameObject.AddComponent<Image>(); im.sprite = sp; im.preserveAspect = true; im.raycastTarget = false;
            if (rng.NextDouble() < 0.5) rt.localScale = new Vector3(-1f, 1f, 1f);
            clouds.Add((rt, Mathf.Lerp(9f, 28f, k) * (rng.NextDouble() < 0.5 ? 1f : 0.8f), k));
        }
        // the far ones first, so the near (bigger) ones pass in front
        for (int i = 0; i < clouds.Count; i++) clouds[i].rt.SetAsLastSibling();
    }

    // the sky and the haze follow the screen's height
    void LayoutSky()
    {
        float sh = ViewH * SkyShare;
        skyRt.sizeDelta = new Vector2(0f, sh); skyRt.anchoredPosition = Vector2.zero;
        var hr = haze.rectTransform;
        hr.sizeDelta = new Vector2(0f, 300f); hr.anchoredPosition = new Vector2(0f, -sh + 2f);
    }

    void UpdateClouds(float dt)
    {
        if (cloudLayer == null) return;
        float W = Mathf.Max(1080f, root.rect.width), sh = ViewH * SkyShare, t = Time.unscaledTime;
        Color tint = Color.Lerp(Color.white, skyBottom, 0.22f);
        tint = Color.Lerp(tint, new Color(0.42f, 0.47f, 0.74f), nightK);
        for (int i = 0; i < clouds.Count; i++)
        {
            var c = clouds[i];
            var p = c.rt.anchoredPosition;
            float w = c.rt.sizeDelta.x;
            p.x += c.speed * dt;
            if (p.x > (W + w) * 0.5f) p.x = -(W + w) * 0.5f;
            // far clouds hang just over the horizon, near ones higher up; a slow gentle bob
            p.y = Mathf.Lerp(10f, sh * 0.6f, c.k) + 6f * Mathf.Sin(t * 0.3f + i * 1.7f);
            c.rt.anchoredPosition = p;
            var im = c.rt.GetComponent<Image>();
            tint.a = Mathf.Lerp(0.8f, 1f, c.k);
            im.color = tint;
        }
        for (int i = 0; i < skyStars.Count; i++)
        {
            var s = skyStars[i];
            float a = nightK * (0.35f + 0.65f * Mathf.Pow(Mathf.Abs(Mathf.Sin(t * 1.1f + s.phase)), 2f));
            s.img.color = new Color(1f, 0.97f, 0.8f, a);
        }
    }

    // sky colours by the time of day along a world: p = 0 its first level, 1 its last
    static void SkyOf(int world, float p, out Color top, out Color bottom)
    {
        if (world >= 0) p = Worlds.DayPart(world, p);                // a whole-morning / whole-evening world stays put
        Color mT = new Color(0.5f, 0.62f, 0.94f), mB = new Color(1f, 0.84f, 0.76f);
        Color dT = new Color(0.33f, 0.62f, 0.97f), dB = new Color(0.78f, 0.91f, 1f);
        Color eT = new Color(0.47f, 0.4f, 0.8f), eB = new Color(1f, 0.7f, 0.55f);
        if (p < 0.25f) { float k = p / 0.25f; top = Color.Lerp(mT, dT, k); bottom = Color.Lerp(mB, dB, k); }
        else if (p < 0.68f) { top = dT; bottom = dB; }
        else { float k = (p - 0.68f) / 0.32f; top = Color.Lerp(dT, eT, k); bottom = Color.Lerp(dB, eB, k); }
        if (world >= 0 && Worlds.Night(world)) { top = new Color(0.07f, 0.09f, 0.27f); bottom = new Color(0.25f, 0.27f, 0.55f); }
    }

    static bool Close(Color a, Color b) => Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) < 0.006f;

    float WorldProgress(int world) =>
        world < 0 ? 0.5f : Mathf.Clamp01((LevelAt(scroll) - (Worlds.First(world) - 1)) / Worlds.Length(world));

    void UpdateSky(int w, int other, float mine)
    {
        if (sky == null) return;
        SkyOf(w, WorldProgress(w), out var top, out var bot);
        float night = Worlds.Night(w) ? 1f : 0f;
        if (other >= 0 && mine < 1f)
        {
            SkyOf(other, WorldProgress(other), out var t2, out var b2);
            top = Color.Lerp(top, t2, 1f - mine); bot = Color.Lerp(bot, b2, 1f - mine);
            night = Mathf.Lerp(night, Worlds.Night(other) ? 1f : 0f, 1f - mine);
        }
        // glide to the new colours rather than jump
        float k = skySet ? 1f - Mathf.Exp(-4f * Time.unscaledDeltaTime) : 1f;
        skyTop = Color.Lerp(skyTop, top, k); skyBottom = Color.Lerp(skyBottom, bot, k); nightK = Mathf.Lerp(nightK, night, k);
        skySet = true;
        skyStill = Close(skyTop, top) && Close(skyBottom, bot) && Mathf.Abs(nightK - night) < 0.003f;
        Kit.Gradient(skyBottom, skyTop, skyTex);
        for (int y = 0; y < 64; y++)
        {
            float a = y / 63f;                                     // 1 = at the horizon line
            // fully the sky's colour at the horizon, so there is no hard line: the far ground melts into the sky
            hazeTex.SetPixel(0, y, new Color(skyBottom.r, skyBottom.g, skyBottom.b, Mathf.Pow(a, 1.6f)));
        }
        hazeTex.Apply();
        LayoutSky();
    }

    // a soft white cloud drawn from overlapping round puffs (used until there is cloud art)
    static Sprite CloudSprite()
    {
        if (cloudSprite != null) return cloudSprite;
        const int W = 256, H = 128;
        var t = new Texture2D(W, H, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        var puffs = new[] { new Vector3(0.30f, 0.42f, 0.30f), new Vector3(0.50f, 0.55f, 0.40f), new Vector3(0.70f, 0.45f, 0.32f),
                            new Vector3(0.40f, 0.30f, 0.28f), new Vector3(0.62f, 0.30f, 0.28f), new Vector3(0.84f, 0.32f, 0.18f),
                            new Vector3(0.15f, 0.30f, 0.17f) };
        var px = new Color[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                float a = 0f;
                foreach (var p in puffs)
                {
                    float dx = x - p.x * W, dy = y - p.y * H, r = p.z * H;
                    a = Mathf.Max(a, Mathf.Clamp01((r - Mathf.Sqrt(dx * dx + dy * dy)) / 4f));
                }
                a *= Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.12f * H, 0.2f * H, y));     // a flat-ish bottom
                float lit = Mathf.Lerp(0.84f, 1f, Mathf.Clamp01(y / (0.75f * H)));            // a little shade underneath
                px[y * W + x] = new Color(lit * 0.97f, lit * 0.98f, lit, a);
            }
        t.SetPixels(px); t.Apply();
        cloudSprite = Sprite.Create(t, new Rect(0, 0, W, H), new Vector2(0.5f, 0.5f), 100f);
        return cloudSprite;
    }

    // ---------------------------------------------------------------- build / recycle
    // while the map stands still (and the sky has finished changing colour) nothing needs redoing:
    // this saves rebuilding textures and meshes every frame on the phone
    float lastScroll = float.NaN, lastViewH; bool skyStill;
    float nextStateCheck; bool chestReady, dailyReady;

    void Refresh(bool force)
    {
        float vh = ViewH;
        if (!force && skyStill && Mathf.Abs(scroll - lastScroll) < 0.01f && Mathf.Abs(vh - lastViewH) < 0.5f) return;
        lastScroll = scroll; lastViewH = vh;
        content.anchoredPosition = new Vector2(0, -scroll);
        UpdateBackground();

        int lo = Mathf.Max(1, Mathf.FloorToInt(LevelAt(scroll - ViewH * 0.5f - Spacing)));
        int hi = Mathf.CeilToInt(LevelAt(scroll + JoinY + FarCut)) + 1;     // the road runs on to the horizon
        var drop = new List<int>(); bool added = false;
        foreach (var k in built.Keys) if (k < lo || k > hi) drop.Add(k);
        foreach (var k in drop) { foreach (var go in built[k]) if (go != null) Destroy(go); built.Remove(k); }
        for (int l = lo; l <= hi; l++)
            if (!built.ContainsKey(l))
            {
                BuildLevel(l);
                foreach (var go in built[l])
                    if (go != null && go.transform is RectTransform rt) flat[rt] = new Vector3(rt.anchoredPosition.x, rt.anchoredPosition.y, rt.localScale.x);
                AddGlow(l);
                added = true;
            }
        if (added) { SortByDepth(decorLayer); SortByDepth(nodeLayer); }
        if (markerRt != null) markerRt.SetAsLastSibling();
        if (heroRt != null) heroRt.SetAsLastSibling();
        ApplyDrum();
    }

    // farther things are drawn first, so nearer ones stand in front of them
    void SortByDepth(RectTransform layer)
    {
        var kids = new List<RectTransform>();
        foreach (Transform c in layer) kids.Add((RectTransform)c);
        kids.Sort((a, b) => FlatY(b).CompareTo(FlatY(a)));
        for (int i = 0; i < kids.Count; i++) kids[i].SetSiblingIndex(i);
    }
    float FlatY(RectTransform t) => flat.TryGetValue(t, out var v) ? v.y : t.anchoredPosition.y;

    void BuildLevel(int level)
    {
        var list = new List<GameObject>();
        built[level] = list;
        var rng = new System.Random(level * 7919 + 13);
        float y0 = YOf(level), y1 = YOf(level + 1);
        bool roadAhead = true;                       // the road goes on and on, over the horizon
        int world = WorldOf(level); bool worldArt = WorldHasArt(world);
        Color cream = new Color(1f, 0.96f, 0.86f), rim = new Color(0.86f, 0.72f, 0.55f);
        if (worldArt) { var look = WorldLook[Worlds.Art(world)]; cream = look.road; rim = look.rim; }
        bool night = worldArt && Worlds.Night(world);
        if (night) { cream *= new Color(0.74f, 0.78f, 0.98f); rim *= new Color(0.6f, 0.64f, 0.9f); }
        int nProps = worldArt ? CountKeys(world, "prop") : 0, nSmall = worldArt ? CountKeys(world, "small") : 0;
        bool lockedStretch = level >= current;       // the road ahead of the player is a little paler

        // road from this level up to the next one (the very first level also gets the stretch that comes in through its gate)
        if (roadAhead)
        {
            for (float y = level == 1 ? GateY(0) - 520f : y0; y < y1; y += DiscGap)
            {
                var p = P(y);
                list.Add(Disc(roadBack, p, Road + Outline * 2f, lockedStretch ? new Color(rim.r, rim.g, rim.b, 0.75f) : rim));
                list.Add(Disc(roadFront, p, Road, lockedStretch ? new Color(cream.r, cream.g, cream.b, 0.85f) : cream));
            }
            // paw steps, alternating left / right, pointing along the road
            if (GameArt.I.Has("bomb_scorch"))
            {
                int k = 0;
                for (float y = y0 + PawGap * 0.8f; y < y1 - PawGap * 0.6f; y += PawGap, k++)
                {
                    Vector2 p = P(y), tan = (P(y + 4f) - P(y - 4f)).normalized, side = new Vector2(tan.y, -tan.x);
                    Vector2 at = p + side * ((k % 2 == 0) ? 24f : -24f);
                    var img = UIKit.Image(roadFront, "bomb_scorch", new Vector2(0.5f, 0.5f), at, new Vector2(52, 52));
                    img.color = new Color(0.8f, 0.63f, 0.47f, lockedStretch ? 0.45f : 0.75f);
                    img.rectTransform.localRotation = Quaternion.Euler(0, 0, Mathf.Atan2(tan.y, tan.x) * Mathf.Rad2Deg - 90f);
                    list.Add(img.gameObject);
                }
            }
        }

        // decorations: one or two props beside the road, plus a few flowers on the verge
        float halfW = Mathf.Max(540f, root.rect.width * 0.5f);
        int props = 1 + (rng.NextDouble() < 0.5 ? 1 : 0);
        for (int i = 0; i < props; i++)
        {
            float y = y0 + Spacing * (0.25f + 0.5f * i + (float)rng.NextDouble() * 0.2f);
            if (NearGate(y)) continue;
            float roadX = Wave(y);
            // put it on the side with more room
            float side = roadX > 0 ? -1f : 1f;
            if (props == 2 && i == 1) side = -side;
            float x = roadX + side * (Road * 0.5f + 150f + (float)rng.NextDouble() * 130f);
            x = Mathf.Clamp(x, -halfW + 110f, halfW - 110f);
            if (Mathf.Abs(x - roadX) < Road * 0.5f + 110f) continue;     // no room on this bend
            string key = nProps > 0 ? WKey(world, "prop_" + (1 + rng.Next(nProps))) : "map_decor_" + (1 + rng.Next(16));
            if (!GameArt.I.Has(key)) continue;
            float size = 120f + (float)rng.NextDouble() * 40f;
            var img = UIKit.Image(decorLayer, key, new Vector2(0.5f, 0.5f), new Vector2(x, y), new Vector2(size, size));
            if (night) img.color = NightProp;
            list.Add(img.gameObject);
            if (rng.NextDouble() < 0.4) swaying.Add(img.rectTransform);
        }
        if (night)
        {   // fireflies drift about the night worlds
            for (int i = 0; i < 4; i++)
            {
                float y = y0 + (float)rng.NextDouble() * Spacing;
                float x = Mathf.Clamp(Wave(y) + ((float)rng.NextDouble() * 2f - 1f) * 420f, -halfW + 40f, halfW - 40f);
                float fs = 20f + (float)rng.NextDouble() * 16f;
                var rt = UIKit.Place(UIKit.Rect("firefly", decorLayer), new Vector2(0.5f, 0.5f), new Vector2(x, y), new Vector2(fs, fs));
                var fi = rt.gameObject.AddComponent<Image>(); fi.sprite = Kit.SoftCircle(64); fi.raycastTarget = false;
                fi.color = new Color(1f, 0.95f, 0.45f, 0.8f);
                twinkle.Add((fi, (float)rng.NextDouble() * 6.28f));
                list.Add(rt.gameObject);
            }
        }
        int flowers = 2 + rng.Next(3);
        for (int i = 0; i < flowers; i++)
        {
            float y = y0 + (float)rng.NextDouble() * Spacing;
            if (NearGate(y)) continue;
            float roadX = Wave(y);
            float side = rng.NextDouble() < 0.5 ? -1f : 1f;
            float x = roadX + side * (Road * 0.5f + Outline + 22f + (float)rng.NextDouble() * 30f);
            string key = nSmall > 0 ? WKey(world, "small_" + (1 + rng.Next(nSmall))) : "map_flower_" + (1 + rng.Next(6));
            if (!GameArt.I.Has(key)) continue;
            var img = UIKit.Image(decorLayer, key, new Vector2(0.5f, 0.5f), new Vector2(x, y), new Vector2(46, 46));
            if (night) img.color = NightProp;
            img.rectTransform.localRotation = Quaternion.Euler(0, 0, (float)rng.NextDouble() * 60f - 30f);
            list.Add(img.gameObject);
        }

        // the gate into this world, on the long stretch of road before its first level
        if (level == Worlds.First(world) && GameArt.I.Has(WKey(world, "gate")))
        {
            var gs = GameArt.I.S(WKey(world, "gate"));
            float aspect = gs.rect.width / gs.rect.height;
            // wide enough for the whole road to pass between the posts, but never taller than the gap between two levels
            float need = (Road + Outline * 2f + 26f) / GateOpen[Worlds.Art(world)];
            float gw = Mathf.Min(Mathf.Max(need, 500f), Mathf.Min(620f, GateH * aspect)), gh = gw / aspect;
            float gy = GateY(world);
            var gate = UIKit.Image(decorLayer, WKey(world, "gate"), new Vector2(0.5f, 0.5f), new Vector2(Wave(gy), gy), new Vector2(gw, gh));
            gate.name = "gate" + world;
            // the name is written on the blank board from code, so one picture serves every language
            var pl = GatePlate[Worlds.Art(world)];
            if (night) gate.color = NightProp;
            var name = UIKit.Label(gate.transform, Worlds.Name(world), 50, new Color(1f, 0.97f, 0.88f), new Vector2(0.5f, 0.5f),
                                   new Vector2((pl.x - 0.5f) * gw, (0.5f - pl.y) * gh), new Vector2(gw * pl.z * 0.84f, gh * pl.w * 0.8f));
            name.resizeTextForBestFit = true; name.resizeTextMinSize = 18; name.resizeTextMaxSize = 50;
            name.horizontalOverflow = HorizontalWrapMode.Wrap; name.verticalOverflow = VerticalWrapMode.Truncate;
            name.GetComponent<Outline>().effectColor = new Color(0.36f, 0.2f, 0.1f, 0.95f);
            list.Add(gate.gameObject);
            if (world > 0) GateExtras(world, gate.rectTransform, gw, gh);
        }

        // the level node
        if (level <= current + LockedAhead) list.Add(Node(level));
    }

    // What hangs on a gate: while the world behind it is closed - a padlock in the passage and a plate with
    // "stars collected / stars needed" for the world the player is leaving; a crown on top once that world
    // has every star.
    void GateExtras(int world, RectTransform gate, float gw, float gh)
    {
        int from = world - 1;
        int have = Worlds.Stars(from), need = Worlds.StarsNeed(from);
        bool reached = current > Worlds.Last(from);              // the player has walked up to this gate
        if (reached && have >= Worlds.StarsMax(from) && GameArt.I.Has("ui_crown"))
        {
            var crown = UIKit.Image(gate, "ui_crown", new Vector2(0.5f, 0.5f), new Vector2(0, gh * 0.5f + 34f), new Vector2(120, 120));
            crown.name = "crown";
            swaying.Add(crown.rectTransform);
        }
        if (world <= Worlds.Open) return;
        if (world != Worlds.Open + 1) return;                    // only the next gate shows its lock
        // on the right-hand post, so the passage and the road through it stay in view
        // (on the left-hand one where the gate stands too close to the right edge of the screen)
        float halfScreen = Mathf.Max(540f, root.rect.width * 0.5f);
        float lockSide = gate.anchoredPosition.x + gw * 0.5f + 75f > halfScreen ? -1f : 1f;
        var holder = UIKit.Place(UIKit.Rect("lock" + world, gate), new Vector2(0.5f, 0.5f), new Vector2(lockSide * (gw * 0.5f - 30f), -gh * 0.5f + 150f), new Vector2(190, 190));
        holder.localScale = Vector3.one * 0.82f;
        var lb = UIKit.ArtButton(holder, "ui_lock", new Vector2(0.5f, 0.5f), new Vector2(0, 30), 170, () => GateHint(world), "🔒");
        lb.name = "btn_gate_lock";
        swaying.Add((RectTransform)lb.transform);
        // the counter plate under the lock
        var plate = UIKit.Place(UIKit.Rect("plate", holder), new Vector2(0.5f, 0.5f), new Vector2(0, -92), new Vector2(230, 112));
        if (GameArt.I.Has("ui_star_plate")) UIKit.Image(plate, "ui_star_plate", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(230, 124));
        else UIKit.Panel(plate, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(220, 96), UIKit.Navy, 30);
        UIKit.Image(plate, "ui_star", new Vector2(0.5f, 0.5f), new Vector2(-66, 2), new Vector2(54, 54));
        var cnt = UIKit.Label(plate, have + "/" + need, 40, have >= need ? new Color(0.6f, 1f, 0.55f) : Color.white, new Vector2(0.5f, 0.5f), new Vector2(26, 0), new Vector2(130, 60), true);
        cnt.name = "gateCount";
    }

    // The player stands in front of a closed gate (their next level is in the world behind it). If the stars are
    // there the gate opens with a little show; otherwise the map shows the gate and says what is missing.
    IEnumerator GateAhead()
    {
        int world = Worlds.Open + 1, from = world - 1;
        yield return new WaitForSecondsRealtime(hopFrom > 0 ? 1.0f : 0.5f);
        while (starsFlying || newStars > 0) yield return null;        // let the new stars land in the counters first
        if (Worlds.Stars(from) >= Worlds.StarsNeed(from)) yield return OpenGate(world);
        else if (hopFrom > 0) GateHint(world);
    }

    // tap on a closed gate (or "Play" in front of it): say what is missing, glide to the highest level of the
    // world that can still give stars, and light those levels up for a while
    void GateHint(int world)
    {
        if (gateBusy) return;
        int from = world - 1;
        int have = Worlds.Stars(from), need = Worlds.StarsNeed(from);
        if (have >= need) { StartCoroutine(OpenGate(world)); return; }
        onToast?.Invoke(string.Format(Loc.T("Gate.Need"), need, have));
        AudioManager.Play("Hit", 0.5f);
        var lk = nodeLayer != null ? decorLayer.Find("gate" + world + "/lock" + world) : null;
        if (lk != null) StartCoroutine(Wiggle(lk));
        glowWorld = from; glowUntil = Time.unscaledTime + 9f;
        int target = -1;
        for (int l = Mathf.Min(Worlds.Last(from), current - 1); l >= Worlds.First(from); l--) if (Progress.Stars(l) < 3) { target = l; break; }
        if (target > 0) { velocity = 0f; autoScroll = YOf(target) + 300f; }
        foreach (var kv in built) AddGlow(kv.Key);
    }

    void AddGlow(int level)
    {
        if (level < current && Worlds.Of(level) == glowWorld && Time.unscaledTime < glowUntil && Progress.Stars(level) < 3 && GameArt.I.Has("ui_glow_ring"))
        {
            var n = nodeLayer.Find("node" + level);
            if (n == null || n.Find("glow") != null) return;
            var g = UIKit.Image(n, "ui_glow_ring", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(300, 300));
            g.name = "glow"; g.color = new Color(1f, 0.85f, 0.3f, 0f);
            g.transform.SetAsFirstSibling();
            glows.Add(g);
        }
    }

    // the lock shakes, springs open and drops away; flash, confetti, the "new world" ribbon; then the map is rebuilt
    IEnumerator OpenGate(int world)
    {
        if (gateBusy) yield break;
        gateBusy = true;
        velocity = 0f; autoScroll = GateY(world) + 200f;
        yield return new WaitForSecondsRealtime(0.9f);
        var gate = decorLayer.Find("gate" + world);
        var lk = gate != null ? gate.Find("lock" + world) : null;
        if (lk != null)
        {
            var lockImg = lk.Find("btn_gate_lock") != null ? lk.Find("btn_gate_lock").GetComponent<Image>() : lk.GetComponentInChildren<Image>();
            AudioManager.Play("chest_tap", 0.8f);
            yield return TweenU(0.5f, k => { if (lk != null) lk.localRotation = Quaternion.Euler(0, 0, 14f * Mathf.Sin(k * Mathf.PI * 7f) * (1f - k * 0.3f)); });
            if (lockImg != null && GameArt.I.Has("ui_lock_open")) lockImg.sprite = GameArt.I.S("ui_lock_open");
            var plate = lk != null ? lk.Find("plate") : null; if (plate != null) Destroy(plate.gameObject);
            AudioManager.Play("world_unlock", 1f);      // the new world's little fanfare
            Vector3 p0 = lk != null ? lk.localPosition : Vector3.zero;
            yield return TweenU(0.3f, k => { if (lk != null) lk.localScale = Vector3.one * (1f + 0.25f * Mathf.Sin(k * Mathf.PI)); });
            yield return TweenU(0.55f, k =>
            {
                if (lk == null) return;
                lk.localPosition = p0 + new Vector3(60f * k, -520f * k * k, 0);
                lk.localRotation = Quaternion.Euler(0, 0, -70f * k);
                foreach (var g in lk.GetComponentsInChildren<Graphic>()) { var c = g.color; c.a = 1f - k; g.color = c; }
            });
            if (lk != null) Destroy(lk.gameObject);
        }
        Worlds.Open = world;
        // flash + confetti + ribbon on the screen
        var flash = UIKit.Panel(root, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(4000, 6000), new Color(1f, 0.97f, 0.85f, 0.75f), 0);
        StartCoroutine(TweenU(0.5f, k => { if (flash != null) { flash.color = new Color(1f, 0.97f, 0.85f, 0.75f * (1f - k)); if (k >= 1f) Destroy(flash.gameObject); } }));
        var bits = new List<(RectTransform t, Vector2 v, float spin)>();
        for (int i = 0; i < 46; i++)
        {
            string key = "res_confetti_" + (1 + i % 8);
            if (!GameArt.I.Has(key)) continue;
            var im = UIKit.Image(root, key, new Vector2(0.5f, 0.5f), new Vector2(UnityEngine.Random.Range(-160f, 160f), -60f), Vector2.one * UnityEngine.Random.Range(38f, 58f));
            im.color = Color.HSVToRGB(UnityEngine.Random.value, 0.55f, 1f);
            bits.Add((im.rectTransform, new Vector2(UnityEngine.Random.Range(-900f, 900f), UnityEngine.Random.Range(1000f, 2000f)), UnityEngine.Random.Range(-400f, 400f)));
        }
        RectTransform ribbon = null;
        if (GameArt.I.Has("ui_ribbon"))
        {
            var rs = GameArt.I.S("ui_ribbon"); float rw = 940f, rh = rw * rs.rect.height / rs.rect.width;
            var rimg = UIKit.Image(root, "ui_ribbon", new Vector2(0.5f, 0.5f), new Vector2(0, 260), new Vector2(rw, rh));
            ribbon = rimg.rectTransform;
            UIKit.Label(ribbon, Worlds.Name(world), 74, Color.white, new Vector2(0.5f, 0.5f), new Vector2(0, -rh * 0.1f), new Vector2(rw * 0.62f, rh * 0.5f)).resizeTextForBestFit = true;
        }
        var hello = UIKit.Label(root, Loc.T("World.New"), 86, new Color(1f, 0.88f, 0.35f), new Vector2(0.5f, 0.5f), new Vector2(0, 505), new Vector2(1000, 120));
        float age = 0f, H = ViewH;
        while (age < 2.4f)
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f); age += dt;
            float pop = age < 0.35f ? Kit.EaseOutBack(age / 0.35f) : age > 2.0f ? Mathf.Max(0f, 1f - (age - 2.0f) / 0.4f) : 1f;
            if (ribbon != null) ribbon.localScale = Vector3.one * pop;
            hello.transform.localScale = Vector3.one * pop;
            for (int i = bits.Count - 1; i >= 0; i--)
            {
                var b = bits[i];
                b.v.y -= 2600f * dt; b.v.x *= 1f / (1f + 0.8f * dt);
                b.t.anchoredPosition += b.v * dt;
                b.t.localRotation = Quaternion.Euler(0, 0, b.t.localEulerAngles.z + b.spin * dt);
                bits[i] = b;
                if (b.t.anchoredPosition.y < -H * 0.6f) { Destroy(b.t.gameObject); bits.RemoveAt(i); }
            }
            yield return null;
        }
        gateBusy = false;
        onGateOpened?.Invoke();
    }

    static IEnumerator TweenU(float dur, Action<float> step)
    {
        float t = 0f;
        while (t < dur) { t += Mathf.Min(Time.unscaledDeltaTime, 0.05f); step(Mathf.Clamp01(t / dur)); yield return null; }
        step(1f);
    }

    GameObject Disc(RectTransform layer, Vector2 pos, float size, Color c)
    {
        var rt = UIKit.Place(UIKit.Rect("d", layer), new Vector2(0.5f, 0.5f), pos, new Vector2(size, size));
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = Kit.RoundedRect(128, 63); img.color = c; img.raycastTarget = false;
        return rt.gameObject;
    }

    GameObject Node(int level)
    {
        bool done = level < current, cur = level == current && !Worlds.Locked(level);
        bool hard = !done && LevelGen.IsHardLevel(level) && GameArt.I.Has("map_node_hard");   // red with flames until it's beaten
        string key = done ? "map_node_done" : hard ? "map_node_hard" : cur ? "map_node_current" : "map_node_locked";
        float size = cur ? CurrentSize : NodeSize;
        var holder = UIKit.Place(UIKit.Rect("node" + level, nodeLayer), new Vector2(0.5f, 0.5f), P(YOf(level)), new Vector2(size, size));
        Button b;
        if (GameArt.I.Has(key)) b = UIKit.ArtButton(holder, key, new Vector2(0.5f, 0.5f), Vector2.zero, size, () => Pick(level));
        else
        {
            b = UIKit.TextButton(holder, "", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(size, size),
                done ? new Color(1f, 0.5f, 0.7f) : cur ? new Color(0.4f, 0.9f, 0.6f) : new Color(0.7f, 0.7f, 0.8f), () => Pick(level));
        }
        // number sits in the middle of the disc (the ears push the disc centre a little lower)
        var num = UIKit.Label(b.transform, level.ToString(), level >= 100 ? 50 : 60, done || cur ? Color.white : new Color(0.55f, 0.58f, 0.72f),
                              new Vector2(0.5f, 0.5f), new Vector2(0, -size * 0.04f), new Vector2(size, size * 0.6f), true);
        if (!done && !cur && !hard) num.GetComponent<Outline>().effectColor = new Color(1f, 1f, 1f, 0.6f);
        if (hard)
        {
            num.color = Color.white;
            if (!cur) b.GetComponent<Image>().color = new Color(0.82f, 0.78f, 0.82f);    // not reached yet: a little dimmer
        }
        if (cur) currentPulse = b.transform;
        if (done && GameArt.I.Has("ui_star"))
        {   // the level's best result: three little stars in a shallow arc under the node, missing ones are dark
            int got = Progress.Stars(level);
            for (int i = 0; i < 3; i++)
            {
                var st = UIKit.Image(holder, "ui_star", new Vector2(0.5f, 0.5f), new Vector2((i - 1) * 46f, -size * 0.5f + (i == 1 ? -6f : 4f)), new Vector2(50, 50));
                st.rectTransform.localRotation = Quaternion.Euler(0, 0, (1 - i) * 12f);
                if (i >= got) st.color = new Color(0.3f, 0.26f, 0.4f, 0.8f);
            }
        }
        return holder.gameObject;
    }

    // the marker stands beside a node, on the side away from the road's bend, so it never covers the next level
    static Vector2 MarkerSpot(int level, float t)
    {
        Vector2 c = P(YOf(level));
        float side = c.x > 0 ? -1f : 1f;
        return c + new Vector2(side * (CurrentSize * 0.5f + 95f), 55f + 12f * Mathf.Abs(Mathf.Sin(t * 3f)));
    }

    // ---------------------------------------------------------------- the hero cat
    // A fluffy ginger cat stands beside the current level. After a win it walks there from the level just
    // won: steps off, follows the road (four walk frames, a bounce on every step, little dust puffs), and at
    // the new level hops up, lands with a squash, pops the node and waves. Standing, it breathes, turns
    // towards the level, now and then waves or hops on the spot; a tap makes it hop too.
    RectTransform heroRt, heroBody; Image heroImg;
    enum HeroMode { Idle, Wait, Walk, Hop, Wave }
    HeroMode heroMode;
    float heroT, heroNextAct, heroDist, heroLen, heroStepAt, heroFace = 1f;
    readonly List<Vector2> heroPath = new List<Vector2>();
    Vector2 heroPos;
    int heroHopKind;               // 0 = arrival hop (pops the node), 1 = a little hop on the spot
    const float HeroH = 180f, HeroWalkSpeed = 520f, HeroStep = 70f;
    static readonly string[] HeroWalk = { "hero_walk_1", "hero_walk_2", "hero_walk_3", "hero_walk_4" };

    static Vector2 HeroSpot(int level)
    {   // beside the node, on the side away from the road's bend, so it never covers the next level
        Vector2 c = P(YOf(level));
        float side = c.x > 0 ? -1f : 1f;
        return c + new Vector2(side * (CurrentSize * 0.5f + 90f), 30f);
    }

    void BuildHero()
    {
        if (!GameArt.I.Has("hero_stand")) return;
        heroRt = UIKit.Rect("hero", nodeLayer);
        heroRt.anchorMin = heroRt.anchorMax = new Vector2(0.5f, 0.5f); heroRt.pivot = new Vector2(0.5f, 0f);   // pivot at the feet
        heroRt.sizeDelta = new Vector2(HeroH * 1.1f, HeroH);
        heroBody = UIKit.Rect("body", heroRt);
        heroBody.anchorMin = heroBody.anchorMax = new Vector2(0.5f, 0f); heroBody.pivot = new Vector2(0.5f, 0f);
        heroBody.sizeDelta = new Vector2(HeroH * 1.1f, HeroH); heroBody.anchoredPosition = Vector2.zero;
        heroImg = heroBody.gameObject.AddComponent<Image>(); heroImg.preserveAspect = true; heroImg.raycastTarget = true;
        var btn = heroBody.gameObject.AddComponent<Button>(); btn.transition = Selectable.Transition.None;
        btn.onClick.AddListener(() => { if (heroMode == HeroMode.Idle) StartHop(1); });
        HeroSprite("hero_stand");
        if (hopFrom > 0 && hopFrom < current)
        {   // walk from the level just won: off the old spot, along the road, to the new spot
            heroPath.Clear();
            heroPath.Add(HeroSpot(hopFrom));
            float y0 = YOf(hopFrom) + 70f, y1 = YOf(current) - 70f;
            for (float y = y0; y < y1; y += 30f) heroPath.Add(P(y) + new Vector2(0, -20f));
            heroPath.Add(HeroSpot(current));
            heroLen = 0f; for (int i = 1; i < heroPath.Count; i++) heroLen += Vector2.Distance(heroPath[i - 1], heroPath[i]);
            heroPos = heroPath[0]; heroDist = 0f; heroStepAt = 0f;
            heroMode = HeroMode.Wait; heroT = 0f;
        }
        else { heroPos = HeroSpot(current); heroMode = HeroMode.Idle; heroNextAct = 2.5f; FaceNode(); }
        Project(heroRt, heroPos, 1f, Vector2.one);
    }

    void HeroSprite(string key)
    {
        var sp = GameArt.I.S(key);
        if (sp != null && heroImg.sprite != sp) heroImg.sprite = sp;
    }

    void FaceNode()
    {   // standing beside the node: look towards it (the art faces right)
        heroFace = P(YOf(current)).x > heroPos.x ? 1f : -1f;
    }

    Vector2 PathAt(float d)
    {
        for (int i = 1; i < heroPath.Count; i++)
        {
            float seg = Vector2.Distance(heroPath[i - 1], heroPath[i]);
            if (d <= seg || i == heroPath.Count - 1) return Vector2.Lerp(heroPath[i - 1], heroPath[i], seg > 0f ? Mathf.Clamp01(d / seg) : 1f);
            d -= seg;
        }
        return heroPath[heroPath.Count - 1];
    }

    void StartHop(int kind) { heroMode = HeroMode.Hop; heroHopKind = kind; heroT = 0f; }

    void UpdateHero(float dt, float t)
    {
        heroT += dt;
        float bob = 0f, sx = 1f, sy = 1f, rot = 0f;
        switch (heroMode)
        {
            case HeroMode.Wait:      // the win screen has just closed: a breath, then off it goes
                HeroSprite("hero_stand");
                heroFace = PathAt(40f).x >= heroPos.x ? 1f : -1f;
                sy = 1f + 0.02f * Mathf.Sin(t * 4f);
                if (heroT > 0.35f) { heroMode = HeroMode.Walk; heroT = 0f; }
                break;
            case HeroMode.Walk:
            {
                float speed = HeroWalkSpeed * Mathf.Clamp01(0.35f + heroT * 2.5f);          // a quick start
                if (heroLen - heroDist < 120f) speed *= Mathf.Lerp(0.55f, 1f, (heroLen - heroDist) / 120f);   // slows down to arrive
                heroDist = Mathf.Min(heroLen, heroDist + speed * dt);
                Vector2 np = PathAt(heroDist);
                if (Mathf.Abs(np.x - heroPos.x) > 0.5f) heroFace = Mathf.Lerp(heroFace, np.x > heroPos.x ? 1f : -1f, 1f - Mathf.Exp(-14f * dt));
                heroPos = np;
                float ph = heroDist / HeroStep;                                   // one step every HeroStep units
                HeroSprite(HeroWalk[(int)(ph * 2f) % 4]);
                float stepK = ph - Mathf.Floor(ph);
                bob = 12f * Mathf.Abs(Mathf.Sin(stepK * Mathf.PI));               // up and down on every step
                sy = 1f - 0.04f * Mathf.Cos(stepK * Mathf.PI * 2f); sx = 2f - sy;
                rot = 3f * Mathf.Sin(ph * Mathf.PI) * -Mathf.Sign(heroFace);
                if (heroDist >= heroStepAt) { heroStepAt += HeroStep; HeroDust(heroPos, 1); }
                if (heroDist >= heroLen) { StartHop(0); }
                break;
            }
            case HeroMode.Hop:
            {   // up (jump frame) - down - squash on landing (land frame)
                float up = heroHopKind == 0 ? 0.42f : 0.32f, hgt = heroHopKind == 0 ? 90f : 55f;
                if (heroT < up)
                {
                    float k = heroT / up;
                    HeroSprite("hero_jump");
                    bob = hgt * Mathf.Sin(k * Mathf.PI);
                    sy = 1.06f; sx = 0.95f;
                }
                else
                {
                    float k = Mathf.Clamp01((heroT - up) / 0.22f);
                    if (heroT - dt < up)
                    {   // the landing
                        HeroDust(heroPos, 3);
                        if (heroHopKind == 0)
                        {
                            hopFrom = 0;
                            if (currentPulse != null) StartCoroutine(PopNode(currentPulse));
                            AudioManager.Play("balance", 0.6f);
                        }
                        AudioManager.Play("cat_drop", 0.35f);
                    }
                    HeroSprite("hero_land");
                    float sq = Mathf.Sin(k * Mathf.PI);
                    sy = 1f - 0.12f * sq; sx = 1f + 0.1f * sq;
                    if (k >= 1f)
                    {
                        FaceNode();
                        if (heroHopKind == 0) { heroMode = HeroMode.Wave; heroT = 0f; }
                        else { heroMode = HeroMode.Idle; heroT = 0f; heroNextAct = UnityEngine.Random.Range(5f, 9f); }
                    }
                }
                break;
            }
            case HeroMode.Wave:          // a happy wave, the paw going to and fro (the whole cat rocks a little)
                HeroSprite("hero_wave");
                rot = 4f * Mathf.Sin(heroT * 9f) * Mathf.Clamp01(1.4f - heroT);
                sy = 1f + 0.02f * Mathf.Sin(heroT * 9f);
                if (heroT > 1.4f) { heroMode = HeroMode.Idle; heroT = 0f; heroNextAct = UnityEngine.Random.Range(4f, 8f); }
                break;
            default:                     // idle: breathes; now and then waves or hops on the spot
                HeroSprite("hero_stand");
                sy = 1f + 0.025f * Mathf.Sin(t * 3.2f); sx = 1f - 0.012f * Mathf.Sin(t * 3.2f);
                rot = 1.5f * Mathf.Sin(t * 1.3f);
                heroPos = Vector2.Lerp(heroPos, HeroSpot(current), 1f - Mathf.Exp(-6f * dt));
                if (heroT > heroNextAct)
                {
                    if (UnityEngine.Random.value < 0.6f) { heroMode = HeroMode.Wave; heroT = 0f; }
                    else StartHop(1);
                }
                break;
        }
        Project(heroRt, heroPos, 1f, Vector2.one);
        heroBody.anchoredPosition = new Vector2(0f, bob);
        heroBody.localScale = new Vector3(heroFace * sx, sy, 1f);
        heroBody.localRotation = Quaternion.Euler(0, 0, rot);
    }

    // a few soft puffs of dust at the cat's feet (they live in map space, so they keep their place)
    void HeroDust(Vector2 at, int n)
    {
        for (int i = 0; i < n; i++)
        {
            var d = UIKit.Rect("dust", nodeLayer);
            d.anchorMin = d.anchorMax = new Vector2(0.5f, 0.5f);
            d.sizeDelta = new Vector2(34f, 34f);
            var img = d.gameObject.AddComponent<Image>(); img.sprite = Kit.RoundedRect(64, 31); img.raycastTarget = false;
            img.color = new Color(1f, 0.95f, 0.85f, 0.75f);
            Vector2 p = at + new Vector2(-heroFace * 35f + UnityEngine.Random.Range(-25f, 25f), UnityEngine.Random.Range(-4f, 8f));
            Vector2 v = new Vector2(-heroFace * UnityEngine.Random.Range(20f, 60f) + UnityEngine.Random.Range(-40f, 40f), UnityEngine.Random.Range(15f, 45f));
            StartCoroutine(DustPuff(d, img, p, v));
        }
        if (heroRt != null) heroRt.SetAsLastSibling();
    }

    IEnumerator DustPuff(RectTransform d, Image img, Vector2 p, Vector2 v)
    {
        float a = 0f, life = 0.55f;
        while (a < life && d != null)
        {
            a += Time.unscaledDeltaTime; float k = a / life;
            Project(d, p + v * a, 0.6f + 1.1f * k, Vector2.one);
            img.color = new Color(1f, 0.95f, 0.85f, 0.75f * (1f - k) * (1f - k));
            yield return null;
        }
        if (d != null) Destroy(d.gameObject);
    }

    IEnumerator PopNode(Transform n)
    {
        float a = 0f;
        while (a < 0.35f && n != null) { a += Time.unscaledDeltaTime; float s = 1f + 0.35f * Mathf.Sin(Mathf.Clamp01(a / 0.35f) * Mathf.PI); n.localScale = new Vector3(s, s, 1f); yield return null; }
    }

    void Pick(int level)
    {
        if (Mathf.Abs(velocity) > 400f || gateBusy) return;           // a fling, not a tap
        if (level == current && Worlds.Locked(level)) { GateHint(Worlds.Open + 1); return; }
        if (level > current)
        {
            var n = nodeLayer.Find("node" + level);
            if (n != null) StartCoroutine(Wiggle(n));
            AudioManager.Play("Hit", 0.4f);
            return;
        }
        onPick?.Invoke(level);
    }

    IEnumerator Wiggle(Transform t)
    {
        float d = 0.3f, a = 0f;
        while (a < d && t != null)
        {
            a += Time.unscaledDeltaTime; float k = a / d;
            t.localRotation = Quaternion.Euler(0, 0, Mathf.Sin(k * Mathf.PI * 4) * 10f * (1 - k));
            yield return null;
        }
        if (t != null) t.localRotation = Quaternion.identity;
    }

    // ---------------------------------------------------------------- colours
    void UpdateBackground()
    {
        float lv = LevelAt(scroll);                                  // level-ish at the screen centre (0-based)
        float ch = lv / ChapterLen;
        int c0 = Mathf.FloorToInt(ch);
        float f = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.75f, 1f, ch - c0));   // blend in the last quarter of a chapter
        var a = Background.Themes[Mod(c0, Background.Themes.Length)];
        var b = Background.Themes[Mod(c0 + 1, Background.Themes.Length)];
        Color bot = Color.Lerp(a.bottom, b.bottom, f), top = Color.Lerp(a.top, b.top, f);
        Kit.Gradient(bot, top, gradTex);
        // the world's ground: the world at the screen centre, cross-fading with its neighbour around a gate
        int w = WorldAtY(scroll + (Spacing + GateGap) * 0.5f);
        float past = scroll - GateY(w), before = GateY(w + 1) - scroll;
        if (w <= 0) { w = 0; past = 100000f; }            // below the very first gate it is still the first world
        const float Fade = 520f;
        int other = past < Fade ? w - 1 : w + 1;
        float mine = past < Fade ? 0.5f + 0.5f * past / Fade : before < Fade ? 0.5f + 0.5f * before / Fade : 1f;
        float cover = SetFloor(floorA, w, 1f) * 1f;
        float coverB = SetFloor(floorB, other, 1f - mine);
        cover = Mathf.Lerp(cover, 1f, coverB) * (cover > 0f || coverB > 0f ? 1f : 0f);
        if (cover > 0f)
        {   // fog and pattern follow the ground
            Color fa = WorldHasArt(w) ? WorldLook[Worlds.Art(w)].fog * FloorTint(w) : Color.Lerp(bot, top, 0.5f);
            Color fb = other >= 0 && WorldHasArt(other) ? WorldLook[Worlds.Art(other)].fog * FloorTint(other) : Color.Lerp(bot, top, 0.5f);
            fa.a = fb.a = 1f;
            Color fog = Color.Lerp(fa, fb, 1f - mine);
            float k = WorldHasArt(w) ? Mathf.Lerp(1f, other >= 0 && WorldHasArt(other) ? 1f : 0f, 1f - mine) : coverB;
            top = Color.Lerp(top, fog, k); bot = Color.Lerp(bot, fog, k);
            if (pattern != null) pattern.color = new Color(1, 1, 1, 0.18f * (1f - k));
        }
        else if (pattern != null) pattern.color = new Color(1, 1, 1, 0.18f);
        // fog in the background colour: things vanish over the horizon instead of piling up
        UpdateSky(w, other, mine);
        if (pattern != null)
        {
            float tile = 420f;
            var r = root.rect;
            pattern.uvRect = new Rect(0, scroll * 0.6f / tile, r.width / tile, r.height / tile);
        }
    }

    // shows a world's ground tile on a full-screen layer; returns the opacity it ended up with (0 = that world has no art)
    float SetFloor(RawImage img, int world, float alpha)
    {
        if (img == null) return 0f;
        var s = world >= 0 && alpha > 0.001f ? GameArt.I.S(WKey(world, "floor")) : null;
        if (s == null) { if (img.color.a != 0f) img.color = new Color(1, 1, 1, 0); return 0f; }
        if (img.texture != s.texture) { s.texture.wrapMode = TextureWrapMode.Repeat; img.texture = s.texture; }
        Color tint = FloorTint(world); tint.a = alpha;
        img.color = tint;
        float tile = FloorTile[Worlds.Art(world)];
        if (img is CurvedImage ci)
        {   // the ground follows the perspective too: its tiles shrink towards the horizon
            ci.Uv = p => FlatAt(p) / tile;
            ci.SetVerticesDirty();
        }
        else
        {
            var r = root.rect;
            img.uvRect = new Rect(-r.width * 0.5f / tile, (scroll - r.height * 0.5f) / tile, r.width / tile, r.height / tile);
        }
        return alpha;
    }

    // The ground changes with the time of day along a world: a soft warm morning at its first levels, plain
    // daylight in the middle, a golden evening before the next gate. Night worlds are dark blue throughout.
    Color FloorTint(int world)
    {
        float p = Worlds.DayPart(world, Mathf.Clamp01((LevelAt(scroll) - (Worlds.First(world) - 1)) / Worlds.Length(world)));
        Color morning = new Color(1f, 0.95f, 0.88f), day = Color.white, evening = new Color(1f, 0.84f, 0.7f);
        Color c = p < 0.25f ? Color.Lerp(morning, day, p / 0.25f) : p < 0.68f ? day : Color.Lerp(day, evening, (p - 0.68f) / 0.32f);
        if (Worlds.Night(world)) c = Color.Lerp(c, Color.white, 0.6f) * NightTint;
        return c;
    }

    static int Mod(int a, int m) => ((a % m) + m) % m;

    void OnDestroy() { if (gradTex != null) Destroy(gradTex); if (skyTex != null) Destroy(skyTex); if (hazeTex != null) Destroy(hazeTex); }
}

// A RawImage drawn as a fine grid whose texture coordinates come from a function, so a flat repeating
// tile can be bent (used for the map's ground running away to the horizon).
public class CurvedImage : RawImage
{
    public Func<Vector2, Vector2> Uv;
    const int Cols = 8, Rows = 120;     // fine rows: the perspective squeezes the ground towards the horizon

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        if (Uv == null) { base.OnPopulateMesh(vh); return; }
        vh.Clear();
        var r = GetPixelAdjustedRect();
        Color32 c = color;
        for (int j = 0; j <= Rows; j++)
        {
            float y = r.yMin + r.height * j / Rows;
            for (int i = 0; i <= Cols; i++)
            {
                var p = new Vector2(r.xMin + r.width * i / Cols, y);
                vh.AddVert(p, c, Uv(p));
            }
        }
        int w = Cols + 1;
        for (int j = 0; j < Rows; j++)
            for (int i = 0; i < Cols; i++)
            {
                int a = j * w + i;
                vh.AddTriangle(a, a + w, a + w + 1);
                vh.AddTriangle(a, a + w + 1, a + 1);
            }
    }
}
