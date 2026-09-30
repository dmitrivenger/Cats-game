using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

// Builds and runs the whole game screen from code: camera, background, board, HUD, power-up bar,
// popups; owns score / moves / goals / economy (Marbly ScoreManager + BalanceManager +
// ComboManager + LevelManager rules). Drop it on an empty GameObject in the Game scene.
public class GameController : MonoBehaviour
{
    // ---- Marbly economy constants ----
    const int MaxComboMultiplier = 6;
    const int BalanceCascadeThreshold = 4;   // a single move reaching this cascade step pays out
    const int BalanceReward = 7;
    const int SpecialScorePerCat = 7;
    const int PowerUpScorePerCat = 15;
    const int ExtraMovesAmount = 5;
    // coins for winning a level: a flat part plus half the moves left over (so a clean win pays more)
    const int WinCoinsBase = 8, WinCoinsMaxMoveBonus = 8;
    int lastWinCoins;
    // Global coin knob: every payout is scaled by this; the fractions carry over, so over a session the
    // player gets exactly this share of the original amounts (0.8 = 20% fewer coins).
    const float CoinRate = 0.8f;
    float coinCarry;
    int Coins(int n) { coinCarry += n * CoinRate; int whole = Mathf.FloorToInt(coinCarry + 1e-4f); coinCarry -= whole; return whole; }
    static readonly int[] ComboTiers = { 6, 10, 15, 20 };   // cats cleared by one move -> banner 1..4

    Camera cam;
    Board board;
    Background background;
    Canvas canvas;
    RectTransform safe, hudRoot, barRoot, popupRoot, toastRoot;

    LevelDef def;
    int level, moves, score;
    bool balancePaidThisMove, ended;

    Text movesText, balanceText, hintText;
    int movesPending;   // +moves already granted but still flying in as crosses (not shown yet)
    readonly List<GoalView> goalViews = new List<GoalView>();
    readonly Dictionary<PowerUpType, PowerUpButton> puButtons = new Dictionary<PowerUpType, PowerUpButton>();
    RectTransform balanceIcon;

    class GoalView { public LevelGoal goal; public RectTransform root; public Text count; public Image check; public Image icon; }
    class PowerUpButton
    {
        public RectTransform root; public Text badge; public Text price; public GameObject priceGroup;
        // "armed" look: golden glow behind, the icon lifts and sways, a ring of sparkles circles it
        public Image glow; public RectTransform sparkRing; public Image[] sparks; public Vector2 basePos; public float arm;
    }
    static readonly Color ArmGold = new Color(1f, 0.83f, 0.38f);
    const int ArmSparks = 8; const float ArmSparkRadius = 118f;

    // ============================================================ boot
    void Awake()
    {
        Application.targetFrameRate = 60;
        cam = Camera.main;
        if (cam == null) { var go = new GameObject("Main Camera"); go.tag = "MainCamera"; cam = go.AddComponent<Camera>(); }
        cam.orthographic = true;
        cam.orthographicSize = 9.6f;
        cam.transform.position = new Vector3(0, 0, -10);
        cam.clearFlags = CameraClearFlags.SolidColor;

        UIKit.EnsureEventSystem();
        background = new GameObject("Background").AddComponent<Background>();
        background.Build(cam);

        canvas = UIKit.CreateCanvas("UI", 30);          // above board pieces, below flying FX
        safe = UIKit.SafeRoot(canvas);
        BuildHud();
        BuildPowerUpBar();
        var top = UIKit.CreateCanvas("Popups", 100);    // above everything
        toastRoot = UIKit.Rect("Toasts", top.transform); UIKit.Stretch(toastRoot);
        popupRoot = UIKit.Rect("Popups", top.transform); UIKit.Stretch(popupRoot);

        board = new GameObject("Board").AddComponent<Board>();
        board.CascadeStep += OnCascadeStep;
        board.SpecialCleared += n => AddScore(n * SpecialScorePerCat);
        board.PowerUpCleared += n => AddScore(n * PowerUpScorePerCat);
        board.Bonus += AddScore;
        board.CatCleared += OnCatCleared;
        board.GoalHit += OnGoalHit;
        board.MilkCoin += pos =>
        {   // a cat lapped up milk: double points for it ("x2" floats up) and one coin flies to the balance
            AddScore(MilkBonusScore);
            StartCoroutine(FloatX2(pos));
            coinsInFlight++; Progress.AddBalance(1);
            FX.FlyToUI("ui_balance", pos, balanceIcon, board.cell * 0.42f, CoinLanded);
        };
        board.MouseSteal = () =>
        {   // the mouse takes one coin from the balance (nothing to take -> it just hops)
            if (Progress.Balance <= 0 || !Progress.TrySpend(1)) return null;
            if (coinsInFlight > 0) { shownBalance = Mathf.Max(0, shownBalance - 1); if (balanceText != null) balanceText.text = shownBalance.ToString(); }
            else RefreshBalance();
            StartCoroutine(Shake(balanceIcon));
            Vector3 p = balanceIcon.position; p.z = 0; return p;
        };
        board.MouseCaught += (pos, coins) => StartCoroutine(CaughtBanner(Mathf.Max(MouseMinReward, coins * 2)));
        board.MoveCommitted += OnMoveCommitted;
        board.MoveResolved += OnMoveResolved;
        board.PowerUpUsed += t => RefreshPowerUps();
        board.TargetPriority = HunterPriority;
        board.Toast += key => { if (key == "shuffle") Toast(Loc.T("NoMoves")); };

        Progress.OnBalanceChanged += RefreshBalance;
        Progress.OnPowerUpChanged += _ => RefreshPowerUps();
    }

    void OnDestroy()
    {
        Progress.OnBalanceChanged -= RefreshBalance;
    }

    IEnumerator Start()
    {
        AudioManager.PlayMusic("Game");
        yield return null;                       // let the canvases do their first layout
        StartLevel(Progress.Level);
        ShowMap();                               // the game opens on the level map
    }

    // Board sits between the HUD and the power-up bar, whatever the phone's aspect ratio.
    Rect BoardWorldRect()
    {
        foreach (var sc in FindObjectsByType<CameraScaler>(FindObjectsSortMode.None)) sc.Apply();
        Canvas.ForceUpdateCanvases();
        foreach (var sa in FindObjectsByType<SafeArea>(FindObjectsSortMode.None)) sa.Apply();
        Canvas.ForceUpdateCanvases();
        float top = WorldYOf(hudRoot, 0f);        // bottom edge of the HUD
        float bottom = WorldYOf(barRoot, 1f);     // top edge of the power-up bar
        float h = cam.orthographicSize * 2f, w = h * cam.aspect;
        float margin = w * 0.03f;
        return new Rect(-w * 0.5f + margin, bottom + 0.1f, w - margin * 2f, (top - bottom) - 0.2f);
    }

    static float WorldYOf(RectTransform rt, float normalizedY)
    {
        var corners = new Vector3[4];
        rt.GetWorldCorners(corners);          // camera-space canvas: world units
        return Mathf.Lerp(corners[0].y, corners[1].y, normalizedY);
    }

    // ============================================================ level flow
    void StartLevel(int lvl)
    {
        StopAllCoroutines();
        coinsInFlight = 0; balancePulse = null; movesPending = 0;
        AudioManager.SetMusicDuck(1f);
        if (balanceIcon != null) balanceIcon.localScale = Vector3.one;
        foreach (Transform c in popupRoot) Destroy(c.gameObject);
        map = null;
        level = lvl; lossCounted = false;
        def = LevelGen.Build(level);
        moves = def.moves;
        score = 0;
        ended = false;
        background.SetTheme(def.theme, false);
        board.ArmPowerUp(null);
        board.InputLocked = true;
        usedRect = BoardWorldRect();
        board.Setup(def, usedRect);
        foreach (var g in def.goals)
            if (g.type == GoalType.Box || g.type == GoalType.Yarn || g.type == GoalType.Mint || g.type == GoalType.Vase || g.type == GoalType.Foam)
                g.target = Mathf.Max(1, board.Count(g.type));
        BuildGoals();
        RefreshTexts();
        RefreshPowerUps();
        ShowLevelIntro();
    }

    Rect usedRect;

    // The safe area / canvas can settle a frame or two after the level is built (and in the
    // Editor whenever the Game view is resized). Until the first move, rebuild to fit.
    void LateUpdate()
    {
        if (def == null || ended || board.Busy || moves < def.moves || score > 0) return;
        if (Time.frameCount % 10 != 0) return;
        var r = BoardWorldRect();
        if (Mathf.Abs(r.height - usedRect.height) > 0.05f || Mathf.Abs(r.y - usedRect.y) > 0.05f || Mathf.Abs(r.width - usedRect.width) > 0.05f)
        {
            usedRect = r;
            bool locked = board.InputLocked;
            board.Setup(def, r);
            board.InputLocked = locked;
            foreach (var g in def.goals)
                if (g.type == GoalType.Box || g.type == GoalType.Yarn || g.type == GoalType.Mint || g.type == GoalType.Vase || g.type == GoalType.Foam)
                { g.target = Mathf.Max(1, board.Count(g.type)); g.progress = 0; }
            BuildGoals();
        }
    }

    void OnMoveCommitted()
    {
        moves = Mathf.Max(0, moves - 1);
        balancePaidThisMove = false;
        RefreshTexts();
        if (moves <= 3 && moves > 0) StartCoroutine(PunchText(movesText));
    }

    void OnCascadeStep(int step, List<int> runLengths)
    {
        int mult = Mathf.Min(step, MaxComboMultiplier);
        int s = 0;
        foreach (var n in runLengths) s += BaseScoreForRun(n);
        AddScore(s * mult);
        if (step >= BalanceCascadeThreshold && !balancePaidThisMove)
        {
            balancePaidThisMove = true;
            int pay = Coins(BalanceReward);
            coinsInFlight += pay;                    // hold the counter until each coin lands
            Progress.AddBalance(pay);
            AudioManager.Play("balance");
            StartCoroutine(CoinShower(lastClearPos, pay));
        }
    }

    // Marbly GDD scale: 3=30, 4=60, 5=100, 6=150, 7=210, 8=280, 9+=360
    static int BaseScoreForRun(int n)
    {
        switch (n) { case 2: return 20; case 3: return 30; case 4: return 60; case 5: return 100; case 6: return 150; case 7: return 210; case 8: return 280; }
        return n >= 9 ? 360 : 0;
    }

    void AddScore(int amount)
    {
        if (amount <= 0) return;
        score += amount;
        foreach (var g in def.goals) if (g.type == GoalType.Score) g.progress = score;
        RefreshTexts();
        RefreshGoals();
    }

    void OnCatCleared(int color, Vector3 pos)
    {
        lastClearPos = pos;
        foreach (var gv in goalViews)
            if (gv.goal.type == GoalType.Color && gv.goal.color == color && !gv.goal.Done)
            {
                gv.goal.progress++;
                var view = gv;
                FX.FlyToUI("cat_" + color, pos, gv.icon.rectTransform, board.cell * 0.8f, () => { RefreshGoals(); StartCoroutine(Punch(view.root)); });
            }
    }

    void OnGoalHit(GoalType type, Vector3 pos)
    {
        foreach (var gv in goalViews)
        {
            if (gv.goal.type != type) continue;
            if (float.IsNaN(pos.x)) { gv.goal.target++; RefreshGoals(); StartCoroutine(Punch(gv.root)); continue; } // catnip spread
            gv.goal.progress++;
            var view = gv;
            string key = type == GoalType.Box ? "box_full" : type == GoalType.Yarn ? "yarn_ball" : type == GoalType.Mint ? "mint_leaf" : type == GoalType.Vase ? "vase" : type == GoalType.Foam ? "bubble_7" : "fish";
            FX.FlyToUI(key, pos, gv.icon.rectTransform, board.cell * 0.7f, () => { RefreshGoals(); StartCoroutine(Punch(view.root)); });
        }
    }

    void OnMoveResolved(int cleared)
    {
        int tier = 0;
        for (int i = 0; i < ComboTiers.Length; i++) if (cleared >= ComboTiers[i]) tier = i + 1;
        if (tier > 0) StartCoroutine(ComboBanner(tier));
        StartCoroutine(CheckEnd());
    }

    // Hunter cat: what does this level still need? Unfinished goal pieces first, then any blocker, then cats.
    int HunterPriority(Piece p)
    {
        int best = 0;
        if (def != null)
            foreach (var g in def.goals)
            {
                if (g.Done) continue;
                switch (g.type)
                {
                    case GoalType.Box: if (p.cover == Cover.Box) best = Mathf.Max(best, 10); break;
                    case GoalType.Yarn: if (p.cover == Cover.Yarn) best = Mathf.Max(best, 10); break;
                    case GoalType.Mint: if (p.cover == Cover.Mint) best = Mathf.Max(best, 10); break;
                    case GoalType.Vase: if (p.kind == PieceKind.Vase) best = Mathf.Max(best, 10); break;
                    case GoalType.Foam: if (board.FoamAt(p.x, p.y) > 0 && p.kind == PieceKind.Cat) best = Mathf.Max(best, 10); break;
                    case GoalType.Color: if (p.kind == PieceKind.Cat && p.special == Special.None && p.color == g.color) best = Mathf.Max(best, 6); break;
                }
            }
        if (best > 0) return best;
        if (p.kind == PieceKind.Vase || (p.kind == PieceKind.Cat && p.cover != Cover.None)) return 3;
        return p.kind == PieceKind.Cat && p.special == Special.None ? 1 : 0;
    }

