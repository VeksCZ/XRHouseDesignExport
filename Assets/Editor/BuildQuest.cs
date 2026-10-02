using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Command-line / menu build of the Quest APK with the project's own player settings and scene list:
/// Unity.exe -batchmode -quit -projectPath . -buildTarget Android -executeMethod BuildQuest.Build
/// </summary>
public static class BuildQuest
{
    const string OutputPath = "Builds/XRHouseExporter.apk";

    [MenuItem("MRUK/Build Quest APK")]
    public static void Build()
    {
        var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(OutputPath));
        var options = new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = OutputPath,
            target = BuildTarget.Android,
            targetGroup = BuildTargetGroup.Android,
            options = EditorUserBuildSettings.development ? BuildOptions.Development : BuildOptions.None,
        };
        var report = BuildPipeline.BuildPlayer(options);
        var s = report.summary;
        Debug.Log($"[BuildQuest] {s.result}: {s.outputPath}, {s.totalSize / (1024f * 1024f):0.0} MB, {s.totalErrors} error(s), {s.totalTime}");
        if (Application.isBatchMode) EditorApplication.Exit(s.result == BuildResult.Succeeded ? 0 : 1);
    }
}
