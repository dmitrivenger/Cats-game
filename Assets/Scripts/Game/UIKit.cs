using System;
using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

// Procedural uGUI factory: everything on screen is built from code with the art in GameArt, so
// the scenes stay empty and nothing has to be wired by hand. Reference resolution 1080x1920.
public static class UIKit
{
    public static readonly Color Navy = new Color(0.13f, 0.18f, 0.36f);
    public static readonly Color Gold = new Color(1f, 0.78f, 0.25f);
    public static readonly Color Sky = new Color(0.55f, 0.8f, 1f);

    public static Canvas CreateCanvas(string name, int order)
    {
        var go = new GameObject(name);
        var c = go.AddComponent<Canvas>();
        // Camera-space so the UI and the 2D world share one camera (layout never depends on the
        // Editor's Screen size, and fly-to-HUD effects can use world positions directly).
        // World-space canvas glued to the camera: its size comes only from the camera (aspect /
        // ortho size), never from Screen, so layout is identical on device and in the Editor.
        var cam = Camera.main;
        if (cam != null) { c.renderMode = RenderMode.WorldSpace; c.worldCamera = cam; }
        else c.renderMode = RenderMode.ScreenSpaceOverlay;
        c.sortingOrder = order;
        go.AddComponent<CameraScaler>();
        go.AddComponent<GraphicRaycaster>();
        return c;
    }

    public static void EnsureEventSystem()
    {
        if (UnityEngine.Object.FindFirstObjectByType<EventSystem>() != null) return;
        var go = new GameObject("EventSystem");
        go.AddComponent<EventSystem>();
        go.AddComponent<InputSystemUIInputModule>();
    }

    // Root that respects the phone's safe area (notch / home bar).
    public static RectTransform SafeRoot(Canvas canvas)
    {
        var rt = Rect("Safe", canvas.transform);
        Stretch(rt);
        var sa = rt.gameObject.AddComponent<SafeArea>();
        sa.Apply();
        return rt;
    }

