using UnityEngine;

/// <summary>
/// 사운드 에셋 묶음 — Docs/102 3장. 클립·볼륨을 코드에 하드코딩하지 않고 여기(Resources/Audio/SoundBank.asset)에 모은다.
/// 생성/갱신: 메뉴 [Chojiilgwan > Build SoundBank] (Assets/Audio 의 파일명 규약으로 자동 연결).
/// 재생은 <see cref="Sound"/> 정적 API 로만 — 씬 코드는 AudioSource 를 직접 만들지 않는다.
/// </summary>
[CreateAssetMenu(fileName = "SoundBank", menuName = "Chojiilgwan/Sound Bank")]
public class SoundBank : ScriptableObject
{
    public const string ResourcePath = "Audio/SoundBank";

    [Header("배경음")]
    [Tooltip("로비·뜻 선택·에디터·결과 (lobby_edit)")] public AudioClip MusicLobbyEdit;
    [Tooltip("교환 플레이 (battle)")] public AudioClip MusicBattle;

    [Header("효과음 (1회)")]
    [Tooltip("버튼 클릭 공통")] public AudioClip Click;
    [Tooltip("점프")] public AudioClip Jump;
    [Tooltip("착지")] public AudioClip Land;
    [Tooltip("확정됨 — 뜻 확정·맵 제출")] public AudioClip Confirm;

    [Header("효과음 (루프 — 상태가 유지되는 동안)")]
    [Tooltip("펜으로 그리는 동안")] public AudioClip Drawing;
    [Tooltip("지우개 드래그 동안")] public AudioClip Eraser;
    [Tooltip("타이머 경고(마지막 10초) 동안")] public AudioClip Clock;

    [Header("볼륨")]
    [Range(0f, 1f)] public float MusicVolume = 0.35f;
    [Range(0f, 1f)] public float SfxVolume = 0.7f;
    [Range(0f, 1f)] public float LoopVolume = 0.45f;
    [Tooltip("배경음 전환 크로스페이드(초)")] public float MusicFade = 0.6f;

    public AudioClip Get(SfxId id) => id switch
    {
        SfxId.Click => Click,
        SfxId.Jump => Jump,
        SfxId.Land => Land,
        SfxId.Confirm => Confirm,
        _ => null,
    };

    public AudioClip Get(LoopId id) => id switch
    {
        LoopId.Drawing => Drawing,
        LoopId.Eraser => Eraser,
        LoopId.Clock => Clock,
        _ => null,
    };

    public AudioClip Get(MusicId id) => id switch
    {
        MusicId.LobbyEdit => MusicLobbyEdit,
        MusicId.Battle => MusicBattle,
        _ => null,
    };
}

public enum SfxId { Click, Jump, Land, Confirm }
public enum LoopId { Drawing, Eraser, Clock }
public enum MusicId { None, LobbyEdit, Battle }
