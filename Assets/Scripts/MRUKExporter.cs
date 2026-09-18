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
            if (MRUK.Instance == null) {
                uiLog?.AddLog("<color=red>ERROR: MRUK Instance not found in scene!</color>");
                return false;
            }

            if (!Application.isEditor) {
                uiLog?.AddLog("Checking Scene permissions...");
                if (!OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.Scene)) {
                    uiLog?.AddLog("Requesting Scene permission - please respond to the system dialog...");
                    bool granted = false;
                    void OnGranted(string id) { if (id == OVRPermissionsRequester.ScenePermission) granted = true; }
                    OVRPermissionsRequester.PermissionGranted += OnGranted;
                    OVRPermissionsRequester.Request(new[] { OVRPermissionsRequester.Permission.Scene });

                    int waitTicks = 0;
                    while (!granted && !OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.Scene) && waitTicks < 300) {
                        await Task.Delay(100); waitTicks++;
                    }
                    OVRPermissionsRequester.PermissionGranted -= OnGranted;

                    if (!OVRPermissionsRequester.IsPermissionGranted(OVRPermissionsRequester.Permission.Scene)) {
                        uiLog?.AddLog("<color=red>ERROR: Scene permission was not granted.</color>");
                        return false;
                    }
                    uiLog?.AddLog("Scene permission granted.");
                } else {
                    uiLog?.AddLog("Scene permission already granted.");
                }

                uiLog?.AddLog("Calling LoadSceneFromDevice...");
                var loadTask = MRUK.Instance.LoadSceneFromDevice(true, true, MRUK.SceneModel.V2);
                
                // Monitor the load task with a strict timeout
                int loadTimeout = 0;
                while (!loadTask.IsCompleted && loadTimeout < 80) { // 8 seconds timeout
                    await Task.Delay(100);
                    loadTimeout++;
                    if (loadTimeout % 20 == 0) uiLog?.AddLog("Still waiting for device sync...");
                }
                
                if (!loadTask.IsCompleted) {
                    uiLog?.AddLog("<color=red>FATAL ERROR: LoadSceneFromDevice timed out!</color>");
                    uiLog?.AddLog("Check if 'Room Setup' is done in Quest settings.");
                    return false;
                }
                
                await loadTask;
                uiLog?.AddLog("LoadSceneFromDevice finished.");

                int initTimeout = 0;
                while (!MRUK.Instance.IsInitialized && initTimeout < 50) {
                    await Task.Delay(100); initTimeout++;
                }
                uiLog?.AddLog(MRUK.Instance.IsInitialized ? "MRUK Initialized." : "<color=orange>MRUK Init timeout - continuing anyway.</color>");
                } else {
                uiLog?.AddLog("Running in Editor - skipping device sync.");
                }
            
                SetProgress(15, uiLog);
                uiLog?.AddLog("<color=cyan>[2/6] Processing rooms...</color>");
                await Task.Delay(200);
            
            var rooms = MRUKDataProcessor.GetValidRooms(MRUK.Instance);

            uiLog?.AddLog($"Found {rooms.Count} valid rooms.");
            
            if (rooms.Count == 0) {
                uiLog?.AddLog("<color=red>No valid rooms found! Make sure you have completed 'Room Setup' in Quest settings.</color>");
                return false;
            }

            uiLog?.AddLog("<color=cyan>[3/6] Calculating house layout...</color>");
            LastHouseCenter = CalculateHouseCenter(rooms);
            float angle = CalculateGlobalAngle(rooms);
            string session = MRUKPathUtility.CreateSessionFolder(root);
            uiLog?.AddLog($"Session created: {Path.GetFileName(session)}");
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
    private float CalculateGlobalAngle(List<MRUKRoom> rs) { var f = rs.SelectMany(r => r.Anchors).FirstOrDefault(a => a.Label == MRUKAnchor.SceneLabels.FLOOR); if (f == null) return 0f;
    // Use the floor's transform to find the 'house North'
    // Most reliable for Quest: The floor anchor's Forward is the room's North
    Vector3 forward = f.transform.forward;
    forward.y = 0;
    float angle = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
    return angle; }
}