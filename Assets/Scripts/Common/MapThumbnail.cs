using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// MapData → 썸네일 Texture2D (CPU 라스터). 카메라·렌더러 없이 벡터 스트로크(Docs/203 2장)를 선분 단위로 직접 그린다.
/// 결과 화면(Boot 런타임 UI)에서 양쪽 맵을 미리보기로 보여 주는 용도. 캔버스 (0,0)~(CanvasWidth,CanvasHeight)를
/// 비율 유지로 텍스처 안에 맞추고, 바닥·왼쪽 벽·시작점·골도 함께 표시한다.
/// 호출자는 반환된 Texture2D(와 그로 만든 Sprite)를 더 이상 쓰지 않을 때 Destroy 해야 한다.
/// </summary>
public static class MapThumbnail
{
    // 플레이스홀더 색 — 에디터 CanvasView 의 단색 플레이스홀더와 같은 값. 테마 도입 시 여기만 바꾸거나 호출 전에 덮어쓴다.
    public static Color BackgroundColor = new Color(0.96f, 0.96f, 0.94f);
    public static Color BoundaryColor = new Color(0.25f, 0.25f, 0.3f);
    public static Color StartColor = new Color(0.2f, 0.75f, 0.35f, 0.9f);
    public static Color GoalColor = new Color(1f, 0.85f, 0.2f, 0.85f);
    /// <summary>바닥·왼쪽 벽 굵기 (u) — MapEditorTheme.BoundaryWidth 기본값과 동일.</summary>
    public static float BoundaryWidth = 0.25f;
    /// <summary>선이 축소돼도 최소 이 픽셀 반지름은 유지 (얇은 선이 사라지지 않게).</summary>
    public static float MinLineRadiusPx = 0.75f;

    /// <summary>map 을 width×height 텍스처로 렌더. map 이 null 이면 빈 캔버스(배경·경계·시작점만).</summary>
    public static Texture2D Render(MapData map, StrokePalette palette, int width, int height)
    {
        width = Mathf.Max(8, width);
        height = Mathf.Max(8, height);
        if (palette == null) palette = StrokePalette.LoadOrDefault();

        var raster = new Raster(width, height);
        raster.Fill(BackgroundColor);

        // 캔버스 → 픽셀 변환 (비율 유지, 중앙 정렬)
        float scale = Mathf.Min(width / MapConstants.CanvasWidth, height / MapConstants.CanvasHeight);
        float offX = (width - MapConstants.CanvasWidth * scale) * 0.5f;
        float offY = (height - MapConstants.CanvasHeight * scale) * 0.5f;
        raster.SetTransform(scale, offX, offY);

        // 고정 경계: 바닥(y=0), 왼쪽 벽(x=0) — 천장·오른쪽 개방 (Docs/100 5장)
        float half = BoundaryWidth * 0.5f;
        raster.Line(new Vector2(0f, half), new Vector2(MapConstants.CanvasWidth, half), BoundaryWidth, BoundaryColor);
        raster.Line(new Vector2(half, 0f), new Vector2(half, MapConstants.CanvasHeight), BoundaryWidth, BoundaryColor);

        Vector2 startPos = map != null ? map.StartPos : MapConstants.StartPos;

        if (map != null && map.Strokes != null)
        {
            foreach (var s in map.Strokes)
            {
                if (s == null || s.Points == null || s.Points.Count == 0) continue;
                var c = palette.GetColor(s.ColorId);
                if (s.Points.Count == 1)
                {
                    raster.Line(s.Points[0], s.Points[0], s.Width, c);
                    continue;
                }
                for (int i = 1; i < s.Points.Count; i++)
                    raster.Line(s.Points[i - 1], s.Points[i], s.Width, c);
            }
        }

        // 시작점 (에디터 마커와 같은 크기 0.8×0.9), 골 (GoalSize)
        raster.Rect(startPos, new Vector2(0.8f, 0.9f), StartColor);
        if (map != null && map.HasGoal)
            raster.Rect(map.GoalPos, MapConstants.GoalSize, GoalColor);

        var tex = new Texture2D(width, height, TextureFormat.RGBA32, false)
        {
            name = "MapThumbnail (runtime)",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
        };
        tex.SetPixels32(raster.Pixels);
        tex.Apply(false, false);
        return tex;
    }

    /// <summary>Render + Sprite 생성 (uGUI Image 용). Sprite 와 Sprite.texture 둘 다 호출자가 Destroy 한다.</summary>
    public static Sprite RenderSprite(MapData map, StrokePalette palette, int width, int height)
    {
        var tex = Render(map, palette, width, height);
        return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
    }

