using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using System.Globalization;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

#if UNITY_EDITOR
using UnityEditor;
using System.IO.Compression;
#endif

public class MRUKExporter : MonoBehaviour {
    public XRMenu uiLog;
    private StringBuilder dLog = new StringBuilder();
    public static string LastReportPath = "";

    public async void OnExportButton() { await ExportAllRooms(uiLog); }

    public async System.Threading.Tasks.Task<bool> ExportAllRooms(XRMenu uiLog = null) {
    #if META_XR_SDK_INSTALLED
        dLog.Clear();
        dLog.AppendLine($"=== EXPORT START {DateTime.Now} ===");
        string root = Application.isEditor ? "Exports/RoomData" : "/sdcard/Download/XRHouseExports";
        if (uiLog != null) uiLog.AddLog("Příprava exportu...");
        try {
            if (MRUK.Instance == null) {
                dLog.AppendLine("ERROR: MRUK.Instance is null");
                return false;
            }
            if (!Application.isEditor) {
                dLog.AppendLine("Loading scene from device...");
                await MRUK.Instance.LoadSceneFromDevice();
            }

            await System.Threading.Tasks.Task.Delay(1500); 

            var rooms = MRUK.Instance.Rooms.ToList();
            dLog.AppendLine($"Found {rooms.Count} rooms.");
            if (uiLog != null) uiLog.AddLog($"Nalezeno {rooms.Count} místností.");
            if (rooms.Count == 0) {
                dLog.AppendLine("ERROR: No rooms found in MRUK.");
                if (uiLog != null) uiLog.AddLog("CHYBA: Žádné místnosti nenalezeny.");
                return false;
            }

            // Calculate global correction angle (align to longest wall)
            float globalAngle = 0; float maxW = 0;
            int wallCount = 0;
            foreach (var r in rooms) {
                var roomWalls = r.Anchors.Where(x => x.Label.ToString().ToUpper().Contains("WALL") && x.PlaneRect.HasValue).ToList();
                wallCount += roomWalls.Count;
                foreach (var a in roomWalls) {
                    if (a.PlaneRect.Value.width > maxW) { 
                        maxW = a.PlaneRect.Value.width; 
                        globalAngle = -a.transform.eulerAngles.y; 
                    }
                }
            }
            dLog.AppendLine($"Global alignment angle: {globalAngle:F2} (based on {wallCount} walls, max width {maxW:F2}m)");
            if (uiLog != null) uiLog.AddLog($"Zarovnání scény: {globalAngle:F1}°");

            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string session = Path.Combine(root, "Export_" + ts);
            Directory.CreateDirectory(session);
            dLog.AppendLine($"Session directory: {session}");

            dLog.AppendLine("Writing metadata files...");
            File.WriteAllText(Path.Combine(session, "full_scene_dump.txt"), GenerateSceneDump(rooms));
            File.WriteAllText(Path.Combine(session, "house_data.json"), GenerateJson(rooms));
            
            // BOTH REPORTS: V1 (Classic) and V2 (Interactive)
            dLog.AppendLine("Generating HTML reports...");
            if (uiLog != null) uiLog.AddLog("Generuji HTML reporty...");
            File.WriteAllText(Path.Combine(session, "house_report_v1.html"), MRUKReportBuilder.GenerateFullReport(rooms, true, true, globalAngle));
            File.WriteAllText(Path.Combine(session, "house_report_v2.html"), MRUKReportBuilderV2.GenerateFullReport(rooms, true, true, globalAngle));
            
            // MODELS
            dLog.AppendLine("Generating 3D models...");
            if (uiLog != null) uiLog.AddLog("Exportuji 3D modely...");
            
            string analyticalObj = MRUKModelExporterV2.GenerateOBJ(rooms, false, globalAngle);
            dLog.AppendLine($"Analytical OBJ: {analyticalObj.Length} chars");
            File.WriteAllText(Path.Combine(session, "house_analytical.obj"), analyticalObj);

            string meshObj = MRUKModelExporterV2.GenerateOBJ(rooms, true, globalAngle, true);
            if (string.IsNullOrEmpty(meshObj) || meshObj.Length < 100) {
                dLog.AppendLine("WARNING: Mesh OBJ is empty or too small!");
                if (uiLog != null) uiLog.AddLog("VAROVÁNÍ: Mesh model je prázdný!");
            } else {
                dLog.AppendLine($"Mesh OBJ success: {meshObj.Length} chars");
                if (uiLog != null) uiLog.AddLog($"Mesh OK ({meshObj.Length / 1024} KB)");
            }
            File.WriteAllText(Path.Combine(session, "house_mesh.obj"), meshObj);

            File.WriteAllText(Path.Combine(session, "house_model.mtl"), MRUKModelExporterV2.GenerateMTL());
            
            LastReportPath = Path.Combine(session, "house_report_v2.html");

            foreach(var r in rooms) {
                string rName = GetRoomLabel(r);
                var allLabels = r.Anchors.Select(a => a.Label.ToString()).Distinct().ToList();
                dLog.AppendLine($"Processing room: {r.name} -> Label: {rName} | All Anchors: {string.Join(", ", allLabels)}");
                string rP = Path.Combine(session, GetSafeName(rName) + "_-_" + r.name.Substring(Math.Max(0, r.name.Length-4)));
                Directory.CreateDirectory(rP);
                var s = new List<MRUKRoom>{r};
                File.WriteAllText(Path.Combine(rP, "report_v1.html"), MRUKReportBuilder.GenerateFullReport(s, true, false, globalAngle));
                File.WriteAllText(Path.Combine(rP, "report_v2.html"), MRUKReportBuilderV2.GenerateFullReport(s, true, false, globalAngle));
                File.WriteAllText(Path.Combine(rP, "analytical.obj"), MRUKModelExporterV2.GenerateOBJ(s, false, globalAngle));
                File.WriteAllText(Path.Combine(rP, "mesh.obj"), MRUKModelExporterV2.GenerateOBJ(s, true, globalAngle, false));
            }

            dLog.AppendLine("Export finished successfully.");
            File.WriteAllText(Path.Combine(session, "debug_log.txt"), dLog.ToString());

            if (uiLog != null) uiLog.AddLog("Export hotov!");
            return true;
        } catch (Exception ex) {
            dLog.AppendLine($"CRITICAL ERROR: {ex.Message}\n{ex.StackTrace}");
            File.WriteAllText(Path.Combine(root, "last_crash_log.txt"), dLog.ToString());
            if (uiLog != null) uiLog.AddLog("CHYBA: " + ex.Message);
            return false;
        }
    #else
        return false;
    #endif
    }

