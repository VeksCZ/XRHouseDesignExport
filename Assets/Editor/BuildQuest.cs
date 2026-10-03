using UnityEditor;
using UnityEngine;

/// <summary>
/// Batch-mode entry points for the Quest build - the build itself is MRUKEditorTools' (the one Quest build path):
/// Unity.exe -batchmode -quit -projectPath . -buildTarget Android -executeMethod BuildQuest.Build
/// BuildQuest.BuildAndRun also installs and starts it on the USB-connected headset. Exit code 0 = success.
/// </summary>
public static class BuildQuest
{
    /// <summary>Release build (full symbols), APK only.</summary>
    public static void Build() => Exit(MRUKEditorTools.BuildInternal(BuildOptions.None) != null);

    /// <summary>Fast iteration build + adb install + launch (same as menu MRUK > 2. Fast Build and Install APK).</summary>
    public static void BuildAndRun() => Exit(MRUKEditorTools.BuildAPKFast());

    static void Exit(bool ok)
    {
        Debug.Log($"[BuildQuest] {(ok ? "Succeeded" : "FAILED")}");
        if (Application.isBatchMode) EditorApplication.Exit(ok ? 0 : 1);
    }
}
