using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;

public enum EditorTool { Pen, Eraser, Goal }

/// <summary>
/// 맵 에디터 상태 머신 (MapEditor 씬). Docs/203 1·3·6장.
///
/// - 입력(마우스/터치/펜) → 월드 좌표 → 점 수집(최소 거리 필터) → 확정(단순화·양자화) → MapData
/// - 도구: 펜(굵기·색상), 지우개(구간 잘라내기), 골 배치, 실행취소/다시실행(스냅샷, Ctrl+Z / Ctrl+Y), 전체 지우기
/// - 검증 플레이: StartVerification() → PlaySession 이 맵을 콜라이더로 로드하고 플레이어를 스폰. 골 도달 = 검증 성공(패타임 기록).
///   ESC/버튼으로 언제든 에디터 복귀. 맵을 수정하면 검증 무효.
/// - Complete(): 검증 성공 필수 → 직렬화 → 역직렬화 왕복 검증 → MatchData 반영 → Completed 이벤트 (네트워크 전송 지점)
///
/// 모든 조작은 public 메서드로도 노출되어 UI·테스트·자동화가 같은 경로를 탄다.
/// </summary>
public class MapEditorController : MonoBehaviour
{
    [SerializeField] StrokePalette palette;
    [SerializeField] Camera targetCamera;
    [Tooltip("씬에 MapEditorHud 프리팹이 없을 때만 쓰는 플레이스홀더 런타임 UI")]
    [SerializeField] bool buildRuntimeUI = true;

    public EditorTool Tool { get; private set; } = EditorTool.Pen;
    public int WidthIndex { get; private set; } = MapConstants.DefaultWidthIndex;
    public int ColorId { get; private set; } = 0;
    public float PenWidth => MapConstants.PenWidths[Mathf.Clamp(WidthIndex, 0, MapConstants.PenWidths.Length - 1)];
    public float EraserRadius => MapConstants.EraserRadii[Mathf.Clamp(WidthIndex, 0, MapConstants.EraserRadii.Length - 1)];

    public MapData Map { get; private set; } = new MapData();
    public StrokePalette Palette => palette;
    public Camera Camera => targetCamera;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool IsDrawing => _drawing;
    public string Status { get; private set; } = "";
    public byte[] LastPayload { get; private set; }

    /// <summary>검증 플레이로 시작점→골 클리어를 증명했는지. 맵을 수정하면 false 로 돌아간다.</summary>
    public bool IsVerified { get; private set; }
    /// <summary>검증 클리어 시간 = 패타임 (Docs/100 3장 ③).</summary>
    public float VerifiedParTime { get; private set; }
    public bool InVerification => _session != null;
    public PlaySession Session => _session;
    public bool CanVerify => Map.HasGoal && Map.Strokes.Count > 0 && !InVerification && !Locked;
    public bool CanComplete => IsVerified && Map.HasGoal && Map.Strokes.Count > 0 && !InVerification && !Locked;

    /// <summary>제출 후 잠금 — 입력·UI 차단 (GameFlow 가 상대 대기 중 설정). Docs/100 6장 "제출 후 수정 불가".</summary>
    public bool Locked { get; private set; }

    /// <summary>검증 플레이에 적용할 뜻 덮어쓰기 (단독 테스트용). 비어 있으면 MatchData.OpponentVows = 상대의 뜻.
    /// Unity 가 public List 를 빈 리스트로 직렬화하므로 [NonSerialized] — 안 그러면 항상 빈 리스트가 상대 뜻을 가린다.</summary>
    [System.NonSerialized] public System.Collections.Generic.List<VowId> VerificationVows;

    /// <summary>실제로 검증에 적용되는 뜻 목록 (상대의 뜻). Docs/100 6장: 제작자는 상대의 뜻으로 자기 맵을 클리어해야 한다.</summary>
    public System.Collections.Generic.List<VowId> EffectiveVerificationVows =>
        (VerificationVows != null && VerificationVows.Count > 0) ? VerificationVows : MatchData.Instance.OpponentVows;

