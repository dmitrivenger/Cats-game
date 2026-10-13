using System.Collections;
using UnityEngine;

public enum PieceKind { Cat, Fish, Vase, Milk }
public enum Special { None, Line, Bomb, Rainbow, Hunter }
public enum Cover { None, Box, Yarn, Mint }

// One thing sitting in a board cell: a cat (optionally special, optionally under a cover),
// a fish bowl (walks down to the bottom to be collected) or a vase (static obstacle).
public class Piece : MonoBehaviour
{
    public PieceKind kind;
    public int color = -1;          // cats only (0..4)
    public Special special;
    public bool lineRow;            // Line: which way it fires when triggered indirectly
    public Cover cover;
    public int coverHp;             // Box: 2 -> 1 -> free
    public int x, y;
    public bool dead;

    public SpriteRenderer back, body, over, extra;
    public Transform visual;        // bobbing / squash lives here so logic position stays clean
    float cell;
    bool angry;
    float idlePhase;

    public static Piece Create(Transform parent, float cell, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        var p = go.AddComponent<Piece>();
        p.cell = cell;
        p.visual = new GameObject("visual").transform;
        p.visual.SetParent(go.transform, false);
        p.back = NewLayer(p.visual, "back", 8);     // behind the cat (inside of the box)
        p.body = NewLayer(p.visual, "body", 10);
        p.over = NewLayer(p.visual, "over", 12);
        p.extra = NewLayer(p.visual, "extra", 13);
        p.idlePhase = Random.value * 10f;
        return p;
    }

    static SpriteRenderer NewLayer(Transform parent, string n, int order)
    {
        var holder = new GameObject(n);
        holder.transform.SetParent(parent, false);
        var img = new GameObject("img");
        img.transform.SetParent(holder.transform, false);
        var sr = img.AddComponent<SpriteRenderer>();
        sr.sortingOrder = order;
        return sr;
    }

    // ---- rules ----
    public bool Movable => !dead && (kind == PieceKind.Fish || (kind == PieceKind.Cat && (cover == Cover.None || cover == Cover.Yarn)));
    public bool Falls => !dead && kind != PieceKind.Vase && kind != PieceKind.Milk && !(kind == PieceKind.Cat && (cover == Cover.Box || cover == Cover.Mint));
    public bool IsStatic => !Falls;
    // Specials are "colourless" (the art is one cream cat for every colour) - they never join a
    // colour run; they fire when swiped, tapped, or caught in another blast (Marbly rule).
    public int MatchColor => (!dead && kind == PieceKind.Cat && special == Special.None && (cover == Cover.None || cover == Cover.Yarn)) ? color : -1;
    public bool PlainCat => kind == PieceKind.Cat && special == Special.None && cover == Cover.None;

    // how much of the cell the art fills. The special cats' art includes things sticking out of the body
    // (arrows, fuse and ring, sparkles), so it is drawn larger to keep the cat itself the size of its neighbours.
    float BodyFill => special == Special.Line ? 0.72f : 0.92f;   // the navigator is smaller: the arrowheads of its compass rose peek out from behind
    // how far a cat may swell while pulsing and still stay inside its own cell (never over a neighbour)
    float MaxGrow => Mathf.Max(0.02f, 0.985f / (special == Special.Line ? 0.965f : BodyFill) - 1f);

    // the angry art of these cats has things flying out of the body (sparks), so it is laid out by the calm art
    string LayoutKey()
    {
        if (kind == PieceKind.Cat && special == Special.Line) return "special_line";
        if (kind == PieceKind.Cat && special == Special.Bomb) return "special_bomb";
        if (kind == PieceKind.Cat && special == Special.Rainbow) return "special_rainbow";
        if (kind == PieceKind.Fish) return "fish_bowl";             // the empty bowl sits exactly where the full one did
        return BodyKey();
    }

    // A painted compass rose (four arrows: up, down, left, right) lies BEHIND the navigator cat, so only its
    // arrowheads show around it. It stays inside the cell and breathes very gently.
    const float RoseSize = 0.98f;
    Transform rays;
    SpriteRenderer roseSr;

