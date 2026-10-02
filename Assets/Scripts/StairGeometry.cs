using System.Collections.Generic;
using System.Linq;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

/// <summary>
/// Turns a StairEdit into real geometry: flights of steps, square landings, and the plan footprint used to cut the
/// stairwell opening into the floor/ceiling above. Everything is computed in the scan's world frame first
/// (Resolve), then converted to model space for the walk-through/Dollhouse.
/// </summary>
public static class StairGeometry
{
    public const float DefaultRiser = 0.175f;
    static readonly Color StairColor = new Color(0.72f, 0.62f, 0.48f);

    /// <summary>A stair resolved into scan-world geometry.</summary>
    public class Resolved
    {
        public List<Vector3> points = new List<Vector3>(); // walking-line points at bottom floor height
        public float bottomY, topY, width, rise;
        public readonly List<(Vector3 start, Vector3 end, Vector3 dir, int risers, float y0)> flights = new List<(Vector3, Vector3, Vector3, int, float)>();
        public readonly List<(Vector3 center, Vector3 dir, float y)> landings = new List<(Vector3, Vector3, float)>();
        /// <summary>Plan rectangles (4 corners each, scan world, y ignored) - the stairwell footprint.</summary>
        public readonly List<Vector3[]> footprint = new List<Vector3[]>();
    }

    public static List<Vector3> WorldPoints(StairEdit s, List<MRUKRoom> rooms)
    {
        var room = rooms.FirstOrDefault(r => DoorCatalog.RoomId(r) == s.roomUuid);
        return s.localPoints.Select(p =>
        {
            var v = new Vector3(p.x, p.y, p.z);
            return room != null ? room.transform.TransformPoint(v) : v;
        }).ToList();
    }

    /// <summary>Floor height of the story above 'bottomY' (lowest floor anchor at least 1.5 m higher).</summary>
    public static float TopFloorY(List<MRUKRoom> rooms, float bottomY)
    {
        float best = float.MaxValue;
        foreach (var r in rooms)
            foreach (var f in r.FloorAnchors)
                if (f != null && f.transform.position.y > bottomY + 1.5f) best = Mathf.Min(best, f.transform.position.y);
        return best < float.MaxValue ? best : bottomY + 2.9f;
    }

