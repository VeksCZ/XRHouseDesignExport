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
    const float DimTextSize = 0.077f; // ~30% smaller than before - dimension numbers were overlapping each other

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
                cornerCeilingHeights = new List<float>(r.cornerCeilingHeights), // same vertex order, unaffected by yaw
            };
            foreach (var o in r.openings)
                n.openings.Add(new PlanOpening { kind = o.kind, width = o.width, sill = o.sill, height = o.height, center = Rotate(o.center, yawDeg) });
            result.Add(n);
        }
        return result;
    }

    const float MaxPolygonJitterDeg = 15f; // matches XRModelFactory.SnapToPerpendicular's own tolerance

    /// <summary>
    /// A copy of an already-aligned room (see Aligned) with every polygon edge's direction snapped to the
    /// nearest world axis - the same "clean, right-angled" idea as XRModelFactory.SnapToPerpendicular for the 3D
    /// model, but for a closed 2D outline, and offered as an alternative "Adjusted" view rather than replacing
    /// the real one: a floor plan's job is to show the true measured shape, so this is opt-in, not the default.
    /// Edges are walked in order, each edge's own real length is kept and the next corner is placed by extending
    /// the previous one along the snapped direction, so the shape stays closed. An edge more than
    /// MaxPolygonJitterDeg off a right angle is left as measured - a real angled wall (a bay window, a cut
    /// corner), not scan noise.
    /// </summary>
    public static List<RoomOutline> Rectified(IEnumerable<RoomOutline> alignedRooms)
    {
        var result = new List<RoomOutline>();
        foreach (var r in alignedRooms)
        {
            var n = new RoomOutline
            {
                id = r.id, roomKey = r.roomKey, name = r.name, floorY = r.floorY, ceilingMin = r.ceilingMin, ceilingMax = r.ceilingMax, detail = r.detail,
                polygon = RectifiedPolygon(r.polygon),
                cornerCeilingHeights = new List<float>(r.cornerCeilingHeights), // RectifiedPolygon keeps the same vertex order/count
            };
            foreach (var o in r.openings)
                n.openings.Add(new PlanOpening { kind = o.kind, width = o.width, sill = o.sill, height = o.height, center = o.center });
            result.Add(n);
        }
        return result;
    }

    static List<Vector2> RectifiedPolygon(List<Vector2> polygon)
    {
        int n = polygon.Count;
        if (n < 3) return new List<Vector2>(polygon);
        var result = new List<Vector2>(n) { polygon[0] };
        for (int i = 0; i < n - 1; i++)
        {
            Vector2 edge = polygon[i + 1] - polygon[i];
            float len = edge.magnitude;
            if (len < 0.02f) { result.Add(result[result.Count - 1]); continue; }
            float angle = Mathf.Atan2(edge.y, edge.x) * Mathf.Rad2Deg;
            float snapped = Mathf.Round(angle / 90f) * 90f;
            float use = Mathf.Abs(Mathf.DeltaAngle(angle, snapped)) <= MaxPolygonJitterDeg ? snapped : angle;
            Vector2 dir = new Vector2(Mathf.Cos(use * Mathf.Deg2Rad), Mathf.Sin(use * Mathf.Deg2Rad));
            result.Add(result[result.Count - 1] + dir * len);
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
            var group = groups[i];
            var lp = new LevelPlan { index = i };
            if (group.Count == 1)
            {
                // A single-room floor has nothing for the overview to add over the room's own sheet - it would
                // just repeat the same drawing on the next page.
                lp.rooms.Add(new RoomPlan { room = group[0], page = BuildRoom(group[0]) });
            }
            else
            {
                lp.overview = BuildOverview(group, i);
                foreach (var r in group) lp.rooms.Add(new RoomPlan { room = r, page = BuildRoom(r) });
            }
            plans.Add(lp);
        }
        return plans;
    }

    /// <summary>Overview -> its rooms (each followed by its own elevation page), for every level: the page order
    /// used by the viewer and the report. A single-room level has no overview (see BuildLevels), so it
    /// contributes only its one room page plus its elevation.</summary>
    public static List<FloorPlanPage> Flatten(IEnumerable<LevelPlan> levels)
    {
        var pages = new List<FloorPlanPage>();
        foreach (var l in levels)
        {
            if (l.overview != null) pages.Add(l.overview);
            foreach (var r in l.rooms)
            {
                pages.Add(r.page);
                pages.Add(BuildElevation(r.room));
            }
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

    /// <summary>Text grows with the plan so labels stay legible when a whole house is fitted onto one sheet.
    /// Floored at 1 so a small room's numbers never shrink below a readable size.</summary>
    static float TextScale(IEnumerable<Vector2> points, float divisor)
    {
        var p = points.ToList();
        if (p.Count == 0) return 1f;
        float extent = Mathf.Max(p.Max(v => v.x) - p.Min(v => v.x), p.Max(v => v.y) - p.Min(v => v.y));
        return Mathf.Max(1f, extent / divisor);
    }

    /// <summary>
    /// Same as TextScale but without the legibility floor - used only for how far a dimension's number sits
    /// from its own line. That gap used to be driven by the (floored) text size too, which meant every room
    /// smaller than the floor's own threshold got the exact same minimum gap regardless of how small it
    /// actually was - proportionally much too far from the line on a small room's short walls, even though the
    /// same formula looked right on a big room's long ones (where the floor never kicks in).
    /// </summary>
    static float GapScale(IEnumerable<Vector2> points, float divisor)
    {
        var p = points.ToList();
        if (p.Count == 0) return 1f;
        float extent = Mathf.Max(p.Max(v => v.x) - p.Min(v => v.x), p.Max(v => v.y) - p.Min(v => v.y));
        return extent / divisor;
    }

    /// <summary>A dimension line from a to b, shifted by 'offset' with extension lines, ticks and a value label alongside.
    /// 'gapSize' (defaults to textSize) drives only the number's distance from the line - see GapScale.</summary>
    static void AddDimension(FloorPlanPage page, Vector2 a, Vector2 b, Vector2 offset, string text, float textSize = DimTextSize, float gapSize = -1f, bool outer = false)
    {
        if (gapSize < 0f) gapSize = textSize;
        var line = outer ? PlanStyle.OuterDimension : PlanStyle.Dimension;
        var ext = outer ? PlanStyle.OuterExtension : PlanStyle.Extension;
        var tick = outer ? PlanStyle.OuterTick : PlanStyle.Tick;
        Vector2 A = a + offset, B = b + offset;
        Vector2 dir = (b - a).normalized, on = offset.normalized;
        page.lines.Add(new PlanLine(A, B, line));
        page.lines.Add(new PlanLine(a + on * 0.04f, A + on * 0.05f, ext));
        page.lines.Add(new PlanLine(b + on * 0.04f, B + on * 0.05f, ext));
        Vector2 tk = (dir + on).normalized * 0.05f;
        page.lines.Add(new PlanLine(A - tk, A + tk, tick));
        page.lines.Add(new PlanLine(B - tk, B + tk, tick));
        // The value is only written when it fits between the ticks, so neighbouring labels never pile up.
        if ((b - a).magnitude >= Mathf.Max(0.2f, textSize * 2.6f))
            page.texts.Add(new PlanText { pos = (A + B) / 2f + on * (0.06f + gapSize * 0.4f), text = text, angleDeg = TextAngle(dir), size = textSize, style = line });
    }

    /// <summary>The short segments of one wall - corner to opening, opening width, opening to opening, opening to corner - measured on the inside.</summary>
    static void AddInnerChain(FloorPlanPage page, Edge e, List<(float t0, float t1, PlanOpening o)> spans, float offset, float textSize, float gapSize)
    {
        Vector2 inward = -e.outward;
        float cursor = 0;
        foreach (var (t0Raw, t1, _) in spans)
        {
            float t0 = Mathf.Max(t0Raw, cursor);
            if (t0 - cursor > 0.02f) AddDimension(page, e.a + e.dir * cursor, e.a + e.dir * t0, inward * offset, Fmt(t0 - cursor), textSize, gapSize);
            if (t1 - t0 > 0.02f) AddDimension(page, e.a + e.dir * t0, e.a + e.dir * t1, inward * offset, Fmt(t1 - t0), textSize, gapSize);
            cursor = Mathf.Max(cursor, t1);
        }
        if (e.len - cursor > 0.02f) AddDimension(page, e.a + e.dir * cursor, e.b, inward * offset, Fmt(e.len - cursor), textSize, gapSize);
    }

    static void AddWallsAndOpenings(FloorPlanPage page, List<Edge> edges, List<List<(float t0, float t1, PlanOpening o)>> perEdge)
    {
        foreach (var e in edges) page.lines.Add(new PlanLine(e.a, e.b, PlanStyle.Wall));
        for (int i = 0; i < edges.Count; i++)
            foreach (var (t0, t1, o) in perEdge[i])
                page.lines.Add(new PlanLine(edges[i].a + edges[i].dir * t0, edges[i].a + edges[i].dir * t1,
                    o.kind == OpeningKind.Door ? PlanStyle.Door : PlanStyle.Window));
    }

    /// <summary>One room: walls and openings. The whole length of every wall is dimensioned from the outside (green), the short
    /// segments between corners, doors and windows on the inside (red) - both in the same drawing.</summary>
    public static FloorPlanPage BuildRoom(RoomOutline room)
    {
        var page = new FloorPlanPage
        {
            title = room.name,
            roomKey = room.roomKey,
            subtitle = $"Area {Fmt1(room.Area)} m2 | Perimeter {Fmt(Perimeter(room))} m | Ceiling {CeilingText(room)}",
        };
        float ts = DimTextSize * TextScale(room.polygon, 6f);
        float gs = DimTextSize * GapScale(room.polygon, 6f);
        var edges = Edges(room.polygon);
        var perEdge = AssignOpenings(room, edges);
        AddWallsAndOpenings(page, edges, perEdge);

        for (int i = 0; i < edges.Count; i++)
        {
            var e = edges[i];
            AddDimension(page, e.a, e.b, e.outward * OuterOffset, Fmt(e.len), ts, gs, outer: true);
            if (perEdge[i].Count > 0) AddInnerChain(page, e, perEdge[i], InnerChainOffset, ts, gs);
        }
        return page;
    }

    /// <summary>
    /// One "unfolded" elevation sheet for a room: every wall laid out left to right in sequence (as if the
    /// room's walls were cut apart at each corner and laid flat), each with its own floor line, its ceiling
    /// line - sloped if RoomOutline.cornerCeilingHeights gives the two ends different heights - and its own
    /// doors/windows drawn to their real sill and height. A top-down floor plan can never show any of this
    /// (sill heights, opening heights, or a sloped ceiling's true height at each end of a wall); this is the
    /// only place in the PC export that does. Reuses the exact same PlanLine/PlanText/AddDimension primitives
    /// as the floor plan, so the HTML report's existing SVG rendering, pan/zoom and legend need no changes.
    /// </summary>
    public static FloorPlanPage BuildElevation(RoomOutline room)
    {
        var page = new FloorPlanPage
        {
            title = $"{room.name} - elevation",
            subtitle = "walls unfolded left to right, floor at the bottom - not to the same scale as the floor plan",
        };

        int n = room.polygon.Count;
        bool haveCeilingData = room.cornerCeilingHeights.Count == n && n > 0;
        float HeightAt(int i) => haveCeilingData ? room.cornerCeilingHeights[i] : room.ceilingMax;

        // Where each real (non-degenerate) wall starts along the unfolded strip, and which original polygon
        // vertices bound it - kept separate from the drawing pass below so each shared corner gets exactly one
        // height dimension afterwards, not one per wall meeting there.
        var startX = new List<float>();
        var vertA = new List<int>();
        var vertB = new List<int>();
        float cursor = 0f;
        const float wallGap = 0.6f;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            float len = Vector2.Distance(room.polygon[i], room.polygon[j]);
            if (len < 0.2f) continue;
            startX.Add(cursor);
            vertA.Add(i);
            vertB.Add(j);
            cursor += len + wallGap;
        }

        for (int w = 0; w < startX.Count; w++)
        {
            int i = vertA[w], j = vertB[w];
            Vector2 a = room.polygon[i], b = room.polygon[j];
            float len = Vector2.Distance(a, b);
            float x0 = startX[w], x1 = x0 + len;
            float h0 = HeightAt(i), h1 = HeightAt(j);

            page.lines.Add(new PlanLine(new Vector2(x0, 0), new Vector2(x1, 0), PlanStyle.Wall));
            page.lines.Add(new PlanLine(new Vector2(x0, 0), new Vector2(x0, h0), PlanStyle.Wall));
            page.lines.Add(new PlanLine(new Vector2(x1, 0), new Vector2(x1, h1), PlanStyle.Wall));
            page.lines.Add(new PlanLine(new Vector2(x0, h0), new Vector2(x1, h1), PlanStyle.Wall));
            page.texts.Add(new PlanText { pos = new Vector2((x0 + x1) / 2f, -0.35f), text = $"Wall {w + 1} ({Fmt(len)} m)", size = 0.16f, style = PlanStyle.Label, bold = true });

            Vector2 dir = (b - a) / len;
            foreach (var o in room.openings)
            {
                Vector2 v = o.center - a;
                float t = Vector2.Dot(v, dir);
                float distFromLine = Mathf.Abs(dir.x * v.y - dir.y * v.x);
                if (t < -0.3f || t > len + 0.3f || distFromLine > OpeningSnapDist) continue;
                float ox0 = x0 + Mathf.Clamp(t - o.width / 2f, 0, len);
                float ox1 = x0 + Mathf.Clamp(t + o.width / 2f, 0, len);
                if (ox1 - ox0 < 0.05f) continue;

                var style = o.kind == OpeningKind.Door ? PlanStyle.Door : PlanStyle.Window;
                page.lines.Add(new PlanLine(new Vector2(ox0, o.sill), new Vector2(ox1, o.sill), style));
                page.lines.Add(new PlanLine(new Vector2(ox0, o.sill + o.height), new Vector2(ox1, o.sill + o.height), style));
                page.lines.Add(new PlanLine(new Vector2(ox0, o.sill), new Vector2(ox0, o.sill + o.height), style));
                page.lines.Add(new PlanLine(new Vector2(ox1, o.sill), new Vector2(ox1, o.sill + o.height), style));

                float midX = (ox0 + ox1) / 2f;
                AddDimension(page, new Vector2(midX, 0), new Vector2(midX, o.sill), new Vector2(0.12f, 0), Fmt(o.sill), DimTextSize * 0.85f);
                AddDimension(page, new Vector2(midX, o.sill), new Vector2(midX, o.sill + o.height), new Vector2(0.12f, 0), Fmt(o.height), DimTextSize * 0.85f);
            }
        }

        // One ceiling-height dimension per real corner - each wall's own start vertex covers every internal
        // corner exactly once; the very last wall's end vertex (the polygon's closing corner, unfolded to the
        // far right instead of back to x=0) needs one more of its own.
        for (int w = 0; w < startX.Count; w++)
            AddDimension(page, new Vector2(startX[w], 0), new Vector2(startX[w], HeightAt(vertA[w])), new Vector2(-0.25f, 0), Fmt(HeightAt(vertA[w])), DimTextSize, outer: true);
        if (startX.Count > 0)
        {
            float xEnd = startX[startX.Count - 1] + Vector2.Distance(room.polygon[vertA[vertA.Count - 1]], room.polygon[vertB[vertB.Count - 1]]);
            int lastJ = vertB[vertB.Count - 1];
            AddDimension(page, new Vector2(xEnd, 0), new Vector2(xEnd, HeightAt(lastJ)), new Vector2(0.25f, 0), Fmt(HeightAt(lastJ)), DimTextSize, outer: true);
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
            subtitle = $"{level.Count} room(s) | total area {Fmt1(level.Sum(r => r.Area))} m2",
        };
        var edgeCache = level.ToDictionary(r => r, r => Edges(r.polygon));
        float ts = TextScale(level.SelectMany(r => r.polygon), 6f);
        float gs = GapScale(level.SelectMany(r => r.polygon), 6f);
        float ls = TextScale(level.SelectMany(r => r.polygon), 10f); // room names/areas: smaller so they fit inside rooms

        // The overview only ever dimensioned a shared wall's inside face from whichever one of the two rooms
        // happened to draw it second (IsShared skips it from the other), and never at all where a wall meets
        // one it doesn't share with any neighbour - drawing "some but not all" inner chains here read as
        // incomplete/inconsistent. Per-room sheets (BuildRoom) still show every inner chain for that one room.
        foreach (var r in level)
        {
            var edges = edgeCache[r];
            var openings = AssignOpenings(r, edges);
            AddWallsAndOpenings(page, edges, openings);
        }

        foreach (var r in level)
        {
            var edges = edgeCache[r];
            for (int i = 0; i < edges.Count; i++)
            {
                var e = edges[i];
                if (e.len < 0.3f || IsShared(e, r, level, edgeCache)) continue;
                AddDimension(page, e.a, e.b, e.outward * OuterOffset, Fmt(e.len), DimTextSize * ts, DimTextSize * gs, outer: true);
            }
        }

        var all = level.SelectMany(r => r.polygon).ToList();
        if (all.Count > 0)
        {
            float minX = all.Min(p => p.x), maxX = all.Max(p => p.x), minY = all.Min(p => p.y), maxY = all.Max(p => p.y);
            if (maxX - minX > 0.5f) AddDimension(page, new Vector2(minX, minY), new Vector2(maxX, minY), new Vector2(0, -BoundsOffset), Fmt(maxX - minX), DimTextSize * ts * 1.3f, DimTextSize * gs * 1.3f, outer: true);
            if (maxY - minY > 0.5f) AddDimension(page, new Vector2(minX, minY), new Vector2(minX, maxY), new Vector2(-BoundsOffset, 0), Fmt(maxY - minY), DimTextSize * ts * 1.3f, DimTextSize * gs * 1.3f, outer: true);
        }

        foreach (var r in level) AddAreaLabel(page, r, ls);
        return page;
    }

    const float MinLabelledArea = 3f; // m2 - a closet/toilet/narrow hallway this small has no room for even one centred number

    /// <summary>The room's area, centred inside its own outline - the room's name used to sit above it here too, but
    /// the report/room sheet's own title already names it (in both the HTML report and the in-headset viewer), so
    /// repeating the name a second time, drawn over the room itself, was just clutter.</summary>
    static void AddAreaLabel(FloorPlanPage page, RoomOutline r, float labelScale)
    {
        if (r.Area < MinLabelledArea) return;
        Vector2 c = Centroid(r.polygon);
        page.texts.Add(new PlanText { pos = c, text = $"{Fmt1(r.Area)} m2", size = 0.18f * labelScale, style = PlanStyle.Label, bold = true });
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
