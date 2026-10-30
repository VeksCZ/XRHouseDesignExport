using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

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
}

/// <summary>Colours, names and sizes of the route kinds.</summary>
public static class RouteStyle
{
    public static readonly RouteKind[] Kinds = (RouteKind[])Enum.GetValues(typeof(RouteKind));

    public static string Name(RouteKind k) => k switch
    {
        RouteKind.Electric => "Electric",
        RouteKind.ColdWater => "Cold water",
        RouteKind.HotWater => "Hot water",
        RouteKind.Waste => "Waste",
        RouteKind.Heating => "Heating",
        _ => "Other",
    };

    /// <summary>Czech names for the drawings (the report is for the house, the app UI stays English).</summary>
    public static string NameCz(RouteKind k) => k switch
    {
        RouteKind.Electric => "Elektro",
        RouteKind.ColdWater => "Studená voda",
        RouteKind.HotWater => "Teplá voda",
        RouteKind.Waste => "Odpad",
        RouteKind.Heating => "Topení",
        _ => "Jiné",
    };

    public static Color Color(RouteKind k) => k switch
    {
        RouteKind.Electric => new Color(1.00f, 0.72f, 0.05f),
        RouteKind.ColdWater => new Color(0.18f, 0.48f, 1.00f),
        RouteKind.HotWater => new Color(0.90f, 0.18f, 0.18f),
        RouteKind.Waste => new Color(0.55f, 0.36f, 0.20f),
        RouteKind.Heating => new Color(0.80f, 0.20f, 0.65f),
        _ => new Color(0.25f, 0.70f, 0.30f),
    };

    public static string Hex(RouteKind k) => "#" + ColorUtility.ToHtmlStringRGB(Color(k));

    /// <summary>Common outer diameters (m), the first is the default.</summary>
    public static float[] Diameters(RouteKind k) => k switch
    {
        RouteKind.Electric => new[] { 0.020f, 0.016f, 0.025f, 0.032f },
        RouteKind.ColdWater or RouteKind.HotWater => new[] { 0.020f, 0.016f, 0.025f, 0.032f },
        RouteKind.Waste => new[] { 0.050f, 0.032f, 0.040f, 0.075f, 0.110f },
        RouteKind.Heating => new[] { 0.016f, 0.020f, 0.025f },
        _ => new[] { 0.020f, 0.032f, 0.050f },
    };

    public static float Diameter(PipeRoute r) => r.diameter > 0.001f ? r.diameter : Diameters(r.kind)[0];

    /// <summary>Preset end labels (what a route leads to) - picked on the panel, no keyboard needed.</summary>
    public static readonly string[] EndLabels =
    {
        "", "Sink", "WC", "Shower", "Bath", "Floor drain", "Washer", "Dishwasher", "Kitchen sink", "Boiler",
        "Radiator", "Manifold", "Riser", "Socket", "Switch", "Light", "Panel", "Outside",
    };

    /// <summary>What a route is made of - picked on the panel (first = not said).</summary>
    public static string[] Specs(RouteKind k) => k switch
    {
        RouteKind.Electric => new[] { "", "CYKY 3x1,5", "CYKY 3x2,5", "CYKY 5x1,5", "CYKY 5x2,5", "CYKY 2x1,5", "CYKY 4x1,5", "CYKY 5x4", "CYKY 5x6", "UTP Cat6", "Koax", "Chránička" },
        RouteKind.ColdWater or RouteKind.HotWater => new[] { "", "PPR 20", "PPR 25", "PPR 32", "PEX-AL 16", "PEX-AL 20", "PEX-AL 26", "Cu 15", "Cu 18" },
        RouteKind.Waste => new[] { "", "HT 32", "HT 40", "HT 50", "HT 75", "HT 110", "KG 110", "KG 125", "KG 160" },
        RouteKind.Heating => new[] { "", "PEX 16 podlahovka", "PEX 17 podlahovka", "PEX-AL 16", "PEX-AL 20", "Cu 15", "Cu 18" },
        _ => new[] { "", "Chránička 25", "Chránička 32", "Chránička 50" },
    };

    /// <summary>Circuit / breaker numbers offered (as text, "" = not said).</summary>
    public const int MaxCircuit = 40;

