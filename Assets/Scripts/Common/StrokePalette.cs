using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 스트로크 색상 팔레트 (테마 ScriptableObject — Docs/201 5장 "색상은 코드 하드코딩 대신 테마 SO 참조").
/// ColorId → 표시 색. 코어에서는 시각 구분용이고, 색별 기능(은폐·바운스 등)은 Docs/101 색상 시스템에서 로더가 해석한다.
/// 에셋 위치: Assets/Resources/StrokePalette.asset (없으면 기본값으로 동작).
/// </summary>
[CreateAssetMenu(menuName = "Chojiilgwan/Stroke Palette", fileName = "StrokePalette")]
public class StrokePalette : ScriptableObject
{
    [Serializable]
    public class Entry
    {
        public int ColorId;
        public string Name;
        public Color Color = Color.black;
    }

    public List<Entry> Entries = new List<Entry>();

    public Color GetColor(int colorId)
    {
        foreach (var e in Entries) if (e.ColorId == colorId) return e.Color;
        return Color.black;
    }

    public string GetName(int colorId)
    {
        foreach (var e in Entries) if (e.ColorId == colorId) return e.Name;
        return colorId.ToString();
    }

    public static StrokePalette LoadOrDefault()
    {
        var p = Resources.Load<StrokePalette>("StrokePalette");
        if (p != null && p.Entries.Count > 0) return p;
        p = CreateInstance<StrokePalette>();
        p.Entries = DefaultEntries();
        return p;
    }

    /// <summary>Docs/101 1장 색 목록. 0 검정(벽) / 1 하늘(은폐) / 2 노랑(골) / 3 초록(바운스) / 4 파랑(얼음) / 5 빨강(위험)</summary>
    public static List<Entry> DefaultEntries() => new List<Entry>
    {
        new Entry { ColorId = 0, Name = "검정", Color = new Color(0.12f, 0.12f, 0.12f) },
        new Entry { ColorId = 1, Name = "하늘", Color = new Color(0.45f, 0.80f, 1.00f) },
        new Entry { ColorId = 2, Name = "노랑", Color = new Color(1.00f, 0.85f, 0.20f) },
        new Entry { ColorId = 3, Name = "초록", Color = new Color(0.30f, 0.80f, 0.35f) },
        new Entry { ColorId = 4, Name = "파랑", Color = new Color(0.20f, 0.40f, 0.95f) },
        new Entry { ColorId = 5, Name = "빨강", Color = new Color(0.95f, 0.25f, 0.25f) },
    };
}
