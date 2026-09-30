using System;
using System.Collections.Generic;
using UnityEngine;

// Single catalog of every sprite / sound / font the game uses, looked up by a short key
// ("cat_red", "box_full", "ui_play"...). Built automatically in the Editor by GameArtBuilder
// (Assets/Scripts/Editor) from the real files in Assets/Sprites, Assets/ui, Assets/Combos... -
// so dropping a new/updated PNG at the expected path is all it takes to get it into the game,
// no scene wiring. Missing art simply returns null and the game falls back to a placeholder.
//
// "content" is the visible (non-transparent) part of each sprite in 0..1 texture space. The
// generated art comes on very different canvases (cats on 1024/1254/1281/1305 px canvases with
// different padding), so pieces are scaled by what you can actually SEE, not by the canvas -
// that's what keeps every cat the same size on the board.
[CreateAssetMenu(fileName = "GameArt", menuName = "Kuzya/Game Art Catalog")]
public class GameArt : ScriptableObject
{
    [Serializable]
    public class SpriteEntry
    {
        public string key;
        public string path;
        public Sprite sprite;
        public Rect content = new Rect(0, 0, 1, 1);
        public long stamp;
    }

    [Serializable]
    public class ClipEntry
    {
        public string key;
        public AudioClip clip;
    }

    public List<SpriteEntry> sprites = new List<SpriteEntry>();
    public List<ClipEntry> clips = new List<ClipEntry>();
    public Font displayFont;   // Marmelad - titles / captions (has Cyrillic)
    public Font numberFont;    // Nunito Black - numbers and buttons

    Dictionary<string, SpriteEntry> spriteMap;
    Dictionary<string, AudioClip> clipMap;

    static GameArt instance;
    public static GameArt I
    {
        get
        {
            if (instance == null) instance = Resources.Load<GameArt>("GameArt");
            if (instance == null)
            {
                Debug.LogWarning("[GameArt] Resources/GameArt.asset not found - run Kuzya > Rebuild Art Catalog. Using placeholders.");
                instance = CreateInstance<GameArt>();
            }
            return instance;
        }
    }

    void EnsureMaps()
    {
        if (spriteMap != null) return;
        spriteMap = new Dictionary<string, SpriteEntry>();
        foreach (var e in sprites) if (e != null && !string.IsNullOrEmpty(e.key) && e.sprite != null) spriteMap[e.key] = e;
        clipMap = new Dictionary<string, AudioClip>();
        foreach (var c in clips) if (c != null && c.clip != null) clipMap[c.key] = c.clip;
    }

    public void InvalidateMaps() { spriteMap = null; clipMap = null; }

    public bool Has(string key) { EnsureMaps(); return spriteMap.ContainsKey(key); }

    public Sprite S(string key)
    {
        EnsureMaps();
        return key != null && spriteMap.TryGetValue(key, out var e) ? e.sprite : null;
    }

    public Rect Content(string key)
    {
        EnsureMaps();
        return key != null && spriteMap.TryGetValue(key, out var e) ? e.content : new Rect(0, 0, 1, 1);
    }

    public AudioClip Clip(string key)
    {
        EnsureMaps();
        return key != null && clipMap.TryGetValue(key, out var c) ? c : null;
    }

    public Font Display => displayFont != null ? displayFont : Font.CreateDynamicFontFromOSFont("Arial", 32);
    public Font Number => numberFont != null ? numberFont : Display;
}
