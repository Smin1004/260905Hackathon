using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public enum MatchState { Lobby, WaitingOpponent, MapEdit, WaitingSubmit, ExchangePlay, WaitingResult, Result, Aborted }

/// <summary>
/// 매치 흐름 FSM (Boot 씬, Docs/205 4장). Boot 씬은 항상 남고 MapEditor / Play 씬을 애디티브로 얹는다.
///
///   Lobby → (방 생성/참가) → WaitingOpponent → (Hello 교환) → MapEdit
///   → (내 맵 완료: 전송 + 잠금) WaitingSubmit → (내 제출 + 상대 맵 수신) ExchangePlay
///   → (내 플레이 종료: 결과 전송) WaitingResult → (상대 결과 수신) Result
///   끊김 → Aborted (매치 무효)
///
/// 로비·대기 오버레이·결과 화면은 런타임 UI 플레이스홀더 (Docs/204). 씬 코드는 NetService API 만 사용한다.
/// </summary>
public class GameFlow : MonoBehaviour
{
    public static GameFlow Instance { get; private set; }

    public MatchState State { get; private set; } = MatchState.Lobby;
    public event Action<MatchState> StateChanged;
    public string Nickname { get; set; } = "플레이어";
    public bool NetReady => _net != null && _net.IsInitialized;
    public MapEditorController Editor { get; private set; }
    public string LastError { get; private set; }

    const string SceneMapEditor = "MapEditor";
    const string ScenePlay = "Play";

    NetService _net;
    MatchData _data;
    Camera _bootCamera;
    bool _mySubmitted, _opponentMapReceived, _myResultSent, _opponentResultReceived;
    bool _busy;
    bool _leaving;
    bool _editorLoadStarted;
    AsyncOperation _pendingLoad;   // 진행 중인 애디티브 로드 — 중단 시 완료를 기다려 정리

    // UI
    Canvas _canvas;
    GameObject _lobbyPanel, _waitPanel, _resultPanel;
    Text _lobbyStatus, _roomCodeText, _waitText, _resultTitle, _resultBody;
    InputField _nickInput, _codeInput;
    Button _createBtn, _joinBtn, _leaveBtn;

    // ------------------------------------------------------------------ lifecycle

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        Application.runInBackground = true;

        _bootCamera = Camera.main;
        _data = MatchData.Instance;
        _net = NetService.Instance;
        _net.StatusChanged += OnNetStatus;
        _net.OpponentReady += OnOpponentReady;
        _net.MapChunkProgress += OnMapChunkProgress;
        _net.MapReceived += OnOpponentMapReceived;
        _net.ResultReceived += OnOpponentResult;
        _net.MatchAborted += OnMatchAborted;
        PlayBootstrap.Finished += OnMyPlayFinished;

