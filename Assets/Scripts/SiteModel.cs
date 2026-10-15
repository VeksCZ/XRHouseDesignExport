using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// The surroundings of the house from the spec's "site" (GLB axes, metres): ground, paved and gravel areas, a field or
/// a bank (per-corner heights), fences (posts + see-through mesh), hedges, trees and simple gabled neighbouring
/// buildings - enough to see where you are when you step outside or look out of a window. Built into the furniture
/// model as the "Site" group (the Dollhouse leaves it out).
/// </summary>
public static class SiteModel
{
    public const string GroupName = "Site";

    static readonly Dictionary<string, Color> Colors = new Dictionary<string, Color>
    {
        { "grass", new Color(0.46f, 0.62f, 0.33f) }, { "bank", new Color(0.50f, 0.64f, 0.36f) },
        { "field", new Color(0.74f, 0.66f, 0.42f) }, { "gravel", new Color(0.66f, 0.64f, 0.60f) },
        { "paving", new Color(0.56f, 0.54f, 0.53f) }, { "concrete", new Color(0.74f, 0.74f, 0.72f) },
        { "hedge", new Color(0.21f, 0.38f, 0.19f) }, { "crown", new Color(0.33f, 0.53f, 0.26f) },
        { "trunk", new Color(0.42f, 0.32f, 0.22f) }, { "fence", new Color(0.22f, 0.28f, 0.24f) },
        { "barn", new Color(0.78f, 0.73f, 0.64f) }, { "tiles", new Color(0.55f, 0.28f, 0.20f) },
        { "wood", new Color(0.62f, 0.48f, 0.34f) },
    };

    static Color Col(string name, string fallback)
    {
        string n = name ?? fallback;
        if (Colors.TryGetValue(n, out var c)) return c;
        if (n.StartsWith("#") && ColorUtility.TryParseHtmlString(n, out var h)) return h;
        return Colors[fallback];
    }

    static float F(JObject o, string k, float d) => o?[k] != null ? (float)o[k] : d;

