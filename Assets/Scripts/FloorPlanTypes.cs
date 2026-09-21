using System.Collections.Generic;
using UnityEngine;

public enum PlanStyle { Wall, Door, Window, Dimension, Extension, Tick, Label }
public enum OpeningKind { Door, Window }

public struct PlanLine
{
    public Vector2 a, b;
    public PlanStyle style;
    public PlanLine(Vector2 a, Vector2 b, PlanStyle style) { this.a = a; this.b = b; this.style = style; }
}

public struct PlanText
{
    public Vector2 pos;
    public string text;
    public float angleDeg;
    public float size;
    public PlanStyle style;
    public bool bold;
}

/// <summary>One drawable plan sheet in plan meters (x right, y = world Z "up"), independent of any renderer.</summary>
public class FloorPlanPage
{
    public string title = "";
    public string subtitle = "";
    public string roomKey;   // the room this sheet shows (null for a story overview) - key for RoomNames
    public readonly List<PlanLine> lines = new List<PlanLine>();
    public readonly List<PlanText> texts = new List<PlanText>();

    public Rect Bounds()
    {
        bool any = false;
        float minX = 0, minY = 0, maxX = 0, maxY = 0;
        void Add(Vector2 p)
        {
            if (!any) { minX = maxX = p.x; minY = maxY = p.y; any = true; return; }
            minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
            minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
        }
        foreach (var l in lines) { Add(l.a); Add(l.b); }
        foreach (var t in texts) Add(t.pos);
        return any ? Rect.MinMaxRect(minX, minY, maxX, maxY) : new Rect(0, 0, 1, 1);
    }
}

public class PlanOpening
{
    public OpeningKind kind;
    public Vector2 center;
    public float width;
    public float sill;    // bottom edge above the floor
    public float height;
}

/// <summary>One room's floor outline in world XZ, with its openings. No dependency on MRUK types.</summary>
public class RoomOutline
{
    public string id = "";
    public string roomKey = "";   // stable room UUID, the key for RoomNames
    public string name = "";
    public List<Vector2> polygon = new List<Vector2>();
    public float floorY;
    public float ceilingMin = 2.5f;
    public float ceilingMax = 2.5f;
    public int detail;    // how much the scan captured (anchor count) - used to pick the better of two duplicate scans
    public List<PlanOpening> openings = new List<PlanOpening>();

    public float Area => Mathf.Abs(FloorPlanBuilder.SignedArea(polygon));
}

public class RoomPlan
{
    public RoomOutline room;
    public FloorPlanPage page;
}

public class LevelPlan
{
    public int index;
    public FloorPlanPage overview;
    public List<RoomPlan> rooms = new List<RoomPlan>();
}
