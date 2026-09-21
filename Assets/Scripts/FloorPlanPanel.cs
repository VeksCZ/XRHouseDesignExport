using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>Draws a FloorPlanPage's lines as UI quads, fitted into its rect. Text is placed by the panel.</summary>
public class PlanGraphic : MaskableGraphic
{
    FloorPlanPage page;
    Vector2 center;
    float scale = 1f;

    public float Scale => scale;

    public void SetPage(FloorPlanPage p)
    {
        page = p;
        Layout();
        SetVerticesDirty();
    }

    /// <summary>Plan metres to this graphic's local canvas units (origin at the rect centre, y up).</summary>
    public Vector2 ToLocal(Vector2 p) => (p - center) * scale;

    void Layout()
    {
        if (page == null) return;
        Rect b = page.Bounds();
        Rect r = rectTransform.rect;
        const float pad = 0.8f; // metres of margin around the plan
        scale = Mathf.Max(1f, Mathf.Min(r.width / (b.width + pad), r.height / (b.height + pad)));
        center = b.center;
    }

    protected override void OnRectTransformDimensionsChange()
    {
        base.OnRectTransformDimensionsChange();
        Layout();
    }

    static (float meters, float minPx, Color32 color) StyleOf(PlanStyle s)
    {
        switch (s)
        {
            case PlanStyle.Wall: return (0.12f, 3f, new Color32(235, 235, 240, 255));
            case PlanStyle.Door: return (0.14f, 4f, new Color32(240, 165, 70, 255));
            case PlanStyle.Window: return (0.14f, 4f, new Color32(90, 190, 255, 255));
            case PlanStyle.Tick: return (0.03f, 2f, new Color32(255, 110, 110, 255));
            case PlanStyle.Extension: return (0.01f, 1f, new Color32(255, 110, 110, 160));
            default: return (0.015f, 1.5f, new Color32(255, 110, 110, 255));
        }
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        if (page == null) return;
        // Mesh vertices are relative to the rect's pivot, but ToLocal (and the text objects, which are anchored to the
        // rect centre) measure from the centre - shift by the centre so lines and dimension text coincide.
        Vector2 origin = rectTransform.rect.center;
        foreach (var l in page.lines)
        {
            var (meters, minPx, lineColor) = StyleOf(l.style);
            AddLine(vh, ToLocal(l.a) + origin, ToLocal(l.b) + origin, Mathf.Max(minPx, meters * scale), lineColor, l.style == PlanStyle.Wall);
        }
    }

    static void AddLine(VertexHelper vh, Vector2 a, Vector2 b, float width, Color32 color, bool extend)
    {
        Vector2 d = b - a;
        if (d.sqrMagnitude < 1e-6f) return;
        d.Normalize();
        if (extend) { a -= d * width / 2f; b += d * width / 2f; } // square caps so wall corners close cleanly
        Vector2 n = new Vector2(-d.y, d.x) * (width / 2f);
        int i = vh.currentVertCount;
        vh.AddVert(a - n, color, Vector2.zero);
        vh.AddVert(a + n, color, Vector2.zero);
        vh.AddVert(b + n, color, Vector2.zero);
        vh.AddVert(b - n, color, Vector2.zero);
        vh.AddTriangle(i, i + 1, i + 2);
        vh.AddTriangle(i + 2, i + 3, i);
    }
}

/// <summary>
/// In-headset floor plan viewer: a world-space panel in front of the user that pages through the
/// plan sheets (overview per floor, then each room) - the same sheets as the HTML report. Clicked
/// with the same XRI ray as the wrist menu.
/// </summary>
public class FloorPlanPanel : MonoBehaviour
{
    const float PanelDistance = 1.1f;
    const float Width = 1400f, Height = 900f;

    Canvas canvas;
    PlanGraphic graphic;
    TMP_Text titleText, subtitleText, pageText;
    readonly List<TextMeshProUGUI> textPool = new List<TextMeshProUGUI>();
    List<FloorPlanPage> pages = new List<FloorPlanPage>();
    string sourceName = "";
    int index;
    Button nameButton;

    /// <summary>Rebuilds the sheets after a room was renamed (the names are baked into them).</summary>
    public System.Func<List<FloorPlanPage>> Rebuild;

    public bool IsOpen => canvas != null && canvas.gameObject.activeSelf;

    public void Show(List<FloorPlanPage> newPages, string source)
    {
        if (newPages == null || newPages.Count == 0) return;
        if (canvas == null) Build();

        pages = newPages;
        sourceName = source;
        index = 0;
        Place();
        canvas.gameObject.SetActive(true);
        ShowPage();
    }

    public void Hide()
    {
        if (canvas != null) canvas.gameObject.SetActive(false);
    }

