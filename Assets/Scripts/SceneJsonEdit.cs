using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

/// <summary>
/// Changes a saved scan's own scene JSON (the file MRUK loads it from), so the change is a real part of the scan:
/// every view, the floor plans and the exports see it like anything the headset scanned. Used to add a doorway the
/// Space Setup missed (e.g. between a living room and its kitchen).
///
/// Two formats exist: V1 (rooms with roomLayoutMETA, every wall/door an entity of its own with a pose) and V2 (the
/// newer room mesh: walls and doors are faces of one roomMeshMETA mesh in the room's local frame, a door's face
/// parented to its wall's face). The JSON's world frame is not Unity's (handedness), so it is worked out from the
/// loaded scan itself: the axis flip that puts the JSON walls onto the loaded walls. The new door is then built from
/// its wall's own JSON geometry, so nothing depends on how MRUK converts frames.
/// The scan file is copied to *.bak_doors before its first change.
/// </summary>
public static class SceneJsonEdit
{
    public const float DefaultDoorHeight = 1.97f;

    // JSON world <-> Unity world: x and/or z mirrored (each one is its own inverse)
    static readonly Vector3[] Flips = { new Vector3(1, 1, 1), new Vector3(1, 1, -1), new Vector3(-1, 1, 1), new Vector3(-1, 1, -1) };

#if META_XR_SDK_INSTALLED
    /// <summary>Adds a door (width x height, standing on the wall's bottom edge) to the saved scan's JSON, centred
    /// where worldPoint is along the wall of 'room' it lies on. Returns null when done, else why not.</summary>
    public static string AddDoor(string scanName, MRUKRoom room, Vector3 worldPoint, float width, float height)
    {
        string path = MRUKSceneCache.ScanPath(scanName);
        if (string.IsNullOrEmpty(scanName) || !File.Exists(path)) return "the saved scan's file is missing";
        if (room == null) return "no room there";
        JObject json;
        try { json = JObject.Parse(File.ReadAllText(path)); }
        catch (Exception e) { return "can't read the scan: " + e.Message; }
        var entities = json["spatialEntities"] as JArray;
        if (entities == null) return "unknown scan format";

        var (ida, idb) = PendingChanges.IdForms(room.Anchor.Uuid);
        var roomEnt = entities.OfType<JObject>().FirstOrDefault(e => Eq((string)e["uuid"], ida) || Eq((string)e["uuid"], idb));
        if (roomEnt == null) return "the room isn't in the scan file";

        var unityWalls = room.WallAnchors.Where(w => w != null).Select(w => w.transform.position).ToList();
        if (unityWalls.Count == 0) return "the room has no walls";

        string err = roomEnt["roomMeshMETA"] is JObject mesh
            ? AddDoorV2(roomEnt, mesh, unityWalls, worldPoint, width, height)
            : AddDoorV1(entities, roomEnt, unityWalls, worldPoint, width, height);
        if (err != null) return err;

        try
        {
            string bak = path + ".bak_doors";
            if (!File.Exists(bak)) File.Copy(path, bak);
            File.WriteAllText(path, json.ToString(Formatting.Indented));
        }
        catch (Exception e) { return "can't write the scan: " + e.Message; }
        Debug.Log($"[SceneJsonEdit] {scanName}: door {width:0.00} x {height:0.00} m added in room {ida.Substring(0, 8)} at {worldPoint}");
        return null;
    }

    static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ---------- V2: a face of the room mesh ----------

