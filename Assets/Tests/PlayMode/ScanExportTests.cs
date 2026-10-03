using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using Meta.XR.MRUtilityKit;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

/// <summary>
/// Batch export of a saved scan on the PC, without a headset: loads a MRUK scene JSON (an export's
/// 92_Data_Scan.scene.json, or a loose ".scene.json" that "Save scan" writes on the headset) and runs the real
/// MRUKExporter pipeline on it, with the scan's edits and room names. Only runs when XR_SCAN_JSON points to a file,
/// so it is skipped in normal test runs. Typical use (or just Tools\BatchExport.ps1):
///   set XR_SCAN_JSON=C:\...\Exports\RoomData\Export_..._20261002_0848_15rooms\92_Data_Scan.scene.json
///   Unity.exe -batchmode -projectPath . -runTests -testPlatform PlayMode -testFilter ScanExportTests
/// Output goes to Exports/RoomData next to the other exports (or XR_EXPORT_ROOT); the session folder is logged as
/// "[BatchExport] DONE ...".
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
        if (string.IsNullOrEmpty(root)) root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Exports", "RoomData"));
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

        // An export's/scan folder's 92_Data_Scan.scene.json is named after its folder, and its edits (93) and room
        // names (94) next to it go into the new export too; a loose Name.scene.json keeps its own name.
        string dir = Path.GetDirectoryName(Path.GetFullPath(scan));
        bool inFolder = Path.GetFileName(scan) == MRUKPathUtility.DATA_SCENE;
        string name = inFolder ? MRUKPathUtility.ScanNameFromFolder(Path.GetFileName(dir)) : Path.GetFileName(scan).Replace(MRUKSceneCache.EXTENSION, "");
        string edits = inFolder ? Path.Combine(dir, MRUKPathUtility.DATA_EDITS) : Path.ChangeExtension(Path.ChangeExtension(scan, null), null) + HouseEditsStore.EXTENSION;
        string names = Path.Combine(dir, MRUKPathUtility.DATA_ROOM_NAMES);
        HouseEditsStore.Bind(name, edits);
        RoomNames.FilePath = File.Exists(names) ? names : Path.Combine(Path.GetTempPath(), "XRHouse_batch_room_names.json");

        var export = exporter.ExportLoadedScene(name, root);
        yield return Await(export);
        HouseEditsStore.Bind(null);
        RoomNames.FilePath = null;
        Assert.IsFalse(export.IsFaulted, "Export failed: " + export.Exception?.GetBaseException());
        Assert.IsNotNull(export.Result, "Export produced no session (see log)");
        Debug.Log("[BatchExport] DONE " + Path.GetFullPath(export.Result));

        UnityEngine.Object.Destroy(go);
        UnityEngine.Object.Destroy(rig);
    }
}
