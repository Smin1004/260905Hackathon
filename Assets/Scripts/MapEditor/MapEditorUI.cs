using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 맵 에디터 런타임 UI (플레이스홀더 — 공용 UI 킷 확정 후 프리팹으로 교체, Docs/204 2.2).
/// 상단 바: 도구 / 실행취소·다시실행 / 전체 지우기 / 검증 플레이 / 완료, 굵기 / 색상. 하단 바: 상태 문구 + 통계.
/// 카메라는 두 바 사이에 캔버스를 맞추므로(TopBarFraction/BottomBarFraction) UI 가 드로잉 영역을 가리지 않는다.
/// 로직은 전부 MapEditorController 에 있고 여기서는 호출만 한다.
/// </summary>
public class MapEditorUI : MonoBehaviour
{
    /// <summary>화면 높이 대비 상단 바 비율 — CanvasView.FitCamera 가 참조</summary>
    public const float TopBarFraction = 0.13f;
    /// <summary>화면 높이 대비 하단 바 비율</summary>
    public const float BottomBarFraction = 0.075f;

    MapEditorController _c;
    Canvas _canvas;

    Text _status, _stats;
    readonly Dictionary<EditorTool, Image> _toolBtns = new Dictionary<EditorTool, Image>();
    readonly List<Image> _widthBtns = new List<Image>();
    readonly Dictionary<int, Image> _colorBtns = new Dictionary<int, Image>();
    Button _undoBtn, _redoBtn, _verifyBtn, _completeBtn;

    static readonly Color BtnNormal = new Color(0.30f, 0.32f, 0.38f);
    static readonly Color BtnSelected = new Color(0.25f, 0.55f, 0.95f);
    static readonly Color BtnDanger = new Color(0.75f, 0.30f, 0.30f);
    static readonly Color BtnVerify = new Color(0.85f, 0.55f, 0.15f);
    static readonly Color BtnAccent = new Color(0.20f, 0.65f, 0.40f);

    public void Bind(MapEditorController controller)
    {
        _c = controller;
        Build();
        _c.Changed += Refresh;
        _c.StatusChanged += OnStatus;
        Refresh();
        _status.text = _c.Status;
    }

    void OnDestroy()
    {
        if (_c != null) { _c.Changed -= Refresh; _c.StatusChanged -= OnStatus; }
    }

    public void SetVisible(bool visible)
    {
        if (_canvas != null) _canvas.gameObject.SetActive(visible);
    }

    void OnStatus(string s) { if (_status != null) _status.text = s; }

    void Build()
    {
        _canvas = RuntimeUI.Canvas("Editor UI (runtime)", 100, gameObject);
        var root = _canvas.transform;

        // ---- 상단 바 (2줄)
        var top = RuntimeUI.Panel(root, new Vector2(0, 1f - TopBarFraction), new Vector2(1, 1), new Color(0.10f, 0.11f, 0.13f, 0.97f));

        // 1줄: 도구·편집·검증·완료
        float y1a = 0.52f, y1b = 0.95f;
        RuntimeUI.Label(top, new Vector2(0.01f, y1a), new Vector2(0.06f, y1b), "도구", 22, TextAnchor.MiddleLeft, Color.gray);
        _toolBtns[EditorTool.Pen]    = Btn(top, 0.06f, 0.135f, y1a, y1b, "펜", () => _c.SetTool(EditorTool.Pen), BtnNormal);
        _toolBtns[EditorTool.Eraser] = Btn(top, 0.14f, 0.215f, y1a, y1b, "지우개", () => _c.SetTool(EditorTool.Eraser), BtnNormal);
        _toolBtns[EditorTool.Goal]   = Btn(top, 0.22f, 0.295f, y1a, y1b, "골 배치", () => _c.SetTool(EditorTool.Goal), BtnNormal);

        _undoBtn = RuntimeUI.Button(top, new Vector2(0.32f, y1a), new Vector2(0.41f, y1b), "실행취소 (Ctrl+Z)", () => _c.Undo(), BtnNormal, 19);
        _redoBtn = RuntimeUI.Button(top, new Vector2(0.415f, y1a), new Vector2(0.49f, y1b), "다시실행 (Ctrl+Y)", () => _c.Redo(), BtnNormal, 18);
        RuntimeUI.Button(top, new Vector2(0.50f, y1a), new Vector2(0.58f, y1b), "전체 지우기", () => _c.ClearAll(), BtnDanger, 20);

        _verifyBtn = RuntimeUI.Button(top, new Vector2(0.64f, y1a), new Vector2(0.80f, y1b), "▶ 검증 플레이", () => _c.StartVerification(), BtnVerify);
        _completeBtn = RuntimeUI.Button(top, new Vector2(0.81f, y1a), new Vector2(0.99f, y1b), "완료 (맵 확정 → 전송 준비)", () => _c.Complete(), BtnAccent, 20);

        // 2줄: 굵기 + 색상
        float y2a = 0.05f, y2b = 0.48f;
        RuntimeUI.Label(top, new Vector2(0.01f, y2a), new Vector2(0.06f, y2b), "굵기", 22, TextAnchor.MiddleLeft, Color.gray);
        string[] widthNames = { "얇게", "보통", "굵게" };
        for (int i = 0; i < MapConstants.PenWidths.Length; i++)
        {
            int idx = i;
            float x0 = 0.06f + i * 0.08f;
            _widthBtns.Add(Btn(top, x0, x0 + 0.075f, y2a, y2b, widthNames[Mathf.Min(i, widthNames.Length - 1)], () => _c.SetWidthIndex(idx), BtnNormal));
        }

        RuntimeUI.Label(top, new Vector2(0.32f, y2a), new Vector2(0.37f, y2b), "색상", 22, TextAnchor.MiddleLeft, Color.gray);
        var entries = _c.Palette.Entries;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            float x0 = 0.37f + i * 0.055f;
            var btn = RuntimeUI.Button(top, new Vector2(x0, y2a), new Vector2(x0 + 0.05f, y2b), e.Name, () => _c.SetColorId(e.ColorId), e.Color, 20);
            btn.GetComponentInChildren<Text>().color = Luma(e.Color) > 0.6f ? Color.black : Color.white;
            _colorBtns[e.ColorId] = btn.GetComponent<Image>();
        }

