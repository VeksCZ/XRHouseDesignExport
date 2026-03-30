using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using System.Globalization;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

public class MRUKExporter : MonoBehaviour
{
    public string exportFolderName = "RoomExports";

#if UNITY_EDITOR && META_XR_SDK_INSTALLED
    [UnityEditor.MenuItem("MRUK/1. Build & Install (FAST)", false, 1)]
    public static void BuildAndInstallFast()
    {
        string packageId = UnityEditor.PlayerSettings.GetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android);
        string adbPath = GetAdbPath();
        if (!string.IsNullOrEmpty(adbPath)) RunAdbCommand(adbPath, $"shell am force-stop {packageId}");

        string apkPath = BuildInternal(BuildOptions.Development | BuildOptions.BuildScriptsOnly);
        if (!string.IsNullOrEmpty(apkPath)) {
            InstallAPK(apkPath);
            Debug.Log("<color=green><b>FAST BUILD AND INSTALL FINISHED!</b></color>");
        }
    }

    [UnityEditor.MenuItem("MRUK/2. Build & Install (FULL)", false, 2)]
    public static void CleanBuildAndInstall()
    {
        UninstallApp();
        string apkPath = BuildInternal(BuildOptions.Development);
        if (!string.IsNullOrEmpty(apkPath)) {
            InstallAPK(apkPath);
            Debug.Log("<color=green><b>CLEAN BUILD AND INSTALL FINISHED!</b></color>");
        }
    }

    [UnityEditor.MenuItem("MRUK/3. Uninstall App from Quest", false, 3)]
    public static void UninstallApp()
    {
        string adbPath = GetAdbPath();
        if (string.IsNullOrEmpty(adbPath)) return;
        string packageId = UnityEditor.PlayerSettings.GetApplicationIdentifier(UnityEditor.Build.NamedBuildTarget.Android);
        
        RunAdbCommand(adbPath, $"shell am force-stop {packageId}");
        
        Debug.Log($"Uninstalling {packageId} from Quest...");
        string res = RunAdbCommand(adbPath, $"uninstall {packageId}");
        Debug.Log("Uninstall result: " + res);
    }

    [UnityEditor.MenuItem("MRUK/4. Pull Exports to PC", false, 20)]
    public static void PullExportsFromQuest()
    {
        string adbPath = GetAdbPath();
        if (string.IsNullOrEmpty(adbPath)) return;

        string remotePath = "/sdcard/Download/XRHouseExports";
        string localPath = Path.Combine(Directory.GetCurrentDirectory(), "Exports/RoomData");

        if (!Directory.Exists(localPath)) Directory.CreateDirectory(localPath);

        Debug.Log($"Pulling exports from Quest: {remotePath} -> {localPath}");
        
        var process = new System.Diagnostics.Process();
        process.StartInfo.FileName = adbPath;
        process.StartInfo.Arguments = $"pull \"{remotePath}/.\" \"{localPath}\"";
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.Start();
        process.WaitForExit();
        
        Debug.Log("Pull finished. Opening folder: " + localPath);
        Application.OpenURL("file://" + localPath);
    }

    [UnityEditor.MenuItem("MRUK/5. Export All Rooms (Editor Mode)", false, 21)]
    public static async void EditorExportAllRooms()
    {
        if (!Application.isPlaying) return;
        var exporter = Object.FindFirstObjectByType<MRUKExporter>() ?? new GameObject("MRUK_Exporter").AddComponent<MRUKExporter>();
        await exporter.ExportAllRooms(null);
    }

    private static string BuildInternal(BuildOptions options)
    {
        string buildPath = "Builds";
        if (!Directory.Exists(buildPath)) Directory.CreateDirectory(buildPath);
        string apkPath = Path.Combine(buildPath, "XRHouseExporter.apk");
        
        BuildPlayerOptions opt = new BuildPlayerOptions();
        opt.scenes = new[] { "Assets/Scenes/MRUKExportScene.unity" };
        opt.locationPathName = apkPath;
        opt.target = BuildTarget.Android;
        opt.options = options;
        
        Debug.Log("Starting Build...");
        var report = BuildPipeline.BuildPlayer(opt);
        if (report.summary.result == UnityEditor.Build.Reporting.BuildResult.Succeeded) {
            Debug.Log($"Build Succeeded: {apkPath}");
            return apkPath;
        } else {
            Debug.LogError("Build Failed! Check console for errors.");
            return null;
        }
    }

    private static void InstallAPK(string apkPath)
    {
        string adbPath = GetAdbPath();
        if (string.IsNullOrEmpty(adbPath)) {
            Debug.LogError("ADB not found. Cannot install APK.");
            return;
        }
        Debug.Log("Installing APK to Quest... (Editor will pause for a moment)");
        string output = RunAdbCommand(adbPath, $"install -r \"{Path.GetFullPath(apkPath)}\" ");
        Debug.Log("ADB Install Output:\n" + output);
    }

    private static string GetAdbPath() {
        string sdkRoot = EditorPrefs.GetString("AndroidSdkRoot");
        if (string.IsNullOrEmpty(sdkRoot)) sdkRoot = Path.Combine(EditorApplication.applicationContentsPath, "PlaybackEngines/AndroidPlayer/SDK");
        string adbPath = Path.Combine(sdkRoot, "platform-tools", "adb.exe");
        if (!File.Exists(adbPath)) adbPath = Path.Combine(sdkRoot, "platform-tools", "adb");
        return File.Exists(adbPath) ? adbPath : null;
    }

    private static string RunAdbCommand(string adbPath, string args)
    {
        var process = new System.Diagnostics.Process();
        process.StartInfo.FileName = adbPath;
        process.StartInfo.Arguments = args;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.CreateNoWindow = true;
        process.Start();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output;
    }
