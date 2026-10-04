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
    public static JObject Load(string scanName)
    {
        string path = MRUKSceneCache.FurniturePath(scanName);
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try { return JObject.Parse(File.ReadAllText(path)); }
        catch (Exception ex) { Debug.LogWarning($"[Furniture] {path}: {ex.Message}"); return null; }
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
    };

    /// <summary>Glass parts are named so the viewer can make them see-through.</summary>
    public const string GlassPart = "FURN_glass";

    /// <summary>Room's floor in GLB axes (model space with Z mirrored).</summary>
    struct RoomBox { public float xmin, xmax, zmin, zmax, y; }

    /// <summary>The furniture as model parts (model space: Euler(0,yaw,0) * (scanWorld - center)), one XRRoomModel
    /// per furnished room. Unknown rooms/types are skipped with a warning.</summary>
    public static XRHouseModel Build(JObject spec, List<MRUKRoom> rooms, float yaw, Vector3 center)
    {
        var model = new XRHouseModel();
        if (spec?["rooms"] is not JArray specRooms) return model;
        Quaternion g = Quaternion.Euler(0, yaw, 0);
        foreach (var rs in specRooms.OfType<JObject>())
        {
            string key = ((string)rs["room"] ?? "").ToLowerInvariant();
            if (key.StartsWith("room_")) key = key.Substring(5);
            var room = rooms.FirstOrDefault(r => DoorCatalog.RoomId(r).ToLowerInvariant().StartsWith(key));
            if (room == null || !TryRoomBox(room, g, center, out var box)) { Debug.LogWarning($"[Furniture] room '{rs["room"]}' not found"); continue; }
            var rm = new XRRoomModel { roomName = "Furniture_" + key };
            int k = 0;
            foreach (var item in (rs["items"] as JArray ?? new JArray()).OfType<JObject>())
            {
                k++;
                string type = (string)item["type"] ?? "box";
                if (!Defaults.TryGetValue(type, out var def)) { Debug.LogWarning($"[Furniture] unknown type '{type}'"); continue; }
                float w = F(item, "w", def.w), d = F(item, "d", def.d);
                var toXZ = Place(box, item);
                float h;
                if (item["h"]?.Type == JTokenType.String && (string)item["h"] == "ceiling")
                {
                    // up to the ceiling above the item's middle (ceilings can slope), minus its own lift
                    var (cx, cz) = toXZ(w / 2f, d / 2f);
                    Vector3 world = Quaternion.Inverse(g) * new Vector3(cx, box.y, -cz) + center;
                    float ceil = XRModelFactory.SampleCeilingHeight(room.CeilingAnchors, world, world.y + 2.6f) - center.y;
                    h = Mathf.Max(0.1f, ceil - box.y - F(item, "y", 0f));
                }
                else h = F(item, "h", def.h);
                var boxes = Shape(item, type, w, d, h, out float lift);
                foreach (var (mat, u0, u1, v0, v1, y0, y1) in boxes)
                {
                    var (xa, za) = toXZ(u0, v0);
                    var (xb, zb) = toXZ(u1, v1);
                    float yb = box.y + lift;
                    rm.parts.Add(Box((string)item["name"] ?? $"{type}_{k}", mat, xa, xb, yb + y0, yb + y1, za, zb));
                }
            }
            model.rooms.Add(rm);
        }
        foreach (var er in ExtraRooms(spec, rooms, g, center))
        {
            // floor, ceiling and only the listed walls (the others are neighbours' walls that already exist)
            var rm = new XRRoomModel { roomName = "Extra_" + er.name };
            const float slab = 0.05f;
            rm.parts.Add(Box(er.name + "_floor", "floor", er.x0, er.x1, er.floor - slab, er.floor, er.z0, er.z1, "FLOOR"));
            rm.parts.Add(Box(er.name + "_ceiling", "ceiling", er.x0, er.x1, er.ceil, er.ceil + slab, er.z0, er.z1, "CEILING"));
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
                float y0 = er.floor - slab, y1 = er.ceil + slab;
                if (side == "-x") rm.parts.Add(Box(er.name + "_wall", "wall", er.x0 - t, er.x0, y0, y1, a, b, "WALL"));
                else if (side == "+x") rm.parts.Add(Box(er.name + "_wall", "wall", er.x1, er.x1 + t, y0, y1, a, b, "WALL"));
                else if (side == "-z") rm.parts.Add(Box(er.name + "_wall", "wall", a, b, y0, y1, er.z0 - t, er.z0, "WALL"));
                else if (side == "+z") rm.parts.Add(Box(er.name + "_wall", "wall", a, b, y0, y1, er.z1, er.z1 + t, "WALL"));
            }
            model.rooms.Add(rm);
        }
        return model;
    }

    /// <summary>A room the headset couldn't scan any more (the scan has a room limit), drawn from the spec:
    /// "extra_rooms": [{"name", "like": room id (floor height and ceiling taken from it), "x0","x1","z0","z1"
    /// (GLB axes), "walls": ["-x", {"side": "+z", "from": x, "to": x}, ...]}]. Only the listed walls are drawn -
    /// the other sides are neighbours' walls, which get thin where they meet it (see <see cref="ExtraFaces"/>).</summary>
    struct ExtraRoom { public string name; public JObject spec; public float x0, x1, z0, z1, floor, ceil; }

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
            yield return new ExtraRoom { name = (string)er["name"] ?? "extra", spec = er, x0 = x0, x1 = x1, z0 = z0, z1 = z1, floor = floor, ceil = floor + F(er, "h", h) };
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
                        B.Add(("glass", mid - 0.05f, a1, d - 2 * gl - 0.012f, d - gl - 0.012f, tray, h - rail));
                        B.Add(("chrome", a0, a1, d - 0.04f, d, h - rail, h));
                        B.Add(("chrome", a1 - 0.10f, a1 - 0.08f, d - gl - 0.012f, d + 0.02f, 0.95f, 1.25f));
                    }
                    else if (s == "front") B.Add(("glass", a0, side.Type == JTokenType.Object ? F((JObject)side, "to", w) : w, d - gl, d, tray, h));
                    else if (s == "left") B.Add(("glass", 0, gl, a0, side.Type == JTokenType.Object ? F((JObject)side, "to", d) : d, tray, h));
                    else if (s == "right") B.Add(("glass", w - gl, w, a0, side.Type == JTokenType.Object ? F((JObject)side, "to", d) : d, tray, h));
                }
                B.Add(("chrome", w / 2 - 0.08f, w / 2 + 0.08f, 0f, 0.25f, h - 0.02f, h));
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
                    B.Add(("steel", 0.03f, w - 0.03f, d, d + 0.01f, 0.80f, 1.40f));
                    B.Add(("dark", 0.08f, w - 0.08f, d + 0.01f, d + 0.012f, 0.88f, 1.28f));
                }
                if (item["microwave"] != null && (bool)item["microwave"])
                {
                    B.Add(("steel", 0.03f, w - 0.03f, d, d + 0.01f, 1.45f, 1.85f));
                    B.Add(("dark", 0.06f, w * 0.68f, d + 0.01f, d + 0.012f, 1.50f, 1.80f));
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

    public void Apply(GameObject visual, float yaw, Vector3 center, List<MRUKRoom> rooms, string scanName, int layer)
    {
        Clear();
        if (!visual || string.IsNullOrEmpty(scanName)) return;
        var spec = Furniture.Load(scanName);
        if (spec == null) return;
        var model = Furniture.Build(spec, rooms, yaw, center);
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
            if (mf.name != Furniture.GlassPart || glass == null) continue;
            var mr = mf.GetComponent<MeshRenderer>();
            if (mr.sharedMaterial) UnityEngine.Object.Destroy(mr.sharedMaterial);
            var m = new Material(glass) { color = new Color(1f, 1f, 1f, 0.3f) };
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
