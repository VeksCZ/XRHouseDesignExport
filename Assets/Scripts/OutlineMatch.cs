using System;
using System.Collections.Generic;

/// <summary>
/// Lines two scans of the same place up by the SHAPE of their rooms, for when they share no room UUID (the rooms were scanned again, so the live
/// scene has new ids): finds the rotation about the vertical axis + the shift that lays the live rooms' floor outlines over the saved scan's
/// (<c>saved = Rotate(yaw) * live + shift</c>, Rotate as Unity's <c>Quaternion.Euler(0, yaw, 0)</c> on (x, z)). Pure geometry, no Unity types -
/// tested offline in Tools/OfflineTests/WallTest. A result is only given when it is unambiguous (a single rectangular room fits two ways).
/// </summary>
public static class OutlineMatch
{
    public struct Fit { public float yawDeg, tx, tz, iou; public int rooms; public float score; }

    public static (float x, float z) Rotate((float x, float z) p, float yawDeg)
    {
        float a = yawDeg * (float)Math.PI / 180f, c = (float)Math.Cos(a), s = (float)Math.Sin(a);
        return (p.x * c + p.z * s, -p.x * s + p.z * c);
    }

    public static float Area(IList<(float x, float z)> poly)
    {
        double a = 0;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++) a += (double)poly[j].x * poly[i].z - (double)poly[i].x * poly[j].z;
        return (float)Math.Abs(a / 2);
    }

    public static (float x, float z) Centroid(IList<(float x, float z)> poly)
    {
        double ax = 0, az = 0, a = 0;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            double cr = (double)poly[j].x * poly[i].z - (double)poly[i].x * poly[j].z;
            a += cr; ax += (poly[j].x + poly[i].x) * cr; az += (poly[j].z + poly[i].z) * cr;
        }
        if (Math.Abs(a) < 1e-9) return (poly[0].x, poly[0].z);
        return ((float)(ax / (3 * a)), (float)(az / (3 * a)));
    }

    public static bool Inside(IList<(float x, float z)> poly, float x, float z)
    {
        bool c = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            if ((poly[i].z > z) != (poly[j].z > z) && x < (poly[j].x - poly[i].x) * (z - poly[i].z) / (poly[j].z - poly[i].z) + poly[i].x) c = !c;
        return c;
    }

    static List<(float x, float z)> Moved(IList<(float x, float z)> poly, float yaw, float tx, float tz)
    {
        var r = new List<(float, float)>(poly.Count);
        foreach (var p in poly) { var q = Rotate(p, yaw); r.Add((q.x + tx, q.z + tz)); }
        return r;
    }

    /// <summary>Intersection over union of two outlines, sampled on a grid of 'step' metres.</summary>
    public static float Iou(IList<(float x, float z)> a, IList<(float x, float z)> b, float step)
    {
        float x0 = float.MaxValue, x1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
        foreach (var p in a) { x0 = Math.Min(x0, p.x); x1 = Math.Max(x1, p.x); z0 = Math.Min(z0, p.z); z1 = Math.Max(z1, p.z); }
        foreach (var p in b) { x0 = Math.Min(x0, p.x); x1 = Math.Max(x1, p.x); z0 = Math.Min(z0, p.z); z1 = Math.Max(z1, p.z); }
        int inter = 0, uni = 0;
        for (float z = z0 + step / 2; z < z1; z += step)
            for (float x = x0 + step / 2; x < x1; x += step)
            {
                bool ia = Inside(a, x, z), ib = Inside(b, x, z);
                if (ia && ib) inter++;
                if (ia || ib) uni++;
            }
        return uni == 0 ? 0f : (float)inter / uni;
    }

    static float Heading(float x, float z) => (float)(Math.Atan2(x, z) * 180.0 / Math.PI);

    /// <summary>The longest edges' headings (degrees) of an outline.</summary>
    static List<float> MainHeadings(IList<(float x, float z)> poly, int n)
    {
        var edges = new List<(float len, float h)>();
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            float dx = poly[i].x - poly[j].x, dz = poly[i].z - poly[j].z;
            edges.Add(((float)Math.Sqrt(dx * dx + dz * dz), Heading(dx, dz)));
        }
        edges.Sort((p, q) => q.len.CompareTo(p.len));
        var r = new List<float>();
        for (int i = 0; i < edges.Count && r.Count < n; i++) r.Add(edges[i].h);
        return r;
    }

    /// <summary>The ways to lay 'moving' over 'fixedPoly' that overlap well (each refined by a small search): usually one, two for a shape that
    /// fits turned by 180 degrees as well (a plain rectangle).</summary>
    public static List<Fit> FitPair(IList<(float x, float z)> moving, IList<(float x, float z)> fixedPoly)
    {
        var res = new List<Fit>();
        if (moving.Count < 3 || fixedPoly.Count < 3) return res;
        var cm = Centroid(moving); var cf = Centroid(fixedPoly);
        var coarse = new List<Fit>();
        foreach (float hm in MainHeadings(moving, 2))
            foreach (float hf in MainHeadings(fixedPoly, 2))
                foreach (float flip in new[] { 0f, 180f })
                {
                    float yaw = hf - hm + flip;
                    var r = Rotate(cm, yaw);
                    float tx = cf.x - r.x, tz = cf.z - r.z;
                    float iou = Iou(Moved(moving, yaw, tx, tz), fixedPoly, 0.2f);
                    if (iou < 0.5f) continue;
                    bool dup = false;
                    foreach (var c in coarse) if (Math.Abs(Delta(c.yawDeg, yaw)) < 5f) { dup = true; break; }
                    if (!dup) coarse.Add(new Fit { yawDeg = yaw, tx = tx, tz = tz, iou = iou });
                }
        coarse.Sort((p, q) => q.iou.CompareTo(p.iou));
        for (int k = 0; k < Math.Min(coarse.Count, 4); k++)
        {
            var f = coarse[k]; float bestIou = f.iou, byaw = f.yawDeg, btx = f.tx, btz = f.tz;
            foreach (float step in new[] { 0.15f, 0.07f, 0.03f })
            {
                bool improved = true; int guard = 0;
                while (improved && guard++ < 12)
                {
                    improved = false;
                    foreach (var (dx, dz, dy) in new[] { (step, 0f, 0f), (-step, 0f, 0f), (0f, step, 0f), (0f, -step, 0f), (0f, 0f, 0.5f), (0f, 0f, -0.5f) })
                    {
                        float iou = Iou(Moved(moving, byaw + dy, btx + dx, btz + dz), fixedPoly, step < 0.1f ? 0.05f : 0.1f);
                        if (iou > bestIou + 0.002f) { bestIou = iou; byaw += dy; btx += dx; btz += dz; improved = true; }
                    }
                }
            }
            res.Add(new Fit { yawDeg = byaw, tx = btx, tz = btz, iou = bestIou, rooms = 1, score = bestIou });
        }
        return res;
    }

    /// <summary>Lines the live rooms up with the saved ones. Every plausible pair (similar area) proposes a transform; each is scored by how many live
    /// rooms then land on a saved room (overlap >= 0.6). The best transform wins; two different transforms with the same score = ambiguous = no result.</summary>
    public static bool Align(IList<List<(float x, float z)>> live, IList<List<(float x, float z)>> saved, out Fit result)
    {
        result = default;
        var cands = new List<Fit>();
        for (int i = 0; i < live.Count; i++)
        {
            float ai = Area(live[i]);
            if (ai < 1.5f) continue;
            for (int j = 0; j < saved.Count; j++)
            {
                float aj = Area(saved[j]);
                if (aj < 1.5f || aj / ai > 1.5f || ai / aj > 1.5f) continue;
                foreach (var f in FitPair(live[i], saved[j])) if (f.iou >= 0.7f) cands.Add(f);
            }
        }
        if (cands.Count == 0) return false;
        cands.Sort((p, q) => q.iou.CompareTo(p.iou));
        var scored = new List<Fit>();
        for (int c = 0; c < Math.Min(cands.Count, 12); c++)
        {
            var f = cands[c]; int rooms = 0; float sum = 0; var used = new HashSet<int>();
            for (int i = 0; i < live.Count; i++)
            {
                var mv = Moved(live[i], f.yawDeg, f.tx, f.tz);
                float bestJ = 0; int bj = -1;
                for (int j = 0; j < saved.Count; j++)
                {
                    if (used.Contains(j)) continue;
                    float iou = Iou(mv, saved[j], 0.15f);
                    if (iou > bestJ) { bestJ = iou; bj = j; }
                }
                if (bj >= 0 && bestJ >= 0.6f) { rooms++; sum += bestJ; used.Add(bj); }
            }
            f.rooms = rooms; f.score = rooms + sum / Math.Max(1, rooms) * 0.5f;
            scored.Add(f);
        }
        scored.Sort((p, q) => q.score.CompareTo(p.score));
        var top = scored[0];
        // ambiguous: another clearly different transform explains as many rooms as well (e.g. one rectangle that also fits turned by 180 degrees)
        foreach (var o in scored.GetRange(1, scored.Count - 1))
        {
            bool different = Math.Abs(Delta(o.yawDeg, top.yawDeg)) > 10f || Math.Abs(o.tx - top.tx) + Math.Abs(o.tz - top.tz) > 0.6f;
            if (different && o.rooms >= top.rooms && o.score >= top.score - 0.15f) return false;
        }
        result = top;
        return top.rooms >= 1;
    }

    static float Delta(float a, float b)
    {
        float d = (a - b) % 360f;
        if (d > 180f) d -= 360f; if (d < -180f) d += 360f;
        return d;
    }
}
