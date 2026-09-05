using System;
using UnityEngine;

/// <summary>
/// 스트로크 ColorId 상수 (StrokePalette.DefaultEntries 와 같은 번호 — Docs/101 1장 색상별 오브젝트 시스템).
/// 팔레트 에셋의 표시 색은 바꿔도 되지만 번호는 맵 직렬화·기능 매핑에 쓰이므로 고정.
/// </summary>
public static class StrokeColorId
{
    public const int Wall = 0;     // 검정 — 벽 (기본)
    public const int Hide = 1;     // 하늘 — 컨베이어 ← (왼쪽으로 밀어냄). 이름은 팔레트 번호 호환을 위해 유지
    public const int Yellow = 2;   // 노랑 — 컨베이어 → (오른쪽으로 밀어냄)
    public const int Bounce = 3;   // 초록 — 바운스
    public const int Ice = 4;      // 파랑 — 얼음
    public const int Hazard = 5;   // 빨강 — 위험 구역
}

/// <summary>위험 구역 (빨강). 벽 콜라이더는 유지하고, 플레이어가 닿으면 PlayerController.HazardTouched → PlaySession 이 시작점 리스폰(시도 미소모).</summary>
public class HazardStroke : MonoBehaviour { }

/// <summary>바운스 (초록). 착지 순간 PlayerController 가 상승 속도를 JumpSpeed × SpeedMultiplier 로 설정한다.</summary>
public class BounceStroke : MonoBehaviour
{
    public float SpeedMultiplier = 1.3f;
}

/// <summary>
/// 발밑 표면 보정 (파랑 = 얼음). PlayerController 는 이 위에 서 있는 동안만 값을 덧씌우고, 뜻(Vows)이 정한 기준값 자체는 건드리지 않는다.
///   - 가속·감속 시간: 기준값 + ExtraGroundAccelTime/ExtraGroundDecelTime (가산 — 기본 GroundDecelTime 이 0 이라 배율로는 표현 불가)
///   - 정지 마찰: 기준값 × FrictionMultiplier
/// 따라서 "미끄러운 발" 뜻(0.5 / 0.6 / 0.05)과 겹치면 가속 0.95·감속 1.2 로 더 미끄러워진다 (QA: 뜻 + 파랑이 무의미하던 문제).
/// </summary>
public class SurfaceModifier : MonoBehaviour
{
    public float ExtraGroundAccelTime = 0.45f;
    public float ExtraGroundDecelTime = 0.6f;
    public float FrictionMultiplier = 0.05f;
    /// <summary>컨베이어: 서 있는 동안 지면 접선 방향으로 더해지는 속도 (u/s). + 오른쪽(노랑), − 왼쪽(하늘). 0 = 없음</summary>
    public float ConveyorSpeed = 0f;
}

/// <summary>ColorId → 스트로크 오브젝트에 기능 컴포넌트 부착. MapLoader 가 BuildColliders 일 때만 호출한다 (에디터 미리보기에는 붙지 않음).</summary>
public static class StrokeBehaviours
{
    [Serializable]
    public class Settings
    {
        [Tooltip("초록: 착지 시 상승 속도 = JumpSpeed × 이 값")] public float BounceMultiplier = 1.3f;
        [Tooltip("파랑: 지상 가속 시간에 더하는 값 (기본 0.05 → 0.5)")] public float IceAccelTime = 0.45f;
        [Tooltip("파랑: 지상 감속 시간에 더하는 값 (기본 0 → 0.6)")] public float IceDecelTime = 0.6f;
        [Tooltip("파랑: 정지 마찰 배율 (기본 1.0 → 0.05)")] public float IceFrictionMultiplier = 0.05f;
        [Tooltip("노랑(→)·하늘(←): 컨베이어가 밀어내는 속도 (u/s). 이동 속도 5 기준 3 이면 거슬러 걸을 수 있다")] public float ConveyorSpeed = 3f;
    }

    public static void Attach(GameObject strokeObject, int colorId, Settings settings)
    {
        if (strokeObject == null) return;
        settings ??= new Settings();
        switch (colorId)
        {
            case StrokeColorId.Hazard:
                strokeObject.AddComponent<HazardStroke>();
                break;
            case StrokeColorId.Bounce:
                strokeObject.AddComponent<BounceStroke>().SpeedMultiplier = settings.BounceMultiplier;
                break;
            case StrokeColorId.Ice:
            {
                var m = strokeObject.AddComponent<SurfaceModifier>();
                m.ExtraGroundAccelTime = settings.IceAccelTime;
                m.ExtraGroundDecelTime = settings.IceDecelTime;
                m.FrictionMultiplier = settings.IceFrictionMultiplier;
                break;
            }
            case StrokeColorId.Yellow:
                Conveyor(strokeObject, +Mathf.Abs(settings.ConveyorSpeed));   // 노랑: 오른쪽으로
                break;
            case StrokeColorId.Hide:
                Conveyor(strokeObject, -Mathf.Abs(settings.ConveyorSpeed));   // 하늘: 왼쪽으로
                break;
            default:
                break;   // 검정: 기본 벽
        }
    }

    /// <summary>컨베이어 표면: 마찰·가속은 기본값 그대로, 서 있으면 접선 방향으로 밀린다 (PlayerController 가 SurfaceModifier.ConveyorSpeed 를 더한다)</summary>
    static void Conveyor(GameObject strokeObject, float speed)
    {
        var m = strokeObject.AddComponent<SurfaceModifier>();
        m.ExtraGroundAccelTime = 0f;
        m.ExtraGroundDecelTime = 0f;
        m.FrictionMultiplier = 1f;
        m.ConveyorSpeed = speed;
    }
}
