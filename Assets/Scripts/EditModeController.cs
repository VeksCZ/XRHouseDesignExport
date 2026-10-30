using System;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Edit mode: switched on/off by holding Y (left controller) for a second, so it never gets in the way of plain
/// walking/viewing. While on, a small toolbar rides above the left controller (tool picker, Undo/Redo, Done) and
/// the right trigger acts for the selected tool instead of entering the walk-through.
/// Edits of a saved scan save by themselves; edits on the live scan wait in the live edits file until the user saves the
/// live scan (nothing is saved or switched automatically, a saved scan is never touched from the live one).
/// The tools themselves (doors, stairs) plug in through CurrentTool / TriggerPressed.
/// </summary>
public class EditModeController : MonoBehaviour
{
    public enum Tool { None, Doors, Stairs, Holes, Furniture, Measure, Sun, Photos, Routes }

    public XRMenu uiLog;
    public MRUKExporter exporter;

    const float CanvasW = 640f, CanvasH = 132f, CanvasScale = 0.0004f; // ~26 x 5 cm, in front of you, a bit low

    Canvas toolbar;
    TMP_Text titleText, infoText;
    Button undoButton, redoButton;

    public bool IsOn { get; private set; }
    /// <summary>Edit mode is on (e.g. doors in the walk-through then open by themselves).</summary>
    public static bool AnyOn { get; private set; }
    public Tool CurrentTool { get; private set; } = Tool.None;
    /// <summary>Right trigger pressed while edit mode is on and the ray isn't on any UI - for the active tool.</summary>
    public event Action TriggerPressed;

    void OnEnable() { HouseEditsStore.Changed += RefreshInfo; }
    void OnDisable() { HouseEditsStore.Changed -= RefreshInfo; }

    public void Toggle()
    {
        IsOn = !IsOn;
        AnyOn = IsOn;
        openedForTool = false;
        if (IsOn) { BuildToolbar(); toolbar.gameObject.SetActive(true); if (!(toolbarDrag && toolbarDrag.Moved)) FollowHand(); }
        else { CurrentTool = Tool.None; if (toolbar) toolbar.gameObject.SetActive(false); }
        RefreshInfo();
        Haptic(IsOn ? 0.6f : 0.3f);
        if (!IsOn) uiLog?.AddLog("Tool closed.");
    }

    /// <summary>Called by XRMenu for a trigger press not used by any UI.</summary>
    public void OnTrigger()
    {
        if (!IsOn) return;
        if (CurrentTool == Tool.None) { uiLog?.AddLog("Pick a tool in the menu's Tools tab first."); return; }
        TriggerPressed?.Invoke();
    }

    /// <summary>
    /// Makes sure edits can be stored. On the live scan they wait as pending edits (nothing is saved by itself - the Data
    /// tab turns orange and "Save live scan" keeps them in the new scan); on a saved scan they save by themselves.
    /// False if nothing is loaded.
    /// </summary>
    public Task<bool> EnsureEditable()
    {
        if (HouseEditsStore.CanEdit) return Task.FromResult(true);
        uiLog?.AddLog("<color=red>Edit: load a scan first.</color>");
        return Task.FromResult(false);
    }

    /// <summary>
    /// For changes that go into a saved scan's own file (a new doorway, a missing wall): a SAVED scan must be selected.
    /// The live scan is never changed into one behind your back and never touches a saved scan - save it first (Data tab).
    /// </summary>
    public Task<bool> EnsureSavedScan()
    {
        if (HouseEditsStore.CurrentScan != null) return Task.FromResult(true);
        uiLog?.AddLog("<color=orange>The live scan is not saved - it can't take a new doorway or wall. Save it first (menu Data > Save live scan), then pick the saved scan and add it there.</color>");
        return Task.FromResult(false);
    }