    /// <summary>One line for labels: "CYKY 3x2,5 · circuit 5 · -> Socket".</summary>
    public static string Describe(PipeRoute r, bool cz = false)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(r.spec)) parts.Add(r.spec);
        if (!string.IsNullOrEmpty(r.circuit)) parts.Add((cz ? "okruh " : "circuit ") + r.circuit);
        string a = cz ? LabelCz(r.startLabel) : r.startLabel, b = cz ? LabelCz(r.endLabel) : r.endLabel;
        if (!string.IsNullOrEmpty(a) || !string.IsNullOrEmpty(b))
            parts.Add($"{(string.IsNullOrEmpty(a) ? "" : a + " ")}-> {(string.IsNullOrEmpty(b) ? "?" : b)}");
        if (!string.IsNullOrEmpty(r.note)) parts.Add(r.note);
        return string.Join(" · ", parts);
    }

    /// <summary>Length of the route's centre line (m).</summary>
    public static float Length(IList<Vector3> pts)
    {
        float l = 0; for (int i = 1; i < pts.Count; i++) l += Vector3.Distance(pts[i - 1], pts[i]);
        return l;
    }

    /// <summary>Point items (RouteMarker.type) - the full list, across every kind.</summary>
    public static readonly string[] MarkerTypes = { "Box", "Socket", "Switch", "Light", "Outlet", "Valve", "Floor drain", "Panel", "Data" };

    /// <summary>Point items that make sense for a kind (Box always fits - a generic junction/connection point).</summary>
    public static string[] MarkerTypesFor(RouteKind k) => k switch
    {
        RouteKind.Electric => new[] { "Box", "Socket", "Switch", "Light", "Outlet", "Panel", "Data" },
        RouteKind.ColdWater or RouteKind.HotWater or RouteKind.Heating => new[] { "Box", "Valve" },
        RouteKind.Waste => new[] { "Box", "Floor drain", "Valve" },
        _ => MarkerTypes,
    };

    /// <summary>EndLabels that make sense for a kind.</summary>
    public static string[] EndLabelsFor(RouteKind k) => k switch
    {
        RouteKind.Electric => new[] { "", "Socket", "Switch", "Light", "Panel", "Data", "Outside" },
        RouteKind.ColdWater or RouteKind.HotWater => new[] { "", "Sink", "WC", "Shower", "Bath", "Washer", "Dishwasher", "Kitchen sink", "Boiler", "Manifold", "Riser", "Outside" },
        RouteKind.Waste => new[] { "", "Sink", "WC", "Shower", "Bath", "Floor drain", "Washer", "Dishwasher", "Kitchen sink", "Riser", "Outside" },
        RouteKind.Heating => new[] { "", "Radiator", "Manifold", "Boiler", "Outside" },
        _ => EndLabels,
    };

    /// <summary>Plate size on the surface (w x h, m) of a marker type; outlets/valves are stubs instead.</summary>
    public static Vector2 MarkerSize(string t) => t switch
    {
        "Panel" => new Vector2(0.40f, 0.50f), "Floor drain" => new Vector2(0.15f, 0.15f),
        "Light" => new Vector2(0.10f, 0.10f), _ => new Vector2(0.08f, 0.08f),
    };

    public static bool IsStub(string t) => t == "Outlet" || t == "Valve";

    public static string MarkerCz(string t) => t switch
    {
        "Box" => "krabice", "Socket" => "zásuvka", "Switch" => "vypínač", "Light" => "světlo", "Outlet" => "vývod",
        "Valve" => "ventil", "Floor drain" => "vpusť", "Panel" => "rozvaděč", "Data" => "data",
        _ => t ?? "",
    };

    public static string LabelCz(string l) => l switch
    {
        "Sink" => "umyvadlo", "WC" => "WC", "Shower" => "sprcha", "Bath" => "vana", "Floor drain" => "vpusť",
        "Washer" => "pračka", "Dishwasher" => "myčka", "Kitchen sink" => "dřez", "Boiler" => "kotel / ohřívač",
        "Radiator" => "radiátor", "Manifold" => "rozdělovač", "Riser" => "stoupačka", "Socket" => "zásuvka",
        "Switch" => "vypínač", "Light" => "světlo", "Panel" => "rozvaděč", "Outside" => "ven",
        _ => l ?? "",
    };
}

/// <summary>
/// The traced routes of the house - one file for the whole house, not per saved scan, because they are traced over
/// passthrough on the live scan but belong to every copy of it (matched by room UUID): the headset keeps it in
/// persistentDataPath/Routes/routes.json, the PC in Exports/RoomData/Routes/routes.json ("Pull data from Quest"
/// merges the two). Every change goes through Apply (undoable, saved at once, raises Changed).
/// </summary>
public static class RouteStore
{
    public const string Folder = "Routes", FileName = "routes.json";
    const int MaxUndo = 60;

    static PipeRouteFile current;
    static readonly List<string> undo = new List<string>();
    public static event Action Changed;
    /// <summary>Bumped on every change - views rebuild when it differs from what they drew.</summary>
    public static int Version { get; private set; }

    /// <summary>Another routes file (desktop tests: -routes &lt;file&gt;), null = the house's own.</summary>
    public static string OverridePath;

    public static string FilePath => OverridePath ?? Path.Combine(MRUKPathUtility.IsDesktop
        ? Path.GetFullPath(MRUKPathUtility.GetExportRoot())
        : Application.persistentDataPath, Folder, FileName);

