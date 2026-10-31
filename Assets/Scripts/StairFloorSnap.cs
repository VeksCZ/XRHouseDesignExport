using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A stair starts on a floor. If a room was re-scanned and moved, its stairs (stored room-local) can come out a whole
/// storey above or below the floor they were drawn on. This finds how far the first point is off the floor under it
/// (pure geometry, no MRUK - StairGeometry feeds it the scanned floors).
/// </summary>
public static class StairFloorSnap
{
    /// <summary>Off by less than this = fine (rough vs finished floor, a click that landed a bit high).</summary>
    public const float MinOffset = 0.35f;
    /// <summary>Further than this there is no sensible floor to snap to.</summary>
    public const float MaxOffset = 8f;

    /// <summary>The vertical move that puts p on the nearest floor that lies under (or over) it in plan - 0 when p is
    /// already on a floor (within MinOffset), when no floor is below p's spot, or when the nearest one is MaxOffset away.</summary>
    public static float Offset(Vector3 p, IEnumerable<(List<Vector3> outline, float y)> floors)
    {
        bool any = false; float best = 0f, bestAbs = float.MaxValue;
        foreach (var (outline, y) in floors)
        {
            if (outline == null || outline.Count < 3 || !Inside(outline, p)) continue;
            float dy = y - p.y;
            if (Mathf.Abs(dy) < bestAbs) { bestAbs = Mathf.Abs(dy); best = dy; any = true; }
        }
        return any && bestAbs >= MinOffset && bestAbs <= MaxOffset ? best : 0f;
    }

    /// <summary>Point in polygon, in plan (x, z).</summary>
    public static bool Inside(List<Vector3> poly, Vector3 p)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            Vector3 a = poly[i], b = poly[j];
            if ((a.z > p.z) != (b.z > p.z) && p.x < (b.x - a.x) * (p.z - a.z) / (b.z - a.z) + a.x) inside = !inside;
        }
        return inside;
    }
}