    /// <summary>맵 내용·도구·모드가 바뀔 때 (UI 갱신용)</summary>
    public event Action Changed;
    public event Action<string> StatusChanged;
    /// <summary>완료 확정 — (맵, 전송용 페이로드). NetService.SendMap 이 구독할 지점.</summary>
    public event Action<MapData, byte[]> Completed;
    /// <summary>검증 모드 진입/종료</summary>
    public event Action<bool> VerificationChanged;

    readonly Stack<MapData> _undo = new Stack<MapData>();
    readonly Stack<MapData> _redo = new Stack<MapData>();
    readonly List<GameObject> _strokeObjects = new List<GameObject>();
    readonly List<StrokeData> _strokeObjectSources = new List<StrokeData>();
    Transform _strokesRoot;
    CanvasView _view;
    MapEditorUI _ui;      // 플레이스홀더 (HUD 프리팹이 없을 때)
    MapEditorHud _hud;    // 씬의 HUD 프리팹 (Assets/Prefabs/UI/MapEditorHud.prefab)
    LineRenderer _preview;
    Transform _eraserCursor;
    List<Vector2> _current;
    bool _drawing;
    bool _erasing;
    bool _eraseChangedAny;
    bool _pressedLastFrame;
    float _lastAspect;
    PlaySession _session;
    Coroutine _returnCoroutine;

    // ------------------------------------------------------------------ lifecycle

    void Awake()
    {
        Application.runInBackground = true;   // 창 포커스를 잃어도 플레이(검증·타이머)가 멈추지 않게 — 멀티 대기 중에도 필요
        if (palette == null) palette = StrokePalette.LoadOrDefault();
        if (targetCamera == null) targetCamera = Camera.main;
        if (targetCamera == null)
        {
            var camGo = new GameObject("Main Camera (runtime)");
            camGo.tag = "MainCamera";
            targetCamera = camGo.AddComponent<Camera>();
            if (gameObject.scene.IsValid() && camGo.scene != gameObject.scene)
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(camGo, gameObject.scene);
        }
        _hud = FindHudInScene();
        var theme = _hud != null ? _hud.Theme : null;
        targetCamera.backgroundColor = theme != null ? theme.Background : new Color(0.16f, 0.17f, 0.2f);
        targetCamera.clearFlags = CameraClearFlags.SolidColor;
        FitCamera();

        _view = gameObject.AddComponent<CanvasView>();
        _view.Build(theme);

        _strokesRoot = new GameObject("Strokes").transform;
        _strokesRoot.SetParent(transform, false);

        var pgo = new GameObject("Preview Stroke");
        pgo.transform.SetParent(transform, false);
        _preview = StrokeVisual.Build(pgo, new List<Vector2>(), PenWidth, palette.GetColor(ColorId), false, sortingOrder: 1);
        _preview.positionCount = 0;

        var cursor = RuntimeSprites.MakeSquare("Eraser Cursor", transform, Vector2.zero, Vector2.one, new Color(1f, 0.4f, 0.4f, 0.35f), 10);
        _eraserCursor = cursor.transform;
        _eraserCursor.gameObject.SetActive(false);

        EnsureEventSystem();
        if (_hud != null) _hud.Bind(this);
        else if (buildRuntimeUI) { _ui = gameObject.AddComponent<MapEditorUI>(); _ui.Bind(this); }

        SetStatus("펜으로 선을 그리세요. 선이 곧 벽입니다. 골을 배치하고 검증 플레이로 클리어하면 완료할 수 있습니다.");
    }

    void Update()
    {
        if (_hud == null && !Mathf.Approximately(_lastAspect, targetCamera.aspect)) FitCamera();   // HUD 가 있으면 HUD 가 슬롯에 맞춰 호출
        if (InVerification || Locked) return;   // 플레이 중에는 세션이 입력을 가진다 (ESC 로 복귀) / 제출 후에는 편집 불가
        HandleShortcuts();
        HandlePointer();
    }

