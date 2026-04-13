using UnityEngine;
using UnityEngine.UI;
using System.Text;
using System.Linq;

public class XRMenu : MonoBehaviour
{
    public Button exportAllButton;
    public Button viewHouseButton;
    public Button openReportButton; 
    public MRUKExporter exporter;
    public DollHouseVisualizer dollhouse;
    public Text statusText;
    public Text logText;
    public ScrollRect logScrollView;
    private StringBuilder logHistory = new StringBuilder();

    async void Start()
    {
        AddLog("<color=yellow>Build: " + VersionDisplay.BuildTime + "</color>");
        if (exportAllButton != null) exportAllButton.onClick.AddListener(OnExportAll);
        if (viewHouseButton != null) viewHouseButton.onClick.AddListener(OnToggleHouseView);
        if (openReportButton != null) openReportButton.onClick.AddListener(OpenLastReport);
        
        if (exporter == null) exporter = Object.FindAnyObjectByType<MRUKExporter>();
        
        // Find or create Dollhouse component
        dollhouse = Object.FindAnyObjectByType<DollHouseVisualizer>();
        if (dollhouse == null) {
            AddLog("Adding Dollhouse component...");
            dollhouse = gameObject.AddComponent<DollHouseVisualizer>();
        }
        
        dollhouse.uiLog = this;
        if (statusText != null) statusText.text = "System Ready";

        // Try to dismiss universal menu (Navigator)
        #if META_XR_SDK_INSTALLED
        await System.Threading.Tasks.Task.Delay(1000);
        try {
            var manager = OVRManager.instance;
            if (manager != null) {
                // There is no public Dismiss API in standard OVRManager, 
                // but setting focus can help.
            }
        } catch {}
        #endif
    }

    public void AddLog(string msg) {
        string formattedMsg = $"[{System.DateTime.Now:HH:mm:ss}] {msg}";
        if (logText != null) logText.text += formattedMsg + "\n";
        logHistory.AppendLine(formattedMsg);
        if (logScrollView != null) { 
            Canvas.ForceUpdateCanvases(); 
            logScrollView.verticalNormalizedPosition = 0f; 
            if (logScrollView.verticalScrollbar == null) AddScrollbarToLog();
        }
        Debug.Log("VR_LOG: " + msg);
    }

    void Update()
    {
        if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch)) OnExportAll();
        if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch)) OnToggleHouseView();
        if (OVRInput.GetDown(OVRInput.Button.Three, OVRInput.Controller.LTouch)) OpenLastReport();
        if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch) || OVRInput.GetDown(OVRInput.Button.Four, OVRInput.Controller.LTouch)) SaveLogToFile();
        
        if (logScrollView != null) {
            Vector2 thumb = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
            if (Mathf.Abs(thumb.y) > 0.1f) {
                logScrollView.verticalNormalizedPosition = Mathf.Clamp01(logScrollView.verticalNormalizedPosition + thumb.y * Time.deltaTime * 3.0f);
            }
        }
    }

    public async void OnExportAll() {
        AddLog("ExportAll Button Clicked.");
        if (statusText != null) statusText.text = "Working...";
        AddLog("<color=cyan>Spouštím export...</color>");
        bool ok = await exporter.ExportAllRooms(this);
        if (statusText != null) statusText.text = ok ? "DOKONČENO" : "CHYBA";
        if (ok) AddLog("<color=green>Export hotov!</color>");
    }

    public void OnToggleHouseView() {
        AddLog("ToggleHouse Button Clicked.");
        if (dollhouse != null) {
            bool active = dollhouse.Toggle();
            AddLog(active ? "Dollhouse spawned." : "Dollhouse closed.");
            if (statusText != null) statusText.text = active ? "House View: ON" : "House View: OFF";
        } else {
            AddLog("<color=red>ERROR: Dollhouse component not found!</color>");
        }
    }

    public void SaveLogToFile() {
        try {
            string root = Application.isEditor ? "Exports/Logs" : "/sdcard/Download/XRHouseExports/Logs";
            System.IO.Directory.CreateDirectory(root);
            string filename = $"SessionLog_{System.DateTime.Now:yyyyMMdd_HHmmss}.txt";
            string path = System.IO.Path.Combine(root, filename);
            System.IO.File.WriteAllText(path, logHistory.ToString());
            AddLog("<color=yellow>LOG ULOŽEN:</color> " + filename);
        } catch (System.Exception ex) {
            Debug.LogError("Failed to save log: " + ex.Message);
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

    private void AddScrollbarToLog() {
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
        
        handle.GetComponent<Image>().color = new Color(1, 1, 1, 0.8f);
        sb.handleRect = hRt;
        logScrollView.verticalScrollbar = sb;
        logScrollView.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
    }
}
