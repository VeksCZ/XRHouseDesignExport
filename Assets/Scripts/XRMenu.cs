using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// The head-anchored menu and its controller shortcuts. The menu is built here in code - the scene's
/// authored menu canvas is switched off - so its layout, size and interaction don't depend on what
/// happens to be in the scene. Every action works on the scan source picked in the &lt; label &gt; row:
/// the live device scan or one of the scans saved to the cache.
/// </summary>
public class XRMenu : MonoBehaviour
{
    public MRUKExporter exporter;
    public DollHouseVisualizer dollhouse;

    // Canvas units; the canvas is scaled to CanvasScale metres per unit (600 units -> 36 cm at 0.75 m from the eyes).
    const float CanvasWidth = 600f, CanvasHeight = 600f, CanvasScale = 0.0006f;
    const float MenuDistance = 0.75f;   // metres in front of the eyes
    const float MenuDrop = 0.24f;       // metres below eye level, so it doesn't cover what you look at
    const float FollowStart = 30f;      // degrees you must turn away before the menu follows
    const float FollowStop = 3f;        // ...and it keeps gliding until it is this close to centred
    const int VisibleLogLines = 5;
    const float FlickThreshold = 0.6f;
    const float FlickCooldown = 0.35f;

    readonly StringBuilder logHistory = new StringBuilder();
    readonly Queue<string> visibleLog = new Queue<string>();
    FloorPlanPanel plansPanel;
    Canvas menuCanvas;
    TMP_Text statusText, logText;
    RectTransform progressFill;
    Button scanLabelButton;
    XRRayInteractor rightRay;
    float flickCooldown;
    float menuYaw;
    bool menuPlaced, menuFollowing;

    async void Start()
    {
        exporter ??= FindAnyObjectByType<MRUKExporter>();
        dollhouse ??= FindAnyObjectByType<DollHouseVisualizer>() ?? gameObject.AddComponent<DollHouseVisualizer>();
        dollhouse.uiLog = this;
        plansPanel = gameObject.AddComponent<FloorPlanPanel>();

        BuildMenu();
        LimitRaysToRightHand();
        RefreshScanLabel();

        string version = VersionDisplay.BuildTime;
        AddLog("Build " + version);
        SetStatus("Ready");
        await Task.Delay(300);
        AddLog("XR Ready.");
    }

    void BuildMenu()
    {
        menuCanvas = XRUi.CreateWorldCanvas("WristMenu", new Vector2(CanvasWidth, CanvasHeight), CanvasScale);
        var root = menuCanvas.transform;

        XRUi.CreatePanel(root, "Background", XRUi.PanelColor, 0, 0, CanvasWidth, CanvasHeight);
        XRUi.CreateText(root, "Title", "XR House Export", 34, TextAlignmentOptions.MidlineLeft, 24, 12, 340, 44, XRUi.TextColor, FontStyles.Bold);
        statusText = XRUi.CreateText(root, "Status", "", 26, TextAlignmentOptions.MidlineRight, 340, 12, 236, 44, XRUi.AccentText);

        XRUi.CreatePanel(root, "ProgressBack", new Color(1, 1, 1, 0.12f), 24, 62, 552, 8);
        progressFill = XRUi.CreatePanel(root, "ProgressFill", XRUi.AccentText, 24, 62, 552, 8).rectTransform;
        SetProgress(0);

        const float left = 24, right = 308, w = 268, h = 78, gap = 10, top = 84;
        XRUi.CreateButton(root, "Export (B)", left, top, w, h, OnExportAll);
        // Dollhouse on/off and which of its 3 tiers to show are two separate buttons sharing this cell, so
        // turning it on never guesses which tier you left it on and switching tiers never needs to cycle
        // through "off" first.
        const float modeW = 90;
        XRUi.CreateButton(root, "Dollhouse (A)", right, top, w - modeW - gap, h, OnToggleHouseView);
        XRUi.CreateButton(root, "Mode", right + w - modeW, top, modeW, h, OnCycleDollhouseMode, 22);
        XRUi.CreateButton(root, "Floor plans", left, top + (h + gap), w, h, OnShowPlans);
        XRUi.CreateButton(root, "Save scan", right, top + (h + gap), w, h, OnSaveScan);
        XRUi.CreateButton(root, "Open report", left, top + 2 * (h + gap), w, h, OpenReportInApp);
        XRUi.CreateButton(root, "Load scan", right, top + 2 * (h + gap), w, h, OnLoadSource);

        float rowY = top + 3 * (h + gap);
        XRUi.CreateButton(root, "<", left, rowY, 80, h, () => OnCycleSource(-1), 40);
        scanLabelButton = XRUi.CreateButton(root, MRUKExporter.LiveSource, left + 90, rowY, 372, h, OnLoadSource, 28);
        XRUi.CreateButton(root, ">", left + 472, rowY, 80, h, () => OnCycleSource(1), 40);

        logText = XRUi.CreateText(root, "Log", "", 22, TextAlignmentOptions.TopLeft, 24, rowY + h + 12, 552, CanvasHeight - (rowY + h + 12) - 10, XRUi.MutedText);
        logText.overflowMode = TextOverflowModes.Truncate;
    }