    bool AllGoalsDone() { foreach (var g in def.goals) if (!g.Done) return false; return true; }

    IEnumerator CheckEnd()
    {
        if (ended) yield break;
        // Goals are counted at the moment of clearing, so the outcome is already known here:
        // lock the board right away so no extra move can sneak in while fly-to-HUD plays.
        bool finishing = AllGoalsDone() || moves <= 0;
        if (!finishing) yield break;
        board.InputLocked = true;
        yield return new WaitForSeconds(0.75f);     // let fly-to-HUD animations land
        while (board.Busy) yield return null;
        if (ended) yield break;
        if (AllGoalsDone()) { ended = true; StartCoroutine(Win()); }
        else if (moves <= 0) { ended = true; Lose(); }
        else board.InputLocked = false;
    }

    IEnumerator Win()
    {
        board.InputLocked = true;
        LevelGen.RegisterWin(level);
        if (board.MouseActive)
        {   // the mouse wasn't caught: it runs off with whatever it stole
            int lost = board.MouseCoins;
            Toast(lost > 0 ? "Мышка убежала с монетками!" : "Мышка убежала!");
            yield return board.MouseRunAway();
        }
        AudioManager.Play("win");
        StartCoroutine(DuckMusic(3.4f));
        var r = board.WorldRect;
        for (int i = 0; i < 6; i++)
        {
            FX.Burst(new Vector3(Random.Range(r.xMin, r.xMax), Random.Range(r.yMin, r.yMax), 0), Color.HSVToRGB(Random.value, 0.5f, 1f), board.cell * 2f, 14);
            yield return new WaitForSeconds(0.12f);
        }
        Progress.Level = Mathf.Max(Progress.Level, level + 1);
        lastWinCoins = Coins(WinCoinsBase + Mathf.Min(WinCoinsMaxMoveBonus, Mathf.Max(0, moves) / 2));
        Progress.AddBalance(lastWinCoins);
        RefreshBalance();
        ShowResult(true);
    }

    // one loss per attempt: counted as soon as the lose screen shows (closing the app there still counts),
    // or when the level is restarted from the pause menu
    bool lossCounted;
    void CountLoss() { if (lossCounted) return; lossCounted = true; LevelGen.RegisterLoss(level); }

    void Lose()
    {
        board.InputLocked = true;
        CountLoss();
        AudioManager.Play("lost");
        StartCoroutine(DuckMusic(4.2f));
        ShowResult(false);
    }

    // the music steps back while the win / lose jingle plays, then comes back
    IEnumerator DuckMusic(float hold)
    {
        yield return Kit.Tween(0.25f, k => AudioManager.SetMusicDuck(Mathf.Lerp(1f, 0.15f, k)));
        yield return new WaitForSeconds(hold);
        yield return Kit.Tween(1f, k => AudioManager.SetMusicDuck(Mathf.Lerp(0.15f, 1f, k)));
    }

    // ============================================================ HUD
    // HUD panel art (ui/panel.png, 2048x640): [moves slot] (goal 1) (goal 2) [balance slot].
    // Positions below are measured on the art, in its own pixels, and scaled to the panel width.
    // HUD panel art (ui/hud_panel3.png, 2048x492): moves slot, three goal medallions, balance slot
    const float PanelArtW = 2048f, PanelArtH = 492f;
    static readonly Vector2 ArtMovesSlot = new Vector2(364, 194), ArtBalanceSlot = new Vector2(1692, 194);
    static readonly Vector2[] ArtGoals = { new Vector2(731, 193), new Vector2(1028, 193), new Vector2(1323, 193) };
    const float ArtGoalInner = 194f;          // diameter of the beige inside of a medallion
    readonly RectTransform[] goalSlots = new RectTransform[3];
    Text levelLabel;
    RectTransform hudChest;

    void BuildHud()
    {
        hudRoot = UIKit.Place(UIKit.Rect("HUD", safe), new Vector2(0.5f, 1f), new Vector2(0, -250), new Vector2(1080, 500));
        float pw = 1068f, ph = pw * PanelArtH / PanelArtW, k = pw / PanelArtW;
        var panel = UIKit.Image(hudRoot, "ui_panel", new Vector2(0.5f, 1f), new Vector2(0, -ph * 0.5f - 6), new Vector2(pw, ph), false);
        if (panel.sprite == null) UIKit.Panel(hudRoot, new Vector2(0.5f, 1f), new Vector2(0, -ph * 0.5f - 6), new Vector2(pw, ph * 0.5f), UIKit.Sky);
        var pr = panel.rectTransform;
        Vector2 P(Vector2 art) => new Vector2((art.x - PanelArtW * 0.5f) * k, (PanelArtH * 0.5f - art.y) * k);

        // left slot: moves
        Vector2 m = P(ArtMovesSlot);
        UIKit.Image(pr, "ui_moves", new Vector2(0.5f, 0.5f), m + new Vector2(-58, 0), new Vector2(58, 58));
        movesText = UIKit.Label(pr, "25", 50, Color.white, new Vector2(0.5f, 0.5f), m + new Vector2(24, 0), new Vector2(120, 64), true);

        // right slot: balance (fish coins fly here)
        Vector2 b = P(ArtBalanceSlot);
        balanceIcon = UIKit.Image(pr, "ui_balance", new Vector2(0.5f, 0.5f), b + new Vector2(-60, 0), new Vector2(62, 62)).rectTransform;
        balanceText = UIKit.Label(pr, "0", 46, Color.white, new Vector2(0.5f, 0.5f), b + new Vector2(24, 0), new Vector2(130, 64), true);

        // three goal medallions
        float inner = ArtGoalInner * k;
        for (int i = 0; i < goalSlots.Length; i++)
            goalSlots[i] = UIKit.Place(UIKit.Rect("goal" + (i + 1), pr), new Vector2(0.5f, 0.5f), P(ArtGoals[i]), new Vector2(inner, inner));

        // under the panel: pause (left), level number (small, centre)
        float rowY = -ph - 62;
        UIKit.ArtButton(hudRoot, "ui_pause", new Vector2(0.5f, 1f), new Vector2(-452, rowY), 112, ShowPause, "II");
        levelLabel = UIKit.Label(hudRoot, "", 38, Color.white, new Vector2(0.5f, 1f), new Vector2(0, rowY + 10), new Vector2(400, 50));
        // a chest waiting to be opened shows on the right, opposite the pause button, and pulses; tap opens it
        hudChest = UIKit.Place(UIKit.Rect("hudChest", hudRoot), new Vector2(0.5f, 1f), new Vector2(452, rowY), new Vector2(130, 130));
        UIKit.ArtButton(hudChest, "map_chest", new Vector2(0.5f, 0.5f), Vector2.zero, 130, () => { int c = Progress.PendingChest; if (c > 0 && !ended && !board.Busy) ShowChest(c); }, "★");
        hudChest.gameObject.SetActive(false);
        hudRoot.sizeDelta = new Vector2(1080, -rowY + 60);
        hudRoot.anchoredPosition = new Vector2(0, -hudRoot.sizeDelta.y * 0.5f);
    }

    void BuildGoals()
    {
        foreach (var slot in goalSlots) foreach (Transform c in slot) Destroy(c.gameObject);
        goalViews.Clear();
        int n = Mathf.Min(def.goals.Count, goalSlots.Length);
        for (int i = 0; i < n; i++)
        {
            var g = def.goals[i];
            var gv = new GoalView { goal = g };
            // symmetric use of the three medallions: 1 goal -> middle, 2 -> left + right, 3 -> all
            var slot = goalSlots[n == 1 ? 1 : n == 2 ? i * 2 : i];
            float d = slot.sizeDelta.x;
            gv.root = UIKit.Place(UIKit.Rect("goal", slot), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(d, d));
            gv.icon = UIKit.Image(gv.root, GoalIcon(g), new Vector2(0.5f, 0.5f), new Vector2(0, d * 0.12f), new Vector2(d * 0.66f, d * 0.66f));
            if (gv.icon.sprite == null) UIKit.Label(gv.root, "★", 60, UIKit.Gold, new Vector2(0.5f, 0.5f), new Vector2(0, d * 0.13f), new Vector2(d, d), true);
            gv.count = UIKit.Label(gv.root, "", Mathf.RoundToInt(d * 0.34f), Color.white, new Vector2(0.5f, 0.5f), new Vector2(0, -d * 0.3f), new Vector2(d * 1.5f, d * 0.44f), true);
            gv.check = UIKit.Image(gv.root, "ui_check", new Vector2(0.5f, 0.5f), new Vector2(d * 0.3f, -d * 0.22f), new Vector2(d * 0.5f, d * 0.5f));
            gv.check.gameObject.SetActive(false);
            goalViews.Add(gv);
        }
        RefreshGoals();
    }

    static string GoalIcon(LevelGoal g)
    {
        switch (g.type)
        {
            case GoalType.Score: return GameArt.I.Has("ui_score") ? "ui_score" : "combo_1";
            case GoalType.Color: return "cat_" + g.color;
            case GoalType.Box: return "box_full";
            case GoalType.Yarn: return "yarn_wrap";
            case GoalType.Mint: return "mint_wreath";
            case GoalType.Vase: return "vase";
            case GoalType.Foam: return "goal_foam";
            default: return "fish_bowl";
        }
    }

    void RefreshGoals()
    {
        foreach (var gv in goalViews)
        {
            var g = gv.goal;
            bool done = g.Done;
            gv.count.text = done ? "" : (g.type == GoalType.Score ? $"{Mathf.Min(g.progress, g.target)}/{g.target}" : g.Remaining.ToString());
            gv.count.fontSize = g.type == GoalType.Score ? 24 : 36;
            if (done && !gv.check.gameObject.activeSelf) { gv.check.gameObject.SetActive(true); StartCoroutine(UIKit.PopIn(gv.check.transform)); }
        }
    }

    void RefreshTexts()
    {
        if (movesText != null) { movesText.text = (moves - movesPending).ToString(); movesText.color = moves <= 3 ? new Color(1f, 0.55f, 0.5f) : Color.white; }
        if (levelLabel != null) levelLabel.text = Loc.T("Level", level);
        RefreshBalance();
    }

    // ---- balance: coins fly from the explosion to the HUD, the counter ticks up one coin at a
    // time and the icon pulses on every tick ----
    Vector3 lastClearPos;
    int coinsInFlight, shownBalance;
    Coroutine balancePulse;

    void RefreshBalance()
    {
        if (coinsInFlight <= 0) { coinsInFlight = 0; shownBalance = Progress.Balance; }
        if (balanceText != null) balanceText.text = shownBalance.ToString();
    }

    IEnumerator CoinShower(Vector3 from, int count)
    {
        float coinSize = board.cell * 0.42f;
        for (int i = 0; i < count; i++)
        {
            Vector3 start = from + (Vector3)(Random.insideUnitCircle * board.cell * 0.45f);
            FX.FlyToUI("ui_balance", start, balanceIcon, coinSize, CoinLanded);
            yield return new WaitForSeconds(0.07f);
        }
    }

    void CoinLanded()
    {
        coinsInFlight = Mathf.Max(0, coinsInFlight - 1);
        shownBalance = coinsInFlight == 0 ? Progress.Balance : Mathf.Min(shownBalance + 1, Progress.Balance);
        if (balanceText != null) balanceText.text = shownBalance.ToString();
        AudioManager.Play("balance", 0.45f);      // a little coin "ding" for every coin that lands
        if (balancePulse != null) StopCoroutine(balancePulse);
        balancePulse = StartCoroutine(BalancePulse());
    }

    // one quick beat per counted coin: icon swells and settles, the number bumps with it
    IEnumerator BalancePulse()
    {
        Transform icon = balanceIcon, label = balanceText != null ? balanceText.transform : null;
        yield return Kit.Tween(0.14f, k =>
        {
            float s = 1f + 0.3f * Mathf.Sin(k * Mathf.PI);
            if (icon != null) icon.localScale = Vector3.one * s;
            if (label != null) label.localScale = Vector3.one * (1f + 0.15f * Mathf.Sin(k * Mathf.PI));
        });
        if (icon != null) icon.localScale = Vector3.one;
        if (label != null) label.localScale = Vector3.one;
        balancePulse = null;
    }

