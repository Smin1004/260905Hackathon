using UnityEngine;

/// <summary>
/// 플레이어 스프라이트 시트 프레임 묶음 (Docs/102 1.1). Resources/Art/PlayerSpriteSet.asset — 메뉴 [Chojiilgwan > Build Player Sprites] 가
/// Assets/Art/Player/player_sheet.png 를 4×4 로 잘라 채운다. 없으면 PlayerController 는 예전 사각형 플레이스홀더로 동작.
///
/// 시트 배치 (4×4, 왼쪽 위부터):
///   1행        Idle 4프레임
///   2행 + 3행 앞 2칸  점프 6프레임 — 앞 3개: 상승(최고점까지), 뒤 3개: 하강(착지까지)
///   3행 뒤 2칸  잉여 (미사용)
///   4행        Walk 4프레임
/// 스프라이트 피벗 = 발(idle 프레임 알파 바닥), PPU = idle 프레임 높이 / PlayerController.BodySize.y → 몸 높이가 콜라이더와 같다.
/// </summary>
[CreateAssetMenu(fileName = "PlayerSpriteSet", menuName = "Chojiilgwan/Player Sprite Set")]
public class PlayerSpriteSet : ScriptableObject
{
    public const string ResourcePath = "Art/PlayerSpriteSet";

    public Sprite[] Idle;
    public Sprite[] JumpUp;     // 상승 — 속도가 줄어드는 순서
    public Sprite[] JumpDown;   // 하강 — 낙하 속도가 커지는 순서
    public Sprite[] Walk;
    public Sprite[] Spare;      // 잉여 프레임 (미사용)

    [Tooltip("Idle 프레임/초")] public float IdleFps = 4f;
    [Tooltip("Walk 프레임/초 (최고 속도 기준, 속도에 비례)")] public float WalkFps = 10f;

    public bool IsValid => Idle != null && Idle.Length > 0 && Idle[0] != null;

    static PlayerSpriteSet _cached; static bool _loaded;
    public static PlayerSpriteSet LoadOrNull()
    {
        if (!_loaded) { _cached = Resources.Load<PlayerSpriteSet>(ResourcePath); _loaded = true; }
        return _cached != null && _cached.IsValid ? _cached : null;
    }

    public static Sprite Pick(Sprite[] frames, int index)
    {
        if (frames == null || frames.Length == 0) return null;
        return frames[Mathf.Clamp(index, 0, frames.Length - 1)];
    }

    public static Sprite Loop(Sprite[] frames, float time, float fps)
    {
        if (frames == null || frames.Length == 0) return null;
        int i = Mathf.FloorToInt(time * Mathf.Max(0.01f, fps)) % frames.Length;
        return frames[i];
    }

    /// <summary>진행도 0~1 을 프레임 인덱스로 (마지막 프레임은 1.0 에서만)</summary>
    public static Sprite ByProgress(Sprite[] frames, float t)
    {
        if (frames == null || frames.Length == 0) return null;
        int i = Mathf.Min(frames.Length - 1, Mathf.FloorToInt(Mathf.Clamp01(t) * frames.Length));
        return frames[i];
    }
}