    /// <summary>제출 후 편집 잠금. UI 를 숨기고 입력을 막는다 (해제 시 복원).</summary>
    public void SetLocked(bool locked)
    {
        if (Locked == locked) return;
        if (locked) { if (_drawing) EndStroke(); if (_erasing) EndErase(); }
        Locked = locked;
        _eraserCursor.gameObject.SetActive(false);
        _preview.positionCount = 0;
        SetUiVisible(!locked && !InVerification);
        Changed?.Invoke();
    }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
    /// <summary>자동 테스트 전용: 검증 플레이 없이 검증 성공 상태로 만든다. 릴리즈 빌드에는 포함되지 않는다.</summary>
    public void DebugForceVerified(float parTime)
    {
        if (!Map.HasGoal || Map.Strokes.Count == 0) return;
        IsVerified = true;
        VerifiedParTime = parTime;
        MatchData.Instance.MyParTime = parTime;
        SetStatus($"[디버그] 검증 강제 성공 — 패타임 {parTime:0.00}s");
        Changed?.Invoke();
    }
#endif

    void FitCamera()
    {
        if (_hud != null) return;   // MapEditorHud.FitCameraToSlot 이 FitCamera(Rect) 로 맞춘다
        CanvasView.FitCamera(targetCamera, MapEditorUI.TopBarFraction, MapEditorUI.BottomBarFraction);
        _lastAspect = targetCamera.aspect;
    }

    /// <summary>HUD 가 종이 슬롯(뷰포트 비율)을 넘겨 카메라를 맞춘다.</summary>
    public void FitCamera(Rect viewport, float margin)
    {
        CanvasView.FitCamera(targetCamera, viewport, margin);
        _lastAspect = targetCamera.aspect;
    }

    void SetUiVisible(bool visible)
    {
        if (_ui != null) _ui.SetVisible(visible);
        if (_hud != null) _hud.SetVisible(visible);
    }

