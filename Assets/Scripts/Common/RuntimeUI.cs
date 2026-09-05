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

    /// <summary>캔버스를 만들고, owner 가 있으면 owner 의 씬으로 옮긴다 (애디티브 씬 언로드 시 함께 정리되게).</summary>
    public static Canvas Canvas(string name, int sortingOrder, GameObject owner = null)
    {
        var go = new GameObject(name, typeof(RectTransform));
        if (owner != null && owner.scene.IsValid() && owner.scene != go.scene)
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, owner.scene);
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

    public static InputField Input(Transform parent, Vector2 aMin, Vector2 aMax, string placeholder, int fontSize = 26)
    {
        var rt = Rect("Input", parent, aMin, aMax);
        var img = rt.gameObject.AddComponent<Image>();
        img.color = new Color(0.95f, 0.95f, 0.95f);
        var field = rt.gameObject.AddComponent<InputField>();
        field.targetGraphic = img;

        var textComp = Label(rt, Vector2.zero, Vector2.one, "", fontSize, TextAnchor.MiddleLeft, Color.black);
        textComp.supportRichText = false;
        textComp.verticalOverflow = VerticalWrapMode.Truncate;
        textComp.GetComponent<RectTransform>().offsetMin = new Vector2(14, 4);
        textComp.GetComponent<RectTransform>().offsetMax = new Vector2(-14, -4);

        var ph = Label(rt, Vector2.zero, Vector2.one, placeholder, fontSize, TextAnchor.MiddleLeft, new Color(0.5f, 0.5f, 0.5f), FontStyle.Italic);
        ph.GetComponent<RectTransform>().offsetMin = new Vector2(14, 4);
        ph.GetComponent<RectTransform>().offsetMax = new Vector2(-14, -4);

        field.textComponent = textComp;
        field.placeholder = ph;
        field.lineType = InputField.LineType.SingleLine;
        return field;
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
