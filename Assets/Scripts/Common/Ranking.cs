using UnityEngine;

/// <summary>
/// 랭킹·승패 계산 — Docs/206 2·3장. 부작용 없는 순수 함수. Result 화면과 테스트가 같은 코드를 쓴다.
///
///   점수(마진) = 클리어 시간 × 뜻 계수 − 패타임(그 맵 제작자의 검증 기록)  →  낮을수록 승 (음수 = 제작자보다 빨랐다)
///   패타임은 제작자가 "내 뜻"으로 자기 맵을 검증한 시간이라 같은 맵·같은 제약의 기준선이다 → 맵 길이가 달라도 공정 비교
///   판정  : 클리어가 미클리어를 항상 이긴다 / 둘 다 미클리어 = 무승부 / 둘 다 클리어 → 마진 낮은 쪽, 동점이면 시도 적은 쪽, 그래도 같으면 무승부
/// </summary>
public static class Ranking
{
    public enum Outcome { Win, Lose, Draw }

    const float Epsilon = 0.005f;

    public static bool IsCleared(PlayerRecord r) => r != null && r.Cleared && !r.GaveUp;

    /// <summary>클리어 → 클리어 시간(상한 PlayTimeLimit) / 미클리어 → PlayTimeLimit (표시용 — 판정은 클리어 여부를 먼저 본다)</summary>
    public static float EffectiveTime(PlayerRecord r, RoomSettings s)
    {
        if (!IsCleared(r)) return s.PlayTimeLimit;
        return Mathf.Min(r.ClearTime, s.PlayTimeLimit);
    }

    /// <summary>계수 적용 후 시간. 미클리어면 PlayTimeLimit</summary>
    public static float AdjustedTime(PlayerRecord r, RoomSettings s, float vowMultiplier)
    {
        float t = EffectiveTime(r, s);
        return IsCleared(r) ? t * Mathf.Clamp(vowMultiplier, 0.1f, 1f) : t;
    }

    /// <summary>
    /// 점수 = 계수 적용 시간 − 패타임. 미클리어는 의미가 없어 PlayTimeLimit 을 그대로 돌려준다 (판정에서는 쓰지 않음).
    /// </summary>
    /// <param name="playedMapParTime">그 플레이어가 플레이한 맵(= 상대가 만든 맵)의 패타임</param>
    /// <param name="vowMultiplier">뜻 난이도 × 일관성 계수 (VowCatalog.ScoreMultiplier). 클리어 기록에만 곱한다</param>
    public static float Score(PlayerRecord r, float playedMapParTime, RoomSettings s, float vowMultiplier = 1f)
    {
        if (!IsCleared(r)) return s.PlayTimeLimit;
        return AdjustedTime(r, s, vowMultiplier) - Mathf.Max(0f, playedMapParTime);
    }

    /// <summary>내 관점의 판정. mine 은 내가 상대 맵을 플레이한 기록, theirs 는 상대가 내 맵을 플레이한 기록.</summary>
    public static Outcome Judge(PlayerRecord mine, float opponentMapParTime, PlayerRecord theirs, float myMapParTime, RoomSettings s, float myVowMultiplier = 1f, float theirVowMultiplier = 1f)
    {
        bool myClear = IsCleared(mine), theirClear = IsCleared(theirs);
        if (myClear && !theirClear) return Outcome.Win;
        if (theirClear && !myClear) return Outcome.Lose;
        if (!myClear && !theirClear) return Outcome.Draw;          // 둘 다 실패 = 무승부 (패타임 차이로 갈라지지 않게)

        float my = Score(mine, opponentMapParTime, s, myVowMultiplier);
        float their = Score(theirs, myMapParTime, s, theirVowMultiplier);
        if (my < their - Epsilon) return Outcome.Win;
        if (their < my - Epsilon) return Outcome.Lose;
        if (mine.AttemptsUsed < theirs.AttemptsUsed) return Outcome.Win;
        if (theirs.AttemptsUsed < mine.AttemptsUsed) return Outcome.Lose;
        return Outcome.Draw;
    }

    public static string OutcomeText(Outcome o) => o switch
    {
        Outcome.Win => "승리",
        Outcome.Lose => "패배",
        _ => "무승부",
    };

    /// <summary>마진 표기: +2.13s / −1.40s (음수 = 패타임보다 빨랐다)</summary>
    public static string MarginText(float margin) => (margin >= 0f ? "+" : "−") + Mathf.Abs(margin).ToString("0.00") + "s";

    public static string RecordText(PlayerRecord r, RoomSettings s)
    {
        if (r == null) return "기록 없음";
        if (!IsCleared(r)) return $"미클리어 (시도 {r.AttemptsUsed})";
        return $"{r.ClearTime:0.00}초 (시도 {r.AttemptsUsed})";
    }
}
