using UnityEngine;

/// <summary>
/// 랭킹·승패 계산 — Docs/206 2·3장. 부작용 없는 순수 함수. Result 화면과 테스트가 같은 코드를 쓴다.
///
///   EffectiveTime : 클리어 → 클리어 시간 / 미클리어(기권·시간 만료) → PlayTimeLimit (모드 무관 — 어떤 클리어보다 항상 나쁨)
///   Score         : 패타임 모드 OFF → EffectiveTime / ON → EffectiveTime ÷ (플레이한 맵의 패타임)
///   판정          : 점수 낮은 쪽 승. 동점이면 시도 횟수 적은 쪽 승, 그래도 같으면 무승부
/// </summary>
public static class Ranking
{
    public enum Outcome { Win, Lose, Draw }

    const float Epsilon = 0.005f;

    public static float EffectiveTime(PlayerRecord r, RoomSettings s)
    {
        if (r == null || r.GaveUp || !r.Cleared) return s.PlayTimeLimit;
        return Mathf.Min(r.ClearTime, s.PlayTimeLimit);
    }

    /// <param name="playedMapParTime">그 플레이어가 플레이한 맵(= 상대가 만든 맵)의 패타임</param>
    /// <param name="vowMultiplier">뜻 난이도 × 일관성 계수 (VowCatalog.ScoreMultiplier). 클리어 기록에만 곱한다 — 미클리어는 PlayTimeLimit 그대로</param>
    public static float Score(PlayerRecord r, float playedMapParTime, RoomSettings s, float vowMultiplier = 1f)
    {
        float t = EffectiveTime(r, s);
        bool cleared = r != null && r.Cleared && !r.GaveUp;
        if (cleared) t *= Mathf.Clamp(vowMultiplier, 0.1f, 1f);
        if (s.ParTimeMode && playedMapParTime > 0.01f) return t / playedMapParTime;
        return t;
    }

    /// <summary>계수 적용 후 시간 (표시용). 미클리어면 PlayTimeLimit</summary>
    public static float AdjustedTime(PlayerRecord r, RoomSettings s, float vowMultiplier)
    {
        float t = EffectiveTime(r, s);
        bool cleared = r != null && r.Cleared && !r.GaveUp;
        return cleared ? t * Mathf.Clamp(vowMultiplier, 0.1f, 1f) : t;
    }

    /// <summary>내 관점의 판정. mine 은 내가 상대 맵을 플레이한 기록, theirs 는 상대가 내 맵을 플레이한 기록.</summary>
    public static Outcome Judge(PlayerRecord mine, float opponentMapParTime, PlayerRecord theirs, float myMapParTime, RoomSettings s, float myVowMultiplier = 1f, float theirVowMultiplier = 1f)
    {
        // 클리어가 미클리어를 항상 이긴다. 두 플레이어는 서로 다른 맵(다른 패타임)을 플레이하므로
        // 패타임 모드에서는 "미클리어 = PlayTimeLimit" 만으로는 이 불변식이 보장되지 않아 명시적으로 먼저 판정한다.
        bool myClear = mine != null && mine.Cleared && !mine.GaveUp;
        bool theirClear = theirs != null && theirs.Cleared && !theirs.GaveUp;
        if (myClear && !theirClear) return Outcome.Win;
        if (theirClear && !myClear) return Outcome.Lose;

        float my = Score(mine, opponentMapParTime, s, myVowMultiplier);
        float their = Score(theirs, myMapParTime, s, theirVowMultiplier);
        if (my < their - Epsilon) return Outcome.Win;
        if (their < my - Epsilon) return Outcome.Lose;
        int myAttempts = mine?.AttemptsUsed ?? int.MaxValue;
        int theirAttempts = theirs?.AttemptsUsed ?? int.MaxValue;
        if (myAttempts < theirAttempts) return Outcome.Win;
        if (theirAttempts < myAttempts) return Outcome.Lose;
        return Outcome.Draw;
    }

    public static string OutcomeText(Outcome o) => o switch
    {
        Outcome.Win => "승리",
        Outcome.Lose => "패배",
        _ => "무승부",
    };

    public static string RecordText(PlayerRecord r, RoomSettings s)
    {
        if (r == null) return "기록 없음";
        if (r.GaveUp || !r.Cleared) return $"미클리어 (기록 {s.PlayTimeLimit:0}초 처리, 시도 {r.AttemptsUsed})";
        return $"{r.ClearTime:0.00}초 (시도 {r.AttemptsUsed})";
    }
}