    // ============================================================ power-up bar
    void BuildPowerUpBar()
    {
        barRoot = UIKit.Place(UIKit.Rect("PowerUps", safe), new Vector2(0.5f, 0f), new Vector2(0, 135), new Vector2(1080, 250));
        var types = new[] { PowerUpType.Paw, PowerUpType.MegaBomb, PowerUpType.ColorBlast, PowerUpType.ExtraMoves };
        var keys = new[] { "pu_paw", "pu_bomb", "pu_color", "pu_moves" };
        for (int i = 0; i < types.Length; i++)
        {
            var t = types[i];
            var b = new PowerUpButton();
            var btn = UIKit.ArtButton(barRoot, keys[i], new Vector2(0.5f, 0.5f), new Vector2(-360 + i * 240, 10), 190, () => OnPowerUpTap(t), "+" + ExtraMovesAmount);
            b.root = (RectTransform)btn.transform;
            if (t == PowerUpType.ExtraMoves && GameArt.I.Has(keys[i]))   // the number lives in code, not in the art
                UIKit.Label(b.root, "+" + ExtraMovesAmount, 54, Color.white, new Vector2(0.5f, 0.5f), new Vector2(-8, -18), new Vector2(120, 70), true);
            b.basePos = b.root.anchoredPosition;
            b.glow = UIKit.Image(barRoot, "ui_booster_glow", new Vector2(0.5f, 0.5f), b.basePos + new Vector2(0, 14), new Vector2(330, 330), false);
            b.glow.color = new Color(ArmGold.r, ArmGold.g, ArmGold.b, 0f);
            b.glow.transform.SetSiblingIndex(b.root.GetSiblingIndex());          // behind the icon
            b.sparkRing = UIKit.Place(UIKit.Rect("sparks", barRoot), new Vector2(0.5f, 0.5f), b.basePos + new Vector2(0, 14), new Vector2(10, 10));
            b.sparks = new Image[ArmSparks];
            for (int s = 0; s < ArmSparks; s++)
            {
                float a = s * Mathf.PI * 2f / ArmSparks;
                b.sparks[s] = UIKit.Image(b.sparkRing, "ui_booster_sparkle", new Vector2(0.5f, 0.5f), new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * ArmSparkRadius, new Vector2(60, 60));
                b.sparks[s].color = new Color(ArmGold.r, ArmGold.g, ArmGold.b, 0f);
            }
            var badgeBg = UIKit.Panel(b.root, new Vector2(0.5f, 0.5f), new Vector2(70, -70), new Vector2(64, 64), new Color(0.2f, 0.75f, 0.35f), 32);
            b.badge = UIKit.Label(badgeBg.rectTransform, "1", 38, Color.white, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(64, 64), true);
            b.priceGroup = UIKit.Panel(b.root, new Vector2(0.5f, 0.5f), new Vector2(0, -95), new Vector2(140, 52), new Color(0.13f, 0.18f, 0.36f, 0.85f), 26).gameObject;
            UIKit.Image(b.priceGroup.transform, "ui_balance", new Vector2(0.5f, 0.5f), new Vector2(-40, 0), new Vector2(44, 44));
            b.price = UIKit.Label(b.priceGroup.transform, Progress.Price(t).ToString(), 34, Color.white, new Vector2(0.5f, 0.5f), new Vector2(18, 0), new Vector2(90, 50), true);
            badgeBg.name = "badge";
            puButtons[t] = b;
        }
        hintText = UIKit.Label(barRoot, "", 44, Color.white, new Vector2(0.5f, 1f), new Vector2(0, 40), new Vector2(900, 60));
    }

    void RefreshPowerUps()
    {
        foreach (var kv in puButtons)
        {
            int c = Progress.Charges(kv.Key);
            kv.Value.badge.transform.parent.gameObject.SetActive(c > 0);
            kv.Value.badge.text = c.ToString();
            kv.Value.priceGroup.SetActive(c == 0);
            bool armed = board != null && board.Armed == kv.Key;
        }
        if (hintText != null) hintText.text = board != null && board.Armed != null ? Loc.T("PickCat") : "";
    }

    void OnPowerUpTap(PowerUpType t)
    {
        if (ended || board.Busy || board.InputLocked) return;
        if (board.Armed == t) { board.ArmPowerUp(null); RefreshPowerUps(); return; }
        if (Progress.Charges(t) == 0)
        {
            if (!Progress.Buy(t)) { Toast(Loc.T("NotEnough")); StartCoroutine(Shake(puButtons[t].root)); return; }
            Toast(Loc.T("Bought"));
        }
        if (t == PowerUpType.ExtraMoves)
        {
            if (!Progress.Consume(t)) return;
            moves += ExtraMovesAmount;
            AudioManager.Play("balance");
            RefreshPowerUps();
            PlusMovesShower(puButtons[t].root);
            return;
        }
        board.ArmPowerUp(t);
        RefreshPowerUps();
        StartCoroutine(WatchArmed(t));
    }

    // One little green cross per extra move flies from the button and lands on the board; the counter ticks up on each landing.
    void PlusMovesShower(RectTransform from)
    {
        var cells = board.LiveCellPositions();
        Vector3 start = from.position; start.z = 0;
        movesPending += ExtraMovesAmount;
        RefreshTexts();
        for (int i = 0; i < ExtraMovesAmount; i++)
        {
            Vector3 to = cells.Count > 0 ? cells[i % cells.Count] : start;
            FX.LifeCross(start, to, board.Cell, i * 0.09f, () =>
            {
                movesPending = Mathf.Max(0, movesPending - 1);
                RefreshTexts();
                StartCoroutine(PunchText(movesText));
                AudioManager.Play("click", 0.35f);
            });
        }
    }

    // Consume the charge only when the board actually used it.
    IEnumerator WatchArmed(PowerUpType t)
    {
        while (board.Armed == t) yield return null;
        if (board.Busy) Progress.Consume(t);   // armed -> cleared by a use (board went busy)
        RefreshPowerUps();
    }

    // ============================================================ popups
    const float PopupMinHeight = 700f;   // top (ribbon) + bottom (tail) borders of the panel art at 880 wide
    const float PopupTitleY = 270f;      // title centre below the top edge, inside the blue area
    const float PopupBottomPad = 150f;   // keep bottom buttons above the golden rim

    RectTransform OpenPopup(string title, float height, out RectTransform body, bool closeOnDim = false)
    {
        board.InputLocked = true;
        var root = UIKit.Rect("popup", popupRoot); UIKit.Stretch(root);
        var dim = root.gameObject.AddComponent<Image>(); dim.color = new Color(0.05f, 0.05f, 0.15f, 0.6f);
        if (closeOnDim) { var b = root.gameObject.AddComponent<Button>(); b.transition = Selectable.Transition.None; b.onClick.AddListener(() => ClosePopup(root)); }

        var art = GameArt.I.S("ui_popup_panel");
        if (art != null) height = Mathf.Max(height, PopupMinHeight);
        var holder = UIKit.Place(UIKit.Rect("window", root), new Vector2(0.5f, 0.5f), new Vector2(0, 40), new Vector2(880, height));
        if (art != null)
        {
            // 9-sliced (borders set on import): ears/ribbon and tail keep their shape at any height
            var i = UIKit.Image(holder, "ui_popup_panel", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(880, height), false);
            i.type = Image.Type.Sliced;
            i.pixelsPerUnitMultiplier = art.rect.width / 880f;
            i.raycastTarget = true;
        }
        else
        {
            // placeholder panel in the button style: navy outline, gold rim, sky-blue body
            UIKit.Panel(holder, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(880, height), UIKit.Navy, 70).raycastTarget = true;
            UIKit.Panel(holder, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(860, height - 20), UIKit.Gold, 62);
            UIKit.Panel(holder, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(826, height - 54), UIKit.Sky, 50);
            UIKit.Panel(holder, new Vector2(0.5f, 0.5f), new Vector2(0, -30), new Vector2(760, height - 180), new Color(1, 1, 1, 0.35f), 40);
            UIKit.Panel(holder, new Vector2(0.5f, 1f), new Vector2(0, 0), new Vector2(620, 120), UIKit.Navy, 60);
            UIKit.Panel(holder, new Vector2(0.5f, 1f), new Vector2(0, 0), new Vector2(604, 104), UIKit.Gold, 52);
        }
        // with the panel art the ribbon already carries the game name, so the window title sits
        // just under it, inside the blue area
        if (!string.IsNullOrEmpty(title)) UIKit.Label(holder, title, 64, Color.white, new Vector2(0.5f, 1f), new Vector2(0, art != null ? -PopupTitleY : 0), new Vector2(600, 100));
        body = holder;
        StartCoroutine(UIKit.PopIn(holder));
        return root;
    }

    static bool Art => GameArt.I.Has("ui_popup_panel");
    static float PauseRowY => Art ? -40 : -20;
    static float BottomY(float y) => GameArt.I.Has("ui_popup_panel") ? y + PopupBottomPad - 20f : y;

    void ClosePopup(RectTransform root)
    {
        if (root != null) Destroy(root.gameObject);
        StartCoroutine(UnlockNextFrame());
    }

    IEnumerator UnlockNextFrame()
    {
        yield return null;
        if (popupRoot.childCount == 0 && !ended) board.InputLocked = false;
    }

    void ShowLevelIntro()
    {
        var meet = NextMeet();
        if (meet != null) { ShowMeet(meet, ShowLevelIntro); return; }
        var root = OpenPopup(Loc.T("Level", level), Art ? 1040 : 620, out var body, false);
        UIKit.Label(body, Loc.T("Goals"), 46, Color.white, new Vector2(0.5f, 0.5f), new Vector2(0, Art ? 160 : 150), new Vector2(700, 60));
        int n = def.goals.Count;
        for (int i = 0; i < n; i++)
        {
            var g = def.goals[i];
            float x = (i - (n - 1) * 0.5f) * 220;
            UIKit.Image(body, GoalIcon(g), new Vector2(0.5f, 0.5f), new Vector2(x, Art ? 40 : 30), new Vector2(150, 150));
            UIKit.Label(body, g.target.ToString(), 50, Color.white, new Vector2(0.5f, 0.5f), new Vector2(x, Art ? -70 : -80), new Vector2(200, 60), true);
        }
        UIKit.ArtButton(body, "ui_play", new Vector2(0.5f, 0f), new Vector2(0, BottomY(40) + (Art ? 110 : 0)), 170, () => ClosePopup(root), "▶");
    }

    void ShowPause()
    {
        if (ended || board.Busy) return;
        var root = OpenPopup(Loc.T("Pause"), Art ? 860 : 760, out var body, false);
        UIKit.Label(body, Loc.T("Level", level), 50, Color.white, new Vector2(0.5f, 0.5f), new Vector2(0, Art ? 80 : 170), new Vector2(700, 70));
        UIKit.ArtButton(body, "ui_play", new Vector2(0.5f, 0.5f), new Vector2(-270, PauseRowY), 168, () => ClosePopup(root), "▶");
        UIKit.ArtButton(body, "ui_replay", new Vector2(0.5f, 0.5f), new Vector2(-90, PauseRowY), 168, () => { ClosePopup(root); CountLoss(); StartLevel(level); }, "↻");
        UIKit.ArtButton(body, "ui_sett", new Vector2(0.5f, 0.5f), new Vector2(90, PauseRowY), 168, ShowSettings, "⚙");
        UIKit.ArtButton(body, "ui_levels", new Vector2(0.5f, 0.5f), new Vector2(270, PauseRowY), 168, () => ShowMap(), "☰");
    }

    void ShowSettings()
    {
        var root = OpenPopup(Loc.T("Settings"), Art ? 860 : 640, out var body, false);
        UIKit.Label(body, Loc.T("Music"), 46, Color.white, new Vector2(0.5f, 0.5f), new Vector2(0, Art ? 60 : 130), new Vector2(600, 60));
        UIKit.MakeSlider(body, new Vector2(0.5f, 0.5f), new Vector2(0, Art ? 0 : 60), 520, AudioManager.MusicVolume, AudioManager.SetMusicVolume);
        UIKit.Label(body, Loc.T("Sound"), 46, Color.white, new Vector2(0.5f, 0.5f), new Vector2(0, Art ? -80 : -40), new Vector2(600, 60));
        UIKit.MakeSlider(body, new Vector2(0.5f, 0.5f), new Vector2(0, Art ? -140 : -110), 520, AudioManager.SfxVolume, AudioManager.SetSfxVolume);
        UIKit.ArtButton(body, "ui_close", new Vector2(1f, 1f), new Vector2(-70, GameArt.I.Has("ui_popup_panel") ? -230 : -30), 120, () => ClosePopup(root), "X");
    }

    // ============================================================ level map
    LevelMap map;

    void ShowMap(bool closable = true, int hopFrom = 0)
    {
        if (map != null) return;
        board.InputLocked = true;
        AudioManager.PlayMusic("menu");
        map = LevelMap.Open(popupRoot, Progress.Level,
            lvl => { CloseMap(false); AudioManager.PlayMusic("Game"); StartLevel(lvl); },
            closable ? () => CloseMap(true) : (System.Action)null, hopFrom, ShowSettings);
        map.onChest = lvl => { if (lvl == Progress.PendingChest) ShowChest(lvl); };
        // a chest just became available: after the marker's hop, it opens by itself
        if (Progress.PendingChest > 0) StartCoroutine(AutoChest(hopFrom > 0 ? 1.5f : 0.6f));
    }

