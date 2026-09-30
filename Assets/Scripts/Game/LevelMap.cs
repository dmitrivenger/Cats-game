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

    // ---- the "3D" bend: the map lies on a big drum; things roll away (shrink, bunch up) towards the
    // top and bottom of the screen. Every element keeps its flat map position and is re-projected each frame.
    const float DrumRadius = 1050f;     // smaller = stronger bend
    const float DrumDepth = 0.55f;      // how much things shrink at the far edge
    readonly Dictionary<RectTransform, Vector3> flat = new Dictionary<RectTransform, Vector3>();   // x, y, base scale
    RawImage shadeTop, shadeBottom;
    Texture2D fogTopTex, fogBottomTex;
    Text balanceText; int shownBalance = -1;
    Action onSettings;
    readonly Dictionary<int, List<GameObject>> built = new Dictionary<int, List<GameObject>>();
    readonly List<RectTransform> swaying = new List<RectTransform>();
    readonly List<RectTransform> pulsing = new List<RectTransform>();
    int current; Action<int> onPick;
    public Action<int> onChest;          // tap on a treasure chest that is ready to open
    int hopFrom; float hopAge;      // after a win the marker jumps from the old level to the new one
    float scroll, velocity; bool dragging;
    float lastDragTime;
    // "back to my level": a round button that shows up once the current level scrolls off screen,
    // and a big "Играть" button that starts the current level straight away (no searching at all)
    RectTransform findBtn; Text findArrow; float findShow; float? autoScroll;

    public static float YOf(int level) => (level - 1) * Spacing;
    public static float Wave(float y) { float t = y / Spacing; return 250f * Mathf.Sin(t * 0.85f) + 45f * Mathf.Sin(t * 2.3f + 1f); }
    static Vector2 P(float y) => new Vector2(Wave(y), y);

    public static LevelMap Open(RectTransform parent, int currentLevel, Action<int> onPick, Action onClose, int hopFrom = 0, Action onSettings = null)
    {
        var rt = UIKit.Rect("LevelMap", parent); UIKit.Stretch(rt);
        var m = rt.gameObject.AddComponent<LevelMap>();
        m.hopFrom = hopFrom > 0 && hopFrom < currentLevel ? hopFrom : 0;
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

        content = UIKit.Rect("content", root);
        content.anchorMin = content.anchorMax = new Vector2(0.5f, 0.5f); content.sizeDelta = Vector2.zero;
        roadBack = Layer("roadBack"); roadFront = Layer("roadFront"); decorLayer = Layer("decor"); nodeLayer = Layer("nodes");

        // "you are here" marker
        if (GameArt.I.Has("map_marker"))
        {
            markerRt = (RectTransform)UIKit.Image(content, "map_marker", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(200, 200)).transform;
            markerRt.name = "marker";
        }

        BuildShades();
        if (onClose != null)
            UIKit.ArtButton(root, "ui_close", new Vector2(1f, 1f), new Vector2(-90, -130), 130, () => onClose(), "X");
        if (onSettings != null)
            UIKit.ArtButton(root, "ui_sett", new Vector2(0f, 1f), new Vector2(90, -130), 130, () => onSettings(), "⚙");
        var chip = UIKit.Panel(root, new Vector2(0.5f, 1f), new Vector2(0, -130), new Vector2(300, 96), new Color(0.13f, 0.18f, 0.36f, 0.88f), 48);
        UIKit.Panel(chip.transform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(288, 84), new Color(1f, 0.8f, 0.3f, 0.35f), 42);
        UIKit.Image(chip.transform, "ui_balance", new Vector2(0f, 0.5f), new Vector2(62, 0), new Vector2(92, 92));
        balanceText = UIKit.Label(chip.transform, Progress.Balance.ToString(), 54, Color.white, new Vector2(0.5f, 0.5f), new Vector2(34, 0), new Vector2(190, 80), true);

        BuildPlayButtons();
        scroll = HomeScroll;                 // current level a little below the middle of the screen
        Refresh(true);
    }

    RectTransform Layer(string n)
    {
        var l = UIKit.Rect(n, content);
        l.anchorMin = l.anchorMax = new Vector2(0.5f, 0.5f); l.sizeDelta = Vector2.zero;
        return l;
    }

    float HomeScroll => YOf(current) + 120f;

    void BuildPlayButtons()
    {
        // big "Играть" at the bottom: plays the current level
        var normal = GameArt.I.S("ui_play_big");
        if (normal != null)
        {
            float w = 520f, h = w * normal.rect.height / normal.rect.width;
            var img = UIKit.Image(root, "ui_play_big", new Vector2(0.5f, 0f), new Vector2(0, 120 + h * 0.5f), new Vector2(w, h));
            img.raycastTarget = true; img.name = "btn_map_play";
            var b = img.gameObject.AddComponent<Button>(); b.targetGraphic = img;
            var pressed = GameArt.I.S("ui_play_big_pressed");
            if (pressed != null) { b.transition = Selectable.Transition.SpriteSwap; b.spriteState = new SpriteState { pressedSprite = pressed, highlightedSprite = normal, selectedSprite = normal }; }
            else { b.transition = Selectable.Transition.None; img.gameObject.AddComponent<Squish>(); }
            b.onClick.AddListener(() => { AudioManager.PlayClick(); onPick?.Invoke(current); });
        }
        else UIKit.TextButton(root, "Играть", new Vector2(0.5f, 0f), new Vector2(0, 200), new Vector2(480, 140), new Color(0.3f, 0.8f, 0.4f), () => onPick?.Invoke(current));

        // round "find me" button (cat marker + arrow towards the current level), bottom-right
        var ring = UIKit.Panel(root, new Vector2(1f, 0f), new Vector2(-120, 230), new Vector2(170, 170), new Color(0.13f, 0.18f, 0.36f, 0.92f), 85);
        ring.raycastTarget = true; ring.name = "btn_map_find";
        UIKit.Panel(ring.transform, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(154, 154), new Color(1f, 0.8f, 0.3f, 0.45f), 77);
        UIKit.Image(ring.transform, "map_marker", new Vector2(0.5f, 0.5f), new Vector2(0, 8), new Vector2(120, 120));
        findArrow = UIKit.Label(ring.transform, "▼", 44, Color.white, new Vector2(0.5f, 0f), new Vector2(0, 26), new Vector2(80, 50));
        var fb = ring.gameObject.AddComponent<Button>(); fb.targetGraphic = ring; fb.transition = Selectable.Transition.None;
        ring.gameObject.AddComponent<Squish>();
        fb.onClick.AddListener(() => { AudioManager.PlayClick(); velocity = 0f; autoScroll = HomeScroll; });
        findBtn = ring.rectTransform;
        findBtn.localScale = Vector3.zero;
    }

    float MinScroll => YOf(1) + ViewH * 0.5f - 420f;
    float MaxScroll => Mathf.Max(MinScroll, YOf(current + LockedAhead) - ViewH * 0.25f);
    float ViewH => root.rect.height > 1 ? root.rect.height : 1920f;

    // ---------------------------------------------------------------- input
    public void OnPointerDown(PointerEventData e) { velocity = 0f; autoScroll = null; }
    public void OnBeginDrag(PointerEventData e) { dragging = true; velocity = 0f; }
    public void OnDrag(PointerEventData e)
    {
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
        }
        if (currentPulse != null) { float s = 1f + 0.06f * Mathf.Sin(t * 4f); currentPulse.localScale = new Vector3(s, s, 1f); }
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
        for (int i = pulsing.Count - 1; i >= 0; i--)
        {
            if (pulsing[i] == null) { pulsing.RemoveAt(i); continue; }
            float beat = Mathf.Pow(Mathf.Abs(Mathf.Sin(t * 2.6f)), 3f);   // a heartbeat-like swell
            pulsing[i].localScale = Vector3.one * (1f + 0.16f * beat);
        }
    }

    // ---------------------------------------------------------------- drum projection
    // y: map distance from the screen centre -> arc on a drum of DrumRadius; scale shrinks with depth.
    Vector3 Drum(Vector2 p)
    {
        float d = p.y - scroll;
        float r = d > 0 ? DrumRadius : DrumRadius * 2.2f;     // rolls away mostly at the top (a horizon), gently at the bottom
        float a = Mathf.Clamp(d / r, -1.45f, 1.45f);
        float y = r * Mathf.Sin(a);
        float s = 1f - DrumDepth * (1f - Mathf.Cos(a));
        return new Vector3(p.x * s, scroll + y, s);
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

    // soft darker bands at the top and bottom edges sell the curve
    void BuildShades()
    {
        shadeTop = Shade("shadeTop", new Vector2(0.5f, 1f));
        shadeBottom = Shade("shadeBottom", new Vector2(0.5f, 0f));
    }

    RawImage Shade(string n, Vector2 anchor)
    {
        var img = new GameObject(n, typeof(RectTransform)).AddComponent<RawImage>();
        img.transform.SetParent(root, false);
        var rt = img.rectTransform;
        rt.anchorMin = new Vector2(0f, anchor.y); rt.anchorMax = new Vector2(1f, anchor.y); rt.pivot = anchor;
        rt.sizeDelta = new Vector2(0, anchor.y > 0.5f ? 520f : 300f); rt.anchoredPosition = Vector2.zero;
        var t = new Texture2D(1, 64, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        if (anchor.y > 0.5f) fogTopTex = t; else fogBottomTex = t;
        img.texture = t;
        img.raycastTarget = false;
        return img;
    }

    // ---------------------------------------------------------------- build / recycle
    void Refresh(bool force)
    {
        content.anchoredPosition = new Vector2(0, -scroll);
        UpdateBackground();

        float half = DrumRadius * 1.45f + Spacing;
        int lo = Mathf.Max(1, Mathf.FloorToInt((scroll - half) / Spacing) + 1);
        int hi = Mathf.Min(current + LockedAhead + 1, Mathf.CeilToInt((scroll + half) / Spacing) + 1);
        var drop = new List<int>();
        foreach (var k in built.Keys) if (k < lo || k > hi) drop.Add(k);
        foreach (var k in drop) { foreach (var go in built[k]) if (go != null) Destroy(go); built.Remove(k); }
        for (int l = lo; l <= hi; l++)
            if (!built.ContainsKey(l))
            {
                BuildLevel(l);
                foreach (var go in built[l])
                    if (go != null && go.transform is RectTransform rt) flat[rt] = new Vector3(rt.anchoredPosition.x, rt.anchoredPosition.y, rt.localScale.x);
            }
        if (markerRt != null) markerRt.SetAsLastSibling();
        ApplyDrum();
    }

    void BuildLevel(int level)
    {
        var list = new List<GameObject>();
        built[level] = list;
        var rng = new System.Random(level * 7919 + 13);
        float y0 = YOf(level), y1 = YOf(level + 1);
        bool roadAhead = level <= current + LockedAhead;
        Color cream = new Color(1f, 0.96f, 0.86f), rim = new Color(0.86f, 0.72f, 0.55f);
        bool lockedStretch = level >= current;       // the road ahead of the player is a little paler

        // road from this level up to the next one
        if (roadAhead)
        {
            for (float y = y0; y < y1; y += DiscGap)
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
            float roadX = Wave(y);
            // put it on the side with more room
            float side = roadX > 0 ? -1f : 1f;
            if (props == 2 && i == 1) side = -side;
            float markerSide = Wave(YOf(current)) > 0 ? -1f : 1f;
            if (side == markerSide && Mathf.Abs(y - YOf(current)) < Spacing * 0.9f) continue;   // that's where the cat marker stands
            float x = roadX + side * (Road * 0.5f + 150f + (float)rng.NextDouble() * 130f);
            x = Mathf.Clamp(x, -halfW + 110f, halfW - 110f);
            if (Mathf.Abs(x - roadX) < Road * 0.5f + 110f) continue;     // no room on this bend
            string key = "map_decor_" + (1 + rng.Next(16));
            if (!GameArt.I.Has(key)) continue;
            float size = 150f + (float)rng.NextDouble() * 50f;
            var img = UIKit.Image(decorLayer, key, new Vector2(0.5f, 0.5f), new Vector2(x, y), new Vector2(size, size));
            list.Add(img.gameObject);
            if (rng.NextDouble() < 0.4) swaying.Add(img.rectTransform);
        }
        int flowers = 2 + rng.Next(3);
        for (int i = 0; i < flowers; i++)
        {
            float y = y0 + (float)rng.NextDouble() * Spacing;
            float roadX = Wave(y);
            float side = rng.NextDouble() < 0.5 ? -1f : 1f;
            float x = roadX + side * (Road * 0.5f + Outline + 22f + (float)rng.NextDouble() * 30f);
            string key = "map_flower_" + (1 + rng.Next(6));
            if (!GameArt.I.Has(key)) continue;
            var img = UIKit.Image(decorLayer, key, new Vector2(0.5f, 0.5f), new Vector2(x, y), new Vector2(58, 58));
            img.rectTransform.localRotation = Quaternion.Euler(0, 0, (float)rng.NextDouble() * 60f - 30f);
            list.Add(img.gameObject);
        }

        // the level node
        if (level <= current + LockedAhead) list.Add(Node(level));
        // treasure chest on the road after every 5th level, until it is opened
        if (level % Progress.ChestEvery == 0 && level > Progress.ChestOpened && level <= current + LockedAhead && GameArt.I.Has("map_chest"))
        {
            int lv = level;
            var holder = UIKit.Place(UIKit.Rect("chest" + level, nodeLayer), new Vector2(0.5f, 0.5f), P(YOf(level) + Spacing * 0.5f), new Vector2(150, 150));
            var cb = UIKit.ArtButton(holder, "map_chest", new Vector2(0.5f, 0.5f), Vector2.zero, 150, () =>
            {
                if (lv < current) onChest?.Invoke(lv);
                else { StartCoroutine(Wiggle(holder)); AudioManager.Play("Hit", 0.4f); }
            });
            if (level < current) swaying.Add((RectTransform)cb.transform);   // a ready chest wobbles to invite a tap
            if (level < current + Progress.ChestEvery) pulsing.Add(holder);   // the ready / next chest pulses so it catches the eye
            list.Add(holder.gameObject);
        }
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
        bool done = level < current, cur = level == current;
        string key = done ? "map_node_done" : cur ? "map_node_current" : "map_node_locked";
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
        if (!done && !cur) num.GetComponent<Outline>().effectColor = new Color(1f, 1f, 1f, 0.6f);
        if (cur) currentPulse = b.transform;
        return holder.gameObject;
    }

    public void RemoveChest(int level)
    {
        var t = nodeLayer.Find("chest" + level);
        if (t != null) Destroy(t.gameObject);
    }

    // the marker stands beside a node, on the side away from the road's bend, so it never covers the next level
    static Vector2 MarkerSpot(int level, float t)
    {
        Vector2 c = P(YOf(level));
        float side = c.x > 0 ? -1f : 1f;
        return c + new Vector2(side * (CurrentSize * 0.5f + 95f), 55f + 12f * Mathf.Abs(Mathf.Sin(t * 3f)));
    }

    IEnumerator PopNode(Transform n)
    {
        float a = 0f;
        while (a < 0.35f && n != null) { a += Time.unscaledDeltaTime; float s = 1f + 0.35f * Mathf.Sin(Mathf.Clamp01(a / 0.35f) * Mathf.PI); n.localScale = new Vector3(s, s, 1f); yield return null; }
    }

    void Pick(int level)
    {
        if (Mathf.Abs(velocity) > 400f) return;           // a fling, not a tap
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
        float lv = scroll / Spacing;                                 // level-ish at the screen centre (0-based)
        float ch = lv / ChapterLen;
        int c0 = Mathf.FloorToInt(ch);
        float f = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.75f, 1f, ch - c0));   // blend in the last quarter of a chapter
        var a = Background.Themes[Mod(c0, Background.Themes.Length)];
        var b = Background.Themes[Mod(c0 + 1, Background.Themes.Length)];
        Color bot = Color.Lerp(a.bottom, b.bottom, f), top = Color.Lerp(a.top, b.top, f);
        Kit.Gradient(bot, top, gradTex);
        // fog in the background colour: things vanish over the horizon instead of piling up
        if (fogTopTex != null) Fog(fogTopTex, top, true);
        if (fogBottomTex != null) Fog(fogBottomTex, bot, false);
        if (balanceText != null && shownBalance != Progress.Balance) { shownBalance = Progress.Balance; balanceText.text = shownBalance.ToString(); }
        if (pattern != null)
        {
            float tile = 420f;
            var r = root.rect;
            pattern.uvRect = new Rect(0, scroll * 0.6f / tile, r.width / tile, r.height / tile);
        }
    }

    static void Fog(Texture2D t, Color c, bool top)
    {
        for (int y = 0; y < 64; y++)
        {
            float k = y / 63f;                                // 0 = bottom of the band, 1 = top
            float a = top ? Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.05f, 0.8f, k)) : 0.75f * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.9f, 0.1f, k));
            t.SetPixel(0, y, new Color(c.r, c.g, c.b, a));
        }
        t.Apply();
    }

    static int Mod(int a, int m) => ((a % m) + m) % m;

    void OnDestroy() { if (gradTex != null) Destroy(gradTex); if (fogTopTex != null) Destroy(fogTopTex); if (fogBottomTex != null) Destroy(fogBottomTex); }
}
