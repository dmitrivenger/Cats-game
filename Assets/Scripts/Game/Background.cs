using UnityEngine;

// Three code-tinted layers (all art is pure white, colour comes from here):
//   gradient (2 theme colours) -> cat-icon pattern (tiled, slowly drifting) -> bokeh glow.
// Changing the "world" colour = SetTheme(i), with a smooth cross-fade.
public class Background : MonoBehaviour
{
    public static readonly (Color bottom, Color top)[] Themes =
    {
        (new Color(0.96f, 0.62f, 0.45f), new Color(1.00f, 0.84f, 0.66f)), // peach
        (new Color(0.30f, 0.64f, 0.57f), new Color(0.60f, 0.87f, 0.78f)), // mint
        (new Color(0.44f, 0.40f, 0.78f), new Color(0.72f, 0.68f, 0.95f)), // lavender
        (new Color(0.30f, 0.56f, 0.88f), new Color(0.62f, 0.82f, 1.00f)), // sky
        (new Color(0.90f, 0.46f, 0.62f), new Color(1.00f, 0.74f, 0.83f)), // pink
        (new Color(0.95f, 0.66f, 0.24f), new Color(1.00f, 0.88f, 0.52f)), // sunny
    };

    SpriteRenderer gradient, pattern, bokeh;
    Texture2D gradTex;
    Color curBottom, curTop, fromBottom, fromTop, toBottom, toTop;
    float blend = 1f;
    Vector2 patternOffset;
    float patternTile;

    public void Build(Camera cam)
    {
        float h = cam.orthographicSize * 2f, w = h * cam.aspect;
        Vector3 c = new Vector3(cam.transform.position.x, cam.transform.position.y, 0);

        gradTex = Kit.Gradient(Themes[0].bottom, Themes[0].top);
        gradient = new GameObject("gradient").AddComponent<SpriteRenderer>();
        gradient.transform.SetParent(transform, false);
        gradient.sprite = Sprite.Create(gradTex, new Rect(0, 0, 1, 64), new Vector2(0.5f, 0.5f), 1f);
        gradient.transform.position = c;
        gradient.transform.localScale = new Vector3(w * 1.1f, h * 1.1f / 64f, 1);
        gradient.sortingOrder = -100;

        var ps = GameArt.I.S("bg_pattern");
        if (ps != null)
        {
            pattern = new GameObject("pattern").AddComponent<SpriteRenderer>();
            pattern.transform.SetParent(transform, false);
            pattern.sprite = ps;
            pattern.drawMode = SpriteDrawMode.Tiled;
            patternTile = w / 2.2f;                       // ~2.2 tiles across the screen
            float s = patternTile / ps.bounds.size.x;
            pattern.transform.localScale = new Vector3(s, s, 1);
            pattern.size = new Vector2((w + patternTile * 2f) / s, (h + patternTile * 2f) / s);
            pattern.transform.position = c;
            pattern.color = new Color(1, 1, 1, 0.16f);
            pattern.sortingOrder = -90;
        }

        var bs = GameArt.I.S("bg_bokeh");
        if (bs != null)
        {
            bokeh = new GameObject("bokeh").AddComponent<SpriteRenderer>();
            bokeh.transform.SetParent(transform, false);
            bokeh.sprite = bs;
            float s = w / bs.bounds.size.x;
            bokeh.transform.localScale = new Vector3(s, s, 1);
            bokeh.transform.position = new Vector3(c.x, c.y + h * 0.5f - bs.bounds.size.y * s * 0.5f, 0);
            bokeh.color = new Color(1, 1, 1, 0.8f);
            bokeh.sortingOrder = -80;
        }
        curBottom = toBottom = Themes[0].bottom; curTop = toTop = Themes[0].top;
    }

    public void SetTheme(int i, bool instant = false)
    {
        var t = Themes[((i % Themes.Length) + Themes.Length) % Themes.Length];
        fromBottom = curBottom; fromTop = curTop; toBottom = t.bottom; toTop = t.top;
        blend = instant ? 1f : 0f;
        if (instant) Apply(t.bottom, t.top);
        var cam = Camera.main; if (cam != null) cam.backgroundColor = t.bottom;
    }

    void Apply(Color b, Color t)
    {
        curBottom = b; curTop = t;
        Kit.Gradient(b, t, gradTex);
    }

    void Update()
    {
        if (blend < 1f)
        {
            blend = Mathf.Min(1f, blend + Time.deltaTime / 0.8f);
            Apply(Color.Lerp(fromBottom, toBottom, blend), Color.Lerp(fromTop, toTop, blend));
        }
        if (pattern != null)
        {
            patternOffset += new Vector2(0.12f, 0.08f) * Time.deltaTime;
            float ox = Mathf.Repeat(patternOffset.x, patternTile), oy = Mathf.Repeat(patternOffset.y, patternTile);
            var cam = Camera.main;
            if (cam != null) pattern.transform.position = new Vector3(cam.transform.position.x + ox, cam.transform.position.y + oy, 0);
        }
        if (bokeh != null)
        {
            var c = bokeh.color; c.a = 0.7f + 0.1f * Mathf.Sin(Time.time * 0.7f); bokeh.color = c;
        }
    }
}
