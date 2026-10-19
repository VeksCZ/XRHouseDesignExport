#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Turns a pulled wall-photo session (Exports/RoomData/Photos_yyyyMMdd, see WallPhotoCapture) into a COLMAP dataset
/// for Gaussian-splat training: Colmap/images/*.jpg + Colmap/sparse/0/{cameras,images,points3D}.txt. Works with any
/// trainer that reads COLMAP (gsplat simple_trainer, LichtFeld Studio, Postshot, 3DGS...).
///
/// The poses are the photos' house-frame poses (local to one fixed room, the same for the whole session), so the
/// trained splat comes out in that frame - placed under that room's transform it lines up with the scan.
/// Axis convention: COLMAP world = house frame (Unity: x right, y up, z forward) with z negated (right-handed);
/// COLMAP camera = Unity camera with y negated (x right, y down, z forward). To bring a splat back: negate z.
/// </summary>
public static class WallPhotoColmap
{
    public const string OutFolder = "Colmap";

    [MenuItem("MRUK/Wall photos/Export COLMAP (splat training data)...", false, 35)]
    static void ExportMenu()
    {
        string root = Path.GetFullPath(MRUKPathUtility.GetExportRoot());
        string dir = EditorUtility.OpenFolderPanel("Wall photo session (Photos_yyyyMMdd)", root, "");
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            string outDir = Export(dir, out string report);
            Debug.Log($"<color=green>COLMAP export: {report}</color>\n{outDir}");
            EditorUtility.RevealInFinder(Path.Combine(outDir, "sparse"));
        }
        catch (Exception e) { Debug.LogError($"<color=red>COLMAP export failed: {e.Message}</color>\n{e}"); }
    }

    /// <summary>Writes sessionDir/Colmap (replacing an earlier export) and returns its path.</summary>
    public static string Export(string sessionDir, out string report, bool includeCablePhotos = true)
    {
        var metas = new List<(WallPhotoMeta meta, string jpg)>();
        void Load(string folder, bool dense)
        {
            if (!Directory.Exists(folder)) return;
            foreach (var json in Directory.GetFiles(folder, "*.json").OrderBy(f => f, StringComparer.Ordinal))
            {
                string name = Path.GetFileName(json);
                if (!char.IsDigit(name[0])) continue; // 92_Data_Scan... and other non-photo files
                WallPhotoMeta m;
                try { m = JsonUtility.FromJson<WallPhotoMeta>(File.ReadAllText(json)); } catch { continue; }
                string jpg = Path.ChangeExtension(json, ".jpg");
                if (m == null || !File.Exists(jpg) || string.IsNullOrEmpty(m.houseRoomUuid) || m.fu <= 0) continue;
                if (m.dense != dense) continue;
                metas.Add((m, jpg));
            }
        }
        Load(Path.Combine(sessionDir, WallPhotoCapture.DenseFolder), true);
        if (includeCablePhotos) Load(sessionDir, false);
        if (metas.Count == 0) throw new Exception("no photos with a house-frame pose in " + sessionDir);

        // One frame per export: the house room most photos use (a session normally has exactly one).
        string house = metas.GroupBy(x => x.meta.houseRoomUuid).OrderByDescending(g => g.Count()).First().Key;
        int skipped = metas.RemoveAll(x => x.meta.houseRoomUuid != house);

        string outDir = Path.Combine(sessionDir, OutFolder);
        if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
        string imgDir = Path.Combine(outDir, "images"), sparse = Path.Combine(outDir, "sparse", "0");
        Directory.CreateDirectory(imgDir);
        Directory.CreateDirectory(sparse);

        var ci = CultureInfo.InvariantCulture;
        string F(double v) => v.ToString("R", ci);

        var cameras = new Dictionary<string, int>();
        var camText = new StringBuilder("# Camera list with one line of data per camera:\n#   CAMERA_ID, MODEL, WIDTH, HEIGHT, PARAMS[]\n");
        var imgText = new StringBuilder("# Image list with two lines of data per image:\n#   IMAGE_ID, QW, QX, QY, QZ, TX, TY, TZ, CAMERA_ID, NAME\n#   POINTS2D[] as (X, Y, POINT3D_ID)\n");
        var ptsText = new StringBuilder("# 3D point list with one line of data per point:\n#   POINT3D_ID, X, Y, Z, R, G, B, ERROR, TRACK[] as (IMAGE_ID, POINT2D_IDX)\n");

        // S: house frame -> COLMAP world (negate z). D: Unity camera -> COLMAP camera (negate y).
        var S = Matrix4x4.Scale(new Vector3(1, 1, -1));
        var D = Matrix4x4.Scale(new Vector3(1, -1, 1));

        int imageId = 0, pointId = 0, seedTotal = metas.Sum(x => x.meta.seedPoints.Count);
        int stride = Mathf.Max(1, Mathf.CeilToInt(seedTotal / 300000f)); // keep the initial cloud manageable
        foreach (var (m, jpg) in metas)
        {
            int w = m.imageWidth, h = m.imageHeight;
            double fx = m.fu * w, fy = m.fv * h, cx = m.cu * w, cy = (1 - m.cv) * h;
            string key = $"{w}x{h}:{fx:F2}:{fy:F2}:{cx:F2}:{cy:F2}";
            if (!cameras.TryGetValue(key, out int camId))
            {
                camId = cameras.Count + 1;
                cameras[key] = camId;
                camText.Append($"{camId} PINHOLE {w} {h} {F(fx)} {F(fy)} {F(cx)} {F(cy)}\n");
            }

            // World->camera: Pc = D R^T (S X - C)  =>  R_c = D R^T S, t_c = -D R^T C.
            var rt = Matrix4x4.Rotate(Quaternion.Inverse(m.houseRotation));
            var rc = D * rt * S;
            Vector3 tc = -(D * rt).MultiplyVector(m.housePosition);
            Quaternion q = rc.rotation;

            string name = (m.dense ? "d_" : "p_") + m.index.ToString("0000") + ".jpg";
            File.Copy(jpg, Path.Combine(imgDir, name), true);
            imageId++;
            imgText.Append($"{imageId} {F(q.w)} {F(q.x)} {F(q.y)} {F(q.z)} {F(tc.x)} {F(tc.y)} {F(tc.z)} {camId} {name}\n\n");

            for (int i = 0; i < m.seedPoints.Count && i < m.seedColors.Count; i++)
            {
                if ((pointId + i) % stride != 0) continue;
                Vector3 p = S.MultiplyPoint3x4(m.seedPoints[i]);
                Color32 c = m.seedColors[i];
                ptsText.Append($"{pointId + i + 1} {F(p.x)} {F(p.y)} {F(p.z)} {c.r} {c.g} {c.b} 0\n");
            }
            pointId += m.seedPoints.Count;
        }

        File.WriteAllText(Path.Combine(sparse, "cameras.txt"), camText.ToString());
        File.WriteAllText(Path.Combine(sparse, "images.txt"), imgText.ToString());
        File.WriteAllText(Path.Combine(sparse, "points3D.txt"), ptsText.ToString());
        File.WriteAllText(Path.Combine(outDir, "house_frame.json"), JsonUtility.ToJson(new HouseFrameInfo
        {
            houseRoomUuid = house,
            scan = MRUKPathUtility.DATA_SCENE,
            note = "COLMAP world = house room's local frame (Unity axes) with z negated. Splat -> Unity: negate z, then parent to the room houseRoomUuid.",
        }, true));
        File.WriteAllText(Path.Combine(outDir, "README.txt"),
            "COLMAP dataset from XRHouseDesignExport wall photos (images/ + sparse/0 as text).\n" +
            "Train e.g. with gsplat:  python examples/simple_trainer.py default --data_dir <this folder> --data_factor 1 --result_dir <out>\n" +
            "or open this folder in LichtFeld Studio / Postshot (COLMAP input).\n" +
            "Poses come from the headset tracking (no SfM needed); points3D.txt is a seed cloud from the scan, coloured from the photos.\n");

        int dense = metas.Count(x => x.meta.dense);
        report = $"{metas.Count} images ({dense} dense, {metas.Count - dense} cable photos), {cameras.Count} camera(s), " +
                 $"{pointId / stride} seed points" + (skipped > 0 ? $", {skipped} skipped (other house frame)" : "");
        return outDir;
    }

    [Serializable]
    class HouseFrameInfo { public string houseRoomUuid, scan, note; }
}
#endif
