using System;
using System.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Edit mode: switched on/off by holding Y (left controller) for a second, so it never gets in the way of plain
/// walking/viewing. While on, a small toolbar rides above the left controller (tool picker, Undo/Redo, Done) and
/// the right trigger acts for the selected tool instead of entering the walk-through.
/// Edits always belong to a saved scan: the first change made on the live scan saves it first and switches the
/// app over to that saved copy (EnsureEditable), so nothing done here is lost when the live scan changes.
/// The tools themselves (doors, stairs) plug in through CurrentTool / TriggerPressed.
/// </summary>
public class EditModeController : MonoBehaviour
{
    public enum Tool { None, Doors, Stairs }

    public XRMenu uiLog;
    public MRUKExporter exporter;

    const float CanvasW = 560f, CanvasH = 190f, CanvasScale = 0.00032f; // ~18 x 6 cm above the left controller

    Canvas toolbar;
    TMP_Text infoText;
    Button doorsButton, stairsButton, undoButton, redoButton;
    Transform leftHand;
    bool saving;

    public bool IsOn { get; private set; }
    public Tool CurrentTool { get; private set; } = Tool.None;
    /// <summary>Right trigger pressed while edit mode is on and the ray isn't on any UI - for the active tool.</summary>
    public event Action TriggerPressed;

    void OnEnable() { HouseEditsStore.Changed += RefreshInfo; }
    void OnDisable() { HouseEditsStore.Changed -= RefreshInfo; }

    public void Toggle()
    {
        IsOn = !IsOn;
        if (IsOn) { BuildToolbar(); toolbar.gameObject.SetActive(true); }
        else { CurrentTool = Tool.None; if (toolbar) toolbar.gameObject.SetActive(false); }
        RefreshInfo();
        Haptic(IsOn ? 0.6f : 0.3f);
        uiLog?.AddLog(IsOn ? "<color=orange>EDIT mode ON</color> - hold Y again to leave" : "Edit mode OFF");
    }

    /// <summary>Called by XRMenu for a trigger press not used by any UI.</summary>
    public void OnTrigger()
    {
        if (!IsOn) return;
        if (CurrentTool == Tool.None) { uiLog?.AddLog("Edit: pick a tool on the left-hand toolbar first."); return; }
        TriggerPressed?.Invoke();
    }

    /// <summary>
    /// Makes sure edits can be stored: a saved scan must be active. On the live scan it is saved first and the app
    /// switches to the saved copy. False if that's not possible (nothing loaded, save failed).
    /// </summary>
    public async Task<bool> EnsureEditable()
    {
        if (HouseEditsStore.CanEdit) return true;
        if (exporter == null || saving) return false;
        if (exporter.ActiveSource != MRUKExporter.LiveSource)
        {
            uiLog?.AddLog("<color=red>Edit: load a scan first.</color>");
            return false;
        }
        saving = true;
        try
        {
            uiLog?.AddLog("Edit on the live scan - saving it first, your edits will belong to the saved copy...");
            string name = await exporter.SaveScanToCache(uiLog);
            if (name == null) { uiLog?.AddLog("<color=red>Edit: could not save the live scan - edit not applied.</color>"); return false; }
            exporter.AdoptSavedLiveScan(name);
            uiLog?.RefreshSourceLabel();
            uiLog?.AddLog($"<color=green>Edits now go to saved scan '{name}'.</color>");
            return true;
        }
        finally { saving = false; }
    }

    /// <summary>The one way tools change anything: ensures a saved scan, applies the change (undoable), saves.</summary>
    public async Task<bool> Commit(Action<HouseEdits> change)
    {
        if (!await EnsureEditable()) return false;
        bool ok = HouseEditsStore.Apply(change);
        if (ok) Haptic(0.4f);
        return ok;
    }

    void SelectTool(Tool t)
    {
        CurrentTool = CurrentTool == t ? Tool.None : t;
        if (CurrentTool == Tool.Stairs) uiLog?.AddLog("Stairs: trigger on the floor under the first step, each landing's middle, the top end - then Finish.");
        if (CurrentTool == Tool.Doors) uiLog?.AddLog("Doors: point at a door + trigger to pick it, then set it on the panel.");
        RefreshInfo();
    }

    void OnUndo() { if (HouseEditsStore.Undo()) uiLog?.AddLog("Undo"); else uiLog?.AddLog("Nothing to undo."); }
    void OnRedo() { if (HouseEditsStore.Redo()) uiLog?.AddLog("Redo"); else uiLog?.AddLog("Nothing to redo."); }

    void BuildToolbar()
    {
        if (toolbar) return;
        var rig = FindFirstObjectByType<OVRCameraRig>();
        leftHand = rig ? rig.leftHandAnchor : null;

        toolbar = XRUi.CreateWorldCanvas("EditToolbar", new Vector2(CanvasW, CanvasH), CanvasScale, leftHand);
        var t = toolbar.transform;
        // Just above the controller, facing back at you (a world canvas reads from its -Z side, and the hand's
        // forward points away from you), tipped back a little towards the eyes.
        t.localPosition = new Vector3(0f, 0.07f, 0.03f);
        t.localRotation = Quaternion.Euler(-25f, 0f, 0f);

        XRUi.CreatePanel(t, "Background", new Color(0.30f, 0.17f, 0.03f, 0.94f), 0, 0, CanvasW, CanvasH);
        XRUi.CreateText(t, "Title", "EDIT", 26, TextAlignmentOptions.MidlineLeft, 14, 6, 90, 36, new Color(1f, 0.7f, 0.25f), FontStyles.Bold);
        infoText = XRUi.CreateText(t, "Info", "", 20, TextAlignmentOptions.MidlineRight, 100, 6, CanvasW - 114, 36, XRUi.MutedText);

        const float y = 52, h = 60, g = 8;
        float w = (CanvasW - 14 * 2 - g * 4) / 5f;
        float x = 14;
        doorsButton = XRUi.CreateButton(t, "Doors", x, y, w, h, () => SelectTool(Tool.Doors), 22); x += w + g;
        stairsButton = XRUi.CreateButton(t, "Stairs", x, y, w, h, () => SelectTool(Tool.Stairs), 22); x += w + g;
        undoButton = XRUi.CreateButton(t, "Undo", x, y, w, h, OnUndo, 22); x += w + g;
        redoButton = XRUi.CreateButton(t, "Redo", x, y, w, h, OnRedo, 22); x += w + g;
        XRUi.CreateButton(t, "Done", x, y, w, h, Toggle, 22);

        XRUi.CreateText(t, "Hint", "Right trigger = use tool  •  hold Y = leave", 18, TextAlignmentOptions.Center, 14, y + h + 10, CanvasW - 28, 50, XRUi.MutedText);
    }

    void RefreshInfo()
    {
        if (!toolbar) return;
        XRUi.SetTint(doorsButton, CurrentTool == Tool.Doors ? XRUi.ButtonOnColor : XRUi.ButtonToggleColor);
        XRUi.SetTint(stairsButton, CurrentTool == Tool.Stairs ? XRUi.ButtonOnColor : XRUi.ButtonToggleColor);
        undoButton.interactable = HouseEditsStore.UndoCount > 0;
        redoButton.interactable = HouseEditsStore.RedoCount > 0;
        string scan = HouseEditsStore.CurrentScan ?? "live scan (saved on first edit)";
        infoText.text = $"{scan}  •  {HouseEditsStore.Current.doors.Count} door(s), {HouseEditsStore.Current.stairs.Count} stair(s)";
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
