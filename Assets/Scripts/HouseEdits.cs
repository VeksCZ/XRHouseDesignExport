using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// The user's own changes to a saved scan - everything MRUK doesn't capture (door hinges/swing, stairs, ...).
/// Kept in a sidecar file next to the scan (Name.scene.json + Name.edits.json), so the scan itself is never
/// touched and the edits can always be thrown away or carried separately.
/// Positions are stored relative to the MRUK room they belong to (room UUID + room-local position): a saved scan
/// can come back from JSON in a different world frame each load, but its rooms and anchor UUIDs don't change.
/// </summary>
[Serializable]
public class HouseEdits
{
    public string schemaVersion = "1";
    public string scanName;
    public string modified;
    public List<DoorEdit> doors = new List<DoorEdit>();
    public List<StairEdit> stairs = new List<StairEdit>();
}

/// <summary>
/// A staircase drawn along its walking line: points[0] = bottom of the first step, then the middle of each landing,
/// last = top end of the last step (all on the plan, at the bottom floor's height). Flights run between them, a
/// square landing (width x width) sits on every middle point. Stored room-local to roomUuid (the room at the
/// bottom), like DoorEdit, so it survives the scan being loaded in a different world frame.
/// </summary>
[Serializable]
public class StairEdit
{
    public string id;
    public string roomUuid;
    public List<Vector3Data> localPoints = new List<Vector3Data>();
    public float width = 1.0f;
    /// <summary>Risers per flight (points.Count - 1 flights). The rise per step is the floor-to-floor height
    /// divided by their sum, so the landings' heights follow from these counts.</summary>
    public List<int> risers = new List<int>();
}

public enum DoorKind { Single, Double, Sliding, Opening }
public enum HingeSide { Left, Right }

/// <summary>One door's user-defined properties. Matched to its door by anchorUuid first; roomUuid + localPos is
/// the fallback (a shared doorway has one anchor per room, and only one of them may have been edited).</summary>
[Serializable]
public class DoorEdit
{
    public string anchorUuid;
    public string roomUuid;
    public Vector3Data localPos;
    public DoorKind kind = DoorKind.Single;
    /// <summary>Hinge side as seen standing in opensIntoRoomUuid, looking at the door.</summary>
    public HingeSide hinge = HingeSide.Left;
    /// <summary>The room the leaf swings into.</summary>
    public string opensIntoRoomUuid;
}

/// <summary>
/// Loads/saves the edits of whichever saved scan is active, with undo/redo (whole-document snapshots - the
/// document is tiny). Every change goes through Apply(), which saves immediately and raises Changed so views
/// (walk-through, Dollhouse, floor plans) can refresh. The live scan has no edits of its own: the first change
/// made on it saves it as a scan first (see EditModeController.EnsureEditable).
/// </summary>
public static class HouseEditsStore
{
    public const string EXTENSION = ".edits.json";
    const int MaxUndo = 50;

    static readonly List<string> undo = new List<string>(), redo = new List<string>();

    /// <summary>Saved scan the current edits belong to; null while the live scan (or nothing) is active.</summary>
    public static string CurrentScan { get; private set; }
    public static HouseEdits Current { get; private set; } = new HouseEdits();
    public static bool CanEdit => CurrentScan != null;
    public static int UndoCount => undo.Count;
    public static int RedoCount => redo.Count;
    public static event Action Changed;

    public static string PathFor(string scanName) => Path.Combine(MRUKSceneCache.GetCacheRoot(), scanName + EXTENSION);

    /// <summary>Switches to the edits of the given saved scan (null = live scan, no edits). Clears undo history.</summary>
    public static void Bind(string scanName)
    {
        if (scanName == CurrentScan) return;
        CurrentScan = scanName;
        undo.Clear(); redo.Clear();
        Current = scanName == null ? new HouseEdits() : Load(scanName);
        Changed?.Invoke();
    }

    static HouseEdits Load(string scanName)
    {
        string path = PathFor(scanName);
        if (File.Exists(path))
        {
            try
            {
                var e = JsonUtility.FromJson<HouseEdits>(File.ReadAllText(path));
                if (e != null) { e.doors ??= new List<DoorEdit>(); e.stairs ??= new List<StairEdit>(); return e; }
            }
            catch (Exception ex)
            {
                // Never silently overwrite a file we couldn't read - keep a copy of it before starting fresh.
                Debug.LogException(ex);
                try { File.Copy(path, path + ".broken", true); } catch { }
            }
        }
        return new HouseEdits { scanName = scanName };
    }

    /// <summary>Applies one change (undoable) and saves. False if no saved scan is active.</summary>
    public static bool Apply(Action<HouseEdits> change)
    {
        if (CurrentScan == null || change == null) return false;
        undo.Add(JsonUtility.ToJson(Current));
        if (undo.Count > MaxUndo) undo.RemoveAt(0);
        redo.Clear();
        change(Current);
        SaveAndNotify();
        return true;
    }

    public static bool Undo() => Step(undo, redo);
    public static bool Redo() => Step(redo, undo);

    static bool Step(List<string> from, List<string> to)
    {
        if (CurrentScan == null || from.Count == 0) return false;
        to.Add(JsonUtility.ToJson(Current));
        Current = JsonUtility.FromJson<HouseEdits>(from[from.Count - 1]);
        from.RemoveAt(from.Count - 1);
        SaveAndNotify();
        return true;
    }

    static void SaveAndNotify()
    {
        Current.scanName = CurrentScan;
        Current.modified = DateTime.Now.ToString("s");
        try { File.WriteAllText(PathFor(CurrentScan), JsonUtility.ToJson(Current, true)); }
        catch (Exception ex) { Debug.LogException(ex); }
        Changed?.Invoke();
    }

    /// <summary>Removes a scan's edits file (when the scan itself is deleted).</summary>
    public static void DeleteFor(string scanName)
    {
        if (string.IsNullOrEmpty(scanName)) return;
        string path = PathFor(scanName);
        if (File.Exists(path)) File.Delete(path);
        if (scanName == CurrentScan)
        {
            CurrentScan = null;
            undo.Clear(); redo.Clear();
            Current = new HouseEdits();
            Changed?.Invoke();
        }
    }
}
