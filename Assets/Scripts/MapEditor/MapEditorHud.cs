using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 맵 에디터 HUD (프리팹 Assets/Prefabs/UI/MapEditorHud.prefab 의 루트 컴포넌트). Docs/204 2.2 · Docs/203 1장.
///
/// 구성
///  - BackCanvas (Screen Space Camera, 정렬 -100): 배경 도트 + 종이 프레임. 월드 스트로크 뒤에 그려진다.
///  - HudCanvas (Overlay, 정렬 100): 라운드·안내문·타이머 / 도구 패널 / 굵기·색상 / 상태 / 검증·완료 버튼.
///  - PaperSlot: 캔버스가 차지할 영역. 매 프레임 이 영역을 뷰포트 비율로 바꿔 카메라를 맞추고(FitCamera),
///    맵 사각형(0,0)~(30,15)을 화면에 투영해 종이 프레임을 그 위에 덮는다 → 해상도가 바뀌어도 UI 와 맵이 어긋나지 않는다.
///
/// 로직은 전부 MapEditorController 에 있고 여기서는 호출·표시만 한다. 씬에 이 프리팹이 있으면 컨트롤러가 찾아서 Bind 한다.
/// 그리기 타이머: 멀티 매치에서는 GameFlow.DrawTimeRemaining 을 표시하고, 만료 시 GameFlow 가 제출 실패(패배)로 처리한다. 단독 실행 시에는 표시만.
/// </summary>
public class MapEditorHud : MonoBehaviour
{
    [Header("테마")]
    public MapEditorTheme Theme;

    [Header("캔버스")]
    [SerializeField] Canvas backCanvas;
    [SerializeField] Canvas hudCanvas;
    [SerializeField] RectTransform paperSlot;
    [SerializeField] RectTransform paperFrame;
    [Tooltip("맵 사각형 바깥으로 종이 프레임을 얼마나 키울지 (기준 해상도 px)")]
    [SerializeField] float paperFrameOutset = 30f;
    [Tooltip("맵이 슬롯 안에서 차지하는 비율 여유 (1 = 꽉 채움)")]
    [SerializeField] float cameraMargin = 1.04f;

    [Header("상단")]
    [SerializeField] Text roundText;
    [SerializeField] Text subtitleText;
    [SerializeField] Text titleText;
    [Tooltip("표시용 총 라운드 수. 0 이면 라운드 번호만 표시 — 라운드 상한이 없으므로(Docs/100 3장) 기본 0")]
    [SerializeField] int totalRounds = 0;

    [Header("타이머")]
    [SerializeField] Image ringFill;
    [SerializeField] Sprite ringNormal;
    [SerializeField] Sprite ringWarning;
    [SerializeField] Text timerNumber;
    [SerializeField] Text timerRemaining;
    [SerializeField] float warningSeconds = 10f;

    [Header("도구")]
    [SerializeField] HudToolButton penButton;
    [SerializeField] HudToolButton eraserButton;
    [SerializeField] HudToolButton undoButton;
    [SerializeField] HudToolButton clearButton;
    [SerializeField] HudToolButton goalButton;
    [SerializeField] HudToolButton[] widthButtons;

    [Header("색상 팔레트")]
    [SerializeField] RectTransform swatchRoot;
    [SerializeField] GameObject swatchTemplate;   // 비활성 템플릿: Image(원) + 자식 Image(체크)

    [Header("하단")]
    [SerializeField] Text statusText;
    [SerializeField] Text statsText;
    [SerializeField] HudToolButton verifyButton;
    [SerializeField] HudToolButton completeButton;

    MapEditorController _c;
    Camera _cam;
    readonly Dictionary<int, SwatchView> _swatches = new Dictionary<int, SwatchView>();
    Rect _lastViewport = new Rect(-1, -1, 0, 0);
    float _drawLimit;
    float _drawStart;
    bool _warning;

    class SwatchView { public Image Circle; public Image Check; public RectTransform Rect; }

    // ------------------------------------------------------------------ binding

    public void Bind(MapEditorController controller)
    {
        _c = controller;
        _cam = controller.Camera;
        if (backCanvas != null)
        {
            backCanvas.renderMode = RenderMode.ScreenSpaceCamera;
            backCanvas.worldCamera = _cam;
            backCanvas.planeDistance = 20f;
        }

        WireButtons();
        BuildSwatches();

        _drawLimit = Mathf.Max(0, MatchData.Instance.Settings.DrawTimeLimit);
        _drawStart = Time.time;

        _c.Changed += Refresh;
        _c.StatusChanged += OnStatus;
        _c.VerificationChanged += OnVerification;
        Refresh();
        OnStatus(_c.Status);
        UpdateTimer(true);

        Canvas.ForceUpdateCanvases();   // 첫 프레임부터 슬롯 크기가 맞도록 레이아웃을 먼저 계산
        FitCameraToSlot();
        PlacePaperFrame();
    }

    void OnDestroy()
    {
        if (_c != null)
        {
            _c.Changed -= Refresh;
            _c.StatusChanged -= OnStatus;
            _c.VerificationChanged -= OnVerification;
        }
    }

