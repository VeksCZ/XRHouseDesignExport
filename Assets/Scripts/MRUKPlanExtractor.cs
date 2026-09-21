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

        // Several rooms with the same name (two bedrooms) get numbered so plans and reports can tell them apart.
        foreach (var group in result.GroupBy(o => o.name).Where(g => g.Count() > 1))
        {
            int n = 0;
            foreach (var o in group) o.name = $"{o.name} {++n}";
        }
        return result;
    }
#endif
}
