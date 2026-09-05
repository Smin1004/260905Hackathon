using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>Play 씬이 의존하는 유일한 인터페이스 (Docs/202 4장, Docs/203 4장).</summary>
public interface ILoadableMap
{
    void Load(MapData map);
    void Unload();
    MapData Current { get; }
    GoalZone Goal { get; }
}

/// <summary>골 존 트리거. 플레이어 태그 오브젝트가 닿으면 Reached 발생.</summary>
public class GoalZone : MonoBehaviour
{
    public string RequiredTag = "Player";
    public event Action<Collider2D> Reached;

    void OnTriggerEnter2D(Collider2D other)
    {
        if (string.IsNullOrEmpty(RequiredTag) || other.CompareTag(RequiredTag)) Reached?.Invoke(other);
    }
}

/// <summary>
/// MapData → 콜라이더 + 렌더러 (에디터 담당이 Play 씬에 납품하는 맵 로더 컴포넌트).
/// 스트로크당 오브젝트 1개(EdgeCollider2D + LineRenderer), 기본 경계(바닥·왼쪽 벽), 골 존을 런타임 생성한다. 씬 파일 오염 없음.
/// </summary>
public class MapLoader : MonoBehaviour, ILoadableMap
{
    [SerializeField] StrokePalette palette;
    [Tooltip("바닥(y=0)·왼쪽 벽(x=0)·오른쪽 벽(x=Width)·천장(y=Height) 콜라이더 생성 — Docs/100 5장 기본 경계 (사방 막힘)")]
    public bool BuildBoundaries = true;
    public bool BuildGoal = true;
    [Tooltip("false면 시각만 생성 (에디터 미리보기용)")]
    public bool BuildColliders = true;
    public float BoundaryWidth = 0.25f;
    public float LeftWallHeight = 40f;
    public Color BoundaryColor = new Color(0.25f, 0.25f, 0.3f);
    public Color GoalColor = new Color(1f, 0.85f, 0.2f, 0.6f);
    [Tooltip("색상별 기능 수치 (Docs/101 1장). BuildColliders 일 때만 스트로크에 부착된다")]
    public StrokeBehaviours.Settings ColorBehaviours = new StrokeBehaviours.Settings();

    public MapData Current { get; private set; }
    public GoalZone Goal { get; private set; }

    Transform _root;

    public void Load(MapData map)
    {
        if (map == null) throw new ArgumentNullException(nameof(map));
        Unload();
        if (palette == null) palette = StrokePalette.LoadOrDefault();

        Current = map;
        _root = new GameObject("Map").transform;
        _root.SetParent(transform, false);
        _root.localPosition = Vector3.zero;

        if (BuildBoundaries) BuildBoundaryObjects();

        for (int i = 0; i < map.Strokes.Count; i++)
        {
            var s = map.Strokes[i];
            if (s.Points.Count < 2) continue;
            var go = new GameObject($"Stroke {i} (c{s.ColorId})");
            go.transform.SetParent(_root, false);
            StrokeVisual.Build(go, s.Points, s.Width, palette.GetColor(s.ColorId), BuildColliders, sortingOrder: 0);
            if (BuildColliders) StrokeBehaviours.Attach(go, s.ColorId, ColorBehaviours);   // 색별 기능 (Docs/101 1장) — 미리보기에는 없음
        }

        if (BuildGoal && map.HasGoal) BuildGoalObject(map.GoalPos);
    }

    public void Unload()
    {
        if (_root != null) Destroy(_root.gameObject);
        _root = null;
        Goal = null;
        Current = null;
    }

    void BuildBoundaryObjects()
    {
        // 사방 경계 (QA: 오른쪽·위가 열려 있어 플레이어가 맵 밖으로 나가던 문제 → 캔버스 네 변 모두 벽). 모서리는 살짝 겹치게 연장
        float w = MapConstants.CanvasWidth, h = MapConstants.CanvasHeight, e = 1f;
        Boundary("Boundary Floor", new Vector2(-e, 0f), new Vector2(w + e, 0f));
        Boundary("Boundary Left Wall", new Vector2(0f, -e), new Vector2(0f, h + e));
        Boundary("Boundary Right Wall", new Vector2(w, -e), new Vector2(w, h + e));
        Boundary("Boundary Ceiling", new Vector2(-e, h), new Vector2(w + e, h));
    }

    void Boundary(string name, Vector2 a, Vector2 b)
    {
        var go = new GameObject(name);
        go.transform.SetParent(_root, false);
        StrokeVisual.Build(go, new[] { a, b }, BoundaryWidth, BoundaryColor, BuildColliders, sortingOrder: -5);
    }

    void BuildGoalObject(Vector2 pos)
    {
        // 시작 마커와 같은 원형 (테마 GoalPulse + GoalMarker). 테마가 없으면 절차 생성 원 (QA: 네모 → 동그라미)
        var go = new GameObject("Goal");
        go.transform.SetParent(_root, false);
        go.transform.position = new Vector3(pos.x, pos.y, 0f);
        var theme = MapEditorTheme.LoadOrNull();
        if (theme != null && theme.GoalMarker != null)
        {
            CanvasView.Marker(go.transform, theme.GoalPulse, theme.PulseSize, 4);
            CanvasView.Marker(go.transform, theme.GoalMarker, theme.MarkerSize, 5);
        }
        else
        {
            var vis = new GameObject("Circle");
            vis.transform.SetParent(go.transform, false);
            vis.transform.localScale = new Vector3(MapConstants.GoalSize.x, MapConstants.GoalSize.y, 1f);
            var sr = vis.AddComponent<SpriteRenderer>();
            sr.sprite = RuntimeSprites.Circle; sr.color = GoalColor; sr.sortingOrder = 5;
        }
        if (BuildColliders)
        {
            var circle = go.AddComponent<CircleCollider2D>();
            circle.isTrigger = true;
            circle.radius = MapConstants.GoalSize.x * 0.5f;
        }
        Goal = go.AddComponent<GoalZone>();
    }
}
