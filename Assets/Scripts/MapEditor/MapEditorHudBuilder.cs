#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// 맵 에디터 HUD 프리팹 생성기 (에디터 전용 — 릴리즈 빌드에 포함되지 않음. Docs/201 6장 규칙상 Editor 폴더 대신 #if UNITY_EDITOR).
/// 메뉴 [Chojiilgwan > Build MapEditor HUD]:
///   1. Assets/image 스프라이트 임포트 설정 (Single · 9-slice 보더 · 타일 Repeat · PPU)
///   2. Assets/UI/MapEditorTheme.asset 생성 (없을 때만 — 색은 인스펙터에서 바꾼다)
///   3. Assets/Prefabs/UI/MapEditorHud.prefab 생성/덮어쓰기
///   4. MapEditor 씬에 인스턴스가 없으면 배치 후 저장
/// 프리팹을 인스펙터에서 손본 뒤 다시 실행하면 덮어써지므로, 레이아웃을 바꾸려면 이 파일의 수치를 고치는 편이 안전하다.
/// 기준 해상도 1920×1080 (Docs/201 1장).
/// </summary>
public static class MapEditorHudBuilder
{
    const string ImgRoot = "Assets/image/";
    const string ThemePath = "Assets/UI/MapEditorTheme.asset";
    const string PrefabPath = "Assets/Prefabs/UI/MapEditorHud.prefab";
    const string ScenePath = "Assets/Scenes/MapEditor.unity";

    // ---- 레이아웃 (기준 px)
    const float MarginX = 240f;         // 좌우 여백
    const float TopBarH = 183f;         // 상단 바 높이 = 종이 슬롯 시작
    const float BottomH = 150f;         // 하단 바 높이 = 종이 슬롯 끝
    const float ToolW = 170f;           // 오른쪽 도구 열 너비 (QA: 우측 인터페이스 확대)
    const float ToolGap = 19f;          // 종이와 도구 열 사이
    static float PaperRight => MarginX + ToolW + ToolGap;   // 종이 오른쪽 오프셋 (377)