    IEnumerator ChestAfterWin(RectTransform resultRoot)
    {
        yield return new WaitForSecondsRealtime(1.8f);          // let the win screen and its coins play first
        int c = Progress.PendingChest;
        if (c > 0 && resultRoot != null && chestRoot == null) ShowChest(c);
    }

    IEnumerator AutoChest(float delay)
    {
        yield return new WaitForSecondsRealtime(delay);
        int c = Progress.PendingChest;
        if (c > 0 && map != null && chestRoot == null) ShowChest(c);
    }

    // ============================================================ treasure chest
    // Full-screen: golden rays, the closed chest bobs; three taps (each one shakes it harder), the lock
    // pops, the lid flies open with a flash, confetti and coins burst out, the reward counts up.
    RectTransform chestRoot;

    void ShowChest(int chestLevel)
    {
        if (chestRoot != null) return;
        var root = UIKit.Rect("chest", popupRoot); UIKit.Stretch(root);
        chestRoot = root;
        var dim = root.gameObject.AddComponent<Image>(); dim.color = new Color(0.05f, 0.04f, 0.14f, 0.9f);
        StartCoroutine(ChestRoutine(root, chestLevel));
    }

    IEnumerator ChestRoutine(RectTransform root, int chestLevel)
    {
        yield return null;
        float W = root.rect.width, H = root.rect.height;
        Image rays = null, rays2 = null;
        if (GameArt.I.Has("res_win_rays"))
        {
            rays = UIKit.Image(root, "res_win_rays", new Vector2(0.5f, 0.5f), new Vector2(0, 60), Vector2.one * 1700f);
            rays2 = UIKit.Image(root, "res_win_rays", new Vector2(0.5f, 0.5f), new Vector2(0, 60), Vector2.one * 1150f);
        }
        var title = UIKit.Label(root, "Сундук!", 84, new Color(1f, 0.85f, 0.35f), new Vector2(0.5f, 0.5f), new Vector2(0, 560), new Vector2(900, 120));
        var closed = GameArt.I.S("chest_closed");
        float cw = 640f, ch = closed != null ? cw * closed.rect.height / closed.rect.width : 480f;
        var chest = UIKit.Image(root, "chest_closed", new Vector2(0.5f, 0.5f), new Vector2(0, 60), new Vector2(cw, ch));
        chest.raycastTarget = true;
        var hint = UIKit.Label(root, "Нажми на сундук", 50, Color.white, new Vector2(0.5f, 0.5f), new Vector2(0, -300), new Vector2(900, 80));
        var crt = chest.rectTransform;
        int taps = 0; float shake = 0f, shakeAmp = 0f;
        var btn = chest.gameObject.AddComponent<Button>(); btn.transition = Selectable.Transition.None;
        btn.onClick.AddListener(() => { if (taps < 3) { taps++; shake = 0f; shakeAmp = 6f + taps * 4f; AudioManager.Play("chest_tap", 0.6f + 0.15f * taps); } });

        // entrance
        crt.localScale = Vector3.zero;
        yield return UIKit.PopIn(crt, 0.45f);
        float t = 0f;
        while (taps < 3 && root != null)
        {
            float dt = Time.unscaledDeltaTime; t += dt; shake += dt;
            if (rays != null) { rays.rectTransform.localRotation = Quaternion.Euler(0, 0, -t * 10f); rays.color = new Color(1f, 0.82f, 0.4f, 0.35f); rays2.rectTransform.localRotation = Quaternion.Euler(0, 0, t * 6f); rays2.color = new Color(1f, 0.95f, 0.8f, 0.2f); }
            float amp = shakeAmp * Mathf.Exp(-shake * 5f);
            crt.localRotation = Quaternion.Euler(0, 0, amp * Mathf.Sin(shake * 40f));
            float bob = 1f + 0.03f * Mathf.Sin(t * 3f) + 0.06f * amp / 18f;
            crt.localScale = new Vector3(bob, bob, 1f);
            hint.transform.localScale = Vector3.one * (1f + 0.06f * Mathf.Sin(t * 5f));
            yield return null;
        }
        if (root == null) yield break;

        // open!
        hint.gameObject.SetActive(false);
        AudioManager.Play("chest_open", 1f);
        var open = GameArt.I.S("chest_open");
        if (open != null) { chest.sprite = open; float ow = 760f; crt.sizeDelta = new Vector2(ow, ow * open.rect.height / open.rect.width); }
        crt.localRotation = Quaternion.identity;
        var flash = UIKit.Panel(root, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(4000, 6000), new Color(1f, 0.97f, 0.85f, 0.9f), 0);
        StartCoroutine(Kit.Tween(0.5f, k => { if (flash != null) flash.color = new Color(1f, 0.97f, 0.85f, 0.9f * (1f - k)); }));
        StartCoroutine(Kit.Tween(0.45f, k => { if (crt != null) crt.localScale = Vector3.one * (0.85f + 0.25f * Kit.EaseOutBack(k)); }));

        // rewards: 20-40 coins, and every other chest a power-up
        var rng = new System.Random(chestLevel * 977 + 5);
        int coins = 20 + rng.Next(5) * 5;
        PowerUpType? pu = rng.NextDouble() < 0.55 ? (PowerUpType)rng.Next(4) : (PowerUpType?)null;
        Progress.ChestOpened = chestLevel;
        Progress.AddBalance(coins);
        if (pu.HasValue) Progress.AddCharge(pu.Value);
        if (map != null) map.RemoveChest(chestLevel);

        // burst: confetti + coins thrown up out of the chest
        var bits = new List<(RectTransform t, Vector2 v, float spin)>();
        for (int i = 0; i < 40; i++)
        {
            bool coin = i % 3 == 0;
            string key = coin ? "ui_balance" : "res_confetti_" + (1 + i % 8);
            if (!GameArt.I.Has(key)) continue;
            var im = UIKit.Image(root, key, new Vector2(0.5f, 0.5f), new Vector2(Random.Range(-120f, 120f), 120f), Vector2.one * (coin ? 70f : Random.Range(40f, 60f)));
            if (!coin) im.color = Color.HSVToRGB(Random.value, 0.55f, 1f);
            bits.Add((im.rectTransform, new Vector2(Random.Range(-900f, 900f), Random.Range(1300f, 2100f)), Random.Range(-400f, 400f)));
        }
        // reward row
        float ry = -300f;
        var rewardRow = UIKit.Rect("reward", root); UIKit.Place(rewardRow, new Vector2(0.5f, 0.5f), new Vector2(0, ry), new Vector2(700, 140));
        float coinX = pu.HasValue ? -170f : -60f;
        var coinImg = UIKit.Image(rewardRow, "ui_balance", new Vector2(0.5f, 0.5f), new Vector2(coinX, 0), new Vector2(100, 100));
        var coinLbl = UIKit.Label(rewardRow, "+0", 70, new Color(1f, 0.9f, 0.4f), new Vector2(0.5f, 0.5f), new Vector2(coinX + 115, 0), new Vector2(200, 100), true, TextAnchor.MiddleLeft);
        if (pu.HasValue)
        {
            string pk = pu.Value == PowerUpType.Paw ? "pu_paw" : pu.Value == PowerUpType.MegaBomb ? "pu_bomb" : pu.Value == PowerUpType.ColorBlast ? "pu_color" : "pu_moves";
            var pim = UIKit.Image(rewardRow, pk, new Vector2(0.5f, 0.5f), new Vector2(190, 0), new Vector2(130, 130));
            UIKit.Label(pim.transform, "+1", 44, Color.white, new Vector2(1f, 0f), new Vector2(-10, 10), new Vector2(80, 60), true);
            StartCoroutine(UIKit.PopIn(pim.transform, 0.35f));
        }
        StartCoroutine(UIKit.PopIn(rewardRow, 0.35f));
        var take = UIKit.TextButton(root, "Забрать", new Vector2(0.5f, 0.5f), new Vector2(0, -470), new Vector2(460, 130), new Color(0.3f, 0.8f, 0.4f), () =>
        {
            AudioManager.PlayClick();
            if (chestRoot != null) Destroy(chestRoot.gameObject);
            chestRoot = null;
        });
        take.transform.localScale = Vector3.zero;

        float at = 0f; int shown = 0; float tick = 0f;
        while (root != null)
        {
            float dt = Time.unscaledDeltaTime; at += dt; t += dt;
            if (rays != null) { rays.rectTransform.localRotation = Quaternion.Euler(0, 0, -t * 10f); rays.color = new Color(1f, 0.82f, 0.4f, 0.5f); rays2.rectTransform.localRotation = Quaternion.Euler(0, 0, t * 6f); rays2.color = new Color(1f, 0.95f, 0.8f, 0.3f); }
            for (int i = bits.Count - 1; i >= 0; i--)
            {
                var b = bits[i];
                if (b.t == null) { bits.RemoveAt(i); continue; }
                b.v.y -= 2600f * dt; b.v.x *= 1f / (1f + 0.8f * dt);
                b.t.anchoredPosition += b.v * dt;
                b.t.localRotation = Quaternion.Euler(0, 0, b.t.localEulerAngles.z + b.spin * dt);
                bits[i] = b;
                if (b.t.anchoredPosition.y < -H * 0.6f) { Destroy(b.t.gameObject); bits.RemoveAt(i); }
            }
            // count the coins up
            tick += dt;
            if (at > 0.35f && shown < coins && tick > 0.03f) { tick = 0f; shown = Mathf.Min(coins, shown + 1); coinLbl.text = "+" + shown; if (shown % 3 == 0) AudioManager.Play("balance", 0.3f); }
            if (at > 0.9f && take != null && take.transform.localScale.x < 0.01f) StartCoroutine(UIKit.PopIn(take.transform, 0.3f));
            if (coinImg != null) coinImg.rectTransform.localScale = Vector3.one * (1f + 0.05f * Mathf.Sin(at * 6f));
            yield return null;
        }
    }

    void CloseMap(bool resume)
    {
        if (map != null) Destroy(map.gameObject);
        map = null;
        if (resume) { AudioManager.PlayMusic("Game"); StartCoroutine(UnlockNextFrame()); }
    }

    void ShowMapStub()
    {
        var root = OpenPopup(Loc.T("Level", Progress.Level), Art ? 900 : 620, out var body, false);
        AudioManager.PlayMusic("menu");
        UIKit.Label(body, Loc.T("MapSoon", Progress.Level), 44, Color.white, new Vector2(0.5f, 0.5f), new Vector2(0, Art ? 20 : 60), new Vector2(760, 200));
        UIKit.ArtButton(body, "ui_play", new Vector2(0.5f, 0f), new Vector2(0, BottomY(40)), 170, () => { ClosePopup(root); AudioManager.PlayMusic("Game"); }, "▶");
        UIKit.ArtButton(body, "ui_close", new Vector2(1f, 1f), new Vector2(-70, GameArt.I.Has("ui_popup_panel") ? -230 : -30), 120, () => { ClosePopup(root); AudioManager.PlayMusic("Game"); }, "X");
    }

    const float ResultBtnY = 140f;   // the tall result window has a thicker bottom rim: keep the buttons inside it

