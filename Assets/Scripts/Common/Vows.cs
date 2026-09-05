using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>뜻(제약) 식별자 — 네트워크로 int 로 전송되므로 값은 바꾸지 말고 뒤에만 추가한다.</summary>
public enum VowId
{
    None = 0,
    SlowMove = 1,       // 저속
    LowJump = 2,        // 저점프
    HeavyGravity = 3,   // 고중력
    JumpCooldown = 4,   // 점프 쿨다운
    JumpLimit = 5,      // 점프 횟수 제한 (시도당)
    NoAirControl = 6,   // 공중 제어 금지
    BigBody = 7,        // 큰 몸집
    Slippery = 8,       // 미끄러운 발
}

/// <summary>뜻 적용 시점에 넘겨주는 문맥. 뜻은 이 안의 공개 파라미터만 바꾼다.</summary>
public class VowContext
{
    public PlayerController Player;
    public PlaySession Session;
}

/// <summary>뜻 정의 1개. 새 뜻 = VowId 추가 + VowCatalog.All 에 항목 1개 추가.</summary>
public class VowDef
{
    public VowId Id;
    public string Name;
    public string Description;
    /// <summary>1 = 가벼움, 2 = 보통, 3 = 강함 — 보상 계수·후보 구성에 쓸 예정 (Docs/101 뜻 보상)</summary>
    public int Tier;
    /// <summary>플레이어 스폰 직후 1회 호출. PlayerController 의 공개 파라미터를 바꾼다.</summary>
    public Action<VowContext> Apply;
    /// <summary>HUD 에 붙일 실시간 상태 (남은 점프 수 등). null 이면 이름만 표시.</summary>
    public Func<VowContext, string> Status;
}

/// <summary>
/// 뜻 카탈로그 (Docs/100 4.1, Docs/202 2장). 조작 사양이 확정된 뒤 그 파라미터를 손잡이로 쓰는 뜻들.
/// 뜻은 "검증 플레이 = 상대의 뜻, 교환 플레이 = 자기 뜻" 으로 PlaySession 이 적용한다.
/// 선택 가능 여부 검사(조합 금지 등)는 후속 작업 — 여기서는 정의와 적용만 담당.
/// </summary>
public static class VowCatalog
{
    public static readonly List<VowDef> All = new List<VowDef>
    {
        new VowDef { Id = VowId.SlowMove, Name = "저속", Tier = 1, Description = "이동 속도가 절반이 됩니다.",
            Apply = c => c.Player.MoveSpeed *= 0.5f },

        new VowDef { Id = VowId.LowJump, Name = "저점프", Tier = 1, Description = "점프 높이가 절반이 됩니다.",
            Apply = c => c.Player.JumpSpeed *= 0.71f },   // 높이 ∝ v² → ×0.5

        new VowDef { Id = VowId.HeavyGravity, Name = "고중력", Tier = 2, Description = "중력이 1.6배. 점프가 짧고 낙하가 빠릅니다.",
            Apply = c => { c.Player.RiseGravity *= 1.6f; c.Player.FallGravity *= 1.6f; } },

        new VowDef { Id = VowId.JumpCooldown, Name = "점프 쿨다운", Tier = 2, Description = "착지 후 1초 동안 다시 뛸 수 없습니다.",
            Apply = c => c.Player.JumpCooldownAfterLanding = 1.0f,
            Status = c => c.Player.JumpCooldownRemaining > 0f ? $"쿨다운 {c.Player.JumpCooldownRemaining:0.0}s" : null },

        new VowDef { Id = VowId.JumpLimit, Name = "점프 5회", Tier = 2, Description = "시도당 점프를 5번만 할 수 있습니다. 리스폰하면 초기화.",
            Apply = c => c.Player.MaxJumpsPerAttempt = 5,
            Status = c => $"점프 {Mathf.Max(0, c.Player.MaxJumpsPerAttempt - c.Player.JumpCount)}회 남음" },

        new VowDef { Id = VowId.NoAirControl, Name = "공중 제어 금지", Tier = 2, Description = "점프한 순간의 속도가 착지까지 고정됩니다.",
            Apply = c => c.Player.AirControl = false },

        new VowDef { Id = VowId.BigBody, Name = "큰 몸집", Tier = 2, Description = "캐릭터가 1.5배 커집니다. 좁은 길이 막힙니다.",
            Apply = c => c.Player.ApplyBodyScale(1.5f) },

        new VowDef { Id = VowId.Slippery, Name = "미끄러운 발", Tier = 3, Description = "가속과 감속이 느리고 경사에서 미끄러집니다.",
            Apply = c => { c.Player.GroundAccelTime = 0.5f; c.Player.GroundDecelTime = 0.6f; c.Player.IdleFriction = 0.05f; c.Player.RefreshMaterials(); } },
    };

