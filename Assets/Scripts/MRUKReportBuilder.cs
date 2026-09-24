using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

/// <summary>Builds the HTML property report: an overview plan per story, then one sheet per room.</summary>
public static class MRUKReportBuilder
{
    const string STYLE = @"
        @page{size:A4 landscape;margin:12mm}
        body{font-family:'Segoe UI', Tahoma, Geneva, Verdana, sans-serif;margin:0;background:#f0f2f5;color:#1a202c;}
        .header{background:#2d3748; color:white; padding:32px 20px; text-align:center;}
        .header p{margin:6px 0 0; opacity:.8}
        .content{max-width:1200px; margin:auto; padding:20px;}
        .floor-section{margin-bottom:40px;}
        .section-title{border-bottom:3px solid #3182ce; padding-bottom:10px; margin:40px 0 20px;}
        .container{background:white;padding:24px;border-radius:12px;margin-bottom:24px;box-shadow:0 6px 18px rgba(0,0,0,0.06);}
        .container h3{margin:0 0 4px} .sub{color:#4a5568;font-size:.9em;margin:0 0 14px}
        .badge-container{display:flex; gap:10px; margin-bottom:16px; flex-wrap:wrap}
        .badge{background:#e2e8f0; padding:6px 14px; border-radius:30px; font-size:.8em; font-weight:700; color:#2d3748;}
        .svg-container{width:100%; overflow:hidden; cursor:move; background:#f7fafc; border:2px solid #edf2f7; border-radius:10px; position:relative; height:640px; margin-bottom:18px;}
        svg{width:100%; height:100%; background:#fff; display:block;}
        .wall{stroke:#2d3748;stroke-width:0.12;stroke-linecap:square}
        .door{stroke:#b7791f;stroke-width:0.14}
        .window{stroke:#3182ce;stroke-width:0.14}
        /* Guide/extension lines and ticks stay red on both inner and outer dimensions (no .outer override needed
           since they're already red) - only the dimension line itself (where the number sits) tells the two
           apart: purple inside, green outside. */
        .dimline{stroke:#805ad5;stroke-width:0.015}
        .ext{stroke:#c53030;stroke-width:0.01;opacity:.7}
        .tick{stroke:#c53030;stroke-width:0.03}
        .dimline.outer{stroke:#2f855a}
        text{font-family:'Segoe UI',Arial,sans-serif}
        /* Dimension numbers are dark (not the line's own red/green) with a white halo, so they stay
           readable wherever they cross a line, a wall or another number. */
        .dim{fill:#1a202c;text-anchor:middle;dominant-baseline:central;font-weight:600;paint-order:stroke fill;stroke:#fff;stroke-width:0.05px}
        .lbl{fill:#1a202c;text-anchor:middle;dominant-baseline:central}
        .lblb{font-weight:700}
        .legend{font-size:.8em;color:#4a5568;margin-top:-8px;margin-bottom:12px}
        .legend b{display:inline-block;width:22px;height:0;border-top:5px solid;vertical-align:middle;margin:0 4px 0 12px}
        table{width:100%;border-collapse:separate; border-spacing:0; margin-top:10px; border-radius:8px; overflow:hidden; border:1px solid #e2e8f0; font-size:.9em}
        th,td{padding:10px 14px;text-align:left; border-bottom:1px solid #edf2f7;}
        th{background:#edf2f7; color:#4a5568; font-weight:700; font-size:.75em; text-transform:uppercase; letter-spacing:1px;}
        tr:last-child td{border-bottom:none;}
        .type-door{color:#b7791f;font-weight:600} .type-window{color:#3182ce;font-weight:600}
        .zoom-controls{position:absolute; top:16px; left:16px; z-index:10; display:flex; flex-direction:column; gap:8px;}
        .zoom-controls button{width:38px; height:38px; cursor:pointer; background:white; border:none; border-radius:8px; font-weight:bold; box-shadow:0 3px 6px rgba(0,0,0,0.1); font-size:1.1em; color:#2d3748;}
        .mode-toggle{margin-top:14px; background:rgba(255,255,255,.12); color:white; border:1px solid rgba(255,255,255,.4); border-radius:20px; padding:8px 18px; font-size:.85em; font-weight:700; cursor:pointer;}
        .mode-toggle:hover{background:rgba(255,255,255,.2)}
        @media print{body{background:white} .container{box-shadow:none;break-inside:avoid;break-after:page} .zoom-controls{display:none} .mode-toggle{display:none} .svg-container{height:150mm;border:none}}
    ";

    const string SCRIPT = "document.querySelectorAll('.svg-container').forEach(c=>{const s=c.querySelector('svg');let sc=1,x=0,y=0,d=false,sx,sy;c.onwheel=e=>{e.preventDefault();sc=Math.min(Math.max(0.1,sc*(e.deltaY>0?0.9:1.1)),10);u()};c.onmousedown=e=>{if(e.target.tagName=='BUTTON')return;d=true;sx=e.clientX-x;sy=e.clientY-y};window.onmousemove=e=>{if(d){x=e.clientX-sx;y=e.clientY-sy;u()}};window.onmouseup=()=>d=false;function u(){s.style.transform=`translate(${x}px,${y}px) scale(${sc})`};c.querySelector('.btn-in').onclick=()=>{sc*=1.2;u()};c.querySelector('.btn-out').onclick=()=>{sc/=1.2;u()};c.querySelector('.btn-reset').onclick=()=>{sc=1;x=0;y=0;u()}})";

    // Toggles between the "Exact" (true measured shape) and "Adjusted" (small corner jitter cleaned up to real
    // right angles) floor plans, both already rendered into the page - this just shows/hides one or the other.
    const string MODE_SCRIPT = "(function(){var b=document.getElementById('modeToggle'),e=document.getElementById('mode-exact'),a=document.getElementById('mode-adjusted'),adj=false;b.onclick=function(){adj=!adj;e.style.display=adj?'none':'block';a.style.display=adj?'block':'none';b.textContent=(adj?'Adjusted':'Exact')+' view (click for '+(adj?'Exact':'Adjusted')+')'};})();";

    static string Enc(string s) => WebUtility.HtmlEncode(s ?? "");

    /// <param name="rooms">Outlines as extracted from the scan (world coordinates, not yet aligned).</param>
    /// <param name="alignYaw">Yaw from FloorPlanBuilder.CorrectionYaw, so walls run parallel to the page edges.</param>
    public static string GenerateFullReport(List<RoomOutline> rooms, float alignYaw, string sourceName)
    {
        var aligned = FloorPlanBuilder.Aligned(rooms, alignYaw);
        var levelsExact = FloorPlanBuilder.BuildLevels(aligned);
        var levelsAdjusted = FloorPlanBuilder.BuildLevels(FloorPlanBuilder.Rectified(aligned));

        var html = new StringBuilder();
        html.Append("<html><head><meta charset='UTF-8'><title>Property Report</title><style>" + STYLE + "</style></head><body>");
        html.Append("<div class='header'><h1>Property Documentation</h1><p>")
            .Append(Enc(sourceName)).Append(" &middot; ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm"))
            .Append(" &middot; ").Append(rooms.Count).Append(" room(s), ").Append(levelsExact.Count).Append(" floor(s), ")
            .Append(rooms.Sum(r => r.Area).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append(" m&sup2;</p>")
            // "Exact" shows the true measured shape; "Adjusted" cleans up small corner jitter to real right
            // angles (FloorPlanBuilder.Rectified) - a real angled wall (a bay window, a cut corner) stays as
            // measured either way. Both are already rendered below; this just shows one and hides the other.
            .Append("<button id='modeToggle' class='mode-toggle'>Exact view (click for Adjusted)</button>")
            .Append("</div>");
        html.Append("<div class='content'>");
        html.Append("<div id='mode-exact'>"); AppendLevels(html, levelsExact); html.Append("</div>");
        html.Append("<div id='mode-adjusted' style='display:none'>"); AppendLevels(html, levelsAdjusted); html.Append("</div>");

        // The explicit ';' between the two scripts matters: with none, "...forEach(...)})(function(){...})();"
        // parses as calling forEach's return value (undefined) as a function - a TypeError that aborts before
        // the mode-toggle IIFE ever runs, which is exactly why "click for Adjusted" silently did nothing.
        html.Append("</div><script>").Append(SCRIPT).Append(";").Append(MODE_SCRIPT).Append("</script></body></html>");
        return html.ToString();
    }

    static void AppendLevels(StringBuilder html, List<LevelPlan> levels)
    {
        foreach (var level in levels)
        {
            html.Append($"<div class='floor-section'><h2 class='section-title'>Floor {level.index + 1}</h2>");

            // A single-room floor has no overview - its one room's own sheet (with the room name centred in it,
            // like an overview would) is the whole story, so there is nothing to add before it.
            if (level.overview != null)
            {
                html.Append("<div class='container'><h3>").Append(Enc(level.overview.title)).Append(" - floor plan</h3><p class='sub'>")
                    .Append(Enc(level.overview.subtitle)).Append("</p>");
                AppendLegend(html);
                FloorPlanSvg.Write(html, level.overview);
                html.Append("</div>");
            }

            foreach (var rp in level.rooms)
            {
                var r = rp.room;
                var walls = FloorPlanBuilder.Walls(r);
                html.Append("<div class='container'><h3>Room: ").Append(Enc(r.name)).Append("</h3>");
                html.Append("<div class='badge-container'>")
                    .Append($"<div class='badge'>AREA: {FloorPlanBuilder.Fmt(r.Area)} m&sup2;</div>")
                    .Append($"<div class='badge'>PERIMETER: {FloorPlanBuilder.Fmt(FloorPlanBuilder.Perimeter(r))} m</div>")
                    .Append($"<div class='badge'>CEILING: {Enc(FloorPlanBuilder.CeilingText(r))}</div>")
                    .Append($"<div class='badge'>OPENINGS: {r.openings.Count}</div></div>");
                AppendLegend(html);
                FloorPlanSvg.Write(html, rp.page);
                AppendTables(html, walls);
                html.Append("</div>");

                // Every wall unfolded flat, with real sill heights, opening heights and (for a sloped ceiling)
                // the true height at each end of each wall - none of which a top-down floor plan can show.
                var elevation = FloorPlanBuilder.BuildElevation(r);
                html.Append("<div class='container'><h3>").Append(Enc(r.name)).Append(" - elevation</h3><p class='sub'>")
                    .Append(Enc(elevation.subtitle)).Append("</p>");
                AppendLegend(html);
                FloorPlanSvg.Write(html, elevation);
                html.Append("</div>");
            }
            html.Append("</div>");
        }
    }

    static void AppendLegend(StringBuilder html)
    {
        html.Append("<div class='legend'><b style='border-color:#2d3748'></b>wall<b style='border-color:#b7791f'></b>door<b style='border-color:#3182ce'></b>window<b style='border-color:#2f855a;border-top-width:2px'></b>whole wall (from outside)<b style='border-color:#805ad5;border-top-width:2px'></b>openings and segments (from inside)</div>");
    }

    static void AppendTables(StringBuilder html, List<(float length, List<PlanOpening> openings)> walls)
    {
        html.Append("<table><thead><tr><th>Wall</th><th>Length (m)</th><th>Openings on this wall (width x height, sill)</th></tr></thead><tbody>");
        for (int i = 0; i < walls.Count; i++)
        {
            var ops = walls[i].openings.OrderBy(o => o.center.x).ThenBy(o => o.center.y).Select(o =>
            {
                string cls = o.kind == OpeningKind.Door ? "type-door" : "type-window";
                string kind = o.kind == OpeningKind.Door ? "Door" : "Window";
                return $"<span class='{cls}'>{kind}</span> {FloorPlanBuilder.Fmt(o.width)} x {FloorPlanBuilder.Fmt(o.height)}, sill {FloorPlanBuilder.Fmt(o.sill)}";
            });
            html.Append($"<tr><td>{i + 1}</td><td>{FloorPlanBuilder.Fmt(walls[i].length)}</td><td>{(walls[i].openings.Count == 0 ? "-" : string.Join("<br>", ops))}</td></tr>");
        }
        html.Append("</tbody></table>");
    }
}
