using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

// Dev helper for automated playtests from the editor (play mode only).
public static class DevTest
{
    public static string Click(string buttonName)
    {
        foreach (var b in Object.FindObjectsByType<Button>(FindObjectsSortMode.None))
            if (b.name == buttonName && b.isActiveAndEnabled) { b.onClick.Invoke(); return "clicked " + buttonName; }
        return "no button " + buttonName;
    }

    public static string Buttons()
    {
        var sb = new StringBuilder();
        foreach (var b in Object.FindObjectsByType<Button>(FindObjectsSortMode.None))
            sb.Append(b.name).Append(b.interactable ? "" : "(off)").Append(' ');
        return sb.ToString();
    }

    public static string Move()
    {
        var board = Object.FindFirstObjectByType<Board>();
        if (board == null) return "no board";
        return board.DebugAutoMove() ? "moved" : (board.Busy ? "busy" : board.InputLocked ? "locked" : "no move");
    }

    public static string State()
    {
        var board = Object.FindFirstObjectByType<Board>();
        var texts = new StringBuilder();
        foreach (var t in Object.FindObjectsByType<Text>(FindObjectsSortMode.None))
            if (!string.IsNullOrEmpty(t.text) && t.isActiveAndEnabled) texts.Append(t.text.Replace("\n", " ")).Append(" | ");
        return "busy=" + (board ? board.Busy : false) + " locked=" + (board ? board.InputLocked : false) + "\nTEXT: " + texts + "\n" + (board ? board.DebugDump() : "");
    }

    public static string Shot(string name) => Capture.Shot("_claude_tmp/Screenshots/" + name + ".png", 1080, 1920);

    // ---- autoplay: keeps making hint moves; logs each end-of-level; snaps a few screenshots ----
    static double next; static int movesMade, shots; static bool on; public static string Log = "";
    public static string AutoPlay(bool enable)
    {
        on = enable; movesMade = 0; shots = 0; Log = "";
        EditorApplication.update -= Tick;
        if (enable) EditorApplication.update += Tick;
        return "autoplay " + enable;
    }
    static void Tick()
    {
        if (!EditorApplication.isPlaying) { EditorApplication.update -= Tick; return; }
        if (EditorApplication.timeSinceStartup < next) return;
        next = EditorApplication.timeSinceStartup + 0.6;
        var board = Object.FindFirstObjectByType<Board>();
        if (board == null) return;
        if (board.InputLocked)
        {
            foreach (var n in new[] { "btn_ui_next", "btn_ui_play" })
            {
                bool has = false;
                foreach (var b in Object.FindObjectsByType<Button>(FindObjectsSortMode.None)) if (b.name == n && b.isActiveAndEnabled && b.transform.parent != null && b.transform.parent.name == "window") has = true;
                if (has)
                {
                    if (n == "btn_ui_next") { Shot("auto_win_" + (shots++)); Log += "WIN after " + movesMade + " moves\n"; }
                    Click(n); next += 1.0; return;
                }
            }
            // lost: replay
            foreach (var b in Object.FindObjectsByType<Button>(FindObjectsSortMode.None))
                if (b.name == "btn_ui_replay" && b.isActiveAndEnabled && b.transform.parent != null && b.transform.parent.name == "window")
                { Shot("auto_lose_" + (shots++)); Log += "LOSE after " + movesMade + " moves\n"; b.onClick.Invoke(); next += 1.0; return; }
            return;
        }
        if (board.Busy) return;
        if (board.DebugAutoMove()) { movesMade++; if (movesMade % 7 == 3) Shot("auto_move_" + movesMade); }
    }

