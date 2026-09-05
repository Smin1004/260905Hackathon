using UnityEngine;

/// <summary>
/// 에디터 캔버스의 정적 시각 요소: 배경, 격자, 기본 경계(바닥·왼쪽 벽), 시작점 마커, 골 마커.
/// 전부 런타임 생성. MapEditorTheme 이 주어지면 테마 스프라이트(도트 종이·마커)로, 없으면 단색 플레이스홀더로 그린다 — Docs/102 1.2.
/// </summary>
public class CanvasView : MonoBehaviour
{
    public Color BackgroundColor = new Color(0.96f, 0.96f, 0.94f);
    public Color GridColor = new Color(0.85f, 0.85f, 0.83f);
    public Color BorderColor = new Color(0.6f, 0.6f, 0.6f);
    public Color StartColor = new Color(0.2f, 0.75f, 0.35f, 0.9f);
    public Color GoalColor = new Color(1f, 0.85f, 0.2f, 0.75f);

    Transform _root;
    Transform _goalMarker;
    Transform _startMarker;
    MapLoader _boundaries;
    MapEditorTheme _theme;

    /// <summary>theme 이 있으면 도트 타일 종이·마커 스프라이트로 그리고(HUD 프리팹과 한 벌), 없으면 단색 플레이스홀더.</summary>
    public void Build(MapEditorTheme theme = null)
    {
        _theme = theme;
        if (_root != null) Destroy(_root.gameObject);
        _root = new GameObject("CanvasView").transform;
        _root.SetParent(transform, false);

        float w = MapConstants.CanvasWidth, h = MapConstants.CanvasHeight;

        if (theme != null) { BuildThemed(theme, w, h); return; }

        RuntimeSprites.MakeSquare("Background", _root, new Vector2(w / 2f, h / 2f), new Vector2(w, h), BackgroundColor, -20);

        // 격자 1u
        var grid = new GameObject("Grid").transform;
        grid.SetParent(_root, false);
        for (int x = 1; x < w; x++) Line(grid, new Vector2(x, 0), new Vector2(x, h), 0.02f, GridColor, -19);
        for (int y = 1; y < h; y++) Line(grid, new Vector2(0, y), new Vector2(w, y), 0.02f, GridColor, -19);

        // 테두리 (천장·오른쪽은 열려 있음을 점선 대신 얇은 회색으로만 표시)
        Line(_root, new Vector2(0, h), new Vector2(w, h), 0.04f, BorderColor, -18);
        Line(_root, new Vector2(w, 0), new Vector2(w, h), 0.04f, BorderColor, -18);

        // 바닥·왼쪽 벽은 Play 씬과 같은 로더로 그려 보이는 것이 일치하게 한다 (콜라이더 없음)
        var bgo = new GameObject("Boundaries");
        bgo.transform.SetParent(_root, false);
        _boundaries = bgo.AddComponent<MapLoader>();
        _boundaries.BuildColliders = false;
        _boundaries.BuildGoal = false;
        _boundaries.Load(new MapData());

        // 시작 마커 (배치 전 숨김)
        _startMarker = RuntimeSprites.MakeSquare("Start Marker", _root, MapConstants.StartPos, new Vector2(0.8f, 0.9f), StartColor, 4).transform;
        _startMarker.gameObject.SetActive(false);

        // 골 마커 (배치 전 숨김)
        var goal = RuntimeSprites.MakeSquare("Goal Marker", _root, Vector2.zero, MapConstants.GoalSize, GoalColor, 4);
        _goalMarker = goal.transform;
        _goalMarker.gameObject.SetActive(false);
    }

    /// <summary>
    /// 교환 플레이(Play 씬)용 배경: 종이 + 도트 타일 + 시작 마커만 (경계·골은 세션의 MapLoader 가 그린다).
    /// 상대 맵을 플레이할 때 배경이 검어 선이 안 보이던 문제 — 에디터·검증과 같은 종이 위에서 플레이한다.
    /// theme 이 없으면 단색 종이만 깐다.
    /// </summary>
    public static Transform BuildPlayBackdrop(Transform parent, MapEditorTheme theme, Vector2 startPos)
    {
        var root = new GameObject("Play Backdrop").transform;
        root.SetParent(parent, false);
        float w = MapConstants.CanvasWidth, h = MapConstants.CanvasHeight;
        if (theme == null)
        {
            RuntimeSprites.MakeSquare("Paper", root, new Vector2(w / 2f, h / 2f), new Vector2(w, h), new Color(0.96f, 0.96f, 0.94f), -20);
            return root;
        }
        BuildPaper(root, theme, w, h);
        BuildStartMarker(root, theme).position = new Vector3(startPos.x, startPos.y, 0f);
        return root;
    }