    static Font _font;
    static Font Font => _font != null ? _font : (_font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));

    [MenuItem("Chojiilgwan/Build MapEditor HUD")]
    public static void Build()
    {
        ConfigureSprites();
        var theme = EnsureTheme();
        var prefab = BuildPrefab(theme);
        PlaceInScene(prefab);
        Debug.Log("[MapEditorHudBuilder] 완료 — " + PrefabPath + " / " + ThemePath);
    }

    // ------------------------------------------------------------------ 1. 스프라이트 임포트

    class SpriteSpec { public string Path; public Vector4 Border; public float Ppu = 100f; public bool Repeat; }

    static readonly SpriteSpec[] Specs =
    {
        new SpriteSpec { Path = "panels/panel_dark", Border = V(52) },
        new SpriteSpec { Path = "panels/panel_paper_canvas", Border = V(64) },
        new SpriteSpec { Path = "panels/panel_chip_light", Border = V(58) },
        new SpriteSpec { Path = "panels/panel_card_overlay", Border = V(124) },
        new SpriteSpec { Path = "panels/divider" },
        new SpriteSpec { Path = "buttons/btn_tool_normal", Border = V(57) },
        new SpriteSpec { Path = "buttons/btn_tool_hover", Border = V(57) },
        new SpriteSpec { Path = "buttons/btn_tool_active", Border = V(57) },
        new SpriteSpec { Path = "buttons/btn_tool_active_goal", Border = V(57) },
        new SpriteSpec { Path = "buttons/btn_tool_disabled", Border = V(57) },
        new SpriteSpec { Path = "buttons/btn_dark_blank", Border = V(58) },
        new SpriteSpec { Path = "buttons/btn_verify_active_blank", Border = new Vector4(112, 121, 112, 87) },
        new SpriteSpec { Path = "buttons/btn_verify_disabled_blank", Border = new Vector4(112, 136, 112, 87) },
        new SpriteSpec { Path = "buttons/btn_verify_pressed_blank", Border = new Vector4(112, 121, 112, 93) },
        new SpriteSpec { Path = "timer/badge_round_blank", Border = V(63) },
        new SpriteSpec { Path = "timer/timer_ring_track" },
        new SpriteSpec { Path = "timer/timer_ring_fill_normal" },
        new SpriteSpec { Path = "timer/timer_ring_fill_warning" },
        new SpriteSpec { Path = "tooltips/pill_green_blank", Border = new Vector4(72, 70, 72, 70) },
        new SpriteSpec { Path = "tooltips/pill_coral_blank", Border = new Vector4(107, 120, 107, 83) },
        new SpriteSpec { Path = "tooltips/tooltip_dark_blank", Border = new Vector4(106, 95, 107, 82) },
        new SpriteSpec { Path = "tooltips/tooltip_warn_blank", Border = new Vector4(106, 95, 107, 82) },
        new SpriteSpec { Path = "tiles/tile_bg_dots", Repeat = true },
        new SpriteSpec { Path = "tiles/tile_paper_dots", Repeat = true, Ppu = 64f },   // 도트 간격 64px → 월드 1u
        new SpriteSpec { Path = "tiles/color_bg", Repeat = true },
        new SpriteSpec { Path = "tiles/color_paper", Repeat = true },
        new SpriteSpec { Path = "tiles/overlay_dim", Repeat = true },
        new SpriteSpec { Path = "icons/icon_pen" },
        new SpriteSpec { Path = "icons/icon_undo" },
        new SpriteSpec { Path = "icons/icon_trash" },
        new SpriteSpec { Path = "icons/icon_flag" },
        new SpriteSpec { Path = "icons/icon_check" },
        new SpriteSpec { Path = "markers/marker_start" },
        new SpriteSpec { Path = "markers/marker_goal" },
        new SpriteSpec { Path = "markers/marker_pulse_start" },
        new SpriteSpec { Path = "markers/marker_pulse_goal" },
        new SpriteSpec { Path = "markers/dot_start_small" },
        new SpriteSpec { Path = "markers/dot_goal_small" },
        new SpriteSpec { Path = "markers/dot_goal_empty" },
        new SpriteSpec { Path = "markers/dot_dashed" },
        new SpriteSpec { Path = "markers/dot_loading" },
    };

    static Vector4 V(float b) => new Vector4(b, b, b, b);

    static void ConfigureSprites()
    {
        AssetDatabase.StartAssetEditing();
        try
        {
            foreach (var sp in Specs)
            {
                string path = ImgRoot + sp.Path + ".png";
                var ti = AssetImporter.GetAtPath(path) as TextureImporter;
                if (ti == null) { Debug.LogWarning("[MapEditorHudBuilder] 스프라이트 없음: " + path); continue; }
                ti.textureType = TextureImporterType.Sprite;
                ti.spriteImportMode = SpriteImportMode.Single;
                ti.spriteBorder = sp.Border;
                ti.spritePixelsPerUnit = sp.Ppu;
                ti.spritePivot = new Vector2(0.5f, 0.5f);
                ti.mipmapEnabled = false;
                ti.alphaIsTransparency = true;
                ti.wrapMode = sp.Repeat ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
                ti.filterMode = FilterMode.Bilinear;
                var settings = new TextureImporterSettings();
                ti.ReadTextureSettings(settings);
                settings.spriteMeshType = SpriteMeshType.FullRect;
                settings.spriteGenerateFallbackPhysicsShape = false;
                ti.SetTextureSettings(settings);
                ti.SaveAndReimport();
            }
        }
        finally { AssetDatabase.StopAssetEditing(); }
        AssetDatabase.Refresh();
    }

    static Sprite S(string rel)
    {
        var s = AssetDatabase.LoadAssetAtPath<Sprite>(ImgRoot + rel + ".png");
        if (s == null) Debug.LogWarning("[MapEditorHudBuilder] 스프라이트 로드 실패: " + rel);
        return s;
    }

    static Sprite Builtin(string name) => AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/" + name);

    // ------------------------------------------------------------------ 2. 테마

    static MapEditorTheme EnsureTheme()
    {
        EnsureFolder("Assets/UI");
        var t = AssetDatabase.LoadAssetAtPath<MapEditorTheme>(ThemePath);
        bool created = t == null;
        if (created) { t = ScriptableObject.CreateInstance<MapEditorTheme>(); AssetDatabase.CreateAsset(t, ThemePath); }
        // 스프라이트 참조는 매번 갱신 (색은 사용자가 바꿨을 수 있으니 생성 시에만 기본값)
        t.PaperDotsTile = S("tiles/tile_paper_dots");
        t.StartMarker = S("markers/marker_start");
        t.StartPulse = S("markers/marker_pulse_start");
        t.GoalMarker = S("markers/marker_goal");
        t.GoalPulse = S("markers/marker_pulse_goal");
        EditorUtility.SetDirty(t);
        AssetDatabase.SaveAssets();
        return t;
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path)) return;
        int i = path.LastIndexOf('/');
        string parent = path.Substring(0, i), name = path.Substring(i + 1);
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, name);
    }

    // ------------------------------------------------------------------ 3. 프리팹

    static GameObject BuildPrefab(MapEditorTheme t)
    {
        EnsureFolder("Assets/Prefabs/UI");

        var root = new GameObject("MapEditorHud", typeof(RectTransform));
        var hud = root.AddComponent<MapEditorHud>();
        var so = new SerializedObject(hud);
        so.FindProperty("Theme").objectReferenceValue = t;

        // ---------- BackCanvas: 배경 + 종이 프레임 (월드 스트로크 뒤) — 레이캐스터 없음 (그리기 입력을 막지 않게)
        var back = MakeCanvas("BackCanvas", root.transform, RenderMode.ScreenSpaceCamera, -100, false);
        Img("Background", back.transform, null, t.Background, Image.Type.Simple).rectTransform.Stretch();
        var bgDots = Img("BackgroundDots", back.transform, S("tiles/tile_bg_dots"), Color.white, Image.Type.Tiled);
        bgDots.rectTransform.Stretch();
        var frame = Img("PaperFrame", back.transform, S("panels/panel_paper_canvas"), Color.white, Image.Type.Sliced, 1.6f);
        frame.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1300, 700));

        // ---------- HudCanvas: 조작 UI
        var hudCanvas = MakeCanvas("HudCanvas", root.transform, RenderMode.ScreenSpaceOverlay, 100, true);
        var hc = hudCanvas.transform;

        // 종이 슬롯 (그래픽 없음)
        var slot = Rt("PaperSlot", hc);
        slot.anchorMin = Vector2.zero; slot.anchorMax = Vector2.one;
        slot.offsetMin = new Vector2(MarginX, BottomH); slot.offsetMax = new Vector2(-PaperRight, -TopBarH);

        // ---- 상단
        var top = Rt("TopBar", hc);
        top.anchorMin = new Vector2(0, 1); top.anchorMax = new Vector2(1, 1); top.pivot = new Vector2(0.5f, 1);
        top.anchoredPosition = Vector2.zero; top.sizeDelta = new Vector2(0, TopBarH);

        var badge = Img("RoundBadge", top, S("timer/badge_round_blank"), Color.white, Image.Type.Sliced, 2.7f);
        badge.rectTransform.Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(MarginX, -90), new Vector2(110, 58));
        var roundText = Txt("RoundText", badge.transform, "1", 30, FontStyle.Bold, t.TextPrimary, TextAnchor.MiddleCenter);
        roundText.rectTransform.Stretch();
        Txt("RoundKo", top, "라운드", 18, FontStyle.Normal, t.TextMuted, TextAnchor.MiddleLeft)
            .rectTransform.Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(MarginX + 122, -92), new Vector2(120, 24));
        Txt("RoundEn", top, "ROUND", 16, FontStyle.Bold, t.TextMuted, TextAnchor.MiddleLeft)
            .rectTransform.Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(MarginX + 122, -116), new Vector2(120, 24));

        // (QA) 안내 문구(부제·제목)는 두지 않는다 — MapEditorHud 의 subtitleText/titleText 는 null 허용
        Text subtitle = null, title = null;

        // 타이머
        // (QA) 타이머 패널 확대 — 남은 시간이 잘 보이게
        var timer = Img("TimerPanel", top, S("panels/panel_dark"), Color.white, Image.Type.Sliced, 2.5f);
        timer.rectTransform.Place(new Vector2(1, 1), new Vector2(1, 1), new Vector2(-MarginX, -62), new Vector2(270, 112));
        var ringRect = new Vector2(84, 84);
        Img("RingTrack", timer.transform, S("timer/timer_ring_track"), Color.white, Image.Type.Simple)
            .rectTransform.Place(new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(14, 0), ringRect);
        var ringFill = Img("RingFill", timer.transform, S("timer/timer_ring_fill_normal"), Color.white, Image.Type.Filled);
        ringFill.fillMethod = Image.FillMethod.Radial360;
        ringFill.fillOrigin = (int)Image.Origin360.Top;
        ringFill.fillClockwise = true;
        ringFill.fillAmount = 1f;
        ringFill.rectTransform.Place(new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(14, 0), ringRect);
        var timerNumber = Txt("TimerNumber", timer.transform, "56", 28, FontStyle.Bold, t.TextPrimary, TextAnchor.MiddleCenter);
        timerNumber.rectTransform.Place(new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(14, 0), ringRect);
        Txt("RemainingLabel", timer.transform, "남은 시간", 18, FontStyle.Normal, t.TextMuted, TextAnchor.MiddleLeft)
            .rectTransform.Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(112, -14), new Vector2(140, 22));
        var timerRemaining = Txt("RemainingTime", timer.transform, "0:56", 46, FontStyle.Bold, t.Accent, TextAnchor.MiddleLeft);
        timerRemaining.rectTransform.Place(new Vector2(0, 1), new Vector2(0, 1), new Vector2(112, -38), new Vector2(150, 60));

        // ---- 도구 패널 (오른쪽 열)
        Sprite toolN = S("buttons/btn_tool_normal"), toolA = S("buttons/btn_tool_active"), toolD = S("buttons/btn_tool_disabled"), toolGoal = S("buttons/btn_tool_active_goal");
        var toolPanel = Img("ToolPanel", hc, S("panels/panel_dark"), Color.white, Image.Type.Sliced, 2.5f);
        const float btnH = 100f, btnGap = 8f, pad = 14f;   // (QA) 버튼 확대
        float y = -pad;
        var pen = ToolButton(toolPanel.transform, "Btn_Pen", y, S("icons/icon_pen"), "펜", toolN, toolA, toolD, t); y -= btnH + btnGap;
        var eraser = ToolButton(toolPanel.transform, "Btn_Eraser", y, null, "지우개", toolN, toolA, toolD, t); y -= btnH + btnGap;
        // 지우개 아이콘 에셋이 없어 기본 스프라이트(UISprite)를 기울인 블록으로 대신한다
        var eraserIcon = Img("Icon", eraser.transform, Builtin("UISprite.psd"), Color.white, Image.Type.Sliced);
        eraserIcon.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, 14), new Vector2(44, 26));
        eraserIcon.rectTransform.localRotation = Quaternion.Euler(0, 0, -30);
        eraserIcon.raycastTarget = false;
        eraser.Icon = eraserIcon;
        var undo = ToolButton(toolPanel.transform, "Btn_Undo", y, S("icons/icon_undo"), "실행 취소", toolN, toolA, toolD, t); y -= btnH + btnGap;
        // (QA) 다시 실행 버튼 — Ctrl+Y 와 같은 동작. 아이콘은 실행 취소 아이콘을 좌우 반전
        var redo = ToolButton(toolPanel.transform, "Btn_Redo", y, S("icons/icon_undo"), "다시 실행", toolN, toolA, toolD, t); y -= btnH + btnGap;
        if (redo.Icon != null) redo.Icon.rectTransform.localScale = new Vector3(-1, 1, 1);
        var clear = ToolButton(toolPanel.transform, "Btn_Clear", y, S("icons/icon_trash"), "전체 지우기", toolN, toolA, toolD, t); y -= btnH + btnGap;
        var divider = Img("Divider", toolPanel.transform, S("panels/divider"), Color.white, Image.Type.Simple);
        divider.rectTransform.Place(new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, y + 2), new Vector2(140, 14));
        divider.raycastTarget = false;
        y -= 14 + 4;
        // (QA) 골 배치는 다른 도구와 구분 — 평상시에도 코랄(경고) 색 아이콘·라벨, 선택 시 코랄 배경
        var goal = ToolButton(toolPanel.transform, "Btn_Goal", y, S("icons/icon_flag"), "골 배치", toolN, toolGoal, toolD, t); y -= btnH;
        goal.ContentNormal = t.Warning;
        goal.ContentActive = Color.white;   // 코랄 배경 위 흰 아이콘
        float toolPanelH = -y + pad;
        toolPanel.rectTransform.Place(new Vector2(1, 1), new Vector2(1, 1), new Vector2(-MarginX, -TopBarH), new Vector2(ToolW, toolPanelH));

        // ---- (QA) 검증 → 제출: 빨간 원형 버튼 2개, 번호로 순서 표시. 제출은 검증 성공 후에만 활성
        var complete = Circle(hc, "Btn_Complete", "② 제출", 26, 140, t, t.Accent);   // (QA) 활성 초록 / 비활성 회색
        complete.Background.rectTransform.Place(new Vector2(1, 0), new Vector2(1, 0), new Vector2(-MarginX - (ToolW - 140) * 0.5f, 30), new Vector2(140, 140));

        // ---- 하단: 굵기·색상
        var options = Img("OptionsPanel", hc, S("panels/panel_dark"), Color.white, Image.Type.Sliced, 2.5f);
        options.rectTransform.Place(new Vector2(0, 0), new Vector2(0, 0), new Vector2(MarginX, 44), new Vector2(720, 100));
        Txt("WidthLabel", options.transform, "굵기", 16, FontStyle.Normal, t.TextMuted, TextAnchor.MiddleLeft)
            .rectTransform.Place(new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(18, 0), new Vector2(44, 24));
        string[] widthNames = { "얇게", "보통", "굵게" };
        var widthBtns = new HudToolButton[MapConstants.PenWidths.Length];
        for (int i = 0; i < widthBtns.Length; i++)
        {
            var wb = Chip(options.transform, "Btn_Width" + i, widthNames[Mathf.Min(i, widthNames.Length - 1)], 15, new Vector2(60, 44), toolN, toolA, toolD, t);
            wb.Background.rectTransform.Place(new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(64 + i * 66, 0), new Vector2(60, 44));
            widthBtns[i] = wb;
        }
        var vdiv = Img("VDivider", options.transform, S("panels/divider"), Color.white, Image.Type.Simple);
        vdiv.rectTransform.Place(new Vector2(0, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(276, 0), new Vector2(52, 12));
        vdiv.rectTransform.localRotation = Quaternion.Euler(0, 0, 90);
        vdiv.raycastTarget = false;

        var swatchRoot = Rt("SwatchRoot", options.transform);
        swatchRoot.Place(new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(292, 6), new Vector2(420, 60));
        var hlg = swatchRoot.gameObject.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing = 36; hlg.childAlignment = TextAnchor.MiddleLeft;   // (QA) 스와치 아래 기능 이름 라벨이 들어갈 간격
        hlg.childControlWidth = hlg.childControlHeight = false;
        hlg.childForceExpandWidth = hlg.childForceExpandHeight = false;
        var swatch = Img("SwatchTemplate", swatchRoot, Builtin("Knob.psd"), Color.white, Image.Type.Simple);
        swatch.rectTransform.sizeDelta = new Vector2(34, 34);
        var le = swatch.gameObject.AddComponent<LayoutElement>(); le.preferredWidth = le.preferredHeight = 34;
        var swBtn = swatch.gameObject.AddComponent<Button>(); swBtn.targetGraphic = swatch; Tint(swBtn);
        var check = Img("Check", swatch.transform, S("icons/icon_check"), Color.white, Image.Type.Simple);
        check.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(18, 18));
        check.raycastTarget = false;
        // (QA) 색상 설명: 스와치 아래 기능 이름 (MapEditorHud.BuildSwatches 가 채움)
        var swatchLabel = Txt("Label", swatch.transform, "", 13, FontStyle.Bold, t.TextMuted, TextAnchor.UpperCenter);
        swatchLabel.rectTransform.Place(new Vector2(0.5f, 0), new Vector2(0.5f, 1), new Vector2(0, -4), new Vector2(70, 16));
        swatch.gameObject.SetActive(false);

        // ---- 하단: 상태 칩
        var status = Img("StatusChip", hc, S("panels/panel_chip_light"), Color.white, Image.Type.Sliced, 2f);
        status.rectTransform.anchorMin = new Vector2(0, 0); status.rectTransform.anchorMax = new Vector2(1, 0); status.rectTransform.pivot = new Vector2(0.5f, 0);
        status.rectTransform.offsetMin = new Vector2(MarginX + 740, 44);
        status.rectTransform.offsetMax = new Vector2(-(PaperRight + 250 + 16), 44 + 92);
        var statusText = Txt("StatusText", status.transform, "", 15, FontStyle.Bold, t.TextOnLight, TextAnchor.MiddleLeft);
        statusText.rectTransform.anchorMin = new Vector2(0, 0.38f); statusText.rectTransform.anchorMax = new Vector2(1, 1);
        statusText.rectTransform.offsetMin = new Vector2(20, 0); statusText.rectTransform.offsetMax = new Vector2(-20, -8);
        statusText.horizontalOverflow = HorizontalWrapMode.Wrap; statusText.verticalOverflow = VerticalWrapMode.Truncate;
        var mutedOnLight = t.TextOnLight; mutedOnLight.a = 0.6f;
        var statsText = Txt("StatsText", status.transform, "", 13, FontStyle.Normal, mutedOnLight, TextAnchor.MiddleLeft);
        statsText.rectTransform.anchorMin = new Vector2(0, 0); statsText.rectTransform.anchorMax = new Vector2(1, 0.38f);
        statsText.rectTransform.offsetMin = new Vector2(20, 8); statsText.rectTransform.offsetMax = new Vector2(-20, 0);
        statsText.horizontalOverflow = HorizontalWrapMode.Wrap; statsText.verticalOverflow = VerticalWrapMode.Truncate;

        // ---- 하단: 검증 (빨간 원형, 제출 왼쪽)
        var verify = Circle(hc, "Btn_Verify", "① 검증", 26, 140, t, t.Warning);
        verify.Background.rectTransform.Place(new Vector2(1, 0), new Vector2(1, 0), new Vector2(-PaperRight - 24, 30), new Vector2(140, 140));

        // ---------- 참조 연결
        so.FindProperty("backCanvas").objectReferenceValue = back;
        so.FindProperty("hudCanvas").objectReferenceValue = hudCanvas;
        so.FindProperty("paperSlot").objectReferenceValue = slot;
        so.FindProperty("paperFrame").objectReferenceValue = frame.rectTransform;
        so.FindProperty("roundText").objectReferenceValue = roundText;
        so.FindProperty("subtitleText").objectReferenceValue = subtitle;
        so.FindProperty("titleText").objectReferenceValue = title;
        so.FindProperty("ringFill").objectReferenceValue = ringFill;
        so.FindProperty("ringNormal").objectReferenceValue = S("timer/timer_ring_fill_normal");
        so.FindProperty("ringWarning").objectReferenceValue = S("timer/timer_ring_fill_warning");
        so.FindProperty("timerNumber").objectReferenceValue = timerNumber;
        so.FindProperty("timerRemaining").objectReferenceValue = timerRemaining;
        so.FindProperty("penButton").objectReferenceValue = pen;
        so.FindProperty("eraserButton").objectReferenceValue = eraser;
        so.FindProperty("undoButton").objectReferenceValue = undo;
        so.FindProperty("redoButton").objectReferenceValue = redo;
        so.FindProperty("clearButton").objectReferenceValue = clear;
        so.FindProperty("goalButton").objectReferenceValue = goal;
        var wp = so.FindProperty("widthButtons"); wp.arraySize = widthBtns.Length;
        for (int i = 0; i < widthBtns.Length; i++) wp.GetArrayElementAtIndex(i).objectReferenceValue = widthBtns[i];
        so.FindProperty("swatchRoot").objectReferenceValue = swatchRoot;
        so.FindProperty("swatchTemplate").objectReferenceValue = swatch.gameObject;
        so.FindProperty("statusText").objectReferenceValue = statusText;
        so.FindProperty("statsText").objectReferenceValue = statsText;
        so.FindProperty("verifyButton").objectReferenceValue = verify;
        so.FindProperty("completeButton").objectReferenceValue = complete;
        so.ApplyModifiedPropertiesWithoutUndo();

        var prefab = PrefabUtility.SaveAsPrefabAsset(root, PrefabPath);
        Object.DestroyImmediate(root);
        return prefab;
    }

    // ------------------------------------------------------------------ 4. 씬 배치

    static void PlaceInScene(GameObject prefab)
    {
        var scene = SceneManager.GetSceneByPath(ScenePath);
        bool wasOpen = scene.IsValid() && scene.isLoaded;
        if (!wasOpen) scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Additive);

        bool exists = false;
        foreach (var go in scene.GetRootGameObjects())
            if (go.GetComponentInChildren<MapEditorHud>(true) != null) { exists = true; break; }
        if (!exists)
        {
            var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            inst.name = "MapEditorHud";
            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[MapEditorHudBuilder] MapEditor 씬에 HUD 배치");
        }
        if (!wasOpen) EditorSceneManager.CloseScene(scene, true);
    }

    // ------------------------------------------------------------------ 위젯 헬퍼

    static Canvas MakeCanvas(string name, Transform parent, RenderMode mode, int order, bool raycaster)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var c = go.AddComponent<Canvas>();
        c.renderMode = mode;
        c.sortingOrder = order;
        var sc = go.AddComponent<CanvasScaler>();
        sc.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        sc.referenceResolution = new Vector2(1920, 1080);
        sc.matchWidthOrHeight = 0.5f;
        if (raycaster) go.AddComponent<GraphicRaycaster>();
        return c;
    }

    static RectTransform Rt(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        return rt;
    }

    static Image Img(string name, Transform parent, Sprite sprite, Color color, Image.Type type, float ppuMult = 1f)
    {
        var rt = Rt(name, parent);
        var img = rt.gameObject.AddComponent<Image>();
        img.sprite = sprite;
        img.color = color;
        img.type = type;
        img.pixelsPerUnitMultiplier = ppuMult;
        img.raycastTarget = true;
        return img;
    }

    static Text Txt(string name, Transform parent, string text, int size, FontStyle style, Color color, TextAnchor align)
    {
        var rt = Rt(name, parent);
        var t = rt.gameObject.AddComponent<Text>();
        t.font = Font;
        t.text = text;
        t.fontSize = size;
        t.fontStyle = style;
        t.color = color;
        t.alignment = align;
        t.horizontalOverflow = HorizontalWrapMode.Overflow;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.raycastTarget = false;
        return t;
    }

    static void Tint(Button b)
    {
        b.transition = Selectable.Transition.ColorTint;
        var c = b.colors;
        c.normalColor = Color.white;
        c.highlightedColor = new Color(0.92f, 0.92f, 0.92f);
        c.pressedColor = new Color(0.8f, 0.8f, 0.8f);
        c.selectedColor = Color.white;
        c.disabledColor = Color.white;   // 비활성은 스프라이트 교체로 표현
        c.fadeDuration = 0.08f;
        b.colors = c;
    }

    /// <summary>도구 패널 버튼: 배경(9-slice) + 아이콘 + 라벨. 상단 중앙 앵커, y 는 패널 위에서의 오프셋.</summary>
    static HudToolButton ToolButton(Transform parent, string name, float y, Sprite icon, string label, Sprite normal, Sprite active, Sprite disabled, MapEditorTheme t)
    {
        var bg = Img(name, parent, normal, Color.white, Image.Type.Sliced, 2.8f);
        bg.rectTransform.Place(new Vector2(0.5f, 1), new Vector2(0.5f, 1), new Vector2(0, y), new Vector2(140, 100));
        var btn = bg.gameObject.AddComponent<Button>();
        btn.targetGraphic = bg;
        Tint(btn);
        var tb = bg.gameObject.AddComponent<HudToolButton>();
        tb.Button = btn; tb.Background = bg;
        tb.Normal = normal; tb.Active = active; tb.Disabled = disabled;
        tb.ContentNormal = t.TextPrimary; tb.ContentActive = t.TextOnLight; tb.ContentDisabled = t.IconDisabled;
        if (icon != null)
        {
            var ic = Img("Icon", bg.transform, icon, Color.white, Image.Type.Simple);
            ic.rectTransform.Place(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, 14), new Vector2(44, 44));
            ic.raycastTarget = false;
            tb.Icon = ic;
        }
        var lb = Txt("Label", bg.transform, label, 18, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter);
        lb.rectTransform.anchorMin = new Vector2(0, 0); lb.rectTransform.anchorMax = new Vector2(1, 0); lb.rectTransform.pivot = new Vector2(0.5f, 0);
        lb.rectTransform.anchoredPosition = new Vector2(0, 10); lb.rectTransform.sizeDelta = new Vector2(0, 22);
        tb.Label = lb;
        return tb;
    }

    /// <summary>텍스트만 있는 버튼 (굵기 칩·완료·검증). 위치는 호출 쪽에서 Place.</summary>
    static HudToolButton Chip(Transform parent, string name, string label, int fontSize, Vector2 size, Sprite normal, Sprite active, Sprite disabled, MapEditorTheme t, float ppuMult = 2.8f)
    {
        var bg = Img(name, parent, normal, Color.white, Image.Type.Sliced, ppuMult);
        bg.rectTransform.sizeDelta = size;
        var btn = bg.gameObject.AddComponent<Button>();
        btn.targetGraphic = bg;
        Tint(btn);
        var tb = bg.gameObject.AddComponent<HudToolButton>();
        tb.Button = btn; tb.Background = bg;
        tb.Normal = normal; tb.Active = active; tb.Disabled = disabled;
        tb.ContentNormal = t.TextPrimary; tb.ContentActive = t.TextOnLight; tb.ContentDisabled = t.IconDisabled;
        var lb = Txt("Label", bg.transform, label, fontSize, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter);
        lb.rectTransform.Stretch();
        tb.Label = lb;
        return tb;
    }

    /// <summary>(QA) 빨간 원형 버튼 — 검증·제출. 기본 Knob 스프라이트를 상태별 색으로 칠한다 (활성 코랄, 비활성 회색).</summary>
    static HudToolButton Circle(Transform parent, string name, string label, int fontSize, float diameter, MapEditorTheme t, Color activeColor)
    {
        var knob = Builtin("Knob.psd");
        var bg = Img(name, parent, knob, activeColor, Image.Type.Simple);
        bg.preserveAspect = true;
        bg.rectTransform.sizeDelta = new Vector2(diameter, diameter);
        var btn = bg.gameObject.AddComponent<Button>();
        btn.targetGraphic = bg;
        Tint(btn);
        var tb = bg.gameObject.AddComponent<HudToolButton>();
        tb.Button = btn; tb.Background = bg;
        tb.Normal = tb.Active = tb.Disabled = knob;
        tb.TintBackground = true;
        tb.BackgroundNormal = activeColor;
        tb.BackgroundActive = activeColor;
        tb.BackgroundDisabled = new Color(0.30f, 0.32f, 0.38f);
        tb.ContentNormal = tb.ContentActive = Color.white;
        tb.ContentDisabled = t.IconDisabled;
        var lb = Txt("Label", bg.transform, label, fontSize, FontStyle.Bold, Color.white, TextAnchor.MiddleCenter);
        lb.rectTransform.Stretch();
        tb.Label = lb;
        return tb;
    }

    static void Place(this RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = anchor;
        rt.pivot = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
    }

    static void Stretch(this RectTransform rt)
    {
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }
}
#endif