    // ------------------------------------------------------------------ 점수 계수 (Docs/206 2.5, 초지일관 = 뜻을 끝까지 유지)

    /// <summary>뜻 난이도 계수 — 클리어 시간에 곱한다. Tier 1 ×1.00 / 2 ×0.93 / 3 ×0.85</summary>
    public static float TierCoefficient(int tier) => tier <= 1 ? 1.00f : (tier == 2 ? 0.93f : 0.85f);

    /// <summary>선택한 뜻들의 난이도 계수 곱</summary>
    public static float TierMultiplier(IList<VowId> ids)
    {
        float m = 1f;
        if (ids == null) return m;
        foreach (var id in ids) { var d = Get(id); if (d != null) m *= TierCoefficient(d.Tier); }
        return m;
    }

    /// <summary>일관성 계수 — 같은 뜻 조합을 연속 유지한 라운드 수. 1 ×1.00 / 2 ×0.95 / 3 ×0.90 / 4+ ×0.85</summary>
    public static float ConsistencyCoefficient(int streak) => streak <= 1 ? 1.00f : (streak == 2 ? 0.95f : (streak == 3 ? 0.90f : 0.85f));

    /// <summary>순서 무관 집합 비교</summary>
    public static bool SameSet(IList<VowId> a, IList<VowId> b)
    {
        if (a == null || b == null) return a == b;
        if (a.Count != b.Count) return false;
        foreach (var id in a) if (!b.Contains(id)) return false;
        return true;
    }

    /// <summary>라운드별 뜻 이력에서 마지막 라운드까지 연속으로 같은 조합을 유지한 횟수 (이력이 비면 0)</summary>
    public static int Streak(IList<List<VowId>> history)
    {
        if (history == null || history.Count == 0) return 0;
        int n = 1;
        for (int i = history.Count - 1; i > 0; i--)
        {
            if (SameSet(history[i], history[i - 1])) n++; else break;
        }
        return n;
    }

    /// <summary>최종 곱 계수 = 난이도 × 일관성</summary>
    public static float ScoreMultiplier(IList<VowId> vows, IList<List<VowId>> history)
        => TierMultiplier(vows) * ConsistencyCoefficient(Streak(history));

    public static VowDef Get(VowId id)
    {
        foreach (var v in All) if (v.Id == id) return v;
        return null;
    }

    public static string NameOf(VowId id) => Get(id)?.Name ?? id.ToString();

    public static string NamesOf(IList<VowId> ids)
    {
        if (ids == null || ids.Count == 0) return "없음";
        var names = new List<string>();
        foreach (var id in ids) names.Add(NameOf(id));
        return string.Join(", ", names);
    }

    /// <summary>후보 뽑기: count 개를 무작위로 (count ≥ 전체면 전체, 순서만 섞음).</summary>
    public static List<VowDef> RandomCandidates(int count)
    {
        var pool = new List<VowDef>(All);
        for (int i = pool.Count - 1; i > 0; i--) { int j = UnityEngine.Random.Range(0, i + 1); var t = pool[i]; pool[i] = pool[j]; pool[j] = t; }
        if (count > 0 && count < pool.Count) pool.RemoveRange(count, pool.Count - count);
        return pool;
    }

    /// <summary>뜻 목록을 플레이어에 적용 (스폰 직후 1회).</summary>
    public static void Apply(IList<VowId> ids, PlayerController player, PlaySession session)
    {
        if (ids == null || player == null) return;
        var ctx = new VowContext { Player = player, Session = session };
        foreach (var id in ids)
        {
            var def = Get(id);
            if (def == null || def.Apply == null) continue;
            try { def.Apply(ctx); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }

    /// <summary>HUD 한 줄: "뜻: 저속 · 점프 3회 남음"</summary>
    public static string HudLine(IList<VowId> ids, PlayerController player, PlaySession session)
    {
        if (ids == null || ids.Count == 0) return "뜻: 없음";
        var ctx = new VowContext { Player = player, Session = session };
        var parts = new List<string>();
        foreach (var id in ids)
        {
            var def = Get(id);
            if (def == null) continue;
            string s = def.Status != null && player != null ? def.Status(ctx) : null;
            parts.Add(string.IsNullOrEmpty(s) ? def.Name : def.Name + " (" + s + ")");
        }
        return "뜻: " + string.Join(" · ", parts);
    }
}
