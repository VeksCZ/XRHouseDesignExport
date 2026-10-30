using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

/// <summary>
/// Drawings of the house's walls for finishing work: per room a plan (floor routes, ceiling routes dashed, risers as
/// circles, numbered walls) and an elevation of every wall seen from inside (doors, windows, niches, the routes that
/// run on it) - with the points' distances from the wall's left corner and from the rough floor, in cm. One HTML file;
/// check boxes in it switch the routes (all, or per kind) and the dimension labels on and off, so the same file also
/// prints clean drawings without them. Dimensions are to the scanned surfaces (the bare masonry before plastering).
/// </summary>
public static class WallDrawings
{
    public const string FileName = "97_Walls_Routes.html";
    /// <summary>Sheets for one trade each (only its routes and boxes, with a list of them and the lengths per material),
    /// printable (A4 landscape) - for the electrician and the plumber/heating fitter.</summary>
    public const string ElectricFile = "98_Elektro.html", PlumbingFile = "98_Voda_Odpad_Topeni.html";
    static readonly RouteKind[] ElectricKinds = { RouteKind.Electric }, PlumbingKinds = { RouteKind.ColdWater, RouteKind.HotWater, RouteKind.Waste, RouteKind.Heating };
    static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    static string N(float v) => v.ToString("0.#", Ci);
    static string Cm(float m) => (m * 100f).ToString("0", Ci);

#if META_XR_SDK_INSTALLED
    class RouteLine { public PipeRoute route; public List<Vector3> pts; }
    class MarkerAt { public RouteMarker m; public Vector3 p, n; }
    class PhotoAt { public WallPhotoMeta meta; public string href; public Vector3 p, n; public PipeRoute route; }
    static string Cls(PipeRoute r) => $"route k{(int)r.kind}" + (r.proposed ? " prop" : "");
    static string Dash(PipeRoute r) => r.proposed ? " stroke-dasharray=\"6 4\" stroke-opacity=\"0.7\"" : "";
    class Opening { public Vector3 pos, normal, right; public float w, h; public bool door; }

