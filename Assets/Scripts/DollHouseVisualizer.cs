using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
#if META_XR_SDK_INSTALLED
using Meta.XR.MRUtilityKit;
#endif

public class DollHouseVisualizer : MonoBehaviour {
    public float scale = 0.05f; // 1:20 scale
    public Material wallMaterial;
    public Material floorMaterial;
    public Material doorMaterial;
    public Material windowMaterial;

    [ContextMenu("Generate Dollhouse")]
    public void Generate() {
#if META_XR_SDK_INSTALLED
        // Clean existing
        for (int i = transform.childCount - 1; i >= 0; i--) {
            DestroyImmediate(transform.GetChild(i).gameObject);
        }

        if (MRUK.Instance == null) return;

        foreach (var room in MRUK.Instance.Rooms) {
            GameObject roomRoot = new GameObject("Room_" + room.name);
            roomRoot.transform.SetParent(this.transform);
            roomRoot.transform.localPosition = Vector3.zero;
            roomRoot.transform.localRotation = Quaternion.identity;
            roomRoot.transform.localScale = Vector3.one;

            foreach (var anchor in room.Anchors) {
                if (!anchor.PlaneRect.HasValue) continue;

                string label = anchor.Label.ToString().ToUpper();
                Vector3 size = Vector3.one;
                Material mat = wallMaterial;

                if (label.Contains("WALL")) {
                    size = new Vector3(anchor.PlaneRect.Value.width, anchor.PlaneRect.Value.height, 0.25f);
                    mat = wallMaterial;
                } else if (label.Contains("FLOOR")) {
                    size = new Vector3(anchor.PlaneRect.Value.width, anchor.PlaneRect.Value.height, 0.1f);
                    mat = floorMaterial;
                } else if (label.Contains("DOOR")) {
                    size = new Vector3(anchor.PlaneRect.Value.width, anchor.PlaneRect.Value.height, 0.15f);
                    mat = doorMaterial;
                } else if (label.Contains("WINDOW")) {
                    size = new Vector3(anchor.PlaneRect.Value.width, anchor.PlaneRect.Value.height, 0.15f);
                    mat = windowMaterial;
                } else continue;

                GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
                box.name = label;
                box.transform.SetParent(roomRoot.transform);
                
                // Position and rotation relative to MRUK Room
                box.transform.position = anchor.transform.position;
                box.transform.rotation = anchor.transform.rotation;
                box.transform.localScale = size;

                if (mat != null) box.GetComponent<Renderer>().sharedMaterial = mat;
                
                // Apply global scale of the dollhouse
                box.transform.position = transform.position + (box.transform.position - room.transform.position) * scale;
                box.transform.localScale *= scale;
            }
        }
#endif
    }
}
