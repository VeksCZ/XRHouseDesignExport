using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Unity.Collections;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
using Meta.XR;
#endif

public static class XRModelFactory
{
    public static XRHouseModel CreateAnchorAnalytical(List<MRUKRoom> rooms, float rotation, Vector3 center)
    {
        var model = new XRHouseModel { center = center, globalRotation = rotation };
        foreach (var room in rooms) {
            var rm = new XRRoomModel { roomName = MRUKDataProcessor.GetRoomLabel(room) };
            var anchors = room.Anchors.Where(a => a != null && a.PlaneRect.HasValue).ToList();
            var openings = anchors.Where(a => a.Label.ToString().Contains("DOOR") || a.Label.ToString().Contains("WINDOW")).ToList();
            
            foreach (var f in anchors.Where(a => a.Label == MRUKAnchor.SceneLabels.FLOOR))
                rm.parts.Add(CreateBoxPart("FLOOR", f.transform.position, f.transform.rotation, new Vector3(f.PlaneRect.Value.width, f.PlaneRect.Value.height, 0.05f), "FLOOR", center, rotation, new Vector3(0,0,-0.025f)));

            foreach (var w in anchors.Where(a => a.Label.ToString().ToUpper().Contains("WALL"))) {
                float wW = w.PlaneRect.Value.width, wH = w.PlaneRect.Value.height;
                var wallHoles = openings.Where(o => {
                    Vector3 lp = w.transform.InverseTransformPoint(o.transform.position);
                    return Mathf.Abs(lp.z) < 0.25f && Mathf.Abs(lp.x) < (wW / 2f + 0.1f) && Mathf.Abs(lp.y) < (wH / 2f + 0.1f);
                }).ToList();
                
                if (wallHoles.Count > 0) {
                    var xC = new List<float> { -wW/2f, wW/2f };
                    var yC = new List<float> { -wH/2f, wH/2f };
                    foreach(var h in wallHoles) {
                        Vector3 lp = w.transform.InverseTransformPoint(h.transform.position);
                        float hw = h.PlaneRect.Value.width/2f, hh = h.PlaneRect.Value.height/2f;
                        xC.Add(Mathf.Clamp(lp.x - hw, -wW/2f, wW/2f)); xC.Add(Mathf.Clamp(lp.x + hw, -wW/2f, wW/2f));
                        yC.Add(Mathf.Clamp(lp.y - hh, -wH/2f, wH/2f)); yC.Add(Mathf.Clamp(lp.y + hh, -wH/2f, wH/2f));
                    }
                    var sX = xC.Distinct().OrderBy(x => x).ToList();
                    var sY = yC.Distinct().OrderBy(y => y).ToList();
                    for (int i=0; i<sX.Count-1; i++) {
                        for (int j=0; j<sY.Count-1; j++) {
                            float x1=sX[i], x2=sX[i+1], y1=sY[j], y2=sY[j+1];
                            if (x2-x1 < 0.01f || y2-y1 < 0.01f) continue;
                            float midX = (x1+x2)/2f, midY = (y1+y2)/2f;
                            bool isHole = wallHoles.Any(h => {
                                Vector3 lp = w.transform.InverseTransformPoint(h.transform.position);
                                float hw = h.PlaneRect.Value.width/2f, hh = h.PlaneRect.Value.height/2f;
                                return midX > lp.x-hw+0.01f && midX < lp.x+hw-0.01f && midY > lp.y-hh+0.01f && midY < lp.y+hh-0.01f;
                            });
                            if (!isHole) rm.parts.Add(CreateBoxPart("WallSeg", w.transform.TransformPoint(new Vector3(midX, midY, 0)), w.transform.rotation, new Vector3(x2-x1, y2-y1, 0.25f), "WALL", center, rotation));
                        }
                    }
                } else rm.parts.Add(CreateBoxPart("WALL", w.transform.position, w.transform.rotation, new Vector3(wW, wH, 0.25f), "WALL", center, rotation));
            }
            foreach (var o in openings) {
                bool isD = o.Label.ToString().Contains("DOOR");
                rm.parts.Add(CreateBoxPart(o.Label.ToString(), o.transform.position, o.transform.rotation, new Vector3(o.PlaneRect.Value.width, o.PlaneRect.Value.height, isD ? 0.10f : 0.12f), isD ? "DOOR" : "WINDOW", center, rotation));
            }
            model.rooms.Add(rm);
        }
        return model;
    }

