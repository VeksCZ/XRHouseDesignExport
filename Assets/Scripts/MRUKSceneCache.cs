using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

/// <summary>
/// Lets a scan be captured once - while physically in the space, when MRUK's live anchors are
/// actually locatable - and exported later from anywhere, by round-tripping MRUK's own scene JSON
/// (MRUK.SaveSceneToJsonString / LoadSceneFromJsonString) through local storage. Loading a cached
/// scene populates ordinary MRUKRoom/MRUKAnchor components just like a live device scan, so the
/// rest of the export pipeline (MRUKDataProcessor, XRModelFactory, OBJWriter, GLBExporter) doesn't
/// need to know or care whether the data came from the headset or a previously saved cache.
/// </summary>
public static class MRUKSceneCache
{
    private const string EXTENSION = ".scene.json";

    /// <summary>
    /// The app's own storage on the headset (Android/data/&lt;package&gt;/files/ScanCache). Unlike the Downloads
    /// folder, scoped storage lets the app read every file there - including scans copied in over ADB - and
    /// it needs no permission. In the Editor it is a project-relative folder.
    /// </summary>
    public static string GetCacheRoot()
    {
        string root = Application.isEditor ? "Exports/ScanCache" : Path.Combine(Application.persistentDataPath, "ScanCache");
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>Cached scan names (no extension), most recently saved first.</summary>
    public static List<string> ListCachedScans()
    {
        string root = GetCacheRoot();
        if (!Directory.Exists(root)) return new List<string>();
        return Directory.GetFiles(root, "*" + EXTENSION)
            .OrderByDescending(File.GetLastWriteTime)
            .Select(f => Path.GetFileName(f).Substring(0, Path.GetFileName(f).Length - EXTENSION.Length))
            .ToList();
    }

#if META_XR_SDK_INSTALLED
    /// <summary>
    /// Snapshots whatever MRUK currently has loaded (all rooms, anchors and the global mesh) to a
    /// local JSON file. Returns the saved scan's name, or null if there was nothing to save.
    /// </summary>
    public static string SaveCurrentScene(MRUK mruk, string name = null)
    {
        if (mruk == null || mruk.Rooms.Count == 0) return null;

        // Positional bool arg: MRUK has both an obsolete SaveSceneToJsonString(CoordinateSystem, bool, ...)
        // and the current SaveSceneToJsonString(bool, ...) overload, both with an "includeGlobalMesh"
        // parameter - a named argument is ambiguous between them (CS0121), a positional bool is not.
        string json = mruk.SaveSceneToJsonString(true);
        if (string.IsNullOrEmpty(json)) return null;

        name = string.IsNullOrEmpty(name) ? "Scan_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") : name;
        File.WriteAllText(Path.Combine(GetCacheRoot(), name + EXTENSION), json);
        return name;
    }

    /// <summary>Replaces whatever MRUK currently has loaded with a previously cached scan.</summary>
    public static async Task<bool> LoadCachedScene(MRUK mruk, string name)
    {
        if (mruk == null || string.IsNullOrEmpty(name)) return false;
        string path = Path.Combine(GetCacheRoot(), name + EXTENSION);
        if (!File.Exists(path)) return false;

        string json = File.ReadAllText(path);
        var result = await mruk.LoadSceneFromJsonString(json, removeMissingRooms: true);
        return result == MRUK.LoadDeviceResult.Success;
    }
#endif
}