    // A cat with catnip is asleep: three "Z" drift up from its head and soft green catnip smoke rises from its paws.
    // All of it is drawn by code and stays inside the cat's own cell.
    Transform sleep;
    readonly Transform[] zT = new Transform[3];
    readonly SpriteRenderer[] zSr = new SpriteRenderer[3];
    const int Puffs = 8;                         // the green cloud the sleeping cat sits in
    readonly Transform[] mistT = new Transform[Puffs];
    readonly SpriteRenderer[] mistSr = new SpriteRenderer[Puffs];
    const int DarkCat = 3;                       // the black cat

    void SetSleep(bool on)
    {
        if (!on) { if (sleep != null && sleep.gameObject.activeSelf) { sleep.gameObject.SetActive(false); visual.localScale = Vector3.one; } return; }
        if (sleep == null)
        {
            sleep = new GameObject("sleep").transform; sleep.SetParent(visual, false);
            for (int i = 0; i < Puffs; i++)
            {
                var m = new GameObject("puff" + i).transform; m.SetParent(sleep, false);
                var ms = new GameObject("img").AddComponent<SpriteRenderer>(); ms.transform.SetParent(m, false);
                // a faint haze under the cat, and small soft puffs of smoke round it
                ms.sprite = Kit.SoftCircle(64);
                float d = cell * (i == 0 ? 0.62f : 0.27f) / Mathf.Max(0.0001f, ms.sprite.bounds.size.x);
                ms.transform.localScale = new Vector3(d, d, 1f);
                ms.sortingOrder = body.sortingOrder + 1;
                mistT[i] = m; mistSr[i] = ms;
            }
            for (int i = 0; i < 3; i++)
            {

                var z = new GameObject("z" + i).transform; z.SetParent(sleep, false);
                var zs = new GameObject("img").AddComponent<SpriteRenderer>(); zs.transform.SetParent(z, false);
                Kit.FitSprite(zs, "sleep_z", cell * 0.2f, 1f);
                zs.sortingOrder = body.sortingOrder + 3;
                zT[i] = z; zSr[i] = zs;
            }
        }
        sleep.gameObject.SetActive(true);
        AnimateSleep(Time.time + idlePhase);
    }

    void AnimateSleep(float t)
    {
        for (int i = 0; i < 3; i++)
        {
            // each Z rises from the head towards the top-right corner, growing, then fades out
            float u = Mathf.Repeat(t * 0.4f + i / 3f, 1f);
            zT[i].localPosition = new Vector3(cell * (0.14f + 0.24f * u + 0.025f * Mathf.Sin(u * 9f)), cell * (0.16f + 0.24f * u), 0);
            float zs = 0.45f + 0.55f * u;
            zT[i].localScale = new Vector3(zs, zs, 1f);
            zT[i].localRotation = Quaternion.Euler(0, 0, -12f + 10f * Mathf.Sin(u * 6f));
            zSr[i].color = new Color(1, 1, 1, Mathf.Clamp01(Mathf.Min(u * 5f, (1f - u) * 3f)));
        }
        for (int i = 0; i < Puffs; i++)
        {   // catnip smoke: soft wisps rise from the cat's paws, drift, swell, sway and melt away (all inside the cell)
            if (i == 0)
            {   // a faint green haze the cat lies in
                float w0 = Mathf.Sin(t * 0.9f);
                mistT[0].localPosition = new Vector3(0, -cell * 0.14f, 0);
                mistT[0].localScale = new Vector3(1.12f + 0.03f * w0, 0.75f + 0.03f * w0, 1f);
                mistSr[0].color = new Color(0.8f, 1f, 0.82f, color == DarkCat ? 0f : 0.05f + 0.015f * w0);
                continue;
            }
            float seed = i * 0.618034f, side = i % 2 == 0 ? -1f : 1f;
            float u = Mathf.Repeat(t * 0.2f + (i - 1) / (float)(Puffs - 1), 1f);         // 0 = born at the paws, 1 = gone
            float sway = Mathf.Sin(u * Mathf.PI * 2.4f + seed * 6f) * cell * 0.04f;
            // pale wisps curl up from the cat's sides past its ears and melt away above its head (the face stays clear)
            mistT[i].localPosition = new Vector3(side * cell * (0.35f - 0.12f * u) + sway, cell * (-0.22f + 0.66f * u), 0);
            float sc = 0.6f + 1.1f * u;
            mistT[i].localScale = new Vector3(sc * (1f - 0.25f * u), sc * (1f + 0.3f * u), 1f);   // stretches upward as it rises
            mistT[i].localRotation = Quaternion.Euler(0, 0, -side * 18f * u);
            float a = Mathf.Sin(Mathf.Pow(u, 0.8f) * Mathf.PI); a = a * Mathf.Sqrt(a);
            if (color == DarkCat) a *= 0.55f;                       // on black fur a pale mist shows much more
            float wl = u;                                           // minty at first, nearly white as it rises
            mistSr[i].color = new Color(Mathf.Lerp(0.72f, 0.96f, wl), Mathf.Lerp(1f, 1f, wl), Mathf.Lerp(0.76f, 0.96f, wl), 0.42f * a);
        }
    }

