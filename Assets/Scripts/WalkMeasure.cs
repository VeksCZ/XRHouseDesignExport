using TMPro;
using UnityEngine;

/// <summary>
/// Tape measure in the walk-through: first click = start point on whatever the ray hits (wall, floor, furniture),
/// the line then follows the ray, second click fixes the end; a third click starts a new one. Shows the length and,
/// when it isn't straight, its horizontal and vertical parts. The points live in the model's own space, so the
/// measurement stays put while you walk, turn or change floors.
/// </summary>
public class WalkMeasure
{
    Transform model;
    GameObject go;
    LineRenderer line;
    Transform dotA, dotB;
    TextMeshPro label;
    Material mat;
    Vector3 a, b;          // model-local
    int state;             // 0 = nothing, 1 = A set (B follows the ray), 2 = done

    public bool Active => state == 1;

    static readonly Color Col = new Color(1f, 0.82f, 0.1f);

    /// <summary>A click: start, finish, or start over. Returns a status line (null if the ray hit nothing).</summary>
    public string Click(Ray ray, Transform modelRoot, int mask)
    {
        if (!Physics.Raycast(ray, out var hit, 30f, mask, QueryTriggerInteraction.Ignore)) return null;
        Ensure(modelRoot);
        Vector3 p = model.InverseTransformPoint(hit.point);
        if (state != 1) { a = p; b = p; state = 1; Redraw(); return "Measure: start set - point at the end and click"; }
        b = p; state = 2; Redraw();
        return "Measure: " + Text(false);
    }

    /// <summary>While the start is set: the end follows the ray.</summary>
    public void Track(Ray ray, int mask, Transform cam)
    {
        if (go && cam) FaceLabel(cam);
        if (state != 1 || !model) return;
        if (Physics.Raycast(ray, out var hit, 30f, mask, QueryTriggerInteraction.Ignore)) { b = model.InverseTransformPoint(hit.point); Redraw(); }
    }

    public void Clear()
    {
        state = 0;
        if (go) Object.Destroy(go);
        if (mat) Object.Destroy(mat);
        go = null; model = null;
    }

    void Ensure(Transform modelRoot)
    {
        if (go && model == modelRoot) return;
        Clear();
        model = modelRoot;
        go = new GameObject("Measure");
        go.transform.SetParent(model, false);
        mat = new Material(Shader.Find("Sprites/Default")) { color = Col };
        line = go.AddComponent<LineRenderer>();
        line.useWorldSpace = false;
        line.positionCount = 2;
        line.widthMultiplier = 0.008f;
        line.sharedMaterial = mat;
        line.numCapVertices = 2;
        dotA = Dot("A"); dotB = Dot("B");
        var t = new GameObject("Label");
        t.transform.SetParent(go.transform, false);
        label = t.AddComponent<TextMeshPro>();
        label.fontSize = 0.9f;
        label.alignment = TextAlignmentOptions.Center;
        label.color = Color.white;
        label.outlineWidth = 0.25f;
        label.outlineColor = new Color32(0, 0, 0, 255);
        label.rectTransform.sizeDelta = new Vector2(1.6f, 0.4f);
        label.enableWordWrapping = false;
    }

    Transform Dot(string name)
    {
        var d = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        d.name = name;
        Object.Destroy(d.GetComponent<Collider>());
        d.transform.SetParent(go.transform, false);
        d.transform.localScale = Vector3.one * 0.025f;
        d.GetComponent<Renderer>().sharedMaterial = mat;
        return d.transform;
    }

    void Redraw()
    {
        if (!go) return;
        line.SetPosition(0, a); line.SetPosition(1, b);
        dotA.localPosition = a; dotB.localPosition = b;
        dotB.gameObject.SetActive(state == 2 || (b - a).sqrMagnitude > 1e-6f);
        label.text = Text(true);
        label.transform.localPosition = (a + b) / 2f + Vector3.up * 0.06f;
    }

    string Text(bool twoLines)
    {
        Vector3 d = b - a;   // model space = metres, y up
        float len = d.magnitude, hor = new Vector2(d.x, d.z).magnitude, ver = Mathf.Abs(d.y);
        string F(float v) => v.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        string s = F(len) + " m";
        if (hor > 0.03f && ver > 0.03f) s += (twoLines ? "\n" : " (") + $"horiz. {F(hor)} / vert. {F(ver)}" + (twoLines ? "" : ")");
        return s;
    }

    void FaceLabel(Transform cam)
    {
        if (!label) return;
        Vector3 dir = label.transform.position - cam.position;
        if (dir.sqrMagnitude > 1e-4f) label.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
        // keep it readable at a distance: ~constant angular size beyond 1.5 m
        float dist = dir.magnitude;
        label.transform.localScale = Vector3.one * Mathf.Max(1f, dist / 1.5f) / Mathf.Max(1e-4f, model ? model.lossyScale.x : 1f);
    }
}
