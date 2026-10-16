using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Small shared helpers: easing tweens as coroutines, procedural placeholder sprites (rounded
// panels, soft circles) for art that doesn't exist yet, and world-space sprite placement that
// fits a sprite by its VISIBLE content (GameArt content rect), not by its padded canvas.
public static class Kit
{
    public static float EaseOutBack(float t) { const float c1 = 1.70158f, c3 = c1 + 1f; t -= 1f; return 1f + c3 * t * t * t + c1 * t * t; }
    public static float EaseOutCubic(float t) { t = 1f - t; return 1f - t * t * t; }
    public static float EaseInCubic(float t) => t * t * t;
    public static float EaseInOut(float t) => t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) / 2f;

    public static IEnumerator Tween(float duration, Action<float> step, Func<float, float> ease = null)
    {
        float t = 0f;
        if (duration <= 0f) { step(1f); yield break; }
        while (t < duration)
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / duration);
            step(ease != null ? ease(k) : k);
            yield return null;
        }
        step(1f);
    }

    // ---------------- sprites ----------------

    // Place `sr` so that the visible part of `key`'s sprite fits inside a `size` x `size` box
    // centred on the parent's origin (with `fill` = fraction of the box).
    public static void FitSprite(SpriteRenderer sr, string key, float size, float fill = 0.92f, Vector2 offset = default)
        => FitSpriteAs(sr, key, key, size, fill, offset);

    // Like FitSprite, but positions/scales `key` using the visible rect of `layoutKey` - for art
    // split into layers on one shared canvas (e.g. box back/front), so the halves stay aligned.
    public static void FitSpriteAs(SpriteRenderer sr, string key, string layoutKey, float size, float fill = 0.92f, Vector2 offset = default)
    {
        var s = GameArt.I.S(key);
        sr.sprite = s;
        if (s == null) return;
        Rect c = GameArt.I.Content(layoutKey);
        Vector2 texSize = s.rect.size / s.pixelsPerUnit;           // full sprite in world units at scale 1
        Vector2 vis = new Vector2(texSize.x * c.width, texSize.y * c.height);
        float scale = size * fill / Mathf.Max(vis.x, vis.y, 0.0001f);
        sr.transform.localScale = new Vector3(scale, scale, 1f);
        // visible-centre offset relative to the sprite pivot (centre)
        Vector2 visCentre = new Vector2((c.x + c.width * 0.5f - 0.5f) * texSize.x, (c.y + c.height * 0.5f - 0.5f) * texSize.y);
        sr.transform.localPosition = new Vector3(-visCentre.x * scale + offset.x, -visCentre.y * scale + offset.y, sr.transform.localPosition.z);
    }

    static readonly Dictionary<string, Sprite> generated = new Dictionary<string, Sprite>();

    // Rounded rectangle, white, 9-sliceable. Tint with Image.color / SpriteRenderer.color.
    public static Sprite RoundedRect(int size = 128, int radius = 40, int border = 0)
    {
        string k = $"rr{size}_{radius}_{border}";
        if (generated.TryGetValue(k, out var sp)) return sp;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        var px = new Color32[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = Mathf.Max(radius - x - 0.5f, x + 0.5f - (size - radius), 0f);
                float dy = Mathf.Max(radius - y - 0.5f, y + 0.5f - (size - radius), 0f);
                float d = Mathf.Sqrt(dx * dx + dy * dy) - radius;   // <0 inside
                float a = Mathf.Clamp01(0.5f - d);
                if (border > 0) a *= Mathf.Clamp01(d + border + 0.5f); // ring only
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
        tex.SetPixels32(px); tex.Apply();
        sp = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(radius + 2, radius + 2, radius + 2, radius + 2));
        generated[k] = sp;
        return sp;
    }

    public static Sprite SoftCircle(int size = 64)
    {
        string k = "circle" + size;
        if (generated.TryGetValue(k, out var sp)) return sp;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[size * size];
        float r = size * 0.5f;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r)) / r;
                float a = Mathf.Clamp01((1f - d) * 3f);
                px[y * size + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
        tex.SetPixels32(px); tex.Apply();
        sp = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
        generated[k] = sp;
        return sp;
    }

    // Vertical gradient (bottom -> top), used as the background base.
    public static Texture2D Gradient(Color bottom, Color top, Texture2D reuse = null)
    {
        var tex = reuse != null ? reuse : new Texture2D(1, 64, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        for (int y = 0; y < 64; y++) tex.SetPixel(0, y, Color.Lerp(bottom, top, y / 63f));
        tex.Apply();
        return tex;
    }
}

// Fire-and-forget world FX. Everything self-destructs; runs on a hidden host MonoBehaviour.
public class FX : MonoBehaviour
{
    static FX host;
    public static FX Host
    {
        get
        {
            if (host == null) { var go = new GameObject("FX"); host = go.AddComponent<FX>(); }
            return host;
        }
    }

    public const int FxOrder = 60;

    // Soft sparkle burst in a colour (placeholder until the real 2D explosion effects exist).
    public static void Burst(Vector3 pos, Color color, float size, int count = 10)
    {
        var dot = GameArt.I.S("bokeh_dot") ?? Kit.SoftCircle();
        for (int i = 0; i < count; i++)
        {
            var go = new GameObject("spark");
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = dot; sr.sortingOrder = FxOrder; sr.color = Color.Lerp(color, Color.white, UnityEngine.Random.value * 0.6f);
            go.transform.position = pos;
            float s = size * UnityEngine.Random.Range(0.12f, 0.28f) / (dot.bounds.size.x);
            Vector2 dir = UnityEngine.Random.insideUnitCircle.normalized * size * UnityEngine.Random.Range(0.5f, 1.1f);
            Host.StartCoroutine(Fly(go.transform, sr, pos, pos + (Vector3)dir, s, UnityEngine.Random.Range(0.35f, 0.55f), 0f, false));
        }
    }

    // ================= regular cat pop ("пушистый хлопок") =================
    // Layers (Sprites/FX/Burn/regular), all driven here; white layers take the cat's colour:
    //   flash  0.00-0.10  star flash snaps open and fades
    //   ring   0.00-0.25  shockwave ring expands and thins out
    //   puff   0.03-0.40  fluffy fur cloud swells, drifts up, dissolves
    //   fur    0.05-0.55  5-7 fur tufts fly out, spin, fall with gravity
    //   spark  0.05-0.45  3-4 golden sparkles shoot out and twinkle
    //   bonus  ~1 in 8    a small heart or paw print floats up
    // Falls back to the old dot burst when the art is missing.
    public const float BonusChance = 0.12f;   // heart / paw on roughly 1 pop in 8

    // ---- small "juice" effects for every move ----
    static SpriteRenderer Blob(string name, Sprite sprite, Vector3 pos, int order, Color c)
    {
        var sr = new GameObject(name).AddComponent<SpriteRenderer>();
        sr.sprite = sprite; sr.sortingOrder = order; sr.color = c; sr.transform.position = pos;
        return sr;
    }

    // a streak of light that sweeps along a matched line (or a soft glow over a 2x2 square)
    public static void RunTrace(Vector3 a, Vector3 b, Color tint, float cell, bool square)
    {
        Host.StartCoroutine(RunTraceRoutine(a, b, tint, cell, square));
    }