    static string AddDoorV2(JObject roomEnt, JObject mesh, List<Vector3> unityWalls, Vector3 worldPoint, float width, float height)
    {
        var (rp, rq) = Pose(roomEnt["locatableMETA"]);
        var verts = Floats3((string)mesh["vertices"]);
        var faces = mesh["faces"] as JArray;
        if (faces == null || verts.Count == 0) return "the room mesh is empty";
        var walls = new List<(JObject face, List<int> idx, Vector3 centroid)>();
        foreach (var f in faces.OfType<JObject>())
        {
            if ((string)f["semanticLabel"] != "WALL_FACE") continue;
            var idx = UInts((string)f["indices"]);
            if (idx.Count < 3 || idx.Any(i => i >= verts.Count)) continue;
            var c = idx.Distinct().Aggregate(Vector3.zero, (s, i) => s + verts[i]) / idx.Distinct().Count();
            walls.Add((f, idx, rp + rq * c));
        }
        if (walls.Count == 0) return "the room mesh has no walls";

        Vector3 flip = BestFlip(walls.Select(w => w.centroid).ToList(), unityWalls, out float err);
        if (err > 0.25f) return $"can't line the scan file up with the loaded scan ({err:0.00} m)";
        Vector3 target = Quaternion.Inverse(rq) * (Vector3.Scale(worldPoint, flip) - rp);   // room-local (JSON)
        Vector3 up = Quaternion.Inverse(rq) * Vector3.up;

        // the wall face whose plane the point is on (and that spans it)
        (JObject face, List<int> idx, Vector3 c)? best = null; float bestD = 0.3f;
        Vector3 bA = default, bD = default, bN = default; float bLen = 0, bBottom = 0, bTop = 0;
        foreach (var w in walls)
        {
            var vs = w.idx.Distinct().Select(i => verts[i]).ToList();
            if (!WallFrame(vs, up, out var a, out var d, out var n, out float len, out float bottom, out float top)) continue;
            float dist = Mathf.Abs(Vector3.Dot(target - a, n));
            float along = Vector3.Dot(target - a, d);
            if (along < -0.05f || along > len + 0.05f || dist >= bestD) continue;
            best = (w.face, w.idx, w.centroid); bestD = dist;
            bA = a; bD = d; bN = n; bLen = len; bBottom = bottom; bTop = top;
        }
        if (best == null) return "point right at a wall of the room";
        if (bLen < width + 0.05f) return $"the wall is only {bLen * 100:0} cm long";
        float h = Mathf.Min(height, bTop - bBottom - 0.03f);
        float t = Mathf.Clamp(Vector3.Dot(target - bA, bD), width / 2f, bLen - width / 2f);
        // corners on the wall's plane, standing on its bottom edge
        Vector3 basePt = bA + bD * t;
        basePt += up * (bBottom - Vector3.Dot(basePt, up));
        Vector3 p0 = basePt - bD * (width / 2f), p1 = basePt + bD * (width / 2f), p2 = p1 + up * h, p3 = p0 + up * h;

        int i0 = verts.Count;
        verts.AddRange(new[] { p0, p1, p2, p3 });
        var tri = new List<int> { i0, i0 + 1, i0 + 2, i0, i0 + 2, i0 + 3 };
        // same winding as the wall's own faces (their normal points into the room)
        var wi = best.Value.idx;
        Vector3 wn = Vector3.Cross(verts[wi[1]] - verts[wi[0]], verts[wi[2]] - verts[wi[0]]);
        Vector3 dn = Vector3.Cross(p1 - p0, p2 - p0);
        if (Vector3.Dot(wn, dn) < 0f) tri = new List<int> { i0, i0 + 2, i0 + 1, i0, i0 + 3, i0 + 2 };

        mesh["vertices"] = ToB64(verts);
        faces.Add(new JObject
        {
            ["uuid"] = Guid.NewGuid().ToString(),
            ["parentUuid"] = (string)best.Value.face["uuid"],
            ["semanticLabel"] = "DOOR_FRAME",
            ["indices"] = ToB64(tri),
        });
        return null;
    }

    /// <summary>A wall face's frame: a = one bottom end, d = along the wall (horizontal), n = its normal, len, and its
    /// bottom/top heights (along 'up').</summary>
    static bool WallFrame(List<Vector3> vs, Vector3 up, out Vector3 a, out Vector3 d, out Vector3 n, out float len, out float bottom, out float top)
    {
        a = d = n = default; len = bottom = top = 0;
        if (vs.Count < 3) return false;
        bottom = vs.Min(v => Vector3.Dot(v, up)); top = vs.Max(v => Vector3.Dot(v, up));
        // the two vertices furthest apart horizontally are the wall's ends
        float bestL = 0; Vector3 e0 = default, e1 = default;
        for (int i = 0; i < vs.Count; i++)
            for (int j = i + 1; j < vs.Count; j++)
            {
                Vector3 h = Vector3.ProjectOnPlane(vs[j] - vs[i], up);
                if (h.magnitude > bestL) { bestL = h.magnitude; e0 = vs[i]; e1 = vs[j]; }
            }
        if (bestL < 0.1f) return false;
        d = Vector3.ProjectOnPlane(e1 - e0, up).normalized;
        a = e0; len = bestL;
        n = Vector3.Cross(up, d).normalized;
        return true;
    }

    // ---------- V1: an entity of its own ----------

