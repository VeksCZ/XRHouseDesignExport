using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.XR.Management;
using UnityEngine;

/// <summary>OpenXR's OpenXRChooseRuntimeLibraries lets the first feature whose script folder "contains" a plugin's folder
/// decide whether that plugin goes into the build - and for libUnityOpenXR.so that can be any disabled feature (it was
/// the enabled Composition Layers feature until com.unity.xr.meta-openxr went). Without the library the app hangs on
/// the loading dots. Runs after it and puts UnityOpenXR back whenever the OpenXR loader is active.</summary>
class OpenXRRuntimeLibFix : IPreprocessBuildWithReport
{
    public int callbackOrder => 100;

    public void OnPreprocessBuild(BuildReport report)
    {
        var group = report.summary.platformGroup;
        var settings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(group);
        bool openXR = settings != null && settings.Manager != null &&
                      settings.Manager.activeLoaders.Any(l => l != null && l.GetType().Name.Contains("OpenXRLoader"));
        Debug.Log($"[OpenXRRuntimeLibFix] {group}: settings {(settings != null)}, manager {(settings?.Manager != null)}, " +
                  $"loaders [{string.Join(",", settings?.Manager?.activeLoaders?.Select(l => l == null ? "null" : l.GetType().Name) ?? new string[0])}]");
        if (!openXR) return;
        foreach (var importer in PluginImporter.GetAllImporters()
                     .Where(i => i.assetPath.Contains("UnityOpenXR") && i.GetCompatibleWithPlatform(report.summary.platform)))
        {
            importer.SetIncludeInBuildDelegate(_ => true);
            Debug.Log($"[OpenXRRuntimeLibFix] including {importer.assetPath}");
        }
    }
}
