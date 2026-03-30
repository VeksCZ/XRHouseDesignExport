using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using System.Text;
using System.Linq;
using System.Collections.Generic;

public class XRMenu : MonoBehaviour
{
    public Button exportAllButton;
    public Button viewHouseButton;
    public Button openReportButton; 
    public MRUKExporter exporter;
    public Text statusText;
    public Text logText;
    public ScrollRect logScrollView;
    private StringBuilder logHistory = new StringBuilder();

    private Material hologramMaterial; 
    private GameObject houseDollhouse;
    private XRRayInteractor[] interactors;

    void Start()
    {
        AddLog("System initialized.");
        if (exportAllButton != null) exportAllButton.onClick.AddListener(OnExportAll);
        if (viewHouseButton != null) viewHouseButton.onClick.AddListener(OnToggleHouseView);
        if (openReportButton != null) openReportButton.onClick.AddListener(OpenLastReport);
        
        if (exporter == null) exporter = Object.FindFirstObjectByType<MRUKExporter>();
        interactors = Object.FindObjectsByType<XRRayInteractor>(FindObjectsSortMode.None);
        
        hologramMaterial = Resources.Load<Material>("HologramMat");
        if (statusText != null) statusText.text = "System Ready";
    }

    public void AddLog(string msg) {
        string formattedMsg = $"[{System.DateTime.Now:HH:mm:ss}] {msg}";
        if (logText != null) logText.text += formattedMsg + "\n";
        logHistory.AppendLine(formattedMsg);
        if (logScrollView != null) { Canvas.ForceUpdateCanvases(); logScrollView.verticalNormalizedPosition = 0f; }
        Debug.Log("VR_LOG: " + msg);
    }

    public string GetLogHistory() => logHistory.ToString();

