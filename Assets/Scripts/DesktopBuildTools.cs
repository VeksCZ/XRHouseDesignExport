#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

/// <summary>
/// The Windows desktop walk-through app: its scene (generated, so it never drifts from the scripts) and its build.
/// The scene is NOT in EditorBuildSettings - the Quest APK keeps building MRUKExportScene only; the Windows build
/// passes its own scene list. Batch: Unity.exe -batchmode -quit -projectPath . -executeMethod DesktopBuildTools.BuildWindowsBatch
/// </summary>
public static class DesktopBuildTools
{
    public const string ScenePath = "Assets/Scenes/DesktopWalkScene.unity";
    public const string ExePath = "Builds/Windows/XRHouseWalk.exe";

    [MenuItem("MRUK/Desktop/1. Create Desktop Walk Scene", false, 60)]
    public static void CreateSceneMenu()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        CreateScene();
        EditorSceneManager.OpenScene(ScenePath);
    }

    [MenuItem("MRUK/Desktop/2. Build Windows App", false, 61)]
    public static void BuildWindowsMenu()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        if (BuildWindows()) EditorUtility.RevealInFinder(ExePath);
    }

    [MenuItem("MRUK/Desktop/3. Run Windows App", false, 62)]
    public static void RunWindows()
    {
        if (!File.Exists(ExePath)) { Debug.LogError($"{ExePath} not built yet."); return; }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.GetFullPath(ExePath))
            { WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(ExePath)) });
    }

    public static void BuildWindowsBatch()
    {
        bool ok = BuildWindows();
        if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
    }

    /// <summary>(Re)creates the scene: keyboard/mouse rig with the camera, MRUK (loads nothing by itself - scans
    /// come from JSON), WalkThroughMode + DesktopWalkApp.</summary>
    public static void CreateScene()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var rigGo = new GameObject("DesktopRig");
        var rig = rigGo.AddComponent<DesktopWalkRig>();
        var camGo = new GameObject("Camera") { tag = "MainCamera" };
        camGo.transform.SetParent(rigGo.transform, false);
        camGo.transform.localPosition = new Vector3(0f, rig.eyeHeight, 0f);
        var cam = camGo.AddComponent<Camera>();
        cam.nearClipPlane = 0.05f;
        cam.farClipPlane = 300f;
        cam.fieldOfView = 75f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.62f, 0.72f, 0.82f);
        camGo.AddComponent<AudioListener>();
        rig.cam = cam;

#if META_XR_SDK_INSTALLED
        var mrukGo = new GameObject("MRUK");
        var mruk = mrukGo.AddComponent<MRUK>();
        mruk.SceneSettings ??= new MRUK.MRUKSettings();
        mruk.SceneSettings.LoadSceneOnStartup = false;
        mruk.SceneSettings.DataSource = MRUK.SceneDataSource.Json;
        mruk.EnableWorldLock = false;
#endif

        var appGo = new GameObject("DesktopWalkApp");
        var walk = appGo.AddComponent<WalkThroughMode>();
        var app = appGo.AddComponent<DesktopWalkApp>();
        app.walk = walk;
        app.rig = rig;

        Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
        EditorSceneManager.SaveScene(scene, ScenePath);
        Debug.Log($"[DesktopBuildTools] Scene saved: {ScenePath}");
    }

    /// <summary>Windows x64, Mono (quick builds; IL2CPP buys nothing here). Writes DataFolder.txt next to the exe
    /// pointing at this project's Exports/RoomData, so the app reads exactly what the Editor pulls from the Quest.</summary>
    public static bool BuildWindows()
    {
        if (!File.Exists(ScenePath)) CreateScene();
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
        Directory.CreateDirectory(Path.GetDirectoryName(ExePath));
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = ExePath,
            target = BuildTarget.StandaloneWindows64,
            targetGroup = BuildTargetGroup.Standalone,
            options = BuildOptions.None,
        });
        var s = report.summary;
        Debug.Log($"[DesktopBuildTools] {s.result}: {s.outputPath}, {s.totalSize / (1024f * 1024f):0.0} MB, {s.totalErrors} error(s), {s.totalTime}");
        if (s.result != BuildResult.Succeeded) { Debug.LogError("WINDOWS BUILD FAILED"); return false; }

        string data = Path.GetFullPath(MRUKPathUtility.GetExportRoot());
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(ExePath), MRUKPathUtility.DesktopDataFolderFile), data);
        Debug.Log($"WINDOWS BUILD COMPLETED SUCCESSFULLY - data folder: {data}");
        return true;
    }
}
#endif
