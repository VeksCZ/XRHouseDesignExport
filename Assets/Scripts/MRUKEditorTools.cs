#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.IO.Compression;
using UnityEditor;
using UnityEngine;

public static class MRUKEditorTools {
    [MenuItem("MRUK/1. Full Build and Install APK", false, 10)]
    public static void BuildAndInstallFull() { 
        string apk = BuildInternal(BuildOptions.None); 
        if(apk != null) {
            InstallAPK(apk);
            Debug.Log("<color=green>ALL DONE: Build and Installation successful.</color>");
        } else {
            Debug.LogError("<color=red>ERROR: Build failed, installation cancelled.</color>");
        }
    }

    [MenuItem("MRUK/2. Fast Build and Install APK", false, 11)]
    public static void BuildAPKFast() { 
        string apk = BuildInternal(BuildOptions.Development); 
        if(apk != null) {
            InstallAPK(apk);
            Debug.Log("<color=green>FAST BUILD DONE.</color>");
        }
    }

    [MenuItem("MRUK/3. Install APK Only", false, 12)]
    public static void InstallOnly() { 
        string apk = "Builds/XRHouseExporter.apk";
        if (File.Exists(apk)) {
            var info = new FileInfo(apk);
            Debug.Log($"<color=cyan>Installing APK built on: {info.LastWriteTime:dd.MM. HH:mm:ss} (Size: {info.Length/1024/1024:F1} MB)</color>");
            InstallAPK(apk);
        } else {
            string msg = "APK file not found at Builds/XRHouseExporter.apk. Please run Build first.";
            Debug.LogError(msg);
            EditorUtility.DisplayDialog("Error", msg, "OK");
        }
    }

    [MenuItem("MRUK/4. Uninstall App", false, 13)]
    public static void UninstallAPK() { 
        Debug.Log("<color=orange>Uninstalling application...</color>");
        RunAdb("uninstall com.veks.XRHouseDesignExport"); 
        Debug.Log("<color=green>UNINSTALL COMPLETE.</color>");
    }

    [MenuItem("MRUK/5. Pull data from Quest", false, 30)]
    public static void PullFromQuest() {
        string remote = "/sdcard/Download/XRHouseExports/.";
        string local = Path.GetFullPath("Exports/RoomData");
        Directory.CreateDirectory(local);
        Debug.Log($"<color=cyan>Pulling data from Quest to {local}...</color>");
        RunAdb($"pull \"{remote}\" \"{local}\"");
        var dirs = Directory.GetDirectories(local, "Export_*");
        if (dirs.Length > 0) {
            var latest = dirs.OrderByDescending(d => Directory.GetCreationTime(d)).First();
            Debug.Log($"<color=green>DOWNLOADED: Opening {Path.GetFileName(latest)}</color>");
            EditorUtility.RevealInFinder(latest);
        } else {
            Debug.LogWarning("No exports were downloaded.");
            EditorUtility.RevealInFinder(local);
        }
        Application.OpenURL("file://" + local);
    }

    [MenuItem("MRUK/6. Run Export (in Play Mode)", false, 31)]
    public static async void ExportFromMenu() {
        var exp = UnityEngine.Object.FindAnyObjectByType<MRUKExporter>();
        if (exp != null) await exp.ExportAllRooms();
        else Debug.LogError("MRUKExporter not found in scene.");
    }

    [MenuItem("MRUK/7. Open Exports Folder", false, 50)]
    public static void OpenExportsFolder() {
        string path = Path.GetFullPath("Exports/RoomData");
        Directory.CreateDirectory(path);
        EditorUtility.RevealInFinder(path);
    }

