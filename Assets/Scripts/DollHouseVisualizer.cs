using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

public class DollHouseVisualizer : MonoBehaviour {
    public float scale = 0.01f; 
    public XRMenu uiLog;
    private GameObject root;

    public bool Toggle() {
        if (root != null) { 
            Destroy(root); 
            root = null; 
            return false; 
        }
        Build();
        return true;
    }

    private async void Build() {
#if META_XR_SDK_INSTALLED
        if (MRUK.Instance == null) return;
        var rooms = MRUK.Instance.Rooms.ToList();
        if (rooms.Count == 0) {
            if (uiLog != null) uiLog.AddLog("Inicializace scény...");
            await MRUK.Instance.LoadSceneFromDevice();
            await System.Threading.Tasks.Task.Delay(1000);
            rooms = MRUK.Instance.Rooms.ToList();
        }

        root = new GameObject("DollhouseRoot");
        Transform hand = GameObject.Find("RightHandAnchor")?.transform ?? Camera.main.transform;
        root.transform.SetParent(hand, false);
        root.transform.localPosition = new Vector3(0, 0.2f, 0.4f);
        root.transform.localRotation = Quaternion.Euler(0, 180, 0);
        root.transform.localScale = Vector3.one * scale;

        Vector3 houseCenter = Vector3.zero; int structuralCount = 0;
        float globalAngle = 0; float maxW = 0;
        foreach(var r in rooms) {
            foreach(var a in r.Anchors.Where(x => x.Label.ToString().Contains("WALL"))) {
                houseCenter += a.transform.position; structuralCount++;
                if (a.PlaneRect.HasValue && a.PlaneRect.Value.width > maxW) { maxW = a.PlaneRect.Value.width; globalAngle = -a.transform.eulerAngles.y; }
            }
        }
        if (structuralCount > 0) houseCenter /= structuralCount;
        Quaternion globalCorrection = Quaternion.Euler(0, globalAngle, 0);

        Shader unlit = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
        Color wallCol = new Color(0.4f, 0.4f, 0.4f, 1f);
        Color floorCol = new Color(0.7f, 0.7f, 0.7f, 1f);
        Color doorCol = new Color(0.5f, 0.3f, 0.1f, 1f);
        Color winCol = new Color(0.3f, 0.6f, 0.9f, 1f);

        var allAnchors = rooms.SelectMany(r => r.Anchors).Where(a => a.PlaneRect.HasValue).ToList();
        var openings = allAnchors.Where(a => { string l = a.Label.ToString().ToUpper(); return l.Contains("DOOR") || l.Contains("WINDOW") || l.Contains("OPENING"); }).ToList();

        int totalObjCount = 0;
        foreach (var a in allAnchors) {
            string lab = a.Label.ToString().ToUpper();
            if (lab.Contains("CEILING") || lab.Contains("INVISIBLE")) continue;
            bool isWall = lab.Contains("WALL"), isFloor = lab.Contains("FLOOR"), isOpening = lab.Contains("DOOR") || lab.Contains("WINDOW") || lab.Contains("OPENING");
            if (!isWall && !isFloor && !isOpening) continue;

            if (isWall) {
                float wW = a.PlaneRect.Value.width, wH = a.PlaneRect.Value.height;
                var wallHoles = openings.Where(o => { Vector3 lp = a.transform.InverseTransformPoint(o.transform.position); return Mathf.Abs(lp.z) < 0.3f && Mathf.Abs(lp.x) < (wW/2f + 0.1f) && Mathf.Abs(lp.y) < (wH/2f + 0.1f); }).ToList();
                if (wallHoles.Count > 0) {
                    var xC = new List<float>{-wW/2f, wW/2f}; var yC = new List<float>{-wH/2f, wH/2f};
                    foreach(var h in wallHoles) { Vector3 lp = a.transform.InverseTransformPoint(h.transform.position); float hw = h.PlaneRect.Value.width/2f, hh = h.PlaneRect.Value.height/2f; xC.Add(Mathf.Clamp(lp.x-hw, -wW/2f, wW/2f)); xC.Add(Mathf.Clamp(lp.x+hw, -wW/2f, wW/2f)); yC.Add(Mathf.Clamp(lp.y-hh, -wH/2f, wH/2f)); yC.Add(Mathf.Clamp(lp.y+hh, -wH/2f, wH/2f)); }
                    var sX = xC.Distinct().OrderBy(x=>x).ToList(); var sY = yC.Distinct().OrderBy(y=>y).ToList();
                    for (int i=0; i<sX.Count-1; i++) {
                        for (int j=0; j<sY.Count-1; j++) {
                            float x1=sX[i], x2=sX[i+1], y1=sY[j], y2=sY[j+1]; if (x2-x1 < 0.02f || y2-y1 < 0.02f) continue;
                            Vector2 mid = new Vector2((x1+x2)/2f, (y1+y2)/2f); bool isH = false;
                            foreach(var h in wallHoles) { Vector3 lp = a.transform.InverseTransformPoint(h.transform.position); float hw = h.PlaneRect.Value.width/2f, hh = h.PlaneRect.Value.height/2f; if (mid.x > lp.x-hw+0.01f && mid.x < lp.x+hw-0.01f && mid.y > lp.y-hh+0.01f && mid.y < lp.y+hh-0.01f) { isH=true; break; } }
                            if (!isH) {
                                totalObjCount++;
                                CreateBox(root.transform, globalCorrection * (a.transform.TransformPoint(new Vector3(mid.x, mid.y, 0)) - houseCenter), globalCorrection * a.transform.rotation, new Vector3(x2-x1, y2-y1, 0.25f), wallCol, unlit);
                            }
                        }
                    }
                } else {
                    totalObjCount++;
                    CreateBox(root.transform, globalCorrection * (a.transform.position - houseCenter), globalCorrection * a.transform.rotation, new Vector3(wW, wH, 0.25f), wallCol, unlit);
                }
            } else {
                totalObjCount++;
                float th = isFloor ? 0.1f : (lab.Contains("DOOR") ? 0.12f : 0.15f);
                Color col = isFloor ? floorCol : (lab.Contains("DOOR") ? doorCol : winCol);
                CreateBox(root.transform, globalCorrection * (a.transform.TransformPoint(isFloor ? new Vector3(0,0,-0.05f) : Vector3.zero) - houseCenter), globalCorrection * a.transform.rotation, new Vector3(a.PlaneRect.Value.width, a.PlaneRect.Value.height, th), col, unlit);
            }
        }
        if (uiLog != null) uiLog.AddLog($"Dollhouse spawned: {rooms.Count} rooms, {totalObjCount} objects.");
#endif
    }

    private void CreateBox(Transform parent, Vector3 localPos, Quaternion localRot, Vector3 size, Color col, Shader shader) {
        GameObject b = GameObject.CreatePrimitive(PrimitiveType.Cube);
        b.transform.SetParent(parent, false);
        b.transform.localPosition = localPos;
        b.transform.localRotation = localRot;
        b.transform.localScale = size;
        b.GetComponent<MeshRenderer>().material = new Material(shader) { color = col };
        Destroy(b.GetComponent<BoxCollider>());
    }
}
