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
    public const string MODEL_CLEAN_OBJ = "21_Model_Analytical_Anchors.obj";
    public const string MODEL_MESH_ANALYTICAL_OBJ = "22_Model_Analytical_Mesh.obj";
    public const string MODEL_MESH_OBJ = "23_Model_Reconstruction.obj";
    public const string MODEL_RAW_OBJ = "24_Model_Raw_Scan.obj";
    public const string MODEL_MTL = "00_Materials.mtl";
    
    public const string DATA_JSON = "90_Data_Rooms.json";
    public const string DATA_DUMP = "91_Data_Scene_Dump.txt";
    public const string DATA_REPORT = "99_Report_House.html";
    public const string DATA_REPORT_ROOM = "99_Report_Room.html";

    public static string GetExportRoot()
    {
        return Application.isEditor 
            ? "Exports/RoomData" 
            : "/sdcard/Download/XRHouseExports";
    }

    /// <summary>Export_{date_time}_{scan name}, so exports of different scans/houses sort and read clearly.</summary>
    public static string CreateSessionFolder(string root, string sourceName)
    {
        string session = Path.Combine(root, $"Export_{DateTime.Now:yyyyMMdd_HHmmss}_{MRUKDataProcessor.GetSafeName(sourceName)}");
        Directory.CreateDirectory(session);
        return session;
    }

    public static string GetLogRoot()
    {
        return Application.isEditor
            ? "Exports/Logs"
            : "/sdcard/Download/XRHouseExports/Logs";
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
