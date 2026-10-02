using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// Turns XRDimensionLabels into plain mesh geometry: a thin line with end ticks per measurement and, for the
/// exported model, the number itself drawn with a small built-in stroke font (seven-segment digits) as thin
/// flat strips lying on the label's own plane - OBJ/GLB carry no text, so the digits have to be geometry.
/// Text faces the label's 'normal' (into the room), reads left to right for someone standing in that room,
/// always upright, and sits beside its line (above a horizontal one, to the right of a vertical one).
/// Everything is in the model's own local space, like XRDimensionLabel itself.
/// </summary>
public static class DimensionGeometry
{
    public const float LineThickness = 0.008f;
    public const float TextHeight = 0.08f;
    const float TickLength = 0.06f;

    public static readonly Color TextColor = new Color32(0x1a, 0x20, 0x2c, 255);

    /// <summary>Adds the line (and, if requested, the number) of every label to 'room' as separate parts.</summary>
    public static void Add(XRRoomModel room, IEnumerable<XRDimensionLabel> dims, bool withText)
    {
        foreach (var d in dims)
        {
            Color color = d.outer ? DimensionColors.Outer : DimensionColors.Inner;
            var line = new XRMeshPart { name = "DimLine", materialName = d.outer ? "DIM_OUTER" : "DIM_INNER", color = color };
            Vector3 dir = d.lineEnd - d.lineStart;
            if (dir.magnitude < 0.01f) continue;
            Vector3 n = d.normal.sqrMagnitude > 1e-6f ? d.normal.normalized : Vector3.forward;
            AddBar(line, d.lineStart, d.lineEnd, LineThickness);
            // Ticks: short perpendicular marks in the label's plane at both ends.
            Vector3 across = Vector3.Cross(n, dir.normalized).normalized * (TickLength / 2f);
            AddBar(line, d.lineStart - across, d.lineStart + across, LineThickness);
            AddBar(line, d.lineEnd - across, d.lineEnd + across, LineThickness);
            room.parts.Add(line);

            // Text is single-sided (DIM_TEXT is exported with doubleSided=false), drawn twice back to back: each
            // copy reads correctly from its own side, so a number is never seen mirrored from either side.
            if (withText) { AddText(room, d, n); AddText(room, d, -n); }
        }
    }

    static void AddText(XRRoomModel room, XRDimensionLabel d, Vector3 n)
    {
        string text = NumberOnly(d.text);
        if (text.Length == 0) return;

        // Reading direction: the right-hand side of someone standing in the room looking at the wall (along -n).
        Vector3 right = Vector3.Cross(Vector3.up, -n);
        if (right.sqrMagnitude < 1e-6f) right = Vector3.right;
        right.Normalize();
        Vector3 up = Vector3.Cross(-n, right).normalized; // upright, in the wall's plane
        if (up.y < 0) up = -up;

        float h = TextHeight, w = Width(text, h);
        Vector3 dir = (d.lineEnd - d.lineStart).normalized;
        Vector3 mid = (d.lineStart + d.lineEnd) / 2f + n * 0.004f; // a hair in front of the line
        bool vertical = Mathf.Abs(dir.y) > 0.7f;
        Vector3 center = vertical ? mid + right * (w / 2f + 0.04f) : mid + up * (h / 2f + 0.03f);
        Vector3 origin = center - right * (w / 2f) - up * (h / 2f); // bottom-left of the text block

        var part = new XRMeshPart { name = "DimText", materialName = "DIM_TEXT", color = TextColor };
        float unit = h / 2f, t = h * 0.12f, x = 0f;
        foreach (char c in text)
        {
            if (c == '.')
            {
                float dot = t * 1.4f;
                Vector3 p = origin + right * (x + 0.1f * unit) + up * (dot / 2f);
                AddBar(part, p, p + right * dot, dot, n);
                x += 0.1f * unit + dot + 0.45f * unit;
                continue;
            }
            if (c == '-')
            {
                AddBar(part, origin + right * x + up * unit, origin + right * (x + unit) + up * unit, t, n);
                x += 1.6f * unit;
                continue;
            }
            int digit = c - '0';
            if (digit < 0 || digit > 9) { x += 1.6f * unit; continue; }
            foreach (var (a, b) in Segments(Digits[digit]))
                AddBar(part, origin + right * ((x + a.x * unit)) + up * (a.y * unit),
                             origin + right * ((x + b.x * unit)) + up * (b.y * unit), t, n);
            x += 1.6f * unit;
        }
        if (part.vertices.Count > 0) room.parts.Add(part);
    }