        // ---- 하단 바
        var bottom = RuntimeUI.Panel(root, new Vector2(0, 0), new Vector2(1, BottomBarFraction), new Color(0.10f, 0.11f, 0.13f, 0.97f));
        _status = RuntimeUI.Label(bottom, new Vector2(0.01f, 0.5f), new Vector2(0.99f, 1f), "", 22, TextAnchor.MiddleLeft, new Color(1f, 0.9f, 0.55f));
        _stats = RuntimeUI.Label(bottom, new Vector2(0.01f, 0f), new Vector2(0.99f, 0.5f), "", 18, TextAnchor.MiddleLeft, new Color(0.8f, 0.8f, 0.85f));
        _status.verticalOverflow = VerticalWrapMode.Truncate;
        _stats.verticalOverflow = VerticalWrapMode.Truncate;
    }

    void Refresh()
    {
        foreach (var kv in _toolBtns) kv.Value.color = kv.Key == _c.Tool ? BtnSelected : BtnNormal;
        for (int i = 0; i < _widthBtns.Count; i++) _widthBtns[i].color = i == _c.WidthIndex ? BtnSelected : BtnNormal;
        foreach (var kv in _colorBtns)
        {
            var baseColor = _c.Palette.GetColor(kv.Key);
            bool sel = kv.Key == _c.ColorId && _c.Tool != EditorTool.Eraser;
            kv.Value.color = sel ? baseColor : Color.Lerp(baseColor, new Color(0.3f, 0.3f, 0.3f), 0.55f);
        }
        _undoBtn.interactable = _c.CanUndo;
        _redoBtn.interactable = _c.CanRedo;
        _verifyBtn.interactable = _c.CanVerify;
        _completeBtn.interactable = _c.CanComplete;

        int raw = MapSerializer.EstimateRawBytes(_c.Map);
        string goal = _c.Map.HasGoal ? $"골 ({_c.Map.GoalPos.x:0.0}, {_c.Map.GoalPos.y:0.0})" : "골 미배치";
        string verify = _c.IsVerified ? $"검증 OK (패타임 {_c.VerifiedParTime:0.00}s)" : "검증 필요";
        string payload = _c.LastPayload != null ? $" · 마지막 확정 {_c.LastPayload.Length / 1024f:0.0} KB" : "";
        _stats.text = $"스트로크 {_c.Map.Strokes.Count}/{MapConstants.MaxStrokes} · 점 {_c.Map.TotalPoints} · 압축 전 약 {raw / 1024f:0.0} KB{payload} · {goal} · {verify} · 시작점 왼쪽 하단 고정 · 캔버스 {MapConstants.CanvasWidth:0}×{MapConstants.CanvasHeight:0}u";
    }

    Image Btn(Transform parent, float x0, float x1, float y0, float y1, string label, UnityEngine.Events.UnityAction onClick, Color color)
        => RuntimeUI.Button(parent, new Vector2(x0, y0), new Vector2(x1, y1), label, onClick, color).GetComponent<Image>();

    static float Luma(Color c) => 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
}
