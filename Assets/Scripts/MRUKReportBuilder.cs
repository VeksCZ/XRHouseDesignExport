using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

public static class MRUKReportBuilder
{
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
               
                container.onmousedown = e => { 
                    if(e.target.tagName==='BUTTON') return; 
                    isDragging = true; 
                    startX = e.clientX - x; 
                    startY = e.clientY - y; 
                    container.style.cursor='grabbing'; 
                };
                window.onmousemove = e => { 
                    if (!isDragging) return; 
                    x = e.clientX - startX; 
                    y = e.clientY - startY; 
                    update(); 
                };
                window.onmouseup = () => { 
                    isDragging = false; 
                    container.style.cursor='move'; 
                };
               
                function update() { 
                    svg.style.transform = `translate(${x}px, ${y}px) scale(${scale})`; 
                }
               
                container.querySelector('.btn-in').onclick = () => { scale *= 1.2; update(); };
                container.querySelector('.btn-out').onclick = () => { scale /= 1.2; update(); };
                container.querySelector('.btn-reset').onclick = () => { scale = 1; x = 0; y = 0; update(); };
            });
        }
        window.onload = initZoom;
    ";

    public static string GenerateFullReport(List<MRUKRoom> rooms, bool includeTables = true, bool showFloorPlan = true, float correctionAngle = 0f)
    {
        var html = new StringBuilder();
        html.Append("<html><head><meta charset='UTF-8'><title>Dokumentace nemovitosti</title><style>")
            .Append(STYLE)
            .Append("</style></head><body>");

        html.Append("<h1>Dokumentace nemovitosti</h1>");

        // Group by floors (approximate)
        var floors = rooms.GroupBy(r => Math.Round(r.transform.position.y / 2.5f))
                          .OrderBy(g => g.Key);

        int floorCounter = 0;

        foreach (var floor in floors)
        {
            html.Append($"<div class='floor-section'><h2>Podlaží {++floorCounter}</h2>");

            if (showFloorPlan && floor.Any())
            {
                html.Append("<div class='container'><h3>Celkový půdorys podlaží</h3>");
                DrawSvg(html, floor.ToList(), correctionAngle);
                html.Append("</div>");
            }

            foreach (var room in floor)
            {
                float area = 0f;
                float perimeter = 0f;

#if META_XR_SDK_INSTALLED
                var floorAnchor = room.Anchors.FirstOrDefault(a => 
                    a.Label == MRUKAnchor.SceneLabels.FLOOR && a.PlaneRect.HasValue);

                if (floorAnchor != null)
                {
                    area = floorAnchor.PlaneRect.Value.width * floorAnchor.PlaneRect.Value.height;
                    perimeter = 2 * (floorAnchor.PlaneRect.Value.width + floorAnchor.PlaneRect.Value.height);
                }
#endif

                html.Append("<div class='container'>");
                html.Append($"<div class='room-title'><h4>Místnost: {MRUKDataProcessor.GetRoomLabel(room)}</h4></div>");

                // Badges
                html.Append("<div class='badge-container'>");
                html.Append($"<div class='badge'>Plocha: {area:F2} m²</div>");
                html.Append($"<div class='badge'>Obvod: {perimeter:F2} m</div>");
                html.Append("</div>");

                // Room floor plan
                DrawSvg(html, new List<MRUKRoom> { room }, correctionAngle);

                // Table with anchors
                if (includeTables)
                {
                    html.Append("<table><thead><tr><th>Typ prvku</th><th>Rozměry (Š × V)</th><th>Výška od podlahy</th></tr></thead><tbody>");

                    foreach (var anchor in room.Anchors.Where(a => a.PlaneRect.HasValue)
                                                       .OrderBy(a => a.Label.ToString()))
                    {
                        string labelStr = anchor.Label.ToString().ToUpperInvariant();
                        string displayLabel = "Ostatní";
                        string cssClass = "";

                        if (labelStr.Contains("WALL"))      { displayLabel = "STĚNA";      cssClass = "type-wall"; }
                        else if (labelStr.Contains("DOOR")) { displayLabel = "DVEŘNÍ ZÁRUBEŇ"; cssClass = "type-door"; }
                        else if (labelStr.Contains("WINDOW")) { displayLabel = "OKENNÍ RÁM"; cssClass = "type-window"; }
                        else if (labelStr.Contains("FLOOR")) { displayLabel = "PODLAHA";    cssClass = "type-wall"; }
                        else if (labelStr.Contains("CEILING")) continue;

                        float elevation = anchor.transform.position.y - room.transform.position.y;

                        html.Append(string.Format(CultureInfo.InvariantCulture,
                            "<tr><td class='{0}'>{1}</td><td>{2:F2} × {3:F2} m</td><td>{4}</td></tr>",
                            cssClass,
                            displayLabel,
                            anchor.PlaneRect.Value.width,
                            anchor.PlaneRect.Value.height,
                            (labelStr.Contains("WALL") || labelStr.Contains("FLOOR")) ? "-" : $"{elevation:F2} m"
                        ));
                    }

                    html.Append("</tbody></table>");
                }

                html.Append("</div>");
            }

            html.Append("</div>");
        }

        html.Append("<script>").Append(SCRIPT).Append("</script>");
        html.Append("</body></html>");

        return html.ToString();
    }

    private static void DrawSvg(StringBuilder html, List<MRUKRoom> rooms, float correctionAngle = 0f)
    {
        float minX = 1000f, maxX = -1000f, minZ = 1000f, maxZ = -1000f;
        var items = new List<(string label, Vector2 p1, Vector2 p2, float width, Vector2 direction)>();

        Quaternion rot = Quaternion.Euler(0, correctionAngle, 0);

        var allAnchors = rooms.SelectMany(r => r.Anchors).ToList();

        foreach (var anchor in allAnchors.Where(a => a.PlaneRect.HasValue))
        {
            string labelUpper = anchor.Label.ToString().ToUpperInvariant();
            if (!labelUpper.Contains("WALL") && !labelUpper.Contains("DOOR") && !labelUpper.Contains("WINDOW"))
                continue;

            float width = anchor.PlaneRect.Value.width;
            Vector3 pos = rot * anchor.transform.position;
            Vector3 rightDir = (rot * anchor.transform.right) * (width / 2f);

            Vector2 p1 = new Vector2(pos.x - rightDir.x, pos.z - rightDir.z);
            Vector2 p2 = new Vector2(pos.x + rightDir.x, pos.z + rightDir.z);

            minX = Mathf.Min(minX, p1.x, p2.x);
            maxX = Mathf.Max(maxX, p1.x, p2.x);
            minZ = Mathf.Min(minZ, p1.y, p2.y);
            maxZ = Mathf.Max(maxZ, p1.y, p2.y);

            Vector3 forward = rot * anchor.transform.forward;
            Vector2 direction = new Vector2(forward.x, forward.z).normalized;

            items.Add((labelUpper, p1, p2, width, direction));
        }

        if (items.Count == 0)
        {
            html.Append("<div class='svg-container' style='display:flex;align-items:center;justify-content:center;'>Žádná data pro půdorys</div>");
            return;
        }

        float padding = 1.0f;
        html.Append("<div class='svg-container'>");
        html.Append("<div class='zoom-controls'><button class='btn-in'>+</button><button class='btn-out'>-</button><button class='btn-reset'>Reset</button></div>");

        html.Append(string.Format(CultureInfo.InvariantCulture,
            "<svg viewBox='{0:F2} {1:F2} {2:F2} {3:F2}'>",
            minX - padding, -(maxZ + padding),
            (maxX - minX) + padding * 2,
            (maxZ - minZ) + padding * 2));

        // Walls
        foreach (var item in items.Where(i => i.label.Contains("WALL")))
        {
            html.Append(string.Format(CultureInfo.InvariantCulture,
                "<line x1='{0:F3}' y1='{1:F3}' x2='{2:F3}' y2='{3:F3}' class='wall' />",
                item.p1.x, -item.p1.y, item.p2.x, -item.p2.y));
        }

        // Doors & Windows
        foreach (var item in items.Where(i => !i.label.Contains("WALL")))
        {
            string color = item.label.Contains("DOOR") ? "#e67e22" : "#3498db";
            html.Append(string.Format(CultureInfo.InvariantCulture,
                "<line x1='{0:F3}' y1='{1:F3}' x2='{2:F3}' y2='{3:F3}' stroke='#fff' stroke-width='0.27' />", 
                item.p1.x, -item.p1.y, item.p2.x, -item.p2.y));
            html.Append(string.Format(CultureInfo.InvariantCulture,
                "<line x1='{0:F3}' y1='{1:F3}' x2='{2:F3}' y2='{3:F3}' stroke='{4}' stroke-width='0.12' />",
                item.p1.x, -item.p1.y, item.p2.x, -item.p2.y, color));
        }

        // Dimensions
        foreach (var item in items)
        {
            Vector2 mid = (item.p1 + item.p2) / 2f;
            Vector2 textPos = mid + item.direction * 0.22f;

            html.Append(string.Format(CultureInfo.InvariantCulture,
                "<text x='{0:F3}' y='{1:F3}' class='dim'>{2:F2}m</text>",
                textPos.x, -textPos.y, item.width));
        }

        html.Append("</svg></div>");
    }
}
