using System.Collections.Generic;
using System.Linq;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

/// <summary>
/// The scanned walls (MRUK wall anchors) in the scan's world frame, for edit tools to snap to: a point near a wall
/// lands exactly on its face, stairs run along a wall, a landing stops at the wall ahead.
/// </summary>
public class WallSnap
{
    public struct Wall
    {
        public Vector3 pos, n, right;   // n: horizontal, pointing into the room the anchor belongs to
        public float halfW, bottom, top;
    }

    readonly List<Wall> walls = new List<Wall>();

    public WallSnap(List<MRUKRoom> rooms)
    {
        foreach (var a in rooms.SelectMany(r => r.Anchors))
        {
            if (a == null || !a.PlaneRect.HasValue || !MRUKDataProcessor.IsStructuralWall(a)) continue;
            Vector3 n = Vector3.ProjectOnPlane(a.transform.forward, Vector3.up);
            if (n.sqrMagnitude < 1e-4f) continue;
            n.Normalize();
            var rect = a.PlaneRect.Value;
            walls.Add(new Wall
            {
                pos = a.transform.position, n = n, right = Vector3.Cross(Vector3.up, n),
                halfW = rect.width / 2f, bottom = a.transform.position.y - rect.height / 2f, top = a.transform.position.y + rect.height / 2f,
            });
        }
    }

    bool Covers(Wall w, Vector3 p, float probeHeight)
    {
        float y = p.y + probeHeight;
        return Mathf.Abs(Vector3.Dot(p - w.pos, w.right)) <= w.halfW + 0.05f && y >= w.bottom - 0.3f && y <= w.top + 0.4f;
    }

    /// <summary>The wall whose face is nearest to p (in front of it, within maxDist). probeHeight: p is on the
    /// floor (+0.5 m checks the wall really stands there) or on a ceiling (-0.5 m).</summary>
    public bool Nearest(Vector3 p, float maxDist, float probeHeight, out Wall wall, out float dist)
    {
        wall = default; dist = float.MaxValue;
        bool found = false;
        foreach (var w in walls)
        {
            float d = Vector3.Dot(p - w.pos, w.n);
            if (d < -0.1f || d > maxDist || d >= dist || !Covers(w, p, probeHeight)) continue;
            wall = w; dist = d; found = true;
        }
        return found;
    }

    /// <summary>p moved onto the nearest wall face if one is within maxDist (also into a corner, if two are).</summary>
    public Vector3 SnapPoint(Vector3 p, float maxDist, float probeHeight)
    {
        if (!Nearest(p, maxDist, probeHeight, out var w1, out float d1)) return p;
        p -= w1.n * d1;
        // A second wall roughly square to the first, also close: snap into the corner.
        foreach (var w in walls)
        {
            if (Mathf.Abs(Vector3.Dot(w.n, w1.n)) > 0.3f) continue;
            float d = Vector3.Dot(p - w.pos, w.n);
            if (d < -0.1f || d > maxDist || !Covers(w, p, probeHeight)) continue;
            p -= w.n * d;
            break;
        }
        return p;
    }

    /// <summary>Distance from q along dir to the first wall facing back at it (within maxDist), else -1.</summary>
    public float Ahead(Vector3 q, Vector3 dir, float maxDist, float probeHeight)
    {
        float best = -1f;
        foreach (var w in walls)
        {
            float dn = Vector3.Dot(dir, w.n);
            if (dn > -0.7f) continue; // must face the walker
            float s = Vector3.Dot(w.pos - q, w.n) / dn;
            if (s <= 0f || s > maxDist || (best >= 0f && s >= best)) continue;
            if (!Covers(w, q + dir * s, probeHeight)) continue;
            best = s;
        }
        return best;
    }
}