    public void Step(int direction)
    {
        if (pages.Count == 0) return;
        index = ((index + direction) % pages.Count + pages.Count) % pages.Count;
        ShowPage();
    }

    void Place()
    {
        var cam = Camera.main;
        if (cam == null) return;
        Vector3 fwd = Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up);
        fwd = fwd.sqrMagnitude < 0.01f ? Vector3.forward : fwd.normalized;
        canvas.transform.SetPositionAndRotation(cam.transform.position + fwd * PanelDistance + Vector3.down * 0.05f, Quaternion.LookRotation(fwd, Vector3.up));
        canvas.worldCamera = cam;
    }

    void ShowPage()
    {
        var page = pages[index];
        titleText.text = $"{page.title}  <size=60%><color=#9aa5b5>{sourceName}</color></size>";
        subtitleText.text = page.subtitle;
        pageText.text = $"{index + 1} / {pages.Count}";
        graphic.SetPage(page);
        RefreshTexts(page);
        nameButton.gameObject.SetActive(!string.IsNullOrEmpty(page.roomKey));
    }

    /// <summary>Quest never shares its room names with apps, so the name is picked here: each click steps through the presets.</summary>
    void RenameCurrent()
    {
        string key = pages[index].roomKey;
        if (string.IsNullOrEmpty(key) || Rebuild == null) return;
        RoomNames.Cycle(key);
        var fresh = Rebuild();
        if (fresh == null || fresh.Count == 0) return;
        pages = fresh;
        index = Mathf.Clamp(index, 0, pages.Count - 1);
        ShowPage();
    }

    void RefreshTexts(FloorPlanPage page)
    {
        while (textPool.Count < page.texts.Count) textPool.Add(NewText());
        for (int i = 0; i < textPool.Count; i++)
        {
            bool used = i < page.texts.Count;
            textPool[i].gameObject.SetActive(used);
            if (!used) continue;

            var t = page.texts[i];
            var tmp = textPool[i];
            tmp.text = t.text;
            tmp.fontSize = Mathf.Max(16f, t.size * graphic.Scale);
            tmp.fontStyle = t.bold ? FontStyles.Bold : FontStyles.Normal;
            tmp.color = t.style == PlanStyle.Label ? Color.white : new Color(1f, 0.55f, 0.55f);
            tmp.rectTransform.anchoredPosition = graphic.ToLocal(t.pos);
            tmp.rectTransform.localRotation = Quaternion.Euler(0, 0, t.angleDeg);
        }
    }

    TextMeshProUGUI NewText()
    {
        var go = new GameObject("PlanText", typeof(RectTransform));
        go.layer = canvas.gameObject.layer;
        go.transform.SetParent(graphic.transform, false);
        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.raycastTarget = false;
        tmp.overflowMode = TextOverflowModes.Overflow;
        var r = tmp.rectTransform;
        r.anchorMin = r.anchorMax = r.pivot = new Vector2(0.5f, 0.5f);
        r.sizeDelta = new Vector2(420f, 60f);
        return tmp;
    }

    void Build()
    {
        canvas = XRUi.CreateWorldCanvas("FloorPlanPanel", new Vector2(Width, Height), 0.001f);
        var root = canvas.transform;

        XRUi.CreatePanel(root, "Background", XRUi.PanelColor, 0, 0, Width, Height);
        titleText = XRUi.CreateText(root, "Title", "", 44, TextAlignmentOptions.MidlineLeft, 30, 14, 1000, 70, XRUi.TextColor, FontStyles.Bold);
        subtitleText = XRUi.CreateText(root, "Subtitle", "", 24, TextAlignmentOptions.MidlineLeft, 30, 84, 1340, 44, XRUi.MutedText);

        var planGo = new GameObject("Plan", typeof(RectTransform));
        planGo.layer = root.gameObject.layer;
        planGo.transform.SetParent(root, false);
        graphic = planGo.AddComponent<PlanGraphic>();
        graphic.raycastTarget = false;
        XRUi.Place((RectTransform)planGo.transform, 30, 140, Width - 60, Height - 140 - 130);

        XRUi.CreateButton(root, "<", 360, Height - 110, 200, 90, () => Step(-1), 44);
        pageText = XRUi.CreateText(root, "Page", "", 40, TextAlignmentOptions.Center, 580, Height - 110, 240, 90, XRUi.TextColor, FontStyles.Bold);
        XRUi.CreateButton(root, ">", 840, Height - 110, 200, 90, () => Step(1), 44);
        XRUi.CreateButton(root, "Close", Width - 250, 20, 220, 80, Hide, 34);
        nameButton = XRUi.CreateButton(root, "Rename room", Width - 470, Height - 110, 440, 90, RenameCurrent, 34);

        canvas.gameObject.SetActive(false);
    }
}