    void ShowResult(bool win)
    {
        foreach (Transform c in popupRoot) Destroy(c.gameObject);
        string bannerKey = win ? "banner_win" : "banner_lose";
        bool hasBanner = GameArt.I.Has(bannerKey);
        var root = OpenPopup(hasBanner ? null : (win ? Loc.T("Win") : Loc.T("Lose")), Art ? 1300 : 1080, out var body, false);
        ResultBackdrop(root, win);
        if (hasBanner) StartCoroutine(UIKit.PopIn(UIKit.Image(body, bannerKey, new Vector2(0.5f, 1f), new Vector2(0, Art ? -129 : 40), Art ? new Vector2(900, 507) : new Vector2(820, 420)).transform, 0.45f));
        // the level's tasks with how far the player got: done ones get a check mark, unfinished ones show progress in soft red
        int gn = def.goals.Count;
        for (int i = 0; i < gn; i++)
        {
            var g = def.goals[i];
            float x = (i - (gn - 1) * 0.5f) * 250;
            float gy = Art ? 110 : 60;
            var icon = UIKit.Image(body, GoalIcon(g), new Vector2(0.5f, 0.5f), new Vector2(x, gy), new Vector2(170, 170));
            if (!g.Done) icon.color = new Color(1f, 1f, 1f, 0.75f);
            string txt = g.type == GoalType.Score ? $"{Mathf.Min(g.progress, g.target)}/{g.target}" : $"{Mathf.Min(g.progress, g.target)}/{g.target}";
            UIKit.Label(body, txt, g.type == GoalType.Score ? 40 : 48, g.Done ? Color.white : new Color(1f, 0.6f, 0.55f), new Vector2(0.5f, 0.5f), new Vector2(x, gy - 120), new Vector2(240, 60), true);
            if (g.Done && GameArt.I.Has("ui_check"))
                StartCoroutine(UIKit.PopIn(UIKit.Image(body, "ui_check", new Vector2(0.5f, 0.5f), new Vector2(x + 62, gy - 55), new Vector2(80, 80)).transform, 0.35f));
        }
        UIKit.Label(body, $"{Loc.T("Score")}: {score}", 52, Color.white, new Vector2(0.5f, 0.5f), new Vector2(0, Art ? -120 : -230), new Vector2(700, 70), true);
        if (win && lastWinCoins > 0)
        {
            // reward row: coin + "+N", pops in and counts up
            float ry = Art ? -205 : -300;
            var coin = UIKit.Image(body, "ui_balance", new Vector2(0.5f, 0.5f), new Vector2(-70, ry), new Vector2(78, 78));
            var lbl = UIKit.Label(body, "+0", 56, new Color(1f, 0.9f, 0.4f), new Vector2(0.5f, 0.5f), new Vector2(70, ry), new Vector2(200, 80), true, TextAnchor.MiddleLeft);
            StartCoroutine(RewardCountUp(coin.transform, lbl, lastWinCoins));
        }
        // a chest was earned by this win: it opens right here over the win screen, the player doesn't
        // have to visit the map for it (the map still opens any chest left pending, e.g. after a restart)
        if (win && Progress.PendingChest > 0) StartCoroutine(ChestAfterWin(root));

        if (win)
        {
            UIKit.ArtButton(body, "ui_replay", new Vector2(0.5f, 0f), new Vector2(-130, BottomY(ResultBtnY)), 180, () => StartLevel(level), "↻");
            UIKit.ArtButton(body, "ui_next", new Vector2(0.5f, 0f), new Vector2(130, BottomY(ResultBtnY)), 180, () => ShowMap(false, level), "→");      // back to the map: the cat marker hops to the next level
        }
        else
        {
            int charges = Progress.Charges(PowerUpType.ExtraMoves);
            int price = Progress.Price(PowerUpType.ExtraMoves);
            bool can = charges > 0 || Progress.Balance >= price;
            string label = Loc.T("Continue") + (charges > 0 ? "" : $"  ({price})");
            var cont = UIKit.TextButton(body, label, new Vector2(0.5f, 0f), new Vector2(0, BottomY(ResultBtnY) + 160), new Vector2(520, 110), can ? new Color(0.3f, 0.8f, 0.4f) : new Color(0.55f, 0.55f, 0.6f), () =>
            {
                if (Progress.Charges(PowerUpType.ExtraMoves) == 0 && !Progress.Buy(PowerUpType.ExtraMoves)) { Toast(Loc.T("NotEnough")); return; }
                Progress.Consume(PowerUpType.ExtraMoves);
                moves += ExtraMovesAmount; ended = false;
                RefreshTexts(); RefreshPowerUps();
                ClosePopup(root);
            });
            cont.interactable = can;
            UIKit.ArtButton(body, "ui_replay", new Vector2(0.5f, 0f), new Vector2(-130, BottomY(ResultBtnY)), 180, () => StartLevel(level), "↻");
            UIKit.ArtButton(body, "ui_levels", new Vector2(0.5f, 0f), new Vector2(130, BottomY(ResultBtnY)), 180, () => { StartLevel(level); ShowMap(); }, "☰");
        }
    }

    // ============================================================ "Знакомься!" cards
    // The first time a level has a new mechanic, a card introduces it before the level's goals.
    // Mechanics the player already went past when this feature arrived count as known.
    class Meet { public string id, icon, title, text; public int unlock; }
    static readonly Meet[] Meets =
    {
        new Meet { id = "box",   unlock = LevelGen.BoxUnlock,   icon = "box_full",     title = "Коробка",        text = "Котик спрятался в коробку! Собирай котиков рядом — два удара, и она сломается." },
        new Meet { id = "yarn",  unlock = LevelGen.YarnUnlock,  icon = "yarn_wrap",    title = "Клубок",         text = "Котик запутался в клубке. Собери его в ряд с другими — и он свободен!" },
        new Meet { id = "vase",  unlock = LevelGen.VaseUnlock,  icon = "vase",         title = "Ваза",           text = "Собирай котиков рядом с вазой — она разобьётся." },
        new Meet { id = "fish",  unlock = LevelGen.FishUnlock,  icon = "fish",         title = "Рыбка",          text = "Доведи рыбку до самого низа поля — прямо в миску." },
        new Meet { id = "milk",  unlock = LevelGen.MilkUnlock,  icon = "milk_bucket",  title = "Ведро молока",   text = "Задень ведро — молоко разольётся. Котик в молоке даёт монетку и двойные очки!" },
        new Meet { id = "mint",  unlock = LevelGen.MintUnlock,  icon = "mint_wreath",  title = "Кошачья мята",   text = "Мята разрастается, если её не трогать. Собирай котиков рядом с ней!" },
        new Meet { id = "foam",  unlock = LevelGen.FoamUnlock,  icon = "goal_foam",    title = "Мыльная пена",   text = "Убирай котиков, которые стоят на пене, — и она лопнет." },
        new Meet { id = "mouse", unlock = LevelGen.MouseUnlock, icon = "mouse_idle",   title = "Мышка-воришка",  text = "Каждые 3 хода она крадёт у тебя монетку! Убери котика, на котором она сидит, — и получишь всё обратно вдвойне." },
    };
    const string MetBaseKey = "Kuzya.MetBase";

    bool MeetPresent(Meet m)
    {
        switch (m.id)
        {
            case "box": return def.boxes > 0;
            case "yarn": return def.yarns > 0;
            case "vase": return def.vases > 0;
            case "fish": return def.fish > 0;
            case "milk": return def.milk > 0;
            case "mint": return def.mints > 0;
            case "foam": return def.foam > 0;
            case "mouse": return def.mouse;
        }
        return false;
    }

    Meet NextMeet()
    {
        int baseLvl = PlayerPrefs.GetInt(MetBaseKey, -1);
        if (baseLvl < 0) { baseLvl = Progress.Level; PlayerPrefs.SetInt(MetBaseKey, baseLvl); PlayerPrefs.Save(); }
        foreach (var m in Meets)
            if (m.unlock >= baseLvl && MeetPresent(m) && PlayerPrefs.GetInt("Kuzya.Met." + m.id, 0) == 0) return m;
        return null;
    }

    void ShowMeet(Meet m, System.Action next)
    {
        var root = OpenPopup(null, Art ? 1100 : 900, out var body, false);
        AudioManager.Play("meet_card", 0.7f);
        var icon = UIKit.Image(body, m.icon, new Vector2(0.5f, 0.5f), new Vector2(0, 150), new Vector2(250, 250));
        StartCoroutine(MeetIconBob(icon.rectTransform));
        UIKit.Label(body, m.title, 62, new Color(1f, 0.87f, 0.4f), new Vector2(0.5f, 0.5f), new Vector2(0, -20), new Vector2(760, 80));
        var txt = UIKit.Label(body, m.text, 40, Color.white, new Vector2(0.5f, 0.5f), new Vector2(0, -135), new Vector2(680, 170));
        txt.horizontalOverflow = HorizontalWrapMode.Wrap;
        UIKit.TextButton(body, "Понятно!", new Vector2(0.5f, 0f), new Vector2(0, BottomY(40) + 10), new Vector2(440, 120), new Color(0.3f, 0.8f, 0.4f), () =>
        {
            PlayerPrefs.SetInt("Kuzya.Met." + m.id, 1); PlayerPrefs.Save();
            Destroy(root.gameObject);
            next?.Invoke();
        });
        // the badge sits over the window's top ribbon and swings in last
        var badge = UIKit.Image(body, "badge_meet", new Vector2(0.5f, 1f), new Vector2(0, Art ? -95 : 30), new Vector2(720, 267));
        badge.rectTransform.localScale = Vector3.zero;
        StartCoroutine(BadgeIn(badge.rectTransform));
    }

    IEnumerator BadgeIn(RectTransform t)
    {
        yield return new WaitForSeconds(0.15f);
        yield return Kit.Tween(0.45f, k => { if (t != null) { t.localScale = Vector3.one * Kit.EaseOutBack(k); t.localRotation = Quaternion.Euler(0, 0, -8f * (1f - k)); } });
        float tm = 0f;
        while (t != null) { tm += Time.unscaledDeltaTime; t.localRotation = Quaternion.Euler(0, 0, 2.5f * Mathf.Sin(tm * 2.2f)); yield return null; }
    }

    IEnumerator MeetIconBob(RectTransform t)
    {
        Vector2 p0 = t != null ? t.anchoredPosition : Vector2.zero;
        if (t != null) t.localScale = Vector3.zero;
        yield return Kit.Tween(0.35f, k => { if (t != null) t.localScale = Vector3.one * Kit.EaseOutBack(k); });
        float tm = 0f;
        while (t != null) { tm += Time.unscaledDeltaTime; t.anchoredPosition = p0 + new Vector2(0, 12f * Mathf.Sin(tm * 3f)); yield return null; }
    }

    // ============================================================ "Попался!" (mouse caught)
    // Same layered style as the combo banners: a big cat paw slams down on the mouse, a golden splat,
    // the word lands with a wobble, and the coins pour out of the mouse's sack back to the balance.
    const int MouseMinReward = 5;

    IEnumerator CaughtBanner(int reward)
    {
        coinsInFlight += reward; Progress.AddBalance(reward);    // paid at once: safe even if the level ends now
        if (!GameArt.I.Has("caught_word")) { Toast("Попался! +" + reward); StartCoroutine(CoinShower(lastClearPos, reward)); yield break; }
        if (activeComboRoot != null) Destroy(activeComboRoot.gameObject);
        var root = UIKit.Rect("caught", toastRoot); UIKit.Stretch(root);
        activeComboRoot = root;
        Color gold = new Color(1f, 0.78f, 0.25f);
        Vector2 C = new Vector2(0, 80);
        float H = toastRoot.rect.height;
        Image Img(string key, Vector2 pos, Vector2 size, Color c) { var i = UIKit.Image(root, key, new Vector2(0.5f, 0.5f), pos, size); i.color = c; return i; }

        var burst = Img("cmb_burst", C + new Vector2(0, -20), new Vector2(860, 820), new Color(gold.r, gold.g, gold.b, 0f));
        var bitsLayer = UIKit.Rect("bits", root); UIKit.Stretch(bitsLayer);
        var sack = Img("caught_sack", C + new Vector2(250, -110), new Vector2(190, 267), new Color(1, 1, 1, 0));
        var mouse = Img("mouse_caught", C + new Vector2(-10, -20), new Vector2(400, 400), new Color(1, 1, 1, 0));
        var paw = Img("caught_paw", Vector2.zero, new Vector2(330, 414), Color.white);
        paw.rectTransform.pivot = new Vector2(0.5f, 0.04f);          // the toes
        Vector2 pawRest = C + new Vector2(-40, 120), pawFrom = new Vector2(pawRest.x, H * 0.5f + 80f);
        var wordSprite = GameArt.I.S("caught_word");
        float ww = 800f, wh = ww * wordSprite.rect.height / wordSprite.rect.width;
        var word = Img("caught_word", C + new Vector2(0, -310), new Vector2(ww, wh), new Color(1, 1, 1, 0));

        var bits = new List<(RectTransform t, Image i, Vector2 v, float spin, float life)>();
        Color[] pal = { gold, new Color(1f, 0.92f, 0.5f), Color.white, new Color(1f, 0.6f, 0.2f) };
        void SpawnBits(int n, Vector2 at)
        {
            for (int k = 0; k < n; k++)
            {
                float a = Random.Range(0f, Mathf.PI * 2f), sp = Random.Range(600f, 1150f);
                var i = UIKit.Image(bitsLayer, "cmb_bit_" + Random.Range(1, 9), new Vector2(0.5f, 0.5f), at + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 50f, Vector2.one * Random.Range(44f, 78f));
                i.color = pal[Random.Range(0, pal.Length)];
                i.rectTransform.localRotation = Quaternion.Euler(0, 0, Random.Range(0f, 360f));
                bits.Add((i.rectTransform, i, new Vector2(Mathf.Cos(a), Mathf.Sin(a) * 0.8f + 0.35f) * sp, Random.Range(-400f, 400f), Random.Range(0.7f, 1.05f)));
            }
        }

        const float Impact = 0.2f, LiftAt = 0.95f, PayAt = 0.6f, ExitAt = 1.75f, End = 2.15f;
        bool impacted = false, paid = false;
        float t = 0f;
        while (t < End && root != null)
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, 1f / 30f);
            t += dt;
            // paw: drops from above, squashes on the mouse, later lifts away
            var prt = paw.rectTransform;
            if (t < Impact) prt.anchoredPosition = Vector2.Lerp(pawFrom, pawRest, Kit.EaseInCubic(t / Impact));
            else if (t < LiftAt)
            {
                float it0 = t - Impact;
                float sq = Mathf.Exp(-it0 * 9f) * Mathf.Sin(it0 * 30f);
                prt.anchoredPosition = pawRest;
                prt.localScale = new Vector3(1f + 0.08f * sq, 1f - 0.1f * sq, 1f);
            }
            else
            {
                float lk = Mathf.Clamp01((t - LiftAt) / 0.3f);
                prt.anchoredPosition = Vector2.Lerp(pawRest, pawFrom, Kit.EaseInCubic(lk));
                prt.localScale = Vector3.one;
            }

