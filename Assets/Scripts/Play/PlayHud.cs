using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 플레이 HUD (Docs/204 2.3). PlaySession 이 생성·갱신만 하고, 표시·연출은 전부 여기서 처리한다.
/// 검증 플레이(MapEditor 씬 안)와 교환 플레이(Play 씬) 공용 — 에디터 HUD(MapEditorHud)와 같은 테마·패널 스프라이트를 쓴다.
///
/// 구성 (기준 해상도 1920×1080, 상단 바 183px 는 에디터 HUD 의 TopBarH 와 동일)
///  - 좌상단: 라운드 배지(badge_round_blank) + 모드 필(검증 플레이 = 초록 / 교환 플레이 = 코랄) + 부제(상대 뜻 / 상대 닉네임의 맵)
///  - 중앙 상단: 뜻 표시 칩(VowCatalog.HudLine) + 시도 표시
///  - 우상단: 시간 링(에디터 타이머와 같은 패널·링 스프라이트) + 기권/에디터 복귀 버튼 (GameFlow 방 나가기 버튼은 검증 중 숨겨진다)
///            검증 플레이 = 에디터의 남은 그리기 시간(수정 마감이 검증 중에도 흐르므로), 교환 플레이 = 남은 플레이 시간
///  - 하단: 조작 안내 (작게)
///  - 연출: 클리어 시 결과 카드 팝 + 화면 플래시 / 사망 시 붉은 비네트 + 카메라 흔들림 / 남은 10초 이하 경고색 + 초 단위 틱 소리
///
/// 에셋 참조: 테마 에셋·이미지는 Resources 폴더에 없으므로 에디터에서는 AssetDatabase 로 읽고(MapEditorTheme.LoadOrNull 과 같은 방식),
/// 빌드에서는 색 상수(Assets/UI/MapEditorTheme.asset 복제)와 절차 생성 스프라이트(둥근 사각형·링)로 대체한다.
/// </summary>
public class PlayHud : MonoBehaviour
{
    // ------------------------------------------------------------------ 테마 (출처: Assets/UI/MapEditorTheme.asset — 값을 바꾸면 여기도 맞춘다)

    public static class Theme
    {
        public static Color Background = new Color32(9, 27, 49, 255);
        public static Color TextPrimary = Color.white;
        public static Color TextMuted = new Color32(160, 172, 190, 255);
        public static Color TextOnLight = new Color32(18, 37, 60, 255);
        public static Color Accent = new Color32(111, 226, 118, 255);
        public static Color Warning = new Color32(247, 95, 76, 255);
        public static Color Mint = new Color32(0, 191, 165, 255);

        static bool _loaded;

