using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

/// <summary>
/// Small factory for the app's world-space UI - one look for every panel: rounded dark glass panels, rounded buttons
/// that light up and get a thin outline while the ray is on them (hover), and toggles that carry a switch on their
/// right edge (grey = off, green = on) - so "pointed at" and "switched on" can never be mistaken for each other.
/// Everything is positioned explicitly (top-left based, in canvas units) instead of using layout groups, so there is
/// nothing to rebuild at runtime and the result is identical every time.
/// Callers keep using SetTint with the colour constants below; XRUiButton turns them into states (on / off toggle /
/// tab / warning / danger / disabled) and draws them.
/// </summary>
public static class XRUi
{
    // ---- palette ----
    public static readonly Color PanelColor = new Color(0.075f, 0.09f, 0.12f, 0.95f);
    public static readonly Color InsetColor = new Color(1f, 1f, 1f, 0.045f);
    public static readonly Color ButtonColor = new Color(0.16f, 0.19f, 0.25f, 1f);
    public static readonly Color ButtonDisabled = new Color(0.11f, 0.13f, 0.17f, 1f);
    /// <summary>SetTint: the button is ON (a toggle that is on, the active tool, the picked option).</summary>
    public static readonly Color ButtonOnColor = new Color(0.16f, 0.45f, 0.95f, 1f);
    /// <summary>SetTint: not picked / off (same look as ButtonColor; a button made a toggle with SetToggle shows its switch).</summary>
    public static readonly Color ButtonToggleColor = new Color(0.16f, 0.19f, 0.25f, 1f);
    public static readonly Color ButtonWarnColor = new Color(0.82f, 0.47f, 0.12f, 1f);
    public static readonly Color ButtonDangerColor = new Color(0.42f, 0.15f, 0.20f, 1f);
    /// <summary>SetTint: the selected tab of a tab row.</summary>
    public static readonly Color AccentDark = new Color(0.16f, 0.45f, 0.95f, 1f);
    public static readonly Color TextColor = new Color(0.94f, 0.95f, 0.97f, 1f);
    public static readonly Color MutedText = new Color(0.62f, 0.67f, 0.76f, 1f);
    public static readonly Color AccentText = new Color(0.50f, 0.78f, 1f, 1f);
    public static readonly Color SwitchOn = new Color(0.20f, 0.78f, 0.42f, 1f);
    public static readonly Color SwitchOff = new Color(0.30f, 0.34f, 0.42f, 1f);

    /// <summary>Corner radius of panels / buttons (canvas units).</summary>
    public const float PanelRadius = 26f, ButtonRadius = 14f;

