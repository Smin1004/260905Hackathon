#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.U2D.Sprites;
using UnityEngine;

/// <summary>
/// 플레이어 스프라이트 시트 임포트·슬라이스·세트 생성 (에디터 전용 — Docs/102 1.1, Docs/201 6장 규칙상 #if UNITY_EDITOR).
/// 메뉴 [Chojiilgwan > Build Player Sprites]:
///   1. Assets/Art/Player/player_sheet.png 를 Sprite(Multiple) 로 4×4 슬라이스
///   2. idle 첫 프레임의 알파 바운딩 박스로 PPU(= 높이 / BodySize.y)와 피벗(발) 결정 → 모든 프레임 공통
///   3. Resources/Art/PlayerSpriteSet.asset 에 Idle 4 / JumpUp 3 / JumpDown 3 / Spare 2 / Walk 4 로 배정
/// </summary>
public static class PlayerSpriteBuilder
{
    const string SheetPath = "Assets/Art/Player/player_sheet.png";
    const string AssetPath = "Assets/Resources/" + PlayerSpriteSet.ResourcePath + ".asset";
    const int Cols = 4, Rows = 4;

    [MenuItem("Chojiilgwan/Build Player Sprites")]
    public static void Build()
    {
        var importer = AssetImporter.GetAtPath(SheetPath) as TextureImporter;
        if (importer == null) { Debug.LogError($"[PlayerSpriteBuilder] {SheetPath} 없음"); return; }

        // 1) 픽셀을 읽기 위해 먼저 읽기 가능 + 비압축으로 임포트
        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Multiple;
        importer.isReadable = true;
        importer.mipmapEnabled = false;
        importer.filterMode = FilterMode.Bilinear;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.alphaIsTransparency = true;
        importer.maxTextureSize = 2048;
        importer.SaveAndReimport();

        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(SheetPath);
        int w = tex.width, h = tex.height, cw = w / Cols, ch = h / Rows;
        var px = tex.GetPixels32();

        // 2) idle 첫 프레임(왼쪽 위 칸)의 알파 바운딩 박스 → PPU·발 피벗
        int cellX0 = 0, cellY0 = h - ch;   // 텍스처 좌표는 아래가 0 → 첫 행은 위쪽
        int minY = int.MaxValue, maxY = -1, minX = int.MaxValue, maxX = -1;
        for (int y = cellY0; y < cellY0 + ch; y++)
            for (int x = cellX0; x < cellX0 + cw; x++)
                if (px[y * w + x].a > 16) { if (y < minY) minY = y; if (y > maxY) maxY = y; if (x < minX) minX = x; if (x > maxX) maxX = x; }
        if (maxY < 0) { Debug.LogError("[PlayerSpriteBuilder] idle 프레임에 불투명 픽셀이 없습니다"); return; }
        int bodyH = maxY - minY + 1, bodyW = maxX - minX + 1;
        float ppu = bodyH / PlayerController.BodySize.y;                 // idle 높이 = 몸 높이(0.9u)
        float pivotY = (minY - cellY0) / (float)ch;                       // 발 = 알파 바닥
        float pivotX = ((minX + maxX) * 0.5f - cellX0) / cw;              // idle 몸 중심 x

        // 3) 슬라이스 (Sprite Editor 데이터 공급자 — Unity 6 방식)
        var factory = new SpriteDataProviderFactories();
        factory.Init();
        var provider = factory.GetSpriteEditorDataProviderFromObject(importer);
        provider.InitSpriteEditorDataProvider();
        var rects = new List<SpriteRect>();
        for (int r = 0; r < Rows; r++)
            for (int c = 0; c < Cols; c++)
            {
                int i = r * Cols + c;
                rects.Add(new SpriteRect
                {
                    name = $"player_{i:00}",
                    spriteID = GUID.Generate(),
                    rect = new Rect(c * cw, h - (r + 1) * ch, cw, ch),
                    alignment = SpriteAlignment.Custom,
                    pivot = new Vector2(pivotX, pivotY),
                    border = Vector4.zero,
                });
            }
        provider.SetSpriteRects(rects.ToArray());
        var nameIds = provider.GetDataProvider<ISpriteNameFileIdDataProvider>();
        if (nameIds != null) nameIds.SetNameFileIdPairs(rects.Select(x => new SpriteNameFileIdPair(x.name, x.spriteID)));
        provider.Apply();

        importer.spritePixelsPerUnit = ppu;
        importer.isReadable = false;
        importer.textureCompression = TextureImporterCompression.Uncompressed;   // 작은 시트 — 선명하게
        importer.SaveAndReimport();

        // 4) 세트 에셋
        var sprites = AssetDatabase.LoadAllAssetRepresentationsAtPath(SheetPath).OfType<Sprite>().OrderBy(s => s.name).ToArray();
        if (sprites.Length != Cols * Rows) { Debug.LogError($"[PlayerSpriteBuilder] 스프라이트 {sprites.Length}개 — 16개여야 합니다"); return; }
        Sprite S(int i) => sprites[i];

        var set = AssetDatabase.LoadAssetAtPath<PlayerSpriteSet>(AssetPath);
        bool created = set == null;
        if (created)
        {
            set = ScriptableObject.CreateInstance<PlayerSpriteSet>();
            Directory.CreateDirectory(Path.GetDirectoryName(AssetPath));
            AssetDatabase.CreateAsset(set, AssetPath);
        }
        set.Idle = new[] { S(0), S(1), S(2), S(3) };
        set.JumpUp = new[] { S(4), S(5), S(6) };
        set.JumpDown = new[] { S(7), S(8), S(9) };
        set.Spare = new[] { S(10), S(11) };
        set.Walk = new[] { S(12), S(13), S(14), S(15) };
        EditorUtility.SetDirty(set);
        AssetDatabase.SaveAssets();

        Debug.Log($"[PlayerSpriteBuilder] {(created ? "생성" : "갱신")} — {AssetPath}. 시트 {w}×{h}, 칸 {cw}×{ch}, idle 몸 {bodyW}×{bodyH}px → PPU {ppu:0.0}, 피벗 ({pivotX:0.00}, {pivotY:0.00}), 몸 폭 {bodyW / ppu:0.00}u (콜라이더 {PlayerController.BodySize.x}u)");
    }
}
#endif
