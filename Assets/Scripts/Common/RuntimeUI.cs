using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 런타임 생성 legacy uGUI 위젯 팩토리 (플레이스홀더). 공용 UI 킷(Docs/201 4장) 확정 전까지 임시 화면에 사용한다.
/// 한글 폰트(Resources/Fonts, Docs/102 1.3)를 우선 사용하고, 없으면 LegacyRuntime(OS 폰트 폴백)으로 동작한다.
/// </summary>
public static class RuntimeUI
{
    static Font _font;
    public static Font Font => _font != null ? _font : (_font = Resources.Load<Font>("Fonts/Pretendard-Regular")
        ?? Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));

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

    // ------------------------------------------------------------------ 테마 (Resources/UI/MapEditorTheme) — 배경색·도트 타일
    static MapEditorTheme Theme => MapEditorTheme.LoadOrNull();
    public static Color BackgroundColor => Theme != null ? Theme.Background : new Color32(9, 27, 49, 255);

    static Sprite _dotTile;
    /// <summary>배경 도트 타일 — 테마 스프라이트(에디터 BackCanvas 와 동일), 없으면 절차 생성 (64px 타일, 중앙 점)</summary>
    public static Sprite DotTile
    {
        get
        {
            if (Theme != null && Theme.BgDotsTile != null) return Theme.BgDotsTile;
            if (_dotTile != null) return _dotTile;
            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "bg_dots", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Repeat };
            var px = new Color[size * size];
            var c = new Vector2(size * 0.5f, size * 0.5f);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), c);
                    px[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(2.2f - d + 0.5f) * 0.10f);
                }
            tex.SetPixels(px); tex.Apply();
            _dotTile = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
            return _dotTile;
        }
    }

    static readonly Dictionary<int, Sprite> _rounded = new Dictionary<int, Sprite>();
    /// <summary>흰색 둥근 사각형 9-slice 스프라이트 (radius px, PPU 100 → UI 픽셀 그대로). 색은 Image.color 로.</summary>
    public static Sprite RoundedRect(int radius)
    {
        radius = Mathf.Max(2, radius);
        if (_rounded.TryGetValue(radius, out var cached) && cached != null) return cached;
        int size = radius * 2 + 4;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "rounded_" + radius, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        var px = new Color[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float cx = Mathf.Clamp(x + 0.5f, radius, size - radius), cy = Mathf.Clamp(y + 0.5f, radius, size - radius);
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
                px[y * size + x] = new Color(1f, 1f, 1f, Mathf.Clamp01(radius - d + 0.5f));
            }
        tex.SetPixels(px); tex.Apply();
        var s = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(radius, radius, radius, radius));
        _rounded[radius] = s;
        return s;
    }

    /// <summary>화면 전체 배경: 테마 네이비 + 도트 타일 (맵 에디터 BackCanvas 와 같은 모습). 레이캐스트 없음.</summary>
    public static void Backdrop(Transform parent)
    {
        var bg = Rect("Backdrop", parent, Vector2.zero, Vector2.one, 0f).gameObject.AddComponent<Image>();
        bg.color = BackgroundColor; bg.raycastTarget = false;
        var dots = Rect("BackdropDots", parent, Vector2.zero, Vector2.one, 0f).gameObject.AddComponent<Image>();
        dots.sprite = DotTile; dots.type = Image.Type.Tiled; dots.color = Color.white; dots.raycastTarget = false;
    }

    /// <summary>월드(스프라이트) 뒤에 깔리는 배경 캔버스 — Play 씬용. 카메라 배경색 위에 도트를 얹는다 (정렬 −100).</summary>
    public static Canvas BackdropCanvas(Camera cam, GameObject owner = null)
    {
        var go = new GameObject("Backdrop Canvas (runtime)", typeof(RectTransform));
        if (owner != null && owner.scene.IsValid() && owner.scene != go.scene)
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, owner.scene);
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        canvas.worldCamera = cam;
        canvas.planeDistance = 50f;
        canvas.sortingOrder = -100;
        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        Backdrop(go.transform);
        return canvas;
    }

    /// <summary>
    /// 패널. 화면 전체(anchors 0~1, pad 0)이면 배경(네이비 + 도트)으로 취급해 color 를 무시하고 Backdrop 을 깐다.
    /// 그 외에는 둥근 모서리(24px) 상자.
    /// </summary>
    public static RectTransform Panel(Transform parent, Vector2 aMin, Vector2 aMax, Color color, float pad = 0f)
    {
        var rt = Rect("Panel", parent, aMin, aMax, pad);
        bool fullscreen = aMin == Vector2.zero && aMax == Vector2.one && pad <= 0f;
        if (fullscreen)
        {
            rt.gameObject.AddComponent<Image>().color = BackgroundColor;   // 레이캐스트 차단용 배경
            var dots = Rect("BackdropDots", rt, Vector2.zero, Vector2.one, 0f).gameObject.AddComponent<Image>();
            dots.sprite = DotTile; dots.type = Image.Type.Tiled; dots.color = Color.white; dots.raycastTarget = false;
            return rt;
        }
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = RoundedRect(24); img.type = Image.Type.Sliced; img.color = color;
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
        img.sprite = RoundedRect(12); img.type = Image.Type.Sliced;
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
        img.sprite = RoundedRect(16); img.type = Image.Type.Sliced;
        img.color = color;
        var btn = rt.gameObject.AddComponent<Button>();
        btn.targetGraphic = img;
        var colors = btn.colors;
        colors.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.45f);
        colors.highlightedColor = new Color(1.1f, 1.1f, 1.1f);
        btn.colors = colors;
        btn.onClick.AddListener(() => Sound.Click());   // 클릭음 공통 (Docs/102 3장)
        btn.onClick.AddListener(onClick);
        Label(rt, Vector2.zero, Vector2.one, label, fontSize, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
        return btn;
    }
}