    static float Width(string s, float h)
    {
        float unit = h / 2f, x = 0f;
        foreach (char c in s) x += c == '.' ? 0.55f * unit + h * 0.12f * 1.4f : 1.6f * unit;
        return Mathf.Max(0f, x - 0.6f * unit);
    }

    static string NumberOnly(string s)
    {
        var sb = new StringBuilder();
        // Labels are formatted with the current culture, so a Czech system writes "1,25" - draw it as "1.25",
        // the same as the floor plans.
        foreach (char c in s ?? "") if (char.IsDigit(c) || c == '-') sb.Append(c); else if (c == '.' || c == ',') sb.Append('.');
        return sb.ToString().Trim('.');
    }

    // Seven-segment layout on a 1 x 2 grid: a top, b top-right, c bottom-right, d bottom, e bottom-left, f top-left, g middle.
    static readonly string[] Digits = { "abcdef", "bc", "abged", "abgcd", "fgbc", "afgcd", "afgedc", "abc", "abcdefg", "abcdfg" };

    static IEnumerable<(Vector2, Vector2)> Segments(string segs)
    {
        foreach (char s in segs)
        {
            switch (s)
            {
                case 'a': yield return (new Vector2(0, 2), new Vector2(1, 2)); break;
                case 'b': yield return (new Vector2(1, 2), new Vector2(1, 1)); break;
                case 'c': yield return (new Vector2(1, 1), new Vector2(1, 0)); break;
                case 'd': yield return (new Vector2(0, 0), new Vector2(1, 0)); break;
                case 'e': yield return (new Vector2(0, 0), new Vector2(0, 1)); break;
                case 'f': yield return (new Vector2(0, 1), new Vector2(0, 2)); break;
                case 'g': yield return (new Vector2(0, 1), new Vector2(1, 1)); break;
            }
        }
    }

    /// <summary>A thin square bar from a to b (used for lines and ticks - visible from any side).</summary>
    static void AddBar(XRMeshPart part, Vector3 a, Vector3 b, float thickness)
    {
        Vector3 dir = b - a;
        float len = dir.magnitude;
        if (len < 1e-4f) return;
        dir /= len;
        Vector3 refUp = Mathf.Abs(Vector3.Dot(dir, Vector3.up)) > 0.99f ? Vector3.right : Vector3.up;
        Vector3 side = Vector3.Cross(dir, refUp).normalized * (thickness / 2f);
        Vector3 vert = Vector3.Cross(side, dir).normalized * (thickness / 2f);
        int o = part.vertices.Count;
        part.vertices.AddRange(new[] {
            a - side - vert, a + side - vert, a + side + vert, a - side + vert,
            b - side - vert, b + side - vert, b + side + vert, b - side + vert });
        part.triangles.AddRange(new[] { 0,3,2, 0,2,1, 4,5,6, 4,6,7, 1,2,6, 1,6,5, 0,4,7, 0,7,3, 3,7,6, 3,6,2, 0,1,5, 0,5,4 });
        for (int i = part.triangles.Count - 36; i < part.triangles.Count; i++) part.triangles[i] += o;
    }

    /// <summary>A flat strip from a to b lying in the plane with normal n, visible from the +n side only (text strokes).</summary>
    static void AddBar(XRMeshPart part, Vector3 a, Vector3 b, float thickness, Vector3 n)
    {
        Vector3 dir = b - a;
        float len = dir.magnitude;
        if (len < 1e-5f) { dir = Vector3.Cross(n, Vector3.up); if (dir.sqrMagnitude < 1e-6f) dir = Vector3.right; dir.Normalize(); }
        else dir /= len;
        Vector3 w = Vector3.Cross(n, dir).normalized * (thickness / 2f);
        Vector3 ext = len < 1e-5f ? Vector3.zero : dir * (thickness / 2f); // square ends so strokes join cleanly at the corners
        int o = part.vertices.Count;
        part.vertices.AddRange(new[] { a - ext - w, a - ext + w, b + ext + w, b + ext - w });
        part.triangles.AddRange(new[] { o, o + 2, o + 1, o, o + 3, o + 2 });
    }
}
