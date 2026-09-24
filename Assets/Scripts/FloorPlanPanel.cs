using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

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

    // Guide/extension lines and ticks stay red on both inner and outer dimensions - only the dimension line
    // itself (where the number sits) tells the two apart: purple inside, green outside (matches the HTML report).
    static (float meters, float minPx, Color32 color) StyleOf(PlanStyle s)
    {
        switch (s)
        {
            case PlanStyle.Wall: return (0.12f, 3f, new Color32(235, 235, 240, 255));
            case PlanStyle.Door: return (0.14f, 4f, new Color32(240, 165, 70, 255));
            case PlanStyle.Window: return (0.14f, 4f, new Color32(90, 190, 255, 255));
            case PlanStyle.Tick: return (0.03f, 2f, new Color32(255, 110, 110, 255));
            case PlanStyle.Extension: return (0.01f, 1f, new Color32(255, 110, 110, 160));
            case PlanStyle.OuterTick: return (0.03f, 2f, new Color32(255, 110, 110, 255));
            case PlanStyle.OuterExtension: return (0.01f, 1f, new Color32(255, 110, 110, 160));
            case PlanStyle.OuterDimension: return (0.015f, 1.5f, new Color32(90, 215, 140, 255));
            default: return (0.015f, 1.5f, new Color32(128, 90, 213, 255));
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
    const float HeaderH = 50f; // the draggable handle strip at the very top
    const float Width = 1400f, Height = 900f + HeaderH;

    Canvas canvas;
    PlanGraphic graphic;
    TMP_Text titleText, subtitleText, pageText;
    readonly List<TextMeshProUGUI> textPool = new List<TextMeshProUGUI>();
    List<FloorPlanPage> pages = new List<FloorPlanPage>();
    List<FloorPlanPage> pagesExact = new List<FloorPlanPage>();
    List<FloorPlanPage> pagesAdjusted = new List<FloorPlanPage>();
    bool useAdjusted;
    string sourceName = "";
    int index;
    Button nameButton, modeButton;
    // Grabbable by its own handle bar (not just anywhere on the panel, so it can't be dragged by accident while
    // clicking a button), like the Dollhouse/wrist menu/Minimap - so it can be dragged aside instead of sitting
    // fixed in front of you and blocking the wrist menu, which used to hide itself entirely while this was open.
    GameObject handle;
    Transform rightHand;
    bool grabbed;
    Vector3 grabOff;
    Quaternion grabRotOff;

    /// <summary>Rebuilds the sheets after a room was renamed (the names are baked into them).</summary>
    public System.Func<(List<FloorPlanPage> exact, List<FloorPlanPage> adjusted)> Rebuild;

    public bool IsOpen => canvas != null && canvas.gameObject.activeSelf;
    public bool IsDragging => grabbed;

    /// <summary>
    /// "Exact" shows the room's true measured shape; "Adjusted" cleans up small corner jitter to real right
    /// angles (see FloorPlanBuilder.Rectified) - a real angled wall stays as measured either way.
    /// </summary>
    public void Show((List<FloorPlanPage> exact, List<FloorPlanPage> adjusted) newPages, string source)
    {
        if (newPages.exact == null || newPages.exact.Count == 0) return;
        bool firstTime = canvas == null;
        if (firstTime) Build();

        pagesExact = newPages.exact;
        pagesAdjusted = newPages.adjusted != null && newPages.adjusted.Count > 0 ? newPages.adjusted : newPages.exact;
        pages = useAdjusted ? pagesAdjusted : pagesExact;
        sourceName = source;
        index = 0;
        // Only place it in front of you the very first time - reopening (or renaming a room, which calls
        // Show() again internally) keeps wherever it was last dragged to, same as the Dollhouse.
        if (firstTime) Place();
        canvas.gameObject.SetActive(true);
        ShowPage();
    }

    void ToggleMode()
    {
        useAdjusted = !useAdjusted;
        XRUi.SetLabel(modeButton, useAdjusted ? "Adjusted" : "Exact");
        pages = useAdjusted ? pagesAdjusted : pagesExact;
        index = Mathf.Clamp(index, 0, pages.Count - 1);
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
        if (fresh.exact == null || fresh.exact.Count == 0) return;
        pagesExact = fresh.exact;
        pagesAdjusted = fresh.adjusted != null && fresh.adjusted.Count > 0 ? fresh.adjusted : fresh.exact;
        pages = useAdjusted ? pagesAdjusted : pagesExact;
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
            // Room/area labels keep a 16px legibility floor; dimension numbers get a lower one (~30% smaller
            // overall, matching FloorPlanBuilder.DimTextSize) since they sit close together and were overlapping.
            tmp.fontSize = Mathf.Max(t.style == PlanStyle.Label ? 16f : 11f, t.size * graphic.Scale);
            tmp.fontStyle = t.bold ? FontStyles.Bold : FontStyles.Normal;
            // Dimension numbers are a neutral pale yellow - distinct from both the red (inner) and green (outer)
            // dimension lines they sit on, so they stay readable regardless of which one they're next to.
            tmp.color = t.style == PlanStyle.Label ? Color.white : new Color(1f, 0.92f, 0.55f);
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

        // A dedicated drag handle strip at the very top - grip while pointing at THIS bar, not just anywhere on
        // the panel, moves the whole thing, so it can't be dragged by accident while clicking a button below.
        // XRSimpleInteractable registers it with the XR Interaction Toolkit purely so the ray hovers it as a
        // valid target (turns green - see XRMenu.StyleRay); the actual drag is still the same custom
        // grip+raycast approach as the Dollhouse/wrist menu/Minimap, wired up in Update().
        var handleGo = XRUi.CreatePanel(root, "Handle", XRUi.ButtonToggleColor, 0, 0, Width, HeaderH);
        XRUi.CreateText(root, "HandleLabel", "≡ drag to move", 20, TextAlignmentOptions.Center, 0, 0, Width, HeaderH, XRUi.MutedText);
        var handleRt = (RectTransform)handleGo.transform;
        var handleCol = handleGo.gameObject.AddComponent<BoxCollider>();
        handleCol.center = handleRt.rect.center;
        handleCol.size = new Vector3(handleRt.rect.width, handleRt.rect.height, 50f);
        handleGo.gameObject.AddComponent<XRSimpleInteractable>();
        handle = handleGo.gameObject;

        titleText = XRUi.CreateText(root, "Title", "", 44, TextAlignmentOptions.MidlineLeft, 30, 14 + HeaderH, 1000, 70, XRUi.TextColor, FontStyles.Bold);
        subtitleText = XRUi.CreateText(root, "Subtitle", "", 24, TextAlignmentOptions.MidlineLeft, 30, 84 + HeaderH, 1340, 44, XRUi.MutedText);

        var planGo = new GameObject("Plan", typeof(RectTransform));
        planGo.layer = root.gameObject.layer;
        planGo.transform.SetParent(root, false);
        graphic = planGo.AddComponent<PlanGraphic>();
        graphic.raycastTarget = false;
        // Coordinates are top-left based with y growing downwards (see XRUi.Place), so the two button
        // rows below need to sit at the LARGEST y values to actually land near the bottom of the panel -
        // they previously used y=20, which is right under the title/subtitle at the top and overlapped them.
        XRUi.Place((RectTransform)planGo.transform, 30, 140 + HeaderH, Width - 60, Height - 140 - HeaderH - 230);

        modeButton = XRUi.CreateButton(root, useAdjusted ? "Adjusted" : "Exact", 30, Height - 220, 300, 90, ToggleMode, 30);
        XRUi.CreateButton(root, "<", 360, Height - 220, 200, 90, () => Step(-1), 44);
        pageText = XRUi.CreateText(root, "Page", "", 40, TextAlignmentOptions.Center, 580, Height - 220, 240, 90, XRUi.TextColor, FontStyles.Bold);
        XRUi.CreateButton(root, ">", 840, Height - 220, 200, 90, () => Step(1), 44);
        // Bottom row, below the page-nav row above it.
        nameButton = XRUi.CreateButton(root, "Rename room", 30, Height - 110, 440, 80, RenameCurrent, 34);
        XRUi.CreateButton(root, "Close", Width - 250, Height - 110, 220, 80, Hide, 34);

        canvas.gameObject.SetActive(false);
    }

    void Update()
    {
        if (!IsOpen) return;
        if (!rightHand)
        {
            var rig = FindFirstObjectByType<OVRCameraRig>();
            rightHand = rig ? rig.rightHandAnchor : null;
            if (!rightHand) return;
        }

        bool grip = OVRInput.Get(OVRInput.Button.PrimaryHandTrigger, OVRInput.Controller.RTouch);
        if (!grabbed)
        {
            if (grip && Physics.Raycast(rightHand.position, rightHand.forward, out RaycastHit hit) && hit.collider.gameObject == handle)
            {
                grabbed = true;
                OVRInput.SetControllerVibration(0.1f, 0.1f, OVRInput.Controller.RTouch); Invoke(nameof(StopVib), 0.05f);
                grabOff = rightHand.InverseTransformPoint(canvas.transform.position);
                grabRotOff = Quaternion.Inverse(rightHand.rotation) * canvas.transform.rotation;
            }
        }
        else
        {
            if (!grip) { grabbed = false; return; }
            canvas.transform.position = rightHand.TransformPoint(grabOff);
            canvas.transform.rotation = rightHand.rotation * grabRotOff;
        }
    }

    void StopVib() => OVRInput.SetControllerVibration(0, 0, OVRInput.Controller.RTouch);
}
