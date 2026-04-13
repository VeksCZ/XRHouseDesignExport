using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

public class DollHouseVisualizer : MonoBehaviour
{
    [Header("Dollhouse Settings")]
    public float scale = 0.01f;
    public float heightOffset = 0.2f;
    public float distanceFromHand = 0.4f;

    public XRMenu uiLog;

    private GameObject root;

    public bool Toggle()
    {
        if (root != null)
        {
            Destroy(root);
            root = null;
            uiLog?.AddLog("Dollhouse skryt.");
            return false;
        }

        Build();
        return true;
    }

    private async void Build()
    {
    #if META_XR_SDK_INSTALLED
        if (MRUK.Instance == null)
        {
            uiLog?.AddLog("<color=red>MRUK.Instance je null</color>");
            return;
        }

        var rooms = MRUK.Instance.Rooms.ToList();

        if (rooms.Count == 0)
        {
            uiLog?.AddLog("Načítám scénu pro Dollhouse...");
            try
            {
                // LoadSceneFromDevice returns a Task in some SDK versions, or void in others.
                // Using Task.Delay to be safe.
                var loadTask = MRUK.Instance.LoadSceneFromDevice();
                await Task.Delay(1500);
            }
            catch
            {
                await Task.Delay(1500);
            }
            rooms = MRUK.Instance.Rooms.ToList();
        }

        if (rooms.Count == 0)
        {
            uiLog?.AddLog("<color=yellow>Žádné místnosti nenalezeny pro Dollhouse.</color>");
            return;
        }

        root = new GameObject("DollhouseRoot");
        Transform handAnchor = GameObject.Find("RightHandAnchor")?.transform ?? Camera.main?.transform;

        if (handAnchor != null)
        {
            root.transform.SetParent(handAnchor, false);
            root.transform.localPosition = new Vector3(0, heightOffset, distanceFromHand);
            root.transform.localRotation = Quaternion.Euler(0, 180, 0);
        }

        root.transform.localScale = Vector3.one * scale;

        // === NOVÉ A LEPŠÍ VÝPOČET ROTACE ===
        Quaternion globalCorrection = Quaternion.identity;
        Vector3 houseCenter = Vector3.zero;

        // Najdeme podlahu první místnosti (nejspolehlivější reference)
        MRUKAnchor referenceFloor = null;
        foreach (var room in rooms)
        {
            var floorAnchor = room.Anchors.FirstOrDefault(a => a.Label == MRUKAnchor.SceneLabels.FLOOR);
            if (floorAnchor != null)
            {
                referenceFloor = floorAnchor;
                break;
            }
        }

        if (referenceFloor != null)
        {
            // Použijeme rotaci podlahy jako hlavní referenci
            globalCorrection = Quaternion.Euler(0, -referenceFloor.transform.eulerAngles.y, 0);
            houseCenter = referenceFloor.transform.position;
            uiLog?.AddLog($"Dollhouse rotace podle podlahy: {referenceFloor.transform.eulerAngles.y:F1}°");
        }
        else
        {
            // Fallback na starou metodu (pokud není podlaha)
            float maxW = 0f;
            float bestAngle = 0f;
            int count = 0;
            foreach (var r in rooms)
            {
                foreach (var a in r.Anchors.Where(x => x.Label.ToString().ToUpperInvariant().Contains("WALL") && x.PlaneRect.HasValue))
                {
                    houseCenter += a.transform.position;
                    count++;
                    if (a.PlaneRect.Value.width > maxW)
                    {
                        maxW = a.PlaneRect.Value.width;
                        bestAngle = a.transform.eulerAngles.y;
                    }
                }
            }
            if (count > 0) houseCenter /= count;
            globalCorrection = Quaternion.Euler(0, -bestAngle, 0);
        }

        Shader unlitShader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
        Color wallColor  = new Color(0.42f, 0.42f, 0.45f, 1f);
        Color floorColor = new Color(0.75f, 0.75f, 0.78f, 1f);
        Color doorColor  = new Color(0.55f, 0.35f, 0.18f, 1f);
        Color windowColor = new Color(0.25f, 0.60f, 0.92f, 1f);

        var allAnchors = rooms.SelectMany(r => r.Anchors).Where(a => a.PlaneRect.HasValue).ToList();
        var openings = allAnchors.Where(a => 
        {
            string l = a.Label.ToString().ToUpperInvariant();
            return l.Contains("DOOR") || l.Contains("WINDOW") || l.Contains("OPENING");
        }).ToList();

        int totalObjects = 0;

        foreach (var anchor in allAnchors)
        {
            if (anchor == null) continue;
            try {
                string labelUpper = anchor.Label.ToString().ToUpperInvariant();
                if (labelUpper.Contains("CEILING") || labelUpper.Contains("INVISIBLE")) continue;

                bool isWall = labelUpper.Contains("WALL");
                bool isFloor = labelUpper.Contains("FLOOR");
                bool isOpening = labelUpper.Contains("DOOR") || labelUpper.Contains("WINDOW") || labelUpper.Contains("OPENING");

                if (!isWall && !isFloor && !isOpening) continue;

                Quaternion localRot = globalCorrection * anchor.transform.rotation;

                if (isWall)
                {
                    CreateWallWithHoles(root.transform, anchor, houseCenter, globalCorrection, localRot, wallColor, unlitShader, openings, ref totalObjects);
                }
                else if (isFloor || labelUpper.Contains("CEILING"))
                {
                    Color color = isFloor ? floorColor : new Color(0.8f, 0.8f, 0.8f, 1f);
                    if (anchor.PlaneBoundary2D != null && anchor.PlaneBoundary2D.Count > 2)
                    {
                        CreatePolygon(root.transform, anchor, houseCenter, globalCorrection, color, unlitShader, isFloor ? -0.05f : 0.05f);
                    }
                    else
                    {
                        // Fallback to box
                        float thickness = isFloor ? 0.1f : 0.15f;
                        Vector3 offset = isFloor ? new Vector3(0, 0, -0.05f) : Vector3.zero;
                        Vector3 pos = globalCorrection * (anchor.transform.TransformPoint(offset) - houseCenter);
                        CreateBox(root.transform, pos, localRot, new Vector3(anchor.PlaneRect.Value.width, anchor.PlaneRect.Value.height, thickness), color, unlitShader);
                    }
                    totalObjects++;
                }
                else if (isOpening)
                {
                    Color color = labelUpper.Contains("DOOR") ? doorColor : windowColor;
                    Vector3 pos = globalCorrection * (anchor.transform.position - houseCenter);
                    CreateBox(root.transform, pos, localRot, new Vector3(anchor.PlaneRect.Value.width, anchor.PlaneRect.Value.height, 0.15f), color, unlitShader);
                    totalObjects++;
                }
            } catch (Exception ex) {
                Debug.LogWarning($"Dollhouse object creation failed for {anchor.name}: {ex.Message}");
            }
        }

        uiLog?.AddLog($"Dollhouse vytvořen: {rooms.Count} místností, {totalObjects} objektů | Rotace: {globalCorrection.eulerAngles.y:F1}°");
    #endif
    }

