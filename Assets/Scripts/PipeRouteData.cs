using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>What runs in a route. Stored as a number - only ever append.</summary>
public enum RouteKind { Electric, ColdWater, HotWater, Waste, Heating, Other }

/// <summary>
/// One installation route traced in the house (edit-mode tool "Routes"): a cable chase, a water or waste pipe, a
/// heating pipe - as a polyline along its centre. Points are room-local to roomUuid (the room of its first point), like
/// HouseEdits, so it lands in place in any loaded copy of the scan that has that room.
/// </summary>
[Serializable]
public class PipeRoute
{
    public string id;
    public RouteKind kind;
    /// <summary>Outer diameter in metres (0 = the kind's default).</summary>
    public float diameter;
    public string roomUuid;
    public List<Vector3Data> localPoints = new List<Vector3Data>();
    /// <summary>What the ends connect to ("Sink", "WC", "Floor drain"...), empty = nothing said.</summary>
    public string startLabel, endLabel;
    public string created;
    /// <summary>Found in the wall photos by RouteDetector, not confirmed yet (drawn faint/dashed until accepted).</summary>
    public bool proposed;
    /// <summary>Proposals: how sure the detector was (0..1) and where it came from.</summary>
    public float confidence;
    public string source;
    /// <summary>What it is made of ("CYKY 3x2,5", "PPR 20", "HT 110"...), the circuit / breaker it belongs to ("5"),
    /// a free note (PC) - shown when you point at it and in the drawings.</summary>
    public string spec, circuit, note;
    /// <summary>Last change (ISO) - "Pull" keeps the newer copy of a route/box edited on both sides.</summary>
    public string modified;
}

/// <summary>A point item on a route or on its own: an electrical box, socket, switch, a water/waste outlet, a floor
/// drain... - position on the surface (room-local, like routes) and the surface's normal there.</summary>
[Serializable]
public class RouteMarker
{
    public string id;
    public RouteKind kind;
    public string type;
    public string roomUuid;
    public Vector3Data localPos, localNormal;
    public string label;
    public string created;
    /// <summary>Circuit / breaker of an electrical box ("5"), as for routes.</summary>
    public string circuit;
    public string modified;
}

[Serializable]
public class PipeRouteFile
{
    public string schemaVersion = "1";
    public string modified;
    public List<PipeRoute> routes = new List<PipeRoute>();
    public List<RouteMarker> markers = new List<RouteMarker>();
    /// <summary>Routes / boxes deleted (id + when): without it a merge with the other side's file - which still has them -
    /// brings every deleted route back.</summary>
    public List<DeletedItem> deleted = new List<DeletedItem>();
}

[Serializable]
public class DeletedItem
{
    public string id;
    public string at;   // ISO, like 'modified'
}

/// <summary>The merge of two routes files ("Pull from Quest" merges the headset's with the PC's and writes the result to
/// both). Pure data, no Unity API.</summary>
public static class RouteMerge
{
    const int MaxDeleted = 1000;

    /// <summary>Marks every id in 'idsBefore' that is no longer in the file as deleted at 'now'; an id that is back loses its mark.</summary>
    public static void NoteDeletions(PipeRouteFile f, IEnumerable<string> idsBefore, string now)
    {
        f.deleted ??= new List<DeletedItem>();
        var present = new HashSet<string>(f.routes.Where(r => r.id != null).Select(r => r.id));
        foreach (var m in f.markers) if (m.id != null) present.Add(m.id);
        foreach (var id in idsBefore)
        {
            if (present.Contains(id)) continue;
            f.deleted.RemoveAll(d => d.id == id);
            f.deleted.Add(new DeletedItem { id = id, at = now });
        }
        f.deleted.RemoveAll(d => present.Contains(d.id));   // re-added (undo, a new route with the same id)
        if (f.deleted.Count > MaxDeleted) f.deleted = f.deleted.OrderBy(d => d.at, StringComparer.Ordinal).Skip(f.deleted.Count - MaxDeleted).ToList();
    }

    /// <summary>Routes and boxes of both files (same id: the newer edit wins). Deletions of either side count: an item
    /// whose last edit is not newer than its deletion is dropped; one edited after it stays.</summary>
    public static PipeRouteFile Merge(PipeRouteFile a, PipeRouteFile b)
    {
        var res = new PipeRouteFile
        {
            routes = new List<PipeRoute>(a?.routes ?? new List<PipeRoute>()),
            markers = new List<RouteMarker>(a?.markers ?? new List<RouteMarker>()),
        };
        foreach (var m in b?.markers ?? new List<RouteMarker>())
        {
            int i = res.markers.FindIndex(x => x.id == m.id);
            if (i < 0) res.markers.Add(m);
            else if (string.CompareOrdinal(m.modified ?? "", res.markers[i].modified ?? "") > 0) res.markers[i] = m;
        }
        foreach (var r in b?.routes ?? new List<PipeRoute>())
        {
            int i = res.routes.FindIndex(x => x.id == r.id);
            if (i < 0) { res.routes.Add(r); continue; }
            // the newer edit wins; without dates (older files) the longer one - routes only grow while drawn
            int c = string.CompareOrdinal(r.modified ?? "", res.routes[i].modified ?? "");
            if (c > 0 || (c == 0 && r.localPoints.Count > res.routes[i].localPoints.Count)) res.routes[i] = r;
        }

        // deletions of both sides: per id the latest
        var latest = new Dictionary<string, string>();
        foreach (var d in (a?.deleted ?? new List<DeletedItem>()).Concat(b?.deleted ?? new List<DeletedItem>()))
            if (d?.id != null && (!latest.TryGetValue(d.id, out var at) || string.CompareOrdinal(d.at ?? "", at ?? "") > 0)) latest[d.id] = d.at;
        res.deleted = new List<DeletedItem>();
        foreach (var kv in latest)
        {
            bool kept = false;
            int ri = res.routes.FindIndex(x => x.id == kv.Key);
            if (ri >= 0)
            {
                if (string.CompareOrdinal(res.routes[ri].modified ?? "", kv.Value ?? "") <= 0) res.routes.RemoveAt(ri); else kept = true;
            }
            int mi = res.markers.FindIndex(x => x.id == kv.Key);
            if (mi >= 0)
            {
                if (string.CompareOrdinal(res.markers[mi].modified ?? "", kv.Value ?? "") <= 0) res.markers.RemoveAt(mi); else kept = true;
            }
            if (!kept) res.deleted.Add(new DeletedItem { id = kv.Key, at = kv.Value });   // an item edited after its deletion is alive again
        }
        res.modified = DateTime.Now.ToString("s");
        return res;
    }
}