    /// <summary>종이 (단색) + 도트 타일 (Tiled 스프라이트 — PPU 가 도트 간격을 정한다)</summary>
    static void BuildPaper(Transform root, MapEditorTheme t, float w, float h)
    {
        RuntimeSprites.MakeSquare("Paper", root, new Vector2(w / 2f, h / 2f), new Vector2(w, h), t.Paper, -20);
        if (t.PaperDotsTile != null)
        {
            var dots = new GameObject("Paper Dots");
            dots.transform.SetParent(root, false);
            dots.transform.position = new Vector3(w / 2f, h / 2f, 0f);
            var sr = dots.AddComponent<SpriteRenderer>();
            sr.sprite = t.PaperDotsTile;
            sr.drawMode = SpriteDrawMode.Tiled;
            sr.tileMode = SpriteTileMode.Continuous;
            sr.size = new Vector2(w, h);
            sr.color = t.PaperDots;
            sr.sortingOrder = -19;
        }
    }

    /// <summary>테마 버전: 종이색 + 도트 타일 배경, 바닥·왼쪽 벽, 스프라이트 시작/골 마커(펄스 포함), START 라벨.</summary>
    void BuildThemed(MapEditorTheme t, float w, float h)
    {
        BuildPaper(_root, t, w, h);

        // 사방 경계 (Play 의 MapLoader 와 같은 굵기·색, 단 종이 밖으로 나가지 않게 캔버스 범위로 자른다 — 콜라이더 없음)
        float cap = t.BoundaryWidth * 0.5f;   // 라운드 캡이 종이 밖으로 나가지 않게 끝점을 캡 반지름만큼 안쪽으로
        Line(_root, new Vector2(cap, 0f), new Vector2(w - cap, 0f), t.BoundaryWidth, t.BoundaryColor, -5);
        Line(_root, new Vector2(0f, cap), new Vector2(0f, h - cap), t.BoundaryWidth, t.BoundaryColor, -5);
        Line(_root, new Vector2(w, cap), new Vector2(w, h - cap), t.BoundaryWidth, t.BoundaryColor, -5);
        Line(_root, new Vector2(cap, h), new Vector2(w - cap, h), t.BoundaryWidth, t.BoundaryColor, -5);

        _startMarker = BuildStartMarker(_root, t);
        _startMarker.gameObject.SetActive(false);   // 배치 전 숨김

        // 골: 펄스 + 마커 (배치 전 숨김)
        var goalRoot = new GameObject("Goal Marker").transform;
        goalRoot.SetParent(_root, false);
        Marker(goalRoot, t.GoalPulse, t.PulseSize, 3);
        Marker(goalRoot, t.GoalMarker, t.MarkerSize, 4);
        _goalMarker = goalRoot;
        _goalMarker.gameObject.SetActive(false);
    }

    /// <summary>시작점: 펄스 + 마커 + START 라벨</summary>
    static Transform BuildStartMarker(Transform root, MapEditorTheme t)
    {
        var startRoot = new GameObject("Start Marker").transform;
        startRoot.SetParent(root, false);
        startRoot.position = new Vector3(MapConstants.StartPos.x, MapConstants.StartPos.y, 0f);
        Marker(startRoot, t.StartPulse, t.PulseSize, 3);
        Marker(startRoot, t.StartMarker, t.MarkerSize, 4);
        var label = new GameObject("Label");
        label.transform.SetParent(startRoot, false);
        label.transform.localPosition = new Vector3(t.MarkerSize * 0.5f + 0.45f, 0f, 0f);
        var tm = label.AddComponent<TextMesh>();
        tm.text = "START";
        tm.font = RuntimeUI.Font;
        tm.fontSize = 48;
        tm.fontStyle = FontStyle.Bold;
        tm.characterSize = 0.1f;
        tm.anchor = TextAnchor.MiddleLeft;
        tm.color = t.Mint;
        var tmr = label.GetComponent<MeshRenderer>();
        tmr.sortingOrder = 4;
        tmr.sharedMaterial = RuntimeUI.Font.material;
        return startRoot;
    }

