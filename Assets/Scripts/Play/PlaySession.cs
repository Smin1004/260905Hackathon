using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>한 번의 플레이(검증 또는 교환) 결과.</summary>
public class PlayResult
{
    public bool Cleared;
    public float ClearTime;
    /// <summary>1 + R키 수동 리스폰 횟수 (낙하 자동 리스폰은 미소모) — Docs/206 1장</summary>
    public int Attempts;
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
    public MapData Map { get; private set; }
    public PlayerController Player { get; private set; }
    public MapLoader Loader { get; private set; }
    public float Elapsed { get; private set; }
    public int Attempts { get; private set; } = 1;
    public bool IsFinished { get; private set; }
    public bool Cleared { get; private set; }

    /// <summary>골 도달.</summary>
    public event Action<PlayResult> Completed;
    /// <summary>플레이어가 중단 (ESC / 버튼).</summary>
    public event Action Aborted;

    Canvas _hud;
    Text _hudText, _resultText;
    bool _ending;
    bool _respawning;

    /// <summary>사망 후 시작점에 나타나기까지의 정지 시간 — 어디서 죽었는지 인지할 여유 (조작 구성 확정값)</summary>
    public float RespawnDelay = 0.2f;

    const float FallY = -3f;
    const float FallMargin = 3f;

    public static PlaySession Begin(MapData map, string title, Transform parent = null)
    {
        var go = new GameObject("PlaySession");
        if (parent != null) go.transform.SetParent(parent, false);
        var s = go.AddComponent<PlaySession>();
        s.Title = title;
        s.Setup(map);
        return s;
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
        BuildHud();
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb != null && kb.escapeKey.wasPressedThisFrame) { Abort(); return; }
        if (IsFinished) return;

        Elapsed += Time.deltaTime;
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
        if (countsAsAttempt) Attempts++;
        StartCoroutine(RespawnRoutine());
    }

    /// <summary>사망 연출: 그 자리에 잠깐 멈춤(타이머는 계속 흐름) → 시작점 복귀.</summary>
    System.Collections.IEnumerator RespawnRoutine()
    {
        _respawning = true;
        Player.Freeze();
        Player.Body.simulated = false;
        Player.PlayDeathFeedback();
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
        if (IsFinished) return;
        IsFinished = true;
        Cleared = true;
        Player.Freeze();
        if (_resultText != null)
        {
            _resultText.gameObject.SetActive(true);
            _resultText.text = $"클리어!  {Elapsed:0.00}초  (시도 {Attempts})";
        }
        UpdateHud();
        Completed?.Invoke(new PlayResult { Cleared = true, ClearTime = Elapsed, Attempts = Attempts });
    }

    public void Abort()
    {
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

    // ------------------------------------------------------------------ HUD (플레이스홀더 — Docs/204 2.3)

    void BuildHud()
    {
        _hud = RuntimeUI.Canvas("Play HUD (runtime)", 200);
        var root = _hud.transform;

        var top = RuntimeUI.Panel(root, new Vector2(0f, 0.92f), new Vector2(1f, 1f), new Color(0.05f, 0.05f, 0.08f, 0.85f));
        _hudText = RuntimeUI.Label(top, new Vector2(0.01f, 0f), new Vector2(0.78f, 1f), "", 24, TextAnchor.MiddleLeft, Color.white);
        RuntimeUI.Button(top, new Vector2(0.80f, 0.12f), new Vector2(0.99f, 0.88f), "에디터로 돌아가기 (ESC)", Abort, new Color(0.75f, 0.35f, 0.3f));

        _resultText = RuntimeUI.Label(root, new Vector2(0.2f, 0.42f), new Vector2(0.8f, 0.58f), "", 64, TextAnchor.MiddleCenter, new Color(0.4f, 1f, 0.5f), FontStyle.Bold);
        _resultText.gameObject.SetActive(false);

        UpdateHud();
    }

    void UpdateHud()
    {
        if (_hudText == null) return;
        _hudText.text = $"{Title}   ⏱ {Elapsed:0.0}s   시도 {Attempts}   |   이동 A/D·←/→   점프 W·↑ (길게 누르면 높이)   빠른 낙하 S·↓   R 리스폰   ESC 에디터로" +
                        (IsFinished ? "   |   클리어 — 잠시 후 에디터로 돌아갑니다" : "");
    }
}
