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

    /// <summary>Furniture spec of a saved scan (95_Data_Furniture.json in its folder / Name.furniture.json).</summary>
    public const string FurnitureExtension = ".furniture.json";
    public static string FurniturePath(string name) => string.IsNullOrEmpty(name) ? null : FolderMode
        ? Path.Combine(GetCacheRoot(), name, MRUKPathUtility.DATA_FURNITURE)
        : Path.Combine(GetCacheRoot(), name + FurnitureExtension);

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
        // Furnished scans (the finished house model: furniture, niches, real ceilings) come first, then by date.
        if (FolderMode)
            return Directory.GetDirectories(root)
                .Where(d => File.Exists(Path.Combine(d, MRUKPathUtility.DATA_SCENE)))
                .OrderByDescending(d => File.Exists(Path.Combine(d, MRUKPathUtility.DATA_FURNITURE)))
                .ThenByDescending(d => File.GetLastWriteTime(Path.Combine(d, MRUKPathUtility.DATA_SCENE)))
                .Select(Path.GetFileName)
                .ToList();
        return Directory.GetFiles(root, "*" + EXTENSION)
            .Select(f => (file: f, name: Path.GetFileName(f).Substring(0, Path.GetFileName(f).Length - EXTENSION.Length)))
            .OrderByDescending(x => File.Exists(FurniturePath(x.name)))
            .ThenByDescending(x => File.GetLastWriteTime(x.file))
            .Select(x => x.name)
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

    /// <summary>An old saved scan that a newer one fully covers.</summary>
    public class Redundant { public string name, keptIn; }

    /// <summary>Saved scans that can go: every room they have is in a newer saved scan (keptIn), and they hold nothing
    /// of their own - no furniture spec, no edits (doors, stairs, holes), no doorway written into their JSON. On the PC
    /// only pulled headset scans (Scan_ folders) count, never export folders. Collects paths here (main thread), reads
    /// the files in the background.</summary>
    public static Task<List<Redundant>> FindRedundantAsync()
    {
        var scans = ListCachedScans()
            .Where(n => !FolderMode || n.StartsWith(ScanFolderPrefix))
            .Select(n => (name: n, scene: ScanPath(n), edits: EditsPath(n), furniture: FurniturePath(n)))
            .ToList();
        return Task.Run(() => FindRedundant(scans));
    }

    static List<Redundant> FindRedundant(List<(string name, string scene, string edits, string furniture)> scans)
    {
        var info = new List<(string name, DateTime time, HashSet<string> rooms, bool own)>();
        foreach (var s in scans)
        {
            try
            {
                var rooms = RoomIdsOf(s.scene);
                if (rooms.Count == 0) continue;
                bool own = File.Exists(s.furniture) || HasEdits(s.edits)
                           || Directory.GetFiles(Path.GetDirectoryName(s.scene), Path.GetFileName(s.scene) + ".bak_doors*").Length > 0;
                info.Add((s.name, File.GetLastWriteTimeUtc(s.scene), rooms, own));
            }
            catch (Exception e) { Debug.LogWarning($"[Cleanup] {s.name}: {e.Message}"); }
        }
        bool Newer((string name, DateTime time, HashSet<string> rooms, bool own) a, (string name, DateTime time, HashSet<string> rooms, bool own) b)
            => a.time > b.time || (a.time == b.time && string.CompareOrdinal(a.name, b.name) > 0);
        var gone = new HashSet<string>(info.Where(s => !s.own && info.Any(t => t.name != s.name && Newer(t, s) && s.rooms.IsSubsetOf(t.rooms))).Select(s => s.name));
        var res = new List<Redundant>();
        foreach (var s in info.Where(x => gone.Contains(x.name)))
        {
            // kept in: the newest scan that stays and has all its rooms
            var t = info.Where(x => !gone.Contains(x.name) && s.rooms.IsSubsetOf(x.rooms)).OrderByDescending(x => x.time).FirstOrDefault();
            if (t.name != null) res.Add(new Redundant { name = s.name, keptIn = t.name });
        }
        return res;
    }

    /// <summary>The room ids in a scene JSON (V1 rooms with roomLayoutMETA, V2 with roomMeshMETA), streamed - the file
    /// holds the whole global mesh.</summary>
    public static HashSet<string> RoomIdsOf(string path)
    {
        var ids = new HashSet<string>();
        using var reader = new Newtonsoft.Json.JsonTextReader(new StreamReader(path));
        string uuid = null; bool room = false;
        while (reader.Read())
        {
            if (reader.TokenType == Newtonsoft.Json.JsonToken.StartObject && reader.Depth == 2) { uuid = null; room = false; }
            else if (reader.TokenType == Newtonsoft.Json.JsonToken.PropertyName && reader.Depth == 3)
            {
                string p = (string)reader.Value;
                if (p == "uuid") uuid = reader.ReadAsString();
                else if (p == "roomLayoutMETA" || p == "roomMeshMETA") room = true;
            }
            else if (reader.TokenType == Newtonsoft.Json.JsonToken.EndObject && reader.Depth == 2 && room && uuid != null) ids.Add(uuid.ToLowerInvariant());
        }
        return ids;
    }

    static bool HasEdits(string path)
    {
        if (!File.Exists(path)) return false;
        var e = JsonUtility.FromJson<HouseEdits>(File.ReadAllText(path));
        return e != null && ((e.doors?.Count ?? 0) + (e.stairs?.Count ?? 0) + (e.holes?.Count ?? 0)) > 0;
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
        // Clear first. A saved scan of the house has the same room UUIDs as the house's live scene: loaded on top of it,
        // MRUK merged the two - the matching rooms kept their live poses (and, if the live room was damaged, its
        // missing walls), the rest came in the saved scan's own frame. The house fell apart (furniture outside, stairs
        // in the middle, the plan's orientation flipping between loads). Cleared, the saved scan always comes back
        // exactly as saved; MRUKExporter lines it up with the real house afterwards (AlignToLive).
        mruk.ClearScene();
        await Task.Yield();
        var result = await mruk.LoadSceneFromJsonString(json, removeMissingRooms: true);
        return result == MRUK.LoadDeviceResult.Success;
    }
#endif
}
