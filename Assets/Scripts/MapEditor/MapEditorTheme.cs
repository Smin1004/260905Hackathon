using UnityEngine;

/// <summary>
/// 맵 에디터 화면 테마 (Docs/201 5장 — 색상은 코드 하드코딩 대신 테마 ScriptableObject 참조).
/// HUD 프리팹(Assets/Prefabs/UI/MapEditorHud.prefab)과 CanvasView 가 함께 참조한다.
/// 에셋 위치: Assets/Resources/UI/MapEditorTheme.asset — 메뉴 [Chojiilgwan > Build MapEditor HUD] 가 없으면 만든다.
/// </summary>
[CreateAssetMenu(menuName = "Chojiilgwan/MapEditor Theme", fileName = "MapEditorTheme")]
public class MapEditorTheme : ScriptableObject
{
    [Header("색상")]
    public Color Background = new Color32(9, 27, 49, 255);      // 화면 배경 (네이비)
    public Color Paper = new Color32(251, 249, 243, 255);       // 드로잉 캔버스 (아이보리)
    public Color PaperDots = new Color32(255, 255, 255, 255);   // 캔버스 도트 타일 틴트
    public Color TextPrimary = Color.white;
    public Color TextMuted = new Color32(160, 172, 190, 255);
    public Color TextOnLight = new Color32(18, 37, 60, 255);    // 밝은 배경 위 글자 (네이비)
    public Color Accent = new Color32(111, 226, 118, 255);      // 초록 — 타이머·안내
    public Color Warning = new Color32(247, 95, 76, 255);       // 코랄 — 골 배치·경고
    public Color Mint = new Color32(0, 191, 165, 255);          // 시작점
    public Color IconDisabled = new Color32(90, 108, 132, 255);
    [Tooltip("바닥·왼쪽 벽 표시색 — Play 씬 MapLoader 기본값과 맞춘다")]
    public Color BoundaryColor = new Color(0.25f, 0.25f, 0.3f);
    public float BoundaryWidth = 0.25f;

    [Header("캔버스(월드) 스프라이트 — CanvasView 가 사용")]
    public Sprite PaperDotsTile;   // Tiled 로 캔버스 전체에 깐다 (PPU 로 도트 간격 결정)

    [Header("화면 배경 — 모든 화면(로비·방·뜻 선택·결과·플레이)이 같은 네이비 + 도트를 쓴다 (RuntimeUI.Backdrop)")]
    public Sprite BgDotsTile;      // 에디터 BackCanvas 와 같은 tiles/tile_bg_dots
    public Sprite StartMarker;
    public Sprite StartPulse;
    public Sprite GoalMarker;
    public Sprite GoalPulse;
    [Tooltip("마커 지름 (u)")] public float MarkerSize = 1.2f;
    [Tooltip("펄스(뒤 원) 지름 (u)")] public float PulseSize = 2.0f;

    public const string ResourcePath = "UI/MapEditorTheme";
    static MapEditorTheme _cached; static bool _loaded;

    public static MapEditorTheme LoadOrNull()
    {
        // Resources/UI/MapEditorTheme.asset — 빌드에서도 로드된다 (2026-09-06 이동. 이전 Assets/UI 는 에디터 전용이라 빌드에서 마커·배경이 빠졌다)
        if (!_loaded) { _cached = Resources.Load<MapEditorTheme>(ResourcePath); _loaded = true; }
        return _cached;
    }
}
