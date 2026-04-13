using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

public class MRUKExporter : MonoBehaviour {
    public XRMenu uiLog;
    private StringBuilder dLog = new StringBuilder();
    public static string LastReportPath = "";

    public async void OnExportButton() 
    { 
        await ExportAllRooms(uiLog); 
    }

    public async System.Threading.Tasks.Task<bool> ExportAllRooms(XRMenu uiLog = null) {
    #if META_XR_SDK_INSTALLED
        dLog.Clear();
        dLog.AppendLine($"=== EXPORT START {DateTime.Now} ===");
        string root = Application.isEditor ? "Exports/RoomData" : "/sdcard/Download/XRHouseExports";
        
        if (uiLog != null) uiLog.AddLog("Příprava exportu...");
        
        try {
            if (MRUK.Instance == null) {
                dLog.AppendLine("ERROR: MRUK Instance not found.");
                return false;
            }
            if (!Application.isEditor) {
                dLog.AppendLine("Loading scene from device (V2 High-Fidelity)...");
                if (uiLog != null) uiLog.AddLog("Načítám High-Fidelity scénu...");
                // Explicitly request V2 model for dense mesh access in 2026
                await MRUK.Instance.LoadSceneFromDevice(true, true, MRUK.SceneModel.V2);
            }

            await System.Threading.Tasks.Task.Delay(1500); 

            // Filter rooms: Only include rooms that have at least one floor anchor
            var rooms = MRUK.Instance.Rooms.Where(r => {
                try {
                    var prop = r.GetType().GetProperty("FloorAnchors");
                    var list = prop?.GetValue(r) as System.Collections.IEnumerable;
                    return list != null && list.GetEnumerator().MoveNext();
                } catch { return false; }
            }).ToList();

            dLog.AppendLine($"Found {rooms.Count} valid structural rooms (filtered from {MRUK.Instance.Rooms.Count}).");
            if (uiLog != null) uiLog.AddLog($"Nalezeno {rooms.Count} místností.");
            if (rooms.Count == 0) return false;

            // Global Alignment
            float globalAngle = 0; float maxW = 0;
            foreach (var r in rooms) {
                foreach (var a in r.Anchors.Where(x => x.Label.ToString().ToUpper().Contains("WALL") && x.PlaneRect.HasValue)) {
                    if (a.PlaneRect.Value.width > maxW) { 
                        maxW = a.PlaneRect.Value.width; 
                        globalAngle = -a.transform.eulerAngles.y; 
                    }
                }
            }
            if (uiLog != null) uiLog.AddLog($"Zarovnání: {globalAngle:F1}°");

            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string session = Path.Combine(root, "Export_" + ts);
            Directory.CreateDirectory(session);

            // Metadata
            if (uiLog != null) uiLog.AddLog("Zapisuji metadata a dump...");
            File.WriteAllText(Path.Combine(session, "full_scene_dump.txt"), MRUKDataProcessor.GenerateSceneDump(rooms));
            File.WriteAllText(Path.Combine(session, "house_data.json"), MRUKDataProcessor.GenerateJson(rooms));
            
            // Reports
            if (uiLog != null) uiLog.AddLog("Generuji HTML report...");
            File.WriteAllText(Path.Combine(session, "house_report_v2.html"), MRUKReportBuilderV2.GenerateFullReport(rooms, true, true, globalAngle));
            
            // Models (Analytical, Reconstructed, and RAW SCAN)
            if (uiLog != null) uiLog.AddLog("Exportuji 3D modely...");
            
            // 1. Analytical (Boxes)
            string analyticalObj = MRUKModelExporterV2.GenerateOBJ(rooms, false, globalAngle);
            File.WriteAllText(Path.Combine(session, "house_analytical.obj"), analyticalObj);
            byte[] analyticalGlb = MRUKModelExporterV2.GenerateGLB(rooms, false, globalAngle);
            if (analyticalGlb != null) File.WriteAllBytes(Path.Combine(session, "house_analytical.glb"), analyticalGlb);

            // 2. Reconstructed (Polygonal)
            string meshObj = MRUKModelExporterV2.GenerateOBJ(rooms, true, globalAngle, false);
            File.WriteAllText(Path.Combine(session, "house_mesh.obj"), meshObj);
            byte[] meshGlb = MRUKModelExporterV2.GenerateGLB(rooms, true, globalAngle);
            if (meshGlb != null) File.WriteAllBytes(Path.Combine(session, "house_mesh.glb"), meshGlb);

            // 3. RAW SCAN (The actual dirty mesh from Quest)
            if (uiLog != null) uiLog.AddLog("Hledám surový sken...");
            string rawObj = MRUKModelExporterV2.GenerateRawScanOBJ(globalAngle);
            if (!string.IsNullOrEmpty(rawObj) && rawObj.Length > 500) {
                File.WriteAllText(Path.Combine(session, "house_mesh_raw.obj"), rawObj);
                if (uiLog != null) uiLog.AddLog($"Raw Mesh OK ({rawObj.Length / 1024} KB)");
                byte[] rawGlb = MRUKModelExporterV2.GenerateRawScanGLB(globalAngle);
                if (rawGlb != null) File.WriteAllBytes(Path.Combine(session, "house_mesh_raw.glb"), rawGlb);
            } else {
                if (uiLog != null) uiLog.AddLog("<color=yellow>Raw sken nenalezen v paměti.</color>");
            }
            
            File.WriteAllText(Path.Combine(session, "house_model.mtl"), MRUKModelExporterV2.GenerateMTL());
            
            LastReportPath = Path.Combine(session, "house_report_v2.html");

            foreach(var r in rooms) {
                string rName = MRUKDataProcessor.GetRoomLabel(r);
                dLog.AppendLine($"Processing: {r.name} -> {rName}");
                string rP = Path.Combine(session, MRUKDataProcessor.GetSafeName(rName) + "_-_" + r.name.Substring(Math.Max(0, r.name.Length-4)));
                Directory.CreateDirectory(rP);
                var s = new List<MRUKRoom>{r};
                File.WriteAllText(Path.Combine(rP, "report_v2.html"), MRUKReportBuilderV2.GenerateFullReport(s, true, false, globalAngle));
                File.WriteAllText(Path.Combine(rP, "analytical.obj"), MRUKModelExporterV2.GenerateOBJ(s, false, globalAngle));
                File.WriteAllText(Path.Combine(rP, "mesh.obj"), MRUKModelExporterV2.GenerateOBJ(s, true, globalAngle, false));
                
                byte[] rGlb = MRUKModelExporterV2.GenerateGLB(s, true, globalAngle);
                if (rGlb != null) File.WriteAllBytes(Path.Combine(rP, "mesh.glb"), rGlb);
            }

            dLog.AppendLine("Export successful.");
            File.WriteAllText(Path.Combine(session, "debug_log.txt"), dLog.ToString());

            if (uiLog != null) uiLog.AddLog("<color=green>Export hotov!</color>");
            return true;
        } catch (Exception ex) {
            dLog.AppendLine("CRASH: " + ex.Message + "\n" + ex.StackTrace);
            if (uiLog != null) uiLog.AddLog("<color=red>CHYBA:</color> " + ex.Message);
            return false;
        }
    #else
        await System.Threading.Tasks.Task.Yield();
        return false;
    #endif
    }
}