    /// <summary>검증 플레이·제출 잠금 중에는 조작 UI 만 숨긴다 (배경·종이 프레임은 유지).</summary>
    public void SetVisible(bool visible)
    {
        if (hudCanvas != null) hudCanvas.gameObject.SetActive(visible);
    }

    void WireButtons()
    {
        Hook(penButton, () => _c.SetTool(EditorTool.Pen));
        Hook(eraserButton, () => _c.SetTool(EditorTool.Eraser));
        Hook(undoButton, () => _c.Undo());
        Hook(clearButton, () => _c.ClearAll());
        Hook(goalButton, () => _c.SetTool(EditorTool.Goal));
        if (widthButtons != null)
            for (int i = 0; i < widthButtons.Length; i++) { int idx = i; Hook(widthButtons[i], () => _c.SetWidthIndex(idx)); }
        Hook(verifyButton, () => _c.StartVerification());
        Hook(completeButton, () => _c.Complete());
    }

    static void Hook(HudToolButton b, UnityEngine.Events.UnityAction a)
    {
        if (b == null || b.Button == null) return;
        b.Button.onClick.RemoveAllListeners();
        b.Button.onClick.AddListener(a);
    }

    void BuildSwatches()
    {
        if (swatchRoot == null || swatchTemplate == null || _c.Palette == null) return;
        foreach (var kv in _swatches) if (kv.Value.Rect != null) Destroy(kv.Value.Rect.gameObject);
        _swatches.Clear();
        swatchTemplate.SetActive(false);

        var entries = _c.Palette.Entries;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var go = Instantiate(swatchTemplate, swatchRoot);
            go.name = "Swatch " + e.Name;
            go.SetActive(true);
            var v = new SwatchView { Rect = go.GetComponent<RectTransform>(), Circle = go.GetComponent<Image>() };
            var check = go.transform.childCount > 0 ? go.transform.GetChild(0).GetComponent<Image>() : null;
            v.Check = check;
            v.Circle.color = e.Color;
            if (check != null) check.color = Luma(e.Color) > 0.6f ? (Theme != null ? Theme.TextOnLight : Color.black) : Color.white;
            var btn = go.GetComponent<Button>();
            if (btn == null) btn = go.AddComponent<Button>();
            btn.targetGraphic = v.Circle;
            int id = e.ColorId;
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(() => _c.SetColorId(id));
            _swatches[id] = v;
        }
    }

    // ------------------------------------------------------------------ refresh

    void Refresh()
    {
        if (_c == null) return;
        bool editable = !_c.Locked && !_c.InVerification;

        Apply(penButton, _c.Tool == EditorTool.Pen, editable);
        Apply(eraserButton, _c.Tool == EditorTool.Eraser, editable);
        Apply(goalButton, _c.Tool == EditorTool.Goal, editable);
        Apply(undoButton, false, editable && _c.CanUndo);
        Apply(clearButton, false, editable && (_c.Map.Strokes.Count > 0 || _c.Map.HasGoal));

        if (widthButtons != null)
            for (int i = 0; i < widthButtons.Length; i++) Apply(widthButtons[i], i == _c.WidthIndex, editable);

        foreach (var kv in _swatches)
        {
            bool sel = kv.Key == _c.ColorId && _c.Tool != EditorTool.Eraser;
            if (kv.Value.Check != null) kv.Value.Check.enabled = sel;
            kv.Value.Rect.localScale = Vector3.one * (sel ? 1.15f : 1f);
        }

        Apply(verifyButton, true, _c.CanVerify);
        Apply(completeButton, true, _c.CanComplete);

        RefreshTitle();
        RefreshRound();
        RefreshStats();
    }

    void RefreshTitle()
    {
        string sub, title; Color subColor = Theme != null ? Theme.Accent : Color.green;
        if (_c.Locked) { sub = "제출 완료"; title = "상대가 맵을 완성할 때까지 기다리는 중"; }
        else if (_c.InVerification) { sub = "검증 플레이"; title = "시작점에서 골까지 도달하면 검증 성공"; }
        else if (_c.Tool == EditorTool.Goal) { sub = "골 배치"; title = "캔버스를 클릭해 골을 배치하세요"; subColor = Theme != null ? Theme.Warning : Color.red; }
        else if (_c.Tool == EditorTool.Eraser) { sub = "지우개"; title = "드래그한 부분의 선이 지워집니다"; }
        else if (_c.IsVerified) { sub = "검증 클리어!"; title = "완료를 누르면 맵이 제출됩니다"; }
        else if (!_c.Map.HasGoal && _c.Map.Strokes.Count > 0) { sub = "지금은, 그릴 시간!"; title = "골을 배치해야 검증할 수 있습니다"; }
        else { sub = "지금은, 그릴 시간!"; title = "시작점에서 골까지 경로를 그려보세요"; }
        if (subtitleText != null) { subtitleText.text = sub; subtitleText.color = subColor; }
        if (titleText != null) titleText.text = title;
    }

    void RefreshRound()
    {
        if (roundText == null) return;
        int round = GameFlow.Instance != null ? GameFlow.Instance.Round : 1;
        roundText.text = totalRounds > 0 ? round + " / " + totalRounds : round.ToString();
    }

    void RefreshStats()
    {
        if (statsText == null) return;
        int raw = MapSerializer.EstimateRawBytes(_c.Map);
        string goal = _c.Map.HasGoal ? "골 배치됨" : "골 미배치";
        string verify = _c.IsVerified ? string.Format("검증 OK · 패타임 {0:0.00}s", _c.VerifiedParTime) : "검증 필요";
        statsText.text = string.Format("스트로크 {0}/{1} · 점 {2} · 약 {3:0.0} KB · {4} · {5}",
            _c.Map.Strokes.Count, MapConstants.MaxStrokes, _c.Map.TotalPoints, raw / 1024f, goal, verify);
    }

    void OnStatus(string s) { if (statusText != null) statusText.text = s; }

    void OnVerification(bool inVerification)
    {
        SetVisible(!inVerification && !_c.Locked);
        Refresh();
    }

    static void Apply(HudToolButton b, bool active, bool interactable) { if (b != null) b.Apply(active, interactable); }

    static float Luma(Color c) => 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;

    // ------------------------------------------------------------------ per-frame: 타이머 · 카메라 맞춤 · 종이 프레임

    void Update()
    {
        if (_c == null) return;
        UpdateTimer(false);
    }

    void LateUpdate()
    {
        if (_c == null || _cam == null) return;
        FitCameraToSlot();
        PlacePaperFrame();
    }

    void UpdateTimer(bool force)
    {
        // 멀티 매치 중에는 GameFlow 가 라운드 마감(만료 시 패배 처리)을 관리하므로 그 시계를 그대로 표시한다. 단독 실행 시에는 로컬 시계
        var flow = GameFlow.Instance;
        float remaining = (flow != null && flow.DrawTimeRemaining >= 0f)
            ? flow.DrawTimeRemaining
            : (_drawLimit > 0 ? Mathf.Max(0f, _drawLimit - (Time.time - _drawStart)) : 0f);
        int sec = Mathf.CeilToInt(remaining);
        if (timerNumber != null) timerNumber.text = sec.ToString();
        if (timerRemaining != null) timerRemaining.text = string.Format("{0}:{1:00}", sec / 60, sec % 60);
        if (ringFill != null) ringFill.fillAmount = _drawLimit > 0 ? remaining / _drawLimit : 0f;

        bool warn = _drawLimit > 0 && remaining <= warningSeconds;
        if (warn != _warning || force)
        {
            _warning = warn;
            if (ringFill != null && (warn ? ringWarning : ringNormal) != null) ringFill.sprite = warn ? ringWarning : ringNormal;
            var col = Theme != null ? (warn ? Theme.Warning : Theme.Accent) : (warn ? Color.red : Color.green);
            if (timerRemaining != null) timerRemaining.color = col;
        }
    }

    /// <summary>PaperSlot 의 화면 영역을 뷰포트 비율로 바꿔 카메라를 맞춘다 (해상도 변경에도 대응).</summary>
    void FitCameraToSlot()
    {
        if (paperSlot == null || Screen.width == 0 || Screen.height == 0) return;
        var c = new Vector3[4];
        paperSlot.GetWorldCorners(c);   // Overlay 캔버스 → 월드 좌표 = 화면 픽셀
        var vp = Rect.MinMaxRect(c[0].x / Screen.width, c[0].y / Screen.height, c[2].x / Screen.width, c[2].y / Screen.height);
        if (vp.width <= 0f || vp.height <= 0f) return;
        if ((vp.min - _lastViewport.min).sqrMagnitude < 1e-8f && (vp.max - _lastViewport.max).sqrMagnitude < 1e-8f) return;
        _lastViewport = vp;
        _c.FitCamera(vp, cameraMargin);
    }

    /// <summary>맵 사각형 (0,0)~(W,H) 를 화면에 투영해 종이 프레임을 그 위에 놓는다.</summary>
    void PlacePaperFrame()
    {
        if (paperFrame == null || backCanvas == null) return;
        var canvasRt = backCanvas.transform as RectTransform;
        Vector2 min, max;
        var sMin = _cam.WorldToScreenPoint(new Vector3(0f, 0f, 0f));
        var sMax = _cam.WorldToScreenPoint(new Vector3(MapConstants.CanvasWidth, MapConstants.CanvasHeight, 0f));
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRt, sMin, backCanvas.worldCamera, out min)) return;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRt, sMax, backCanvas.worldCamera, out max)) return;
        paperFrame.anchorMin = paperFrame.anchorMax = new Vector2(0.5f, 0.5f);
        paperFrame.pivot = new Vector2(0.5f, 0.5f);
        paperFrame.anchoredPosition = (min + max) * 0.5f;
        paperFrame.sizeDelta = (max - min) + Vector2.one * (paperFrameOutset * 2f);
    }
}
