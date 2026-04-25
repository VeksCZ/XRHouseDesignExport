using System;
using System.Collections.Generic;
using System.IO;
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
    public static string LastReportPath = "";
    public Vector3 LastHouseCenter { get; private set; } = Vector3.zero;

    public async void OnExportButton() { await ExportAllRooms(uiLog); }

    public async Task<bool> ExportAllRooms(XRMenu uiLog = null)
    {
    #if META_XR_SDK_INSTALLED
        SetProgress(0, uiLog);
        string root = MRUKPathUtility.GetExportRoot();
        
        // Android 13+ storage protection fallback
        try { if (!Directory.Exists(root)) Directory.CreateDirectory(root); }
        catch { 
            root = Application.persistentDataPath; 
            uiLog?.AddLog("<color=orange>Using persistentDataPath fallback</color>");
        }

        uiLog?.AddLog("<color=cyan>[1/6] Scene Sync...</color>");
        try {
            if (!Application.isEditor) {
                OVRPermissionsRequester.Request(new[] { OVRPermissionsRequester.Permission.Scene });
                await Task.Delay(500); 
                await MRUK.Instance.LoadSceneFromDevice(true, true, MRUK.SceneModel.V2);
                
                int timeout = 0;
                while (!MRUK.Instance.IsInitialized && timeout < 50) {
                    await Task.Delay(100); timeout++;
                }
            }
            SetProgress(15, uiLog);
            await Task.Delay(500);
            
            // 2026 Best Practice: Filter rooms by floor area (> 2.0m2) and exclude storage/wardrobes
            var rooms = MRUK.Instance.Rooms.Where(r => {
                var floor = r.Anchors.FirstOrDefault(a => a.Label == MRUKAnchor.SceneLabels.FLOOR);
                if (floor == null || !floor.PlaneRect.HasValue) return false;
                float area = floor.PlaneRect.Value.width * floor.PlaneRect.Value.height;
                if (area < 2.0f) return false;
                
                // Exclude rooms that are likely just storage/closets based on anchors
                if (r.Anchors.Any(a => {
                    string l = a.Label.ToString().ToUpper();
                    return l.Contains("STORAGE") || l.Contains("WARDROBE");
                }) && area < 3.0f) return false;

                return true;
            }).ToList();

            if (rooms.Count == 0) {
                uiLog?.AddLog("<color=red>No valid rooms found (Area > 2.0m2)</color>");
                return false;
            }

            LastHouseCenter = CalculateHouseCenter(rooms);
            float angle = CalculateGlobalAngle(rooms);
            string session = MRUKPathUtility.CreateSessionFolder(root);
            SetProgress(30, uiLog);

            uiLog?.AddLog("<color=cyan>[4/6] Data generation...</color>");
            File.WriteAllText(Path.Combine(session, MRUKPathUtility.DATA_JSON), MRUKDataProcessor.GenerateJson(rooms));
            File.WriteAllText(Path.Combine(session, MRUKPathUtility.DATA_DUMP), MRUKDataProcessor.GenerateSceneDump(rooms));
            File.WriteAllText(Path.Combine(session, MRUKPathUtility.DATA_REPORT), MRUKReportBuilder.GenerateFullReport(rooms, true, true, angle));
            SetProgress(45, uiLog);

            uiLog?.AddLog($"<color=cyan>[5/6] Exporting models...</color>");
            uiLog?.AddLog($"Using MTL: {MRUKPathUtility.MODEL_MTL}");
            Save(XRModelFactory.CreateAnchorAnalytical(rooms, angle, LastHouseCenter), session, MRUKPathUtility.MODEL_CLEAN_OBJ, MRUKPathUtility.MODEL_CLEAN_GLB); SetProgress(60, uiLog);
            Save(await XRModelFactory.CreateMeshAnalytical(rooms, angle, LastHouseCenter), session, MRUKPathUtility.MODEL_MESH_ANALYTICAL_OBJ, MRUKPathUtility.MODEL_MESH_ANALYTICAL_GLB); SetProgress(75, uiLog);
            Save(XRModelFactory.CreateReconstruction(rooms, angle, LastHouseCenter), session, MRUKPathUtility.MODEL_MESH_OBJ, MRUKPathUtility.MODEL_MESH_GLB); SetProgress(85, uiLog);
            Save(await XRModelFactory.CreateRawScan(rooms, angle, LastHouseCenter), session, MRUKPathUtility.MODEL_RAW_OBJ, null); SetProgress(95, uiLog);
            
            File.WriteAllText(Path.Combine(session, MRUKPathUtility.MODEL_MTL), OBJWriter.GenerateMTL());

            // --- Room breakdown ---
            uiLog?.AddLog("<color=cyan>[6/6] Room breakdown...</color>");
            foreach (var r in rooms) {
                var single = new List<MRUKRoom> { r };
                var model = XRModelFactory.CreateReconstruction(single, angle, LastHouseCenter);
                string roomLabel = MRUKDataProcessor.GetRoomLabel(r);
                string roomGuid = r.Anchor.Uuid.ToString().Substring(0, 8);
                string rDirName = $"{MRUKDataProcessor.GetSafeName(roomLabel)}_{roomGuid}";
                string rPath = Path.Combine(session, rDirName);
                Directory.CreateDirectory(rPath);
                // FIXED: Relative MTL path for subfolders
                File.WriteAllText(Path.Combine(rPath, "mesh.obj"), OBJWriter.WriteToString(model, "../" + MRUKPathUtility.MODEL_MTL));
            }

            uiLog?.AddLog("<color=green><b>EXPORT FINISHED!</b></color>");
            SetProgress(100, uiLog);
            LastReportPath = Path.Combine(session, MRUKPathUtility.DATA_REPORT);
            #if UNITY_EDITOR
            UnityEditor.EditorUtility.RevealInFinder(session);
            #endif
            return true;
            } catch (Exception ex) { 
            Debug.LogException(ex); 
            uiLog?.AddLog("<color=red>ERROR: " + ex.Message + "</color>"); 
            return false; 
            }
    #else
        return false;
    #endif
    }

    private void SetProgress(float v, XRMenu m) { if (m != null && m.progressBar != null) m.progressBar.value = v; }
    private void Save(XRHouseModel m, string f, string obj, string glb) {
        if (obj != null) File.WriteAllText(Path.Combine(f, obj), OBJWriter.WriteToString(m));
        if (glb != null) { byte[] b = GLBExporter.ExportToGLB(m); if (b != null) File.WriteAllBytes(Path.Combine(f, glb), b); }
    }
    private Vector3 CalculateHouseCenter(List<MRUKRoom> rs) { Vector3 c = Vector3.zero; int n = 0; foreach (var r in rs) { var f = r.Anchors.FirstOrDefault(a => a.Label == MRUKAnchor.SceneLabels.FLOOR); if (f != null) { c += f.transform.position; n++; } } return n > 0 ? c / n : rs[0].transform.position; }
    private float CalculateGlobalAngle(List<MRUKRoom> rs) { var f = rs.SelectMany(r => r.Anchors).FirstOrDefault(a => a.Label == MRUKAnchor.SceneLabels.FLOOR); return f != null ? -f.transform.eulerAngles.y : 0f; }
}