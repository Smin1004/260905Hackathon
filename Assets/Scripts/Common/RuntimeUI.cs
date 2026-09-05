using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 런타임 생성 legacy uGUI 위젯 팩토리 (플레이스홀더). 공용 UI 킷(Docs/201 4장) 확정 전까지 임시 화면에 사용한다.
/// LegacyRuntime 동적 폰트는 OS 폰트 폴백으로 한글이 표시된다.
/// </summary>
public static class RuntimeUI
{
    static Font _font;
    public static Font Font => _font != null ? _font : (_font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));

    public static Canvas Canvas(string name, int sortingOrder)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = sortingOrder;
        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        go.AddComponent<GraphicRaycaster>();
        return canvas;
    }

    public static RectTransform Rect(string name, Transform parent, Vector2 aMin, Vector2 aMax, float pad = 4f)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = aMin;
        rt.anchorMax = aMax;
        rt.offsetMin = new Vector2(pad, pad);
        rt.offsetMax = new Vector2(-pad, -pad);
        return rt;
    }

    public static RectTransform Panel(Transform parent, Vector2 aMin, Vector2 aMax, Color color, float pad = 0f)
    {
        var rt = Rect("Panel", parent, aMin, aMax, pad);
        rt.gameObject.AddComponent<Image>().color = color;
        return rt;
    }

    public static Text Label(Transform parent, Vector2 aMin, Vector2 aMax, string text, int size, TextAnchor align, Color color, FontStyle style = FontStyle.Normal)
    {
        var rt = Rect("Text", parent, aMin, aMax);
        var t = rt.gameObject.AddComponent<Text>();
        t.font = Font;
        t.fontSize = size;
        t.fontStyle = style;
        t.alignment = align;
        t.color = color;
        t.text = text;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.raycastTarget = false;
        return t;
    }

    public static Button Button(Transform parent, Vector2 aMin, Vector2 aMax, string label, UnityEngine.Events.UnityAction onClick, Color color, int fontSize = 22)
    {
        var rt = Rect("Button " + label, parent, aMin, aMax);
        var img = rt.gameObject.AddComponent<Image>();
        img.color = color;
        var btn = rt.gameObject.AddComponent<Button>();
        btn.targetGraphic = img;
        var colors = btn.colors;
        colors.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.45f);
        colors.highlightedColor = new Color(1.1f, 1.1f, 1.1f);
        btn.colors = colors;
        btn.onClick.AddListener(onClick);
        Label(rt, Vector2.zero, Vector2.one, label, fontSize, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
        return btn;
    }
}
