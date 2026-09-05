using System;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>한 번의 플레이(검증 또는 교환) 결과.</summary>
public class PlayResult
{
    public bool Cleared;
    public float ClearTime;
    /// <summary>1 + R키 수동 리스폰 횟수 (낙하 자동 리스폰은 미소모) — Docs/206 1장</summary>
    public int Attempts;
    /// <summary>미클리어 (기권 / 시도 제한 소진 / 시간 만료)</summary>
    public bool GaveUp;

    public PlayerRecord ToRecord() => new PlayerRecord { Cleared = Cleared, ClearTime = ClearTime, AttemptsUsed = Attempts, GaveUp = GaveUp };
}

/// <summary>
/// 플레이 1회를 담당하는 씬 무관 세션 (Docs/202 3·5장). 검증 모드(MapEditor 씬 안)와 교환 플레이 모드(Play 씬) 공용.
///   맵 로딩(콜라이더 포함) → 플레이어 스폰 → 타이머 → R 리스폰(시도 +1) → 경계 밖 낙하 자동 리스폰 → 골 도달 시 Completed.
///   ESC 또는 [에디터로 돌아가기] → Aborted (갇힘 탈출·맵 수정 목적).
/// 뜻(제약)과 시간 제한은 아직 미적용 — Play 씬 정리 시 PlayerController 입력 필터와 함께 붙인다.
/// </summary>
public class PlaySession : MonoBehaviour
{
    public string Title = "검증 플레이";
    /// <summary>0 = 제한 없음. 초과 시 미클리어(GaveUp)로 종료 — 방 설정 PlayTimeLimit (Docs/100 7.1)</summary>
    public float TimeLimit = 0f;
    /// <summary>0 = 무한. R키 시도가 이 값을 넘으면 미클리어로 종료 — 방 설정 AttemptLimit</summary>
    public int AttemptLimit = 0;
    /// <summary>true 면 ESC/버튼이 "기권"(Completed, GaveUp=true) 으로 처리된다 (교환 플레이). false 면 Aborted (검증 플레이: 에디터 복귀)</summary>
    public bool AbortMeansGiveUp = false;
    public string AbortLabel = "에디터로 돌아가기 (ESC)";
    public MapData Map { get; private set; }
    public PlayerController Player { get; private set; }
    public MapLoader Loader { get; private set; }
    public float Elapsed { get; private set; }
    public int Attempts { get; private set; } = 1;
    public bool IsFinished { get; private set; }
    public bool Cleared { get; private set; }

    /// <summary>플레이 종료 — 골 도달(Cleared) 또는 기권·시간 만료·시도 소진(GaveUp).</summary>
    public event Action<PlayResult> Completed;
    /// <summary>플레이어가 중단 (ESC / 버튼).</summary>
    public event Action Aborted;

    /// <summary>HUD (표시·연출은 PlayHud 가 담당 — 여기서는 생성·갱신 호출만)</summary>
    PlayHud _hud;
    bool _ending;
    bool _respawning;

    /// <summary>사망 후 시작점에 나타나기까지의 정지 시간 — 어디서 죽었는지 인지할 여유 (조작 구성 확정값)</summary>
    public float RespawnDelay = 0.2f;

    const float FallY = -3f;
    const float FallMargin = 3f;

    /// <summary>이 세션에 적용된 뜻 (검증 = 상대 뜻, 교환 = 내 뜻)</summary>
    public System.Collections.Generic.List<VowId> Vows { get; private set; } = new System.Collections.Generic.List<VowId>();

    public static PlaySession Begin(MapData map, string title, Transform parent = null, System.Collections.Generic.IList<VowId> vows = null)
    {
        var go = new GameObject("PlaySession");
        if (parent != null) go.transform.SetParent(parent, false);
        var s = go.AddComponent<PlaySession>();
        s.Title = title;
        if (vows != null) s.Vows.AddRange(vows);
        s.Setup(map);
        return s;
    }

    /// <summary>Begin 이후에 AbortLabel 을 바꾼 경우 HUD 버튼에 반영.</summary>
    public void RefreshAbortLabel()
    {
        if (_hud != null) _hud.SetAbortLabel(AbortLabel);
    }

    void Setup(MapData map)
    {
        Map = map;
        Loader = gameObject.AddComponent<MapLoader>();
        Loader.BuildColliders = true;
        Loader.BuildBoundaries = true;
        Loader.BuildGoal = true;
        Loader.Load(map);
        if (Loader.Goal != null) Loader.Goal.Reached += OnGoalReached;
        else Debug.LogWarning("[PlaySession] 골이 없는 맵 — 클리어 판정 불가");

        Player = PlayerController.Spawn(map.StartPos, transform);
        VowCatalog.Apply(Vows, Player, this);   // 뜻은 스폰 직후 1회 적용 — 파라미터만 바꾸므로 리스폰에도 유지된다
        if (Vows.Count > 0) Debug.Log("[PlaySession] 뜻 적용: " + VowCatalog.NamesOf(Vows));
        BuildHud();
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb != null && kb.escapeKey.wasPressedThisFrame) { Abort(); return; }
        if (IsFinished) return;