    // ---- the goldfish in its bowl: drawn from parts so it can swim on the spot - the tail sways, the fins
    // paddle, the body bobs. The parts share the fish's 512 canvas; each moving part turns round its root.
    static bool SwimArt => GameArt.I.Has("fish_bowl_empty") && GameArt.I.Has("fish_part_body");
    Transform swimmer;
    readonly Transform[] finT = new Transform[5];
    readonly SpriteRenderer[] finSr = new SpriteRenderer[5];
    static readonly string[] FinKeys = { "fish_part_tail", "fish_part_dorsal", "fish_part_body", "fish_part_fins", "fish_part_side" };
    // the root of each part on the fish canvas (pixels, y down)
    static readonly Vector2[] FinRoot = { new Vector2(214, 250), new Vector2(290, 168), new Vector2(256, 256), new Vector2(300, 306), new Vector2(404, 318) };
    const float FishInBowlX = 297f, FishInBowlY = 244f, FishInBowlScale = 0.5f;   // where the fish swam in the old bowl art

    void SetSwimmer(bool on)
    {
        if (!on) { if (swimmer != null) swimmer.gameObject.SetActive(false); return; }
        float ppu = body.sprite != null ? body.sprite.pixelsPerUnit : 100f;
        if (swimmer == null)
        {
            swimmer = new GameObject("swimmer").transform;
            for (int i = 0; i < FinKeys.Length; i++)
            {
                var holder = new GameObject(FinKeys[i]).transform; holder.SetParent(swimmer, false);
                var sr = new GameObject("img").AddComponent<SpriteRenderer>(); sr.transform.SetParent(holder, false);
                sr.sprite = GameArt.I.S(FinKeys[i]);
                finT[i] = holder; finSr[i] = sr;
            }
        }
        // a child of the bowl sprite, so it follows its size and place; positions in the bowl's sprite units
        swimmer.SetParent(body.transform, false);
        swimmer.gameObject.SetActive(true);
        for (int i = 0; i < FinKeys.Length; i++)
        {
            float fppu = finSr[i].sprite != null ? finSr[i].sprite.pixelsPerUnit : ppu;
            var root = new Vector2((FinRoot[i].x - 256f) / fppu, (256f - FinRoot[i].y) / fppu);
            finT[i].localPosition = root;
            finSr[i].transform.localPosition = -root;
        }
        swimmer.localScale = new Vector3(-FishInBowlScale, FishInBowlScale, 1f);       // it faces left in the bowl
        swimmer.localPosition = new Vector3((FishInBowlX - 256f) / ppu, (256f - FishInBowlY) / ppu, 0f);
        SetSwimOrder(body.sortingOrder);
        AnimateSwim(Time.time + idlePhase);
    }

    void SetSwimOrder(int baseOrder)
    {   // tail and the top fin behind the body, the lower fins in front
        int[] d = { 1, 1, 2, 3, 3 };
        for (int i = 0; i < finSr.Length; i++) if (finSr[i] != null) finSr[i].sortingOrder = baseOrder + d[i];
    }

