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
///
/// Where a saved scan lives:
/// - Headset: the app's own storage (Android/data/&lt;package&gt;/files/ScanCache) as Name.scene.json + Name.edits.json.
///   Unlike Downloads, scoped storage lets the app read/delete every file there across reinstalls, with no permission.
/// - Editor: there is no separate cache. Every folder in the export root (Exports/RoomData) that holds a
///   92_Data_Scan.scene.json is a scan source, named after its folder; its edits are 93_Data_Edits.json and its room
///   names 94_Data_RoomNames.json in the same folder. Exports are self-contained, so a pulled or batch export can be
///   reloaded, edited and re-exported on the PC as is. Scans pulled from the headset's ScanCache become Scan_&lt;name&gt;.
/// </summary>
public static class MRUKSceneCache
{
    public const string EXTENSION = ".scene.json";
    /// <summary>Folder prefix of a saved headset scan pulled to the PC (Editor), as opposed to an Export_ session.</summary>
    public const string ScanFolderPrefix = "Scan_";

    static bool FolderMode => MRUKPathUtility.IsDesktop; // Editor and the Windows desktop app

    /// <summary>Headset: persistentDataPath/ScanCache. Editor: the export root (Exports/RoomData).</summary>
    public static string GetCacheRoot()
    {
        string root = FolderMode ? MRUKPathUtility.GetExportRoot() : Path.Combine(Application.persistentDataPath, "ScanCache");
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>The scene JSON of a saved scan.</summary>
    public static string ScanPath(string name) => FolderMode
        ? Path.Combine(GetCacheRoot(), name, MRUKPathUtility.DATA_SCENE)
        : Path.Combine(GetCacheRoot(), name + EXTENSION);

    /// <summary>The user's edits (HouseEdits) of a saved scan.</summary>
    public static string EditsPath(string name) => FolderMode
        ? Path.Combine(GetCacheRoot(), name, MRUKPathUtility.DATA_EDITS)
        : Path.Combine(GetCacheRoot(), name + HouseEditsStore.EXTENSION);

    /// <summary>Room names file to use while this scan is active: its own file in the Editor; null (= the shared
    /// default file) on the headset, where room UUIDs stay the same across re-saves of one house.</summary>
    public static string RoomNamesPath(string name) => FolderMode && !string.IsNullOrEmpty(name)
        ? Path.Combine(GetCacheRoot(), name, MRUKPathUtility.DATA_ROOM_NAMES)
        : null;

    /// <summary>Cached scan names, most recently saved first.</summary>
    public static List<string> ListCachedScans()
    {
        string root = GetCacheRoot();
        if (!Directory.Exists(root)) return new List<string>();
        if (FolderMode)
            return Directory.GetDirectories(root)
                .Where(d => File.Exists(Path.Combine(d, MRUKPathUtility.DATA_SCENE)))
                .OrderByDescending(d => File.GetLastWriteTime(Path.Combine(d, MRUKPathUtility.DATA_SCENE)))
                .Select(Path.GetFileName)
                .ToList();
        return Directory.GetFiles(root, "*" + EXTENSION)
            .OrderByDescending(File.GetLastWriteTime)
            .Select(f => Path.GetFileName(f).Substring(0, Path.GetFileName(f).Length - EXTENSION.Length))
            .ToList();
    }

    /// <summary>Deletes a cached scan by name (in the Editor: its whole export/scan folder). True if something was removed.</summary>
    public static bool DeleteCachedScan(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (FolderMode)
        {
            string dir = Path.Combine(GetCacheRoot(), name);
            if (!File.Exists(Path.Combine(dir, MRUKPathUtility.DATA_SCENE))) return false;
            Directory.Delete(dir, true);
            return true;
        }
        string path = ScanPath(name);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
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
        if (FolderMode && !name.StartsWith(ScanFolderPrefix)) name = ScanFolderPrefix + name;
        string path = ScanPath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, json);
        return name;
    }

    /// <summary>Replaces whatever MRUK currently has loaded with a previously cached scan.</summary>
    public static async Task<bool> LoadCachedScene(MRUK mruk, string name)
    {
        if (mruk == null || string.IsNullOrEmpty(name)) return false;
        string path = ScanPath(name);
        if (!File.Exists(path)) return false;

        string json = File.ReadAllText(path);
        var result = await mruk.LoadSceneFromJsonString(json, removeMissingRooms: true);
        return result == MRUK.LoadDeviceResult.Success;
    }
#endif
}
