using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// The cursor at the end of the pointing ray: a small ring with a dot on anything clickable, and a bigger ring with four
/// arrows over anything the grip can drag (the menu / floor plan / minimap panels, the Dollhouse) - it fills in while
/// the grip is held. Always the same size to the eye, billboarded, never a collider (so nothing raycasts onto it).
/// </summary>
[DefaultExecutionOrder(100)]
public class RayCursor : MonoBehaviour
{
    public static void Attach(XRRayInteractor ray)
    {
        if (ray == null || ray.GetComponent<RayCursor>() != null) return;
        ray.gameObject.AddComponent<RayCursor>().ray = ray;
    }

    XRRayInteractor ray;
    Transform quad;
    Material mat;
    Texture2D clickTex, dragTex;
    float size, tint;                 // smoothed
    bool wasDrag;

    static readonly Color Blue = new Color(0.30f, 0.62f, 1f), Green = new Color(0.30f, 0.95f, 0.45f);

    void OnEnable() { if (quad) quad.gameObject.SetActive(false); }
    void OnDisable() { if (quad) quad.gameObject.SetActive(false); }

    void OnDestroy()
    {
        if (quad) Destroy(quad.gameObject);
        if (mat) Destroy(mat);
        if (clickTex) Destroy(clickTex);
        if (dragTex) Destroy(dragTex);
    }

    bool Build()
    {
        var shader = Shader.Find("Sprites/Default");
        if (shader == null) return false;
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        go.name = "RayCursor";
        Destroy(go.GetComponent<Collider>());
        mat = new Material(shader);
        go.GetComponent<Renderer>().sharedMaterial = mat;
        quad = go.transform;
        clickTex = MakeTexture(false);
        dragTex = MakeTexture(true);
        return true;
    }

    /// <summary>Anti-aliased ring + centre dot; with arrows: the four-way "move" glyph.</summary>
    static Texture2D MakeTexture(bool arrows)
    {
        const int N = 128; const float C = N / 2f;
        var tex = new Texture2D(N, N, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear };
        var px = new Color32[N * N];
        float ringR = arrows ? 34f : 46f, ringW = arrows ? 3.2f : 4.5f;
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                var p = new Vector2(x + 0.5f - C, y + 0.5f - C);
                float len = p.magnitude;
                float sd = Mathf.Min(Mathf.Abs(len - ringR) - ringW, len - (arrows ? 4.5f : 7f));
                if (arrows)
                    foreach (var d in new[] { Vector2.up, Vector2.down, Vector2.left, Vector2.right })
                    {
                        float t = Vector2.Dot(p, d), perp = Mathf.Abs(p.x * d.y - p.y * d.x);
                        sd = Mathf.Min(sd, Mathf.Max(perp - (60f - t) * 0.8f, Mathf.Max(41f - t, t - 60f)));
                    }
                float a = Mathf.Clamp01(0.5f - sd);
                px[y * N + x] = new Color(1f, 1f, 1f, a);
            }
        tex.SetPixels32(px);
        tex.Apply(true);
        return tex;
    }

    void LateUpdate()
    {
        if (ray == null) return;
        if (quad == null && !Build()) { enabled = false; return; }
        var cam = Camera.main;
        if (cam == null) { quad.gameObject.SetActive(false); return; }

        Vector3 point = default, normal = Vector3.up;
        bool hit = false, onButton = false;
        if (ray.TryGetCurrentUIRaycastResult(out RaycastResult ui) && ui.gameObject != null)
        {
            hit = true; point = ui.worldPosition; normal = ui.worldNormal;
            onButton = ui.gameObject.GetComponentInParent<Selectable>() != null;
        }
        else if (ray.TryGetCurrent3DRaycastHit(out RaycastHit h)) { hit = true; point = h.point; normal = h.normal; }

        // draggable = the ray hovers something registered with the XR Interaction Toolkit, and it is not a button on it
        bool drag = hit && !onButton && ray.interactablesHovered.Count > 0;
        if (!hit) { quad.gameObject.SetActive(false); return; }
        if (!quad.gameObject.activeSelf) { size = 0f; quad.gameObject.SetActive(true); }

        bool gripHeld = drag && OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch);
        float dist = Vector3.Distance(cam.transform.position, point);
        float target = Mathf.Max(0.012f, dist * (drag ? 0.05f : 0.024f)) * (gripHeld ? 0.82f : 1f);
        float k = 1f - Mathf.Exp(-22f * Time.unscaledDeltaTime);
        size = Mathf.Lerp(size, target, k);
        tint = Mathf.Lerp(tint, drag ? 1f : onButton ? 0.5f : 0f, k);
        if (drag != wasDrag) { mat.mainTexture = drag ? dragTex : clickTex; wasDrag = drag; }
        if (mat.mainTexture == null) mat.mainTexture = clickTex;

        Color c = Color.Lerp(Blue, Green, tint);
        c.a = gripHeld ? 1f : 0.85f;
        mat.color = c;

        Vector3 toCam = (cam.transform.position - point).normalized;
        quad.position = point + toCam * 0.004f;
        quad.rotation = Quaternion.LookRotation(-toCam, cam.transform.up);
        quad.localScale = Vector3.one * size;
    }
}
