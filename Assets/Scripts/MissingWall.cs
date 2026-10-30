#if META_XR_SDK_INSTALLED
using System.Collections.Generic;
using System.Linq;
using Meta.XR.MRUtilityKit;
using UnityEngine;

/// <summary>
/// Finds a wall the scan missed (a big mirror the Quest could not see): the walls of a room form a closed loop, so a gap
/// shows as two loose wall ends that no other wall meets. The pair closest to where the user points is the missing
/// wall - it runs between the two ends and takes its height from the walls beside it. Pure geometry on the loaded rooms
/// (scan frame), no JSON.
/// </summary>
public static class MissingWall
{
    const float JoinDist = 0.25f;           // two wall ends closer than this are one corner
    const float MinLen = 0.3f, MaxLen = 8f; // a gap shorter / longer than this is not a wall
    const float MaxAim = 2.5f;              // how far (m) from the pointing ray the gap may be

    struct End { public int wall; public Vector3 pt; public float top; }

    static float Horiz(Vector3 a, Vector3 b) { a.y = b.y = 0f; return Vector3.Distance(a, b); }

    /// <summary>The loose wall ends of a room (bottom points; 'top' = the wall's top height).</summary>
    static List<End> LooseEnds(MRUKRoom room)
    {
        var ends = new List<End>();
        int wi = 0;
        foreach (var w in room.WallAnchors)
        {
            if (w != null && w.PlaneRect.HasValue)
            {
                var rc = w.PlaneRect.Value; var t = w.transform;
                ends.Add(new End { wall = wi, pt = t.TransformPoint(new Vector3(rc.xMin, rc.yMin, 0f)), top = t.TransformPoint(new Vector3(rc.xMin, rc.yMax, 0f)).y });
                ends.Add(new End { wall = wi, pt = t.TransformPoint(new Vector3(rc.xMax, rc.yMin, 0f)), top = t.TransformPoint(new Vector3(rc.xMax, rc.yMax, 0f)).y });
            }
            wi++;
        }
        return ends.Where(e => !ends.Any(o => o.wall != e.wall && Horiz(o.pt, e.pt) < JoinDist)).ToList();
    }

    /// <summary>How far the point is from the (horizontal) pointing ray; behind the hand counts as far.</summary>
    static float AimDistance(Vector3 point, Ray ray)
    {
        Vector3 d = new Vector3(ray.direction.x, 0f, ray.direction.z);
        if (d.sqrMagnitude < 1e-6f) return float.MaxValue;
        d.Normalize();
        Vector3 v = new Vector3(point.x - ray.origin.x, 0f, point.z - ray.origin.z);
        float along = Vector3.Dot(v, d);
        if (along < 0f) return float.MaxValue;
        return (v - d * along).magnitude;
    }

    /// <summary>The gap nearest to where the ray points: its two bottom points (scan frame, same height) and its
    /// height. False when no room has a gap near the ray.</summary>
    public static bool Find(List<MRUKRoom> rooms, Ray ray, out MRUKRoom room, out Vector3 p, out Vector3 q, out float height)
    {
        room = null; p = q = default; height = 0f;
        float best = MaxAim;
        foreach (var r in rooms)
        {
            if (r == null) continue;
            var loose = LooseEnds(r);
            for (int i = 0; i < loose.Count; i++)
                for (int j = i + 1; j < loose.Count; j++)
                {
                    if (loose[i].wall == loose[j].wall) continue;
                    float len = Horiz(loose[i].pt, loose[j].pt);
                    if (len < MinLen || len > MaxLen) continue;
                    float score = AimDistance((loose[i].pt + loose[j].pt) / 2f, ray);
                    if (score >= best) continue;
                    best = score; room = r;
                    float bottom = (loose[i].pt.y + loose[j].pt.y) / 2f;
                    p = new Vector3(loose[i].pt.x, bottom, loose[i].pt.z);
                    q = new Vector3(loose[j].pt.x, bottom, loose[j].pt.z);
                    height = Mathf.Max(0.5f, (loose[i].top + loose[j].top) / 2f - bottom);
                }
        }
        return room != null;
    }
}
#endif
