using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

// The 2D match-3 board. Rules ported from Marbly's BoardManager / BoardInputController:
//   * swap two neighbours by dragging; a swap that makes no run swaps back (no move spent)
//   * runs of 3+; 4 in a line -> Line cat (cross), L/T -> Bomb cat, 5 in a line -> Rainbow cat
//   * specials are colourless: they fire when swiped (Line: row or column by swipe direction,
//     Bomb: 3x3, Rainbow: every cat of the partner's colour), tapped, or caught in a blast;
//     two specials swapped together make a bigger combo
//   * cascades with a growing multiplier, idle hint, auto-shuffle when no moves are left
// Kuzya blockers: Box (static, 2 hits), Yarn (moves; freed when its run matches, cat stays),
// Catnip (static, 1 hit, spreads if ignored), Vase (static obstacle, 1 hit), Fish bowl
// (falls; collected at the bottom row). "Hit" = a match right next to it or any blast.
public class Board : MonoBehaviour
{
    public int cols = LevelGen.Cols, rows = LevelGen.Rows;
    public float cell = 1f;
    public Vector2 origin;               // world position of the bottom-left corner

    Piece[,] grid;
    bool[,] live;                        // board shape: false = no cell (nothing drawn, nothing falls into it)
    LevelDef def;
    System.Random rng;
    Transform piecesRoot, tilesRoot;

    public bool Busy { get; private set; }
    public string DebugPhase = "";   // last step a move went through (for diagnosing a stuck board)
    public bool InputLocked;             // set by the game (popups, level end)
    public PowerUpType? Armed { get; private set; }

    // ---- events for the game controller (score / goals / HUD) ----
    public event Action<int, List<int>> CascadeStep;          // step (1..), run lengths
    public event Action<int> SpecialCleared;                   // extra cats cleared by specials
    public event Action<int> PowerUpCleared;
    public event Action<int> Bonus;                            // flat bonus points
    public event Action<int, Vector3> CatCleared;              // colour, world pos
    public event Action<GoalType, Vector3> GoalHit;            // Box/Yarn/Mint/Vase/Fish progress
    public event Action MoveCommitted;
    public event Action<int> MoveResolved;                     // cats cleared by the whole move
    public event Action<PowerUpType> PowerUpUsed;
    public event Action<Vector3> MilkCoin;                     // a cat cleared in a milk puddle: +1 coin
    // Hunter targeting: how much the level wants this piece gone (goal blockers / goal colour score high). Set by GameController.
    public Func<Piece, int> TargetPriority;
    public event Action<string> Toast;

    int clearedThisMove;
    int mintCounter;
    bool mintHitThisMove;
    int fishToSpawn;
    int fishDelivered;
    // soap foam lying under the cats: layers per cell (0 = none, 1 = thin, 2 = thick). It belongs to the
    // cell, not the cat: whenever the piece standing on it is cleared (match, special, booster), one layer pops.
    int[,] foam;
    SpriteRenderer[,] foamSr;
    public int FoamAt(int x, int y) => foam != null && In(x, y) ? foam[x, y] : 0;
    // milk puddles: moves left before the puddle dries (0 = no milk)
    int[,] milk;
    SpriteRenderer[,] milkSr;
    const int MilkLife = 6;
    static bool Hittable(Piece n) => n.kind == PieceKind.Vase || n.kind == PieceKind.Milk || n.cover == Cover.Box || n.cover == Cover.Mint;
    const int MintSpreadEvery = 3;
    // mouse thief: rides on a cat, hops to a neighbour after every move and every few moves steals a coin
    // from the balance into its sack. Clearing the cat it sits on catches it: the coins come back doubled.
    public Func<Vector3?> MouseSteal;                         // game takes one coin; returns where the coin flies from (null = none to take)
    public event Action<Vector3, int> MouseCaught;             // world pos, coins in the sack
    const int MouseStealEvery = 3, MouseMaxCoins = 5;
    Piece mouseOn;
    Transform mouseT, mouseBody;
    SpriteRenderer mouseSr;
    readonly List<SpriteRenderer> mousePile = new List<SpriteRenderer>();
    int mouseCoins, mouseMoves, mouseFace = 1;
    bool mouseHopping;
    public bool MouseActive => mouseT != null && mouseOn != null;
    public int MouseCoins => mouseCoins;

    // ======================================================= setup
    public void Setup(LevelDef level, Rect worldRect)
    {
        StopAllCoroutines();
        def = level;
        rng = new System.Random(level.level * 7349 + 3 + (int)(Time.realtimeSinceStartup * 1000) % 1000);
        cell = Mathf.Min(worldRect.width / cols, worldRect.height / rows);
        origin = new Vector2(worldRect.center.x - cell * cols * 0.5f, worldRect.center.y - cell * rows * 0.5f);

        foreach (Transform c in transform) Destroy(c.gameObject);
        tilesRoot = new GameObject("Tiles").transform; tilesRoot.SetParent(transform, false);
        piecesRoot = new GameObject("Pieces").transform; piecesRoot.SetParent(transform, false);
        grid = new Piece[cols, rows];
        live = level.mask ?? FullMask();
        Armed = null; Busy = false; mintCounter = 0; fishToSpawn = 0; fishDelivered = 0;
        foam = new int[cols, rows]; foamSr = new SpriteRenderer[cols, rows];
        milk = new int[cols, rows]; milkSr = new SpriteRenderer[cols, rows];
        mouseOn = null; mouseT = null; mouseBody = null; mouseSr = null; mousePile.Clear(); mouseCoins = 0; mouseMoves = 0; mouseHopping = false;

        BuildTiles();
        FillInitial();
        StartCoroutine(IntroDrop());
    }

    bool[,] FullMask() { var m = new bool[cols, rows]; for (int x = 0; x < cols; x++) for (int y = 0; y < rows; y++) m[x, y] = true; return m; }
    public bool Live(int x, int y) => In(x, y) && live[x, y];
    // a cell nothing can fall through: outside the shape, or a static piece (vase, box, catnip)
    bool Blocked(int x, int y) => !live[x, y] || (grid[x, y] != null && grid[x, y].IsStatic);
    // true when every cell above (x, y) is outside the shape: new cats can drop in from the top
    bool OpenAbove(int x, int y) { for (int yy = y + 1; yy < rows; yy++) if (live[x, yy]) return false; return true; }
    int LowestLive(int x) { for (int y = 0; y < rows; y++) if (live[x, y]) return y; return -1; }
    int HighestLive(int x) { for (int y = rows - 1; y >= 0; y--) if (live[x, y]) return y; return -1; }

    void BuildTiles()
    {
        // soft backing that follows the board shape: one slightly larger rounded square per cell
        for (int x = 0; x < cols; x++)
            for (int y = 0; y < rows; y++)
            {
                if (!live[x, y]) continue;
                var f = new GameObject("frame").AddComponent<SpriteRenderer>();
                f.transform.SetParent(tilesRoot, false);
                f.sprite = Kit.RoundedRect(128, 36);
                f.drawMode = SpriteDrawMode.Sliced;
                f.size = new Vector2(cell * 1.12f, cell * 1.12f);
                f.transform.position = CellPos(x, y);
                f.color = new Color(1f, 1f, 1f, 0.28f);
                f.sortingOrder = 1;
            }

        bool customTile = GameArt.I.Has("cell_tile");
        for (int x = 0; x < cols; x++)
            for (int y = 0; y < rows; y++)
            {
                if (!live[x, y]) continue;
                var t = new GameObject("tile").AddComponent<SpriteRenderer>();
                t.transform.SetParent(tilesRoot, false);
                t.transform.position = CellPos(x, y);
                if (customTile) { Kit.FitSprite(t, "cell_tile", cell, 0.97f); t.transform.position = CellPos(x, y); t.color = ((x + y) % 2 == 0) ? new Color(1f, 1f, 1f, 0.92f) : new Color(0.9f, 0.9f, 0.96f, 0.92f); }
                else
                {
                    t.sprite = Kit.RoundedRect(128, 30);
                    t.drawMode = SpriteDrawMode.Sliced;
                    t.size = new Vector2(cell * 0.94f, cell * 0.94f);
                    t.color = ((x + y) % 2 == 0) ? new Color(1f, 1f, 1f, 0.42f) : new Color(1f, 1f, 1f, 0.26f);
                }
                t.sortingOrder = 2;
            }

        if (def.fish > 0)
            for (int x = 0; x < cols; x++)
            {
                int ly = LowestLive(x);
                if (ly < 0) continue;
                var holder = new GameObject("exit").transform;
                holder.SetParent(tilesRoot, false);
                holder.position = CellPos(x, ly) + new Vector3(0, -cell * MouseSize, 0);
                var img = new GameObject("img").AddComponent<SpriteRenderer>();
                img.transform.SetParent(holder, false);
                Kit.FitSprite(img, "bowl_exit", cell * 0.5f, 1f);
                img.sortingOrder = 3;
            }
    }

    // How many of each blocker actually made it onto the board (goals use these).
    public int Count(GoalType t)
    {
        int n = 0;
        if (t == GoalType.Foam) { foreach (var f in foam) if (f > 0) n++; return n; }
        foreach (var p in grid)
        {
            if (p == null) continue;
            if (t == GoalType.Vase && p.kind == PieceKind.Vase) n++;
            else if (t == GoalType.Box && p.cover == Cover.Box) n++;
            else if (t == GoalType.Yarn && p.cover == Cover.Yarn) n++;
            else if (t == GoalType.Mint && p.cover == Cover.Mint) n++;
        }
        return n;
    }

    public Vector3 CellPos(int x, int y) => new Vector3(origin.x + (x + 0.5f) * cell, origin.y + (y + 0.5f) * cell, 0);
    bool In(int x, int y) => x >= 0 && y >= 0 && x < cols && y < rows;
    Piece At(int x, int y) => In(x, y) ? grid[x, y] : null;
    public Rect WorldRect => new Rect(origin.x, origin.y, cols * cell, rows * cell);
    public float Cell => cell;
    // world positions of every playable cell, shuffled (for decorative FX that land on the board)
    public List<Vector3> LiveCellPositions()
    {
        var l = new List<Vector3>();
        for (int x = 0; x < cols; x++) for (int y = 0; y < rows; y++) if (Live(x, y)) l.Add(CellPos(x, y));
        for (int i = l.Count - 1; i > 0; i--) { int j = UnityEngine.Random.Range(0, i + 1); var t = l[i]; l[i] = l[j]; l[j] = t; }
        return l;
    }

    Piece NewPiece(PieceKind kind, int color, int x, int y)
    {
        var p = Piece.Create(piecesRoot, cell, kind + "_" + x + "_" + y);
        p.kind = kind; p.color = color; p.x = x; p.y = y;
        p.transform.position = CellPos(x, y);
        p.Refresh();
        return p;
    }

    void Put(Piece p, int x, int y) { grid[x, y] = p; if (p != null) { p.x = x; p.y = y; } }

