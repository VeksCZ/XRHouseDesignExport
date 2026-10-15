using System.Collections.Generic;
using System.Linq;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

/// <summary>
/// The real ceiling of a room from the scanned global mesh - the headset's Space Setup only ever stores one flat,
/// horizontal ceiling plane per room, but the mesh has the slope. Takes the near-horizontal triangles above head
/// height over the room's floor (well inside its outline, so walls and the neighbours' ceilings don't count), keeps
/// the lowest per 25 cm cell (the first surface straight up - the storey above's floor is higher), and fits a plane y = a + bx*x + bz*z (GLB axes)
/// through those cells, dropping outliers twice.
/// </summary>
public static class CeilingFit
{
    public struct Result
    {
        public float a, bx, bz;   // GLB y = a + bx*x + bz*z
        public float rms;         // metres, of the cells kept
        public float coverage;    // share of the room's (inner) floor cells that had ceiling above them
        public int cells;
        public float HeightAt(float x, float z) => a + bx * x + bz * z;
    }

    const float Cell = 0.25f, Inset = 0.25f, MinAbove = 1.9f, MaxAbove = 6f;

    // world-space vertices of each mesh, computed once per fitting session
    static readonly Dictionary<Mesh, Vector3[]> worldVerts = new Dictionary<Mesh, Vector3[]>();
    public static void ResetCache() => worldVerts.Clear();

    public static bool Fit(MRUKRoom room, Quaternion g, Vector3 center, out Result res)
    {
        res = default;
        var floor = room.FloorAnchors.FirstOrDefault(f => f != null && f.PlaneBoundary2D != null && f.PlaneBoundary2D.Count >= 3);
        var meshAnchor = room.GlobalMeshAnchor;
        var mesh = meshAnchor ? meshAnchor.GlobalMesh : null;
        if (floor == null || mesh == null || mesh.vertexCount == 0) return false;

        if (!worldVerts.TryGetValue(mesh, out var wv))
        {
            var v = mesh.vertices;
            wv = new Vector3[v.Length];
            for (int i = 0; i < v.Length; i++) wv[i] = meshAnchor.transform.TransformPoint(v[i]);
            worldVerts[mesh] = wv;
        }
        var tris = mesh.triangles;
        Quaternion fInv = Quaternion.Inverse(floor.transform.rotation);
        Vector3 fPos = floor.transform.position;
        var poly = floor.PlaneBoundary2D;
        float floorY = fPos.y;

        // lowest horizontal surface above head height per cell (GLB x/z grid)
        var top = new Dictionary<(int, int), float>();
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            Vector3 p0 = wv[tris[t]], p1 = wv[tris[t + 1]], p2 = wv[tris[t + 2]];
            Vector3 n = Vector3.Cross(p1 - p0, p2 - p0);
            float area2 = n.magnitude;
            if (area2 < 1e-6f) continue;
            n /= area2;
            if (Mathf.Abs(n.y) < 0.75f) continue;   // ceilings are near-horizontal (either winding)
            Vector3 c = (p0 + p1 + p2) / 3f;
            float above = c.y - floorY;
            if (above < MinAbove || above > MaxAbove) continue;
            Vector3 l = fInv * (c - fPos);
            var uv = new Vector2(l.x, l.y);
            if (!Inside(uv, poly) || EdgeDistance(uv, poly) < Inset) continue;
            Vector3 m = g * (c - center);
            var key = (Mathf.FloorToInt(m.x / Cell), Mathf.FloorToInt(-m.z / Cell));
            float y = m.y;
            if (!top.TryGetValue(key, out float old) || y < old) top[key] = y;
        }
        if (top.Count < 12) return false;