    /// <summary>
    /// Anchors the menu to the view: always at the same distance and height relative to your eyes, so only
    /// the right controller (pointing + trigger) is needed. It doesn't stick rigidly to the head - it stays put
    /// until you turn well away, then glides back to the centre of your view.
    /// </summary>
    void LateUpdate()
    {
        var cam = Camera.main;
        if (cam == null || menuCanvas == null) return;

        float camYaw = cam.transform.eulerAngles.y;
        float delta = Mathf.Abs(Mathf.DeltaAngle(menuYaw, camYaw));
        if (!menuPlaced) { menuYaw = camYaw; menuPlaced = true; }
        else
        {
            if (delta > FollowStart) menuFollowing = true;
            if (menuFollowing)
            {
                menuYaw = Mathf.LerpAngle(menuYaw, camYaw, 1f - Mathf.Exp(-3f * Time.deltaTime));
                if (delta < FollowStop) menuFollowing = false;
            }
        }

        Vector3 eye = cam.transform.position;
        Vector3 position = eye + Quaternion.Euler(0f, menuYaw, 0f) * new Vector3(0f, -MenuDrop, MenuDistance);
        menuCanvas.transform.SetPositionAndRotation(position, Quaternion.LookRotation(position - eye, Vector3.up));
        menuCanvas.worldCamera = cam;
    }

    /// <summary>The scene has duplicate ray interactors on both hands; keep exactly one, on the right hand, so the left hand never points at its own menu.</summary>
    void LimitRaysToRightHand()
    {
        var rig = FindFirstObjectByType<OVRCameraRig>();
        if (rig == null || rig.rightHandAnchor == null) return;

        foreach (var ray in FindObjectsByType<XRRayInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            bool keep = rightRay == null && ray.transform.IsChildOf(rig.rightHandAnchor);
            if (keep) rightRay = ray;
            ray.gameObject.SetActive(keep);
        }
    }

    public void AddLog(string msg)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        logHistory.AppendLine(line);
        Debug.Log("VR: " + msg);

