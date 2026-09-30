using System.IO;
using UnityEditor;
using UnityEngine;

// Dev helper: renders Camera.main (world + camera-space UI) to a PNG at phone resolution,
// independent of the Game view window. Kuzya > Capture Screenshot.
public static class Capture
{
    [MenuItem("Kuzya/Capture Screenshot")]
    public static void Menu() { Shot("_claude_tmp/Screenshots/capture.png", 1080, 1920); }

    public static string Shot(string path, int w, int h)
    {
        var cam = Camera.main;
        if (cam == null) return "no camera";
        // match the real Game view so camera-space canvases keep their live layout
        w = cam.pixelWidth; h = cam.pixelHeight;
        var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
        var prev = cam.targetTexture;
        cam.targetTexture = rt;
        Canvas.ForceUpdateCanvases();
        cam.Render();
        var old = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        tex.Apply();
        RenderTexture.active = old;
        cam.targetTexture = prev;
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        rt.Release(); Object.DestroyImmediate(rt);
        return path;
    }
}
