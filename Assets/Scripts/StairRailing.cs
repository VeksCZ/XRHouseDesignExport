using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

/// <summary>
/// Wooden railings for the walk-through: vertical slats (4 x 4 cm, 11 cm apart) under a handrail along every open
/// side of the stairs' flights and landings, and round the floor openings in the storey above. "Open" is found by
/// looking for a wall right where the railing would stand (sampled every slat), so a side running along a wall gets
/// none, a gap between two openings that are really one stairwell gets none, and neither does the edge where the
/// top flight arrives. Built in model space under the walk model, with colliders (you can't walk off the edge).
/// </summary>
public class StairRailing
{
    GameObject go;
    const float Spacing = 0.11f, Slat = 0.04f, Rail = 0.06f, StairHeight = 0.90f, EdgeHeight = 1.00f;
    static readonly Color Wood = new Color(0.62f, 0.47f, 0.32f);

    public void Clear()
    {
        if (!go) return;
        foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true)) if (mf.sharedMesh) UnityEngine.Object.Destroy(mf.sharedMesh);
        foreach (var mr in go.GetComponentsInChildren<Renderer>(true)) if (mr.sharedMaterial) UnityEngine.Object.Destroy(mr.sharedMaterial);
        go.transform.SetParent(null, false);
        UnityEngine.Object.Destroy(go);
        go = null;
    }

    Transform model;
    int mask;
    readonly List<XRMeshPart> parts = new List<XRMeshPart>();
    readonly List<(Vector3 p, float top)> stairSlats = new List<(Vector3, float)>();
    bool recordSlats;

    public void Build(Transform modelRoot, float yaw, Vector3 center, List<MRUKRoom> rooms, HouseEdits edits, int layer, int wallMask)
    {
        Clear();
        if (!modelRoot || edits == null || rooms == null) return;
        model = modelRoot; mask = wallMask; parts.Clear(); stairSlats.Clear(); recordSlats = true;
        Quaternion g = Quaternion.Euler(0, yaw, 0);
        Vector3 M(Vector3 p) => g * (p - center);   // scan world -> model space
        Vector3 Md(Vector3 d) => g * d;

        // the floor openings (model space rectangles) and their floor height
        var holes = new List<(Rect r, float y)>();
        foreach (var h in edits.holes)
        {
            if (!StairGeometry.HoleWorld(h, rooms, out var a, out var b)) continue;
            var room = rooms.FirstOrDefault(r => DoorCatalog.RoomId(r) == h.roomUuid);
            float fy = room != null && room.FloorAnchors.Count > 0 ? room.FloorAnchors.Where(f => f != null).Min(f => f.transform.position.y) : a.y;
            Vector3 ma = M(a), mb = M(b);
            holes.Add((Rect.MinMaxRect(Mathf.Min(ma.x, mb.x), Mathf.Min(ma.z, mb.z), Mathf.Max(ma.x, mb.x), Mathf.Max(ma.z, mb.z)), fy - center.y));
        }

        var arrivals = new List<(Vector3 p, Vector3 right, float w)>();
        foreach (var s in edits.stairs)
        {
            var r = StairGeometry.Resolve(s, rooms);
            if (r == null) continue;
            float w = r.width, rise = r.rise, tread = r.tread;
            for (int i = 0; i < r.flights.Count; i++)
            {
                var (s0, s1, dir, n, y) = r.flights[i];
                Vector3 a = M(s0), b = M(s1), d = Md(dir).normalized, right = Vector3.Cross(Vector3.up, d);
                float y0 = y - center.y;
                // nosing line: the front edge of each tread; slats stand on the tread they're over
                Func<float, float> nose = t => y0 + rise + t / tread * rise;
                Func<float, float> treadTop = t => y0 + Mathf.Min(n, Mathf.Floor(t / tread) + 1) * rise;
                // the top flight's railing runs level with the one round the opening upstairs
                bool lastFlight = i == r.flights.Count - 1;
                float levelTop = r.topY - center.y + EdgeHeight;
                foreach (int side in new[] { -1, 1 })
                {
                    Vector3 off = right * side * (w / 2f - 0.03f);
                    RailRun(a + off, b + off, right * side, t => treadTop(t), t => lastFlight ? levelTop : nose(t) + StairHeight, null);
                }
                if (lastFlight) arrivals.Add((b, right, w));
            }
            for (int i = 0; i < r.landings.Count; i++)
            {
                var (c, dir, y) = r.landings[i];
                Vector3 mc = M(c), din = Md(dir).normalized;
                Vector3 dout = i + 1 < r.flights.Count ? Md(r.flights[i + 1].dir).normalized : din;
                float ly = y - center.y;
                foreach (var nrm in new[] { din, -din, Vector3.Cross(Vector3.up, din), -Vector3.Cross(Vector3.up, din) })
                {
                    if (Vector3.Dot(nrm, -din) > 0.9f || Vector3.Dot(nrm, dout) > 0.9f) continue;   // where the flights attach
                    Vector3 along = Vector3.Cross(Vector3.up, nrm);
                    Vector3 mid = mc + nrm * (w / 2f - 0.03f);
                    RailRun(mid - along * (w / 2f - 0.03f), mid + along * (w / 2f - 0.03f), nrm, t => ly, t => ly + StairHeight, null);
                }
            }
            foreach (var (fs, fe, dir, y) in r.flats)
            {
                Vector3 d = Md(dir).normalized, right = Vector3.Cross(Vector3.up, d);
                float fy = y - center.y;
                foreach (int side in new[] { -1, 1 })
                {
                    Vector3 off = right * side * (w / 2f - 0.03f);
                    RailRun(M(fs) + off, M(fe) + off, right * side, t => fy, t => fy + StairHeight, null);
                }
            }
        }

        recordSlats = false;
        // round the openings: every edge, except where it borders another opening or the top flight arrives
        foreach (var (rect, fy) in holes)
        {
            var edges = new[]
            {
                (new Vector3(rect.xMin, 0, rect.yMin), new Vector3(rect.xMax, 0, rect.yMin), Vector3.back),
                (new Vector3(rect.xMax, 0, rect.yMin), new Vector3(rect.xMax, 0, rect.yMax), Vector3.right),
                (new Vector3(rect.xMax, 0, rect.yMax), new Vector3(rect.xMin, 0, rect.yMax), Vector3.forward),
                (new Vector3(rect.xMin, 0, rect.yMax), new Vector3(rect.xMin, 0, rect.yMin), Vector3.left),
            };
            foreach (var (a, b, outN) in edges)
            {
                Vector3 inset = -outN * 0.03f;   // just inside the opening, on its rim
                Func<Vector3, bool> skip = p =>
                {
                    Vector3 o = p - inset + outN * 0.08f;   // a little beyond the rim: still an opening?
                    if (holes.Any(h => h.r.Contains(new Vector2(o.x, o.z)) && Mathf.Abs(h.y - fy) < 0.5f)) return true;
                    foreach (var (e, right, w) in arrivals)
                    {
                        Vector3 rel = p - e; rel.y = 0;
                        if (Mathf.Abs(Vector3.Dot(rel, right)) < w / 2f + 0.05f && Mathf.Abs(Vector3.Dot(rel, Vector3.Cross(right, Vector3.up))) < 0.25f) return true;
                    }
                    // a stair's own railing already runs right here at about this height
                    float railTop = fy + EdgeHeight;
                    return stairSlats.Any(q => (new Vector2(q.p.x - p.x, q.p.z - p.z)).sqrMagnitude < 0.15f * 0.15f && Mathf.Abs(q.top - railTop) < 0.6f);
                };
                RailRun(a + inset, b + inset, outN, t => fy, t => fy + EdgeHeight, skip, bottomRail: true);
            }
        }

        if (parts.Count == 0) return;
        var house = new XRHouseModel();
        var rm = new XRRoomModel { roomName = "Railings" };
        rm.parts.AddRange(parts);
        house.rooms.Add(rm);
        go = UnityModelLoader.LoadToScene(house, shaded: true);
        if (!go) return;
        go.name = "Railings";
        go.transform.SetParent(model, false);
        foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
        {
            mf.gameObject.layer = layer;
            if (mf.sharedMesh) mf.gameObject.AddComponent<MeshCollider>().sharedMesh = mf.sharedMesh;
        }
        Debug.Log($"[Railing] {parts.Count} parts");
    }

    /// <summary>Slats every Spacing along a-b (model space, y ignored), from foot(t) up to top(t) (t = distance from
    /// a), with a handrail along the tops of each unbroken stretch. A slat is left out where a wall stands there
    /// already or skip says so; the handrail breaks there too.</summary>
    void RailRun(Vector3 a, Vector3 b, Vector3 outward, Func<float, float> foot, Func<float, float> top, Func<Vector3, bool> skip, bool bottomRail = false)
    {
        a.y = b.y = 0f;
        float len = (b - a).magnitude;
        if (len < 0.05f) return;
        Vector3 d = (b - a) / len;
        int n = Mathf.Max(1, Mathf.RoundToInt(len / Spacing));
        var run = new List<(float t, Vector3 p)>();
        void Flush()
        {
            if (run.Count >= 2)
            {
                var (t0, p0) = run[0]; var (t1, p1) = run[run.Count - 1];
                AddBeam(new Vector3(p0.x, top(t0), p0.z), new Vector3(p1.x, top(t1), p1.z), Rail, 0.045f);
                if (bottomRail) AddBeam(new Vector3(p0.x, foot(t0) + 0.08f, p0.z), new Vector3(p1.x, foot(t1) + 0.08f, p1.z), Slat, 0.03f);
            }
            run.Clear();
        }
        for (int k = 0; k <= n; k++)
        {
            float t = len * k / n;
            Vector3 p = a + d * t;
            bool blocked = (skip != null && skip(p)) || WallAt(p, foot(t), outward);
            if (blocked) { Flush(); continue; }
            run.Add((t, p));
            float f = foot(t), tp = top(t);
            if (recordSlats) stairSlats.Add((p, tp));
            AddBox(new Vector3(p.x, (f + tp) / 2f, p.z), Quaternion.LookRotation(d, Vector3.up), new Vector3(Slat, tp - f, Slat));
        }
        Flush();
    }

    /// <summary>A wall already stands where a slat would (checked at knee and chest height).</summary>
    bool WallAt(Vector3 modelP, float footY, Vector3 outward)
    {
        static bool IsWall(Collider c) { string nm = c.name; return nm == "WALL" || nm == "WallSeg" || nm == "WallCap" || nm.EndsWith("_wall") || nm.Contains("attic"); }
        Vector3 outW = model.TransformDirection(outward).normalized;
        foreach (float h in new[] { 0.3f, 0.6f, 0.9f })
        {
            Vector3 w = model.TransformPoint(new Vector3(modelP.x, footY + h, modelP.z));
            if (Physics.OverlapSphere(w, 0.06f, mask, QueryTriggerInteraction.Ignore).Any(IsWall)) return true;
            // a wall face just beyond the edge (the stair or opening runs along it)
            Vector3 from = w - outW * 0.10f;
            foreach (var hit in Physics.RaycastAll(from, outW, 0.32f, mask, QueryTriggerInteraction.Ignore))
                if (IsWall(hit.collider)) return true;
        }
        return false;
    }

    void AddBeam(Vector3 a, Vector3 b, float w, float h)
    {
        Vector3 d = b - a;
        if (d.sqrMagnitude < 1e-6f) return;
        AddBox((a + b) / 2f, Quaternion.LookRotation(d.normalized, Vector3.up), new Vector3(w, h, d.magnitude + w));
    }

    void AddBox(Vector3 c, Quaternion r, Vector3 size)
    {
        var p = XRModelFactory.CreateSolidBoxPart("RAIL", c, r, size, "FURNITURE", Vector3.zero, 0f);
        p.color = Wood;
        parts.Add(p);
    }
}
