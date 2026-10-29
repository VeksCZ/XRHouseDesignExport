using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Visuals;

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
    public LiveScanOverlay scanOverlay;
    public MiniMapPanel miniMap;
    public WalkThroughMode walk;
    public EditModeController editMode;
    const float EditHoldSeconds = 1f;   // hold Y this long to toggle edit mode (a short tap still saves the log)
    float yDownTime;
    bool yHoldFired;

    // Canvas units; the canvas is scaled to CanvasScale metres per unit (600 units -> 36 cm at 0.75 m from the eyes).
    const float HeaderH = 40f;          // the draggable handle strip at the very top
    const float CanvasWidth = 600f, CanvasHeight = 766f + StateH + HeaderH, CanvasScale = 0.0006f;
    const float StateH = 30f;           // the one-line state under the scan row
    const float MenuDistance = 0.75f;   // metres in front of the eyes
    const float MenuDrop = 0.24f;       // metres below eye level, so it doesn't cover what you look at
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
    // On/off toggles get their tint kept in sync every frame (see SyncToggleTints) instead of set at each call
    // site - a toggle can also turn itself off from elsewhere (the floor plan panel's own Close button, or a
    // scan-source change invalidating the scan overlay/minimap), and a per-frame sync can never go stale the
    // way scattered manual updates did.
    Button dollhouseButton, plansButton, scanOverlayButton, saveButton, exportButton, finishedButton, modeButton, miniMapButton, walkButton, photosButton, routesButton, showRoutesButton, showPhotosButton, fittingsButton;
    XRRayInteractor rightRay;
    float flickCooldown;
    bool menuPlaced;
    // The menu is draggable by its own handle bar, like the Dollhouse/Floor plans/Minimap - once you've grabbed
    // it yourself, the head-follow behaviour above stops for good, the same way those other panels never
    // auto-reposition once placed.
    GameObject menuHandle;
    Transform menuHand;
    bool menuGrabbed, menuManuallyPlaced;
    Vector3 menuGrabOff;
    Quaternion menuGrabRotOff;

    async void Start()
    {
        exporter ??= FindAnyObjectByType<MRUKExporter>();
        dollhouse ??= FindAnyObjectByType<DollHouseVisualizer>() ?? gameObject.AddComponent<DollHouseVisualizer>();
        dollhouse.uiLog = this;
        scanOverlay ??= FindAnyObjectByType<LiveScanOverlay>() ?? gameObject.AddComponent<LiveScanOverlay>();
        scanOverlay.uiLog = this;
        miniMap ??= FindAnyObjectByType<MiniMapPanel>() ?? gameObject.AddComponent<MiniMapPanel>();
        miniMap.uiLog = this;
        walk ??= FindAnyObjectByType<WalkThroughMode>() ?? gameObject.AddComponent<WalkThroughMode>();
        walk.uiLog = this;
        editMode ??= FindAnyObjectByType<EditModeController>() ?? gameObject.AddComponent<EditModeController>();
        editMode.uiLog = this;
        editMode.exporter = exporter;
        var doorTool = FindAnyObjectByType<DoorEditTool>() ?? gameObject.AddComponent<DoorEditTool>();
        doorTool.uiLog = this; doorTool.editMode = editMode; doorTool.walk = walk; doorTool.dollhouse = dollhouse;
        var stairTool = FindAnyObjectByType<StairEditTool>() ?? gameObject.AddComponent<StairEditTool>();
        stairTool.uiLog = this; stairTool.editMode = editMode; stairTool.walk = walk; stairTool.dollhouse = dollhouse;
        var holeTool = FindAnyObjectByType<HoleEditTool>() ?? gameObject.AddComponent<HoleEditTool>();
        holeTool.uiLog = this; holeTool.editMode = editMode; holeTool.walk = walk; holeTool.dollhouse = dollhouse;
        // Wall photos over passthrough (cable chases before plastering), poses tied to the live scan's rooms.
        var photoTool = FindAnyObjectByType<WallPhotoCapture>() ?? gameObject.AddComponent<WallPhotoCapture>();
        photoTool.uiLog = this; photoTool.editMode = editMode; photoTool.exporter = exporter; photoTool.walk = walk;
        // Installation routes (cables, water, waste, heating) traced on the real walls, house-wide (RouteStore).
        var routeTool = FindAnyObjectByType<RouteEditTool>() ?? gameObject.AddComponent<RouteEditTool>();
        routeTool.uiLog = this; routeTool.editMode = editMode; routeTool.walk = walk; routeTool.exporter = exporter;
        // Routes and the cable photos drawn into passthrough / walk-through / Dollhouse (each switchable).
        var overlays = FindAnyObjectByType<WallOverlays>() ?? gameObject.AddComponent<WallOverlays>();
        overlays.walk = walk; overlays.dollhouse = dollhouse; overlays.exporter = exporter;
        // Doorways walled up in edit mode are left out of every built model (walk, Dollhouse, minimap, export).
        XRModelFactory.RemovedOpening = DoorCatalog.IsWalledUp;
        var quality = FindAnyObjectByType<RenderQuality>() ?? gameObject.AddComponent<RenderQuality>();
        quality.uiLog = this;
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

        // A dedicated drag handle strip at the very top - grip while pointing at THIS bar, not just anywhere on
        // the panel, moves the whole menu, so it can't be dragged by accident while clicking a button just
        // below it. XRSimpleInteractable registers it with the XR Interaction Toolkit purely so the ray hovers
        // it as a valid target (see LimitRaysToRightHand's colour gradients) - the actual drag is still the
        // same custom grip+raycast approach as the Dollhouse/Floor plans/Minimap, wired up in Update().
        var handle = XRUi.CreatePanel(root, "Handle", XRUi.ButtonToggleColor, 0, 0, CanvasWidth, HeaderH);
        XRUi.CreateText(root, "HandleLabel", "≡ drag to move • B to close", 20, TextAlignmentOptions.Center, 0, 0, CanvasWidth, HeaderH, XRUi.MutedText);
        var handleRt = (RectTransform)handle.transform;
        var handleCol = handle.gameObject.AddComponent<BoxCollider>();
        handleCol.center = handleRt.rect.center;
        handleCol.size = new Vector3(handleRt.rect.width, handleRt.rect.height, 20f);
        handle.gameObject.AddComponent<XRSimpleInteractable>();
        menuHandle = handle.gameObject;

        XRUi.CreateText(root, "Title", "XR House Export", 34, TextAlignmentOptions.MidlineLeft, 24, 12 + HeaderH, 340, 44, XRUi.TextColor, FontStyles.Bold);
        statusText = XRUi.CreateText(root, "Status", "", 26, TextAlignmentOptions.MidlineRight, 340, 12 + HeaderH, 236, 44, XRUi.AccentText);

        XRUi.CreatePanel(root, "ProgressBack", new Color(1, 1, 1, 0.12f), 24, 62 + HeaderH, 552, 8);
        progressFill = XRUi.CreatePanel(root, "ProgressFill", XRUi.AccentText, 24, 62 + HeaderH, 552, 8).rectTransform;
        SetProgress(0);

        // On/off toggles (Dollhouse, Floor plans, Scan overlay, Minimap) all live in the left column, tinted
        // XRUi.ButtonToggleColor so they read as toggles even before ever being turned on; the right column is
        // plain one-shot actions. SyncToggleTints (in Update) overrides a toggle's tint to ButtonOnColor while
        // it's actually on.
        const float left = 24, right = 308, w = 268, h = 78, gap = 10, top = 84 + HeaderH;
        // What you work on, always at the top: the scan source (live or a saved one). Clicking the name reloads it.
        XRUi.CreateButton(root, "<", left, top, 80, h, () => OnCycleSource(-1), 40);
        scanLabelButton = XRUi.CreateButton(root, MRUKExporter.LiveSource, left + 90, top, 372, h, OnLoadSource, 28);
        XRUi.CreateButton(root, ">", left + 472, top, 80, h, () => OnCycleSource(1), 40);

        // One line, always visible: is this scan lined up with the place you stand in, and is anything not exported.
        stateText = XRUi.CreateText(root, "State", "", 19, TextAlignmentOptions.MidlineLeft, left + 4, top + h + 4, 548, StateH - 2, XRUi.MutedText);
        stateText.richText = true;

        // Four tabs by what you are doing: look at the model, choose what is drawn on the walls, work with a tool, data.
        float tabY = top + h + gap + StateH, tabH = 60, tabW = (552f - 3 * gap) / 4f;
        string[] tabNames = { "View", "Layers", "Tools", "Data" };
        tabPages = new GameObject[tabNames.Length]; tabButtons = new Button[tabNames.Length];
        for (int i = 0; i < tabNames.Length; i++)
        {
            int k = i;
            tabButtons[i] = XRUi.CreateButton(root, tabNames[i], left + i * (tabW + gap), tabY, tabW, tabH, () => ShowTab(k), 25);
            var page = new GameObject("Tab_" + tabNames[i], typeof(RectTransform));
            page.layer = root.gameObject.layer;
            page.transform.SetParent(root, false);
            XRUi.Place((RectTransform)page.transform, 0, 0, CanvasWidth, CanvasHeight);
            tabPages[i] = page;
        }
        float y0 = tabY + tabH + gap;
        float Row(int r) => y0 + r * (h + gap);
        Transform view = tabPages[0].transform, layers = tabPages[1].transform, tools = tabPages[2].transform, data = tabPages[3].transform;

        // View: the house as a model. Left/right pairs; every on/off button is tinted while on.
        walkButton = XRUi.CreateButton(view, "Walk", left, Row(0), w, h, OnToggleWalk);
        dollhouseButton = XRUi.CreateButton(view, "Dollhouse", right, Row(0), w, h, OnToggleHouseView);
        plansButton = XRUi.CreateButton(view, "Floor plans", left, Row(1), w, h, OnShowPlans);
        miniMapButton = XRUi.CreateButton(view, "Minimap", right, Row(1), w, h, OnToggleMiniMap);
        XRUi.CreateButton(view, "Next floor", left, Row(2), w, h, OnNextFloor);
        modeButton = XRUi.CreateButton(view, "", right, Row(2), w, h, OnCycleDollhouseMode, 22);
        finishedButton = XRUi.CreateButton(view, "", left, Row(3), 552, h, OnToggleFinished, 24);

        // Layers: what is drawn onto the walls. Routes, photos and zones show wherever the house is - the real walls (when
        // this scan is of the place you stand in), the walk-through and the Dollhouse; the last two need the real walls.
        layersText = XRUi.CreateText(layers, "Where", "", 18, TextAlignmentOptions.TopLeft, left, y0 - 2, 552, 58, XRUi.MutedText);
        layersText.richText = true;
        float L(int r) => y0 + 60 + r * (h + gap);
        showRoutesButton = XRUi.CreateButton(layers, "Routes", left, L(0), w, h, () => WallOverlays.ShowRoutes = !WallOverlays.ShowRoutes);
        showPhotosButton = XRUi.CreateButton(layers, "Photos", right, L(0), w, h, () => WallOverlays.ShowPhotos = !WallOverlays.ShowPhotos);
        zonesButton = XRUi.CreateButton(layers, "No-drill zones", left, L(1), w, h, () =>
        {
            WallOverlays.ShowZones = !WallOverlays.ShowZones;
            if (WallOverlays.ShowZones && !WallOverlays.ShowRoutes) WallOverlays.ShowRoutes = true;
            AddLog(WallOverlays.ShowZones ? $"No-drill zones: {WallOverlays.ZoneMargin * 100:0} cm either side of every route and box." : "No-drill zones off.");
        });
        fittingsButton = XRUi.CreateButton(layers, "Fittings + niches\n<size=70%>real walls only</size>", right, L(1), w, h, OnToggleFittings, 24);
        scanOverlayButton = XRUi.CreateButton(layers, "Scan outline\n<size=70%>real walls only</size>", left, L(2), w, h, OnToggleScanOverlay, 24);
        siteText = XRUi.CreateText(layers, "Hint", "", 17, TextAlignmentOptions.MidlineLeft, right + 6, L(2), w - 6, h, XRUi.MutedText);

        // Tools: each one straight away (edit mode comes on with it; its bar has Undo/Redo). Again = leave the tool.
        toolButtons.Clear();
        void ToolButton(EditModeController.Tool t, string label, float x, float y)
        {
            var btn = XRUi.CreateButton(tools, label, x, y, w, h, () => OnTool(t), 24);
            toolButtons[t] = btn;
        }
        ToolButton(EditModeController.Tool.Routes, "Trace routes", left, Row(0));
        ToolButton(EditModeController.Tool.Photos, "Wall photos", right, Row(0));
        ToolButton(EditModeController.Tool.Doors, "Doors + windows", left, Row(1));
        ToolButton(EditModeController.Tool.Stairs, "Stairs", right, Row(1));
        ToolButton(EditModeController.Tool.Holes, "Floor opening", left, Row(2));
        ToolButton(EditModeController.Tool.Furniture, "Move furniture", right, Row(2));
        ToolButton(EditModeController.Tool.Measure, "Measure", left, Row(3));
        ToolButton(EditModeController.Tool.Sun, "Sun", right, Row(3));
        routesButton = toolButtons[EditModeController.Tool.Routes];
        photosButton = toolButtons[EditModeController.Tool.Photos];

        // Data: save / export; the destructive ones apart at the bottom of the tab.
        saveButton = XRUi.CreateButton(data, "Save live scan", left, Row(0), w, h, OnSaveScan);
        exportButton = XRUi.CreateButton(data, "Export", right, Row(0), w, h, OnExportAll);
        pendingText = XRUi.CreateText(data, "PendingHint", "", 19, TextAlignmentOptions.Top, left, Row(1) + 6, 552, h, XRUi.MutedText);
        cleanupButton = XRUi.CreateButton(data, "Clean up old scans...", left, Row(2), 552, h, OnCleanupScans, 24);
        var deleteScanButton = XRUi.CreateButton(data, "Delete scan", left, Row(3), w, h, OnDeleteScan);
        XRUi.SetTint(deleteScanButton, XRUi.ButtonDangerColor);
        var deleteExportsButton = XRUi.CreateButton(data, "Delete exports", right, Row(3), w, h, OnDeleteExports);
        XRUi.SetTint(deleteExportsButton, XRUi.ButtonDangerColor);

        foreach (var b in new[] { walkButton, dollhouseButton, plansButton, miniMapButton, showRoutesButton, showPhotosButton, fittingsButton, scanOverlayButton, zonesButton })
            XRUi.SetTint(b, XRUi.ButtonToggleColor);
        foreach (var b in toolButtons.Values) XRUi.SetTint(b, XRUi.ButtonToggleColor);

        float logTop = Row(4) + 2;
        logText = XRUi.CreateText(root, "Log", "", 22, TextAlignmentOptions.TopLeft, 24, logTop, 552, CanvasHeight - logTop - 10, XRUi.MutedText);
        logText.overflowMode = TextOverflowModes.Truncate;

        ShowTab(Mathf.Clamp(PlayerPrefs.GetInt(TabPref, 0), 0, tabPages.Length - 1));
    }

    const string TabPref = "Menu.Tab4"; // 4 tabs now - the old index meant something else
    GameObject[] tabPages;
    Button[] tabButtons;
    int currentTab;

    /// <summary>Shows one tab of the menu (remembered for the next start).</summary>
    public void ShowTab(int i)
    {
        if (tabPages == null) return;
        currentTab = i;
        for (int k = 0; k < tabPages.Length; k++)
        {
            tabPages[k].SetActive(k == i);
            XRUi.SetTint(tabButtons[k], k == i ? XRUi.AccentDark : XRUi.ButtonColor);
        }
        PlayerPrefs.SetInt(TabPref, i);
        PlayerPrefs.Save();
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
        // Once you've dragged the menu by its own handle, it stays exactly where you put it - same as the
        // Dollhouse/Floor plans/Minimap never auto-repositioning after being placed.
        menuCanvas.worldCamera = cam;
        // Placed in front of you when it opens (B), then it stays put like every other panel - it no longer turns
        // with your head. Walk away from it and it comes back in front of you.
        if (!menuCanvas.gameObject.activeSelf) return;
        // The first seconds after start the head pose is still settling (tracking origin, recenter): keep it in front.
        bool settling = Time.realtimeSinceStartup < 6f && !menuManuallyPlaced;
        if (!menuPlaced || settling || (!menuManuallyPlaced && XRUi.LeftBehind(menuCanvas.transform, cam.transform)))
        {
            XRUi.PlaceInFront(menuCanvas.transform, cam.transform, MenuDistance, MenuDrop);
            menuPlaced = true;
        }
    }

    /// <summary>The scene has duplicate ray interactors on both hands; keep exactly one, on the right hand, so the left hand never points at its own menu.</summary>
    void LimitRaysToRightHand()
    {
        var rig = FindFirstObjectByType<OVRCameraRig>();
        if (rig == null || rig.rightHandAnchor == null) return;

        foreach (var ray in FindObjectsByType<XRRayInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            bool keep = rightRay == null && ray.transform.IsChildOf(rig.rightHandAnchor);
            if (keep) { rightRay = ray; StyleRay(ray); }
            ray.gameObject.SetActive(keep);
        }
    }

    /// <summary>Thinner, round-capped and blue by default, green while pointing at anything the XR Interaction
    /// Toolkit considers a valid target - every UI button already counts automatically, and so does anything
    /// with an XRSimpleInteractable (the menu/floor plan drag handles, the Dollhouse, the Minimap).</summary>
    static void StyleRay(XRRayInteractor ray)
    {
        var lr = ray.GetComponent<LineRenderer>();
        if (lr != null) { lr.numCapVertices = 8; lr.numCornerVertices = 4; }

        var visual = ray.GetComponent<XRInteractorLineVisual>();
        if (visual == null) return;
        visual.lineWidth = 0.004f;
        visual.setLineColorGradient = true;
        var blue = new Color(0.30f, 0.62f, 1f);
        var green = new Color(0.30f, 0.95f, 0.45f);
        visual.invalidColorGradient = SolidGradient(blue);
        visual.blockedColorGradient = SolidGradient(blue);
        visual.validColorGradient = SolidGradient(green);
    }

    static Gradient SolidGradient(Color c)
    {
        var g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(c, 0f), new GradientColorKey(c, 1f) },
            new[] { new GradientAlphaKey(c.a, 0f), new GradientAlphaKey(c.a, 1f) });
        return g;
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
        // The wrist menu used to hide itself entirely while floor plans were open - now the plan panel is its
        // own separate, draggable object (like the Dollhouse), so the menu stays up and usable alongside it.
        bool plansOpen = plansPanel != null && plansPanel.IsOpen;
        SyncToggleTints(plansOpen);
        SyncLabels();
        WatchScene();
        TrackUiUnderRay();
        ReturnMenuAfterTool();

        // Right trigger clicks whatever button the ray points at. The XR UI module only handles hover here
        // (the scene never enables the Input System actions that would deliver "press"), and the rest of the
        // app already reads the controllers through OVRInput.
        // Not on a button: in edit mode the trigger belongs to the active edit tool; otherwise, pointing at the
        // Dollhouse floor, it walks into the model right there.
        if (OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch) && !ClickUnderRay())
        {
            if (editMode != null && editMode.IsOn) editMode.OnTrigger();
            else if (walk != null && walk.IsOn) walk.TryToggleDoor(); // open/close the door leaf you point at
            else TryEnterWalkFromDollhouse();
        }

        bool walking = walk != null && walk.IsOn;

        // Right hand: B = open/close the whole menu, A = dollhouse on/off, stick left/right = dollhouse mode
        // (only while it's on and floor plans are closed - otherwise the stick pages floor plans instead).
        // Export used to be on B; reaching for the menu to use anything else on it anyway made a dedicated
        // shortcut for Export, at the cost of one for closing the menu, not worth it - Export is still one
        // click away on the menu itself.
        if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch)) OnToggleMenu();
        // The left menu button (three lines) too - what it does in most Quest apps (it used to open Meta's Immersive
        // Debugger, now switched off in Resources/ImmersiveDebuggerSettings).
        if (OVRInput.GetDown(OVRInput.Button.Start)) OnToggleMenu();
        // While walking, A leaves the walk-through (back to the Dollhouse, if it was up) instead.
        if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch)) { if (walking) OnToggleWalk(); else OnToggleHouseView(); }
        HandleMenuDrag();

        // Left hand: Y tap = save log, Y hold (1 s) = edit mode on/off, X = routes / photos on walls (cycles; Save scan
        // is in the menu's Data tab - it used to be on X and made new scan copies by accident), stick click = floor plans
        // (seated/standing while walking). The scan source changes only with the menu's < > - a stick flick used to switch
        // it by accident (e.g. right after leaving the walk-through with the stick still pushed).
        if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch)) { yDownTime = Time.time; yHoldFired = false; }
        if (OVRInput.Get(OVRInput.Button.Two, OVRInput.Controller.LTouch) && !yHoldFired && Time.time - yDownTime >= EditHoldSeconds)
        {
            yHoldFired = true;
            editMode?.Toggle();
        }
        if (OVRInput.GetUp(OVRInput.Button.Two, OVRInput.Controller.LTouch) && !yHoldFired) SaveLogToFile();
        if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.LTouch)) CycleWallLayers();
        if (OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.LTouch)) { if (walking) walk.ToggleSeated(); else OnShowPlans(); }

        // Both sticks belong to the walk-through (move / turn / teleport) - no flick shortcuts while walking.
        if (walking || StairEditTool.StickCapture) return;

        flickCooldown -= Time.deltaTime;
        if (flickCooldown > 0f) return;

        Vector2 right = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);

        if (plansOpen && !(dollhouse != null && dollhouse.IsDragging) && !plansPanel.IsDragging && Mathf.Abs(right.x) > FlickThreshold)
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
    }

    /// <summary>Keeps every on/off toggle button tinted to match what's actually on screen right now, regardless
    /// of how it got that way (its own button, a shortcut, the floor plan panel's own Close button, or a scan
    /// source change quietly turning the scan overlay/minimap back off) - so the indicator can never lie.</summary>
    void SyncToggleTints(bool plansOpen)
    {
        if (plansButton != null) XRUi.SetTint(plansButton, plansOpen ? XRUi.ButtonOnColor : XRUi.ButtonToggleColor);
        if (dollhouseButton != null) XRUi.SetTint(dollhouseButton, dollhouse != null && dollhouse.IsOn ? XRUi.ButtonOnColor : XRUi.ButtonToggleColor);
        // the real-wall-only layers are greyed out while the loaded scan is of another place
        bool here = exporter != null && exporter.WorldAligned && !MRUKExporter.Loading;
        if (scanOverlayButton != null) XRUi.SetTint(scanOverlayButton, scanOverlay != null && scanOverlay.IsOn ? XRUi.ButtonOnColor : here ? XRUi.ButtonToggleColor : XRUi.ButtonDisabled);
        SyncPending();
        if (miniMapButton != null) XRUi.SetTint(miniMapButton, miniMap != null && miniMap.IsOn ? XRUi.ButtonOnColor : XRUi.ButtonToggleColor);
        if (walkButton != null) XRUi.SetTint(walkButton, walk != null && walk.IsOn ? XRUi.ButtonOnColor : XRUi.ButtonToggleColor);
        foreach (var kv in toolButtons)
            XRUi.SetTint(kv.Value, editMode != null && editMode.IsOn && editMode.CurrentTool == kv.Key ? XRUi.ButtonOnColor : XRUi.ButtonToggleColor);
        if (showRoutesButton != null) XRUi.SetTint(showRoutesButton, WallOverlays.ShowRoutes ? XRUi.ButtonOnColor : XRUi.ButtonToggleColor);
        if (showPhotosButton != null) XRUi.SetTint(showPhotosButton, WallOverlays.ShowPhotos ? XRUi.ButtonOnColor : XRUi.ButtonToggleColor);
        if (fittingsButton != null) XRUi.SetTint(fittingsButton, WallOverlays.ShowFittings ? XRUi.ButtonOnColor : here ? XRUi.ButtonToggleColor : XRUi.ButtonDisabled);
        if (zonesButton != null) XRUi.SetTint(zonesButton, WallOverlays.ShowZones ? XRUi.ButtonOnColor : XRUi.ButtonToggleColor);
        if (finishedButton != null)
        {
            bool f = Furniture.Finished;
            XRUi.SetTint(finishedButton, f ? XRUi.ButtonOnColor : XRUi.ButtonToggleColor);
            if (finishedShown != f) { finishedShown = f; XRUi.SetLabel(finishedButton, f ? "Finished floors + ceilings" : "Rough shell (as scanned)"); }
        }
    }

    bool? finishedShown;
    string modeShown, siteShown, stateShown;
    TMP_Text siteText, stateText, layersText;
    string layersShown;
    readonly Dictionary<EditModeController.Tool, Button> toolButtons = new Dictionary<EditModeController.Tool, Button>();
    Button zonesButton, cleanupButton;

    /// <summary>Labels that show state: the Dollhouse mode, and where the Layers tab draws (real walls or only the models).</summary>
    void SyncLabels()
    {
        if (modeButton != null && dollhouse != null)
        {
            string m = "Dollhouse: " + dollhouse.ModeLabel;
            if (m != modeShown) { modeShown = m; XRUi.SetLabel(modeButton, m); }
        }
        if (siteText != null)
        {
            const string t = "Point at a route, box or photo to see what it is. Left X = routes / photos.";
            if (t != siteShown) { siteShown = t; siteText.text = t; }
        }
        if (layersText != null && exporter != null)
        {
            string t = MRUKExporter.Loading ? "Loading the scan..."
                : exporter.WorldAligned
                    ? "<color=#7fe07f>On the real walls here</color>, in Walk and in the Dollhouse."
                    : "<color=#ffa040>This scan is of another place</color> - layers show in Walk and the Dollhouse only. Real-wall layers need the scan of the place you stand in (pick it with < >).";
            if (t != layersShown) { layersShown = t; layersText.text = t; }
        }
        if (stateText != null && exporter != null)
        {
            bool live = exporter.SelectedSource == MRUKExporter.LiveSource;
            string place = MRUKExporter.Loading ? "loading..."
                : exporter.ActiveSource != exporter.SelectedSource ? "<color=#ffa040>not loaded yet</color>"
                : exporter.WorldAligned ? "<color=#7fe07f>lined up here</color>"
                : "<color=#ffa040>another place (not on these walls)</color>";
            string save = live && PendingChanges.LiveUnsaved ? "  |  <color=#ffa040>live scan not saved</color>" : "";
            string exp = PendingChanges.ExportPending ? $"  |  <color=#ffa040>not exported: {PendingChanges.ExportWhat}</color>" : "  |  exported";
            string t = (live ? "Live scan: " : "Saved scan: ") + place + save + exp;
            if (t != stateShown) { stateShown = t; stateText.text = t; }
        }
    }

    /// <summary>Finished floors (build-up) + suspended ceilings on/off - off is the rough shell, for tracing pipes in the
    /// floor and cables above the ceiling. Rebuilds whatever shows the model.</summary>
    async void OnToggleFinished()
    {
        Furniture.Finished = !Furniture.Finished;
        AddLog(Furniture.Finished ? "Model: finished floors + suspended ceilings" : "Model: rough shell as scanned (pipes in the floor, cables above the ceiling)");
        await RefreshViews();
    }

    TMP_Text pendingText;
    float pendingAt;
    string lastPendingKey;
    /// <summary>The Data tab: "Save live scan" only means something on the live scan (orange when it has a room no
    /// saved scan has); a saved scan saves its edits by itself. Export turns orange after any change since the last
    /// export. The tab shows (!) while either is waiting.</summary>
    void SyncPending()
    {
        if (saveButton == null || Time.unscaledTime < pendingAt) return;
        pendingAt = Time.unscaledTime + 0.5f;
        bool live = exporter == null || exporter.SelectedSource == MRUKExporter.LiveSource;
        bool save = live && PendingChanges.LiveUnsaved, export = PendingChanges.ExportPending;
        string what = PendingChanges.ExportWhat;
        string key = $"{live}|{save}|{export}|{what}";
        if (key == lastPendingKey) return;
        lastPendingKey = key;
        XRUi.SetTint(saveButton, save ? XRUi.ButtonWarnColor : live ? XRUi.ButtonColor : XRUi.ButtonDisabled);
        XRUi.SetLabel(saveButton, live ? (save ? "Save live scan  (new!)" : "Save live scan") : "Saved automatically");
        XRUi.SetTint(exportButton, export ? XRUi.ButtonWarnColor : XRUi.ButtonColor);
        XRUi.SetLabel(exportButton, export ? "Export  (changed)" : "Export");
        string s1 = live
            ? (save ? "Quest has rooms no saved scan has - Save live scan keeps them. " : "The live scan is saved. ")
            : "Edits, routes and names of this scan save by themselves. ";
        pendingText.text = s1 + (export ? $"Not exported yet: {what}." : "Exported.");
        if (tabButtons != null) XRUi.SetLabel(tabButtons[3], save || export ? "Data <color=#ffa040>(!)</color>" : "Data");
    }

    /// <summary>Fittings + niches over passthrough: needs the live scan (loads it if a saved one is active).</summary>
    async void OnToggleFittings()
    {
        WallOverlays.ShowFittings = !WallOverlays.ShowFittings;
        if (!WallOverlays.ShowFittings) { AddLog("Fittings off."); return; }
        if (walk != null && walk.IsOn) OnToggleWalk(); // they are drawn on the real walls
        if (exporter != null && !exporter.WorldAligned) await exporter.EnsureLiveLoaded(this);
        await Task.Delay(500);
        AddLog("Fittings + niches: " + WallOverlays.FittingsInfo);
    }

    /// <summary>X: routes + photos on walls -> routes only -> nothing -> routes + photos... (works while walking too).</summary>
    void CycleWallLayers()
    {
        bool r = WallOverlays.ShowRoutes, p = WallOverlays.ShowPhotos;
        if (r && p) { WallOverlays.ShowPhotos = false; AddLog("Walls: routes"); }
        else if (r) { WallOverlays.ShowRoutes = false; AddLog("Walls: nothing"); }
        else { WallOverlays.ShowRoutes = true; WallOverlays.ShowPhotos = true; AddLog("Walls: routes + photos"); }
    }

    bool RoutesOn => editMode != null && editMode.IsOn && editMode.CurrentTool == EditModeController.Tool.Routes;

    // Wall photos / Trace routes from the menu hide the menu (a clear view); when that tool is left - its Done, the
    // edit toolbar, the menu button again - the menu comes back, so nothing is ever left with no menu at all.
    bool menuHiddenForTool;
    void HideMenuForTool()
    {
        if (menuCanvas != null && menuCanvas.gameObject.activeSelf) { OnToggleMenu(); menuHiddenForTool = true; }
    }
    void ReturnMenuAfterTool()
    {
        if (!menuHiddenForTool || RoutesOn || PhotosOn) return;
        menuHiddenForTool = false;
        if (menuCanvas != null && !menuCanvas.gameObject.activeSelf) OnToggleMenu();
    }

    /// <summary>Main-menu shortcut to tracing routes (edit mode + Routes tool); hides the menu. Again = leave.</summary>
    public void OnTraceRoutes()
    {
        if (editMode == null) return;
        if (RoutesOn) { editMode.Toggle(); return; }
        editMode.UseTool(EditModeController.Tool.Routes);
        HideMenuForTool();
    }

    /// <summary>Tools tab: the tool on (edit mode comes on with it), pressed again off. Routes and wall photos hide the
    /// menu for a clear view (it comes back when the tool is left).</summary>
    void OnTool(EditModeController.Tool t)
    {
        if (editMode == null) return;
        if (editMode.IsOn && editMode.CurrentTool == t) { editMode.LeaveTool(); return; }
        editMode.UseTool(t);
        if (t == EditModeController.Tool.Routes || t == EditModeController.Tool.Photos) HideMenuForTool();
    }

    bool PhotosOn => editMode != null && editMode.IsOn && editMode.CurrentTool == EditModeController.Tool.Photos;

    /// <summary>Main-menu shortcut to the wall photos (edit mode + Photos tool); hides the menu so the view is clear
    /// (B brings it back). Pressed again it leaves edit mode.</summary>
    public void OnWallPhotos()
    {
        if (editMode == null) return;
        if (PhotosOn) { editMode.Toggle(); return; }
        editMode.UseTool(EditModeController.Tool.Photos);
        HideMenuForTool();
    }

    /// <summary>Opens/closes the whole wrist menu (bound to B - see the comment where it's read).</summary>
    public void OnToggleMenu()
    {
        if (menuCanvas == null) return;
        bool open = !menuCanvas.gameObject.activeSelf;
        menuCanvas.gameObject.SetActive(open);
        // Every time it opens it appears in front of you again (also after you dragged it somewhere).
        if (open) { menuPlaced = false; menuManuallyPlaced = false; }
    }

    /// <summary>Grip while pointing at the menu's own handle bar drags the whole menu, exactly like the
    /// Dollhouse/Floor plans/Minimap - the first successful grab also permanently stops LateUpdate's
    /// head-follow behaviour (see menuManuallyPlaced).</summary>
    void HandleMenuDrag()
    {
        if (menuCanvas == null || !menuCanvas.gameObject.activeSelf || menuHandle == null) return;
        if (!menuHand)
        {
            var rig = FindFirstObjectByType<OVRCameraRig>();
            menuHand = rig ? rig.rightHandAnchor : null;
            if (!menuHand) return;
        }

        bool grip = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch);
        if (!menuGrabbed)
        {
            if (GrabLock.GripPressed && Physics.Raycast(menuHand.position, menuHand.forward, out RaycastHit hit) && hit.collider.gameObject == menuHandle && GrabLock.TryTake(this))
            {
                menuGrabbed = true;
                menuManuallyPlaced = true;
                OVRInput.SetControllerVibration(0.1f, 0.1f, OVRInput.Controller.RTouch); Invoke(nameof(StopMenuVib), 0.05f);
                menuGrabOff = menuHand.InverseTransformPoint(menuCanvas.transform.position);
                menuGrabRotOff = Quaternion.Inverse(menuHand.rotation) * menuCanvas.transform.rotation;
            }
        }
        else
        {
            if (!grip) { menuGrabbed = false; GrabLock.Release(this); return; }
            menuCanvas.transform.position = menuHand.TransformPoint(menuGrabOff);
            menuCanvas.transform.rotation = menuHand.rotation * menuGrabRotOff;
        }
    }
    void StopMenuVib() => OVRInput.SetControllerVibration(0, 0, OVRInput.Controller.RTouch);

    /// <summary>Clicks the UI button under the ray. True if the ray was on any UI at all (so the trigger press is
    /// "used up" and must not also act on something behind the panel).</summary>
    bool ClickUnderRay()
    {
        if (rightRay == null || !rightRay.TryGetCurrentUIRaycastResult(out RaycastResult hit) || hit.gameObject == null) return false;
        var button = hit.gameObject.GetComponentInParent<Button>();
        var canvas = hit.gameObject.GetComponentInParent<Canvas>();
        // every click in the log: "the button didn't work" can then be told apart from "the ray wasn't on it"
        string label = button != null ? (button.GetComponentInChildren<TMP_Text>()?.text ?? button.name) : hit.gameObject.name;
        Debug.Log($"[UI] click '{label}' on {(canvas ? canvas.name : "?")}{(button == null ? " (not a button)" : !button.IsInteractable() ? " (disabled)" : "")}");
        if (button != null && button.IsInteractable()) button.onClick.Invoke();
        return true;
    }

    static GameObject uiUnderRay;
    /// <summary>True while the right ray is on UI under t (a panel must not move away from under it).</summary>
    public static bool PointingAt(Transform t) => uiUnderRay != null && t != null && uiUnderRay.transform.IsChildOf(t);
    /// <summary>True while the right ray is on any panel/button.</summary>
    public static bool PointingAtAnyUI => uiUnderRay != null;

    void TrackUiUnderRay()
    {
        uiUnderRay = rightRay != null && rightRay.TryGetCurrentUIRaycastResult(out RaycastResult hit) ? hit.gameObject : null;
    }

    void OnNextFloor()
    {
        if (walk == null || !walk.IsOn)
        {
            // Next floor moves you between storeys of the 1:1 model - say so, and whether there is another storey at all.
            int n = 0;
#if META_XR_SDK_INSTALLED
            if (Meta.XR.MRUtilityKit.MRUK.Instance != null) n = WalkThroughMode.CountFloorLevels(MRUKDataProcessor.GetValidRooms(Meta.XR.MRUtilityKit.MRUK.Instance));
#endif
            string m = n == 1 ? "Next floor: this scan has only one floor."
                     : n > 1 ? $"Next floor: this scan has {n} floors - it moves you between them in the walk-through, start Walk first."
                     : "Next floor: moves you between floors in the walk-through - load a scan and start Walk first.";
            AddLog(m); SetStatus(n == 1 ? "One floor only" : "Start Walk first");
            return;
        }
        string s = walk.NextFloor() ?? "Next floor: no position yet - try again";
        AddLog(s); SetStatus(s);
        Debug.Log($"[XRMenu] Next floor: {s}");
    }

    async void TryEnterWalkFromDollhouse()
    {
        if (walk == null || walk.IsOn || dollhouse == null || !dollhouse.IsOn || dollhouse.IsDragging) return;
        if (!dollhouse.TryGetPointedFloor(out Vector3 point)) return;
        await Busy("Building...");
        if (await walk.Enter(point, dollhouse.FacingYaw))
        {
            dollhouse.SetHidden(true);
            SetStatus("Walk");
        }
    }

    /// <summary>Walk on/off. Entered from here (not from a Dollhouse point) it uses the scan's own real-world
    /// alignment when you're standing inside the scanned area, else the middle of the largest room.</summary>
    public async void OnToggleWalk()
    {
        if (walk == null) return;
        if (walk.IsOn)
        {
            walk.Exit();
            dollhouse?.SetHidden(false);
            SetStatus(dollhouse != null && dollhouse.IsOn ? $"Dollhouse: {dollhouse.ModeLabel}" : "Ready");
            return;
        }
        if (!RequireExporter()) return;
        await Busy("Loading...");
        if (!await exporter.EnsureSelectedSourceLoaded(this)) { SetStatus("ERROR"); return; }
        if (!exporter.HasValidRooms())
        {
            AddLog("<color=red>No rooms in this scan.</color>");
            SetStatus("No rooms");
            return;
        }
        await Busy("Building...");
        if (await walk.Enter(null, null))
        {
            dollhouse?.SetHidden(true);
            SetStatus("Walk");
        }
        else SetStatus("ERROR");
    }

    /// <summary>For other components that change the selected scan (e.g. edit mode saving the live scan).</summary>
    public void RefreshSourceLabel() => RefreshScanLabel();

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

    int cycleVersion;

    /// <summary>&lt; / &gt;: picks the previous/next scan and loads it (quick taps step through the list, only the scan
    /// you stop on is loaded). What is selected is always what is loaded - every view follows it (WatchScene).</summary>
    async void OnCycleSource(int direction)
    {
        if (!RequireExporter()) return;
        // The walk-through model belongs to the scan it was built from - leave it before switching.
        if (walk != null && walk.IsOn) OnToggleWalk();
        exporter.CycleSource(direction, this);
        RefreshScanLabel();
        int my = ++cycleVersion;
        await Task.Delay(700);
        if (my != cycleVersion) return;
        await Busy("Loading...");
        bool loaded = await exporter.EnsureSelectedSourceLoaded(this, forceReload: true) && exporter.HasValidRooms();
        SetStatus(loaded ? "Ready" : "No rooms");
    }

    // ---------- every view follows the loaded scan ----------

    int seenSceneVersion = -1;
    bool refreshing, refreshAgain;

    /// <summary>Whenever a scan load finishes (MRUKExporter.SceneVersion) - switching scans, a tool lining a scan up,
    /// saving, a doorway added - everything showing the model is rebuilt from it: walk-through, Dollhouse, scan
    /// overlay, minimap (WallOverlays does the same by itself). Nothing is rebuilt halfway through a load.</summary>
    void WatchScene()
    {
        if (exporter == null || MRUKExporter.Loading) return;
        int v = MRUKExporter.SceneVersion;
        if (v == seenSceneVersion) return;
        bool first = seenSceneVersion < 0;
        seenSceneVersion = v;
        if (!first) _ = RefreshViews();
    }

    async Task RefreshViews()
    {
        if (refreshing) { refreshAgain = true; return; }
        refreshing = true;
        try
        {
            do
            {
                refreshAgain = false;
                bool loaded = exporter != null && exporter.HasValidRooms();
                if (walk != null && walk.IsOn)
                {
                    // back where you stood, in the rebuilt model
                    bool here = walk.TryGetFeetScan(out var feet, out _);
                    walk.Exit();
                    if (loaded && await walk.Enter(null, null))
                    {
                        dollhouse?.SetHidden(true);
                        if (here) walk.TeleportToScanPoint(feet, feet.y);
                    }
                    else dollhouse?.SetHidden(false);
                }
                if (dollhouse != null && dollhouse.IsOn)
                {
                    if (loaded) dollhouse.RebuildInPlace();
                    else AddLog("<color=orange>No rooms in this scan - the Dollhouse keeps the previous one.</color>");
                }
                if (scanOverlay != null && scanOverlay.IsOn)
                {
                    if (loaded) scanOverlay.RebuildInPlace(); else scanOverlay.ForceOff();
                    if (!scanOverlay.IsOn) AddLog("<color=orange>Scan overlay turned off - no rooms in this scan.</color>");
                    else if (exporter != null && !exporter.WorldAligned) AddLog($"<color=orange>Scan overlay: '{exporter.SelectedSource}' isn't lined up with this place.</color>");
                }
                if (miniMap != null && miniMap.IsOn)
                {
                    if (loaded) miniMap.RebuildInPlace(); else miniMap.ForceOff();
                }
            } while (refreshAgain);
        }
        finally { refreshing = false; }
    }

    /// <summary>The scan's own file changed (a doorway added): reload it - the views follow (WatchScene), the
    /// walk-through comes back where you stood.</summary>
    public async Task ReloadAfterScanEdit()
    {
        if (!RequireExporter()) return;
        await Busy("Loading...");
        bool ok = await exporter.EnsureSelectedSourceLoaded(this, forceReload: true);
        WatchScene();
        while (refreshing) await Task.Yield();
        SetStatus(ok ? "Ready" : "ERROR");
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
    public async void OnCycleDollhouseMode()
    {
        // While walking, Mode switches which model you walk through (Anchor / Mesh / Raw) instead.
        if (walk != null && walk.IsOn)
        {
            await Busy("Building...");
            string wm = await walk.CycleModel();
            AddLog($"<b>{wm}</b>");
            SetStatus(wm);
            return;
        }
        if (dollhouse == null) return;
        string m = dollhouse.CycleMode();
        AddLog($"Dollhouse mode: <b>{m}</b>");
        if (dollhouse.IsOn) SetStatus($"Dollhouse: {m}");
    }

    /// <summary>Toggles the passthrough overlay that highlights the scanned room(s) - useful while physically
    /// walking the space, unlike the dollhouse which shows a separate scaled-down copy in front of you.</summary>
    public async void OnToggleScanOverlay()
    {
        if (scanOverlay == null) return;
        if (scanOverlay.IsOn) { scanOverlay.ToggleOnOff(); AddLog("Scan overlay OFF"); SetStatus("Ready"); return; }

        if (!RequireExporter()) return;
        await Busy("Loading...");
        if (!await exporter.EnsureSelectedSourceLoaded(this)) { SetStatus("ERROR"); return; }
        if (!exporter.HasValidRooms())
        {
            AddLog("<color=red>No rooms in this scan.</color>");
            SetStatus("No rooms");
            return;
        }
        bool on = scanOverlay.ToggleOnOff();
        AddLog(on ? $"<color=green>Scan overlay ON - '{exporter.SelectedSource}'</color>" : "<color=red>Scan overlay failed</color>");
        if (on && !exporter.WorldAligned) AddLog($"<color=orange>'{exporter.SelectedSource}' isn't lined up with this place - its outlines won't sit on these walls.</color>");
        SetStatus(on ? "Scan overlay ON" : "Ready");
    }

    /// <summary>Toggles the small wrist-anchored minimap with a live position marker.</summary>
    public async void OnToggleMiniMap()
    {
        if (miniMap == null) return;
        if (miniMap.IsOn) { miniMap.ToggleOnOff(); AddLog("Minimap OFF"); SetStatus("Ready"); return; }

        if (!RequireExporter()) return;
        await Busy("Loading...");
        if (!await exporter.EnsureSelectedSourceLoaded(this)) { SetStatus("ERROR"); return; }
        if (!exporter.HasValidRooms())
        {
            AddLog("<color=red>No rooms in this scan.</color>");
            SetStatus("No rooms");
            return;
        }
        bool on = miniMap.ToggleOnOff();
        SetStatus(on ? "Minimap ON" : "Ready");
    }

    public async void OnSaveScan()
    {
        if (!RequireExporter()) return;
        if (exporter.SelectedSource != MRUKExporter.LiveSource)
        {
            // Saving the live scan from here used to load it and make yet another copy - never what was meant.
            AddLog($"'{exporter.SelectedSource}' saves its edits by itself - nothing to save. Export writes the files for the PC. (To save the Quest's live scan, pick Live scan with < > first.)");
            return;
        }
        await Busy("Saving...");
        string name = await exporter.SaveScanToCache(this);
        SetStatus(name != null ? "Scan saved" : "Save failed");
        RefreshScanLabel();
    }

    List<MRUKSceneCache.Redundant> cleanupList;
    float cleanupUntil;

    /// <summary>First press: finds saved scans that a newer saved scan fully covers (same rooms or more) and that hold
    /// nothing of their own (no furniture spec, no edits, no doorway added to their JSON) and lists them. A second press
    /// within 15 s deletes them. Routes, photos and room names are house-wide - they stay.</summary>
    async void OnCleanupScans()
    {
        if (!RequireExporter()) return;
        if (cleanupList != null && Time.unscaledTime < cleanupUntil)
        {
            var list = cleanupList; cleanupList = null;
            XRUi.SetLabel(cleanupButton, "Clean up old scans...");
            XRUi.SetTint(cleanupButton, XRUi.ButtonColor);
            int n = 0;
            string selected = exporter.SelectedSource;
            foreach (var r in list)
            {
                if (MRUKSceneCache.DeleteCachedScan(r.name)) { HouseEditsStore.DeleteFor(r.name); n++; Debug.Log($"[Cleanup] deleted '{r.name}' (covered by '{r.keptIn}')"); }
                else AddLog($"<color=orange>Could not delete '{r.name}'.</color>");
            }
            // the scan you had picked is gone: pick the one that covers it (never the live scan by itself)
            var mine = list.FirstOrDefault(r => r.name == selected);
            if (mine != null) { exporter.Select(mine.keptIn); RefreshScanLabel(); _ = ReloadAfterScanEdit(); }
            AddLog($"<color=green>Deleted {n} old scan(s).</color>");
            SetStatus("Cleaned up");
            return;
        }
        await Busy("Checking...");
        var found = await MRUKSceneCache.FindRedundantAsync();
        if (found.Count == 0) { AddLog("Clean up: no old scan is fully covered by a newer one - nothing to delete."); SetStatus("Ready"); return; }
        cleanupList = found;
        cleanupUntil = Time.unscaledTime + 15f;
        foreach (var r in found) AddLog($"Old: '{r.name}' - all its rooms are in '{r.keptIn}'.");
        XRUi.SetLabel(cleanupButton, $"Delete {found.Count} old scan(s)? Press again");
        XRUi.SetTint(cleanupButton, XRUi.ButtonDangerColor);
        SetStatus("Confirm delete");
        await Task.Delay(15000);
        if (cleanupList == found) { cleanupList = null; XRUi.SetLabel(cleanupButton, "Clean up old scans..."); XRUi.SetTint(cleanupButton, XRUi.ButtonColor); }
    }

    /// <summary>Deletes the currently selected cached scan (a saved sample) from local storage.</summary>
    public void OnDeleteScan()
    {
        if (!RequireExporter()) return;
        bool ok = exporter.DeleteSelectedScan(this);
        SetStatus(ok ? "Scan deleted" : "Ready");
        RefreshScanLabel();
    }

    /// <summary>
    /// Clears every exported session from device storage, freeing up space. Runs off the main thread - a
    /// synchronous recursive delete of potentially many export sessions' worth of files was freezing the whole
    /// app for a long time and, on a single locked/permission-denied file, aborting with nothing removed and no
    /// detail beyond "could not delete" - now every failure is still logged individually and whatever else can
    /// be removed still gets removed.
    /// </summary>
    public async void OnDeleteExports()
    {
        await Busy("Deleting...");
        var (deleted, failed) = await Task.Run(() => MRUKPathUtility.ClearExportRoot());
        if (failed == 0) AddLog($"<color=green>Deleted {deleted} exported file(s).</color>");
        else AddLog($"<color=orange>Deleted {deleted} file(s), {failed} could not be removed (see Unity log for details).</color>");
        SetStatus(failed == 0 ? "Exports deleted" : "Partial delete");
    }

    public async void OnShowPlans()
    {
        if (plansPanel == null) return;
        if (plansPanel.IsOpen) { plansPanel.Hide(); return; }
        if (!RequireExporter()) return;
        await Busy("Loading...");
        if (!await exporter.EnsureSelectedSourceLoaded(this)) { SetStatus("ERROR"); return; }

        var pages = exporter.BuildPlanPages();
        if (pages.exact.Count == 0)
        {
            AddLog("<color=red>No floor plans - the scan has no valid rooms.</color>");
            SetStatus("No rooms");
            return;
        }
        plansPanel.Rebuild = exporter.BuildPlanPages;
        plansPanel.Show(pages, exporter.SelectedSource);
        AddLog($"Floor plans: {pages.exact.Count} sheet(s).");
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

}
