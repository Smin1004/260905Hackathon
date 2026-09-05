using System;
using UnityEngine;

/// <summary>
/// 스트로크 ColorId 상수 (StrokePalette.DefaultEntries 와 같은 번호 — Docs/101 1장 색상별 오브젝트 시스템).
/// 팔레트 에셋의 표시 색은 바꿔도 되지만 번호는 맵 직렬화·기능 매핑에 쓰이므로 고정.
/// </summary>
public static class StrokeColorId
{
    public const int Wall = 0;     // 검정 — 벽 (기본)
    public const int Hide = 1;     // 하늘 — 은폐 구역 (미구현, 아래 TODO)
    public const int Yellow = 2;   // 노랑 — 코어에서는 벽과 동일 (Docs/101: 골 흡수 후보)
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
///   - 가속·감속 시간: 기준값과 MinGroundAccelTime/MinGroundDecelTime 중 큰 쪽 (기본 GroundDecelTime 이 0 이라 배율로는 표현 불가 → 하한 방식)
///   - 정지 마찰: 기준값 × FrictionMultiplier
/// 따라서 "미끄러운 발" 뜻(0.5 / 0.6 / 0.05)과 겹치면 가속·감속은 같고 마찰만 더 낮아진다.
/// </summary>
public class SurfaceModifier : MonoBehaviour
{
    public float MinGroundAccelTime = 0.5f;
    public float MinGroundDecelTime = 0.6f;
    public float FrictionMultiplier = 0.05f;
}

/// <summary>ColorId → 스트로크 오브젝트에 기능 컴포넌트 부착. MapLoader 가 BuildColliders 일 때만 호출한다 (에디터 미리보기에는 붙지 않음).</summary>
public static class StrokeBehaviours
{
    [Serializable]
    public class Settings
    {
        [Tooltip("초록: 착지 시 상승 속도 = JumpSpeed × 이 값")] public float BounceMultiplier = 1.3f;
        [Tooltip("파랑: 지상 가속 시간 하한 (기본 0.05 → 0.5)")] public float IceAccelTime = 0.5f;
        [Tooltip("파랑: 지상 감속 시간 하한 (기본 0 → 0.6)")] public float IceDecelTime = 0.6f;
        [Tooltip("파랑: 정지 마찰 배율 (기본 1.0 → 0.05)")] public float IceFrictionMultiplier = 0.05f;
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
                m.MinGroundAccelTime = settings.IceAccelTime;
                m.MinGroundDecelTime = settings.IceDecelTime;
                m.FrictionMultiplier = settings.IceFrictionMultiplier;
                break;
            }
            case StrokeColorId.Hide:
                // TODO(Docs/101 1장 하늘색 은폐 구역): 스트로크가 닫힌 영역일 때 내부 판정 → 플레이어 스프라이트 알파 0.
                //   닫힌 영역 판정(첫 점·끝 점 근접 + 폴리곤 내부 검사)이 필요해 이번 단계에서는 벽으로만 동작한다.
                break;
            default:
                break;   // 검정·노랑: 기본 벽
        }
    }
}
