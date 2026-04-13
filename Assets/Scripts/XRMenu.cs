using UnityEngine;
using UnityEngine.UI;
using System.Text;
using System.Linq;
using System.Threading.Tasks;
using System;
using System.IO;

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
        AddLog($"<color=yellow>Build: {VersionDisplay.BuildTime}</color>");

        exportAllButton?.onClick.AddListener(OnExportAll);
        viewHouseButton?.onClick.AddListener(OnToggleHouseView);
        openReportButton?.onClick.AddListener(OpenLastReport);

        exporter ??= FindAnyObjectByType<MRUKExporter>();

        dollhouse ??= FindAnyObjectByType<DollHouseVisualizer>() ?? gameObject.AddComponent<DollHouseVisualizer>();
        dollhouse.uiLog = this;

        if (statusText != null) statusText.text = "System Ready";

        await Task.Delay(800);
        AddLog("XR Menu initialized.");
    }

    public void AddLog(string msg)
    {
        string formatted = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        if (logText != null) logText.text += formatted + "\n";
        logHistory.AppendLine(formatted);

        if (logScrollView != null)
        {
            Canvas.ForceUpdateCanvases();
            logScrollView.verticalNormalizedPosition = 0f;
        }

        Debug.Log("VR_LOG: " + msg);
    }

    void Update()
    {
        if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch)) OnExportAll();
        if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch)) OnToggleHouseView();
        if (OVRInput.GetDown(OVRInput.Button.Three, OVRInput.Controller.LTouch)) OpenLastReport();
        if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch) || 
            OVRInput.GetDown(OVRInput.Button.Four, OVRInput.Controller.LTouch)) 
            SaveLogToFile();

        // Thumbstick scrolling
        if (logScrollView != null)
        {
            Vector2 thumb = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
            if (Mathf.Abs(thumb.y) > 0.1f)
            {
                logScrollView.verticalNormalizedPosition = Mathf.Clamp01(
                    logScrollView.verticalNormalizedPosition + thumb.y * Time.deltaTime * 3f);
            }
        }
    }

    public async void OnExportAll()
    {
        AddLog("<color=cyan>Spouštím export všech místností...</color>");
        if (statusText != null) statusText.text = "Working...";

        bool success = await exporter.ExportAllRooms(this);

        if (statusText != null)
            statusText.text = success ? "DOKONČENO" : "CHYBA";
        
        // Removed duplicate log here as exporter.ExportAllRooms already logs success
    }

    public void OnToggleHouseView()
    {
        if (dollhouse != null)
        {
            bool active = dollhouse.Toggle();
            AddLog(active ? "Dollhouse zobrazen." : "Dollhouse skryt.");
            if (statusText != null) statusText.text = active ? "House View: ON" : "House View: OFF";
        }
    }

    public void SaveLogToFile()
    {
        try
        {
            string root = Application.isEditor ? "Exports/Logs" : "/sdcard/Download/XRHouseExports/Logs";
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, $"SessionLog_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(path, logHistory.ToString());
            AddLog($"<color=yellow>Log uložen:</color> {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            Debug.LogError("Save log failed: " + ex.Message);
        }
    }

    public void OpenLastReport()
    {
        if (string.IsNullOrEmpty(MRUKExporter.LastReportPath))
        {
            AddLog("<color=yellow>Žádný report k otevření.</color>");
            return;
        }

        string path = MRUKExporter.LastReportPath;
        if (Application.platform == RuntimePlatform.Android)
        {
            AddLog("Report je na Questu: " + path);
            AddLog("Použij Pull z editoru.");
        }
        else
        {
            Application.OpenURL("file://" + path);
            AddLog("Otevírám report...");
        }
    }
}
