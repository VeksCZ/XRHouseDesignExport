using UnityEngine;
using UnityEngine.UI;
using System.Text;
using System.Linq;
using System.Threading.Tasks;
using System;
using System.IO;

public class XRMenu : MonoBehaviour
{
    public Button exportAllButton, viewHouseButton, openReportButton;
    public MRUKExporter exporter;
    public DollHouseVisualizer dollhouse;
    public Text statusText, logText;
    public ScrollRect logScrollView;
    private StringBuilder logHistory = new StringBuilder();

    async void Start() {
        AddLog("<color=yellow>Build: " + VersionDisplay.BuildTime + "</color>");
        exportAllButton?.onClick.AddListener(OnExportAll);
        viewHouseButton?.onClick.AddListener(OnToggleHouseView);
        openReportButton?.onClick.AddListener(OpenReportInApp);
        exporter ??= FindAnyObjectByType<MRUKExporter>();
        dollhouse ??= FindAnyObjectByType<DollHouseVisualizer>() ?? gameObject.AddComponent<DollHouseVisualizer>();
        dollhouse.uiLog = this;
        if (statusText != null) statusText.text = "Ready";
        await Task.Delay(500); AddLog("XR Ready.");
        FixLayout();
    }

    private void FixLayout() {
        var layout = GetComponentInChildren<VerticalLayoutGroup>();
        if (layout != null && openReportButton != null) openReportButton.transform.SetSiblingIndex(2);
    }

    public void AddLog(string msg) {
        string f = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        if (logText != null) logText.text += f + "\n";
        logHistory.AppendLine(f);
        if (logScrollView != null) { Canvas.ForceUpdateCanvases(); logScrollView.verticalNormalizedPosition = 0f; }
        Debug.Log("VR: " + msg);
    }

    void Update() {
        if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch)) OnExportAll();
        if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch)) OnToggleHouseView();
        if (OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.RTouch)) OpenReportInApp();
        if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch)) SaveLogToFile();

        if (logScrollView != null && (dollhouse == null || !dollhouse.IsDragging)) {
            Vector2 s = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
            if (Mathf.Abs(s.y) > 0.1f) logScrollView.verticalNormalizedPosition = Mathf.Clamp01(logScrollView.verticalNormalizedPosition + s.y * Time.deltaTime * 3f);
        }
    }

    public async void OnExportAll() {
        AddLog("<color=cyan>Exporting...</color>");
        if (statusText != null) statusText.text = "Working";
        bool ok = await exporter.ExportAllRooms(this);
        if (statusText != null) statusText.text = ok ? "COMPLETED" : "ERROR";
    }

    public void OnToggleHouseView() {
        if (dollhouse != null) { bool on = dollhouse.Toggle(); AddLog(on ? "View ON" : "View OFF"); }
    }

    public void SaveLogToFile() {
        try {
            string root = MRUKPathUtility.GetLogRoot(); Directory.CreateDirectory(root);
            string p = Path.Combine(root, $"Log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(p, logHistory.ToString()); AddLog("Log saved.");
        } catch (Exception ex) { Debug.LogError(ex.Message); }
    }

    public void OpenReportInApp() {
        if (string.IsNullOrEmpty(MRUKExporter.LastReportPath)) { AddLog("No report."); return; }
        string url = "file://" + MRUKExporter.LastReportPath;
        if (Application.platform == RuntimePlatform.Android) {
            try {
                using (AndroidJavaClass iC = new AndroidJavaClass("android.content.Intent"))
                using (AndroidJavaObject iO = new AndroidJavaObject("android.content.Intent")) {
                    iO.Call<AndroidJavaObject>("setAction", iC.GetStatic<string>("ACTION_VIEW"));
                    using (AndroidJavaClass uC = new AndroidJavaClass("android.net.Uri"))
                    using (AndroidJavaObject uO = uC.CallStatic<AndroidJavaObject>("parse", url)) {
                        iO.Call<AndroidJavaObject>("setData", uO);
                        iO.Call<AndroidJavaObject>("addFlags", iC.GetStatic<int>("FLAG_ACTIVITY_NEW_TASK"));
                        using (AndroidJavaClass uP = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                        using (AndroidJavaObject cA = uP.GetStatic<AndroidJavaObject>("currentActivity")) cA.Call("startActivity", iO);
                    }
                }
                AddLog("Opening browser...");
            } catch { Application.OpenURL(url); }
        } else Application.OpenURL(url);
    }
}