        Elapsed += Time.deltaTime;
        if (TimeLimit > 0f && Elapsed >= TimeLimit) { Elapsed = TimeLimit; Finish(false, true, "시간 만료"); return; }
        if (kb != null && kb.rKey.wasPressedThisFrame) Respawn(countsAsAttempt: true);
        CheckOutOfBounds();
        UpdateHud();
    }

    void CheckOutOfBounds()
    {
        var p = Player.transform.position;
        if (p.y < FallY || p.x > MapConstants.CanvasWidth + FallMargin || p.x < -FallMargin)
            Respawn(countsAsAttempt: false);   // 자연 패널티: 시간만 흐름 (Docs/100 7.3)
    }

    public void Respawn(bool countsAsAttempt)
    {
        if (IsFinished || _respawning) return;
        if (countsAsAttempt)
        {
            // 마지막 시도 중 R → 더 이상 시도가 없으므로 미클리어. 기록되는 Attempts 는 상한을 넘지 않는다
            if (AttemptLimit > 0 && Attempts >= AttemptLimit) { Finish(false, true, "시도 소진"); return; }
            Attempts++;
        }
        StartCoroutine(RespawnRoutine());
    }

    /// <summary>사망 연출: 그 자리에 잠깐 멈춤(타이머는 계속 흐름) → 시작점 복귀.</summary>
    System.Collections.IEnumerator RespawnRoutine()
    {
        _respawning = true;
        Player.Freeze();
        Player.Body.simulated = false;
        Player.PlayDeathFeedback();
        if (_hud != null) _hud.PlayDeathEffect();   // 화면 연출 (비네트 + 흔들림) — 규칙과 무관
        yield return new WaitForSeconds(RespawnDelay);
        if (Player != null)
        {
            Player.Respawn(Map.StartPos);
            Player.Body.simulated = true;
            if (!IsFinished) Player.Unfreeze();
        }
        _respawning = false;
    }

    void OnGoalReached(Collider2D other)
    {
        Finish(true, false, null);
    }

    void Finish(bool cleared, bool gaveUp, string reason)
    {
        if (IsFinished) return;
        IsFinished = true;
        Cleared = cleared;
        if (Player != null) Player.Freeze();
        ShowResult(cleared, reason);
        UpdateHud();
        Completed?.Invoke(new PlayResult { Cleared = cleared, ClearTime = Elapsed, Attempts = Attempts, GaveUp = gaveUp });
    }

    /// <summary>ESC/버튼. 검증 모드 → Aborted(에디터 복귀). 교환 모드(AbortMeansGiveUp) → 기권으로 종료.</summary>
    public void Abort()
    {
        if (AbortMeansGiveUp)
        {
            if (!IsFinished) Finish(false, true, "기권");
            return;
        }
        if (_ending) return;
        _ending = true;
        Aborted?.Invoke();
        End();
    }

    /// <summary>세션 정리 (맵·플레이어·HUD 제거).</summary>
    public void End()
    {
        _ending = true;
        if (this != null && gameObject != null) Destroy(gameObject);
    }

    void OnDestroy()
    {
        if (Loader != null && Loader.Goal != null) Loader.Goal.Reached -= OnGoalReached;
        if (_hud != null) Destroy(_hud.gameObject);
    }

    // ------------------------------------------------------------------ HUD (Docs/204 2.3 — 구성·연출은 PlayHud.cs)

    void BuildHud()
    {
        _hud = PlayHud.Create(this);
    }

    void UpdateHud()
    {
        if (_hud != null) _hud.Refresh();
    }

    /// <summary>종료 결과 카드. 클리어 = 팝 + 플래시, 미클리어 = 팝 + 붉은 비네트.</summary>
    void ShowResult(bool cleared, string reason)
    {
        if (_hud == null) return;
        string main = cleared ? $"클리어!  {Elapsed:0.00}초" : $"미클리어 — {reason}";
        string tries = $"시도 {Attempts}";
        string next = AbortMeansGiveUp ? "상대 결과를 기다리는 중" : (cleared ? "잠시 후 에디터로 돌아갑니다" : "ESC 로 에디터로 돌아갑니다");
        _hud.ShowResult(cleared, main, tries + "   ·   " + next);
    }
}
