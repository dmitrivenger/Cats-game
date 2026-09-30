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

    public string BodyKey()
    {
        switch (kind)
        {
            case PieceKind.Fish: return "fish_bowl";
            case PieceKind.Vase: return "vase";
            case PieceKind.Milk: return "milk_bucket";
        }
        switch (special)
        {
            case Special.Line: return "special_line";
            case Special.Bomb: return "special_bomb";
            case Special.Rainbow: return "special_rainbow";
            case Special.Hunter: return "special_hunter";
        }
        if (cover == Cover.Mint && GameArt.I.Has("mint_face_" + color)) return "mint_face_" + color;
        if (angry && GameArt.I.Has("cat_" + color + "_an")) return "cat_" + color + "_an";
        return "cat_" + color;
    }

    public void Refresh()
    {
        bool inBox = kind == PieceKind.Cat && cover == Cover.Box;
        if (inBox) Kit.FitSprite(body, BodyKey(), cell, InBoxFill, new Vector2(0, cell * InBoxLift));   // cat sits inside, head and shoulders out
        else Kit.FitSprite(body, BodyKey(), cell, kind == PieceKind.Vase || kind == PieceKind.Milk ? 0.9f : 0.92f);
        if (body.sprite == null) Placeholder();
        body.color = Color.white;

        over.sprite = null; extra.sprite = null; back.sprite = null;
        extra.transform.parent.localRotation = Quaternion.identity;
        if (kind == PieceKind.Cat)
        {
            switch (cover)
            {
                case Cover.Box:
                    string box = coverHp >= 2 ? "box_full" : "box_damaged";
                    if (GameArt.I.Has(box + "_front") && GameArt.I.Has(box + "_back"))
                    {
                        // back wall + inside behind the cat, front wall in front: the cat sits IN the box
                        Kit.FitSpriteAs(back, box + "_back", box, cell, 0.98f, new Vector2(0, -cell * 0.2f));
                        Kit.FitSpriteAs(over, box + "_front", box, cell, 0.98f, new Vector2(0, -cell * 0.2f));
                    }
                    else Kit.FitSprite(over, box, cell, 0.98f, new Vector2(0, -cell * 0.2f));
                    break;
                case Cover.Yarn:
                    Kit.FitSprite(over, "yarn_wrap", cell, 1.02f);
                    break;
                case Cover.Mint:
                    Kit.FitSprite(over, "mint_wreath", cell, 1.1f);
                    Kit.FitSprite(extra, "dizzy_stars", cell * 0.8f, 1f, new Vector2(0, cell * 0.36f));
                    break;
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

    const float InBoxFill = 0.74f, InBoxLift = 0.07f;

    public void SetAngry(bool on)
    {
        if (angry == on || kind != PieceKind.Cat || special != Special.None || cover == Cover.Mint) return;
        angry = on;
        if (cover == Cover.Box) Kit.FitSprite(body, BodyKey(), cell, InBoxFill, new Vector2(0, cell * InBoxLift));
        else Kit.FitSprite(body, BodyKey(), cell, 0.92f);
    }

    public void SetOrder(int baseOrder)
    {
        back.sortingOrder = baseOrder - 2; body.sortingOrder = baseOrder; over.sortingOrder = baseOrder + 2; extra.sortingOrder = baseOrder + 3;
    }

    // gentle idle life: specials pulse, mint stars spin, everything breathes a tiny bit
    void Update()
    {
        if (dead) return;
        float t = Time.time + idlePhase;
        if (special != Special.None)
        {
            float s = 1f + 0.05f * Mathf.Sin(t * 5f);
            visual.localScale = new Vector3(s, s, 1);
        }
        if (cover == Cover.Mint && extra.sprite != null)
            extra.transform.parent.localRotation = Quaternion.Euler(0, 0, Mathf.Sin(t * 2f) * 12f);
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