    void Update()
    {
        if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch)) OnExportAll();
        if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch)) OnToggleHouseView();
        if (OVRInput.GetDown(OVRInput.Button.Three, OVRInput.Controller.LTouch)) OpenLastReport();
    }

    public async void OnToggleHouseView()
    {
        AddLog("ToggleHouseView clicked...");
        if (houseDollhouse != null) {
            Destroy(houseDollhouse); 
            houseDollhouse = null;
            if (statusText != null) statusText.text = "House View: OFF";
            AddLog("House view closed.");
            return;
        }

        #if META_XR_SDK_INSTALLED
        if (Meta.XR.MRUtilityKit.MRUK.Instance == null) { AddLog("ERROR: MRUK Instance not found!"); return; }

        var mrukRooms = Meta.XR.MRUtilityKit.MRUK.Instance.Rooms.Where(r => r.Anchors.Any(a => a.Label.ToString().Contains("WALL"))).ToList();
        if (mrukRooms.Count == 0) {
            AddLog("Scene empty, loading...");
            await Meta.XR.MRUtilityKit.MRUK.Instance.LoadSceneFromDevice();
            mrukRooms = Meta.XR.MRUtilityKit.MRUK.Instance.Rooms;
            if (mrukRooms.Count == 0) { AddLog("ERROR: No rooms found."); return; }
        }
        
        Transform handTransform = null;
        GameObject rightHand = GameObject.Find("RightHandAnchor") ?? GameObject.Find("RightControllerAnchor") ?? GameObject.Find("RightController");
        if (rightHand != null) handTransform = rightHand.transform;
        else handTransform = Camera.main != null ? Camera.main.transform : null;

        if (handTransform == null) { AddLog("CRITICAL ERROR: No anchor!"); return; }

        houseDollhouse = new GameObject("DollhouseRoot");
        houseDollhouse.transform.SetParent(handTransform, false);
        houseDollhouse.transform.localPosition = new Vector3(0, 0.2f, 0.4f); 
        houseDollhouse.transform.localRotation = Quaternion.Euler(0, 180, 0); 
        houseDollhouse.transform.localScale = Vector3.one * 0.01f; 

        Vector3 houseCenter = Vector3.zero;
        foreach(var r in mrukRooms) houseCenter += r.transform.position;
        houseCenter /= mrukRooms.Count;

        Shader unlitShader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Standard");

        int roomCount = 0;
        int totalObjCount = 0;
        foreach (var room in mrukRooms) {
            roomCount++;
            GameObject roomRoot = new GameObject("Room_" + roomCount);
            roomRoot.transform.SetParent(houseDollhouse.transform, false);
            roomRoot.transform.localPosition = (room.transform.position - houseCenter);
            roomRoot.transform.localRotation = room.transform.localRotation;

            var anchors = room.Anchors.Where(a => a.PlaneRect.HasValue).ToList();
            var walls = anchors.Where(a => a.Label.ToString().Contains("WALL")).ToList();
            var openings = anchors.Where(a => a.Label.ToString().Contains("DOOR") || a.Label.ToString().Contains("WINDOW")).ToList();
            var floors = anchors.Where(a => a.Label.ToString().Contains("FLOOR")).ToList();

            foreach (var f in floors) {
                CreateBox(roomRoot.transform, room.transform.InverseTransformPoint(f.transform.position), 
                    Quaternion.Inverse(room.transform.rotation) * f.transform.rotation, 
                    f.PlaneRect.Value.width, f.PlaneRect.Value.height, 0.02f, new Color(0.9f, 0.9f, 0.9f, 1f), unlitShader);
            }
            foreach (var o in openings) {
                totalObjCount++;
                string lab = o.Label.ToString().ToUpper();
                Color col = lab.Contains("DOOR") ? new Color(0.45f, 0.26f, 0.08f, 1f) : new Color(0.1f, 0.5f, 0.9f, 1f);
                CreateBox(roomRoot.transform, room.transform.InverseTransformPoint(o.transform.position), 
                    Quaternion.Inverse(room.transform.rotation) * o.transform.rotation, 
                    o.PlaneRect.Value.width, o.PlaneRect.Value.height, 0.10f, col, unlitShader);
            }
            foreach (var w in walls) {
                float wW = w.PlaneRect.Value.width; float wH = w.PlaneRect.Value.height;
                var wallHoles = openings.Where(o => {
                    Vector3 lp = w.transform.InverseTransformPoint(o.transform.position);
                    return Mathf.Abs(lp.z) < 0.15f && Mathf.Abs(lp.x) < (wW/2f + 0.05f) && Mathf.Abs(lp.y) < (wH/2f + 0.05f);
                }).ToList();

                if (wallHoles.Count > 0) {
                    var xC = new List<float> { -wW/2f, wW/2f };
                    var yC = new List<float> { -wH/2f, wH/2f };
                    foreach(var h in wallHoles) {
                        Vector3 lp = w.transform.InverseTransformPoint(h.transform.position);
                        float hh = h.PlaneRect.Value.height;
                        yC.Add(Mathf.Clamp(lp.y - hh/2f, -wH/2f, wH/2f));
                        yC.Add(Mathf.Clamp(lp.y + hh/2f, -wH/2f, wH/2f));
                    }
                    var sX = xC.Distinct().OrderBy(x => x).ToList(); var sY = yC.Distinct().OrderBy(y => y).ToList();
                    for (int i=0; i<sX.Count-1; i++) {
                        for (int j=0; j<sY.Count-1; j++) {
                            float x1=sX[i], x2=sX[i+1], y1=sY[j], y2=sY[j+1];
                            if (x2-x1 < 0.005f || y2-y1 < 0.005f) continue;
                            Vector2 mid = new Vector2((x1+x2)/2f, (y1+y2)/2f);
                            bool isH = false;
                            foreach(var h in wallHoles) {
                                Vector3 lp = w.transform.InverseTransformPoint(h.transform.position);
                                if (mid.x > lp.x-h.PlaneRect.Value.width/2f+0.001f && mid.x < lp.x+h.PlaneRect.Value.width/2f-0.001f && 
                                    mid.y > lp.y-h.PlaneRect.Value.height/2f+0.001f && mid.y < lp.y+h.PlaneRect.Value.height/2f-0.001f) { isH=true; break; }
                            }
                            if (!isH) {
                                totalObjCount++;
                                CreateBox(roomRoot.transform, room.transform.InverseTransformPoint(w.transform.TransformPoint(new Vector3(mid.x, mid.y, 0))), 
                                    Quaternion.Inverse(room.transform.rotation) * w.transform.rotation, x2-x1, y2-y1, 0.25f, new Color(0.5f, 0.5f, 0.5f, 1f), unlitShader);
                            }
                        }
                    }
                } else {
                    totalObjCount++;
                    CreateBox(roomRoot.transform, room.transform.InverseTransformPoint(w.transform.position), 
                        Quaternion.Inverse(room.transform.rotation) * w.transform.rotation, wW, wH, 0.25f, new Color(0.5f, 0.5f, 0.5f, 1f), unlitShader);
                }
            }
        }
        if (statusText != null) statusText.text = "House View: ON";
        AddLog($"Dollhouse spawned: {roomCount} rooms, {totalObjCount} objects.");
        #endif
    }

    private void CreateBox(Transform parent, Vector3 localPos, Quaternion localRot, float w, float h, float d, Color col, Shader shader) {
        GameObject b = GameObject.CreatePrimitive(PrimitiveType.Cube);
        b.transform.SetParent(parent, false);
        b.transform.localPosition = localPos;
        b.transform.localRotation = localRot;
        b.transform.localScale = new Vector3(w, h, d);
        var ren = b.GetComponent<MeshRenderer>();
        ren.material = new Material(shader);
        ren.material.color = col;
        b.layer = 0;
        Destroy(b.GetComponent<BoxCollider>());
    }

    public async void OnExportAll() {
        if (statusText != null) statusText.text = "Working...";
        AddLog("Starting export...");
        bool ok = await exporter.ExportAllRooms(this);
        if (statusText != null) statusText.text = ok ? "SUCCESS" : "FAILED";
        if (ok) AddLog("Files saved to Download/XRHouseExports");
    }

    public void OpenLastReport() {
        if (exporter != null && !string.IsNullOrEmpty(exporter.LastReportPath)) {
            string p = exporter.LastReportPath;
            if (Application.platform == RuntimePlatform.Android) {
                AddLog("Report is on Quest: " + p);
                AddLog("Use 'Pull Exports to PC' in Editor.");
            } else {
                Application.OpenURL("file://" + p);
                AddLog("Opening report...");
            }
        }
    }
}