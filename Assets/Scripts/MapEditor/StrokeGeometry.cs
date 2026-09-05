using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 스트로크 점 목록에 대한 순수 기하 연산 (부작용 없음). 에디터·테스트에서 공용.
/// - 양자화 (Docs/203 5장)
/// - RDP(Douglas-Peucker) 단순화 + 점 상한 강제 (Docs/203 3·4장)
/// - 지우개: 원과 겹치는 구간을 잘라 스트로크를 분할
/// </summary>
public static class StrokeGeometry
{
    public static Vector2 Quantize(Vector2 v)
    {
        float q = MapConstants.Quantization;
        return new Vector2(Mathf.Round(v.x / q) * q, Mathf.Round(v.y / q) * q);
    }

    public static Vector2 ClampToCanvas(Vector2 v)
    {
        return new Vector2(Mathf.Clamp(v.x, 0f, MapConstants.CanvasWidth), Mathf.Clamp(v.y, 0f, MapConstants.CanvasHeight));
    }

    public static bool InCanvas(Vector2 v)
    {
        return v.x >= 0f && v.x <= MapConstants.CanvasWidth && v.y >= 0f && v.y <= MapConstants.CanvasHeight;
    }

    /// <summary>
    /// 확정 처리: 단순화 → 점 상한 강제 → 양자화 → 연속 중복점 제거.
    /// 검증 플레이 전에 호출되어야 전송본과 로컬 물리가 같아진다.
    /// </summary>
    public static void Finalize(StrokeData s)
    {
        if (s.Points.Count > 2) s.Points = Simplify(s.Points, MapConstants.SimplifyEpsilon);

        float eps = MapConstants.SimplifyEpsilon;
        while (s.Points.Count > MapConstants.MaxPointsPerStroke && eps < 5f)
        {
            eps *= 1.5f;
            s.Points = Simplify(s.Points, eps);
        }
        if (s.Points.Count > MapConstants.MaxPointsPerStroke)
            s.Points.RemoveRange(MapConstants.MaxPointsPerStroke, s.Points.Count - MapConstants.MaxPointsPerStroke);

        for (int i = 0; i < s.Points.Count; i++) s.Points[i] = Quantize(ClampToCanvas(s.Points[i]));
        RemoveConsecutiveDuplicates(s.Points);
    }

    public static void RemoveConsecutiveDuplicates(List<Vector2> pts)
    {
        for (int i = pts.Count - 1; i > 0; i--)
            if ((pts[i] - pts[i - 1]).sqrMagnitude < 1e-8f) pts.RemoveAt(i);
    }