        /// <summary>에디터에서는 실제 테마 에셋 값을 우선 사용 (빌드에서는 위 상수).</summary>
        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            var t = MapEditorTheme.LoadOrNull();
            if (t == null) return;
            Background = t.Background; TextPrimary = t.TextPrimary; TextMuted = t.TextMuted; TextOnLight = t.TextOnLight;
            Accent = t.Accent; Warning = t.Warning; Mint = t.Mint;
        }
    }

    // ------------------------------------------------------------------ 스프라이트 (Assets/image — 에디터에서만 로드, 없으면 절차 생성)

    struct Skin
    {
        public Sprite Sprite;
        public float PpuMult;   // 9-slice 보더가 @2x/@3x 캡처 기준이라 MapEditorHudBuilder 와 같은 배율을 쓴다
        public Image.Type Type;
    }

    static Skin _panelDark, _badgeRound, _pillGreen, _pillCoral, _btnDark, _ringTrack, _ringNormal, _ringWarning;
    static Sprite _vignette;
    static bool _skinsLoaded;

    static void LoadSkins()
    {
        if (_skinsLoaded) return;
        _skinsLoaded = true;
        _panelDark = Sliced("panels/panel_dark", 2.5f, new Color32(46, 58, 82, 255), 18);
        _badgeRound = Sliced("timer/badge_round_blank", 2.7f, new Color32(46, 58, 82, 255), 14);
        _pillGreen = Sliced("tooltips/pill_green_blank", 2.8f, Theme.Accent, 20);
        _pillCoral = Sliced("tooltips/pill_coral_blank", 2.8f, Theme.Warning, 20);
        _btnDark = Sliced("buttons/btn_dark_blank", 2.8f, new Color32(70, 83, 110, 255), 12);
        _ringTrack = Simple("timer/timer_ring_track", () => MakeRing("ring_track", new Color32(70, 83, 110, 255)));
        _ringNormal = Simple("timer/timer_ring_fill_normal", () => MakeRing("ring_fill_normal", Theme.Accent));
        _ringWarning = Simple("timer/timer_ring_fill_warning", () => MakeRing("ring_fill_warning", Theme.Warning));
        _vignette = MakeVignette();
    }

    static Sprite LoadSprite(string rel)
    {
#if UNITY_EDITOR
        return UnityEditor.AssetDatabase.LoadAssetAtPath<Sprite>("Assets/image/" + rel + ".png");
#else
        return null;
#endif
    }

    static Skin Sliced(string rel, float ppuMult, Color fallbackColor, int fallbackRadius)
    {
        var s = LoadSprite(rel);
        if (s != null) return new Skin { Sprite = s, PpuMult = ppuMult, Type = Image.Type.Sliced };
        return new Skin { Sprite = MakeRoundedRect(rel.Replace('/', '_'), fallbackColor, fallbackRadius), PpuMult = 1f, Type = Image.Type.Sliced };
    }

    static Skin Simple(string rel, System.Func<Sprite> fallback)
    {
        var s = LoadSprite(rel);
        return new Skin { Sprite = s != null ? s : fallback(), PpuMult = 1f, Type = Image.Type.Simple };
    }

    /// <summary>9-slice 용 둥근 사각형 (한 변 = 반지름×2 + 4, 보더 = 반지름). 빌드 폴백.</summary>
    static Sprite MakeRoundedRect(string name, Color color, int radius)
    {
        int size = radius * 2 + 4;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = name, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        var px = new Color[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float cx = Mathf.Clamp(x + 0.5f, radius, size - radius), cy = Mathf.Clamp(y + 0.5f, radius, size - radius);
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
                float a = Mathf.Clamp01(radius - d + 0.5f);
                px[y * size + x] = new Color(color.r, color.g, color.b, color.a * a);
            }
        tex.SetPixels(px); tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(radius, radius, radius, radius));
    }

    /// <summary>타이머 링 (Filled Radial360 용). 빌드 폴백.</summary>
    static Sprite MakeRing(string name, Color color)
    {
        const int size = 128;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = name, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        var px = new Color[size * size];
        float rOut = size * 0.5f - 1f, rIn = rOut - 9f;
        var c = new Vector2(size * 0.5f, size * 0.5f);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), c);
                float a = Mathf.Clamp01(rOut - d + 0.5f) * Mathf.Clamp01(d - rIn + 0.5f);
                px[y * size + x] = new Color(color.r, color.g, color.b, a);
            }
        tex.SetPixels(px); tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
    }

    /// <summary>가장자리로 갈수록 진해지는 비네트 (사망 연출). 색은 Image.color 로 준다.</summary>
    static Sprite MakeVignette()
    {
        const int size = 96;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "vignette", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        var px = new Color[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float nx = (x + 0.5f) / size * 2f - 1f, ny = (y + 0.5f) / size * 2f - 1f;
                float d = Mathf.Sqrt(nx * nx + ny * ny);
                float a = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.45f, 1.15f, d));
                px[y * size + x] = new Color(1f, 1f, 1f, a);
            }
        tex.SetPixels(px); tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
    }

    // ------------------------------------------------------------------ 레이아웃 상수 (기준 px)

    const float SideMargin = 48f;
    const float WarningSeconds = 10f;   // Docs/204 2.3 "10초 이하 강조"

    // ------------------------------------------------------------------ 인스턴스

    PlaySession _session;
    Canvas _canvas;
    RectTransform _root;

    Text _roundText, _modeText, _subtitleText, _vowText, _attemptsText, _hintText;
    Image _modePill;
    Image _ringFill;
    Text _timerNumber, _timerLabel, _timerRemaining;
    Button _abortBtn;
    Text _abortLabel;

    RectTransform _resultCard;
    Text _resultText, _resultSubText;
    Image _flash, _vignetteImg;

    bool _warning;
    int _lastTickSecond = -1;
    float _resultPop = -1f;
    float _flashT = -1f;
    float _vignetteT = -1f;
    float _shakeT = -1f;
    Vector3 _shakeApplied;
    Camera _shakeCam;

    AudioSource _audio;
    static AudioClip _sfxTick, _sfxTickLast;

    const float ResultPopDuration = 0.4f;
    const float FlashDuration = 0.35f;
    const float VignetteDuration = 0.4f;
    const float ShakeDuration = 0.25f;
    const float ShakeAmplitude = 0.18f;   // 월드 단위

    /// <summary>HUD 캔버스를 만들어 세션에 붙인다. 캔버스는 세션 오브젝트와 같은 씬에 놓인다 (애디티브 언로드 시 함께 정리).</summary>
    public static PlayHud Create(PlaySession session)
    {
        Theme.Load();
        LoadSkins();
        var canvas = RuntimeUI.Canvas("Play HUD (runtime)", 200, session.gameObject);
        var hud = canvas.gameObject.AddComponent<PlayHud>();
        hud._session = session;
        hud._canvas = canvas;
        hud.Build();
        hud.Refresh();
        return hud;
    }

    // ------------------------------------------------------------------ build

    void Build()
    {
        _root = Rt("Root", _canvas.transform);
        _root.anchorMin = Vector2.zero; _root.anchorMax = Vector2.one;
        _root.offsetMin = _root.offsetMax = Vector2.zero;

        // ---- 연출 레이어 (맨 뒤): 비네트 · 플래시 — 입력을 막지 않게 raycast 끔
        _vignetteImg = Img("Vignette", _root, new Skin { Sprite = _vignette, PpuMult = 1f, Type = Image.Type.Simple }, Theme.Warning);
        Stretch(_vignetteImg.rectTransform);
        _vignetteImg.color = new Color(Theme.Warning.r, Theme.Warning.g, Theme.Warning.b, 0f);
        _vignetteImg.gameObject.SetActive(false);

        _flash = Img("Flash", _root, new Skin { Sprite = null, PpuMult = 1f, Type = Image.Type.Simple }, Color.white);
        Stretch(_flash.rectTransform);
        _flash.color = new Color(1f, 1f, 1f, 0f);
        _flash.gameObject.SetActive(false);

        // ---- 좌상단: 라운드 배지 + 모드 필 + 부제
        var badge = Img("RoundBadge", _root, _badgeRound, Color.white);
        Place(badge.rectTransform, new Vector2(0, 1), new Vector2(0, 0.5f), new Vector2(SideMargin, -60), new Vector2(110, 58));
        _roundText = Txt("RoundText", badge.transform, "1", 30, FontStyle.Bold, Theme.TextPrimary, TextAnchor.MiddleCenter);
        Stretch(_roundText.rectTransform);
        Place(Txt("RoundKo", _root, "라운드", 18, FontStyle.Normal, Theme.TextMuted, TextAnchor.MiddleLeft).rectTransform,
            new Vector2(0, 1), new Vector2(0, 0.5f), new Vector2(SideMargin + 122, -48), new Vector2(120, 24));
        Place(Txt("RoundEn", _root, "ROUND", 16, FontStyle.Bold, Theme.TextMuted, TextAnchor.MiddleLeft).rectTransform,
            new Vector2(0, 1), new Vector2(0, 0.5f), new Vector2(SideMargin + 122, -72), new Vector2(120, 24));

        _modePill = Img("ModePill", _root, _pillGreen, Color.white);
        Place(_modePill.rectTransform, new Vector2(0, 1), new Vector2(0, 0.5f), new Vector2(SideMargin, -122), new Vector2(180, 42));
        _modeText = Txt("ModeText", _modePill.transform, "검증 플레이", 20, FontStyle.Bold, Theme.TextOnLight, TextAnchor.MiddleCenter);
        Stretch(_modeText.rectTransform);
        _subtitleText = Txt("Subtitle", _root, "", 18, FontStyle.Normal, Theme.TextMuted, TextAnchor.MiddleLeft);
        Place(_subtitleText.rectTransform, new Vector2(0, 1), new Vector2(0, 0.5f), new Vector2(SideMargin + 192, -122), new Vector2(520, 28));
        _subtitleText.horizontalOverflow = HorizontalWrapMode.Wrap; _subtitleText.verticalOverflow = VerticalWrapMode.Truncate;

        // ---- 중앙 상단: 뜻 칩 + 시도
        var vowChip = Img("VowChip", _root, _panelDark, Color.white);
        Place(vowChip.rectTransform, new Vector2(0.5f, 1), new Vector2(0.5f, 0.5f), new Vector2(0, -62), new Vector2(760, 60));
        _vowText = Txt("VowText", vowChip.transform, "", 26, FontStyle.Bold, Theme.TextPrimary, TextAnchor.MiddleCenter);
        Stretch(_vowText.rectTransform);
        _vowText.rectTransform.offsetMin = new Vector2(20, 0); _vowText.rectTransform.offsetMax = new Vector2(-20, 0);
        _vowText.horizontalOverflow = HorizontalWrapMode.Wrap; _vowText.verticalOverflow = VerticalWrapMode.Truncate;
        _attemptsText = Txt("Attempts", _root, "", 20, FontStyle.Bold, Theme.TextMuted, TextAnchor.MiddleCenter);
        Place(_attemptsText.rectTransform, new Vector2(0.5f, 1), new Vector2(0.5f, 0.5f), new Vector2(0, -118), new Vector2(760, 28));

        // ---- 우상단: 타이머 패널 (에디터 HUD 와 동일 구성)
        var timer = Img("TimerPanel", _root, _panelDark, Color.white);
        Place(timer.rectTransform, new Vector2(1, 1), new Vector2(1, 0.5f), new Vector2(-SideMargin, -79), new Vector2(176, 84));
        var ringRect = new Vector2(62, 62);
        var track = Img("RingTrack", timer.transform, _ringTrack, Color.white);
        Place(track.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(14, 0), ringRect);
        _ringFill = Img("RingFill", timer.transform, _ringNormal, Color.white);
        _ringFill.type = Image.Type.Filled;
        _ringFill.fillMethod = Image.FillMethod.Radial360;
        _ringFill.fillOrigin = (int)Image.Origin360.Top;
        _ringFill.fillClockwise = true;
        _ringFill.fillAmount = 1f;
        Place(_ringFill.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(14, 0), ringRect);
        _timerNumber = Txt("TimerNumber", timer.transform, "0", 22, FontStyle.Bold, Theme.TextPrimary, TextAnchor.MiddleCenter);
        Place(_timerNumber.rectTransform, new Vector2(0, 0.5f), new Vector2(0, 0.5f), new Vector2(14, 0), ringRect);
        _timerLabel = Txt("RemainingLabel", timer.transform, "남은 시간", 15, FontStyle.Normal, Theme.TextMuted, TextAnchor.MiddleLeft);
        Place(_timerLabel.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(88, -14), new Vector2(84, 18));
        _timerRemaining = Txt("RemainingTime", timer.transform, "0:00", 30, FontStyle.Bold, Theme.Accent, TextAnchor.MiddleLeft);
        Place(_timerRemaining.rectTransform, new Vector2(0, 1), new Vector2(0, 1), new Vector2(88, -32), new Vector2(84, 40));

        // ---- 우상단: 기권 / 에디터로 돌아가기 (타이머 왼쪽)
        var abortBg = Img("AbortButton", _root, _btnDark, Color.white);
        abortBg.raycastTarget = true;
        Place(abortBg.rectTransform, new Vector2(1, 1), new Vector2(1, 0.5f), new Vector2(-SideMargin - 176 - 14, -79), new Vector2(230, 48));
        _abortBtn = abortBg.gameObject.AddComponent<Button>();
        _abortBtn.targetGraphic = abortBg;
        var colors = _abortBtn.colors;
        colors.normalColor = Color.white; colors.highlightedColor = new Color(0.92f, 0.92f, 0.92f); colors.pressedColor = new Color(0.8f, 0.8f, 0.8f);
        colors.selectedColor = Color.white; colors.fadeDuration = 0.08f;
        _abortBtn.colors = colors;
        _abortBtn.onClick.AddListener(() => { if (_session != null) _session.Abort(); });
        _abortLabel = Txt("Label", abortBg.transform, _session.AbortLabel, 18, FontStyle.Bold, Theme.Warning, TextAnchor.MiddleCenter);
        Stretch(_abortLabel.rectTransform);

        // ---- 하단: 조작 안내 (작게)
        var hintBg = Img("HintPanel", _root, _panelDark, new Color(1f, 1f, 1f, 0.75f));
        Place(hintBg.rectTransform, new Vector2(0.5f, 0), new Vector2(0.5f, 0), new Vector2(0, 18), new Vector2(880, 36));
        _hintText = Txt("Hint", hintBg.transform, "", 16, FontStyle.Normal, Theme.TextMuted, TextAnchor.MiddleCenter);
        Stretch(_hintText.rectTransform);

        // ---- 중앙: 결과 카드 (팝 연출용 — 평소 숨김)
        var card = Img("ResultCard", _root, _panelDark, Color.white);
        Place(card.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0, 40), new Vector2(720, 170));
        _resultCard = card.rectTransform;
        _resultText = Txt("ResultText", card.transform, "", 56, FontStyle.Bold, Theme.Accent, TextAnchor.MiddleCenter);
        _resultText.rectTransform.anchorMin = new Vector2(0, 0.35f); _resultText.rectTransform.anchorMax = new Vector2(1, 1);
        _resultText.rectTransform.offsetMin = _resultText.rectTransform.offsetMax = Vector2.zero;
        _resultSubText = Txt("ResultSub", card.transform, "", 20, FontStyle.Normal, Theme.TextMuted, TextAnchor.MiddleCenter);
        _resultSubText.rectTransform.anchorMin = new Vector2(0, 0); _resultSubText.rectTransform.anchorMax = new Vector2(1, 0.35f);
        _resultSubText.rectTransform.offsetMin = new Vector2(0, 10); _resultSubText.rectTransform.offsetMax = Vector2.zero;
        _resultCard.gameObject.SetActive(false);

        // ---- 오디오 (틱 소리) — PlayerController 와 같은 절차 생성 톤
        _audio = gameObject.AddComponent<AudioSource>();
        _audio.playOnAwake = false;
        _audio.spatialBlend = 0f;
        EnsureSfx();

        RefreshStatic();
    }

    // ------------------------------------------------------------------ refresh

    /// <summary>모드·부제·조작 안내처럼 세션 중 거의 바뀌지 않는 텍스트.</summary>
    void RefreshStatic()
    {
        if (_session == null) return;

        // Title 형식: "검증 플레이 — 상대 뜻: 저속" / "교환 플레이 — 플레이어2의 맵" / "교환 플레이 (데모 맵 — 상대 맵 없음)"
        string title = _session.Title ?? "";
        string mode = title, sub = "";
        int dash = title.IndexOf(" — ", System.StringComparison.Ordinal);
        int paren = title.IndexOf(" (", System.StringComparison.Ordinal);
        if (paren >= 0 && (dash < 0 || paren < dash)) { mode = title.Substring(0, paren); sub = title.Substring(paren + 2).TrimEnd(')'); }
        else if (dash >= 0) { mode = title.Substring(0, dash); sub = title.Substring(dash + 3); }

        bool exchange = _session.AbortMeansGiveUp;
        Apply(_modePill, exchange ? _pillCoral : _pillGreen);
        _modeText.text = mode;
        _modeText.color = exchange ? Theme.TextPrimary : Theme.TextOnLight;
        _subtitleText.text = sub;

        int round = GameFlow.Instance != null ? GameFlow.Instance.Round : 1;
        _roundText.text = round.ToString();

        string esc = exchange ? "ESC 기권" : "ESC 에디터로";
        _hintText.text = "이동 A/D · ←/→     점프 W · ↑     빠른 낙하 S · ↓     R 리스폰     " + esc;

        _timerLabel.text = exchange ? (_session.TimeLimit > 0f ? "남은 시간" : "경과 시간") : "남은 수정 시간";
        if (_abortLabel != null) _abortLabel.text = _session.AbortLabel;
    }

    /// <summary>매 프레임 (PlaySession.UpdateHud 에서 호출). 시간·시도·뜻 상태.</summary>
    public void Refresh()
    {
        if (_session == null) return;

        // 뜻 (실시간 상태 포함: 남은 점프 수, 쿨다운)
        _vowText.text = VowCatalog.HudLine(_session.Vows, _session.Player, _session);

        // 시도
        string tries = _session.AttemptLimit > 0 ? $"시도 {_session.Attempts} / {_session.AttemptLimit}" : $"시도 {_session.Attempts}";
        _attemptsText.text = tries + "   ·   R 리스폰";
        _attemptsText.color = _session.AttemptLimit > 0 && _session.Attempts >= _session.AttemptLimit ? Theme.Warning : Theme.TextMuted;

        // 타이머 — 검증 플레이: 에디터의 남은 그리기 시간 (검증 중에도 그리기 마감은 계속 흐른다) / 교환 플레이: 남은 플레이 시간
        float limit, shown;
        bool countdown;
        if (_session.AbortMeansGiveUp)
        {
            limit = _session.TimeLimit;
            countdown = limit > 0f;
            shown = countdown ? Mathf.Max(0f, limit - _session.Elapsed) : _session.Elapsed;
        }
        else
        {
            limit = DrawTimeLimit();
            countdown = limit > 0f;
            shown = DrawTimeRemaining(limit);
        }
        int sec = countdown ? Mathf.CeilToInt(shown) : Mathf.FloorToInt(shown);
        _timerNumber.text = sec.ToString();
        _timerRemaining.text = string.Format("{0}:{1:00}", sec / 60, sec % 60);
        _ringFill.fillAmount = countdown ? Mathf.Clamp01(shown / limit) : 1f;

        bool warn = countdown && shown <= WarningSeconds && !_session.IsFinished;
        if (warn != _warning)
        {
            _warning = warn;
            Apply(_ringFill, warn ? _ringWarning : _ringNormal);
            _ringFill.type = Image.Type.Filled;
            _timerRemaining.color = warn ? Theme.Warning : Theme.Accent;
            _timerNumber.color = warn ? Theme.Warning : Theme.TextPrimary;
        }
        if (warn)
        {
            // 경고 중 숫자 펄스 + 초가 바뀔 때마다 틱
            float pulse = 1f + 0.12f * Mathf.Abs(Mathf.Sin((shown % 1f) * Mathf.PI));
            _timerNumber.rectTransform.localScale = Vector3.one * pulse;
            if (sec != _lastTickSecond)
            {
                _lastTickSecond = sec;
                PlaySfx(sec <= 3 ? _sfxTickLast : _sfxTick);
            }
        }
        else if (_timerNumber.rectTransform.localScale != Vector3.one) _timerNumber.rectTransform.localScale = Vector3.one;
    }

    /// <summary>그리기 제한 시간 (방 설정). 0 = 제한 없음.</summary>
    static float DrawTimeLimit() => Mathf.Max(0f, MatchData.Instance.Settings.DrawTimeLimit);

    /// <summary>
    /// 에디터 HUD(MapEditorHud.UpdateTimer)와 같은 시계: 멀티 매치에서는 GameFlow 의 그리기 마감, 단독 실행 시에는 씬 로드 기준 로컬 시계.
    /// (MapEditorHud 는 Bind 시각을 기준으로 재지만 단독 실행에서는 씬 시작과 같으므로 timeSinceLevelLoad 로 대신한다)
    /// </summary>
    static float DrawTimeRemaining(float limit)
    {
        var flow = GameFlow.Instance;
        if (flow != null && flow.DrawTimeRemaining >= 0f) return flow.DrawTimeRemaining;
        return limit > 0f ? Mathf.Max(0f, limit - Time.timeSinceLevelLoad) : 0f;
    }

    /// <summary>AbortLabel 이 바뀐 뒤 (PlayBootstrap 이 Begin 후에 바꾼다).</summary>
    public void SetAbortLabel(string label)
    {
        if (_abortLabel != null) _abortLabel.text = label;
        RefreshStatic();
    }

    // ------------------------------------------------------------------ 연출

    /// <summary>플레이 종료 결과 카드. 클리어 = 팝 + 플래시, 미클리어 = 팝 + 붉은 비네트.</summary>
    public void ShowResult(bool cleared, string mainText, string subText)
    {
        _resultCard.gameObject.SetActive(true);
        _resultText.text = mainText;
        _resultText.color = cleared ? Theme.Accent : Theme.Warning;
        _resultSubText.text = subText ?? "";
        _resultCard.localScale = Vector3.zero;
        _resultPop = 0f;
        if (cleared) { _flash.gameObject.SetActive(true); _flashT = 0f; }
        else PlayDeathEffect(shake: false);
        _abortBtn.interactable = !(_session != null && _session.AbortMeansGiveUp);   // 교환 플레이는 종료 후 대기만, 검증은 곧바로 복귀 가능
    }

    /// <summary>사망(리스폰) 연출: 붉은 비네트 + 짧은 카메라 흔들림.</summary>
    public void PlayDeathEffect(bool shake = true)
    {
        _vignetteImg.gameObject.SetActive(true);
        _vignetteT = 0f;
        if (shake)
        {
            _shakeCam = Camera.main;
            if (_shakeCam != null) { _shakeT = 0f; _shakeApplied = Vector3.zero; }
        }
    }

    void Update()
    {
        float dt = Time.unscaledDeltaTime;

        if (_resultPop >= 0f)
        {
            _resultPop += dt;
            float t = Mathf.Clamp01(_resultPop / ResultPopDuration);
            float s = 1f + 0.35f * (1f - t) * Mathf.Sin(t * Mathf.PI * 1.5f);   // 오버슛 후 안정
            _resultCard.localScale = Vector3.one * Mathf.Lerp(0.6f, 1f, EaseOut(t)) * (t < 1f ? Mathf.Max(0.6f, s) : 1f);
            if (t >= 1f) { _resultCard.localScale = Vector3.one; _resultPop = -1f; }
        }

        if (_flashT >= 0f)
        {
            _flashT += dt;
            float t = Mathf.Clamp01(_flashT / FlashDuration);
            _flash.color = new Color(1f, 1f, 1f, 0.55f * (1f - EaseOut(t)));
            if (t >= 1f) { _flashT = -1f; _flash.gameObject.SetActive(false); }
        }

        if (_vignetteT >= 0f)
        {
            _vignetteT += dt;
            float t = Mathf.Clamp01(_vignetteT / VignetteDuration);
            float a = t < 0.2f ? Mathf.Lerp(0f, 0.75f, t / 0.2f) : Mathf.Lerp(0.75f, 0f, (t - 0.2f) / 0.8f);
            _vignetteImg.color = new Color(Theme.Warning.r, Theme.Warning.g, Theme.Warning.b, a);
            if (t >= 1f) { _vignetteT = -1f; _vignetteImg.gameObject.SetActive(false); }
        }

        if (_shakeT >= 0f)
        {
            _shakeT += dt;
            float t = Mathf.Clamp01(_shakeT / ShakeDuration);
            Vector3 target = t < 1f ? (Vector3)(Random.insideUnitCircle * ShakeAmplitude * (1f - t)) : Vector3.zero;
            if (_shakeCam != null) _shakeCam.transform.position += target - _shakeApplied;   // 다른 코드가 카메라를 옮겨도 누적 오차가 남지 않게 델타만 적용
            _shakeApplied = target;
            if (t >= 1f) { _shakeT = -1f; _shakeCam = null; }
        }
    }

    void OnDisable()
    {
        // 흔들림 도중 파괴되면 카메라를 제자리로
        if (_shakeT >= 0f && _shakeCam != null) { _shakeCam.transform.position -= _shakeApplied; _shakeApplied = Vector3.zero; _shakeT = -1f; }
    }

    static float EaseOut(float t) => 1f - (1f - t) * (1f - t) * (1f - t);

    // ------------------------------------------------------------------ 오디오 (절차 생성 — PlayerController.MakeTone 과 같은 방식)

    void PlaySfx(AudioClip clip)
    {
        if (clip == null || _audio == null) return;
        _audio.PlayOneShot(clip, 0.35f);
    }

    static void EnsureSfx()
    {
        if (_sfxTick != null) return;
        _sfxTick = MakeTone("sfx_tick", 0.05f, 1400f, 1100f, 0.5f);
        _sfxTickLast = MakeTone("sfx_tick_last", 0.08f, 1900f, 1500f, 0.6f);
    }

    static AudioClip MakeTone(string name, float duration, float f0, float f1, float gain)
    {
        const int rate = 44100;
        int n = Mathf.CeilToInt(duration * rate);
        var data = new float[n];
        float phase = 0f;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)n;
            float f = Mathf.Lerp(f0, f1, t);
            phase += 2f * Mathf.PI * f / rate;
            float env = Mathf.Sin(Mathf.PI * t);
            float sq = Mathf.Sin(phase) >= 0f ? 1f : -1f;
            data[i] = (sq * 0.25f + Mathf.Sin(phase) * 0.75f) * env * gain;
        }
        var clip = AudioClip.Create(name, n, 1, rate, false);
        clip.SetData(data, 0);
        return clip;
    }

    // ------------------------------------------------------------------ 위젯 헬퍼 (MapEditorHudBuilder 와 같은 규약)

    static RectTransform Rt(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        return rt;
    }

    static Image Img(string name, Transform parent, Skin skin, Color color)
    {
        var rt = Rt(name, parent);
        var img = rt.gameObject.AddComponent<Image>();
        Apply(img, skin);
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    static void Apply(Image img, Skin skin)
    {
        img.sprite = skin.Sprite;
        img.type = skin.Sprite != null ? skin.Type : Image.Type.Simple;
        img.pixelsPerUnitMultiplier = skin.PpuMult;
    }

    static Text Txt(string name, Transform parent, string text, int size, FontStyle style, Color color, TextAnchor align)
    {
        var rt = Rt(name, parent);
        var t = rt.gameObject.AddComponent<Text>();
        t.font = RuntimeUI.Font;
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

    static void Place(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = anchor;
        rt.pivot = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
    }

    static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = rt.offsetMax = Vector2.zero;
    }
}
