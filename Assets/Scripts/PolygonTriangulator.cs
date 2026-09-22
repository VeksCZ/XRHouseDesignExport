using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Ear-clipping triangulation for simple (also concave) polygons. Room floors and ceilings are often L-shaped, where a
/// triangle fan from the first corner throws triangles across the notch.
/// </summary>
public static class PolygonTriangulator
{
    /// <summary>Index triples into <paramref name="poly"/>, all wound counter-clockwise in the polygon's own 2D plane.</summary>
    public static List<int> Triangulate(IList<Vector2> poly)
    {
        var tris = new List<int>();
        int n = poly.Count;
        if (n < 3) return tris;

        var idx = new List<int>(n);
        for (int i = 0; i < n; i++) idx.Add(i);
        if (FloorPlanBuilder.SignedArea(poly) < 0) idx.Reverse(); // clip in counter-clockwise order

        int guard = n * n + 8;
        while (idx.Count > 3 && guard-- > 0)
        {
            bool clipped = false;
            for (int k = 0; k < idx.Count && !clipped; k++)
            {
                int i0 = idx[(k + idx.Count - 1) % idx.Count], i1 = idx[k], i2 = idx[(k + 1) % idx.Count];
                Vector2 a = poly[i0], b = poly[i1], c = poly[i2];
                if (Cross(a, b, c) <= 1e-7f) continue; // reflex or collinear corner: not an ear

                bool empty = true;
                foreach (int j in idx)
                {
                    if (j == i0 || j == i1 || j == i2) continue;
                    if (InsideTriangle(poly[j], a, b, c)) { empty = false; break; }
                }
                if (!empty) continue;

                tris.Add(i0); tris.Add(i1); tris.Add(i2);
                idx.RemoveAt(k);
                clipped = true;
            }
            if (!clipped) break; // degenerate outline (self-touching / collinear): finish with a fan below
        }

        if (idx.Count >= 3)
            for (int i = 1; i < idx.Count - 1; i++) { tris.Add(idx[0]); tris.Add(idx[i]); tris.Add(idx[i + 1]); }
        return tris;
    }

    /// <summary>
    /// Triangles for a horizontal polygon given as 3D points (the height is ignored). Wound so the face looks up
    /// (floor) or down (ceiling) with Unity's clockwise-front convention.
    /// </summary>
    public static List<int> TriangulateHorizontal(IList<Vector3> ring, bool faceUp)
    {
        var flat = new List<Vector2>(ring.Count);
        foreach (var p in ring) flat.Add(new Vector2(p.x, p.z));
        var t = Triangulate(flat); // counter-clockwise in (x, z) = facing down in Unity
        if (faceUp)
            for (int i = 0; i < t.Count; i += 3) { int tmp = t[i + 1]; t[i + 1] = t[i + 2]; t[i + 2] = tmp; }
        return t;
    }

    static float Cross(Vector2 a, Vector2 b, Vector2 c) => (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);

    static bool InsideTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        if ((p - a).sqrMagnitude < 1e-10f || (p - b).sqrMagnitude < 1e-10f || (p - c).sqrMagnitude < 1e-10f) return false; // repeated corner
        const float eps = -1e-7f;
        return Cross(a, b, p) >= eps && Cross(b, c, p) >= eps && Cross(c, a, p) >= eps;
    }
}