    public static async Task<XRHouseModel> CreateMeshAnalytical(List<MRUKRoom> rooms, float rotation, Vector3 center)
    {
        var model = new XRHouseModel { center = center, globalRotation = rotation };
        foreach (var room in rooms) {
            var rm = new XRRoomModel { roomName = MRUKDataProcessor.GetRoomLabel(room) };
            if (room.GlobalMeshAnchor != null && room.GlobalMeshAnchor.Anchor.TryGetComponent<OVRTriangleMesh>(out var mesh))
                rm.parts.Add(await CreateMeshPart("GlobalMesh", mesh, room.GlobalMeshAnchor.Anchor, "WALL", center, rotation));
            else foreach (var a in room.Anchors.Where(x => x.Label.ToString().ToUpper().Contains("WALL") || x.Label == MRUKAnchor.SceneLabels.FLOOR))
                if (a.Anchor.TryGetComponent<OVRTriangleMesh>(out var tm)) rm.parts.Add(await CreateMeshPart(a.Label.ToString(), tm, a.Anchor, a.Label == MRUKAnchor.SceneLabels.FLOOR ? "FLOOR" : "WALL", center, rotation));
            foreach (var o in room.Anchors.Where(a => a.PlaneRect.HasValue && (a.Label.ToString().Contains("DOOR") || a.Label.ToString().Contains("WINDOW"))))
                rm.parts.Add(CreateBoxPart(o.Label.ToString(), o.transform.position, o.transform.rotation, new Vector3(o.PlaneRect.Value.width, o.PlaneRect.Value.height, 0.08f), o.Label.ToString().Contains("DOOR") ? "DOOR" : "WINDOW", center, rotation));
            model.rooms.Add(rm);
        }
        return model;
    }