    /// <summary>The one way tools change anything: ensures a saved scan, applies the change (undoable), saves.</summary>
    public async Task<bool> Commit(Action<HouseEdits> change)
    {
        if (!await EnsureEditable()) return false;
        bool ok = HouseEditsStore.Apply(change);
        if (ok) Haptic(0.4f);
        return ok;
    }

    bool openedForTool;

    /// <summary>Edit mode on with this tool selected (from the main menu's shortcuts).</summary>
    public void UseTool(Tool t)
    {
        if (!IsOn) { Toggle(); openedForTool = true; }
        if (CurrentTool != t) SelectTool(t);
    }

    /// <summary>A tool panel's Done: leaves the tool. Edit mode opened just for it (the menu's Wall photos / Trace
    /// routes) closes with it; opened by hand, it stays with its toolbar to pick the next tool.</summary>
    public void LeaveTool()
    {
        if (!IsOn) return;
        if (openedForTool) { Toggle(); return; }
        CurrentTool = Tool.None;
        RefreshInfo();
        ShowToolbarForTool();
    }

    /// <summary>Routes and photos have their own panel (with their own Undo and Done) - no second bar for them.</summary>
    void ShowToolbarForTool()
    {
        if (toolbar) toolbar.gameObject.SetActive(IsOn && CurrentTool != Tool.Routes && CurrentTool != Tool.Photos);
    }

    void SelectTool(Tool t)
    {
        CurrentTool = CurrentTool == t ? Tool.None : t;
        if (CurrentTool == Tool.Stairs) uiLog?.AddLog("Stairs: trigger on the floor under the first step, each landing's middle, the top end - then Finish.");
        if (CurrentTool == Tool.Doors) uiLog?.AddLog("Doors: point at a door + trigger to pick it, then set it on the panel.");
        if (CurrentTool == Tool.Holes) uiLog?.AddLog("Opening: two opposite corners on the ceiling (from below) or the floor (from above).");
        if (CurrentTool == Tool.Sun) uiLog?.AddLog("Sun (while walking): sunny patches from the windows - right stick left/right = half an hour, up/down = a month.");
        if (CurrentTool == Tool.Measure) uiLog?.AddLog("Measure: trigger on the start point, then on the end point (walls, floor, furniture) - trigger again starts a new one.");
        if (CurrentTool == Tool.Routes) uiLog?.AddLog("Routes (over passthrough or while walking): pick the kind, then trigger along a pipe or cable - start, every bend, end.");
        if (CurrentTool == Tool.Photos) uiLog?.AddLog("Photos (over passthrough, live scan): look at a bare wall/floor/ceiling and hold still, or trigger - documents cable chases before plastering.");
        if (CurrentTool == Tool.Furniture) uiLog?.AddLog("Furniture (while walking): trigger on a piece picks it up, it follows the ray over the floor; trigger again puts it down. Right stick click turns it 90 degrees.");
        RefreshInfo();
        ShowToolbarForTool();
    }

    void OnUndo() { if (HouseEditsStore.Undo()) uiLog?.AddLog("Undo"); else uiLog?.AddLog("Nothing to undo."); }
    void OnRedo() { if (HouseEditsStore.Redo()) uiLog?.AddLog("Redo"); else uiLog?.AddLog("Nothing to redo."); }