    void AnimateSwim(float t)
    {
        float w = t * 2.3f;
        finT[0].localRotation = Quaternion.Euler(0, 0, 13f * Mathf.Sin(w));                       // tail sways
        finT[0].localScale = new Vector3(1f - 0.07f * Mathf.Abs(Mathf.Sin(w)), 1f, 1f);           // and folds a little at each stroke
        finT[1].localRotation = Quaternion.Euler(0, 0, 5f * Mathf.Sin(w * 0.6f + 1f));            // the top fin ripples
        finT[3].localRotation = Quaternion.Euler(0, 0, 9f * Mathf.Sin(w * 0.9f + 2f));            // the long lower fins trail
        finT[4].localRotation = Quaternion.Euler(0, 0, 20f * Mathf.Sin(w * 1.8f));                // the little fin paddles fast
        float ppu = body.sprite != null ? body.sprite.pixelsPerUnit : 100f;
        // the whole fish bobs and drifts a touch, nose dipping as it goes
        swimmer.localPosition = new Vector3((FishInBowlX - 256f + 6f * Mathf.Sin(t * 0.7f)) / ppu, (256f - FishInBowlY + 5f * Mathf.Sin(t * 1.3f)) / ppu, 0f);
        swimmer.localRotation = Quaternion.Euler(0, 0, 3f * Mathf.Sin(t * 1.3f + 0.8f));
    }

    // ---- box helmet: two eyes peek out of the holes and look about
    // a low wide box pulled down over the head to the eyes: it covers the top half of the cat across its
    // whole width, the belly and paws show below it
    const float BoxFill = 1.0f, BoxSquash = 1f, BoxLift = 0.14f;
    // eye holes, measured on the art as a share of the box width from its centre (same on both boxes)
    static Vector2 HolePos(Vector2 u) => new Vector2(u.x * BoxFill, u.y * BoxFill * BoxSquash + BoxLift);
    static readonly Vector2[] HoleFull = { new Vector2(-0.1367f, -0.0882f), new Vector2(0.1345f, -0.0869f) };
    static readonly Vector2[] HoleDamaged = HoleFull;
    const float HoleSize = 0.142f * BoxFill;
    Transform eyes;
    readonly SpriteRenderer[] holeSr = new SpriteRenderer[2], eyeSr = new SpriteRenderer[2];
    Vector2 look, lookTo; float nextLook, nextBlink, blinkT = -1f, startle;
    static Sprite eyeSprite;

