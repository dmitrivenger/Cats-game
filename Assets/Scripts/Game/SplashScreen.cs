using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// The "Purrade" title poster. The whole poster is always visible (fit, never cropped);
// whatever screen space is left around it is filled with a blurred copy of the poster, so there are
// no bars. A big "Играть" button at the bottom starts the game (pressed / released art via sprite swap).
public class SplashScreen : MonoBehaviour
{
    const float PosterShift = 0f, PosterZoom = 1f;   // the Purrade poster is centred as drawn (the old one needed a nudge)
    bool leaving;
    RectTransform playBtn;
    float t;

    void Start()
    {
        var cam = Camera.main;
        if (cam != null) { cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.55f, 0.7f, 1f); }
        UIKit.EnsureEventSystem();
        var canvas = UIKit.CreateCanvas("Splash", 0);

        // blurred backdrop: covers the whole screen (cropping is fine here)
        var blurSprite = GameArt.I.S("splash_blur") ?? GameArt.I.S("splash");
        if (blurSprite != null) Poster(canvas.transform, blurSprite, AspectRatioFitter.AspectMode.EnvelopeParent);

        // the poster itself: fitted, fully visible
        // the title block in this poster sits ~2-3% right of centre: the fitted poster lives in a holder that is
        // nudged left by 2.5% and scaled up 5% so no blurred strip shows at the right edge
        var poster = GameArt.I.S("splash");
        if (poster != null)
        {
            var holder = UIKit.Rect("posterHolder", canvas.transform);
            holder.anchorMin = new Vector2(PosterShift, 0f); holder.anchorMax = new Vector2(1f + PosterShift, 1f);
            holder.offsetMin = holder.offsetMax = Vector2.zero;
            holder.localScale = new Vector3(PosterZoom, PosterZoom, 1f);
            // the poster is tall (9:20) with plenty of spare sky top and bottom, so it can cover the
            // whole screen on any phone - the trimmed edges are empty sky, never the title or the cats
            Poster(holder, poster, AspectRatioFitter.AspectMode.EnvelopeParent);
        }

        // play button
        if (GameArt.I.Has(Loc.Art("ui_play_big")))
        {
            var normal = GameArt.I.S(Loc.Art("ui_play_big"));
            float w = 620f, h = w * normal.rect.height / normal.rect.width;
            var img = UIKit.Image(canvas.transform, Loc.Art("ui_play_big"), new Vector2(0.5f, 0f), new Vector2(0, 110 + h * 0.5f), new Vector2(w, h));
            img.raycastTarget = true;
            var b = img.gameObject.AddComponent<Button>();
            b.targetGraphic = img;
            var pressed = GameArt.I.S(Loc.Art("ui_play_big_pressed"));
            if (pressed != null) { b.transition = Selectable.Transition.SpriteSwap; b.spriteState = new SpriteState { pressedSprite = pressed, highlightedSprite = normal, selectedSprite = normal }; }
            else { b.transition = Selectable.Transition.None; img.gameObject.AddComponent<Squish>(); }
            b.onClick.AddListener(Leave);
            playBtn = img.rectTransform;
        }
        else
        {
            var b = UIKit.TextButton(canvas.transform, Loc.T("Play"), new Vector2(0.5f, 0f), new Vector2(0, 220), new Vector2(520, 150), new Color(0.3f, 0.8f, 0.4f), Leave);
            playBtn = (RectTransform)b.transform;
        }
        playBtn.localScale = Vector3.zero;
        AudioManager.StopMusic();   // the splash is silent; music starts in the game
    }

    static void Poster(Transform parent, Sprite s, AspectRatioFitter.AspectMode mode)
    {
        var rt = UIKit.Rect("poster", parent);
        rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = s; img.raycastTarget = false;
        var fit = rt.gameObject.AddComponent<AspectRatioFitter>();
        fit.aspectMode = mode;
        fit.aspectRatio = s.rect.width / s.rect.height;
    }

    // the button pops in after a moment, then gently "breathes" to invite a tap
    void Update()
    {
        if (playBtn == null) return;
        t += Time.deltaTime;
        float k = Mathf.Clamp01((t - 0.5f) / 0.45f);
        float s = k < 1f ? Kit.EaseOutBack(k) : 1f + 0.04f * Mathf.Sin((t - 0.95f) * 3.2f);
        playBtn.localScale = Vector3.one * s;
    }

    void Leave()
    {
        if (leaving) return;
        leaving = true;
        AudioManager.Play("click");
        AudioManager.StopMusic();
        SceneManager.LoadScene("Game");
    }
}
