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

    // Canvas units; the canvas is scaled to CanvasScale metres per unit (600 units -> 36 cm at 0.75 m from the eyes).
    const float HeaderH = 40f;          // the draggable handle strip at the very top
    const float CanvasWidth = 600f, CanvasHeight = 776f + HeaderH, CanvasScale = 0.0006f;
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
    // On/off toggles get their tint kept in sync every frame (see SyncToggleTints) instead of set at each call
    // site - a toggle can also turn itself off from elsewhere (the floor plan panel's own Close button, or a
    // scan-source change invalidating the scan overlay/minimap), and a per-frame sync can never go stale the
    // way scattered manual updates did.
    Button dollhouseButton, plansButton, scanOverlayButton, miniMapButton;
    XRRayInteractor rightRay;
    float flickCooldown;
    float menuYaw;
    bool menuPlaced, menuFollowing;
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

        float rowY = top + 4 * (h + gap);
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
        if (menuManuallyPlaced) return;

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
        if (OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch)) ClickUnderRay();

        // Right hand: B = open/close the whole menu, A = dollhouse on/off, stick left/right = dollhouse mode
        // (only while it's on and floor plans are closed - otherwise the stick pages floor plans instead).
        // Export used to be on B; reaching for the menu to use anything else on it anyway made a dedicated
        // shortcut for Export, at the cost of one for closing the menu, not worth it - Export is still one
        // click away on the menu itself.
        if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.RTouch)) OnToggleMenu();
        if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.RTouch)) OnToggleHouseView();
        HandleMenuDrag();

        // Left hand: Y = save log, X = save scan, stick click = floor plans, stick left/right = scan source.
        if (OVRInput.GetDown(OVRInput.Button.Two, OVRInput.Controller.LTouch)) SaveLogToFile();
        if (OVRInput.GetDown(OVRInput.Button.One, OVRInput.Controller.LTouch)) OnSaveScan();
        if (OVRInput.GetDown(OVRInput.Button.PrimaryThumbstick, OVRInput.Controller.LTouch)) OnShowPlans();

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
    }

    /// <summary>Opens/closes the whole wrist menu (bound to B - see the comment where it's read).</summary>
    public void OnToggleMenu()
    {
        if (menuCanvas == null) return;
        menuCanvas.gameObject.SetActive(!menuCanvas.gameObject.activeSelf);
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
            if (grip && Physics.Raycast(menuHand.position, menuHand.forward, out RaycastHit hit) && hit.collider.gameObject == menuHandle)
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
            if (!grip) { menuGrabbed = false; return; }
            menuCanvas.transform.position = menuHand.TransformPoint(menuGrabOff);
            menuCanvas.transform.rotation = menuHand.rotation * menuGrabRotOff;
        }
    }
    void StopMenuVib() => OVRInput.SetControllerVibration(0, 0, OVRInput.Controller.RTouch);

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

    async void OnCycleSource(int direction)
    {
        if (!RequireExporter()) return;
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
    public void OnCycleDollhouseMode()
    {
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
