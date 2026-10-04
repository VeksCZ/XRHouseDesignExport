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
        { "mirror", new Color(0.85f, 0.90f, 0.95f) }, { "chrome", new Color(0.80f, 0.80f, 0.82f) },
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
                float w = F(item, "w", def.w), d = F(item, "d", def.d), h = F(item, "h", def.h);
                var boxes = Shape(item, type, w, d, h, out float lift);
                var toXZ = Place(box, item);
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
        return model;
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
                    if (s == "front") B.Add(("glass", a0, side.Type == JTokenType.Object ? F((JObject)side, "to", w) : w, d - gl, d, tray, h));
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
                    B.Add(("mirror", (w - mw) / 2, (w + mw) / 2, 0, 0.02f, m0, m0 + F(item, "mirror_h", 0.80f)));
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
            case "mirror": { float y0 = F(item, "y", 1.10f); B.Add(("mirror", 0, w, 0, d, y0, y0 + h)); lift = 0f; break; }
            case "cabinet": { float y0 = F(item, "y", 1.40f); B.Add(("wood", 0, w, 0, d, y0, y0 + h)); lift = 0f; break; }
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
    static XRMeshPart Box(string name, string mat, float x0, float x1, float y0, float y1, float z0, float z1)
    {
        float ax = Mathf.Min(x0, x1), bx = Mathf.Max(x0, x1), ay = Mathf.Min(y0, y1), by = Mathf.Max(y0, y1);
        float az = Mathf.Min(-z0, -z1), bz = Mathf.Max(-z0, -z1);
        var size = new Vector3(Mathf.Max(0.002f, bx - ax), Mathf.Max(0.002f, by - ay), Mathf.Max(0.002f, bz - az));
        var c = new Vector3((ax + bx) / 2f, (ay + by) / 2f, (az + bz) / 2f);
        var part = XRModelFactory.CreateSolidBoxPart(mat == "glass" ? GlassPart : "FURN_" + name, c, Quaternion.identity, size, "FURNITURE", Vector3.zero, 0f);
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
