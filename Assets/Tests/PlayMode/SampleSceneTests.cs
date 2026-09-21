using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Meta.XR.MRUtilityKit;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

/// <summary>
/// Loads MRUK's sample scenes through the same path the headset uses for cached scans
/// (LoadSceneFromJsonString) and runs the whole export/preview pipeline on them, timing every step.
/// The step-by-step report is written to Exports/Test/pipeline_&lt;scene&gt;.txt.
/// </summary>
public class SampleSceneTests
{
    static readonly string[] Samples = { "MeshBedroom1", "MeshLivingRoom1", "MeshOffice1" };

    static string SampleDir()
    {
        string cache = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "PackageCache"));
        string pkg = Directory.GetDirectories(cache, "com.meta.xr.mrutilitykit@*").First();
        return Path.Combine(pkg, "Core", "Rooms", "Json");
    }

    class Report
    {
        readonly StringBuilder sb = new StringBuilder();
        public void Line(string s) { sb.AppendLine(s); }
        public T Step<T>(string name, Func<T> action, Func<T, string> describe = null)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                T result = action();
                Line($"[{sw.ElapsedMilliseconds,6} ms] {name}" + (describe != null ? " -> " + describe(result) : ""));
                return result;
            }
            catch (Exception ex)
            {
                Line($"[{sw.ElapsedMilliseconds,6} ms] {name} FAILED: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                return default;
            }
        }
        public override string ToString() => sb.ToString();
    }

    static IEnumerator Await(Task task)
    {
        while (!task.IsCompleted) yield return null;
    }

    static string Describe(XRHouseModel m)
    {
        if (m == null) return "null";
        var all = m.GetAllParts().SelectMany(p => p.vertices).ToList();
        string bounds = all.Count == 0 ? "empty" : $"x {all.Min(v => v.x):0.0}..{all.Max(v => v.x):0.0}, y {all.Min(v => v.y):0.0}..{all.Max(v => v.y):0.0}, z {all.Min(v => v.z):0.0}..{all.Max(v => v.z):0.0}";
        return $"{m.rooms.Count} rooms, {m.GetAllParts().Count()} parts, {all.Count} vertices, {m.GetAllParts().Sum(p => p.triangles.Count) / 3} triangles | {bounds}";
    }

    [UnityTest]
    public IEnumerator SampleScene_RunsThroughTheWholePipeline([ValueSource(nameof(Samples))] string sample)
    {
        var report = new Report();
        var rig = new GameObject("OVRCameraRig_Test");
        rig.AddComponent<OVRCameraRig>(); // MRUK refuses to start without one
        var go = new GameObject("MRUK_Test");
        var mruk = go.AddComponent<MRUK>();
        mruk.SceneSettings = new MRUK.MRUKSettings { LoadSceneOnStartup = false, DataSource = MRUK.SceneDataSource.Json };
        yield return null;

        string json = File.ReadAllText(Path.Combine(SampleDir(), sample + ".json"));
        var sw = Stopwatch.StartNew();
        var load = mruk.LoadSceneFromJsonString(json);
        yield return Await(load);
        report.Line($"[{sw.ElapsedMilliseconds,6} ms] LoadSceneFromJsonString -> {(load.IsFaulted ? "FAULT " + load.Exception?.GetBaseException().Message : load.Result.ToString())}, MRUK.Rooms={mruk.Rooms.Count}");

        foreach (var r in mruk.Rooms)
        {
            var labels = r.Anchors.GroupBy(a => a.Label.ToString()).OrderBy(g => g.Key).Select(g => $"{g.Key}x{g.Count()}");
            report.Line($"  raw room '{r.name}' anchors={r.Anchors.Count}: {string.Join(", ", labels)}");
            report.Line($"    floors={r.FloorAnchors.Count} ceilings={r.CeilingAnchors.Count} walls={r.WallAnchors.Count} globalMesh={(r.GlobalMeshAnchor != null)} anchorUuid={r.Anchor.Uuid}");
            foreach (var f in r.FloorAnchors)
                report.Line($"    floor boundary points={f.PlaneBoundary2D?.Count} rect={f.PlaneRect}");
        }

        var rooms = report.Step("GetValidRooms", () => MRUKDataProcessor.GetValidRooms(mruk), v => $"{v.Count} valid room(s)");
        if (rooms == null || rooms.Count == 0)
        {
            Finish(report, sample, go);
            Assert.Fail("no valid rooms - see Exports/Test/pipeline_" + sample + ".txt");
            yield break;
        }

        report.Step("GetRoomLabel", () => rooms.Select(MRUKDataProcessor.GetRoomLabel).ToList(), v => string.Join(", ", v));
        report.Step("walls (IsStructuralWall)", () => rooms.Sum(r => r.Anchors.Count(MRUKDataProcessor.IsStructuralWall)), v => v + " structural wall anchor(s)");
        report.Step("doors/windows (IsDoor/IsWindow)", () => (rooms.Sum(r => r.Anchors.Count(MRUKDataProcessor.IsDoor)), rooms.Sum(r => r.Anchors.Count(MRUKDataProcessor.IsWindow))), v => $"{v.Item1} door(s), {v.Item2} window(s)");

        var outlines = report.Step("MRUKPlanExtractor.Extract", () => MRUKPlanExtractor.Extract(rooms),
            v => string.Join(" | ", v.Select(o => $"{o.name}: {o.polygon.Count} pts, {o.Area:0.0} m2, {o.openings.Count} openings")));
        float yaw = report.Step("CorrectionYaw", () => FloorPlanBuilder.CorrectionYaw(outlines), v => $"{v:0.0} deg");
        var pages = report.Step("BuildPlanPages", () => FloorPlanBuilder.Flatten(FloorPlanBuilder.BuildLevels(FloorPlanBuilder.Aligned(outlines, yaw))),
            v => $"{v.Count} page(s), {v.Sum(p => p.lines.Count)} lines, {v.Sum(p => p.texts.Count)} texts");
        report.Step("Report HTML", () => MRUKReportBuilder.GenerateFullReport(outlines, yaw, sample), v => v.Length + " chars");

        Vector3 center = Vector3.zero;
        var anchorModel = report.Step("CreateAnchorAnalytical", () => XRModelFactory.CreateAnchorAnalytical(rooms, yaw, center), Describe);
        var reconModel = report.Step("CreateReconstruction", () => XRModelFactory.CreateReconstruction(rooms, yaw, center), Describe);

        XRHouseModel meshModel = null, rawModel = null;
        sw.Restart();
        var meshTask = XRModelFactory.CreateMeshAnalytical(rooms, yaw, center);
        yield return Await(meshTask);
        meshModel = meshTask.IsFaulted ? null : meshTask.Result;
        report.Line($"[{sw.ElapsedMilliseconds,6} ms] CreateMeshAnalytical -> {(meshTask.IsFaulted ? "FAULT " + meshTask.Exception.GetBaseException().Message : Describe(meshModel))}");

        sw.Restart();
        var rawTask = XRModelFactory.CreateRawScan(rooms, yaw, center);
        yield return Await(rawTask);
        rawModel = rawTask.IsFaulted ? null : rawTask.Result;
        report.Line($"[{sw.ElapsedMilliseconds,6} ms] CreateRawScan -> {(rawTask.IsFaulted ? "FAULT " + rawTask.Exception.GetBaseException().Message : Describe(rawModel))}");

        report.Step("GenerateJson", () => MRUKDataProcessor.GenerateJson(rooms), v => v.Length + " chars");
        report.Step("GenerateSceneDump", () => MRUKDataProcessor.GenerateSceneDump(rooms), v => v.Length + " chars");
        foreach (var (name, model) in new[] { ("anchor", anchorModel), ("recon", reconModel), ("mesh", meshModel), ("raw", rawModel) })
        {
            if (model == null) continue;
            report.Step($"OBJWriter[{name}]", () => OBJWriter.WriteToString(model), v => v.Length + " chars");
            report.Step($"GLBExporter[{name}]", () => GLBExporter.ExportToGLB(model), v => (v?.Length ?? 0) + " bytes");
        }

        foreach (var (name, model) in new[] { ("anchor", anchorModel), ("recon", reconModel), ("mesh", meshModel), ("raw", rawModel) })
        {
            if (model == null) continue;
            sw.Restart();
            GameObject visual = null;
            try { visual = UnityModelLoader.LoadToScene(model); }
            catch (Exception ex) { report.Line($"  LoadToScene[{name}] FAILED: {ex.Message}"); }
            report.Line($"[{sw.ElapsedMilliseconds,6} ms] UnityModelLoader.LoadToScene[{name}] -> {(visual != null ? visual.GetComponentsInChildren<MeshFilter>().Length + " mesh objects" : "null")}");
            if (visual != null) UnityEngine.Object.Destroy(visual);
        }

        Finish(report, sample, go);
        Assert.That(anchorModel, Is.Not.Null);
        Assert.That(anchorModel.GetAllParts().Count(), Is.GreaterThan(0));
    }

    static void Finish(Report report, string sample, GameObject go)
    {
        string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Exports", "Test"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "pipeline_" + sample + ".txt"), report.ToString());
        UnityEngine.Object.Destroy(go);
        foreach (var rig in UnityEngine.Object.FindObjectsByType<OVRCameraRig>(FindObjectsSortMode.None)) UnityEngine.Object.Destroy(rig.gameObject);
    }
}
