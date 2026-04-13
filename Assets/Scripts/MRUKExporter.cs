using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
using Meta.XR;
#endif

public class MRUKExporter : MonoBehaviour
{
    public XRMenu uiLog;
    private StringBuilder dLog = new StringBuilder();
    public static string LastReportPath = "";

    public async void OnExportButton()
    {
        await ExportAllRooms(uiLog);
    }

    public async Task<bool> ExportAllRooms(XRMenu uiLog = null)
    {
#if META_XR_SDK_INSTALLED
        dLog.Clear();
        dLog.AppendLine($"=== EXPORT START {DateTime.Now} ===");

        string root = Application.isEditor 
            ? "Exports/RoomData" 
            : "/sdcard/Download/XRHouseExports";

        if (uiLog != null) uiLog.AddLog("Žádám o oprávnění a načítám scénu...");

        try
        {
            // Request permission (SDK 85 style usually has Request, IsPermissionGranted)
            OVRPermissionsRequester.Request(new[] { OVRPermissionsRequester.Permission.Scene });
            bool hasPermission = OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.Scene);

            if (!hasPermission)
            {
                uiLog?.AddLog("<color=red>Chybí Scene permission!</color>");
                return false;
            }

            if (MRUK.Instance == null)
            {
                uiLog?.AddLog("<color=red>MRUK.Instance je null</color>");
                return false;
            }

            // Načtení scény
            if (!Application.isEditor)
            {
                uiLog?.AddLog("Načítám scénu z headsetu...");
                // Stick to the synchronous version as LoadSceneFromDeviceAsync seems missing in this SDK build
                await MRUK.Instance.LoadSceneFromDevice(true, true, MRUK.SceneModel.V2);
                }

            // Počkáme na načtení
            await Task.Delay(1500);

            var rooms = MRUK.Instance.Rooms
                .Where(r => {
                    var floor = r.Anchors.FirstOrDefault(a => a.Label == MRUKAnchor.SceneLabels.FLOOR && a.PlaneRect.HasValue);
                    if (floor == null) return false;
                    float area = floor.PlaneRect.Value.width * floor.PlaneRect.Value.height;
                    return area > 2.0f; // Ignorujeme místnosti menší než 2 m2 (skříně atd.)
                })
                .ToList();

            if (uiLog != null) uiLog.AddLog($"Nalezeno {rooms.Count} relevantních místností.");

            if (rooms.Count == 0)
            {
                uiLog?.AddLog("<color=red>Žádné místnosti nenalezeny. Spusť Space Setup v Questu.</color>");
                return false;
            }

            // === NOVÉ A LEPŠÍ VÝPOČET ROTACE ===
            float globalAngle = 0f;
            MRUKAnchor referenceFloor = null;

            foreach (var room in rooms)
            {
                var floorAnchor = room.Anchors.FirstOrDefault(a => a.Label == MRUKAnchor.SceneLabels.FLOOR);
                if (floorAnchor != null)
                {
                    referenceFloor = floorAnchor;
                    break;
                }
            }

            if (referenceFloor != null)
            {
                // Použijeme rotaci podlahy jako hlavní referenci (invertujeme ji pro korekci)
                globalAngle = -referenceFloor.transform.eulerAngles.y;
                uiLog?.AddLog($"Export rotace podle podlahy: {referenceFloor.transform.eulerAngles.y:F1}°");
            }
            else
            {
                // Fallback na nejdelší zeď (zaokrouhleně), pokud není podlaha
                float maxWidth = 0f;
                foreach (var r in rooms)
                {
                    foreach (var a in r.Anchors.Where(x => x.Label.ToString().ToUpper().Contains("WALL") && x.PlaneRect.HasValue))
                    {
                        if (a.PlaneRect.Value.width > maxWidth)
                        {
                            maxWidth = a.PlaneRect.Value.width;
                            globalAngle = -Mathf.Round(a.transform.eulerAngles.y / 90f) * 90f;
                        }
                    }
                }
            }

            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string session = Path.Combine(root, "Export_" + ts);
            Directory.CreateDirectory(session);

            // Dumpy a reporty
            try {
                File.WriteAllText(Path.Combine(session, "full_scene_dump.txt"), MRUKDataProcessor.GenerateSceneDump(rooms));
            } catch (Exception ex) { uiLog?.AddLog("<color=yellow>Dump failed: " + ex.Message + "</color>"); }

            try {
                File.WriteAllText(Path.Combine(session, "house_data.json"), MRUKDataProcessor.GenerateJson(rooms));
            } catch (Exception ex) { uiLog?.AddLog("<color=yellow>JSON failed: " + ex.Message + "</color>"); }

            try {
                File.WriteAllText(Path.Combine(session, "house_report_v2.html"), MRUKReportBuilder.GenerateFullReport(rooms, true, true, globalAngle));
            } catch (Exception ex) { uiLog?.AddLog("<color=yellow>HTML Report failed: " + ex.Message + "</color>"); }

            if (uiLog != null) uiLog.AddLog("Exportuji modely...");

            // Analytical model (Barevný z raw meshe)
            try {
                string analyticalObj = await MRUKModelExporterV2.GenerateCleanColoredAnalytical(globalAngle);
                if (!string.IsNullOrEmpty(analyticalObj)) {
                    File.WriteAllText(Path.Combine(session, "house_analytical.obj"), analyticalObj);
                    
                    byte[] aGlb = MRUKModelExporterV2.GenerateGLB(rooms, false, globalAngle); 
                    if (aGlb != null) File.WriteAllBytes(Path.Combine(session, "house_analytical.glb"), aGlb);
                }
            } catch (Exception ex) {
                uiLog?.AddLog("<color=yellow>Analytical model failed: " + ex.Message + "</color>");
            }

            // Mesh model (Polygonal Reconstruction)
            try {
                string meshObj = MRUKModelExporterV2.GenerateOBJ(rooms, true, globalAngle);
                if (!string.IsNullOrEmpty(meshObj)) {
                    File.WriteAllText(Path.Combine(session, "house_mesh.obj"), meshObj);
                    byte[] mGlb = MRUKModelExporterV2.GenerateGLB(rooms, true, globalAngle);
                    if (mGlb != null) File.WriteAllBytes(Path.Combine(session, "house_mesh.glb"), mGlb);
                }
            } catch (Exception ex) {
                uiLog?.AddLog("<color=yellow>Mesh model failed: " + ex.Message + "</color>");
            }

            // RAW High-Fidelity Mesh
            try {
                if (uiLog != null) uiLog.AddLog("Exportuji surový High-Fidelity mesh...");
                string rawObj = await MRUKModelExporterV2.GenerateRawHighFidelityMesh(globalAngle);
                
                if (!string.IsNullOrEmpty(rawObj) && rawObj.Length > 1000) {
                    File.WriteAllText(Path.Combine(session, "house_mesh_raw.obj"), rawObj);
                    if (uiLog != null) uiLog.AddLog($"<color=green>Raw mesh exportován ({rawObj.Length / 1024} KB)</color>");
                } else {
                    if (uiLog != null) uiLog.AddLog("<color=yellow>Raw mesh nenalezen nebo je příliš malý.</color>");
                }
            } catch (Exception ex) {
                uiLog?.AddLog("<color=yellow>Raw mesh failed: " + ex.Message + "</color>");
            }

            try {
                File.WriteAllText(Path.Combine(session, "house_model.mtl"), MRUKModelExporterV2.GenerateMTL());
            } catch (Exception ex) { uiLog?.AddLog("<color=yellow>MTL failed: " + ex.Message + "</color>"); }

            // Per-room export
            foreach (var r in rooms)
            {
                if (r == null) continue;
                try {
                    string rName = MRUKDataProcessor.GetRoomLabel(r);
                    string safeName = MRUKDataProcessor.GetSafeName(rName);
                    string roomPath = Path.Combine(session, $"{safeName}_-_{r.name}");
                    Directory.CreateDirectory(roomPath);

                    var singleRoom = new List<MRUKRoom> { r };
                    string roomObj = MRUKModelExporterV2.GenerateOBJ(singleRoom, true, globalAngle);
                    if (!string.IsNullOrEmpty(roomObj)) {
                        File.WriteAllText(Path.Combine(roomPath, "mesh.obj"), roomObj);
                    }
                } catch (Exception ex) {
                    Debug.LogWarning($"Room export failed for {r.name}: {ex.Message}");
                }
            }

            LastReportPath = Path.Combine(session, "house_report_v2.html");
            dLog.AppendLine("Export successful.");
            File.WriteAllText(Path.Combine(session, "debug_log.txt"), dLog.ToString());

            if (uiLog != null) uiLog.AddLog("<color=green>Export úspěšně dokončen!</color>");
            return true;
        }
        catch (Exception ex)
        {
            string errorMsg = $"CHYBA: {ex.Message}";
            if (uiLog != null) uiLog.AddLog("<color=red>" + errorMsg + "</color>");
            dLog.AppendLine("ERROR: " + ex);
            Debug.LogError(ex);
            return false;
        }
#else
        await Task.Yield();
        return false;
#endif
    }
}