    // make one hint move and capture frames at the given times after it (seconds)
    static readonly System.Collections.Generic.List<(double at, string name)> queue = new System.Collections.Generic.List<(double, string)>();
    public static string MoveAndShoot(string prefix, float[] times)
    {
        var r = Move();
        double now = EditorApplication.timeSinceStartup;
        foreach (var t in times) queue.Add((now + t, prefix + "_" + Mathf.RoundToInt(t * 1000)));
        EditorApplication.update -= ShotTick; EditorApplication.update += ShotTick;
        return r;
    }
    public static string ClickAndShoot(string button, string prefix, float[] times)
    {
        var r = Click(button);
        double now = EditorApplication.timeSinceStartup;
        foreach (var t in times) queue.Add((now + t, prefix + "_" + Mathf.RoundToInt(t * 1000)));
        EditorApplication.update -= ShotTick; EditorApplication.update += ShotTick;
        return r;
    }
    public static string TapAndShoot(int x, int y, string prefix, float[] times)
    {
        var b = Object.FindFirstObjectByType<Board>();
        if (b == null || !b.DebugTap(x, y)) return "tap failed";
        double now = EditorApplication.timeSinceStartup;
        foreach (var t in times) queue.Add((now + t, prefix + "_" + Mathf.RoundToInt(t * 1000)));
        EditorApplication.update -= ShotTick; EditorApplication.update += ShotTick;
        return "ok";
    }
    // open the win/lose popup directly and capture frames of its animated backdrop
    public static string ResultAndShoot(bool win, string prefix, float[] times)
    {
        var gc = Object.FindFirstObjectByType<GameController>();
        if (gc == null) return "no controller";
        typeof(GameController).GetMethod("ShowResult", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(gc, new object[] { win });
        double now = EditorApplication.timeSinceStartup;
        foreach (var t in times) queue.Add((now + t, prefix + "_" + Mathf.RoundToInt(t * 1000)));
        EditorApplication.update -= ShotTick; EditorApplication.update += ShotTick;
        return "ok";
    }
    public static string ComboAndShoot(int tier, string prefix, float[] times)
    {
        var gc = Object.FindFirstObjectByType<GameController>();
        if (gc == null) return "no controller";
        var e = (System.Collections.IEnumerator)typeof(GameController).GetMethod("ComboBanner", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(gc, new object[] { tier });
        gc.StartCoroutine(e);
        double now = EditorApplication.timeSinceStartup;
        foreach (var t in times) queue.Add((now + t, prefix + "_" + Mathf.RoundToInt(t * 1000)));
        EditorApplication.update -= ShotTick; EditorApplication.update += ShotTick;
        return "ok";
    }
    public static string CaughtAndShoot(int reward, string prefix, float[] times)
    {
        var gc = Object.FindFirstObjectByType<GameController>();
        if (gc == null) return "no controller";
        var e = (System.Collections.IEnumerator)typeof(GameController).GetMethod("CaughtBanner", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(gc, new object[] { reward });
        gc.StartCoroutine(e);
        double now = EditorApplication.timeSinceStartup;
        foreach (var t in times) queue.Add((now + t, prefix + "_" + Mathf.RoundToInt(t * 1000)));
        EditorApplication.update -= ShotTick; EditorApplication.update += ShotTick;
        return "ok";
    }
    // same clock as the banner (unscaled dt clamped to 1/30), so frames line up even if the editor hitches
    public static string ComboFrames(int tier, string prefix, float[] times)
    {
        var gc = Object.FindFirstObjectByType<GameController>();
        if (gc == null) return "no controller";
        var e = (System.Collections.IEnumerator)typeof(GameController).GetMethod("ComboBanner", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(gc, new object[] { tier });
        gc.StartCoroutine(e);
        gc.StartCoroutine(ShootOnClock(prefix, times));
        return "ok";
    }
    static System.Collections.IEnumerator ShootOnClock(string prefix, float[] times)
    {
        float t = 0f; int i = 0;
        while (i < times.Length)
        {
            yield return null;
            t += Mathf.Min(Time.unscaledDeltaTime, 1f / 30f);
            while (i < times.Length && t >= times[i]) { Shot(prefix + "_" + Mathf.RoundToInt(times[i] * 1000)); i++; }
        }
    }
    // ---- balance simulation: plays levels from..to, `runs` times each, at high speed with the
    // sensible bot (or the naive first-move bot), no power-ups, no mercy; logs win/lose, moves left
    // and coins earned per attempt to _claude_tmp/sim.txt. Level progress and balance are restored.
    static bool simOn, simSmart; static int simLevel, simTo, simRun, simRuns, simStage, simSavedLevel, simSavedBal, simBalBefore, simMoves;
    static int simSavedStars, simSavedSpent; static readonly System.Collections.Generic.Dictionary<int, int> simStarKeys = new System.Collections.Generic.Dictionary<int, int>();
    static double simStageStart, simBusySince; static System.Random simRng; static StringBuilder simOut; public static string SimStatus = "";
    const System.Reflection.BindingFlags NP = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

    public static string Sim(int from, int to, int runs, bool smart, float speed)
    {
        var gc = Object.FindFirstObjectByType<GameController>();
        if (gc == null) return "no controller";
        simSavedLevel = Progress.Level; simSavedBal = Progress.Balance;
        simSavedStars = Progress.StarsTotal; simSavedSpent = Progress.ChestSpent; simStarKeys.Clear();
        for (int l = from; l <= to; l++) simStarKeys[l] = PlayerPrefs.GetInt("Kuzya.Stars." + l, -1);
        simLevel = from; simTo = to; simRuns = runs; simRun = 0; simStage = 0; simSmart = smart; simRng = new System.Random(12345);
        simOut = new StringBuilder("level\trun\twin\tmovesLeft\tmovesUsed\tcoins\tbudget\tgoals\n");
        Time.timeScale = speed; AudioListener.volume = 0f;
        simOn = true;
        EditorApplication.update -= SimTick; EditorApplication.update += SimTick;
        return "sim started";
    }

    public static string SimStop() { SimFinish("stopped"); return SimStatus; }

    static void SimFinish(string why)
    {
        simOn = false;
        EditorApplication.update -= SimTick;
        Time.timeScale = 1f; AudioListener.volume = 1f;
        Progress.Level = simSavedLevel;
        PlayerPrefs.SetInt("Kuzya.Balance", simSavedBal);
        PlayerPrefs.SetInt("Kuzya.StarsTotal", simSavedStars); PlayerPrefs.SetInt("Kuzya.ChestStars", simSavedSpent);
        foreach (var kv in simStarKeys) { if (kv.Value < 0) PlayerPrefs.DeleteKey("Kuzya.Stars." + kv.Key); else PlayerPrefs.SetInt("Kuzya.Stars." + kv.Key, kv.Value); }
        PlayerPrefs.Save();
        for (int i = 1; i < 500; i++) PlayerPrefs.DeleteKey(LevelGen.MercyKey(i));
        System.IO.Directory.CreateDirectory("_claude_tmp");
        if (simOut != null) System.IO.File.WriteAllText("_claude_tmp/sim.txt", simOut.ToString());
        SimStatus = why;
    }

    static void SimTick()
    {
        if (!simOn) return;
        if (!EditorApplication.isPlaying) { SimFinish("play mode ended"); return; }
        UnityEditorInternal.InternalEditorUtility.RepaintAllViews();   // keep frames coming even when the editor is in the background
        var gc = Object.FindFirstObjectByType<GameController>();
        var board = Object.FindFirstObjectByType<Board>();
        if (gc == null || board == null) return;
        var T = typeof(GameController);
        double now = EditorApplication.timeSinceStartup;
        if (simStage == 0)
        {
            PlayerPrefs.DeleteKey(LevelGen.MercyKey(simLevel));
            simBalBefore = Progress.Balance; simMoves = 0;
            T.GetMethod("StartLevel", NP).Invoke(gc, new object[] { simLevel });
            simStage = 1; simStageStart = now;
            return;
        }
        bool ended = (bool)T.GetField("ended", NP).GetValue(gc);
        if (simStage == 1)
        {
            if (now - simStageStart < 0.3) return;
            Click("btn_ui_play");
            simStage = 2; simStageStart = now;
            return;
        }
        if (!ended)
        {
            // stuck = busy for 1500 rendered frames (counting frames, not seconds: a locked screen pauses frames)
            if (board.Busy) { if (simBusySince == 0) simBusySince = Time.frameCount; } else simBusySince = 0;
            if (simBusySince > 0 && Time.frameCount - simBusySince > 1500)
            {   // a stuck board: note it with the board's last phase and carry on with the next attempt
                simOut.Append(simLevel).Append('\t').Append(simRun).Append("\tSTUCK phase=").Append(board.DebugPhase).Append(" busy=").Append(board.Busy).Append('\n')
                      .Append(board.DebugDump()).Append('\n');
                simRun++; if (simRun >= simRuns) { simRun = 0; simLevel++; }
                if (simLevel > simTo) { SimFinish("done"); return; }
                simStage = 0; simBusySince = 0; return;
            }
            if (board.Busy || board.InputLocked) return;
            bool moved = simSmart ? board.DebugSmartMove(simRng) : board.DebugAutoMove();
            if (moved) simMoves++;
            return;
        }
        // level over: give it a moment (last coins land), then record
        if (simStage == 2) { simStage = 3; simStageStart = now; return; }
        if (now - simStageStart < 0.25) return;
        bool win = (bool)T.GetMethod("AllGoalsDone", NP).Invoke(gc, null);
        int movesLeft = (int)T.GetField("moves", NP).GetValue(gc);
        var def = (LevelDef)T.GetField("def", NP).GetValue(gc);
        var goals = new StringBuilder();
        foreach (var g in def.goals) goals.Append(g.type).Append(':').Append(Mathf.Min(g.progress, g.target)).Append('/').Append(g.target).Append(' ');
        simOut.Append(simLevel).Append('\t').Append(simRun).Append('\t').Append(win ? 1 : 0).Append('\t').Append(movesLeft).Append('\t').Append(simMoves)
              .Append('\t').Append(Progress.Balance - simBalBefore).Append('\t').Append(def.moves).Append('\t').Append(goals).Append('\n');
        System.IO.Directory.CreateDirectory("_claude_tmp");
        System.IO.File.WriteAllText("_claude_tmp/sim.txt", simOut.ToString());
        SimStatus = "level " + simLevel + " run " + simRun;
        simRun++;
        if (simRun >= simRuns) { simRun = 0; simLevel++; }
        if (simLevel > simTo) { SimFinish("done"); return; }
        simStage = 0;
    }

    static void ShotTick()
    {
        double now = EditorApplication.timeSinceStartup;
        for (int i = queue.Count - 1; i >= 0; i--)
            if (now >= queue[i].at) { Shot(queue[i].name); queue.RemoveAt(i); }
        if (queue.Count == 0) EditorApplication.update -= ShotTick;
    }
}