    /// <summary>같은 씬에 놓인 HUD 프리팹 인스턴스 (비활성 포함). 없으면 null → 플레이스홀더 UI.</summary>
    MapEditorHud FindHudInScene()
    {
        foreach (var h in FindObjectsByType<MapEditorHud>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (h.gameObject.scene == gameObject.scene) return h;
        return null;
    }

    // ------------------------------------------------------------------ input

    void HandleShortcuts()
    {
        var kb = Keyboard.current;
        if (kb == null) return;
        bool ctrl = kb.ctrlKey.isPressed || kb.leftCommandKey.isPressed || kb.rightCommandKey.isPressed;
        if (!ctrl) return;
        if (kb.zKey.wasPressedThisFrame)
        {
            if (kb.shiftKey.isPressed) Redo(); else Undo();
        }
        else if (kb.yKey.wasPressedThisFrame) Redo();
    }

    void HandlePointer()
    {
        var pointer = Pointer.current;
        if (pointer == null) return;

        bool pressed = pointer.press.isPressed;
        bool pressedNow = pressed && !_pressedLastFrame;
        bool releasedNow = !pressed && _pressedLastFrame;
        _pressedLastFrame = pressed;

        Vector2 screen = pointer.position.ReadValue();
        Vector3 w3 = targetCamera.ScreenToWorldPoint(new Vector3(screen.x, screen.y, 10f));
        var world = new Vector2(w3.x, w3.y);
        bool overUI = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

        bool showCursor = Tool == EditorTool.Eraser && !overUI;
        if (_eraserCursor.gameObject.activeSelf != showCursor) _eraserCursor.gameObject.SetActive(showCursor);
        if (showCursor)
        {
            _eraserCursor.position = new Vector3(world.x, world.y, 0f);
            _eraserCursor.localScale = Vector3.one * EraserRadius * 2f;
        }

        switch (Tool)
        {
            case EditorTool.Pen:
                if (pressedNow && !overUI && StrokeGeometry.InCanvas(world)) BeginStroke(world);
                else if (pressed && _drawing) AddPoint(world);
                if (releasedNow && _drawing) EndStroke();
                break;

            case EditorTool.Eraser:
                if (pressedNow && !overUI) BeginErase();
                if (pressed && _erasing) EraseAt(world);
                if (releasedNow && _erasing) EndErase();
                break;

            case EditorTool.Goal:
                if (pressedNow && !overUI && StrokeGeometry.InCanvas(world)) SetGoal(world);
                break;
        }
    }

    // ------------------------------------------------------------------ tools (public API)

    public void SetTool(EditorTool tool)
    {
        if (_drawing) EndStroke();
        if (_erasing) EndErase();
        Tool = tool;
        SetStatus(tool switch
        {
            EditorTool.Pen => "펜: 드래그해서 벽을 그립니다.",
            EditorTool.Eraser => "지우개: 드래그한 부분의 선이 잘려 나갑니다.",
            EditorTool.Goal => "골 배치: 캔버스를 클릭한 위치에 골이 놓입니다. 다시 클릭하면 이동합니다.",
            _ => ""
        });
        Changed?.Invoke();
    }

    public void SetWidthIndex(int index)
    {
        WidthIndex = Mathf.Clamp(index, 0, MapConstants.PenWidths.Length - 1);
        _preview.startWidth = _preview.endWidth = PenWidth;
        Changed?.Invoke();
    }

    public void SetColorId(int colorId)
    {
        ColorId = colorId;
        var c = palette.GetColor(colorId);
        _preview.startColor = _preview.endColor = c;
        if (Tool == EditorTool.Eraser) SetTool(EditorTool.Pen);
        Changed?.Invoke();
    }

    // ---- pen

    public bool BeginStroke(Vector2 world)
    {
        if (InVerification) return false;
        if (_drawing) EndStroke();
        if (Map.Strokes.Count >= MapConstants.MaxStrokes)
        {
            SetStatus($"스트로크 상한({MapConstants.MaxStrokes}개)에 도달했습니다. 지우개나 실행취소로 정리하세요.");
            return false;
        }
        _current = new List<Vector2> { StrokeGeometry.ClampToCanvas(world) };
        _drawing = true;
        _preview.startWidth = _preview.endWidth = PenWidth;
        _preview.startColor = _preview.endColor = palette.GetColor(ColorId);
        StrokeVisual.SetPoints(_preview, _current);
        return true;
    }

    public void AddPoint(Vector2 world)
    {
        if (!_drawing) return;
        var p = StrokeGeometry.ClampToCanvas(world);
        if ((p - _current[_current.Count - 1]).sqrMagnitude < MapConstants.MinPointDistance * MapConstants.MinPointDistance) return;
        if (_current.Count >= MapConstants.MaxPointsPerStroke * 3) return;   // 수집 단계 상한 (확정 시 단순화됨)
        _current.Add(p);
        StrokeVisual.SetPoints(_preview, _current);
    }

    public StrokeData EndStroke()
    {
        if (!_drawing) return null;
        _drawing = false;
        _preview.positionCount = 0;

        if (_current == null || _current.Count < 2) { _current = null; return null; }

        var stroke = new StrokeData { Points = _current, Width = PenWidth, ColorId = ColorId };
        _current = null;
        StrokeGeometry.Finalize(stroke);
        if (stroke.Points.Count < 2) return null;

        PushUndo();
        Map.Strokes.Add(stroke);
        SyncStrokeVisuals();
        SetStatus($"스트로크 {Map.Strokes.Count}/{MapConstants.MaxStrokes}");
        Changed?.Invoke();
        return stroke;
    }

    /// <summary>테스트·자동화용: 점 목록을 한 번에 스트로크로 추가.</summary>
    public StrokeData AddStroke(IList<Vector2> worldPoints)
    {
        if (worldPoints == null || worldPoints.Count == 0) return null;
        if (!BeginStroke(worldPoints[0])) return null;
        for (int i = 1; i < worldPoints.Count; i++) AddPoint(worldPoints[i]);
        return EndStroke();
    }

    // ---- eraser

    public void BeginErase()
    {
        if (_erasing || InVerification) return;
        _erasing = true;
        _eraseChangedAny = false;
        PushUndo();   // 드래그 1회 = 실행취소 1단계. 아무것도 안 지웠으면 EndErase 에서 되돌린다
    }

    public bool EraseAt(Vector2 world)
    {
        if (!_erasing) return false;
        bool changed = StrokeGeometry.EraseCircle(Map.Strokes, world, EraserRadius);
        if (changed)
        {
            _eraseChangedAny = true;
            SyncStrokeVisuals();   // 바뀐 스트로크만 제자리 갱신 → 깜빡임 없음
            Changed?.Invoke();
        }
        return changed;
    }

    public void EndErase()
    {
        if (!_erasing) return;
        _erasing = false;
        if (!_eraseChangedAny && _undo.Count > 0) _undo.Pop();
        if (_eraseChangedAny) SetStatus($"지움 — 스트로크 {Map.Strokes.Count}/{MapConstants.MaxStrokes}");
        Changed?.Invoke();
    }

    /// <summary>한 번에 원 하나를 지우는 편의 함수 (되돌리기 1단계).</summary>
    public bool EraseOnce(Vector2 world)
    {
        BeginErase();
        bool r = EraseAt(world);
        EndErase();
        return r;
    }

    // ---- goal

    public void SetGoal(Vector2 world)
    {
        if (InVerification) return;
        var p = StrokeGeometry.Quantize(StrokeGeometry.ClampToCanvas(world));
        float dist = Vector2.Distance(p, Map.StartPos);
        if (dist < MapConstants.MinGoalDistanceFromStart)
        {
            SetStatus($"골은 시작점에서 {MapConstants.MinGoalDistanceFromStart:0}u 이상 떨어져야 합니다 (현재 {dist:0.0}u).");
            return;
        }
        PushUndo();
        Map.GoalPos = p;
        _view.SetGoal(Map);
        SetStatus($"골 배치: ({p.x:0.00}, {p.y:0.00})");
        Changed?.Invoke();
    }

    // ---- undo / redo / clear

    public bool Undo()
    {
        if (InVerification) return false;
        if (_drawing) { _drawing = false; _preview.positionCount = 0; _current = null; }
        if (_erasing) { _erasing = false; }
        if (_undo.Count == 0) return false;
        _redo.Push(Map.Clone());
        Map = _undo.Pop();
        InvalidateVerification();
        SyncStrokeVisuals();
        _view.SetGoal(Map);
        SetStatus($"실행취소 — 스트로크 {Map.Strokes.Count}/{MapConstants.MaxStrokes}");
        Changed?.Invoke();
        return true;
    }

    public bool Redo()
    {
        if (InVerification || _redo.Count == 0) return false;
        _undo.Push(Map.Clone());
        Map = _redo.Pop();
        InvalidateVerification();
        SyncStrokeVisuals();
        _view.SetGoal(Map);
        SetStatus($"다시실행 — 스트로크 {Map.Strokes.Count}/{MapConstants.MaxStrokes}");
        Changed?.Invoke();
        return true;
    }

    public void ClearAll()
    {
        if (InVerification) return;
        if (Map.Strokes.Count == 0 && !Map.HasGoal) return;
        PushUndo();
        Map = new MapData();
        SyncStrokeVisuals();
        _view.SetGoal(Map);
        SetStatus("전체 지우기 (Ctrl+Z 로 되돌릴 수 있습니다)");
        Changed?.Invoke();
    }

    // ---- verification play (Docs/100 6장, Docs/203 6장)

    /// <summary>검증 플레이 시작: 현재 맵을 콜라이더로 로드하고 플레이어를 스폰. 골 도달 시 검증 성공.</summary>
    public bool StartVerification()
    {
        if (InVerification) return false;
        if (_drawing) EndStroke();
        if (_erasing) EndErase();
        if (!Map.HasGoal) { SetStatus("검증 불가: 골을 먼저 배치하세요."); return false; }
        if (Map.Strokes.Count == 0) { SetStatus("검증 불가: 선을 하나 이상 그리세요."); return false; }

        _strokesRoot.gameObject.SetActive(false);   // 세션의 MapLoader 가 같은 모양을 콜라이더 포함으로 다시 그린다
        _view.SetGoalMarkerVisible(false);
        _eraserCursor.gameObject.SetActive(false);
        _preview.positionCount = 0;
        SetUiVisible(false);

        var vows = EffectiveVerificationVows;   // 검증은 상대의 뜻으로 (Docs/100 6장)
        if (GameFlow.Instance != null && (vows == null || vows.Count == 0))
            Debug.LogWarning("[MapEditor] 검증에 적용할 상대 뜻이 없습니다 — 뜻 교환이 끝나지 않았거나 상대가 뜻을 고르지 않은 상태");
        Debug.Log("[MapEditor] 검증 플레이 시작 — 적용 뜻: " + VowCatalog.NamesOf(vows));
        _session = PlaySession.Begin(Map.Clone(), vows != null && vows.Count > 0 ? "검증 플레이 — 상대 뜻: " + VowCatalog.NamesOf(vows) : "검증 플레이", transform, vows);
        _session.Completed += OnVerificationCompleted;
        _session.Aborted += OnVerificationAborted;

        SetStatus("검증 플레이: 시작점에서 골까지 도달하면 검증 성공. R 리스폰, ESC 에디터 복귀.");
        VerificationChanged?.Invoke(true);
        Changed?.Invoke();
        return true;
    }

    /// <summary>검증 플레이 종료 → 에디터 복귀.</summary>
    public void StopVerification()
    {
        if (_session == null) return;
        var s = _session;
        _session = null;
        s.Completed -= OnVerificationCompleted;
        s.Aborted -= OnVerificationAborted;
        s.End();
        if (_returnCoroutine != null) { StopCoroutine(_returnCoroutine); _returnCoroutine = null; }

        _strokesRoot.gameObject.SetActive(true);
        _view.SetGoalMarkerVisible(true);
        _view.SetGoal(Map);
        SetUiVisible(true);
        _pressedLastFrame = true;   // 복귀 클릭이 곧바로 펜 입력으로 새지 않게

        if (IsVerified) SetStatus($"검증 성공 — 클리어 {VerifiedParTime:0.00}초 (패타임). [완료]로 확정할 수 있습니다. 맵을 수정하면 재검증이 필요합니다.");
        else SetStatus("에디터로 돌아왔습니다. 맵을 수정한 뒤 다시 검증하세요.");
        VerificationChanged?.Invoke(false);
        Changed?.Invoke();
    }

    void OnVerificationCompleted(PlayResult r)
    {
        if (!r.Cleared) return;
        IsVerified = true;
        VerifiedParTime = r.ClearTime;
        MatchData.Instance.MyParTime = r.ClearTime;
        Debug.Log($"[MapEditor] 검증 성공 — {r.ClearTime:0.00}s, 시도 {r.Attempts}");
        _returnCoroutine = StartCoroutine(ReturnAfter(2.5f));
    }

    void OnVerificationAborted() => StopVerification();

    IEnumerator ReturnAfter(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        _returnCoroutine = null;
        StopVerification();
    }

    void InvalidateVerification()
    {
        IsVerified = false;
        VerifiedParTime = 0f;
    }

    // ---- complete

    /// <summary>
    /// 맵 확정. 검증 성공(골 도달) 필수. 직렬화 → 역직렬화 왕복이 원본과 같아야 성공.
    /// 성공 시 MatchData.MyMap/MyParTime 에 반영하고 Completed(map, payload) 를 발생시킨다. 실패 시 null.
    /// </summary>
    public byte[] Complete()
    {
        if (InVerification) { SetStatus("완료 불가: 검증 플레이 중입니다."); return null; }
        if (_drawing) EndStroke();
        if (_erasing) EndErase();

        if (!Map.HasGoal) { SetStatus("완료 불가: 골을 먼저 배치하세요."); return null; }
        if (Map.Strokes.Count == 0) { SetStatus("완료 불가: 선을 하나 이상 그리세요."); return null; }
        if (!IsVerified) { SetStatus("완료 불가: [검증 플레이]로 시작점→골 클리어를 먼저 증명하세요."); return null; }

        byte[] payload;
        MapData roundTrip;
        try
        {
            payload = MapSerializer.Serialize(Map);
            roundTrip = MapSerializer.Deserialize(payload);
        }
        catch (Exception e)
        {
            SetStatus("완료 불가: 직렬화 오류 — " + e.Message);
            Debug.LogException(e);
            return null;
        }

        if (!MapData.ApproximatelyEqual(Map, roundTrip))
        {
            SetStatus("완료 불가: 직렬화 왕복 검증 실패 (전송본이 원본과 다름). Console 확인.");
            Debug.LogError("[MapEditor] round-trip mismatch\n" + MapSerializer.ToJson(Map) + "\n---\n" + MapSerializer.ToJson(roundTrip));
            return null;
        }

        int chunks = MapChunker.ChunkCount(payload.Length);
        string sizeWarn = payload.Length > MapConstants.TargetPayloadBytes ? " ⚠ 목표 100KB 초과" : "";
        LastPayload = payload;
        MatchData.Instance.MyMap = Map.Clone();
        MatchData.Instance.MyParTime = VerifiedParTime;
        SetStatus($"완료 — 스트로크 {Map.Strokes.Count}, 점 {Map.TotalPoints}, 패타임 {VerifiedParTime:0.00}s, 전송 {payload.Length / 1024f:0.0} KB (청크 {chunks}개), 왕복 검증 OK{sizeWarn}");
        Debug.Log("[MapEditor] " + Status);
        Completed?.Invoke(Map.Clone(), payload);
        Changed?.Invoke();
        return payload;
    }

    /// <summary>수신한 맵을 에디터에 불러오기 (디버그·검증용).</summary>
    public void LoadMap(MapData map)
    {
        if (map == null || InVerification) return;
        PushUndo();
        Map = map.Clone();
        SyncStrokeVisuals();
        _view.SetGoal(Map);
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ internals

    /// <summary>맵을 바꾸는 모든 조작 직전에 호출: 스냅샷 저장, 다시실행 스택 비움, 검증 무효화.</summary>
    void PushUndo()
    {
        _undo.Push(Map.Clone());
        _redo.Clear();
        InvalidateVerification();
        if (_undo.Count > MapConstants.MaxUndo)
        {
            var arr = _undo.ToArray();           // top → bottom
            _undo.Clear();
            for (int i = MapConstants.MaxUndo - 1; i >= 0; i--) _undo.Push(arr[i]);
        }
    }

    /// <summary>
    /// 스트로크 시각 오브젝트를 Map.Strokes 에 맞춘다. 같은 StrokeData 참조는 건드리지 않고, 바뀐 것만 제자리에서 갱신,
    /// 남는 오브젝트만 제거 — 지우개 드래그 중 파괴/재생성으로 생기던 깜빡임 제거.
    /// </summary>
    void SyncStrokeVisuals()
    {
        var strokes = Map.Strokes;
        for (int i = 0; i < strokes.Count; i++)
        {
            var s = strokes[i];
            if (i < _strokeObjects.Count)
            {
                if (ReferenceEquals(_strokeObjectSources[i], s)) continue;
                StrokeVisual.Build(_strokeObjects[i], s.Points, s.Width, palette.GetColor(s.ColorId), withCollider: false, sortingOrder: 0);
                _strokeObjects[i].name = $"Stroke {i} (c{s.ColorId})";
                _strokeObjectSources[i] = s;
            }
            else
            {
                var go = new GameObject($"Stroke {i} (c{s.ColorId})");
                go.transform.SetParent(_strokesRoot, false);
                StrokeVisual.Build(go, s.Points, s.Width, palette.GetColor(s.ColorId), withCollider: false, sortingOrder: 0);
                _strokeObjects.Add(go);
                _strokeObjectSources.Add(s);
            }
        }
        for (int i = _strokeObjects.Count - 1; i >= strokes.Count; i--)
        {
            Destroy(_strokeObjects[i]);
            _strokeObjects.RemoveAt(i);
            _strokeObjectSources.RemoveAt(i);
        }
    }

    void SetStatus(string msg)
    {
        Status = msg;
        StatusChanged?.Invoke(msg);
    }

    static void EnsureEventSystem()
    {
        if (EventSystem.current != null || FindFirstObjectByType<EventSystem>() != null) return;
        var go = new GameObject("EventSystem (runtime)");
        go.AddComponent<EventSystem>();
        go.AddComponent<InputSystemUIInputModule>();
    }
}
