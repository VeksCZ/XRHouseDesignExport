using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

/// <summary>
/// Pure geometry for the floor plans: aligns a scan to its walls, groups rooms into stories and lays
/// out plan sheets (walls, openings, dimension chains). Knows nothing about MRUK or any renderer, so
/// the HTML report and the in-headset viewer draw exactly the same sheets.
/// </summary>
public static class FloorPlanBuilder
{
    const float StoryGap = 1.5f;          // minimum vertical gap between rooms to count as different stories
    const float OpeningSnapDist = 0.5f;   // how far an opening may sit from a wall edge and still belong to it
    const float InnerChainOffset = 0.45f; // room sheets: distance of the segment chain from the wall (inside)
    const float InnerTotalOffset = 0.85f; // room sheets: distance of the overall-length dimension (inside)
    const float OuterOffset = 0.6f;       // overview sheets: exterior wall dimensions (outside)
    const float BoundsOffset = 1.3f;      // overview sheets: overall building dimensions (outside)
    const float DimTextSize = 0.11f;

    struct Edge
    {
        public Vector2 a, b, dir, outward;
        public float len;
    }

    public static float SignedArea(IList<Vector2> p)
    {
        float s = 0;
        for (int i = 0; i < p.Count; i++)
        {
            Vector2 a = p[i], b = p[(i + 1) % p.Count];
            s += a.x * b.y - b.x * a.y;
        }
        return s * 0.5f;
    }

    /// <summary>
    /// Yaw (degrees, around Y) that turns the scan so its dominant wall direction is axis-aligned.
    /// Wall angles are folded modulo 90 and averaged, weighted by wall length (the 4x-angle circular
    /// mean), so it is independent of how MRUK happened to orient any single anchor.
    /// </summary>
    public static float CorrectionYaw(IEnumerable<RoomOutline> rooms)
    {
        double sx = 0, sy = 0;
        foreach (var r in rooms)
        {
            var p = r.polygon;
            for (int i = 0; i < p.Count; i++)
            {
                Vector2 d = p[(i + 1) % p.Count] - p[i];
                float len = d.magnitude;
                if (len < 0.2f) continue;
                double phi = Math.Atan2(d.x, d.y); // yaw of the edge
                sx += len * Math.Cos(4 * phi);
                sy += len * Math.Sin(4 * phi);
            }
        }
        if (sx * sx + sy * sy < 1e-9) return 0f;
        double dominant = Math.Atan2(sy, sx) / 4.0;
        return (float)(-dominant * 180.0 / Math.PI);
    }

    /// <summary>
    /// True if two outlines are the same physical room scanned twice (MRUK keeps every Space Setup
    /// scan, so a re-scanned room shows up as two rooms lying on top of each other): same story,
    /// nearly the same centre and a similar size.
    /// </summary>
    public static bool IsSameRoom(RoomOutline a, RoomOutline b)
    {
        if (Mathf.Abs(a.floorY - b.floorY) > 0.5f) return false;
        if (Vector2.Distance(Centroid(a.polygon), Centroid(b.polygon)) > 0.75f) return false;
        float areaA = a.Area, areaB = b.Area;
        return Mathf.Min(areaA, areaB) / Mathf.Max(Mathf.Max(areaA, areaB), 0.0001f) > 0.6f;
    }

    /// <summary>Drops duplicate scans of the same room, keeping the most detailed (then the largest) one.</summary>
    public static List<RoomOutline> RemoveDuplicates(IEnumerable<RoomOutline> rooms)
    {
        var kept = new List<RoomOutline>();
        foreach (var r in rooms.OrderByDescending(r => r.detail).ThenByDescending(r => r.Area))
            if (!kept.Any(k => IsSameRoom(k, r))) kept.Add(r);
        return kept;
    }

