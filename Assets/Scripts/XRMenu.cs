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
        AddLog("<color=yellow>Build: " + VersionDisplay.BuildTime + "</color>");

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
        if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch) || OVRInput.GetDown(OVRInput.Button.Four, OVRInput.Controller.LTouch)) SaveLogToFile();

        // Right thumbstick scrolling for log
        if (logScrollView != null) {
            Vector2 thumb = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
            if (Mathf.Abs(thumb.y) > 0.1f) {
                // Adjust sensitivity as needed, 2.0f is a starting point
                logScrollView.verticalNormalizedPosition = Mathf.Clamp01(logScrollView.verticalNormalizedPosition + thumb.y * Time.deltaTime * 3.0f);
            }
        }
    }

    public void SaveLogToFile() {
        try {
            string root = Application.isEditor ? "Exports/Logs" : "/sdcard/Download/XRHouseExports/Logs";
            System.IO.Directory.CreateDirectory(root);
            string filename = $"SessionLog_{System.DateTime.Now:yyyyMMdd_HHmmss}.txt";
            string path = System.IO.Path.Combine(root, filename);
            System.IO.File.WriteAllText(path, GetLogHistory());
            AddLog("<color=yellow>LOG ULOŽEN:</color> " + filename);
        } catch (System.Exception ex) {
            Debug.LogError("Failed to save log: " + ex.Message);
        }
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
        if (mrukRooms.Count == 0 || !Application.isEditor) {
            AddLog("Inicializace scény...");
            if (!Application.isEditor) await Meta.XR.MRUtilityKit.MRUK.Instance.LoadSceneFromDevice();
            await System.Threading.Tasks.Task.Delay(1000); 
            mrukRooms = Meta.XR.MRUtilityKit.MRUK.Instance.Rooms.ToList();
            if (mrukRooms.Count == 0) { AddLog("CHYBA: Scéna je prázdná."); return; }
        }
        
        Transform handTransform = null;
        GameObject rightHand = GameObject.Find("RightHandAnchor") ?? GameObject.Find("RightControllerAnchor") ?? GameObject.Find("RightController");
        if (rightHand != null) handTransform = rightHand.transform;
        else handTransform = Camera.main != null ? Camera.main.transform : null;

        if (handTransform == null) { AddLog("CRITICAL ERROR: No anchor!"); return; }

        // 1. Calculate center of the whole house in world space
        Vector3 houseCenter = Vector3.zero;
        int totalStructural = 0;
        foreach(var r in mrukRooms) {
            foreach(var a in r.Anchors.Where(x => x.Label.ToString().Contains("WALL"))) {
                houseCenter += a.transform.position;
                totalStructural++;
            }
        }
        if (totalStructural > 0) houseCenter /= totalStructural;
        else houseCenter = mrukRooms[0].transform.position;

        // 1.5 Calculate global correction angle (align to longest wall)
        float globalAngle = 0; float maxW = 0;
        foreach (var r in mrukRooms) {
            foreach (var a in r.Anchors.Where(x => x.Label.ToString().ToUpper().Contains("WALL") && x.PlaneRect.HasValue)) {
                if (a.PlaneRect.Value.width > maxW) { 
                    maxW = a.PlaneRect.Value.width; 
                    globalAngle = -a.transform.eulerAngles.y; 
                }
            }
        }
        Quaternion globalCorrection = Quaternion.Euler(0, globalAngle, 0);

        // 2. Create the Dollhouse Root
        houseDollhouse = new GameObject("DollhouseRoot");
        houseDollhouse.transform.SetParent(handTransform, false);
        houseDollhouse.transform.localPosition = new Vector3(0, 0.2f, 0.4f); 
        houseDollhouse.transform.localRotation = Quaternion.Euler(0, 180, 0); 
        houseDollhouse.transform.localScale = Vector3.one * 0.01f; // 1:100 scale

        Shader unlitShader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
        Color wallCol = new Color(0.4f, 0.4f, 0.4f, 1f);
        Color floorCol = new Color(0.7f, 0.7f, 0.7f, 1f);
        Color doorCol = new Color(0.5f, 0.3f, 0.1f, 1f);
        Color winCol = new Color(0.3f, 0.6f, 0.9f, 1f);

        int totalObjCount = 0;

        // Collect all structural anchors
        var allAnchors = mrukRooms.SelectMany(r => r.Anchors).Where(a => a.PlaneRect.HasValue).ToList();
        var openings = allAnchors.Where(a => {
            string l = a.Label.ToString().ToUpper();
            return l.Contains("DOOR") || l.Contains("WINDOW") || l.Contains("OPENING");
        }).ToList();

        foreach (var a in allAnchors) {
            string lab = a.Label.ToString().ToUpper();
            if (lab.Contains("CEILING") || lab.Contains("INVISIBLE")) continue;
            
            bool isWall = lab.Contains("WALL");
            bool isFloor = lab.Contains("FLOOR");
            bool isOpening = lab.Contains("DOOR") || lab.Contains("WINDOW") || lab.Contains("OPENING");

            if (!isWall && !isFloor && !isOpening) continue;

            if (isWall) {
                float wW = a.PlaneRect.Value.width; float wH = a.PlaneRect.Value.height;
                var wallHoles = openings.Where(o => {
                    Vector3 lp = a.transform.InverseTransformPoint(o.transform.position);
                    return Mathf.Abs(lp.z) < 0.3f && Mathf.Abs(lp.x) < (wW/2f + 0.1f) && Mathf.Abs(lp.y) < (wH/2f + 0.1f);
                }).ToList();

                if (wallHoles.Count > 0) {
                    var xC = new List<float> { -wW/2f, wW/2f }; var yC = new List<float> { -wH/2f, wH/2f };
                    foreach(var h in wallHoles) {
                        Vector3 lp = a.transform.InverseTransformPoint(h.transform.position);
                        float hw = h.PlaneRect.Value.width/2f; float hh = h.PlaneRect.Value.height/2f;
                        xC.Add(Mathf.Clamp(lp.x - hw, -wW/2f, wW/2f)); xC.Add(Mathf.Clamp(lp.x + hw, -wW/2f, wW/2f));
                        yC.Add(Mathf.Clamp(lp.y - hh, -wH/2f, wH/2f)); yC.Add(Mathf.Clamp(lp.y + hh, -wH/2f, wH/2f));
                    }
                    var sX = xC.Distinct().OrderBy(x => x).ToList(); var sY = yC.Distinct().OrderBy(y => y).ToList();
                    for (int i=0; i<sX.Count-1; i++) {
                        for (int j=0; j<sY.Count-1; j++) {
                            float x1=sX[i], x2=sX[i+1], y1=sY[j], y2=sY[j+1];
                            if (x2-x1 < 0.02f || y2-y1 < 0.02f) continue;
                            Vector2 mid = new Vector2((x1+x2)/2f, (y1+y2)/2f);
                            bool isH = false;
                            foreach(var h in wallHoles) {
                                Vector3 lp = a.transform.InverseTransformPoint(h.transform.position);
                                float hw = h.PlaneRect.Value.width/2f; float hh = h.PlaneRect.Value.height/2f;
                                if (mid.x > lp.x-hw+0.01f && mid.x < lp.x+hw-0.01f && mid.y > lp.y-hh+0.01f && mid.y < lp.y+hh-0.01f) { isH=true; break; }
                            }
                            if (!isH) {
                                totalObjCount++;
                                CreateBoxV2(houseDollhouse.transform, globalCorrection * (a.transform.TransformPoint(new Vector3(mid.x, mid.y, 0)) - houseCenter), 
                                    globalCorrection * a.transform.rotation, new Vector3(x2-x1, y2-y1, 0.25f), wallCol, unlitShader);
                            }
                        }
                    }
                } else {
                    totalObjCount++;
                    CreateBoxV2(houseDollhouse.transform, globalCorrection * (a.transform.position - houseCenter), globalCorrection * a.transform.rotation, new Vector3(wW, wH, 0.25f), wallCol, unlitShader);
                }
            } else {
                totalObjCount++;
                float thickness = isFloor ? 0.1f : (lab.Contains("DOOR") ? 0.12f : 0.15f);
                Color col = isFloor ? floorCol : (lab.Contains("DOOR") ? doorCol : winCol);
                Vector3 offset = isFloor ? new Vector3(0, 0, -0.05f) : Vector3.zero;
                
                CreateBoxV2(houseDollhouse.transform, globalCorrection * (a.transform.TransformPoint(offset) - houseCenter), globalCorrection * a.transform.rotation, 
                    new Vector3(a.PlaneRect.Value.width, a.PlaneRect.Value.height, thickness), col, unlitShader);
            }
        }

        if (logScrollView != null && logScrollView.verticalScrollbar == null) AddScrollbarToLog();

        if (statusText != null) statusText.text = "House View: ON";
        AddLog($"Dollhouse spawned: {mrukRooms.Count} rooms, {totalObjCount} objects.");
        #endif
    }

    private void CreateBoxV2(Transform parent, Vector3 localPos, Quaternion localRot, Vector3 size, Color col, Shader shader) {
        GameObject b = GameObject.CreatePrimitive(PrimitiveType.Cube);
        b.name = "Cube";
        b.transform.SetParent(parent, false);
        b.transform.localPosition = localPos;
        b.transform.localRotation = localRot;
        b.transform.localScale = size;
        var ren = b.GetComponent<MeshRenderer>();
        ren.material = new Material(shader);
        ren.material.color = col;
        b.layer = 0;
        Destroy(b.GetComponent<BoxCollider>());
    }

    private void AddScrollbarToLog() {
        Sprite dot = Resources.FindObjectsOfTypeAll<Sprite>().FirstOrDefault(s => s.name == "UISprite" || s.name == "Background");
        GameObject sbObj = new GameObject("Scrollbar", typeof(RectTransform), typeof(Scrollbar), typeof(Image));
        sbObj.transform.SetParent(logScrollView.transform, false);
        var rt = sbObj.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(1, 0); rt.anchorMax = new Vector2(1, 1);
        rt.pivot = new Vector2(1, 1); rt.sizeDelta = new Vector2(30, -10);
        rt.anchoredPosition = new Vector2(-5, 0);
        sbObj.GetComponent<Image>().color = new Color(0, 0, 0, 0.4f);
        var sb = sbObj.GetComponent<Scrollbar>();
        sb.direction = Scrollbar.Direction.BottomToTop;
        GameObject slidingArea = new GameObject("SlidingArea", typeof(RectTransform));
        slidingArea.transform.SetParent(sbObj.transform, false);
        var saRt = slidingArea.GetComponent<RectTransform>();
        saRt.anchorMin = Vector2.zero; saRt.anchorMax = Vector2.one; saRt.sizeDelta = Vector2.zero;
        GameObject handle = new GameObject("Handle", typeof(RectTransform), typeof(Image));
        handle.transform.SetParent(slidingArea.transform, false);
        var hRt = handle.GetComponent<RectTransform>();
        hRt.anchorMin = Vector2.zero; hRt.anchorMax = new Vector2(1, 0.2f); hRt.sizeDelta = Vector2.zero;
        var hImg = handle.GetComponent<Image>();
        hImg.color = new Color(1, 1, 1, 0.8f);
        if (dot != null) hImg.sprite = dot;
        sb.handleRect = hRt;
        logScrollView.verticalScrollbar = sb;
        logScrollView.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
    }

    public async void OnExportAll() {
        if (statusText != null) statusText.text = "Working...";
        AddLog("<color=cyan>Spouštím export...</color>");
        try {
            bool ok = await exporter.ExportAllRooms(this);
            if (statusText != null) statusText.text = ok ? "DOKONČENO" : "CHYBA";
            if (ok) AddLog("<color=green>Export hotov!</color> Soubory jsou v Download/XRHouseExports");
            else AddLog("<color=red>Export selhal.</color> Zkontroluj logy.");
        } catch (System.Exception ex) {
            AddLog("<color=red>KRITICKÁ CHYBA:</color> " + ex.Message);
            if (statusText != null) statusText.text = "CRASH";
        }
    }

    public void OpenLastReport() {
        if (!string.IsNullOrEmpty(MRUKExporter.LastReportPath)) {
            string p = MRUKExporter.LastReportPath;
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