#endif

    public string LastReportPath { get; private set; }

    public async System.Threading.Tasks.Task<bool> ExportAllRooms(XRMenu uiLog = null)
    {
#if META_XR_SDK_INSTALLED
        if (MRUK.Instance == null) return false;
        uiLog?.AddLog("Requesting scene...");
        await MRUK.Instance.LoadSceneFromDevice();
        
        var rooms = MRUK.Instance.Rooms.Where(r => r.Anchors.Any(a => a.Label.ToString().Contains("WALL"))).ToList();
        if (rooms == null || rooms.Count == 0) { uiLog?.AddLog("No rooms found!"); return false; }

        string timestamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
        
        string basePath = Application.isEditor ? 
            Path.Combine(Directory.GetCurrentDirectory(), "RoomExports") : 
            "/sdcard/Download/XRHouseExports";

        string sessionPath = Path.Combine(basePath, "Export_" + timestamp);
        if (!Directory.Exists(basePath)) Directory.CreateDirectory(basePath);
        Directory.CreateDirectory(sessionPath);

        uiLog?.AddLog("Generating house reports...");
        File.WriteAllText(Path.Combine(sessionPath, "house_report.html"), GenerateHtmlReport(rooms, true));
        ExportRawModel(rooms, Path.Combine(sessionPath, "house_scanned_mesh.obj"));
        ExportAnalyticalModel(rooms, Path.Combine(sessionPath, "house_analytical_model.obj"));
        File.WriteAllText(Path.Combine(sessionPath, "house_analytical_model.mtl"), GenerateMtl());
        File.WriteAllText(Path.Combine(sessionPath, "house_data.json"), GenerateJson(rooms));
        
        if (uiLog != null) {
            File.WriteAllText(Path.Combine(sessionPath, "session_debug_log.txt"), uiLog.GetLogHistory());
        }
        
        LastReportPath = Path.Combine(sessionPath, "house_report.html");

        foreach (var room in rooms) {
            string roomName = room.name.Replace(" ", "_").Replace(":", "-");
            string roomPath = Path.Combine(sessionPath, roomName);
            Directory.CreateDirectory(roomPath);
            
            var singleRoomList = new List<MRUKRoom> { room };
            File.WriteAllText(Path.Combine(roomPath, "report.html"), GenerateHtmlReport(singleRoomList, false));
            ExportRawModel(singleRoomList, Path.Combine(roomPath, "scanned_mesh.obj"));
            ExportAnalyticalModel(singleRoomList, Path.Combine(roomPath, "analytical_model.obj"));
            File.WriteAllText(Path.Combine(roomPath, "analytical_model.mtl"), GenerateMtl());
            File.WriteAllText(Path.Combine(roomPath, "room_data.json"), GenerateJson(singleRoomList));
        }

        uiLog?.AddLog("Export finished! Files in RoomExports.");
        return true;
#else
        return false;
#endif
    }

    private string GenerateHtmlReport(List<MRUKRoom> rooms, bool isMaster) {
        StringBuilder html = new StringBuilder();
        html.AppendLine("<!DOCTYPE html><html><head><meta charset='UTF-8'><style>");
        html.AppendLine("body { font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif; margin: 0; padding: 40px; background: #f8f9fa; color: #333; }");
        html.AppendLine(".container { max-width: 1000px; margin: 0 auto; }");
        html.AppendLine(".room-box { background: white; padding: 30px; border-radius: 16px; margin-bottom: 40px; box-shadow: 0 10px 30px rgba(0,0,0,0.05); border: 1px solid #eee; }");
        html.AppendLine("h1 { color: #1a1a1a; font-size: 2.5em; margin-bottom: 30px; text-align: center; }");
        html.AppendLine("h2 { color: #2d3436; margin-top: 0; border-left: 5px solid #0984e3; padding-left: 15px; }");
        html.AppendLine(".stats { display: flex; gap: 20px; margin: 20px 0; color: #636e72; font-size: 0.9em; }");
        html.AppendLine(".stat-item { background: #f1f2f6; padding: 8px 15px; border-radius: 20px; }");
        html.AppendLine("svg { width: 100%; height: auto; background: #ffffff; border-radius: 8px; display: block; margin: 20px 0; filter: drop-shadow(0 2px 5px rgba(0,0,0,0.02)); }");
        html.AppendLine(".dim-text { font-family: 'Segoe UI', sans-serif; font-size: 0.18px; font-weight: 600; fill: #e67e22; paint-order: stroke; stroke: #fff; stroke-width: 0.05px; stroke-linecap: round; stroke-linejoin: round; }");
        html.AppendLine(".wall-line { stroke: #eeeeee; stroke-width: 0.12; stroke-linecap: round; }");
        html.AppendLine(".opening-line { stroke: #3498db; stroke-width: 0.08; }");
        html.AppendLine(".door-line { stroke: #734214; stroke-width: 0.1; }");
        html.AppendLine(".sub-dim-text { font-family: 'Segoe UI', sans-serif; font-size: 0.12px; fill: #7f8c8d; }");
        html.AppendLine(".dim-table { width: 100%; border-collapse: collapse; margin: 20px 0; font-size: 0.9em; }");
        html.AppendLine(".dim-table th, .dim-table td { padding: 10px; border: 1px solid #eee; text-align: left; }");
        html.AppendLine(".dim-table th { background: #fafafa; color: #2d3436; }");
        html.AppendLine(".type-wall { color: #95a5a6; font-weight: bold; }");
        html.AppendLine(".type-door { color: #734214; font-weight: bold; }");
        html.AppendLine(".type-window { color: #3498db; font-weight: bold; }");
        html.AppendLine("@media print { .room-box { page-break-after: always; border: none; box-shadow: none; } }");
        html.AppendLine("</style></head><body><div class='container'>");
        html.AppendLine("<h1>" + (isMaster ? "Property Measurement Report" : "Room Measurement Report") + "</h1>");

        foreach (var room in rooms) {
            var anchors = room.Anchors.Where(a => a.PlaneRect.HasValue).ToList();
            var walls = anchors.Where(a => a.Label.ToString().Contains("WALL")).ToList();
            
            float alignAngle = 0;
            if (walls.Count > 0) {
                var longest = walls.OrderByDescending(w => w.PlaneRect.Value.width).First();
                Vector3 wallDir = longest.transform.right;
                alignAngle = -Mathf.Atan2(wallDir.z, wallDir.x) * Mathf.Rad2Deg;
            }
            Quaternion normRot = Quaternion.Euler(0, alignAngle, 0);

            var items = new List<(string lab, Vector2 p1, Vector2 p2, float w, float h, Vector2 mid, float elev)>();
            float minX = 1000, maxX = -1000, minZ = 1000, maxZ = -1000;
            float totalArea = 0;

            foreach (var a in anchors) {
                string lab = a.Label.ToString().ToUpper();
                if (!lab.Contains("WALL") && !lab.Contains("WINDOW") && !lab.Contains("DOOR") && !lab.Contains("FLOOR")) continue;
                
                float w = a.PlaneRect.Value.width;
                float h = a.PlaneRect.Value.height;
                if (lab.Contains("FLOOR")) { totalArea = w * h; continue; }

                Vector3 localPos = room.transform.InverseTransformPoint(a.transform.position);
                Vector3 localRight = room.transform.InverseTransformDirection(a.transform.right) * (w / 2f);
                
                Vector3 mN = normRot * localPos;
                Vector3 rN = normRot * localRight;

                Vector2 p1 = new Vector2(mN.x - rN.x, mN.z - rN.z);
                Vector2 p2 = new Vector2(mN.x + rN.x, mN.z + rN.z);
                
                minX = Mathf.Min(minX, p1.x, p2.x); maxX = Mathf.Max(maxX, p1.x, p2.x);
                minZ = Mathf.Min(minZ, p1.y, p2.y); maxZ = Mathf.Max(maxZ, p1.y, p2.y);
                                float floorY = anchors.Where(a => a.Label.ToString().Contains("FLOOR")).Select(a => a.transform.position.y).FirstOrDefault();
                float currentElev = a.transform.position.y - (h / 2f) - floorY;
                items.Add((lab, p1, p2, w, h, new Vector2(mN.x, mN.z), currentElev));
            }

            html.AppendLine("<div class='room-box'><h2>Room: " + room.name + "</h2>");
            html.AppendLine("<div class='stats'>");
            html.AppendLine($"<span class='stat-item'><b>Area:</b> {totalArea:F2} m²</span>");
            html.AppendLine($"<span class='stat-item'><b>Perimeter:</b> {(maxX-minX)*2 + (maxZ-minZ)*2:F2} m</span>");
            html.AppendLine("</div>");

            html.AppendLine("<table class='dim-table'>");
            html.AppendLine("<thead><tr><th>Element Type</th><th>Dimensions (W x H)</th><th>Elevation (from floor)</th></tr></thead><tbody>");
            foreach (var i in items.OrderBy(x => x.lab)) {
                string typeClass = i.lab.Contains("WALL") ? "type-wall" : (i.lab.Contains("DOOR") ? "type-door" : "type-window");
                string friendlyLab = i.lab.Replace("_", " ");
                html.AppendLine($"<tr><td class='{typeClass}'>{friendlyLab}</td><td>{i.w:F2} m x {i.h:F2} m</td></tr>");
            }
            html.AppendLine("</tbody></table>");

            float pad = 1.0f;
            float totalW = (maxX - minX) + pad * 2;
            float totalH = (maxZ - minZ) + pad * 2;
            
            html.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "<svg viewBox='{0:F2} {1:F2} {2:F2} {3:F2}'>", minX - pad, -(maxZ + pad), totalW, totalH));
            
            html.AppendLine("<defs><pattern id='grid' width='1' height='1' patternUnits='userSpaceOnUse'>");
            html.AppendLine("<path d='M 1 0 L 0 0 0 1' fill='none' stroke='#f0f0f0' stroke-width='0.02'/></pattern></defs>");
            html.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "<rect x='{0:F2}' y='{1:F2}' width='{2:F2}' height='{3:F2}' fill='url(#grid)' />", minX-pad, -(maxZ+pad), totalW, totalH));

            foreach (var i in items) {
                string cssClass = i.lab.Contains("WALL") ? "wall-line" : (i.lab.Contains("DOOR") ? "door-line" : "opening-line");
                html.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "<line x1='{0:F2}' y1='{1:F2}' x2='{2:F2}' y2='{3:F2}' class='{4}' />", 
                    i.p1.x, -i.p1.y, i.p2.x, -i.p2.y, cssClass));
            }
            
            var walls2D = items.Where(x => x.lab.Contains("WALL")).ToList();
            var openings2D = items.Where(x => !x.lab.Contains("WALL") && !x.lab.Contains("FLOOR")).ToList();

            foreach (var wall in walls2D) {
                Vector2 wallDir = (wall.p2 - wall.p1).normalized;
                Vector2 normal = new Vector2(-wallDir.y, wallDir.x);
                Vector2 lineMid = (wall.p1 + wall.p2) * 0.5f;
                if (Vector2.Dot(normal, lineMid) < 0) normal = -normal;
                
                Vector2 wallTextPos = lineMid + normal * 0.45f;
                float wallAngle = Mathf.Atan2(-(wall.p2.y - wall.p1.y), wall.p2.x - wall.p1.x) * Mathf.Rad2Deg;
                if (wallAngle > 90) wallAngle -= 180; if (wallAngle < -90) wallAngle += 180;
                html.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "<text x='{0:F2}' y='{1:F2}' class='dim-text' text-anchor='middle' transform='rotate({2:F2} {0:F2} {1:F2})'>{3:F2}m</text>", wallTextPos.x, -wallTextPos.y, wallAngle, wall.w));

                var onWall = openings2D.Where(o => {
                    float distToLine = Mathf.Abs(Vector2.Dot(normal, o.mid - wall.p1));
                    float t = Vector2.Dot(wallDir, o.mid - wall.p1);
                    return distToLine < 0.2f && t > -0.1f && t < wall.w + 0.1f;
                }).Select(o => {
                    float t1 = Vector2.Dot(wallDir, o.p1 - wall.p1);
                    float t2 = Vector2.Dot(wallDir, o.p2 - wall.p1);
                    return (s: Mathf.Clamp(Mathf.Min(t1, t2), 0, wall.w), e: Mathf.Clamp(Mathf.Max(t1, t2), 0, wall.w), w: o.w);
                }).OrderBy(o => o.s).ToList();

                float lastT = 0;
                foreach (var ow in onWall) {
                    float gap = ow.s - lastT;
                    if (gap > 0.05f) {
                        Vector2 gapMid = wall.p1 + wallDir * (lastT + gap/2f) + normal * 0.15f;
                        html.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "<text x='{0:F2}' y='{1:F2}' class='sub-dim-text' text-anchor='middle' transform='rotate({2:F2} {0:F2} {1:F2})'>{3:F2}m</text>", gapMid.x, -gapMid.y, wallAngle, gap));
                    }
                    Vector2 openMid = wall.p1 + wallDir * (ow.s + (ow.e-ow.s)/2f) - normal * 0.2f;
                    html.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "<text x='{0:F2}' y='{1:F2}' class='sub-dim-text' style='fill:#3498db' text-anchor='middle' transform='rotate({2:F2} {0:F2} {1:F2})'>{3:F2}m</text>", openMid.x, -openMid.y, wallAngle, ow.w));
                    lastT = ow.e;
                }
                float finalGap = wall.w - lastT;
                if (finalGap > 0.05f) {
                    Vector2 gapMid = wall.p1 + wallDir * (lastT + finalGap/2f) + normal * 0.15f;
                    html.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "<text x='{0:F2}' y='{1:F2}' class='sub-dim-text' text-anchor='middle' transform='rotate({2:F2} {0:F2} {1:F2})'>{3:F2}m</text>", gapMid.x, -gapMid.y, wallAngle, finalGap));
                }
            }
            
            html.AppendLine("</svg></div>");
        }
        html.AppendLine("</div></body></html>");
        return html.ToString();
    }

    private string GenerateJson(List<MRUKRoom> rooms) {
        StringBuilder json = new StringBuilder();
        json.AppendLine("{");
        json.AppendLine("  \"exportDate\": \"" + System.DateTime.Now.ToString("O") + "\", ");
        json.AppendLine("  \"rooms\": [");
        for (int i=0; i<rooms.Count; i++) {
            var room = rooms[i];
            json.AppendLine("    {");
            json.AppendLine($"      \"name\": \"{room.name}\", ");
            json.AppendLine($"      \"pos\": {{\"x\":{room.transform.position.x:F3}, \"y\":{room.transform.position.y:F3}, \"z\":{room.transform.position.z:F3}}}, ");
            json.AppendLine("      \"anchors\": [");
            var anchors = room.Anchors;
            for (int j=0; j<anchors.Count; j++) {
                var a = anchors[j];
                json.AppendLine("        {");
                json.AppendLine($"          \"label\": \"{a.Label}\", ");
                if (a.PlaneRect.HasValue) {
                    var r = a.PlaneRect.Value;
                    json.AppendLine($"          \"rect\": {{\"w\":{r.width:F3}, \"h\":{r.height:F3}}}, ");
                }
                json.AppendLine($"          \"pos\": {{\"x\":{a.transform.position.x:F3}, \"y\":{a.transform.position.y:F3}, \"z\":{a.transform.position.z:F3}}}, ");
                json.AppendLine($"          \"rot\": {{\"x\":{a.transform.rotation.x:F3}, \"y\":{a.transform.rotation.y:F3}, \"z\":{a.transform.rotation.z:F3}, \"w\":{a.transform.rotation.w:F3}}} ");
                json.AppendLine("        }" + (j < anchors.Count - 1 ? "," : ""));
            }
            json.AppendLine("      ]");
            json.AppendLine("    }" + (i < rooms.Count - 1 ? "," : ""));
        }
        json.AppendLine("  ]");
        json.AppendLine("}");
        return json.ToString();
    }

    private void ExportRawModel(List<MRUKRoom> rooms, string path) {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("# Scanned Room Mesh Export");
        int vOff = 0;
        foreach (var room in rooms) {
            foreach (var a in room.Anchors) {
                MeshFilter mf = a.GetComponentInChildren<MeshFilter>();
                if (mf != null && mf.sharedMesh != null) {
                    sb.AppendLine($"o {a.name}_{a.Label}");
                    foreach (var v in mf.sharedMesh.vertices) {
                        Vector3 w = a.transform.TransformPoint(v);
                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0:F4} {1:F4} {2:F4}", w.x, w.y, -w.z));
                    }
                    int[] tris = mf.sharedMesh.triangles;
                    for (int i=0; i<tris.Length; i+=3) {
                        sb.AppendLine($"f {tris[i]+1+vOff} {tris[i+2]+1+vOff} {tris[i+1]+1+vOff}");
                    }
                    vOff += mf.sharedMesh.vertexCount;
                }
            }
        }
        File.WriteAllText(path, sb.ToString());
    }

    private void ExportAnalyticalModel(List<MRUKRoom> rooms, string path) {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("# Analytical Room Model (Volumes with Openings)");
        sb.AppendLine("mtllib analytical_model.mtl");
        int vOff = 0;
        foreach (var room in rooms) {
            var anchors = room.Anchors.Where(a => a.PlaneRect.HasValue).ToList();
            var walls = anchors.Where(a => a.Label.ToString().Contains("WALL")).ToList();
            var openings = anchors.Where(a => a.Label.ToString().Contains("DOOR") || a.Label.ToString().Contains("WINDOW")).ToList();
            var floors = anchors.Where(a => a.Label.ToString().Contains("FLOOR")).ToList();

            foreach (var f in floors) {
                AddBoxToObj(sb, f.transform.position, f.transform.rotation, f.PlaneRect.Value.width, f.PlaneRect.Value.height, 0.02f, "FloorMat", ref vOff);
            }
            foreach (var a in openings) {
                string mat = a.Label.ToString().ToUpper().Contains("DOOR") ? "DoorMat" : "WindowMat";
                AddBoxToObj(sb, a.transform.position, a.transform.rotation, a.PlaneRect.Value.width, a.PlaneRect.Value.height, 0.10f, mat, ref vOff);
            }
            foreach (var w in walls) {
                float wallW = w.PlaneRect.Value.width; float wallH = w.PlaneRect.Value.height;
                var wallHoles = openings.Where(o => {
                    Vector3 lp = w.transform.InverseTransformPoint(o.transform.position);
                    return Mathf.Abs(lp.z) < 0.15f && Mathf.Abs(lp.x) < (wallW/2f + 0.05f) && Mathf.Abs(lp.y) < (wallH/2f + 0.05f);
                }).ToList();

                if (wallHoles.Count > 0) {
                    var xCoords = new List<float> { -wallW/2f, wallW/2f };
                    var yCoords = new List<float> { -wallH/2f, wallH/2f };
                    foreach (var hole in wallHoles) {
                        Vector3 loc = w.transform.InverseTransformPoint(hole.transform.position);
                        float hh = hole.PlaneRect.Value.height;
                        // Clamp window vertical bounds within wall height
                        yCoords.Add(Mathf.Clamp(loc.y - hh/2f, -wallH/2f, wallH/2f));
                        yCoords.Add(Mathf.Clamp(loc.y + hh/2f, -wallH/2f, wallH/2f));
                    };
                    foreach (var hole in wallHoles) {
                        Vector3 loc = w.transform.InverseTransformPoint(hole.transform.position);
                        float hw = hole.PlaneRect.Value.width; float hh = hole.PlaneRect.Value.height;
                        xCoords.Add(loc.x - hw/2f); xCoords.Add(loc.x + hw/2f);
                        yCoords.Add(loc.y - hh/2f); yCoords.Add(loc.y + hh/2f);
                        if (hole.Label.ToString().Contains("WINDOW")) UnityEngine.Debug.Log($"WINDOW DEBUG: Name={hole.name}, LocalY={loc.y:F2}, H={hh:F2}");
                    }
                    var sortedX = xCoords.Distinct().OrderBy(x => x).ToList();
                    var sortedY = yCoords.Distinct().OrderBy(y => y).ToList();

                    for (int i = 0; i < sortedX.Count - 1; i++) {
                        for (int j = 0; j < sortedY.Count - 1; j++) {
                            float x1 = sortedX[i], x2 = sortedX[i+1], y1 = sortedY[j], y2 = sortedY[j+1];
                            if (x2 - x1 < 0.005f || y2 - y1 < 0.005f) continue;
                            Vector2 segmentMid = new Vector2((x1+x2)/2f, (y1+y2)/2f);
                            bool isHole = false;
                            foreach (var hole in wallHoles) {
                                Vector3 loc = w.transform.InverseTransformPoint(hole.transform.position);
                                float hw = hole.PlaneRect.Value.width; float hh = hole.PlaneRect.Value.height;
                                if (segmentMid.x > loc.x-hw/2f+0.001f && segmentMid.x < loc.x+hw/2f-0.001f &&
                                    segmentMid.y > loc.y-hh/2f+0.001f && segmentMid.y < loc.y+hh/2f-0.001f) { isHole = true; break; }
                            }
                            if (!isHole) {
                                Vector3 pos = w.transform.TransformPoint(new Vector3(segmentMid.x, segmentMid.y, 0));
                                AddBoxToObj(sb, pos, w.transform.rotation, x2-x1, y2-y1, 0.25f, "WallMat", ref vOff);
                            }
                        }
                    }
                } else {
                    AddBoxToObj(sb, w.transform.position, w.transform.rotation, wallW, wallH, 0.25f, "WallMat", ref vOff);
                }
            }
        }
        File.WriteAllText(path, sb.ToString());
    }

    private void AddBoxToObj(StringBuilder sb, Vector3 pos, Quaternion rot, float w, float h, float d, string mat, ref int vOff) {
        if (w <= 0.005f || h <= 0.005f) return;
        sb.AppendLine($"usemtl {mat}");
        Vector3 hD = new Vector3(w/2, h/2, d/2);
        Vector3[] v = {
            new Vector3(-hD.x,-hD.y,-hD.z), new Vector3(hD.x,-hD.y,-hD.z), new Vector3(hD.x,hD.y,-hD.z), new Vector3(-hD.x,hD.y,-hD.z),
            new Vector3(-hD.x,-hD.y, hD.z), new Vector3(hD.x,-hD.y, hD.z), new Vector3(hD.x,hD.y, hD.z), new Vector3(-hD.x,hD.y, hD.z)
        };
        foreach (var pt in v) {
            Vector3 worldPt = pos + rot * pt;
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "v {0:F4} {1:F4} {2:F4}", worldPt.x, worldPt.y, -worldPt.z));
        }
        int[][] f = {
            new int[]{1,3,2}, new int[]{1,4,3}, new int[]{5,6,7}, new int[]{5,7,8},
            new int[]{1,2,6}, new int[]{1,6,5}, new int[]{4,7,3}, new int[]{4,8,7},
            new int[]{1,5,8}, new int[]{1,8,4}, new int[]{2,3,7}, new int[]{2,7,6}
        };
        foreach (var tri in f) sb.AppendLine($"f {tri[0]+vOff} {tri[1]+vOff} {tri[2]+vOff}");
        vOff += 8;
    }

    private string GenerateMtl() {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("newmtl WallMat\nKd 0.35 0.35 0.35");
        sb.AppendLine("newmtl DoorMat\nKd 0.45 0.26 0.08"); // Brown
        sb.AppendLine("newmtl WindowMat\nKd 0.03 0.51 0.89");
        sb.AppendLine("newmtl FloorMat\nKd 0.8 0.8 0.8");
        return sb.ToString();
    }

    private string GetRoomLabel(MRUKRoom room) => room.name;
}