    public static XRRoomModel Build(JObject site)
    {
        var rm = new XRRoomModel { roomName = GroupName };
        if (site == null) return rm;
        float gy = F(site, "ground_y", 0f);

        // ground: a big flat square (grass) unless "ground": false
        if (site["ground"]?.Type != JTokenType.Boolean || (bool)site["ground"])
        {
            float s = F(site["ground"] as JObject, "size", 160f);
            var sq = new List<Vector3> { new Vector3(-s / 2, gy, -s / 2), new Vector3(s / 2, gy, -s / 2), new Vector3(s / 2, gy, s / 2), new Vector3(-s / 2, gy, s / 2) };
            rm.parts.Add(Prism("ground", sq, gy - 0.2f, Col((string)(site["ground"] as JObject)?["color"], "grass")));
        }

        // areas: [{"name", "poly": [[x,z] | [x,y,z], ...], "y": top (flat ones), "t": thickness, "color"}]
        foreach (var a in site["areas"] as JArray ?? new JArray())
        {
            if (a is not JObject ao || ao["poly"] is not JArray pa) continue;
            float top = F(ao, "y", gy + 0.02f), t = F(ao, "t", 0.1f);
            var pts = pa.OfType<JArray>().Select(p => p.Count >= 3 ? new Vector3((float)p[0], (float)p[1], (float)p[2]) : new Vector3((float)p[0], top, (float)p[1])).ToList();
            if (pts.Count < 3) continue;
            rm.parts.Add(Prism((string)ao["name"] ?? "area", pts, pts.Min(p => p.y) - t, Col((string)ao["color"], "concrete")));
        }

        // fences: [{"line": [[x,z], ...], "h", "post": spacing}] - posts plus a see-through mesh panel
        foreach (var f in site["fences"] as JArray ?? new JArray())
        {
            if (f is not JObject fo || fo["line"] is not JArray la) continue;
            float h = F(fo, "h", 1.8f), spacing = F(fo, "post", 2.5f);
            var line = la.OfType<JArray>().Select(p => new Vector2((float)p[0], (float)p[1])).ToList();
            for (int i = 0; i + 1 < line.Count; i++)
            {
                Vector2 a = line[i], b = line[i + 1];
                var panel = Strip("fence_panel", a, b, 0.02f, gy, gy + h, Col((string)fo["color"], "fence"));
                panel.name = Furniture.GlassPart;   // see-through like glass
                rm.parts.Add(panel);
                int n = Mathf.Max(1, Mathf.CeilToInt((b - a).magnitude / spacing));
                for (int k = 0; k <= n; k++)
                {
                    Vector2 p = Vector2.Lerp(a, b, k / (float)n);
                    rm.parts.Add(Prism("fence_post", Rect(p, 0.06f, 0.06f, gy + h + 0.05f), gy - 0.05f, Col((string)fo["color"], "fence")));
                }
            }
        }

        // hedges: [{"line": [[x,z], ...], "w", "h"}]
        foreach (var hd in site["hedges"] as JArray ?? new JArray())
        {
            if (hd is not JObject ho || ho["line"] is not JArray la) continue;
            float w = F(ho, "w", 1.4f), h = F(ho, "h", 2.4f);
            var line = la.OfType<JArray>().Select(p => new Vector2((float)p[0], (float)p[1])).ToList();
            for (int i = 0; i + 1 < line.Count; i++)
                rm.parts.Add(Strip("hedge", line[i], line[i + 1], w / 2f, gy, gy + h, Col((string)ho["color"], "hedge")));
        }

        // trees: [{"at": [x,z], "h", "r"}] - a trunk and an octagonal crown
        foreach (var tr in site["trees"] as JArray ?? new JArray())
        {
            if (tr is not JObject to || to["at"] is not JArray at) continue;
            var c = new Vector2((float)at[0], (float)at[1]);
            float h = F(to, "h", 4f), r = F(to, "r", 1.5f);
            rm.parts.Add(Prism("tree_trunk", Rect(c, 0.18f, 0.18f, gy + h * 0.45f), gy, Col(null, "trunk")));
            var oct = Enumerable.Range(0, 8).Select(k => { float an = k * Mathf.PI / 4f + Mathf.PI / 8f; return new Vector3(c.x + r * Mathf.Cos(an), gy + h, c.y + r * Mathf.Sin(an)); }).ToList();
            rm.parts.Add(Prism("tree_crown", oct, gy + h * 0.35f, Col((string)to["color"], "crown")));
        }

        // buildings: [{"name", "x0","x1","z0","z1", "eave", "ridge", "ridge_along": "x"|"z" (default: the longer side)}]
        foreach (var bd in site["buildings"] as JArray ?? new JArray())
        {
            if (bd is not JObject bo) continue;
            float x0 = F(bo, "x0", 0), x1 = F(bo, "x1", 0), z0 = F(bo, "z0", 0), z1 = F(bo, "z1", 0);
            float eave = gy + F(bo, "eave", 3.5f), ridge = gy + F(bo, "ridge", 6.5f), o = 0.4f, t = 0.2f;
            string name = (string)bo["name"] ?? "building";
            Color wc = Col((string)bo["color"], "barn"), rc = Col((string)bo["roof"], "tiles");
            rm.parts.Add(Prism(name, new List<Vector3> { new Vector3(x0, eave, z0), new Vector3(x1, eave, z0), new Vector3(x1, eave, z1), new Vector3(x0, eave, z1) }, gy, wc));
            bool alongZ = ((string)bo["ridge_along"] ?? ((z1 - z0) >= (x1 - x0) ? "z" : "x")) == "z";
            if (alongZ)
            {
                float xm = (x0 + x1) / 2f, half = (x1 - x0) / 2f + o, k = (ridge - eave) / ((x1 - x0) / 2f);
                Func<float, float> roofY = x => ridge - k * Mathf.Abs(x - xm);
                rm.parts.Add(Slab(name + "_roof", x0 - o, xm, z0 - o, z1 + o, (x, z) => roofY(x), t, rc));
                rm.parts.Add(Slab(name + "_roof", xm, x1 + o, z0 - o, z1 + o, (x, z) => roofY(x), t, rc));
                // gable ends
                rm.parts.Add(Gable(name, x0, x1, z0, eave, ridge, alongZ, wc)); rm.parts.Add(Gable(name, x0, x1, z1, eave, ridge, alongZ, wc));
            }
            else
            {
                float zm = (z0 + z1) / 2f, k = (ridge - eave) / ((z1 - z0) / 2f);
                Func<float, float> roofY = z => ridge - k * Mathf.Abs(z - zm);
                rm.parts.Add(Slab(name + "_roof", x0 - o, x1 + o, z0 - o, zm, (x, z) => roofY(z), t, rc));
                rm.parts.Add(Slab(name + "_roof", x0 - o, x1 + o, zm, z1 + o, (x, z) => roofY(z), t, rc));
                rm.parts.Add(Gable(name, z0, z1, x0, eave, ridge, alongZ, wc)); rm.parts.Add(Gable(name, z0, z1, x1, eave, ridge, alongZ, wc));
            }
        }
        return rm;
    }

