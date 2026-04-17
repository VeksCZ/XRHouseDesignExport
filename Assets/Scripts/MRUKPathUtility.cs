using System;
using System.IO;
using UnityEngine;

public static class MRUKPathUtility
{
    public const string MODEL_CLEAN_OBJ = "01_Model_Analytical_Anchors.obj";
    public const string MODEL_CLEAN_GLB = "01_Model_Analytical_Anchors.glb";
    public const string MODEL_MESH_ANALYTICAL_OBJ = "02_Model_Analytical_Mesh.obj";
    public const string MODEL_MESH_ANALYTICAL_GLB = "02_Model_Analytical_Mesh.glb";
    public const string MODEL_MESH_OBJ = "03_Model_Reconstruction.obj";
    public const string MODEL_MESH_GLB = "03_Model_Reconstruction.glb";
    public const string MODEL_RAW_OBJ = "04_Model_Raw_Scan.obj";
    public const string MODEL_MTL = "house_materials.mtl";
    
    public const string DATA_JSON = "90_Data_Rooms.json";
    public const string DATA_DUMP = "91_Data_Scene_Dump.txt";
    public const string DATA_REPORT = "99_Report_House.html";

    public static string GetExportRoot()
    {
        return Application.isEditor 
            ? "Exports/RoomData" 
            : "/sdcard/Download/XRHouseExports";
    }

    public static string CreateSessionFolder(string root)
    {
        string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string session = Path.Combine(root, "Export_" + ts);
        if (!Directory.Exists(session)) Directory.CreateDirectory(session);
        return session;
    }

    public static string GetLogRoot()
    {
        return Application.isEditor 
            ? "Exports/Logs" 
            : "/sdcard/Download/XRHouseExports/Logs";
    }
}
