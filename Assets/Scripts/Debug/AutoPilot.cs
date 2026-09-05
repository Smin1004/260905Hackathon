#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using UnityEngine;

/// <summary>
/// 자동 테스트 파일럿 — 개발 빌드/에디터 전용. 커맨드라인 인자로 매치 전 과정을 사람 손 없이 진행한다.
///   Game.exe -autohost [-nick 이름]              : 방을 만들고 코드를 로그에 출력, 맵 자동 제작·확정, 교환 플레이 자동 클리어
///   Game.exe -autojoin ABC123 [-nick 이름]       : 코드로 참가 후 동일
///   -autodelay 초                                : 각 단계 사이 대기 (기본 1.5)
/// 두 프로세스를 이 인자로 띄우면 맵 교환 → 결과 화면까지 자동으로 끝난다 (Docs/205 8장 체크리스트 자동화).
/// 릴리즈 빌드에는 컴파일되지 않는다.
/// </summary>
public class AutoPilot : MonoBehaviour
{
    public static bool TryStartFromCommandLine(GameFlow flow)
    {
        var args = Environment.GetCommandLineArgs();
        bool host = Array.Exists(args, a => string.Equals(a, "-autohost", StringComparison.OrdinalIgnoreCase));
        string join = GetArg(args, "-autojoin");
        if (!host && string.IsNullOrEmpty(join)) return false;
        float delay; if (!float.TryParse(GetArg(args, "-autodelay") ?? "1.5", out delay)) delay = 1.5f;
        Start(flow, host, join, GetArg(args, "-nick"), delay);
        return true;
    }

    /// <summary>코드에서 직접 시작 (에디터 테스트용). host=true 면 방 생성, 아니면 joinCode 로 참가.</summary>
    public static AutoPilot Start(GameFlow flow, bool host, string joinCode, string nick = null, float delay = 1.5f)
    {
        var go = new GameObject("AutoPilot");
        DontDestroyOnLoad(go);
        var ap = go.AddComponent<AutoPilot>();
        ap._flow = flow;
        ap._host = host;
        ap._joinCode = joinCode;
        ap._nick = nick ?? (host ? "오토호스트" : "오토게스트");
        ap._delay = delay;
        ap.StartCoroutine(ap.Run());
        return ap;
    }

    GameFlow _flow;
    bool _host;
    string _joinCode, _nick;
    float _delay = 1.5f;

    IEnumerator Run()
    {
        Log("시작 — " + (_host ? "호스트" : "참가 " + _joinCode));
        while (_flow != null && !_flow.NetReady) yield return null;
        if (_flow == null) { Destroy(gameObject); yield break; }
        yield return new WaitForSeconds(0.5f);

        _flow.Nickname = _nick;
        if (_host) _flow.CreateRoom(); else _flow.JoinRoom(_joinCode);

        // 방 코드 출력 (호스트)
        float t = 0f;
        while (_flow.State == MatchState.Lobby && t < 30f) { t += Time.deltaTime; yield return null; }
        if (_flow.State == MatchState.Lobby) { Log("실패: 방 생성/참가 안 됨 — " + _flow.LastError); yield break; }
        if (_host) Log("ROOM CODE: " + NetService.Instance.RoomCode);

        // 상대 접속 → MapEdit
        while (_flow.State == MatchState.WaitingOpponent) yield return null;
        if (_flow.State != MatchState.MapEdit) { Log("실패: 상태 " + _flow.State + " — " + _flow.LastError); yield break; }
        while (_flow.Editor == null) yield return null;
        yield return new WaitForSeconds(_delay);

        // 자동 맵 제작: 호스트/게스트가 서로 다른 맵을 그린다 (수신 검증용)
        var ed = _flow.Editor;
        var pts = new System.Collections.Generic.List<Vector2>();
        if (_host)
        {
            for (int i = 0; i <= 40; i++) pts.Add(new Vector2(5f + i * 0.2f, 2f + Mathf.Sin(i * 0.3f)));
            ed.SetWidthIndex(2); ed.SetColorId(3); ed.AddStroke(pts);
            ed.SetWidthIndex(1); ed.SetColorId(0); ed.AddStroke(new System.Collections.Generic.List<Vector2> { new Vector2(16f, 0f), new Vector2(16f, 3f) });
            ed.SetGoal(new Vector2(22f, 1f));
        }
        else
        {
            for (int i = 0; i <= 30; i++) pts.Add(new Vector2(4f + i * 0.3f, 4f + 0.05f * i));
            ed.SetWidthIndex(0); ed.SetColorId(5); ed.AddStroke(pts);
            ed.SetWidthIndex(1); ed.SetColorId(4); ed.AddStroke(new System.Collections.Generic.List<Vector2> { new Vector2(10f, 0f), new Vector2(12f, 2f), new Vector2(14f, 0f) });
            ed.SetWidthIndex(2); ed.SetColorId(1); ed.AddStroke(new System.Collections.Generic.List<Vector2> { new Vector2(20f, 6f), new Vector2(26f, 6f) });
            ed.SetGoal(new Vector2(27f, 2f));
        }
        Log($"맵 제작: 스트로크 {ed.Map.Strokes.Count}, 점 {ed.Map.TotalPoints}");
        ed.DebugForceVerified(_host ? 12.5f : 20.25f);
        var payload = ed.Complete();
        Log("완료: payload=" + (payload == null ? "null (" + ed.Status + ")" : payload.Length + "B"));
        if (payload == null) yield break;

        // 교환 플레이 → 2초 뒤 골로 텔레포트해 클리어
        while (_flow.State == MatchState.WaitingSubmit) yield return null;
        if (_flow.State != MatchState.ExchangePlay) { Log("실패: 상태 " + _flow.State + " — " + _flow.LastError); yield break; }
        PlayBootstrap pb = null;
        while ((pb = FindFirstObjectByType<PlayBootstrap>()) == null || pb.Session == null || pb.Session.Player == null) yield return null;
        Log($"교환 플레이: 상대 맵 스트로크 {pb.Session.Map.Strokes.Count}, 점 {pb.Session.Map.TotalPoints}, 골 {pb.Session.Map.GoalPos}");
        yield return new WaitForSeconds(_delay);
        var goal = pb.Session.Map.GoalPos;
        var body = pb.Session.Player.Body;
        body.position = goal; pb.Session.Player.transform.position = new Vector3(goal.x, goal.y, 0f);

        while (_flow.State == MatchState.ExchangePlay) yield return null;
        Log("플레이 종료 → " + _flow.State);
        while (_flow.State == MatchState.WaitingResult) yield return null;
        Log("최종 상태: " + _flow.State + (string.IsNullOrEmpty(_flow.LastError) ? "" : " — " + _flow.LastError));
        Destroy(gameObject);   // 임무 완료 — 재시작 후 잔존하지 않게
    }

    static string GetArg(string[] args, string key)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    void Log(string s) => Debug.Log("[AutoPilot] " + s);
}
#endif
