using System.Globalization;
using System.Text;
using UnityEngine;

/// <summary>Draws a FloorPlanPage as inline SVG (viewBox in metres, north up).</summary>
public static class FloorPlanSvg
{
    const float Padding = 0.8f;
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static string N(float v) => v.ToString("0.###", Inv);

    static string ClassFor(PlanStyle s)
    {
        switch (s)
        {
            case PlanStyle.Wall: return "wall";
            case PlanStyle.Door: return "door";
            case PlanStyle.Window: return "window";
            case PlanStyle.Extension: return "ext";
            case PlanStyle.Tick: return "tick";
            case PlanStyle.OuterExtension: return "ext outer";
            case PlanStyle.OuterTick: return "tick outer";
            case PlanStyle.OuterDimension: return "dimline outer";
            default: return "dimline";
        }
    }

    public static void Write(StringBuilder html, FloorPlanPage page)
    {
        Rect b = page.Bounds();
        html.Append("<div class='svg-container'><div class='zoom-controls'><button class='btn-in'>+</button><button class='btn-out'>-</button><button class='btn-reset'>&#8634;</button></div>");
        html.Append(string.Format(Inv, "<svg viewBox='{0} {1} {2} {3}' preserveAspectRatio='xMidYMid meet'>",
            N(b.xMin - Padding), N(-(b.yMax + Padding)), N(b.width + Padding * 2), N(b.height + Padding * 2)));

        // SVG's y axis points down, plan y (world Z) points up: flip y everywhere.
        foreach (var l in page.lines)
            html.Append(string.Format(Inv, "<line x1='{0}' y1='{1}' x2='{2}' y2='{3}' class='{4}'/>", N(l.a.x), N(-l.a.y), N(l.b.x), N(-l.b.y), ClassFor(l.style)));

        foreach (var t in page.texts)
        {
            string cls = t.style == PlanStyle.Label ? (t.bold ? "lbl lblb" : "lbl") : t.style == PlanStyle.OuterDimension ? "dim outer" : "dim";
            string transform = System.Math.Abs(t.angleDeg) > 0.01f ? string.Format(Inv, " transform='rotate({0} {1} {2})'", N(-t.angleDeg), N(t.pos.x), N(-t.pos.y)) : "";
            html.Append(string.Format(Inv, "<text x='{0}' y='{1}' font-size='{2}' class='{3}'{4}>{5}</text>",
                N(t.pos.x), N(-t.pos.y), N(t.size), cls, transform, System.Net.WebUtility.HtmlEncode(t.text)));
        }
        html.Append("</svg></div>");
    }
}
