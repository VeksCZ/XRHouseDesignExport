using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

/// <summary>
/// Small factory for the app's world-space UI. Everything is positioned explicitly (top-left based, in
/// canvas units) instead of using layout groups, so there is nothing to rebuild at runtime and the
/// result is identical every time - no dependence on authored scene layout.
/// </summary>
public static class XRUi
{
    public static readonly Color PanelColor = new Color(0.06f, 0.07f, 0.09f, 0.94f);
    public static readonly Color ButtonColor = new Color(0.17f, 0.20f, 0.27f, 1f);
    public static readonly Color ButtonHover = new Color(0.20f, 0.55f, 0.40f, 1f);
    public static readonly Color ButtonPressed = new Color(0.15f, 0.42f, 0.85f, 1f);
    public static readonly Color ButtonDisabled = new Color(0.12f, 0.13f, 0.16f, 1f);
    public static readonly Color TextColor = new Color(0.94f, 0.95f, 0.97f, 1f);
    public static readonly Color MutedText = new Color(0.62f, 0.68f, 0.78f, 1f);
    public static readonly Color AccentText = new Color(0.45f, 0.85f, 1f, 1f);

    /// <summary>A world-space canvas the XR UI ray (XRUIInputModule) can hit. 'scale' is metres per canvas unit.</summary>
    public static Canvas CreateWorldCanvas(string name, Vector2 size, float scale, Transform parent = null)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(TrackedDeviceGraphicRaycaster));
        go.layer = LayerMask.NameToLayer("UI");
        if (parent != null) go.transform.SetParent(parent, false);

        var canvas = go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = Camera.main;

        var rt = (RectTransform)go.transform;
        rt.sizeDelta = size;
        rt.localScale = Vector3.one * scale;
        return canvas;
    }

    /// <summary>Top-left anchored placement: x/y measured from the parent's top-left corner, y growing downwards.</summary>
    public static void Place(RectTransform rt, float x, float y, float width, float height)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, -y);
        rt.sizeDelta = new Vector2(width, height);
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
        return image;
    }

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
        Place((RectTransform)go.transform, x, y, width, height);
        return tmp;
    }

    public static Button CreateButton(Transform parent, string label, float x, float y, float width, float height,
        UnityAction onClick, float fontSize = 30f)
    {
        var go = new GameObject("Btn_" + label, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Button));
        go.layer = parent.gameObject.layer;
        go.transform.SetParent(parent, false);
        Place((RectTransform)go.transform, x, y, width, height);

        var image = go.GetComponent<Image>();
        image.color = Color.white;
        image.raycastTarget = true;

        var button = go.GetComponent<Button>();
        button.targetGraphic = image;
        var colors = button.colors;
        colors.normalColor = ButtonColor;
        colors.highlightedColor = ButtonHover;
        colors.selectedColor = ButtonColor;
        colors.pressedColor = ButtonPressed;
        colors.disabledColor = ButtonDisabled;
        colors.colorMultiplier = 1f;
        colors.fadeDuration = 0.05f;
        button.colors = colors;
        if (onClick != null) button.onClick.AddListener(onClick);

        var text = CreateText(go.transform, "Label", label, fontSize, TextAlignmentOptions.Center, 0, 0, width, height);
        var textRect = text.rectTransform;
        textRect.anchorMin = Vector2.zero; textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(6, 0); textRect.offsetMax = new Vector2(-6, 0);
        return button;
    }

    public static void SetLabel(Button button, string label)
    {
        var tmp = button != null ? button.GetComponentInChildren<TMP_Text>() : null;
        if (tmp != null) tmp.text = label;
    }
}
