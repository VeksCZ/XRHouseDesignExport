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
        if (uiLog != null) uiLog.AddLog("Příprava exportu...");
        try {
            if (MRUK.Instance == null) return false;
            if (!Application.isEditor) await MRUK.Instance.LoadSceneFromDevice();

            await System.Threading.Tasks.Task.Delay(1500); 

            var rooms = MRUK.Instance.Rooms.ToList();
            if (rooms.Count == 0) return false;

            // Calculate global correction angle (align to longest wall)
            float globalAngle = 0; float maxW = 0;
            foreach (var r in rooms) {
                foreach (var a in r.Anchors.Where(x => x.Label.ToString().ToUpper().Contains("WALL") && x.PlaneRect.HasValue)) {
                    if (a.PlaneRect.Value.width > maxW) { 
                        maxW = a.PlaneRect.Value.width; 
                        globalAngle = -a.transform.eulerAngles.y; 
                    }
                }
            }

            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string root = Application.isEditor ? "Exports/RoomData" : "/sdcard/Download/XRHouseExports";
            string session = Path.Combine(root, "Export_" + ts);
            Directory.CreateDirectory(session);

            File.WriteAllText(Path.Combine(session, "full_scene_dump.txt"), GenerateSceneDump(rooms));
            File.WriteAllText(Path.Combine(session, "house_data.json"), GenerateJson(rooms));
            
            // BOTH REPORTS: V1 (Classic) and V2 (Interactive)
            File.WriteAllText(Path.Combine(session, "house_report_v1.html"), MRUKReportBuilder.GenerateFullReport(rooms, true, true, globalAngle));
            File.WriteAllText(Path.Combine(session, "house_report_v2.html"), MRUKReportBuilderV2.GenerateFullReport(rooms, true, true, globalAngle));
            
            // MODELS
            File.WriteAllText(Path.Combine(session, "house_analytical.obj"), MRUKModelExporterV2.GenerateOBJ(rooms, false, globalAngle));
            File.WriteAllText(Path.Combine(session, "house_mesh.obj"), MRUKModelExporterV2.GenerateOBJ(rooms, true, globalAngle, true));
            File.WriteAllText(Path.Combine(session, "house_model.mtl"), MRUKModelExporterV2.GenerateMTL());
            
            LastReportPath = Path.Combine(session, "house_report_v2.html");

            foreach(var r in rooms) {
                string rName = GetRoomLabel(r);
                string rP = Path.Combine(session, GetSafeName(rName) + "_-_" + r.name.Substring(Math.Max(0, r.name.Length-4)));
                Directory.CreateDirectory(rP);
                var s = new List<MRUKRoom>{r};
                File.WriteAllText(Path.Combine(rP, "report_v1.html"), MRUKReportBuilder.GenerateFullReport(s, true, false, globalAngle));
                File.WriteAllText(Path.Combine(rP, "report_v2.html"), MRUKReportBuilderV2.GenerateFullReport(s, true, false, globalAngle));
                File.WriteAllText(Path.Combine(rP, "analytical.obj"), MRUKModelExporterV2.GenerateOBJ(s, false, globalAngle));
                File.WriteAllText(Path.Combine(rP, "mesh.obj"), MRUKModelExporterV2.GenerateOBJ(s, true, globalAngle, false));
            }

            if (uiLog != null) uiLog.AddLog("Export hotov!");
            return true;
        } catch (Exception ex) {
            if (uiLog != null) uiLog.AddLog("CHYBA: " + ex.Message);
            return false;
        }
#else
        return false;
#endif
    }

    private string GenerateSceneDump(List<MRUKRoom> rooms) {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("=== EXHAUSTIVE SCENE DUMP (V7) ===");
        foreach (var r in rooms) {
            sb.AppendLine($"\nROOM: {r.name}");
            var allLabels = r.Anchors.Select(a => a.Label.ToString()).Distinct().ToList();
            sb.AppendLine($"All Labels in Room: {string.Join(", ", allLabels)}");
            sb.AppendLine($"Detected Room Type: {GetRoomLabel(r)}");
            foreach (var a in r.Anchors) {
                sb.AppendLine($"  - ANCHOR: {a.name} | Label: {a.Label} | Rot: {a.transform.eulerAngles}");
                if (a.PlaneRect.HasValue) sb.AppendLine($"    PLANE: {a.PlaneRect.Value.width:F3}m x {a.PlaneRect.Value.height:F3}m");
                var mfs = a.GetComponentsInChildren<MeshFilter>(true);
                foreach(var mf in mfs) sb.AppendLine($"    [MESH] {mf.name}: {mf.sharedMesh?.vertexCount ?? 0} verts");
            }
        }
        return sb.ToString();
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
        var roomTypes = new[] { "LIVING_ROOM", "BEDROOM", "KITCHEN", "BATHROOM", "DINING_ROOM", "HALLWAY", "OFFICE", "GARAGE", "STORAGE", "LAUNDRY_ROOM", "LIBRARY" };
        foreach (var type in roomTypes) if (r.Anchors.Any(a => a.Label.ToString().ToUpper() == type)) return type;
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
        RunAdb($"pull \"{remote}\" \"{local}\"");
        EditorUtility.RevealInFinder(local);
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
