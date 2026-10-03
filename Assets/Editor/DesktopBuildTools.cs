using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEngine;
using Debug = UnityEngine.Debug;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

/// <summary>
/// The Windows desktop walk-through app: its scene (generated, so it never drifts from the scripts) and its build.
///
/// The build never runs in this project's Library: switching this editor between Android and Windows reimports
/// every texture/shader for the other platform each way, so the next Quest build after a Windows build was slow.
/// Instead it runs in a "shadow" project next to this one (../XRHouseDesignExport_WinBuild) whose Assets, Packages
/// and ProjectSettings are junctions to this project's - same sources, its own Library that stays on Windows.
/// This editor stays on Android; the shadow build runs in the background (log: Logs/desktop_build.log). Its first
/// build imports everything once (minutes); after that both Libraries stay warm.
///
/// DesktopWalkScene is not in EditorBuildSettings - the Quest APK keeps building MRUKExportScene only.
/// </summary>
public static class DesktopBuildTools
{
    public const string ScenePath = "Assets/Scenes/DesktopWalkScene.unity";
    const string ExeRelPath = "Builds/Windows/XRHouseWalk.exe";
    const string LogRelPath = "Logs/desktop_build.log";
    const string ShadowSuffix = "_WinBuild";

    static string ProjectRoot => Directory.GetCurrentDirectory();
    static string ShadowRoot => Path.Combine(Path.GetDirectoryName(ProjectRoot), Path.GetFileName(ProjectRoot) + ShadowSuffix);
    static string ExePath => Path.Combine(ProjectRoot, ExeRelPath);

