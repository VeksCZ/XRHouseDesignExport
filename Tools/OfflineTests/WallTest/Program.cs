using System; using System.Collections.Generic; using System.IO; using System.Linq;
using Newtonsoft.Json; using Newtonsoft.Json.Linq; using UnityEngine; using Meta.XR.MRUtilityKit;

class T {
  static int fails = 0;
  static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fails++; }
  static bool Near(Vector3 a, Vector3 b, float e = 1e-3f) => (a - b).magnitude < e;
  static Quaternion FromBasis(Vector3 x, Vector3 y, Vector3 z) {
    float m00 = x.x, m01 = y.x, m02 = z.x, m10 = x.y, m11 = y.y, m12 = z.y, m20 = x.z, m21 = y.z, m22 = z.z;
    float tr = m00 + m11 + m22, qw, qx, qy, qz;
    if (tr > 0) { float s = 0.5f / Mathf.Sqrt(tr + 1f); qw = 0.25f / s; qx = (m21 - m12) * s; qy = (m02 - m20) * s; qz = (m10 - m01) * s; }
    else if (m00 > m11 && m00 > m22) { float s = 2f * Mathf.Sqrt(1f + m00 - m11 - m22); qw = (m21 - m12) / s; qx = 0.25f * s; qy = (m01 + m10) / s; qz = (m02 + m20) / s; }
    else if (m11 > m22) { float s = 2f * Mathf.Sqrt(1f + m11 - m00 - m22); qw = (m02 - m20) / s; qx = (m01 + m10) / s; qy = 0.25f * s; qz = (m12 + m21) / s; }
    else { float s = 2f * Mathf.Sqrt(1f + m22 - m00 - m11); qw = (m10 - m01) / s; qx = (m02 + m20) / s; qy = (m12 + m21) / s; qz = 0.25f * s; }
    return new Quaternion(qx, qy, qz, qw); }

  // Unity-world truth: a 4 x 3 m room, 2.5 m high, the wall along z = 3 is missing.
  static readonly Vector3 Flip = new Vector3(1, 1, -1);   // JSON world = Unity world with z mirrored
  const float H = 2.5f;
  static readonly (Vector3 a, Vector3 b)[] Walls = {
    (new Vector3(0, 0, 0), new Vector3(4, 0, 0)),   // south
    (new Vector3(4, 0, 0), new Vector3(4, 0, 3)),   // east
    (new Vector3(0, 0, 3), new Vector3(0, 0, 0)),   // west     (north z=3 missing)
  };

  static MRUKRoom UnityRoom(Guid id) {
    var r = new MRUKRoom(); r.Anchor.Uuid = id;
    foreach (var (a, b) in Walls) {
      var an = new MRUKAnchor(); Vector3 dir = (b - a).normalized; float len = (b - a).magnitude;
      an.transform.position = (a + b) / 2f + Vector3.up * (H / 2f);
      an.transform.rotation = FromBasis(dir, Vector3.up, Vector3.Cross(dir, Vector3.up));
      an.PlaneRect = new Rect(-len / 2f, -H / 2f, len, H);
      r.WallAnchors.Add(an);
    }
    return r; }

  static JObject V3(Vector3 v) => new JObject { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z };
  static JObject Q4(Quaternion q) => new JObject { ["x"] = q.x, ["y"] = q.y, ["z"] = q.z, ["w"] = q.w };

  static string B64(IEnumerable<Vector3> v) { var l = new List<byte>(); foreach (var p in v) { l.AddRange(BitConverter.GetBytes(p.x)); l.AddRange(BitConverter.GetBytes(p.y)); l.AddRange(BitConverter.GetBytes(p.z)); } return Convert.ToBase64String(l.ToArray()); }
  static string B64(IEnumerable<int> v) { var l = new List<byte>(); foreach (var i in v) l.AddRange(BitConverter.GetBytes((uint)i)); return Convert.ToBase64String(l.ToArray()); }
  static List<Vector3> Floats3(string b) { var r = new List<Vector3>(); var d = Convert.FromBase64String(b); for (int i = 0; i + 12 <= d.Length; i += 12) r.Add(new Vector3(BitConverter.ToSingle(d, i), BitConverter.ToSingle(d, i + 4), BitConverter.ToSingle(d, i + 8))); return r; }
  static List<int> UInts(string b) { var r = new List<int>(); var d = Convert.FromBase64String(b); for (int i = 0; i + 4 <= d.Length; i += 4) r.Add((int)BitConverter.ToUInt32(d, i)); return r; }

  static JObject BuildV1(string roomId, out List<string> wallIds) {
    var entities = new JArray(); wallIds = new List<string>(); var container = new JArray();
    Vector3 center = new Vector3(2, 0, 1.5f);
    foreach (var (a, b) in Walls) {
      string id = Guid.NewGuid().ToString(); wallIds.Add(id); container.Add(id);
      Vector3 ja = Vector3.Scale(a, Flip), jb = Vector3.Scale(b, Flip), jc = Vector3.Scale(center, Flip);
      Vector3 mid = (ja + jb) / 2f; float len = (jb - ja).magnitude;
      Vector3 zIn = (jc - mid); zIn.y = 0; zIn = zIn.normalized;                       // the wall's z points into the room
      Vector3 x = Vector3.Cross(Vector3.up, zIn).normalized;                           // so that z = x cross y (right-handed)
      var q = FromBasis(x, Vector3.up, Vector3.Cross(x, Vector3.up));
      entities.Add(new JObject { ["uuid"] = id, ["semanticLabelMETA"] = "WALL_FACE",
        ["locatableMETA"] = new JObject { ["orientation"] = Q4(q), ["position"] = V3(mid + Vector3.up * (H / 2f)) },
        ["bounded2DMETA"] = new JObject { ["offset"] = new JObject { ["x"] = -len / 2f, ["y"] = -H / 2f }, ["extent"] = new JObject { ["width"] = len, ["height"] = H } } });
    }
    entities.Add(new JObject { ["uuid"] = roomId, ["spaceContainerMETA"] = new JObject { ["uuids"] = container },
      ["roomLayoutMETA"] = new JObject { ["wallUuids"] = new JArray(wallIds.Select(w => (JToken)w)) } });
    return new JObject { ["spatialEntities"] = entities }; }

  static JObject BuildV2(string roomId, Vector3 rp, Quaternion rq) {
    var verts = new List<Vector3>(); var faces = new JArray(); Vector3 center = Vector3.Scale(new Vector3(2, 0, 1.5f), Flip);
    Vector3 ToLocal(Vector3 jw) => Quaternion.Inverse(rq) * (jw - rp);
    Vector3 roomC = ToLocal(center);
    foreach (var (a, b) in Walls) {
      Vector3 ja = Vector3.Scale(a, Flip), jb = Vector3.Scale(b, Flip);
      var q = new[] { ToLocal(ja), ToLocal(jb), ToLocal(jb + Vector3.up * H), ToLocal(ja + Vector3.up * H) };
      int i0 = verts.Count; verts.AddRange(q);
      var tri = new List<int> { i0, i0 + 1, i0 + 2, i0, i0 + 2, i0 + 3 };
      Vector3 n = Vector3.Cross(q[1] - q[0], q[2] - q[0]); Vector3 mid = (q[0] + q[1] + q[2] + q[3]) / 4f;
      if (Vector3.Dot(n, roomC - mid) < 0) tri = new List<int> { i0, i0 + 2, i0 + 1, i0, i0 + 3, i0 + 2 };   // winding: normal into the room
      faces.Add(new JObject { ["uuid"] = Guid.NewGuid().ToString(), ["semanticLabel"] = "WALL_FACE", ["parentUuid"] = "room", ["indices"] = B64(tri) });
    }
    var room = new JObject { ["uuid"] = roomId, ["locatableMETA"] = new JObject { ["orientation"] = Q4(rq), ["position"] = V3(rp) },
      ["roomMeshMETA"] = new JObject { ["vertices"] = B64(verts), ["faces"] = faces } };
    return new JObject { ["spatialEntities"] = new JArray(room) }; }

  static int Main() {
    var gid = Guid.NewGuid(); var (ida, idb) = PendingChanges.IdForms(gid);
    var room = UnityRoom(gid);

    // ---- gap finder ----
    var found = MissingWall.Find(new List<MRUKRoom> { room }, new Ray(new Vector3(2, 1.5f, 1.5f), new Vector3(0, 0, 1)), out var fr, out var p, out var q, out float h);
    Check(found, "MissingWall.Find finds the gap");
    Check(found && Near(new Vector3(Mathf.Min(p.x, q.x), 0, p.z), new Vector3(0, 0, 3)) && Near(new Vector3(Mathf.Max(p.x, q.x), 0, q.z), new Vector3(4, 0, 3)), $"gap runs between (0,3) and (4,3): p={p} q={q}");
    Check(found && Math.Abs(h - H) < 1e-3f, $"gap height = wall height ({h})");
    var none = MissingWall.Find(new List<MRUKRoom> { room }, new Ray(new Vector3(2, 1.5f, 1.5f), new Vector3(0, 0, -1)), out _, out _, out _, out _);
    Check(!none, "pointing away from the gap (behind the hand) finds nothing");

    // ---- V1 ----
    {
      string scan = "t_v1"; var json = BuildV1(ida, out var wallIds);
      File.WriteAllText(MRUKSceneCache.ScanPath(scan), json.ToString());
      string err = SceneJsonEdit.AddWall(scan, room, p, q, h);
      Check(err == null, "V1 AddWall succeeds (" + err + ")");
      var res = JObject.Parse(File.ReadAllText(MRUKSceneCache.ScanPath(scan)));
      var ents = (JArray)res["spatialEntities"]; var added = ents.OfType<JObject>().Last(e => (string)e["semanticLabelMETA"] == "WALL_FACE" && !wallIds.Contains((string)e["uuid"]));
      Check(added != null, "V1 new WALL_FACE entity exists");
      var pos = new Vector3((float)added["locatableMETA"]["position"]["x"], (float)added["locatableMETA"]["position"]["y"], (float)added["locatableMETA"]["position"]["z"]);
      var rot = new Quaternion((float)added["locatableMETA"]["orientation"]["x"], (float)added["locatableMETA"]["orientation"]["y"], (float)added["locatableMETA"]["orientation"]["z"], (float)added["locatableMETA"]["orientation"]["w"]);
      Check(Near(pos, Vector3.Scale(new Vector3(2, H / 2f, 3), Flip)), $"V1 position = middle of the gap in file space {pos}");
      Vector3 ax = rot * Vector3.right, up = rot * Vector3.up, zz = rot * Vector3.forward;
      Check(Math.Abs(Math.Abs(ax.x) - 1f) < 1e-3f && Math.Abs(ax.z) < 1e-3f, $"V1 local x runs along the gap {ax}");
      Check(Near(up, Vector3.up), "V1 local y is up");
      Vector3 roomC = Vector3.Scale(new Vector3(2, 0, 1.5f), Flip);
      float newSide = Vector3.Dot(zz, new Vector3(roomC.x - pos.x, 0, roomC.z - pos.z));
      // the file's own walls
      var ref0 = ents.OfType<JObject>().First(e => (string)e["uuid"] == wallIds[0]);
      var rq0 = new Quaternion((float)ref0["locatableMETA"]["orientation"]["x"], (float)ref0["locatableMETA"]["orientation"]["y"], (float)ref0["locatableMETA"]["orientation"]["z"], (float)ref0["locatableMETA"]["orientation"]["w"]);
      var rp0 = new Vector3((float)ref0["locatableMETA"]["position"]["x"], 0, (float)ref0["locatableMETA"]["position"]["z"]);
      float refSide = Vector3.Dot(rq0 * Vector3.forward, new Vector3(roomC.x - rp0.x, 0, roomC.z - rp0.z));
      Check(newSide * refSide > 0, $"V1 normal faces the same way as the file's walls (new {newSide:0.00}, ref {refSide:0.00})");
      var order = ((JArray)res["spatialEntities"].OfType<JObject>().First(e => (string)e["uuid"] == ida)["roomLayoutMETA"]["wallUuids"]).Select(x => (string)x).ToList();
      string newId = (string)added["uuid"];
      Check(order.Count == 4 && order.IndexOf(newId) == 2, $"V1 wall list order: new wall between east and west (index {order.IndexOf(newId)} of {order.Count})");
      Check(File.Exists(MRUKSceneCache.ScanPath(scan) + ".bak_doors"), "V1 backup written");
    }

    // ---- V2 (room pose not identity) ----
    {
      string scan = "t_v2"; var rq = Quaternion.AngleAxis(37f, Vector3.up); var rp = new Vector3(1.5f, 0.2f, -2f);
      var json = BuildV2(ida, rp, rq);
      File.WriteAllText(MRUKSceneCache.ScanPath(scan), json.ToString());
      string err = SceneJsonEdit.AddWall(scan, room, p, q, h);
      Check(err == null, "V2 AddWall succeeds (" + err + ")");
      var res = JObject.Parse(File.ReadAllText(MRUKSceneCache.ScanPath(scan)));
      var mesh = (JObject)res["spatialEntities"][0]["roomMeshMETA"]; var verts = Floats3((string)mesh["vertices"]); var faces = (JArray)mesh["faces"];
      Check(faces.Count == 4 && verts.Count == 16, $"V2 4 faces, 16 vertices ({faces.Count}/{verts.Count})");
      Vector3 ToLocal(Vector3 jw) => Quaternion.Inverse(rq) * (jw - rp);
      Vector3 roomC = ToLocal(Vector3.Scale(new Vector3(2, 0, 1.5f), Flip));
      float Side(JObject f) { var idx = UInts((string)f["indices"]); var d = idx.Distinct().Select(i => verts[i]).ToList(); var c = d.Aggregate(Vector3.zero, (s, v) => s + v) / d.Count;
        return Vector3.Dot(Vector3.Cross(verts[idx[1]] - verts[idx[0]], verts[idx[2]] - verts[idx[0]]), roomC - c); }
      var nf = (JObject)faces[3];
      Check(nf != null && (string)nf["semanticLabel"] == "WALL_FACE" && (string)nf["parentUuid"] == "room", "V2 new face is a WALL_FACE with the file's own properties");
      Check(Side((JObject)faces[0]) * Side(nf) > 0, $"V2 new face winds like the file's walls (new {Side(nf):0.0}, ref {Side((JObject)faces[0]):0.0})");
      var idxN = UInts((string)nf["indices"]).Distinct().Select(i => verts[i]).ToList();
      Vector3 minW = idxN.Select(v => rp + rq * v).Aggregate((a, b) => new Vector3(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Min(a.z, b.z)));
      Vector3 maxW = idxN.Select(v => rp + rq * v).Aggregate((a, b) => new Vector3(Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y), Mathf.Max(a.z, b.z)));
      Check(Near(minW, new Vector3(0, 0, -3)) && Near(maxW, new Vector3(4, H, -3)), $"V2 new wall corners in file space {minW} .. {maxW}");
    }

    // a closed room has no gap
    var closed = UnityRoom(Guid.NewGuid());
    var an = new MRUKAnchor(); an.transform.position = new Vector3(2, H / 2f, 3); an.transform.rotation = FromBasis(new Vector3(-1, 0, 0), Vector3.up, new Vector3(0, 0, -1)); an.PlaneRect = new Rect(-2, -H / 2f, 4, H);
    closed.WallAnchors.Add(an);
    Check(!MissingWall.Find(new List<MRUKRoom> { closed }, new Ray(new Vector3(2, 1.5f, 1.5f), new Vector3(0, 0, 1)), out _, out _, out _, out _), "a closed outline reports no missing wall");

    // ---- stairs snap back onto the floor ----
    {
      var sq = new List<Vector3> { new Vector3(0, 0, 0), new Vector3(4, 0, 0), new Vector3(4, 0, 3), new Vector3(0, 0, 3) };
      var floors = new List<(List<Vector3>, float)> { (sq, -3.137f), (sq, 0f) };                    // two storeys on top of each other
      Check(StairFloorSnap.Offset(new Vector3(1, -3.19f, 1), floors) == 0f, "stair on its floor (6 cm off) is left alone");
      Check(StairFloorSnap.Offset(new Vector3(1, -3.0f, 1), floors) == 0f, "finished floor 14 cm higher is left alone");
      float dy = StairFloorSnap.Offset(new Vector3(1, -6.33f, 1), floors);
      Check(Math.Abs(dy - 3.193f) < 1e-3f, $"stair a storey too low is lifted onto the floor ({dy:0.000} m)");
      float dy2 = StairFloorSnap.Offset(new Vector3(1, 2.9f, 1), floors);
      Check(Math.Abs(dy2 - (-2.9f)) < 1e-3f, $"stair a storey too high comes down to the nearest floor ({dy2:0.000} m)");
      Check(StairFloorSnap.Offset(new Vector3(9, -6.33f, 9), floors) == 0f, "no floor under the spot: nothing to snap to");
      Check(StairFloorSnap.Offset(new Vector3(1, -20f, 1), floors) == 0f, "absurdly far off: left alone");
    }

    // ---- frame culling ----
    {
      // camera at the origin looking along +z, a pinhole with fu = fv = 1 (about 53 degrees), centre of the image (0.5, 0.5)
      Func<Vector3, Vector3> id = v => v;
      Check(FrameCulling.SeesPlane(id, 1f, 1f, .5f, .5f, new Vector3(-2, -1, 3), new Vector3(4, 0, 0), new Vector3(0, 2, 0)), "a wall straight ahead is seen");
      Check(!FrameCulling.SeesPlane(id, 1f, 1f, .5f, .5f, new Vector3(5, -1, 3), new Vector3(4, 0, 0), new Vector3(0, 2, 0)), "a wall far to the side is not");
      Check(!FrameCulling.SeesPlane(id, 1f, 1f, .5f, .5f, new Vector3(-2, -1, -3), new Vector3(4, 0, 0), new Vector3(0, 2, 0)), "a wall behind the camera is not");
      Check(FrameCulling.SeesPlane(id, 1f, 1f, .5f, .5f, new Vector3(-30, -2, 4), new Vector3(60, 0, 0), new Vector3(0, 4, 0)), "a long wall of which only a slice is in view is seen");
      Check(!FrameCulling.SeesPlane(id, 1f, 1f, .5f, .5f, new Vector3(-2, 4, 3), new Vector3(4, 0, 0), new Vector3(0, 2, 0)), "a wall above the view is not");
    }

    // ---- coverage grid ----
    {
      Func<Vector3, Vector3> id = v => v;
      Vector3 cam = Vector3.zero;
      var full = new CoverageGrid(2f, 1f);
      full.Mark(id, 1f, 1f, .5f, .5f, cam, new Vector3(-1, -0.5f, 3), new Vector3(2, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, -1));
      Check(full.Fraction > 0.99f, $"a plane in full view 3 m ahead is covered ({full.Fraction:0.00})");
      var far = new CoverageGrid(2f, 1f);
      far.Mark(id, 1f, 1f, .5f, .5f, cam, new Vector3(-1, -0.5f, 5), new Vector3(2, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, -1));
      Check(far.Fraction == 0f, "5 m away counts for nothing (max 3.5 m)");
      var away = new CoverageGrid(2f, 1f);
      away.Mark(id, 1f, 1f, .5f, .5f, cam, new Vector3(-1, -0.5f, 3), new Vector3(2, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1));
      Check(away.Fraction == 0f, "a plane facing away from the camera is not covered");
      var part = new CoverageGrid(6f, 1f);
      part.Mark(id, 1f, 1f, .5f, .5f, cam, new Vector3(0, -0.5f, 3), new Vector3(6, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, -1));
      Check(part.Fraction > 0.15f && part.Fraction < 0.3f, $"a long wall seen only in part: a fraction of it ({part.Fraction:0.00})");
      float before = part.Fraction; part.Mark(id, 1f, 1f, .5f, .5f, cam, new Vector3(0, -0.5f, 3), new Vector3(6, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, -1));
      Check(part.Fraction == before, "the same frame twice adds nothing");
      var tex = new CoverageGrid(4f, 2f); tex.MarkFromAlpha((u, v) => u < 0.5f ? 1f : 0f);
      Check(Math.Abs(tex.Fraction - 0.5f) < 0.02f, $"from a saved texture: half covered ({tex.Fraction:0.00})");
      tex.Clear(); Check(tex.Seen == 0, "clear");
    }

    // ---- textured GLB of the wall scan ----
    {
      byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAQAAAAECAYAAACp8Z5+AAAAEklEQVR4nGM4UWHzHxkzkC4AAEKnJ7GoAuNuAAAAAElFTkSuQmCC");
      var quads = new List<PhotoQuad> {
        new PhotoQuad { name = "Wall A", p0 = new Vector3(0, 0, 0), p1 = new Vector3(4, 0, 0), p2 = new Vector3(4, 2.5f, 0), p3 = new Vector3(0, 2.5f, 0), png = png },
        new PhotoQuad { name = "Floor \"B\"", p0 = new Vector3(0, 0, 0), p1 = new Vector3(4, 0, 0), p2 = new Vector3(4, 0, 3), p3 = new Vector3(0, 0, 3), png = png },
        new PhotoQuad { name = "no image", p0 = Vector3.zero, p1 = Vector3.right, p2 = Vector3.one, p3 = Vector3.up, png = null },
      };
      byte[] glb = PhotoModelGlb.Write(quads);
      Check(glb != null && glb.Length > 100, "textured GLB is written");
      Check(PhotoModelGlb.Write(new List<PhotoQuad>()) == null && PhotoModelGlb.Write(null) == null, "nothing to write: null");
      if (glb != null) {
        File.WriteAllBytes("photo_test.glb", glb);
        Check(BitConverter.ToUInt32(glb, 0) == 0x46546C67 && BitConverter.ToUInt32(glb, 4) == 2 && BitConverter.ToUInt32(glb, 8) == (uint)glb.Length, "GLB header: magic, version 2, total length");
        int jl = (int)BitConverter.ToUInt32(glb, 12);
        var j = JObject.Parse(System.Text.Encoding.UTF8.GetString(glb, 20, jl));
        Check(jl % 4 == 0 && (glb.Length - 20 - jl - 8) % 4 == 0, "chunks are 4-byte aligned");
        Check(((JArray)j["nodes"]).Count == 2 && ((JArray)j["images"]).Count == 2 && ((JArray)j["materials"]).Count == 2, "2 planes (the one without an image is left out)");
        Check((string)j["images"][0]["mimeType"] == "image/png" && (string)j["materials"][0]["alphaMode"] == "MASK" && (string)j["extensionsUsed"][0] == "KHR_materials_unlit", "PNG images, alpha-masked, unlit");
        var posAcc = j["accessors"][0];
        Check((float)posAcc["max"][2] == 0f && (float)posAcc["max"][1] == 2.5f, "POSITION min / max present and correct (Z mirrored)");
        int imgView = (int)j["images"][0]["bufferView"]; int off = (int)j["bufferViews"][imgView]["byteOffset"], len = (int)j["bufferViews"][imgView]["byteLength"];
        int binStart = 20 + jl + 8;
        Check(len == png.Length && glb[binStart + off] == 0x89 && glb[binStart + off + 1] == (byte)'P' && off % 4 == 0, "the PNG sits in the BIN chunk, intact and aligned");
      }
    }

    Console.WriteLine(fails == 0 ? "ALL PASSED" : fails + " FAILED"); return fails; }
}
