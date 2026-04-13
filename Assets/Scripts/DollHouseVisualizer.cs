using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
using Meta.XR;
#endif

public class DollHouseVisualizer : MonoBehaviour
{
    [Header("Dollhouse Settings")]
    public float scale = 0.012f;
    public float heightOffset = 0.25f;
    public float distanceFromHand = 0.45f;

    public XRMenu uiLog;

    private GameObject currentDollhouse;
    private DollhouseMode currentMode = DollhouseMode.Off;

    private enum DollhouseMode
    {
        Off,
        Analytical,
        RawMesh
    }

    public bool Toggle()
    {
        // Cyklus: Analytical → RawMesh → Off
        currentMode = currentMode switch
        {
            DollhouseMode.Off => DollhouseMode.Analytical,
            DollhouseMode.Analytical => DollhouseMode.RawMesh,
            DollhouseMode.RawMesh => DollhouseMode.Off,
            _ => DollhouseMode.Off
        };

        RefreshDollhouse();
        return currentMode != DollhouseMode.Off;
    }

    private async void RefreshDollhouse()
    {
        // Nejdřív smažeme starý dollhouse
        if (currentDollhouse != null)
        {
            Destroy(currentDollhouse);
            currentDollhouse = null;
        }

        if (currentMode == DollhouseMode.Off)
        {
            uiLog?.AddLog("Dollhouse vypnut.");
            return;
        }

        uiLog?.AddLog($"Vytvářím Dollhouse: {currentMode}");

        currentDollhouse = new GameObject($"Dollhouse_{currentMode}");
        Transform hand = GameObject.Find("RightHandAnchor")?.transform ?? Camera.main?.transform;

        if (hand != null)
        {
            currentDollhouse.transform.SetParent(hand, false);
            currentDollhouse.transform.localPosition = new Vector3(0, heightOffset, distanceFromHand);
            currentDollhouse.transform.localRotation = Quaternion.Euler(0, 180, 0);
            currentDollhouse.transform.localScale = Vector3.one * scale;
        }

        if (currentMode == DollhouseMode.Analytical)
            await BuildAnalyticalDollhouse();
        else if (currentMode == DollhouseMode.RawMesh)
            await BuildRawMeshDollhouse();
    }

    private async Task BuildAnalyticalDollhouse()
    {
        // Zde použijeme tvůj stávající clean analytical model (nebo boxový)
        string objData = await MRUKModelExporterV2.GenerateCleanColoredAnalytical(0f); // globální rotace 0 pro dollhouse
        if (string.IsNullOrEmpty(objData)) return;

        // Jednoduchý OBJ loader do scény (inline verze)
        var go = LoadOBJFromString(objData);
        if (go != null)
        {
            go.transform.SetParent(currentDollhouse.transform, false);
            uiLog?.AddLog("Analytical Dollhouse vytvořen.");
        }
    }

    private async Task BuildRawMeshDollhouse()
    {
        string rawObj = await MRUKModelExporterV2.GenerateRawHighFidelityMesh(0f);
        if (string.IsNullOrEmpty(rawObj)) return;

        var go = LoadOBJFromString(rawObj);
        if (go != null)
        {
            go.transform.SetParent(currentDollhouse.transform, false);
            uiLog?.AddLog("Raw Mesh Dollhouse vytvořen (high-fidelity).");
        }
    }

    private GameObject LoadOBJFromString(string objData)
    {
        if (string.IsNullOrEmpty(objData)) return null;

        var vertices = new List<Vector3>();
        var submeshes = new Dictionary<string, List<int>>();
        string currentMaterial = "Default";
        submeshes[currentMaterial] = new List<int>();

        using (var reader = new System.IO.StringReader(objData))
        {
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.StartsWith("v "))
                {
                    var p = line.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length >= 4 && 
                        float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x) &&
                        float.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y) &&
                        float.TryParse(p[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z))
                    {
                        vertices.Add(new Vector3(x, y, z));
                    }
                }
                else if (line.StartsWith("usemtl "))
                {
                    currentMaterial = line.Substring(7).Trim();
                    if (!submeshes.ContainsKey(currentMaterial)) submeshes[currentMaterial] = new List<int>();
                }
                else if (line.StartsWith("f "))
                {
                    var p = line.Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
                    if (p.Length >= 4)
                    {
                        try {
                            submeshes[currentMaterial].Add(int.Parse(p[1].Split('/')[0]) - 1);
                            submeshes[currentMaterial].Add(int.Parse(p[2].Split('/')[0]) - 1);
                            submeshes[currentMaterial].Add(int.Parse(p[3].Split('/')[0]) - 1);
                        } catch {}
                    }
                }
            }
        }

        if (vertices.Count == 0) return null;

        var go = new GameObject("OBJModel");
        var mf = go.AddComponent<MeshFilter>();
        var mr = go.AddComponent<MeshRenderer>();
        var mesh = new Mesh { vertices = vertices.ToArray() };
        
        var matNames = submeshes.Keys.ToList();
        mesh.subMeshCount = matNames.Count;
        var materials = new Material[matNames.Count];

        for (int i = 0; i < matNames.Count; i++)
        {
            mesh.SetTriangles(submeshes[matNames[i]], i);
            materials[i] = GetOBJMaterial(matNames[i]);
        }

        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        mf.mesh = mesh;
        mr.materials = materials;
        return go;
    }

    private Material GetOBJMaterial(string name)
    {
        var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
        var mat = new Material(shader);
        switch (name.ToLower())
        {
            case "wall": mat.color = new Color(0.42f, 0.42f, 0.45f, 1f); break;
            case "floor": mat.color = new Color(0.75f, 0.75f, 0.78f, 1f); break;
            case "door": mat.color = new Color(0.55f, 0.35f, 0.18f, 1f); break;
            case "window": mat.color = new Color(0.25f, 0.60f, 0.92f, 1f); break;
            default: mat.color = Color.white; break;
        }
        return mat;
    }
}