    public static XRHouseModel CreateReconstruction(List<MRUKRoom> rooms, float rotation, Vector3 center)
    {
        var model = new XRHouseModel { center = center, globalRotation = rotation };
        float baseY = rooms.Count > 0 && rooms[0].FloorAnchors.Count > 0 ? rooms[0].FloorAnchors[0].transform.position.y : 0f;
        Quaternion gRot = Quaternion.Euler(0, rotation, 0);

        // Global deduplicated openings list
        var allOpenings = rooms.SelectMany(r => r.Anchors)
            .Where(a => a != null && (a.Label.ToString().Contains("DOOR") || a.Label.ToString().Contains("WINDOW")))
            .ToList();
        var uniqueOpenings = new List<MRUKAnchor>();
        foreach (var o in allOpenings) {
            if (!uniqueOpenings.Any(existing => Vector3.Distance(o.transform.position, existing.transform.position) < 0.15f))
                uniqueOpenings.Add(o);
        }

        var addedOpenings = new HashSet<Guid>();

        foreach (var room in rooms) {
            var rm = new XRRoomModel { roomName = MRUKDataProcessor.GetRoomLabel(room) };

            // A room can report more than one FloorAnchor (e.g. a split-level room) - build each
            // floor "island" separately instead of only ever looking at FloorAnchors[0].
            var floors = room.FloorAnchors.Where(f => f != null && f.PlaneBoundary2D != null && f.PlaneBoundary2D.Count >= 3).ToList();
            if (floors.Count == 0) continue;

            float fallbackH = room.CeilingAnchors.Count > 0
                ? Mathf.Abs(room.CeilingAnchors[0].transform.position.y - floors[0].transform.position.y)
                : 2.5f;

            int floorIdx = 0;
            foreach (var floor in floors) {
                floorIdx++;
                string tag = floors.Count > 1 ? "_L" + floorIdx : "";

                Vector3 pos = floor.transform.position; if (Mathf.Abs(pos.y - baseY) < 0.3f) pos.y = baseY;
                Quaternion rot = floor.transform.rotation;

                var fp = new XRMeshPart { name = "FLOOR" + tag, materialName = "FLOOR", color = new Color(0.75f, 0.75f, 0.78f) };
                var cp = new XRMeshPart { name = "Ceiling" + tag, materialName = "WALL", color = new Color(0.42f, 0.42f, 0.45f) };

                // Per-vertex ceiling height, sampled from the room's actual ceiling plane(s) so
                // sloped/vaulted ceilings (and rooms with more than one CeilingAnchor) are followed
                // instead of assuming one flat height for the whole room.
                int c = floor.PlaneBoundary2D.Count;
                var ceilY = new float[c];
                for (int i = 0; i < c; i++) {
                    var p = floor.PlaneBoundary2D[i];
                    Vector3 worldPt = pos + rot * new Vector3(p.x, p.y, 0);
                    ceilY[i] = SampleCeilingHeight(room.CeilingAnchors, worldPt, worldPt.y + fallbackH);
                    fp.vertices.Add(gRot * (worldPt - center));
                    cp.vertices.Add(gRot * (new Vector3(worldPt.x, ceilY[i], worldPt.z) - center));
                }
                for (int i = 0; i < c - 2; i++) { fp.triangles.AddRange(new[] { 0, i+1, i+2 }); cp.triangles.AddRange(new[] { 0, i+2, i+1 }); }
                rm.parts.Add(fp); rm.parts.Add(cp);

                for (int i = 0; i < c; i++) {
                    int iNext = (i+1) % c;
                    Vector2 p1 = floor.PlaneBoundary2D[i], p2 = floor.PlaneBoundary2D[iNext];
                    Vector3 wP1 = pos + rot * new Vector3(p1.x, p1.y, 0);
                    Vector3 wP2 = pos + rot * new Vector3(p2.x, p2.y, 0);
                    float segLen = Vector3.Distance(wP1, wP2);
                    if (segLen < 0.05f) continue;

                    // Local (per-corner) wall heights - equal for a flat ceiling, different for a sloped one.
                    float h1 = ceilY[i] - wP1.y;
                    float h2 = ceilY[iNext] - wP2.y;
                    float hMin = Mathf.Max(0.3f, Mathf.Min(h1, h2));
                    float hMax = Mathf.Max(h1, h2);

                    Vector3 segDir = (wP2 - wP1).normalized;
                    Quaternion segRot = Quaternion.LookRotation(Vector3.Cross(segDir, Vector3.up), Vector3.up);
                    Vector3 segMid = (wP1 + wP2) / 2f + Vector3.up * (hMin/2f);

                    var holes = uniqueOpenings.Where(o => {
                        Vector3 lp = Quaternion.Inverse(segRot) * (o.transform.position - segMid);
                        return Mathf.Abs(lp.z) < 0.3f && Mathf.Abs(lp.x) < (segLen/2f + 0.1f);
                    }).ToList();

                    if (holes.Count > 0) {
                        var xC = new List<float> { -segLen/2f, segLen/2f };
                        var yC = new List<float> { -hMin/2f, hMin/2f };
                        foreach(var ho in holes) {
                            Vector3 lp = Quaternion.Inverse(segRot) * (ho.transform.position - segMid);
                            float hw = ho.PlaneRect.Value.width/2f, hh = ho.PlaneRect.Value.height/2f;
                            xC.Add(Mathf.Clamp(lp.x - hw, -segLen/2f, segLen/2f)); xC.Add(Mathf.Clamp(lp.x + hw, -segLen/2f, segLen/2f));
                            yC.Add(Mathf.Clamp(lp.y - hh, -hMin/2f, hMin/2f)); yC.Add(Mathf.Clamp(lp.y + hh, -hMin/2f, hMin/2f));
                        }
                        var sX = xC.Distinct().OrderBy(x => x).ToList();
                        var sY = yC.Distinct().OrderBy(y => y).ToList();
                        for (int ix=0; ix<sX.Count-1; ix++) {
                            for (int iy=0; iy<sY.Count-1; iy++) {
                                float x1=sX[ix], x2=sX[ix+1], y1=sY[iy], y2=sY[iy+1];
                                if (x2-x1 < 0.005f || y2-y1 < 0.005f) continue;
                                float midX = (x1+x2)/2f, midY = (y1+y2)/2f;
                                bool isHole = holes.Any(ho => {
                                    Vector3 lp = Quaternion.Inverse(segRot) * (ho.transform.position - segMid);
                                    float hw = ho.PlaneRect.Value.width/2f, hh = ho.PlaneRect.Value.height/2f;
                                    return midX > lp.x-hw+0.005f && midX < lp.x+hw-0.005f && midY > lp.y-hh+0.005f && midY < lp.y+hh-0.005f;
                                });
                                if (!isHole) rm.parts.Add(CreateBoxPart("WallSeg", segMid + segRot * new Vector3(midX, midY, 0), segRot, new Vector3(x2-x1, y2-y1, 0.15f), "WALL", center, rotation));
                            }
                        }
                    } else if (hMin > 0.02f) {
                        rm.parts.Add(CreateBoxPart("WALL", segMid, segRot, new Vector3(segLen, hMin, 0.15f), "WALL", center, rotation));
                    }

                    // Sloped/vaulted ceiling cap: solid wall material from hMin up to the real
                    // ceiling line at each end of the segment. Zero-height at the low end, so it
                    // degrades to nothing for a flat ceiling (hMax == hMin).
                    if (hMax - hMin > 0.03f) {
                        rm.parts.Add(CreateSlopedCapPart(wP1, wP2, hMin, h1, h2, 0.15f, "WALL", center, rotation));
                    }
                }
            }

            // Deduplicated door/window boxes (once per room, not per floor island)
            foreach (var o in uniqueOpenings) {
                if (addedOpenings.Contains(o.Anchor.Uuid)) continue;
                // Only add if it belongs to this room (exists in room anchors)
                if (room.Anchors.Any(ra => ra.Anchor.Uuid == o.Anchor.Uuid)) {
                    bool isD = o.Label.ToString().Contains("DOOR");
                    rm.parts.Add(CreateBoxPart(o.Label.ToString(), o.transform.position, o.transform.rotation, new Vector3(o.PlaneRect.Value.width, o.PlaneRect.Value.height, isD ? 0.12f : 0.14f), isD ? "DOOR" : "WINDOW", center, rotation));
                    addedOpenings.Add(o.Anchor.Uuid);
                }
            }
            model.rooms.Add(rm);
        }
return model;
    }

