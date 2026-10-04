using System;
using System.IO;
using System.Linq;
using UnityEngine;

public static class MRUKPathUtility
{
    // Numbered so every GLB sorts together and every OBJ sorts together in a file browser, instead of
    // interleaving two files per model tier (a shared prefix used to put e.g. the Anchors .glb and .obj right
    // next to each other, and the next tier's pair right after - name sorting means the numbers do that job now).
    public const string MODEL_CLEAN_GLB = "11_Model_Analytical_Anchors.glb";
    public const string MODEL_MESH_ANALYTICAL_GLB = "12_Model_Analytical_Mesh.glb";
    public const string MODEL_MESH_GLB = "13_Model_Reconstruction.glb";
    public const string MODEL_RAW_GLB = "14_Model_Raw_Scan.glb";
    public const string MODEL_DIM_GLB = "15_Model_Analytical_Dimensioned.glb";
    public const string MODEL_CLEAN_OBJ = "21_Model_Analytical_Anchors.obj";
    public const string MODEL_MESH_ANALYTICAL_OBJ = "22_Model_Analytical_Mesh.obj";
    public const string MODEL_MESH_OBJ = "23_Model_Reconstruction.obj";
    public const string MODEL_RAW_OBJ = "24_Model_Raw_Scan.obj";
    public const string MODEL_DIM_OBJ = "25_Model_Analytical_Dimensioned.obj";
    public const string MODEL_MTL = "00_Materials.mtl";
    
    public const string DATA_JSON = "90_Data_Rooms.json";
    public const string DATA_DUMP = "91_Data_Scene_Dump.txt";
    /// <summary>MRUK's own full scene JSON (all rooms, anchors, global mesh) - the same format "Save scan" writes
    /// to the ScanCache, so the export can be reloaded as a scan source later (see MRUKSceneCache).</summary>
    public const string DATA_SCENE = "92_Data_Scan" + MRUKSceneCache.EXTENSION;
    /// <summary>The user's edits of the exported scan (doors, stairs, openings - HouseEdits), so an export is self-contained.</summary>
    public const string DATA_EDITS = "93_Data_Edits.json";
    /// <summary>Room names picked in the app (RoomNames format, keyed by room UUID).</summary>
    public const string DATA_ROOM_NAMES = "94_Data_RoomNames.json";
    /// <summary>Furniture spec (bathroom fittings, boxes) - shown in the walk-through and turned into
    /// 16_Model_Furnished.glb by Tools/furnish.py. See Furniture.</summary>
    public const string DATA_FURNITURE = "95_Data_Furniture.json";
    public const string DATA_REPORT = "99_Report_House.html";
    public const string DATA_REPORT_ROOM = "99_Report_Room.html";

    /// <summary>Editor or the Windows desktop app: scans live as folders in one local data folder (see MRUKSceneCache),
    /// not in the headset's app storage.</summary>
    public static bool IsDesktop => Application.isEditor || Application.platform == RuntimePlatform.WindowsPlayer;

    /// <summary>File next to the desktop exe holding the data folder's path (written by the Windows build, see
    /// DesktopBuildTools) - so the exe reads the very same Exports/RoomData the Editor pulls the Quest's data into.</summary>
    public const string DesktopDataFolderFile = "DataFolder.txt";

    /// <summary>Exists in Builds/Windows while a Windows build runs (see DesktopBuildTools); the Quest build waits for it.</summary>
    public const string DesktopBuildMarker = ".building";

    static string desktopRoot;

    /// <summary>Overrides the desktop app's data folder at runtime (e.g. picked in its menu).</summary>
    public static string DesktopDataRoot
    {
        get => desktopRoot ??= ResolveDesktopDataRoot();
        set => desktopRoot = value;
    }

