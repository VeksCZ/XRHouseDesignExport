using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using System.Globalization;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

public static class MRUKReportBuilderV2 {
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

    public static string GenerateFullReport(List<MRUKRoom> rooms, bool includeTables, bool showFloorPlan = true, float correctionAngle = 0) {
        StringBuilder html = new StringBuilder();
        html.Append("<html><head><meta charset='UTF-8'><title>Report V2</title><style>" + STYLE + "</style></head><body>");
        html.Append("<div class='header'><h1>Dokumentace objektu v2.0</h1><p>Automatizovaný export z Quest XR</p></div>");
        html.Append("<div class='content'>");

        var floors = rooms.GroupBy(r => Math.Round(r.transform.position.y / 2.5)).OrderBy(g => g.Key);
        int fIdx = 0;

        foreach (var floor in floors) {
            html.Append($"<div class='floor-section'><h2 class='section-title'>Podlaží {++fIdx}</h2>");
            if (showFloorPlan) {
                html.Append("<div class='container'><h3>Komplexní půdorys</h3>");
                DrawSvg(html, floor.ToList(), correctionAngle);
                html.Append("</div>");
            }

            foreach (var r in floor) {
                float area = 0, perimeter = 0;
                #if META_XR_SDK_INSTALLED
                var fAnchor = r.Anchors.FirstOrDefault(a => a.Label == MRUKAnchor.SceneLabels.FLOOR && a.PlaneRect.HasValue);
                if (fAnchor != null) {
                    area = fAnchor.PlaneRect.Value.width * fAnchor.PlaneRect.Value.height;
                    perimeter = 2 * (fAnchor.PlaneRect.Value.width + fAnchor.PlaneRect.Value.height);
                }
                #endif

                html.Append("<div class='container'><h3>Pokoj: " + r.name + "</h3>");
                html.Append($"<div class='badge-container'><div class='badge'>PLOCHA: {area:F2} m²</div><div class='badge'>OBVOD: {perimeter:F2} m</div></div>");
                DrawSvg(html, new List<MRUKRoom> { r }, correctionAngle);

                if (includeTables) {
                    html.Append("<table><thead><tr><th>Typ</th><th>Rozměr (Š x V)</th><th>Výška</th></tr></thead><tbody>");
                    foreach (var a in r.Anchors.Where(x => x.PlaneRect.HasValue).OrderBy(x => x.Label.ToString())) {
                        string l = a.Label.ToString().ToUpper();
                        if (l.Contains("CEILING")) continue;
                        string cL = "Ostatní", css = "";
                        if (l.Contains("WALL")) { cL = "STĚNA"; css = "type-wall"; }
                        else if (l.Contains("DOOR")) { cL = "DVEŘE"; css = "type-door"; }
                        else if (l.Contains("WINDOW")) { cL = "OKNO"; css = "type-window"; }
                        else if (l.Contains("FLOOR")) { cL = "PODLAHA"; css = "type-wall"; }
                        
                        float elev = a.transform.position.y - r.transform.position.y;
                        html.Append(string.Format(CultureInfo.InvariantCulture, "<tr><td class='{0}'>{1}</td><td>{2:F2}m x {3:F2}m</td><td>{4}</td></tr>", css, cL, a.PlaneRect.Value.width, a.PlaneRect.Value.height, (l.Contains("WALL") || l.Contains("FLOOR")) ? "-" : elev.ToString("F2") + "m"));
                    }
                    html.Append("</tbody></table>");
                }
                html.Append("</div>");
            }
            html.Append("</div>");
        }

        html.Append("</div><script>document.querySelectorAll('.svg-container').forEach(c=>{const s=c.querySelector('svg');let sc=1,x=0,y=0,d=false,sx,sy;c.onwheel=e=>{e.preventDefault();sc=Math.min(Math.max(0.1,sc*(e.deltaY>0?0.9:1.1)),10);u()};c.onmousedown=e=>{if(e.target.tagName=='BUTTON')return;d=true;sx=e.clientX-x;sy=e.clientY-y};window.onmousemove=e=>{if(d){x=e.clientX-sx;y=e.clientY-sy;u()}};window.onmouseup=()=>d=false;function u(){s.style.transform=`translate(${x}px,${y}px) scale(${sc})`};c.querySelector('.btn-in').onclick=()=>{sc*=1.2;u()};c.querySelector('.btn-out').onclick=()=>{sc/=1.2;u()};c.querySelector('.btn-reset').onclick=()=>{sc=1;x=0;y=0;u()}})</script></body></html>");
        return html.ToString();
    }