    /// <summary>Stamps a route/box as just edited (Merge keeps the newer one).</summary>
    public static string Now => DateTime.Now.ToString("s");

    public static PipeRouteFile Current { get { if (current == null) Reload(); return current; } }
    public static List<PipeRoute> Routes => Current.routes;
    public static int UndoCount => undo.Count;

    public static void Reload()
    {
        current = Read(FilePath) ?? new PipeRouteFile();
        undo.Clear();
        Version++;
        Changed?.Invoke();
    }

    public static PipeRouteFile Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var f = JsonUtility.FromJson<PipeRouteFile>(File.ReadAllText(path));
            if (f == null) return null;
            f.routes ??= new List<PipeRoute>();
            f.markers ??= new List<RouteMarker>();
            foreach (var r in f.routes) r.localPoints ??= new List<Vector3Data>();
            return f;
        }
        catch (Exception e) { Debug.LogWarning($"[Routes] {path}: {e.Message}"); return null; }
    }

    public static void Apply(Action<PipeRouteFile> change)
    {
        if (change == null) return;
        undo.Add(JsonUtility.ToJson(Current));
        if (undo.Count > MaxUndo) undo.RemoveAt(0);
        // every route/box this change touched gets a new 'modified' (Merge keeps the newer copy of each)
        var before = new Dictionary<string, string>();
        foreach (var r in Current.routes) if (r.id != null) before[r.id] = Stamp(r);
        foreach (var m in Current.markers) if (m.id != null) before[m.id] = Stamp(m);
        change(Current);
        string now = Now;
        foreach (var r in Current.routes) if (r.id == null || !before.TryGetValue(r.id, out var b) || b != Stamp(r)) r.modified = now;
        foreach (var m in Current.markers) if (m.id == null || !before.TryGetValue(m.id, out var b) || b != Stamp(m)) m.modified = now;
        Save();
        PendingChanges.Mark("routes");
    }

    static string Stamp(PipeRoute r) { string m = r.modified; r.modified = null; string j = JsonUtility.ToJson(r); r.modified = m; return j; }
    static string Stamp(RouteMarker x) { string m = x.modified; x.modified = null; string j = JsonUtility.ToJson(x); x.modified = m; return j; }

    public static bool Undo()
    {
        if (undo.Count == 0) return false;
        current = JsonUtility.FromJson<PipeRouteFile>(undo[undo.Count - 1]) ?? new PipeRouteFile();
        undo.RemoveAt(undo.Count - 1);
        Save();
        PendingChanges.Mark("routes");
        return true;
    }

    static void Save()
    {
        current.modified = DateTime.Now.ToString("s");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonUtility.ToJson(current, true));
        }
        catch (Exception e) { Debug.LogException(e); }
        Version++;
        Changed?.Invoke();
    }

    /// <summary>Routes and boxes of 'b' added to 'a' (same id: the newer edit wins).</summary>
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
        res.modified = DateTime.Now.ToString("s");
        return res;
    }

#if META_XR_SDK_INSTALLED
    /// <summary>The route's centre line in the current scan's world frame, or false if its room isn't loaded.</summary>
    public static bool WorldPoints(PipeRoute r, IEnumerable<MRUKRoom> rooms, List<Vector3> into)
    {
        into.Clear();
        var room = rooms?.FirstOrDefault(x => x != null && DoorCatalog.RoomId(x) == r.roomUuid);
        if (room == null) return false;
        foreach (var p in r.localPoints) into.Add(room.transform.TransformPoint(new Vector3(p.x, p.y, p.z)));
        return into.Count > 0;
    }

    public static bool WorldMarker(RouteMarker m, IEnumerable<MRUKRoom> rooms, out Vector3 pos, out Vector3 normal)
    {
        pos = default; normal = Vector3.up;
        var room = rooms?.FirstOrDefault(x => x != null && DoorCatalog.RoomId(x) == m.roomUuid);
        if (room == null || m.localPos == null) return false;
        pos = room.transform.TransformPoint(new Vector3(m.localPos.x, m.localPos.y, m.localPos.z));
        if (m.localNormal != null) normal = room.transform.TransformDirection(new Vector3(m.localNormal.x, m.localNormal.y, m.localNormal.z)).normalized;
        return true;
    }
#endif
}

/// <summary>Low-poly tubes (vertex-coloured, with baked shading) for routes.</summary>
public static class RouteMesh
{
    const int Sides = 8;
    static readonly Vector3 Light = new Vector3(0.35f, 1f, 0.25f).normalized;

    static Color Shade(Color c, Vector3 n)
    {
        float s = 0.62f + 0.38f * Mathf.Max(0f, Vector3.Dot(n, Light));
        return new Color(c.r * s, c.g * s, c.b * s, c.a);
    }