    private void CreateWallWithHoles(Transform parent, MRUKAnchor wallAnchor, Vector3 houseCenter, Quaternion globalCorrection, Quaternion localRot,
        Color color, Shader shader, List<MRUKAnchor> openings, ref int totalObjects)
    {
        float width = wallAnchor.PlaneRect.Value.width;
        float height = wallAnchor.PlaneRect.Value.height;

        var wallHoles = openings.Where(o =>
        {
            Vector3 lp = wallAnchor.transform.InverseTransformPoint(o.transform.position);
            return Mathf.Abs(lp.z) < 0.3f && Mathf.Abs(lp.x) < (width / 2f + 0.15f) && Mathf.Abs(lp.y) < (height / 2f + 0.15f);
        }).ToList();

        if (wallHoles.Count > 0)
        {
            var xCuts = new List<float> { -width / 2f, width / 2f };
            var yCuts = new List<float> { -height / 2f, height / 2f };

            foreach (var hole in wallHoles)
            {
                Vector3 lp = wallAnchor.transform.InverseTransformPoint(hole.transform.position);
                float hw = hole.PlaneRect.Value.width / 2f;
                float hh = hole.PlaneRect.Value.height / 2f;
                xCuts.Add(Mathf.Clamp(lp.x - hw, -width / 2f, width / 2f));
                xCuts.Add(Mathf.Clamp(lp.x + hw, -width / 2f, width / 2f));
                yCuts.Add(Mathf.Clamp(lp.y - hh, -height / 2f, height / 2f));
                yCuts.Add(Mathf.Clamp(lp.y + hh, -height / 2f, height / 2f));
            }

            var sortedX = xCuts.Distinct().OrderBy(x => x).ToList();
            var sortedY = yCuts.Distinct().OrderBy(y => y).ToList();

            for (int i = 0; i < sortedX.Count - 1; i++)
            {
                for (int j = 0; j < sortedY.Count - 1; j++)
                {
                    float segmentW = sortedX[i + 1] - sortedX[i];
                    float segmentH = sortedY[j + 1] - sortedY[j];
                    if (segmentW < 0.03f || segmentH < 0.03f) continue;

                    Vector2 mid = new Vector2((sortedX[i] + sortedX[i + 1]) / 2f, (sortedY[j] + sortedY[j + 1]) / 2f);
                    bool isHole = wallHoles.Any(h =>
                    {
                        Vector3 lp = wallAnchor.transform.InverseTransformPoint(h.transform.position);
                        float hw = h.PlaneRect.Value.width / 2f;
                        float hh = h.PlaneRect.Value.height / 2f;
                        return mid.x > lp.x - hw + 0.01f && mid.x < lp.x + hw - 0.01f && mid.y > lp.y - hh + 0.01f && mid.y < lp.y + hh - 0.01f;
                    });

                    if (!isHole)
                    {
                        Vector3 pieceWorld = wallAnchor.transform.TransformPoint(new Vector3(mid.x, mid.y, 0));
                        Vector3 pieceLocal = globalCorrection * (pieceWorld - houseCenter);
                        CreateBox(parent, pieceLocal, localRot, new Vector3(segmentW, segmentH, 0.25f), color, shader);
                        totalObjects++;
                    }
                }
            }
        }
        else
        {
            Vector3 pos = globalCorrection * (wallAnchor.transform.position - houseCenter);
            CreateBox(parent, pos, localRot, new Vector3(width, height, 0.25f), color, shader);
            totalObjects++;
        }
    }

