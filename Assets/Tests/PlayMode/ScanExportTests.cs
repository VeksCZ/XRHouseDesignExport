using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using Meta.XR.MRUtilityKit;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

/// <summary>
/// Batch export of a saved scan on the PC, without a headset: loads a MRUK scene JSON (the ".scene.json" that
/// "Save scan" writes, or an export's 92_Data_Scan.scene.json) and runs the real MRUKExporter pipeline on it.
/// Only runs when XR_SCAN_JSON points to a file, so it is skipped in normal test runs. Typical use:
///   set XR_SCAN_JSON=C:\...\Exports\ScanCache\20261002_0848_15rooms.scene.json
///   Unity.exe -batchmode -projectPath . -runTests -testPlatform PlayMode -testFilter ScanExportTests
/// Output goes to Exports/Batch (or XR_EXPORT_ROOT); the session folder is logged as "[BatchExport] DONE ...".
/// </summary>
public class ScanExportTests
{
    static IEnumerator Await(Task task)
    {
        while (!task.IsCompleted) yield return null;
    }

    [UnityTest]
    public IEnumerator ExportScanFromJson()
    {
        string scan = Environment.GetEnvironmentVariable("XR_SCAN_JSON");
        if (string.IsNullOrEmpty(scan) || !File.Exists(scan))
        {
            Assert.Ignore("XR_SCAN_JSON not set (or file missing) - batch export skipped.");
            yield break;
        }
        string root = Environment.GetEnvironmentVariable("XR_EXPORT_ROOT");
        if (string.IsNullOrEmpty(root)) root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Exports", "Batch"));
        Directory.CreateDirectory(root);

        var rig = new GameObject("OVRCameraRig_Batch");
        rig.AddComponent<OVRCameraRig>(); // MRUK refuses to start without one
        var go = new GameObject("MRUK_Batch");
        var mruk = go.AddComponent<MRUK>();
        mruk.SceneSettings = new MRUK.MRUKSettings { LoadSceneOnStartup = false, DataSource = MRUK.SceneDataSource.Json };
        var exporter = go.AddComponent<MRUKExporter>();
        yield return null;

        var load = mruk.LoadSceneFromJsonString(File.ReadAllText(scan));
        yield return Await(load);
        Assert.IsFalse(load.IsFaulted, "LoadSceneFromJsonString failed: " + load.Exception?.GetBaseException().Message);
        Assert.AreEqual(MRUK.LoadDeviceResult.Success, load.Result);

        string name = Path.GetFileName(scan).Replace(MRUKSceneCache.EXTENSION, "");
        var export = exporter.ExportLoadedScene(name, root);
        yield return Await(export);
        Assert.IsFalse(export.IsFaulted, "Export failed: " + export.Exception?.GetBaseException());
        Assert.IsNotNull(export.Result, "Export produced no session (see log)");
        Debug.Log("[BatchExport] DONE " + Path.GetFullPath(export.Result));

        UnityEngine.Object.Destroy(go);
        UnityEngine.Object.Destroy(rig);
    }
}