    /// <summary>The bar of the active tool: its name and what the scan holds, Undo / Redo, Done. Tools are picked in the
    /// menu's Tools tab (one place for them, not two).</summary>
    void BuildToolbar()
    {
        if (toolbar) return;
        var rig = FindFirstObjectByType<OVRCameraRig>();

        toolbar = XRUi.CreateWorldCanvas("EditToolbar", new Vector2(CanvasW, CanvasH), CanvasScale);
        head = rig ? rig.centerEyeAnchor : null;
        var t = toolbar.transform;
        FollowHand();
        // Grip-drag it somewhere and it stays there.
        toolbarDrag = PanelDrag.Attach(toolbar, CanvasW, CanvasH);

        XRUi.CreatePanel(t, "Background", XRUi.PanelColor, 0, 0, CanvasW, CanvasH);
        const float pad = 18, h = 56, w = 112, g = 10;
        titleText = XRUi.CreateText(t, "Title", "", 24, TextAlignmentOptions.MidlineLeft, pad + 4, 12, CanvasW - 2 * pad, 32, XRUi.TextColor, FontStyles.Bold);
        infoText = XRUi.CreateText(t, "Info", "", 16, TextAlignmentOptions.MidlineLeft, pad + 4, 44, CanvasW - 2 * pad - 3 * (w + g), 64, XRUi.MutedText);
        infoText.textWrappingMode = TextWrappingModes.Normal;
        float y = CanvasH - pad - h;
        undoButton = XRUi.CreateButton(t, "Undo", CanvasW - pad - 3 * w - 2 * g, y, w, h, OnUndo, 22);
        redoButton = XRUi.CreateButton(t, "Redo", CanvasW - pad - 2 * w - g, y, w, h, OnRedo, 22);
        var done = XRUi.CreateButton(t, "Done", CanvasW - pad - w, y, w, h, LeaveTool, 22);
        XRUi.SetTint(done, XRUi.ButtonOnColor);
    }

    Transform head;

    /// <summary>The toolbar appears just below your line of sight in front of you when edit mode turns on, then
    /// stays put (world-locked, grip-drag to move). It used to ride on the left controller and turned up wherever
    /// the hand happened to be.</summary>
    void FollowHand()
    {
        if (!toolbar || !head) return;
        XRUi.PlaceInFront(toolbar.transform, head, 0.55f, 0.30f);
        toolbarPlaced = true;
    }
    bool toolbarPlaced;

    void LateUpdate()
    {
        // Only re-placed when you've walked away from it (and haven't put it somewhere yourself).
        if (IsOn && toolbar && toolbar.gameObject.activeSelf && !(toolbarDrag && toolbarDrag.Moved)
            && (!toolbarPlaced || XRUi.LeftBehind(toolbar.transform, head))) FollowHand();
    }

    PanelDrag toolbarDrag;

    void RefreshInfo()
    {
        if (!toolbar) return;
        titleText.text = CurrentTool switch
        {
            Tool.Doors => "Doors & windows", Tool.Stairs => "Stairs", Tool.Holes => "Openings", Tool.Furniture => "Furniture",
            Tool.Measure => "Measure", Tool.Sun => "Sun", Tool.Photos => "Photos / 3D scan", Tool.Routes => "Routes",
            _ => "Pick a tool in the menu (Tools)",
        };
        XRUi.SetTint(undoButton, HouseEditsStore.UndoCount > 0 ? XRUi.ButtonColor : XRUi.ButtonDisabled);
        XRUi.SetTint(redoButton, HouseEditsStore.RedoCount > 0 ? XRUi.ButtonColor : XRUi.ButtonDisabled);
        undoButton.interactable = HouseEditsStore.UndoCount > 0;
        redoButton.interactable = HouseEditsStore.RedoCount > 0;
        string scan = HouseEditsStore.CurrentScan ?? "live scan (saved on the first edit)";
        infoText.text = $"{scan}\n{HouseEditsStore.Current.doors.Count} door(s), {HouseEditsStore.Current.stairs.Count} stair(s), {HouseEditsStore.Current.holes.Count} opening(s)  ·  trigger = use the tool";
    }

    void Haptic(float strength)
    {
        OVRInput.SetControllerVibration(0.2f, strength, OVRInput.Controller.LTouch);
        Invoke(nameof(StopHaptic), 0.08f);
    }
    void StopHaptic() => OVRInput.SetControllerVibration(0, 0, OVRInput.Controller.LTouch);

    void OnDestroy()
    {
        if (toolbar) Destroy(toolbar.gameObject);
    }
}
