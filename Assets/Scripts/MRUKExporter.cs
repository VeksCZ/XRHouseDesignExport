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
        
        if (uiLog != null) uiLog.AddLog("Příprava scény a oprávnění...");
        
        try {
            // 1. Permissions (Using available Request method)
            OVRPermissionsRequester.Request(new[] { OVRPermissionsRequester.Permission.Scene });
            
            if (MRUK.Instance == null) return false;

            // 2. Modern Scene Loading (Handling v83-v85 patterns)
            if (!Application.isEditor) {
                if (uiLog != null) uiLog.AddLog("Načítám scénu z Questu...");
                
                // We use the version-safe approach: call the method and wait for event
                var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
                Action onLoaded = null;
                onLoaded = () => {
                    MRUK.Instance.SceneLoadedEvent.RemoveListener(new UnityEngine.Events.UnityAction(onLoaded));
                    tcs.TrySetResult(true);
                };
                MRUK.Instance.SceneLoadedEvent.AddListener(new UnityEngine.Events.UnityAction(onLoaded));
                
                // Start loading (V2 provides High-Fidelity data)
                await MRUK.Instance.LoadSceneFromDevice(true, true, MRUK.SceneModel.V2);
                
                // Wait for either the event or a timeout
                await System.Threading.Tasks.Task.WhenAny(tcs.Task, System.Threading.Tasks.Task.Delay(5000));
            }

            var rooms = MRUK.Instance.Rooms.ToList();
            if (uiLog != null) uiLog.AddLog($"Nalezeno {rooms.Count} místností.");
            if (rooms.Count == 0) return false;

            // Global Alignment (Orthogonal snapping)
            float globalAngle = 0; float maxW = 0;
            foreach (var r in rooms) {
                foreach (var a in r.Anchors.Where(x => x.Label.ToString().ToUpper().Contains("WALL") && x.PlaneRect.HasValue)) {
                    if (a.PlaneRect.Value.width > maxW) { 
                        maxW = a.PlaneRect.Value.width; 
                        globalAngle = -Mathf.Round(a.transform.eulerAngles.y / 90f) * 90f; 
                    }
                }
            }

            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string session = Path.Combine(root, "Export_" + ts);
            Directory.CreateDirectory(session);

            File.WriteAllText(Path.Combine(session, "full_scene_dump.txt"), MRUKDataProcessor.GenerateSceneDump(rooms));
            File.WriteAllText(Path.Combine(session, "house_data.json"), MRUKDataProcessor.GenerateJson(rooms));
            File.WriteAllText(Path.Combine(session, "house_report_v2.html"), MRUKReportBuilderV2.GenerateFullReport(rooms, true, true, globalAngle));
            
            // 3. Models (Standard and High-Fidelity)
            if (uiLog != null) uiLog.AddLog("Exportuji 3D modely...");
            File.WriteAllText(Path.Combine(session, "house_mesh.obj"), MRUKModelExporterV2.GenerateOBJ(rooms, true, globalAngle));
            File.WriteAllText(Path.Combine(session, "house_analytical.obj"), MRUKModelExporterV2.GenerateOBJ(rooms, false, globalAngle));
            
            // 4. RAW HIGH-FIDELITY SCAN (OVRTriangleMesh)
            if (uiLog != null) uiLog.AddLog("Skenuji High-Fidelity mesh...");
            string rawObj = await MRUKModelExporterV2.GenerateRawHighFidelityMesh(globalAngle);
            if (!string.IsNullOrEmpty(rawObj) && rawObj.Length > 1000) {
                File.WriteAllText(Path.Combine(session, "house_mesh_raw.obj"), rawObj);
                if (uiLog != null) uiLog.AddLog($"<color=green>Raw Mesh OK ({rawObj.Length / 1024} KB)</color>");
            } else {
                if (uiLog != null) uiLog.AddLog("<color=yellow>Raw mesh nebyl nalezen.</color>");
            }

            File.WriteAllText(Path.Combine(session, "house_model.mtl"), MRUKModelExporterV2.GenerateMTL());
            LastReportPath = Path.Combine(session, "house_report_v2.html");

            foreach(var r in rooms) {
                string rName = MRUKDataProcessor.GetRoomLabel(r);
                string rP = Path.Combine(session, MRUKDataProcessor.GetSafeName(rName) + "_-_" + r.name.Substring(Math.Max(0, r.name.Length-4)));
                Directory.CreateDirectory(rP);
                var s = new List<MRUKRoom>{r};
                File.WriteAllText(Path.Combine(rP, "mesh.obj"), MRUKModelExporterV2.GenerateOBJ(s, true, globalAngle));
            }

            dLog.AppendLine("Export successful.");
            File.WriteAllText(Path.Combine(session, "debug_log.txt"), dLog.ToString());
            if (uiLog != null) uiLog.AddLog("<color=green>Export hotov!</color>");
            return true;
        } catch (Exception ex) {
            if (uiLog != null) uiLog.AddLog("<color=red>CHYBA:</color> " + ex.Message);
            return false;
        }
    #else
        await System.Threading.Tasks.Task.Yield();
        return false;
    #endif
    }
}
