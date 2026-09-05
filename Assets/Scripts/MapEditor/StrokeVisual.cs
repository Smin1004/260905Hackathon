using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 스트로크 → LineRenderer(+EdgeCollider2D) 생성. 에디터 씬과 Play 씬(MapLoader)이 같은 코드를 써서 보이는 것과 물리가 일치한다.
/// 머티리얼·굵기는 여기서만 정하므로 아트 후배치 시 이 파일만 바꾼다 (Docs/203 7장).
/// </summary>
public static class StrokeVisual
{
    static Material _lineMaterial;

    public static Material LineMaterial
    {
        get
        {
            if (_lineMaterial == null)
            {
                var sh = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default")
                         ?? Shader.Find("Sprites/Default")
                         ?? Shader.Find("Unlit/Color");
                _lineMaterial = new Material(sh) { name = "StrokeLine (runtime)" };
            }
            return _lineMaterial;
        }
    }

    /// <summary>go 에 선 렌더러(필요 시 콜라이더)를 붙이고 점을 채운다. 점은 월드 좌표이므로 go 는 원점·무회전·단위 스케일이어야 한다.</summary>
    public static LineRenderer Build(GameObject go, IList<Vector2> points, float width, Color color, bool withCollider, int sortingOrder = 0)
    {
        var lr = go.GetComponent<LineRenderer>();
        if (lr == null) lr = go.AddComponent<LineRenderer>();
        lr.useWorldSpace = false;
        lr.alignment = LineAlignment.TransformZ;
        lr.textureMode = LineTextureMode.Stretch;
        lr.numCapVertices = 6;
        lr.numCornerVertices = 6;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        lr.sharedMaterial = LineMaterial;
        lr.startWidth = lr.endWidth = width;
        lr.startColor = lr.endColor = color;
        lr.sortingOrder = sortingOrder;
        SetPoints(lr, points);

        if (withCollider)
        {
            var ec = go.GetComponent<EdgeCollider2D>();
            if (ec == null) ec = go.AddComponent<EdgeCollider2D>();
            ec.SetPoints(new List<Vector2>(points));
            ec.edgeRadius = width * 0.5f;   // 두께 없는 선 콜라이더의 터널링·끼임 방지
        }
        return lr;
    }

    public static void SetPoints(LineRenderer lr, IList<Vector2> points)
    {
        lr.positionCount = points.Count;
        for (int i = 0; i < points.Count; i++) lr.SetPosition(i, new Vector3(points[i].x, points[i].y, 0f));
    }
}

/// <summary>런타임 생성 스프라이트 (플레이스홀더). 아트 교체 시 프리팹 스프라이트로 대체.</summary>
public static class RuntimeSprites
{
    static Sprite _white;

    /// <summary>1u × 1u 흰색 정사각형 스프라이트.</summary>
    public static Sprite White
    {
        get
        {
            if (_white == null)
            {
                var tex = new Texture2D(4, 4, TextureFormat.RGBA32, false);
                var px = new Color32[16];
                for (int i = 0; i < px.Length; i++) px[i] = new Color32(255, 255, 255, 255);
                tex.SetPixels32(px);
                tex.Apply();
                tex.filterMode = FilterMode.Point;
                _white = Sprite.Create(tex, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 4f);
            }
            return _white;
        }
    }

    public static SpriteRenderer MakeSquare(string name, Transform parent, Vector2 pos, Vector2 size, Color color, int sortingOrder)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.position = new Vector3(pos.x, pos.y, 0f);
        go.transform.localScale = new Vector3(size.x, size.y, 1f);
        var sr = go.AddComponent<SpriteRenderer>();
        sr.sprite = White;
        sr.color = color;
        sr.sortingOrder = sortingOrder;
        return sr;
    }
}