        EnsureEventSystem();
        BuildUI();
        SetState(MatchState.Lobby);
    }

    async void Start()
    {
        try
        {
            await _net.Init();
            SetLobbyStatus("준비 완료. 방을 만들거나 코드로 참가하세요.");
            _createBtn.interactable = true;
            _joinBtn.interactable = true;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            AutoPilot.TryStartFromCommandLine(this);
#endif
        }
        catch (Exception e)
        {
            LastError = e.Message;
            SetLobbyStatus("네트워크 초기화 실패: " + e.Message + "\nProject Settings > Services 연결과 인터넷 상태를 확인하세요.");
            Debug.LogException(e);
        }
    }

    void OnDestroy()
    {
        if (_net != null)
        {
            _net.StatusChanged -= OnNetStatus;
            _net.OpponentReady -= OnOpponentReady;
            _net.MapChunkProgress -= OnMapChunkProgress;
            _net.MapReceived -= OnOpponentMapReceived;
            _net.ResultReceived -= OnOpponentResult;
            _net.MatchAborted -= OnMatchAborted;
        }
        PlayBootstrap.Finished -= OnMyPlayFinished;
        if (Instance == this) Instance = null;
    }

    // ------------------------------------------------------------------ lobby actions (UI / AutoPilot)

    public async void CreateRoom()
    {
        if (_busy || State != MatchState.Lobby) return;
        try
        {
            _busy = true; SetLobbyButtons(false);
            _data.MyNickname = Nickname = ReadNickname("플레이어1");
            string code = await _net.CreateRoom(_data.Settings, Nickname);
            _roomCodeText.text = "방 코드: " + code;
            SetState(MatchState.WaitingOpponent);
            SetLobbyStatus("상대에게 방 코드를 알려주세요. 접속하면 자동으로 시작됩니다.");
            _leaveBtn.gameObject.SetActive(true);
            if (_net.IsOpponentReady) OnOpponentReady(_net.OpponentNickname, _net.Settings);   // 대기 상태 전에 Hello 가 끝난 경우
        }
        catch (Exception e)
        {
            LastError = e.Message;
            SetLobbyStatus("방 생성 실패: " + e.Message);
            Debug.LogException(e);
            SetLobbyButtons(true);
        }
        finally { _busy = false; }
    }

    public async void JoinRoom(string code)
    {
        if (_busy || State != MatchState.Lobby) return;
        try
        {
            _busy = true; SetLobbyButtons(false);
            _data.MyNickname = Nickname = ReadNickname("플레이어2");
            await _net.JoinRoom(code, Nickname);
            _roomCodeText.text = "방 코드: " + _net.RoomCode;
            SetState(MatchState.WaitingOpponent);
            SetLobbyStatus("호스트와 연결 중...");
            _leaveBtn.gameObject.SetActive(true);
            if (_net.IsOpponentReady) OnOpponentReady(_net.OpponentNickname, _net.Settings);
        }
        catch (Exception e)
        {
            LastError = e.Message;
            SetLobbyStatus("참가 실패: " + e.Message);
            Debug.LogException(e);
            SetLobbyButtons(true);
        }
        finally { _busy = false; }
    }

    public async void LeaveToLobby()
    {
        if (_leaving) return;
        _leaving = true;
        if (_leaveBtn != null) _leaveBtn.interactable = false;
        try { await _net.Leave(); } catch (Exception e) { Debug.LogException(e); }
        if (this == null) return;
        StartCoroutine(RestartRoutine());
    }

    /// <summary>전부 초기화하고 Boot 씬을 다시 로드 (DontDestroyOnLoad 싱글턴 제거 포함).</summary>
    public void RestartToLobby()
    {
        if (_leaving) return;
        _leaving = true;
        StartCoroutine(RestartRoutine());
    }

    IEnumerator RestartRoutine()
    {
        while (_pendingLoad != null && !_pendingLoad.isDone) yield return null;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        foreach (var ap in FindObjectsByType<AutoPilot>(FindObjectsSortMode.None)) Destroy(ap.gameObject);
#endif
        var unloads = new System.Collections.Generic.List<AsyncOperation>();
        var me = SceneManager.GetSceneByName(SceneMapEditor); if (me.isLoaded) unloads.Add(SceneManager.UnloadSceneAsync(me));
        var pl = SceneManager.GetSceneByName(ScenePlay); if (pl.isLoaded) unloads.Add(SceneManager.UnloadSceneAsync(pl));
        foreach (var op in unloads) while (op != null && !op.isDone) yield return null;

        var leave = _net.Leave();
        while (!leave.IsCompleted) yield return null;

        if (_data != null) Destroy(_data.gameObject);
        Destroy(_net.gameObject);
        var nm = Unity.Netcode.NetworkManager.Singleton;
        if (nm != null) Destroy(nm.gameObject);
        if (_canvas != null) Destroy(_canvas.gameObject);
        Destroy(gameObject);
        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex, LoadSceneMode.Single);
    }

    string ReadNickname(string fallback)
    {
        var n = _nickInput != null ? _nickInput.text.Trim() : Nickname;
        if (string.IsNullOrEmpty(n) || n == "플레이어") n = Nickname != "플레이어" ? Nickname : fallback;
        return n;
    }

    // ------------------------------------------------------------------ net events

    void OnNetStatus(string s) { if (State == MatchState.Lobby || State == MatchState.WaitingOpponent) SetLobbyStatus(s); }

    void OnOpponentReady(string nickname, RoomSettings settings)
    {
        _data.OpponentNickname = nickname;
        _data.Settings = settings;
        if (_editorLoadStarted || State == MatchState.Aborted) return;
        if (State == MatchState.WaitingOpponent || (State == MatchState.Lobby && _busy))
        {
            _editorLoadStarted = true;
            StartCoroutine(LoadMapEditor());
        }
    }

    void OnMapChunkProgress(int received, int total)
    {
        if (State == MatchState.WaitingSubmit || State == MatchState.MapEdit)
            SetWaitText($"상대 맵 수신 중 {received}/{total}");
    }

    void OnOpponentMapReceived(MapData map, float parTime)
    {
        if (State == MatchState.Aborted) return;
        _data.OpponentMap = map;
        _data.OpponentParTime = parTime;
        _opponentMapReceived = true;
        Debug.Log($"[GameFlow] 상대 맵 수신: 스트로크 {map.Strokes.Count}, 패타임 {parTime:0.00}");
        TryStartExchange();
    }

    void OnOpponentResult(PlayerRecord r)
    {
        if (State == MatchState.Aborted) return;
        _data.OpponentResult = r;
        _opponentResultReceived = true;
        TryShowResult();
    }

    void OnMatchAborted(string reason)
    {
        if (State == MatchState.Result || State == MatchState.Aborted) return;
        LastError = reason;
        StartCoroutine(AbortRoutine(reason));
    }

    IEnumerator AbortRoutine(string reason)
    {
        SetState(MatchState.Aborted);
        yield return UnloadContentScenes();
        ShowPanel(_resultPanel);
        _resultTitle.text = "매치 무효";
        _resultBody.text = reason + "\n\n남은 매치는 무효 처리됩니다 (Docs/100 8장). 로비로 돌아가 다시 시작하세요.";
    }

    // ------------------------------------------------------------------ flow

    IEnumerator LoadMapEditor()
    {
        SetState(MatchState.MapEdit);
        ShowPanel(null);
        SetBootCamera(false);
        _pendingLoad = SceneManager.LoadSceneAsync(SceneMapEditor, LoadSceneMode.Additive);
        while (!_pendingLoad.isDone) yield return null;
        _pendingLoad = null;
        yield return null;   // Awake/Start 완료 대기
        if (State == MatchState.Aborted) { yield return UnloadContentScenes(); yield break; }

        Editor = FindFirstObjectByType<MapEditorController>();
        if (Editor == null) { OnMatchAborted("MapEditor 씬에 MapEditorController 가 없습니다."); yield break; }
        Editor.Completed += OnMyMapCompleted;
        Debug.Log($"[GameFlow] 맵 제작 시작 — 상대 {_data.OpponentNickname}, 플레이 시간 제한 {_data.Settings.PlayTimeLimit}s");
    }

    void OnMyMapCompleted(MapData map, byte[] payload)
    {
        if (_mySubmitted || State == MatchState.Aborted) return;
        _mySubmitted = true;
        _data.MyMap = map;
        _data.MyParTime = Editor != null ? Editor.VerifiedParTime : _data.MyParTime;
        if (Editor != null) Editor.SetLocked(true);
        _net.SendMap(payload, _data.MyParTime);
        SetState(MatchState.WaitingSubmit);
        ShowPanel(_waitPanel);
        SetWaitText(_opponentMapReceived ? "상대 맵 수신 완료 — 곧 시작합니다" : "맵 제출 완료. 상대가 맵을 완성할 때까지 기다립니다...");
        TryStartExchange();
    }

    void TryStartExchange()
    {
        if (State == MatchState.ExchangePlay || State == MatchState.WaitingResult || State == MatchState.Result || State == MatchState.Aborted) return;
        if (!_mySubmitted || !_opponentMapReceived) return;
        StartCoroutine(StartExchange());
    }

    IEnumerator StartExchange()
    {
        SetState(MatchState.ExchangePlay);
        SetWaitText("양쪽 맵 준비 완료 — 상대의 맵을 플레이합니다");
        if (Editor != null) { Editor.Completed -= OnMyMapCompleted; Editor = null; }
        var me = SceneManager.GetSceneByName(SceneMapEditor);
        if (me.isLoaded) { var un = SceneManager.UnloadSceneAsync(me); while (!un.isDone) yield return null; }
        if (State == MatchState.Aborted) yield break;
        ShowPanel(null);
        _pendingLoad = SceneManager.LoadSceneAsync(ScenePlay, LoadSceneMode.Additive);
        while (!_pendingLoad.isDone) yield return null;
        _pendingLoad = null;
        if (State == MatchState.Aborted) { yield return UnloadContentScenes(); yield break; }
        Debug.Log("[GameFlow] 교환 플레이 시작");
    }

    void OnMyPlayFinished(PlayResult r)
    {
        if (_myResultSent || State == MatchState.Aborted) return;
        _myResultSent = true;
        _data.MyResult = r.ToRecord();
        _net.SendResult(_data.MyResult);
        SetState(MatchState.WaitingResult);
        if (!_opponentResultReceived) { ShowPanel(_waitPanel); SetWaitText("내 기록 전송 완료. 상대의 플레이가 끝날 때까지 기다립니다..."); }
        TryShowResult();
    }

    void TryShowResult()
    {
        if (State == MatchState.Result || State == MatchState.Aborted) return;
        if (!_myResultSent || !_opponentResultReceived) return;
        StartCoroutine(ShowResult());
    }

    IEnumerator ShowResult()
    {
        SetState(MatchState.Result);
        yield return UnloadContentScenes();
        ShowPanel(_resultPanel);

        var s = _data.Settings;
        var outcome = Ranking.Judge(_data.MyResult, _data.OpponentParTime, _data.OpponentResult, _data.MyParTime, s);
        _resultTitle.text = Ranking.OutcomeText(outcome);
        string par = s.ParTimeMode
            ? $"\n\n패타임 모드: 내 점수 {Ranking.Score(_data.MyResult, _data.OpponentParTime, s):0.00}  /  상대 점수 {Ranking.Score(_data.OpponentResult, _data.MyParTime, s):0.00}"
            : "";
        _resultBody.text =
            $"{_data.MyNickname} (나) — {_data.OpponentNickname}의 맵: {Ranking.RecordText(_data.MyResult, s)}   (맵 패타임 {_data.OpponentParTime:0.00}s)\n" +
            $"{_data.OpponentNickname} — {_data.MyNickname}의 맵: {Ranking.RecordText(_data.OpponentResult, s)}   (맵 패타임 {_data.MyParTime:0.00}s)" + par;
        Debug.Log($"[GameFlow] 결과: {_resultTitle.text} | {_resultBody.text.Replace('\n', ' ')}");
    }

    IEnumerator UnloadContentScenes()
    {
        while (_pendingLoad != null && !_pendingLoad.isDone) yield return null;   // 로드 중이던 씬이 있으면 끝난 뒤 내린다
        _pendingLoad = null;
        var me = SceneManager.GetSceneByName(SceneMapEditor);
        if (me.isLoaded) { var un = SceneManager.UnloadSceneAsync(me); while (!un.isDone) yield return null; }
        var pl = SceneManager.GetSceneByName(ScenePlay);
        if (pl.isLoaded) { var un = SceneManager.UnloadSceneAsync(pl); while (!un.isDone) yield return null; }
        SetBootCamera(true);
    }

    void SetState(MatchState s)
    {
        State = s;
        Debug.Log("[GameFlow] state → " + s);
        StateChanged?.Invoke(s);
    }

    void SetBootCamera(bool on)
    {
        if (_bootCamera != null) _bootCamera.gameObject.SetActive(on);
    }

    // ------------------------------------------------------------------ UI (플레이스홀더 — Docs/204 2.1 / 2.4)

    void BuildUI()
    {
        _canvas = RuntimeUI.Canvas("Boot UI (runtime)", 300);
        var root = _canvas.transform;

        // ---- 로비
        _lobbyPanel = RuntimeUI.Panel(root, Vector2.zero, Vector2.one, new Color(0.10f, 0.11f, 0.14f)).gameObject;
        var lp = _lobbyPanel.transform;
        RuntimeUI.Label(lp, new Vector2(0f, 0.84f), new Vector2(1f, 0.96f), "초지일관", 64, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
        RuntimeUI.Label(lp, new Vector2(0f, 0.78f), new Vector2(1f, 0.85f), "스스로 걸은 뜻을 지킨 채, 상대가 그린 맵을 클리어하라", 24, TextAnchor.MiddleCenter, new Color(0.8f, 0.8f, 0.85f));

        RuntimeUI.Label(lp, new Vector2(0.30f, 0.66f), new Vector2(0.40f, 0.72f), "닉네임", 24, TextAnchor.MiddleRight, Color.gray);
        _nickInput = RuntimeUI.Input(lp, new Vector2(0.41f, 0.655f), new Vector2(0.70f, 0.725f), "플레이어");
        _nickInput.characterLimit = 12;

        _createBtn = RuntimeUI.Button(lp, new Vector2(0.30f, 0.54f), new Vector2(0.70f, 0.62f), "방 만들기", CreateRoom, new Color(0.25f, 0.55f, 0.95f), 30);
        _codeInput = RuntimeUI.Input(lp, new Vector2(0.30f, 0.44f), new Vector2(0.55f, 0.51f), "방 코드 6자리");
        _codeInput.characterLimit = 8;
        _joinBtn = RuntimeUI.Button(lp, new Vector2(0.56f, 0.44f), new Vector2(0.70f, 0.51f), "참가", () => JoinRoom(_codeInput.text), new Color(0.20f, 0.65f, 0.40f), 26);
        _createBtn.interactable = false; _joinBtn.interactable = false;

        _roomCodeText = RuntimeUI.Label(lp, new Vector2(0f, 0.30f), new Vector2(1f, 0.40f), "", 56, TextAnchor.MiddleCenter, new Color(0.5f, 1f, 0.6f), FontStyle.Bold);
        _lobbyStatus = RuntimeUI.Label(lp, new Vector2(0.1f, 0.18f), new Vector2(0.9f, 0.29f), "Unity Services 초기화 중...", 24, TextAnchor.MiddleCenter, new Color(1f, 0.9f, 0.55f));
        _leaveBtn = RuntimeUI.Button(lp, new Vector2(0.40f, 0.08f), new Vector2(0.60f, 0.15f), "나가기", LeaveToLobby, new Color(0.6f, 0.3f, 0.3f), 24);
        _leaveBtn.gameObject.SetActive(false);

        // ---- 대기 오버레이 (MapEditor / Play 위)
        _waitPanel = RuntimeUI.Panel(root, new Vector2(0.2f, 0.40f), new Vector2(0.8f, 0.60f), new Color(0.05f, 0.05f, 0.08f, 0.92f)).gameObject;
        _waitText = RuntimeUI.Label(_waitPanel.transform, new Vector2(0.03f, 0f), new Vector2(0.97f, 1f), "", 30, TextAnchor.MiddleCenter, Color.white);

        // ---- 결과
        _resultPanel = RuntimeUI.Panel(root, Vector2.zero, Vector2.one, new Color(0.10f, 0.11f, 0.14f)).gameObject;
        var rp = _resultPanel.transform;
        _resultTitle = RuntimeUI.Label(rp, new Vector2(0f, 0.70f), new Vector2(1f, 0.90f), "", 96, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
        _resultBody = RuntimeUI.Label(rp, new Vector2(0.08f, 0.35f), new Vector2(0.92f, 0.68f), "", 28, TextAnchor.MiddleCenter, new Color(0.9f, 0.9f, 0.95f));
        RuntimeUI.Button(rp, new Vector2(0.35f, 0.15f), new Vector2(0.65f, 0.24f), "로비로 돌아가기", LeaveToLobby, new Color(0.25f, 0.55f, 0.95f), 28);

        ShowPanel(_lobbyPanel);
    }

    void ShowPanel(GameObject panel)
    {
        _lobbyPanel.SetActive(panel == _lobbyPanel);
        _waitPanel.SetActive(panel == _waitPanel);
        _resultPanel.SetActive(panel == _resultPanel);
    }

    void SetLobbyStatus(string s) { if (_lobbyStatus != null) _lobbyStatus.text = s; }
    void SetWaitText(string s) { if (_waitText != null) _waitText.text = s; }
    void SetLobbyButtons(bool on) { _createBtn.interactable = on && NetReady; _joinBtn.interactable = on && NetReady; }

    static void EnsureEventSystem()
    {
        if (UnityEngine.EventSystems.EventSystem.current != null || FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() != null) return;
        var go = new GameObject("EventSystem (runtime)");
        go.AddComponent<UnityEngine.EventSystems.EventSystem>();
        go.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
    }
}