    /// <summary>시작 마커를 맵의 시작 위치로 (미배치면 숨김)</summary>
    public void SetStart(MapData map)
    {
        if (_startMarker == null) return;
        _startMarker.gameObject.SetActive(map.HasStart);
        if (map.HasStart) _startMarker.position = new Vector3(map.StartPos.x, map.StartPos.y, 0f);
    }

    public static void Marker(Transform parent, Sprite sprite, float diameter, int order)
    {
        if (sprite == null) return;
        var go = new GameObject(sprite.name);
        go.transform.SetParent(parent, false);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = sprite;
        sr.sortingOrder = order;
        float px = Mathf.Max(sprite.rect.width, sprite.rect.height) / sprite.pixelsPerUnit;   // 스프라이트의 월드 크기
        go.transform.localScale = Vector3.one * (diameter / Mathf.Max(0.001f, px));
    }

    bool _goalMarkerVisible = true;

    public void SetGoal(MapData map)
    {
        if (_goalMarker == null) return;
        _goalMarker.gameObject.SetActive(map.HasGoal && _goalMarkerVisible);
        if (map.HasGoal) _goalMarker.position = new Vector3(map.GoalPos.x, map.GoalPos.y, 0f);
    }

    /// <summary>검증 플레이 중에는 세션의 골 존이 대신 표시되므로 에디터 마커를 숨긴다.</summary>
    public void SetGoalMarkerVisible(bool visible)
    {
        _goalMarkerVisible = visible;
        if (_goalMarker != null && !visible) _goalMarker.gameObject.SetActive(false);
    }

    static void Line(Transform parent, Vector2 a, Vector2 b, float width, Color color, int order)
    {
        var go = new GameObject("Line");
        go.transform.SetParent(parent, false);
        StrokeVisual.Build(go, new[] { a, b }, width, color, false, order);
    }

    /// <summary>
    /// 카메라가 캔버스 전체를 여백 포함해 보도록 맞춘다 (해상도 무관).
    /// 화면 위·아래의 UI 바 비율(topBar, bottomBar)을 빼고 남은 영역의 가운데에 캔버스가 오도록 시야와 위치를 계산한다.
    /// </summary>
    public static void FitCamera(Camera cam, float topBar = 0f, float bottomBar = 0f, float margin = 1.08f)
    {
        float free = Mathf.Clamp(1f - topBar - bottomBar, 0.2f, 1f);
        FitCamera(cam, new Rect(0f, bottomBar, 1f, free), margin);
    }

    /// <summary>
    /// 캔버스(0,0)~(W,H)가 뷰포트의 주어진 사각형(0~1 비율) 안에 여백 포함으로 들어가도록 카메라 시야·위치를 맞춘다.
    /// HUD 프리팹이 종이 슬롯 영역을 넘겨 호출한다 (MapEditorHud.FitCameraToSlot).
    /// </summary>
    public static void FitCamera(Camera cam, Rect viewport, float margin = 1.08f)
    {
        float w = MapConstants.CanvasWidth, h = MapConstants.CanvasHeight;
        cam.orthographic = true;
        float aspect = Mathf.Max(0.1f, cam.aspect);
        float freeH = Mathf.Clamp(viewport.height, 0.05f, 1f);
        float freeW = Mathf.Clamp(viewport.width, 0.05f, 1f);

        float halfH = Mathf.Max((h * margin) / (2f * freeH), (w * margin) / (2f * aspect * freeW));
        cam.orthographicSize = halfH;

        // 캔버스 중심이 뷰포트 사각형의 중심에 오도록 카메라를 이동
        float camY = h / 2f - (viewport.center.y - 0.5f) * 2f * halfH;
        float camX = w / 2f - (viewport.center.x - 0.5f) * 2f * halfH * aspect;
        cam.transform.position = new Vector3(camX, camY, -10f);
        cam.transform.rotation = Quaternion.identity;
    }
}
