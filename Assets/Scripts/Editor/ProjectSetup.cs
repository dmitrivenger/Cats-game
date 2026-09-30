using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Kuzya > Setup Scenes: creates Splash + Game scenes (each just a camera + one bootstrap
// component - everything else is built from code at runtime), registers them in Build
// Settings and sets the basic Player settings (name, portrait).
public static class ProjectSetup
{
    [MenuItem("Kuzya/Setup Scenes")]
    public static void SetupScenes()
    {
        GameArtBuilder.Build(false);
        MakeScene("Assets/Scenes/Splash.unity", typeof(SplashScreen), "Splash");
        MakeScene("Assets/Scenes/Game.unity", typeof(GameController), "Game");
        EditorBuildSettings.scenes = new[]
        {
            new EditorBuildSettingsScene("Assets/Scenes/Splash.unity", true),
            new EditorBuildSettingsScene("Assets/Scenes/Game.unity", true),
        };
        PlayerSettings.productName = "Кузя и Нафаня";
        PlayerSettings.companyName = "Dmitri Venger";
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.Portrait;
        PlayerSettings.allowedAutorotateToLandscapeLeft = false;
        PlayerSettings.allowedAutorotateToLandscapeRight = false;
        AssetDatabase.SaveAssets();
        EditorSceneManager.OpenScene("Assets/Scenes/Game.unity");
        Debug.Log("[Kuzya] Scenes ready: Splash -> Game.");
    }

    static void MakeScene(string path, System.Type bootstrap, string rootName)
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
        var cam = camGo.AddComponent<Camera>();
        cam.orthographic = true; cam.orthographicSize = 9.6f;
        cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = new Color(0.96f, 0.62f, 0.45f);
        camGo.transform.position = new Vector3(0, 0, -10);
        camGo.AddComponent<AudioListener>();
        var lightGo = new GameObject("Global Light 2D");
        var light = lightGo.AddComponent<UnityEngine.Rendering.Universal.Light2D>();
        light.lightType = UnityEngine.Rendering.Universal.Light2D.LightType.Global;
        light.intensity = 1f;
        var root = new GameObject(rootName);
        root.AddComponent(bootstrap);
        EditorSceneManager.SaveScene(scene, path);
    }
}