    static IEnumerator RunTraceRoutine(Vector3 a, Vector3 b, Color tint, float cell, bool square)
    {
        Color glowC = Color.Lerp(tint, Color.white, 0.25f);
        var bar = Kit.RoundedRect(128, 60);
        Vector3 mid = (a + b) * 0.5f;
        bool vertical = Mathf.Abs(b.y - a.y) > Mathf.Abs(b.x - a.x);
        float len = (square ? cell : Vector3.Distance(a, b)) + cell * 0.9f;
        float thick = square ? len : cell * 0.62f;
        var glow = Blob("trace_glow", bar, mid, FxOrder - 2, new Color(glowC.r, glowC.g, glowC.b, 0f));   // a soft coloured haze over the cats
        var core = Blob("trace_core", bar, mid, FxOrder - 1, new Color(1f, 1f, 1f, 0f));              // a thin bright line over them
        foreach (var sr in new[] { glow, core }) { sr.drawMode = SpriteDrawMode.Sliced; if (vertical && !square) sr.transform.rotation = Quaternion.Euler(0, 0, 90f); }
        var head = square ? null : Blob("trace_head", Kit.SoftCircle(64), a, FxOrder, new Color(1f, 1f, 1f, 0.95f));
        float t = 0f; const float Sweep = 0.13f, Life = 0.32f;
        Vector3 dir = square ? Vector3.zero : (b - a).normalized;
        Vector3 start = a - dir * cell * 0.45f;
        while (t < Life)
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / Sweep), e = Kit.EaseOutCubic(k);
            float fade = t < Sweep ? 1f : 1f - (t - Sweep) / (Life - Sweep);
            float cur = square ? len : len * e;
            Vector3 centre = square ? mid : start + dir * cur * 0.5f;
            glow.transform.position = centre; core.transform.position = centre;
            glow.size = new Vector2(cur, thick * (1.25f + 0.15f * Mathf.Sin(t * 30f)));
            core.size = new Vector2(cur, square ? thick * 0.6f : thick * 0.22f);
            glow.color = new Color(glowC.r, glowC.g, glowC.b, (square ? 0.3f : 0.42f) * fade);
            core.color = new Color(1f, 1f, 1f, (square ? 0.35f : 0.85f) * fade);
            if (head != null)
            {
                head.transform.position = start + dir * cur;
                head.transform.localScale = Vector3.one * cell * 0.9f * (1f - 0.5f * k);
                var hc = head.color; hc.a = 0.95f * (1f - k); head.color = hc;
            }
            yield return null;
        }
        Destroy(glow.gameObject); Destroy(core.gameObject); if (head != null) Destroy(head.gameObject);
    }

    // ---- two special cats merging ----
    static SpriteRenderer ArtBlob(string key, Vector3 pos, int order, Color c, out float unit)
    {
        var sp = GameArt.I.S(key) ?? Kit.SoftCircle(64);
        unit = sp.bounds.size.x > 0.001f ? 1f / sp.bounds.size.x : 1f;     // scale that makes the sprite 1 world unit wide
        return Blob(key, sp, pos, order, c);
    }

    public static void MergeSwirl(Vector3 pos, float cell, Color tint, Color tint2, float dur, bool rainbow)
    {
        Host.StartCoroutine(MergeSwirlRoutine(pos, cell, tint, tint2, dur, rainbow));
    }

    static IEnumerator MergeSwirlRoutine(Vector3 pos, float cell, Color tint, Color tint2, float dur, bool rainbow)
    {
        var back = ArtBlob("fx_merge_swirl", pos, 9, tint, out float u);                       // coloured, under the cats
        var front = ArtBlob("fx_merge_swirl", pos, FxOrder - 1, tint2, out float u2);          // the second cat's colour, over them
        float t = 0f;
        while (t < dur)
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / dur);
            // opens wide at once, then tightens onto the two cats while it spins faster and faster
            float size = cell * Mathf.Lerp(4.6f, 1.6f, Kit.EaseInCubic(k)) * Mathf.Min(1f, k * 6f);
            float rot = -(240f * k + 500f * k * k);
            Color c = rainbow ? Hue(k * 1.5f, 0.5f) : tint;
            float a = Mathf.Min(1f, k * 5f);
            back.transform.localScale = Vector3.one * size * u; back.transform.rotation = Quaternion.Euler(0, 0, rot);
            front.transform.localScale = Vector3.one * size * 0.8f * u2; front.transform.rotation = Quaternion.Euler(0, 0, rot * 1.3f + 60f);
            back.color = new Color(c.r, c.g, c.b, 0.95f * a);
            Color c2 = rainbow ? Hue(k * 1.5f + 0.5f, 0.5f) : tint2;
            front.color = new Color(c2.r, c2.g, c2.b, 0.8f * a);
            yield return null;
        }
        Destroy(back.gameObject); Destroy(front.gameObject);
    }

    public static void MergeFlash(Vector3 pos, float cell, Color tint, Color tint2, bool rainbow)
    {
        Host.StartCoroutine(MergeFlashRoutine(pos, cell, tint, tint2, rainbow));
    }

    static IEnumerator MergeFlashRoutine(Vector3 pos, float cell, Color tint, Color tint2, bool rainbow)
    {
        Color light = Color.Lerp(tint, Color.white, 0.2f);
        var screen = Blob("merge_screen", Kit.SoftCircle(64), pos, FxOrder - 3, new Color(1f, 1f, 1f, 0f));     // a soft white blink over the board
        var glow = ArtBlob("fx_merge_star", pos, FxOrder + 5, light, out float u);
        var star = ArtBlob("fx_merge_star", pos, FxOrder + 6, Color.white, out float u2);
        var ring = Blob("merge_ring", Kit.RoundedRect(256, 128, 10), pos, FxOrder + 4, Color.white);
        float t = 0f; const float Life = 0.42f;
        while (t < Life)
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / Life), e = Kit.EaseOutCubic(k), fade = (1f - k) * (1f - k);
            Color c = rainbow ? Hue(k * 2f, 0.45f) : light;
            glow.transform.localScale = Vector3.one * cell * Mathf.Lerp(1.6f, 7.5f, e) * u;
            glow.transform.rotation = Quaternion.Euler(0, 0, 40f * k);
            glow.color = new Color(c.r, c.g, c.b, 0.9f * fade);
            star.transform.localScale = Vector3.one * cell * Mathf.Lerp(1.0f, 4.8f, e) * u2;
            star.transform.rotation = Quaternion.Euler(0, 0, -25f * k);
            star.color = new Color(1f, 1f, 1f, fade);
            ring.transform.localScale = Vector3.one * cell * Mathf.Lerp(0.4f, 4.4f, e) / 2.56f;
            Color rc = rainbow ? Hue(k * 2f + 0.5f, 0.45f) : tint2;
            ring.color = new Color(rc.r, rc.g, rc.b, 0.9f * (1f - k));
            screen.transform.localScale = Vector3.one * cell * 26f;
            screen.color = new Color(1f, 1f, 1f, 0.2f * Mathf.Clamp01(1f - k * 2.5f));
            yield return null;
        }
        Destroy(screen.gameObject); Destroy(glow.gameObject); Destroy(star.gameObject); Destroy(ring.gameObject);
    }

    // the bright blink in the cat's colour at the moment it pops
    public static void PopFlash(Vector3 pos, Color tint, float cell)
    {
        Host.StartCoroutine(PopFlashRoutine(pos, tint, cell));
    }

    static IEnumerator PopFlashRoutine(Vector3 pos, Color tint, float cell)
    {
        yield return new WaitForSeconds(0.05f);
        Color c = Color.Lerp(tint, Color.white, 0.6f);
        var sr = Blob("pop_flash", Kit.SoftCircle(64), pos, FxOrder + 4, c);
        yield return Kit.Tween(0.16f, k =>
        {
            sr.transform.localScale = Vector3.one * cell * Mathf.Lerp(0.7f, 1.9f, Kit.EaseOutCubic(k));
            sr.color = new Color(c.r, c.g, c.b, 0.95f * (1f - k) * (1f - k));
        });
        Destroy(sr.gameObject);
    }

    // two little puffs of dust that roll out sideways from under a cat that has just landed
    public static void LandDust(Vector3 pos, float cell)
    {
        Host.StartCoroutine(LandDustRoutine(pos, cell));
    }

    static IEnumerator LandDustRoutine(Vector3 pos, float cell)
    {
        var sprite = GameArt.I.S("fx_puff") ?? Kit.SoftCircle(64);
        float unit = sprite.bounds.size.x > 0.001f ? 1f / sprite.bounds.size.x : 1f;
        var l = Blob("dust", sprite, pos, 9, new Color(1f, 1f, 1f, 0f));
        var r = Blob("dust", sprite, pos, 9, new Color(1f, 1f, 1f, 0f));
        yield return Kit.Tween(0.3f, k =>
        {
            float e = Kit.EaseOutCubic(k), s = cell * Mathf.Lerp(0.18f, 0.42f, e) * unit, al = 0.6f * (1f - k);
            l.transform.position = pos + new Vector3(-cell * 0.34f * e, cell * 0.06f * e, 0);
            r.transform.position = pos + new Vector3(cell * 0.34f * e, cell * 0.06f * e, 0);
            l.transform.localScale = new Vector3(-s, s, 1); r.transform.localScale = new Vector3(s, s, 1);
            l.color = new Color(1f, 1f, 1f, al); r.color = new Color(1f, 1f, 1f, al);
        });
        Destroy(l.gameObject); Destroy(r.gameObject);
    }

    public static void CatPop(Vector3 pos, Color tint, float cell, float power = 1f)
    {
        if (!GameArt.I.Has("fx_puff")) { Burst(pos, tint, cell, 8); return; }
        var R = new System.Random(UnityEngine.Random.Range(0, int.MaxValue));
        float Rnd(float a, float b) => a + (float)R.NextDouble() * (b - a);
        Color light = Color.Lerp(tint, Color.white, 0.55f);

        // flash
        Layer("fx_flash", pos, cell, FxOrder + 3, light, 0f, 0.12f, Rnd(-20f, 20f), 0f,
            k => Mathf.Lerp(0.35f, 1.35f * power, Kit.EaseOutCubic(k)), k => 1f - k * k, Vector3.zero);
        // ring
        Layer("fx_ring", pos, cell, FxOrder + 1, light, 0f, 0.26f, 0f, 0f,
            k => Mathf.Lerp(0.4f, 1.7f * power, Kit.EaseOutCubic(k)), k => 0.9f * (1f - k), Vector3.zero);
        // puff (cloud tinted in the cat colour; dark cats get a slightly lifted tone so it reads)
        Color puff = Color.Lerp(tint, Color.white, tint.grayscale < 0.45f ? 0.3f : 0.4f);   // soft tint keeps the fluffy shading
        puff.a = 0.9f;
        Layer("fx_puff", pos, cell, FxOrder, puff, 0.03f, 0.34f, Rnd(0f, 360f), Rnd(-90f, 90f),
            k => Mathf.Lerp(0.45f, 1.1f * power, Kit.EaseOutCubic(k)), k => k < 0.3f ? 1f : 1f - (k - 0.3f) / 0.7f, new Vector3(0, cell * 0.18f, 0));
        // fur tufts
        int fur = R.Next(4, 7);
        for (int i = 0; i < fur; i++)
        {
            string key = "fx_fur_" + (1 + R.Next(4));
            if (!GameArt.I.Has(key)) continue;
            float ang = (i + Rnd(-0.35f, 0.35f)) / fur * Mathf.PI * 2f;
            Vector2 v = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang) * 0.8f + 0.5f) * cell * Rnd(2.2f, 3.6f) * power;
            var t = Make(key, pos, cell * Rnd(0.3f, 0.42f), FxOrder + 2, puff, out var sr);
            Host.StartCoroutine(Tuft(t, sr, v, Rnd(-540f, 540f), Rnd(0.45f, 0.6f), Rnd(0.02f, 0.07f)));
        }
        // sparkles (keep their gold)
        int sp = R.Next(3, 5);
        for (int i = 0; i < sp; i++)
        {
            Vector3 dir = (Vector3)(UnityEngine.Random.insideUnitCircle.normalized * cell * Rnd(0.6f, 1.05f) * power);
            float size = Rnd(0.16f, 0.28f), delay = Rnd(0.04f, 0.1f), phase = Rnd(0f, 6f);
            Layer("fx_sparkle", pos, cell, FxOrder + 4, Color.white, delay, Rnd(0.32f, 0.45f), Rnd(-30f, 30f), Rnd(-200f, 200f),
                k => size * (0.6f + 0.4f * Mathf.Abs(Mathf.Sin(phase + k * 14f))) * (1f - 0.4f * k), k => k < 0.6f ? 1f : 1f - (k - 0.6f) / 0.4f, dir);
        }
        // bonus heart / paw
        if (UnityEngine.Random.value < BonusChance)
        {
            string key = UnityEngine.Random.value < 0.5f ? "fx_heart" : "fx_paw";
            if (GameArt.I.Has(key))
                Layer(key, pos, cell, FxOrder + 5, Color.white, 0.06f, 0.7f, Rnd(-15f, 15f), 0f,
                    k => 0.38f * Kit.EaseOutBack(Mathf.Min(1f, k * 3f)), k => k < 0.6f ? 1f : 1f - (k - 0.6f) / 0.4f, new Vector3(Rnd(-0.2f, 0.2f) * cell, cell * 0.9f, 0));
        }
    }

    // ================= Hunter cat ("Кот-Следопыт") =================
    // Layers (Sprites/FX/Burn/Hunter), white ones tinted forest green:
    //   compass   0.00-0.60  compass rose opens under the cat
    //   horn      0.02-0.32  sound waves roll out in 4 directions (the cross neighbours pop)
    //   needle    0.00-0.50  needle spins, then settles pointing at the target
    //   crosshair 0.12-hit   reticle closes in on the target
    //   arrow     charge-hit the arrow flies on a shallow arc with a green trail
    //   hit       on arrival burst + feathers, the arrow sticks for a moment and fades
    public const float HunterCharge = 0.4f, HunterHornHit = 0.12f;
    public static float HunterFly(float cells) => 0.2f + 0.025f * cells;
    public static readonly Color HunterGlow = new Color(0.42f, 0.88f, 0.42f);

    public static void HunterBlast(Vector3 o, Vector3 target, float cell, float delay, bool bomb = false)
    {
        Host.StartCoroutine(HunterRoutine(o, target, cell, delay, bomb));
    }

    static IEnumerator HunterRoutine(Vector3 o, Vector3 tgt, float cell, float delay, bool bomb)
    {
        if (delay > 0) yield return new WaitForSeconds(delay);
        Vector3 d = tgt - o;
        float ang = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
        float fly = HunterFly(d.magnitude / cell);
        Color glow = HunterGlow, light = Color.Lerp(HunterGlow, Color.white, 0.55f);
        var cg = glow; cg.a = 0.85f;

        Layer("hunter_compass", o, cell, 9, cg, 0f, HunterCharge + 0.25f, 0f, 30f,
            k => 2.1f * Kit.EaseOutBack(Mathf.Min(1f, k * 4f)), k => k < 0.65f ? 1f : 1f - (k - 0.65f) / 0.35f, Vector3.zero);
        for (int i = 0; i < 4; i++)
            Layer("hunter_horn", o, cell, FxOrder + 4, light, 0.02f + i * 0.02f, 0.3f, i * 90f, 0f,
                k => Mathf.Lerp(0.9f, 2.6f, Kit.EaseOutCubic(k)), k => 1f - k, Vector3.zero);
        if (GameArt.I.Has("hunter_needle")) Host.StartCoroutine(Needle(o, cell, ang, light));
        Layer("hunter_crosshair", tgt, cell, FxOrder + 5, light, 0.12f, HunterCharge - 0.12f + fly + 0.12f, 0f, 60f,
            k => Mathf.Lerp(1.9f, 1.15f, Kit.EaseOutCubic(Mathf.Min(1f, k * 2.5f))),
            k => k < 0.15f ? k / 0.15f : (k < 0.85f ? 1f : 1f - (k - 0.85f) / 0.15f), Vector3.zero);

        yield return new WaitForSeconds(HunterCharge);

        // ---- the arrow ----
        Transform arrow = null; SpriteRenderer asr = null;
        if (GameArt.I.Has("hunter_arrow"))
        {
            arrow = Make("hunter_arrow", o, cell * 1.25f, FxOrder + 8, Color.white, out asr);
            var mat = TrailMaterial("hunter_trail");
            if (mat != null)
            {
                var tr = arrow.gameObject.AddComponent<TrailRenderer>();
                tr.sharedMaterial = mat; tr.textureMode = LineTextureMode.Stretch;
                tr.time = 0.16f; tr.minVertexDistance = cell * 0.05f;
                tr.widthCurve = new AnimationCurve(new Keyframe(0f, cell * 0.5f), new Keyframe(1f, cell * 0.1f));
                var g = new Gradient();
                g.SetKeys(new[] { new GradientColorKey(light, 0f), new GradientColorKey(glow, 1f) },
                          new[] { new GradientAlphaKey(0.9f, 0f), new GradientAlphaKey(0f, 1f) });
                tr.colorGradient = g; tr.sortingOrder = FxOrder + 7;
            }
        }
        // hunter + bomb: a little bomb rides on the arrow's tip, wobbling, its fuse sparking
        Transform tipBomb = null; SpriteRenderer tbsr = null;
        if (bomb && arrow != null && GameArt.I.Has("hunter_bomb"))
            tipBomb = Make("hunter_bomb", o, cell * 0.62f, FxOrder + 9, Color.white, out tbsr);
        float nextSpark = 0f;
        Vector3 perp = new Vector3(-d.y, d.x, 0).normalized; if (perp.y < 0) perp = -perp;
        Vector3 ctrl = (o + tgt) * 0.5f + perp * d.magnitude * 0.18f;
        float age = 0f;
        while (age < fly)
        {
            age += Time.deltaTime;
            float k = Mathf.Clamp01(age / fly);
            Vector3 a = Vector3.Lerp(o, ctrl, k), b = Vector3.Lerp(ctrl, tgt, k);
            if (arrow != null)
            {
                arrow.position = Vector3.Lerp(a, b, k);
                Vector3 tan = b - a;
                arrow.rotation = Quaternion.Euler(0, 0, Mathf.Atan2(tan.y, tan.x) * Mathf.Rad2Deg);
                if (tipBomb != null)
                {
                    tipBomb.position = arrow.position + arrow.right * cell * 0.5f;
                    tipBomb.rotation = Quaternion.Euler(0, 0, Mathf.Sin(age * 40f) * 12f);
                    float bs = 1f + 0.08f * Mathf.Sin(age * 55f);
                    tipBomb.localScale = new Vector3(bs, bs, 1f);
                    if (age >= nextSpark && GameArt.I.Has("fx_sparkle"))
                    {   // fuse sparks trail behind
                        nextSpark = age + 0.035f;
                        Layer("fx_sparkle", tipBomb.position + new Vector3(cell * 0.12f, cell * 0.24f, 0f), cell, FxOrder + 9, BombHot, 0f, 0.25f,
                            UnityEngine.Random.Range(0f, 90f), 0f, k => 0.22f * (1f - k), k => 1f - k,
                            (Vector3)(UnityEngine.Random.insideUnitCircle * cell * 0.25f));
                    }
                }
            }
            yield return null;
        }
        if (tipBomb != null)
        {   // it hits: the bomb swells for an instant, then the board's blast takes over
            Host.StartCoroutine(TipBombPop(tipBomb, tbsr));
        }

        // ---- hit ----
        Layer("hunter_hit", tgt, cell, FxOrder + 6, light, 0f, 0.36f, UnityEngine.Random.Range(0f, 45f), 0f,
            k => Mathf.Lerp(0.6f, 1.8f, Kit.EaseOutCubic(k)), k => k < 0.3f ? 1f : 1f - (k - 0.3f) / 0.7f, Vector3.zero);
        for (int i = 0; i < 5; i++)
        {
            string key = "hunter_feather_" + UnityEngine.Random.Range(1, 5);
            if (!GameArt.I.Has(key)) continue;
            var t = Make(key, tgt, cell * UnityEngine.Random.Range(0.3f, 0.42f), FxOrder + 6, Color.Lerp(Color.white, light, 0.4f), out var sr);
            float fa = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
            Vector2 v = new Vector2(Mathf.Cos(fa), Mathf.Sin(fa) * 0.7f + 0.6f) * cell * UnityEngine.Random.Range(1.6f, 2.6f);
            Host.StartCoroutine(Ember(t, sr, v, false, UnityEngine.Random.Range(0.6f, 0.9f), 0f));
        }
        Shake(cell * 0.04f, 0.14f);
        if (arrow != null)
        {
            var tr = arrow.GetComponent<TrailRenderer>(); if (tr != null) tr.emitting = false;
            Quaternion rest = arrow.rotation;
            float t2 = 0f;
            while (t2 < 0.3f && arrow != null)
            {
                t2 += Time.deltaTime;
                float k = t2 / 0.3f;
                arrow.rotation = rest * Quaternion.Euler(0, 0, Mathf.Sin(k * 30f) * 8f * (1f - k));   // twang
                var c = asr.color; c.a = k < 0.5f ? 1f : 1f - (k - 0.5f) / 0.5f; asr.color = c;
                yield return null;
            }
            if (arrow != null) Destroy(arrow.gameObject);
        }
    }

    static IEnumerator TipBombPop(Transform t, SpriteRenderer sr)
    {
        float age = 0f, life = 0.14f;
        while (age < life && t != null)
        {
            age += Time.deltaTime;
            float k = age / life, s = 1f + 0.9f * k;
            t.localScale = new Vector3(s, s, 1f);
            var c = Color.Lerp(Color.white, BombHot, k); c.a = 1f - k * k; sr.color = c;
            yield return null;
        }
        if (t != null) Destroy(t.gameObject);
    }

    static IEnumerator Needle(Vector3 o, float cell, float ang, Color c)
    {
        var t = Make("hunter_needle", o, cell * 1.5f, FxOrder + 3, c, out var sr);
        float life = HunterCharge + 0.15f, age = 0f, start = ang + 720f + UnityEngine.Random.Range(0f, 90f);
        while (age < life && t != null)
        {
            age += Time.deltaTime;
            float k = Mathf.Clamp01(age / 0.32f);
            float a = Mathf.LerpUnclamped(start, ang, Kit.EaseOutBack(k));   // spins, overshoots, settles on the target
            t.rotation = Quaternion.Euler(0, 0, a);
            float s = Kit.EaseOutBack(Mathf.Min(1f, age * 6f));
            t.localScale = new Vector3(s, s, 1f);
            var col = c; col.a = age < life - 0.12f ? 1f : (life - age) / 0.12f; sr.color = col;
            yield return null;
        }
        if (t != null) Destroy(t.gameObject);
    }

    // ================= Rainbow cat blast ("радужное волшебство") =================
    // Layers (Sprites/FX/Burn/Rainbow), white, hue cycled here:
    //   swirl   0.00-...   rainbow vortex spins on the rainbow cat while orbs launch
    //   wave    0.10-0.70  soft glitter ring washes over the board
    //   orbs    staggered  one glowing orb per target cat, curved flight, rainbow ribbon trail
    //   target  on arrival cat-ears ring flashes on the target, glitter (heart/star/sparkle/moon) pops out
    // Board pops each target when its orb lands (RainbowArrive).
    public const float RainbowCharge = 0.22f, RainbowLaunchGap = 0.03f, RainbowFlyBase = 0.24f, RainbowFlyPerCell = 0.03f;
    public static float RainbowFly(float cells) => RainbowFlyBase + RainbowFlyPerCell * cells;
    public static float RainbowArrive(int order, float cells) => RainbowCharge + order * RainbowLaunchGap + RainbowFly(cells);
    public static Color Hue(float h, float sat = 0.55f) => Color.HSVToRGB(Mathf.Repeat(h, 1f), sat, 1f);

    // targets / arrive: world positions and arrival times (relative to the blast start).
    // orbs = false (rainbow + rainbow): no orbs, every cell just lights up in a wave.
    public static void RainbowBlast(Vector3 o, float cell, List<Vector3> targets, List<float> arrive, bool orbs, float delay)
    {
        Host.StartCoroutine(RainbowRoutine(o, cell, targets, arrive, orbs, delay));
    }

    static IEnumerator RainbowRoutine(Vector3 o, float cell, List<Vector3> targets, List<float> arrive, bool orbs, float delay)
    {
        if (delay > 0) yield return new WaitForSeconds(delay);
        float h0 = UnityEngine.Random.value, last = 0f;
        foreach (var a in arrive) last = Mathf.Max(last, a);
        if (GameArt.I.Has("rainbow_swirl"))
            Host.StartCoroutine(HueLayer("rainbow_swirl", o, cell, FxOrder + 7, h0, 0f, Mathf.Max(0.5f, last - 0.1f), -420f,
                k => (orbs ? 1.7f : 2.4f) * Kit.EaseOutBack(Mathf.Min(1f, k * 4f)) * (1f - 0.3f * Mathf.Max(0f, k - 0.7f) / 0.3f),
                k => k < 0.75f ? 1f : 1f - (k - 0.75f) / 0.25f, 0.9f));
        if (GameArt.I.Has("rainbow_wave"))
            Host.StartCoroutine(HueLayer("rainbow_wave", o, cell, FxOrder + 1, h0 + 0.3f, 0.1f, orbs ? 0.6f : 0.8f, 0f,
                k => Mathf.Lerp(0.8f, orbs ? 10f : 14f, Kit.EaseOutCubic(k)), k => 0.38f * (1f - k) * (1f - k), 1.5f));

        for (int i = 0; i < targets.Count; i++)
        {
            float hue = h0 + i * 0.137f;
            if (orbs && GameArt.I.Has("rainbow_orb"))
            {
                float fly = RainbowFly(Vector3.Distance(o, targets[i]) / cell);
                Host.StartCoroutine(Orb(o, targets[i], cell, hue, arrive[i] - fly, fly, i % 2 == 0 ? 1f : -1f));
            }
            Host.StartCoroutine(TargetHit(targets[i], cell, hue, arrive[i], orbs ? 5 : 2));
        }
    }

    // a sprite whose colour keeps cycling through the rainbow
    static IEnumerator HueLayer(string key, Vector3 pos, float cell, int order, float h0, float delay, float life, float spin,
                                Func<float, float> scale, Func<float, float> alpha, float hueSpeed)
    {
        if (delay > 0) yield return new WaitForSeconds(delay);
        var t = Make(key, pos, cell, order, Hue(h0), out var sr);
        t.localScale = Vector3.zero;
        float age = 0f;
        while (age < life && t != null)
        {
            age += Time.deltaTime;
            float k = Mathf.Clamp01(age / life);
            float s = scale(k);
            t.localScale = new Vector3(s, s, 1f);
            t.Rotate(0, 0, spin * Time.deltaTime);
            var c = Hue(h0 + age * hueSpeed, 0.7f); c.a = Mathf.Clamp01(alpha(k)); sr.color = c;
            yield return null;
        }
        if (t != null) Destroy(t.gameObject);
    }

    static readonly Dictionary<string, Material> trailMats = new Dictionary<string, Material>();
    static Material TrailMaterial(string key = "rainbow_trail")
    {
        if (trailMats.TryGetValue(key, out var m) && m != null) return m;
        var s = GameArt.I.S(key);
        if (s == null) return null;
        var probe = new GameObject("probe").AddComponent<SpriteRenderer>();
        m = new Material(probe.sharedMaterial) { mainTexture = s.texture };
        Destroy(probe.gameObject);
        trailMats[key] = m;
        return m;
    }

    static IEnumerator Orb(Vector3 from, Vector3 to, float cell, float hue, float delay, float fly, float side)
    {
        if (delay > 0) yield return new WaitForSeconds(delay);
        var t = Make("rainbow_orb", from, cell * 0.62f, FxOrder + 8, Hue(hue, 0.35f), out var sr);
        TrailRenderer tr = null;
        var mat = TrailMaterial();
        if (mat != null)
        {
            tr = t.gameObject.AddComponent<TrailRenderer>();
            tr.sharedMaterial = mat; tr.textureMode = LineTextureMode.Stretch;
            tr.time = 0.2f; tr.minVertexDistance = cell * 0.05f;
            tr.widthCurve = new AnimationCurve(new Keyframe(0f, cell * 0.42f), new Keyframe(1f, 0f));
            var g = new Gradient();
            g.SetKeys(new[] { new GradientColorKey(Hue(hue, 0.45f), 0f), new GradientColorKey(Hue(hue + 0.12f, 0.6f), 1f) },
                      new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
            tr.colorGradient = g;
            tr.sortingOrder = FxOrder + 7;
        }
        Vector3 d = to - from;
        Vector3 perp = new Vector3(-d.y, d.x, 0).normalized;
        Vector3 ctrl = (from + to) * 0.5f + perp * side * d.magnitude * UnityEngine.Random.Range(0.25f, 0.45f);
        float age = 0f, nextGlitter = 0.04f;
        while (age < fly && t != null)
        {
            age += Time.deltaTime;
            float k = Kit.EaseInOut(Mathf.Clamp01(age / fly));
            Vector3 a = Vector3.Lerp(from, ctrl, k), b = Vector3.Lerp(ctrl, to, k);
            t.position = Vector3.Lerp(a, b, k);
            float s = 1f + 0.15f * Mathf.Sin(age * 30f);
            t.localScale = new Vector3(s, s, 1f);
            sr.color = Hue(hue + age * 2f, 0.35f);
            if (age > nextGlitter) { nextGlitter += 0.05f; Glitter(t.position, cell, hue + age * 2f, 0.16f, cell * 0.6f); }
            yield return null;
        }
        if (t == null) yield break;
        t.position = to; sr.enabled = false;
        if (tr != null) { tr.emitting = false; yield return new WaitForSeconds(tr.time); }
        if (t != null) Destroy(t.gameObject);
    }

    static IEnumerator TargetHit(Vector3 pos, float cell, float hue, float at, int glitter)
    {
        if (at > 0) yield return new WaitForSeconds(at);
        if (GameArt.I.Has("rainbow_target"))
            Host.StartCoroutine(HueLayer("rainbow_target", pos, cell, FxOrder + 6, hue, 0f, 0.42f, 120f,
                k => Mathf.Lerp(0.6f, 1.35f, Kit.EaseOutCubic(k)), k => k < 0.4f ? 1f : 1f - (k - 0.4f) / 0.6f, 1.2f));
        for (int i = 0; i < glitter; i++) Glitter(pos, cell, hue + i * 0.2f, UnityEngine.Random.Range(0.22f, 0.32f), cell * UnityEngine.Random.Range(2.2f, 3.4f));
    }

    // one glitter piece (heart / star / sparkle / moon) thrown with gravity
    static void Glitter(Vector3 pos, float cell, float hue, float size, float speed)
    {
        string key = "rainbow_glitter_" + UnityEngine.Random.Range(1, 5);
        if (!GameArt.I.Has(key)) return;
        var t = Make(key, pos, cell * size, FxOrder + 6, Hue(hue, 0.5f), out var sr);
        float ang = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
        Vector2 v = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang) * 0.8f + 0.4f) * speed;
        Host.StartCoroutine(Ember(t, sr, v, false, UnityEngine.Random.Range(0.45f, 0.7f), 0f));
    }

    // ================= Bomb cat blast ("кошачий бабах") =================
    // Layers (Sprites/FX/Burn/Bomb), white, tinted warm orange / gold here:
    //   flash    0.00-0.18  hot star flash
    //   rays     0.02-0.36  thin light rays shoot out and spin a little
    //   ring     0.03-0.40  shockwave ring sweeps over the blast square
    //   fireball 0.04-0.52  fluffy orange cloud + smaller golden core
    //   smoke    0.15-1.00  4-6 warm grey puffs drift up and dissolve
    //   embers   0.03-0.80  sparks / stars / streaks fly out with gravity
    //   scorch   0.05-1.20  soot paw print left on the floor under the pieces
    //   + a short camera shake
    // radius = 1 for a normal bomb (3x3), 2 for bomb+bomb (5x5).
    public const float BombStepTime = 0.05f;
    public static float BombHitTime(int ring) => 0.04f + ring * BombStepTime;
    public static readonly Color BombGlow = new Color(1f, 0.58f, 0.2f), BombHot = new Color(1f, 0.86f, 0.45f);

    public static void BombBlast(Vector3 pos, float cell, int radius, float delay)
    {
        if (!GameArt.I.Has("bomb_fireball")) { Host.StartCoroutine(Delayed(delay, () => Burst(pos, BombGlow, cell * (1 + radius), 16))); return; }
        Host.StartCoroutine(Delayed(delay, () => BombNow(pos, cell, radius)));
    }

    static IEnumerator Delayed(float t, Action a) { if (t > 0f) yield return new WaitForSeconds(t); a(); }

    static void BombNow(Vector3 pos, float cell, int radius)
    {
        float R = radius, span = 2 * R + 1;                         // blast square side in cells
        float Rnd(float a, float b) => UnityEngine.Random.Range(a, b);
        Color hot = BombHot, glow = BombGlow, light = Color.Lerp(BombHot, Color.white, 0.5f);

        Layer("bomb_flash", pos, cell, FxOrder + 8, light, 0f, 0.18f, Rnd(0f, 45f), 0f,
            k => Mathf.Lerp(0.5f, 1.2f * span * 0.8f, Kit.EaseOutCubic(k)), k => 1f - k * k, Vector3.zero);
        Layer("bomb_rays", pos, cell, FxOrder + 7, hot, 0.02f, 0.34f, Rnd(0f, 360f), Rnd(-80f, 80f),
            k => Mathf.Lerp(0.8f, 1.25f * span, Kit.EaseOutCubic(k)), k => 0.9f * (1f - k), Vector3.zero);
        Layer("bomb_ring", pos, cell, FxOrder + 6, light, 0.03f, 0.37f, 0f, 0f,
            k => Mathf.Lerp(0.6f, 1.3f * span, Kit.EaseOutCubic(k)), k => 0.95f * (1f - k), Vector3.zero);
        var ball = glow; ball.a = 0.95f;
        Layer("bomb_fireball", pos, cell, FxOrder + 4, ball, 0.04f, 0.48f, Rnd(0f, 360f), Rnd(-60f, 60f),
            k => Mathf.Lerp(0.5f, 0.85f * span, Kit.EaseOutCubic(Mathf.Min(1f, k * 1.6f))) * (1f + 0.08f * k),
            k => k < 0.45f ? 1f : 1f - (k - 0.45f) / 0.55f, new Vector3(0, cell * 0.2f, 0));
        Layer("bomb_fireball", pos, cell, FxOrder + 5, hot, 0.05f, 0.36f, Rnd(0f, 360f), Rnd(-90f, 90f),
            k => Mathf.Lerp(0.3f, 0.5f * span, Kit.EaseOutCubic(Mathf.Min(1f, k * 1.8f))),
            k => k < 0.3f ? 1f : 1f - (k - 0.3f) / 0.7f, new Vector3(0, cell * 0.12f, 0));

        // smoke puffs around the blast, drifting up
        int puffs = 4 + radius * 2;
        for (int i = 0; i < puffs; i++)
        {
            string key = "bomb_smoke_" + UnityEngine.Random.Range(1, 5);
            Vector3 at = pos + (Vector3)(UnityEngine.Random.insideUnitCircle * cell * R * 0.9f);
            float size = Rnd(0.7f, 1.1f) * (0.8f + 0.2f * R);
            Color c = new Color(0.8f, 0.72f, 0.66f, 0.75f);
            Layer(key, at, cell, FxOrder + 3, c, Rnd(0.14f, 0.24f), Rnd(0.6f, 0.85f), Rnd(0f, 360f), Rnd(-40f, 40f),
                k => size * Mathf.Lerp(0.5f, 1f, Kit.EaseOutCubic(k)), k => k < 0.25f ? k / 0.25f : 1f - (k - 0.25f) / 0.75f,
                new Vector3(Rnd(-0.3f, 0.3f) * cell, cell * Rnd(0.7f, 1.2f), 0));
        }

        // embers
        int n = 10 + radius * 6;
        for (int i = 0; i < n; i++)
        {
            int kind = UnityEngine.Random.Range(1, 5);
            string key = "bomb_ember_" + kind;
            if (!GameArt.I.Has(key)) continue;
            float ang = Rnd(0f, Mathf.PI * 2f);
            Vector2 v = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang) * 0.85f + 0.35f) * cell * Rnd(3f, 5.5f) * (0.8f + 0.25f * R);
            float size = kind == 3 ? Rnd(0.45f, 0.6f) : Rnd(0.2f, 0.34f);
            Color c = UnityEngine.Random.value < 0.35f ? Color.white : Color.Lerp(glow, hot, UnityEngine.Random.value);
            var t = Make(key, pos, cell * size, FxOrder + 6, c, out var sr);
            Host.StartCoroutine(Ember(t, sr, v, kind == 3, Rnd(0.45f, 0.75f), Rnd(0.02f, 0.08f)));
        }

        // soot paw print on the floor (under the pieces, over the tiles)
        if (GameArt.I.Has("bomb_scorch"))
        {
            var soot = new Color(0.32f, 0.18f, 0.12f, 0.5f);
            float sz = 1.3f + 0.5f * (R - 1);
            Layer("bomb_scorch", pos, cell, 4, soot, 0.05f, 1.2f, Rnd(-20f, 20f), 0f,
                k => sz * (0.85f + 0.15f * Kit.EaseOutCubic(Mathf.Min(1f, k * 8f))), k => k < 0.05f ? k / 0.05f : (k < 0.55f ? 1f : 1f - (k - 0.55f) / 0.45f), Vector3.zero);
        }
        Shake(cell * 0.07f * R, 0.26f);
    }

    // streak embers point along their flight; the others spin
    static IEnumerator Ember(Transform t, SpriteRenderer sr, Vector2 v, bool streak, float life, float delay)
    {
        t.localScale = Vector3.zero;
        if (delay > 0) yield return new WaitForSeconds(delay);
        float age = 0f, spin = UnityEngine.Random.Range(-500f, 500f);
        Vector3 p = t.position; Color c0 = sr.color;
        while (age < life && t != null)
        {
            float dt = Time.deltaTime; age += dt;
            float k = age / life;
            v *= 1f - 2.8f * dt;
            v.y -= 6f * dt;
            p += (Vector3)(v * dt);
            t.position = p;
            // streak art: head at bottom-left, tail to the top-right (flight direction = 225 deg in the art)
            if (streak) t.rotation = Quaternion.Euler(0, 0, Mathf.Atan2(v.y, v.x) * Mathf.Rad2Deg - 225f);
            else t.Rotate(0, 0, spin * dt);
            float s = Mathf.Min(1f, k * 8f) * (1f - 0.5f * k);
            t.localScale = new Vector3(s, s, 1f);
            var c = c0; c.a = c0.a * (k < 0.55f ? 1f : 1f - (k - 0.55f) / 0.45f); sr.color = c;
            yield return null;
        }
        if (t != null) Destroy(t.gameObject);
    }

    // ---- camera shake (additive, always returns to the rest position) ----
    static Vector3 camRest; static float shakeAmp, shakeUntil, shakeDur; static bool shaking;
    public static void Shake(float amp, float dur)
    {
        var cam = Camera.main; if (cam == null) return;
        shakeAmp = Mathf.Max(amp, shaking ? shakeAmp : 0f); shakeDur = dur; shakeUntil = Time.time + dur;
        if (!shaking) { camRest = cam.transform.position; Host.StartCoroutine(ShakeRoutine(cam)); }
    }
    static IEnumerator ShakeRoutine(Camera cam)
    {
        shaking = true;
        while (Time.time < shakeUntil && cam != null)
        {
            float k = (shakeUntil - Time.time) / Mathf.Max(0.01f, shakeDur);
            Vector2 o = UnityEngine.Random.insideUnitCircle * shakeAmp * k;
            cam.transform.position = camRest + new Vector3(o.x, o.y, 0);
            yield return null;
        }
        if (cam != null) cam.transform.position = camRest;
        shaking = false; shakeAmp = 0f;
    }

    // ================= Line cat blast ("кошачий луч") =================
    // Layers (Sprites/FX/Burn/Line), all white and tinted here:
    //   charge 0.00-0.30  star flash on the line cat, spins and fades
    //   claws  0.03-0.55  three claw scratches across the origin cell
    //   beam   0.08-...   glow + white core stretch out behind the two heads
    //   heads  0.08-...   two arrow heads race to both board edges (LineStepTime per cell)
    //   sparks            dropped along the path behind each head
    // Board pops every cat at the moment a head passes it (LineHitTime).
    public const float LineStartDelay = 0.08f, LineStepTime = 0.045f;
    public static readonly Color LineGlow = new Color(0.30f, 0.88f, 1f);        // turquoise
    public static float LineHitTime(int cells) => LineStartDelay + cells * LineStepTime;

    public static void LineBlast(Vector3 o, bool horizontal, float cell, float distNeg, float distPos, float delay)
    {
        Host.StartCoroutine(LineRoutine(o, horizontal, cell, distNeg, distPos, delay));
    }

    static IEnumerator LineRoutine(Vector3 o, bool hor, float cell, float dNeg, float dPos, float delay)
    {
        if (delay > 0) yield return new WaitForSeconds(delay);
        float ang = hor ? 0f : 90f;
        Vector3 axis = hor ? Vector3.right : Vector3.up, side = hor ? Vector3.up : Vector3.right;
        Color glow = LineGlow, light = Color.Lerp(LineGlow, Color.white, 0.65f);

        string charge = GameArt.I.Has("line_charge") ? "line_charge" : "fx_flash";
        Layer(charge, o, cell, FxOrder + 7, light, 0f, 0.32f, 0f, 160f,
            k => k < 0.3f ? Mathf.Lerp(0.3f, 1.9f, Kit.EaseOutBack(k / 0.3f)) : 1.9f - 0.8f * (k - 0.3f) / 0.7f,
            k => k < 0.4f ? 1f : 1f - (k - 0.4f) / 0.6f, Vector3.zero);
        Layer("line_claws", o, cell, FxOrder + 2, light, 0.03f, 0.55f, ang + UnityEngine.Random.Range(-12f, 12f), 0f,
            k => 1.3f * (0.8f + 0.2f * Kit.EaseOutCubic(Mathf.Min(1f, k * 5f))),
            k => k < 0.12f ? k / 0.12f : (k < 0.5f ? 1f : 1f - (k - 0.5f) / 0.5f), Vector3.zero);

        yield return new WaitForSeconds(LineStartDelay);

        var beamSprite = GameArt.I.S("line_beam");
        // one solid beam (painted art: white core, turquoise body, red edge) with red flashes racing along it
        var bodySprite = GameArt.I.S("line_beam_x") ?? beamSprite;
        SpriteRenderer glowSr = null;
        const int Dashes = 4;
        var dash = new SpriteRenderer[Dashes * 2];
        Color lineRed = new Color(1f, 0.25f, 0.22f);
        if (beamSprite != null)
        {
            glowSr = BeamPart(bodySprite, FxOrder + 3, Color.white);
            for (int i = 0; i < dash.Length; i++) dash[i] = BeamPart(beamSprite, FxOrder + 4, lineRed);
        }
        Transform headP = null, headN = null; SpriteRenderer hpSr = null, hnSr = null;
        if (GameArt.I.Has("line_head"))
        {
            headP = Make("line_head", o, cell * 0.75f, FxOrder + 6, light, out hpSr); headP.rotation = Quaternion.Euler(0, 0, ang);
            headN = Make("line_head", o, cell * 0.75f, FxOrder + 6, light, out hnSr); headN.rotation = Quaternion.Euler(0, 0, ang + 180f);
        }

        float speed = cell / LineStepTime, travel = Mathf.Max(dNeg, dPos) / speed;
        const float hold = 0.08f, fade = 0.22f;
        float age = 0f, nextSparkP = cell * 0.4f, nextSparkN = cell * 0.4f;
        while (age < travel + hold + fade)
        {
            age += Time.deltaTime;
            float run = age * speed;
            float cp = Mathf.Min(dPos, run), cn = Mathf.Min(dNeg, run);
            float f = age <= travel + hold ? 0f : Mathf.Clamp01((age - travel - hold) / fade);
            if (glowSr != null)
            {
                float swell = 1f + 0.12f * Mathf.Sin(age * 40f);
                SetBeam(glowSr, bodySprite, o, axis, cn, cp, ang, cell * 0.46f * swell * (1f - 0.6f * f), 1f - f);
                for (int i = 0; i < Dashes; i++)
                {   // red flashes leave the cat one after another and run out to both ends
                    float d = run * 0.75f - i * cell * 1.25f, half = cell * 0.32f;
                    for (int sgn = 0; sgn < 2; sgn++)
                    {
                        float lim = sgn == 0 ? cp : cn;
                        var ds = dash[i * 2 + sgn];
                        bool show = d > half && d < lim - half;
                        float da = show ? 0.9f * (1f - f) * Mathf.Clamp01((lim - half - d) / (cell * 0.5f)) : 0f;
                        SetBeam(ds, beamSprite, o + axis * (sgn == 0 ? d : -d), axis, half, half, ang, cell * 0.16f, da);
                    }
                }
            }
            if (headP != null)
            {
                headP.position = o + axis * cp; headN.position = o - axis * cn;
                SetAlpha(hpSr, light, 1f - Mathf.Clamp01((run - dPos) / (speed * 0.08f)));
                SetAlpha(hnSr, light, 1f - Mathf.Clamp01((run - dNeg) / (speed * 0.08f)));
            }
            while (nextSparkP < cp) { LineSpark(o + axis * nextSparkP, axis, side, cell, glow); nextSparkP += cell * UnityEngine.Random.Range(0.35f, 0.6f); }
            while (nextSparkN < cn) { LineSpark(o - axis * nextSparkN, -axis, side, cell, glow); nextSparkN += cell * UnityEngine.Random.Range(0.35f, 0.6f); }
            yield return null;
        }
        if (glowSr != null) { Destroy(glowSr.gameObject); foreach (var ds in dash) Destroy(ds.gameObject); }
        if (headP != null) { Destroy(headP.gameObject); Destroy(headN.gameObject); }
    }

    // Two special cats merged: a big emblem of the pair pops at the spot for a moment.
    public static void Emblem(Vector3 pos, string key, float cell)
    {
        if (key == null || !GameArt.I.Has(key)) return;
        var t = Make(key, pos, cell * 2.6f, FxOrder + 9, Color.white, out var sr);
        t.localScale = Vector3.zero;
        Host.StartCoroutine(EmblemRoutine(t, sr));
    }

    static IEnumerator EmblemRoutine(Transform t, SpriteRenderer sr)
    {
        float life = 0.6f, age = 0f, rot = UnityEngine.Random.Range(-8f, 8f);
        Vector3 p0 = t.position; float rise = sr.bounds.size.y * 0.9f;   // pops, then floats up and away so the combo itself stays in view
        while (age < life)
        {
            age += Time.deltaTime;
            if (t == null) yield break;
            float k = age / life;
            float s = k < 0.25f ? Kit.EaseOutBack(k / 0.25f) : 1f - 0.35f * (k - 0.25f);
            t.localScale = new Vector3(s, s, 1f);
            t.position = p0 + Vector3.up * rise * Kit.EaseInCubic(Mathf.Clamp01((k - 0.2f) / 0.8f));
            t.rotation = Quaternion.Euler(0, 0, rot * (1f - k));
            var c = sr.color; c.a = k < 0.35f ? 1f : 1f - (k - 0.35f) / 0.65f; sr.color = c;
            yield return null;
        }
        if (t != null) Destroy(t.gameObject);
    }

    static SpriteRenderer BeamPart(Sprite s, int order, Color c)
    {
        var go = new GameObject("line_beam");
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = s; sr.sortingOrder = order; sr.color = c;
        go.transform.localScale = Vector3.zero;
        return sr;
    }

    static void SetBeam(SpriteRenderer sr, Sprite s, Vector3 o, Vector3 axis, float neg, float pos, float ang, float thick, float a)
    {
        var t = sr.transform;
        Vector2 size = s.bounds.size;
        t.position = o + axis * ((pos - neg) * 0.5f);
        t.rotation = Quaternion.Euler(0, 0, ang);
        t.localScale = new Vector3((neg + pos) / size.x, thick / size.y, 1f);
        var c = sr.color; c.a = a; sr.color = c;
    }

    static void SetAlpha(SpriteRenderer sr, Color c, float a) { c.a = Mathf.Clamp01(a); sr.color = c; }

    static void LineSpark(Vector3 p, Vector3 dir, Vector3 side, float cell, Color glow)
    {
        string key = "line_spark_" + UnityEngine.Random.Range(1, 5);
        if (!GameArt.I.Has(key)) key = "fx_sparkle";
        float sgn = UnityEngine.Random.value < 0.5f ? -1f : 1f;
        Vector3 at = p + side * sgn * cell * UnityEngine.Random.Range(0f, 0.3f);
        Vector3 drift = side * sgn * cell * UnityEngine.Random.Range(0.3f, 0.8f) - dir * cell * UnityEngine.Random.Range(0.1f, 0.4f);
        float size = UnityEngine.Random.Range(0.28f, 0.45f), phase = UnityEngine.Random.Range(0f, 6f);
        Color c = UnityEngine.Random.value < 0.3f ? new Color(1f, 0.9f, 0.55f) : Color.Lerp(glow, Color.white, UnityEngine.Random.Range(0.3f, 0.8f));
        Layer(key, at, cell, FxOrder + 5, c, 0f, UnityEngine.Random.Range(0.3f, 0.45f), UnityEngine.Random.Range(0f, 360f), UnityEngine.Random.Range(-300f, 300f),
            k => size * (0.7f + 0.3f * Mathf.Abs(Mathf.Sin(phase + k * 12f))) * (1f - 0.5f * k), k => k < 0.5f ? 1f : 1f - (k - 0.5f) / 0.5f, drift);
    }

    static Transform Make(string key, Vector3 pos, float size, int order, Color c, out SpriteRenderer sr)
    {
        var go = new GameObject(key);
        var child = new GameObject("img"); child.transform.SetParent(go.transform, false);
        sr = child.AddComponent<SpriteRenderer>();
        Kit.FitSprite(sr, key, size, 1f);
        sr.sortingOrder = order; sr.color = c;
        go.transform.position = pos;
        return go.transform;
    }

    // One animated sprite: scale(k) is in cells, alpha(k) 0..1, drift = total offset over life.
    static void Layer(string key, Vector3 pos, float cell, int order, Color c, float delay, float life, float rot0, float spin,
                      Func<float, float> scale, Func<float, float> alpha, Vector3 drift)
    {
        if (!GameArt.I.Has(key)) return;
        var t = Make(key, pos, cell, order, c, out var sr);
        t.rotation = Quaternion.Euler(0, 0, rot0);
        t.localScale = Vector3.zero;
        Host.StartCoroutine(LayerRoutine(t, sr, c, pos, delay, life, spin, scale, alpha, drift));
    }

    static IEnumerator LayerRoutine(Transform t, SpriteRenderer sr, Color c, Vector3 pos, float delay, float life, float spin,
                                    Func<float, float> scale, Func<float, float> alpha, Vector3 drift)
    {
        if (delay > 0) yield return new WaitForSeconds(delay);
        float age = 0f;
        while (age < life && t != null)
        {
            age += Time.deltaTime;
            float k = Mathf.Clamp01(age / life);
            float s = scale(k);
            t.localScale = new Vector3(s, s, 1f);
            t.position = pos + drift * Kit.EaseOutCubic(k);
            t.Rotate(0, 0, spin * Time.deltaTime);
            var col = c; col.a = c.a * Mathf.Clamp01(alpha(k)); sr.color = col;
            yield return null;
        }
        if (t != null) Destroy(t.gameObject);
    }

    static IEnumerator Tuft(Transform t, SpriteRenderer sr, Vector2 v, float spin, float life, float delay)
    {
        if (delay > 0) { t.localScale = Vector3.zero; yield return new WaitForSeconds(delay); }
        float age = 0f;
        Vector3 p = t.position;
        Color c0 = sr.color;
        while (age < life && t != null)
        {
            float dt = Time.deltaTime; age += dt;
            float k = age / life;
            v *= 1f - 3.5f * dt;               // air drag: fast burst, then drift
            v.y -= 7f * dt;                    // gravity
            p += (Vector3)(v * dt);
            t.position = p;
            t.Rotate(0, 0, spin * dt);
            float s = Mathf.Min(1f, k * 6f) * (1f - 0.3f * k);
            t.localScale = new Vector3(s, s, 1f);
            var c = c0; c.a = c0.a * (k < 0.5f ? 1f : 1f - (k - 0.5f) / 0.5f); sr.color = c;
            yield return null;
        }
        if (t != null) Destroy(t.gameObject);
    }

    // Milk: a splash or two jumps up from the cell and drops back
    public static void MilkSplash(Vector3 pos, float size, int count)
    {
        for (int i = 0; i < count; i++)
        {
            string key = "milk_splash_" + UnityEngine.Random.Range(1, 7);
            if (!GameArt.I.Has(key)) continue;
            var go = new GameObject("milkSplash");
            var sr = new GameObject("img").AddComponent<SpriteRenderer>(); sr.transform.SetParent(go.transform, false);
            sr.sortingOrder = FxOrder + 1;
            Kit.FitSprite(sr, key, size * UnityEngine.Random.Range(0.45f, 0.7f), 1f);
            go.transform.position = pos + new Vector3(UnityEngine.Random.Range(-0.25f, 0.25f), UnityEngine.Random.Range(-0.1f, 0.2f), 0) * size;
            Vector2 v = new Vector2(UnityEngine.Random.Range(-0.6f, 0.6f), UnityEngine.Random.Range(0.8f, 1.4f)) * size * 1.6f;
            Host.StartCoroutine(Throw(go.transform, sr, v, UnityEngine.Random.Range(-120f, 120f)));
        }
    }

    // Soap foam popping: shiny bubbles rise and wobble out of the cell, each ends with a little "pop"
    // splash; when the last layer goes, a puff of foam swells and fades on the spot.
    static readonly string[] Bubbles = { "bubble_1", "bubble_2", "bubble_3", "bubble_8", "bubble_3", "bubble_8" };
    public static void FoamPop(Vector3 pos, float size, bool last)
    {
        int n = last ? 6 : 3;
        for (int i = 0; i < n; i++)
        {
            string key = Bubbles[UnityEngine.Random.Range(0, Bubbles.Length)];
            if (!GameArt.I.Has(key)) continue;
            var go = new GameObject("bubble");
            var sr = new GameObject("img").AddComponent<SpriteRenderer>(); sr.transform.SetParent(go.transform, false);
            sr.sortingOrder = FxOrder + 1;
            Kit.FitSprite(sr, key, size * UnityEngine.Random.Range(0.22f, 0.42f), 1f);
            go.transform.position = pos + new Vector3(UnityEngine.Random.Range(-0.35f, 0.35f), UnityEngine.Random.Range(-0.3f, 0.2f), 0) * size;
            Host.StartCoroutine(BubbleRise(go.transform, sr, size, UnityEngine.Random.Range(0.55f, 0.95f), UnityEngine.Random.Range(0f, 0.12f)));
        }
        if (last && GameArt.I.Has("bubble_7"))
        {
            var go = new GameObject("foamPuff");
            var sr = new GameObject("img").AddComponent<SpriteRenderer>(); sr.transform.SetParent(go.transform, false);
            sr.sortingOrder = FxOrder;
            Kit.FitSprite(sr, "bubble_7", size * 0.95f, 1f);
            go.transform.position = pos;
            Host.StartCoroutine(Puff(go.transform, sr));
        }
    }

    static IEnumerator BubbleRise(Transform t, SpriteRenderer sr, float size, float life, float delay)
    {
        if (delay > 0f) { sr.enabled = false; yield return new WaitForSeconds(delay); if (t == null) yield break; sr.enabled = true; }
        Vector3 p0 = t.position; float rise = size * UnityEngine.Random.Range(0.6f, 1.2f), phase = UnityEngine.Random.value * 6f, age = 0f;
        while (age < life && t != null)
        {
            age += Time.deltaTime; float k = age / life;
            t.position = p0 + new Vector3(Mathf.Sin(age * 9f + phase) * size * 0.06f, rise * Kit.EaseOutCubic(k), 0);
            float s = Mathf.Min(1f, k * 5f) * (1f + 0.15f * k);
            t.localScale = new Vector3(s * (1f + 0.05f * Mathf.Sin(age * 14f)), s * (1f - 0.05f * Mathf.Sin(age * 14f)), 1f);
            yield return null;
        }
        if (t == null) yield break;
        // pop: switch to the splash picture, flash bigger and vanish
        if (GameArt.I.Has("bubble_6")) Kit.FitSprite(sr, "bubble_6", size * 0.45f, 1f);
        yield return Kit.Tween(0.12f, k => { if (t != null) { t.localScale = Vector3.one * (1f + 0.4f * k); var c = sr.color; c.a = 1f - k; sr.color = c; } });
        if (t != null) Destroy(t.gameObject);
    }

    static IEnumerator Puff(Transform t, SpriteRenderer sr)
    {
        yield return Kit.Tween(0.4f, k => { if (t != null) { t.localScale = Vector3.one * Mathf.Lerp(0.8f, 1.3f, Kit.EaseOutCubic(k)); var c = sr.color; c.a = 0.9f * (1f - k); sr.color = c; } });
        if (t != null) Destroy(t.gameObject);
    }

    // Pieces of a broken object thrown outward with a bit of gravity and spin.
    public static void Debris(Vector3 pos, string[] keys, float size, int count)
    {
        for (int i = 0; i < count; i++)
        {
            string key = keys[i % keys.Length];
            if (!GameArt.I.Has(key)) continue;
            var go = new GameObject("debris");
            var child = new GameObject("img"); child.transform.SetParent(go.transform, false);
            var sr = child.AddComponent<SpriteRenderer>(); sr.sortingOrder = FxOrder + 1;
            Kit.FitSprite(sr, key, size * UnityEngine.Random.Range(0.35f, 0.5f), 1f);
            go.transform.position = pos;
            Vector2 v = new Vector2(UnityEngine.Random.Range(-1f, 1f), UnityEngine.Random.Range(0.4f, 1.2f)) * size * 2.2f;
            Host.StartCoroutine(Throw(go.transform, sr, v, UnityEngine.Random.Range(-400f, 400f)));
        }
    }

    // Behind a running mouse: a puff of yellow smoke, and now and then a bit of cheese tumbling off.
    public static void MouseTrail(Vector3 pos, float cell, float dir)
    {
        var go = new GameObject("trail");
        var sr = go.AddComponent<SpriteRenderer>(); sr.sprite = Kit.SoftCircle(64); sr.sortingOrder = 29;
        go.transform.position = pos + new Vector3(0, UnityEngine.Random.Range(-0.06f, 0.06f) * cell, 0);
        Host.StartCoroutine(TrailPuff(go.transform, sr, cell, dir));
        if (UnityEngine.Random.value < 0.3f) Debris(pos + new Vector3(0, cell * 0.12f, 0), new[] { "cheese_1", "cheese_2", "cheese_3" }, cell * 0.55f, 1);
    }

    static IEnumerator TrailPuff(Transform t, SpriteRenderer sr, float cell, float dir)
    {
        float s0 = cell * UnityEngine.Random.Range(0.16f, 0.26f) / sr.sprite.bounds.size.x, life = UnityEngine.Random.Range(0.45f, 0.65f), age = 0f;
        Vector3 p0 = t.position, drift = new Vector3(-dir * cell * UnityEngine.Random.Range(0.1f, 0.3f), cell * UnityEngine.Random.Range(0.1f, 0.3f), 0);
        while (age < life)
        {
            age += Time.deltaTime; float k = age / life;
            if (t == null) yield break;
            t.position = p0 + drift * Kit.EaseOutCubic(k);
            t.localScale = Vector3.one * s0 * (1f + 1.6f * k);
            sr.color = new Color(1f, 0.86f, 0.3f, 0.6f * (1f - k));
            yield return null;
        }
        if (t != null) Destroy(t.gameObject);
    }

    // The catnip cloud lifts off: soft green puffs rush upward, swell and fade like steam.
    public static void Steam(Vector3 pos, float cell)
    {
        for (int i = 0; i < 11; i++)
        {
            var go = new GameObject("steam");
            var sr = go.AddComponent<SpriteRenderer>(); sr.sprite = Kit.SoftCircle(64); sr.sortingOrder = FxOrder;
            Vector3 p0 = pos + new Vector3(UnityEngine.Random.Range(-0.3f, 0.3f), UnityEngine.Random.Range(-0.3f, 0.2f), 0) * cell;
            go.transform.position = p0;
            Host.StartCoroutine(SteamPuff(go.transform, sr, p0, cell, UnityEngine.Random.Range(0f, 0.12f)));
        }
    }

    static IEnumerator SteamPuff(Transform t, SpriteRenderer sr, Vector3 p0, float cell, float delay)
    {
        float s0 = cell * UnityEngine.Random.Range(0.3f, 0.45f) / sr.sprite.bounds.size.x;
        t.localScale = Vector3.one * s0;
        var c = new Color(0.55f, 0.95f, 0.5f, 0f); sr.color = c;
        if (delay > 0f) yield return new WaitForSeconds(delay);
        float life = UnityEngine.Random.Range(0.55f, 0.8f), age = 0f, rise = cell * UnityEngine.Random.Range(1.3f, 2f), drift = cell * UnityEngine.Random.Range(-0.35f, 0.35f);
        while (age < life)
        {
            age += Time.deltaTime; float k = age / life;
            if (t == null) yield break;
            t.position = p0 + new Vector3(drift * k, rise * Kit.EaseOutCubic(k), 0);
            t.localScale = Vector3.one * s0 * (1f + 1.4f * k);
            c = Color.Lerp(new Color(0.55f, 0.95f, 0.5f), new Color(0.9f, 1f, 0.9f), k);
            c.a = 0.55f * (k < 0.15f ? k / 0.15f : 1f - (k - 0.15f) / 0.85f);
            sr.color = c;
            yield return null;
        }
        if (t != null) Destroy(t.gameObject);
    }

    // Catnip cleared off a cat: a puff of leaves bursts out, then they flutter down slowly, swaying.
    public static void LeafFall(Vector3 pos, string key, float size, int count) => LeafFall(pos, new[] { key }, size, count);

    public static void LeafFall(Vector3 pos, string[] keys, float size, int count)
    {
        for (int i = 0; i < count; i++)
        {
            string key = keys[i % keys.Length];
            if (!GameArt.I.Has(key)) continue;
            var go = new GameObject("leaf");
            var child = new GameObject("img"); child.transform.SetParent(go.transform, false);
            var sr = child.AddComponent<SpriteRenderer>(); sr.sortingOrder = FxOrder + 1;
            Kit.FitSprite(sr, key, size * UnityEngine.Random.Range(0.22f, 0.42f), 1f);
            float ang = (i + UnityEngine.Random.Range(-0.4f, 0.4f)) / count * Mathf.PI * 2f;
            go.transform.position = pos + new Vector3(Mathf.Cos(ang), Mathf.Sin(ang), 0) * size * 0.3f;
            go.transform.rotation = Quaternion.Euler(0, 0, UnityEngine.Random.Range(0f, 360f));
            Vector2 v = new Vector2(Mathf.Cos(ang) * 1.6f, Mathf.Sin(ang) * 1.2f + 1.3f) * size * UnityEngine.Random.Range(0.8f, 1.3f);
            Host.StartCoroutine(Flutter(go.transform, sr, v, size, UnityEngine.Random.Range(1.2f, 1.9f), UnityEngine.Random.Range(0f, 6.28f)));
        }
    }

    static IEnumerator Flutter(Transform t, SpriteRenderer sr, Vector2 v, float size, float life, float phase)
    {
        float age = 0f, fall = size * UnityEngine.Random.Range(1.3f, 1.9f), sway = size * UnityEngine.Random.Range(0.5f, 0.9f), freq = UnityEngine.Random.Range(4f, 6f);
        Vector3 p = t.position; Vector3 s0 = t.localScale;
        while (age < life)
        {
            float dt = Time.deltaTime; age += dt;
            v.x *= 1f / (1f + 3.5f * dt);                                   // the burst dies out fast...
            v.y = Mathf.MoveTowards(v.y, -fall, size * 7f * dt);              // ...then the leaf just drifts down
            float w = Mathf.Sin(age * freq + phase);
            p += new Vector3(v.x + w * sway, v.y, 0) * dt;
            t.position = p;
            t.rotation = Quaternion.Euler(0, 0, w * 40f + phase * 57f);
            t.localScale = new Vector3(s0.x * (0.35f + 0.65f * Mathf.Abs(Mathf.Cos(age * freq * 0.5f + phase))), s0.y, 1f);   // turning over in the air
            var c = sr.color; c.a = 1f - Mathf.Clamp01((age - life * 0.7f) / (life * 0.3f)); sr.color = c;
            yield return null;
        }
        Destroy(t.gameObject);
    }

    static IEnumerator Throw(Transform t, SpriteRenderer sr, Vector2 v, float spin)
    {
        float life = 0.8f, age = 0f;
        Vector3 p = t.position;
        while (age < life)
        {
            float dt = Time.deltaTime; age += dt;
            v.y -= 9f * dt;
            p += (Vector3)(v * dt);
            t.position = p;
            t.Rotate(0, 0, spin * dt);
            var c = sr.color; c.a = 1f - Mathf.Clamp01((age - life * 0.5f) / (life * 0.5f)); sr.color = c;
            yield return null;
        }
        Destroy(t.gameObject);
    }

    static IEnumerator Fly(Transform t, SpriteRenderer sr, Vector3 from, Vector3 to, float scale, float dur, float delay, bool keepAlpha)
    {
        if (delay > 0) yield return new WaitForSeconds(delay);
        float time = 0;
        while (time < dur)
        {
            time += Time.deltaTime;
            float k = Mathf.Clamp01(time / dur);
            t.position = Vector3.Lerp(from, to, Kit.EaseOutCubic(k));
            float s = scale * (1f - 0.6f * k);
            t.localScale = new Vector3(s, s, 1);
            if (!keepAlpha) { var c = sr.color; c.a = 1f - k; sr.color = c; }
            yield return null;
        }
        Destroy(t.gameObject);
    }

    // ================= boosters =================
    // Paw ("шлёп"): a big fluffy paw comes down with its shadow, slaps the cell, lifts away; pink paw print stays a moment.
    // Returns at the moment of impact (the board pops the cell then); the lift-off continues on its own.
    public static IEnumerator PawSlam(Vector3 pos, float cell)
    {
        if (!GameArt.I.Has("booster_paw")) yield break;
        var shadowGo = new GameObject("paw_shadow");
        var sh = shadowGo.AddComponent<SpriteRenderer>(); sh.sprite = Kit.SoftCircle(64); sh.sortingOrder = FxOrder;
        shadowGo.transform.position = pos + new Vector3(0, -cell * 0.1f, 0);
        var paw = Make("booster_paw", pos + new Vector3(cell * 0.4f, cell * 3.2f, 0), cell * 1.7f, FxOrder + 9, Color.white, out var psr);
        paw.rotation = Quaternion.Euler(0, 0, 12f);
        Vector3 from = paw.position, to = pos + new Vector3(0, cell * 0.15f, 0);
        float dur = 0.34f, t = 0f;
        while (t < dur)
        {
            t += Time.deltaTime; float k = Mathf.Clamp01(t / dur);
            float e = k < 0.35f ? Kit.EaseOutCubic(k / 0.35f) * 0.25f : 0.25f + 0.75f * Kit.EaseInCubic((k - 0.35f) / 0.65f);   // wind-up, then smack
            paw.position = Vector3.Lerp(from, to, e);
            paw.rotation = Quaternion.Euler(0, 0, Mathf.Lerp(12f, 0f, e));
            float s = Mathf.Lerp(1.25f, 1f, e);
            paw.localScale = new Vector3(s, s, 1f);
            var c = psr.color; c.a = Mathf.Min(1f, k * 5f); psr.color = c;
            float ss = cell * Mathf.Lerp(0.5f, 1.3f, e);
            shadowGo.transform.localScale = new Vector3(ss * 1.2f, ss * 0.55f, 1f);
            sh.color = new Color(0.1f, 0.05f, 0.2f, Mathf.Lerp(0.05f, 0.35f, e));
            yield return null;
        }
        // impact
        Color pink = new Color(1f, 0.75f, 0.82f);
        Layer("booster_slap", pos, cell, FxOrder + 8, Color.Lerp(pink, Color.white, 0.5f), 0f, 0.3f, UnityEngine.Random.Range(0f, 30f), 0f,
            k => Mathf.Lerp(0.8f, 2.1f, Kit.EaseOutCubic(k)), k => k < 0.35f ? 1f : 1f - (k - 0.35f) / 0.65f, Vector3.zero);
        Layer("fx_ring", pos, cell, FxOrder + 7, pink, 0f, 0.32f, 0f, 0f, k => Mathf.Lerp(0.6f, 2.2f, Kit.EaseOutCubic(k)), k => 0.9f * (1f - k), Vector3.zero);
        if (GameArt.I.Has("bomb_scorch"))
            Layer("bomb_scorch", pos, cell, 4, new Color(1f, 0.55f, 0.7f, 0.55f), 0f, 1.1f, UnityEngine.Random.Range(-15f, 15f), 0f,
                k => 1.15f, k => k < 0.5f ? 1f : 1f - (k - 0.5f) / 0.5f, Vector3.zero);
        Shake(cell * 0.09f, 0.22f);
        Host.StartCoroutine(PawLift(paw, psr, shadowGo, sh, cell));
    }

    static IEnumerator PawLift(Transform paw, SpriteRenderer psr, GameObject shadowGo, SpriteRenderer sh, float cell)
    {
        Vector3 p0 = paw.position;
        float t = 0f;
        while (t < 0.08f) { t += Time.deltaTime; float k = t / 0.08f; paw.localScale = new Vector3(1f + 0.08f * k, 1f - 0.12f * k, 1f); yield return null; }   // squash
        t = 0f;
        while (t < 0.3f && paw != null)
        {
            t += Time.deltaTime; float k = Mathf.Clamp01(t / 0.3f);
            paw.position = p0 + new Vector3(cell * 0.3f * k, cell * 2.2f * Kit.EaseInCubic(k), 0);
            paw.localScale = Vector3.one * Mathf.Lerp(1f, 1.2f, k);
            var c = psr.color; c.a = 1f - k; psr.color = c;
            sh.color = new Color(0.1f, 0.05f, 0.2f, 0.35f * (1f - k));
            yield return null;
        }
        Destroy(paw.gameObject); Destroy(shadowGo);
    }

    // Mega-bomb: the bomb drops onto the cell, bounces twice, the fuse fizzes, it swells and flashes - then the 5x5 blast.
    public static IEnumerator BombDrop(Vector3 pos, float cell)
    {
        if (!GameArt.I.Has("booster_bomb")) yield break;
        var bomb = Make("booster_bomb", pos + new Vector3(0, cell * 4f, 0), cell * 1.3f, FxOrder + 9, Color.white, out var sr);
        Vector3 top = bomb.position;
        float t = 0f;
        while (t < 0.28f) { t += Time.deltaTime; float k = Mathf.Clamp01(t / 0.28f); bomb.position = Vector3.Lerp(top, pos, Kit.EaseInCubic(k)); bomb.Rotate(0, 0, -300f * Time.deltaTime); yield return null; }
        bomb.rotation = Quaternion.identity;
        Shake(cell * 0.04f, 0.1f);
        foreach (float h in new[] { 0.55f, 0.2f })       // two little bounces with squash
        {
            float d = 0.08f + h * 0.2f; t = 0f;
            while (t < d) { t += Time.deltaTime; float k = Mathf.Clamp01(t / d); bomb.position = pos + new Vector3(0, cell * h * Mathf.Sin(k * Mathf.PI), 0); float sq = 1f - 0.12f * (1f - Mathf.Sin(k * Mathf.PI)); bomb.localScale = new Vector3(2f - sq, sq, 1f); yield return null; }
        }
        bomb.position = pos; bomb.localScale = Vector3.one;
        // fuse fizz + swell with a white flash pulse
        Vector3 fuse = pos + new Vector3(cell * 0.3f, cell * 0.55f, 0);
        t = 0f; float nextSpark = 0f;
        while (t < 0.42f)
        {
            t += Time.deltaTime; float k = t / 0.42f;
            float s = 1f + 0.25f * k + 0.06f * Mathf.Sin(t * 60f);
            bomb.localScale = new Vector3(s, s, 1f);
            bomb.rotation = Quaternion.Euler(0, 0, Mathf.Sin(t * 50f) * 6f * k);
            sr.color = Color.Lerp(Color.white, new Color(1f, 0.7f, 0.7f), Mathf.Abs(Mathf.Sin(t * 25f)) * k);
            if (t > nextSpark)
            {
                nextSpark += 0.035f;
                string key = GameArt.I.Has("line_spark_1") ? "line_spark_" + UnityEngine.Random.Range(1, 5) : "fx_sparkle";
                Vector3 dir = (Vector3)(UnityEngine.Random.insideUnitCircle.normalized * cell * UnityEngine.Random.Range(0.3f, 0.7f));
                float sz = UnityEngine.Random.Range(0.18f, 0.3f);
                Layer(key, fuse, cell, FxOrder + 10, Color.Lerp(BombHot, Color.white, UnityEngine.Random.value * 0.5f), 0f, 0.3f, UnityEngine.Random.Range(0f, 360f), 0f, kk => sz * (1f - kk), kk => 1f - kk, dir);
            }
            yield return null;
        }
        Destroy(bomb.gameObject);
    }

    // Colour blast: a magic wand swings over the cat, its star flashes and a magic circle opens under the cat;
    // then the board sends the rainbow orbs out from there.
    public static IEnumerator WandCast(Vector3 pos, float cell)
    {
        if (!GameArt.I.Has("booster_wand")) yield break;
        float size = cell * 1.8f;
        Vector3 centre = pos + new Vector3(-cell * 0.35f, cell * 0.45f, 0);
        var wand = Make("booster_wand", centre + new Vector3(-cell * 0.5f, cell * 0.8f, 0), size, FxOrder + 9, Color.white, out var sr);
        Vector3 tipLocal = new Vector3(size * 0.3f, size * 0.3f, 0);   // the star sits in the top-right of the art
        Host.StartCoroutine(HueLayer("booster_magic_circle", pos, cell, 9, UnityEngine.Random.value, 0.05f, 1.3f, 50f,
            k => 2.4f * Kit.EaseOutBack(Mathf.Min(1f, k * 3f)), k => k < 0.7f ? 0.9f : 0.9f * (1f - (k - 0.7f) / 0.3f), 0.8f));
        float t = 0f, d = 0.36f;
        Vector3 start = wand.position;
        while (t < d)
        {
            t += Time.deltaTime; float k = Mathf.Clamp01(t / d);
            float e = Kit.EaseOutBack(k);
            wand.position = Vector3.Lerp(start, centre, Kit.EaseOutCubic(k));
            wand.rotation = Quaternion.Euler(0, 0, Mathf.LerpUnclamped(40f, -15f, e));    // the swing
            var c = sr.color; c.a = Mathf.Min(1f, k * 4f); sr.color = c;
            yield return null;
        }
        Vector3 tip = wand.position + wand.rotation * tipLocal;
        Layer("booster_starburst", tip, cell, FxOrder + 10, new Color(1f, 0.95f, 0.7f), 0f, 0.4f, 0f, 90f,
            k => Mathf.Lerp(0.6f, 2.2f, Kit.EaseOutCubic(k)), k => 1f - k * k, Vector3.zero);
        for (int i = 0; i < 6; i++) Glitter(tip, cell, i * 0.17f, UnityEngine.Random.Range(0.2f, 0.3f), cell * UnityEngine.Random.Range(1.8f, 2.8f));
        Host.StartCoroutine(FadeOut(wand, sr, 0.35f, 0.15f));
    }

    static IEnumerator FadeOut(Transform t, SpriteRenderer sr, float dur, float delay)
    {
        if (delay > 0) yield return new WaitForSeconds(delay);
        float a = 0f; Vector3 p0 = t.position;
        while (a < dur && t != null)
        {
            a += Time.deltaTime; float k = a / dur;
            var c = sr.color; c.a = 1f - k; sr.color = c;
            t.position = p0 + new Vector3(0, Kit.EaseOutCubic(k) * 0.3f, 0);
            yield return null;
        }
        if (t != null) Destroy(t.gameObject);
    }

    // ================= +moves: little green crosses fly from the button onto the board =================
    // Each cross arcs up and over, spins, leaves a sparkle trail and lands with a green pop.
    public static readonly Color LifeGlow = new Color(0.55f, 0.95f, 0.3f);

    public static void LifeCross(Vector3 from, Vector3 to, float cell, float delay, Action onLand)
    {
        Host.StartCoroutine(LifeCrossRoutine(from, to, cell, delay, onLand));
    }

    static IEnumerator LifeCrossRoutine(Vector3 from, Vector3 to, float cell, float delay, Action onLand)
    {
        if (delay > 0) yield return new WaitForSeconds(delay);
        string key = GameArt.I.Has("fx_life") ? "fx_life" : "fx_sparkle";
        var t = Make(key, from, cell * 0.7f, FxOrder + 9, Color.white, out var sr);
        t.localScale = Vector3.zero;
        float side = UnityEngine.Random.Range(-1.5f, 1.5f) * cell;
        Vector3 ctrl = new Vector3((from.x + to.x) * 0.5f + side, Mathf.Max(from.y, to.y) + cell * UnityEngine.Random.Range(1.5f, 2.6f), 0);
        float dur = UnityEngine.Random.Range(0.55f, 0.7f), age = 0f, nextSpark = 0f, spin = UnityEngine.Random.Range(-360f, 360f);
        while (age < dur && t != null)
        {
            age += Time.deltaTime;
            float k = Kit.EaseInOut(Mathf.Clamp01(age / dur));
            Vector3 a = Vector3.Lerp(from, ctrl, k), b = Vector3.Lerp(ctrl, to, k);
            t.position = Vector3.Lerp(a, b, k);
            float pop = Kit.EaseOutBack(Mathf.Min(1f, age / 0.18f));
            float s = pop * (1f + 0.25f * Mathf.Sin(k * Mathf.PI));          // grows mid-flight, like it comes towards you
            t.localScale = new Vector3(s, s, 1f);
            t.rotation = Quaternion.Euler(0, 0, spin * k + Mathf.Sin(age * 12f) * 8f);
            if (age > nextSpark)
            {
                nextSpark += 0.045f;
                float sz = UnityEngine.Random.Range(0.14f, 0.22f), ph = UnityEngine.Random.Range(0f, 6f);
                Layer("fx_sparkle", t.position, cell, FxOrder + 8, Color.Lerp(LifeGlow, Color.white, 0.5f), 0f, 0.35f, UnityEngine.Random.Range(0f, 90f), 0f,
                    kk => sz * (1f - kk) * (0.7f + 0.3f * Mathf.Abs(Mathf.Sin(ph + kk * 10f))), kk => 1f - kk, new Vector3(0, -cell * 0.25f, 0));
            }
            yield return null;
        }
        if (t == null) yield break;
        // landing pop
        Color light = Color.Lerp(LifeGlow, Color.white, 0.5f);
        Layer("fx_flash", to, cell, FxOrder + 7, light, 0f, 0.16f, UnityEngine.Random.Range(0f, 45f), 0f, k => Mathf.Lerp(0.4f, 1.3f, Kit.EaseOutCubic(k)), k => 1f - k * k, Vector3.zero);
        Layer("fx_ring", to, cell, FxOrder + 6, light, 0f, 0.3f, 0f, 0f, k => Mathf.Lerp(0.4f, 1.5f, Kit.EaseOutCubic(k)), k => 0.9f * (1f - k), Vector3.zero);
        for (int i = 0; i < 4; i++)
        {
            Vector3 dir = (Vector3)(UnityEngine.Random.insideUnitCircle.normalized * cell * UnityEngine.Random.Range(0.5f, 0.9f));
            float sz = UnityEngine.Random.Range(0.16f, 0.26f);
            Layer("fx_sparkle", to, cell, FxOrder + 8, light, 0.02f, 0.4f, 0f, UnityEngine.Random.Range(-200f, 200f), k => sz * (1f - 0.5f * k), k => 1f - k, dir);
        }
        onLand?.Invoke();
        float t2 = 0f; Quaternion r0 = t.rotation;
        while (t2 < 0.22f && t != null)
        {
            t2 += Time.deltaTime; float k = t2 / 0.22f;
            float s = 1f + 0.6f * Kit.EaseOutCubic(k);
            t.localScale = new Vector3(s, s, 1f);
            var c = sr.color; c.a = 1f - k; sr.color = c;
            yield return null;
        }
        if (t != null) Destroy(t.gameObject);
    }

    // A sprite flies from a world position to a screen-space UI target, then calls onArrive.
    public static void FlyToUI(string key, Vector3 worldFrom, RectTransform target, float worldSize, Action onArrive)
    {
        Host.StartCoroutine(FlyToUIRoutine(key, worldFrom, target, worldSize, onArrive));
    }

    static IEnumerator FlyToUIRoutine(string key, Vector3 from, RectTransform target, float size, Action onArrive)
    {
        var cam = Camera.main;
        var go = new GameObject("fly");
        var child = new GameObject("img"); child.transform.SetParent(go.transform, false);
        var sr = child.AddComponent<SpriteRenderer>(); sr.sortingOrder = FxOrder + 5;
        Kit.FitSprite(sr, key, size, 1f);
        float dur = 0.65f, t = 0f;
        Vector3 mid = from + new Vector3(UnityEngine.Random.Range(-1f, 1f), 1.5f, 0f);
        while (t < dur)
        {
            t += Time.deltaTime;
            float k = Kit.EaseInOut(Mathf.Clamp01(t / dur));
            Vector3 to = from;
            if (target != null) { to = target.position; to.z = 0; }
            Vector3 a = Vector3.Lerp(from, mid, k), b = Vector3.Lerp(mid, to, k);
            go.transform.position = Vector3.Lerp(a, b, k);
            float s = Mathf.Lerp(1f, 0.55f, k);
            go.transform.localScale = new Vector3(s, s, 1);
            yield return null;
        }
        Destroy(go);
        onArrive?.Invoke();
    }
}
