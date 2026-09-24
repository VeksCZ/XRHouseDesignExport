using System.Collections.Generic;
using System.Linq;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

/// <summary>Turns MRUK rooms into renderer-independent RoomOutlines (the only place the floor plans touch MRUK).</summary>
public static class MRUKPlanExtractor
{
#if META_XR_SDK_INSTALLED
    public static List<RoomOutline> Extract(List<MRUKRoom> rooms)
    {
        var result = new List<RoomOutline>();
        foreach (var room in rooms)
        {
            if (room == null) continue;

            // A split-level room reports several floor anchors; each becomes its own outline.
            var floors = room.FloorAnchors.Where(f => f != null && f.PlaneBoundary2D != null && f.PlaneBoundary2D.Count >= 3).ToList();
            string baseName = MRUKDataProcessor.GetRoomLabel(room);

            for (int i = 0; i < floors.Count; i++)
            {
                var floor = floors[i];
                var outline = new RoomOutline
                {
                    id = room.Anchor.Uuid + "_" + i,
                    roomKey = room.Anchor.Uuid.ToString(),
                    name = floors.Count > 1 ? $"{baseName} L{i + 1}" : baseName,
                    floorY = floor.transform.position.y,
                    detail = room.Anchors.Count,
                };

                foreach (var p in floor.PlaneBoundary2D)
                {
                    Vector3 w = floor.transform.position + floor.transform.rotation * new Vector3(p.x, p.y, 0);
                    outline.polygon.Add(new Vector2(w.x, w.z));
                    // Same per-point ceiling sampling XRModelFactory.CreateReconstruction uses for the 3D model,
                    // so an elevation view can show a sloped ceiling's true height at each end of a wall.
                    float ceilY = XRModelFactory.SampleCeilingHeight(room.CeilingAnchors, w, w.y + 2.5f);
                    outline.cornerCeilingHeights.Add(ceilY - outline.floorY);
                }

                var ceilings = room.CeilingAnchors.Where(c => c != null).Select(c => c.transform.position.y - outline.floorY).ToList();
                if (ceilings.Count > 0) { outline.ceilingMin = ceilings.Min(); outline.ceilingMax = ceilings.Max(); }

                foreach (var a in room.Anchors)
                {
                    if (a == null || !a.PlaneRect.HasValue) continue;
                    bool door = MRUKDataProcessor.IsDoor(a);
                    if (!door && !MRUKDataProcessor.IsWindow(a)) continue;

                    float h = a.PlaneRect.Value.height;
                    outline.openings.Add(new PlanOpening
                    {
                        kind = door ? OpeningKind.Door : OpeningKind.Window,
                        center = new Vector2(a.transform.position.x, a.transform.position.z),
                        width = a.PlaneRect.Value.width,
                        height = h,
                        sill = Mathf.Max(0f, a.transform.position.y - h / 2f - outline.floorY),
                    });
                }
                result.Add(outline);
            }
        }

        MirrorSharedOpenings(result);

        // Several rooms with the same name (two bedrooms) get numbered so plans and reports can tell them apart.
        foreach (var group in result.GroupBy(o => o.name).Where(g => g.Count() > 1))
        {
            int n = 0;
            foreach (var o in group) o.name = $"{o.name} {++n}";
        }
        return result;
    }
#endif

    /// <summary>
    /// MRUK only ever attaches a door/window frame anchor to whichever room's own scan captured it, so a real
    /// doorway between two scanned rooms often ends up as an opening in only one room's floor plan and a plain,
    /// solid wall in the other. Copies every opening across to any other room whose own wall runs right through
    /// that same spot and doesn't already have one there. Pure RoomOutline geometry (no MRUK types), so it can be
    /// unit-tested directly instead of only indirectly through a full MRUK scan.
    /// </summary>
    internal static void MirrorSharedOpenings(List<RoomOutline> rooms)
    {
        const float onWallTolerance = 0.35f;    // how far off a neighbour's wall line still counts as "on it"
        const float alreadyThereTolerance = 0.5f; // this room's own opening already covers that spot

        foreach (var src in rooms)
            foreach (var dst in rooms)
            {
                if (ReferenceEquals(src, dst)) continue;
                foreach (var op in src.openings.ToList())
                {
                    if (DistanceToPolygon(dst.polygon, op.center) > onWallTolerance) continue;
                    if (dst.openings.Any(o => Vector2.Distance(o.center, op.center) < alreadyThereTolerance)) continue;

                    // Sill height is relative to each room's own floor - convert through the shared world height
                    // in case the two rooms' floors aren't at exactly the same level.
                    float worldSillY = op.sill + src.floorY;
                    dst.openings.Add(new PlanOpening
                    {
                        kind = op.kind, center = op.center, width = op.width, height = op.height,
                        sill = Mathf.Max(0f, worldSillY - dst.floorY),
                    });
                }
            }
    }

    static float DistanceToPolygon(List<Vector2> poly, Vector2 p)
    {
        float best = float.MaxValue;
        for (int i = 0; i < poly.Count; i++)
            best = Mathf.Min(best, DistanceToSegment(p, poly[i], poly[(i + 1) % poly.Count]));
        return best;
    }

    static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
        return Vector2.Distance(p, a + ab * t);
    }
}