    /// <summary>A world-space canvas the XR UI ray (XRUIInputModule) can hit. 'scale' is metres per canvas unit.</summary>
    public static Canvas CreateWorldCanvas(string name, Vector2 size, float scale, Transform parent = null)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(TrackedDeviceGraphicRaycaster));
        go.layer = LayerMask.NameToLayer("UI");
        if (parent != null) go.transform.SetParent(parent, false);

        var canvas = go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = Camera.main;
        canvas.referencePixelsPerUnit = 100f;

        var rt = (RectTransform)go.transform;
        rt.sizeDelta = size;
        rt.localScale = Vector3.one * scale;
        return canvas;
    }

    /// <summary>
    /// Puts a world-space panel once in front of the viewer - level (yaw only, so looking at the floor doesn't put it
    /// on the floor), 'drop' metres below eye height, 'side' metres to the right (negative = left), facing the eyes.
    /// Panels then stay where they are (world-locked) instead of following the head.
    /// </summary>
    public static void PlaceInFront(Transform t, Transform head, float dist, float drop, float side = 0f)
    {
        if (t == null || head == null) return;
        Vector3 fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up);
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.ProjectOnPlane(head.up, Vector3.up);
        fwd.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, fwd);
        Vector3 pos = head.position + fwd * dist - Vector3.up * drop + right * side;
        t.SetPositionAndRotation(pos, Quaternion.LookRotation(pos - head.position, Vector3.up));
    }

    /// <summary>True when a placed panel has been left behind (you walked away) and should come back in front.</summary>
    public static bool LeftBehind(Transform t, Transform head, float maxDist = 2.5f) =>
        t != null && head != null && !XRMenu.PointingAt(t) // never jump away from under the ray you're about to click with
        && (Vector3.Distance(t.position, head.position) > maxDist
            // or far above/below the eyes: placed before tracking had settled (startup, recenter) - it ended up ~1 m
            // under the floor-level origin while the head was still at 0
            || Mathf.Abs(t.position.y - head.position.y) > 0.9f);

    /// <summary>Top-left anchored placement: x/y measured from the parent's top-left corner, y growing downwards.</summary>
    public static void Place(RectTransform rt, float x, float y, float width, float height)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(width, height);
    }

    // ---------- rounded shapes ----------

    const int SpriteSize = 64, SpriteRadius = 30, RingWidth = 3;
    static Sprite fillSprite, ringSprite, circleSprite;

    /// <summary>A 9-sliced rounded rectangle (filled, or a thin ring for outlines), generated once.</summary>
    static Sprite RoundSprite(bool ring)
    {
        ref Sprite s = ref ring ? ref ringSprite : ref fillSprite;
        if (s) return s;
        var tex = new Texture2D(SpriteSize, SpriteSize, TextureFormat.RGBA32, false) { name = ring ? "UiRing" : "UiRound", wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[SpriteSize * SpriteSize];
        float c = SpriteSize / 2f;
        for (int y = 0; y < SpriteSize; y++)
            for (int x = 0; x < SpriteSize; x++)
            {
                // signed distance to a rounded square of half size c, radius SpriteRadius
                float qx = Mathf.Abs(x + 0.5f - c) - (c - SpriteRadius), qy = Mathf.Abs(y + 0.5f - c) - (c - SpriteRadius);
                float d = new Vector2(Mathf.Max(qx, 0), Mathf.Max(qy, 0)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0) - SpriteRadius;
                float a = Mathf.Clamp01(0.5f - d);                                  // inside, antialiased edge
                if (ring) a = Mathf.Min(a, Mathf.Clamp01(d + RingWidth + 0.5f));    // only the outer RingWidth px
                px[y * SpriteSize + x] = new Color32(255, 255, 255, (byte)(a * 255));
            }
        tex.SetPixels32(px); tex.Apply(false, true);
        float b = SpriteRadius + 1;
        s = Sprite.Create(tex, new Rect(0, 0, SpriteSize, SpriteSize), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(b, b, b, b));
        s.name = tex.name;
        return s;
    }

    static Sprite Circle
    {
        get
        {
            if (circleSprite) return circleSprite;
            const int n = 48;
            var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "UiCircle", wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = new Vector2(x + 0.5f - n / 2f, y + 0.5f - n / 2f).magnitude - (n / 2f - 1);
                    px[y * n + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(0.5f - d) * 255));
                }
            tex.SetPixels32(px); tex.Apply(false, true);
            circleSprite = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
            return circleSprite;
        }
    }

    /// <summary>Gives an image rounded corners of radius r (canvas units), clamped to half its smaller side.</summary>
    public static void Round(Image img, float r, bool ring = false)
    {
        if (img == null) return;
        var size = ((RectTransform)img.transform).rect.size;
        if (size.x <= 0 || size.y <= 0) size = ((RectTransform)img.transform).sizeDelta;
        float max = Mathf.Max(1f, Mathf.Min(size.x, size.y) / 2f);
        r = Mathf.Clamp(r, 1f, max);
        img.sprite = RoundSprite(ring);
        img.type = Image.Type.Sliced;
        img.pixelsPerUnitMultiplier = (SpriteRadius + 1) / r;
    }

    public static Image CreatePanel(Transform parent, string name, Color color, float x, float y, float width, float height)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        go.layer = parent.gameObject.layer;
        go.transform.SetParent(parent, false);
        var image = go.GetComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
        Place((RectTransform)go.transform, x, y, width, height);
        // big panels: the panel radius and a hairline edge; small bits (bars, chips): fully rounded ends
        bool big = width >= 200 && height >= 120;
        Round(image, big ? PanelRadius : Mathf.Min(width, height) / 2f);
        if (big && color.a > 0.5f)
        {
            var edge = new GameObject("Edge", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            edge.layer = go.layer;
            edge.transform.SetParent(go.transform, false);
            var ei = edge.GetComponent<Image>();
            ei.color = new Color(1f, 1f, 1f, 0.10f);
            ei.raycastTarget = false;
            var ert = (RectTransform)edge.transform;
            ert.anchorMin = Vector2.zero; ert.anchorMax = Vector2.one; ert.offsetMin = ert.offsetMax = Vector2.zero;
            Round(ei, PanelRadius, ring: true);
        }
        return image;
    }

    /// <summary>A lighter rounded area that groups a few controls (a section of a panel).</summary>
    public static Image CreateInset(Transform parent, float x, float y, float width, float height)
    {
        var img = CreatePanel(parent, "Inset", InsetColor, x, y, width, height);
        Round(img, 18f);
        return img;
    }

    /// <summary>A small upper-case caption above a group of controls.</summary>
    public static TMP_Text CreateCaption(Transform parent, string text, float x, float y, float width) =>
        CreateText(parent, "Caption", text.ToUpperInvariant(), 16, TextAlignmentOptions.BottomLeft, x + 6, y - 4, width, 24, MutedText, FontStyles.Bold);

    public static TMP_Text CreateText(Transform parent, string name, string text, float fontSize, TextAlignmentOptions alignment,
        float x, float y, float width, float height, Color? color = null, FontStyles style = FontStyles.Normal)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.layer = parent.gameObject.layer;
        go.transform.SetParent(parent, false);
        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.fontStyle = style;
        tmp.alignment = alignment;
        tmp.color = color ?? TextColor;
        tmp.raycastTarget = false;
        tmp.overflowMode = TextOverflowModes.Ellipsis;
        tmp.richText = true;
        Place((RectTransform)go.transform, x, y, width, height);
        return tmp;
    }

    public static Button CreateButton(Transform parent, string label, float x, float y, float width, float height,
        UnityAction onClick, float fontSize = 26f)
    {
        var go = new GameObject("Btn_" + label, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
        go.layer = parent.gameObject.layer;
        go.transform.SetParent(parent, false);
        Place((RectTransform)go.transform, x, y, width, height);

        var image = go.GetComponent<Image>();
        image.color = ButtonColor;
        image.raycastTarget = true;
        Round(image, ButtonRadius);

        var button = go.GetComponent<Button>();
        button.targetGraphic = image;
        button.transition = Selectable.Transition.None; // XRUiButton draws hover / on / pressed itself
        var nav = button.navigation; nav.mode = Navigation.Mode.None; button.navigation = nav;
        if (onClick != null) button.onClick.AddListener(onClick);

        var text = CreateText(go.transform, "Label", label, fontSize, TextAlignmentOptions.Center, 0, 0, width, height);
        var textRect = text.rectTransform;
        textRect.anchorMin = Vector2.zero; textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(10, 0); textRect.offsetMax = new Vector2(-10, 0);
        text.lineSpacing = -12f;

        var vis = go.AddComponent<XRUiButton>();
        vis.Init(image, text);
        return button;
    }

    public static void SetLabel(Button button, string label)
    {
        var tmp = button != null ? button.GetComponentInChildren<TMP_Text>() : null;
        if (tmp != null && tmp.text != label) tmp.text = label;
    }

    /// <summary>Sets a button's resting state through the colour constants above: ButtonOnColor = on / picked,
    /// ButtonToggleColor and ButtonColor = off / plain (toggles made with SetToggle keep their switch),
    /// AccentDark = the selected tab, ButtonWarnColor / ButtonDangerColor / ButtonDisabled as named; any other colour
    /// is used as the button's own fill (e.g. the route kinds' colours).</summary>
    public static void SetTint(Button button, Color color)
    {
        if (button == null) return;
        var vis = button.GetComponent<XRUiButton>();
        if (vis == null) { if (button.targetGraphic) button.targetGraphic.color = color; return; }
        // a switch only on buttons made toggles with SetToggle; otherwise On = picked/active (blue), else plain
        if (color == ButtonOnColor) { if (vis.IsToggle) vis.SetToggle(true); else vis.SetOn(true); }
        else if (color == ButtonToggleColor || color == ButtonColor) { if (vis.IsToggle) vis.SetToggle(false); else vis.SetPlain(); }
        else if (color == ButtonDisabled) vis.SetStyle(XRUiButton.Style.Disabled);
        else if (color == ButtonWarnColor) vis.SetStyle(XRUiButton.Style.Warn);
        else if (color == ButtonDangerColor) vis.SetStyle(XRUiButton.Style.Danger);
        else vis.SetCustom(color);
    }

    /// <summary>An on/off toggle: the switch shows the state, the fill stays calm (hover is the outline).</summary>
    public static void SetToggle(Button button, bool on)
    {
        var vis = button != null ? button.GetComponent<XRUiButton>() : null;
        if (vis != null) vis.SetToggle(on);
    }

    // ---------- icons (drawn here - the UI font has no symbols) ----------

    public enum Icon { ChevronDown, ChevronLeft, ChevronRight, Trash, Close }
    static readonly System.Collections.Generic.Dictionary<Icon, Sprite> icons = new System.Collections.Generic.Dictionary<Icon, Sprite>();

    static float SegDist(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a; float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
        return (a + ab * t - p).magnitude;
    }

    static Sprite IconSprite(Icon kind)
    {
        if (icons.TryGetValue(kind, out var sp) && sp) return sp;
        const int n = 64;
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, false) { name = "UiIcon_" + kind, wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                float d; // distance outside the shape (<= 0 inside)
                switch (kind)
                {
                    case Icon.ChevronDown: d = Mathf.Min(SegDist(p, new Vector2(14, 40), new Vector2(32, 22)), SegDist(p, new Vector2(32, 22), new Vector2(50, 40))) - 4f; break;
                    case Icon.ChevronLeft: d = Mathf.Min(SegDist(p, new Vector2(40, 14), new Vector2(22, 32)), SegDist(p, new Vector2(22, 32), new Vector2(40, 50))) - 4f; break;
                    case Icon.ChevronRight: d = Mathf.Min(SegDist(p, new Vector2(24, 14), new Vector2(42, 32)), SegDist(p, new Vector2(42, 32), new Vector2(24, 50))) - 4f; break;
                    case Icon.Close: d = Mathf.Min(SegDist(p, new Vector2(18, 18), new Vector2(46, 46)), SegDist(p, new Vector2(18, 46), new Vector2(46, 18))) - 3.5f; break;
                    default: // trash: lid with a handle, a tapering body with two grooves
                        float lid = Mathf.Min(SegDist(p, new Vector2(12, 48), new Vector2(52, 48)) - 2.5f,
                                     Mathf.Max(Mathf.Abs(p.y - 52) - 3.5f, Mathf.Abs(p.x - 32) - 7f));
                        float handleHole = Mathf.Max(Mathf.Abs(p.y - 52.5f) - 1.2f, Mathf.Abs(p.x - 32) - 4f);
                        lid = Mathf.Max(lid, -handleHole);
                        float half = Mathf.Lerp(14f, 17f, Mathf.InverseLerp(8, 42, p.y));
                        float body = Mathf.Max(Mathf.Abs(p.x - 32) - half, Mathf.Abs(p.y - 25) - 17f);
                        float inner = Mathf.Max(Mathf.Abs(p.x - 32) - (half - 3.5f), Mathf.Abs(p.y - 26) - 13.5f);
                        body = Mathf.Max(body, -inner); // hollow
                        float grooves = Mathf.Min(SegDist(p, new Vector2(26, 15), new Vector2(26, 36)), SegDist(p, new Vector2(38, 15), new Vector2(38, 36))) - 1.6f;
                        d = Mathf.Min(lid, Mathf.Min(body, grooves));
                        break;
                }
                px[y * n + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(0.5f - d) * 255));
            }
        tex.SetPixels32(px); tex.Apply(false, true);
        sp = Sprite.Create(tex, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
        icons[kind] = sp;
        return sp;
    }

    /// <summary>A small icon image (top-left x, y; size x size canvas units).</summary>
    public static Image CreateIcon(Transform parent, Icon kind, float x, float y, float size, Color? color = null)
    {
        var go = new GameObject("Icon_" + kind, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        go.layer = parent.gameObject.layer;
        go.transform.SetParent(parent, false);
        var img = go.GetComponent<Image>();
        img.sprite = IconSprite(kind);
        img.color = color ?? TextColor;
        img.raycastTarget = false;
        Place((RectTransform)go.transform, x, y, size, size);
        return img;
    }

    /// <summary>A button showing only an icon (centred).</summary>
    public static Button CreateIconButton(Transform parent, Icon kind, float x, float y, float width, float height, UnityAction onClick, float iconSize = 30f)
    {
        var b = CreateButton(parent, "", x, y, width, height, onClick);
        b.name = "Btn_icon_" + kind;
        CreateIcon(b.transform, kind, (width - iconSize) / 2f, (height - iconSize) / 2f, iconSize);
        return b;
    }

    // ---------- the button's look ----------

    internal static Sprite CircleSprite => Circle;
    internal static Sprite Ring => RoundSprite(true);
}

