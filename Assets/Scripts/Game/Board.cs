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
    const int MilkLife = 5;                 // a puddle lasts exactly five moves after the one that spilled it
    bool[,] milkFresh;                      // spilled during the current move: does not dry at the end of it
    static bool Hittable(Piece n) => n.kind == PieceKind.Vase || n.kind == PieceKind.Milk || n.kind == PieceKind.House || n.cover == Cover.Box || n.cover == Cover.Mint || n.cover == Cover.Leash;
    // a cat that stays in its cell when its row is matched (the yarn / leash takes the hit instead)
    static bool Tied(Piece p) => p.cover == Cover.Yarn || p.cover == Cover.Leash;
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
    // laser dot: a red dot on one cell. Clear the cat standing on it to catch it; every move it jumps somewhere
    // else (always to a cell some move can clear). The level asks to catch it a few times.
    Vector2Int laserCell = new Vector2Int(-1, -1);
    int laserLeft;
    bool laserCaught;
    Transform laserT;
    public Vector2Int LaserCell => laserLeft > 0 ? laserCell : new Vector2Int(-1, -1);
    public int MouseCoins => mouseCoins;

    // ======================================================= setup
    const float ExitDrop = 0.62f;      // fish exit bowl: below the centre of the lowest cell, in cells
    const float ExitRoom = 0.45f;      // extra height kept under the grid for those bowls, in cells

    public void Setup(LevelDef level, Rect worldRect)
    {
        StopAllCoroutines();
        def = level;
        rng = new System.Random(level.level * 7349 + 3 + (int)(Time.realtimeSinceStartup * 1000) % 1000);
        // fish levels have little exit bowls under the bottom row: keep room for them inside the area,
        // otherwise they hang over the booster bar
        float below = level.fish > 0 ? ExitRoom : 0f;
        cell = Mathf.Min(worldRect.width / cols, worldRect.height / (rows + below));
        origin = new Vector2(worldRect.center.x - cell * cols * 0.5f, worldRect.center.y - cell * rows * 0.5f + cell * below * 0.5f);

        foreach (Transform c in transform) Destroy(c.gameObject);
        tilesRoot = new GameObject("Tiles").transform; tilesRoot.SetParent(transform, false);
        piecesRoot = new GameObject("Pieces").transform; piecesRoot.SetParent(transform, false);
        grid = new Piece[cols, rows];
        live = level.mask ?? FullMask();
        Armed = null; SetSwapPick(null); Busy = false; mintCounter = 0; fishToSpawn = 0; fishDelivered = 0;
        foam = new int[cols, rows]; foamSr = new SpriteRenderer[cols, rows];
        milk = new int[cols, rows]; milkSr = new SpriteRenderer[cols, rows]; milkFresh = new bool[cols, rows];
        tileSr = new SpriteRenderer[cols, rows]; tileBase = new Vector3[cols, rows];
        guiding = false; guideRoot = null; guideLit.Clear();
        mouseOn = null; mouseT = null; mouseBody = null; mouseSr = null; mousePile.Clear(); mouseCoins = 0; mouseMoves = 0; mouseHopping = false;
        laserCell = new Vector2Int(-1, -1); laserLeft = 0; laserCaught = false; laserT = null;

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
                tileSr[x, y] = t; tileBase[x, y] = t.transform.localScale;
            }

        if (def.fish > 0)
            for (int x = 0; x < cols; x++)
            {
                int ly = LowestLive(x);
                if (ly < 0) continue;
                var holder = new GameObject("exit").transform;
                holder.SetParent(tilesRoot, false);
                holder.position = CellPos(x, ly) + new Vector3(0, -cell * ExitDrop, 0);
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
        if (t == GoalType.Laser) return laserLeft;
        foreach (var p in grid)
        {
            if (p == null) continue;
            if (t == GoalType.Vase && p.kind == PieceKind.Vase) n++;
            else if (t == GoalType.Box && p.cover == Cover.Box) n++;
            else if (t == GoalType.Yarn && p.cover == Cover.Yarn) n++;
            else if (t == GoalType.Mint && p.cover == Cover.Mint) n++;
            else if (t == GoalType.Leash && p.cover == Cover.Leash) n++;
            else if (t == GoalType.House && p.HouseAnchor) n++;
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

        for (int i = 0; i < def.houses; i++) PlaceHouse();
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
        for (int i = 0; i < def.leashes; i++)
        {
            var c = Take(v => v.y < rows - 1);
            if (c.x < 0) break;
            var p = grid[c.x, c.y]; p.cover = Cover.Leash; p.coverHp = 2; p.Refresh();
        }
        // fish: first one on the top row, the rest arrive from the top as each is delivered
        if (def.fish > 0)
        {
            // prefer a column with a clear run to the bottom (no holes, no boxes / vases under the fish)
            int fx = rng.Next(cols); bool fishPlaced = false;
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
                if (placed) { fishPlaced = true; break; }
            }
            fishToSpawn = def.fish - (fishPlaced ? 1 : 0);
        }
        PlaceFoam();
        EnsureMovesExist(false);
        if (def.mouse) PlaceMouse();
        if (def.lasers > 0) { laserLeft = def.lasers; laserCell = PickLaserCell(); LaserVisual(true); }
    }

    // ---- cat house: a 2x2 piece of four cells. Every hit (a match next to it, a blast) takes one layer off;
    // after four the house falls apart and a kitten jumps out.
    const int HouseLayers = 4;
    void PlaceHouse()
    {
        var spots = new List<Vector2Int>();
        for (int x = 0; x + 1 < cols; x++)
            for (int y = 1; y + 2 < rows; y++)        // not on the bottom row, and never in the top row (cats must still drop in)
            {
                bool ok = true;
                for (int dx = 0; dx < 2 && ok; dx++)
                    for (int dy = 0; dy < 2 && ok; dy++)
                        ok = Live(x + dx, y + dy) && grid[x + dx, y + dy] != null && grid[x + dx, y + dy].PlainCat;
                if (ok) spots.Add(new Vector2Int(x, y));
            }
        if (spots.Count == 0) return;
        // keep it off the edges when possible: more cells around it to make matches in
        spots.Sort((a, b) => Edge(a).CompareTo(Edge(b)));
        int Edge(Vector2Int v) => (v.x == 0 || v.x + 1 == cols - 1 ? 1 : 0) + (v.y == 1 ? 1 : 0);
        int best = Edge(spots[0]); var good = spots.FindAll(v => Edge(v) == best);
        var at = good[rng.Next(good.Count)];
        Piece anchor = null;
        for (int dy = 0; dy < 2; dy++)
            for (int dx = 0; dx < 2; dx++)
            {
                int x = at.x + dx, y = at.y + dy;
                Destroy(grid[x, y].gameObject);
                var hp = NewPiece(PieceKind.House, -1, x, y);
                Put(hp, x, y);
                if (anchor == null) anchor = hp;
                hp.house = anchor;
            }
        anchor.houseHp = HouseLayers;
        foreach (var q in HousePieces(anchor)) q.Refresh();
    }

    List<Piece> HousePieces(Piece anchor)
    {
        var l = new List<Piece>();
        foreach (var q in grid) if (q != null && q.kind == PieceKind.House && q.house == anchor) l.Add(q);
        return l;
    }

    Vector3 HouseCentre(Piece a) => CellPos(a.x, a.y) + new Vector3(cell * 0.5f, cell * 0.5f, 0);

    void HitHouse(Piece any)
    {
        var a = any.house;
        if (a == null || a.dead) return;
        a.houseHp--;
        Vector3 c = HouseCentre(a);
        AudioManager.Play("Hit", 0.8f);
        FX.Burst(c, new Color(0.86f, 0.74f, 0.98f), cell * 1.4f, 10);     // lilac fluff
        FX.LandDust(c + new Vector3(0, -cell * 0.6f, 0), cell * 1.6f);
        FX.Shake(cell * 0.06f, 0.15f);
        Bonus?.Invoke(20);
        if (a.houseHp > 0) { a.Refresh(); StartCoroutine(HouseWobble(a)); return; }
        // the last layer: the house falls apart and a kitten jumps out
        foreach (var q in HousePieces(a)) { q.dead = true; grid[q.x, q.y] = null; }
        StartCoroutine(HouseBreak(a, c));
        GoalHit?.Invoke(GoalType.House, c);
    }

    IEnumerator HouseWobble(Piece a)
    {
        var v = a.visual; Vector3 pivot = new Vector3(cell * 0.5f, 0f, 0f);
        yield return Kit.Tween(0.45f, k =>
        {
            if (v == null) return;
            float w = Mathf.Exp(-k * 5f) * Mathf.Cos(k * Mathf.PI * 5f);
            v.localScale = new Vector3(1f + 0.08f * w, 1f - 0.1f * w, 1f);
            v.localPosition = pivot * (1f - v.localScale.x);              // squash around the house's own centre, feet on the floor
        });
        if (v != null) { v.localScale = Vector3.one; v.localPosition = Vector3.zero; }
    }

    IEnumerator HouseBreak(Piece a, Vector3 c)
    {
        AudioManager.Play("vase", 0.9f);
        FX.Burst(c, new Color(1f, 0.95f, 0.85f), cell * 2.2f, 18);
        FX.Shake(cell * 0.1f, 0.25f);
        StartCoroutine(KittenPop(c));
        var pieces = new List<Piece>(); foreach (var q in FindObjectsByType<Piece>(FindObjectsSortMode.None)) if (q.house == a) pieces.Add(q);
        yield return Kit.Tween(0.3f, k =>
        {
            if (a == null) return;
            float s = 1f + 0.25f * k; a.visual.localScale = new Vector3(s, s, 1f);
            a.visual.localPosition = new Vector3(cell * 0.5f, cell * 0.5f, 0) * (1f - s);
            var col = a.body.color; col.a = 1f - k; a.body.color = col;
        });
        foreach (var q in pieces) if (q != null) Destroy(q.gameObject);
    }

    IEnumerator KittenPop(Vector3 c)
    {
        if (!GameArt.I.Has("kitten_pop")) yield break;
        var holder = new GameObject("kitten").transform; holder.position = c;
        var sr = new GameObject("img").AddComponent<SpriteRenderer>(); sr.transform.SetParent(holder, false);
        Kit.FitSprite(sr, "kitten_pop", cell * 1.5f, 1f); sr.sortingOrder = FX.FxOrder + 2;
        AudioManager.Play("celebrate", 0.5f);
        yield return Kit.Tween(1.1f, k =>
        {
            if (holder == null) return;
            float up = Mathf.Sin(Mathf.Min(1f, k * 1.4f) * Mathf.PI * 0.5f);                  // jumps up and hangs there for a moment
            holder.position = c + new Vector3(Mathf.Sin(k * 9f) * cell * 0.05f, up * cell * 1.3f, 0);
            float s = Kit.EaseOutBack(Mathf.Min(1f, k * 3f)); holder.localScale = new Vector3(s, s, 1f);
            holder.rotation = Quaternion.Euler(0, 0, Mathf.Sin(k * 12f) * 8f);
            var col = sr.color; col.a = k < 0.75f ? 1f : 1f - (k - 0.75f) / 0.25f; sr.color = col;
        });
        if (holder != null) Destroy(holder.gameObject);
    }

    // ---- laser dot
    // the cells some move would clear right now (where the dot can land so it can always be caught)
    List<Vector2Int> ClearableCells()
    {
        var set = new HashSet<Vector2Int>();
        for (int x = 0; x < cols; x++)
            for (int y = 0; y < rows; y++)
                for (int d = 0; d < 2; d++)
                {
                    int bx = x + (d == 0 ? 1 : 0), by = y + (d == 1 ? 1 : 0);
                    if (!In(bx, by)) continue;
                    var a = grid[x, y]; var b = grid[bx, by];
                    if (a == null || b == null || !a.Movable || !b.Movable || a.special != Special.None || b.special != Special.None) continue;
                    grid[x, y] = b; grid[bx, by] = a;
                    if (a.MatchColor >= 0 && WouldMatchAt(bx, by, a.MatchColor)) set.Add(new Vector2Int(bx, by));
                    if (b.MatchColor >= 0 && WouldMatchAt(x, y, b.MatchColor)) set.Add(new Vector2Int(x, y));
                    grid[x, y] = a; grid[bx, by] = b;
                }
        return new List<Vector2Int>(set);
    }

    Vector2Int PickLaserCell()
    {
        var cands = ClearableCells();
        cands.RemoveAll(v => v == laserCell);
        if (cands.Count == 0)
        {   // no move clears anything (a shuffle is coming): any plain cat will do
            for (int x = 0; x < cols; x++) for (int y = 0; y < rows; y++) { var p = At(x, y); if (p != null && p.kind == PieceKind.Cat && new Vector2Int(x, y) != laserCell) cands.Add(new Vector2Int(x, y)); }
            if (cands.Count == 0) return laserCell;
        }
        // it runs away: prefer a spot at least two cells from where it was
        if (laserCell.x >= 0)
        {
            var far = cands.FindAll(v => Mathf.Abs(v.x - laserCell.x) + Mathf.Abs(v.y - laserCell.y) >= 3);
            if (far.Count > 0) cands = far;
        }
        return cands[rng.Next(cands.Count)];
    }

    SpriteRenderer laserGlow, laserCore, laserHalo;
    static readonly Vector3 LaserSpot = new Vector3(0.12f, -0.2f, 0f);     // on the cat's tummy, so the face stays visible
    void LaserVisual(bool appear)
    {
        if (laserT == null)
        {
            laserT = new GameObject("laser").transform; laserT.SetParent(transform, false);
            SpriteRenderer Dot(string n, int order) { var r = new GameObject(n).AddComponent<SpriteRenderer>(); r.transform.SetParent(laserT, false); r.sprite = Kit.SoftCircle(64); r.sortingOrder = order; return r; }
            laserHalo = Dot("halo", 15); laserGlow = Dot("glow", 16); laserCore = Dot("core", 17);
            float u = laserGlow.sprite.bounds.size.x;
            laserHalo.transform.localScale = Vector3.one * (cell * 0.75f / u);
            laserGlow.transform.localScale = Vector3.one * (cell * 0.36f / u);
            laserCore.transform.localScale = Vector3.one * (cell * 0.13f / u);
            laserCore.color = new Color(1f, 0.93f, 0.93f, 1f);
            StartCoroutine(LaserLive(laserT));
        }
        laserT.position = CellPos(laserCell.x, laserCell.y) + LaserSpot * cell;
        if (appear) StartCoroutine(PopIn(laserT));
    }

    // the dot never sits still: it trembles a little, its glow throbs and a faint ring breathes round it
    IEnumerator LaserLive(Transform t)
    {
        float seed = UnityEngine.Random.value * 10f;
        while (t != null)
        {
            float tm = Time.time + seed;
            if (laserGlow != null)
            {
                laserGlow.color = new Color(1f, 0.05f, 0.1f, 0.75f + 0.2f * Mathf.Sin(tm * 9f));
                float h = Mathf.Repeat(tm * 0.9f, 1f);
                laserHalo.color = new Color(1f, 0.15f, 0.2f, 0.35f * (1f - h));
                float u = laserHalo.sprite.bounds.size.x;
                laserHalo.transform.localScale = Vector3.one * (cell * Mathf.Lerp(0.35f, 0.9f, h) / u);
                var j = new Vector3(Mathf.Sin(tm * 13f), Mathf.Cos(tm * 11f), 0) * cell * 0.025f;
                laserGlow.transform.localPosition = laserCore.transform.localPosition = j;
            }
            yield return null;
        }
    }

    void LaserCatch(Vector3 pos)
    {
        if (laserLeft <= 0) return;
        laserLeft--;
        AudioManager.Play("mouse_caught", 0.6f);
        FX.Burst(pos, new Color(1f, 0.3f, 0.3f), cell, 12);
        GoalHit?.Invoke(GoalType.Laser, pos);
        if (laserT != null) StartCoroutine(LaserHide(laserT, laserLeft <= 0));
    }

    IEnumerator LaserHide(Transform t, bool gone)
    {
        yield return Kit.Tween(0.18f, k => { if (t != null) t.localScale = Vector3.one * (1f - k); });
        if (gone && t != null) { Destroy(t.gameObject); laserT = null; }
    }

    // after every move the dot darts to a new cell (caught or not)
    IEnumerator LaserJump()
    {
        if (laserLeft <= 0 || laserT == null) { laserCaught = false; yield break; }
        var to = PickLaserCell();
        bool wasCaught = laserCaught; laserCaught = false;
        Vector3 a = laserT.position, b = CellPos(to.x, to.y) + LaserSpot * cell;
        laserCell = to;
        if (wasCaught)
        {   // it was "pounced on": pops up again in its new spot
            laserT.position = b;
            yield return Kit.Tween(0.2f, k => { if (laserT != null) laserT.localScale = Vector3.one * Kit.EaseOutBack(k); });
            yield break;
        }
        Vector3 mid = (a + b) * 0.5f + new Vector3(0, cell * 0.8f, 0);
        yield return Kit.Tween(0.3f, k =>
        {
            if (laserT == null) return;
            laserT.position = Vector3.Lerp(Vector3.Lerp(a, mid, k), Vector3.Lerp(mid, b, k), Kit.EaseInOut(k));
        });
        if (laserT != null) laserT.position = b;
    }

    void Shuffle<T>(List<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); var t = list[i]; list[i] = list[j]; list[j] = t; }
    }

    // The board's entrance. While IntroHold is set (the level card is still open) the board stays empty;
    // then the tiles ripple out from the centre and the cats pour in from above, column after column,
    // each landing with a bounce.
    public bool IntroHold;

    IEnumerator IntroDrop()
    {
        Busy = false;
        var tiles = new List<SpriteRenderer>(); var tileA = new List<float>();
        foreach (Transform t in tilesRoot)
        {
            if (t.name != "tile" && t.name != "frame" && t.name != "foam") continue;
            var sr = t.GetComponent<SpriteRenderer>();
            if (sr == null) sr = t.GetComponentInChildren<SpriteRenderer>();
            if (sr == null) continue;
            tiles.Add(sr); tileA.Add(sr.color.a);
            var c = sr.color; c.a = 0f; sr.color = c;
        }
        var list = new List<Piece>();
        for (int x = 0; x < cols; x++)
            for (int y = 0; y < rows; y++)
            {
                var p = grid[x, y];
                if (p == null) continue;
                p.transform.position = CellPos(x, y) + new Vector3(0, 100f, 0);       // far off screen while waiting
                p.gameObject.SetActive(false);
                list.Add(p);
            }
        while (IntroHold) yield return null;

        Busy = true;
        Vector3 mid = (CellPos(0, 0) + CellPos(cols - 1, rows - 1)) * 0.5f;
        for (int i = 0; i < tiles.Count; i++)
            if (tiles[i] != null) StartCoroutine(TileIn(tiles[i], tileA[i], Vector3.Distance(tiles[i].transform.position, mid) / cell * 0.035f));
        yield return new WaitForSeconds(0.14f);
        float topY = CellPos(0, rows - 1).y + cell * 1.6f, last = 0f;
        foreach (var p in list)
        {
            if (p == null) continue;
            float delay = p.x * 0.06f + p.y * 0.04f;
            last = Mathf.Max(last, delay);
            StartCoroutine(DropIn(p, CellPos(p.x, p.y), topY, delay, p.y == LowestLive(p.x)));
        }
        StartCoroutine(DropSounds());
        yield return new WaitForSeconds(last + 0.6f);
        Busy = false;
    }

    // the cats landing on the board at the start: two soft drops, the second quieter
    IEnumerator DropSounds()
    {
        yield return new WaitForSeconds(0.15f);
        AudioManager.Play("cat_drop", 0.6f);
        yield return new WaitForSeconds(0.3f);
        AudioManager.Play("cat_drop", 0.35f);
    }

    // Special cats the player bought on the level card: they take the place of random plain cats.
    public void AddStartSpecials(List<Special> types)
    {
        if (types == null || types.Count == 0 || grid == null) return;
        var free = new List<Piece>(); var second = new List<Piece>();
        foreach (var p in grid)
        {
            if (p == null || !p.PlainCat || p == mouseOn) continue;
            if (foam[p.x, p.y] > 0) second.Add(p); else free.Add(p);
        }
        Shuffle(free); Shuffle(second); free.AddRange(second);
        for (int i = 0; i < types.Count && i < free.Count; i++)
        {
            var p = free[i];
            p.special = types[i]; p.lineRow = rng.Next(2) == 0; p.Refresh();
        }
    }

    IEnumerator TileIn(SpriteRenderer sr, float alpha, float delay)
    {
        if (delay > 0f) yield return new WaitForSeconds(delay);
        yield return Kit.Tween(0.22f, k => { if (sr != null) { var c = sr.color; c.a = alpha * k; sr.color = c; } });
    }

    IEnumerator DropIn(Piece p, Vector3 to, float topY, float delay, bool dust)
    {
        if (delay > 0f) yield return new WaitForSeconds(delay);
        if (p == null || p.dead) yield break;
        p.transform.position = new Vector3(to.x, Mathf.Max(topY, to.y + cell), 0);
        p.gameObject.SetActive(true);
        yield return MoveTo(p, to, 0.26f + 0.02f * (rows - p.y), true, dust);
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
            if ((Armed == null || Armed == PowerUpType.Swap) && d.magnitude > cell * 0.35f)
            {
                pressing = false;
                Vector2Int dir = Mathf.Abs(d.x) > Mathf.Abs(d.y) ? new Vector2Int(d.x > 0 ? 1 : -1, 0) : new Vector2Int(0, d.y > 0 ? 1 : -1);
                if (Armed == PowerUpType.Swap) SwapBoostSwipe(downCell, dir);
                else StartCoroutine(TrySwap(downCell, dir));
            }
        }
        else if (pressing && ptr.press.wasReleasedThisFrame)
        {
            pressing = false;
            if (canAct) StartCoroutine(Tap(downCell));
        }
    }

    void ResetIdle() { idleTimer = 0; hintShown = false; }

    public void ArmPowerUp(PowerUpType? t) { Armed = t; SetSwapPick(null); ResetIdle(); }

    // ======================================================= player actions
    IEnumerator Tap(Vector2Int c)
    {
        var p = At(c.x, c.y);
        if (p == null) yield break;
        if (guiding) { if (!guideTap || c != guideA) yield break; EndGuide(true); }
        if (Armed == PowerUpType.Swap) { SwapBoostTap(p); yield break; }
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
        if (guiding) { if (guideTap || !((a == guideA && b == guideB) || (a == guideB && b == guideA))) yield break; EndGuide(true); }
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
            if (aSp && bSp) { yield return MergeIntro(pa, pb); Combo(pa, pb, ctx); }
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

    // Two special cats swapped together: a swirl pulls them into one spot, they spin around each other
    // and fuse in a star flash (with a shake) - then the combined effect goes off.
    static Color SpecialTint(Special s)
    {
        switch (s)
        {
            case Special.Line: return FX.LineGlow;
            case Special.Bomb: return FX.BombGlow;
            case Special.Hunter: return FX.HunterGlow;
            default: return new Color(1f, 0.55f, 0.95f);   // rainbow
        }
    }

    IEnumerator MergeIntro(Piece a, Piece b)
    {
        Vector3 c = b.transform.position, a0 = a.transform.position - c;
        Color tint = SpecialTint(a.special), tint2 = SpecialTint(b.special);   // each cat keeps its own colour (mixing them gives grey)
        bool rainbow = a.special == Special.Rainbow || b.special == Special.Rainbow;
        const float Dur = 0.36f;
        FX.MergeSwirl(c, cell, tint, tint2, Dur, rainbow);
        AudioManager.Play("swap_3", 0.9f);
        a.SetOrder(22); b.SetOrder(21);
        float ang0 = Mathf.Atan2(a0.y, a0.x), rad0 = a0.magnitude;
        yield return Kit.Tween(Dur, k =>
        {
            float e = Kit.EaseInCubic(k), ang = ang0 + e * Mathf.PI * 2.5f;
            Vector3 dir = new Vector3(Mathf.Cos(ang), Mathf.Sin(ang), 0);
            a.transform.position = c + dir * rad0 * (1f - e);
            b.transform.position = c - dir * cell * 0.3f * Mathf.Sin(k * Mathf.PI);
            float s = 1f + 0.18f * Mathf.Sin(k * Mathf.PI) - 0.25f * e;
            a.visual.localScale = b.visual.localScale = new Vector3(s, s, 1);
        });
        a.transform.position = c; b.transform.position = c;
        a.visual.localScale = b.visual.localScale = Vector3.one;
        a.SetOrder(10); b.SetOrder(10);
        FX.MergeFlash(c, cell, tint, tint2, rainbow);
        FX.Shake(cell * 0.16f, 0.3f);
        yield return new WaitForSeconds(0.07f);     // a beat on the flash before everything goes off
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
        DebugPhase = "laser"; if (laserLeft > 0) yield return LaserJump();
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
                if (p.kind == PieceKind.Vase || p.kind == PieceKind.Milk || p.kind == PieceKind.House) ctx.hits.Add(p);
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
        if (laserCaught) yield return LaserJump();
        MoveResolved?.Invoke(clearedThisMove);
        Busy = false; quietPops = false;
    }

    // ---- swap booster: two neighbouring cats trade places, no match needed, no move spent
    Piece swapPick; GameObject swapMark;

    void SetSwapPick(Piece p)
    {
        swapPick = p;
        if (swapMark != null) Destroy(swapMark);
        if (p == null) return;
        var sr = new GameObject("swapPick").AddComponent<SpriteRenderer>();
        sr.sprite = Kit.SoftCircle(64); sr.sortingOrder = 9; sr.color = new Color(1f, 0.83f, 0.38f, 0.85f);
        sr.transform.SetParent(p.transform, false);
        swapMark = sr.gameObject;
        StartCoroutine(SwapPickPulse(p, sr));
        StartCoroutine(p.Wiggle());
        AudioManager.Play("click", 0.6f);
    }

    IEnumerator SwapPickPulse(Piece p, SpriteRenderer sr)
    {
        float baseS = cell * 1.25f / sr.sprite.bounds.size.x / Mathf.Max(0.001f, p.transform.lossyScale.x), t = 0;
        while (sr != null && swapPick == p)
        {
            t += Time.deltaTime;
            sr.transform.localScale = Vector3.one * baseS * (1f + 0.08f * Mathf.Sin(t * 6f));
            yield return null;
        }
    }

    void SwapBoostTap(Piece p)
    {
        if (!p.Movable) { StartCoroutine(p.Wiggle()); return; }
        if (swapPick == null || swapPick.dead) { SetSwapPick(p); return; }
        if (p == swapPick) { SetSwapPick(null); return; }
        if (Mathf.Abs(p.x - swapPick.x) + Mathf.Abs(p.y - swapPick.y) != 1) { SetSwapPick(p); return; }   // not a neighbour: pick this one instead
        var a = swapPick;
        StartCoroutine(UseSwap(a, p));
    }

    void SwapBoostSwipe(Vector2Int c, Vector2Int dir)
    {
        var a = At(c.x, c.y); var b = At(c.x + dir.x, c.y + dir.y);
        if (a == null) return;
        if (!a.Movable || b == null || !b.Movable) { StartCoroutine(a.Wiggle()); if (b != null) StartCoroutine(b.Wiggle()); return; }
        StartCoroutine(UseSwap(a, b));
    }

    IEnumerator UseSwap(Piece pa, Piece pb)
    {
        Busy = true; Armed = null; SetSwapPick(null);
        clearedThisMove = 0;
        Vector2Int a = new Vector2Int(pa.x, pa.y), b = new Vector2Int(pb.x, pb.y);
        // a little magic: sparkles on both, then the cats hop over each other on an arc
        FX.Burst(pa.transform.position, new Color(0.45f, 0.95f, 0.95f), cell, 8);
        FX.Burst(pb.transform.position, new Color(1f, 0.83f, 0.38f), cell, 8);
        AudioManager.Play("swap_" + UnityEngine.Random.Range(1, 4), 0.9f);
        Vector3 p0 = pa.transform.position, p1 = pb.transform.position;
        Vector3 side = Vector3.Cross(p1 - p0, Vector3.forward).normalized * cell * 0.45f;
        pa.SetOrder(20); pb.SetOrder(18);
        yield return Kit.Tween(0.3f, k =>
        {
            float arc = Mathf.Sin(k * Mathf.PI);
            pa.transform.position = Vector3.Lerp(p0, p1, k) + side * arc;
            pb.transform.position = Vector3.Lerp(p1, p0, k) - side * arc;
            float s = 1f + 0.15f * arc;
            pa.visual.localScale = pb.visual.localScale = new Vector3(s, s, 1);
        }, Kit.EaseInOut);
        pa.visual.localScale = pb.visual.localScale = Vector3.one;
        pa.SetOrder(10); pb.SetOrder(10);
        Put(pa, b.x, b.y); Put(pb, a.x, a.y);
        PowerUpUsed?.Invoke(PowerUpType.Swap);

        bool aSp = pa.special != Special.None, bSp = pb.special != Special.None;
        if (aSp && bSp)
        {   // two special cats brought together go off as a combo
            quietPops = true;
            var ctx = new Ctx { byPowerUp = true };
            yield return MergeIntro(pa, pb); Combo(pa, pb, ctx);
            yield return Execute(ctx, null);
            yield return Gravity();
        }
        yield return ResolveLoop(new HashSet<Vector2Int> { a, b });
        if (!HasPossibleMove()) yield return ShuffleBoard();
        if (laserCaught) yield return LaserJump();
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
    struct HunterShot { public int x, y, tx, ty; public float t; public bool bomb; }

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
        ctx.hunters.Add(new HunterShot { x = ox, y = oy, tx = best.x, ty = best.y, t = t0, bomb = carry == Special.Bomb });
        float hit = t0 + arrive;
        if (carry == Special.Line) LineShoot(best.x, best.y, rng.Next(2) == 0, hit, ctx);
        else if (carry == Special.Bomb)
        {   // the arrow carries a little bomb on its tip: the arrow takes the target out, then the bomb goes off there (5x5)
            Affect(best.x, best.y, ctx, hit);
            BombShoot(best.x, best.y, 2, hit + HunterBombFuse, ctx);
        }
        else Affect(best.x, best.y, ctx, hit);
    }

    const float HunterBombFuse = 0.14f;   // between the arrow's hit and the bomb going off

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
            foreach (var r in runs) { int n = 0; foreach (var p in r.cells) if (!Tied(p)) n++; lens.Add(Mathf.Max(n, 2)); }
            CascadeStep?.Invoke(step, lens);

            var ctx = new Ctx();
            var spawns = PlanSpawns(runs, preferred);
            foreach (var r in runs)
                foreach (var p in r.cells)
                {
                    if (p.cover == Cover.Yarn) ctx.yarn.Add(p);
                    else if (p.cover == Cover.Leash) ctx.hits.Add(p);
                    else { ctx.clear.Add(p); ctx.seed.Add(p); }
                    foreach (var n in Neighbours(p.x, p.y))
                        if (Hittable(n))
                        {
                            ctx.hits.Add(n);
                            if (n.kind == PieceKind.Milk && !ctx.hitFrom.ContainsKey(n)) ctx.hitFrom[n] = new Vector2Int(p.x, p.y);   // it tips away from the match
                        }
                }
            if (!quietPops) AudioManager.Play("Pop", 0.9f);      // the combo sound belongs to the combo banner
            // a glowing streak in the cats' colour sweeps along every matched line (a soft glow for a 2x2 square)
            foreach (var r in runs)
            {
                int rc = r.Color; Color tc = rc >= 0 ? CatFxColors[rc % CatFxColors.Length] : new Color(1f, .9f, .5f);
                if (tc.grayscale < 0.4f) tc = Color.Lerp(tc, new Color(0.75f, 0.6f, 1f), 0.6f);   // black cats: a violet glow reads better than a dark one
                Vector3 lo = r.cells[0].transform.position, hi = lo;
                foreach (var p in r.cells) { var q = p.transform.position; lo = Vector3.Min(lo, q); hi = Vector3.Max(hi, q); }
                FX.RunTrace(lo, hi, tc, cell, r.square);
            }
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
            if (preferred != null) foreach (var p in r.cells) if (preferred.Contains(new Vector2Int(p.x, p.y)) && !Tied(p)) { at = p; break; }
            if (at == null) foreach (var p in r.cells) if (!Tied(p)) { at = p; if (p == r.cells[r.cells.Count / 2]) break; }
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
            if (preferred != null) foreach (var p in r.cells) if (preferred.Contains(new Vector2Int(p.x, p.y)) && !Tied(p)) { at = p; break; }
            if (at == null) foreach (var p in r.cells) if (!Tied(p)) { at = p; break; }
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

    static string ComboEmblem(Special a, Special b)
    {
        bool Has(Special s) => a == s || b == s;
        if (a == Special.Hunter && b == Special.Hunter) return "combo_hh";
        if (Has(Special.Hunter)) return Has(Special.Bomb) ? "combo_hb" : Has(Special.Line) ? "combo_hl" : Has(Special.Rainbow) ? "combo_hr" : null;
        if (a == Special.Rainbow && b == Special.Rainbow) return "combo_rr";
        if (Has(Special.Rainbow)) return Has(Special.Bomb) ? "combo_rb" : Has(Special.Line) ? "combo_rl" : null;
        if (a == Special.Bomb && b == Special.Bomb) return "combo_bb";
        if (a == Special.Line && b == Special.Line) return "combo_ll";
        if (Has(Special.Line) && Has(Special.Bomb)) return "combo_lb";
        return null;
    }

    void Combo(Piece a, Piece b, Ctx ctx)
    {
        ctx.clear.Add(a); ctx.clear.Add(b); ctx.triggered.Add(a); ctx.triggered.Add(b);
        int cx = b.x, cy = b.y;
        var t1 = a.special; var t2 = b.special;
        bool Has(Special s) => t1 == s || t2 == s;
        Vector3 ep = CellPos(cx, cy);                       // kept fully on screen even for a merge at the edge
        ep.x = Mathf.Clamp(ep.x, WorldRect.xMin + cell * 1.1f, WorldRect.xMax - cell * 1.1f);
        FX.Emblem(ep, ComboEmblem(t1, t2), cell);
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
        foreach (var hs in ctx.hunters) FX.HunterBlast(CellPos(hs.x, hs.y), CellPos(hs.tx, hs.ty), cell, hs.t, hs.bomb);
        foreach (var rb in ctx.rainbows)
        {
            var pts = new List<Vector3>();
            foreach (var c in rb.cells) pts.Add(CellPos(c.x, c.y));
            FX.RainbowBlast(CellPos(rb.x, rb.y), cell, pts, rb.arrive, rb.orbs, rb.t);
        }
        float delay = 0f, latest = 0f;
        if (list.Count > 0) StartCoroutine(TileRipple(centre, Mathf.Clamp(0.05f + 0.012f * list.Count, 0.07f, 0.17f)));
        foreach (var p in list)
        {
            if (p.dead) continue;
            p.dead = true;
            grid[p.x, p.y] = null;
            clearedThisMove++;
            if (laserLeft > 0 && !laserCaught && p.x == laserCell.x && p.y == laserCell.y)
            {   // the cat on the red dot is cleared: the dot is caught
                laserCaught = true; Vector3 lp = CellPos(p.x, p.y); float lt3 = ctx.When(p);
                StartCoroutine(Later(lt3 > 0f ? lt3 : delay, () => LaserCatch(lp)));
            }
            if (p == mouseOn && mouseT != null)
            {   // the cat the mouse rides on is gone: caught!
                mouseOn = null; float lt2 = ctx.When(p);
                StartCoroutine(Later(lt2 > 0f ? lt2 : delay, CatchMouse));
            }
            if (milk[p.x, p.y] > 0 && p.kind == PieceKind.Cat)
            {   // the cat laps up the milk: a coin, that bit of the puddle is gone, and the cat counts twice for a colour task
                if (p.special == Special.None) CatCleared?.Invoke(p.color, p.transform.position);
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
            if (p.kind == PieceKind.Cat) StartCoroutine(TileFlash(p.x, p.y, p.special == Special.None ? p.color : -1, lt > 0f ? lt : delay));
            if (lt > 0f) { StartCoroutine(PopRoutine(p, lt)); latest = Mathf.Max(latest, lt); }
            else { StartCoroutine(PopRoutine(p, delay)); delay += 0.018f; }
        }

        // blockers take one hit each (on a line: when the beam gets there); a cat house takes one hit per blast,
        // however many of its four cells were reached
        var housesHit = new HashSet<Piece>();
        foreach (var h in ctx.hits)
            if (!h.dead)
            {
                if (h.kind == PieceKind.House && !housesHit.Add(h.house)) continue;
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

    static readonly Color[] CatFxColors = { new Color(.95f, .78f, .58f), new Color(1f, 1f, 1f), new Color(.75f, .75f, .8f), new Color(.35f, .3f, .4f), new Color(1f, .85f, .25f) };

    IEnumerator PopRoutine(Piece p, float delay)
    {
        if (delay > 0) yield return new WaitForSeconds(delay);
        Vector3 pos = p.transform.position;
        Color c = p.kind == PieceKind.Cat && p.color >= 0 ? CatFxColors[p.color % CatFxColors.Length] : new Color(1f, .9f, .5f);
        if (p.kind == PieceKind.Cat) StartCoroutine(Later(0.06f, () => FX.CatPop(pos, c, cell, p != null && p.special != Special.None ? 1.4f : 1f)));
        else FX.Burst(pos, c, cell, 8);
        var srs = p.GetComponentsInChildren<SpriteRenderer>();
        if (p.kind == PieceKind.Cat) FX.PopFlash(pos, c, cell);
        // anticipation: the cat squashes down for a moment, then bursts outwards and fades
        yield return Kit.Tween(0.06f, k => p.visual.localScale = new Vector3(1f + 0.2f * k, 1f - 0.25f * k, 1));
        yield return Kit.Tween(0.13f, k =>
        {
            float e = Kit.EaseOutCubic(k);
            p.visual.localScale = new Vector3(Mathf.Lerp(1.2f, 1.4f, e), Mathf.Lerp(0.75f, 1.4f, e), 1);
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
        if (h.kind == PieceKind.House) { HitHouse(h); return; }
        if (h.cover == Cover.Leash)
        {
            h.coverHp--;
            AudioManager.Play("Chain", 0.8f);
            if (h.coverHp <= 0) ReleaseCover(h, pos);
            else
            {   // first hit: the leash frays
                h.Refresh();
                StartCoroutine(h.Pulse(0.12f, 0.3f));
                FX.Burst(pos + new Vector3(0, -cell * 0.1f, 0), new Color(0.9f, 0.3f, 0.25f), cell * 0.8f, 6);
            }
            return;
        }
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
            if (h.coverHp <= 0) ReleaseCover(h, pos);
            else
            {   // first hit: the box crumples hard, a few scraps and a puff of dust fly off
                h.Refresh();
                StartCoroutine(h.BoxCrush());
                FX.Debris(pos + new Vector3(0, cell * 0.25f, 0), new[] { "box_piece_1", "box_piece_2", "box_piece_3" }, cell, 4);
                FX.LandDust(pos + new Vector3(0, cell * 0.3f, 0), cell);
                FX.Shake(cell * 0.06f, 0.15f);
            }
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
            StartCoroutine(Later(delay, () => { milk[v.x, v.y] = MilkLife; milkFresh[v.x, v.y] = true; MilkVisual(v.x, v.y, true); FX.MilkSplash(CellPos(v.x, v.y), cell, 2); }));
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
        var c = milkSr[x, y].color; c.a = Mathf.Lerp(0.6f, 1f, m / (float)MilkLife);
        if (foam[x, y] > 0) c.a *= 0.5f;   // milk spilled over foam: keep the blue foam readable through the puddle
        milkSr[x, y].color = c;
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
            {
                if (milk[x, y] <= 0) continue;
                if (milkFresh[x, y]) { milkFresh[x, y] = false; continue; }
                milk[x, y]--; MilkVisual(x, y, false);
            }
    }

    // ======================================================= win celebration
    // The level is won with moves to spare: every spare move sends a spark from the moves counter into a
    // random cat, which turns into a navigator or a demolition cat; then they go off one after another.
    public bool CelebrateSkip;
    public IEnumerator Celebrate(int count, Vector3 from, Action<Vector3> onFire)
    {
        Busy = true; quietPops = true; CelebrateSkip = false;
        var made = new List<Piece>();
        for (int i = 0; i < count && !CelebrateSkip; i++)
        {
            var cands = new List<Piece>();
            foreach (var p in grid) if (p != null && p.PlainCat && !made.Contains(p)) cands.Add(p);
            if (cands.Count == 0) break;
            var c = cands[rng.Next(cands.Count)];
            made.Add(c);
            StartCoroutine(Spark(from, c));
            yield return new WaitForSeconds(0.1f);
        }
        yield return new WaitForSeconds(0.35f);
        // go off in volleys of up to 3 so a long party stays short
        for (int i = 0; i < made.Count && !CelebrateSkip; i += 3)
        {
            var ctx = new Ctx(); bool any = false; tappedCross = null;
            for (int j = i; j < Mathf.Min(i + 3, made.Count); j++)
            {
                var p = made[j];
                if (p == null || p.dead || p.special == Special.None) continue;
                Trigger(p, ctx, false); any = true;   // keep each line its own random direction
                onFire?.Invoke(p.transform.position);
            }
            if (!any) continue;
            yield return Execute(ctx, null);
            yield return Gravity();
            yield return ResolveLoop(null);
        }
        Busy = false; quietPops = false;
    }

    IEnumerator Spark(Vector3 from, Piece target)
    {
        var sr = new GameObject("spark").AddComponent<SpriteRenderer>();
        sr.sprite = Kit.SoftCircle(64); sr.sortingOrder = FX.FxOrder + 4; sr.color = new Color(1f, 0.85f, 0.35f);
        float s = cell * 0.35f / sr.sprite.bounds.size.x; sr.transform.localScale = Vector3.one * s;
        from.z = 0; Vector3 mid = (from + target.transform.position) * 0.5f + new Vector3(0, cell * 1.2f, 0);
        AudioManager.Play("Pop", 0.4f);
        yield return Kit.Tween(0.3f, k =>
        {
            if (sr == null || target == null) return;
            Vector3 to = target.transform.position;
            sr.transform.position = Vector3.Lerp(Vector3.Lerp(from, mid, k), Vector3.Lerp(mid, to, k), k);
        });
        if (sr != null) Destroy(sr.gameObject);
        if (target == null || target.dead || !target.PlainCat) yield break;
        target.special = rng.Next(3) == 0 ? Special.Bomb : Special.Line; target.lineRow = rng.Next(2) == 0; target.Refresh();
        FX.Burst(target.transform.position, new Color(1f, 0.85f, 0.35f), cell, 8);
        StartCoroutine(PopIn(target));
    }

    // ======================================================= tutorial guide
    // One guided action: the board is dimmed, only the cats of that action stay bright, a paw shows what to
    // do, and nothing else can be moved until the player does it.
    bool guiding, guideTap;
    Vector2Int guideA, guideB;
    Transform guideRoot;
    readonly List<Piece> guideLit = new List<Piece>();
    public bool Guiding => guiding;
    public event Action GuideDone;

    void GuideRun(Piece mover, Vector2Int dest, Vector2Int from, HashSet<Piece> set)
    {
        if (mover == null) return;
        int c = mover.MatchColor; if (c < 0) return;
        foreach (var d in new[] { new Vector2Int(1, 0), new Vector2Int(0, 1) })
        {
            var run = new List<Piece>();
            for (int sgn = -1; sgn <= 1; sgn += 2)
                for (int i = 1; i < 5; i++)
                {
                    var q = dest + d * (i * sgn);
                    if (q == from || !Live(q.x, q.y)) break;
                    var p = At(q.x, q.y);
                    if (p == null || p.MatchColor != c) break;
                    run.Add(p);
                }
            if (run.Count >= 2) foreach (var p in run) set.Add(p);
        }
    }

    void BeginGuide(HashSet<Piece> lit, Vector3 from, Vector3 to, bool tap)
    {
        guiding = true; guideTap = tap;
        guideRoot = new GameObject("Guide").transform; guideRoot.SetParent(transform, false);
        var dim = new GameObject("dim").AddComponent<SpriteRenderer>(); dim.transform.SetParent(guideRoot, false);
        dim.sprite = Kit.RoundedRect(128, 8); dim.drawMode = SpriteDrawMode.Sliced; dim.size = new Vector2(60f, 60f);
        dim.color = new Color(0.04f, 0.05f, 0.14f, 0f); dim.sortingOrder = 44;
        guideLit.Clear();
        foreach (var p in lit) { p.SetOrder(50); guideLit.Add(p); }
        var hand = new GameObject("hand").transform; hand.SetParent(guideRoot, false);
        var hs = new GameObject("img").AddComponent<SpriteRenderer>(); hs.transform.SetParent(hand, false);
        Kit.FitSprite(hs, "ui_hand", cell * 1.25f, 1f, new Vector2(cell * 0.45f, -cell * 0.45f));   // the fingertip is the top-left of the art
        hs.sortingOrder = 58;
        StartCoroutine(GuideAnim(dim, hand, from, to, tap));
    }

    IEnumerator GuideAnim(SpriteRenderer dim, Transform hand, Vector3 from, Vector3 to, bool tap)
    {
        float t = 0f;
        while (guiding && dim != null)
        {
            t += Time.deltaTime;
            var c = dim.color; c.a = Mathf.Min(0.72f, t * 2.4f); dim.color = c;
            float u = Mathf.Repeat(t, 1.5f) / 1.5f;
            if (tap)
            {   // press, press, pause
                float press = Mathf.Max(0f, Mathf.Sin(u * Mathf.PI * 4f)) * (u < 0.5f ? 1f : 0f);
                hand.position = from + new Vector3(cell * 0.12f, -cell * 0.12f, 0) * (1f - press);
                float hsq = 1f - 0.08f * press; hand.localScale = new Vector3(hsq, hsq, 1f);
            }
            else
            {   // slides from one cat to the other, then starts again
                float k = u < 0.15f ? 0f : u < 0.7f ? Kit.EaseInOut((u - 0.15f) / 0.55f) : 1f;
                hand.position = Vector3.Lerp(from, to, k);
            }
            foreach (var p in guideLit) if (p != null && !p.dead) p.visual.localScale = Vector3.one * (1f + 0.05f * Mathf.Sin(t * 5f));
            yield return null;
        }
    }

    void EndGuide(bool done)
    {
        if (!guiding) return;
        guiding = false;
        foreach (var p in guideLit) if (p != null && !p.dead) { p.SetOrder(10); p.visual.localScale = Vector3.one; }
        guideLit.Clear();
        if (guideRoot != null) Destroy(guideRoot.gameObject);
        if (done) GuideDone?.Invoke();
    }

    // level 1: one swap that makes a match
    public bool GuideSwap()
    {
        if (guiding || !FindMove(out var a, out var b)) return false;
        var lit = new HashSet<Piece>();
        var pa = At(a.x, a.y); var pb = At(b.x, b.y);
        if (pa == null || pb == null) return false;
        lit.Add(pa); lit.Add(pb);
        GuideRun(pa, b, a, lit); GuideRun(pb, a, b, lit);
        guideA = a; guideB = b;
        // the paw drags the cat that completes the run
        var test = new HashSet<Piece>(); GuideRun(pa, b, a, test);
        bool aMoves = test.Count >= 2;
        BeginGuide(lit, CellPos(aMoves ? a.x : b.x, aMoves ? a.y : b.y), CellPos(aMoves ? b.x : a.x, aMoves ? b.y : a.y), false);
        return true;
    }

    // level 2: a special cat is put on the board and the player taps it
    public bool GuideTapSpecial(Special type)
    {
        if (guiding) return false;
        Piece best = null; float bestD = 1e9f;
        Vector2 mid = new Vector2((cols - 1) * 0.5f, (rows - 1) * 0.5f);
        foreach (var p in grid)
        {
            if (p == null || !p.PlainCat) continue;
            float d = Vector2.Distance(new Vector2(p.x, p.y), mid);
            if (d < bestD) { bestD = d; best = p; }
        }
        if (best == null) return false;
        best.special = type; best.lineRow = true; best.Refresh();
        StartCoroutine(PopIn(best));
        guideA = guideB = new Vector2Int(best.x, best.y);
        BeginGuide(new HashSet<Piece> { best }, CellPos(best.x, best.y), CellPos(best.x, best.y), true);
        return true;
    }

    // ======================================================= tiles react to a match
    // The cell under a cleared cat flashes in that cat's colour, a ripple runs through the neighbouring
    // tiles from the middle of the match, and a pale paw print stays on the cell for a second.
    SpriteRenderer[,] tileSr;
    Vector3[,] tileBase;
    static readonly Color[] TileFlashColors = { new Color(1f, .8f, .52f), new Color(.72f, .87f, 1f), new Color(.7f, .73f, .84f), new Color(.5f, .44f, .62f), new Color(1f, .85f, .28f) };
    static readonly Color PawPink = new Color(.93f, .6f, .62f);

    IEnumerator TileFlash(int x, int y, int color, float delay)
    {
        if (delay > 0f) yield return new WaitForSeconds(delay);
        if (tilesRoot == null) yield break;
        Color c = color >= 0 ? TileFlashColors[color % TileFlashColors.Length] : new Color(1f, .93f, .6f);
        Vector3 pos = CellPos(x, y);
        var glow = new GameObject("flash").AddComponent<SpriteRenderer>();
        glow.transform.SetParent(tilesRoot, false); glow.transform.position = pos;
        glow.sprite = Kit.RoundedRect(128, 30); glow.drawMode = SpriteDrawMode.Sliced;
        glow.size = new Vector2(cell * 0.97f, cell * 0.97f); glow.sortingOrder = 3;
        StartCoroutine(TilePaw(pos, c));           // the print shows at once, while the cell is still empty
        // a white blink first, then the cat's colour fading out
        yield return Kit.Tween(0.42f, k =>
        {
            if (glow == null) return;
            Color cc = Color.Lerp(Color.white, c, Mathf.Clamp01(k * 4f));
            cc.a = k < 0.15f ? 0.95f : 0.85f * (1f - (k - 0.15f) / 0.85f);
            glow.color = cc;
            float sc = 1f + 0.06f * Mathf.Sin(Mathf.Clamp01(k * 2f) * Mathf.PI);
            glow.transform.localScale = new Vector3(sc, sc, 1f);
        });
        if (glow != null) Destroy(glow.gameObject);
    }

    IEnumerator TilePaw(Vector3 pos, Color c)
    {
        // a random cat thing (fish bone, toy mouse, bell, feather...) is left on the cell; the paw is only the fallback
        string icon = "tile_icon_" + UnityEngine.Random.Range(1, 9);
        if (!GameArt.I.Has(icon)) icon = "fx_paw";
        if (!GameArt.I.Has(icon) || tilesRoot == null) yield break;
        var holder = new GameObject("pawprint").transform; holder.SetParent(tilesRoot, false);
        holder.position = pos; holder.localRotation = Quaternion.Euler(0, 0, UnityEngine.Random.Range(-14f, 14f));
        // drawn above the cats, so it is still seen after new cats have dropped into the cell. The glossy art
        // keeps its own colours (only the paw fallback is tinted).
        var paw = new GameObject("img").AddComponent<SpriteRenderer>(); paw.transform.SetParent(holder, false);
        Kit.FitSprite(paw, icon, cell * 0.5f, 1f); paw.sortingOrder = 41;
        Color pc = icon == "fx_paw" ? Color.Lerp(c, PawPink, 0.55f) : Color.white;
        yield return Kit.Tween(1.5f, k =>
        {
            if (paw == null) return;
            // pops in, holds for a moment, then fades while it drifts up a little
            float a = k < 0.08f ? k / 0.08f : k < 0.45f ? 1f : 1f - (k - 0.45f) / 0.55f;
            pc.a = a * 0.7f; paw.color = pc;          // 30% see-through at full strength
            holder.position = pos + new Vector3(0, cell * 0.14f * k, 0);
            float pop = k < 0.12f ? Kit.EaseOutBack(k / 0.12f) : 1f;
            holder.localScale = new Vector3(pop, pop, 1f);
        });
        if (holder != null) Destroy(holder.gameObject);
    }

    IEnumerator TileRipple(Vector3 from, float amp)
    {
        if (tileSr == null) yield break;
        const float step = 0.045f, life = 0.26f, reach = 5.5f;
        float total = reach * step + life, age = 0f;
        var tiles = tileSr; var bases = tileBase;
        while (age < total)
        {
            age += Time.deltaTime;
            for (int x = 0; x < cols; x++)
                for (int y = 0; y < rows; y++)
                {
                    var t = tiles[x, y]; if (t == null) continue;
                    float d = Vector3.Distance(CellPos(x, y), from) / cell;
                    if (d > reach) continue;
                    float k = (age - d * step) / life;
                    float f = k <= 0f || k >= 1f ? 0f : Mathf.Sin(k * Mathf.PI) * amp * Mathf.Exp(-d * 0.42f);
                    t.transform.localScale = bases[x, y] * (1f + f);
                }
            yield return null;
        }
        for (int x = 0; x < cols; x++) for (int y = 0; y < rows; y++) if (tiles[x, y] != null) tiles[x, y].transform.localScale = bases[x, y];
    }

    // ======================================================= mouse thief
    const float MouseSize = 0.816f;   // +20%
    // sitting in the middle of the cat: the art's tail sticks out to the left, so the picture is shifted left
    // a little to put the mouse's body (not the middle of the picture) on the cat's centre
    Vector3 MouseOffset => new Vector3(-cell * 0.11f, cell * 0.02f, 0f);
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
        BuildMouseParts();
        MouseIdleLook();
        mouseT.position = mouseOn.transform.position + MouseOffset;
        mouseFace = 1;
    }

    // ---- the mouse comes alive: its tail (a separate piece of the art) swishes, a soft glow keeps it
    //      readable on grey cats, it fidgets now and then and the coin in its sack glints
    Transform mouseTail, mouseGlint; SpriteRenderer mouseTailSr, mouseGlowSr, mouseGlintSr;
    float mouseNextFidget, mouseFidgetT = -1f;

    void BuildMouseParts()
    {   // the new mouse art is complete (tail and sack drawn in); only the fidget timer is set up here
        mouseTail = null; mouseGlint = null; mouseGlowSr = null;
        mouseNextFidget = Time.time + UnityEngine.Random.Range(1.5f, 3f); mouseFidgetT = -1f;
    }

    // running: four frames of the run cycle, a trail of yellow smoke and bits of cheese behind
    float mouseTrailNext;
    void MouseRunFrame(SpriteRenderer sr, Transform at, float dir)
    {
        if (sr == null) return;
        int f = 1 + (int)(Time.time * 14f) % 4;
        string key = GameArt.I.Has("mouse_run_" + f) ? "mouse_run_" + f : "mouse_run";
        if (sr.sprite != GameArt.I.S(key)) Kit.FitSpriteAs(sr, key, "mouse_run_1", cell * MouseSize, 1f);
        if (at != null && Time.time >= mouseTrailNext)
        {
            mouseTrailNext = Time.time + 0.04f;
            FX.MouseTrail(at.position + new Vector3(-dir * cell * 0.22f, -cell * 0.2f, 0), cell, dir);
        }
    }

    void MouseIdleLook()
    {
        if (mouseSr == null) return;
        Kit.FitSprite(mouseSr, "mouse_idle", cell * MouseSize, 1f);
        if (mouseTail != null) mouseTail.gameObject.SetActive(true);
        if (mouseGlint != null) mouseGlint.gameObject.SetActive(true);
    }

    static void MouseRunLook(Transform body)
    {   // the running / caught art has its own tail and sack
        if (body == null) return;
        var tl = body.Find("tail"); if (tl != null) tl.gameObject.SetActive(false);
        var gl = body.Find("glint"); if (gl != null) gl.gameObject.SetActive(false);
    }

    void LateUpdate()
    {
        if (mouseT == null) return;
        if (!mouseHopping && mouseOn != null) mouseT.position = mouseOn.transform.position + MouseOffset;
        float tm = Time.time;
        if (mouseGlowSr != null) mouseGlowSr.color = new Color(1f, 0.97f, 0.85f, 0.32f + 0.08f * Mathf.Sin(tm * 2.2f));
        if (mouseTail != null) mouseTail.localRotation = Quaternion.Euler(0, 0, 9f * Mathf.Sin(tm * 3.1f) + 4f * Mathf.Sin(tm * 7.3f));
        if (mouseGlintSr != null)
        {
            float g = Mathf.Max(0f, Mathf.Sin(tm * 2.6f)); g = g * g * g;
            mouseGlintSr.color = new Color(1f, 0.95f, 0.6f, g);
            mouseGlint.localScale = Vector3.one * (0.5f + 0.7f * g);
            mouseGlint.localRotation = Quaternion.Euler(0, 0, tm * 90f);
        }
        if (mouseBody != null && !mouseHopping)
        {   // idle: breathes and looks a bit shifty; now and then a nervous little bounce with a glance round
            float s = Mathf.Sin(tm * 5f);
            float jump = 0f, glance = 0f;
            if (mouseFidgetT < 0f && tm > mouseNextFidget) { mouseFidgetT = 0f; mouseNextFidget = tm + UnityEngine.Random.Range(2.5f, 5f); }
            if (mouseFidgetT >= 0f)
            {
                mouseFidgetT += Time.deltaTime; float k = mouseFidgetT / 0.7f;
                if (k >= 1f) mouseFidgetT = -1f;
                else { jump = Mathf.Max(0f, Mathf.Sin(Mathf.Min(1f, k * 2.2f) * Mathf.PI)); glance = Mathf.Sin(k * Mathf.PI * 3f) * (1f - k); }
            }
            mouseBody.localScale = new Vector3(mouseFace * (1f + 0.04f * s - 0.06f * jump), 1f - 0.04f * s + 0.08f * jump, 1f);
            mouseBody.localRotation = Quaternion.Euler(0, 0, 4f * Mathf.Sin(tm * 1.7f) + 12f * glance);
            mouseBody.localPosition = new Vector3(0, cell * 0.08f * jump, 0);
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
        MouseRunLook(mouseBody);
        mouseBody.localRotation = Quaternion.identity; mouseBody.localPosition = Vector3.zero;
        AudioManager.Play("mouse_hop", 0.35f);
        yield return Kit.Tween(0.42f, k =>
        {
            if (mouseT == null) return;
            Vector3 b = (mouseOn != null ? mouseOn.transform.position : a - MouseOffset) + MouseOffset;
            mouseT.position = Vector3.Lerp(a, b, k) + new Vector3(0, cell * 0.25f * Mathf.Sin(k * Mathf.PI), 0);
            MouseRunFrame(mouseSr, mouseT, mouseFace);
            float sq = k > 0.85f ? 1f - 0.15f * Mathf.Sin((k - 0.85f) / 0.15f * Mathf.PI) : 1f;
            if (mouseBody != null) mouseBody.localScale = new Vector3(mouseFace * (2f - sq), sq, 1f);
        });
        MouseIdleLook();
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
            holder.localPosition = new Vector3(cell * (0.08f + 0.06f * (i % 3)), cell * (-0.2f + 0.05f * (i / 3)), 0);
            var sr = new GameObject("img").AddComponent<SpriteRenderer>(); sr.transform.SetParent(holder, false);
            Kit.FitSprite(sr, "ui_balance", cell * 0.16f, 1f);   // FitSprite owns the child's scale; PopIn animates the holder
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
        if (sr != null) Kit.FitSprite(sr, "mouse_caught", cell * 0.65f, 1f);
        MouseRunLook(body);
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
        MouseRunLook(body);
        if (body != null) { body.localScale = new Vector3(dir, 1f, 1f); body.localRotation = Quaternion.identity; body.localPosition = Vector3.zero; }
        AudioManager.Play("mouse_run", 0.8f);
        Vector3 a = t.position;
        float dist = Mathf.Abs((dir < 0 ? WorldRect.xMin : WorldRect.xMax) - a.x) + cell * 2f;
        yield return Kit.Tween(1.1f, k =>
        {
            if (t == null) return;
            t.position = a + new Vector3(dir * dist * Kit.EaseInCubic(k) * 0.6f + dir * dist * 0.4f * k, cell * 0.12f * Mathf.Abs(Mathf.Sin(k * Mathf.PI * 5f)), 0);
            MouseRunFrame(sr, t, dir);
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
                // the box bursts: scraps fly out fast, more of them flutter down, dust
                FX.Debris(pos + new Vector3(0, cell * 0.2f, 0), new[] { "box_piece_1", "box_piece_2", "box_piece_3" }, cell, 6);
                FX.LeafFall(pos + new Vector3(0, cell * 0.2f, 0), new[] { "box_piece_1", "box_piece_2", "box_piece_3" }, cell * 1.3f, 9);
                FX.LandDust(pos + new Vector3(0, cell * 0.25f, 0), cell);
                FX.Shake(cell * 0.08f, 0.18f);
                Bonus?.Invoke(20); GoalHit?.Invoke(GoalType.Box, pos); break;
            case Cover.Yarn:
                AudioManager.Play("Chain", 0.8f);
                FX.LeafFall(pos, new[] { "yarn_thread_1", "yarn_thread_3", "yarn_thread_4", "yarn_thread_2", "yarn_thread_3", "yarn_thread_4" }, cell, 13);   // the strand snaps into bits that flutter down
                Bonus?.Invoke(20); GoalHit?.Invoke(GoalType.Yarn, pos); break;
            case Cover.Leash:
                // the leash snaps: bits of red strap fly off, the bell jingles away
                AudioManager.Play("Chain", 0.9f);
                FX.Burst(pos + new Vector3(0, -cell * 0.1f, 0), new Color(0.92f, 0.28f, 0.22f), cell * 1.1f, 12);
                FX.Burst(pos + new Vector3(-cell * 0.2f, -cell * 0.3f, 0), new Color(1f, 0.84f, 0.3f), cell * 0.6f, 5);
                Bonus?.Invoke(20); GoalHit?.Invoke(GoalType.Leash, pos); break;
            case Cover.Mint:
                AudioManager.Play("mint_off", 0.8f);
                FX.LeafFall(pos, "mint_leaf", cell, 14);
                FX.Steam(pos, cell);                                // the green cloud rushes up like steam
                FX.Burst(pos + new Vector3(cell * 0.25f, cell * 0.3f, 0), new Color(0.75f, 0.92f, 1f), cell * 0.7f, 8);   // the "Zzz" pops: the cat is awake
                mintHitThisMove = true;
                Bonus?.Invoke(20); GoalHit?.Invoke(GoalType.Mint, pos); break;
        }
    }

    // ======================================================= gravity & refill
    IEnumerator Gravity()
    {
        while (true)
        {
            float longest = 0f; dustCols.Clear();
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
                            StartCoroutine(MoveTo(p, CellPos(x, write), d, true, dustCols.Add(x)));   // one dust puff per column: under the lowest cat that fell
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
                // the bottom of the shape, a hole in the shape right under it, or only static pieces below
                bool bottom = fy == 0 || !live[x, fy - 1];
                if (!bottom) { bottom = true; for (int y = 0; y < fy; y++) if (!Blocked(x, y)) { bottom = false; break; } }
                if (bottom)
                {
                    delivered = true;
                    grid[x, fy] = null; p.dead = true;
                    Vector3 pos = p.transform.position;
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
        AudioManager.Play("fish_plop", 0.75f);
        yield return Kit.Tween(0.3f, k => { float s = 1f - k; p.visual.localScale = new Vector3(s, s, 1); p.visual.localPosition = new Vector3(0, -cell * 0.5f * k, 0); });
        Destroy(p.gameObject);
    }

    float MoveDuration(int cellsDown) => 0.12f + 0.055f * Mathf.Sqrt(Mathf.Max(1, cellsDown)) * 2f;

    readonly HashSet<int> dustCols = new HashSet<int>();
    IEnumerator MoveTo(Piece p, Vector3 to, float dur, bool bounce, bool dust = false)
    {
        Vector3 from = p.transform.position;
        yield return Kit.Tween(dur, k => { if (p != null) p.transform.position = Vector3.LerpUnclamped(from, to, bounce ? Kit.EaseInCubic(k) : k); });
        if (p == null) yield break;
        p.transform.position = to;
        if (bounce)
        {
            if (dust) FX.LandDust(to + new Vector3(0, -cell * 0.38f, 0), cell);
            // lands flat and wide, springs back up a little too tall, settles (the feet stay on the floor)
            float drop = Mathf.Clamp01((from.y - to.y) / (cell * 3f));
            float amp = 0.1f + 0.1f * drop;
            yield return Kit.Tween(0.24f, k =>
            {
                if (p == null) return;
                float w = Mathf.Exp(-k * 3.2f) * Mathf.Cos(k * Mathf.PI * 2.2f);   // damped spring: +1 -> flat, negative -> stretched
                float sy = 1f - amp * w, sx = 1f + amp * 0.8f * w;
                p.visual.localScale = new Vector3(sx, sy, 1);
                p.visual.localPosition = new Vector3(0, -(1f - sy) * cell * 0.42f, 0);
            });
            if (p != null) p.visual.localPosition = Vector3.zero;
        }
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
        cats.Sort((a, b) => a.y != b.y ? a.y.CompareTo(b.y) : a.x.CompareTo(b.x));
        int Col(int xx, int yy) => xx >= 0 && yy >= 0 && grid[xx, yy] != null ? grid[xx, yy].color : -1;
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (FindRuns().Count == 0 && HasPossibleMove()) break;
            // new colours that avoid ready-made runs with the cats already coloured (left / below)
            foreach (var p in cats)
            {
                int x = p.x, y = p.y, c, guard = 0;
                do { c = rng.Next(LevelGen.Colors); guard++; }
                while (guard < 20 && ((Col(x - 1, y) == c && Col(x - 2, y) == c) || (Col(x, y - 1) == c && Col(x, y - 2) == c)));
                p.color = c;
            }
        }
        foreach (var p in cats) p.Refresh();
    }

    IEnumerator ShuffleBoard()
    {
        Toast?.Invoke("shuffle");
        AudioManager.Play("board_shuffle", 0.8f);
        var cats = new List<Piece>();
        foreach (var p in grid) if (p != null && p.PlainCat) cats.Add(p);
        yield return Kit.Tween(0.2f, k => { foreach (var p in cats) p.visual.localScale = Vector3.one * (1f - k); });
        EnsureMovesExist(true);
        foreach (var p in cats) p.Refresh();
        yield return Kit.Tween(0.25f, k => { foreach (var p in cats) p.visual.localScale = Vector3.one * Kit.EaseOutBack(k); });
        yield return ResolveLoop(null);
        // if the shuffle still left no move (or a cascade took it away), shuffle again - a few times at most
        if (!HasPossibleMove() && shuffleDepth < 3) { shuffleDepth++; yield return ShuffleBoard(); shuffleDepth--; }
    }
    int shuffleDepth;

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
        int Prio(Piece p) => p == null ? 0 : TargetPriority != null ? TargetPriority(p) : (p.kind == PieceKind.Vase || p.kind == PieceKind.House || p.cover != Cover.None ? 3 : 1);
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
                    if (qp != null && (qp.kind == PieceKind.Vase || qp.kind == PieceKind.House || qp.cover != Cover.None || (qp.x == laserCell.x && qp.y == laserCell.y && laserLeft > 0))) s += Prio(qp);
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
                sb.Append(p == null ? '.' : p.kind == PieceKind.Vase ? 'V' : p.kind == PieceKind.House ? 'H' : p.kind == PieceKind.Fish ? 'F' : p.cover == Cover.Leash ? 'l' : p.special != Special.None ? p.special.ToString()[0] : p.cover == Cover.Box ? 'b' : p.cover == Cover.Yarn ? 'y' : p.cover == Cover.Mint ? 'm' : (char)('0' + p.color));
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
