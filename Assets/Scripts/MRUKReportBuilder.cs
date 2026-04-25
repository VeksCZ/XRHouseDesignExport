using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.Globalization;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

public static class MRUKReportBuilder
{
    private const string STYLE = @"
        body{font-family:'Segoe UI', Tahoma, Geneva, Verdana, sans-serif;margin:0;background:#f0f2f5;color:#1a202c;} 
        .header{background:#2d3748; color:white; padding:40px 20px; text-align:center; box-shadow:0 4px 6px rgba(0,0,0,0.1);}
        .content{max-width:1200px; margin:auto; padding:20px;}
        .floor-section{margin-bottom:60px;} 
        .section-title{border-bottom:3px solid #3182ce; padding-bottom:10px; margin:40px 0 20px;}
        .container{background:white;padding:30px;border-radius:15px;margin-bottom:30px;box-shadow:0 10px 25px rgba(0,0,0,0.05);position:relative; transition: transform 0.2s;} 
        .container:hover{transform: translateY(-5px);}
        .badge-container{display:flex; gap:12px; margin-bottom:20px;}
        .badge{background:#e2e8f0; padding:8px 18px; border-radius:30px; font-size:0.85em; font-weight:700; color:#2d3748; letter-spacing:0.5px;}
        .svg-container{width:100%; overflow:hidden; cursor:move; background:#f7fafc; border:2px solid #edf2f7; border-radius:12px; position:relative; height:650px; margin-bottom:25px;}
        svg{width:100%; height:100%; background:#fff; display:block;} 
        .wall{stroke:#2d3748;stroke-width:0.3; stroke-linecap:round;} 
        .door{stroke:#d69e2e;stroke-width:0.15; stroke-dasharray: 0.1 0.1;} 
        .window{stroke:#3182ce;stroke-width:0.12;} 
        .dim{font-size:0.07px;fill:#e53e3e;font-weight:800;text-anchor:middle;} 
        table{width:100%;border-collapse:separate; border-spacing:0; margin-top:20px; border-radius:10px; overflow:hidden; border:1px solid #e2e8f0;} 
        th,td{padding:16px;text-align:left; border-bottom:1px solid #edf2f7;} 
        th{background:#edf2f7; color:#4a5568; font-weight:700; font-size:0.75em; text-transform:uppercase; letter-spacing:1px;}
        tr:last-child td{border-bottom:none;}
        .type-wall{color:#718096;} .type-door{color:#b7791f;} .type-window{color:#3182ce;}
        .zoom-controls{position:absolute; top:20px; left:20px; z-index:10; display:flex; flex-direction:column; gap:8px;}
        .zoom-controls button{width:40px; height:40px; cursor:pointer; background:white; border:none; border-radius:8px; font-weight:bold; box-shadow:0 4px 6px rgba(0,0,0,0.1); font-size:1.2em; color:#2d3748;}
        .zoom-controls button:hover{background:#edf2f7;}
    ";

    public static string GenerateFullReport(List<MRUKRoom> rooms, bool includeTables, bool showFloorPlan = true, float correctionAngle = 0)
    {
        StringBuilder html = new StringBuilder();
        html.Append("<html><head><meta charset='UTF-8'><title>Property Report</title><style>" + STYLE + "</style></head><body>");
        html.Append("<div class='header'><h1>Property Documentation</h1><p>Automated Export from Quest XR</p></div>");
        html.Append("<div class='content'>");

        var floors = rooms.GroupBy(r => Math.Round(r.transform.position.y / 2.5)).OrderBy(g => g.Key);
        int fIdx = 0;

        foreach (var floor in floors)
        {
            html.Append($"<div class='floor-section'><h2 class='section-title'>Floor {++fIdx}</h2>");
            if (showFloorPlan)
            {
                html.Append("<div class='container'><h3>Floor Plan</h3>");
                MRUKReportSVGHelper.DrawSvg(html, floor.ToList(), correctionAngle);
                html.Append("</div>");
            }

            foreach (var r in floor)
            {
                float area = 0, perimeter = 0;
#if META_XR_SDK_INSTALLED
                var fAnchor = r.Anchors.FirstOrDefault(a => a.Label == MRUKAnchor.SceneLabels.FLOOR && a.PlaneRect.HasValue);
                if (fAnchor != null)
                {
                    area = fAnchor.PlaneRect.Value.width * fAnchor.PlaneRect.Value.height;
                    perimeter = 2 * (fAnchor.PlaneRect.Value.width + fAnchor.PlaneRect.Value.height);
                }
#endif
                string label = MRUKDataProcessor.GetRoomLabel(r);
                html.Append("<div class='container'><h3>Room: " + label + "</h3>");
                html.Append($"<div class='badge-container'><div class='badge'>AREA: {area:F2} m²</div><div class='badge'>PERIMETER: {perimeter:F2} m</div><div class='badge'>GUID: {r.Anchor.Uuid.ToString().Substring(0, 8)}</div></div>");
                MRUKReportSVGHelper.DrawSvg(html, new List<MRUKRoom> { r }, correctionAngle);

                if (includeTables)
                {
                    AppendRoomTable(html, r);
                }
                html.Append("</div>");
            }
            html.Append("</div>");
        }

        html.Append("</div><script>document.querySelectorAll('.svg-container').forEach(c=>{const s=c.querySelector('svg');let sc=1,x=0,y=0,d=false,sx,sy;c.onwheel=e=>{e.preventDefault();sc=Math.min(Math.max(0.1,sc*(e.deltaY>0?0.9:1.1)),10);u()};c.onmousedown=e=>{if(e.target.tagName=='BUTTON')return;d=true;sx=e.clientX-x;sy=e.clientY-y};window.onmousemove=e=>{if(d){x=e.clientX-sx;y=e.clientY-sy;u()}};window.onmouseup=()=>d=false;function u(){s.style.transform=`translate(${x}px,${y}px) scale(${sc})`};c.querySelector('.btn-in').onclick=()=>{sc*=1.2;u()};c.querySelector('.btn-out').onclick=()=>{sc/=1.2;u()};c.querySelector('.btn-reset').onclick=()=>{sc=1;x=0;y=0;u()}})</script></body></html>");
        return html.ToString();
    }

    private static void AppendRoomTable(StringBuilder html, MRUKRoom r)
    {
        html.Append("<table><thead><tr><th>Type</th><th>Dimensions (W x H)</th><th>Elevation</th></tr></thead><tbody>");
        foreach (var a in r.Anchors.Where(x => x.PlaneRect.HasValue).OrderBy(x => x.Label.ToString()))
        {
            string l = a.Label.ToString().ToUpper();
            if (l.Contains("CEILING")) continue;
            string cL = "Other", css = "";
            if (l.Contains("WALL")) { cL = "WALL"; css = "type-wall"; }
            else if (l.Contains("DOOR")) { cL = "DOOR"; css = "type-door"; }
            else if (l.Contains("WINDOW")) { cL = "WINDOW"; css = "type-window"; }
            else if (l.Contains("FLOOR")) { cL = "FLOOR"; css = "type-wall"; }

            float elev = a.transform.position.y - r.transform.position.y;
            html.Append(string.Format(CultureInfo.InvariantCulture, "<tr><td class='{0}'>{1}</td><td>{2:F2}m x {3:F2}m</td><td>{4}</td></tr>", css, cL, a.PlaneRect.Value.width, a.PlaneRect.Value.height, (l.Contains("WALL") || l.Contains("FLOOR")) ? "-" : elev.ToString("F2") + "m"));
        }
        html.Append("</tbody></table>");
    }
}