    /// <summary>Adds a tube along pts (radius r) plus a small ball at every bend.</summary>
    public static void AddTube(List<Vector3> v, List<Color> col, List<int> tri, IList<Vector3> pts, float r, Color c)
    {
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            Vector3 a = pts[i], b = pts[i + 1], d = b - a;
            if (d.sqrMagnitude < 1e-8f) continue;
            Vector3 dir = d.normalized;
            Vector3 u = Vector3.Cross(dir, Mathf.Abs(dir.y) > 0.9f ? Vector3.right : Vector3.up).normalized;
            Vector3 w = Vector3.Cross(dir, u);
            int k = v.Count;
            for (int s = 0; s < Sides; s++)
            {
                float ang = s * Mathf.PI * 2f / Sides;
                Vector3 n = u * Mathf.Cos(ang) + w * Mathf.Sin(ang);
                v.Add(a + n * r); v.Add(b + n * r);
                var sc = Shade(c, n); col.Add(sc); col.Add(sc);
            }
            for (int s = 0; s < Sides; s++)
            {
                int s2 = (s + 1) % Sides;
                int a0 = k + s * 2, b0 = a0 + 1, a1 = k + s2 * 2, b1 = a1 + 1;
                tri.Add(a0); tri.Add(b0); tri.Add(b1);
                tri.Add(a0); tri.Add(b1); tri.Add(a1);
            }
        }
        for (int i = 0; i < pts.Count; i++)
            if (i == 0 || i == pts.Count - 1 || pts.Count > 2) AddBall(v, col, tri, pts[i], r * 1.05f, c);
    }

    public static void AddBall(List<Vector3> v, List<Color> col, List<int> tri, Vector3 p, float r, Color c)
    {
        const int rings = 4, segs = 8;
        int k = v.Count;
        for (int i = 0; i <= rings; i++)
        {
            float th = Mathf.PI * i / rings;
            for (int j = 0; j < segs; j++)
            {
                float ph = 2f * Mathf.PI * j / segs;
                var n = new Vector3(Mathf.Sin(th) * Mathf.Cos(ph), Mathf.Cos(th), Mathf.Sin(th) * Mathf.Sin(ph));
                v.Add(p + n * r); col.Add(Shade(c, n));
            }
        }
        for (int i = 0; i < rings; i++)
            for (int j = 0; j < segs; j++)
            {
                int j2 = (j + 1) % segs;
                int a = k + i * segs + j, b = k + i * segs + j2, cc = k + (i + 1) * segs + j, d = k + (i + 1) * segs + j2;
                tri.Add(a); tri.Add(b); tri.Add(d);
                tri.Add(a); tri.Add(d); tri.Add(cc);
            }
    }

    /// <summary>A box lying on a surface: centre on the surface, 'normal' out of it, w x h on the surface, d thick.</summary>
    public static void AddPlate(List<Vector3> v, List<Color> col, List<int> tri, Vector3 center, Vector3 normal, float w, float h, float d, Color c)
    {
        Vector3 n = normal.normalized;
        Vector3 up = Mathf.Abs(n.y) > 0.9f ? Vector3.forward : Vector3.up;
        Vector3 ax = Vector3.Cross(up, n).normalized, ay = Vector3.Cross(n, ax);
        Vector3 o = center + n * (d / 2f);
        Vector3[] dirs = { n, -n, ax, -ax, ay, -ay };
        float[] half = { d / 2f, d / 2f, w / 2f, w / 2f, h / 2f, h / 2f };
        for (int f = 0; f < 6; f++)
        {
            Vector3 fn = dirs[f];
            Vector3 u = f < 2 ? ax : f < 4 ? ay : n, t = f < 2 ? ay : f < 4 ? n : ax;
            float hu = f < 2 ? w / 2f : f < 4 ? h / 2f : d / 2f, ht = f < 2 ? h / 2f : f < 4 ? d / 2f : w / 2f;
            Vector3 fc = o + fn * half[f];
            int k = v.Count;
            v.Add(fc - u * hu - t * ht); v.Add(fc + u * hu - t * ht); v.Add(fc + u * hu + t * ht); v.Add(fc - u * hu + t * ht);
            var sc = Shade(c, fn);
            col.Add(sc); col.Add(sc); col.Add(sc); col.Add(sc);
            tri.Add(k); tri.Add(k + 1); tri.Add(k + 2); tri.Add(k); tri.Add(k + 2); tri.Add(k + 3);
        }
    }

    public static Mesh ToMesh(string name, List<Vector3> v, List<Color> col, List<int> tri)
    {
        var m = new Mesh { name = name, indexFormat = v.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
        m.SetVertices(v); m.SetColors(col); m.SetTriangles(tri, 0);
        m.RecalculateBounds();
        return m;
    }
}