    /// <summary>Writes the drawings of these rooms to dir/97_Walls_Routes.html (+ the trade sheets 98_*.html when there
    /// are routes of that trade) and returns the main file's path.</summary>
    public static string Write(string dir, List<MRUKRoom> rooms, string title)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, FileName);
        var photos = CollectPhotos(dir, rooms);
        File.WriteAllText(path, Build(rooms, title, null, null, photos), Encoding.UTF8);
        foreach (var (file, kinds, trade) in new[] { (ElectricFile, ElectricKinds, "Elektro"), (PlumbingFile, PlumbingKinds, "Voda, odpad, topení") })
        {
            string tp = Path.Combine(dir, file);
            bool any = RouteStore.Routes.Any(r => kinds.Contains(r.kind)) || RouteStore.Current.markers.Any(m => kinds.Contains(m.kind));
            if (any) File.WriteAllText(tp, Build(rooms, title, kinds, trade, photos), Encoding.UTF8);
            else if (File.Exists(tp)) File.Delete(tp);
        }
        return path;
    }

    /// <summary>The cable photos and where on the scan they look (linked from the drawings, relative to 'dir').</summary>
    static List<PhotoAt> CollectPhotos(string dir, List<MRUKRoom> rooms)
    {
        var res = new List<PhotoAt>();
        var all = MRUK.Instance != null ? MRUK.Instance.Rooms.Where(r => r != null).ToList() : rooms;
        foreach (var (meta, jpg) in WallOverlays.ListPhotos())
        {
            if (meta == null || !WallOverlays.PhotoTarget(meta, all, out var p, out var n)) continue;
            string href;
            try { href = Path.GetRelativePath(dir, jpg).Replace('\\', '/'); } catch { href = jpg; }
            res.Add(new PhotoAt { meta = meta, href = href.Replace(" ", "%20"), p = p, n = n, route = WallOverlays.PhotoRoute(p, all) });
        }
        return res;
    }

    public static string Build(List<MRUKRoom> rooms, string title) => Build(rooms, title, null, null, new List<PhotoAt>());

    static string Build(List<MRUKRoom> rooms, string title, RouteKind[] kinds, string trade, List<PhotoAt> photos)
    {
        rooms = rooms.Where(r => r != null && r.FloorAnchors.Count > 0).ToList();
        float yaw = rooms.Count > 0 ? FloorPlanBuilder.CorrectionYaw(MRUKPlanExtractor.Extract(rooms)) : 0f;
        Vector3 center = rooms.Count > 0 ? DollHouseVisualizer.CalculateCenter(rooms) : Vector3.zero;
        Quaternion g = Quaternion.Euler(0, yaw, 0);

        var allRooms = MRUK.Instance != null ? MRUK.Instance.Rooms : rooms;
        var routes = new List<RouteLine>();
        foreach (var r in RouteStore.Routes)
        {
            if (kinds != null && (!kinds.Contains(r.kind) || r.proposed)) continue;
            var pts = new List<Vector3>();
            if (RouteStore.WorldPoints(r, allRooms, pts) && pts.Count > 0) routes.Add(new RouteLine { route = r, pts = pts });
        }
        var openings = CollectOpenings(rooms);
        var markers = new List<MarkerAt>();
        foreach (var m in RouteStore.Current.markers)
            if ((kinds == null || kinds.Contains(m.kind)) && RouteStore.WorldMarker(m, allRooms, out var mp, out var mn)) markers.Add(new MarkerAt { m = m, p = mp, n = mn });
        if (kinds != null) photos = photos.Where(ph => ph.route != null && kinds.Contains(ph.route.kind)).ToList();

        var sb = new StringBuilder();
        Head(sb, trade == null ? title : $"{trade} – {title}", routes, markers.Count, trade != null);
        if (trade != null) Lists(sb, allRooms.Where(r => r != null).ToList(), routes, markers);
        foreach (var room in rooms.OrderBy(r => MRUKDataProcessor.GetRoomLabel(r), StringComparer.CurrentCulture))
        {
            var walls = room.Anchors.Where(a => a != null && a.PlaneRect.HasValue && MRUKDataProcessor.IsStructuralWall(a))
                .OrderBy(a => Mathf.Atan2(a.transform.forward.x, a.transform.forward.z)).ToList();
            if (walls.Count == 0) continue;
            float floorY = room.FloorAnchors.Where(f => f != null).Min(f => f.transform.position.y);
            int nRoutes = routes.Count(rl => rl.pts.Any(p => room.IsPositionInRoom(p + Vector3.up * 0.05f, true) || room.IsPositionInRoom(p - Vector3.up * 0.05f, true)));
            string rid = DoorCatalog.RoomId(room);
            int nMarks = markers.Count(mk => mk.m.roomUuid == rid || room.IsPositionInRoom(mk.p + mk.n * 0.05f, true));
            if (trade != null && nRoutes == 0 && nMarks == 0) continue; // a trade sheet: only rooms with its work
            sb.Append($"<section><h2>{Esc(MRUKDataProcessor.GetRoomLabel(room))} <small>{walls.Count} stěn{(nRoutes > 0 ? $", {nRoutes} tras" : "")}</small></h2>\n<div class=grid>\n");
            Plan(sb, room, walls, routes, markers, floorY, g, center);
            for (int i = 0; i < walls.Count; i++) Elevation(sb, room, walls[i], i + 1, routes, markers, openings, floorY, photos, trade != null);
            sb.Append("</div></section>\n");
        }
        sb.Append("</body></html>\n");
        return sb.ToString();
    }

    static void Head(StringBuilder sb, string title, List<RouteLine> routes, int nMarkers, bool tradeSheet)
    {
        sb.Append("<!doctype html><html lang=cs><head><meta charset=utf-8><meta name=viewport content=\"width=device-width,initial-scale=1\">");
        sb.Append($"<title>Stěny a trasy – {Esc(title)}</title><style>");
        sb.Append("body{font:14px/1.4 system-ui,Segoe UI,Arial,sans-serif;margin:16px;color:#222;background:#fff}h1{font-size:22px;margin:0 0 4px}");
        sb.Append("h2{font-size:18px;margin:28px 0 8px;border-bottom:2px solid #333}h2 small{font-weight:normal;color:#777;font-size:13px}");
        sb.Append(".grid{display:flex;flex-wrap:wrap;gap:14px}.card{border:1px solid #ddd;border-radius:6px;padding:6px 8px;background:#fafafa}");
        sb.Append(".card h3{font-size:14px;margin:0 0 4px}svg{display:block;background:#fff}.bar{position:sticky;top:0;background:#fffd;padding:8px 0;z-index:2;border-bottom:1px solid #eee}");
        sb.Append(".bar label{margin-right:14px;white-space:nowrap}.sw{display:inline-block;width:14px;height:6px;margin:0 4px 1px 0;vertical-align:middle}");
        sb.Append("text{font-family:system-ui,Arial,sans-serif}.dim{fill:#333}.lbl{fill:#000;font-weight:600}");
        sb.Append("body.noroutes .route{display:none}body.nodims .dim{display:none}");
        foreach (var k in RouteStyle.Kinds) sb.Append($"body.nok{(int)k} .k{(int)k}{{display:none}}");
        sb.Append("body.noprop .prop{display:none}body.nomark .mark{display:none}body.nophoto .photo{display:none}");
        sb.Append("table{border-collapse:collapse;margin:6px 0 14px;font-size:13px}td,th{border:1px solid #ccc;padding:3px 7px;text-align:left;vertical-align:top}th{background:#f0f0f0}td.n{text-align:right;white-space:nowrap}");
        sb.Append(".photo{cursor:pointer}.photo:hover rect{stroke:#06c;stroke-width:2}");
        sb.Append("@page{size:A4 landscape;margin:10mm}@media print{.bar{display:none}.card{break-inside:avoid}section{break-before:page}h2{margin-top:0}a{color:inherit;text-decoration:none}}</style></head><body>\n");
        sb.Append($"<h1>Stěny a trasy – {Esc(title)}</h1><div style=\"color:#666\">Pohledy na stěny zevnitř místnosti; kóty bodů tras „vodorovně/svisle“ v cm od levého rohu zdiva a od hrubé podlahy (scan = holé zdivo). V půdorysu od bodu 0,0 (levý horní roh místnosti); kroužek = stoupačka. Vytvořeno {DateTime.Now:d. M. yyyy HH:mm}, tras: {routes.Count}.</div>\n");
        sb.Append("<div class=bar><label><input type=checkbox checked onchange=\"document.body.classList.toggle('noroutes',!this.checked)\"> <b>Trasy</b></label>");
        foreach (var k in RouteStyle.Kinds)
        {
            int n = routes.Count(r => r.route.kind == k);
            if (tradeSheet && n == 0) continue;
            sb.Append($"<label><input type=checkbox checked onchange=\"document.body.classList.toggle('nok{(int)k}',!this.checked)\"> <span class=sw style=\"background:{RouteStyle.Hex(k)}\"></span>{RouteStyle.NameCz(k)} ({n})</label>");
        }
        int nProp = routes.Count(r => r.route.proposed);
        sb.Append($"<label><input type=checkbox checked onchange=\"document.body.classList.toggle('nomark',!this.checked)\"> Krabice a vývody ({nMarkers})</label>");
        sb.Append("<label><input type=checkbox checked onchange=\"document.body.classList.toggle('nophoto',!this.checked)\"> Fotky (klik = otevřít)</label>");
        if (nProp > 0) sb.Append($"<label><input type=checkbox checked onchange=\"document.body.classList.toggle('noprop',!this.checked)\"> Návrhy z fotek ({nProp}, čárkovaně)</label>");
        sb.Append("<label><input type=checkbox checked onchange=\"document.body.classList.toggle('nodims',!this.checked)\"> Kóty</label></div>\n");
    }

    // ---------- trade sheets: lists ----------

    static void Lists(StringBuilder sb, List<MRUKRoom> rooms, List<RouteLine> routes, List<MarkerAt> markers)
    {
        // the room a route/box was drawn in (its own room id)
        MRUKRoom RoomById(string id) => rooms.FirstOrDefault(x => DoorCatalog.RoomId(x) == id);
        string RoomName(string id) { var r = RoomById(id); return r != null ? MRUKDataProcessor.GetRoomLabel(r) : "?"; }
        float FloorAt(string id, Vector3 p)
        {
            var r = RoomById(id);
            if (r == null || r.FloorAnchors.Count == 0) return p.y;
            var below = r.FloorAnchors.Where(f => f != null && f.transform.position.y <= p.y + 0.05f).Select(f => f.transform.position.y).ToList();
            return below.Count > 0 ? below.Max() : r.FloorAnchors.Where(f => f != null).Min(f => f.transform.position.y);
        }

        // material: total length per kind + spec (what to buy), with a reserve
        sb.Append("<section style=\"break-before:auto\"><h2>Souhrn materiálu</h2><table><tr><th>Druh</th><th>Materiál</th><th>Tras</th><th>Délka</th><th>+15 % rezerva</th></tr>");
        foreach (var g in routes.GroupBy(r => (r.route.kind, spec: string.IsNullOrEmpty(r.route.spec) ? "(neuvedeno)" : r.route.spec)).OrderBy(g => g.Key.kind).ThenBy(g => g.Key.spec))
        {
            float len = g.Sum(r => RouteStyle.Length(r.pts));
            sb.Append($"<tr><td><span class=sw style=\"background:{RouteStyle.Hex(g.Key.kind)}\"></span>{RouteStyle.NameCz(g.Key.kind)}</td><td>{Esc(g.Key.spec)}</td><td class=n>{g.Count()}</td><td class=n>{len:0.0} m</td><td class=n>{len * 1.15f:0.0} m</td></tr>");
        }
        sb.Append("</table>");
        if (markers.Count > 0)
        {
            sb.Append("<table><tr><th>Krabice / vývody</th><th>Počet</th></tr>");
            foreach (var g in markers.GroupBy(m => (m.m.kind, m.m.type)).OrderBy(g => g.Key.kind).ThenBy(g => g.Key.type))
                sb.Append($"<tr><td>{RouteStyle.NameCz(g.Key.kind)} – {Esc(RouteStyle.MarkerCz(g.Key.type))}</td><td class=n>{g.Count()}</td></tr>");
            sb.Append("</table>");
        }

        sb.Append("<h2>Trasy</h2><table><tr><th>#</th><th>Druh</th><th>Materiál</th><th>Okruh</th><th>Odkud → kam</th><th>Místnost (začátek)</th><th>Délka</th><th>Poznámka</th></tr>");
        int i = 1;
        foreach (var rl in routes.OrderBy(r => r.route.kind).ThenBy(r => r.route.circuit ?? "", StringComparer.Ordinal).ThenBy(r => RoomName(r.route.roomUuid), StringComparer.CurrentCulture))
        {
            var r = rl.route;
            string fromTo = $"{(string.IsNullOrEmpty(r.startLabel) ? "–" : RouteStyle.LabelCz(r.startLabel))} → {(string.IsNullOrEmpty(r.endLabel) ? "–" : RouteStyle.LabelCz(r.endLabel))}";
            sb.Append($"<tr><td class=n>{i++}</td><td><span class=sw style=\"background:{RouteStyle.Hex(r.kind)}\"></span>{RouteStyle.NameCz(r.kind)} {Mathf.RoundToInt(RouteStyle.Diameter(r) * 1000)} mm</td><td>{Esc(r.spec)}</td><td>{Esc(r.circuit)}</td><td>{Esc(fromTo)}</td><td>{Esc(RoomName(r.roomUuid))}</td><td class=n>{RouteStyle.Length(rl.pts):0.0} m</td><td>{Esc(r.note)}</td></tr>");
        }
        sb.Append("</table>");

        if (markers.Count > 0)
        {
            sb.Append("<h2>Krabice a vývody</h2><table><tr><th>Místnost</th><th>Co</th><th>Druh</th><th>Okruh</th><th>Výška středu nad hrubou podlahou</th></tr>");
            foreach (var mk in markers.OrderBy(m => RoomName(m.m.roomUuid), StringComparer.CurrentCulture).ThenBy(m => m.m.type))
                sb.Append($"<tr><td>{Esc(RoomName(mk.m.roomUuid))}</td><td>{Esc(RouteStyle.MarkerCz(mk.m.type))}</td><td>{RouteStyle.NameCz(mk.m.kind)}</td><td>{Esc(mk.m.circuit)}</td><td class=n>{Cm(mk.p.y - FloorAt(mk.m.roomUuid, mk.p))} cm</td></tr>");
            sb.Append("</table>");
        }
        sb.Append("<p style=\"color:#666\">Délky po osách tras (bez přesahů do krabic a rozvaděče). Kóty v pohledech: cm od levého rohu zdiva / od hrubé podlahy.</p></section>\n");
    }

    // ---------- plan ----------

    static void Plan(StringBuilder sb, MRUKRoom room, List<MRUKAnchor> walls, List<RouteLine> routes, List<MarkerAt> markers, float floorY, Quaternion g, Vector3 center)
    {
        // plan coordinates: X = model x, Y = -model z (as the furniture SVGs), in metres
        Vector2 P(Vector3 w) { var m = g * (w - center); return new Vector2(m.x, -m.z); }
        var polys = new List<List<Vector2>>();
        foreach (var f in room.FloorAnchors.Where(f => f != null && f.PlaneBoundary2D != null && f.PlaneBoundary2D.Count >= 3))
            polys.Add(f.PlaneBoundary2D.Select(b => P(f.transform.TransformPoint(new Vector3(b.x, b.y, 0)))).ToList());
        if (polys.Count == 0) return;
        float minX = polys.SelectMany(p => p).Min(p => p.x), maxX = polys.SelectMany(p => p).Max(p => p.x);
        float minY = polys.SelectMany(p => p).Min(p => p.y), maxY = polys.SelectMany(p => p).Max(p => p.y);
        const float pad = 0.45f;
        float W = (maxX - minX + 2 * pad) * 100f, H = (maxY - minY + 2 * pad) * 100f;
        string S(Vector2 p) => $"{N((p.x - minX + pad) * 100f)},{N((p.y - minY + pad) * 100f)}";

        float ceilAt(Vector3 w) => XRModelFactory.SampleCeilingHeight(room.CeilingAnchors, w, floorY + 2.6f);
        var labels = new Labels();
        sb.Append($"<div class=card><h3>Půdorys – podlaha (strop čárkovaně)</h3><svg viewBox=\"0 0 {N(W)} {N(H)}\" width=\"{N(Mathf.Min(W * 1.4f, 560))}\">");
        foreach (var poly in polys)
            sb.Append($"<polygon points=\"{string.Join(" ", poly.Select(S))}\" fill=\"#f3efe6\" stroke=\"#333\" stroke-width=\"2.5\"/>");
        // wall numbers (as the elevations)
        for (int i = 0; i < walls.Count; i++)
        {
            var w = walls[i];
            Vector2 c = P(w.transform.position + w.transform.forward * 0.18f);
            sb.Append($"<circle cx=\"{N((c.x - minX + pad) * 100)}\" cy=\"{N((c.y - minY + pad) * 100)}\" r=\"9\" fill=\"#333\"/><text x=\"{N((c.x - minX + pad) * 100)}\" y=\"{N((c.y - minY + pad) * 100 + 4)}\" font-size=\"11\" fill=\"#fff\" text-anchor=\"middle\">{i + 1}</text>");
        }
        sb.Append($"<circle class=dim cx=\"{N(pad * 100)}\" cy=\"{N(pad * 100)}\" r=\"3\"/><text class=dim x=\"{N(pad * 100 - 4)}\" y=\"{N(pad * 100 - 6)}\" font-size=\"9\" text-anchor=\"end\">0,0</text>");

        foreach (var rl in routes)
        {
            string cls = Cls(rl.route), col = RouteStyle.Hex(rl.route.kind);
            float sw = Mathf.Max(2f, RouteStyle.Diameter(rl.route) * 100f);
            for (int i = 0; i + 1 < rl.pts.Count; i++)
            {
                Vector3 a = rl.pts[i], b = rl.pts[i + 1];
                if (!InRoomColumn(room, a) && !InRoomColumn(room, b)) continue;
                float va = a.y - floorY, vb = b.y - floorY;
                bool floor = va < 0.3f && vb < 0.3f;
                bool ceiling = a.y > ceilAt(a) - 0.35f && b.y > ceilAt(b) - 0.35f;
                Vector2 pa = P(a), pb = P(b);
                bool riser = (pa - pb).magnitude < 0.05f && Mathf.Abs(va - vb) > 0.2f;
                if (riser) { sb.Append($"<circle class=\"{cls}\" cx=\"{N((pa.x - minX + pad) * 100)}\" cy=\"{N((pa.y - minY + pad) * 100)}\" r=\"{N(sw * 0.8f + 2)}\" fill=\"none\" stroke=\"{col}\" stroke-width=\"2\"/>"); continue; }
                if (!floor && !ceiling) continue;
                sb.Append($"<line class=\"{cls}\" x1=\"{N((pa.x - minX + pad) * 100)}\" y1=\"{N((pa.y - minY + pad) * 100)}\" x2=\"{N((pb.x - minX + pad) * 100)}\" y2=\"{N((pb.y - minY + pad) * 100)}\" stroke=\"{col}\" stroke-width=\"{N(sw)}\" stroke-linecap=\"round\"{(ceiling && !floor ? " stroke-dasharray=\"8 5\"" : Dash(rl.route))}/>");
            }
            // dimensions of the floor points from the room's top-left corner (0,0)
            for (int i = 0; i < rl.pts.Count; i++)
            {
                var p = rl.pts[i];
                if (p.y - floorY >= 0.3f || !InRoomColumn(room, p) || !IsKeyPoint(rl.pts, i)) continue;
                Vector2 q = P(p);
                float x = (q.x - minX + pad) * 100, y = (q.y - minY + pad) * 100;
                string t = $"{Cm(q.x - minX)}/{Cm(q.y - minY)}";
                var at = labels.Place(x + 5, y - 4, t, 9);
                sb.Append($"<g class=\"{cls}\"><circle cx=\"{N(x)}\" cy=\"{N(y)}\" r=\"2.5\" fill=\"#000\"/><text class=dim x=\"{N(at.x)}\" y=\"{N(at.y)}\" font-size=\"9\">{t}</text></g>");
            }
            EndLabels(sb, rl, cls, p => p.y - floorY < 0.3f && InRoomColumn(room, p), p => { var q = P(p); return new Vector2((q.x - minX + pad) * 100, (q.y - minY + pad) * 100); }, labels);
        }
        // floor markers (drains, outlets coming out of the floor)
        foreach (var mk in markers)
        {
            if (mk.p.y - floorY >= 0.3f || !InRoomColumn(room, mk.p)) continue;
            Vector2 q = P(mk.p);
            float x = (q.x - minX + pad) * 100, y = (q.y - minY + pad) * 100;
            var sz = RouteStyle.MarkerSize(mk.m.type) * 100f;
            string t = $"{RouteStyle.MarkerCz(mk.m.type)} {Cm(q.x - minX)}/{Cm(q.y - minY)}";
            var at = labels.Place(x + sz.x / 2 + 3, y - 3, t, 9);
            sb.Append($"<g class=\"mark k{(int)mk.m.kind}\"><rect x=\"{N(x - sz.x / 2)}\" y=\"{N(y - sz.y / 2)}\" width=\"{N(sz.x)}\" height=\"{N(sz.y)}\" fill=\"{RouteStyle.Hex(mk.m.kind)}\" stroke=\"#000\" stroke-width=\"0.8\"/><text class=dim x=\"{N(at.x)}\" y=\"{N(at.y)}\" font-size=\"9\">{Esc(t)}</text></g>");
        }
        sb.Append("</svg></div>\n");
    }

    static bool InRoomColumn(MRUKRoom room, Vector3 p)
    {
        foreach (var f in room.FloorAnchors)
        {
            if (f == null || f.PlaneBoundary2D == null) continue;
            var l = f.transform.InverseTransformPoint(p);
            var v = new Vector2(l.x, l.y);
            if (Inside(v, f.PlaneBoundary2D)) return true;
        }
        return false;
    }

    static bool Inside(Vector2 pt, IReadOnlyList<Vector2> poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            if ((poly[i].y > pt.y) != (poly[j].y > pt.y) && pt.x < (poly[j].x - poly[i].x) * (pt.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                inside = !inside;
        return inside;
    }

    /// <summary>Ends and real bends (not every collinear click) get dimensions.</summary>
    static bool IsKeyPoint(List<Vector3> pts, int i)
    {
        if (i == 0 || i == pts.Count - 1) return true;
        Vector3 a = pts[i] - pts[i - 1], b = pts[i + 1] - pts[i];
        return a.sqrMagnitude > 1e-6f && b.sqrMagnitude > 1e-6f && Vector3.Angle(a, b) > 20f;
    }

    static void EndLabels(StringBuilder sb, RouteLine rl, string cls, Func<Vector3, bool> here, Func<Vector3, Vector2> at, Labels labels)
    {
        void One(string label, Vector3 p)
        {
            if (string.IsNullOrEmpty(label) || !here(p)) return;
            var q = at(p);
            string t = "→ " + RouteStyle.LabelCz(label);
            var l = labels.Place(q.x + 6, q.y + 12, t, 11);
            sb.Append($"<text class=\"{cls} lbl\" x=\"{N(l.x)}\" y=\"{N(l.y)}\" font-size=\"11\">{Esc(t)}</text>");
        }
        One(rl.route.startLabel, rl.pts[0]);
        One(rl.route.endLabel, rl.pts[rl.pts.Count - 1]);
        // what it is (material, circuit) next to its first point shown here
        string what = string.Join(" · ", new[] { rl.route.spec, string.IsNullOrEmpty(rl.route.circuit) ? null : "okruh " + rl.route.circuit }.Where(x => !string.IsNullOrEmpty(x)));
        if (what.Length == 0) return;
        var first = rl.pts.FirstOrDefault(here);
        if (first == default && !here(first)) return;
        var q0 = at(first);
        var l0 = labels.Place(q0.x + 6, q0.y - 10, what, 10);
        sb.Append($"<text class=\"{cls} lbl\" x=\"{N(l0.x)}\" y=\"{N(l0.y)}\" font-size=\"10\" font-style=\"italic\">{Esc(what)}</text>");
    }

    // ---------- elevations ----------

    static List<Opening> CollectOpenings(List<MRUKRoom> rooms)
    {
        var list = new List<Opening>();
        foreach (var a in rooms.SelectMany(r => r.Anchors))
        {
            if (a == null || !a.PlaneRect.HasValue) continue;
            string l = a.Label.ToString();
            if (!l.Contains("DOOR") && !l.Contains("WINDOW")) continue;
            if (XRModelFactory.RemovedOpening(a)) continue;
            var o = new Opening { pos = a.transform.position, normal = a.transform.forward, right = a.transform.right, w = a.PlaneRect.Value.width, h = a.PlaneRect.Value.height, door = MRUKDataProcessor.IsDoor(a) };
            // one doorway, two anchors (one per room): keep one
            if (list.Any(x => Vector3.Distance(x.pos, o.pos) < 0.45f && Mathf.Abs(Vector3.Dot(x.normal, o.normal)) > 0.9f)) continue;
            list.Add(o);
        }
        return list;
    }

    static void Elevation(StringBuilder sb, MRUKRoom room, MRUKAnchor wall, int number, List<RouteLine> routes, List<MarkerAt> markers, List<Opening> openings, float floorY, List<PhotoAt> photos, bool onlyWithWork)
    {
        Vector3 c = wall.transform.position, f = wall.transform.forward;
        f.y = 0; f.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, -f).normalized; // seen from inside, facing the wall
        float W = wall.PlaneRect.Value.width;
        Vector3 L = c - right * (W / 2f); L.y = floorY;
        float panelTop = c.y + wall.PlaneRect.Value.height / 2f - floorY;
        float hL = Mathf.Max(0.5f, XRModelFactory.SampleCeilingHeight(room.CeilingAnchors, L, floorY + panelTop) - floorY);
        float hR = Mathf.Max(0.5f, XRModelFactory.SampleCeilingHeight(room.CeilingAnchors, L + right * W, floorY + panelTop) - floorY);
        float Htop = Mathf.Max(hL, hR);

        const float padL = 40, padR = 70, padT = 30, padB = 46; // cm of drawing margin
        float vw = W * 100 + padL + padR, vh = Htop * 100 + padT + padB;
        float X(float u) => padL + u * 100f;
        float Y(float v) => padT + (Htop - v) * 100f;
        float U(Vector3 p) => Vector3.Dot(p - L, right);
        float V(Vector3 p) => p.y - floorY;
        float D(Vector3 p) => Vector3.Dot(p - c, f); // in front of the wall face (+ = into the room)
        bool On(Vector3 p) => Mathf.Abs(D(p)) < 0.15f && U(p) > -0.08f && U(p) < W + 0.08f && V(p) > -0.1f && V(p) < Htop + 0.1f;
        // pipes lying on the floor along the wall belong to the plan; on the wall only what climbs it
        bool Seg(Vector3 a, Vector3 b) => On(a) && On(b) && (V(a) > 0.15f || V(b) > 0.15f);
        if (onlyWithWork && !routes.Any(rl => Enumerable.Range(0, rl.pts.Count - 1).Any(i => Seg(rl.pts[i], rl.pts[i + 1])))
            && !markers.Any(mk => Vector3.Dot(mk.n, f) >= 0.7f && On(mk.p))) return;

        sb.Append($"<div class=card><h3>Stěna {number} <span style=\"color:#777;font-weight:normal\">– {Cm(W)} cm</span></h3><svg viewBox=\"0 0 {N(vw)} {N(vh)}\" width=\"{N(Mathf.Min(vw * 1.25f, 760))}\">");
        // wall outline (top follows a sloped ceiling)
        sb.Append($"<polygon points=\"{N(X(0))},{N(Y(0))} {N(X(W))},{N(Y(0))} {N(X(W))},{N(Y(hR))} {N(X(0))},{N(Y(hL))}\" fill=\"#f7f5f0\" stroke=\"#333\" stroke-width=\"2\"/>");
        sb.Append($"<text class=dim x=\"{N(X(0) - 4)}\" y=\"{N(Y(0) + 3)}\" font-size=\"9\" text-anchor=\"end\">±0 HP</text>");
        sb.Append($"<text class=dim x=\"{N(X(W) / 2 + padL / 2)}\" y=\"{N(Y(0) + 30)}\" font-size=\"11\" text-anchor=\"middle\">{Cm(W)}</text>");
        sb.Append($"<text class=dim x=\"{N(X(0) - 4)}\" y=\"{N(Y(hL) + 3)}\" font-size=\"9\" text-anchor=\"end\">{Cm(hL)}</text>");
        if (Mathf.Abs(hR - hL) > 0.02f) sb.Append($"<text class=dim x=\"{N(X(W) + 4)}\" y=\"{N(Y(hR) + 3)}\" font-size=\"9\">{Cm(hR)}</text>");

        // doors / windows on this wall
        foreach (var o in openings)
        {
            if (Mathf.Abs(Vector3.Dot(o.normal, f)) < 0.9f || Mathf.Abs(D(o.pos)) > 0.4f) continue;
            float uc = U(o.pos), v0 = V(o.pos) - o.h / 2f, u0 = uc - o.w / 2f;
            if (uc < -0.05f || uc > W + 0.05f) continue;
            if (v0 < -0.3f || v0 + o.h > Htop + 0.4f) continue; // another story's opening in the same wall plane
            sb.Append($"<rect x=\"{N(X(u0))}\" y=\"{N(Y(v0 + o.h))}\" width=\"{N(o.w * 100)}\" height=\"{N(o.h * 100)}\" fill=\"{(o.door ? "#efe2cf" : "#dbe9f7")}\" stroke=\"#555\" stroke-width=\"1.5\"/>");
            string what = o.door ? "DVEŘE" : "OKNO";
            sb.Append($"<text class=dim x=\"{N(X(uc))}\" y=\"{N(Y(v0 + o.h / 2))}\" font-size=\"10\" text-anchor=\"middle\">{what} {Cm(o.w)}/{Cm(o.h)}</text>");
            sb.Append($"<text class=dim x=\"{N(X(u0) + 2)}\" y=\"{N(Y(0) + 14)}\" font-size=\"9\">{Cm(u0)}</text>");
            if (!o.door && v0 > 0.05f) sb.Append($"<text class=dim x=\"{N(X(uc))}\" y=\"{N(Y(v0) + 11)}\" font-size=\"9\" text-anchor=\"middle\">parapet {Cm(v0)}</text>");
        }

        // niches (furniture spec)
        foreach (var n in XRModelFactory.Niches)
        {
            if (Vector3.Dot(n.inward, f) < 0.9f || Mathf.Abs(D(n.a)) > 0.15f) continue;
            float u0 = Mathf.Min(U(n.a), U(n.b)), u1 = Mathf.Max(U(n.a), U(n.b));
            if (u1 < 0 || u0 > W) continue;
            float v0 = n.y0 - floorY, v1 = n.y1 - floorY;
            sb.Append($"<rect x=\"{N(X(u0))}\" y=\"{N(Y(v1))}\" width=\"{N((u1 - u0) * 100)}\" height=\"{N((v1 - v0) * 100)}\" fill=\"#e2ddd3\" stroke=\"#333\" stroke-width=\"1.5\" stroke-dasharray=\"5 3\"/>");
            string nt = $"NIKA {Cm(u1 - u0)}×{Cm(v1 - v0)}, hl. {Cm(n.depth)}";
            if (u1 - u0 > 0.8f) sb.Append($"<text class=dim x=\"{N(X((u0 + u1) / 2))}\" y=\"{N(Y((v0 + v1) / 2) + 3)}\" font-size=\"9\" text-anchor=\"middle\">{nt}</text>");
            else sb.Append($"<text class=dim x=\"{N(X(u0) - 4)}\" y=\"{N(Y((v0 + v1) / 2) + 3)}\" font-size=\"9\" text-anchor=\"end\">{nt}</text>");
            sb.Append($"<text class=dim x=\"{N(X(u1) + 3)}\" y=\"{N(Y(v0))}\" font-size=\"9\">{Cm(v0)}</text>");
            sb.Append($"<text class=dim x=\"{N(X(u0))}\" y=\"{N(Y(v1) - 3)}\" font-size=\"9\">{Cm(u0)}</text>");
        }

        // routes running on this wall (both ends of a segment close to its face)
        var labels = new Labels();
        foreach (var rl in routes)
        {
            string cls = Cls(rl.route), col = RouteStyle.Hex(rl.route.kind);
            float sw = Mathf.Max(2f, RouteStyle.Diameter(rl.route) * 100f);
            for (int i = 0; i + 1 < rl.pts.Count; i++)
            {
                Vector3 a = rl.pts[i], b = rl.pts[i + 1];
                if (!Seg(a, b)) continue;
                sb.Append($"<line class=\"{cls}\" x1=\"{N(X(U(a)))}\" y1=\"{N(Y(V(a)))}\" x2=\"{N(X(U(b)))}\" y2=\"{N(Y(V(b)))}\" stroke=\"{col}\" stroke-width=\"{N(sw)}\" stroke-linecap=\"round\"{Dash(rl.route)}/>");
            }
            // dots + dimensions on the ends/bends of what runs on this wall; a point merely touching it (a pipe coming
            // straight out of the wall at a sink) still gets its dot
            for (int i = 0; i < rl.pts.Count; i++)
            {
                var p = rl.pts[i];
                if (!On(p) || !IsKeyPoint(rl.pts, i)) continue;
                bool onSeg = (i > 0 && Seg(rl.pts[i - 1], p)) || (i + 1 < rl.pts.Count && Seg(p, rl.pts[i + 1]));
                if (!onSeg && V(p) <= 0.15f) continue;
                float x = X(Mathf.Clamp(U(p), 0, W)), y = Y(V(p));
                string t = $"{Cm(U(p))}/{Cm(V(p))}";
                var at = labels.Place(x + 5, y - 4, t, 9);
                sb.Append($"<g class=\"{cls}\"><circle cx=\"{N(x)}\" cy=\"{N(y)}\" r=\"{N(sw / 2 + 1.5f)}\" fill=\"{col}\" stroke=\"#000\" stroke-width=\"0.8\"/><text class=dim x=\"{N(at.x)}\" y=\"{N(at.y)}\" font-size=\"9\">{t}</text></g>");
            }
            EndLabels(sb, rl, cls, p => On(p) && V(p) > 0.15f, p => new Vector2(X(U(p)), Y(V(p))), labels);
        }
        // boxes / sockets / outlets on this wall (facing out of it)
        foreach (var mk in markers)
        {
            if (Vector3.Dot(mk.n, f) < 0.7f || !On(mk.p)) continue;
            var sz = RouteStyle.MarkerSize(mk.m.type) * 100f;
            float x = X(U(mk.p)), y = Y(V(mk.p));
            string t = $"{RouteStyle.MarkerCz(mk.m.type)} {Cm(U(mk.p))}/{Cm(V(mk.p))}";
            var at = labels.Place(x + sz.x / 2 + 3, y - 3, t, 9);
            string shape = RouteStyle.IsStub(mk.m.type)
                ? $"<circle cx=\"{N(x)}\" cy=\"{N(y)}\" r=\"4\" fill=\"{RouteStyle.Hex(mk.m.kind)}\" stroke=\"#000\" stroke-width=\"0.8\"/>"
                : $"<rect x=\"{N(x - sz.x / 2)}\" y=\"{N(y - sz.y / 2)}\" width=\"{N(sz.x)}\" height=\"{N(sz.y)}\" fill=\"{RouteStyle.Hex(mk.m.kind)}\" fill-opacity=\"0.85\" stroke=\"#000\" stroke-width=\"0.8\"/>";
            sb.Append($"<g class=\"mark k{(int)mk.m.kind}\">{shape}<text class=dim x=\"{N(at.x)}\" y=\"{N(at.y)}\" font-size=\"9\">{Esc(t)}</text></g>");
        }
        // photos of this wall: a small camera where the photo looks, a click opens it
        foreach (var ph in photos)
        {
            if (Vector3.Dot(ph.n, f) < 0.7f || !On(ph.p)) continue;
            float x = X(Mathf.Clamp(U(ph.p), 0, W)), y = Y(V(ph.p));
            string col = ph.route != null ? RouteStyle.Hex(ph.route.kind) : "#888";
            sb.Append($"<a class=photo href=\"{Esc(ph.href)}\" target=_blank><title>Foto {ph.meta.index} {Esc(ph.meta.capturedAt)}</title><rect x=\"{N(x - 7)}\" y=\"{N(y - 5)}\" width=\"14\" height=\"10\" rx=\"2\" fill=\"#fff\" stroke=\"{col}\" stroke-width=\"1.5\"/><circle cx=\"{N(x)}\" cy=\"{N(y)}\" r=\"3\" fill=\"{col}\"/><text x=\"{N(x + 9)}\" y=\"{N(y + 3)}\" font-size=\"8\" fill=\"#06c\">{ph.meta.index}</text></a>");
        }
        sb.Append("</svg></div>\n");
    }
#endif

    /// <summary>Keeps dimension labels of one drawing from piling on top of each other (pipes run side by side).</summary>
    class Labels
    {
        readonly List<Rect> used = new List<Rect>();
        public Vector2 Place(float x, float y, string text, float size)
        {
            float w = text.Length * size * 0.58f, h = size * 1.1f;
            float[] dy = { 0, -h, h, -2 * h, 2 * h, -3 * h, 3 * h, -4 * h, 4 * h };
            foreach (float sx in new[] { 0f, -w - 10f })
                foreach (float d in dy)
                {
                    var r = new Rect(x + sx, y + d - h, w, h);
                    if (used.Any(u => u.Overlaps(r))) continue;
                    used.Add(r);
                    return new Vector2(x + sx, y + d);
                }
            used.Add(new Rect(x, y - h, w, h));
            return new Vector2(x, y);
        }
    }

    static string Esc(string s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