    private string GenerateSceneDump(List<MRUKRoom> rooms) {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("=== EXHAUSTIVE SCENE DUMP (V11) ===");
        foreach (var r in rooms) {
            sb.AppendLine($"\nROOM: {r.name}");
            var allLabels = r.Anchors.Select(a => a.Label.ToString()).Distinct().ToList();
            sb.AppendLine($"All Labels in Room: {string.Join(", ", allLabels)}");
            sb.AppendLine($"Detected Room Type: {GetRoomLabel(r)}");
            foreach (var a in r.Anchors) {
                sb.AppendLine($"  - ANCHOR: {a.name} | Label: {a.Label} | Pos: {a.transform.position}");
                
                // Exhaustive component check for suspected mesh anchors
                if (a.Label == MRUKAnchor.SceneLabels.GLOBAL_MESH || a.name.Contains("MESH")) {
                    var comps = a.GetComponents<Component>();
                    sb.AppendLine($"    COMPONENTS ON {a.name}: {string.Join(", ", comps.Select(c => c?.GetType().Name ?? "null"))}");
                    var allChildrenComp = a.GetComponentsInChildren<Component>(true);
                    sb.AppendLine($"    TOTAL COMPONENTS IN BRANCH: {allChildrenComp.Length}");
                }

                if (a.PlaneRect.HasValue) sb.AppendLine($"    PLANE: {a.PlaneRect.Value.width:F3}m x {a.PlaneRect.Value.height:F3}m");
                
                var mfs = a.GetComponentsInChildren<MeshFilter>(true);
                foreach(var mf in mfs) sb.AppendLine($"    [MF_IN_ANCHOR] {mf.name} | Mesh: {mf.sharedMesh?.name} | Verts: {mf.sharedMesh?.vertexCount ?? 0}");
            }
        }
        
        sb.AppendLine("\n=== SCENE ROOT OBJECTS AND MESH FILTERS ===");
        var allObjects = GameObject.FindObjectsByType<Transform>(FindObjectsSortMode.None);
        int count = 0;
        foreach(var t in allObjects.Where(x => x.parent == null)) {
            DumpHierarchy(t, "", sb, ref count);
        }
        
        return sb.ToString();
    }