    [MenuItem("MRUK/Desktop/1. Create Desktop Walk Scene", false, 60)]
    public static void CreateSceneMenu()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        CreateScene();
        EditorSceneManager.OpenScene(ScenePath);
    }

    // ---------- background build in the shadow project ----------

    static Process buildProc;
    static Stopwatch buildTime;

    [MenuItem("MRUK/Desktop/2. Build Windows App (background)", false, 61)]
    public static void BuildWindowsMenu()
    {
        if (buildProc != null && !buildProc.HasExited) { Debug.LogWarning("[DesktopBuildTools] A Windows build is already running."); return; }
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        AssetDatabase.SaveAssets(); // the shadow project reads the files on disk
        if (!File.Exists(ScenePath)) CreateScene();
        if (!EnsureShadowProject()) return;

        string log = Path.Combine(ProjectRoot, LogRelPath);
        string data = Path.GetFullPath(MRUKPathUtility.GetExportRoot());
        string args = $"-batchmode -quit -projectPath \"{ShadowRoot}\" -buildTarget StandaloneWindows64 " +
                      $"-executeMethod DesktopBuildTools.BuildWindowsBatch -out \"{ExePath}\" -data \"{data}\" -logFile \"{log}\"";
        buildProc = Process.Start(new ProcessStartInfo(EditorApplication.applicationPath, args) { UseShellExecute = false, CreateNoWindow = true });
        buildTime = Stopwatch.StartNew();
        EditorApplication.update -= PollBuild;
        EditorApplication.update += PollBuild;
        Debug.Log($"<color=cyan>[DesktopBuildTools] Windows build started in the background ({ShadowRoot}). Log: {log}</color>");
    }

    static void PollBuild()
    {
        if (buildProc == null) { EditorApplication.update -= PollBuild; return; }
        if (!buildProc.HasExited) return;
        EditorApplication.update -= PollBuild;
        int code = buildProc.ExitCode;
        buildProc = null;
        if (code == 0)
        {
            Debug.Log($"<color=green>WINDOWS BUILD COMPLETED SUCCESSFULLY</color> in {buildTime.Elapsed.TotalSeconds:0} s: {ExePath}");
            EditorUtility.RevealInFinder(ExePath);
        }
        else Debug.LogError($"WINDOWS BUILD FAILED (exit {code}) - see {LogRelPath}");
    }

    [MenuItem("MRUK/Desktop/3. Run Windows App", false, 62)]
    public static void RunWindows()
    {
        if (!File.Exists(ExePath)) { Debug.LogError($"{ExePath} not built yet."); return; }
        Process.Start(new ProcessStartInfo(ExePath) { WorkingDirectory = Path.GetDirectoryName(ExePath) });
    }

    /// <summary>Creates ../&lt;project&gt;_WinBuild with junctions to this project's Assets, Packages and
    /// ProjectSettings (junctions need no admin rights). Its Library, Temp and Logs are its own.</summary>
    static bool EnsureShadowProject()
    {
        try
        {
            Directory.CreateDirectory(ShadowRoot);
            foreach (var dir in new[] { "Assets", "Packages", "ProjectSettings" })
            {
                string link = Path.Combine(ShadowRoot, dir);
                if (Directory.Exists(link)) continue;
                var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{Path.Combine(ProjectRoot, dir)}\"")
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
                p.WaitForExit();
                if (p.ExitCode != 0) throw new Exception($"mklink {dir}: {p.StandardError.ReadToEnd()}");
            }
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError("[DesktopBuildTools] Could not set up the shadow build project: " + ex.Message);
            return false;
        }
    }

    // ---------- the build itself (runs inside the shadow project, or any editor via -executeMethod) ----------

    /// <summary>Unity.exe -batchmode -quit -projectPath &lt;shadow&gt; -buildTarget StandaloneWindows64
    /// -executeMethod DesktopBuildTools.BuildWindowsBatch [-out &lt;exe&gt;] [-data &lt;data folder&gt;]</summary>
    public static void BuildWindowsBatch()
    {
        string exe = Arg("-out") ?? Path.GetFullPath(ExeRelPath);
        string data = Arg("-data") ?? Path.GetFullPath(MRUKPathUtility.GetExportRoot());
        bool ok = BuildWindows(exe, data);
        if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
    }

    /// <summary>Windows x64, Mono (quick builds; IL2CPP buys nothing here). XR is not started on launch in this
    /// build (the shared Standalone XR settings start OpenXR for Link play mode - in the flat desktop app that
    /// would put the window into VR whenever Link is running); the setting is saved off for the build and restored
    /// right after. Writes
    /// DataFolder.txt next to the exe so the app reads the given data folder (the project's Exports/RoomData).</summary>
    public static bool BuildWindows(string exe, string dataFolder)
    {
        // Marker for the Quest build (MRUKEditorTools.BuildInternal refuses to start meanwhile): both builds share
        // Assets, and this one briefly rewrites the XR settings while AR Foundation moves assets to Assets/XR/Temp.
        string marker = Path.Combine(Path.GetDirectoryName(exe), MRUKPathUtility.DesktopBuildMarker);
        Directory.CreateDirectory(Path.GetDirectoryName(exe));
        File.WriteAllText(marker, DateTime.Now.ToString("s"));
        try { return BuildWindowsInner(exe, dataFolder); }
        finally { File.Delete(marker); }
    }

    static bool BuildWindowsInner(string exe, string dataFolder)
    {
        if (!File.Exists(ScenePath)) CreateScene();
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Standalone, ScriptingImplementation.Mono2x);
        Directory.CreateDirectory(Path.GetDirectoryName(exe));
        // Native plugins left over from an earlier build would otherwise stay next to the exe.
        string plugins = Path.Combine(Path.GetDirectoryName(exe), Path.GetFileNameWithoutExtension(exe) + "_Data", "Plugins");
        if (Directory.Exists(plugins)) Directory.Delete(plugins, true);

        var xr = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Standalone);
        bool xrInit = xr != null && xr.InitManagerOnStart;
        var excluded = ExcludeNativePlugins();
        BuildReport report;
        try
        {
            // Must be on disk: the build reads the settings asset, an in-memory change alone doesn't reach the player.
            if (xr != null) { xr.InitManagerOnStart = false; EditorUtility.SetDirty(xr); AssetDatabase.SaveAssets(); }
            report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { ScenePath },
                locationPathName = exe,
                target = BuildTarget.StandaloneWindows64,
                targetGroup = BuildTargetGroup.Standalone,
                options = BuildOptions.None,
            });
        }
        finally
        {
            if (xr != null) { xr.InitManagerOnStart = xrInit; EditorUtility.SetDirty(xr); AssetDatabase.SaveAssets(); }
            foreach (var imp in excluded) imp.SetIncludeInBuildDelegate(null);
        }

        var s = report.summary;
        Debug.Log($"[DesktopBuildTools] {s.result}: {s.outputPath}, {s.totalSize / (1024f * 1024f):0.0} MB, {s.totalErrors} error(s), {s.totalTime}");
        if (s.result != BuildResult.Succeeded) { Debug.LogError("WINDOWS BUILD FAILED"); return false; }
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(exe), MRUKPathUtility.DesktopDataFolderFile), dataFolder);
        Debug.Log($"WINDOWS BUILD COMPLETED SUCCESSFULLY - data folder: {dataFolder}");
        return true;
    }

    /// <summary>Meta native plugins the desktop app has no use for, left out of the Windows build only (the Quest
    /// build and the packages are untouched). Each telemetry library blocks startup for ~5 s on Windows: its init
    /// starts an ADB server to look for a headset. Their managed callers catch DllNotFoundException.</summary>
    static readonly string[] DesktopExcludedPlugins =
    {
        "ISDKEngineTelemetry.dll",          // Interaction SDK telemetry, initialised unconditionally before scene load
        "RuntimeOptimizer_Plugin_dll.dll",  // Meta Runtime Optimizer (profiling tool)
        "XrApiLayer_METAX_operator.dll",    // OpenXR API layer for the Meta XR AI tooling
    };

    static System.Collections.Generic.List<PluginImporter> ExcludeNativePlugins()
    {
        var list = new System.Collections.Generic.List<PluginImporter>();
        foreach (var imp in PluginImporter.GetAllImporters())
        {
            if (Array.IndexOf(DesktopExcludedPlugins, Path.GetFileName(imp.assetPath)) < 0) continue;
            imp.SetIncludeInBuildDelegate(_ => false);
            list.Add(imp);
            Debug.Log($"[DesktopBuildTools] Not in the Windows build: {imp.assetPath}");
        }
        return list;
    }

    static string Arg(string name)
    {
        var a = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < a.Length; i++) if (a[i] == name) return a[i + 1];
        return null;
    }

    // ---------- scene ----------

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
}
