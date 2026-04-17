using System.Collections.Generic;
using System.Text;
using System.Linq;
using System.Globalization;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

public static class MRUKReportSVGHelper
{
    public static void DrawSvg(StringBuilder html, List<MRUKRoom> rooms, float correctionAngle = 0)
    {
        float minX = 1000, maxX = -1000, minZ = 1000, maxZ = -1000;
        Quaternion rot = Quaternion.Euler(0, correctionAngle, 0);

        var allAnchors = rooms.SelectMany(r => r.Anchors).ToList();
        
        var rawOpenings = allAnchors.Where(a => (a.Label.ToString().Contains("DOOR") || a.Label.ToString().Contains("WINDOW")) && a.PlaneRect.HasValue).ToList();
        var openings = new List<MRUKAnchor>();
        foreach (var o in rawOpenings) {
            if (!openings.Any(existing => Vector3.Distance(o.transform.position, existing.transform.position) < 0.25f)) {
                openings.Add(o);
            }
        }
            
        var walls = allAnchors.Where(a => a.Label.ToString().Contains("WALL") && a.PlaneRect.HasValue).ToList();
        var wallSegments = new List<(Vector2 p1, Vector2 p2, float w)>();
        var openingItems = new List<(string l, Vector2 p1, Vector2 p2, float w, Vector2 inv)>();

        foreach (var w in walls) {
            float wW = w.PlaneRect.Value.width;
            Vector3 wallPos = rot * w.transform.position;
            Vector3 worldRight = w.transform.right;
            Vector3 flatRight = new Vector3(worldRight.x, 0, worldRight.z).normalized;
            if (flatRight.sqrMagnitude < 0.01f) flatRight = Vector3.forward;
            Vector3 rotatedRight = rot * flatRight;

            var wallHoles = openings.Where(o => {
                Vector3 lp = w.transform.InverseTransformPoint(o.transform.position);
                return Mathf.Abs(lp.z) < 0.25f && Mathf.Abs(lp.x) < (wW / 2f + 0.1f);
            }).OrderBy(o => w.transform.InverseTransformPoint(o.transform.position).x).ToList();

            if (wallHoles.Count > 0) {
                float currentX = -wW / 2f;
                foreach (var h in wallHoles) {
                    Vector3 lp = w.transform.InverseTransformPoint(h.transform.position);
                    float hHalfW = h.PlaneRect.Value.width / 2f;
                    float startX = lp.x - hHalfW;
                    float endX = lp.x + hHalfW;

                    if (startX > currentX + 0.02f) {
                        Vector3 s1 = w.transform.TransformPoint(new Vector3(currentX, 0, 0));
                        Vector3 s2 = w.transform.TransformPoint(new Vector3(startX, 0, 0));
                        Vector3 rs1 = rot * s1; Vector3 rs2 = rot * s2;
                        wallSegments.Add((new Vector2(rs1.x, rs1.z), new Vector2(rs2.x, rs2.z), startX - currentX));
                    }
                    currentX = Mathf.Max(currentX, endX);
                }
                if (currentX < (wW / 2f) - 0.02f) {
                    Vector3 s1 = w.transform.TransformPoint(new Vector3(currentX, 0, 0));
                    Vector3 s2 = w.transform.TransformPoint(new Vector3(wW / 2f, 0, 0));
                    Vector3 rs1 = rot * s1; Vector3 rs2 = rot * s2;
                    wallSegments.Add((new Vector2(rs1.x, rs1.z), new Vector2(rs2.x, rs2.z), (wW / 2f) - currentX));
                }
            } else {
                Vector3 rd = rotatedRight * (wW / 2f);
                Vector2 p1 = new Vector2(wallPos.x - rd.x, wallPos.z - rd.z);
                Vector2 p2 = new Vector2(wallPos.x + rd.x, wallPos.z + rd.z);
                wallSegments.Add((p1, p2, wW));
            }
        }

        foreach (var o in openings) {
            string l = o.Label.ToString().ToUpper();
            float w = o.PlaneRect.Value.width; Vector3 p = rot * o.transform.position;
            Vector3 worldRight = o.transform.right;
            Vector3 flatRight = new Vector3(worldRight.x, 0, worldRight.z).normalized;
            if (flatRight.sqrMagnitude < 0.01f) flatRight = Vector3.forward;
            Vector3 rd = (rot * flatRight) * (w / 2f);
            Vector2 p1 = new Vector2(p.x - rd.x, p.z - rd.z); Vector2 p2 = new Vector2(p.x + rd.x, p.z + rd.z);
            Vector3 worldFwd = o.transform.forward;
            Vector3 flatFwd = new Vector3(worldFwd.x, 0, worldFwd.z).normalized;
            Vector3 inv3 = rot * flatFwd;
            openingItems.Add((l, p1, p2, w, new Vector2(inv3.x, inv3.z).normalized));
        }

        foreach (var s in wallSegments) {
            minX = Mathf.Min(minX, s.p1.x, s.p2.x); maxX = Mathf.Max(maxX, s.p1.x, s.p2.x);
            minZ = Mathf.Min(minZ, s.p1.y, s.p2.y); maxZ = Mathf.Max(maxZ, s.p1.y, s.p2.y);
        }
        foreach (var o in openingItems) {
            minX = Mathf.Min(minX, o.p1.x, o.p2.x); maxX = Mathf.Max(maxX, o.p1.x, o.p2.x);
            minZ = Mathf.Min(minZ, o.p1.y, o.p2.y); maxZ = Mathf.Max(maxZ, o.p1.y, o.p2.y);
        }

        if (wallSegments.Count == 0 && openingItems.Count == 0) {
            html.Append("<div class='svg-container' style='display:flex;align-items:center;justify-content:center;'>No floor plan data available</div>");
            return;
        }

        float pad = 1.0f;
        html.Append("<div class='svg-container'><div class='zoom-controls'><button class='btn-in'>+</button><button class='btn-out'>-</button><button class='btn-reset'>↺</button></div>");
        html.Append(string.Format(CultureInfo.InvariantCulture, "<svg viewBox='{0:F2} {1:F2} {2:F2} {3:F2}' style='transition: transform 0.1s;'>", minX - pad, -(maxZ + pad), (maxX - minX) + pad * 2, (maxZ - minZ) + pad * 2));
        
        foreach (var s in wallSegments)
            html.Append(string.Format(CultureInfo.InvariantCulture, "<line x1='{0:F3}' y1='{1:F3}' x2='{2:F3}' y2='{3:F3}' class='wall' />", s.p1.x, -s.p1.y, s.p2.x, -s.p2.y));
        
        foreach (var i in openingItems) {
            string c = i.l.Contains("DOOR") ? "#d69e2e" : "#3182ce";
            html.Append(string.Format(CultureInfo.InvariantCulture, "<line x1='{0:F3}' y1='{1:F3}' x2='{2:F3}' y2='{3:F3}' stroke='{4}' stroke-width='0.15' />", i.p1.x, -i.p1.y, i.p2.x, -i.p2.y, c));
        }
        
        foreach (var s in wallSegments) {
            if (s.w < 0.4f) continue;
            Vector2 mid = (s.p1 + s.p2) / 2f;
            html.Append(string.Format(CultureInfo.InvariantCulture, "<text x='{0:F3}' y='{1:F3}' class='dim'>{2:F2}m</text>", mid.x, -mid.y - 0.1f, s.w));
        }
        foreach (var i in openingItems) {
            Vector2 mid = (i.p1 + i.p2) / 2f; Vector2 tP = mid + i.inv * 0.22f;
            html.Append(string.Format(CultureInfo.InvariantCulture, "<text x='{0:F3}' y='{1:F3}' class='dim'>{2:F2}m</text>", tP.x, -tP.y, i.w));
        }
        html.Append("</svg></div>");
    }
}