    /// <summary>"-data &lt;folder&gt;" on the command line, else the path in DataFolder.txt next to the exe, else
    /// RoomData next to the exe.</summary>
    static string ResolveDesktopDataRoot()
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "-data" && !string.IsNullOrWhiteSpace(args[i + 1])) return args[i + 1].Trim('"');
        // dataPath is <exe dir>/<name>_Data in a Windows player.
        string exeDir = Path.GetDirectoryName(Application.dataPath);
        try
        {
            string file = Path.Combine(exeDir, DesktopDataFolderFile);
            if (File.Exists(file))
            {
                string p = File.ReadAllText(file).Trim().Trim('"');
                if (!string.IsNullOrEmpty(p)) return p;
            }
        }
        catch (Exception ex) { Debug.LogWarning($"[MRUKPathUtility] Could not read {DesktopDataFolderFile}: {ex.Message}"); }
        return Path.Combine(exeDir, "RoomData");
    }

    public static string GetExportRoot()
    {
        if (Application.isEditor) return "Exports/RoomData";
        if (IsDesktop) return DesktopDataRoot;
        return "/sdcard/Download/XRHouseExports";
    }

    /// <summary>Export_{date_time}_{scan name}, so exports of different scans/houses sort and read clearly.</summary>
    public static string CreateSessionFolder(string root, string sourceName)
    {
        string session = Path.Combine(root, $"Export_{DateTime.Now:yyyyMMdd_HHmmss}_{MRUKDataProcessor.GetSafeName(ScanNameFromFolder(sourceName))}");
        Directory.CreateDirectory(session);
        return session;
    }

    static readonly System.Text.RegularExpressions.Regex ExportPrefix = new System.Text.RegularExpressions.Regex(@"^Export_\d{8}_\d{6}_");

    /// <summary>The scan's own name from an export/scan folder name ("Export_20261002_105316_20261002_0848_15rooms" or
    /// "Scan_20261002_0848_15rooms" -> "20261002_0848_15rooms"), so re-exporting an export doesn't nest the prefixes.
    /// Anything else is returned unchanged.</summary>
    public static string ScanNameFromFolder(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var m = ExportPrefix.Match(name);
        if (m.Success && name.Length > m.Length) return name.Substring(m.Length);
        if (name.StartsWith(MRUKSceneCache.ScanFolderPrefix) && name.Length > MRUKSceneCache.ScanFolderPrefix.Length)
            return name.Substring(MRUKSceneCache.ScanFolderPrefix.Length);
        return name;
    }

    /// <summary>
    /// Session logs. On the headset they live in the app's own storage, not in the export folder: there they were
    /// pulled to the PC with every export (hundreds of MB), and logs written by an earlier install could never be
    /// deleted by the app (scoped storage). MRUK menu "Pull logs from Quest" fetches them when needed. In the Editor:
    /// the project's (git-ignored) Logs folder.
    /// </summary>
    public static string GetLogRoot()
    {
        return Application.isEditor
            ? "Logs"
            : Path.Combine(Application.persistentDataPath, "Logs");
    }

    /// <summary>
    /// Deletes every exported session under the export root, freeing device storage, then recreates the (now
    /// empty) root directory so later exports have somewhere to write to. Deletes file by file instead of a
    /// single recursive Directory.Delete, so one locked/permission-denied file (Android scoped storage can
    /// refuse an individual file) doesn't abort the whole thing with nothing removed and no detail beyond
    /// "could not delete" - every failure is logged with its own path and reason, and everything else still
    /// gets removed. Call this off the main thread (it can genuinely be many files) - the caller is
    /// responsible for that, this method itself does no threading.
    /// </summary>
    public static (int deleted, int failed) ClearExportRoot()
    {
        string root = GetExportRoot();
        int deleted = 0, failed = 0;
        if (Directory.Exists(root))
        {
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                try { File.Delete(file); deleted++; }
                catch (Exception ex) { failed++; Debug.LogWarning($"[ClearExportRoot] Could not delete '{file}': {ex.Message}"); }
            }
            // Deepest directories first, so a now-empty subfolder is removed before its now-empty parent.
            foreach (var dir in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            {
                try { if (Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir); }
                catch { /* not empty (a file inside failed to delete) or locked - leave it */ }
            }
        }
        try { Directory.CreateDirectory(root); } catch (Exception ex) { Debug.LogWarning($"[ClearExportRoot] Could not recreate '{root}': {ex.Message}"); }
        return (deleted, failed);
    }
}