    /// <summary>Horizontal length of each flight for the given walking-line points and width.</summary>
    public static List<float> FlightLengths(List<Vector3> pts, float width)
    {
        var result = new List<float>();
        int k = pts.Count - 2; // landings
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            Vector3 d = Flat(pts[i + 1] - pts[i]);
            float len = d.magnitude;
            if (i > 0) len -= width / 2f;      // starts at the far edge of the landing it leaves
            if (i < k) len -= width / 2f;      // ends at the near edge of the landing it reaches
            result.Add(Mathf.Max(0f, len));
        }
        return result;
    }

    /// <summary>Riser counts for a new stair: floor-to-floor / ~17.5 cm, shared out by flight length.</summary>
    public static List<int> DefaultRisers(List<Vector3> pts, float width, float rise)
    {
        var lens = FlightLengths(pts, width);
        int total = Mathf.Max(1, Mathf.RoundToInt(rise / DefaultRiser));
        float sum = lens.Sum();
        var n = lens.Select(l => sum > 0.01f && l > 0.15f ? Mathf.RoundToInt(total * l / sum) : 0).ToList();
        int diff = total - n.Sum();
        if (n.Count > 0)
        {
            int longest = lens.IndexOf(lens.Max());
            n[longest] = Mathf.Max(0, n[longest] + diff);
        }
        return n;
    }

    public static Resolved Resolve(StairEdit s, List<MRUKRoom> rooms)
    {
        var pts = WorldPoints(s, rooms);
        if (pts.Count < 2) return null;
        var r = new Resolved { points = pts, width = Mathf.Max(0.5f, s.width), bottomY = pts[0].y };
        r.topY = TopFloorY(rooms, r.bottomY);
        var risers = s.risers != null && s.risers.Count == pts.Count - 1 ? s.risers : DefaultRisers(pts, r.width, r.topY - r.bottomY);
        int total = Mathf.Max(1, risers.Sum());
        r.rise = (r.topY - r.bottomY) / total;

        float w = r.width, y = r.bottomY;
        int k = pts.Count - 2;
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            Vector3 dir = Flat(pts[i + 1] - pts[i]).normalized;
            Vector3 start = pts[i] + (i > 0 ? dir * (w / 2f) : Vector3.zero);
            Vector3 end = pts[i + 1] - (i < k ? dir * (w / 2f) : Vector3.zero);
            if (Vector3.Dot(end - start, dir) < 0f) end = start;
            r.flights.Add((start, end, dir, risers[i], y));
            AddRect(r.footprint, start, end, dir, w);
            y += risers[i] * r.rise;
            if (i < k)
            {
                r.landings.Add((pts[i + 1], dir, y));
                Vector3 c = pts[i + 1];
                AddRect(r.footprint, c - dir * (w / 2f), c + dir * (w / 2f), dir, w);
            }
        }
        return r;
    }

    static void AddRect(List<Vector3[]> list, Vector3 a, Vector3 b, Vector3 dir, float w)
    {
        Vector3 side = Vector3.Cross(Vector3.up, dir) * (w / 2f + 0.02f);
        Vector3 ext = dir * 0.02f;
        if ((b - a).sqrMagnitude < 1e-6f) return;
        list.Add(new[] { a - ext - side, b + ext - side, b + ext + side, a - ext + side });
    }

    static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

    /// <summary>Box parts (model space: gRot * (world - center)) for every step and landing.</summary>
    public static List<XRMeshPart> Parts(Resolved r, float yaw, Vector3 center)
    {
        var parts = new List<XRMeshPart>();
        const float stepBody = 0.15f, landingThickness = 0.2f;
        foreach (var (start, end, dir, risers, y0) in r.flights)
        {
            float len = Vector3.Dot(end - start, dir);
            if (len < 0.01f) continue;
            Quaternion rot = Quaternion.LookRotation(dir, Vector3.up);
            if (risers <= 0)
            {
                // A flight with no steps is just a flat continuation (e.g. between two landings).
                Vector3 mid = (start + end) / 2f;
                parts.Add(Box("STAIR_LANDING", new Vector3(mid.x, y0 - landingThickness / 2f, mid.z), rot, new Vector3(r.width, landingThickness, len), yaw, center));
                continue;
            }
            float tread = len / risers;
            for (int s = 0; s < risers; s++)
            {
                float top = y0 + (s + 1) * r.rise;
                float h = r.rise + stepBody;
                Vector3 c = start + dir * (tread * (s + 0.5f));
                parts.Add(Box("STAIR_STEP", new Vector3(c.x, top - h / 2f, c.z), rot, new Vector3(r.width, h, tread), yaw, center));
            }
        }
        foreach (var (c, dir, y) in r.landings)
            parts.Add(Box("STAIR_LANDING", new Vector3(c.x, y - landingThickness / 2f, c.z), Quaternion.LookRotation(dir, Vector3.up),
                new Vector3(r.width, landingThickness, r.width), yaw, center));
        return parts;
    }

    static XRMeshPart Box(string name, Vector3 pos, Quaternion rot, Vector3 size, float yaw, Vector3 center)
    {
        var p = XRModelFactory.CreateBoxPart(name, pos, rot, size, "STAIRS", center, yaw);
        p.color = StairColor;
        return p;
    }

    // ---------- cutting the stairwell opening ----------

    /// <summary>
    /// Returns a copy of 'mesh' (model space) with every near-horizontal triangle whose height lies in (minY, maxY)
    /// minus the footprint rectangles (model space XZ). Exact polygon difference per triangle - the remaining
    /// pieces are convex and fan-triangulated, keeping the original winding. Null if nothing was cut.
    /// </summary>
    public static Mesh CutHoles(Mesh mesh, List<Vector2[]> rects, float minY, float maxY)
    {
        var v = mesh.vertices;
        var t = mesh.triangles;
        var col = mesh.colors; // shaded (walk-through) meshes carry their colour per vertex - keep it
        bool hasCol = col.Length == v.Length;
        var outV = new List<Vector3>();
        var outT = new List<int>();
        var outC = new List<Color>();
        bool changed = false;
        for (int i = 0; i + 2 < t.Length; i += 3)
        {
            Vector3 a = v[t[i]], b = v[t[i + 1]], c = v[t[i + 2]];
            Vector3 n = Vector3.Cross(b - a, c - a);
            float cy = (a.y + b.y + c.y) / 3f;
            bool candidate = n.sqrMagnitude > 1e-12f && Mathf.Abs(n.normalized.y) > 0.9f && cy > minY && cy < maxY
                             && rects.Any(r => Overlaps(r, a, b, c));
            if (!candidate)
            {
                Emit(outV, outT, a, b, c);
                if (hasCol) { outC.Add(col[t[i]]); outC.Add(col[t[i + 1]]); outC.Add(col[t[i + 2]]); }
                continue;
            }
            Color triCol = hasCol ? col[t[i]] : Color.white;

            changed = true;
            var pieces = new List<List<Vector2>> { new List<Vector2> { XZ(a), XZ(b), XZ(c) } };
            foreach (var r in rects)
            {
                var next = new List<List<Vector2>>();
                foreach (var p in pieces) next.AddRange(Subtract(p, r));
                pieces = next;
            }
            foreach (var p in pieces)
                for (int k = 1; k + 1 < p.Count; k++)
                {
                    Emit(outV, outT, Lift(p[0], a, b, c), Lift(p[k], a, b, c), Lift(p[k + 1], a, b, c));
                    if (hasCol) { outC.Add(triCol); outC.Add(triCol); outC.Add(triCol); }
                }
        }
        if (!changed) return null;
        var m = new Mesh { name = mesh.name + "_cut", indexFormat = outV.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
        m.SetVertices(outV);
        if (outC.Count == outV.Count) m.SetColors(outC);
        m.SetTriangles(outT, 0);
        m.RecalculateNormals();
        m.RecalculateBounds();
        return m;
    }

    static void Emit(List<Vector3> v, List<int> t, Vector3 a, Vector3 b, Vector3 c)
    {
        int i = v.Count;
        v.Add(a); v.Add(b); v.Add(c);
        t.Add(i); t.Add(i + 1); t.Add(i + 2);
    }

    static Vector2 XZ(Vector3 p) => new Vector2(p.x, p.z);

    /// <summary>Back to 3D on the original triangle's plane (keeps sloped ceilings sloped).</summary>
    static Vector3 Lift(Vector2 p, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector2 A = XZ(a), B = XZ(b), C = XZ(c);
        float d = (B.y - C.y) * (A.x - C.x) + (C.x - B.x) * (A.y - C.y);
        if (Mathf.Abs(d) < 1e-9f) return new Vector3(p.x, a.y, p.y);
        float l1 = ((B.y - C.y) * (p.x - C.x) + (C.x - B.x) * (p.y - C.y)) / d;
        float l2 = ((C.y - A.y) * (p.x - C.x) + (A.x - C.x) * (p.y - C.y)) / d;
        float l3 = 1f - l1 - l2;
        return new Vector3(p.x, l1 * a.y + l2 * b.y + l3 * c.y, p.y);
    }

    static bool Overlaps(Vector2[] r, Vector3 a, Vector3 b, Vector3 c)
    {
        float minX = Mathf.Min(a.x, Mathf.Min(b.x, c.x)), maxX = Mathf.Max(a.x, Mathf.Max(b.x, c.x));
        float minZ = Mathf.Min(a.z, Mathf.Min(b.z, c.z)), maxZ = Mathf.Max(a.z, Mathf.Max(b.z, c.z));
        return r.Max(p => p.x) > minX && r.Min(p => p.x) < maxX && r.Max(p => p.y) > minZ && r.Min(p => p.y) < maxZ;
    }

    /// <summary>Convex polygon minus convex polygon, as convex pieces.</summary>
    static List<List<Vector2>> Subtract(List<Vector2> poly, Vector2[] cut)
    {
        var result = new List<List<Vector2>>();
        // Orientation of the cutter, so "inside" of each edge is known.
        float area = 0f;
        for (int i = 0; i < cut.Length; i++) { var p = cut[i]; var q = cut[(i + 1) % cut.Length]; area += p.x * q.y - q.x * p.y; }
        float sign = area >= 0 ? 1f : -1f;
        var remaining = poly;
        for (int i = 0; i < cut.Length && remaining.Count >= 3; i++)
        {
            Vector2 p = cut[i], q = cut[(i + 1) % cut.Length];
            var outside = Clip(remaining, p, q, -sign);
            if (outside.Count >= 3 && Area(outside) > 1e-6f) result.Add(outside);
            remaining = Clip(remaining, p, q, sign);
        }
        return result; // whatever is left in 'remaining' is inside the cutter - dropped
    }

    /// <summary>Sutherland-Hodgman against the line p->q, keeping the side where side * cross >= 0.</summary>
    static List<Vector2> Clip(List<Vector2> poly, Vector2 p, Vector2 q, float side)
    {
        var res = new List<Vector2>();
        float Side(Vector2 x) => side * ((q.x - p.x) * (x.y - p.y) - (q.y - p.y) * (x.x - p.x));
        for (int i = 0; i < poly.Count; i++)
        {
            Vector2 a = poly[i], b = poly[(i + 1) % poly.Count];
            float sa = Side(a), sb = Side(b);
            if (sa >= 0) res.Add(a);
            if ((sa >= 0) != (sb >= 0))
            {
                float t = sa / (sa - sb);
                res.Add(a + (b - a) * t);
            }
        }
        return res;
    }

    static float Area(List<Vector2> p)
    {
        float a = 0f;
        for (int i = 0; i < p.Count; i++) { var x = p[i]; var y = p[(i + 1) % p.Count]; a += x.x * y.y - y.x * x.y; }
        return Mathf.Abs(a) / 2f;
    }
}

