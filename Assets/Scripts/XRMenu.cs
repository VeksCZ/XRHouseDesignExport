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
/// happens to be in the scene. Every action works on the scan picked in the scan line at the top (it opens the list of
/// scans - the live device scan and the saved ones - each with its bin).
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

    // Canvas units; the canvas is scaled to CanvasScale metres per unit (640 units -> 38 cm at 0.75 m from the eyes).
    const float CanvasWidth = 640f, CanvasHeight = 778f, CanvasScale = 0.0006f;
    const float Pad = 28f, ContentW = CanvasWidth - 2 * Pad;
    const float MenuDistance = 0.75f;   // metres in front of the eyes
    const float MenuDrop = 0.24f;       // metres below eye level, so it doesn't cover what you look at
    const int VisibleLogLines = 5;
    const float FlickThreshold = 0.6f;
    const float FlickCooldown = 0.35f;

    readonly StringBuilder logHistory = new StringBuilder();
    readonly Queue<string> visibleLog = new Queue<string>();
    FloorPlanPanel plansPanel;
    Canvas menuCanvas;
    PanelDrag menuDrag;
    TMP_Text statusText, logText;
    RectTransform progressFill;
    GameObject progressBack;
    Button scanLabelButton;
    // Every button that shows a state is synced every frame (SyncToggleTints) from what is really on - a toggle can
    // also change from elsewhere (a panel's own Close, a shortcut, a scan change), and a per-frame sync never goes stale.
    Button dollhouseButton, plansButton, scanOverlayButton, saveButton, exportButton, roughButton, finishedButton, modeButton, miniMapButton, walkButton, showRoutesButton, showPhotosButton, fittingsButton, photoMarkersButton;
    XRRayInteractor rightRay;
    float flickCooldown;
    bool menuPlaced;

    /// <summary>Tests (desktop -test menu): build only the menu, nothing it drives.</summary>
    public static bool Preview;

    async void Start()
    {
        if (Preview) { BuildMenu(); return; }
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
        this.photoTool = photoTool;
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
        // The selected scan (the live one at start) is loaded right away - nothing has to be opened first for the
        // menu's state line, the layers and the tools to have a scan to work with.
        var saved = MRUKSceneCache.ListCachedScans();
        if (MRUKSceneCache.LastListError != null) AddLog($"<color=red>Saved scans can't be read: {MRUKSceneCache.LastListError}</color>");
        else AddLog($"{saved.Count} saved scan(s) - pick one in the list (top).");
        if (exporter != null && !MRUKExporter.Loading)
        {
            await Busy("Loading...");
            bool ok = await exporter.EnsureSelectedSourceLoaded(this);
            SetStatus(ok ? "Ready" : "No scan");
        }
    }

    void BuildMenu()
    {
        menuCanvas = XRUi.CreateWorldCanvas("WristMenu", new Vector2(CanvasWidth, CanvasHeight), CanvasScale);
        var root = menuCanvas.transform;
        XRUi.CreatePanel(root, "Background", XRUi.PanelColor, 0, 0, CanvasWidth, CanvasHeight);
        // grip anywhere on the menu moves it (the trigger keeps clicking); once moved it stays where you put it
        menuDrag = PanelDrag.Attach(menuCanvas, CanvasWidth, CanvasHeight);

        // ---- header: name, what is going on, progress ----
        XRUi.CreateText(root, "Title", "XR House", 32, TextAlignmentOptions.MidlineLeft, Pad, 18, 260, 44, XRUi.TextColor, FontStyles.Bold);
        XRUi.CreatePanel(root, "StatusChip", new Color(1f, 1f, 1f, 0.06f), CanvasWidth - Pad - 250, 22, 250, 36);
        statusText = XRUi.CreateText(root, "Status", "", 20, TextAlignmentOptions.Center, CanvasWidth - Pad - 250, 22, 250, 36, XRUi.AccentText);
        progressBack = XRUi.CreatePanel(root, "ProgressBack", new Color(1, 1, 1, 0.08f), Pad, 70, ContentW, 6).gameObject;
        progressFill = XRUi.CreatePanel(root, "ProgressFill", XRUi.AccentText, Pad, 70, ContentW, 6).rectTransform;
        SetProgress(0);

        // ---- the scan you work on: one line - its name and state; it opens the list of scans ----
        const float scanY = 86, scanH = 60;
        scanLabelButton = XRUi.CreateButton(root, MRUKExporter.LiveSource, Pad, scanY, ContentW, scanH, ToggleScanList, 22);
        var sl = scanLabelButton.GetComponentInChildren<TMP_Text>();
        sl.alignment = TextAlignmentOptions.MidlineLeft;
        sl.rectTransform.offsetMin = new Vector2(18, 0); sl.rectTransform.offsetMax = new Vector2(-56, 0);
        XRUi.CreateIcon(scanLabelButton.transform, XRUi.Icon.ChevronDown, ContentW - 46, (scanH - 28) / 2f, 28, XRUi.MutedText);

        // ---- tabs: a segmented control ----
        const float tabY = 160, tabH = 64, tabPad = 6;
        XRUi.CreateInset(root, Pad, tabY, ContentW, tabH);
        string[] tabNames = { "View", "Layers", "Tools", "Data" };
        float tabW = (ContentW - 2 * tabPad - 3 * tabPad) / tabNames.Length;
        tabPages = new GameObject[tabNames.Length]; tabButtons = new Button[tabNames.Length];
        for (int i = 0; i < tabNames.Length; i++)
        {
            int k = i;
            tabButtons[i] = XRUi.CreateButton(root, tabNames[i], Pad + tabPad + i * (tabW + tabPad), tabY + tabPad, tabW, tabH - 2 * tabPad, () => ShowTab(k), 24);
            var page = new GameObject("Tab_" + tabNames[i], typeof(RectTransform));
            page.layer = root.gameObject.layer;
            page.transform.SetParent(root, false);
            XRUi.Place((RectTransform)page.transform, 0, 0, CanvasWidth, CanvasHeight);
            tabPages[i] = page;
        }
        Transform view = tabPages[0].transform, layers = tabPages[1].transform, tools = tabPages[2].transform, data = tabPages[3].transform;

        const float y0 = 266, h = 66, gap = 10, cap = 28;
        float w2 = (ContentW - gap) / 2f, w3 = (ContentW - 2 * gap) / 3f;
        float X2(int c) => Pad + c * (w2 + gap);
        float X3(int c) => Pad + c * (w3 + gap);

        // ---- View: how you look at the house ----
        XRUi.CreateCaption(view, "Show", Pad, y0 - cap, ContentW);
        walkButton = XRUi.CreateButton(view, "Walk", X2(0), y0, w2, h, OnToggleWalk, 24);
        dollhouseButton = XRUi.CreateButton(view, "Dollhouse", X2(1), y0, w2, h, OnToggleHouseView, 24);
        plansButton = XRUi.CreateButton(view, "Floor plans", X2(0), y0 + h + gap, w2, h, OnShowPlans, 24);
        miniMapButton = XRUi.CreateButton(view, "Minimap", X2(1), y0 + h + gap, w2, h, OnToggleMiniMap, 24);
        float m0 = y0 + 2 * (h + gap) + cap + 8;
        XRUi.CreateCaption(view, "Model", Pad, m0 - cap, ContentW);
        modeButton = XRUi.CreateButton(view, "", X2(0), m0, w2, h, OnCycleDollhouseMode, 22);
        XRUi.CreateButton(view, "Next floor", X2(1), m0, w2, h, OnNextFloor, 22);
        roughButton = XRUi.CreateButton(view, "Rough shell", X2(0), m0 + h + gap, w2, h, () => SetFinished(false), 22);
        finishedButton = XRUi.CreateButton(view, "Finished floors", X2(1), m0 + h + gap, w2, h, () => SetFinished(true), 22);

        // ---- Layers: what is drawn on the walls (real walls, Walk and Dollhouse alike) ----
        layersText = XRUi.CreateText(layers, "Where", "", 18, TextAlignmentOptions.TopLeft, Pad + 4, y0 - cap, ContentW - 8, 56, XRUi.MutedText);
        float l0 = y0 + 36;
        float LY(int r) => l0 + r * (h + gap);
        wallScanButton = XRUi.CreateButton(layers, "Wall scan", X2(0), LY(0), w2, h, () =>
        {
            WallOverlays.ShowScan = !WallOverlays.ShowScan;
            AddLog(WallOverlays.ShowScan ? "Wall scan on: the 3D scan of the bare walls painted on them (holes = not scanned yet)." : "Wall scan off.");
        }, 24);
        showRoutesButton = XRUi.CreateButton(layers, "Routes", X2(1), LY(0), w2, h, () => WallOverlays.ShowRoutes = !WallOverlays.ShowRoutes, 24);
        zonesButton = XRUi.CreateButton(layers, "No-drill zones", X2(0), LY(1), w2, h, () =>
        {
            WallOverlays.ShowZones = !WallOverlays.ShowZones;
            if (WallOverlays.ShowZones && !WallOverlays.ShowRoutes) WallOverlays.ShowRoutes = true;
            AddLog(WallOverlays.ShowZones ? $"No-drill zones: {WallOverlays.ZoneMargin * 100:0} cm either side of every route and box." : "No-drill zones off.");
        }, 24);
        photoMarkersButton = XRUi.CreateButton(layers, "Photo markers", X2(1), LY(1), w2, h, () => WallOverlays.ShowPhotoIcons = !WallOverlays.ShowPhotoIcons, 24);
        showPhotosButton = XRUi.CreateButton(layers, "Photos on walls", X2(0), LY(2), w2, h, () => WallOverlays.ShowPhotos = !WallOverlays.ShowPhotos, 24);
        fittingsButton = XRUi.CreateButton(layers, "Fittings\n<size=72%><color=#9aa6b8>real walls only</color></size>", X2(1), LY(2), w2, h, OnToggleFittings, 24);
        scanOverlayButton = XRUi.CreateButton(layers, "Scan outline\n<size=72%><color=#9aa6b8>real walls only</color></size>", X2(0), LY(3), w2, h, OnToggleScanOverlay, 24);

        // ---- Tools: each one straight away; pressed again = leave it ----
        toolButtons.Clear(); scanButton = null;
        void ToolButton(EditModeController.Tool t, string label, int col, float y, bool dense = false)
        {
            var btn = XRUi.CreateButton(tools, label, X3(col), y, w3, h, () => OnTool(t, dense), 22);
            if (dense) scanButton = btn; else toolButtons[t] = btn;
        }
        float t0 = y0, t1 = t0 + h + gap + cap + 8, t2 = t1 + h + gap + cap + 8;
        XRUi.CreateCaption(tools, "Document the bare house", Pad, t0 - cap, ContentW);
        ToolButton(EditModeController.Tool.Photos, "3D scan", 0, t0, dense: true);
        ToolButton(EditModeController.Tool.Routes, "Routes", 1, t0);
        ToolButton(EditModeController.Tool.Photos, "Photos", 2, t0);
        XRUi.CreateCaption(tools, "Edit the model", Pad, t1 - cap, ContentW);
        ToolButton(EditModeController.Tool.Doors, "Doors &\nwindows", 0, t1);
        ToolButton(EditModeController.Tool.Stairs, "Stairs", 1, t1);
        ToolButton(EditModeController.Tool.Holes, "Openings", 2, t1);
        XRUi.CreateCaption(tools, "Plan", Pad, t2 - cap, ContentW);
        ToolButton(EditModeController.Tool.Furniture, "Furniture", 0, t2);
        ToolButton(EditModeController.Tool.Measure, "Measure", 1, t2);
        ToolButton(EditModeController.Tool.Sun, "Sun", 2, t2);
        XRUi.CreateText(tools, "Hint", "The active tool is blue - press it again to leave. Hold Y = leave any tool.", 17, TextAlignmentOptions.Center, Pad, t2 + h + 10, ContentW, 30, XRUi.MutedText);

        // ---- Data: keep and hand over; the destructive ones apart below ----
        XRUi.CreateCaption(data, "This scan", Pad, y0 - cap, ContentW);
        saveButton = XRUi.CreateButton(data, "Save live scan", X2(0), y0, w2, h, OnSaveScan, 24);
        exportButton = XRUi.CreateButton(data, "Export", X2(1), y0, w2, h, OnExportAll, 24);
        pendingText = XRUi.CreateText(data, "PendingHint", "", 18, TextAlignmentOptions.TopLeft, Pad + 4, y0 + h + 8, ContentW - 8, 52, XRUi.MutedText);
        float d1 = y0 + h + 60 + cap + 8;
        XRUi.CreateCaption(data, "Clean up", Pad, d1 - cap, ContentW);
        var deleteExportsButton = XRUi.CreateButton(data, "Delete exports", X2(0), d1, w2, h, OnDeleteExports, 22);
        XRUi.SetTint(deleteExportsButton, XRUi.ButtonDangerColor);
        XRUi.CreateText(data, "ScansHint", "Scans: open the list at the top - each has its bin, old copies are marked.", 17, TextAlignmentOptions.MidlineLeft, X2(1) + 6, d1, w2 - 6, h, XRUi.MutedText);

        foreach (var b in new[] { walkButton, dollhouseButton, plansButton, miniMapButton, showRoutesButton, showPhotosButton, fittingsButton, scanOverlayButton, zonesButton, wallScanButton, photoMarkersButton })
            XRUi.SetToggle(b, false);

        // ---- log ----
        const float logTop = 618;
        XRUi.CreateInset(root, Pad, logTop, ContentW, CanvasHeight - logTop - Pad);
        logText = XRUi.CreateText(root, "Log", "", 18, TextAlignmentOptions.TopLeft, Pad + 14, logTop + 10, ContentW - 28, CanvasHeight - logTop - Pad - 20, XRUi.MutedText);
        logText.overflowMode = TextOverflowModes.Truncate;

        BuildScanList(root);
        ShowTab(Mathf.Clamp(PlayerPrefs.GetInt(TabPref, 0), 0, tabPages.Length - 1));
    }

    // ---------- the list of scans (opens from the scan line) ----------

    const int ScanRows = 7;
    GameObject scanList;
    TMP_Text scanListTitle;
    readonly List<Button> rowButtons = new List<Button>(), rowBins = new List<Button>();
    Button pagePrev, pageNext, oldCopiesButton;
    int scanPage;
    List<MRUKSceneCache.ScanInfo> scanInfos;
    string binArmed; float binArmedUntil;
    float oldCopiesArmedUntil;

    void BuildScanList(Transform root)
    {
        const float top = 154;
        scanList = new GameObject("ScanList", typeof(RectTransform));
        scanList.layer = root.gameObject.layer;
        scanList.transform.SetParent(root, false);
        XRUi.Place((RectTransform)scanList.transform, 0, 0, CanvasWidth, CanvasHeight);
        var t = scanList.transform;
        // covers the tabs, the content and the log while open
        XRUi.CreatePanel(t, "Back", new Color(0.09f, 0.11f, 0.15f, 1f), Pad - 8, top, ContentW + 16, CanvasHeight - top - Pad + 8);
        scanListTitle = XRUi.CreateText(t, "Title", "Scans", 20, TextAlignmentOptions.MidlineLeft, Pad + 10, top + 10, ContentW - 80, 40, XRUi.MutedText);
        XRUi.CreateIconButton(t, XRUi.Icon.Close, Pad + ContentW - 52, top + 8, 48, 44, () => scanList.SetActive(false), 26);
        const float rowH = 56, rowGap = 6, binW = 60;
        float y = top + 60;
        for (int i = 0; i < ScanRows; i++)
        {
            int k = i;
            var row = XRUi.CreateButton(t, "", Pad, y, ContentW - binW - rowGap, rowH, () => OnScanRow(k), 21);
            var lt = row.GetComponentInChildren<TMP_Text>();
            lt.alignment = TextAlignmentOptions.MidlineLeft;
            lt.rectTransform.offsetMin = new Vector2(16, 0);
            rowButtons.Add(row);
            var bin = XRUi.CreateIconButton(t, XRUi.Icon.Trash, Pad + ContentW - binW, y, binW, rowH, () => OnScanBin(k), 28);
            rowBins.Add(bin);
            y += rowH + rowGap;
        }
        float fy = CanvasHeight - Pad - 58;
        pagePrev = XRUi.CreateIconButton(t, XRUi.Icon.ChevronLeft, Pad, fy, 64, 52, () => { scanPage--; FillScanList(); }, 26);
        pageNext = XRUi.CreateIconButton(t, XRUi.Icon.ChevronRight, Pad + ContentW - 64, fy, 64, 52, () => { scanPage++; FillScanList(); }, 26);
        oldCopiesButton = XRUi.CreateButton(t, "", Pad + 74, fy, ContentW - 148, 52, OnRemoveOldCopies, 20);
        scanList.SetActive(false);
    }

    async void ToggleScanList()
    {
        if (scanList == null || exporter == null) return;
        if (scanList.activeSelf) { scanList.SetActive(false); return; }
        scanList.SetActive(true);
        scanInfos = null; binArmed = null;
        // the page with the selected scan
        int idx = Mathf.Max(0, exporter.Sources.IndexOf(exporter.SelectedSource));
        scanPage = idx / ScanRows;
        FillScanList();
        scanInfos = await MRUKSceneCache.DescribeAsync(); // rooms, edits, old copies - read in the background
        if (scanList.activeSelf) FillScanList();
    }

    // the preview test has no exporter: it hands the list in
    List<string> previewSources;
    List<string> ListSources => exporter != null ? exporter.Sources : previewSources ?? new List<string> { MRUKExporter.LiveSource };
    string SelectedScan => exporter != null ? exporter.SelectedSource : previewSelected ?? MRUKExporter.LiveSource;
    string previewSelected;

    /// <summary>Tests: the scan list open with these scans.</summary>
    public void PreviewScanList(List<string> sources, List<MRUKSceneCache.ScanInfo> infos, string selected = null)
    {
        previewSelected = selected;
        previewSources = sources; scanInfos = infos; scanPage = 0;
        scanList.SetActive(true);
        FillScanList();
    }

    void FillScanList()
    {
        var sources = ListSources;
        int pages = Mathf.Max(1, (sources.Count + ScanRows - 1) / ScanRows);
        scanPage = Mathf.Clamp(scanPage, 0, pages - 1);
        int old = scanInfos?.Count(i => i.keptIn != null) ?? 0;
        scanListTitle.text = $"Scans  <size=80%>{sources.Count - 1} saved{(pages > 1 ? $"  ·  page {scanPage + 1}/{pages}" : "")}</size>";
        for (int i = 0; i < ScanRows; i++)
        {
            int si = scanPage * ScanRows + i;
            bool has = si < sources.Count;
            rowButtons[i].gameObject.SetActive(has);
            rowBins[i].gameObject.SetActive(has && sources[si] != MRUKExporter.LiveSource);
            if (!has) continue;
            string name = sources[si];
            var info = scanInfos?.FirstOrDefault(x => x.name == name);
            string details;
            if (name == MRUKExporter.LiveSource) details = "what the Quest sees here now";
            else if (info == null) details = "...";
            else
            {
                var parts = new List<string> { $"{info.rooms} room{(info.rooms == 1 ? "" : "s")}", info.time.ToString("d. M. yyyy HH:mm") };
                if (info.furniture) parts.Add("furniture");
                if (info.edits) parts.Add("edits");
                if (info.doorway) parts.Add("added doorway");
                details = string.Join("  ·  ", parts);
                if (info.keptIn != null) details += $"  ·  <color=#ffb050>old copy</color>";
            }
            XRUi.SetLabel(rowButtons[i], $"<b>{(name == MRUKExporter.LiveSource ? "Live scan" : name)}</b>\n<size=78%><color=#9aa6b8>{details}</color></size>");
            XRUi.SetTint(rowButtons[i], name == SelectedScan ? XRUi.ButtonOnColor : XRUi.ButtonColor);
            XRUi.SetTint(rowBins[i], binArmed == name && Time.unscaledTime < binArmedUntil ? XRUi.ButtonDangerColor : XRUi.ButtonColor);
        }
        pagePrev.gameObject.SetActive(pages > 1);
        pageNext.gameObject.SetActive(pages > 1);
        oldCopiesButton.gameObject.SetActive(old > 0);
        bool armed = Time.unscaledTime < oldCopiesArmedUntil;
        XRUi.SetLabel(oldCopiesButton, armed ? $"Press again to delete {old} old cop{(old == 1 ? "y" : "ies")}" : $"Remove {old} old cop{(old == 1 ? "y" : "ies")}");
        XRUi.SetTint(oldCopiesButton, armed ? XRUi.ButtonDangerColor : XRUi.ButtonWarnColor);
    }

    string RowSource(int row)
    {
        if (exporter == null) return null;
        var sources = ListSources;
        int si = scanPage * ScanRows + row;
        return si < sources.Count ? sources[si] : null;
    }

    /// <summary>A row: that scan becomes the selected one and is loaded (every view follows).</summary>
    async void OnScanRow(int row)
    {
        string name = RowSource(row);
        if (name == null) return;
        scanList.SetActive(false);
        if (walk != null && walk.IsOn) OnToggleWalk(); // the walk-through model belongs to the scan it was built from
        exporter.Select(name);
        RefreshScanLabel();
        AddLog($"Scan: <b>{(name == MRUKExporter.LiveSource ? "Live scan" : name)}</b>");
        await Busy("Loading...");
        bool loaded = await exporter.EnsureSelectedSourceLoaded(this, forceReload: true) && exporter.HasValidRooms();
        SetStatus(loaded ? "Ready" : "No rooms");
    }

    /// <summary>A row's bin: first press arms it (red), a second press within 4 s deletes that saved scan.</summary>
    async void OnScanBin(int row)
    {
        string name = RowSource(row);
        if (name == null || name == MRUKExporter.LiveSource) return;
        if (binArmed != name || Time.unscaledTime > binArmedUntil)
        {
            binArmed = name; binArmedUntil = Time.unscaledTime + 4f;
            AddLog($"Press the bin again to delete '{name}'.");
            FillScanList();
            await Task.Delay(4100);
            if (scanList.activeSelf) FillScanList();
            return;
        }
        binArmed = null;
        string replacement = scanInfos?.FirstOrDefault(i => i.name == name)?.keptIn;
        bool wasSelected = exporter.SelectedSource == name;
        bool ok = wasSelected ? exporter.DeleteSelectedScan(this) : MRUKSceneCache.DeleteCachedScan(name);
        if (ok && !wasSelected) { HouseEditsStore.DeleteFor(name); AddLog($"<color=green>Deleted '{name}'.</color>"); }
        if (!ok) { AddLog($"<color=orange>Could not delete '{name}'.</color>"); return; }
        if (wasSelected)
        {
            // the scan you had is gone: the one that covers it, else the live scan
            if (replacement != null) exporter.Select(replacement);
            RefreshScanLabel();
            _ = ReloadAfterScanEdit();
        }
        scanInfos = await MRUKSceneCache.DescribeAsync();
        if (scanList.activeSelf) FillScanList();
    }

    /// <summary>Old copies (a newer saved scan has all their rooms, they hold nothing of their own): press, press again.</summary>
    async void OnRemoveOldCopies()
    {
        if (scanInfos == null) return;
        var old = scanInfos.Where(i => i.keptIn != null).ToList();
        if (old.Count == 0) return;
        if (Time.unscaledTime > oldCopiesArmedUntil)
        {
            oldCopiesArmedUntil = Time.unscaledTime + 5f;
            foreach (var o in old) AddLog($"Old copy: '{o.name}' - all its rooms are in '{o.keptIn}'.");
            FillScanList();
            await Task.Delay(5100);
            if (scanList.activeSelf) FillScanList();
            return;
        }
        oldCopiesArmedUntil = 0;
        string selected = exporter.SelectedSource;
        int n = 0;
        foreach (var o in old)
        {
            if (MRUKSceneCache.DeleteCachedScan(o.name)) { HouseEditsStore.DeleteFor(o.name); n++; Debug.Log($"[Cleanup] deleted '{o.name}' (covered by '{o.keptIn}')"); }
            else AddLog($"<color=orange>Could not delete '{o.name}'.</color>");
        }
        var mine = old.FirstOrDefault(o => o.name == selected);
        if (mine != null) { exporter.Select(mine.keptIn); RefreshScanLabel(); _ = ReloadAfterScanEdit(); }
        AddLog($"<color=green>Deleted {n} old cop{(n == 1 ? "y" : "ies")}.</color>");
        scanInfos = await MRUKSceneCache.DescribeAsync();
        if (scanList.activeSelf) FillScanList();
    }

    const string TabPref = "Menu.Tab4"; // 4 tabs now - the old index meant something else
    GameObject[] tabPages;
    Button[] tabButtons;

    /// <summary>Shows one tab of the menu (remembered for the next start).</summary>
    public void ShowTab(int i)
    {
        if (tabPages == null) return;
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
        if (Preview) return;
        bool moved = menuDrag != null && menuDrag.Moved;
        bool settling = Time.realtimeSinceStartup < 6f && !moved;
        if (!menuPlaced || settling || (!moved && XRUi.LeftBehind(menuCanvas.transform, cam.transform)))
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
        float w = ContentW * Mathf.Clamp01(percent / 100f);
        progressFill.sizeDelta = new Vector2(w, 6f);
        progressFill.gameObject.SetActive(w >= 6f); // a rounded bar narrower than it is tall looks broken
        if (progressBack) progressBack.SetActive(w >= 6f && percent < 100f);
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
        if (Preview) return;
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

        // Left hand: Y tap = save log, Y hold (1 s) = edit mode on/off, X = routes / photos on walls (cycles; Save scan
        // is in the menu's Data tab - it used to be on X and made new scan copies by accident), stick click = floor plans
        // (seated/standing while walking). The scan source changes only with the menu's < > - a stick flick used to switch
        // it by accident (e.g. right after leaving the walk-through with the stick still pushed).
        if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch)) { yDownTime = Time.time; yHoldFired = false; }
        if (OVRInput.Get(OVRInput.Button.Two, OVRInput.Controller.LTouch) && !yHoldFired && Time.time - yDownTime >= EditHoldSeconds)
        {
            yHoldFired = true;
            // hold Y: leave the tool you are in; with none, the menu opens on its Tools tab
            if (editMode != null && editMode.IsOn) editMode.LeaveTool();
            else { if (menuCanvas != null && !menuCanvas.gameObject.activeSelf) OnToggleMenu(); ShowTab(2); }
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
        XRUi.SetToggle(plansButton, plansOpen);
        XRUi.SetToggle(dollhouseButton, dollhouse != null && dollhouse.IsOn);
        XRUi.SetToggle(miniMapButton, miniMap != null && miniMap.IsOn);
        XRUi.SetToggle(walkButton, walk != null && walk.IsOn);
        // the real-wall-only layers are greyed out while the loaded scan is of another place
        bool here = exporter != null && exporter.WorldAligned && !MRUKExporter.Loading;
        bool outline = scanOverlay != null && scanOverlay.IsOn;
        if (outline || here) XRUi.SetToggle(scanOverlayButton, outline); else XRUi.SetTint(scanOverlayButton, XRUi.ButtonDisabled);
        if (WallOverlays.ShowFittings || here) XRUi.SetToggle(fittingsButton, WallOverlays.ShowFittings); else XRUi.SetTint(fittingsButton, XRUi.ButtonDisabled);
        XRUi.SetToggle(showRoutesButton, WallOverlays.ShowRoutes);
        XRUi.SetToggle(showPhotosButton, WallOverlays.ShowPhotos);
        XRUi.SetToggle(wallScanButton, WallOverlays.ShowScan);
        XRUi.SetToggle(zonesButton, WallOverlays.ShowZones);
        XRUi.SetToggle(photoMarkersButton, WallOverlays.ShowPhotoIcons);
        SyncPending();
        // tools: the active one is blue (an action, not a switch)
        bool scanning = PhotosOn && photoTool != null && photoTool.Dense;
        foreach (var kv in toolButtons)
        {
            bool on = editMode != null && editMode.IsOn && editMode.CurrentTool == kv.Key && !(kv.Key == EditModeController.Tool.Photos && scanning);
            XRUi.SetTint(kv.Value, on ? XRUi.ButtonOnColor : XRUi.ButtonColor);
        }
        if (scanButton != null) XRUi.SetTint(scanButton, scanning ? XRUi.ButtonOnColor : XRUi.ButtonColor);
        // rough shell | finished: one of the two is picked
        bool f = Furniture.Finished;
        XRUi.SetTint(roughButton, f ? XRUi.ButtonColor : XRUi.ButtonOnColor);
        XRUi.SetTint(finishedButton, f ? XRUi.ButtonOnColor : XRUi.ButtonColor);
    }

    string modeShown, stateShown;
    TMP_Text layersText;
    string layersShown;
    readonly Dictionary<EditModeController.Tool, Button> toolButtons = new Dictionary<EditModeController.Tool, Button>();
    Button scanButton;
    WallPhotoCapture photoTool;
    Button zonesButton, wallScanButton;

    /// <summary>Labels that show state: the Dollhouse mode, and where the Layers tab draws (real walls or only the models).</summary>
    void SyncLabels()
    {
        if (modeButton != null && dollhouse != null)
        {
            string m = "Detail: " + dollhouse.ModeLabel;
            if (m != modeShown) { modeShown = m; XRUi.SetLabel(modeButton, m); }
        }

        if (layersText != null && exporter != null)
        {
            string t = MRUKExporter.Loading ? "Loading the scan..."
                : exporter.WorldAligned
                    ? "<color=#7fe07f>On the real walls here</color>, in Walk and in the Dollhouse. Point at a route or box to see what it is."
                    : "<color=#ffa040>This scan is of another place</color> - layers show in Walk and the Dollhouse only. Real-wall layers need the scan of the place you stand in (pick it with < >).";
            if (!string.IsNullOrEmpty(WallScan.Status)) t += $"  <color=#9ecbff>{WallScan.Status}</color>";
            if (t != layersShown) { layersShown = t; layersText.text = t; }
        }
        if (scanLabelButton != null && exporter != null)
        {
            bool live = exporter.SelectedSource == MRUKExporter.LiveSource;
            string place = MRUKExporter.Loading ? "loading..."
                : exporter.ActiveSource != exporter.SelectedSource ? "<color=#ffb050>not loaded</color>"
                : exporter.WorldAligned ? "<color=#6fe08f>lined up here</color>"
                : "<color=#ffb050>another place</color>";
            string save = live && PendingChanges.LiveUnsaved ? "  ·  <color=#ffb050>not saved</color>" : "";
            string exp = PendingChanges.ExportPending ? "  ·  <color=#ffb050>not exported</color>" : "";
            string t = $"<b>{(live ? "Live scan" : exporter.SelectedSource)}</b>\n<size=72%>{place}{save}{exp}</size>";
            if (t != stateShown) { stateShown = t; XRUi.SetLabel(scanLabelButton, t); }
        }
    }

    /// <summary>Rough shell (as scanned - for tracing pipes in the floor and cables above the ceiling) or finished floors
    /// (build-up) + suspended ceilings. Rebuilds whatever shows the model.</summary>
    async void SetFinished(bool finished)
    {
        if (Furniture.Finished == finished) return;
        Furniture.Finished = finished;
        AddLog(finished ? "Model: finished floors + suspended ceilings" : "Model: rough shell as scanned (pipes in the floor, cables above the ceiling)");
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
        XRUi.SetLabel(saveButton, live ? "Save live scan" : "Saved automatically");
        XRUi.SetTint(exportButton, export ? XRUi.ButtonWarnColor : XRUi.ButtonColor);
        string s1 = live
            ? (save ? "Quest has rooms no saved scan has - Save live scan keeps them. " : "The live scan is saved. ")
            : "Edits, routes and names of this scan save by themselves. ";
        pendingText.text = s1 + (export ? $"Not exported yet: {what}." : "Exported.");
        if (tabButtons != null) XRUi.SetLabel(tabButtons[3], save || export ? "Data <color=#ffb050>•</color>" : "Data");
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

    /// <summary>X: wall scan + routes -> routes only -> nothing -> wall scan + routes... (works while walking too).</summary>
    void CycleWallLayers()
    {
        bool r = WallOverlays.ShowRoutes, sc = WallOverlays.ShowScan || WallOverlays.ShowPhotos;
        if (r && sc) { WallOverlays.ShowScan = false; WallOverlays.ShowPhotos = false; AddLog("Walls: routes"); }
        else if (r) { WallOverlays.ShowRoutes = false; AddLog("Walls: nothing"); }
        else { WallOverlays.ShowRoutes = true; WallOverlays.ShowScan = true; AddLog("Walls: wall scan + routes"); }
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

    /// <summary>Tools tab: the tool on (edit mode comes on with it), pressed again off. Routes and wall photos hide the
    /// menu for a clear view (it comes back when the tool is left).</summary>
    void OnTool(EditModeController.Tool t, bool dense = false)
    {
        if (editMode == null) return;
        if (editMode.IsOn && editMode.CurrentTool == t)
        {
            // 3D scan <-> cable photos: the same tool, just switch its mode
            if (t == EditModeController.Tool.Photos && photoTool != null && photoTool.Dense != dense) { photoTool.SetDense(dense); return; }
            editMode.LeaveTool(); return;
        }
        if (t == EditModeController.Tool.Photos) WallPhotoCapture.StartDense = dense;
        editMode.UseTool(t);
        if (t == EditModeController.Tool.Routes || t == EditModeController.Tool.Photos) HideMenuForTool();
    }

    bool PhotosOn => editMode != null && editMode.IsOn && editMode.CurrentTool == EditModeController.Tool.Photos;

    /// <summary>Opens/closes the whole wrist menu (bound to B - see the comment where it's read).</summary>
    public void OnToggleMenu()
    {
        if (menuCanvas == null) return;
        bool open = !menuCanvas.gameObject.activeSelf;
        menuCanvas.gameObject.SetActive(open);
        // Every time it opens it appears in front of you again (also after you dragged it somewhere).
        if (open) { menuPlaced = false; menuDrag?.ResetMoved(); }
    }


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
        if (walk == null || walk.IsOn || walkStarting || dollhouse == null || !dollhouse.IsOn || dollhouse.IsDragging) return;
        if (!dollhouse.TryGetPointedFloor(out Vector3 point)) return;
        walkStarting = true; // further presses while it builds are dropped
        try
        {
            await Busy("Building...");
            if (await walk.Enter(point, dollhouse.FacingYaw))
            {
                dollhouse.SetHidden(true);
                SetStatus("Walk");
            }
            else SetStatus("Ready");
        }
        finally { walkStarting = false; }
    }

    bool walkStarting; // Walk is loading / building: clicking Walk again meanwhile does nothing (instead of building twice)

    /// <summary>Walk on/off. Entered from here (not from a Dollhouse point) it uses the scan's own real-world
    /// alignment when you're standing inside the scanned area, else the middle of the largest room.</summary>
    public async void OnToggleWalk()
    {
        if (walk == null || walkStarting) return;
        if (walk.IsOn)
        {
            walk.Exit();
            dollhouse?.SetHidden(false);
            SetStatus(dollhouse != null && dollhouse.IsOn ? $"Dollhouse: {dollhouse.ModeLabel}" : "Ready");
            return;
        }
        if (!RequireExporter()) return;
        walkStarting = true;
        try
        {
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
        finally { walkStarting = false; }
    }

    /// <summary>For other components that change the selected scan (e.g. edit mode saving the live scan).</summary>
    public void RefreshSourceLabel() => RefreshScanLabel();

    void RefreshScanLabel()
    {
        stateShown = null; // SyncLabels writes name + state on the next frame
    }

    bool RequireExporter()
    {
        if (exporter != null) return true;
        AddLog("<color=red>ERROR: MRUKExporter not found in the scene.</color>");
        SetStatus("ERROR");
        return false;
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
                while (walkStarting || (walk != null && walk.Entering)) await Task.Yield(); // let a starting walk finish first
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