    private void DumpHierarchy(Transform t, string indent, StringBuilder sb, ref int count) {
        if (count > 500) return;
        count++;
        var mf = t.GetComponent<MeshFilter>();
        string mInfo = mf != null ? $" (MESH: {mf.sharedMesh?.name}, V: {mf.sharedMesh?.vertexCount ?? 0})" : "";
        sb.AppendLine($"{indent}- {t.name}{mInfo}");
        foreach (Transform child in t) DumpHierarchy(child, indent + "  ", sb, ref count);
    }

    private string GetGameObjectPath(GameObject obj) {
        string path = "/" + obj.name;
        Transform t = obj.transform;
        while (t.parent != null) {
            t = t.parent;
            path = "/" + t.name + path;
        }
        return path;
    }

    private string GenerateJson(List<MRUKRoom> rooms) {
        var d = new UltraHouseData { exportDate = DateTime.Now.ToString("O"), rooms = rooms.Select(r => new UltraRoom {
            name = GetRoomLabel(r), guid = r.name,
            pos = new Vector3Data(r.transform.position), rot = new Vector4Data(r.transform.rotation),
            anchors = r.Anchors.Select(a => new UltraAnchor {
                label = a.Label.ToString(),
                pos = new Vector3Data(a.transform.position),
                rot = new Vector4Data(a.transform.rotation),
                rect = a.PlaneRect.HasValue ? new OfflineRect { w = a.PlaneRect.Value.width, h = a.PlaneRect.Value.height } : null,
                volume = a.VolumeBounds.HasValue ? new Vector3Data(a.VolumeBounds.Value.size) : null
            }).ToList()
        }).ToList() };
        return JsonUtility.ToJson(d, true);
    }

