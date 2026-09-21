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
            if (InstallAPK(apk)) {
                Debug.Log("<color=green>ALL DONE: Build and Installation successful.</color>");
            } else {
                Debug.LogWarning("<color=orange>PARTIAL SUCCESS: Build successful, but Installation failed (is Quest connected?).</color>");
            }
        } else {
            Debug.LogError("<color=red>ERROR: Build failed, installation cancelled.</color>");
        }
    }

    [MenuItem("MRUK/2. Fast Build and Install APK", false, 11)]
    public static void BuildAPKFast() { 
        string apk = BuildInternal(BuildOptions.Development, fast: true); 
        if(apk != null) {
            if (InstallAPK(apk)) {
                Debug.Log("<color=green>FAST BUILD AND INSTALL DONE.</color>");
            } else {
                Debug.LogWarning("<color=orange>FAST BUILD DONE, but Installation failed.</color>");
            }
        }
    }

    [MenuItem("MRUK/3. Install APK Only", false, 12)]
    public static void InstallOnly() { 
        string apk = "Builds/XRHouseExporter.apk";
        if (File.Exists(apk)) {
            var info = new FileInfo(apk);
            Debug.Log($"<color=cyan>Installing APK built on: {info.LastWriteTime:dd.MM. HH:mm:ss} (Size: {info.Length/1024/1024:F1} MB)</color>");
            if (InstallAPK(apk)) {
                Debug.Log("<color=green>INSTALLATION SUCCESSFUL.</color>");
            }
        } else {
            string msg = "APK file not found at Builds/XRHouseExporter.apk. Please run Build first.";
            Debug.LogError(msg);
            EditorUtility.DisplayDialog("Error", msg, "OK");
        }
    }

    [MenuItem("MRUK/4. Uninstall App", false, 13)]
    public static void UninstallAPK() { 
        Debug.Log("<color=orange>Uninstalling application...</color>");
        if (RunAdb("uninstall com.veks.XRHouseDesignExport")) {
            Debug.Log("<color=green>UNINSTALL COMPLETE.</color>");
        } else {
            Debug.LogError("<color=red>UNINSTALL FAILED.</color>");
        }
    }

    [MenuItem("MRUK/5. Pull data from Quest", false, 30)]
    public static void PullFromQuest() {
        string remote = "/sdcard/Download/XRHouseExports/.";
        string local = Path.GetFullPath("Exports/RoomData");
        Directory.CreateDirectory(local);
        Debug.Log($"<color=cyan>Pulling data from Quest to {local}...</color>");

        // Saved scans live in the app's own storage, not in Downloads.
        string scanLocal = Path.GetFullPath("Exports/ScanCache");
        Directory.CreateDirectory(scanLocal);
        RunAdb($"pull \"/sdcard/Android/data/com.veks.XRHouseDesignExport/files/ScanCache/.\" \"{scanLocal}\"");
        if (RunAdb($"pull \"{remote}\" \"{local}\"")) {
            var dirs = Directory.GetDirectories(local, "Export_*");
            if (dirs.Length > 0) {
                var latest = dirs.OrderByDescending(d => Directory.GetCreationTime(d)).First();
                Debug.Log($"<color=green>DOWNLOADED: Opening {Path.GetFileName(latest)}</color>");
                EditorUtility.RevealInFinder(latest);
            } else {
                Debug.LogWarning("No exports were found in the downloaded folder.");
                EditorUtility.RevealInFinder(local);
            }
        } else {
            Debug.LogError("<color=red>PULL FAILED: Could not download data from Quest.</color>");
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

    /// <param name="fast">
    /// Iteration build: same optimised IL2CPP settings as the full build (an unoptimised "Debug" IL2CPP
    /// build runs the app several times slower on the headset, and switching configurations throws the
    /// whole incremental C++ cache away) - it only packages a symbol table instead of full debug symbols
    /// (Diagnostics Data needs at least a symbol table, so turning symbols off completely would warn on every build).
    /// </param>
    public static string BuildInternal(BuildOptions o, bool fast = false) {
        Debug.Log($"<color=cyan>Starting APK Build ({(fast ? "fast" : "full")})...</color>");

        SetDebugSymbols(fast ? "SymbolTable" : "Full");
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;

        // Build timestamp for VersionDisplay.BuildTime - a data file, so no script recompile is triggered.
        Directory.CreateDirectory("Assets/Resources");
        File.WriteAllText("Assets/Resources/BuildInfo.txt", DateTime.Now.ToString("dd.MM. HH:mm:ss"));
        AssetDatabase.Refresh();

        string apk = "Builds/XRHouseExporter.apk"; Directory.CreateDirectory("Builds");
        var report = BuildPipeline.BuildPlayer(new[] { "Assets/Scenes/MRUKExportScene.unity" }, apk, BuildTarget.Android, o);
        if (report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded) {
            Debug.Log($"<color=green>BUILD COMPLETED SUCCESSFULLY</color> in {report.summary.totalTime.TotalSeconds:0} s.");
            return apk;
        }
        Debug.LogError("<color=red>BUILD FAILED!</color> Check Console for details.");
        return null;
    }

    // Android debug symbol level lives in UnityEditor.Android.UserBuildSettings.DebugSymbols, a type that only exists
    // with the Android module installed - reflection keeps the editor tools compiling without it. The enum type is taken
    // from the property itself so the exact enum name does not matter.
    static void SetDebugSymbols(string level) {
        try {
            var userBuildSettings = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("UnityEditor.Android.UserBuildSettings", false))
                .FirstOrDefault(t => t != null);
            var levelProp = userBuildSettings?.GetNestedType("DebugSymbols")?.GetProperty("level");
            if (levelProp == null) throw new MissingMemberException("UserBuildSettings.DebugSymbols.level");
            levelProp.SetValue(null, Enum.Parse(levelProp.PropertyType, level));
            Debug.Log($"<color=green>Android Debug Symbols level set to {levelProp.GetValue(null)}.</color>");
        } catch (Exception ex) {
            Debug.LogWarning($"Could not set the Android debug symbol level via UserBuildSettings ({ex.Message}); using the legacy setting.");
    #pragma warning disable 0618
            EditorUserBuildSettings.androidCreateSymbols = level == "Full" ? AndroidCreateSymbols.Debugging : AndroidCreateSymbols.Public;
    #pragma warning restore 0618
        }
    }

    public static bool InstallAPK(string apk) { 
        Debug.Log("<color=cyan>Installing APK to Quest...</color>");
        return RunAdb("install -r \"" + Path.GetFullPath(apk) + "\""); 
    }

    public static bool RunAdb(string args) {
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
                return false;
            } else {
                Debug.Log($"ADB Success: {output}");
                return true;
            }
        } else {
            Debug.LogError("ADB executable not found. Please check Android SDK path in Preferences.");
            EditorUtility.DisplayDialog("ADB Not Found", "Could not find adb.exe. Please check Android SDK path.", "OK");
            return false;
        }
    }
}
#endif