    void FillInitial()
    {
        // 1) cats avoiding ready-made runs
        for (int y = 0; y < rows; y++)
            for (int x = 0; x < cols; x++)
            {
                if (!live[x, y]) continue;
                int c; int guard = 0;
                int Col(int xx, int yy) => grid[xx, yy] != null ? grid[xx, yy].color : -1;
                do { c = rng.Next(LevelGen.Colors); guard++; }
                while (guard < 20 && ((x >= 2 && Col(x - 1, y) == c && Col(x - 2, y) == c) ||
                                      (y >= 2 && Col(x, y - 1) == c && Col(x, y - 2) == c) ||
                                      (x >= 1 && y >= 1 && Col(x - 1, y) == c && Col(x, y - 1) == c && Col(x - 1, y - 1) == c)));
                Put(NewPiece(PieceKind.Cat, c, x, y), x, y);
            }

        // 2) blockers on deterministic-but-varied cells
        var cells = new List<Vector2Int>();
        for (int x = 0; x < cols; x++) for (int y = 0; y < rows; y++) cells.Add(new Vector2Int(x, y));
        Shuffle(cells);
        int vi = 0;
        Vector2Int Take(Func<Vector2Int, bool> ok)
        {
            for (int i = 0; i < cells.Count; i++)
            {
                var c = cells[(vi + i) % cells.Count];
                if (ok(c) && grid[c.x, c.y] != null && grid[c.x, c.y].PlainCat) { vi = (vi + i + 1) % cells.Count; return c; }
            }
            return new Vector2Int(-1, -1);
        }

        for (int i = 0; i < def.vases; i++)
        {
            var c = Take(v => v.y > 0 && v.y < rows - 1);
            if (c.x < 0) break;
            Destroy(grid[c.x, c.y].gameObject);
            Put(NewPiece(PieceKind.Vase, -1, c.x, c.y), c.x, c.y);
        }
        for (int i = 0; i < def.milk; i++)
        {
            var c = Take(v => v.y > 0 && v.y < rows - 1 && v.x > 0 && v.x < cols - 1);
            if (c.x < 0) break;
            Destroy(grid[c.x, c.y].gameObject);
            Put(NewPiece(PieceKind.Milk, -1, c.x, c.y), c.x, c.y);
        }
        for (int i = 0; i < def.boxes; i++)
        {
            var c = Take(v => v.y < rows - 1);
            if (c.x < 0) break;
            var p = grid[c.x, c.y]; p.cover = Cover.Box; p.coverHp = 2; p.Refresh();
        }
        for (int i = 0; i < def.yarns; i++)
        {
            var c = Take(v => true);
            if (c.x < 0) break;
            var p = grid[c.x, c.y]; p.cover = Cover.Yarn; p.Refresh();
        }
        for (int i = 0; i < def.mints; i++)
        {
            var c = Take(v => v.y > 0 && v.y < rows - 2);
            if (c.x < 0) break;
            var p = grid[c.x, c.y]; p.cover = Cover.Mint; p.Refresh();
        }
        // fish: first one on the top row, the rest arrive from the top as each is delivered
        if (def.fish > 0)
        {
            // prefer a column with a clear run to the bottom (no holes, no boxes / vases under the fish)
            int fx = rng.Next(cols);
            for (int pass = 0; pass < 2; pass++)
            {
                bool placed = false;
                for (int k = 0; k < cols; k++)
                {
                    int x = (fx + k) % cols, ty = HighestLive(x);
                    if (ty < 1 || !OpenAbove(x, ty) || grid[x, ty] == null || !grid[x, ty].PlainCat) continue;
                    if (pass == 0 && !ClearColumn(x, ty)) continue;
                    Destroy(grid[x, ty].gameObject); Put(NewPiece(PieceKind.Fish, -1, x, ty), x, ty); placed = true; break;
                }
                if (placed) break;
            }
            fishToSpawn = def.fish - 1;
        }
        PlaceFoam();
        EnsureMovesExist(false);
        if (def.mouse) PlaceMouse();
    }