    /// <summary>
    /// Finds the ceiling height (world Y) directly above a world-space (X,Z) point, by intersecting
    /// a vertical line through that point with each CeilingAnchor's plane. MRUK can report more than
    /// one ceiling anchor per room for sloped/vaulted/dropped ceilings, so each anchor's own footprint
    /// (PlaneBoundary2D) is checked to find which plane actually covers that point; if none of them
    /// do (e.g. right at a seam), the nearest one is used instead of falling back to a flat guess.
    /// </summary>
    private static float SampleCeilingHeight(IEnumerable<MRUKAnchor> ceilingAnchors, Vector3 probeWorldXZ, float fallback)
    {
        bool haveFallback = false; float fallbackY = fallback; float bestDist = float.MaxValue;

        foreach (var ceil in ceilingAnchors) {
            if (ceil == null || ceil.PlaneBoundary2D == null || ceil.PlaneBoundary2D.Count < 3) continue;

            Quaternion crot = ceil.transform.rotation;
            Vector3 normal = crot * Vector3.forward; // MRUK anchor local +Z = plane normal, pointing into the room
            if (Mathf.Abs(normal.y) < 0.05f) continue; // near-vertical "ceiling" plane, not usable for a height sample

            Vector3 p = ceil.transform.position;
            float y = p.y - (normal.x * (probeWorldXZ.x - p.x) + normal.z * (probeWorldXZ.z - p.z)) / normal.y;

            Vector3 worldOnPlane = new Vector3(probeWorldXZ.x, y, probeWorldXZ.z);
            Vector3 local = Quaternion.Inverse(crot) * (worldOnPlane - p);
            Vector2 uv = new Vector2(local.x, local.y);

            if (IsPointInPolygon(uv, ceil.PlaneBoundary2D)) return y;

            float d = NearestPointInPolygonDistance(uv, ceil.PlaneBoundary2D);
            if (d < bestDist) { bestDist = d; fallbackY = y; haveFallback = true; }
        }
        return haveFallback ? fallbackY : fallback;
    }