/// <summary>
/// The look of one XRUi button: fill by state, a light outline + slightly lighter fill while the ray is on it, a
/// short flash when clicked, and - for on/off toggles - a switch at the right edge. Hover never looks like "on".
/// </summary>
public class XRUiButton : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public enum Style { Plain, On, Toggle, Tab, Warn, Danger, Disabled, Custom }

    Image fill, outline, track, knob;
    TMP_Text label;
    Style style = Style.Plain;
    bool toggle, hovered;
#pragma warning disable 0414
    bool on;
#pragma warning restore 0414
    Color custom;
    float flashUntil;
    Color labelColor = XRUi.TextColor;
    bool dirty = true;

    internal void Init(Image img, TMP_Text text)
    {
        fill = img; label = text;
        // outline (hover / flash), drawn over the fill
        var o = new GameObject("Outline", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        o.layer = gameObject.layer;
        o.transform.SetParent(transform, false);
        var rt = (RectTransform)o.transform;
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero;
        outline = o.GetComponent<Image>();
        outline.raycastTarget = false;
        XRUi.Round(outline, XRUi.ButtonRadius, ring: true);
        outline.enabled = false;
        GetComponent<Button>().onClick.AddListener(() => { flashUntil = Time.unscaledTime + 0.15f; dirty = true; });
        Apply();
    }

    public bool IsToggle => toggle;
    public void SetOn(bool v) { on = v; style = v ? Style.On : (toggle ? Style.Toggle : Style.Plain); dirty = true; }
    public void SetToggle(bool v) { toggle = true; on = v; style = v ? Style.On : Style.Toggle; dirty = true; }
    public void SetPlain() { on = false; style = toggle ? Style.Toggle : Style.Plain; dirty = true; }
    public void SetStyle(Style s) { style = s; if (s != Style.On) on = false; dirty = true; }
    public void SetCustom(Color c) { style = Style.Custom; custom = c; on = false; dirty = true; }

    public void OnPointerEnter(PointerEventData e) { hovered = true; dirty = true; }
    public void OnPointerExit(PointerEventData e) { hovered = false; dirty = true; }
    void OnDisable() { hovered = false; dirty = true; }

    void LateUpdate()
    {
        bool flashing = Time.unscaledTime < flashUntil;
        if (dirty || flashing || wasFlashing) { Apply(); wasFlashing = flashing; dirty = false; }
    }
    bool wasFlashing;

    void EnsureSwitch()
    {
        if (track || !toggle) return;
        var r = (RectTransform)transform;
        float h = r.rect.height > 0 ? r.rect.height : r.sizeDelta.y, w = r.rect.width > 0 ? r.rect.width : r.sizeDelta.x;
        if (w < 150) return; // too narrow for a switch: the fill alone shows the state
        float tw = 46, th = 26;
        var t = new GameObject("Switch", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        t.layer = gameObject.layer;
        t.transform.SetParent(transform, false);
        var trt = (RectTransform)t.transform;
        trt.anchorMin = trt.anchorMax = new Vector2(1f, 0.5f); trt.pivot = new Vector2(1f, 0.5f);
        trt.anchoredPosition = new Vector2(-14, 0); trt.sizeDelta = new Vector2(tw, th);
        track = t.GetComponent<Image>(); track.raycastTarget = false;
        XRUi.Round(track, th / 2f);
        var k = new GameObject("Knob", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        k.layer = gameObject.layer;
        k.transform.SetParent(t.transform, false);
        var krt = (RectTransform)k.transform;
        krt.anchorMin = krt.anchorMax = new Vector2(0f, 0.5f); krt.pivot = new Vector2(0.5f, 0.5f);
        krt.sizeDelta = new Vector2(th - 6, th - 6);
        knob = k.GetComponent<Image>(); knob.raycastTarget = false; knob.sprite = XRUi.CircleSprite;
        // the label makes room for the switch
        var lr = label.rectTransform; lr.offsetMax = new Vector2(-(tw + 22), lr.offsetMax.y); lr.offsetMin = new Vector2(16, lr.offsetMin.y);
        label.alignment = TextAlignmentOptions.MidlineLeft;
    }

    static Color Lighten(Color c, float a) => new Color(Mathf.Lerp(c.r, 1f, a), Mathf.Lerp(c.g, 1f, a), Mathf.Lerp(c.b, 1f, a), c.a);

    void Apply()
    {
        if (!fill) return;
        EnsureSwitch();
        Color f; Color txt = XRUi.TextColor;
        switch (style)
        {
            case Style.On: f = toggle && track ? new Color(0.17f, 0.24f, 0.36f, 1f) : XRUi.ButtonOnColor; break; // a toggle: the switch says "on"
            case Style.Warn: f = XRUi.ButtonWarnColor; break;
            case Style.Danger: f = XRUi.ButtonDangerColor; txt = new Color(1f, 0.75f, 0.80f); break;
            case Style.Disabled: f = XRUi.ButtonDisabled; txt = new Color(1f, 1f, 1f, 0.38f); break;
            case Style.Custom: f = custom; break;
            default: f = XRUi.ButtonColor; break;
        }
        bool flashing = Time.unscaledTime < flashUntil;
        if (hovered && style != Style.Disabled) f = Lighten(f, 0.10f);
        if (flashing) f = Lighten(f, 0.25f);
        fill.color = f;
        if (label) label.color = txt;
        if (outline)
        {
            outline.enabled = (hovered && style != Style.Disabled) || flashing;
            outline.color = flashing ? new Color(1f, 1f, 1f, 0.85f) : new Color(1f, 1f, 1f, 0.45f);
        }
        if (track)
        {
            bool isOn = style == Style.On;
            track.color = isOn ? XRUi.SwitchOn : XRUi.SwitchOff;
            knob.color = isOn ? Color.white : new Color(0.80f, 0.84f, 0.90f, 1f);
            float tw = ((RectTransform)track.transform).sizeDelta.x, th = ((RectTransform)track.transform).sizeDelta.y;
            ((RectTransform)knob.transform).anchoredPosition = new Vector2(isOn ? tw - th / 2f : th / 2f, 0);
        }
    }
}
