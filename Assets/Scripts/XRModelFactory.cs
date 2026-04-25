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
                rm.parts.Add(CreateBoxPart("Floor", f.transform.position, f.transform.rotation, new Vector3(f.PlaneRect.Value.width, f.PlaneRect.Value.height, 0.05f), "Floor", center, rotation, new Vector3(0,0,-0.025f)));

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
                            if (!isHole) rm.parts.Add(CreateBoxPart("WallSeg", w.transform.TransformPoint(new Vector3(midX, midY, 0)), w.transform.rotation, new Vector3(x2-x1, y2-y1, 0.25f), "Wall", center, rotation));
                        }
                    }
                } else rm.parts.Add(CreateBoxPart("Wall", w.transform.position, w.transform.rotation, new Vector3(wW, wH, 0.25f), "Wall", center, rotation));
            }
            foreach (var o in openings) {
                bool isD = o.Label.ToString().Contains("DOOR");
                rm.parts.Add(CreateBoxPart(o.Label.ToString(), o.transform.position, o.transform.rotation, new Vector3(o.PlaneRect.Value.width, o.PlaneRect.Value.height, isD ? 0.10f : 0.12f), isD ? "Door" : "Window", center, rotation));
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
                rm.parts.Add(await CreateMeshPart("GlobalMesh", mesh, room.GlobalMeshAnchor.Anchor, "Wall", center, rotation));
            else foreach (var a in room.Anchors.Where(x => x.Label.ToString().ToUpper().Contains("WALL") || x.Label == MRUKAnchor.SceneLabels.FLOOR))
                if (a.Anchor.TryGetComponent<OVRTriangleMesh>(out var tm)) rm.parts.Add(await CreateMeshPart(a.Label.ToString(), tm, a.Anchor, a.Label == MRUKAnchor.SceneLabels.FLOOR ? "Floor" : "Wall", center, rotation));
            foreach (var o in room.Anchors.Where(a => a.PlaneRect.HasValue && (a.Label.ToString().Contains("DOOR") || a.Label.ToString().Contains("WINDOW"))))
                rm.parts.Add(CreateBoxPart(o.Label.ToString(), o.transform.position, o.transform.rotation, new Vector3(o.PlaneRect.Value.width, o.PlaneRect.Value.height, 0.08f), o.Label.ToString().Contains("DOOR") ? "Door" : "Window", center, rotation));
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
            if (!uniqueOpenings.Any(existing => Vector3.Distance(o.transform.position, existing.transform.position) < 0.1f))
                uniqueOpenings.Add(o);
        }

        foreach (var room in rooms) {
            var rm = new XRRoomModel { roomName = MRUKDataProcessor.GetRoomLabel(room) };
            var floor = room.FloorAnchors.FirstOrDefault(); if (floor == null || floor.PlaneBoundary2D == null) continue;
            Vector3 pos = floor.transform.position; if (Mathf.Abs(pos.y - baseY) < 0.3f) pos.y = baseY;
            Quaternion rot = floor.transform.rotation; float h = room.CeilingAnchors.Count > 0 ? Mathf.Abs(room.CeilingAnchors[0].transform.position.y - pos.y) : 2.5f;
            
            var fp = new XRMeshPart { name = "Floor", materialName = "Floor", color = new Color(0.75f, 0.75f, 0.78f) };
            var cp = new XRMeshPart { name = "Ceiling", materialName = "Wall", color = new Color(0.42f, 0.42f, 0.45f) };
            foreach (var p in floor.PlaneBoundary2D) {
                fp.vertices.Add(gRot * (pos + rot * new Vector3(p.x, p.y, 0) - center));
                cp.vertices.Add(gRot * (pos + rot * new Vector3(p.x, p.y, h) - center));
            }
            int c = floor.PlaneBoundary2D.Count;
            for (int i=0; i<c-2; i++) { fp.triangles.AddRange(new[] { 0, i+1, i+2 }); cp.triangles.AddRange(new[] { 0, i+2, i+1 }); }
            rm.parts.Add(fp); rm.parts.Add(cp);

            for (int i=0; i<c; i++) {
                Vector2 p1 = floor.PlaneBoundary2D[i], p2 = floor.PlaneBoundary2D[(i+1)%c];
                Vector3 wP1 = pos + rot * new Vector3(p1.x, p1.y, 0);
                Vector3 wP2 = pos + rot * new Vector3(p2.x, p2.y, 0);
                float segLen = Vector3.Distance(wP1, wP2);
                if (segLen < 0.05f) continue;
                Vector3 segDir = (wP2 - wP1).normalized;
                Quaternion segRot = Quaternion.LookRotation(Vector3.Cross(segDir, Vector3.up), Vector3.up);
                Vector3 segMid = (wP1 + wP2) / 2f + Vector3.up * (h/2f);

                var holes = uniqueOpenings.Where(o => {
                    Vector3 lp = Quaternion.Inverse(segRot) * (o.transform.position - segMid);
                    return Mathf.Abs(lp.z) < 0.25f && Mathf.Abs(lp.x) < (segLen/2f + 0.1f);
                }).ToList();

                if (holes.Count > 0) {
                    var xC = new List<float> { -segLen/2f, segLen/2f };
                    var yC = new List<float> { -h/2f, h/2f };
                    foreach(var ho in holes) {
                        Vector3 lp = Quaternion.Inverse(segRot) * (ho.transform.position - segMid);
                        float hw = ho.PlaneRect.Value.width/2f, hh = ho.PlaneRect.Value.height/2f;
                        xC.Add(Mathf.Clamp(lp.x - hw, -segLen/2f, segLen/2f)); xC.Add(Mathf.Clamp(lp.x + hw, -segLen/2f, segLen/2f));
                        yC.Add(Mathf.Clamp(lp.y - hh, -h/2f, h/2f)); yC.Add(Mathf.Clamp(lp.y + hh, -h/2f, h/2f));
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
                                return midX > lp.x-hw+0.002f && midX < lp.x+hw-0.002f && midY > lp.y-hh+0.002f && midY < lp.y+hh-0.002f;
                            });
                            if (!isHole) rm.parts.Add(CreateBoxPart("WallSeg", segMid + segRot * new Vector3(midX, midY, 0), segRot, new Vector3(x2-x1, y2-y1, 0.15f), "Wall", center, rotation));
                        }
                    }
                } else rm.parts.Add(CreateBoxPart("Wall", segMid, segRot, new Vector3(segLen, h, 0.15f), "Wall", center, rotation));
            }
            foreach (var o in room.Anchors.Where(a => a.Label.ToString().Contains("DOOR") || a.Label.ToString().Contains("WINDOW"))) {
                bool isD = o.Label.ToString().Contains("DOOR");
                rm.parts.Add(CreateBoxPart(o.Label.ToString(), o.transform.position, o.transform.rotation, new Vector3(o.PlaneRect.Value.width, o.PlaneRect.Value.height, isD ? 0.08f : 0.10f), isD ? "Door" : "Window", center, rotation));
            }
            model.rooms.Add(rm);
        }
        return model;
    }

    public static async Task<XRHouseModel> CreateRawScan(List<MRUKRoom> rooms, float rotation, Vector3 center)
    {
        var model = new XRHouseModel { center = center, globalRotation = rotation };
        foreach (var room in rooms) {
            var rm = new XRRoomModel { roomName = MRUKDataProcessor.GetRoomLabel(room) };
            if (room.GlobalMeshAnchor != null && room.GlobalMeshAnchor.Anchor.TryGetComponent<OVRTriangleMesh>(out var mesh))
                rm.parts.Add(await CreateMeshPart("Raw", mesh, room.GlobalMeshAnchor.Anchor, "Wall", center, rotation));
            else foreach (var a in room.Anchors) if (a != null && a.Anchor.TryGetComponent<OVRTriangleMesh>(out var tm))
                rm.parts.Add(await CreateMeshPart(a.Label.ToString(), tm, a.Anchor, "Wall", center, rotation));
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
            0,3,2, 0,2,1, // Front
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

    private static Color GetColorForMaterial(string mat) => mat.ToLower() switch { "wall"=>new Color(.42f,.42f,.45f), "floor"=>new Color(.75f,.75f,.78f), "door"=>new Color(.55f,.35f,.18f), "window"=>new Color(.25f,.60f,.92f), _=>Color.white };
}