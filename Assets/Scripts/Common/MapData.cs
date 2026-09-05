using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 맵 관련 고정 상수. 수치 근거: Docs/203_map_editor.md 3장, Docs/100_game_design.md 5장.
/// 값을 바꾸면 에디터·로더·직렬화가 모두 같은 값을 보므로 여기서만 바꾼다.
/// </summary>
public static class MapConstants
{
    /// <summary>캔버스(맵) 크기. 월드 좌표 (0,0)~(Width,Height). 바닥 y=0, 왼쪽 벽 x=0. 천장·오른쪽 개방.</summary>
    public const float CanvasWidth = 30f;
    public const float CanvasHeight = 15f;

    public const int MaxStrokes = 60;
    public const int MaxPointsPerStroke = 300;

    /// <summary>점 수집 시 최소 이동 거리 — 데이터량 제어.</summary>
    public const float MinPointDistance = 0.1f;
    /// <summary>좌표 양자화 단위 (소수 둘째 자리). 검증 플레이 전에 적용해 전송본과 물리가 일치하게 한다.</summary>
    public const float Quantization = 0.01f;
    /// <summary>확정 시 항상 적용하는 RDP 단순화 허용 오차 (u). 시각적으로 구분되지 않는 점을 제거.</summary>
    public const float SimplifyEpsilon = 0.02f;

    /// <summary>펜 굵기 프리셋 (u). UI 순서와 동일.</summary>
    public static readonly float[] PenWidths = { 0.15f, 0.3f, 0.6f };
    public const int DefaultWidthIndex = 1;

    /// <summary>지우개 반지름 프리셋 (u). 펜 굵기 인덱스와 짝을 맞춘다.</summary>
    public static readonly float[] EraserRadii = { 0.3f, 0.6f, 1.0f };

    /// <summary>시작점 — 플레이어 중심 좌표. 바닥선(굵기 0.25 → 윗면 y=0.125) 위에 0.9u 캐릭터가 서 있는 위치. 고정, 편집 불가.</summary>
    public static readonly Vector2 StartPos = new Vector2(1.5f, 0.65f);
    /// <summary>골 존 트리거 크기.</summary>
    public static readonly Vector2 GoalSize = new Vector2(1f, 1f);
    /// <summary>골은 시작점에서 최소 이 거리만큼 떨어져야 한다 (패타임 모드에서 0.2초짜리 맵 방지 — Docs/100 7.2 논의).</summary>
    public const float MinGoalDistanceFromStart = 3f;

    public const int MaxUndo = 50;

    /// <summary>네트워크 청크 크기 (Docs/205 5장). NGO 기본 페이로드 상한보다 작게.</summary>
    public const int NetworkChunkSize = 4096;
    /// <summary>전송 크기 목표 (Docs/203 5장). 초과 시 UI에서 경고.</summary>
    public const int TargetPayloadBytes = 100 * 1024;
}

/// <summary>
/// 스트로크 1개 — 이미지가 아닌 벡터 점 목록 (Docs/203 2장). 콜라이더 변환과 아트 재렌더링의 근거.
/// </summary>
[Serializable]
public class StrokeData
{
    /// <summary>월드 좌표 점 목록 (순서 유지, 양자화 완료본).</summary>
    public List<Vector2> Points = new List<Vector2>();
    /// <summary>선 굵기 (u). MapConstants.PenWidths 중 하나.</summary>
    public float Width = 0.3f;
    /// <summary>색상 ID. 코어에서는 시각 구분용이며 로더는 모든 색을 벽으로 취급한다. 의미 부여는 Docs/101 색상 시스템.</summary>
    public int ColorId = 0;

    public StrokeData Clone()
    {
        return new StrokeData { Points = new List<Vector2>(Points), Width = Width, ColorId = ColorId };
    }
}

/// <summary>맵 1개 (Docs/201 3장 공유 데이터 계약).</summary>
[Serializable]
public class MapData
{
    /// <summary>고정값이지만 명시 (수신측이 상수에 의존하지 않게).</summary>
    public Vector2 StartPos = MapConstants.StartPos;
    /// <summary>골 존 중심. 미배치 = (-1,-1).</summary>
    public Vector2 GoalPos = new Vector2(-1f, -1f);
    public List<StrokeData> Strokes = new List<StrokeData>();

    public bool HasGoal => GoalPos.x >= 0f && GoalPos.y >= 0f;

    public int TotalPoints
    {
        get { int n = 0; foreach (var s in Strokes) n += s.Points.Count; return n; }
    }

    public MapData Clone()
    {
        var m = new MapData { StartPos = StartPos, GoalPos = GoalPos };
        foreach (var s in Strokes) m.Strokes.Add(s.Clone());
        return m;
    }

    /// <summary>두 맵이 양자화 단위 안에서 같은지 (직렬화 왕복 검증용).</summary>
    public static bool ApproximatelyEqual(MapData a, MapData b, float tolerance = MapConstants.Quantization * 0.51f)
    {
        if (a == null || b == null) return false;
        if (a.Strokes.Count != b.Strokes.Count) return false;
        if ((a.StartPos - b.StartPos).magnitude > tolerance) return false;
        if (a.HasGoal != b.HasGoal) return false;
        if (a.HasGoal && (a.GoalPos - b.GoalPos).magnitude > tolerance) return false;
        for (int i = 0; i < a.Strokes.Count; i++)
        {
            var sa = a.Strokes[i]; var sb = b.Strokes[i];
            if (sa.ColorId != sb.ColorId || sa.Points.Count != sb.Points.Count) return false;
            if (Mathf.Abs(sa.Width - sb.Width) > 0.001f) return false;
            for (int p = 0; p < sa.Points.Count; p++)
                if ((sa.Points[p] - sb.Points[p]).magnitude > tolerance) return false;
        }
        return true;
    }
}