    [MenuItem("MRUK/8. Backup Project (Zipped)", false, 100)]
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
            Debug.Log($"<color=green>BACKUP DONE:</color> {zipPath}");
            EditorUtility.RevealInFinder(zipPath);
        } catch (Exception ex) { Debug.LogError("Backup failed: " + ex.Message); }
    }

    private static void AddFolderToZip(ZipArchive zip, string folder) {
        if (!Directory.Exists(folder)) return;
        foreach (string file in Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories)) {
            if (file.EndsWith(".lock") || file.Contains(".TMP")) continue;
            zip.CreateEntryFromFile(file, file.Replace("\\", "/"));
        }
    }

    public static string BuildInternal(BuildOptions o) {
        Debug.Log("<color=cyan>Starting APK Build...</color>");
        
        // Fix for obsolete warning: Use reflection to set Debug Symbols level
        try {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "UnityEditor.Android.Extensions");
            if (assembly != null) {
                var type = assembly.GetType("UnityEditor.Android.UserBuildSettings");
                var debugSymbolsType = type?.GetNestedType("DebugSymbols");
                var levelProp = debugSymbolsType?.GetProperty("level");
                var levelEnumType = assembly.GetType("UnityEditor.Android.DebugSymbols+Level");
                if (levelProp != null && levelEnumType != null) {
                    var fullValue = Enum.Parse(levelEnumType, "Full");
                    levelProp.SetValue(null, fullValue);
                    Debug.Log("<color=green>Android Debug Symbols level set to Full.</color>");
                }
            }
        } catch { /* fallback to old way if reflection fails */ 
    #pragma warning disable 0618
            EditorUserBuildSettings.androidCreateSymbols = AndroidCreateSymbols.Debugging;
    #pragma warning restore 0618
        }
        
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64; 

        File.WriteAllText("Assets/Scripts/VersionDisplay.cs", "using UnityEngine;\nusing TMPro;\npublic class VersionDisplay : MonoBehaviour {\n    public static string BuildTime = \"" + DateTime.Now.ToString("dd.MM. HH:mm:ss") + "\";\n    public TextMeshProUGUI displayText;\n    void Start() { if (displayText != null) displayText.text = \"Version: \" + BuildTime; }\n}");
AssetDatabase.Refresh();
string apk = "Builds/XRHouseExporter.apk"; Directory.CreateDirectory("Builds");
        var report = BuildPipeline.BuildPlayer(new[] { "Assets/Scenes/MRUKExportScene.unity" }, apk, BuildTarget.Android, o);
        if (report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded) {
            Debug.Log("<color=green>BUILD COMPLETED SUCCESSFULLY.</color>");
            return apk;
        }
        Debug.LogError("<color=red>BUILD FAILED!</color> Check Console for details.");
        return null;
    }

    public static void InstallAPK(string apk) { 
        Debug.Log("<color=cyan>Installing APK to Quest...</color>");
        RunAdb("install -r \"" + Path.GetFullPath(apk) + "\""); 
        Debug.Log("<color=green>INSTALLATION DONE.</color>");
    }

    public static void RunAdb(string args) {
        string sdk = EditorPrefs.GetString("AndroidSdkRoot");
        if(string.IsNullOrEmpty(sdk)) sdk = Path.Combine(EditorApplication.applicationContentsPath, "PlaybackEngines/AndroidPlayer/SDK");
        string adb = Path.Combine(sdk, "platform-tools", "adb" + (Application.platform == RuntimePlatform.WindowsEditor ? ".exe" : ""));
        
        if (File.Exists(adb)) {
            var info = new System.Diagnostics.ProcessStartInfo(adb, args) { 
                UseShellExecute = false, 
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            var process = System.Diagnostics.Process.Start(info);
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            
            if (process.ExitCode != 0) {
                Debug.LogError($"ADB Error ({process.ExitCode}): {error}");
                EditorUtility.DisplayDialog("ADB Error", $"Command failed: {args}\n\nError: {error}", "OK");
            } else {
                Debug.Log($"ADB Success: {output}");
            }
        } else {
            Debug.LogError("ADB executable not found. Please check Android SDK path in Preferences.");
            EditorUtility.DisplayDialog("ADB Not Found", "Could not find adb.exe. Please check Android SDK path.", "OK");
        }
    }
}
#endif