            if (!impacted && t >= Impact)
            {
                impacted = true;
                AudioManager.Play("mouse_caught", 1f); AudioManager.Play("Hit", 0.5f);
                SpawnBits(16, C);
                FX.Shake(board.cell * 0.1f, 0.25f);
            }
            float it = t - Impact;
            if (it >= 0f)
            {
                float bk = Mathf.Clamp01(it / 0.3f);
                burst.rectTransform.localScale = Vector3.one * (0.3f + 0.85f * Kit.EaseOutBack(bk));
                burst.rectTransform.localRotation = Quaternion.Euler(0, 0, -it * 25f);
                burst.color = new Color(gold.r, gold.g, gold.b, 0.9f * Mathf.Clamp01(it / 0.08f));
                // the mouse: squashed flat by the paw, springs back dizzy
                float mk = Mathf.Exp(-it * 6f) * Mathf.Cos(it * 22f);
                mouse.rectTransform.localScale = new Vector3(1f + 0.25f * mk, 1f - 0.25f * mk, 1f);
                mouse.color = new Color(1, 1, 1, Mathf.Clamp01(it / 0.06f));
                // word slams in a beat later
                float wt = it - 0.08f;
                if (wt >= 0f)
                {
                    float wk = Mathf.Clamp01(wt / 0.22f);
                    float sc = Mathf.Lerp(2.1f, 1f, Kit.EaseOutCubic(wk));
                    float land = wt > 0.22f ? Mathf.Exp(-(wt - 0.22f) * 7f) * Mathf.Sin((wt - 0.22f) * 26f) : 0f;
                    word.rectTransform.localScale = new Vector3(sc * (1f + 0.12f * land), sc * (1f - 0.12f * land), 1f);
                    word.rectTransform.localRotation = Quaternion.Euler(0, 0, 4f * land - 3f * (1f - wk));
                    word.color = new Color(1, 1, 1, Mathf.Clamp01(wk * 3f));
                }
                // the sack pops up beside the mouse and jiggles as the coins pour out
                float st = it - 0.2f;
                if (st >= 0f)
                {
                    float sk = Kit.EaseOutBack(Mathf.Clamp01(st / 0.25f));
                    float jig = t > PayAt ? 0.06f * Mathf.Sin(t * 30f) * Mathf.Clamp01(1f - (t - PayAt) / 0.8f) : 0f;
                    sack.rectTransform.localScale = new Vector3(sk * (1f + jig), sk * (1f - jig), 1f);
                    sack.color = Color.white;
                }
            }
            if (!paid && t >= PayAt)
            {
                paid = true;
                Vector3 sp = sack.rectTransform.position; sp.z = 0;
                AudioManager.Play("balance");
                SpawnBits(8, sack.rectTransform.anchoredPosition + new Vector2(0, 100));
                StartCoroutine(CoinShower(sp, reward));
            }
            if (t >= ExitAt)
            {
                float ek = Mathf.Clamp01((t - ExitAt) / (End - ExitAt));
                float a = 1f - Kit.EaseInOut(ek);
                foreach (var im in new[] { word, burst, mouse, sack })
                {
                    var c = im.color; c.a = Mathf.Min(c.a, a); im.color = c;
                }
                root.anchoredPosition = new Vector2(0, 90f * Kit.EaseInOut(ek));
            }
            for (int i = bits.Count - 1; i >= 0; i--)
            {
                var b = bits[i];
                if (b.t == null) { bits.RemoveAt(i); continue; }
                b.v.y -= 1500f * dt; b.v *= 1f / (1f + 1.2f * dt);
                b.t.anchoredPosition += b.v * dt;
                b.t.localRotation = Quaternion.Euler(0, 0, b.t.localEulerAngles.z + b.spin * dt);
                b.life -= dt;
                var c = b.i.color; c.a = Mathf.Clamp01(b.life / 0.35f); b.i.color = c;
                bits[i] = b;
                if (b.life <= 0f) { Destroy(b.t.gameObject); bits.RemoveAt(i); }
            }
            yield return null;
        }
        if (root != null) Destroy(root.gameObject);
    }

    const int MilkBonusScore = 30;   // the extra points for a cat cleared in milk (its points again)

    // "x2" over a cat that was cleared in milk: pops up, floats up with a little wobble and fades
    IEnumerator FloatX2(Vector3 world)
    {
        Graphic lbl;
        if (GameArt.I.Has("milk_x2")) lbl = UIKit.Image(toastRoot, "milk_x2", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(150, 110));
        else lbl = UIKit.Label(toastRoot, "x2", 64, new Color(1f, 0.88f, 0.3f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(160, 90), true);
        var rt = lbl.rectTransform;
        rt.position = new Vector3(world.x, world.y, toastRoot.position.z);
        Vector2 start = rt.anchoredPosition;
        float side = Random.Range(-1f, 1f);
        yield return Kit.Tween(0.9f, k =>
        {
            if (rt == null) return;
            float pop = k < 0.18f ? Kit.EaseOutBack(k / 0.18f) * 1.15f : Mathf.Lerp(1.15f, 1f, (k - 0.18f) / 0.3f);
            rt.localScale = Vector3.one * Mathf.Max(0f, pop);
            rt.anchoredPosition = start + new Vector2(side * 30f * k, 150f * Kit.EaseOutCubic(k));
            rt.localRotation = Quaternion.Euler(0, 0, 8f * Mathf.Sin(k * 12f) * (1f - k));
            var c = lbl.color; c.a = k < 0.6f ? 1f : 1f - (k - 0.6f) / 0.4f; lbl.color = c;
        });
        if (rt != null) Destroy(rt.gameObject);
    }

    IEnumerator RewardCountUp(Transform coin, Text lbl, int amount)
    {
        coin.localScale = Vector3.zero; lbl.transform.localScale = Vector3.zero;
        yield return new WaitForSecondsRealtime(0.45f);
        StartCoroutine(UIKit.PopIn(coin, 0.3f)); StartCoroutine(UIKit.PopIn(lbl.transform, 0.3f));
        for (int i = 1; i <= amount; i++)
        {
            if (lbl == null) yield break;
            lbl.text = "+" + i;
            if (i % 2 == 1) AudioManager.Play("balance", 0.35f);
            yield return new WaitForSecondsRealtime(0.05f);
        }
        if (coin != null) yield return Kit.Tween(0.2f, k => { if (coin != null) coin.localScale = Vector3.one * (1f + 0.25f * Mathf.Sin(k * Mathf.PI)); });
    }

    // ============================================================ result screen backdrop
    // Win: slow golden rays + fireworks behind the window, confetti bursting from the bottom corners and
    // then drifting down in front, Kuzya & Nafanya peek up from the bottom edge and hop.
    // Lose: the screen dims, a sad cloud floats above the window, soft rain with splashes, the cats
    // sit under the umbrella at the bottom and sway a little. Everything lives under the popup root,
    // so it disappears together with the popup.
    static readonly Color[] PartyColors =
    {
        new Color(1f, 0.38f, 0.5f), new Color(1f, 0.8f, 0.25f), new Color(0.35f, 0.8f, 1f),
        new Color(0.5f, 0.9f, 0.42f), new Color(0.78f, 0.52f, 1f), new Color(1f, 0.58f, 0.25f), Color.white
    };

    class ResultBit
    {
        public RectTransform t; public Image img; public Vector2 v; public float g, drag, spin, flip, phase, age, life, size; public int kind;
    }

    void ResultBackdrop(RectTransform root, bool win)
    {
        var dim = root.GetComponent<Image>();
        if (dim != null) dim.color = win ? new Color(0.1f, 0.05f, 0.18f, 0.62f) : new Color(0.03f, 0.05f, 0.13f, 0.78f);
        var back = UIKit.Rect("backdrop", root); UIKit.Stretch(back); back.SetAsFirstSibling();   // behind the window
        var front = UIKit.Rect("front", root); UIKit.Stretch(front);                              // above the window
        StartCoroutine(win ? WinBackdrop(root, back, front) : LoseBackdrop(root, back, front));
    }

    // the cats sit on the bottom edge, in the gap under the window (the window hides their ear tips)
    Image PeekCats(RectTransform parent, string key, float H, out float h)
    {
        float gap = H * 0.5f - 610f;
        h = Mathf.Clamp(gap + 50f, 300f, 520f);
        var img = UIKit.Image(parent, key, new Vector2(0.5f, 0f), Vector2.zero, new Vector2(h * 2.4f, h));
        img.rectTransform.pivot = new Vector2(0.5f, 0f);
        img.rectTransform.anchoredPosition = new Vector2(0, -h);
        return img;
    }

    ResultBit Bit(RectTransform parent, string key, Vector2 pos, float size, Color c)
    {
        var img = UIKit.Image(parent, key, new Vector2(0.5f, 0.5f), pos, new Vector2(size, size));
        img.color = c;
        return new ResultBit { t = img.rectTransform, img = img, size = size };
    }

    IEnumerator WinBackdrop(RectTransform root, RectTransform back, RectTransform front)
    {
        yield return null;                                   // let the layout give the root its size
        if (root == null) yield break;
        float W = root.rect.width, H = root.rect.height;
        Image rays = null, rays2 = null;
        if (GameArt.I.Has("res_win_rays"))
        {
            rays = UIKit.Image(back, "res_win_rays", new Vector2(0.5f, 0.5f), new Vector2(0, 40), Vector2.one * Mathf.Max(W, H) * 1.35f);
            rays2 = UIKit.Image(back, "res_win_rays", new Vector2(0.5f, 0.5f), new Vector2(0, 40), Vector2.one * Mathf.Max(W, H) * 0.95f);
        }
        var fwLayer = UIKit.Rect("fireworks", back); UIKit.Stretch(fwLayer);
        float ch = 0f;
        var cats = GameArt.I.Has("res_win_cats") ? PeekCats(back, "res_win_cats", H, out ch) : null;

        var bits = new List<ResultBit>();
        string[] conf = new string[8];
        for (int i = 0; i < 8; i++) conf[i] = "res_confetti_" + (i + 1);
        bool hasConf = GameArt.I.Has("res_confetti_1");

        void Confetti(Vector2 pos, Vector2 v, float g)
        {
            if (!hasConf) return;
            var b = Bit(front, conf[Random.Range(0, 8)], pos, Random.Range(38f, 64f), PartyColors[Random.Range(0, PartyColors.Length)]);
            b.v = v; b.g = g; b.drag = 1.6f; b.spin = Random.Range(-260f, 260f); b.flip = Random.Range(3f, 7f); b.phase = Random.value * 6f;
            b.life = 9f; b.kind = 0; b.t.localRotation = Quaternion.Euler(0, 0, Random.Range(0f, 360f));
            bits.Add(b);
        }
        void Firework()
        {
            if (!GameArt.I.Has("res_win_firework")) return;
            float top = H * 0.5f - 150f;
            var pos = Random.value < 0.6f
                ? new Vector2(Random.Range(-W * 0.4f, W * 0.4f), Random.Range(top - 140f, top))        // above the window
                : new Vector2((Random.value < 0.5f ? -1 : 1) * Random.Range(W * 0.36f, W * 0.48f), Random.Range(-H * 0.25f, H * 0.3f)); // at the sides
            var c = PartyColors[Random.Range(0, PartyColors.Length - 1)];
            var b = Bit(fwLayer, "res_win_firework", pos, Random.Range(380f, 580f), c);
            b.kind = 1; b.life = 1.3f; b.t.localRotation = Quaternion.Euler(0, 0, Random.Range(0f, 360f)); b.t.localScale = Vector3.one * 0.2f;
            bits.Add(b);
        }

        // opening: two confetti cannons from the bottom corners + a couple of fireworks
        for (int i = 0; i < 26; i++)
        {
            float s = i % 2 == 0 ? -1f : 1f;
            Confetti(new Vector2(s * (W * 0.5f - 20f), -H * 0.5f + 80f),
                     new Vector2(-s * Random.Range(250f, 800f), Random.Range(1300f, 2100f)), 1500f);
        }
        Firework(); Firework();

        float t = 0f, nextFw = 0.45f, nextConf = 0.6f;
        while (root != null)
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            t += dt;
            if (rays != null)
            {
                rays.rectTransform.localRotation = Quaternion.Euler(0, 0, -t * 7f);
                rays.color = new Color(1f, 0.82f, 0.38f, Mathf.Clamp01(t * 2f) * (0.34f + 0.08f * Mathf.Sin(t * 1.7f)));
                rays2.rectTransform.localRotation = Quaternion.Euler(0, 0, 15f + t * 4.5f);
                rays2.color = new Color(1f, 0.95f, 0.8f, Mathf.Clamp01(t * 2f) * (0.22f + 0.08f * Mathf.Sin(t * 2.3f + 1f)));
            }
            if (cats != null)
            {
                // rise with a little overshoot, then happy hops (squash on landing)
                float rise = Kit.EaseOutBack(Mathf.Clamp01((t - 0.25f) / 0.6f));
                float hopT = Mathf.Repeat(t - 0.85f, 1.1f);
                float hop = t > 0.85f && hopT < 0.45f ? Mathf.Sin(hopT / 0.45f * Mathf.PI) : 0f;
                float squash = t > 0.85f && hopT >= 0.45f && hopT < 0.6f ? Mathf.Sin((hopT - 0.45f) / 0.15f * Mathf.PI) : 0f;
                // never lift the bottom edge off the screen edge (the art is cut there): rest low, hop within that slack
                cats.rectTransform.anchoredPosition = new Vector2(0, Mathf.Min(Mathf.Lerp(-ch, -ch * 0.1f, rise) + hop * ch * 0.08f, -2f));
                cats.rectTransform.localScale = new Vector3(1f + 0.03f * squash, 1f - 0.04f * squash, 1f);
                cats.rectTransform.localRotation = Quaternion.Euler(0, 0, 1.5f * Mathf.Sin(t * 2.2f));
            }
            if (t >= nextFw) { Firework(); nextFw = t + Random.Range(0.45f, 0.9f); }
            if (t >= nextConf && bits.Count < 70)
            {
                Confetti(new Vector2(Random.Range(-W * 0.5f, W * 0.5f), H * 0.5f + 60f), new Vector2(Random.Range(-60f, 60f), -Random.Range(120f, 220f)), 260f);
                nextConf = t + (t < 4f ? 0.06f : 0.16f);
            }

            for (int i = bits.Count - 1; i >= 0; i--)
            {
                var b = bits[i];
                b.age += dt;
                if (b.t == null) { bits.RemoveAt(i); continue; }
                if (b.kind == 0)
                {
                    b.v.y -= b.g * dt;
                    b.v *= 1f / (1f + b.drag * dt);
                    var p = b.t.anchoredPosition + b.v * dt;
                    p.x += Mathf.Sin(b.age * 2.4f + b.phase) * 50f * dt;                 // flutter
                    b.t.anchoredPosition = p;
                    b.t.localRotation = Quaternion.Euler(0, 0, b.t.localEulerAngles.z + b.spin * dt);
                    b.t.localScale = new Vector3(Mathf.Cos(b.age * b.flip + b.phase), 1f, 1f);   // tumbling
                    if (p.y < -H * 0.5f - 80f || b.age > b.life) { Destroy(b.t.gameObject); bits.RemoveAt(i); }
                }
                else
                {
                    float k = b.age / b.life;
                    b.t.localScale = Vector3.one * Mathf.Lerp(0.2f, 1.05f, Kit.EaseOutCubic(Mathf.Clamp01(k * 1.6f)));
                    var c = b.img.color; c.a = k < 0.45f ? 1f : Mathf.Clamp01(1f - (k - 0.45f) / 0.55f);
                    b.img.color = c;
                    b.t.anchoredPosition += Vector2.down * 30f * dt;                         // sparks sag a little
                    if (k >= 1f) { Destroy(b.t.gameObject); bits.RemoveAt(i); }
                }
            }
            yield return null;
        }
    }

    IEnumerator LoseBackdrop(RectTransform root, RectTransform back, RectTransform front)
    {
        yield return null;
        if (root == null) yield break;
        float W = root.rect.width, H = root.rect.height;
        // two sad clouds in the top corners (the lose banner already has a little cloud in the middle);
        // the right one is smaller and mirrored so they don't look copy-pasted
        var clouds = new List<Image>(); var cloudPos = new List<Vector2>(); var cloudSize = new List<float>();
        if (GameArt.I.Has("res_lose_cloud"))
            for (int i = 0; i < 2; i++)
            {
                float ch2 = i == 0 ? 300f : 230f;
                var pos = new Vector2((i == 0 ? -1f : 1f) * (W * 0.5f - ch2 * 0.45f), H * 0.5f - ch2 * 0.5f - (i == 0 ? 40f : 150f));
                var c = UIKit.Image(back, "res_lose_cloud", new Vector2(0.5f, 0.5f), pos + new Vector2(0, 400f), new Vector2(ch2 * 1.15f, ch2));
                if (i == 1) c.rectTransform.localScale = new Vector3(-1f, 1f, 1f);
                clouds.Add(c); cloudPos.Add(pos); cloudSize.Add(ch2);
            }
        var rain = UIKit.Rect("rain", back); UIKit.Stretch(rain);
        float ch = 0f;
        var cats = GameArt.I.Has("res_lose_cats") ? PeekCats(back, "res_lose_cats", H, out ch) : null;
        var bits = new List<ResultBit>();
        bool hasDrop = GameArt.I.Has("res_lose_drop"), hasSplash = GameArt.I.Has("res_lose_splash");
        var dropCol = new Color(0.72f, 0.86f, 1f, 0.75f);

        float t = 0f, nextDrop = 0.5f;
        while (root != null)
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            t += dt;
            for (int i = 0; i < clouds.Count; i++)
            {
                float come = Kit.EaseOutCubic(Mathf.Clamp01((t - i * 0.25f) / 0.9f));
                clouds[i].rectTransform.anchoredPosition = cloudPos[i] + new Vector2(22f * Mathf.Sin(t * 0.55f + i * 2f), 400f * (1f - come) + 9f * Mathf.Sin(t * 1.3f + i));
                clouds[i].rectTransform.localRotation = Quaternion.Euler(0, 0, 2.5f * Mathf.Sin(t * 0.8f + i * 1.5f));
            }
            if (cats != null)
            {
                float rise = Kit.EaseOutCubic(Mathf.Clamp01((t - 0.35f) / 0.9f));
                cats.rectTransform.anchoredPosition = new Vector2(0, Mathf.Lerp(-ch, -ch * 0.04f - 8f, rise) - 5f * (0.5f + 0.5f * Mathf.Sin(t * 1.1f)));
                cats.rectTransform.localRotation = Quaternion.Euler(0, 0, 1.2f * Mathf.Sin(t * 0.9f));   // a slow, sad sway
            }
            if (hasDrop && t >= nextDrop)
            {
                // half the drops fall from the cloud, the rest from across the top
                bool fromCloud = Random.value < 0.6f && clouds.Count > 0;
                int ci = Random.Range(0, clouds.Count);
                Vector2 p = fromCloud
                    ? clouds[ci].rectTransform.anchoredPosition + new Vector2(Random.Range(-cloudSize[ci] * 0.4f, cloudSize[ci] * 0.4f), -cloudSize[ci] * 0.25f)
                    : new Vector2(Random.Range(-W * 0.5f, W * 0.5f), H * 0.5f + 40f);
                var b = Bit(rain, "res_lose_drop", p, Random.Range(34f, 50f), dropCol);
                b.v = new Vector2(-40f, -Random.Range(900f, 1250f));
                b.life = -H * 0.5f + Random.Range(25f, 110f);         // ground height for this drop
                b.t.localRotation = Quaternion.Euler(0, 0, -2f);
                b.t.localScale = new Vector3(0.75f, 1.2f, 1f);         // stretched while falling
                bits.Add(b);
                nextDrop = t + Random.Range(0.05f, 0.11f);
            }
            for (int i = bits.Count - 1; i >= 0; i--)
            {
                var b = bits[i];
                b.age += dt;
                if (b.t == null) { bits.RemoveAt(i); continue; }
                if (b.kind == 0)
                {
                    var p = b.t.anchoredPosition + b.v * dt;
                    b.t.anchoredPosition = p;
                    if (p.y <= b.life)
                    {
                        if (hasSplash)
                        {
                            var s = Bit(rain, "res_lose_splash", new Vector2(p.x, b.life + 14f), Random.Range(70f, 110f), dropCol);
                            s.kind = 1; s.life = 0.4f; s.t.localScale = new Vector3(0.4f, 0.3f, 1f);
                            bits.Add(s);
                        }
                        Destroy(b.t.gameObject); bits.RemoveAt(i);
                    }
                }
                else
                {
                    float k = b.age / b.life;
                    float e = Kit.EaseOutCubic(Mathf.Clamp01(k));
                    b.t.localScale = new Vector3(Mathf.Lerp(0.4f, 1f, e), Mathf.Lerp(0.3f, 1f, e), 1f);
                    b.img.color = new Color(dropCol.r, dropCol.g, dropCol.b, dropCol.a * (1f - k));
                    if (k >= 1f) { Destroy(b.t.gameObject); bits.RemoveAt(i); }
                }
            }
            yield return null;
        }
    }

    // ============================================================ small UI effects
    // Combo notification: 85% opaque art (outer splashes already fade out in the soft PNGs),
    // quick but smooth - eases in with a little grow, holds briefly, drifts up and fades away.
    const float ComboAlpha = 0.85f;
    Image activeCombo;

    // ---- layered combo banner ----
    // paw swipes across -> glowing claw marks -> a coloured splat bursts -> the word slams in and wobbles,
    // glossy bits fly out, coins fly to the balance, the tier's character shows up (rocket / star / monster),
    // then everything shrinks and floats away. Bigger tiers = more bits, shake and a screen flash.
    static readonly Color[] ComboTint = { new Color(1f, 0.6f, 0.15f), new Color(0.2f, 0.75f, 1f), new Color(0.45f, 0.9f, 0.2f), new Color(0.75f, 0.3f, 1f) };
    static readonly int[] ComboCoins = { 1, 2, 3, 5 };
    RectTransform activeComboRoot;

    IEnumerator ComboBanner(int tier)
    {
        if (!GameArt.I.Has("cmb_word_" + tier)) { yield return ComboBannerFlat(tier); yield break; }
        AudioManager.Play("Combo");
        if (activeComboRoot != null) Destroy(activeComboRoot.gameObject);   // a newer combo replaces the old one
        var root = UIKit.Rect("combo", toastRoot); UIKit.Stretch(root);
        activeComboRoot = root;
        Color tint = ComboTint[tier - 1];
        Vector2 C = new Vector2(0, 80);
        float W = toastRoot.rect.width;

        Image Img(string key, Vector2 pos, Vector2 size, Color c)
        {
            var i = UIKit.Image(root, key, new Vector2(0.5f, 0.5f), pos, size);
            i.color = c; return i;
        }
        // layers, back to front
        var flash = tier == 4 ? UIKit.Panel(root, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(4000, 6000), new Color(0.7f, 0.3f, 1f, 0f), 0) : null;
        var burst = Img("cmb_burst", C, new Vector2(820, 780), new Color(tint.r, tint.g, tint.b, 0f));
        var slash = Img("cmb_slash", C + new Vector2(-20, -10), new Vector2(980, 560), new Color(1f, 1f, 1f, 0f));
        var slashGlow = Img("cmb_slash", C + new Vector2(-20, -10), new Vector2(1040, 600), new Color(tint.r, tint.g, tint.b, 0f));
        slashGlow.transform.SetSiblingIndex(slash.transform.GetSiblingIndex());
        var bitsLayer = UIKit.Rect("bits", root); UIKit.Stretch(bitsLayer);
        string charKey = tier == 2 ? "cmb_rocket" : tier == 3 ? "cmb_star" : tier == 4 ? "cmb_monster" : null;
        Image chr = charKey != null && GameArt.I.Has(charKey)
            ? Img(charKey, Vector2.zero, tier == 4 ? new Vector2(430, 550) : new Vector2(300, 300), new Color(1, 1, 1, 0)) : null;
        var wordSprite = GameArt.I.S("cmb_word_" + tier);
        float ww = 780f, wh = ww * wordSprite.rect.height / wordSprite.rect.width;
        var word = Img("cmb_word_" + tier, C, new Vector2(ww, wh), new Color(1, 1, 1, 0));
        var paw = Img(tier == 4 ? "cmb_paw_monster" : "cmb_paw", Vector2.zero, new Vector2(900, 550), Color.white);

        // flying bits
        var bits = new List<(RectTransform t, Image i, Vector2 v, float spin, float life)>();
        Color[] pal = { tint, Color.Lerp(tint, Color.white, 0.45f), new Color(1f, 0.85f, 0.25f), Color.white, Color.Lerp(tint, new Color(1f, 0.4f, 0.6f), 0.5f) };
        void SpawnBits(int n)
        {
            for (int k = 0; k < n; k++)
            {
                float a = Random.Range(0f, Mathf.PI * 2f), sp = Random.Range(650f, 1250f) * (0.8f + 0.1f * tier);
                var i = UIKit.Image(bitsLayer, "cmb_bit_" + Random.Range(1, 9), new Vector2(0.5f, 0.5f), C + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 60f, Vector2.one * Random.Range(46f, 84f));
                i.color = pal[Random.Range(0, pal.Length)];
                i.rectTransform.localRotation = Quaternion.Euler(0, 0, Random.Range(0f, 360f));
                bits.Add((i.rectTransform, i, new Vector2(Mathf.Cos(a), Mathf.Sin(a) * 0.8f + 0.35f) * sp, Random.Range(-400f, 400f), Random.Range(0.7f, 1.05f)));
            }
        }

        const float SwipeEnd = 0.2f, Impact = 0.2f, ExitAt = 1.25f, End = 1.6f;
        bool impacted = false, paid = false;
        Vector2 pawFrom = new Vector2(-W * 0.5f - 70f, C.y - 520f);
        paw.rectTransform.pivot = new Vector2(0.02f, 0.2f);   // shoulder end of the arm
        paw.transform.SetSiblingIndex(burst.transform.GetSiblingIndex());   // behind the splat and the word
        float t = 0f;
        while (t < End && root != null)
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, 1f / 30f);   // a hitch never skips the animation
            t += dt;
            // paw swipe (fast ease-in-out, a bit of rotation) and the claw marks drawn behind it
            // the arm is cut at the image edge, so the paw stays rooted off-screen in the lower-left corner:
            // it reaches in, sweeps up across the middle and pulls back out
            float sk = Mathf.Clamp01(t / (SwipeEnd + 0.08f));
            float reach = Mathf.Sin(sk * Mathf.PI);
            paw.rectTransform.anchoredPosition = pawFrom;
            paw.rectTransform.localScale = Vector3.one * Mathf.Lerp(0.55f, 1.25f, reach);
            paw.rectTransform.localRotation = Quaternion.Euler(0, 0, Mathf.Lerp(-30f, 34f, Kit.EaseInOut(sk)));
            if (sk >= 1f && paw.gameObject.activeSelf) paw.gameObject.SetActive(false);
            float reveal = Mathf.Clamp01((t - 0.07f) / 0.16f);
            float slashA = reveal * (t < 0.55f ? 1f : Mathf.Clamp01(1f - (t - 0.55f) / 0.35f));
            slash.color = new Color(1f, 1f, 1f, slashA);
            slashGlow.color = new Color(tint.r, tint.g, tint.b, slashA * 0.85f);
            float sx = Kit.EaseOutCubic(reveal);
            slash.rectTransform.localScale = new Vector3(sx, 0.6f + 0.4f * sx, 1f);
            slashGlow.rectTransform.localScale = slash.rectTransform.localScale;

            if (!impacted && t >= Impact)
            {
                impacted = true;
                SpawnBits(10 + 5 * tier);
                if (tier >= 3) FX.Shake(board.cell * (tier == 4 ? 0.18f : 0.1f), 0.3f);
            }
            if (!paid && t >= Impact + 0.15f)
            {
                paid = true;
                int n = Coins(ComboCoins[tier - 1]);
                coinsInFlight += n; Progress.AddBalance(n);
                StartCoroutine(CoinShower(lastClearPos, n));
            }
            float it = t - Impact;
            if (it >= 0f)
            {
                // burst: pops big, turns slowly, fades before the exit
                float bk = Mathf.Clamp01(it / 0.3f);
                burst.rectTransform.localScale = Vector3.one * (0.3f + 0.85f * Kit.EaseOutBack(bk));
                burst.rectTransform.localRotation = Quaternion.Euler(0, 0, -it * 25f);
                burst.color = new Color(tint.r, tint.g, tint.b, 0.92f * Mathf.Clamp01(it / 0.08f));
                // word: slams in from big, squashes on landing, then wobbles
                float wk = Mathf.Clamp01(it / 0.22f);
                float sc = Mathf.Lerp(2.1f, 1f, Kit.EaseOutCubic(wk));
                float land = it > 0.22f ? Mathf.Exp(-(it - 0.22f) * 7f) * Mathf.Sin((it - 0.22f) * 26f) : 0f;
                word.rectTransform.localScale = new Vector3(sc * (1f + 0.12f * land), sc * (1f - 0.12f * land), 1f);
                word.rectTransform.localRotation = Quaternion.Euler(0, 0, 4f * land - 3f * (1f - wk));
                word.color = new Color(1, 1, 1, Mathf.Clamp01(wk * 3f));
                if (flash != null) flash.color = new Color(0.7f, 0.3f, 1f, 0.35f * Mathf.Clamp01(1f - it / 0.35f));
            }
            // character
            if (chr != null && it >= 0.08f)
            {
                float ct = it - 0.08f;
                var rt = chr.rectTransform;
                if (tier == 2)
                {   // rocket zooms from the lower left past the word and off the top right
                    float k = Mathf.Clamp01(ct / 0.9f);
                    rt.anchoredPosition = Vector2.Lerp(C + new Vector2(-W * 0.5f - 200f, -380f), C + new Vector2(W * 0.5f + 220f, 520f), Kit.EaseInOut(k)) + new Vector2(0, 40f * Mathf.Sin(k * Mathf.PI));
                    rt.localRotation = Quaternion.Euler(0, 0, -8f + 6f * Mathf.Sin(ct * 18f));
                    chr.color = Color.white;
                }
                else if (tier == 3)
                {   // star pops up at the word's top-right corner and waves
                    float k = Kit.EaseOutBack(Mathf.Clamp01(ct / 0.25f));
                    rt.anchoredPosition = C + new Vector2(ww * 0.43f, wh * 0.5f + 40f);
                    rt.localScale = Vector3.one * k * (1f + 0.05f * Mathf.Sin(ct * 12f));
                    rt.localRotation = Quaternion.Euler(0, 0, 14f * Mathf.Sin(ct * 9f));
                    chr.color = Color.white;
                }
                else
                {   // monster slides in from the right edge, bobs with its thumbs up
                    float k = Kit.EaseOutBack(Mathf.Clamp01(ct / 0.3f));
                    rt.anchoredPosition = new Vector2(Mathf.LerpUnclamped(W * 0.5f + 240f, W * 0.5f - 190f, k), C.y - 330f + 10f * Mathf.Sin(ct * 8f));
                    rt.localRotation = Quaternion.Euler(0, 0, 4f * Mathf.Sin(ct * 5f));
                    chr.color = Color.white;
                }
            }
            // exit: everything shrinks, rises and fades
            if (t >= ExitAt)
            {
                float ek = Mathf.Clamp01((t - ExitAt) / (End - ExitAt));
                float a = 1f - Kit.EaseInOut(ek);
                foreach (var im in new[] { word, burst, chr })
                {
                    if (im == null) continue;
                    var c = im.color; c.a = Mathf.Min(c.a, a); im.color = c;
                }
                word.rectTransform.localScale *= 1f - 0.3f * ek;
                burst.rectTransform.localScale *= 1f - 0.4f * ek;
                root.anchoredPosition = new Vector2(0, 90f * Kit.EaseInOut(ek));
            }
            // bits physics
            for (int i = bits.Count - 1; i >= 0; i--)
            {
                var b = bits[i];
                if (b.t == null) { bits.RemoveAt(i); continue; }
                b.v.y -= 1500f * dt; b.v *= 1f / (1f + 1.2f * dt);
                b.t.anchoredPosition += b.v * dt;
                b.t.localRotation = Quaternion.Euler(0, 0, b.t.localEulerAngles.z + b.spin * dt);
                float age = t - Impact;
                var c = b.i.color; c.a = Mathf.Clamp01(1f - (age - b.life * 0.6f) / (b.life * 0.4f)); b.i.color = c;
                bits[i] = b;
                if (age > b.life) { Destroy(b.t.gameObject); bits.RemoveAt(i); }
            }
            yield return null;
        }
        if (root != null) Destroy(root.gameObject);
    }

    // old single-picture banner (used when the layered art is missing)
    IEnumerator ComboBannerFlat(int tier)
    {
        string key = "combo_" + tier;
        if (!GameArt.I.Has(key)) yield break;
        AudioManager.Play("Combo");
        if (activeCombo != null) Destroy(activeCombo.gameObject);   // a newer combo replaces the old one
        var img = UIKit.Image(toastRoot, key, new Vector2(0.5f, 0.5f), new Vector2(0, 80), new Vector2(760, 520));
        activeCombo = img;
        var t = img.rectTransform;
        Vector2 basePos = t.anchoredPosition;
        void Set(float a, float scale, float rise)
        {
            if (img == null) return;
            img.color = new Color(1f, 1f, 1f, ComboAlpha * a);
            t.localScale = Vector3.one * scale;
            t.anchoredPosition = basePos + new Vector2(0, rise);
        }
        Set(0f, 0.8f, 0f);
        yield return Kit.Tween(0.2f, k => Set(k, Mathf.Lerp(0.8f, 1f, k), 0f), Kit.EaseOutCubic);   // in
        yield return new WaitForSeconds(0.5f);                                                      // hold
        yield return Kit.Tween(0.25f, k => Set(1f - k, Mathf.Lerp(1f, 1.06f, k), 40f * k), Kit.EaseInOut); // out
        if (img != null) Destroy(img.gameObject);
    }

    void Toast(string text) { StartCoroutine(ToastRoutine(text)); }

    IEnumerator ToastRoutine(string text)
    {
        var bg = UIKit.Panel(toastRoot, new Vector2(0.5f, 0.5f), new Vector2(0, -200), new Vector2(Mathf.Max(420, text.Length * 26 + 80), 110), new Color(0.13f, 0.18f, 0.36f, 0.9f), 55);
        UIKit.Label(bg.rectTransform, text, 44, Color.white, new Vector2(0.5f, 0.5f), Vector2.zero, bg.rectTransform.sizeDelta);
        yield return UIKit.PopIn(bg.transform, 0.2f);
        yield return new WaitForSeconds(1.2f);
        yield return Kit.Tween(0.25f, k => bg.transform.localScale = Vector3.one * (1f - k));
        Destroy(bg.gameObject);
    }

    IEnumerator Punch(Transform t)
    {
        if (t == null) yield break;
        yield return Kit.Tween(0.25f, k => { if (t != null) t.localScale = Vector3.one * (1f + 0.25f * Mathf.Sin(k * Mathf.PI)); });
    }

    IEnumerator PunchText(Text t) { if (t != null) yield return Punch(t.transform); }

    // armed power-up: glow breathes, icon lifts and sways, sparkles circle and twinkle; everything eases in/out
    void Update()
    {
        float tm = Time.time, dt = Time.deltaTime;
        if (hudChest != null)
        {
            if (Time.frameCount % 15 == 0) hudChest.gameObject.SetActive(Progress.PendingChest > 0 && chestRoot == null && map == null);
            if (hudChest.gameObject.activeSelf)
            {
                float beat = Mathf.Pow(Mathf.Abs(Mathf.Sin(Time.unscaledTime * 2.6f)), 3f);
                hudChest.localScale = Vector3.one * (1f + 0.16f * beat);
                hudChest.localRotation = Quaternion.Euler(0, 0, 6f * Mathf.Sin(Time.unscaledTime * 1.7f));
            }
        }
        foreach (var kv in puButtons)
        {
            var b = kv.Value;
            if (b.glow == null) continue;
            bool armed = board != null && board.Armed == kv.Key;
            float before = b.arm;
            b.arm = Mathf.MoveTowards(b.arm, armed ? 1f : 0f, dt * 4f);
            if (b.arm <= 0f && before <= 0f) continue;       // idle: leave the button alone (the "not enough" shake uses it)
            float e = Kit.EaseOutCubic(b.arm);
            float breath = Mathf.Sin(tm * 3f);
            b.glow.color = new Color(ArmGold.r, ArmGold.g, ArmGold.b, e * (0.95f + 0.05f * breath));
            float gs = (0.85f + 0.15f * e) * (1f + 0.07f * breath);
            b.glow.rectTransform.localScale = new Vector3(gs, gs, 1f);
            b.root.anchoredPosition = b.basePos + new Vector2(0, e * (20f + 5f * Mathf.Sin(tm * 4f)));
            b.root.localRotation = Quaternion.Euler(0, 0, e * 6f * Mathf.Sin(tm * 2.6f));
            b.sparkRing.anchoredPosition = b.basePos + new Vector2(0, 14f + 20f * e);
            b.sparkRing.localRotation = Quaternion.Euler(0, 0, -tm * 45f);
            float rs = 0.7f + 0.3f * e;
            b.sparkRing.localScale = new Vector3(rs, rs, 1f);
            for (int s = 0; s < b.sparks.Length; s++)
            {
                float tw = 0.5f + 0.5f * Mathf.Sin(tm * 6f + s * 1.9f);
                b.sparks[s].color = new Color(ArmGold.r, ArmGold.g, ArmGold.b, e * (0.45f + 0.55f * tw));
                float ss = 0.65f + 0.45f * tw;
                b.sparks[s].rectTransform.localScale = new Vector3(ss, ss, 1f);
                b.sparks[s].rectTransform.localRotation = Quaternion.Euler(0, 0, tm * 45f + tw * 20f);   // cancel the ring spin, add a little twist
            }
        }
    }

    IEnumerator Shake(Transform t)
    {
        Vector3 p = t.localPosition;
        yield return Kit.Tween(0.3f, k => t.localPosition = p + new Vector3(Mathf.Sin(k * 40f) * 12f * (1 - k), 0, 0));
        t.localPosition = p;
    }
}
