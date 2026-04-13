#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.IO.Compression;
using UnityEditor;
using UnityEngine;

public static class MRUKEditorTools {
    [MenuItem("MRUK/1. Plný Build a Instalace APK", false, 10)]
    public static void BuildAndInstallFull() { 
        string apk = BuildInternal(BuildOptions.None); 
        if(apk != null) {
            InstallAPK(apk);
            Debug.Log("<color=green>VŠE HOTOVO: Build i Instalace proběhly úspěšně.</color>");
        } else {
            Debug.LogError("<color=red>CHYBA: Build se nezdařil, instalace zrušena.</color>");
        }
    }

    [MenuItem("MRUK/2. Rychlý Build a Instalace APK", false, 11)]
    public static void BuildAPKFast() { 
        string apk = BuildInternal(BuildOptions.Development); 
        if(apk != null) {
            InstallAPK(apk);
            Debug.Log("<color=green>RYCHLÝ BUILD HOTOV.</color>");
        }
    }

    [MenuItem("MRUK/3. Pouze Instalovat APK", false, 12)]
    public static void InstallOnly() { 
        string apk = "Builds/XRHouseExporter.apk";
        if (File.Exists(apk)) {
            InstallAPK(apk);
        } else {
            Debug.LogError("APK soubor nebyl nalezen v Builds/XRHouseExporter.apk");
        }
    }

    [MenuItem("MRUK/4. Odinstalovat Appku", false, 13)]
    public static void UninstallAPK() { 
        Debug.Log("<color=orange>Odinstalovávám aplikaci...</color>");
        RunAdb("uninstall com.UnityTechnologies.com.unity.template.urpblank"); 
        Debug.Log("<color=green>ODINSTALACE DOKONČENA.</color>");
    }

    [MenuItem("MRUK/5. Stáhnout data z Questu (Pull)", false, 30)]
    public static void PullFromQuest() {
        string remote = "/sdcard/Download/XRHouseExports/.";
        string local = Path.GetFullPath("Exports/RoomData");
        Directory.CreateDirectory(local);
        Debug.Log($"<color=cyan>Stahuji data z Questu do {local}...</color>");
        RunAdb($"pull \"{remote}\" \"{local}\"");
        var dirs = Directory.GetDirectories(local, "Export_*");
        if (dirs.Length > 0) {
            var latest = dirs.OrderByDescending(d => Directory.GetCreationTime(d)).First();
            Debug.Log($"<color=green>STAŽENO: Otevírám {Path.GetFileName(latest)}</color>");
            EditorUtility.RevealInFinder(latest);
        } else {
            Debug.LogWarning("Žádné exporty nebyly staženy.");
            EditorUtility.RevealInFinder(local);
        }
        Application.OpenURL("file://" + local);
    }

    [MenuItem("MRUK/6. Spustit Export (v Play Mode)", false, 31)]
    public static async void ExportFromMenu() {
        var exp = UnityEngine.Object.FindAnyObjectByType<MRUKExporter>();
        if (exp != null) await exp.ExportAllRooms();
        else Debug.LogError("MRUKExporter nebyl ve scéně nalezen.");
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

    public static string BuildInternal(BuildOptions o) {
        Debug.Log("<color=cyan>Spouštím Build APK...</color>");
        try {
            var type = Type.GetType("UnityEditor.Android.UserBuildSettings+DebugSymbols, UnityEditor.Android.Extensions");
            if (type != null) {
                var prop = type.GetProperty("level", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (prop != null) prop.SetValue(null, 1);
            }
        } catch {}

        File.WriteAllText("Assets/Scripts/VersionDisplay.cs", "using UnityEngine;\nusing TMPro;\npublic class VersionDisplay : MonoBehaviour {\n    public static string BuildTime = \"" + DateTime.Now.ToString("dd.MM. HH:mm") + "\";\n    public TextMeshProUGUI displayText;\n    void Start() { if (displayText != null) displayText.text = \"Verze: \" + BuildTime; }\n}");
        AssetDatabase.Refresh();
        string apk = "Builds/XRHouseExporter.apk"; Directory.CreateDirectory("Builds");
        var report = BuildPipeline.BuildPlayer(new[] { "Assets/Scenes/MRUKExportScene.unity" }, apk, BuildTarget.Android, o);
        if (report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded) {
            Debug.Log("<color=green>BUILD ÚSPĚŠNĚ DOKONČEN.</color>");
            return apk;
        }
        Debug.LogError("<color=red>BUILD SELHAL!</color> Zkontroluj Console pro detaily.");
        return null;
    }

    public static void InstallAPK(string apk) { 
        Debug.Log("<color=cyan>Instaluji APK do Questu...</color>");
        RunAdb("install -r \"" + Path.GetFullPath(apk) + "\""); 
        Debug.Log("<color=green>INSTALACE HOTOVA.</color>");
    }

    public static void RunAdb(string args) {
        string sdk = EditorPrefs.GetString("AndroidSdkRoot");
        if(string.IsNullOrEmpty(sdk)) sdk = Path.Combine(EditorApplication.applicationContentsPath, "PlaybackEngines/AndroidPlayer/SDK");
        string adb = Path.Combine(sdk, "platform-tools", "adb" + (Application.platform == RuntimePlatform.WindowsEditor ? ".exe" : ""));
        if (File.Exists(adb)) {
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(adb, args) { UseShellExecute = false, CreateNoWindow = true });
            process.WaitForExit();
        }
    }
}
#endif