    public static RectTransform Rect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }

    public static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
    }

    public static RectTransform Place(RectTransform rt, Vector2 anchor, Vector2 pos, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = anchor;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        return rt;
    }

    public static Image Image(Transform parent, string key, Vector2 anchor, Vector2 pos, Vector2 size, bool preserve = true)
    {
        var rt = Place(Rect(key ?? "img", parent), anchor, pos, size);
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = GameArt.I.S(key);
        img.preserveAspect = preserve;
        img.raycastTarget = false;
        if (img.sprite == null) img.color = new Color(1, 1, 1, 0);
        return img;
    }

    public static Image Panel(Transform parent, Vector2 anchor, Vector2 pos, Vector2 size, Color color, int radius = 40)
    {
        var rt = Place(Rect("panel", parent), anchor, pos, size);
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = Kit.RoundedRect(128, radius);
        img.type = UnityEngine.UI.Image.Type.Sliced;
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    public static Text Label(Transform parent, string text, int size, Color color, Vector2 anchor, Vector2 pos, Vector2 box, bool numbers = false, TextAnchor align = TextAnchor.MiddleCenter)
    {
        var rt = Place(Rect("text", parent), anchor, pos, box);
        var t = rt.gameObject.AddComponent<Text>();
        t.font = numbers ? GameArt.I.Number : GameArt.I.Display;
        t.fontSize = size;
        t.color = color;
        t.alignment = align;
        t.text = text;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.raycastTarget = false;
        var o = rt.gameObject.AddComponent<Outline>();
        o.effectColor = new Color(0.1f, 0.12f, 0.3f, 0.85f);
        o.effectDistance = new Vector2(2.5f, -2.5f);
        return t;
    }

    // Art button (pressed-state sprite swap when "<key>_p" exists) with a squishy press.
    public static Button ArtButton(Transform parent, string key, Vector2 anchor, Vector2 pos, float size, Action onClick, string fallbackText = null)
    {
        var rt = Place(Rect("btn_" + key, parent), anchor, pos, new Vector2(size, size));
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = GameArt.I.S(key);
        img.preserveAspect = true;
        var b = rt.gameObject.AddComponent<Button>();
        if (img.sprite == null)
        {
            img.sprite = Kit.RoundedRect(128, 60);
            img.type = UnityEngine.UI.Image.Type.Sliced;
            img.color = Sky;
            Label(rt, fallbackText ?? "?", (int)(size * 0.32f), Color.white, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(size, size));
        }
        var pressed = GameArt.I.S(key + "_p");
        if (pressed != null)
        {
            b.transition = Selectable.Transition.SpriteSwap;
            b.spriteState = new SpriteState { pressedSprite = pressed, highlightedSprite = img.sprite, selectedSprite = img.sprite };
        }
        else b.transition = Selectable.Transition.None;
        rt.gameObject.AddComponent<Squish>();
        b.onClick.AddListener(() => { AudioManager.PlayClick(); onClick?.Invoke(); });
        return b;
    }

    // "watch a video" button: violet, with a white play triangle drawn in code (the fonts have no ▶ glyph)
    public static readonly Color AdViolet = new Color(0.56f, 0.38f, 0.95f);
    public static Button AdButton(Transform parent, string text, Vector2 anchor, Vector2 pos, Vector2 size, Action onClick)
    {
        var b = TextButton(parent, text, anchor, pos, size, AdViolet, onClick);
        b.name = "btn_ad";
        var lbl = b.GetComponentInChildren<Text>();
        float tri = size.y * 0.42f;
        if (lbl != null)
        {
            var lr = lbl.rectTransform; lr.anchoredPosition += new Vector2(tri * 0.85f, 0); lr.sizeDelta = new Vector2(size.x - tri * 2.6f, size.y);
            // one line always: a long caption gets a smaller font instead of running into the triangle
            lbl.horizontalOverflow = HorizontalWrapMode.Overflow;
            int fs = lbl.fontSize;
            while (fs > 18 && lbl.preferredWidth > lr.sizeDelta.x) { fs -= 2; lbl.fontSize = fs; }
        }
        bool art = b.GetComponent<Image>().sprite == GameArt.I.S("ui_btn_purple");   // glossy art: its face sits a little higher
        var irt = Place(Rect("play", b.transform), new Vector2(0f, 0.5f), new Vector2(size.y * (art ? 0.62f : 0.55f), art ? size.y * 0.07f : 0f), new Vector2(tri, tri));
        var icon = irt.gameObject.AddComponent<Image>();
        icon.sprite = VideoIcon(); icon.raycastTarget = false; icon.color = Color.white;
        irt.sizeDelta = new Vector2(tri * 1.2f, tri * 1.2f);
        return b;
    }

    // "watch a video": a little white screen with a play triangle cut through it, on a stand
    static Sprite videoIcon;
    public static Sprite VideoIcon()
    {
        if (videoIcon != null) return videoIcon;
        const int N = 96;
        var t = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        Vector2 a = new Vector2(38, 37), bb = new Vector2(38, 75), c = new Vector2(68, 56);     // the triangle (cut out)
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                float scr = RoundBox(p, new Vector2(48, 56), new Vector2(42, 32), 12f);          // the screen
                float stand = RoundBox(p, new Vector2(48, 14), new Vector2(18, 5), 4f);           // its foot
                float neck = RoundBox(p, new Vector2(48, 22), new Vector2(5, 6), 1f);
                float inShape = Mathf.Clamp01(0.5f - Mathf.Min(scr, Mathf.Min(stand, neck)));
                float tri = Mathf.Clamp01(Mathf.Min(Edge(p, a, bb), Mathf.Min(Edge(p, bb, c), Edge(p, c, a))) + 0.5f);
                t.SetPixel(x, y, new Color(1, 1, 1, inShape * (1f - tri)));
            }
        t.Apply();
        videoIcon = Sprite.Create(t, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f);
        return videoIcon;
    }

    // signed distance to a rounded box (negative inside)
    static float RoundBox(Vector2 p, Vector2 centre, Vector2 half, float r)
    {
        Vector2 q = new Vector2(Mathf.Abs(p.x - centre.x), Mathf.Abs(p.y - centre.y)) - half + new Vector2(r, r);
        return new Vector2(Mathf.Max(q.x, 0), Mathf.Max(q.y, 0)).magnitude + Mathf.Min(Mathf.Max(q.x, q.y), 0) - r;
    }

    static Sprite playTri;
    public static Sprite PlayTriangle()
    {
        if (playTri != null) return playTri;
        const int N = 64;
        var t = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        // a rounded-ish triangle pointing right, anti-aliased by distance to the three edges
        Vector2 a = new Vector2(14, 8), bb = new Vector2(14, 56), c = new Vector2(58, 32);
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                float d = Mathf.Min(Edge(p, a, bb), Mathf.Min(Edge(p, bb, c), Edge(p, c, a)));
                float al = Mathf.Clamp01(d + 0.5f);
                t.SetPixel(x, y, new Color(1, 1, 1, al));
            }
        t.Apply();
        playTri = Sprite.Create(t, new Rect(0, 0, N, N), new Vector2(0.5f, 0.5f), 100f);
        return playTri;
    }

    // signed distance to the edge a->b, positive inside a clockwise triangle
    static float Edge(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 e = b - a, n = new Vector2(e.y, -e.x).normalized;
        return Vector2.Dot(p - a, n);
    }

    // A green button gets the same glossy art as the big "Play" button on the splash screen (ui_btn_green:
    // the Play art without its word and paw, a 9-slice so it stretches sideways), with a pressed look.
    // Every other colour gets the same art recoloured: purple (videos), blue, grey (disabled / "no thanks").
    static string ButtonArt(Color c, out Color outline)
    {
        Color.RGBToHSV(c, out float h, out float sat, out float v);
        if (sat < 0.25f) { outline = new Color(0.16f, 0.17f, 0.24f, 0.95f); return "ui_btn_grey"; }
        if (h > 0.2f && h < 0.45f) { outline = new Color(0.04f, 0.27f, 0.07f, 0.95f); return "ui_btn_green"; }
        if (h >= 0.68f && h < 0.92f) { outline = new Color(0.22f, 0.07f, 0.4f, 0.95f); return "ui_btn_purple"; }
        outline = new Color(0.06f, 0.14f, 0.38f, 0.95f); return "ui_btn_blue";
    }
    static Button ArtButton(Transform parent, string text, Vector2 anchor, Vector2 pos, Vector2 size, string art, Color outline, Action onClick)
    {
        var rt = Place(Rect("btn_" + text, parent), anchor, pos, size);
        var normal = GameArt.I.S(art);
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = normal;
        img.type = UnityEngine.UI.Image.Type.Sliced;
        img.pixelsPerUnitMultiplier = normal.rect.height / size.y;   // the round ends scale with the button's height
        var b = rt.gameObject.AddComponent<Button>();
        b.targetGraphic = img;
        var pressed = GameArt.I.S(art + "_pressed");
        if (pressed != null) { b.transition = Selectable.Transition.SpriteSwap; b.spriteState = new SpriteState { pressedSprite = pressed, highlightedSprite = normal, selectedSprite = normal }; }
        else b.transition = Selectable.Transition.ColorTint;
        // the face sits a little above the middle of the picture (the button's thick bottom edge is below it)
        var lbl = Label(rt, text, (int)(size.y * 0.44f), Color.white, new Vector2(0.5f, 0.5f), new Vector2(0, size.y * 0.07f), size, true);
        var o = lbl.GetComponent<Outline>(); o.effectColor = outline; o.effectDistance = new Vector2(3f, -3f);
        lbl.horizontalOverflow = HorizontalWrapMode.Overflow;
        int fs = lbl.fontSize;
        while (fs > 18 && lbl.preferredWidth > size.x - size.y * 0.7f) { fs -= 2; lbl.fontSize = fs; }
        rt.gameObject.AddComponent<Squish>();
        b.onClick.AddListener(() => { AudioManager.PlayClick(); onClick?.Invoke(); });
        return b;
    }

    public static Button TextButton(Transform parent, string text, Vector2 anchor, Vector2 pos, Vector2 size, Color color, Action onClick)
    {
        string art = ButtonArt(color, out var outline);
        if (GameArt.I.Has(art)) return ArtButton(parent, text, anchor, pos, size, art, outline, onClick);
        var rt = Place(Rect("btn_" + text, parent), anchor, pos, size);
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = Kit.RoundedRect(128, 50);
        img.type = UnityEngine.UI.Image.Type.Sliced;
        img.color = Navy;                                   // the outline; the coloured face sits inside it
        var b = rt.gameObject.AddComponent<Button>();
        b.transition = Selectable.Transition.ColorTint;
        var face = Panel(rt, new Vector2(0.5f, 0.5f), Vector2.zero, size - new Vector2(12, 12), color, 45);
        face.raycastTarget = false;
        var lbl = Label(rt, text, (int)(size.y * 0.42f), Color.white, new Vector2(0.5f, 0.5f), Vector2.zero, size, true);
        // a caption that doesn't fit stays on one line with a smaller font instead of running past the button
        lbl.horizontalOverflow = HorizontalWrapMode.Overflow;
        int fs = lbl.fontSize;
        while (fs > 18 && lbl.preferredWidth > size.x - 36f) { fs -= 2; lbl.fontSize = fs; }
        rt.gameObject.AddComponent<Squish>();
        b.onClick.AddListener(() => { AudioManager.PlayClick(); onClick?.Invoke(); });
        return b;
    }

    public static Slider MakeSlider(Transform parent, Vector2 anchor, Vector2 pos, float width, float value, Action<float> onChange)
    {
        var rt = Place(Rect("slider", parent), anchor, pos, new Vector2(width, 60));
        var bg = Panel(rt, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(width, 26), new Color(0.1f, 0.15f, 0.35f, 0.5f), 13);
        var fillArea = Rect("fillArea", rt); Stretch(fillArea); fillArea.offsetMin = new Vector2(0, 17); fillArea.offsetMax = new Vector2(0, -17);
        var fill = Rect("fill", fillArea); Stretch(fill);
        var fi = fill.gameObject.AddComponent<Image>(); fi.sprite = Kit.RoundedRect(128, 13); fi.type = UnityEngine.UI.Image.Type.Sliced; fi.color = Gold;
        var handleArea = Rect("handleArea", rt); Stretch(handleArea); handleArea.offsetMin = new Vector2(30, 0); handleArea.offsetMax = new Vector2(-30, 0);   // the coin stays within the track at both ends
        var handle = Rect("handle", handleArea); handle.sizeDelta = new Vector2(64, 64);
        var hi = handle.gameObject.AddComponent<Image>(); hi.sprite = GameArt.I.S("ui_balance") ?? Kit.SoftCircle(64); hi.preserveAspect = true;
        var s = rt.gameObject.AddComponent<Slider>();
        s.fillRect = fill; s.handleRect = handle; s.targetGraphic = hi;
        s.minValue = 0; s.maxValue = 1; s.value = value;
        s.onValueChanged.AddListener(v => onChange?.Invoke(v));
        return s;
    }

    public static IEnumerator PopIn(Transform t, float dur = 0.3f)
    {
        t.localScale = Vector3.zero;
        yield return Kit.Tween(dur, k => { if (t != null) t.localScale = Vector3.one * Kit.EaseOutBack(k); });
    }
}

