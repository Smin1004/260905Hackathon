using UnityEngine;

/// <summary>
/// 에디터 캔버스의 정적 시각 요소: 배경, 격자, 기본 경계(바닥·왼쪽 벽), 시작점 마커, 골 마커.
/// 전부 런타임 생성 (플레이스홀더). 아트 교체 시 프리팹으로 대체 — Docs/102 1.2.
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
    MapLoader _boundaries;

    public void Build()
    {
        if (_root != null) Destroy(_root.gameObject);
        _root = new GameObject("CanvasView").transform;
        _root.SetParent(transform, false);

        float w = MapConstants.CanvasWidth, h = MapConstants.CanvasHeight;

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

        // 시작점 마커 (고정)
        RuntimeSprites.MakeSquare("Start Marker", _root, MapConstants.StartPos, new Vector2(0.8f, 0.9f), StartColor, 4);

        // 골 마커 (배치 전 숨김)
        var goal = RuntimeSprites.MakeSquare("Goal Marker", _root, Vector2.zero, MapConstants.GoalSize, GoalColor, 4);
        _goalMarker = goal.transform;
        _goalMarker.gameObject.SetActive(false);
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
        float w = MapConstants.CanvasWidth, h = MapConstants.CanvasHeight;
        cam.orthographic = true;
        float aspect = Mathf.Max(0.1f, cam.aspect);
        float free = Mathf.Clamp(1f - topBar - bottomBar, 0.2f, 1f);      // 캔버스가 쓸 수 있는 세로 비율
        float freeCenter = bottomBar + free * 0.5f;                        // 그 영역의 중심 (뷰포트 0~1)

        float halfH = Mathf.Max((h * margin) / (2f * free), (w * margin) / (2f * aspect));
        cam.orthographicSize = halfH;

        // 캔버스 중심(h/2)이 뷰포트 freeCenter 에 오도록 카메라 y 를 이동
        float camY = h / 2f - (freeCenter - 0.5f) * 2f * halfH;
        cam.transform.position = new Vector3(w / 2f, camY, -10f);
        cam.transform.rotation = Quaternion.identity;
    }
}