        var pts = top.Select(kv => new Vector3((kv.Key.Item1 + 0.5f) * Cell, kv.Value, (kv.Key.Item2 + 0.5f) * Cell)).ToList();
        // lamps hang below it, holes in the scan show the storey above: start from the cells near the median
        float med = Percentile(pts.Select(p => p.y).ToList(), 0.5f);
        pts = pts.Where(p => Mathf.Abs(p.y - med) < 0.6f).ToList();
        Result r = default;
        for (int it = 0; it < 3; it++)
        {
            if (pts.Count < 8 || !LeastSquares(pts, out r)) return false;
            var rr = r;
            float cut = it == 0 ? 0.30f : 0.12f;
            var kept = pts.Where(p => Mathf.Abs(p.y - rr.HeightAt(p.x, p.z)) < cut).ToList();
            if (kept.Count == pts.Count) break;
            pts = kept;
        }
        if (pts.Count < 8) return false;
        r.rms = Mathf.Sqrt(pts.Average(p => { float d = p.y - r.HeightAt(p.x, p.z); return d * d; }));
        r.cells = pts.Count;
        r.coverage = Mathf.Clamp01(pts.Count * Cell * Cell / Mathf.Max(0.5f, InnerArea(poly)));
        res = r;
        return true;
    }

    static bool LeastSquares(List<Vector3> pts, out Result r)
    {
        // normal equations for y = a + bx*x + bz*z
        double n = pts.Count, sx = 0, sz = 0, sy = 0, sxx = 0, szz = 0, sxz = 0, sxy = 0, szy = 0;
        foreach (var p in pts)
        {
            sx += p.x; sz += p.z; sy += p.y; sxx += p.x * p.x; szz += p.z * p.z; sxz += p.x * p.z; sxy += p.x * p.y; szy += p.z * p.y;
        }
        var A = new double[3, 3] { { n, sx, sz }, { sx, sxx, sxz }, { sz, sxz, szz } };
        var B = new double[] { sy, sxy, szy };
        r = default;
        if (!Solve3(A, B, out var s)) return false;
        r.a = (float)s[0]; r.bx = (float)s[1]; r.bz = (float)s[2];
        return true;
    }

    static bool Solve3(double[,] A, double[] b, out double[] x)
    {
        x = new double[3];
        double det(double[,] m) =>
            m[0, 0] * (m[1, 1] * m[2, 2] - m[1, 2] * m[2, 1]) - m[0, 1] * (m[1, 0] * m[2, 2] - m[1, 2] * m[2, 0]) + m[0, 2] * (m[1, 0] * m[2, 1] - m[1, 1] * m[2, 0]);
        double d = det(A);
        if (System.Math.Abs(d) < 1e-9) return false;
        for (int k = 0; k < 3; k++)
        {
            var M = (double[,])A.Clone();
            for (int i = 0; i < 3; i++) M[i, k] = b[i];
            x[k] = det(M) / d;
        }
        return true;
    }

    static float Percentile(List<float> v, float q)
    {
        v.Sort();
        return v[Mathf.Clamp(Mathf.RoundToInt(q * (v.Count - 1)), 0, v.Count - 1)];
    }

    static bool Inside(Vector2 p, IReadOnlyList<Vector2> poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            if ((poly[i].y > p.y) != (poly[j].y > p.y) &&
                p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x)
                inside = !inside;
        return inside;
    }

    static float EdgeDistance(Vector2 p, IReadOnlyList<Vector2> poly)
    {
        float best = float.MaxValue;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            Vector2 a = poly[j], b = poly[i], ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(1e-6f, ab.sqrMagnitude));
            best = Mathf.Min(best, (a + ab * t - p).magnitude);
        }
        return best;
    }

    static float InnerArea(IReadOnlyList<Vector2> poly)
    {
        float a = 0f;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++) a += poly[j].x * poly[i].y - poly[i].x * poly[j].y;
        a = Mathf.Abs(a) / 2f;
        // minus the inset band, roughly (perimeter * inset)
        float per = 0f;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++) per += (poly[i] - poly[j]).magnitude;
        return Mathf.Max(0.5f, a - per * Inset);
    }
}