// Scales a button down a touch while pressed.
public class Squish : MonoBehaviour, IPointerDownHandler, IPointerUpHandler
{
    public void OnPointerDown(PointerEventData e) { transform.localScale = new Vector3(Mathf.Sign(transform.localScale.x) * 0.92f, 0.92f, 1f); }   // keeps a mirrored button mirrored
    public void OnPointerUp(PointerEventData e) { transform.localScale = new Vector3(Mathf.Sign(transform.localScale.x), 1f, 1f); }
}

// Same maths as CanvasScaler "Scale With Screen Size" in Expand mode (1080x1920), computed from the
// camera's pixel size; sizes and places the world-space canvas so it exactly covers the view.
[ExecuteAlways]
public class CameraScaler : MonoBehaviour
{
    public Vector2 reference = new Vector2(1080, 1920);
    public float match = 0.35f;
    Canvas canvas;
    void OnEnable() { canvas = GetComponent<Canvas>(); Apply(); }
    void LateUpdate() { Apply(); }
    public void Apply()
    {
        if (canvas == null) canvas = GetComponent<Canvas>();
        var cam = canvas.worldCamera;
        if (cam == null || canvas.renderMode != RenderMode.WorldSpace) return;
        float w = cam.pixelWidth, h = cam.pixelHeight;
        if (w <= 0 || h <= 0) return;
        // "Expand": the canvas is never narrower than 1080 nor shorter than 1920 reference px,
        // so nothing gets clipped on tall/narrow phones (extra height just goes to the board).
        float f = Mathf.Min(w / reference.x, h / reference.y);
        var rt = (RectTransform)transform;
        Vector2 size = new Vector2(w / f, h / f);
        if (rt.sizeDelta != size) rt.sizeDelta = size;
        float worldH = cam.orthographicSize * 2f;
        float s = worldH / size.y;
        rt.localScale = new Vector3(s, s, 1f);
        rt.position = new Vector3(cam.transform.position.x, cam.transform.position.y, 0f);
        rt.rotation = Quaternion.identity;
    }
}

public class SafeArea : MonoBehaviour
{
    Rect last;
    public void Apply()
    {
        var rt = (RectTransform)transform;
        Rect sa = Screen.safeArea;
        last = sa;
        var cam = Camera.main;
        bool valid = Screen.width > 0 && Screen.height > 0 && sa.width > 0 && sa.height > 0
                     && sa.xMax <= Screen.width + 1 && sa.yMax <= Screen.height + 1
                     && sa.width >= Screen.width * 0.9f && sa.height >= Screen.height * 0.8f   // Editor Game view reports unscaled device insets
                     && (cam == null || (Mathf.Abs(cam.pixelWidth - Screen.width) < 2 && Mathf.Abs(cam.pixelHeight - Screen.height) < 2));
        if (!valid) { rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; }
        else
        {
            rt.anchorMin = new Vector2(sa.xMin / Screen.width, sa.yMin / Screen.height);
            rt.anchorMax = new Vector2(sa.xMax / Screen.width, sa.yMax / Screen.height);
        }
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }
    Vector2Int lastScreen;
    void Update()
    {
        var sc = new Vector2Int(Screen.width, Screen.height);
        if (Screen.safeArea != last || sc != lastScreen) { lastScreen = sc; Apply(); }
    }
}