    private static void DrawSvg(StringBuilder html, List<MRUKRoom> rooms, float correctionAngle = 0) {
        float minX = 1000, maxX = -1000, minZ = 1000, maxZ = -1000;
        var items = new List<(string l, Vector2 p1, Vector2 p2, float w, Vector2 inv)>();
        Quaternion rot = Quaternion.Euler(0, correctionAngle, 0);

        // NO DEDUPLICATION - Get all anchors to ensure complete floor plan
        var allAnchors = rooms.SelectMany(r => r.Anchors).ToList();

        foreach (var a in allAnchors.Where(x => x.PlaneRect.HasValue)) {
            string l = a.Label.ToString().ToUpper();
            if (!l.Contains("WALL") && !l.Contains("DOOR") && !l.Contains("WINDOW")) continue;
            float w = a.PlaneRect.Value.width; Vector3 p = rot * a.transform.position; 
            Vector3 rd = (rot * a.transform.right) * (w / 2f);
            Vector2 p1 = new Vector2(p.x - rd.x, p.z - rd.z); Vector2 p2 = new Vector2(p.x + rd.x, p.z + rd.z);
            minX = Mathf.Min(minX, p1.x, p2.x); maxX = Mathf.Max(maxX, p1.x, p2.x);
            minZ = Mathf.Min(minZ, p1.y, p2.y); maxZ = Mathf.Max(maxZ, p1.y, p2.y);
            Vector3 inv3 = rot * a.transform.forward;
            items.Add((l, p1, p2, w, new Vector2(inv3.x, inv3.z).normalized));
        }

        if (items.Count == 0) {
            html.Append("<div class='svg-container' style='display:flex;align-items:center;justify-content:center;'>Žádná data pro půdorys</div>");
            return;
        }

        float pad = 1.0f;
        html.Append("<div class='svg-container'><div class='zoom-controls'><button class='btn-in'>+</button><button class='btn-out'>-</button><button class='btn-reset'>↺</button></div>");
        html.Append(string.Format(CultureInfo.InvariantCulture, "<svg viewBox='{0:F2} {1:F2} {2:F2} {3:F2}' style='transition: transform 0.1s;'>", minX - pad, -(maxZ + pad), (maxX - minX) + pad * 2, (maxZ - minZ) + pad * 2));
        foreach (var i in items.Where(x => x.l.Contains("WALL")))
            html.Append(string.Format(CultureInfo.InvariantCulture, "<line x1='{0:F3}' y1='{1:F3}' x2='{2:F3}' y2='{3:F3}' class='wall' />", i.p1.x, -i.p1.y, i.p2.x, -i.p2.y));
        foreach (var i in items.Where(x => !x.l.Contains("WALL"))) {
            html.Append(string.Format(CultureInfo.InvariantCulture, "<line x1='{0:F3}' y1='{1:F3}' x2='{2:F3}' y2='{3:F3}' stroke='#fff' stroke-width='0.3' />", i.p1.x, -i.p1.y, i.p2.x, -i.p2.y));
            string c = i.l.Contains("DOOR") ? "#d69e2e" : "#3182ce";
            html.Append(string.Format(CultureInfo.InvariantCulture, "<line x1='{0:F3}' y1='{1:F3}' x2='{2:F3}' y2='{3:F3}' stroke='{4}' stroke-width='0.15' />", i.p1.x, -i.p1.y, i.p2.x, -i.p2.y, c));
        }
        foreach (var i in items) {
            Vector2 mid = (i.p1 + i.p2) / 2f; Vector2 tP = mid + i.inv * 0.22f;
            html.Append(string.Format(CultureInfo.InvariantCulture, "<text x='{0:F3}' y='{1:F3}' class='dim'>{2:F2}m</text>", tP.x, -tP.y, i.w));
        }
        html.Append("</svg></div>");
    }
}
