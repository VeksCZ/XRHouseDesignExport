using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Command-line / menu build of the Quest APK with the project's own player settings and scene list:
/// Unity.exe -batchmode -quit -projectPath . -buildTarget Android -executeMethod BuildQuest.Build
/// (BuildQuest.BuildAndRun also installs and starts it on the connected headset).
/// Build-time settings that only cost time here: IL2CPP "Faster (smaller) builds" code generation (far less C++
/// to compile) and no symbols.zip (the slow "Building Gradle Symbols" step, and a 300 MB file per build).
/// </summary>
public static class BuildQuest
{
    const string OutputPath = "Builds/XRHouseExporter.apk";

    [MenuItem("MRUK/Build Quest APK")]
    public static void Build() => Run(false);

    [MenuItem("MRUK/Build And Run On Quest")]
    public static void BuildAndRun() => Run(true);

    static void Run(bool andRun)
    {
        PlayerSettings.SetIl2CppCodeGeneration(NamedBuildTarget.Android, Il2CppCodeGeneration.OptimizeSize);
        EditorUserBuildSettings.androidCreateSymbols = AndroidCreateSymbols.Disabled;

        var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));
        var opts = EditorUserBuildSettings.development ? BuildOptions.Development : BuildOptions.None;
        if (andRun) opts |= BuildOptions.AutoRunPlayer;
        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = OutputPath,
            target = BuildTarget.Android,
            targetGroup = BuildTargetGroup.Android,
            options = opts,
        };
        var report = BuildPipeline.BuildPlayer(options);
        var s = report.summary;
        Debug.Log($"[BuildQuest] {s.result}: {s.outputPath}, {s.totalSize / (1024f * 1024f):0.0} MB, {s.totalErrors} error(s), {s.totalTime}");
        if (Application.isBatchMode) EditorApplication.Exit(s.result == BuildResult.Succeeded ? 0 : 1);
    }
}
