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
    public List<HoleEdit> holes = new List<HoleEdit>();
}

/// <summary>
/// An opening drawn by the user into a floor/ceiling (typically the stairwell): a rectangle, square to the house's
/// walls, given by two opposite corners on the surface it was drawn on - the ceiling from below or the floor from
/// above. It cuts every horizontal surface within ~0.6 m of that height, i.e. both the ceiling of the story below
/// and the floor slab of the story above. Corners room-local like the other edits.
/// </summary>
[Serializable]
public class HoleEdit
{
    public string id;
    public string roomUuid;
    public Vector3Data localA, localB;
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

/// <summary>
/// What fills an opening. Doors: Single, Double, Sliding, Opening (empty), Garage (roller door rolling up).
/// Windows: Fixed, Single (side-hung casement), Double, Tilt (bottom-hung, top tips in), Sliding.
/// Both: Wall = there is nothing here at all (a false detection) - the opening is walled up.
/// Values are stored as numbers in the edits file - only ever append.
/// </summary>
public enum DoorKind { Single, Double, Sliding, Opening, Wall, Garage, Fixed, Tilt, TiltTurn }

/// <summary>One part of a window divided into parts side by side (left to right as seen from the room the window
/// opens into): Fixed, TiltTurn (otvíravě-sklopné), Single (casement), Tilt, Sliding (HS portal - slides in front
/// of its neighbour). hinge: which side it's hinged on - or, for Sliding, which way it slides.</summary>
[Serializable]
public class SectionEdit
{
    public DoorKind kind = DoorKind.Fixed;
    public HingeSide hinge = HingeSide.Left;
}
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
    /// <summary>Windows: the parts it's divided into, equal widths, left to right. Empty = one part of 'kind'.</summary>
    public List<SectionEdit> sections = new List<SectionEdit>();
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

    public static string PathFor(string scanName) => MRUKSceneCache.EditsPath(scanName);

    /// <summary>File the current edits are read from / saved to (null while the live scan is active).</summary>
    static string currentPath;

    /// <summary>Switches to the edits of the given saved scan (null = live scan, no edits). Clears undo history.
    /// 'path' overrides where its edits file is (e.g. the batch export binding an arbitrary scan's 93_Data_Edits.json).</summary>
    public static void Bind(string scanName, string path = null)
    {
        path = scanName == null ? null : path ?? PathFor(scanName);
        if (scanName == CurrentScan && path == currentPath) return;
        CurrentScan = scanName;
        currentPath = path;
        undo.Clear(); redo.Clear();
        Current = scanName == null ? new HouseEdits() : Load(scanName, path);
        Changed?.Invoke();
    }

    /// <summary>True if the current edits hold anything worth writing out.</summary>
    public static bool HasAny => Current.doors.Count + Current.stairs.Count + Current.holes.Count > 0;

    static HouseEdits Load(string scanName, string path)
    {
        if (File.Exists(path))
        {
            try
            {
                var e = JsonUtility.FromJson<HouseEdits>(File.ReadAllText(path));
                if (e != null) { e.doors ??= new List<DoorEdit>(); e.stairs ??= new List<StairEdit>(); e.holes ??= new List<HoleEdit>(); return e; }
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
        try { File.WriteAllText(currentPath ?? PathFor(CurrentScan), JsonUtility.ToJson(Current, true)); }
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
            currentPath = null;
            undo.Clear(); redo.Clear();
            Current = new HouseEdits();
            Changed?.Invoke();
        }
    }
}
