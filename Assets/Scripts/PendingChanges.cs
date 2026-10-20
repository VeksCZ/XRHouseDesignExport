using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// What hasn't been saved or exported yet, so the menu can light up "Save scan" / "Export" instead of relying on
/// memory. Export: any change since the last export (routes, boxes, edits, room names, photos). Save scan: the live
/// device scan differs from the last one saved (a new Space Setup). Kept in PlayerPrefs, so it survives a restart.
/// </summary>
public static class PendingChanges
{
    const string ExportKey = "Pending.Export", LiveSigKey = "Pending.LiveSig", SavedSigKey = "Pending.SavedLiveSig";

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

    /// <summary>True when the live scan (as last seen) differs from the one last saved.</summary>
    public static bool SavePending
    {
        get
        {
            string live = PlayerPrefs.GetString(LiveSigKey, ""), saved = PlayerPrefs.GetString(SavedSigKey, "");
            return live.Length > 0 && saved.Length > 0 && live != saved;
        }
    }

    /// <summary>The live scan was just read from the device. The first time ever it counts as saved (nothing to
    /// compare with); after that a different scan means it should be saved.</summary>
    public static void SeenLive(string sig)
    {
        if (string.IsNullOrEmpty(sig)) return;
        PlayerPrefs.SetString(LiveSigKey, sig);
        if (!PlayerPrefs.HasKey(SavedSigKey)) PlayerPrefs.SetString(SavedSigKey, sig);
        PlayerPrefs.Save();
    }

    public static void SavedLive(string sig)
    {
        if (string.IsNullOrEmpty(sig)) return;
        PlayerPrefs.SetString(LiveSigKey, sig);
        PlayerPrefs.SetString(SavedSigKey, sig);
        PlayerPrefs.Save();
        Mark("scan");
    }

#if META_XR_SDK_INSTALLED
    /// <summary>A fingerprint of the rooms MRUK holds: room ids, anchors and their sizes/positions in the room
    /// (cm) - independent of where tracking put the house today.</summary>
    public static string Signature(IEnumerable<Meta.XR.MRUtilityKit.MRUKRoom> rooms)
    {
        var parts = new List<string>();
        foreach (var r in rooms)
        {
            if (r == null) continue;
            var anchors = new List<string>();
            foreach (var a in r.Anchors)
            {
                if (a == null) continue;
                Vector3 p = r.transform.InverseTransformPoint(a.transform.position);
                Vector2 sz = a.PlaneRect.HasValue ? a.PlaneRect.Value.size : Vector2.zero;
                Vector3 vs = a.VolumeBounds.HasValue ? a.VolumeBounds.Value.size : Vector3.zero;
                anchors.Add($"{a.Label}:{Mathf.RoundToInt(p.x * 100)},{Mathf.RoundToInt(p.y * 100)},{Mathf.RoundToInt(p.z * 100)}:" +
                            $"{Mathf.RoundToInt(sz.x * 100)}x{Mathf.RoundToInt(sz.y * 100)}:{Mathf.RoundToInt(vs.x * 100)}x{Mathf.RoundToInt(vs.y * 100)}x{Mathf.RoundToInt(vs.z * 100)}");
            }
            anchors.Sort(System.StringComparer.Ordinal);
            parts.Add(DoorCatalog.RoomId(r) + "|" + string.Join(";", anchors));
        }
        parts.Sort(System.StringComparer.Ordinal);
        string all = string.Join("#", parts);
        using var sha = System.Security.Cryptography.SHA1.Create();
        var h = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(all));
        return System.BitConverter.ToString(h, 0, 8).Replace("-", "");
    }
#endif
}