/// <summary>
/// The stairs of the current edits inside one built model (walk-through or Dollhouse): adds the step/landing
/// geometry under the model and cuts the stairwell opening into the floor slabs/ceilings above the bottom story.
/// Re-applied whenever the edits change; the uncut meshes are kept so a re-cut always starts from the original.
/// </summary>
public class StairsInModel
{
    readonly Dictionary<MeshFilter, Mesh> originals = new Dictionary<MeshFilter, Mesh>();
    GameObject stairsGo;

    public void Apply(GameObject visual, float yaw, Vector3 center, List<MRUKRoom> rooms, HouseEdits edits, bool colliders, int layer, bool shaded = false)
    {
        if (!visual) return;
        // Undo the previous application.
        foreach (var kv in originals)
        {
            if (!kv.Key) continue;
            var cut = kv.Key.sharedMesh;
            kv.Key.sharedMesh = kv.Value;
            var mc = kv.Key.GetComponent<MeshCollider>();
            if (mc) mc.sharedMesh = kv.Value;
            if (cut && cut != kv.Value) Object.Destroy(cut);
        }
        originals.Clear();
        DestroyStairs();
        if (edits == null || edits.stairs == null || edits.stairs.Count == 0) return;

        Quaternion gRot = Quaternion.Euler(0, yaw, 0);
        var model = new XRHouseModel();
        var group = new XRRoomModel { roomName = "Stairs" };
        model.rooms.Add(group);
        var holes = new List<(List<Vector2[]> rects, float minY, float maxY)>();
        foreach (var s in edits.stairs)
        {
            var r = StairGeometry.Resolve(s, rooms);
            if (r == null) continue;
            group.parts.AddRange(StairGeometry.Parts(r, yaw, center));
            var rects = r.footprint.Select(q => q.Select(p => { var m = gRot * (p - center); return new Vector2(m.x, m.z); }).ToArray()).ToList();
            // Cut everything horizontal between ~1 m above the bottom floor and just above the top floor: the
            // ceiling of the story below and the floor slab of the story above.
            holes.Add((rects, r.bottomY - center.y + 1.0f, r.topY - center.y + 0.3f));
        }

        stairsGo = UnityModelLoader.LoadToScene(model, shaded);
        if (stairsGo)
        {
            stairsGo.name = "Stairs";
            stairsGo.transform.SetParent(visual.transform, false);
            foreach (var mf in stairsGo.GetComponentsInChildren<MeshFilter>(true))
            {
                mf.gameObject.layer = colliders ? layer : visual.layer;
                if (colliders) mf.gameObject.AddComponent<MeshCollider>().sharedMesh = mf.sharedMesh;
            }
        }

        // includeInactive: the Dollhouse is inactive (hidden) while walking but still gets re-applied.
        foreach (var mf in visual.GetComponentsInChildren<MeshFilter>(true))
        {
            if (!mf.sharedMesh || (stairsGo && mf.transform.IsChildOf(stairsGo.transform))) continue;
            if (!(mf.name.StartsWith("FLOOR") || mf.name.StartsWith("CEILING") || mf.name.StartsWith("Ceiling"))) continue;
            Mesh current = mf.sharedMesh, result = null;
            foreach (var (rects, minY, maxY) in holes)
            {
                var cut = StairGeometry.CutHoles(result ?? current, rects, minY, maxY);
                if (cut == null) continue;
                if (result) Object.Destroy(result);
                result = cut;
            }
            if (result == null) continue;
            originals[mf] = current;
            mf.sharedMesh = result;
            var mc = mf.GetComponent<MeshCollider>();
            if (mc) mc.sharedMesh = result;
        }
    }

    void DestroyStairs()
    {
        if (!stairsGo) return;
        foreach (var mf in stairsGo.GetComponentsInChildren<MeshFilter>(true)) if (mf.sharedMesh) Object.Destroy(mf.sharedMesh);
        foreach (var mr in stairsGo.GetComponentsInChildren<Renderer>(true)) if (mr.sharedMaterial) Object.Destroy(mr.sharedMaterial);
        Object.Destroy(stairsGo);
        stairsGo = null;
    }

    /// <summary>Before the model itself is destroyed: frees the kept original meshes the model no longer uses
    /// (the model's own cleanup destroys whatever its MeshFilters currently hold).</summary>
    public void Dispose()
    {
        foreach (var kv in originals)
            if (kv.Value && (!kv.Key || kv.Key.sharedMesh != kv.Value)) Object.Destroy(kv.Value);
        originals.Clear();
        stairsGo = null;
    }
}
