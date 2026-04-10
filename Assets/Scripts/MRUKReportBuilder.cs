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

public static class MRUKReportBuilder {
    private const string STYLE = @"
        body{font-family:'Segoe UI', Tahoma, Geneva, Verdana, sans-serif;margin:20px;background:#f4f7f6;color:#333;} 
        .floor-section{border-top:4px solid #3498db;margin-top:40px;padding-top:20px;} 
        .container{background:white;padding:25px;border-radius:12px;margin-bottom:25px;box-shadow:0 4px 15px rgba(0,0,0,0.05);position:relative;} 
        .badge-container{display:flex; gap:10px; margin-bottom:15px;}
        .badge{background:#edf2f7; padding:6px 15px; border-radius:20px; font-size:0.9em; font-weight:600; color:#4a5568;}
        .svg-container{width:100%; overflow:hidden; cursor:move; background:#fdfdfd; border:1px solid #e2e8f0; border-radius:8px; position:relative; height:600px; margin-bottom:20px;}
        svg{width:100%; height:100%; background:#fff; display:block; transition: transform 0.1s;} 
        .wall{stroke:#333;stroke-width:0.25;} 
        .door{stroke:#e67e22;stroke-width:0.12;} 
        .window{stroke:#3498db;stroke-width:0.1;} 
        .dim{font-size:0.08px;fill:#e74c3c;font-weight:bold;text-anchor:middle;} 
        table{width:100%;border-collapse:collapse;margin-top:15px; font-size:0.95em;} 
        th,td{border-bottom:1px solid #edf2f7;padding:12px;text-align:left;} 
        th{background:#f8fafc; color:#64748b; font-weight:600; text-transform:uppercase; font-size:0.8em; letter-spacing:0.05em;}
        .type-wall{color:#94a3b8; font-weight:bold;}
        .type-door{color:#c27803; font-weight:bold;}
        .type-window{color:#2563eb; font-weight:bold;}
        .zoom-controls{position:absolute; top:15px; left:15px; z-index:10; display:flex; gap:8px;}
        .zoom-controls button{padding:8px 12px; cursor:pointer; background:white; border:1px solid #e2e8f0; border-radius:6px; font-weight:bold; box-shadow:0 2px 4px rgba(0,0,0,0.05);}
        h1, h2, h3, h4 { color: #1e293b; }
        .room-title { border-left: 4px solid #3498db; padding-left: 15px; margin-bottom: 20px; }
    ";

    private const string SCRIPT = @"
        function initZoom() {
            document.querySelectorAll('.svg-container').forEach(container => {
                const svg = container.querySelector('svg');
                if(!svg) return;
                let scale = 1, x = 0, y = 0, isDragging = false, startX, startY;
                
                container.onwheel = e => {
                    e.preventDefault();
                    const delta = e.deltaY > 0 ? 0.9 : 1.1;
                    scale = Math.min(Math.max(0.1, scale * delta), 10);
                    update();
                };
                
                container.onmousedown = e => { if(e.target.tagName==='BUTTON') return; isDragging = true; startX = e.clientX - x; startY = e.clientY - y; container.style.cursor='grabbing'; };
                window.onmousemove = e => { if (!isDragging) return; x = e.clientX - startX; y = e.clientY - startY; update(); };
                window.onmouseup = () => { isDragging = false; container.style.cursor='move'; };
                
                function update() { svg.style.transform = `translate(${x}px, ${y}px) scale(${scale})`; }
                
                container.querySelector('.btn-in').onclick = () => { scale *= 1.2; update(); };
                container.querySelector('.btn-out').onclick = () => { scale /= 1.2; update(); };
                container.querySelector('.btn-reset').onclick = () => { scale = 1; x = 0; y = 0; update(); };
            });
        }
        window.onload = initZoom;
    ";

    public static string GenerateFullReport(List<MRUKRoom> rooms, bool includeTables, bool showFloorPlan = true, float correctionAngle = 0) {
        StringBuilder html = new StringBuilder();
        html.Append("<html><head><meta charset='UTF-8'><title>Dokumentace nemovitosti</title><style>" + STYLE + "</style></head><body>");
        html.Append("<h1>Dokumentace nemovitosti</h1>");

        var floors = rooms.GroupBy(r => Math.Round(r.transform.position.y / 2.5)).OrderBy(g => g.Key);
        int floorCounter = 0;

        foreach (var floor in floors) {
            html.Append($"<div class='floor-section'><h2>Podlaží {++floorCounter}</h2>");
            
            if (showFloorPlan) {
                html.Append("<div class='container'><h3>Celkový půdorys podlaží</h3>");
                DrawSvg(html, floor.ToList(), correctionAngle);
                html.Append("</div>");
            }

            foreach (var r in floor) {
                float area = 0;
                float perimeter = 0;
                #if META_XR_SDK_INSTALLED
                var floorAnchor = r.Anchors.FirstOrDefault(a => a.Label == MRUKAnchor.SceneLabels.FLOOR && a.PlaneRect.HasValue);
                if (floorAnchor != null) {
                    area = floorAnchor.PlaneRect.Value.width * floorAnchor.PlaneRect.Value.height;
                    perimeter = 2 * (floorAnchor.PlaneRect.Value.width + floorAnchor.PlaneRect.Value.height);
                }
                #endif

                html.Append("<div class='container'>");
                html.Append($"<div class='room-title'><h4>Místnost: {r.name}</h4></div>");
                
                html.Append("<div class='badge-container'>");
                html.Append(string.Format(CultureInfo.InvariantCulture, "<div class='badge'>Plocha: {0:F2} m²</div>", area));
                html.Append(string.Format(CultureInfo.InvariantCulture, "<div class='badge'>Obvod: {0:F2} m</div>", perimeter));
                html.Append("</div>");

                DrawSvg(html, new List<MRUKRoom> { r }, correctionAngle);

                if (includeTables) {
                    html.Append("<table><thead><tr><th>Typ prvku</th><th>Rozměry (Š x V)</th><th>Výška od podlahy</th></tr></thead><tbody>");
                    foreach (var a in r.Anchors.Where(x => x.PlaneRect.HasValue).OrderBy(x => x.Label.ToString())) {
                        string label = a.Label.ToString().ToUpper();
                        string cLabel = "Ostatní";
                        string css = "";
                        if (label.Contains("WALL")) { cLabel = "STĚNA"; css = "type-wall"; }
                        else if (label.Contains("DOOR")) { cLabel = "DVEŘNÍ ZÁRUBEŇ"; css = "type-door"; }
                        else if (label.Contains("WINDOW")) { cLabel = "OKENNÍ RÁM"; css = "type-window"; }
                        else if (label.Contains("FLOOR")) { cLabel = "PODLAHA"; css = "type-wall"; }
                        else if (label.Contains("CEILING")) continue;

                        float elev = a.transform.position.y - r.transform.position.y;
                        html.Append(string.Format(CultureInfo.InvariantCulture, 
                            "<tr><td class='{0}'>{1}</td><td>{2:F2} m x {3:F2} m</td><td>{4}</td></tr>", 
                            css, cLabel, a.PlaneRect.Value.width, a.PlaneRect.Value.height, 
                            (label.Contains("WALL") || label.Contains("FLOOR")) ? "-" : string.Format(CultureInfo.InvariantCulture, "{0:F2} m", elev)));
                    }
                    html.Append("</tbody></table>");
                }
                html.Append("</div>");
            }
            html.Append("</div>");
        }

        html.Append("<script>" + SCRIPT + "</script>");
        html.Append("</body></html>");
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
            float w = a.PlaneRect.Value.width;
            Vector3 p = rot * a.transform.position;
            Vector3 rd = (rot * a.transform.right) * (w / 2f);
            Vector2 p1 = new Vector2(p.x - rd.x, p.z - rd.z);
            Vector2 p2 = new Vector2(p.x + rd.x, p.z + rd.z);
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
        html.Append("<div class='svg-container'>");
        html.Append("<div class='zoom-controls'><button class='btn-in'>+</button><button class='btn-out'>-</button><button class='btn-reset'>Reset</button></div>");
        html.Append(string.Format(CultureInfo.InvariantCulture, "<svg viewBox='{0:F2} {1:F2} {2:F2} {3:F2}'>", minX - pad, -(maxZ + pad), (maxX - minX) + pad * 2, (maxZ - minZ) + pad * 2));
        foreach (var i in items.Where(x => x.l.Contains("WALL")))
            html.Append(string.Format(CultureInfo.InvariantCulture, "<line x1='{0:F3}' y1='{1:F3}' x2='{2:F3}' y2='{3:F3}' class='wall' />", i.p1.x, -i.p1.y, i.p2.x, -i.p2.y));
        foreach (var i in items.Where(x => !x.l.Contains("WALL"))) {
            html.Append(string.Format(CultureInfo.InvariantCulture, "<line x1='{0:F3}' y1='{1:F3}' x2='{2:F3}' y2='{3:F3}' stroke='#fff' stroke-width='0.27' />", i.p1.x, -i.p1.y, i.p2.x, -i.p2.y));
            string c = i.l.Contains("DOOR") ? "#e67e22" : "#3498db";
            html.Append(string.Format(CultureInfo.InvariantCulture, "<line x1='{0:F3}' y1='{1:F3}' x2='{2:F3}' y2='{3:F3}' stroke='{4}' stroke-width='0.12' />", i.p1.x, -i.p1.y, i.p2.x, -i.p2.y, c));
        }
        foreach (var i in items) {
            Vector2 mid = (i.p1 + i.p2) / 2f;
            Vector2 tP = mid + i.inv * 0.22f;
            html.Append(string.Format(CultureInfo.InvariantCulture, "<text x='{0:F3}' y='{1:F3}' class='dim'>{2:F2}m</text>", tP.x, -tP.y, i.w));
        }
        html.Append("</svg></div>");
    }
}