    private string GetRoomLabel(MRUKRoom r) {
        string roomLabel = "";
        
        // Priority -1: Custom name from OVRSemanticLabels (the most specific name from Space Setup)
        // Use reflection to avoid obsolete warnings in Unity 6
        try {
            var semanticLabels = r.GetComponent<OVRSemanticLabels>();
            if (semanticLabels != null) {
                var prop = semanticLabels.GetType().GetProperty("Labels");
                string labels = prop?.GetValue(semanticLabels) as string;
                if (!string.IsNullOrEmpty(labels)) {
                    string l = labels.ToUpper().Replace(",", "_");
                    if (l != "OTHER" && l != "STORAGE" && l != "ROOM") return l;
                    roomLabel = l;
                }
            }
        } catch {}

        // Priority 0: Direct Label from the Room object
        try {
            var prop = r.GetType().GetProperty("Label");
            if (prop != null) {
                var val = prop.GetValue(r);
                if (val != null) {
                    string l = val.ToString().ToUpper();
                    if (l != "OTHER" && l != "STORAGE" && !string.IsNullOrEmpty(l)) return l;
                    if (string.IsNullOrEmpty(roomLabel)) roomLabel = l;
                }
            }
        } catch {}

        var anchorLabels = r.Anchors.Select(a => a.Label.ToString().ToUpper()).ToList();
        
        // Priority 1: Specific Room Labels found anywhere in anchors
        var roomTypes = new[] { "LIVING_ROOM", "BEDROOM", "KITCHEN", "BATHROOM", "DINING_ROOM", "HALLWAY", "OFFICE", "GARAGE", "LIBRARY", "LAUNDRY_ROOM" };
        foreach (var type in roomTypes) if (anchorLabels.Contains(type)) return type;

        // Priority 2: Use the roomLabel if we found one (even STORAGE)
        if (!string.IsNullOrEmpty(roomLabel) && roomLabel != "OTHER") return roomLabel;

        // Priority 3: Furniture hints
        if (anchorLabels.Any(l => l.Contains("BED"))) return "BEDROOM";
        if (anchorLabels.Any(l => l.Contains("COUCH") || l.Contains("SCREEN") || l.Contains("TELEVISION"))) return "LIVING_ROOM";
        if (anchorLabels.Any(l => l.Contains("SINK") || l.Contains("TOILET") || l.Contains("SHOWER") || l.Contains("BATHTUB"))) return "BATHROOM";
        if (anchorLabels.Any(l => l.Contains("STOVE") || l.Contains("OVEN") || l.Contains("REFRIGERATOR") || l.Contains("KITCHEN"))) return "KITCHEN";
        if (anchorLabels.Any(l => l.Contains("TABLE") && anchorLabels.Count(l => l.Contains("CHAIR")) >= 2)) return "DINING_ROOM";
        if (anchorLabels.Any(l => l.Contains("DESK"))) return "OFFICE";
        
        // Priority 4: Check for ANY label that isn't standard structural
        var structural = new[] { "WALL_FACE", "FLOOR", "CEILING", "DOOR_FRAME", "WINDOW_FRAME", "INVISIBLE_WALL_FACE", "GLOBAL_MESH", "OPENING" };
        var custom = anchorLabels.FirstOrDefault(l => !structural.Contains(l) && l != "OTHER" && l != "STORAGE");
        if (!string.IsNullOrEmpty(custom)) return custom;

        return "Pokoj_" + r.name.Substring(Math.Max(0, r.name.Length - 4));
    }

    private string GetSafeName(string n) { return string.Join("_", n.Split(Path.GetInvalidFileNameChars())).Replace(" ", "_"); }

#if UNITY_EDITOR
    [MenuItem("MRUK/1. Plný Build a Instalace APK", false, 10)]
    public static void BuildAndInstallFull() { string apk = BuildInternal(BuildOptions.None); if(apk != null) InstallAPK(apk); }

    [MenuItem("MRUK/2. Rychlý Build a Instalace APK", false, 11)]
    public static void BuildAPKFast() { string apk = BuildInternal(BuildOptions.Development); if(apk != null) InstallAPK(apk); }

    [MenuItem("MRUK/3. Pouze Instalovat APK (bez buildu)", false, 12)]
    public static void InstallOnly() { 
        string apk = "Builds/XRHouseExporter.apk";
        if (File.Exists(apk)) InstallAPK(apk);
        else Debug.LogError("APK soubor nebyl nalezen.");
    }

    [MenuItem("MRUK/4. Odinstalovat Appku", false, 13)]
    public static void UninstallAPK() { RunAdb("uninstall com.UnityTechnologies.com.unity.template.urpblank"); }

    [MenuItem("MRUK/5. Stáhnout data z Questu (Pull)", false, 30)]
    public static void PullFromQuest() {
        string remote = "/sdcard/Download/XRHouseExports/.";
        string local = Path.GetFullPath("Exports/RoomData");
        Directory.CreateDirectory(local);
        
        Debug.Log($"<color=cyan>Stahuji data z: {remote} do {local}...</color>");
        RunAdb($"pull \"{remote}\" \"{local}\"");
        
        // Try to find the latest session folder
        var dirs = Directory.GetDirectories(local, "Export_*");
        if (dirs.Length > 0) {
            var latest = dirs.OrderByDescending(d => Directory.GetCreationTime(d)).First();
            Debug.Log($"<color=green>Staženo! Otevírám nejnovější export: {Path.GetFileName(latest)}</color>");
            EditorUtility.RevealInFinder(latest);
        } else {
            EditorUtility.RevealInFinder(local);
        }
        
        // Also open the root RoomData folder for convenience
        Application.OpenURL("file://" + local);
    }