    private static bool IsPointInPolygon(Vector2 pt, IReadOnlyList<Vector2> poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++) {
            Vector2 pi = poly[i], pj = poly[j];
            if (((pi.y > pt.y) != (pj.y > pt.y)) &&
                (pt.x < (pj.x - pi.x) * (pt.y - pi.y) / (pj.y - pi.y) + pi.x))
                inside = !inside;
        }
        return inside;
    }

    private static float NearestPointInPolygonDistance(Vector2 pt, IReadOnlyList<Vector2> poly)
    {
        float min = float.MaxValue;
        for (int i = 0; i < poly.Count; i++) min = Mathf.Min(min, Vector2.Distance(pt, poly[i]));
        return min;
    }

    private static XRMeshPart CreateSlopedCapPart(Vector3 wP1, Vector3 wP2, float yBase, float yTop1, float yTop2, float thickness, string mat, Vector3 center, float globalRot)
    {
        var part = new XRMeshPart { name = "WallCap", materialName = mat, color = GetColorForMaterial(mat) };
        Quaternion gRot = Quaternion.Euler(0, globalRot, 0);
        Vector3 dir = (wP2 - wP1).normalized;
        Vector3 halfT = Vector3.Cross(dir, Vector3.up).normalized * (thickness / 2f);

        Vector3 a0 = wP1 + Vector3.up * yBase, a1 = wP2 + Vector3.up * yBase;
        Vector3 b0 = wP1 + Vector3.up * yTop1, b1 = wP2 + Vector3.up * yTop2;

        Vector3[] v = {
            a0 - halfT, a1 - halfT, b1 - halfT, b0 - halfT, // front (0-3)
            a0 + halfT, a1 + halfT, b1 + halfT, b0 + halfT  // back  (4-7)
        };
        int[] t = {
            0,3,2, 0,2,1, // front
            4,5,6, 4,6,7, // back
            1,2,6, 1,6,5, // side at wP2
            0,4,7, 0,7,3, // side at wP1
            3,7,6, 3,6,2, // top (sloped)
            0,1,5, 0,5,4  // bottom
        };
        foreach (var p in v) part.vertices.Add(gRot * (p - center));
        part.triangles.AddRange(t);
        return part;
    }

    public static async Task<XRHouseModel> CreateRawScan(List<MRUKRoom> rooms, float rotation, Vector3 center)
    {
        var model = new XRHouseModel { center = center, globalRotation = rotation };
        foreach (var room in rooms) {
            var rm = new XRRoomModel { roomName = MRUKDataProcessor.GetRoomLabel(room) };
            if (room.GlobalMeshAnchor != null && room.GlobalMeshAnchor.Anchor.TryGetComponent<OVRTriangleMesh>(out var mesh))
                rm.parts.Add(await CreateMeshPart("Raw", mesh, room.GlobalMeshAnchor.Anchor, "WALL", center, rotation));
            else foreach (var a in room.Anchors) if (a != null && a.Anchor.TryGetComponent<OVRTriangleMesh>(out var tm))
                rm.parts.Add(await CreateMeshPart(a.Label.ToString(), tm, a.Anchor, "WALL", center, rotation));
            model.rooms.Add(rm);
        }
        return model;
    }

    private static XRMeshPart CreateBoxPart(string name, Vector3 pos, Quaternion rot, Vector3 size, string mat, Vector3 center, float globalRot, Vector3 localOff = default)
    {
        var part = new XRMeshPart { name = name, materialName = mat, color = GetColorForMaterial(mat) };
        Quaternion gRot = Quaternion.Euler(0, globalRot, 0);
        Vector3[] v = {
            new Vector3(-.5f,-.5f,.5f), new Vector3(.5f,-.5f,.5f), new Vector3(.5f,.5f,.5f), new Vector3(-.5f,.5f,.5f),
            new Vector3(-.5f,-.5f,-.5f), new Vector3(.5f,-.5f,-.5f), new Vector3(.5f,.5f,-.5f), new Vector3(-.5f,.5f,-.5f)
        };
        // FIXED Winding: Unity Clockwise
        int[] t = {
            0, 3, 2, 0, 2, 1, // Front
            4,5,6, 4,6,7, // Back
            1,2,6, 1,6,5, // Right
            0,4,7, 0,7,3, // Left
            3,7,6, 3,6,2, // Top
            0,1,5, 0,5,4  // Bottom
        };
        foreach (var p in v) part.vertices.Add(gRot * (pos + rot * (Vector3.Scale(p, size) + localOff) - center));
        part.triangles.AddRange(t); return part;
    }

    private static async Task<XRMeshPart> CreateMeshPart(string name, OVRTriangleMesh triMesh, OVRAnchor anchor, string mat, Vector3 center, float globalRot)
    {
        var part = new XRMeshPart { name = name, materialName = mat, color = GetColorForMaterial(mat) };
        if (!triMesh.TryGetCounts(out int vCount, out int tCount) || vCount == 0) return part;
        using var vArr = new NativeArray<Vector3>(vCount, Allocator.TempJob);
        using var iArr = new NativeArray<int>(tCount * 3, Allocator.TempJob);
        if (!triMesh.TryGetMesh(vArr, iArr)) return part;
        Matrix4x4 m = Matrix4x4.identity; if (anchor.TryGetComponent<OVRLocatable>(out var loc) && loc.TryGetSceneAnchorPose(out var pose))
            m = Matrix4x4.TRS(pose.Position ?? Vector3.zero, pose.Rotation ?? Quaternion.identity, Vector3.one);
        Quaternion gRot = Quaternion.Euler(0, globalRot, 0);
        for (int i=0; i<vCount; i++) part.vertices.Add(gRot * (m.MultiplyPoint3x4(vArr[i]) - center));
        // Reverse winding if needed? Usually OVRTriangleMesh is already correct for Unity
        part.triangles.AddRange(iArr); await Task.Yield(); return part;
    }

        private static Color GetColorForMaterial(string mat) {
        string m = mat.ToUpperInvariant();
        if (m.Contains("WALL")) return new Color(0.42f, 0.42f, 0.45f);
        if (m.Contains("FLOOR")) return new Color(0.75f, 0.75f, 0.78f);
        if (m.Contains("DOOR")) return new Color(0.55f, 0.35f, 0.18f);
        if (m.Contains("WINDOW")) return new Color(0.25f, 0.60f, 0.92f);
        return Color.white;
        }
        }