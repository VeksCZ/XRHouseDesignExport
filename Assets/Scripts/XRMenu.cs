using System;
using System.Collections.Generic;
using System.IO;
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
    const float CanvasWidth = 600f, CanvasHeight = 776f + 88f + 88f + 88f + 88f + HeaderH, CanvasScale = 0.0006f;
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
    Button dollhouseButton, plansButton, scanOverlayButton, miniMapButton, walkButton, editButton, photosButton, routesButton, showRoutesButton, showPhotosButton, fittingsButton;
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
        // Dollhouse on/off and which of its 4 tiers to show are two separate buttons sharing this row, so
        // turning it on never guesses which tier you left it on and switching tiers never needs to cycle
        // through "off" first.
        dollhouseButton = XRUi.CreateButton(root, "Dollhouse", left, top, w, h, OnToggleHouseView);
        XRUi.SetTint(dollhouseButton, XRUi.ButtonToggleColor);
        XRUi.CreateButton(root, "Mode", right, top, w, h, OnCycleDollhouseMode, 22);
        // "Open report" (opening the exported HTML file in some other app) is gone: it needed a full export to
        // have already run, and even then a raw file:// URI is blocked by Android's scoped storage on the
        // Quest's target SDK - the in-headset Floor plans viewer covers the same need without either problem.
        plansButton = XRUi.CreateButton(root, "Floor plans", left, top + (h + gap), w, h, OnShowPlans);
        XRUi.SetTint(plansButton, XRUi.ButtonToggleColor);
        XRUi.CreateButton(root, "Export", right, top + (h + gap), w, h, OnExportAll);
        // Highlights the scanned room(s) over passthrough, similar to Quest's own Room Setup preview.
        scanOverlayButton = XRUi.CreateButton(root, "Scan overlay", left, top + 2 * (h + gap), w, h, OnToggleScanOverlay);
        XRUi.SetTint(scanOverlayButton, XRUi.ButtonToggleColor);
        XRUi.CreateButton(root, "Save scan", right, top + 2 * (h + gap), w, h, OnSaveScan);
        // A small always-facing copy of the house near the left wrist with a marker for your position, like
        // the little preview Quest's own Room Setup shows.
        miniMapButton = XRUi.CreateButton(root, "Minimap", left, top + 3 * (h + gap), w, h, OnToggleMiniMap);
        XRUi.SetTint(miniMapButton, XRUi.ButtonToggleColor);
        // 1:1 walk-through. Also entered by pointing at the Dollhouse floor + trigger (enters right there);
        // A leaves it again.
        walkButton = XRUi.CreateButton(root, "Walk", right, top + 3 * (h + gap), w, h, OnToggleWalk);
        XRUi.SetTint(walkButton, XRUi.ButtonToggleColor);
        // Edit mode (doors, stairs) - also hold Y. Floor: jump to the next story while walking (seated/standing is the left stick click).
        editButton = XRUi.CreateButton(root, "Edit", left, top + 4 * (h + gap), w, h, () => editMode?.Toggle());
        XRUi.SetTint(editButton, XRUi.ButtonToggleColor);
        XRUi.CreateButton(root, "Next floor", right, top + 4 * (h + gap), w, h, OnNextFloor);
        // One click into photographing the bare walls (edit mode + the Photos tool), the same again leaves it.
        photosButton = XRUi.CreateButton(root, "Wall photos", left, top + 5 * (h + gap), w, h, OnWallPhotos);
        XRUi.SetTint(photosButton, XRUi.ButtonToggleColor);
        // Tracing pipes/cables (edit mode + the Routes tool), the same again leaves it.
        routesButton = XRUi.CreateButton(root, "Trace routes", right, top + 5 * (h + gap), w, h, OnTraceRoutes);
        XRUi.SetTint(routesButton, XRUi.ButtonToggleColor);
        // What the views show: the traced routes and the cable photos on the walls - each on/off.
        showRoutesButton = XRUi.CreateButton(root, "Show routes", left, top + 6 * (h + gap), w, h, () => WallOverlays.ShowRoutes = !WallOverlays.ShowRoutes);
        showPhotosButton = XRUi.CreateButton(root, "Photos on walls", right, top + 6 * (h + gap), w, h, () => WallOverlays.ShowPhotos = !WallOverlays.ShowPhotos);

        // The planned bathroom (bath, shower, basins as ghosts) and the niches sunk into the real walls - over passthrough.
        fittingsButton = XRUi.CreateButton(root, "Fittings + niches on the real walls", left, top + 7 * (h + gap), right + w - left, h, OnToggleFittings, 24);

        float rowY = top + 8 * (h + gap);
        // "Load scan" is gone - every action here (Export, Dollhouse, Floor plans, Scan overlay, Minimap)
        // already loads the selected source itself on demand, so a separate standalone "load" step had nothing
        // left to do that pressing the feature you actually wanted wouldn't already do. Clicking the scan name
        // itself still force-reloads it, for the rare case you want that on its own.
        XRUi.CreateButton(root, "<", left, rowY, 80, h, () => OnCycleSource(-1), 40);
        scanLabelButton = XRUi.CreateButton(root, MRUKExporter.LiveSource, left + 90, rowY, 372, h, OnLoadSource, 28);
        XRUi.CreateButton(root, ">", left + 472, rowY, 80, h, () => OnCycleSource(1), 40);

        // The two destructive actions live in their own row at the very bottom, below the log, away from
        // everything else that gets clicked routinely - so a stray click reaching for the log or the nav row
        // above it can't land on one by accident.
        float deleteRowY = CanvasHeight - h - 10;
        var deleteScanButton = XRUi.CreateButton(root, "Delete scan", left, deleteRowY, w, h, OnDeleteScan);
        XRUi.SetTint(deleteScanButton, XRUi.ButtonDangerColor);
        var deleteExportsButton = XRUi.CreateButton(root, "Delete exports", right, deleteRowY, w, h, OnDeleteExports);
        XRUi.SetTint(deleteExportsButton, XRUi.ButtonDangerColor);

        float logTop = rowY + h + 12;
        logText = XRUi.CreateText(root, "Log", "", 22, TextAlignmentOptions.TopLeft, 24, logTop, 552, deleteRowY - gap - logTop, XRUi.MutedText);
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
        // Once you've dragged the menu by its own handle, it stays exactly where you put it - same as the
        // Dollhouse/Floor plans/Minimap never auto-repositioning after being placed.
        menuCanvas.worldCamera = cam;
        // Placed in front of you when it opens (B), then it stays put like every other panel - it no longer turns
        // with your head. Walk away from it and it comes back in front of you.
        if (!menuCanvas.gameObject.activeSelf) return;
        if (!menuPlaced || (!menuManuallyPlaced && XRUi.LeftBehind(menuCanvas.transform, cam.transform)))
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
        // While walking, A leaves the walk-through (back to the Dollhouse, if it was up) instead.
        if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch)) { if (walking) OnToggleWalk(); else OnToggleHouseView(); }
        HandleMenuDrag();

        // Left hand: Y tap = save log, Y hold (1 s) = edit mode on/off, X = save scan, stick click = floor plans
        // (seated/standing while walking), stick left/right = scan source.
        if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch)) { yDownTime = Time.time; yHoldFired = false; }
        if (OVRInput.Get(OVRInput.Button.Two, OVRInput.Controller.LTouch) && !yHoldFired && Time.time - yDownTime >= EditHoldSeconds)
        {
            yHoldFired = true;
            editMode?.Toggle();
        }
        if (OVRInput.GetUp(OVRInput.Button.Two, OVRInput.Controller.LTouch) && !yHoldFired) SaveLogToFile();
        if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.LTouch)) OnSaveScan();
        if (OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.LTouch)) { if (walking) walk.ToggleSeated(); else OnShowPlans(); }

        // Both sticks belong to the walk-through (move / turn / teleport) - no flick shortcuts while walking.
        if (walking || StairEditTool.StickCapture) return;

        flickCooldown -= Time.deltaTime;
        if (flickCooldown > 0f) return;

        Vector2 right = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.RTouch);
        Vector2 left = OVRInput.Get(OVRInput.Axis2D.PrimaryThumbstick, OVRInput.Controller.LTouch);

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
        else if (Mathf.Abs(left.x) > FlickThreshold)
        {
            OnCycleSource(left.x > 0 ? 1 : -1);
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
        if (scanOverlayButton != null) XRUi.SetTint(scanOverlayButton, scanOverlay != null && scanOverlay.IsOn ? XRUi.ButtonOnColor : XRUi.ButtonToggleColor);
        if (miniMapButton != null) XRUi.SetTint(miniMapButton, miniMap != null && miniMap.IsOn ? XRUi.ButtonOnColor : XRUi.ButtonToggleColor);
        if (walkButton != null) XRUi.SetTint(walkButton, walk != null && walk.IsOn ? XRUi.ButtonOnColor : XRUi.ButtonToggleColor);
        if (editButton != null) XRUi.SetTint(editButton, editMode != null && editMode.IsOn ? XRUi.ButtonOnColor : XRUi.ButtonToggleColor);
        if (photosButton != null) XRUi.SetTint(photosButton, PhotosOn ? XRUi.ButtonOnColor : XRUi.ButtonToggleColor);
        if (routesButton != null) XRUi.SetTint(routesButton, RoutesOn ? XRUi.ButtonOnColor : XRUi.ButtonToggleColor);
        if (showRoutesButton != null) XRUi.SetTint(showRoutesButton, WallOverlays.ShowRoutes ? XRUi.ButtonOnColor : XRUi.ButtonToggleColor);
        if (showPhotosButton != null) XRUi.SetTint(showPhotosButton, WallOverlays.ShowPhotos ? XRUi.ButtonOnColor : XRUi.ButtonToggleColor);
        if (fittingsButton != null) XRUi.SetTint(fittingsButton, WallOverlays.ShowFittings ? XRUi.ButtonOnColor : XRUi.ButtonToggleColor);
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

    bool RoutesOn => editMode != null && editMode.IsOn && editMode.CurrentTool == EditModeController.Tool.Routes;

    /// <summary>Main-menu shortcut to tracing routes (edit mode + Routes tool); hides the menu. Again = leave.</summary>
    public void OnTraceRoutes()
    {
        if (editMode == null) return;
        if (RoutesOn) { editMode.Toggle(); return; }
        editMode.UseTool(EditModeController.Tool.Routes);
        if (menuCanvas != null && menuCanvas.gameObject.activeSelf) OnToggleMenu();
    }

    bool PhotosOn => editMode != null && editMode.IsOn && editMode.CurrentTool == EditModeController.Tool.Photos;

    /// <summary>Main-menu shortcut to the wall photos (edit mode + Photos tool); hides the menu so the view is clear
    /// (B brings it back). Pressed again it leaves edit mode.</summary>
    public void OnWallPhotos()
    {
        if (editMode == null) return;
        if (PhotosOn) { editMode.Toggle(); return; }
        editMode.UseTool(EditModeController.Tool.Photos);
        if (menuCanvas != null && menuCanvas.gameObject.activeSelf) OnToggleMenu();
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
        if (button != null && button.IsInteractable()) button.onClick.Invoke();
        return true;
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

    async void OnCycleSource(int direction)
    {
        if (!RequireExporter()) return;
        // The walk-through model belongs to the scan it was built from - leave it before switching.
        if (walk != null && walk.IsOn) OnToggleWalk();
        exporter.CycleSource(direction, this);
        RefreshScanLabel();

        bool dollhouseOn = dollhouse != null && dollhouse.IsOn;
        bool overlayOn = scanOverlay != null && scanOverlay.IsOn;
        bool miniMapOn = miniMap != null && miniMap.IsOn;
        if (!dollhouseOn && !overlayOn && !miniMapOn) return;

        await Busy("Loading...");
        bool loaded = await exporter.EnsureSelectedSourceLoaded(this, forceReload: true) && exporter.HasValidRooms();

        // The dollhouse keeps showing its previous view if the new scan has no rooms, rather than vanish -
        // RebuildInPlace() keeps it exactly where it was.
        if (dollhouseOn)
        {
            if (loaded) { dollhouse.RebuildInPlace(); SetStatus($"Dollhouse: {dollhouse.ModeLabel}"); }
            else AddLog("<color=red>No rooms in this scan - dollhouse left showing the previous one.</color>");
        }
        // The scan overlay and minimap have no equivalent fallback - their content is tied to the anchors of
        // whatever was loaded before, which are gone once the scan underneath them changes. They either rebuild
        // against the new scan or turn themselves off (ForceOff), instead of what used to happen: silently
        // going stale on screen (or invisible) while still reporting themselves as "on" forever after.
        if (overlayOn)
        {
            if (loaded) scanOverlay.RebuildInPlace(); else scanOverlay.ForceOff();
            if (!scanOverlay.IsOn) AddLog("<color=orange>Scan overlay turned off - no rooms in this scan.</color>");
        }
        if (miniMapOn)
        {
            if (loaded) miniMap.RebuildInPlace(); else miniMap.ForceOff();
            if (!miniMap.IsOn) AddLog("<color=orange>Minimap turned off - no rooms in this scan.</color>");
        }
        if (!dollhouseOn) SetStatus(loaded ? "Ready" : "No rooms");
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
        AddLog(on ? "<color=green>Scan overlay ON</color>" : "<color=red>Scan overlay failed</color>");
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
        await Busy("Saving...");
        string name = await exporter.SaveScanToCache(this);
        SetStatus(name != null ? "Scan saved" : "Save failed");
        RefreshScanLabel();
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