    /// <summary>RenderSprite 로 만든 스프라이트와 그 텍스처를 함께 파괴한다. null 안전.</summary>
    public static void Release(ref Sprite sprite)
    {
        if (sprite == null) return;
        var tex = sprite.texture;
        Object.Destroy(sprite);
        if (tex != null) Object.Destroy(tex);
        sprite = null;
    }

    // ------------------------------------------------------------------ 라스터

    /// <summary>Color32 버퍼에 굵은 선분·사각형을 그리는 최소 라스터라이저. 원점은 좌하단(Texture2D 와 동일).</summary>
    sealed class Raster
    {
        public readonly int W, H;
        public readonly Color32[] Pixels;
        float _scale = 1f, _offX, _offY;

        public Raster(int w, int h)
        {
            W = w; H = h;
            Pixels = new Color32[w * h];
        }

        public void SetTransform(float scale, float offX, float offY)
        {
            _scale = scale; _offX = offX; _offY = offY;
        }

        public void Fill(Color c)
        {
            Color32 c32 = c;
            for (int i = 0; i < Pixels.Length; i++) Pixels[i] = c32;
        }

        Vector2 ToPx(Vector2 world) => new Vector2(world.x * _scale + _offX, world.y * _scale + _offY);

        /// <summary>월드 좌표 선분을 굵기 width(u)의 둥근 캡 선으로 그린다.</summary>
        public void Line(Vector2 a, Vector2 b, float width, Color color)
        {
            Vector2 pa = ToPx(a), pb = ToPx(b);
            float r = Mathf.Max(width * 0.5f * _scale, MinLineRadiusPx);

            int x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(pa.x, pb.x) - r - 1f));
            int x1 = Mathf.Min(W - 1, Mathf.CeilToInt(Mathf.Max(pa.x, pb.x) + r + 1f));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(pa.y, pb.y) - r - 1f));
            int y1 = Mathf.Min(H - 1, Mathf.CeilToInt(Mathf.Max(pa.y, pb.y) + r + 1f));
            if (x0 > x1 || y0 > y1) return;

            Vector2 ab = pb - pa;
            float abLen2 = ab.sqrMagnitude;
            float rOuter2 = (r + 0.5f) * (r + 0.5f);
            float rInner = Mathf.Max(0f, r - 0.5f);
            float rInner2 = rInner * rInner;

            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    float t = abLen2 > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p - pa, ab) / abLen2) : 0f;
                    float d2 = (p - (pa + ab * t)).sqrMagnitude;
                    if (d2 > rOuter2) continue;
                    // 가장자리 1px 만 부드럽게 (간이 안티에일리어싱)
                    float alpha = d2 <= rInner2 ? 1f : 1f - (Mathf.Sqrt(d2) - rInner) / ((r + 0.5f) - rInner);
                    Blend(x, y, color, Mathf.Clamp01(alpha));
                }
            }
        }

        /// <summary>월드 좌표 중심 center, 크기 size(u)의 채운 사각형.</summary>
        public void Rect(Vector2 center, Vector2 size, Color color)
        {
            Vector2 min = ToPx(center - size * 0.5f);
            Vector2 max = ToPx(center + size * 0.5f);
            int x0 = Mathf.Max(0, Mathf.RoundToInt(min.x));
            int x1 = Mathf.Min(W - 1, Mathf.RoundToInt(max.x) - 1);
            int y0 = Mathf.Max(0, Mathf.RoundToInt(min.y));
            int y1 = Mathf.Min(H - 1, Mathf.RoundToInt(max.y) - 1);
            if (x1 < x0) x1 = x0;   // 아주 작은 썸네일에서도 최소 1px
            if (y1 < y0) y1 = y0;
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    Blend(x, y, color, 1f);
        }

        /// <summary>color 를 alpha·color.a 만큼 덮어 합성 (straight alpha over).</summary>
        void Blend(int x, int y, Color color, float alpha)
        {
            if (x < 0 || y < 0 || x >= W || y >= H) return;
            float a = alpha * color.a;
            if (a <= 0f) return;
            int idx = y * W + x;
            Color dst = Pixels[idx];
            if (a >= 1f) { Pixels[idx] = new Color(color.r, color.g, color.b, 1f); return; }
            Pixels[idx] = new Color(
                Mathf.Lerp(dst.r, color.r, a),
                Mathf.Lerp(dst.g, color.g, a),
                Mathf.Lerp(dst.b, color.b, a),
                Mathf.Max(dst.a, a));
        }
    }
}