    /// <summary>Ramer–Douglas–Peucker. 양 끝점은 항상 유지.</summary>
    public static List<Vector2> Simplify(List<Vector2> pts, float epsilon)
    {
        if (pts.Count < 3) return new List<Vector2>(pts);
        var keep = new bool[pts.Count];
        keep[0] = keep[pts.Count - 1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, pts.Count - 1));
        float eps2 = epsilon * epsilon;

        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            if (b - a < 2) continue;
            float maxD = -1f; int idx = -1;
            for (int i = a + 1; i < b; i++)
            {
                float d = SqrDistanceToSegment(pts[i], pts[a], pts[b]);
                if (d > maxD) { maxD = d; idx = i; }
            }
            if (maxD > eps2 && idx >= 0)
            {
                keep[idx] = true;
                stack.Push((a, idx));
                stack.Push((idx, b));
            }
        }

        var result = new List<Vector2>();
        for (int i = 0; i < pts.Count; i++) if (keep[i]) result.Add(pts[i]);
        return result;
    }

    public static float SqrDistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float len2 = ab.sqrMagnitude;
        if (len2 < 1e-10f) return (p - a).sqrMagnitude;
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
        return (p - (a + ab * t)).sqrMagnitude;
    }

    /// <summary>스트로크(굵기 포함)가 원과 겹치는지 — 지우개 대상 판정.</summary>
    public static bool IntersectsCircle(StrokeData s, Vector2 center, float radius)
    {
        float r = radius + s.Width * 0.5f;
        float r2 = r * r;
        var pts = s.Points;
        if (pts.Count == 1) return (pts[0] - center).sqrMagnitude <= r2;
        for (int i = 0; i < pts.Count - 1; i++)
            if (SqrDistanceToSegment(center, pts[i], pts[i + 1]) <= r2) return true;
        return false;
    }

    /// <summary>
    /// 원 안에 들어가는 구간을 제거하고 남은 구간들을 반환한다. 점이 드문 긴 직선도 교차점을 계산해 정확히 잘린다.
    /// 반환된 각 구간은 점 2개 이상. 원본은 수정하지 않는다.
    /// </summary>
    public static List<List<Vector2>> CutByCircle(List<Vector2> pts, Vector2 c, float r)
    {
        var runs = new List<List<Vector2>>();
        var cur = new List<Vector2>();
        float r2 = r * r;

        bool Inside(Vector2 p) => (p - c).sqrMagnitude <= r2;
        void Flush() { if (cur.Count >= 2) runs.Add(cur); cur = new List<Vector2>(); }

        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            bool pin = Inside(p);
            if (!pin) cur.Add(p); else Flush();

            if (i == pts.Count - 1) break;
            var q = pts[i + 1];
            bool qin = Inside(q);

            if (SegmentCircle(p, q, c, r, out float t1, out float t2))
            {
                if (!pin && !qin)
                {
                    if (t1 > 0f && t2 < 1f && t1 < t2)
                    {
                        cur.Add(Vector2.Lerp(p, q, t1));
                        Flush();
                        cur.Add(Vector2.Lerp(p, q, t2));
                    }
                }
                else if (!pin && qin)
                {
                    cur.Add(Vector2.Lerp(p, q, Mathf.Clamp01(t1)));
                    Flush();
                }
                else if (pin && !qin)
                {
                    cur.Add(Vector2.Lerp(p, q, Mathf.Clamp01(t2)));
                }
            }
        }
        Flush();
        return runs;
    }

    /// <summary>선분 p→q 와 원의 교차 파라미터 t1 ≤ t2 (선분 파라미터 공간, 클램프 안 함). 교차 없으면 false.</summary>
    public static bool SegmentCircle(Vector2 p, Vector2 q, Vector2 c, float r, out float t1, out float t2)
    {
        t1 = t2 = 0f;
        var d = q - p;
        var f = p - c;
        float a = Vector2.Dot(d, d);
        if (a < 1e-10f) return false;
        float b = 2f * Vector2.Dot(f, d);
        float cc = Vector2.Dot(f, f) - r * r;
        float disc = b * b - 4f * a * cc;
        if (disc < 0f) return false;
        float s = Mathf.Sqrt(disc);
        t1 = (-b - s) / (2f * a);
        t2 = (-b + s) / (2f * a);
        return t2 >= 0f && t1 <= 1f;
    }

    /// <summary>
    /// 지우개 적용: 원과 겹치는 스트로크를 잘라 교체한다. 굵기의 절반을 반지름에 더해 "보이는 선"을 지운다.
    /// </summary>
    /// <returns>변경이 있었는지</returns>
    public static bool EraseCircle(List<StrokeData> strokes, Vector2 center, float radius)
    {
        bool changed = false;
        for (int i = strokes.Count - 1; i >= 0; i--)
        {
            var s = strokes[i];
            if (!IntersectsCircle(s, center, radius)) continue;

            var runs = CutByCircle(s.Points, center, radius + s.Width * 0.5f);
            strokes.RemoveAt(i);
            int insertAt = i;
            foreach (var run in runs)
            {
                var ns = new StrokeData { Width = s.Width, ColorId = s.ColorId, Points = run };
                for (int k = 0; k < ns.Points.Count; k++) ns.Points[k] = Quantize(ns.Points[k]);
                RemoveConsecutiveDuplicates(ns.Points);
                if (ns.Points.Count >= 2) strokes.Insert(insertAt++, ns);
            }
            changed = true;
        }
        return changed;
    }
}