    static string AddDoorV1(JArray entities, JObject roomEnt, List<Vector3> unityWalls, Vector3 worldPoint, float width, float height)
    {
        var container = roomEnt["spaceContainerMETA"]?["uuids"] as JArray;
        var layout = roomEnt["roomLayoutMETA"] as JObject;
        if (container == null || layout == null) return "unknown room format";
        var wallIds = new HashSet<string>((layout["wallUuids"] as JArray ?? new JArray()).Select(x => ((string)x).ToLowerInvariant()));
        var walls = entities.OfType<JObject>().Where(e => wallIds.Contains(((string)e["uuid"] ?? "").ToLowerInvariant()) && e["locatableMETA"] != null && e["bounded2DMETA"] != null).ToList();
        if (walls.Count == 0) return "the room has no walls in the file";

        Vector3 flip = BestFlip(walls.Select(w => Pose(w["locatableMETA"]).p).ToList(), unityWalls, out float err);
        if (err > 0.25f) return $"can't line the scan file up with the loaded scan ({err:0.00} m)";
        Vector3 target = Vector3.Scale(worldPoint, flip);

        JObject best = null; float bestD = 0.3f; Vector3 wp = default; Quaternion wq = default; float ox = 0, oy = 0, ew = 0, eh = 0;
        foreach (var w in walls)
        {
            var (p, q) = Pose(w["locatableMETA"]);
            var b = w["bounded2DMETA"];
            float x0 = (float)b["offset"]["x"], y0 = (float)b["offset"]["y"], bw = (float)b["extent"]["width"], bh = (float)b["extent"]["height"];
            Vector3 l = Quaternion.Inverse(q) * (target - p);
            if (Mathf.Abs(l.z) >= bestD || l.x < x0 - 0.05f || l.x > x0 + bw + 0.05f) continue;
            best = w; bestD = Mathf.Abs(l.z); wp = p; wq = q; ox = x0; oy = y0; ew = bw; eh = bh;
        }
        if (best == null) return "point right at a wall of the room";
        if (ew < width + 0.05f) return $"the wall is only {ew * 100:0} cm long";
        float h = Mathf.Min(height, eh - 0.03f);
        Vector3 lt = Quaternion.Inverse(wq) * (target - wp);
        float cx = Mathf.Clamp(lt.x, ox + width / 2f, ox + ew - width / 2f);
        Vector3 center = wp + wq * new Vector3(cx, oy + h / 2f, 0f);   // the wall's local y is up, z its normal

        string id = Guid.NewGuid().ToString();
        entities.Add(new JObject
        {
            ["uuid"] = id,
            ["semanticLabelMETA"] = "DOOR_FRAME",
            ["locatableMETA"] = new JObject
            {
                ["orientation"] = new JObject { ["x"] = wq.x, ["y"] = wq.y, ["z"] = wq.z, ["w"] = wq.w },
                ["position"] = new JObject { ["x"] = center.x, ["y"] = center.y, ["z"] = center.z },
            },
            ["boundary2DMETA"] = new JObject
            {
                ["vertices"] = ToB64Floats(new[] { -width / 2f, -h / 2f, width / 2f, -h / 2f, width / 2f, h / 2f, -width / 2f, h / 2f }),
            },
            ["bounded2DMETA"] = new JObject
            {
                ["offset"] = new JObject { ["x"] = -width / 2f, ["y"] = -h / 2f },
                ["extent"] = new JObject { ["width"] = width, ["height"] = h },
            },
        });
        container.Add(id);
        return null;
    }
#endif

    // ---------- helpers ----------

    /// <summary>The axis flip (JSON world -> Unity world) that puts the JSON walls closest onto the loaded ones.</summary>
    static Vector3 BestFlip(List<Vector3> jsonWalls, List<Vector3> unityWalls, out float err)
    {
        err = float.MaxValue; Vector3 best = Vector3.one;
        foreach (var f in Flips)
        {
            float sum = 0;
            foreach (var u in unityWalls) sum += jsonWalls.Min(j => Vector3.Distance(Vector3.Scale(j, f), u));
            float e = sum / unityWalls.Count;
            if (e < err) { err = e; best = f; }
        }
        return best;
    }

    static (Vector3 p, Quaternion q) Pose(JToken loc)
    {
        var p = loc?["position"]; var o = loc?["orientation"];
        if (p == null || o == null) return (Vector3.zero, Quaternion.identity);
        return (new Vector3((float)p["x"], (float)p["y"], (float)p["z"]), new Quaternion((float)o["x"], (float)o["y"], (float)o["z"], (float)o["w"]));
    }

    static List<Vector3> Floats3(string b64)
    {
        var res = new List<Vector3>();
        if (string.IsNullOrEmpty(b64)) return res;
        var b = Convert.FromBase64String(b64);
        for (int i = 0; i + 12 <= b.Length; i += 12)
            res.Add(new Vector3(BitConverter.ToSingle(b, i), BitConverter.ToSingle(b, i + 4), BitConverter.ToSingle(b, i + 8)));
        return res;
    }

    static List<int> UInts(string b64)
    {
        var res = new List<int>();
        if (string.IsNullOrEmpty(b64)) return res;
        var b = Convert.FromBase64String(b64);
        for (int i = 0; i + 4 <= b.Length; i += 4) res.Add((int)BitConverter.ToUInt32(b, i));
        return res;
    }

    static string ToB64(List<Vector3> v)
    {
        var b = new byte[v.Count * 12];
        for (int i = 0; i < v.Count; i++)
        {
            Buffer.BlockCopy(BitConverter.GetBytes(v[i].x), 0, b, i * 12, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(v[i].y), 0, b, i * 12 + 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(v[i].z), 0, b, i * 12 + 8, 4);
        }
        return Convert.ToBase64String(b);
    }

    static string ToB64(List<int> idx)
    {
        var b = new byte[idx.Count * 4];
        for (int i = 0; i < idx.Count; i++) Buffer.BlockCopy(BitConverter.GetBytes((uint)idx[i]), 0, b, i * 4, 4);
        return Convert.ToBase64String(b);
    }

    static string ToB64Floats(float[] f)
    {
        var b = new byte[f.Length * 4];
        Buffer.BlockCopy(f, 0, b, 0, b.Length);
        return Convert.ToBase64String(b);
    }
}
