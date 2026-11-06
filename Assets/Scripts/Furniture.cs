using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

/// <summary>
/// Furniture of a saved scan (bathroom fittings and simple boxes), from the scan's 95_Data_Furniture.json - the same
/// spec Tools/furnish.py turns into 16_Model_Furnished.glb, so one file drives both. Shapes and placement are a
/// port of furnish.py (keep the two in step): every item stands against one side ("-x"/"+x"/"-z"/"+z") of its
/// room's floor bounding box, "along" metres from that side's lower-coordinate end.
/// The spec's coordinates are the exported GLB's, which mirrors Z against Unity (glTF is right-handed), so the
/// room box is taken in GLB axes and every finished box is mirrored back.
/// </summary>
public static class Furniture
{
    /// <summary>The spec for a saved scan, or null if it has none (or it can't be read).</summary>
    static string cachedPath; static DateTime cachedTime; static long cachedLength; static JObject cachedSpec;

    /// <summary>Parsed once per file version: a build asks for the spec ~5 times (walls, ceilings, furniture, site...) and a big
    /// house's spec is hundreds of KB. Callers get their own copy (items are moved / turned in it before being saved).</summary>
    public static JObject Load(string scanName)
    {
        string path = MRUKSceneCache.FurniturePath(scanName);
        // A caller may pass the display name with the "Scan_" folder prefix already stripped (e.g. the batch export
        // test names a scan from its own folder via MRUKPathUtility.ScanNameFromFolder) - in FolderMode that name
        // alone resolves to a folder that doesn't exist, so fall back to the folder it actually came from.
        if ((string.IsNullOrEmpty(path) || !File.Exists(path)) && !string.IsNullOrEmpty(scanName) && !scanName.StartsWith(MRUKSceneCache.ScanFolderPrefix))
            path = MRUKSceneCache.FurniturePath(MRUKSceneCache.ScanFolderPrefix + scanName);
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            var info = new FileInfo(path);
            if (cachedSpec == null || cachedPath != path || cachedTime != info.LastWriteTimeUtc || cachedLength != info.Length)
            {
                cachedSpec = JObject.Parse(File.ReadAllText(path));
                cachedPath = path; cachedTime = info.LastWriteTimeUtc; cachedLength = info.Length;
            }
            return (JObject)cachedSpec.DeepClone();
        }
        catch (Exception ex) { cachedSpec = null; Debug.LogWarning($"[Furniture] {path}: {ex.Message}"); return null; }
    }

    static readonly Dictionary<string, Color> Materials = new Dictionary<string, Color>
    {
        { "ceramic", new Color(0.96f, 0.96f, 0.95f) }, { "water", new Color(0.80f, 0.86f, 0.90f) },
        { "glass", new Color(0.70f, 0.85f, 0.95f, 0.30f) }, { "wood", new Color(0.62f, 0.48f, 0.34f) },
        { "mirror", new Color(0.62f, 0.69f, 0.76f) }, { "sheen", new Color(0.86f, 0.90f, 0.94f) },
        { "chrome", new Color(0.80f, 0.80f, 0.82f) }, { "plaster", new Color(0.93f, 0.92f, 0.90f) },
        { "stone", new Color(0.30f, 0.30f, 0.32f) }, { "fire", new Color(1.00f, 0.42f, 0.05f) },
        { "ember", new Color(1.00f, 0.75f, 0.10f) },
        { "fabric", new Color(0.45f, 0.47f, 0.52f) }, { "linen", new Color(0.94f, 0.93f, 0.89f) },
        { "duvet", new Color(0.62f, 0.70f, 0.78f) }, { "screen", new Color(0.06f, 0.07f, 0.09f) },
        { "tiles", new Color(0.88f, 0.88f, 0.86f) }, { "dark", new Color(0.25f, 0.25f, 0.27f) },
        { "fronts", new Color(0.66f, 0.68f, 0.70f) }, { "oak", new Color(0.68f, 0.56f, 0.44f) },
        { "steel", new Color(0.74f, 0.75f, 0.77f) }, { "gap", new Color(0.35f, 0.36f, 0.38f) },
        { "roof", new Color(0.27f, 0.28f, 0.30f) }, { "wall", new Color(0.93f, 0.93f, 0.91f) },
        { "floor", new Color(0.80f, 0.72f, 0.60f) }, { "ceiling", new Color(0.97f, 0.97f, 0.96f) }, { "brick", new Color(0.62f, 0.36f, 0.28f) },
    };

    static readonly Dictionary<string, (float w, float d, float h)> Defaults = new Dictionary<string, (float, float, float)>
    {
        { "bathtub", (1.70f, 0.75f, 0.58f) }, { "shower", (0.90f, 0.90f, 2.00f) }, { "washbasin", (0.80f, 0.48f, 0.85f) },
        { "wc", (0.40f, 0.56f, 1.15f) }, { "washer", (0.60f, 0.60f, 0.85f) }, { "radiator", (0.50f, 0.10f, 1.20f) },
        { "mirror", (0.80f, 0.02f, 0.80f) }, { "cabinet", (0.60f, 0.20f, 0.70f) }, { "box", (0.60f, 0.60f, 0.85f) },
        // Kitchen: a run of base units with its worktop (sink/hob/dishwasher by u-range), wall units, tall units
        // (oven/microwave/fridge), a free-standing island (place it with "x"/"z").
        { "kitchen_base", (1.20f, 0.60f, 0.90f) }, { "kitchen_wall", (1.20f, 0.35f, 0.72f) },
        { "kitchen_tall", (0.60f, 0.60f, 2.15f) }, { "island", (1.30f, 0.80f, 0.90f) },
        // Corner fireplace: stone base, firebox glazed on the front and on "glass_side" (left/right), plastered
        // cladding up to h ("h": "ceiling" = up to the room's ceiling, also for a "box" like a chimney).
        { "fireplace", (0.90f, 0.60f, 2.20f) },
        // Living/bedrooms: wall TV ("y" = bottom edge), L sofa ("l" = return along the other wall, "corner": start|end
        // = which end of the run turns), dining table with "chairs" round its long sides, bed (headboard on the wall,
        // "nightstands"), desk ("chair" = office chair in front).
        { "tv", (1.45f, 0.05f, 0.84f) }, { "sofa_corner", (2.90f, 0.95f, 0.85f) }, { "dining_table", (1.80f, 0.90f, 0.75f) },
        { "bed", (1.80f, 2.10f, 0.50f) }, { "desk", (1.20f, 0.60f, 0.75f) },
        // TV unit (low cabinet under the TV), wardrobe (closed fronts, or "open": true = shelves + a hanging rail), shoe cabinet
        // (tilting drawers), coat rack (a board with hooks and two jackets; "y" = its bottom edge)
        { "tv_stand", (1.60f, 0.42f, 0.45f) }, { "wardrobe", (1.20f, 0.60f, 2.30f) }, { "shoe_cabinet", (0.80f, 0.30f, 1.25f) },
        { "coat_rack", (0.90f, 0.04f, 0.14f) },
        { "coffee_table", (1.10f, 0.60f, 0.42f) },   // coffee table: oak top, a lower shelf, four legs, a few books
    };

    /// <summary>Glass parts are named so the viewer can make them see-through.</summary>
    public const string GlassPart = "FURN_glass";

    // ---------- sliding parts (shower doors): they move in the walk-through ----------
    const string SlideMat = "_slide";
    [ThreadStatic] static float lastSlide;
    /// <summary>Name suffix of a part that slides open: "@slide&lt;group&gt;;dx;dy;dz" (model-space opening vector, m).</summary>
    public const string SlideMarker = "@slide";
    static string SlideTag(int group, Vector3 v) => FormattableString.Invariant($"{SlideMarker}{group};{v.x:0.###};{v.y:0.###};{v.z:0.###}");

    /// <summary>Parses a sliding part's name: its group (parts of one door move together) and opening vector.</summary>
    public static bool TryParseSlide(string partName, out string group, out Vector3 open)
    {
        group = null; open = default;
        int i = partName?.IndexOf(SlideMarker, StringComparison.Ordinal) ?? -1;
        if (i < 0) return false;
        var f = partName.Substring(i + SlideMarker.Length).Split(';');
        if (f.Length < 4) return false;
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        if (!float.TryParse(f[1], System.Globalization.NumberStyles.Float, ci, out float x) ||
            !float.TryParse(f[2], System.Globalization.NumberStyles.Float, ci, out float y) ||
            !float.TryParse(f[3], System.Globalization.NumberStyles.Float, ci, out float z)) return false;
        group = f[0]; open = new Vector3(x, y, z);
        return true;
    }

    /// <summary>Room's floor in GLB axes (model space with Z mirrored).</summary>
    struct RoomBox { public float xmin, xmax, zmin, zmax, y; }

    /// <summary>The furniture as model parts (model space: Euler(0,yaw,0) * (scanWorld - center)), one XRRoomModel
    /// per furnished room. Unknown rooms/types are skipped with a warning.</summary>
    public static XRHouseModel Build(JObject spec, List<MRUKRoom> rooms, float yaw, Vector3 center)
    {
        var model = new XRHouseModel();
        if (spec?["rooms"] is not JArray specRooms) return model;
        Quaternion g = Quaternion.Euler(0, yaw, 0);
        var buildup = BuildupFunc(spec, rooms);
        foreach (var rs in specRooms.OfType<JObject>())
        {
            string key = ((string)rs["room"] ?? "").ToLowerInvariant();
            if (key.StartsWith("room_")) key = key.Substring(5);
            var room = rooms.FirstOrDefault(r => DoorCatalog.RoomId(r).ToLowerInvariant().StartsWith(key));
            RoomBox box = default;
            if (room == null || !TryRoomBox(room, g, center, out box))
            {
                // an extra room of the spec (a room the scan lacks, e.g. "Satna"): its rectangle and floor are the item frame
                bool found = false;
                foreach (var er in ExtraRooms(spec, rooms, g, center))
                    if (er.name.ToLowerInvariant() == key) { box = new RoomBox { xmin = er.x0, xmax = er.x1, zmin = er.z0, zmax = er.z1, y = er.floor }; found = true; room = null; break; }
                if (!found) { Debug.LogWarning($"[Furniture] room '{rs["room"]}' not found"); continue; }
            }
            // furniture stands on the finished floor (the build-up above the scanned rough floor)
            if (buildup != null) box.y += buildup(box.y + center.y);
            int k = 0;
            foreach (var item in (rs["items"] as JArray ?? new JArray()).OfType<JObject>())
            {
                k++;
                string type = (string)item["type"] ?? "box";
                if (type == NicheType) continue;   // cut into the walls instead (InstallCeilings -> XRModelFactory.Niches)
                var rm = new XRRoomModel { roomName = ItemName(key, k - 1) };
                model.rooms.Add(rm);
                if (!Defaults.TryGetValue(type, out var def)) { Debug.LogWarning($"[Furniture] unknown type '{type}'"); continue; }
                float w = F(item, "w", def.w), d = F(item, "d", def.d);
                var toXZ = Place(box, item);
                float h;
                if (item["h"]?.Type == JTokenType.String && (string)item["h"] == "ceiling")
                {
                    // up to the ceiling above the item's middle (ceilings can slope), minus its own lift
                    var (cx, cz) = toXZ(w / 2f, d / 2f);
                    Vector3 world = Quaternion.Inverse(g) * new Vector3(cx, box.y, -cz) + center;
                    float ceil = room != null ? XRModelFactory.SampleCeilingHeight(room.CeilingAnchors, world, world.y + 2.6f) - center.y : box.y + 2.6f;
                    h = Mathf.Max(0.1f, ceil - box.y - F(item, "y", 0f));
                }
                else h = F(item, "h", def.h);
                lastSlide = 0f;
                var boxes = Shape(item, type, w, d, h, out float lift);
                // a sliding part opens towards u = 0 (model space: GLB z mirrored back)
                var (ox, oz) = toXZ(0f, 0f); var (ux, uz) = toXZ(1f, 0f);
                Vector3 slideDir = new Vector3(ox - ux, 0f, -(oz - uz)).normalized;
                foreach (var (mat, u0, u1, v0, v1, y0, y1) in boxes)
                {
                    var (xa, za) = toXZ(u0, v0);
                    var (xb, zb) = toXZ(u1, v1);
                    float yb = box.y + lift;
                    var part = Box((string)item["name"] ?? $"{type}_{k}", mat, xa, xb, yb + y0, yb + y1, za, zb);
                    if (mat.EndsWith(SlideMat)) part.name += SlideTag(k, slideDir * lastSlide);
                    rm.parts.Add(part);
                }
            }
        }
        foreach (var er in ExtraRooms(spec, rooms, g, center))
        {
            // floor, ceiling and only the listed walls (the others are neighbours' walls that already exist)
            var rm = new XRRoomModel { roomName = "Extra_" + er.name };
            const float slab = 0.05f;
            float bu = buildup != null ? buildup(er.floor + center.y) : 0f;   // the finished floor, as in the scanned rooms
            rm.parts.Add(Box(er.name + "_floor", "floor", er.x0, er.x1, er.floor - slab, er.floor + bu, er.z0, er.z1, "FLOOR"));
            var ceilAt = er.CeilAt;
            // "ceiling_x0/x1/z0/z1": a part of the room without its own ceiling (under the stairs)
            rm.parts.Add(SlopedBox(er.name + "_ceiling", "ceiling", F(er.spec, "ceiling_x0", er.x0), F(er.spec, "ceiling_x1", er.x1),
                F(er.spec, "ceiling_z0", er.z0), F(er.spec, "ceiling_z1", er.z1), ceilAt, (x, z) => ceilAt(x, z) + slab, "CEILING"));
            // "low_ceilings": [{"x0","x1","z0","z1","h"}] - a lowered part (e.g. under the stairs), closed up to the
            // room's own ceiling along its x0 edge
            foreach (var lc in (er.spec["low_ceilings"] as JArray ?? new JArray()).OfType<JObject>())
            {
                float lx0 = F(lc, "x0", er.x0), lx1 = F(lc, "x1", er.x1), lz0 = F(lc, "z0", er.z0), lz1 = F(lc, "z1", er.z1);
                float ly = er.floor + F(lc, "h", 2.4f);
                rm.parts.Add(Box(er.name + "_ceiling_low", "ceiling", lx0, lx1, ly, ly + slab, lz0, lz1, "CEILING"));
                rm.parts.Add(SlopedBox(er.name + "_bulkhead", "wall", lx0 - 0.02f, lx0 + 0.02f, lz0, lz1, (x, z) => ly, (x, z) => ceilAt(x, z) + slab, "WALL"));
            }
            foreach (var wl in er.spec["walls"] as JArray ?? new JArray())
            {
                string side = wl.Type == JTokenType.String ? (string)wl : (string)wl["side"];
                bool alongX = side == "-z" || side == "+z";
                float lo = alongX ? er.x0 : er.z0, hi = alongX ? er.x1 : er.z1;
                float a = wl.Type == JTokenType.Object ? F((JObject)wl, "from", lo) : lo;
                float b = wl.Type == JTokenType.Object ? F((JObject)wl, "to", hi) : hi;
                float t = F(er.spec, "wall_t", 0.25f);
                if (Mathf.Abs(a - lo) < 0.01f) a -= t;   // close the outer corner with the perpendicular wall
                if (Mathf.Abs(b - hi) < 0.01f) b += t;
                float y0 = er.floor - slab;
                Func<float, float, float> bot = (x, z) => y0, top = (x, z) => ceilAt(x, z) + slab;
                // a wall piece between p0..p1 along the wall, bottom / top given as functions
                void Piece(float p0, float p1, Func<float, float, float> lo, Func<float, float, float> hi)
                {
                    if (p1 - p0 < 0.002f) return;
                    if (side == "-x") rm.parts.Add(SlopedBox(er.name + "_wall", "wall", er.x0 - t, er.x0, p0, p1, lo, hi, "WALL"));
                    else if (side == "+x") rm.parts.Add(SlopedBox(er.name + "_wall", "wall", er.x1, er.x1 + t, p0, p1, lo, hi, "WALL"));
                    else if (side == "-z") rm.parts.Add(SlopedBox(er.name + "_wall", "wall", p0, p1, er.z0 - t, er.z0, lo, hi, "WALL"));
                    else if (side == "+z") rm.parts.Add(SlopedBox(er.name + "_wall", "wall", p0, p1, er.z1, er.z1 + t, lo, hi, "WALL"));
                }
                // "windows": [{"at": centre along the wall (GLB x or z), "w", "h", "sill": above the floor}] - an opening in the wall with a frame
                // (white inside, anthracite outside) and glass; sizes as ordered (the real frame size)
                var wins = (wl.Type == JTokenType.Object ? ((JObject)wl)["windows"] as JArray : null)?.OfType<JObject>()
                    .OrderBy(w => F(w, "at", 0f)).ToList() ?? new List<JObject>();
                float cursor = a;
                foreach (var wj in wins)
                {
                    float ww = F(wj, "w", 0.9f), wh = F(wj, "h", 1.2f), sill = er.floor + F(wj, "sill", 0.9f), at = F(wj, "at", (a + b) / 2f);
                    float w0 = at - ww / 2f, w1 = at + ww / 2f, wTop = sill + wh;
                    Piece(cursor, w0, bot, top);                                 // wall up to the window
                    Piece(w0, w1, bot, (x, z) => sill);                          // under it
                    Piece(w0, w1, (x, z) => wTop, top);                          // above it
                    cursor = w1;
                    // the frame: in the middle of the wall, the room half white (RAL 9016), the outer half anthracite; 6.5 cm profiles
                    float inw = (side == "-x" || side == "-z") ? 1f : -1f;      // towards the room, along the wall's normal axis
                    float lo0 = (side == "-x") ? er.x0 - t : (side == "+x") ? er.x1 : (side == "-z") ? er.z0 - t : er.z1;
                    float mid = lo0 + t / 2f, D = Mathf.Min(0.09f, t), fw = 0.065f;
                    void Fr(float p0, float p1, float y0, float y1, float n0, float n1, string mat)
                    {
                        bool xn = side == "-x" || side == "+x";
                        rm.parts.Add(xn ? Box(er.name + "_wframe", mat, n0, n1, y0, y1, p0, p1) : Box(er.name + "_wframe", mat, p0, p1, y0, y1, n0, n1));
                    }
                    foreach (var (mat, n0, n1) in new[] { ("#F1F0EA", mid, mid + inw * D / 2f), ("#292F31", mid - inw * D / 2f, mid) })
                    {
                        Fr(w0, w0 + fw, sill, wTop, n0, n1, mat); Fr(w1 - fw, w1, sill, wTop, n0, n1, mat);       // jambs
                        Fr(w0 + fw, w1 - fw, wTop - fw, wTop, n0, n1, mat); Fr(w0 + fw, w1 - fw, sill, sill + fw, n0, n1, mat);   // head, sill
                    }
                    Fr(w0 + fw, w1 - fw, sill + fw, wTop - fw, mid - 0.01f, mid + 0.01f, "glass");               // the pane
                }
                Piece(cursor, b, bot, top);
            }
            model.rooms.Add(rm);
        }
        // roofs: sloped slabs, "roofs": [{"name", "x0","x1","z0","z1", "plane": [a, bx, bz] (underside at GLB
        // y = a + bx*x + bz*z), "t": thickness, "color"}] - a chimney is just a flat one ("plane": [y, 0, 0], "t": height)
        if (spec["roofs"] is JArray roofs)
        {
            var rm = new XRRoomModel { roomName = "Roofs" };
            foreach (var rf in roofs.OfType<JObject>())
            {
                if (!TryPlane(rf["plane"], out var p)) continue;
                float t = F(rf, "t", 0.3f);
                Func<float, float, float> bot = (x, z) => p.a + p.bx * x + p.bz * z;
                var part = SlopedBox((string)rf["name"] ?? "roof", (string)rf["color"] ?? "roof", F(rf, "x0", 0f), F(rf, "x1", 0f),
                    F(rf, "z0", 0f), F(rf, "z1", 0f), bot, (x, z) => bot(x, z) + t);
                part.name = RoofPart;
                rm.parts.Add(part);
                // "walls": {"x0","x1","z0","z1","drop","t"} - the house's outer walls carried up from below the
                // roof, so the space between a lower ceiling and the roof (the attic edge) isn't an open slot
                if (rf["walls"] is JObject wl)
                {
                    float wx0 = F(wl, "x0", 0f), wx1 = F(wl, "x1", 0f), wz0 = F(wl, "z0", 0f), wz1 = F(wl, "z1", 0f);
                    float drop = F(wl, "drop", 0.8f), wt = F(wl, "t", 0.25f);
                    Func<float, float, float> wb = (x, z) => bot(x, z) - drop;
                    foreach (var aw in new[] {
                        SlopedBox("attic_wall", "wall", wx0, wx0 + wt, wz0, wz1, wb, bot, "WALL"),
                        SlopedBox("attic_wall", "wall", wx1 - wt, wx1, wz0, wz1, wb, bot, "WALL"),
                        SlopedBox("attic_wall", "wall", wx0, wx1, wz0, wz0 + wt, wb, bot, "WALL"),
                        SlopedBox("attic_wall", "wall", wx0, wx1, wz1 - wt, wz1, wb, bot, "WALL") })
                    { aw.name = RoofPart; rm.parts.Add(aw); }
                }
            }
            if (rm.parts.Count > 0) model.rooms.Add(rm);
        }
        // surroundings (see SiteModel)
        if (spec["site"] is JObject site)
        {
            var srm = SiteModel.Build(site);
            if (srm.parts.Count > 0) model.rooms.Add(srm);
        }
        return model;
    }

    /// <summary>Roof parts are named so the Dollhouse can make them see-through.</summary>
    public const string RoofPart = "FURN_roof";

    static bool TryPlane(JToken t, out (float a, float bx, float bz) p)
    {
        p = default;
        if (t is not JArray pl || pl.Count < 3) return false;
        p = ((float)pl[0], (float)pl[1], (float)pl[2]);
        return true;
    }

    /// <summary>The "ceilings" plane (GLB y = a + bx*x + bz*z) for a room id or extra room name, if the spec has one:
    /// "ceilings": [{"rooms": ["6DF8", "Satna", ...], "plane": [a, bx, bz]}].</summary>
    static bool TryCeilingPlane(JObject spec, string key, out (float a, float bx, float bz) p)
    {
        p = default;
        if (spec?["ceilings"] is not JArray list || string.IsNullOrEmpty(key)) return false;
        key = key.ToLowerInvariant();
        foreach (var c in list.OfType<JObject>())
            foreach (var r in c["rooms"] as JArray ?? new JArray())
            {
                string k = ((string)r ?? "").ToLowerInvariant();
                if (k.StartsWith("room_")) k = k.Substring(5);
                if (k.Length > 0 && key.StartsWith(k) && TryPlane(c["plane"], out p)) return true;
            }
        return false;
    }

    /// <summary>A "ceilings" entry {"rooms": [...], "from": "mesh"} lists this room (fit it to the scanned mesh).</summary>
    static bool CeilingFromMesh(JObject spec, string key)
    {
        if (spec?["ceilings"] is not JArray list || string.IsNullOrEmpty(key)) return false;
        key = key.ToLowerInvariant();
        foreach (var c in list.OfType<JObject>())
        {
            if ((string)c["from"] != "mesh") continue;
            foreach (var r in c["rooms"] as JArray ?? new JArray())
            {
                string k = ((string)r ?? "").ToLowerInvariant();
                if (k.StartsWith("room_")) k = k.Substring(5);
                if (k == "*" || (k.Length > 0 && key.StartsWith(k))) return true;
            }
        }
        return false;
    }

    /// <summary>Makes every model of this scan use the spec's "ceilings" instead of the flat ceilings the headset
    /// stored (walls, caps, ceilings and "h": "ceiling" items all sample XRModelFactory.SampleCeilingHeight). The
    /// planes are in the export's GLB axes, so the same frame (CorrectionYaw + CalculateCenter) is used here
    /// whatever frame the caller builds in. Call before building a model of these rooms.</summary>
    public static void InstallCeilings(List<MRUKRoom> rooms, string scanName)
    {
        XRModelFactory.CeilingOverride = null;
        XRModelFactory.Niches = new List<XRModelFactory.WallNiche>();
        var spec = string.IsNullOrEmpty(scanName) ? null : Load(scanName);
        XRModelFactory.FloorBuildupAt = BuildupFunc(spec, rooms);
        XRModelFactory.ExteriorWallThickness = spec?["walls"] is JObject wj ? F(wj, "exterior", 0.25f) : 0.25f;
        if (spec != null && rooms != null && rooms.Count > 0) InstallNiches(spec, rooms, scanName);
        if (rooms == null || rooms.Count == 0) return;
        // suspended ceilings (finished model only): world height per room
        var suspended = new List<(MRUKRoom room, float y)>();
        foreach (var r in rooms)
        {
            if (r == null || r.FloorAnchors.Count == 0) continue;
            var sh = SuspendedHeight(spec, DoorCatalog.RoomId(r));
            if (!sh.HasValue) continue;
            float rough = r.FloorAnchors.Where(f => f != null).Select(f => f.transform.position.y).DefaultIfEmpty(r.transform.position.y).Min();
            suspended.Add((r, rough + XRModelFactory.Buildup(rough) + sh.Value));
        }
        if (spec?["ceilings"] is not JArray && suspended.Count == 0) return;
        Vector3 center = DollHouseVisualizer.CalculateCenter(rooms);
        Quaternion g = Quaternion.Euler(0, MRUKPlanExtractor.HouseYaw(rooms), 0);
        var planes = new List<(MRUKRoom room, (float a, float bx, float bz) p)>();
        CeilingFit.ResetCache();
        foreach (var r in rooms)
        {
            if (r == null || r.CeilingAnchors == null) continue;
            string id = DoorCatalog.RoomId(r);
            if (TryCeilingPlane(spec, id, out var p)) planes.Add((r, p));
            else if (CeilingFromMesh(spec, id))
            {
                // {"rooms": [...], "from": "mesh"}: fitted to the scanned mesh (see CeilingFit)
                if (CeilingFit.Fit(r, g, center, out var fit) && fit.coverage > 0.15f && fit.rms < 0.15f)
                {
                    planes.Add((r, (fit.a, fit.bx, fit.bz)));
                    Debug.Log($"[Furniture] ceiling of {id.Substring(0, Math.Min(4, id.Length))} from the mesh: y = {fit.a:0.000} + {fit.bx:0.000}x + {fit.bz:0.000}z, rms {fit.rms * 100:0} cm, coverage {fit.coverage:P0}");
                }
                else Debug.Log($"[Furniture] ceiling of {id.Substring(0, Math.Min(4, id.Length))}: mesh fit not usable - keeping the scanned one");
            }
        }
        CeilingFit.ResetCache();
        // A ceiling plane - hand-authored or fit to the noisy scanned mesh - is only ever an approximation; the roof
        // above it is a second, independently authored plane. Where they'd overlap (or clear each other by only a
        // couple of cm) the grey roof shows through the white ceiling at that spot. Real insulation/roof structure
        // is normally several cm to tens of cm thick, so lower the ceiling to keep a safety margin below the roof's
        // underside everywhere its footprint covers the room, rather than trusting both planes to already agree.
        if (spec["roofs"] is JArray roofSpecs)
        {
            const float RoofClearance = 0.20f;
            for (int i = 0; i < planes.Count; i++)
            {
                var (room, p) = planes[i];
                var sample = new List<(float x, float z)>();
                foreach (var w in room.WallAnchors) if (w != null) { var m = g * (w.transform.position - center); sample.Add((m.x, -m.z)); }
                if (sample.Count == 0) continue;
                float worstOver = 0f;
                foreach (var rf in roofSpecs.OfType<JObject>())
                {
                    if (!TryPlane(rf["plane"], out var rp)) continue;
                    float x0 = F(rf, "x0", 0f), x1 = F(rf, "x1", 0f), z0 = F(rf, "z0", 0f), z1 = F(rf, "z1", 0f);
                    float xmin = Mathf.Min(x0, x1) - 0.2f, xmax = Mathf.Max(x0, x1) + 0.2f, zmin = Mathf.Min(z0, z1) - 0.2f, zmax = Mathf.Max(z0, z1) + 0.2f;
                    foreach (var (x, z) in sample)
                    {
                        if (x < xmin || x > xmax || z < zmin || z > zmax) continue;
                        float ceilY = p.a + p.bx * x + p.bz * z, roofY = rp.a + rp.bx * x + rp.bz * z;
                        // Plan footprints can overlap between stories (a garage roof sits under an upstairs room
                        // too) without that roof being the one actually over this ceiling - only trust a roof
                        // that is already within a plausible single attic's reach of this ceiling, not metres off.
                        if (roofY - ceilY > 0.8f || ceilY - roofY > 0.05f) continue;
                        float over = (ceilY + RoofClearance) - roofY;
                        if (over > worstOver) worstOver = over;
                    }
                }
                if (worstOver > 0.001f)
                {
                    planes[i] = (room, (p.a - worstOver, p.bx, p.bz));
                    Debug.Log($"[Furniture] ceiling of {DoorCatalog.RoomId(room).Substring(0, 4)} lowered {worstOver * 100:0} cm to clear the roof above it ({RoofClearance * 100:0} cm margin)");
                }
            }
        }
        if (planes.Count == 0 && suspended.Count == 0) return;
        XRModelFactory.CeilingOverride = (anchors, probe) =>
        {
            if (anchors == null) return null;
            var first = anchors.FirstOrDefault(a => a != null);
            bool Mine(MRUKRoom room) => ReferenceEquals(room.CeilingAnchors, anchors) || (first != null && room.CeilingAnchors.Contains(first));
            float? y = null;
            foreach (var (room, p) in planes)
            {
                if (!Mine(room)) continue;
                Vector3 m = g * (probe - center);
                y = center.y + p.a + p.bx * m.x + p.bz * -m.z;   // GLB z = -model z
                break;
            }
            foreach (var (room, sy) in suspended)
            {
                if (!Mine(room)) continue;
                // the suspended ceiling, never above the real one
                float real = y ?? room.CeilingAnchors.Where(c => c != null).Select(c => c.transform.position.y).DefaultIfEmpty(float.MaxValue).Max();
                y = Mathf.Min(real, sy);
                break;
            }
            return y;
        };
        if (suspended.Count > 0) Debug.Log($"[Furniture] {scanName}: suspended ceilings in {suspended.Count} room(s)");
        Debug.Log($"[Furniture] {scanName}: ceilings from the spec for {planes.Count} room(s)");
    }

    // ---------- finished floors + suspended ceilings (switchable) ----------

    /// <summary>A switch of the scan's furniture spec: <c>"house_style": {"blinds": true, "entrance_door": true, "window_frames": true}</c> - things
    /// that belong to one particular house (the new build's contract: exterior blinds, the entrance door, white / anthracite window frames)
    /// and must not appear in other scans. Off when the key is missing.</summary>
    public static bool StyleFlag(string scanName, string flag)
    {
        try
        {
            var t = Load(scanName)?["house_style"]?[flag];
            return t != null && t.Type == JTokenType.Boolean && (bool)t;
        }
        catch { return false; }
    }

    /// <summary>The spec's "house_style" list 'key' (e.g. "no_blinds": ["fb2b7e45"]) names this anchor - by a uuid
    /// prefix, so the short ids from the logs / docs work.</summary>
    public static bool StyleListHas(string scanName, string key, string uuid)
    {
        try
        {
            if (string.IsNullOrEmpty(uuid) || !(Load(scanName)?["house_style"]?[key] is JArray list)) return false;
            foreach (var t in list)
                if (t.Type == JTokenType.String && ((string)t).Length >= 4 && uuid.StartsWith((string)t, System.StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        catch { return false; }
    }

    const string FinishedPref = "Model.Finished";
    /// <summary>On: the model shows the finished floors ("floor_buildup") and the suspended ceilings
    /// ("suspended_ceilings"). Off (default): the rough shell as scanned - what pipes in the floor and cables above the
    /// ceiling are traced on. Remembered; models built afterwards follow it.</summary>
    public static bool Finished
    {
        get { try { return PlayerPrefs.GetInt(FinishedPref, 0) == 1; } catch { return false; } }
        set { PlayerPrefs.SetInt(FinishedPref, value ? 1 : 0); PlayerPrefs.Save(); }
    }

    /// <summary>The spec's "suspended_ceilings": [{"rooms": [room ids / extra room names], "h": m above the finished
    /// floor}] - the height (above the finished floor) for this room, or null (none, or Finished is off).</summary>
    static float? SuspendedHeight(JObject spec, string roomKey)
    {
        if (!Finished || spec?["suspended_ceilings"] is not JArray list || string.IsNullOrEmpty(roomKey)) return null;
        string k = roomKey.ToLowerInvariant();
        foreach (var e in list.OfType<JObject>())
            foreach (var r in (e["rooms"] as JArray ?? new JArray()).Select(x => ((string)x ?? "").ToLowerInvariant()))
                if (r.Length > 0 && (k == r || k.StartsWith(r))) return F(e, "h", 2.5f);
        return null;
    }

    /// <summary>The spec's "floor_buildup": [m per storey, bottom storey first] (e.g. [0.23, 0.12]) as rough floor height
    /// (world y) -> build-up. Storeys are the rooms' floor levels, a new storey from 1.2 m higher; a storey past the
    /// list takes the last value. Null when the spec has none.</summary>
    public static Func<float, float> BuildupFunc(JObject spec, List<MRUKRoom> rooms) => Finished ? AlwaysBuildupFunc(spec, rooms) : null;

    /// <summary>Same as <see cref="BuildupFunc"/> but independent of the live Finished toggle - for reports that want
    /// to show the future finished-floor level (e.g. as a reference line) regardless of what the 3D model currently
    /// shows.</summary>
    public static Func<float, float> AlwaysBuildupFunc(JObject spec, List<MRUKRoom> rooms)
    {
        if (spec?["floor_buildup"] is not JArray arr || arr.Count == 0 || rooms == null) return null;
        var t = arr.Select(v => v.Type == JTokenType.Float || v.Type == JTokenType.Integer ? (float)v : 0f).ToList();
        var levels = new List<float>();
        foreach (var y in rooms.Where(r => r != null && r.FloorAnchors.Count > 0)
                     .Select(r => r.FloorAnchors.Where(f => f != null).Select(f => f.transform.position.y).DefaultIfEmpty(float.NaN).Min())
                     .Where(y => !float.IsNaN(y)).OrderBy(y => y))
            if (levels.Count == 0 || y > levels[levels.Count - 1] + 1.2f) levels.Add(y);
        if (levels.Count == 0) return null;
        return y =>
        {
            int i = 0;
            for (int k = 0; k < levels.Count; k++) if (y > levels[k] - 0.6f) i = k;
            return t[Mathf.Min(i, t.Count - 1)];
        };
    }

    /// <summary>Item type of a recess in a wall: "w" along the wall, "h" tall, "d" deep, "y" = bottom edge above the
    /// room's (rough) floor, placed like any item ("wall" + "along", or "x"/"z").</summary>
    public const string NicheType = "niche";

    /// <summary>The spec's niches as world-space wall recesses (XRModelFactory.Niches), in the export's frame like
    /// the ceilings.</summary>
    static void InstallNiches(JObject spec, List<MRUKRoom> rooms, string scanName)
    {
        XRModelFactory.Niches.AddRange(NichesOf(spec, rooms));
        if (XRModelFactory.Niches.Count > 0) Debug.Log($"[Furniture] {scanName}: {XRModelFactory.Niches.Count} niche(s) cut into the walls");
    }

    /// <summary>The spec's niches in the world frame of these rooms (rooms matched by id, like the items).</summary>
    public static List<XRModelFactory.WallNiche> NichesOf(JObject spec, List<MRUKRoom> rooms)
    {
        var list = new List<XRModelFactory.WallNiche>();
        if (spec?["rooms"] is not JArray specRooms || rooms == null || rooms.Count == 0) return list;
        Vector3 center = DollHouseVisualizer.CalculateCenter(rooms);
        Quaternion g = Quaternion.Euler(0, MRUKPlanExtractor.HouseYaw(rooms), 0);
        Quaternion gi = Quaternion.Inverse(g);
        Vector3 World(float x, float y, float z) => gi * new Vector3(x, y, -z) + center;   // GLB axes -> world
        foreach (var rs in specRooms.OfType<JObject>())
        {
            string key = ((string)rs["room"] ?? "").ToLowerInvariant();
            if (key.StartsWith("room_")) key = key.Substring(5);
            var room = rooms.FirstOrDefault(r => DoorCatalog.RoomId(r).ToLowerInvariant().StartsWith(key));
            if (room == null || !TryRoomBox(room, g, center, out var box)) continue;
            foreach (var item in (rs["items"] as JArray ?? new JArray()).OfType<JObject>())
            {
                if ((string)item["type"] != NicheType) continue;
                float w = F(item, "w", 0.3f), h = F(item, "h", 0.3f), d = F(item, "d", 0.1f), y = box.y + F(item, "y", 0f);
                var toXZ = Place(box, item);
                var (xa, za) = toXZ(0f, 0f);
                var (xb, zb) = toXZ(w, 0f);
                var (xi, zi) = toXZ(0f, 1f);
                Vector3 a = World(xa, y, za), b = World(xb, y, zb);
                Vector3 inward = World(xi, y, zi) - a; inward.y = 0f;
                list.Add(new XRModelFactory.WallNiche
                {
                    a = a, b = b, inward = inward.normalized, y0 = a.y, y1 = a.y + h, depth = d,
                });
            }
        }
        return list;
    }

    /// <summary>The spec's items that stand against a wall ("along", not an absolute "x"/"z" - those depend on the frame
    /// of the whole saved scan) as one ghost mesh in the world frame of these rooms, vertex-coloured with 'alpha' - for
    /// showing the planned fittings over passthrough on the live scan. Null if nothing matched.</summary>
    public static Mesh GhostMesh(JObject spec, List<MRUKRoom> rooms, float alpha)
    {
        if (spec?["rooms"] is not JArray specRooms || rooms == null || rooms.Count == 0) return null;
        var only = new JObject { ["rooms"] = new JArray() };
        foreach (var rs in specRooms.OfType<JObject>())
        {
            var items = new JArray((rs["items"] as JArray ?? new JArray()).OfType<JObject>()
                .Where(i => i["x"] == null && i["z"] == null && (string)i["type"] != NicheType).Select(i => i.DeepClone()));
            if (items.Count > 0) ((JArray)only["rooms"]).Add(new JObject { ["room"] = rs["room"], ["items"] = items });
        }
        float yaw = MRUKPlanExtractor.HouseYaw(rooms);
        Vector3 center = DollHouseVisualizer.CalculateCenter(rooms);
        var model = Build(only, rooms, yaw, center);
        Quaternion gi = Quaternion.Inverse(Quaternion.Euler(0, yaw, 0));
        var v = new List<Vector3>(); var col = new List<Color>(); var tri = new List<int>();
        foreach (var part in model.rooms.Where(r => TryParseItem(r.roomName, out _, out _)).SelectMany(r => r.parts))
        {
            var c = part.color; c.a = part.name.StartsWith(GlassPart) ? alpha * 0.4f : alpha;
            for (int i = 0; i + 2 < part.triangles.Count; i += 3)
            {
                Vector3 a = gi * part.vertices[part.triangles[i]] + center, b = gi * part.vertices[part.triangles[i + 1]] + center, d = gi * part.vertices[part.triangles[i + 2]] + center;
                Vector3 n = Vector3.Cross(b - a, d - a).normalized;
                float sh = 0.7f + 0.3f * Mathf.Abs(Vector3.Dot(n, new Vector3(0.35f, 0.85f, 0.4f).normalized));
                var sc = new Color(c.r * sh, c.g * sh, c.b * sh, c.a);
                int k = v.Count;
                v.Add(a); v.Add(b); v.Add(d); col.Add(sc); col.Add(sc); col.Add(sc);
                tri.Add(k); tri.Add(k + 1); tri.Add(k + 2);
            }
        }
        return v.Count == 0 ? null : RouteMesh.ToMesh("FittingsGhost", v, col, tri);
    }

    /// <summary>A box in GLB axes whose bottom and top follow (x, z) -> y (sloped ceilings, walls under them, roofs).</summary>
    static XRMeshPart SlopedBox(string name, string mat, float x0, float x1, float z0, float z1,
        Func<float, float, float> bottom, Func<float, float, float> top, string modelMat = "FURNITURE")
    {
        var part = Box(name, mat, x0, x1, 0f, 1f, z0, z1, modelMat);
        for (int i = 0; i < part.vertices.Count; i++)
        {
            var v = part.vertices[i];
            v.y = v.y > 0.5f ? top(v.x, -v.z) : bottom(v.x, -v.z);   // model z = -GLB z
            part.vertices[i] = v;
        }
        return part;
    }

    /// <summary>A room the headset couldn't scan any more (the scan has a room limit), drawn from the spec:
    /// "extra_rooms": [{"name", "like": room id (floor height and ceiling taken from it), "x0","x1","z0","z1"
    /// (GLB axes), "walls": ["-x", {"side": "+z", "from": x, "to": x, "windows": [{"at", "w", "h", "sill"}]}, ...]}]. Only the listed walls are drawn -
    /// the other sides are neighbours' walls, which get thin where they meet it (see <see cref="ExtraFaces"/>).</summary>
    struct ExtraRoom
    {
        public string name; public JObject spec; public float x0, x1, z0, z1, floor, ceil;
        public (float a, float bx, float bz)? plane;   // its own "ceilings" entry (sloped), else flat at ceil
        public float? suspended;                        // a suspended ceiling (model y) below it, if any
        public Func<float, float, float> CeilAt { get { var p = plane; float c = ceil; var s = suspended; return p.HasValue ? (x, z) => Mathf.Min(p.Value.a + p.Value.bx * x + p.Value.bz * z, s ?? float.MaxValue) : (x, z) => Mathf.Min(c, s ?? float.MaxValue); } }
    }

    static IEnumerable<ExtraRoom> ExtraRooms(JObject spec, List<MRUKRoom> rooms, Quaternion g, Vector3 center)
    {
        if (spec?["extra_rooms"] is not JArray list) yield break;
        foreach (var er in list.OfType<JObject>())
        {
            string like = ((string)er["like"] ?? "").ToLowerInvariant();
            var room = rooms.FirstOrDefault(r => like.Length > 0 && DoorCatalog.RoomId(r).ToLowerInvariant().StartsWith(like));
            if (room == null || !TryRoomBox(room, g, center, out var box)) { Debug.LogWarning($"[Furniture] extra room '{er["name"]}': room '{er["like"]}' not found"); continue; }
            float x0 = F(er, "x0", 0f), x1 = F(er, "x1", 0f), z0 = F(er, "z0", 0f), z1 = F(er, "z1", 0f);
            if (x1 <= x0 || z1 <= z0) { Debug.LogWarning($"[Furniture] extra room '{er["name"]}': empty rectangle"); continue; }
            // its ceiling: the reference room's ceiling height above its own floor
            Vector3 refWorld = Quaternion.Inverse(g) * new Vector3((box.xmin + box.xmax) / 2f, box.y, -(box.zmin + box.zmax) / 2f) + center;
            float h = XRModelFactory.SampleCeilingHeight(room.CeilingAnchors, refWorld, refWorld.y + 2.6f) - refWorld.y;
            float floor = F(er, "floor", box.y);
            string name = (string)er["name"] ?? "extra";
            var x = new ExtraRoom { name = name, spec = er, x0 = x0, x1 = x1, z0 = z0, z1 = z1, floor = floor, ceil = floor + F(er, "h", h) };
            if (TryCeilingPlane(spec, name, out var p))
            {
                x.plane = p;
                x.ceil = x.CeilAt((x0 + x1) / 2f, (z0 + z1) / 2f);
            }
            var sh = SuspendedHeight(spec, name);
            if (sh.HasValue)
            {
                var bu = BuildupFunc(spec, rooms);
                x.suspended = floor + (bu != null ? bu(floor + center.y) : 0f) + sh.Value;
                x.ceil = x.CeilAt((x0 + x1) / 2f, (z0 + z1) / 2f);
            }
            yield return x;
        }
    }

    /// <summary>The four faces of every extra room in world space (centre at mid-height, normal into the room,
    /// width), so the scanned rooms' walls next to it are drawn as thin as against a scanned neighbour instead
    /// of 25 cm into it.</summary>
    public static List<(Vector3 pos, Vector3 inward, float width)> ExtraFaces(JObject spec, List<MRUKRoom> rooms, float yaw, Vector3 center)
    {
        var faces = new List<(Vector3, Vector3, float)>();
        Quaternion g = Quaternion.Euler(0, yaw, 0), gi = Quaternion.Inverse(g);
        foreach (var er in ExtraRooms(spec, rooms, g, center))
        {
            float ym = (er.floor + er.ceil) / 2f, xm = (er.x0 + er.x1) / 2f, zm = (er.z0 + er.z1) / 2f;
            Vector3 W(float x, float z) => gi * new Vector3(x, ym, -z) + center;   // GLB axes -> world
            faces.Add((W(er.x0, zm), gi * Vector3.right, er.z1 - er.z0));
            faces.Add((W(er.x1, zm), gi * Vector3.left, er.z1 - er.z0));
            faces.Add((W(xm, er.z0), gi * Vector3.back, er.x1 - er.x0));     // GLB +z = model -z
            faces.Add((W(xm, er.z1), gi * Vector3.forward, er.x1 - er.x0));
        }
        return faces;
    }

    // ---------- moving items (walk-through: grab, drag, rotate) ----------

    const string ItemPrefix = "FurnItem:";

    /// <summary>GameObject name of one item's group: room key + index in that room's items.</summary>
    public static string ItemName(string roomKey, int index) => $"{ItemPrefix}{roomKey}#{index}";

    public static bool TryParseItem(string goName, out string roomKey, out int index)
    {
        roomKey = null; index = -1;
        if (goName == null || !goName.StartsWith(ItemPrefix)) return false;
        int hash = goName.LastIndexOf('#');
        if (hash < 0 || !int.TryParse(goName.Substring(hash + 1), out index)) return false;
        roomKey = goName.Substring(ItemPrefix.Length, hash - ItemPrefix.Length);
        return true;
    }

    static bool FindItem(JObject spec, List<MRUKRoom> rooms, Quaternion g, Vector3 center, string key, int index, out JObject item, out RoomBox box)
    {
        item = null; box = default;
        foreach (var rs in (spec?["rooms"] as JArray ?? new JArray()).OfType<JObject>())
        {
            string k = ((string)rs["room"] ?? "").ToLowerInvariant();
            if (k.StartsWith("room_")) k = k.Substring(5);
            if (k != key) continue;
            var items = rs["items"] as JArray;
            if (items == null || index < 0 || index >= items.Count || items[index] is not JObject it) return false;
            var room = rooms.FirstOrDefault(r => DoorCatalog.RoomId(r).ToLowerInvariant().StartsWith(key));
            if (room == null || !TryRoomBox(room, g, center, out box)) return false;
            item = it;
            return true;
        }
        return false;
    }

    /// <summary>Moves an item by (dx, dz) in GLB axes: it gets an absolute corner ("x"/"z") instead of along/gap.</summary>
    public static bool MoveItem(JObject spec, List<MRUKRoom> rooms, float yaw, Vector3 center, string key, int index, float dx, float dz)
    {
        if (!FindItem(spec, rooms, Quaternion.Euler(0, yaw, 0), center, key, index, out var item, out var box)) return false;
        var (ox, oz) = Place(box, item)(0f, 0f);
        item["x"] = Math.Round(ox + dx, 3);
        item["z"] = Math.Round(oz + dz, 3);
        item.Remove("along"); item.Remove("gap");
        return true;
    }

    /// <summary>Turns an item 90 degrees about the middle of its footprint (its back goes to the next wall direction).</summary>
    public static bool RotateItem(JObject spec, List<MRUKRoom> rooms, float yaw, Vector3 center, string key, int index)
    {
        if (!FindItem(spec, rooms, Quaternion.Euler(0, yaw, 0), center, key, index, out var item, out var box)) return false;
        string type = (string)item["type"] ?? "box";
        var def = Defaults.TryGetValue(type, out var dd) ? dd : (0.6f, 0.6f, 0.85f);
        float w = F(item, "w", def.Item1), d = F(item, "d", def.Item2);
        var (cx, cz) = Place(box, item)(w / 2f, d / 2f);
        string wall = (string)item["wall"] ?? "-z";
        string next = wall switch { "-z" => "+x", "+x" => "+z", "+z" => "-x", _ => "-z" };
        var turned = new JObject { ["wall"] = next, ["x"] = 0f, ["z"] = 0f };
        var (rx, rz) = Place(box, turned)(w / 2f, d / 2f);   // middle relative to a corner at (0,0)
        item["wall"] = next;
        item["x"] = Math.Round(cx - rx, 3);
        item["z"] = Math.Round(cz - rz, 3);
        item.Remove("along"); item.Remove("gap");
        return true;
    }

    /// <summary>Writes the spec back to the scan's furniture file.</summary>
    public static bool Save(string scanName, JObject spec)
    {
        string path = MRUKSceneCache.FurniturePath(scanName);
        if (string.IsNullOrEmpty(path) || spec == null) return false;
        try { SafeFile.WriteAllText(path, spec.ToString(Newtonsoft.Json.Formatting.Indented)); return true; }
        catch (Exception ex) { Debug.LogWarning($"[Furniture] save {path}: {ex.Message}"); return false; }
    }

    static float F(JObject o, string k, float def) => o[k] != null && o[k].Type != JTokenType.Null ? (float)o[k] : def;

    static bool TryRoomBox(MRUKRoom room, Quaternion g, Vector3 center, out RoomBox b)
    {
        b = new RoomBox { xmin = float.MaxValue, xmax = float.MinValue, zmin = float.MaxValue, zmax = float.MinValue, y = float.MaxValue };
        bool any = false;
        foreach (var f in room.FloorAnchors)
        {
            if (f == null || f.PlaneBoundary2D == null) continue;
            foreach (var p2 in f.PlaneBoundary2D)
            {
                Vector3 m = g * (f.transform.TransformPoint(new Vector3(p2.x, p2.y, 0f)) - center);
                float zg = -m.z; // GLB axes
                b.xmin = Mathf.Min(b.xmin, m.x); b.xmax = Mathf.Max(b.xmax, m.x);
                b.zmin = Mathf.Min(b.zmin, zg); b.zmax = Mathf.Max(b.zmax, zg);
                b.y = Mathf.Min(b.y, m.y);
                any = true;
            }
        }
        return any;
    }

    /// <summary>(material, u0, u1, v0, v1, y0, y1): u along the wall (0..w), v into the room (0..d), y up.</summary>
    static List<(string, float, float, float, float, float, float)> Shape(JObject item, string t, float w, float d, float h, out float lift)
    {
        var B = new List<(string, float, float, float, float, float, float)>();
        lift = F(item, "y", 0f);
        switch (t)
        {
            case "bathtub":
            {
                const float r = 0.07f;
                B.Add(("tiles", 0, w, 0, d, 0, h - 0.02f));
                B.Add(("ceramic", 0, w, 0, r, h - 0.02f, h)); B.Add(("ceramic", 0, w, d - r, d, h - 0.02f, h));
                B.Add(("ceramic", 0, r, r, d - r, h - 0.02f, h)); B.Add(("ceramic", w - r, w, r, d - r, h - 0.02f, h));
                B.Add(("water", r, w - r, r, d - r, h - 0.03f, h - 0.025f));
                if (item["tap"] != null) BathTap(B, (string)item["tap"], w, d, h);
                break;
            }
            case "shower":
            {
                const float tray = 0.05f, gl = 0.008f;
                B.Add(("ceramic", 0, w, 0, d, 0, tray));
                var glass = item["glass"] as JArray ?? new JArray("front");
                foreach (var side in glass)
                {
                    string s = side.Type == JTokenType.String ? (string)side : (string)side["side"];
                    float a0 = side.Type == JTokenType.Object ? F((JObject)side, "from", 0f) : 0f;
                    bool sliding = side.Type == JTokenType.Object && side["sliding"] != null && (bool)side["sliding"];
                    if (s == "front" && sliding)
                    {
                        // fixed panel on the outer track from the wall end, sliding panel on the inner track
                        // (opens towards u=0 over the fixed one), top rail, handle at the open end
                        float a1 = F((JObject)side, "to", w), mid = (a0 + a1) / 2f, rail = 0.03f;
                        B.Add(("glass", a0, mid + 0.05f, d - gl, d, tray, h - rail));
                        // the sliding panel and its handle move together in the walk-through (opens towards u = 0)
                        B.Add(("glass" + SlideMat, mid - 0.05f, a1, d - 2 * gl - 0.012f, d - gl - 0.012f, tray, h - rail));
                        B.Add(("chrome", a0, a1, d - 0.04f, d, h - rail, h));
                        B.Add(("chrome" + SlideMat, a1 - 0.10f, a1 - 0.08f, d - gl - 0.012f, d + 0.02f, 0.95f, 1.25f));
                        lastSlide = a1 - mid - 0.05f;
                    }
                    else if (s == "front") B.Add(("glass", a0, side.Type == JTokenType.Object ? F((JObject)side, "to", w) : w, d - gl, d, tray, h));
                    else if (s == "left") B.Add(("glass", 0, gl, a0, side.Type == JTokenType.Object ? F((JObject)side, "to", d) : d, tray, h));
                    else if (s == "right") B.Add(("glass", w - gl, w, a0, side.Type == JTokenType.Object ? F((JObject)side, "to", d) : d, tray, h));
                }
                ShowerSet(B, item, w, d, h);
                break;
            }
            case "washbasin":
            {
                float top = h, cab0 = F(item, "cabinet_from", 0.30f);
                int n = item["basins"] != null ? (int)item["basins"] : 1;
                bool vessel = item["vessel"] != null && (bool)item["vessel"];
                B.Add(("wood", 0, w, 0, d - 0.02f, cab0, top - 0.04f));
                B.Add((vessel ? "wood" : "ceramic", 0, w, 0, d, top - 0.04f, top));
                for (int i = 0; i < n; i++)
                {
                    float c = w * (i + 0.5f) / n;
                    if (vessel)
                    {
                        B.Add(("ceramic", c - 0.25f, c + 0.25f, 0.08f, 0.44f, top, top + 0.13f));
                        B.Add(("water", c - 0.21f, c + 0.21f, 0.12f, 0.40f, top + 0.13f, top + 0.132f));
                        B.Add(("chrome", c - 0.015f, c + 0.015f, 0f, 0.18f, top + 0.22f, top + 0.25f));
                    }
                    else
                    {
                        B.Add(("water", c - 0.2f, c + 0.2f, 0.12f, d - 0.08f, top - 0.001f, top + 0.001f));
                        B.Add(("chrome", c - 0.02f, c + 0.02f, 0.03f, 0.15f, top, top + 0.18f));
                    }
                }
                if (item["mirror"] != null && (bool)item["mirror"])
                {
                    float mw = F(item, "mirror_w", w), m0 = F(item, "mirror_y", top + 0.25f);
                    Mirror(B, (w - mw) / 2, (w + mw) / 2, m0, m0 + F(item, "mirror_h", 0.80f), F(item, "mirror_off", 0.05f));
                }
                break;
            }
            case "wc":
            {
                float pre = F(item, "prewall", 0.20f), bw = Mathf.Min(w, 0.37f);
                if (pre > 0) B.Add(("tiles", -0.10f, w + 0.10f, 0, pre, 0, h));
                B.Add(("ceramic", (w - bw) / 2, (w + bw) / 2, pre, pre + 0.54f, 0.20f, 0.40f));
                B.Add(("dark", (w - bw) / 2 + 0.02f, (w + bw) / 2 - 0.02f, pre + 0.02f, pre + 0.52f, 0.40f, 0.42f));
                B.Add(("chrome", w / 2 - 0.12f, w / 2 + 0.12f, pre - 0.005f, pre, 0.95f, 1.10f));
                break;
            }
            case "washer":
                B.Add(("ceramic", 0, w, 0, d, 0, h)); B.Add(("dark", w * 0.2f, w * 0.8f, d, d + 0.01f, h * 0.25f, h * 0.75f));
                break;
            case "radiator":
            {
                float y0 = F(item, "y", 0.30f);
                for (int i = 0; i < (int)(h / 0.08f); i++) B.Add(("chrome", 0, w, 0.02f, d, y0 + i * 0.08f, y0 + i * 0.08f + 0.03f));
                B.Add(("chrome", 0, 0.03f, 0.02f, d, y0, y0 + h)); B.Add(("chrome", w - 0.03f, w, 0.02f, d, y0, y0 + h));
                lift = 0f;
                break;
            }
            case "mirror": { float y0 = F(item, "y", 1.10f); Mirror(B, 0, w, y0, y0 + h, F(item, "mirror_off", 0.05f)); lift = 0f; break; }
            case "fireplace":
            {
                // u=0..w along the wall, the glazed side is open, the other side meets the chimney/wall
                bool left = ((string)item["glass_side"] ?? "left") == "left";
                float fb0 = 0.42f, fb1 = 1.02f, back = 0.16f, cheek = 0.10f, gl = 0.008f;
                float g0 = left ? 0f : cheek, g1 = left ? w - cheek : w;                // glazed span along u
                B.Add(("stone", 0, w, 0, d, 0, fb0 - 0.05f));                                       // base
                B.Add(("stone", left ? -0.03f : 0, left ? w : w + 0.03f, 0, d + 0.03f, fb0 - 0.05f, fb0)); // ledge
                B.Add(("dark", 0, w, 0, back, fb0, fb1));                                             // firebox back
                B.Add(("plaster", left ? w - cheek : 0, left ? w : cheek, 0, d, fb0, fb1));           // closed side
                float c0 = g0 + (g1 - g0) * 0.25f, c1 = g0 + (g1 - g0) * 0.75f;
                B.Add(("wood", c0, c1, back + 0.10f, back + 0.20f, fb0, fb0 + 0.07f));                // logs
                B.Add(("fire", c0 + 0.04f, c1 - 0.04f, back + 0.11f, back + 0.19f, fb0 + 0.07f, fb0 + 0.24f));
                B.Add(("ember", c0 + 0.12f, c1 - 0.12f, back + 0.13f, back + 0.17f, fb0 + 0.24f, fb0 + 0.34f));
                B.Add(("glass", g0, g1, d - gl, d, fb0, fb1));                                        // front glass
                if (left) B.Add(("glass", 0, gl, back, d, fb0, fb1)); else B.Add(("glass", w - gl, w, back, d, fb0, fb1));
                B.Add(("dark", left ? 0 : w - 0.02f, left ? 0.02f : w, d - 0.02f, d, fb0, fb1));      // corner post
                B.Add(("dark", 0, w, 0, d, fb1, fb1 + 0.04f));                                        // frame band
                B.Add(("plaster", 0, w, 0, d, fb1 + 0.04f, h));                                       // cladding
                break;
            }
            case "cabinet": { float y0 = F(item, "y", 1.40f); B.Add(("wood", 0, w, 0, d, y0, y0 + h)); lift = 0f; break; }
            case "tv":
            {
                float y0 = F(item, "y", 0.85f);
                B.Add(("dark", 0, w, 0, d, y0, y0 + h));
                B.Add(("screen", 0.015f, w - 0.015f, d, d + 0.003f, y0 + 0.015f, y0 + h - 0.015f));
                lift = 0f;
                break;
            }
            case "sofa_corner":
            {
                // run along the wall (u 0..w, back on the wall) + return along the perpendicular wall at u=0
                float l = F(item, "l", 1.6f), seat = 0.45f, back = 0.22f, arm = 0.18f, armH = 0.62f;
                var S = new List<(string, float, float, float, float, float, float)>
                {
                    ("dark", 0.05f, w - 0.05f, 0.05f, d - 0.05f, 0f, 0.10f), ("fabric", 0, w, 0, d, 0.10f, seat),
                    ("fabric", 0, w, 0, back, seat, h), ("fabric", w - arm, w, back, d, seat, armH),
                    ("dark", 0.05f, d - 0.05f, d, l - 0.05f, 0f, 0.10f), ("fabric", 0, d, d, l, 0.10f, seat),
                    ("fabric", 0, back, back, l, seat, h), ("fabric", back, d, l - arm, l, seat, armH),
                    ("gap", w * 0.5f, w * 0.5f + 0.006f, back, d, seat, seat + 0.004f),            // cushion seams
                };
                bool atEnd = (string)item["corner"] == "end";
                foreach (var (m, u0, u1, v0, v1, y0, y1) in S) B.Add(atEnd ? (m, w - u1, w - u0, v0, v1, y0, y1) : (m, u0, u1, v0, v1, y0, y1));
                break;
            }
            case "dining_table":
            {
                const float top = 0.04f, lg = 0.07f, ins = 0.06f;
                B.Add(("oak", 0, w, 0, d, h - top, h));
                foreach (var (lu, lv) in new[] { (ins, ins), (w - ins - lg, ins), (ins, d - ins - lg), (w - ins - lg, d - ins - lg) })
                    B.Add(("dark", lu, lu + lg, lv, lv + lg, 0, h - top));
                int chairs = item["chairs"] != null ? (int)item["chairs"] : 6, perSide = Mathf.Max(0, chairs / 2);
                for (int i = 0; i < perSide; i++)
                {
                    float c = w * (i + 0.5f) / perSide;
                    Chair(B, c, -0.05f, -1f);   // seat front at the table edge, back away from it
                    Chair(B, c, d + 0.05f, 1f);
                }
                break;
            }
            case "bed":
            {
                float hb = F(item, "headboard", 1.0f);
                B.Add(("oak", 0, w, 0, 0.06f, 0, hb));                                     // headboard on the wall
                B.Add(("dark", 0.10f, w - 0.10f, 0.20f, d - 0.15f, 0, 0.08f));             // recessed base
                B.Add(("oak", 0, w, 0.06f, d, 0.08f, 0.30f));                              // frame
                B.Add(("linen", 0.03f, w - 0.03f, 0.08f, d - 0.03f, 0.30f, h));            // mattress
                B.Add(("duvet", 0.02f, w - 0.02f, 0.60f, d - 0.02f, h, h + 0.06f));        // duvet
                int pillows = w > 1.2f ? 2 : 1;
                float pw = (w - 0.12f) / pillows;
                for (int i = 0; i < pillows; i++)
                    B.Add(("linen", 0.06f + i * pw + 0.03f, 0.06f + (i + 1) * pw - 0.03f, 0.12f, 0.52f, h, h + 0.12f));
                if (item["nightstands"] != null && (bool)item["nightstands"])
                {
                    B.Add(("oak", -0.50f, -0.05f, 0, 0.40f, 0, 0.50f));
                    B.Add(("oak", w + 0.05f, w + 0.50f, 0, 0.40f, 0, 0.50f));
                }
                break;
            }
            case "desk":
            {
                B.Add(("oak", 0, w, 0, d, h - 0.03f, h));
                B.Add(("fronts", 0, 0.03f, 0.02f, d - 0.02f, 0, h - 0.03f));
                B.Add(("fronts", w - 0.40f, w, 0.02f, d - 0.04f, 0, h - 0.03f));           // drawer unit
                for (int i = 1; i < 3; i++)                                                // drawer gaps
                    B.Add(("gap", w - 0.38f, w - 0.02f, d - 0.04f, d - 0.037f, (h - 0.03f) * i / 3f - 0.002f, (h - 0.03f) * i / 3f + 0.002f));
                if (item["chair"] == null || (bool)item["chair"])
                {
                    // office chair in front, facing the desk
                    float c = (w - 0.40f) / 2f, v0 = d + 0.10f;
                    B.Add(("dark", c - 0.30f, c + 0.30f, v0 + 0.05f, v0 + 0.65f, 0, 0.05f));
                    B.Add(("chrome", c - 0.03f, c + 0.03f, v0 + 0.32f, v0 + 0.38f, 0.05f, 0.45f));
                    B.Add(("fabric", c - 0.25f, c + 0.25f, v0 + 0.10f, v0 + 0.60f, 0.45f, 0.52f));
                    B.Add(("fabric", c - 0.23f, c + 0.23f, v0 + 0.56f, v0 + 0.62f, 0.55f, 1.05f));
                }
                break;
            }
            case "tv_stand":
            {
                // plinth, body, three fronts (gap lines) with slim handle strips along the top edge
                B.Add(("dark", 0.05f, w - 0.05f, 0.03f, d - 0.03f, 0f, 0.07f));
                B.Add(("oak", 0, w, 0, d, 0.07f, h));
                for (int i = 1; i < 3; i++) B.Add(("gap", w * i / 3f - 0.002f, w * i / 3f + 0.002f, d, d + 0.004f, 0.08f, h - 0.01f));
                for (int i = 0; i < 3; i++) B.Add(("steel", w * i / 3f + 0.05f, w * (i + 1) / 3f - 0.05f, d, d + 0.012f, h - 0.06f, h - 0.045f));
                break;
            }
            case "coffee_table":
            {
                const float top = 0.035f, leg = 0.045f;
                B.Add(("oak", 0, w, 0, d, h - top, h));                                                            // top
                B.Add(("oak", leg, w - leg, leg, d - leg, 0.12f, 0.145f));                                         // lower shelf
                foreach (var (lu, lv) in new[] { (0f, 0f), (w - leg, 0f), (0f, d - leg), (w - leg, d - leg) })
                    B.Add(("dark", lu, lu + leg, lv, lv + leg, 0f, h - top));                                      // legs
                B.Add(("linen", w * 0.12f, w * 0.12f + 0.26f, d * 0.2f, d * 0.2f + 0.19f, h, h + 0.035f));         // books and a small tray on top
                B.Add(("duvet", w * 0.12f + 0.02f, w * 0.12f + 0.24f, d * 0.2f + 0.02f, d * 0.2f + 0.17f, h + 0.035f, h + 0.06f));
                B.Add(("ceramic", w * 0.68f, w * 0.68f + 0.12f, d * 0.55f, d * 0.55f + 0.12f, h, h + 0.10f));      // a vase
                break;
            }
            case "shoe_cabinet":
            {
                // body with three tilting drawer fronts (gap lines), handle strips, a dark plinth
                B.Add(("dark", 0.03f, w - 0.03f, 0.02f, d - 0.02f, 0f, 0.08f));
                B.Add(("oak", 0, w, 0, d, 0.08f, h));
                float fh = (h - 0.08f) / 3f;
                for (int i = 0; i < 3; i++)
                {
                    float y0 = 0.08f + i * fh;
                    if (i > 0) B.Add(("gap", 0.01f, w - 0.01f, d, d + 0.004f, y0 - 0.002f, y0 + 0.002f));
                    B.Add(("steel", w * 0.25f, w * 0.75f, d, d + 0.012f, y0 + fh - 0.05f, y0 + fh - 0.035f));
                }
                break;
            }
            case "coat_rack":
            {
                // a wooden board on the wall with five hooks, a hat shelf above it and two jackets hanging from it
                float y0 = F(item, "y", 1.65f);
                B.Add(("wood", 0, w, 0, 0.02f, y0, y0 + h));
                B.Add(("wood", 0, w, 0, 0.20f, y0 + h + 0.16f, y0 + h + 0.18f));
                for (int i = 0; i < 5; i++)
                {
                    float hu = w * (i + 0.5f) / 5f;
                    B.Add(("chrome", hu - 0.01f, hu + 0.01f, 0.02f, 0.07f, y0 + 0.05f, y0 + 0.07f));
                    B.Add(("chrome", hu - 0.01f, hu + 0.01f, 0.06f, 0.07f, y0 + 0.05f, y0 + 0.10f));
                }
                foreach (int i in new[] { 1, 3 })
                {
                    float hu = w * (i + 0.5f) / 5f;
                    B.Add(("fabric", hu - 0.17f, hu + 0.17f, 0.04f, 0.12f, y0 - 0.80f, y0 + 0.06f));
                }
                lift = 0f;
                break;
            }
            case "wardrobe":
            {
                string fr = (string)item["color"] ?? "fronts";
                bool open = item["open"] != null && (bool)item["open"];
                B.Add(("dark", 0.03f, w - 0.03f, 0.03f, d - 0.03f, 0f, 0.08f));
                if (open)
                {
                    // sides, top, shelves on one half, a hanging rail with clothes on the other
                    B.Add(("oak", 0, 0.02f, 0, d, 0.08f, h)); B.Add(("oak", w - 0.02f, w, 0, d, 0.08f, h));
                    B.Add(("oak", 0, w, 0, d, h - 0.02f, h)); B.Add(("oak", 0, w, 0, 0.01f, 0.08f, h));
                    float half = w / 2f;
                    for (int i = 1; i <= 5; i++) B.Add(("oak", 0.02f, half, 0.01f, d - 0.03f, 0.08f + i * (h - 0.1f) / 6f, 0.10f + i * (h - 0.1f) / 6f));
                    B.Add(("chrome", half, w - 0.02f, d * 0.45f, d * 0.45f + 0.025f, h - 0.25f, h - 0.225f));
                    for (int i = 0; i < 6; i++) B.Add(((i % 2 == 0) ? "fabric" : "linen", half + 0.03f + i * (w - half - 0.1f) / 6f, half + 0.03f + (i + 0.8f) * (w - half - 0.1f) / 6f,
                        d * 0.45f - 0.10f, d * 0.45f + 0.10f, h - 1.35f, h - 0.27f));
                }
                else
                {
                    B.Add((fr, 0, w, 0, d, 0.08f, h));
                    int doors = Mathf.Max(1, Mathf.RoundToInt(w / 0.5f));
                    for (int i = 1; i < doors; i++) B.Add(("gap", w * i / doors - 0.002f, w * i / doors + 0.002f, d, d + 0.004f, 0.09f, h - 0.01f));
                    for (int i = 0; i < doors; i++)
                    {
                        float hu = (i % 2 == 0) ? w * (i + 1) / doors - 0.05f : w * i / doors + 0.05f;   // handles at the middle gap of a pair
                        B.Add(("steel", hu - 0.006f, hu + 0.006f, d, d + 0.02f, h * 0.45f, h * 0.45f + 0.25f));
                    }
                }
                break;
            }
            case "kitchen_base":
            {
                string fr = (string)item["color"] ?? "fronts", top = (string)item["top"] ?? "oak";
                const float plinth = 0.10f, wt = 0.04f;
                B.Add(("dark", 0, w, 0, d - 0.06f, 0, plinth));                        // recessed plinth
                B.Add((fr, 0, w, 0, d - 0.02f, plinth, h - wt));                         // carcass + fronts
                B.Add((top, 0, w, 0, d + 0.02f, h - wt, h));                             // worktop, a little overhang
                Doors(B, w, d - 0.02f, plinth, h - wt, 0.6f);                            // door/drawer gaps
                if (Range(item, "dishwasher", out float a0, out float a1))
                    B.Add(("steel", a0 + 0.005f, a1 - 0.005f, d - 0.02f, d - 0.01f, plinth + 0.02f, h - wt - 0.02f));
                if (Range(item, "sink", out a0, out a1))
                {
                    B.Add(("steel", a0, a1, 0.08f, d - 0.08f, h - 0.002f, h + 0.004f));                    // rim
                    B.Add(("water", a0 + 0.04f, a0 + (a1 - a0) * 0.62f, 0.12f, d - 0.12f, h + 0.004f, h + 0.006f)); // bowl
                    float tc = a0 + (a1 - a0) * 0.35f;
                    B.Add(("chrome", tc - 0.02f, tc + 0.02f, 0.03f, 0.07f, h, h + 0.30f));                 // tap
                    B.Add(("chrome", tc - 0.015f, tc + 0.015f, 0.05f, 0.24f, h + 0.27f, h + 0.30f));
                }
                if (Range(item, "hob", out a0, out a1))
                    B.Add(("dark", a0, a1, 0.05f, d - 0.03f, h, h + 0.006f));
                break;
            }
            case "kitchen_wall":
            {
                float y0 = F(item, "y", 1.45f);
                bool glass = item["glass"] != null && (bool)item["glass"];
                B.Add(((string)item["color"] ?? "fronts", 0, w, 0, d - (glass ? 0.02f : 0f), y0, y0 + h));
                if (glass) B.Add(("glass", 0, w, d - 0.02f, d, y0, y0 + h));
                else Doors(B, w, d, y0, y0 + h, 0.6f);
                lift = 0f;
                break;
            }
            case "kitchen_tall":
            {
                string fr = (string)item["color"] ?? "fronts";
                B.Add(("dark", 0, w, 0, d - 0.06f, 0, 0.10f));
                B.Add((fr, 0, w, 0, d, 0.10f, h));
                if (item["oven"] != null && (bool)item["oven"])
                {
                    // built-in oven: steel surround, a control strip (display + five knobs) over a black glass door with a lighter
                    // inner window, a horizontal handle bar on two brackets
                    float y0 = 0.80f, y1 = 1.40f, cu0 = 0.03f, cu1 = w - 0.03f, fz = d + 0.01f;
                    B.Add(("steel", cu0, cu1, d, fz, y0, y1));
                    B.Add(("dark", cu0 + 0.01f, cu1 - 0.01f, fz, fz + 0.002f, y1 - 0.10f, y1 - 0.01f));                       // control strip
                    B.Add(("ember", w / 2f - 0.05f, w / 2f + 0.05f, fz + 0.002f, fz + 0.003f, y1 - 0.065f, y1 - 0.045f));    // display
                    for (int i = 0; i < 5; i++)
                    {
                        float ku = cu0 + 0.05f + i * (cu1 - cu0 - 0.10f) / 4f;
                        if (i == 2) continue;
                        B.Add(("chrome", ku - 0.012f, ku + 0.012f, fz + 0.002f, fz + 0.016f, y1 - 0.07f, y1 - 0.04f));       // knobs
                    }
                    B.Add(("dark", cu0 + 0.01f, cu1 - 0.01f, fz, fz + 0.003f, y0 + 0.01f, y1 - 0.11f));                       // door glass
                    B.Add(("screen", cu0 + 0.07f, cu1 - 0.07f, fz + 0.003f, fz + 0.004f, y0 + 0.08f, y1 - 0.20f));          // inner window
                    B.Add(("steel", cu0 + 0.03f, cu1 - 0.03f, fz + 0.028f, fz + 0.042f, y1 - 0.145f, y1 - 0.125f));          // handle bar
                    B.Add(("steel", cu0 + 0.04f, cu0 + 0.055f, fz, fz + 0.03f, y1 - 0.145f, y1 - 0.125f));                   // brackets
                    B.Add(("steel", cu1 - 0.055f, cu1 - 0.04f, fz, fz + 0.03f, y1 - 0.145f, y1 - 0.125f));
                }
                if (item["microwave"] != null && (bool)item["microwave"])
                {
                    // built-in microwave: steel front, a black door with a visible mesh window and a vertical handle on its right edge,
                    // a control column on the right (display, a knob and three buttons)
                    float y0 = 1.45f, y1 = 1.85f, cu0 = 0.03f, cu1 = w - 0.03f, fz = d + 0.01f, du = cu0 + (cu1 - cu0) * 0.70f;
                    B.Add(("steel", cu0, cu1, d, fz, y0, y1));
                    B.Add(("dark", cu0 + 0.012f, du, fz, fz + 0.003f, y0 + 0.012f, y1 - 0.012f));                           // door
                    B.Add(("screen", cu0 + 0.05f, du - 0.05f, fz + 0.003f, fz + 0.0045f, y0 + 0.06f, y1 - 0.06f));          // window
                    B.Add(("steel", du - 0.03f, du - 0.015f, fz + 0.003f, fz + 0.03f, y0 + 0.05f, y1 - 0.05f));             // vertical handle
                    B.Add(("dark", du + 0.01f, cu1 - 0.012f, fz, fz + 0.002f, y0 + 0.012f, y1 - 0.012f));                    // control panel
                    B.Add(("ember", du + 0.025f, cu1 - 0.03f, fz + 0.002f, fz + 0.003f, y1 - 0.09f, y1 - 0.07f));            // display
                    float kc = (du + cu1) / 2f;
                    B.Add(("chrome", kc - 0.02f, kc + 0.02f, fz + 0.002f, fz + 0.016f, y0 + 0.14f, y0 + 0.18f));            // knob
                    for (int i = 0; i < 3; i++) B.Add(("steel", du + 0.025f, cu1 - 0.03f, fz + 0.002f, fz + 0.006f, y0 + 0.03f + i * 0.03f, y0 + 0.048f + i * 0.03f));
                }
                if (item["fridge"] != null && (bool)item["fridge"])
                    B.Add(("gap", 0, w, d, d + 0.004f, 0.88f, 0.90f));                   // fridge / freezer split
                B.Add(("gap", 0, 0.004f, d, d + 0.004f, 0.10f, h));                      // edge line
                break;
            }
            case "island":
            {
                // u = its width, v = its depth; give it "x"/"z" (GLB axes) to stand free in the room.
                const float side = 0.04f, wt = 0.04f;
                B.Add(("oak", 0, w, 0, d, h - wt, h));                                   // top
                B.Add(("oak", 0, side, 0, d, 0, h - wt)); B.Add(("oak", w - side, w, 0, d, 0, h - wt)); // side panels
                B.Add(((string)item["color"] ?? "fronts", side, w - side, 0.05f, d - 0.05f, 0.10f, h - wt));
                B.Add(("dark", side, w - side, 0.10f, d - 0.10f, 0, 0.10f));
                break;
            }
            default: B.Add(((string)item["color"] ?? "wood", 0, w, 0, d, 0, h)); break;
        }
        return B;
    }

    /// <summary>"key": [from, to] along the item (u), if present.</summary>
    static bool Range(JObject item, string key, out float a, out float b)
    {
        a = b = 0f;
        if (item[key] is not JArray r || r.Count < 2) return false;
        a = (float)r[0]; b = (float)r[1];
        return b > a;
    }

    /// <summary>A wall mirror: thin dark frame, silvery-blue glass and two light streaks, so it reads as a mirror
    /// against a white wall (the walk-through has no reflections).</summary>
    static void Mirror(List<(string, float, float, float, float, float, float)> B, float u0, float u1, float y0, float y1, float v = 0.05f)
    {
        // the floor rectangle's edge can sit a few cm inside the drawn wall (a neighbour room's wall may even
        // overlap it), so stand the mirror off it by v ("mirror_off")
        const float f = 0.015f;
        float w = u1 - u0;
        B.Add(("dark", u0, u1, 0, v + 0.015f, y0, y1));
        B.Add(("mirror", u0 + f, u1 - f, v, v + 0.02f, y0 + f, y1 - f));
        B.Add(("sheen", u0 + w * 0.16f, u0 + w * 0.20f, v + 0.02f, v + 0.021f, y0 + 0.06f, y1 - 0.06f));
        B.Add(("sheen", u0 + w * 0.24f, u0 + w * 0.255f, v + 0.02f, v + 0.021f, y0 + 0.06f, y1 - 0.06f));
    }

    /// <summary>Dining chair centred at u = c, its seat front at v = edge, extending away from it along
    /// sign (-1 / +1); the back is on the far side.</summary>
    static void Chair(List<(string, float, float, float, float, float, float)> B, float c, float edge, float sign)
    {
        const float hw = 0.22f, depth = 0.45f, seat = 0.45f, lg = 0.035f;
        float near = edge, far = edge + sign * depth;
        float v0 = Mathf.Min(near, far), v1 = Mathf.Max(near, far);
        B.Add(("oak", c - hw, c + hw, v0, v1, seat - 0.04f, seat));
        float bv0 = sign > 0 ? v1 - 0.04f : v0, bv1 = sign > 0 ? v1 : v0 + 0.04f;
        B.Add(("oak", c - hw, c + hw, bv0, bv1, seat, 0.90f));
        foreach (var lu in new[] { c - hw + 0.02f, c + hw - 0.02f - lg })
            foreach (var lv in new[] { v0 + 0.02f, v1 - 0.02f - lg })
                B.Add(("dark", lu, lu + lg, lv, lv + lg, 0, seat - 0.04f));
    }

    /// <summary>Thin dark lines on a run's front face every ~unit wide (door/drawer gaps).</summary>
    static void Doors(List<(string, float, float, float, float, float, float)> B, float w, float d, float y0, float y1, float unit)
    {
        int n = Mathf.Max(1, Mathf.RoundToInt(w / unit));
        for (int i = 1; i < n; i++)
        {
            float u = w * i / n;
            B.Add(("gap", u - 0.002f, u + 0.002f, d, d + 0.003f, y0 + 0.01f, y1 - 0.01f));
        }
    }

    /// <summary>Local (u, v) -> GLB-axes (x, z), as furnish.py's place().</summary>
    /// <summary>The shower set (Hansgrohe, as ordered): a concealed thermostat (square trim plate, two knobs), a 450 mm wall arm with a
    /// 300 x 300 mm ultra-thin head, a wall elbow with holder, a 1600 mm hose hanging in a loop and the hand shower in its holder.
    /// "set_u" = where along the wall the arm is (default the middle); all chrome.</summary>
    static void ShowerSet(List<(string, float, float, float, float, float, float)> B, JObject item, float w, float d, float h)
    {
        // The set is drawn on a wall: a = along it, n = out of it. "set_wall": "back" (default, the wall behind) or "left" / "right" (the
        // enclosure's short side wall at u = 0 / u = w), "set_u" = where along that wall (default the middle).
        string wall = (string)item["set_wall"] ?? "back";
        float len = wall == "back" ? w : d;
        float cu = Mathf.Clamp(F(item, "set_u", len / 2f), 0.2f, len - 0.2f);
        void Bx(string m, float a0, float a1, float n0, float n1, float y0, float y1)
        {
            if (wall == "left") B.Add((m, n0, n1, a0, a1, y0, y1));
            else if (wall == "right") B.Add((m, w - n1, w - n0, a0, a1, y0, y1));
            else B.Add((m, a0, a1, n0, n1, y0, y1));
        }
        float room = wall == "back" ? d : w;                         // how far the arm can reach into the enclosure
        // thermostat: trim plate and two knobs
        Bx("chrome", cu - 0.095f, cu + 0.095f, 0f, 0.012f, 0.95f, 1.14f);
        Bx("dark", cu - 0.07f, cu + 0.07f, 0.012f, 0.014f, 1.00f, 1.09f);
        Bx("chrome", cu - 0.065f, cu - 0.025f, 0.012f, 0.032f, 1.025f, 1.065f);
        Bx("chrome", cu + 0.025f, cu + 0.065f, 0.012f, 0.032f, 1.025f, 1.065f);
        // wall arm 450 mm with a rosette, and the square head 300 x 300 x 4 mm on a ball joint
        float ay = Mathf.Min(2.05f, h - 0.08f), arm = Mathf.Min(0.45f, room - 0.30f), hd = 0.30f;
        Bx("chrome", cu - 0.035f, cu + 0.035f, 0f, 0.015f, ay - 0.035f, ay + 0.035f);
        Bx("chrome", cu - 0.009f, cu + 0.009f, 0.015f, arm, ay - 0.009f, ay + 0.009f);
        float hv0 = Mathf.Max(0.05f, arm - hd / 2f - 0.02f);
        Bx("chrome", cu - 0.02f, cu + 0.02f, arm - 0.03f, arm + 0.01f, ay - 0.03f, ay - 0.009f);
        Bx("chrome", cu - hd / 2f, cu + hd / 2f, hv0, hv0 + hd, ay - 0.034f, ay - 0.030f);
        // wall elbow with the hand shower holder, the hose in a loop, the hand shower
        float fu = cu + 0.28f <= len - 0.12f ? cu + 0.28f : Mathf.Max(0.12f, cu - 0.28f);
        Bx("chrome", fu - 0.03f, fu + 0.03f, 0f, 0.075f, 0.88f, 0.94f);
        Bx("chrome", fu - 0.018f, fu + 0.018f, 0f, 0.05f, 1.00f, 1.04f);                       // holder
        Bx("chrome", fu - 0.02f, fu + 0.02f, 0.03f, 0.075f, 1.04f, 1.27f);                     // hand shower
        Bx("chrome", fu - 0.03f, fu + 0.03f, 0.025f, 0.08f, 1.27f, 1.30f);
        Bx("steel", fu - 0.006f, fu + 0.006f, 0.04f, 0.052f, 0.50f, 0.88f);                    // hose: down, across, up
        Bx("steel", fu - 0.006f, fu + 0.006f, 0.04f, 0.16f, 0.50f, 0.512f);
        Bx("steel", fu - 0.006f, fu + 0.006f, 0.148f, 0.16f, 0.50f, 1.04f);
    }

    /// <summary>The bath's fittings on the short wall at its end (u = 0 = "left" / u = w = "right"), in the middle of that wall: a concealed
    /// mixer for filling (round trim plate + lever), the filler spout over the rim, and a small hand shower on a holder with its hose.
    /// "tap": "left" | "right".</summary>
    static void BathTap(List<(string, float, float, float, float, float, float)> B, string end, float w, float d, float h)
    {
        float c = d / 2f;
        void Bx(string m, float n0, float n1, float a0, float a1, float y0, float y1)   // n = out of the end wall, a = along it (v)
        {
            if (end == "right") B.Add((m, w - n1, w - n0, a0, a1, y0, y1)); else B.Add((m, n0, n1, a0, a1, y0, y1));
        }
        Bx("chrome", 0f, 0.012f, c - 0.07f, c + 0.07f, 0.80f, 0.94f);              // trim plate of the concealed mixer
        Bx("chrome", 0.012f, 0.05f, c - 0.012f, c + 0.012f, 0.86f, 0.88f);          // lever
        Bx("chrome", 0f, 0.14f, c - 0.015f, c + 0.015f, h + 0.07f, h + 0.10f);      // filler spout over the rim
        Bx("chrome", 0f, 0.03f, c - 0.02f, c + 0.02f, 1.10f, 1.16f);                // hand shower holder (small)
        Bx("chrome", 0.03f, 0.065f, c - 0.015f, c + 0.015f, 1.00f, 1.30f);          // the small hand shower
        Bx("steel", 0.03f, 0.042f, c - 0.006f, c + 0.006f, 0.70f, 1.00f);           // its hose, looped down
        Bx("steel", 0.03f, 0.10f, c - 0.006f, c + 0.006f, 0.70f, 0.712f);
    }

    static Func<float, float, (float, float)> Place(RoomBox room, JObject item)
    {
        string wall = (string)item["wall"] ?? "-z";
        float along = F(item, "along", 0f), gap = F(item, "gap", 0f);
        float? x0 = item["x"] != null ? (float?)F(item, "x", 0f) : null, z0 = item["z"] != null ? (float?)F(item, "z", 0f) : null;
        if (wall == "-z" || wall == "+z")
        {
            float bx = x0 ?? room.xmin + along;
            float bz = z0 ?? (wall == "-z" ? room.zmin + gap : room.zmax - gap);
            float s = wall == "-z" ? 1f : -1f;
            return (u, v) => (bx + u, bz + s * v);
        }
        float bz2 = z0 ?? room.zmin + along;
        float bx2 = x0 ?? (wall == "-x" ? room.xmin + gap : room.xmax - gap);
        float s2 = wall == "-x" ? 1f : -1f;
        return (u, v) => (bx2 + s2 * v, bz2 + u);
    }

    /// <summary>An axis-aligned box given in GLB axes, as a model-space part (Z mirrored back).</summary>
    static XRMeshPart Box(string name, string mat, float x0, float x1, float y0, float y1, float z0, float z1, string modelMat = "FURNITURE")
    {
        // modelMat FLOOR/WALL/CEILING (extra rooms) gets the walk-through's own floor/wall/ceiling colours
        float ax = Mathf.Min(x0, x1), bx = Mathf.Max(x0, x1), ay = Mathf.Min(y0, y1), by = Mathf.Max(y0, y1);
        float az = Mathf.Min(-z0, -z1), bz = Mathf.Max(-z0, -z1);
        var size = new Vector3(Mathf.Max(0.002f, bx - ax), Mathf.Max(0.002f, by - ay), Mathf.Max(0.002f, bz - az));
        var c = new Vector3((ax + bx) / 2f, (ay + by) / 2f, (az + bz) / 2f);
        if (mat.EndsWith(SlideMat)) mat = mat.Substring(0, mat.Length - SlideMat.Length);
        var part = XRModelFactory.CreateSolidBoxPart(mat == "glass" ? GlassPart : "FURN_" + name, c, Quaternion.identity, size, modelMat, Vector3.zero, 0f);
        part.color = ColorOf(mat);
        return part;
    }

    static Color ColorOf(string mat)
    {
        if (Materials.TryGetValue(mat, out var c)) return new Color(c.r, c.g, c.b);
        if (mat != null && mat.StartsWith("#") && ColorUtility.TryParseHtmlString(mat, out var h)) return h;
        return Materials["wood"];
    }
}

/// <summary>The furniture of the current scan inside one built walk-through model: meshes with colliders (you
/// can't walk through a bathtub), glass see-through. Rebuilt with the model.</summary>
public class FurnitureInModel
{
    GameObject go;
    readonly List<Material> mats = new List<Material>();
    GameObject lastVisual; float lastYaw; Vector3 lastCenter; List<MRUKRoom> lastRooms; string lastScan; int lastLayer;

    /// <summary>The spec the furniture was last built from (moving items edits it, then Save + Reload).</summary>
    public JObject Spec { get; private set; }
    public string ScanName => lastScan;

    /// <summary>Below 1 the roofs are see-through (the Dollhouse is looked into from above).</summary>
    public float RoofAlpha = 1f;

    /// <summary>False leaves the surroundings (ground, fences, trees...) out - the Dollhouse.</summary>
    public bool WithSite = true;

    /// <summary>Rebuilds from the current Spec (after an item was moved/turned).</summary>
    public void Reload()
    {
        if (lastVisual && Spec != null) Build(lastVisual, lastYaw, lastCenter, lastRooms, lastScan, lastLayer, Spec);
    }

    /// <summary>The item group a hit collider belongs to (null if it isn't a movable piece of furniture).</summary>
    public Transform ItemOf(Collider c, out string roomKey, out int index)
    {
        roomKey = null; index = -1;
        for (var t = c ? c.transform : null; t && go && t != go.transform; t = t.parent)
            if (Furniture.TryParseItem(t.name, out roomKey, out index)) return t;
        return null;
    }

    /// <summary>The sliding parts (shower doors) grouped per door, each with its opening vector in model space.</summary>
    public List<(List<Transform> parts, Vector3 open)> Sliders()
    {
        var res = new List<(List<Transform>, Vector3)>();
        if (!go) return res;
        var groups = new Dictionary<string, (List<Transform>, Vector3)>();
        foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
        {
            if (!Furniture.TryParseSlide(mf.name, out var gname, out var open)) continue;
            string key = (mf.transform.parent ? mf.transform.parent.name : "") + "/" + gname;
            if (!groups.TryGetValue(key, out var g)) { g = (new List<Transform>(), open); groups[key] = g; }
            g.Item1.Add(mf.transform);
        }
        res.AddRange(groups.Values);
        return res;
    }

    public void Apply(GameObject visual, float yaw, Vector3 center, List<MRUKRoom> rooms, string scanName, int layer)
    {
        Build(visual, yaw, center, rooms, scanName, layer, string.IsNullOrEmpty(scanName) ? null : Furniture.Load(scanName));
    }

    void Build(GameObject visual, float yaw, Vector3 center, List<MRUKRoom> rooms, string scanName, int layer, JObject spec)
    {
        Clear();
        lastVisual = visual; lastYaw = yaw; lastCenter = center; lastRooms = rooms; lastScan = scanName; lastLayer = layer;
        Spec = spec;
        if (!visual || string.IsNullOrEmpty(scanName)) return;
        if (spec == null) return;
        var model = Furniture.Build(spec, rooms, yaw, center);
        if (!WithSite) model.rooms.RemoveAll(r => r.roomName == SiteModel.GroupName);
        if (model.rooms.Count == 0) return;
        go = UnityModelLoader.LoadToScene(model, shaded: true);
        if (!go) return;
        go.name = "Furniture";
        go.transform.SetParent(visual.transform, false);
        var glass = Shader.Find("Sprites/Default");
        foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
        {
            mf.gameObject.layer = layer;
            if (mf.sharedMesh) mf.gameObject.AddComponent<MeshCollider>().sharedMesh = mf.sharedMesh;
            bool roof = mf.name == Furniture.RoofPart && RoofAlpha < 1f;
            if ((!mf.name.StartsWith(Furniture.GlassPart) && !roof) || glass == null) continue;
            var mr = mf.GetComponent<MeshRenderer>();
            if (mr.sharedMaterial) UnityEngine.Object.Destroy(mr.sharedMaterial);
            var m = new Material(glass) { color = roof ? new Color(0.27f, 0.28f, 0.30f, RoofAlpha) : new Color(1f, 1f, 1f, 0.3f) };
            mats.Add(m);
            mr.sharedMaterial = m;
        }
        Debug.Log($"[Furniture] {scanName}: {model.rooms.Sum(r => r.parts.Count)} parts in {model.rooms.Count} room(s)");
    }

    public void Clear()
    {
        if (go)
        {
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true)) if (mf.sharedMesh) UnityEngine.Object.Destroy(mf.sharedMesh);
            foreach (var mr in go.GetComponentsInChildren<Renderer>(true)) if (mr.sharedMaterial) UnityEngine.Object.Destroy(mr.sharedMaterial);
            go.transform.SetParent(null, false);
            UnityEngine.Object.Destroy(go);
        }
        go = null;
        mats.Clear();
    }
}