    private void CreateBox(Transform parent, Vector3 localPos, Quaternion localRot, Vector3 size, Color color, Shader shader)
    {
        GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
        box.transform.SetParent(parent, false);
        box.transform.localPosition = localPos;
        box.transform.localRotation = localRot;
        box.transform.localScale = size;
        box.GetComponent<MeshRenderer>().material = new Material(shader) { color = color };
        Destroy(box.GetComponent<BoxCollider>());
    }

    private void CreatePolygon(Transform parent, MRUKAnchor anchor, Vector3 houseCenter, Quaternion globalCorrection, Color color, Shader shader, float zOffset)
    {
        var boundary = anchor.PlaneBoundary2D;
        int count = boundary.Count;

        GameObject poly = new GameObject("Poly_" + anchor.Label);
        poly.transform.SetParent(parent, false);

        MeshFilter mf = poly.AddComponent<MeshFilter>();
        MeshRenderer mr = poly.AddComponent<MeshRenderer>();
        mr.material = new Material(shader) { color = color };

        Mesh mesh = new Mesh();
        Vector3[] verts = new Vector3[count];
        int[] tris = new int[(count - 2) * 3];

        for (int i = 0; i < count; i++)
        {
            Vector3 worldPos = anchor.transform.TransformPoint(new Vector3(boundary[i].x, boundary[i].y, 0));
            // Apply zOffset in world space along anchor normal or just locally? 
            // Locally is safer:
            Vector3 localWithZ = new Vector3(boundary[i].x, boundary[i].y, zOffset);
            Vector3 worldWithZ = anchor.transform.TransformPoint(localWithZ);
            verts[i] = globalCorrection * (worldWithZ - houseCenter);
        }

        for (int i = 0; i < count - 2; i++)
        {
            tris[i * 3] = 0;
            tris[i * 3 + 1] = i + 1;
            tris[i * 3 + 2] = i + 2;
        }

        mesh.vertices = verts;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        mf.mesh = mesh;
    }
    }