    [MenuItem("MRUK/6. Spustit Export (v Play Mode)", false, 31)]
    public static async void ExportFromMenu() {
        var exp = GameObject.FindAnyObjectByType<MRUKExporter>();
        if (exp != null) await exp.ExportAllRooms();
    }

    [MenuItem("MRUK/7. Otevřít složku s Exporty", false, 50)]
    public static void OpenExportsFolder() {
        string path = Path.GetFullPath("Exports/RoomData");
        Directory.CreateDirectory(path);
        EditorUtility.RevealInFinder(path);
    }

    [MenuItem("MRUK/8. Zálohovat Projekt (Zmenšený)", false, 100)]
    public static void BackupProject() {
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmm");
        string zipPath = $"Backups/XRHouse_Backup_{timestamp}.zip";
        Directory.CreateDirectory("Backups");
        try {
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create)) {
                AddFolderToZip(zip, "Assets");
                AddFolderToZip(zip, "Packages");
                AddFolderToZip(zip, "ProjectSettings");
            }
            Debug.Log($"<color=green>ZÁLOHA HOTOVA:</color> {zipPath}");
            EditorUtility.RevealInFinder(zipPath);
        } catch (Exception ex) { Debug.LogError("Záloha selhala: " + ex.Message); }
    }

    private static void AddFolderToZip(ZipArchive zip, string folder) {
        if (!Directory.Exists(folder)) return;
        foreach (string file in Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories)) {
            if (file.EndsWith(".lock") || file.Contains(".TMP")) continue;
            zip.CreateEntryFromFile(file, file.Replace("\\", "/"));
        }
    }

    private static string BuildInternal(BuildOptions o) {
        Debug.Log("<color=cyan>Spouštím Build APK...</color>");
        // Fix warning: Diagnostics require Symbols
        // Use reflection to bypass compilation issues with internal/platform-specific namespaces in Unity 6
        try {
            var type = System.Type.GetType("UnityEditor.Android.UserBuildSettings+DebugSymbols, UnityEditor.Android.Extensions");
            if (type != null) {
                var prop = type.GetProperty("level", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (prop != null) prop.SetValue(null, 1); // 1 = Full symbols
            }
        } catch {}

        File.WriteAllText("Assets/Scripts/VersionDisplay.cs", "using UnityEngine;\nusing TMPro;\npublic class VersionDisplay : MonoBehaviour {\n    public static string BuildTime = \"" + DateTime.Now.ToString("dd.MM. HH:mm") + "\";\n    public TextMeshProUGUI displayText;\n    void Start() { if (displayText != null) displayText.text = \"Verze: \" + BuildTime; }\n}");
        AssetDatabase.Refresh();
        string apk = "Builds/XRHouseExporter.apk"; Directory.CreateDirectory("Builds");
        var report = BuildPipeline.BuildPlayer(new[] { "Assets/Scenes/MRUKExportScene.unity" }, apk, BuildTarget.Android, o);
        if (report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded) {
            Debug.Log($"<color=green>BUILD ÚSPĚŠNĚ DOKONČEN.</color>");
            return apk;
        }
        return null;
    }

    private static void InstallAPK(string apk) { 
        Debug.Log("<color=cyan>Instaluji do Questu...</color>");
        RunAdb("install -r \"" + Path.GetFullPath(apk) + "\""); 
        Debug.Log("<color=green>INSTALACE HOTOVA.</color>");
    }

    private static void RunAdb(string args) {
        string sdk = EditorPrefs.GetString("AndroidSdkRoot");
        if(string.IsNullOrEmpty(sdk)) sdk = Path.Combine(EditorApplication.applicationContentsPath, "PlaybackEngines/AndroidPlayer/SDK");
        string adb = Path.Combine(sdk, "platform-tools", "adb" + (Application.platform == RuntimePlatform.WindowsEditor ? ".exe" : ""));
        if (File.Exists(adb)) {
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(adb, args) { UseShellExecute = false, CreateNoWindow = true });
            process.WaitForExit();
        }
    }
#endif
}