    static List<Vector3> Rect(Vector2 c, float w, float d, float y) => new List<Vector3>
    { new Vector3(c.x - w / 2, y, c.y - d / 2), new Vector3(c.x + w / 2, y, c.y - d / 2), new Vector3(c.x + w / 2, y, c.y + d / 2), new Vector3(c.x - w / 2, y, c.y + d / 2) };

    /// <summary>A wall-like strip of half-width hw along a-b (GLB x/z), from y0 to y1.</summary>
    static XRMeshPart Strip(string name, Vector2 a, Vector2 b, float hw, float y0, float y1, Color c)
    {
        Vector2 d = (b - a).normalized, n = new Vector2(-d.y, d.x) * hw;
        var top = new List<Vector3> { V(a + n, y1), V(b + n, y1), V(b - n, y1), V(a - n, y1) };
        return Prism(name, top, y0, c);
        Vector3 V(Vector2 p, float y) => new Vector3(p.x, y, p.y);
    }

    /// <summary>A sloped slab (roof half) whose underside follows f(x, z), t thick.</summary>
    static XRMeshPart Slab(string name, float x0, float x1, float z0, float z1, Func<float, float, float> f, float t, Color c)
    {
        var top = new List<Vector3> { new Vector3(x0, f(x0, z0) + t, z0), new Vector3(x1, f(x1, z0) + t, z0), new Vector3(x1, f(x1, z1) + t, z1), new Vector3(x0, f(x0, z1) + t, z1) };
        var bot = top.Select(p => new Vector3(p.x, p.y - t, p.z)).ToList();
        return Solid(name, top, bot, c);
    }

    /// <summary>The triangle of wall between the eaves and the ridge at one end of a gabled building.</summary>
    static XRMeshPart Gable(string name, float a0, float a1, float at, float eave, float ridge, bool alongZ, Color c)
    {
        float am = (a0 + a1) / 2f;
        Vector3 P(float a, float y) => alongZ ? new Vector3(a, y, at) : new Vector3(at, y, a);
        var part = new XRMeshPart { name = "FURN_" + name + "_gable", materialName = "FURNITURE", color = c };
        foreach (var p in new[] { P(a0, eave), P(a1, eave), P(am, ridge) }) part.vertices.Add(new Vector3(p.x, p.y, -p.z));
        part.triangles.AddRange(new[] { 0, 1, 2, 0, 2, 1 });
        return part;
    }

    /// <summary>A vertical prism: the polygon "top" (GLB x, y, z; convex, any order round) down to y = bottom.</summary>
    static XRMeshPart Prism(string name, List<Vector3> top, float bottom, Color c)
    {
        var bot = top.Select(p => new Vector3(p.x, bottom, p.z)).ToList();
        return Solid(name, top, bot, c);
    }

    /// <summary>Top and bottom polygons (same count, matching corners), sides between them; double-sided.</summary>
    static XRMeshPart Solid(string name, List<Vector3> top, List<Vector3> bot, Color c)
    {
        var part = new XRMeshPart { name = "FURN_" + name, materialName = "FURNITURE", color = c };
        int n = top.Count;
        foreach (var p in top) part.vertices.Add(new Vector3(p.x, p.y, -p.z));   // GLB z -> model z
        foreach (var p in bot) part.vertices.Add(new Vector3(p.x, p.y, -p.z));
        void Tri(int a, int b, int d) { part.triangles.AddRange(new[] { a, b, d, a, d, b }); }
        for (int i = 1; i + 1 < n; i++) { Tri(0, i, i + 1); Tri(n, n + i, n + i + 1); }
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            Tri(i, j, n + j); Tri(i, n + j, n + i);
        }
        return part;
    }
}
