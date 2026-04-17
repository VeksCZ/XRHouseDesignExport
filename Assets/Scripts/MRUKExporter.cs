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
    public Vector3 LastHouseCenter { get; private set; } = Vector3.zero;

    public async void OnExportButton() { await ExportAllRooms(uiLog); }

    public async Task<bool> ExportAllRooms(XRMenu uiLog = null)
    {
    #if META_XR_SDK_INSTALLED
        dLog.Clear(); dLog.AppendLine($"=== EXPORT START {DateTime.Now} ===");
        string root = MRUKPathUtility.GetExportRoot();
        if (uiLog != null) uiLog.AddLog("<color=cyan>[1/6] Permissions and Scene Loading...</color>");
        try {
            OVRPermissionsRequester.Request(new[] { OVRPermissionsRequester.Permission.Scene });
            if (!OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.Scene)) { uiLog?.AddLog("<color=red>No Scene permission!</color>"); return false; }
            if (MRUK.Instance == null) { uiLog?.AddLog("<color=red>MRUK is null</color>"); return false; }

            if (!Application.isEditor) {
                uiLog?.AddLog("<color=cyan>[2/6] Syncing with headset...</color>");
                await MRUK.Instance.LoadSceneFromDevice(true, true, MRUK.SceneModel.V2);
            }
            await Task.Delay(1500);
            var rooms = MRUK.Instance.Rooms.Where(r => {
                var floor = r.Anchors.FirstOrDefault(a => a.Label == MRUKAnchor.SceneLabels.FLOOR && a.PlaneRect.HasValue);
                return floor != null && (floor.PlaneRect.Value.width * floor.PlaneRect.Value.height) > 1.0f;
            }).ToList();
            if (rooms.Count == 0) { uiLog?.AddLog("<color=red>No valid rooms.</color>"); return false; }

            if (uiLog != null) uiLog.AddLog("<color=cyan>[3/6] Aligning house data...</color>");
            LastHouseCenter = CalculateHouseCenter(rooms);
            float angle = CalculateGlobalAngle(rooms);
            uiLog?.AddLog($"Aligned: {LastHouseCenter.ToString("F2")} @ {angle:F1}°");

            string session = MRUKPathUtility.CreateSessionFolder(root);
            if (uiLog != null) uiLog.AddLog("<color=cyan>[4/6] Data generation...</color>");
            File.WriteAllText(Path.Combine(session, MRUKPathUtility.DATA_DUMP), MRUKDataProcessor.GenerateSceneDump(rooms));
            File.WriteAllText(Path.Combine(session, MRUKPathUtility.DATA_JSON), MRUKDataProcessor.GenerateJson(rooms));
            File.WriteAllText(Path.Combine(session, MRUKPathUtility.DATA_REPORT), MRUKReportBuilder.GenerateFullReport(rooms, true, true, angle));

            if (uiLog != null) uiLog.AddLog("<color=cyan>[5/6] 3D Model Export (4 types)...</color>");
            
            // 1. Anchor Analytical (Clean walls)
            string anchorObj = MRUKModelExporter.GenerateAnchorAnalytical(rooms, angle, LastHouseCenter);
            if (!string.IsNullOrEmpty(anchorObj)) {
                File.WriteAllText(Path.Combine(session, MRUKPathUtility.MODEL_CLEAN_OBJ), anchorObj);
                byte[] glb = GLBExporter.ExportToGLB(anchorObj);
                if (glb != null) File.WriteAllBytes(Path.Combine(session, MRUKPathUtility.MODEL_CLEAN_GLB), glb);
            }

            // 2. Mesh Analytical (Furniture + Clean Mesh)
            string meshAnalyticalObj = await MRUKModelExporter.GenerateCleanColoredAnalytical(angle, LastHouseCenter);
            if (!string.IsNullOrEmpty(meshAnalyticalObj)) {
                File.WriteAllText(Path.Combine(session, MRUKPathUtility.MODEL_MESH_ANALYTICAL_OBJ), meshAnalyticalObj);
                byte[] glb = GLBExporter.ExportToGLB(meshAnalyticalObj);
                if (glb != null) File.WriteAllBytes(Path.Combine(session, MRUKPathUtility.MODEL_MESH_ANALYTICAL_GLB), glb);
            }

            // 3. Polygonal Reconstruction
            string reconstructionObj = MRUKModelExporter.GenerateOBJ(rooms, true, angle, LastHouseCenter);
            if (!string.IsNullOrEmpty(reconstructionObj)) {
                File.WriteAllText(Path.Combine(session, MRUKPathUtility.MODEL_MESH_OBJ), reconstructionObj);
                byte[] glb = GLBExporter.ExportToGLB(reconstructionObj);
                if (glb != null) File.WriteAllBytes(Path.Combine(session, MRUKPathUtility.MODEL_MESH_GLB), glb);
            }

            // 4. Raw Scan
            string rawObj = await MRUKModelExporter.GenerateRawHighFidelityMesh(angle, LastHouseCenter);
            if (!string.IsNullOrEmpty(rawObj)) File.WriteAllText(Path.Combine(session, MRUKPathUtility.MODEL_RAW_OBJ), rawObj);
            
            File.WriteAllText(Path.Combine(session, MRUKPathUtility.MODEL_MTL), MRUKModelExporter.GenerateMTL());

            if (uiLog != null) uiLog.AddLog("<color=cyan>[6/6] Room breakdown...</color>");
            foreach (var r in rooms) {
                var obj = MRUKModelExporter.GenerateOBJ(new List<MRUKRoom>{r}, true, angle, LastHouseCenter);
                if (!string.IsNullOrEmpty(obj)) {
                    string rPath = Path.Combine(session, $"{MRUKDataProcessor.GetSafeName(MRUKDataProcessor.GetRoomLabel(r))}_-_{r.name}");
                    Directory.CreateDirectory(rPath);
                    File.WriteAllText(Path.Combine(rPath, "mesh.obj"), obj);
                }
            }
            LastReportPath = Path.Combine(session, MRUKPathUtility.DATA_REPORT);
            uiLog?.AddLog("<color=green><b>EXPORT FINISHED!</b></color>");
            return true;
        } catch (Exception ex) { uiLog?.AddLog("<color=red>ERROR: " + ex.Message + "</color>"); return false; }
    #else
        return false;
    #endif
    }

    private Vector3 CalculateHouseCenter(List<MRUKRoom> rooms) {
        if (rooms.Count == 0) return Vector3.zero;
        Vector3 c = Vector3.zero; int n = 0;
        foreach (var r in rooms) {
            var f = r.Anchors.FirstOrDefault(a => a.Label == MRUKAnchor.SceneLabels.FLOOR);
            if (f != null) { c += f.transform.position; n++; }
        }
        return n > 0 ? c / n : rooms[0].transform.position;
    }

    private float CalculateGlobalAngle(List<MRUKRoom> rooms) {
        float angle = 0f;
    #if META_XR_SDK_INSTALLED
        var f = rooms.SelectMany(r => r.Anchors).FirstOrDefault(a => a.Label == MRUKAnchor.SceneLabels.FLOOR);
        if (f != null) angle = -f.transform.eulerAngles.y;
        else {
            float maxW = 0f;
            foreach (var r in rooms) foreach (var a in r.Anchors.Where(x => x.Label.ToString().Contains("WALL") && x.PlaneRect.HasValue))
                if (a.PlaneRect.Value.width > maxW) { maxW = a.PlaneRect.Value.width; angle = -Mathf.Round(a.transform.eulerAngles.y / 90f) * 90f; }
        }
    #endif
        return angle;
    }
}