        if (logText == null) return;
        visibleLog.Enqueue(line);
        while (visibleLog.Count > VisibleLogLines) visibleLog.Dequeue();
        logText.text = string.Join("\n", visibleLog);
    }

    /// <summary>0..100, used by the export pipeline.</summary>
    public void SetProgress(float percent)
    {
        if (progressFill == null) return;
        progressFill.sizeDelta = new Vector2(552f * Mathf.Clamp01(percent / 100f), 8f);
    }

    void SetStatus(string text)
    {
        if (statusText != null) statusText.text = text;
    }

    /// <summary>Shows a status and lets one frame render it, so heavy work that follows doesn't leave the UI looking frozen with no explanation.</summary>
    async Task Busy(string text)
    {
        SetStatus(text);
        await Task.Yield();
    }

    void Update()
    {
        bool plansOpen = plansPanel != null && plansPanel.IsOpen;
        if (menuCanvas != null && menuCanvas.gameObject.activeSelf == plansOpen) menuCanvas.gameObject.SetActive(!plansOpen);

        // Right trigger clicks whatever button the ray points at. The XR UI module only handles hover here
        // (the scene never enables the Input System actions that would deliver "press"), and the rest of the
        // app already reads the controllers through OVRInput.
        if (OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch)) ClickUnderRay();

        // Right hand: B = export, A = dollhouse on/off, stick click = open report, stick left/right = dollhouse
        // mode (only while it's on and floor plans are closed - otherwise the stick pages floor plans instead).
        if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch)) OnExportAll();
        if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch)) OnToggleHouseView();
        if (OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.RTouch)) OpenReportInApp();

        // Left hand: Y = save log, X = save scan, stick click = floor plans, stick left/right = scan source.
        if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch)) SaveLogToFile();
        if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.LTouch)) OnSaveScan();
        if (OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.LTouch)) OnShowPlans();

        flickCooldown -= Time.deltaTime;
        if (flickCooldown > 0f) return;

        Vector2 right = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
        Vector2 left = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);

        if (plansOpen && !(dollhouse != null && dollhouse.IsDragging) && Mathf.Abs(right.x) > FlickThreshold)
        {
            plansPanel.Step(right.x > 0 ? 1 : -1);
            flickCooldown = FlickCooldown;
        }
        else if (!plansOpen && dollhouse != null && dollhouse.IsOn && !dollhouse.IsDragging && Mathf.Abs(right.x) > FlickThreshold)
        {
            // Quick access to the same 3-tier cycle as the Mode button, without reaching for the menu.
            OnCycleDollhouseMode();
            flickCooldown = FlickCooldown;
        }
        else if (Mathf.Abs(left.x) > FlickThreshold)
        {
            OnCycleSource(left.x > 0 ? 1 : -1);
            flickCooldown = FlickCooldown;
        }
    }

    void ClickUnderRay()
    {
        if (rightRay == null || !rightRay.TryGetCurrentUIRaycastResult(out RaycastResult hit) || hit.gameObject == null) return;
        var button = hit.gameObject.GetComponentInParent<Button>();
        if (button != null && button.IsInteractable()) button.onClick.Invoke();
    }

    void RefreshScanLabel()
    {
        if (exporter != null) XRUi.SetLabel(scanLabelButton, exporter.SelectedSource);
    }

    bool RequireExporter()
    {
        if (exporter != null) return true;
        AddLog("<color=red>ERROR: MRUKExporter not found in the scene.</color>");
        SetStatus("ERROR");
        return false;
    }

    void OnCycleSource(int direction)
    {
        if (!RequireExporter()) return;
        exporter.CycleSource(direction, this);
        RefreshScanLabel();
    }

    async void OnLoadSource()
    {
        if (!RequireExporter()) return;
        await Busy("Loading...");
        bool ok = await exporter.EnsureSelectedSourceLoaded(this, forceReload: true);
        SetStatus(ok ? "Loaded" : "ERROR");
    }

    public async void OnExportAll()
    {
        if (!RequireExporter()) return;
        AddLog($"Exporting '{exporter.SelectedSource}'...");
        await Busy("Exporting...");
        bool ok = await exporter.ExportAllRooms(this);
        SetStatus(ok ? "Export done" : "ERROR");
    }

    /// <summary>Plain on/off - it always shows whatever mode was last picked with the Mode button, so it never
    /// needs the loading/validity checks below except when actually turning on.</summary>
    public async void OnToggleHouseView()
    {
        if (dollhouse == null) return;
        if (dollhouse.IsOn) { dollhouse.ToggleOnOff(); AddLog("Dollhouse OFF"); SetStatus("Ready"); return; }

        if (!RequireExporter()) return;
        await Busy("Loading...");
        if (!await exporter.EnsureSelectedSourceLoaded(this)) { SetStatus("ERROR"); return; }
        if (!exporter.HasValidRooms())
        {
            AddLog("<color=red>No rooms in this scan. Pick a saved scan with < > or scan in Quest Room Setup.</color>");
            SetStatus("No rooms");
            return;
        }
        await Busy("Building...");
        dollhouse.ToggleOnOff();
        AddLog($"Dollhouse ON - mode: <b>{dollhouse.ModeLabel}</b>");
        // The mode name stays in the status bar the whole time it's up, not just a log line that scrolls away -
        // so which of the 3 dollhouse modes (Anchor/Mesh/Raw) is on screen is never a guess.
        SetStatus($"Dollhouse: {dollhouse.ModeLabel}");
    }

    /// <summary>Cycles which of the 3 dollhouse tiers (Anchor/Mesh/Raw) is shown - separate from turning the
    /// dollhouse itself on or off, so switching tiers never needs 1-3 extra presses through an off state first.</summary>
    public void OnCycleDollhouseMode()
    {
        if (dollhouse == null) return;
        string m = dollhouse.CycleMode();
        AddLog($"Dollhouse mode: <b>{m}</b>");
        if (dollhouse.IsOn) SetStatus($"Dollhouse: {m}");
    }

    public async void OnSaveScan()
    {
        if (!RequireExporter()) return;
        await Busy("Saving...");
        string name = await exporter.SaveScanToCache(this);
        SetStatus(name != null ? "Scan saved" : "Save failed");
        RefreshScanLabel();
    }

    public async void OnShowPlans()
    {
        if (plansPanel == null) return;
        if (plansPanel.IsOpen) { plansPanel.Hide(); return; }
        if (!RequireExporter()) return;
        await Busy("Loading...");
        if (!await exporter.EnsureSelectedSourceLoaded(this)) { SetStatus("ERROR"); return; }

        var pages = exporter.BuildPlanPages();
        if (pages.Count == 0)
        {
            AddLog("<color=red>No floor plans - the scan has no valid rooms.</color>");
            SetStatus("No rooms");
            return;
        }
        plansPanel.Rebuild = exporter.BuildPlanPages;
        plansPanel.Show(pages, exporter.SelectedSource);
        AddLog($"Floor plans: {pages.Count} sheet(s).");
        SetStatus("Plans open");
    }

    public void SaveLogToFile()
    {
        try
        {
            string root = MRUKPathUtility.GetLogRoot();
            Directory.CreateDirectory(root);
            string p = Path.Combine(root, $"Log_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(p, logHistory.ToString());
            AddLog("Log saved.");
        }
        catch (Exception ex) { Debug.LogError(ex.Message); }
    }

    public void OpenReportInApp()
    {
        string report = MRUKExporter.LastReportPath;
        if (string.IsNullOrEmpty(report)) { AddLog("<color=red>No report yet - run Export first.</color>"); return; }
        if (!File.Exists(report)) { AddLog("<color=red>File not found: </color>" + Path.GetFileName(report)); return; }

        string url = "file://" + report;
        AddLog("Opening: " + Path.GetFileName(report));
        if (Application.platform == RuntimePlatform.Android)
        {
            try
            {
                using (var intentClass = new AndroidJavaClass("android.content.Intent"))
                using (var intent = new AndroidJavaObject("android.content.Intent"))
                {
                    intent.Call<AndroidJavaObject>("setAction", intentClass.GetStatic<string>("ACTION_VIEW"));
                    using (var uriClass = new AndroidJavaClass("android.net.Uri"))
                    using (var uri = uriClass.CallStatic<AndroidJavaObject>("parse", url))
                    {
                        intent.Call<AndroidJavaObject>("setData", uri);
                        intent.Call<AndroidJavaObject>("addFlags", intentClass.GetStatic<int>("FLAG_ACTIVITY_NEW_TASK"));
                        using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                        using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                            activity.Call("startActivity", intent);
                    }
                }
            }
            catch { Application.OpenURL(url); }
        }
        else Application.OpenURL(url);
    }
}
