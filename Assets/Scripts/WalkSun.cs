using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Where the sun shines in through the windows at a chosen date and time. The walk-through model is unlit (baked
/// vertex shading), so instead of real-time shadows every window is sampled on a 10 cm grid: a cell is lit when
/// nothing outside blocks the way to the sun (roof overhangs, the garage, the other wing) and the ray from it into
/// the house lands where the sunny patch is drawn - on the floor, a wall or furniture. Plus a sun disc in the sky.
/// Site: the spec's "site": {"lat", "lon", "north": [x, z]} (north as a direction in GLB axes); times are local
/// (Central European, with summer time).
/// </summary>
public class WalkSun
{
    public double Lat = 49.8, Lon = 15.5;      // overwritten from the spec
    public Vector2 NorthGlb = new Vector2(0f, -1f);
    public DateTime Local = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, 12, 0, 0);
    public bool On { get; private set; }

    GameObject patches, disc;
    Material patchMat, discMat;
    Transform model;
    Vector3 toSunModel;
    float elevation, azimuth;

    const float Cell = 0.10f;

    public string Status => !On ? "Sun off" :
        $"{Local:d.M.} {Local:HH:mm}  sun {(elevation > 0 ? $"{elevation:0}° high, from {Compass(azimuth)}" : "below the horizon")}";

    static string Compass(float az)
    {
        string[] n = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        return n[Mathf.RoundToInt(((az % 360f) + 360f) % 360f / 45f) % 8];
    }

    /// <summary>Turns it on/off; while on, call Rebuild after the time or the model changed.</summary>
    public void SetOn(bool on) { On = on; if (!on) Clear(); }

    public void Step(double hours, int months)
    {
        Local = Local.AddHours(hours).AddMonths(months);
    }

    /// <summary>windows: (centre, rotation with +Z into the room, width, height) in model space.</summary>
    public void Rebuild(Transform modelRoot, List<(Vector3 c, Quaternion r, float w, float h)> windows, int mask)
    {
        Clear();
        if (!On || !modelRoot) return;
        model = modelRoot;
        SunPosition(ToUtc(Local), Lat, Lon, out azimuth, out elevation);
        // direction to the sun in GLB axes, then model space (GLB z mirrored)
        Vector2 N = NorthGlb.normalized, E = new Vector2(-N.y, N.x);
        float az = azimuth * Mathf.Deg2Rad, el = elevation * Mathf.Deg2Rad;
        Vector2 h = (E * Mathf.Sin(az) + N * Mathf.Cos(az)) * Mathf.Cos(el);
        toSunModel = new Vector3(h.x, Mathf.Sin(el), -h.y).normalized;

        var shader = Shader.Find("Sprites/Default");
        discMat = new Material(shader) { color = new Color(1f, 0.93f, 0.6f, 1f) };
        disc = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        disc.name = "SunDisc";
        UnityEngine.Object.Destroy(disc.GetComponent<Collider>());
        disc.GetComponent<Renderer>().sharedMaterial = discMat;
        if (elevation <= 0f) return;

        Vector3 toSun = model.TransformDirection(toSunModel).normalized;   // world
        var verts = new List<Vector3>(); var tris = new List<int>();
        foreach (var (c, r, w, hgt) in windows)
        {
            Vector3 inward = r * Vector3.forward;
            if (Vector3.Dot(-toSunModel, inward) <= 0.02f) continue;   // the sun is behind this window
            int nx = Mathf.Max(1, Mathf.RoundToInt(w / Cell)), ny = Mathf.Max(1, Mathf.RoundToInt(hgt / Cell));
            for (int i = 0; i < nx; i++)
                for (int j = 0; j < ny; j++)
                {
                    Vector3 local = new Vector3((i + 0.5f) / nx * w - w / 2f, (j + 0.5f) / ny * hgt - hgt / 2f, 0f);
                    Vector3 p = c + r * local;                                     // on the window plane, model space
                    Vector3 outside = model.TransformPoint(p - inward * 0.45f);   // beyond the wall
                    if (Physics.Raycast(outside, toSun, 60f, mask, QueryTriggerInteraction.Ignore)) continue;  // shaded
                    Vector3 inside = model.TransformPoint(p + inward * 0.09f);    // past the sash
                    if (!Physics.Raycast(inside, -toSun, out var hit, 25f, mask, QueryTriggerInteraction.Ignore)) continue;
                    AddPatch(verts, tris, model.InverseTransformPoint(hit.point), model.InverseTransformDirection(hit.normal),
                             w / nx, hgt / ny, r);
                }
        }
        if (verts.Count == 0) return;
        var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(verts); mesh.SetTriangles(tris, 0); mesh.RecalculateBounds();
        patches = new GameObject("SunPatches");
        patches.transform.SetParent(model, false);
        patches.AddComponent<MeshFilter>().sharedMesh = mesh;
        patchMat = new Material(shader) { color = new Color(1f, 0.86f, 0.45f, 0.45f) };
        patches.AddComponent<MeshRenderer>().sharedMaterial = patchMat;
    }

    /// <summary>The light beam of one window cell lands on a surface: its footprint there, stretched along the
    /// sun's direction as it hits at a slant (model space).</summary>
    void AddPatch(List<Vector3> v, List<int> t, Vector3 at, Vector3 n, float cw, float ch, Quaternion winRot)
    {
        Vector3 d = -toSunModel;   // light travel direction
        // project the cell's two edges (window right/up) along d onto the surface plane
        Vector3 Proj(Vector3 e) { float dn = Vector3.Dot(d, n); return Mathf.Abs(dn) < 0.05f ? e : e - d * (Vector3.Dot(e, n) / dn); }
        Vector3 a = Proj(winRot * Vector3.right * cw) * 0.5f, b = Proj(winRot * Vector3.up * ch) * 0.5f;
        if (a.magnitude > 1f) a = a.normalized; if (b.magnitude > 1f) b = b.normalized;
        Vector3 o = at + n * 0.012f;
        int k = v.Count;
        v.Add(o - a - b); v.Add(o + a - b); v.Add(o + a + b); v.Add(o - a + b);
        t.Add(k); t.Add(k + 1); t.Add(k + 2); t.Add(k); t.Add(k + 2); t.Add(k + 3);
        t.Add(k); t.Add(k + 2); t.Add(k + 1); t.Add(k); t.Add(k + 3); t.Add(k + 2);   // both sides
    }

    /// <summary>Keeps the sun disc in the sky (far along the sun direction from your head).</summary>
    public void Tick(Transform head)
    {
        if (!disc || !model || !head) return;
        bool up = elevation > -1f;
        disc.SetActive(up);
        if (!up) return;
        Vector3 toSun = model.TransformDirection(toSunModel).normalized;
        disc.transform.position = head.position + toSun * 80f;
        disc.transform.localScale = Vector3.one * 3.5f;
    }

    public void Clear()
    {
        if (patches) { var mf = patches.GetComponent<MeshFilter>(); if (mf && mf.sharedMesh) UnityEngine.Object.Destroy(mf.sharedMesh); UnityEngine.Object.Destroy(patches); }
        if (disc) UnityEngine.Object.Destroy(disc);
        if (patchMat) UnityEngine.Object.Destroy(patchMat);
        if (discMat) UnityEngine.Object.Destroy(discMat);
        patches = disc = null; patchMat = discMat = null;
    }

    // ---------- astronomy ----------

    /// <summary>Central European time with EU summer time (last Sunday of March .. last Sunday of October, 01:00 UTC).</summary>
    public static DateTime ToUtc(DateTime local)
    {
        DateTime LastSunday(int year, int month)
        {
            var d = new DateTime(year, month, DateTime.DaysInMonth(year, month));
            while (d.DayOfWeek != DayOfWeek.Sunday) d = d.AddDays(-1);
            return d;
        }
        DateTime utc = local.AddHours(-1);
        DateTime start = LastSunday(local.Year, 3).AddHours(1), end = LastSunday(local.Year, 10).AddHours(1);
        if (utc >= start && utc < end) utc = local.AddHours(-2);
        return DateTime.SpecifyKind(utc, DateTimeKind.Utc);
    }

    /// <summary>Solar azimuth (degrees from north, clockwise) and elevation (degrees) - the usual low-precision
    /// formulae (about 0.5 degree), plenty for where a sunny patch lands.</summary>
    public static void SunPosition(DateTime utc, double lat, double lon, out float azimuth, out float elevation)
    {
        const double R = Math.PI / 180.0;
        double n = (utc - new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)).TotalDays;
        double L = (280.460 + 0.9856474 * n) % 360.0;
        double g = ((357.528 + 0.9856003 * n) % 360.0) * R;
        double lambda = (L + 1.915 * Math.Sin(g) + 0.020 * Math.Sin(2 * g)) * R;
        double eps = (23.439 - 0.0000004 * n) * R;
        double ra = Math.Atan2(Math.Cos(eps) * Math.Sin(lambda), Math.Cos(lambda));
        double dec = Math.Asin(Math.Sin(eps) * Math.Sin(lambda));
        double gmst = (18.697374558 + 24.06570982441908 * n) % 24.0;
        double ha = (gmst * 15.0 + lon) * R - ra;
        double phi = lat * R;
        double el = Math.Asin(Math.Sin(phi) * Math.Sin(dec) + Math.Cos(phi) * Math.Cos(dec) * Math.Cos(ha));
        double az = Math.Atan2(-Math.Sin(ha), Math.Tan(dec) * Math.Cos(phi) - Math.Sin(phi) * Math.Cos(ha));
        azimuth = (float)(((az / R) + 360.0) % 360.0);
        elevation = (float)(el / R);
    }
}