    void Shuffle<T>(List<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); var t = list[i]; list[i] = list[j]; list[j] = t; }
    }

    IEnumerator IntroDrop()
    {
        Busy = true;
        var moves = new List<IEnumerator>();
        for (int x = 0; x < cols; x++)
            for (int y = 0; y < rows; y++)
            {
                var p = grid[x, y];
                if (p == null) continue;
                Vector3 to = CellPos(x, y);
                p.transform.position = to + new Vector3(0, cell * (rows + 1), 0);
                StartCoroutine(MoveTo(p, to, 0.35f + y * 0.03f + x * 0.015f, true));
            }
        yield return new WaitForSeconds(0.75f);
        Busy = false;
    }

    // ======================================================= input
    Vector2Int downCell = new Vector2Int(-1, -1);
    Vector3 downWorld;
    bool pressing;
    float idleTimer;
    bool hintShown;
    readonly List<RaycastResult> uiHits = new List<RaycastResult>();

    bool OverUI(Vector2 screen)
    {
        if (EventSystem.current == null) return false;
        var ev = new PointerEventData(EventSystem.current) { position = screen };
        uiHits.Clear();
        EventSystem.current.RaycastAll(ev, uiHits);
        return uiHits.Count > 0;
    }

    Vector2Int WorldToCell(Vector3 w)
    {
        int x = Mathf.FloorToInt((w.x - origin.x) / cell), y = Mathf.FloorToInt((w.y - origin.y) / cell);
        return Live(x, y) ? new Vector2Int(x, y) : new Vector2Int(-1, -1);
    }

    Vector3 ScreenToWorld(Vector2 s)
    {
        var cam = Camera.main;
        var w = cam.ScreenToWorldPoint(new Vector3(s.x, s.y, -cam.transform.position.z));
        w.z = 0; return w;
    }

    void Update()
    {
        if (grid == null) return;
        var ptr = Pointer.current;
        bool canAct = !Busy && !InputLocked;

        if (canAct)
        {
            idleTimer += Time.deltaTime;
            if (!hintShown && idleTimer > 6f && Armed == null) { hintShown = true; ShowHint(); }
        }
        if (ptr == null) return;

        if (ptr.press.wasPressedThisFrame)
        {
            Vector2 sp = ptr.position.ReadValue();
            if (!canAct || OverUI(sp)) { pressing = false; return; }
            var w = ScreenToWorld(sp);
            var c = WorldToCell(w);
            if (c.x < 0) { pressing = false; return; }
            downCell = c; downWorld = w; pressing = true;
            ResetIdle();
        }
        else if (pressing && ptr.press.isPressed)
        {
            if (!canAct) { pressing = false; return; }
            var w = ScreenToWorld(ptr.position.ReadValue());
            Vector3 d = w - downWorld;
            if (Armed == null && d.magnitude > cell * 0.35f)
            {
                pressing = false;
                Vector2Int dir = Mathf.Abs(d.x) > Mathf.Abs(d.y) ? new Vector2Int(d.x > 0 ? 1 : -1, 0) : new Vector2Int(0, d.y > 0 ? 1 : -1);
                StartCoroutine(TrySwap(downCell, dir));
            }
        }
        else if (pressing && ptr.press.wasReleasedThisFrame)
        {
            pressing = false;
            if (canAct) StartCoroutine(Tap(downCell));
        }
    }

    void ResetIdle() { idleTimer = 0; hintShown = false; }

    public void ArmPowerUp(PowerUpType? t) { Armed = t; ResetIdle(); }

    // ======================================================= player actions
    IEnumerator Tap(Vector2Int c)
    {
        var p = At(c.x, c.y);
        if (p == null) yield break;
        if (Armed != null)
        {
            var t = Armed.Value;
            if (!PowerUpTargetOk(t, p)) { StartCoroutine(p.Wiggle()); yield break; }
            Armed = null;
            yield return UsePowerUp(t, p);
            yield break;
        }
        if (p.special != Special.None && p.Movable)
        {
            Busy = true; BeginMove(); quietPops = true;
            var set = new Ctx();
            if (p.special == Special.Line) p.lineRow = true;
            Trigger(p, set, true);
            yield return Execute(set, null);
            yield return Gravity();
            yield return ResolveLoop(null);
            yield return EndMove();
            yield break;
        }
        StartCoroutine(p.Wiggle());
    }

    IEnumerator TrySwap(Vector2Int a, Vector2Int dir)
    {
        var b = a + dir;
        var pa = At(a.x, a.y);
        var pb = At(b.x, b.y);
        if (pa == null) yield break;
        if (!pa.Movable || pb == null || !pb.Movable) { StartCoroutine(pa.Wiggle()); if (pb != null && !pb.Movable) StartCoroutine(pb.Wiggle()); yield break; }

        Busy = true;
        // a swipe that will make a match gets its "whoosh" right away (one of three takes)
        bool withSpecial = pa.special != Special.None || pb.special != Special.None;
        if (!withSpecial && SwapMakesRun(a.x, a.y, b.x, b.y)) AudioManager.Play("swap_" + UnityEngine.Random.Range(1, 4), 0.9f);
        DebugPhase = "swapAnim";
        yield return SwapAnim(pa, pb);
        Put(pa, b.x, b.y); Put(pb, a.x, a.y);

        bool aSp = pa.special != Special.None, bSp = pb.special != Special.None;
        if (aSp || bSp)
        {
            BeginMove(); quietPops = true;
            var ctx = new Ctx();
            if (aSp && bSp) Combo(pa, pb, ctx);
            else
            {
                var s = aSp ? pa : pb; var other = aSp ? pb : pa;
                if (s.special == Special.Line) s.lineRow = dir.x != 0;
                if (s.special == Special.Rainbow) { ctx.clear.Add(s); ctx.triggered.Add(s); ctx.Mark(s, FX.RainbowCharge); RainbowColor(other.MatchColor >= 0 ? other.MatchColor : MostCommonColor(), ctx, s.x, s.y, 0f); }
                else Trigger(s, ctx, false);
            }
            DebugPhase = "special.execute"; yield return Execute(ctx, null);
            DebugPhase = "special.gravity"; yield return Gravity();
            DebugPhase = "special.resolve"; yield return ResolveLoop(null);
            DebugPhase = "special.end"; yield return EndMove();
            yield break;
        }

        if (FindRuns().Count == 0)
        {
            AudioManager.Play("nope", 0.9f);
            pa.SetAngry(true); pb.SetAngry(true);
            yield return SwapAnim(pa, pb);
            Put(pa, a.x, a.y); Put(pb, b.x, b.y);
            pa.SetAngry(false); pb.SetAngry(false);
            Busy = false;
            yield break;
        }

        BeginMove();
        DebugPhase = "resolve"; yield return ResolveLoop(new HashSet<Vector2Int> { a, b });
        DebugPhase = "end"; yield return EndMove();
    }

    void BeginMove()
    {
        clearedThisMove = 0; mintHitThisMove = false;
        MoveCommitted?.Invoke();
        ResetIdle();
    }

    // While a special cat or a booster is going off, the ordinary match sounds (the meows) stay quiet:
    // the special / booster has its own sound and the cascade it causes shouldn't meow over it.
    bool quietPops;

    IEnumerator EndMove()
    {
        DebugPhase = "mint"; yield return MintTick();
        DryMilk();
        if (MouseActive) MouseTurn();
        DebugPhase = "shuffle"; if (!HasPossibleMove()) yield return ShuffleBoard();
        DebugPhase = "resolved"; MoveResolved?.Invoke(clearedThisMove);
        Busy = false; quietPops = false;
        ResetIdle();
    }

    IEnumerator SwapAnim(Piece a, Piece b)
    {
        Vector3 pa = a.transform.position, pb = b.transform.position;
        a.SetOrder(20); b.SetOrder(18);
        yield return Kit.Tween(0.16f, k =>
        {
            a.transform.position = Vector3.Lerp(pa, pb, k);
            b.transform.position = Vector3.Lerp(pb, pa, k);
        }, Kit.EaseInOut);
        a.SetOrder(10); b.SetOrder(10);
    }

    // ======================================================= power-ups
    bool PowerUpTargetOk(PowerUpType t, Piece p)
    {
        if (p.kind == PieceKind.Fish) return t == PowerUpType.MegaBomb;
        if (t == PowerUpType.ColorBlast) return p.kind == PieceKind.Cat && p.special == Special.None;
        return true;
    }

    IEnumerator UsePowerUp(PowerUpType t, Piece p)
    {
        Busy = true; quietPops = true;
        clearedThisMove = 0;
        var ctx = new Ctx { byPowerUp = true };
        Vector3 pos = CellPos(p.x, p.y);
        switch (t)
        {
            case PowerUpType.Paw:
                yield return FX.PawSlam(pos, cell);
                AudioManager.Play("hummer");
                if (p.kind == PieceKind.Vase || p.kind == PieceKind.Milk) ctx.hits.Add(p);
                else { if (p.cover != Cover.None) { ReleaseCover(p, pos); } ctx.clear.Add(p); if (p.special != Special.None) Trigger(p, ctx, true); }
                break;
            case PowerUpType.MegaBomb:
                yield return FX.BombDrop(pos, cell);
                AudioManager.Play("explosion");
                BombShoot(p.x, p.y, 2, 0f, ctx);
                break;
            case PowerUpType.ColorBlast:
                yield return FX.WandCast(pos, cell);
                AudioManager.Play("rainbow");
                RainbowColor(p.color, ctx, p.x, p.y, 0f);
                break;
        }
        PowerUpUsed?.Invoke(t);
        yield return Execute(ctx, null);
        yield return Gravity();
        yield return ResolveLoop(null);
        if (!HasPossibleMove()) yield return ShuffleBoard();
        MoveResolved?.Invoke(clearedThisMove);
        Busy = false; quietPops = false;
    }

    // ======================================================= matching
    class Run { public List<Piece> cells = new List<Piece>(); public bool horizontal; public bool square; public int Color => cells[0].MatchColor; }

    List<Run> FindRuns()
    {
        var runs = new List<Run>();
        for (int y = 0; y < rows; y++)
        {
            int start = 0;
            for (int x = 1; x <= cols; x++)
            {
                bool same = x < cols && grid[x, y] != null && grid[x - 1, y] != null && grid[x, y].MatchColor >= 0 && grid[x, y].MatchColor == grid[x - 1, y].MatchColor;
                if (!same)
                {
                    if (x - start >= 3 && grid[start, y] != null && grid[start, y].MatchColor >= 0)
                    {
                        var r = new Run { horizontal = true };
                        for (int k = start; k < x; k++) r.cells.Add(grid[k, y]);
                        runs.Add(r);
                    }
                    start = x;
                }
            }
        }
        for (int x = 0; x < cols; x++)
        {
            int start = 0;
            for (int y = 1; y <= rows; y++)
            {
                bool same = y < rows && grid[x, y] != null && grid[x, y - 1] != null && grid[x, y].MatchColor >= 0 && grid[x, y].MatchColor == grid[x, y - 1].MatchColor;
                if (!same)
                {
                    if (y - start >= 3 && grid[x, start] != null && grid[x, start].MatchColor >= 0)
                    {
                        var r = new Run { horizontal = false };
                        for (int k = start; k < y; k++) r.cells.Add(grid[x, k]);
                        runs.Add(r);
                    }
                    start = y;
                }
            }
        }
        // 2x2 squares of one colour (-> Hunter)
        for (int x = 0; x + 1 < cols; x++)
            for (int y = 0; y + 1 < rows; y++)
            {
                var p = grid[x, y];
                if (p == null) continue;
                int c = p.MatchColor;
                if (c < 0) continue;
                var q1 = grid[x + 1, y]; var q2 = grid[x, y + 1]; var q3 = grid[x + 1, y + 1];
                if (q1 == null || q2 == null || q3 == null || q1.MatchColor != c || q2.MatchColor != c || q3.MatchColor != c) continue;
                var r = new Run { square = true };
                r.cells.Add(p); r.cells.Add(q1); r.cells.Add(q2); r.cells.Add(q3);
                runs.Add(r);
            }
        return runs;
    }

    struct Spawn { public int x, y; public Special type; public bool lineRow; }

    class Ctx
    {
        public HashSet<Piece> clear = new HashSet<Piece>();
        public HashSet<Piece> hits = new HashSet<Piece>();      // covers / vases to damage once
        public HashSet<Piece> yarn = new HashSet<Piece>();      // yarn to release (cat stays)
        public HashSet<Piece> seed = new HashSet<Piece>();      // cats from the matched runs themselves
        public Queue<Piece> queue = new Queue<Piece>();
        public HashSet<Piece> triggered = new HashSet<Piece>();
        public bool byPowerUp;
        // Line blasts: when (seconds after the pop phase starts) each piece is reached
        public Dictionary<Piece, float> when = new Dictionary<Piece, float>();
        public List<LineShot> lines = new List<LineShot>();
        public List<LineShot> bombs = new List<LineShot>();     // x,y centre; row unused; t = start
        public List<RainbowShot> rainbows = new List<RainbowShot>();
        public List<HunterShot> hunters = new List<HunterShot>();
        public Dictionary<Piece, Vector2Int> hitFrom = new Dictionary<Piece, Vector2Int>();   // milk buckets: where the hit came from
        public float When(Piece p) => when.TryGetValue(p, out var t) ? t : 0f;
        public void Mark(Piece p, float t) { if (t > 0f && (!when.TryGetValue(p, out var o) || t < o)) when[p] = t; }
    }

    struct LineShot { public int x, y; public bool row; public float t; public int r; }
    struct HunterShot { public int x, y, tx, ty; public float t; }

    // The Hunter picks the piece the level most wants gone and shoots it (optionally carrying a Line / Bomb there).
    void HunterShoot(int ox, int oy, float t0, Ctx ctx, Special carry)
    {
        Piece best = null; int bestScore = int.MinValue; int ties = 0;
        foreach (var p in grid)
        {
            if (p == null || p.dead || p.kind == PieceKind.Fish) continue;
            if (p.x == ox && p.y == oy) continue;
            if (ctx.clear.Contains(p) || ctx.hits.Contains(p) || ctx.yarn.Contains(p)) continue;
            int sc = TargetPriority != null ? TargetPriority(p) : (p.kind == PieceKind.Vase || p.cover != Cover.None ? 3 : 1);
            if (sc > bestScore) { best = p; bestScore = sc; ties = 1; }
            else if (sc == bestScore && rng.Next(++ties) == 0) best = p;
        }
        if (best == null) return;
        float arrive = FX.HunterCharge + FX.HunterFly(Vector2.Distance(new Vector2(ox, oy), new Vector2(best.x, best.y)));
        ctx.hunters.Add(new HunterShot { x = ox, y = oy, tx = best.x, ty = best.y, t = t0 });
        float hit = t0 + arrive;
        if (carry == Special.Line) LineShoot(best.x, best.y, rng.Next(2) == 0, hit, ctx);
        else if (carry == Special.Bomb) BombShoot(best.x, best.y, 1, hit, ctx);
        else Affect(best.x, best.y, ctx, hit);
    }

    // horn blast: the four neighbours
    void HunterHorn(int x, int y, float t0, Ctx ctx)
    {
        float t = t0 + FX.HunterHornHit;
        Affect(x + 1, y, ctx, t); Affect(x - 1, y, ctx, t); Affect(x, y + 1, ctx, t); Affect(x, y - 1, ctx, t);
    }

    class RainbowShot { public int x, y; public float t; public bool orbs = true; public List<Vector2Int> cells = new List<Vector2Int>(); public List<float> arrive = new List<float>(); }

    // Send a rainbow orb from (ox,oy) to every target (nearest first); each target is reached at its arrival time.
    RainbowShot RainbowShoot(int ox, int oy, float t0, List<Piece> targets, Ctx ctx, bool affect)
    {
        var shot = new RainbowShot { x = ox, y = oy, t = t0 };
        var o = new Vector2(ox, oy);
        targets.Sort((a, b) => (new Vector2(a.x, a.y) - o).sqrMagnitude.CompareTo((new Vector2(b.x, b.y) - o).sqrMagnitude));
        for (int i = 0; i < targets.Count; i++)
        {
            var p = targets[i];
            float at = FX.RainbowArrive(i, Vector2.Distance(new Vector2(p.x, p.y), o));
            shot.cells.Add(new Vector2Int(p.x, p.y)); shot.arrive.Add(at);
            if (affect) Affect(p.x, p.y, ctx, t0 + at); else ctx.Mark(p, t0 + at);
        }
        ctx.rainbows.Add(shot);
        return shot;
    }

    // Blow up the (2r+1)x(2r+1) square around (x,y); rings pop outward from the centre.
    void BombShoot(int x, int y, int r, float t0, Ctx ctx)
    {
        ctx.bombs.Add(new LineShot { x = x, y = y, r = r, t = t0 });
        for (int dx = -r; dx <= r; dx++)
            for (int dy = -r; dy <= r; dy++)
                Affect(x + dx, y + dy, ctx, t0 + FX.BombHitTime(Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy))));
    }

    // Fire one line beam from (x,y) and schedule every cell it crosses.
    void LineShoot(int x, int y, bool row, float t0, Ctx ctx)
    {
        if (row ? (y < 0 || y >= rows) : (x < 0 || x >= cols)) return;
        ctx.lines.Add(new LineShot { x = x, y = y, row = row, t = t0 });
        if (row) for (int i = 0; i < cols; i++) Affect(i, y, ctx, t0 + FX.LineHitTime(Mathf.Abs(i - x)));
        else for (int j = 0; j < rows; j++) Affect(x, j, ctx, t0 + FX.LineHitTime(Mathf.Abs(j - y)));
    }

    IEnumerator ResolveLoop(HashSet<Vector2Int> preferred)
    {
        int step = 0;
        while (true)
        {
            var runs = FindRuns();
            if (runs.Count == 0) break;
            step++;
            var lens = new List<int>();
            foreach (var r in runs) { int n = 0; foreach (var p in r.cells) if (p.cover != Cover.Yarn) n++; lens.Add(Mathf.Max(n, 2)); }
            CascadeStep?.Invoke(step, lens);

            var ctx = new Ctx();
            var spawns = PlanSpawns(runs, preferred);
            foreach (var r in runs)
                foreach (var p in r.cells)
                {
                    if (p.cover == Cover.Yarn) ctx.yarn.Add(p);
                    else { ctx.clear.Add(p); ctx.seed.Add(p); }
                    foreach (var n in Neighbours(p.x, p.y))
                        if (Hittable(n))
                        {
                            ctx.hits.Add(n);
                            if (n.kind == PieceKind.Milk && !ctx.hitFrom.ContainsKey(n)) ctx.hitFrom[n] = new Vector2Int(p.x, p.y);   // it tips away from the match
                        }
                }
            if (!quietPops) AudioManager.Play("Pop", 0.9f);      // the combo sound belongs to the combo banner
            DebugPhase = "loop.execute " + step; yield return Execute(ctx, spawns);
            DebugPhase = "loop.gravity " + step; yield return Gravity();
            preferred = null;
        }
    }

    List<Spawn> PlanSpawns(List<Run> runs, HashSet<Vector2Int> preferred)
    {
        var spawns = new List<Spawn>();
        var used = new HashSet<Run>();
        foreach (var h in runs)
        {
            if (h.square || !h.horizontal || used.Contains(h)) continue;
            foreach (var v in runs)
            {
                if (v.square || v.horizontal || used.Contains(v) || v.Color != h.Color) continue;
                Piece shared = null;
                foreach (var p in h.cells) if (v.cells.Contains(p)) { shared = p; break; }
                if (shared == null) continue;
                used.Add(h); used.Add(v);
                spawns.Add(new Spawn { x = shared.x, y = shared.y, type = Special.Bomb });
                break;
            }
        }
        foreach (var r in runs)
        {
            if (r.square || used.Contains(r) || r.cells.Count < 4) continue;
            Piece at = null;
            if (preferred != null) foreach (var p in r.cells) if (preferred.Contains(new Vector2Int(p.x, p.y)) && p.cover != Cover.Yarn) { at = p; break; }
            if (at == null) foreach (var p in r.cells) if (p.cover != Cover.Yarn) { at = p; if (p == r.cells[r.cells.Count / 2]) break; }
            if (at == null) continue;
            spawns.Add(new Spawn { x = at.x, y = at.y, type = r.cells.Count >= 5 ? Special.Rainbow : Special.Line, lineRow = r.horizontal });
            used.Add(r);
        }
        // squares -> Hunter, unless a bigger shape already claimed one of its cats
        var claimed = new HashSet<Piece>();
        foreach (var r in used) foreach (var p in r.cells) claimed.Add(p);
        foreach (var r in runs)
        {
            if (!r.square) continue;
            bool taken = false;
            foreach (var p in r.cells) if (claimed.Contains(p)) { taken = true; break; }
            if (taken) continue;
            Piece at = null;
            if (preferred != null) foreach (var p in r.cells) if (preferred.Contains(new Vector2Int(p.x, p.y)) && p.cover != Cover.Yarn) { at = p; break; }
            if (at == null) foreach (var p in r.cells) if (p.cover != Cover.Yarn) { at = p; break; }
            if (at == null) continue;
            foreach (var p in r.cells) claimed.Add(p);
            spawns.Add(new Spawn { x = at.x, y = at.y, type = Special.Hunter });
        }
        return spawns;
    }

    IEnumerable<Piece> Neighbours(int x, int y)
    {
        var n = At(x + 1, y); if (n != null) yield return n;
        n = At(x - 1, y); if (n != null) yield return n;
        n = At(x, y + 1); if (n != null) yield return n;
        n = At(x, y - 1); if (n != null) yield return n;
    }

    // What a blast does to one cell.
    void Affect(int x, int y, Ctx ctx, float t = 0f)
    {
        var p = At(x, y);
        if (p == null || p.kind == PieceKind.Fish) return;
        ctx.Mark(p, t);
        if (Hittable(p)) { ctx.hits.Add(p); return; }
        if (p.cover == Cover.Yarn) { ctx.yarn.Add(p); return; }
        if (ctx.clear.Add(p) && p.special != Special.None) ctx.queue.Enqueue(p);
    }

    void Trigger(Piece s, Ctx ctx, bool tapped)
    {
        ctx.clear.Add(s);
        ctx.queue.Enqueue(s);
        if (tapped && s.special == Special.Line) s.lineRow = true;
        tappedCross = tapped && s.special == Special.Line ? s : tappedCross;
    }
    Piece tappedCross;

    void ExpandQueue(Ctx ctx)
    {
        while (ctx.queue.Count > 0)
        {
            var s = ctx.queue.Dequeue();
            if (!ctx.triggered.Add(s)) continue;
            ctx.clear.Add(s);
            switch (s.special)
            {
                case Special.Line:
                    AudioManager.Play("Line-Pop");
                    bool both = s == tappedCross;
                    float t0 = ctx.When(s);
                    if (s.lineRow || both) LineShoot(s.x, s.y, true, t0, ctx);
                    if (!s.lineRow || both) LineShoot(s.x, s.y, false, t0, ctx);
                    break;
                case Special.Bomb:
                    AudioManager.Play("explosion");
                    BombShoot(s.x, s.y, 1, ctx.When(s), ctx);
                    break;
                case Special.Hunter:
                {
                    AudioManager.Play("hunter");
                    float h0 = ctx.When(s);
                    HunterHorn(s.x, s.y, h0, ctx);
                    HunterShoot(s.x, s.y, h0, ctx, Special.None);
                    ctx.when[s] = h0 + FX.HunterCharge;       // he stays to shoot, then pops
                    break;
                }
                case Special.Rainbow:
                    if (ctx.When(s) <= 0f) ctx.Mark(s, FX.RainbowCharge);
                    RainbowColor(MostCommonColor(), ctx, s.x, s.y, ctx.When(s) > FX.RainbowCharge ? ctx.When(s) : 0f);
                    break;
            }
        }
        tappedCross = null;
    }

    void RainbowColor(int color, Ctx ctx, int ox, int oy, float t0)
    {
        AudioManager.Play("rainbow");
        if (color < 0) return;
        var targets = new List<Piece>();
        for (int x = 0; x < cols; x++)
            for (int y = 0; y < rows; y++)
            {
                var p = grid[x, y];
                if (p != null && p.kind == PieceKind.Cat && p.special == Special.None && p.color == color && !(x == ox && y == oy)) targets.Add(p);
            }
        RainbowShoot(ox, oy, t0, targets, ctx, true);
        var self = At(ox, oy);   // the tapped cat of a colour blast pops with the swirl
        if (self != null && self.special == Special.None && self.color == color) Affect(ox, oy, ctx, t0 + FX.RainbowCharge);
    }

    int MostCommonColor()
    {
        var count = new int[LevelGen.Colors];
        foreach (var p in grid) if (p != null && p.MatchColor >= 0) count[p.MatchColor]++;
        int best = -1, bestN = 0;
        for (int i = 0; i < count.Length; i++) if (count[i] > bestN) { bestN = count[i]; best = i; }
        return best;
    }

    void Combo(Piece a, Piece b, Ctx ctx)
    {
        ctx.clear.Add(a); ctx.clear.Add(b); ctx.triggered.Add(a); ctx.triggered.Add(b);
        int cx = b.x, cy = b.y;
        var t1 = a.special; var t2 = b.special;
        bool Has(Special s) => t1 == s || t2 == s;
        if (t1 == Special.Rainbow && t2 == Special.Rainbow)
        {
            AudioManager.Play("rainbow");
            var shot = new RainbowShot { x = cx, y = cy, t = 0f, orbs = false };
            for (int x = 0; x < cols; x++)
                for (int y = 0; y < rows; y++)
                {
                    if (!Live(x, y)) continue;
                    float at = FX.RainbowCharge + 0.05f * Vector2.Distance(new Vector2(x, y), new Vector2(cx, cy));
                    shot.cells.Add(new Vector2Int(x, y)); shot.arrive.Add(at);
                    Affect(x, y, ctx, at);
                }
            ctx.rainbows.Add(shot);
        }
        else if (Has(Special.Rainbow))
        {
            // every cat of the most common colour turns into the other special and fires
            var other = t1 == Special.Rainbow ? t2 : t1;
            int c = MostCommonColor();
            AudioManager.Play("rainbow");
            var conv = new List<Piece>();
            foreach (var p in grid)
                if (p != null && p.PlainCat && p.color == c)
                {
                    p.special = other; p.lineRow = rng.Next(2) == 0; p.Refresh();
                    ctx.clear.Add(p); ctx.queue.Enqueue(p); conv.Add(p);
                }
            RainbowShoot(cx, cy, 0f, conv, ctx, false);   // orbs deliver the specials; each fires when its orb lands
        }
        else if (t1 == Special.Hunter && t2 == Special.Hunter)
        {
            AudioManager.Play("hunter");
            HunterHorn(cx, cy, 0f, ctx);
            for (int i = 0; i < 3; i++) HunterShoot(cx, cy, i * 0.12f, ctx, Special.None);
        }
        else if (Has(Special.Hunter))
        {
            // the arrow carries the other special to the target
            AudioManager.Play("hunter");
            HunterHorn(cx, cy, 0f, ctx);
            HunterShoot(cx, cy, 0f, ctx, t1 == Special.Hunter ? t2 : t1);
        }
        else if (t1 == Special.Bomb && t2 == Special.Bomb)
        {
            AudioManager.Play("explosion");
            BombShoot(cx, cy, 2, 0f, ctx);
        }
        else if (t1 == Special.Line && t2 == Special.Line)
        {
            AudioManager.Play("Line-Pop");
            LineShoot(cx, cy, true, 0f, ctx);
            LineShoot(cx, cy, false, 0f, ctx);
        }
        else
        {
            AudioManager.Play("explosion");
            bool lineBomb = Has(Special.Line);
            for (int d = -1; d <= 1; d++)
            {
                if (lineBomb) { LineShoot(cx, cy + d, true, 0f, ctx); LineShoot(cx + d, cy, false, 0f, ctx); continue; }
                for (int x = 0; x < cols; x++) Affect(x, cy + d, ctx);
                for (int y = 0; y < rows; y++) Affect(cx + d, y, ctx);
            }
        }
    }

    // ======================================================= execution (the visible part)
    IEnumerator Execute(Ctx ctx, List<Spawn> spawns)
    {
        ExpandQueue(ctx);

        // cats in the clear set react ("фыркнул и исчез") before popping
        foreach (var p in ctx.clear) p.SetAngry(true);
        if (ctx.clear.Count > 0) yield return new WaitForSeconds(0.14f);

        // scoring of the extra cats cleared by specials / power-ups
        int extra = 0;
        foreach (var p in ctx.clear) if (!ctx.seed.Contains(p) && p.special == Special.None && p.kind == PieceKind.Cat) extra++;
        if (ctx.byPowerUp) PowerUpCleared?.Invoke(extra); else if (extra > 0) SpecialCleared?.Invoke(extra);

        // pop, staggered from the centre of the group outward
        var list = new List<Piece>(ctx.clear);
        Vector3 centre = Vector3.zero;
        foreach (var p in list) centre += p.transform.position;
        if (list.Count > 0) centre /= list.Count;
        list.Sort((a, b) => (a.transform.position - centre).sqrMagnitude.CompareTo((b.transform.position - centre).sqrMagnitude));
        // line beams start together with the pops; cats on a line pop when the head reaches them
        foreach (var l in ctx.lines)
        {
            float neg = ((l.row ? l.x : l.y) + 0.5f) * cell + cell * 0.6f;
            float pos = ((l.row ? cols - l.x : rows - l.y) - 0.5f) * cell + cell * 0.6f;
            FX.LineBlast(CellPos(l.x, l.y), l.row, cell, neg, pos, l.t);
        }
        foreach (var bb in ctx.bombs) FX.BombBlast(CellPos(bb.x, bb.y), cell, bb.r, bb.t);
        foreach (var hs in ctx.hunters) FX.HunterBlast(CellPos(hs.x, hs.y), CellPos(hs.tx, hs.ty), cell, hs.t);
        foreach (var rb in ctx.rainbows)
        {
            var pts = new List<Vector3>();
            foreach (var c in rb.cells) pts.Add(CellPos(c.x, c.y));
            FX.RainbowBlast(CellPos(rb.x, rb.y), cell, pts, rb.arrive, rb.orbs, rb.t);
        }
        float delay = 0f, latest = 0f;
        foreach (var p in list)
        {
            if (p.dead) continue;
            p.dead = true;
            grid[p.x, p.y] = null;
            clearedThisMove++;
            if (p == mouseOn && mouseT != null)
            {   // the cat the mouse rides on is gone: caught!
                mouseOn = null; float lt2 = ctx.When(p);
                StartCoroutine(Later(lt2 > 0f ? lt2 : delay, CatchMouse));
            }
            if (milk[p.x, p.y] > 0 && p.kind == PieceKind.Cat)
            {   // the cat laps up the milk: a coin, and that bit of the puddle is gone
                int mx = p.x, my = p.y; float lt1 = ctx.When(p);
                StartCoroutine(Later(lt1 > 0f ? lt1 : delay, () => LapMilk(mx, my)));
            }
            if (foam[p.x, p.y] > 0)
            {   // the foam under it pops together with the cat (on a line: when the beam gets there)
                int fx = p.x, fy = p.y; float lt0 = ctx.When(p);
                StartCoroutine(Later(lt0 > 0f ? lt0 : delay, () => FoamHit(fx, fy)));
            }
            if (p.kind == PieceKind.Cat && p.special == Special.None) CatCleared?.Invoke(p.color, p.transform.position);
            float lt = ctx.When(p);
            if (lt > 0f) { StartCoroutine(PopRoutine(p, lt)); latest = Mathf.Max(latest, lt); }
            else { StartCoroutine(PopRoutine(p, delay)); delay += 0.018f; }
        }

        // blockers take one hit each (on a line: when the beam gets there)
        foreach (var h in ctx.hits)
            if (!h.dead)
            {
                float lt = ctx.When(h); latest = Mathf.Max(latest, lt);
                Vector2Int? src = ctx.hitFrom.TryGetValue(h, out var s0) ? s0 : (Vector2Int?)null;
                StartCoroutine(Later(lt, () => { if (!h.dead) Hit(h, src); }));
            }
        foreach (var y in ctx.yarn) if (!y.dead && y.cover == Cover.Yarn) { float lt = ctx.When(y); latest = Mathf.Max(latest, lt); StartCoroutine(Later(lt, () => { if (!y.dead && y.cover == Cover.Yarn) ReleaseCover(y, y.transform.position); })); }

        yield return new WaitForSeconds(Mathf.Max(Mathf.Min(0.18f + delay, 0.5f), latest + 0.18f));

        if (spawns != null)
            foreach (var s in spawns)
            {
                if (grid[s.x, s.y] != null) continue;
                var p = NewPiece(PieceKind.Cat, -1, s.x, s.y);
                p.special = s.type; p.lineRow = s.lineRow; p.Refresh();
                Put(p, s.x, s.y);
                StartCoroutine(PopIn(p));
                AudioManager.Play("balance", 0.6f);
            }
        if (spawns != null && spawns.Count > 0) yield return new WaitForSeconds(0.18f);
    }

    static readonly Color[] CatFxColors = { new Color(1f, .62f, .3f), new Color(1f, 1f, 1f), new Color(.75f, .75f, .8f), new Color(.35f, .3f, .4f), new Color(1f, .85f, .25f) };

    IEnumerator PopRoutine(Piece p, float delay)
    {
        if (delay > 0) yield return new WaitForSeconds(delay);
        Vector3 pos = p.transform.position;
        Color c = p.kind == PieceKind.Cat && p.color >= 0 ? CatFxColors[p.color % CatFxColors.Length] : new Color(1f, .9f, .5f);
        if (p.kind == PieceKind.Cat) FX.CatPop(pos, c, cell, p.special != Special.None ? 1.4f : 1f);
        else FX.Burst(pos, c, cell, 8);
        var srs = p.GetComponentsInChildren<SpriteRenderer>();
        yield return Kit.Tween(0.16f, k =>
        {
            float s = 1f + 0.25f * k;
            p.visual.localScale = new Vector3(s, s, 1);
            foreach (var sr in srs) { var col = sr.color; col.a = 1f - k; sr.color = col; }
        });
        Destroy(p.gameObject);
    }

    IEnumerator Later(float t, System.Action a)
    {
        if (t > 0f) yield return new WaitForSeconds(t);
        a();
    }

    IEnumerator PopIn(Piece p)
    {
        yield return Kit.Tween(0.25f, k => { float s = Kit.EaseOutBack(k); p.visual.localScale = new Vector3(s, s, 1); });
    }

    void Hit(Piece h, Vector2Int? from = null)
    {
        Vector3 pos = h.transform.position;
        if (h.kind == PieceKind.Milk) { TipBucket(h, from); return; }
        if (h.kind == PieceKind.Vase)
        {
            AudioManager.Play("vase");
            FX.Debris(pos, new[] { "vase_shard_1", "vase_shard_2", "vase_shard_3", "petal_1", "petal_2", "petal_3" }, cell, 6);
            h.dead = true; grid[h.x, h.y] = null;
            Destroy(h.gameObject);
            Bonus?.Invoke(20);
            GoalHit?.Invoke(GoalType.Vase, pos);
            return;
        }
        if (h.cover == Cover.Box)
        {
            h.coverHp--;
            AudioManager.Play("Hit", 0.8f);
            FX.Debris(pos, new[] { "box_piece_1", "box_piece_2", "box_piece_3" }, cell, h.coverHp <= 0 ? 4 : 2);
            if (h.coverHp <= 0) ReleaseCover(h, pos);
            else { h.Refresh(); StartCoroutine(h.Wiggle()); }
            return;
        }
        if (h.cover == Cover.Mint) ReleaseCover(h, pos);
    }

    // ======================================================= milk bucket
    // Hit -> the bucket tips over away from the hit (or, hit by a blast, towards the most cats) and the
    // milk spills: its own cell + 3 cells ahead + the two cells beside the first one. A cat cleared
    // while standing in milk laps it up for a coin. Untouched puddles dry out after a few moves.
    static readonly Vector2Int[] Dirs = { Vector2Int.right, Vector2Int.left, Vector2Int.up, Vector2Int.down };

    List<Vector2Int> SpillCells(Vector2Int c, Vector2Int d)
    {
        var l = new List<Vector2Int> { c };
        for (int i = 1; i <= 3; i++) l.Add(c + d * i);
        var side = new Vector2Int(d.y, d.x);
        l.Add(c + d + side); l.Add(c + d - side);
        l.RemoveAll(v => !Live(v.x, v.y));
        return l;
    }

    void TipBucket(Piece h, Vector2Int? from)
    {
        var c = new Vector2Int(h.x, h.y);
        Vector2Int dir = Vector2Int.right;
        if (from.HasValue && from.Value != c)
        {
            var away = c - from.Value;
            dir = Mathf.Abs(away.x) >= Mathf.Abs(away.y) ? new Vector2Int(Math.Sign(away.x), 0) : new Vector2Int(0, Math.Sign(away.y));
        }
        else
        {
            int best = -1;
            foreach (var d in Dirs)
            {
                int n = 0; foreach (var v in SpillCells(c, d)) { var q = At(v.x, v.y); if (q != null && q.kind == PieceKind.Cat) n++; }
                if (n > best) { best = n; dir = d; }
            }
        }
        Vector3 pos = CellPos(c.x, c.y);
        h.dead = true; grid[c.x, c.y] = null;
        Destroy(h.gameObject);
        AudioManager.Play("milk_spill", 1f);
        StartCoroutine(BucketRoll(pos, dir));
        var cells = SpillCells(c, dir);
        for (int i = 0; i < cells.Count; i++)
        {
            var v = cells[i];
            float delay = 0.08f + 0.06f * Mathf.Max(Mathf.Abs(v.x - c.x), Mathf.Abs(v.y - c.y));
            StartCoroutine(Later(delay, () => { milk[v.x, v.y] = MilkLife; MilkVisual(v.x, v.y, true); FX.MilkSplash(CellPos(v.x, v.y), cell, 2); }));
        }
        Bonus?.Invoke(20);
    }

    // the empty bucket: lies down facing the spill, rolls a little that way and fades
    IEnumerator BucketRoll(Vector3 pos, Vector2Int dir)
    {
        var holder = new GameObject("bucket").transform; holder.position = pos;
        var sr = new GameObject("img").AddComponent<SpriteRenderer>(); sr.transform.SetParent(holder, false);
        sr.sortingOrder = FX.FxOrder - 1;
        Kit.FitSprite(sr, "milk_bucket_tipped", cell, 0.95f);
        // the art pours to the right: mirror for left, turn for up / down
        if (dir.x < 0) holder.localScale = new Vector3(-1, 1, 1);
        else if (dir.y != 0) holder.rotation = Quaternion.Euler(0, 0, dir.y > 0 ? 90f : -90f);
        Vector3 to = pos - new Vector3(dir.x, dir.y, 0) * cell * 0.25f;   // it recoils back a touch as it pours
        yield return Kit.Tween(0.7f, k =>
        {
            if (holder == null) return;
            holder.position = Vector3.Lerp(pos, to, Kit.EaseOutCubic(k));
            var col = sr.color; col.a = k < 0.6f ? 1f : 1f - (k - 0.6f) / 0.4f; sr.color = col;
        });
        if (holder != null) Destroy(holder.gameObject);
    }

    void MilkVisual(int x, int y, bool appear)
    {
        int m = milk[x, y];
        if (m <= 0)
        {
            if (milkSr[x, y] != null) { var t = milkSr[x, y].transform.parent; milkSr[x, y] = null; StartCoroutine(FadeAway(t)); }
            return;
        }
        if (milkSr[x, y] == null)
        {
            var holder = new GameObject("milk").transform; holder.SetParent(tilesRoot, false);
            holder.position = CellPos(x, y);
            var sr = new GameObject("img").AddComponent<SpriteRenderer>(); sr.transform.SetParent(holder, false);
            sr.sortingOrder = 5;                        // over the tile (and foam), under the cats
            Kit.FitSprite(sr, "milk_puddle", cell * 1.18f, 1.0f);   // a bit wider than the cell: the milky rim and drops show around the cat
            holder.localRotation = Quaternion.Euler(0, 0, rng.Next(4) * 90f);
            milkSr[x, y] = sr;
            if (appear) StartCoroutine(PopIn(holder));
        }
        // a drying puddle gets paler
        var c = milkSr[x, y].color; c.a = Mathf.Lerp(0.6f, 1f, m / (float)MilkLife); milkSr[x, y].color = c;
    }

    IEnumerator PopIn(Transform t)
    {
        yield return Kit.Tween(0.25f, k => { if (t != null) t.localScale = Vector3.one * Kit.EaseOutBack(k); });
    }

    IEnumerator FadeAway(Transform t)
    {
        var sr = t != null ? t.GetComponentInChildren<SpriteRenderer>() : null;
        yield return Kit.Tween(0.3f, k => { if (t != null) { t.localScale = Vector3.one * (1f - 0.3f * k); if (sr != null) { var c = sr.color; c.a *= 1f - k * 0.2f; sr.color = c; } } });
        if (t != null) Destroy(t.gameObject);
    }

    void LapMilk(int x, int y)
    {
        if (milk[x, y] <= 0) return;
        milk[x, y] = 0;
        Vector3 pos = CellPos(x, y);
        FX.MilkSplash(pos, cell, 1);
        AudioManager.Play("milk_lap", 0.7f);
        MilkVisual(x, y, false);
        MilkCoin?.Invoke(pos);
    }

    void DryMilk()
    {
        for (int x = 0; x < cols; x++)
            for (int y = 0; y < rows; y++)
                if (milk[x, y] > 0) { milk[x, y]--; MilkVisual(x, y, false); }
    }

    // ======================================================= mouse thief
    const float MouseSize = 0.85f;
    Vector3 MouseOffset => new Vector3(cell * 0.2f, cell * 0.2f, 0f);   // perched on the cat's top-right
    static bool MouseSeat(Piece p) => p != null && !p.dead && p.kind == PieceKind.Cat && p.special == Special.None && p.cover == Cover.None;

    void PlaceMouse()
    {
        var seats = new List<Piece>();
        for (int x = 0; x < cols; x++)
            for (int y = 2; y < rows - 1; y++)
                if (Live(x, y) && MouseSeat(grid[x, y])) seats.Add(grid[x, y]);
        if (seats.Count == 0) return;
        mouseOn = seats[rng.Next(seats.Count)];
        mouseT = new GameObject("Mouse").transform; mouseT.SetParent(transform, false);
        mouseBody = new GameObject("body").transform; mouseBody.SetParent(mouseT, false);
        mouseSr = new GameObject("img").AddComponent<SpriteRenderer>(); mouseSr.transform.SetParent(mouseBody, false);   // FitSprite scales this child; the body carries the animation scale
        mouseSr.sortingOrder = 30;                  // over the cats, under the FX
        Kit.FitSprite(mouseSr, "mouse_idle", cell * MouseSize, 1f);
        mouseT.position = mouseOn.transform.position + MouseOffset;
        mouseFace = 1;
    }

    void LateUpdate()
    {
        if (mouseT == null) return;
        if (!mouseHopping && mouseOn != null) mouseT.position = mouseOn.transform.position + MouseOffset;
        if (mouseBody != null && !mouseHopping)
        {   // idle: breathes and looks a bit shifty
            float s = Mathf.Sin(Time.time * 5f);
            mouseBody.localScale = new Vector3(mouseFace * (1f + 0.04f * s), 1f - 0.04f * s, 1f);
            mouseBody.localRotation = Quaternion.Euler(0, 0, 4f * Mathf.Sin(Time.time * 1.7f));
        }
    }

    void MouseTurn()
    {
        mouseMoves++;
        Vector3? from = null;
        if (mouseMoves % MouseStealEvery == 0 && mouseCoins < MouseMaxCoins && MouseSteal != null)
        {
            from = MouseSteal();
            if (from.HasValue) mouseCoins++;
        }
        StartCoroutine(MouseTurnRoutine(from));
    }

    IEnumerator MouseTurnRoutine(Vector3? stealFrom)
    {
        if (stealFrom.HasValue) yield return StealCoin(stealFrom.Value);
        if (!MouseActive) yield break;
        // hop to a neighbouring plain cat (diagonals too)
        var opts = new List<Piece>();
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                if ((dx != 0 || dy != 0) && MouseSeat(At(mouseOn.x + dx, mouseOn.y + dy))) opts.Add(At(mouseOn.x + dx, mouseOn.y + dy));
        if (opts.Count == 0) yield break;
        yield return MouseHop(opts[rng.Next(opts.Count)]);
    }

    IEnumerator MouseHop(Piece to)
    {
        mouseHopping = true;
        mouseOn = to;
        Vector3 a = mouseT.position;
        float dx = to.transform.position.x + MouseOffset.x - a.x;
        if (Mathf.Abs(dx) > 0.01f) mouseFace = dx < 0 ? -1 : 1;   // the art faces right
        Kit.FitSprite(mouseSr, "mouse_run", cell * MouseSize, 1f);
        mouseBody.localRotation = Quaternion.identity;
        AudioManager.Play("mouse_hop", 0.35f);
        yield return Kit.Tween(0.32f, k =>
        {
            if (mouseT == null) return;
            Vector3 b = (mouseOn != null ? mouseOn.transform.position : a - MouseOffset) + MouseOffset;
            mouseT.position = Vector3.Lerp(a, b, k) + new Vector3(0, cell * 0.4f * Mathf.Sin(k * Mathf.PI), 0);
            float sq = k > 0.85f ? 1f - 0.15f * Mathf.Sin((k - 0.85f) / 0.15f * Mathf.PI) : 1f;
            if (mouseBody != null) mouseBody.localScale = new Vector3(mouseFace * (2f - sq), sq, 1f);
        });
        if (mouseSr != null) Kit.FitSprite(mouseSr, "mouse_idle", cell * MouseSize, 1f);
        mouseHopping = false;
    }

    // a coin leaves the balance and flies into the mouse's sack
    IEnumerator StealCoin(Vector3 from)
    {
        var go = new GameObject("stolen");
        var sr = new GameObject("img").AddComponent<SpriteRenderer>(); sr.transform.SetParent(go.transform, false);
        sr.sortingOrder = FX.FxOrder + 5;
        Kit.FitSprite(sr, "ui_balance", cell * 0.4f, 1f);
        from.z = 0;
        Vector3 mid = from + new Vector3(UnityEngine.Random.Range(-1.5f, 1.5f), -0.5f, 0);
        AudioManager.Play("mouse_steal", 0.8f);
        yield return Kit.Tween(0.7f, k =>
        {
            Vector3 to = mouseT != null ? mouseT.position : from;
            Vector3 p = Vector3.Lerp(Vector3.Lerp(from, mid, k), Vector3.Lerp(mid, to, k), k);
            go.transform.position = p;
            go.transform.localScale = Vector3.one * Mathf.Lerp(1f, 0.6f, k);
            go.transform.localRotation = Quaternion.Euler(0, 0, k * 540f);
        }, Kit.EaseInOut);
        Destroy(go);
        if (mouseT == null) yield break;
        AudioManager.Play("balance", 0.5f);
        RefreshMousePile();
        // a greedy little hug of the sack
        yield return Kit.Tween(0.22f, k => { if (mouseBody != null) { float s = 1f + 0.18f * Mathf.Sin(k * Mathf.PI); mouseBody.localScale = new Vector3(mouseFace * s, s, 1f); } });
    }

    // the stolen coins lie in a little heap at the mouse's feet
    void RefreshMousePile()
    {
        if (mouseT == null) return;
        while (mousePile.Count < mouseCoins)
        {
            int i = mousePile.Count;
            var holder = new GameObject("coin").transform; holder.SetParent(mouseT, false);
            holder.localPosition = new Vector3(cell * (0.12f + 0.09f * (i % 3)), cell * (-0.3f + 0.08f * (i / 3)), 0);
            var sr = new GameObject("img").AddComponent<SpriteRenderer>(); sr.transform.SetParent(holder, false);
            Kit.FitSprite(sr, "ui_balance", cell * 0.24f, 1f);   // FitSprite owns the child's scale; PopIn animates the holder
            sr.sortingOrder = 31;
            mousePile.Add(sr);
            StartCoroutine(PopIn(holder));
        }
    }

    void CatchMouse()
    {
        if (mouseT == null) return;
        Vector3 pos = mouseT.position;
        int coins = mouseCoins;
        var t = mouseT; var body = mouseBody; var sr = mouseSr;
        mouseT = null; mouseOn = null; mouseBody = null; mouseSr = null; mousePile.Clear(); mouseCoins = 0;
        StartCoroutine(MouseCaughtAnim(t, body, sr));
        MouseCaught?.Invoke(pos, coins);
    }

    IEnumerator MouseCaughtAnim(Transform t, Transform body, SpriteRenderer sr)
    {
        if (sr != null) Kit.FitSprite(sr, "mouse_caught", cell * 1.0f, 1f);
        if (body != null) { body.localScale = Vector3.one; body.localRotation = Quaternion.identity; }
        Vector3 a = t.position;
        var srs = t.GetComponentsInChildren<SpriteRenderer>();
        yield return Kit.Tween(1.1f, k =>
        {
            if (t == null) return;
            t.position = a + new Vector3(0, cell * 0.5f * Mathf.Sin(Mathf.Min(1f, k * 1.6f) * Mathf.PI * 0.5f), 0);
            float pop = k < 0.2f ? Kit.EaseOutBack(k / 0.2f) : 1f;
            t.localScale = Vector3.one * (0.6f + 0.6f * pop);
            t.localRotation = Quaternion.Euler(0, 0, 10f * Mathf.Sin(k * 20f) * (1f - k));
            float al = k < 0.7f ? 1f : 1f - (k - 0.7f) / 0.3f;
            foreach (var s in srs) if (s != null) { var c = s.color; c.a = al; s.color = c; }
        });
        if (t != null) Destroy(t.gameObject);
    }

    // level over and the mouse is still free: it scampers off the board with its loot
    public IEnumerator MouseRunAway()
    {
        if (mouseT == null) yield break;
        var t = mouseT; var body = mouseBody; var sr = mouseSr;
        mouseT = null; mouseOn = null; mouseBody = null; mouseSr = null; mousePile.Clear();
        int dir = t.position.x < WorldRect.center.x ? -1 : 1;
        if (sr != null) Kit.FitSprite(sr, "mouse_run", cell * MouseSize, 1f);
        if (body != null) { body.localScale = new Vector3(dir, 1f, 1f); body.localRotation = Quaternion.identity; }
        AudioManager.Play("mouse_run", 0.8f);
        Vector3 a = t.position;
        float dist = Mathf.Abs((dir < 0 ? WorldRect.xMin : WorldRect.xMax) - a.x) + cell * 2f;
        yield return Kit.Tween(0.9f, k =>
        {
            if (t == null) return;
            t.position = a + new Vector3(dir * dist * Kit.EaseInCubic(k) * 0.6f + dir * dist * 0.4f * k, cell * 0.2f * Mathf.Abs(Mathf.Sin(k * Mathf.PI * 5f)), 0);
        });
        if (t != null) Destroy(t.gameObject);
    }

    // ======================================================= soap foam
    // Blobs of foam (a few neighbouring cells each) on plain cats; the thick ones go first.
    void PlaceFoam()
    {
        int want = def.foam, thick = def.foamThick;
        if (want <= 0) return;
        bool Ok(int x, int y) => Live(x, y) && foam[x, y] == 0 && grid[x, y] != null && grid[x, y].PlainCat;
        var free = new List<Vector2Int>();
        for (int x = 0; x < cols; x++) for (int y = 0; y < rows; y++) if (Ok(x, y)) free.Add(new Vector2Int(x, y));
        Shuffle(free);
        int placed = 0, guard = 0;
        while (placed < want && free.Count > 0 && guard++ < 200)
        {
            var seed = free[rng.Next(free.Count)];
            if (!Ok(seed.x, seed.y)) { free.Remove(seed); continue; }
            int blob = Mathf.Min(want - placed, 3 + rng.Next(4));
            var front = new List<Vector2Int> { seed };
            while (blob > 0 && front.Count > 0)
            {
                var c = front[rng.Next(front.Count)]; front.Remove(c);
                if (!Ok(c.x, c.y)) continue;
                foam[c.x, c.y] = placed < thick ? 2 : 1;
                placed++; blob--;
                foreach (var d in new[] { Vector2Int.left, Vector2Int.right, Vector2Int.up, Vector2Int.down })
                    if (Ok(c.x + d.x, c.y + d.y)) front.Add(c + d);
            }
        }
        for (int x = 0; x < cols; x++) for (int y = 0; y < rows; y++) FoamVisual(x, y);
    }

    // Foam look: the foam simply replaces the cell's base square (same size as a normal tile);
    // the cat stands on top of it like on any other cell.
    static readonly Color FoamTint = new Color(0.62f, 0.82f, 1f);   // soapy blue: the foam cells stand out from the plain tiles
    void FoamVisual(int x, int y)
    {
        int f = foam[x, y];
        if (f <= 0) { if (foamSr[x, y] != null) { Destroy(foamSr[x, y].transform.parent.gameObject); foamSr[x, y] = null; } return; }
        if (foamSr[x, y] == null)
        {
            var holder = new GameObject("foam").transform; holder.SetParent(tilesRoot, false);
            holder.position = CellPos(x, y);
            var sr = new GameObject("img").AddComponent<SpriteRenderer>(); sr.transform.SetParent(holder, false);
            sr.sortingOrder = 4;                    // above the base tile, under the cats
            foamSr[x, y] = sr;
        }
        Kit.FitSprite(foamSr[x, y], f >= 2 ? "tile_foam_2" : "tile_foam_1", cell, 0.99f);
        foamSr[x, y].color = FoamTint;
    }

    void FoamHit(int x, int y)
    {
        if (foam == null || foam[x, y] <= 0) return;
        foam[x, y]--;
        Vector3 pos = CellPos(x, y);
        AudioManager.Play("soap_" + UnityEngine.Random.Range(1, 4), 0.75f);
        FX.FoamPop(pos, cell, foam[x, y] == 0);
        FoamVisual(x, y);
        if (foam[x, y] > 0) { if (foamSr[x, y] != null) StartCoroutine(Squash(foamSr[x, y].transform.parent)); return; }
        Bonus?.Invoke(20);
        GoalHit?.Invoke(GoalType.Foam, pos);
    }

    IEnumerator Squash(Transform t)
    {
        yield return Kit.Tween(0.25f, k => { if (t != null) { float s = 1f - 0.12f * Mathf.Sin(k * Mathf.PI); t.localScale = new Vector3(2f - s, s, 1f); } });
        if (t != null) t.localScale = Vector3.one;
    }

    void ReleaseCover(Piece p, Vector3 pos)
    {
        var c = p.cover;
        if (c == Cover.None) return;
        p.cover = Cover.None; p.coverHp = 0;
        p.Refresh();
        StartCoroutine(p.Pulse(0.2f));
        switch (c)
        {
            case Cover.Box:
                FX.Debris(pos, new[] { "box_piece_1", "box_piece_2", "box_piece_3" }, cell, 3);
                Bonus?.Invoke(20); GoalHit?.Invoke(GoalType.Box, pos); break;
            case Cover.Yarn:
                AudioManager.Play("Chain", 0.8f);
                FX.Debris(pos, new[] { "yarn_ball", "yarn_thread_1", "yarn_thread_2" }, cell, 3);
                Bonus?.Invoke(20); GoalHit?.Invoke(GoalType.Yarn, pos); break;
            case Cover.Mint:
                AudioManager.Play("mint_off", 0.8f);
                FX.Debris(pos, new[] { "mint_leaf" }, cell, 4);
                mintHitThisMove = true;
                Bonus?.Invoke(20); GoalHit?.Invoke(GoalType.Mint, pos); break;
        }
    }

    // ======================================================= gravity & refill
    IEnumerator Gravity()
    {
        while (true)
        {
            float longest = 0f;
            for (int x = 0; x < cols; x++)
            {
                int y = 0;
                while (y < rows)
                {
                    // skip static cells; a segment is a vertical stretch between static pieces
                    if (Blocked(x, y)) { y++; continue; }
                    int lo = y;
                    int hi = y;
                    while (hi + 1 < rows && !Blocked(x, hi + 1)) hi++;
                    bool top = OpenAbove(x, hi);

                    // compact falling pieces down
                    int write = lo;
                    for (int r = lo; r <= hi; r++)
                    {
                        var p = grid[x, r];
                        if (p == null) continue;
                        if (r != write)
                        {
                            grid[x, r] = null; Put(p, x, write);
                            float d = MoveDuration(r - write);
                            longest = Mathf.Max(longest, d);
                            StartCoroutine(MoveTo(p, CellPos(x, write), d, true));
                        }
                        write++;
                    }
                    // refill the empties at the top of the segment
                    int missing = hi - write + 1;
                    for (int k = 0; k < missing; k++)
                    {
                        int ty = write + k;
                        Piece np;
                        if (top && fishToSpawn > 0 && k == missing - 1 && FishCount() < 2 && ClearColumn(x, ty))   // up to two fish swim at once
                        { np = NewPiece(PieceKind.Fish, -1, x, ty); fishToSpawn--; }
                        else np = NewPiece(PieceKind.Cat, rng.Next(LevelGen.Colors), x, ty);
                        Put(np, x, ty);
                        if (top)
                        {
                            np.transform.position = CellPos(x, rows + k);
                            float d = MoveDuration(rows + k - ty);
                            longest = Mathf.Max(longest, d);
                            StartCoroutine(MoveTo(np, CellPos(x, ty), d, true));
                        }
                        else
                        {
                            np.visual.localScale = Vector3.zero;
                            StartCoroutine(PopIn(np));
                            longest = Mathf.Max(longest, 0.25f);
                        }
                    }
                    y = hi + 1;
                }
            }
            if (longest > 0) yield return new WaitForSeconds(longest + 0.02f);

            // deliver fish that reached the bottom row
            bool delivered = false;
            for (int x = 0; x < cols; x++)
            {
                // a fish is delivered once nothing is left under it in its column (bottom of the
                // shape, or resting on a static piece)
                Piece p = null; int fy = -1;
                for (int y = 0; y < rows; y++) if (grid[x, y] != null && grid[x, y].kind == PieceKind.Fish) { p = grid[x, y]; fy = y; break; }
                if (p == null) continue;
                bool bottom = true;
                for (int y = 0; y < fy; y++) if (!Blocked(x, y)) { bottom = false; break; }
                if (bottom)
                {
                    delivered = true;
                    grid[x, fy] = null; p.dead = true;
                    Vector3 pos = p.transform.position;
                    AudioManager.Play("win", 0.6f);
                    FX.Debris(pos, new[] { "water_drop_1", "water_drop_2", "water_drop_3" }, cell, 5);
                    StartCoroutine(FishOut(p));
                    fishDelivered++;
                    GoalHit?.Invoke(GoalType.Fish, pos);
                }
            }
            if (!delivered) break;
            yield return new WaitForSeconds(0.25f);
        }
    }

    bool FishOnBoard() { foreach (var p in grid) if (p != null && p.kind == PieceKind.Fish) return true; return false; }
    int FishCount() { int n = 0; foreach (var p in grid) if (p != null && p.kind == PieceKind.Fish && !p.dead) n++; return n; }
    // every cell under (x, top) is part of the board and nothing static sits in the way
    bool ClearColumn(int x, int top) { for (int y = LowestLive(x); y < top; y++) if (!live[x, y] || (grid[x, y] != null && grid[x, y].IsStatic)) return false; return true; }

    IEnumerator FishOut(Piece p)
    {
        yield return Kit.Tween(0.3f, k => { float s = 1f - k; p.visual.localScale = new Vector3(s, s, 1); p.visual.localPosition = new Vector3(0, -cell * 0.5f * k, 0); });
        Destroy(p.gameObject);
    }

    float MoveDuration(int cellsDown) => 0.12f + 0.055f * Mathf.Sqrt(Mathf.Max(1, cellsDown)) * 2f;

    IEnumerator MoveTo(Piece p, Vector3 to, float dur, bool bounce)
    {
        Vector3 from = p.transform.position;
        yield return Kit.Tween(dur, k => { if (p != null) p.transform.position = Vector3.LerpUnclamped(from, to, bounce ? Kit.EaseInCubic(k) : k); });
        if (p == null) yield break;
        p.transform.position = to;
        if (bounce) yield return Kit.Tween(0.12f, k => { if (p != null) { float s = 1f + 0.08f * Mathf.Sin(k * Mathf.PI); p.visual.localScale = new Vector3(1f / s, s * 0.95f + 0.05f, 1); } });
        if (p != null) p.visual.localScale = Vector3.one;
    }

    // ======================================================= catnip spreading
    IEnumerator MintTick()
    {
        bool any = false;
        foreach (var p in grid) if (p != null && p.cover == Cover.Mint) { any = true; break; }
        if (!any) { mintCounter = 0; yield break; }
        if (mintHitThisMove) { mintCounter = 0; yield break; }
        mintCounter++;
        if (mintCounter == MintSpreadEvery - 1)
            foreach (var p in grid) if (p != null && p.cover == Cover.Mint) StartCoroutine(p.Pulse(0.12f, 0.5f));
        if (mintCounter < MintSpreadEvery) yield break;
        mintCounter = 0;

        var sources = new List<Piece>();
        foreach (var p in grid) if (p != null && p.cover == Cover.Mint) sources.Add(p);
        Shuffle(sources);
        foreach (var s in sources)
        {
            var targets = new List<Piece>();
            foreach (var n in Neighbours(s.x, s.y)) if (n.PlainCat) targets.Add(n);
            if (targets.Count == 0) continue;
            var t = targets[rng.Next(targets.Count)];
            // a leaf flies across, then the target gets catnip
            var leaf = new GameObject("leaf").AddComponent<SpriteRenderer>();
            var holder = new GameObject("leafHolder").transform; leaf.transform.SetParent(holder, false);
            Kit.FitSprite(leaf, "mint_leaf", cell * 0.45f, 1f); leaf.sortingOrder = FX.FxOrder;
            Vector3 a = s.transform.position, b = t.transform.position;
            yield return Kit.Tween(0.35f, k => { holder.position = Vector3.Lerp(a, b, k) + new Vector3(0, Mathf.Sin(k * Mathf.PI) * cell * 0.5f, 0); holder.Rotate(0, 0, 720 * Time.deltaTime); });
            Destroy(holder.gameObject);
            t.cover = Cover.Mint; t.Refresh();
            StartCoroutine(t.Pulse(0.2f));
            AudioManager.Play("freez", 0.5f);
            GoalHit?.Invoke(GoalType.Mint, new Vector3(float.NaN, 0, 0)); // NaN pos = spread (+1 to go)
            Toast?.Invoke("mint");
            break;
        }
    }

    // ======================================================= moves / hint / shuffle
    bool WouldMatchAt(int x, int y, int c)
    {
        int Col(int xx, int yy) { var p = At(xx, yy); return p == null ? -1 : p.MatchColor; }
        int h = 1; for (int i = x - 1; Col(i, y) == c; i--) h++; for (int i = x + 1; Col(i, y) == c; i++) h++;
        if (h >= 3) return true;
        int v = 1; for (int i = y - 1; Col(x, i) == c; i--) v++; for (int i = y + 1; Col(x, i) == c; i++) v++;
        if (v >= 3) return true;
        for (int dx = -1; dx <= 1; dx += 2)
            for (int dy = -1; dy <= 1; dy += 2)
                if (Col(x + dx, y) == c && Col(x, y + dy) == c && Col(x + dx, y + dy) == c) return true;
        return false;
    }

    bool SwapMakesRun(int ax, int ay, int bx, int by)
    {
        var a = grid[ax, ay]; var b = grid[bx, by];
        if (a == null || b == null || !a.Movable || !b.Movable) return false;
        if (a.special != Special.None || b.special != Special.None) return true;
        grid[ax, ay] = b; grid[bx, by] = a;
        bool ok = (b.MatchColor >= 0 && WouldMatchAt(ax, ay, b.MatchColor)) || (a.MatchColor >= 0 && WouldMatchAt(bx, by, a.MatchColor));
        grid[ax, ay] = a; grid[bx, by] = b;
        return ok;
    }

    bool FindMove(out Vector2Int a, out Vector2Int b)
    {
        for (int x = 0; x < cols; x++)
            for (int y = 0; y < rows; y++)
            {
                if (x + 1 < cols && SwapMakesRun(x, y, x + 1, y)) { a = new Vector2Int(x, y); b = new Vector2Int(x + 1, y); return true; }
                if (y + 1 < rows && SwapMakesRun(x, y, x, y + 1)) { a = new Vector2Int(x, y); b = new Vector2Int(x, y + 1); return true; }
            }
        a = b = default;
        return false;
    }

    bool HasPossibleMove()
    {
        foreach (var p in grid) if (p != null && p.special != Special.None && !p.dead) return true;
        return FindMove(out _, out _);
    }

    void ShowHint()
    {
        if (!FindMove(out var a, out var b)) return;
        var pa = At(a.x, a.y); var pb = At(b.x, b.y);
        if (pa != null) StartCoroutine(pa.Pulse(0.12f, 0.6f));
        if (pb != null) StartCoroutine(pb.Pulse(0.12f, 0.6f));
        hintShown = false; idleTimer = 2.5f; // repeat every few seconds while idle
    }

    void EnsureMovesExist(bool animate)
    {
        var cats = new List<Piece>();
        foreach (var p in grid) if (p != null && p.PlainCat) cats.Add(p);
        for (int attempt = 0; attempt < 60; attempt++)
        {
            if (FindRuns().Count == 0 && HasPossibleMove()) return;
            foreach (var p in cats) p.color = rng.Next(LevelGen.Colors);
            if (!animate) foreach (var p in cats) p.Refresh();
        }
        foreach (var p in cats) p.Refresh();
    }

    IEnumerator ShuffleBoard()
    {
        Toast?.Invoke("shuffle");
        var cats = new List<Piece>();
        foreach (var p in grid) if (p != null && p.PlainCat) cats.Add(p);
        yield return Kit.Tween(0.2f, k => { foreach (var p in cats) p.visual.localScale = Vector3.one * (1f - k); });
        EnsureMovesExist(true);
        foreach (var p in cats) p.Refresh();
        yield return Kit.Tween(0.25f, k => { foreach (var p in cats) p.visual.localScale = Vector3.one * Kit.EaseOutBack(k); });
        yield return ResolveLoop(null);
    }

    // For the HUD fly-in of a "collect colour" goal
    public string CatKey(int color) => "cat_" + color;

    // ---- dev/test hooks (used by the editor autoplay test) ----
    public bool DebugAutoMove()
    {
        if (Busy || InputLocked || grid == null) return false;
        if (!FindMove(out var a, out var b)) return false;
        StartCoroutine(TrySwap(a, b - a));
        return true;
    }
    // Balance-test bot that plays like a sensible casual player: looks at every legal swap and picks
    // the one that makes the best special / hits the most goal pieces (no look-ahead into cascades).
    public bool DebugSmartMove(System.Random rng)
    {
        if (Busy || InputLocked || grid == null) return false;
        float best = float.MinValue; Vector2Int ba = default, bb = default; bool any = false;
        for (int x = 0; x < cols; x++)
            for (int y = 0; y < rows; y++)
                for (int d = 0; d < 2; d++)
                {
                    int bx = x + (d == 0 ? 1 : 0), by = y + (d == 1 ? 1 : 0);
                    if (!In(bx, by) || !SwapMakesRun(x, y, bx, by)) continue;
                    float sc = ScoreSwap(x, y, bx, by) + (float)rng.NextDouble() * 2f;
                    if (sc > best) { best = sc; ba = new Vector2Int(x, y); bb = new Vector2Int(bx, by); any = true; }
                }
        if (!any) return false;
        StartCoroutine(TrySwap(ba, bb - ba));
        return true;
    }

    float ScoreSwap(int ax, int ay, int bx, int by)
    {
        var a = grid[ax, ay]; var b = grid[bx, by];
        int Prio(Piece p) => p == null ? 0 : TargetPriority != null ? TargetPriority(p) : (p.kind == PieceKind.Vase || p.cover != Cover.None ? 3 : 1);
        if (a.special != Special.None || b.special != Special.None)
        {
            float s = 45f;
            if (a.special != Special.None && b.special != Special.None) s += 70f;
            if (a.special == Special.Rainbow || b.special == Special.Rainbow) s += 15f;
            return s;
        }
        grid[ax, ay] = b; grid[bx, by] = a;
        float total = 0f;
        if (a.kind == PieceKind.Fish && by < ay) total += 18f;   // the swap itself moves a fish down a row
        if (b.kind == PieceKind.Fish && ay < by) total += 18f;
        foreach (var c in new[] { new Vector2Int(ax, ay), new Vector2Int(bx, by) })
        {
            var p = grid[c.x, c.y]; int col = p.MatchColor; if (col < 0) continue;
            int Col(int xx, int yy) { var q = At(xx, yy); return q == null ? -1 : q.MatchColor; }
            int l = c.x, r = c.x, dn = c.y, up = c.y;
            while (Col(l - 1, c.y) == col) l--; while (Col(r + 1, c.y) == col) r++;
            while (Col(c.x, dn - 1) == col) dn--; while (Col(c.x, up + 1) == col) up++;
            int h = r - l + 1, v = up - dn + 1;
            bool sq = false;
            for (int dx = -1; dx <= 1; dx += 2) for (int dy = -1; dy <= 1; dy += 2)
                if (Col(c.x + dx, c.y) == col && Col(c.x, c.y + dy) == col && Col(c.x + dx, c.y + dy) == col) sq = true;
            if (h < 3 && v < 3 && !sq) continue;
            float s = 10f;
            int run = Mathf.Max(h, v);
            if (run >= 5) s += 45f; else if (h >= 3 && v >= 3) s += 30f; else if (run == 4) s += 22f; else if (sq) s += 16f;
            // goal value of the matched cats and of everything next to them (covers / vases take the hit)
            var cells = new List<Vector2Int>();
            if (h >= 3) for (int i = l; i <= r; i++) cells.Add(new Vector2Int(i, c.y));
            if (v >= 3) for (int i = dn; i <= up; i++) cells.Add(new Vector2Int(c.x, i));
            if (cells.Count == 0) cells.Add(c);
            var seen = new HashSet<Vector2Int>();
            foreach (var m in cells)
            {
                s += Prio(At(m.x, m.y)) * 1.5f;
                foreach (var n in new[] { Vector2Int.left, Vector2Int.right, Vector2Int.up, Vector2Int.down })
                {
                    var q = m + n; if (!In(q.x, q.y) || !seen.Add(q)) continue;
                    var qp = At(q.x, q.y);
                    if (qp != null && (qp.kind == PieceKind.Vase || qp.cover != Cover.None)) s += Prio(qp);
                }
                for (int yy = m.y + 1; yy < rows; yy++) { var f = At(m.x, yy); if (f != null && f.kind == PieceKind.Fish) { s += 14f + Mathf.Max(0, 4 - (yy - m.y)) * 3f; break; } }   // clearing under a fish drops it
            }
            s += (rows - c.y) * 0.4f;   // lower moves cascade more
            total += s;
        }
        grid[ax, ay] = a; grid[bx, by] = b;
        return total;
    }

    public bool DebugMakeSpecial(int x, int y, Special sp, bool row)
    {
        var p = At(x, y);
        if (p == null || p.kind != PieceKind.Cat) return false;
        p.cover = Cover.None; p.special = sp; p.lineRow = row; p.Refresh();
        return true;
    }
    public bool DebugTap(int x, int y)
    {
        if (Busy || InputLocked || grid == null || At(x, y) == null) return false;
        StartCoroutine(Tap(new Vector2Int(x, y)));
        return true;
    }
    public string DebugDump()
    {
        var sb = new System.Text.StringBuilder();
        for (int y = rows - 1; y >= 0; y--)
        {
            for (int x = 0; x < cols; x++)
            {
                var p = grid[x, y];
                sb.Append(p == null ? '.' : p.kind == PieceKind.Vase ? 'V' : p.kind == PieceKind.Fish ? 'F' : p.special != Special.None ? p.special.ToString()[0] : p.cover == Cover.Box ? 'b' : p.cover == Cover.Yarn ? 'y' : p.cover == Cover.Mint ? 'm' : (char)('0' + p.color));
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
