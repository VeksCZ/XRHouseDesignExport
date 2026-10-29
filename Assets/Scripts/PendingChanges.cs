using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// What hasn't been saved or exported yet, so the menu can light up "Save live scan" / "Export" instead of relying on
/// memory. Export: any change since the last export (routes, boxes, edits, room names, photos) - kept in PlayerPrefs,
/// so it survives a restart. Save live scan: the live device scan has a room that no saved scan has. Edits, routes and
/// room names of a saved scan save themselves - there is nothing to "save" for them.
/// </summary>
public static class PendingChanges
{
    const string ExportKey = "Pending.Export";

    static HashSet<string> exportKinds;
    static HashSet<string> Kinds
    {
        get
        {
            if (exportKinds == null)
            {
                string s = PlayerPrefs.GetString(ExportKey, "");
                exportKinds = new HashSet<string>(s.Split(',').Where(x => x.Length > 0));
            }
            return exportKinds;
        }
    }

    /// <summary>True when something changed since the last export.</summary>
    public static bool ExportPending => Kinds.Count > 0;
    /// <summary>What changed, e.g. "routes, photos".</summary>
    public static string ExportWhat => string.Join(", ", Kinds.OrderBy(x => x));

    /// <summary>Records a change that the next export should pick up (main thread only).</summary>
    public static void Mark(string what)
    {
        if (string.IsNullOrEmpty(what) || !Kinds.Add(what)) return;
        PlayerPrefs.SetString(ExportKey, string.Join(",", Kinds));
        PlayerPrefs.Save();
    }

    public static void Exported()
    {
        Kinds.Clear();
        PlayerPrefs.DeleteKey(ExportKey);
        PlayerPrefs.Save();
    }

    // ---------- the live scan: is it saved somewhere already? ----------
    // Not a fingerprint of the last save (that lit "Save live scan" in the flat because the house was saved last):
    // the live scan counts as saved when some saved scan holds all of its rooms (same room ids).
    static volatile int liveState;      // 0 unknown, 1 saved, 2 not saved
    static int liveCheck;

    /// <summary>True when the live scan has a room no saved scan has (a new Space Setup worth saving).</summary>
    public static bool LiveUnsaved => liveState == 2;

    /// <summary>Both spellings of a room/anchor id: C# Guid order and the native (byte) order the scene JSON may use.</summary>
    public static (string a, string b) IdForms(System.Guid g)
    {
        string hex = System.BitConverter.ToString(g.ToByteArray()).Replace("-", "").ToLowerInvariant();
        string native = $"{hex.Substring(0, 8)}-{hex.Substring(8, 4)}-{hex.Substring(12, 4)}-{hex.Substring(16, 4)}-{hex.Substring(20)}";
        return (g.ToString(), native);
    }

    /// <summary>The newest saved scan that holds all these rooms (null if none). Reads the saved JSONs - call off
    /// the main thread when it can wait.</summary>
    public static string ScanWithRooms(List<(string a, string b)> ids, List<(string name, string path)> scans)
    {
        if (ids == null || ids.Count == 0) return null;
        foreach (var (name, path) in scans)
        {
            try
            {
                string text = System.IO.File.ReadAllText(path);
                if (ids.All(p => text.Contains(p.a) || text.Contains(p.b))) return name;
            }
            catch (System.Exception) { }
        }
        return null;
    }

#if META_XR_SDK_INSTALLED
    public static List<(string a, string b)> RoomIds(IEnumerable<Meta.XR.MRUtilityKit.MRUKRoom> rooms) =>
        rooms.Where(r => r != null).Select(r => IdForms(r.Anchor.Uuid)).ToList();

    /// <summary>The live scan was just read from the device: find out (in the background) whether it is saved.</summary>
    public static void CheckLive(IEnumerable<Meta.XR.MRUtilityKit.MRUKRoom> rooms)
    {
        var ids = RoomIds(rooms);
        int my = ++liveCheck;
        if (ids.Count == 0) { liveState = 0; return; }
        var scans = MRUKSceneCache.ListCachedScans().Select(n => (n, MRUKSceneCache.ScanPath(n))).ToList();
        System.Threading.Tasks.Task.Run(() =>
        {
            string hit = ScanWithRooms(ids, scans);
            if (my == liveCheck) liveState = hit != null ? 1 : 2;
        });
    }
#endif

    /// <summary>The live scan was just saved.</summary>
    public static void LiveSaved()
    {
        liveCheck++;
        liveState = 1;
        Mark("scan");
    }
}