    static Sprite EyeSprite()
    {   // a round cat eye: golden-green iris, big black pupil, a white glint
        if (eyeSprite != null) return eyeSprite;
        const int S = 64; var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float dx = (x + 0.5f) / S * 2f - 1f, dy = (y + 0.5f) / S * 2f - 1f, d = Mathf.Sqrt(dx * dx + dy * dy);
                Color c = Color.Lerp(new Color(0.98f, 0.86f, 0.25f), new Color(0.55f, 0.78f, 0.2f), Mathf.Clamp01(d * 1.2f - 0.1f));
                float pupil = Mathf.Sqrt(dx * dx * 2.4f + dy * dy * 0.9f);                    // a slightly tall pupil
                if (pupil < 0.52f) c = Color.Lerp(new Color(0.06f, 0.05f, 0.08f), c, Mathf.Clamp01((pupil - 0.46f) / 0.06f));
                float glint = Mathf.Sqrt((dx + 0.3f) * (dx + 0.3f) + (dy - 0.32f) * (dy - 0.32f));
                if (glint < 0.2f) c = Color.Lerp(Color.white, c, Mathf.Clamp01((glint - 0.14f) / 0.06f));
                if (d > 0.9f) c = Color.Lerp(c, new Color(0.2f, 0.14f, 0.08f), Mathf.Clamp01((d - 0.9f) / 0.06f));
                c.a = Mathf.Clamp01((1f - d) / 0.04f);
                tex.SetPixel(x, y, c);
            }
        tex.Apply(); tex.filterMode = FilterMode.Bilinear;
        eyeSprite = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), S);
        return eyeSprite;
    }

    void SetBoxEyes(bool on)
    {
        if (!on) { if (eyes != null) eyes.gameObject.SetActive(false); return; }
        if (eyes == null)
        {
            eyes = new GameObject("eyes").transform; eyes.SetParent(visual, false);
            for (int i = 0; i < 2; i++)
            {
                holeSr[i] = new GameObject("hole" + i).AddComponent<SpriteRenderer>(); holeSr[i].transform.SetParent(eyes, false);
                holeSr[i].sprite = Kit.SoftCircle(64); holeSr[i].color = new Color(0.1f, 0.06f, 0.04f);   // dark inside of the box
                eyeSr[i] = new GameObject("eye" + i).AddComponent<SpriteRenderer>(); eyeSr[i].transform.SetParent(eyes, false);
                eyeSr[i].sprite = EyeSprite();
            }
            nextLook = Time.time + Random.Range(0.3f, 1.5f); nextBlink = Time.time + Random.Range(1f, 4f);
        }
        var holes = coverHp >= 2 ? HoleFull : HoleDamaged;
        for (int i = 0; i < 2; i++)
        {
            float hs = HoleSize * 1.12f * cell / holeSr[i].sprite.bounds.size.x;
            holeSr[i].transform.localScale = new Vector3(hs, hs, 1f);
            var hp = HolePos(holes[i]);
            holeSr[i].transform.localPosition = new Vector3(hp.x * cell, hp.y * cell, 0);
            holeSr[i].sortingOrder = body.sortingOrder + 1; eyeSr[i].sortingOrder = body.sortingOrder + 2;
        }
        eyes.gameObject.SetActive(true);
        AnimateEyes(true);
    }

    void AnimateEyes(bool snap)
    {
        float now = Time.time;
        if (now > nextLook)
        {   // look somewhere else now and then (sometimes straight ahead)
            lookTo = Random.value < 0.3f ? Vector2.zero : Random.insideUnitCircle.normalized * Random.Range(0.6f, 1f);
            nextLook = now + Random.Range(0.8f, 2.4f);
        }
        look = snap ? lookTo : Vector2.Lerp(look, lookTo, 1f - Mathf.Exp(-Time.deltaTime * 10f));
        if (blinkT < 0f && now > nextBlink) { blinkT = 0f; nextBlink = now + Random.Range(2f, 5f); }
        float open = 1f;
        if (blinkT >= 0f) { blinkT += Time.deltaTime; float k = blinkT / 0.16f; open = k < 1f ? Mathf.Abs(1f - 2f * k) : 1f; if (k >= 1f) blinkT = -1f; }
        startle = Mathf.Max(0f, startle - Time.deltaTime * 1.6f);
        var holes = coverHp >= 2 ? HoleFull : HoleDamaged;
        float size = HoleSize * cell * (0.82f + 0.18f * startle);
        float travel = HoleSize * cell * 0.16f * (1f - startle);
        for (int i = 0; i < 2; i++)
        {
            if (eyeSr[i] == null) continue;
            float es = size / eyeSr[i].sprite.bounds.size.x;
            eyeSr[i].transform.localScale = new Vector3(es, es * Mathf.Max(0.08f, open), 1f);
            var hp = HolePos(holes[i]);
            eyeSr[i].transform.localPosition = new Vector3(hp.x * cell + look.x * travel, hp.y * cell + look.y * travel, 0);
        }
    }

    // first hit on the box: it crumples hard, the cat's eyes go wide
    public IEnumerator BoxCrush()
    {
        startle = 1f;
        yield return Kit.Tween(0.5f, k =>
        {
            float w = Mathf.Exp(-k * 5f) * Mathf.Cos(k * Mathf.PI * 5f);         // squash, overshoot, settle
            visual.localScale = new Vector3(1f + 0.22f * w, 1f - 0.3f * w, 1f);
            visual.localPosition = new Vector3(0, -cell * 0.13f * Mathf.Max(0f, w), 0);
            visual.localRotation = Quaternion.Euler(0, 0, 9f * Mathf.Sin(k * 40f) * (1f - k));
        });
        visual.localScale = Vector3.one; visual.localPosition = Vector3.zero; visual.localRotation = Quaternion.identity;
    }

    void SetRays(bool on)
    {
        if (!on || !GameArt.I.Has("compass_rose")) { if (rays != null) rays.gameObject.SetActive(false); return; }
        if (rays == null)
        {
            rays = new GameObject("rose").transform; rays.SetParent(visual, false);
            roseSr = new GameObject("img").AddComponent<SpriteRenderer>(); roseSr.transform.SetParent(rays, false);
            Kit.FitSprite(roseSr, "compass_rose", cell, RoseSize);
            roseSr.sortingOrder = body.sortingOrder - 1;
        }
        rays.gameObject.SetActive(true);
    }

    public string BodyKey()
    {
        switch (kind)
        {
            case PieceKind.Fish: return SwimArt ? "fish_bowl_empty" : "fish_bowl";
            case PieceKind.Vase: return "vase";
            case PieceKind.Milk: return "milk_bucket";
        }
        switch (special)
        {
            // special cats have an angry face too (shown just before they go off), when the art exists
            case Special.Line: return angry && GameArt.I.Has("special_line_an") ? "special_line_an" : "special_line";
            case Special.Bomb: return angry && GameArt.I.Has("special_bomb_an") ? "special_bomb_an" : "special_bomb";
            case Special.Rainbow: return angry && GameArt.I.Has("special_rainbow_an") ? "special_rainbow_an" : "special_rainbow";
            case Special.Hunter: return "special_hunter";
        }
        if (cover == Cover.Mint && GameArt.I.Has("mint_face_" + color)) return "mint_face_" + color;
        if (angry && GameArt.I.Has("cat_" + color + "_an")) return "cat_" + color + "_an";
        return "cat_" + color;
    }

    public void Refresh()
    {
        if (kind == PieceKind.Cat && cover == Cover.Box)   // a smaller cat sitting low under the box: only the belly and paws show below it
            Kit.FitSpriteAs(body, BodyKey(), LayoutKey(), cell, InBoxFill, new Vector2(0, -cell * InBoxDrop));
        else Kit.FitSpriteAs(body, BodyKey(), LayoutKey(), cell, kind == PieceKind.Vase || kind == PieceKind.Milk ? 0.9f : BodyFill);
        if (body.sprite == null) Placeholder();
        body.color = Color.white;

        SetRays(kind == PieceKind.Cat && special == Special.Line);
        SetSleep(kind == PieceKind.Cat && cover == Cover.Mint);
        SetSwimmer(kind == PieceKind.Fish && SwimArt);
        SetBoxEyes(kind == PieceKind.Cat && cover == Cover.Box);

        over.sprite = null; extra.sprite = null; back.sprite = null;
        extra.transform.parent.localRotation = Quaternion.identity;
        if (kind == PieceKind.Cat)
        {
            switch (cover)
            {
                case Cover.Box:
                    // the box is worn like a helmet; the eyes behind its two holes are drawn by SetBoxEyes
                    Kit.FitSprite(extra, coverHp >= 2 ? "box_full" : "box_damaged", cell, BoxFill, new Vector2(0, cell * BoxLift));
                    { var bs = extra.transform.localScale; bs.y *= BoxSquash; extra.transform.localScale = bs; }
                    break;
                case Cover.Yarn:
                    // the strand is wound round the lower half of the cat (belly and paws), the head stays free
                    // the far half of the loop goes behind the cat, the near half (with the crossing and the loose end) in front
                    if (GameArt.I.Has("yarn_wrap_front") && GameArt.I.Has("yarn_wrap_back"))
                    {
                        Kit.FitSpriteAs(back, "yarn_wrap_back", "yarn_wrap", cell, YarnFill, new Vector2(0, -cell * YarnDrop));
                        Kit.FitSpriteAs(over, "yarn_wrap_front", "yarn_wrap", cell, YarnFill, new Vector2(0, -cell * YarnDrop));
                    }
                    else Kit.FitSprite(over, "yarn_wrap", cell, YarnFill, new Vector2(0, -cell * YarnDrop));
                    break;
                case Cover.Mint:
                    break;                                    // the sleepy face is the body art; "Zzz" and the mist are SetSleep
            }
        }
    }

    void Placeholder()
    {
        // coloured circle so a missing sprite never makes a cell invisible
        body.sprite = Kit.SoftCircle(64);
        float s = cell * 0.85f;
        body.transform.localScale = new Vector3(s, s, 1);
        body.transform.localPosition = Vector3.zero;
        Color[] pal = { new Color(1f, .6f, .3f), Color.white, Color.gray, new Color(.15f, .15f, .15f), new Color(1f, .85f, .2f) };
        body.color = kind == PieceKind.Cat && color >= 0 ? pal[color % pal.Length] : new Color(.4f, .7f, 1f);
    }

    const float YarnFill = 1.0f, YarnDrop = 0.2f;
    const float InBoxFill = 0.92f, InBoxDrop = 0f;          // the cat under the box keeps its normal size

    public void SetAngry(bool on)
    {
        if (angry == on || kind != PieceKind.Cat || special == Special.Hunter || cover == Cover.Mint) return;
        angry = on;
        if (cover == Cover.Box) Kit.FitSpriteAs(body, BodyKey(), LayoutKey(), cell, InBoxFill, new Vector2(0, -cell * InBoxDrop));
        else Kit.FitSpriteAs(body, BodyKey(), LayoutKey(), cell, BodyFill);
    }

    public void SetOrder(int baseOrder)
    {
        back.sortingOrder = baseOrder - 2; body.sortingOrder = baseOrder; over.sortingOrder = baseOrder + 2; extra.sortingOrder = baseOrder + 3;
        if (roseSr != null) roseSr.sortingOrder = baseOrder - 1;
        for (int i = 0; i < 3; i++) if (zSr[i] != null) zSr[i].sortingOrder = baseOrder + 3;
        for (int i = 0; i < Puffs; i++) if (mistSr[i] != null) mistSr[i].sortingOrder = baseOrder + 1;
        for (int i = 0; i < 2; i++) if (eyeSr[i] != null) { holeSr[i].sortingOrder = baseOrder + 1; eyeSr[i].sortingOrder = baseOrder + 2; }
        SetSwimOrder(baseOrder);
    }

    // gentle idle life: specials pulse, mint stars spin, everything breathes a tiny bit
    void Update()
    {
        if (dead) return;
        float t = Time.time + idlePhase;
        if (special != Special.None)
        {
            float s = 1f + Mathf.Min(0.05f, MaxGrow) * Mathf.Sin(t * 5f);
            visual.localScale = new Vector3(s, s, 1);
            if (rays != null && rays.gameObject.activeSelf)
            {   // the cat pulses a little; the rose counters it so it never grows past the cell, and only breathes
                float rk = (0.97f + 0.03f * (0.5f + 0.5f * Mathf.Sin(t * 2.4f))) / s;
                rays.localScale = new Vector3(rk, rk, 1f);
            }
        }
        if (cover == Cover.Box && eyes != null && eyes.gameObject.activeSelf) AnimateEyes(false);
        if (kind == PieceKind.Fish && swimmer != null && swimmer.gameObject.activeSelf) AnimateSwim(t);
        if (cover == Cover.Mint && sleep != null && sleep.gameObject.activeSelf)
        {   // asleep: slow breathing, Zzz, mist
            float br = 0.018f * Mathf.Sin(t * 1.6f);
            visual.localScale = new Vector3(1f + br, 1f - br, 1f);
            AnimateSleep(t);
        }
    }

    public IEnumerator Wiggle()
    {
        float d = 0.3f, t = 0;
        while (t < d)
        {
            t += Time.deltaTime;
            float k = t / d;
            visual.localRotation = Quaternion.Euler(0, 0, Mathf.Sin(k * Mathf.PI * 4) * 10f * (1 - k));
            yield return null;
        }
        visual.localRotation = Quaternion.identity;
    }

    public IEnumerator Pulse(float amount = 0.15f, float dur = 0.35f)
    {
        float t = 0;
        if (kind == PieceKind.Cat && cover != Cover.Box) amount = Mathf.Min(amount, MaxGrow);   // a pulsing cat never covers its neighbours
        while (t < dur)
        {
            t += Time.deltaTime;
            float s = 1f + amount * Mathf.Sin(Mathf.Clamp01(t / dur) * Mathf.PI);
            visual.localScale = new Vector3(s, s, 1);
            yield return null;
        }
        visual.localScale = Vector3.one;
    }
}