    /// <summary>Rotates a world (x, z) point the same way Quaternion.Euler(0, yaw, 0) rotates a 3D point.</summary>
    public static Vector2 Rotate(Vector2 p, float yawDeg)
    {
        float r = yawDeg * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r);
        return new Vector2(p.x * c + p.y * s, -p.x * s + p.y * c);
    }

    public static List<RoomOutline> Aligned(IEnumerable<RoomOutline> rooms, float yawDeg)
    {
        var result = new List<RoomOutline>();
        foreach (var r in rooms)
        {
            var n = new RoomOutline
            {
                id = r.id, roomKey = r.roomKey, name = r.name, floorY = r.floorY, ceilingMin = r.ceilingMin, ceilingMax = r.ceilingMax, detail = r.detail,
                polygon = r.polygon.Select(p => Rotate(p, yawDeg)).ToList(),
            };
            foreach (var o in r.openings)
                n.openings.Add(new PlanOpening { kind = o.kind, width = o.width, sill = o.sill, height = o.height, center = Rotate(o.center, yawDeg) });
            result.Add(n);
        }
        return result;
    }

    public static List<List<RoomOutline>> GroupLevels(IEnumerable<RoomOutline> rooms)
    {
        var levels = new List<List<RoomOutline>>();
        List<RoomOutline> current = null;
        float lastY = float.NaN;
        foreach (var r in rooms.OrderBy(r => r.floorY))
        {
            if (current == null || r.floorY - lastY > StoryGap)
            {
                current = new List<RoomOutline>();
                levels.Add(current);
            }
            current.Add(r);
            lastY = r.floorY;
        }
        return levels;
    }

    public static List<LevelPlan> BuildLevels(IEnumerable<RoomOutline> alignedRooms)
    {
        var plans = new List<LevelPlan>();
        var groups = GroupLevels(alignedRooms);
        for (int i = 0; i < groups.Count; i++)
        {
            var lp = new LevelPlan { index = i, overview = BuildOverview(groups[i], i) };
            foreach (var r in groups[i]) lp.rooms.Add(new RoomPlan { room = r, page = BuildRoom(r) });
            plans.Add(lp);
        }
        return plans;
    }

    /// <summary>Overview -> its rooms, for every level: the page order used by the viewer and the report.</summary>
    public static List<FloorPlanPage> Flatten(IEnumerable<LevelPlan> levels)
    {
        var pages = new List<FloorPlanPage>();
        foreach (var l in levels)
        {
            pages.Add(l.overview);
            pages.AddRange(l.rooms.Select(r => r.page));
        }
        return pages;
    }

    public static string Fmt(float meters) => meters.ToString("0.00", CultureInfo.InvariantCulture);
    public static string Fmt1(float value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    public static float Perimeter(RoomOutline r)
    {
        float s = 0;
        for (int i = 0; i < r.polygon.Count; i++) s += Vector2.Distance(r.polygon[i], r.polygon[(i + 1) % r.polygon.Count]);
        return s;
    }

    public static string CeilingText(RoomOutline r)
    {
        return r.ceilingMax - r.ceilingMin > 0.05f
            ? $"{Fmt(r.ceilingMin)}-{Fmt(r.ceilingMax)} m (sloped)"
            : $"{Fmt(r.ceilingMax)} m";
    }

    /// <summary>Wall lengths (inside faces) with the openings that sit on each wall, for tables.</summary>
    public static List<(float length, List<PlanOpening> openings)> Walls(RoomOutline room)
    {
        var edges = Edges(room.polygon);
        var perEdge = AssignOpenings(room, edges);
        var walls = new List<(float, List<PlanOpening>)>();
        for (int i = 0; i < edges.Count; i++)
            walls.Add((edges[i].len, perEdge[i].Select(x => x.o).ToList()));
        return walls;
    }

    static List<Edge> Edges(List<Vector2> poly)
    {
        var edges = new List<Edge>();
        float area = SignedArea(poly);
        for (int i = 0; i < poly.Count; i++)
        {
            Vector2 a = poly[i], b = poly[(i + 1) % poly.Count];
            float len = Vector2.Distance(a, b);
            if (len < 0.02f) continue;
            Vector2 dir = (b - a) / len;
            // For a counter-clockwise polygon the interior is on the left, so outward is the right-hand normal.
            Vector2 outward = area >= 0 ? new Vector2(dir.y, -dir.x) : new Vector2(-dir.y, dir.x);
            edges.Add(new Edge { a = a, b = b, dir = dir, outward = outward, len = len });
        }
        return edges;
    }

    /// <summary>Snaps each opening to its nearest wall edge; returns per-edge [t0,t1] spans along that edge, sorted.</summary>
    static List<List<(float t0, float t1, PlanOpening o)>> AssignOpenings(RoomOutline room, List<Edge> edges)
    {
        var perEdge = edges.Select(_ => new List<(float, float, PlanOpening)>()).ToList();
        foreach (var o in room.openings)
        {
            int best = -1; float bestDist = OpeningSnapDist, bestT = 0;
            for (int i = 0; i < edges.Count; i++)
            {
                Vector2 v = o.center - edges[i].a;
                float t = Vector2.Dot(v, edges[i].dir);
                if (t < -0.3f || t > edges[i].len + 0.3f) continue;
                float dist = Mathf.Abs(edges[i].dir.x * v.y - edges[i].dir.y * v.x);
                if (dist < bestDist) { bestDist = dist; best = i; bestT = t; }
            }
            if (best < 0) continue;
            float t0 = Mathf.Clamp(bestT - o.width / 2f, 0, edges[best].len);
            float t1 = Mathf.Clamp(bestT + o.width / 2f, 0, edges[best].len);
            if (t1 - t0 > 0.05f) perEdge[best].Add((t0, t1, o));
        }
        foreach (var l in perEdge) l.Sort((x, y) => x.Item1.CompareTo(y.Item1));
        return perEdge;
    }

    static Vector2 Centroid(List<Vector2> p)
    {
        float a = SignedArea(p);
        if (Mathf.Abs(a) < 1e-4f) return p.Count == 0 ? Vector2.zero : p.Aggregate(Vector2.zero, (s, v) => s + v) / p.Count;
        float cx = 0, cy = 0;
        for (int i = 0; i < p.Count; i++)
        {
            Vector2 u = p[i], v = p[(i + 1) % p.Count];
            float cr = u.x * v.y - v.x * u.y;
            cx += (u.x + v.x) * cr; cy += (u.y + v.y) * cr;
        }
        return new Vector2(cx, cy) / (6f * a);
    }

    static float TextAngle(Vector2 dir)
    {
        float ang = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
        if (ang > 90f) ang -= 180f; else if (ang <= -90f) ang += 180f; // keep text readable, never upside down
        return ang;
    }

    /// <summary>A dimension line from a to b, shifted by 'offset' with extension lines, ticks and a value label alongside.</summary>
    /// <summary>Text grows with the plan so labels stay legible when a whole house is fitted onto one sheet.</summary>
    static float TextScale(IEnumerable<Vector2> points, float divisor)
    {
        var p = points.ToList();
        if (p.Count == 0) return 1f;
        float extent = Mathf.Max(p.Max(v => v.x) - p.Min(v => v.x), p.Max(v => v.y) - p.Min(v => v.y));
        return Mathf.Max(1f, extent / divisor);
    }

    static void AddDimension(FloorPlanPage page, Vector2 a, Vector2 b, Vector2 offset, string text, float textSize = DimTextSize)
    {
        Vector2 A = a + offset, B = b + offset;
        Vector2 dir = (b - a).normalized, on = offset.normalized;
        page.lines.Add(new PlanLine(A, B, PlanStyle.Dimension));
        page.lines.Add(new PlanLine(a + on * 0.04f, A + on * 0.05f, PlanStyle.Extension));
        page.lines.Add(new PlanLine(b + on * 0.04f, B + on * 0.05f, PlanStyle.Extension));
        Vector2 tk = (dir + on).normalized * 0.05f;
        page.lines.Add(new PlanLine(A - tk, A + tk, PlanStyle.Tick));
        page.lines.Add(new PlanLine(B - tk, B + tk, PlanStyle.Tick));
        if ((b - a).magnitude >= 0.2f)
            page.texts.Add(new PlanText { pos = (A + B) / 2f + on * (0.06f + textSize * 0.4f), text = text, angleDeg = TextAngle(dir), size = textSize, style = PlanStyle.Dimension });
    }

    static void AddWallsAndOpenings(FloorPlanPage page, List<Edge> edges, List<List<(float t0, float t1, PlanOpening o)>> perEdge)
    {
        foreach (var e in edges) page.lines.Add(new PlanLine(e.a, e.b, PlanStyle.Wall));
        for (int i = 0; i < edges.Count; i++)
            foreach (var (t0, t1, o) in perEdge[i])
                page.lines.Add(new PlanLine(edges[i].a + edges[i].dir * t0, edges[i].a + edges[i].dir * t1,
                    o.kind == OpeningKind.Door ? PlanStyle.Door : PlanStyle.Window));
    }

    /// <summary>One room: walls and openings, with dimensions measured on the inside - each wall's
    /// overall length plus a chain of corner-to-opening distances and opening widths.</summary>
    public static FloorPlanPage BuildRoom(RoomOutline room)
    {
        var page = new FloorPlanPage
        {
            title = room.name,
            roomKey = room.roomKey,
            subtitle = $"Area {Fmt1(room.Area)} m2 | Perimeter {Fmt(Perimeter(room))} m | Ceiling {CeilingText(room)} | dimensions in metres, taken inside",
        };
        float ts = DimTextSize * TextScale(room.polygon, 6f);
        var edges = Edges(room.polygon);
        var perEdge = AssignOpenings(room, edges);
        AddWallsAndOpenings(page, edges, perEdge);

        for (int i = 0; i < edges.Count; i++)
        {
            var e = edges[i];
            Vector2 inward = -e.outward;
            var spans = perEdge[i];

            if (spans.Count > 0)
            {
                float cursor = 0;
                foreach (var (t0Raw, t1, _) in spans)
                {
                    float t0 = Mathf.Max(t0Raw, cursor);
                    if (t0 - cursor > 0.02f) AddDimension(page, e.a + e.dir * cursor, e.a + e.dir * t0, inward * InnerChainOffset, Fmt(t0 - cursor), ts);
                    if (t1 - t0 > 0.02f) AddDimension(page, e.a + e.dir * t0, e.a + e.dir * t1, inward * InnerChainOffset, Fmt(t1 - t0), ts);
                    cursor = Mathf.Max(cursor, t1);
                }
                if (e.len - cursor > 0.02f) AddDimension(page, e.a + e.dir * cursor, e.b, inward * InnerChainOffset, Fmt(e.len - cursor), ts);
                AddDimension(page, e.a, e.b, inward * InnerTotalOffset, Fmt(e.len), ts);
            }
            else
            {
                AddDimension(page, e.a, e.b, inward * InnerChainOffset, Fmt(e.len), ts);
            }
        }
        return page;
    }

    /// <summary>A whole story: every room, room names/areas, exterior walls dimensioned from the
    /// outside and the overall building width and depth.</summary>
    public static FloorPlanPage BuildOverview(List<RoomOutline> level, int levelIndex)
    {
        var page = new FloorPlanPage
        {
            title = $"Floor {levelIndex + 1}",
            subtitle = $"{level.Count} room(s) | total area {Fmt1(level.Sum(r => r.Area))} m2 | exterior walls dimensioned from outside, in metres",
        };
        var edgeCache = level.ToDictionary(r => r, r => Edges(r.polygon));
        float ts = TextScale(level.SelectMany(r => r.polygon), 6f);
        float ls = TextScale(level.SelectMany(r => r.polygon), 10f); // room names/areas: smaller so they fit inside rooms

        foreach (var r in level)
        {
            var edges = edgeCache[r];
            AddWallsAndOpenings(page, edges, AssignOpenings(r, edges));
        }

        foreach (var r in level)
        {
            foreach (var e in edgeCache[r])
            {
                if (e.len < 0.3f || IsShared(e, r, level, edgeCache)) continue;
                AddDimension(page, e.a, e.b, e.outward * OuterOffset, Fmt(e.len), DimTextSize * ts);
            }
        }

        var all = level.SelectMany(r => r.polygon).ToList();
        if (all.Count > 0)
        {
            float minX = all.Min(p => p.x), maxX = all.Max(p => p.x), minY = all.Min(p => p.y), maxY = all.Max(p => p.y);
            if (maxX - minX > 0.5f) AddDimension(page, new Vector2(minX, minY), new Vector2(maxX, minY), new Vector2(0, -BoundsOffset), Fmt(maxX - minX), DimTextSize * ts * 1.3f);
            if (maxY - minY > 0.5f) AddDimension(page, new Vector2(minX, minY), new Vector2(minX, maxY), new Vector2(-BoundsOffset, 0), Fmt(maxY - minY), DimTextSize * ts * 1.3f);
        }

        foreach (var r in level)
        {
            Vector2 c = Centroid(r.polygon);
            page.texts.Add(new PlanText { pos = c, text = r.name, size = 0.2f * ls, style = PlanStyle.Label, bold = true });
            page.texts.Add(new PlanText { pos = c + new Vector2(0, -0.25f * ls), text = $"{Fmt1(r.Area)} m2", size = 0.15f * ls, style = PlanStyle.Label });
        }
        return page;
    }

    /// <summary>A wall edge is shared (interior) if another room has a parallel edge right behind it.</summary>
    static bool IsShared(Edge e, RoomOutline owner, List<RoomOutline> level, Dictionary<RoomOutline, List<Edge>> cache)
    {
        Vector2 m = (e.a + e.b) / 2f;
        foreach (var other in level)
        {
            if (ReferenceEquals(other, owner) || other.id == owner.id) continue;
            foreach (var f in cache[other])
            {
                if (Mathf.Abs(e.dir.x * f.dir.y - e.dir.y * f.dir.x) > 0.08f) continue; // not parallel
                Vector2 v = m - f.a;
                if (Mathf.Abs(f.dir.x * v.y - f.dir.y * v.x) > OuterOffset) continue;     // too far away
                float t = Vector2.Dot(v, f.dir);
                if (t < -0.3f || t > f.len + 0.3f) continue;                              // beside it, not behind it
                return true;
            }
        }
        return false;
    }
}
