using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public enum MatchState { Lobby, WaitingOpponent, MapEdit, WaitingSubmit, ExchangePlay, WaitingResult, Result, WaitingNextRound, Aborted }

/// <summary>
/// 매치 흐름 FSM (Boot 씬, Docs/205 4장). Boot 씬은 항상 남고 MapEditor / Play 씬을 애디티브로 얹는다.
///
///   Lobby → (방 생성/참가) → WaitingOpponent → (Hello 교환) → MapEdit
///   → (내 맵 완료: 전송 + 잠금) WaitingSubmit → (내 제출 + 상대 맵 수신) ExchangePlay
///   → (내 플레이 종료: 결과 전송) WaitingResult → (상대 결과 수신) Result
///   → [다음 라운드] 양쪽 준비 → 같은 방에서 MapEdit 부터 다시 (라운드 반복 — 방을 새로 만들지 않는다)
///   → [방 나가기] 어느 화면에서든 → 세션 종료 후 제자리 초기화 → Lobby
///   끊김 → Aborted (매치 무효). 호스트는 같은 방에서 새 상대를 기다릴 수 있다.
///
/// 로비·대기 오버레이·결과 화면은 런타임 UI 플레이스홀더 (Docs/204). 씬 코드는 NetService API 만 사용한다.
/// 싱글턴을 파괴하고 씬을 재로드하는 방식은 쓰지 않는다 (네트워크 종료 대기 중 멈추는 문제) — 모든 복귀는 제자리 초기화.
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
    public int Round { get; private set; } = 1;

    const string SceneMapEditor = "MapEditor";
    const string ScenePlay = "Play";

    NetService _net;
    MatchData _data;
    Camera _bootCamera;
    bool _mySubmitted, _opponentMapReceived, _myResultSent, _opponentResultReceived;
    bool _nextRoundMine, _nextRoundTheirs;
    bool _busy, _leaving, _editorLoadStarted, _transitioning;
    AsyncOperation _pendingLoad;   // 진행 중인 애디티브 로드 — 중단 시 완료를 기다려 정리

    // UI
    Canvas _canvas;
    GameObject _lobbyPanel, _waitPanel, _resultPanel;
    Text _lobbyStatus, _roomCodeText, _waitText, _resultTitle, _resultBody, _roomBarText;
    InputField _nickInput, _codeInput;
    Button _createBtn, _joinBtn, _lobbyLeaveBtn, _nextRoundBtn, _resultLeaveBtn, _waitNewBtn, _roomBarLeaveBtn;
    GameObject _roomBar;

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
        _net.NextRoundReceived += OnOpponentNextRound;
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
            if (this == null) return;
            SetLobbyStatus("준비 완료. 방을 만들거나 코드로 참가하세요.");
            SetLobbyButtons(true);
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
            _net.NextRoundReceived -= OnOpponentNextRound;
            _net.MatchAborted -= OnMatchAborted;
        }
        PlayBootstrap.Finished -= OnMyPlayFinished;
        if (Instance == this) Instance = null;
    }

    // ------------------------------------------------------------------ lobby actions (UI / AutoPilot)

    public async void CreateRoom()
    {
        if (_busy || _leaving || State != MatchState.Lobby) return;
        try
        {
            _busy = true; SetLobbyButtons(false);
            _data.MyNickname = Nickname = ReadNickname("플레이어1");
            string code = await _net.CreateRoom(_data.Settings, Nickname);
            if (this == null) return;
            _roomCodeText.text = "방 코드: " + code;
            SetState(MatchState.WaitingOpponent);
            SetLobbyStatus("상대에게 방 코드를 알려주세요. 접속하면 자동으로 시작됩니다.");
            _lobbyLeaveBtn.gameObject.SetActive(true);
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
        if (_busy || _leaving || State != MatchState.Lobby) return;
        try
        {
            _busy = true; SetLobbyButtons(false);
            _data.MyNickname = Nickname = ReadNickname("플레이어2");
            await _net.JoinRoom(code, Nickname);
            if (this == null) return;
            _roomCodeText.text = "방 코드: " + _net.RoomCode;
            SetState(MatchState.WaitingOpponent);
            SetLobbyStatus("호스트와 연결 중...");
            _lobbyLeaveBtn.gameObject.SetActive(true);
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

    /// <summary>방 나가기 — 어느 상태에서든. 세션 종료(타임아웃 포함) 후 제자리 초기화로 로비 복귀.</summary>
    public void LeaveRoom()
    {
        if (_leaving) return;
        StartCoroutine(LeaveRoutine());
    }

    IEnumerator LeaveRoutine()
    {
        _leaving = true;
        SetState(MatchState.Aborted);            // 진행 중인 콜백·코루틴이 더 이상 씬을 만들지 않게
        ShowPanel(_waitPanel);
        SetWaitText("방을 나가는 중...");
        if (_roomBar != null) _roomBar.SetActive(false);

        yield return UnloadContentScenes();

        var leave = _net.Leave();              // 내부에 타임아웃 — 상대가 먼저 나가 NGO 가 멈춘 경우에도 끝난다
        float t = 0f;
        while (!leave.IsCompleted && t < 8f) { t += Time.deltaTime; yield return null; }
        if (!leave.IsCompleted) Debug.LogWarning("[GameFlow] Leave 가 8초 안에 끝나지 않아 로컬 초기화만 진행");

        ResetAll();
        SetState(MatchState.Lobby);
        ShowPanel(_lobbyPanel);
        _roomCodeText.text = "";
        _lobbyLeaveBtn.gameObject.SetActive(false);
        SetLobbyStatus("방을 나왔습니다. 새 방을 만들거나 코드로 참가하세요.");
        SetLobbyButtons(true);
        _leaving = false;
    }

    /// <summary>[다음 라운드] — 같은 방에서 새 맵으로. 양쪽이 모두 누르면 시작.</summary>
    public void RequestNextRound()
    {
        if (State != MatchState.Result && State != MatchState.WaitingNextRound) return;
        if (_nextRoundMine) return;
        _nextRoundMine = true;
        _net.SendNextRound();
        SetState(MatchState.WaitingNextRound);
        if (_nextRoundBtn != null) { _nextRoundBtn.interactable = false; _nextRoundBtn.GetComponentInChildren<Text>().text = "상대 대기 중..."; }
        TryStartNextRound();
    }

    /// <summary>상대가 나간 뒤(Aborted) 호스트가 같은 방에서 새 상대를 기다린다.</summary>
    public void WaitForNewOpponent()
    {
        if (State != MatchState.Aborted || _leaving) return;
        if (!_net.PrepareForNewOpponent()) { SetWaitText("이 방은 더 이상 사용할 수 없습니다. 방을 나가세요."); return; }
        ResetRound();
        _data.OpponentNickname = "";
        Round = 1;
        LastError = "";
        SetState(MatchState.WaitingOpponent);
        ShowPanel(_lobbyPanel);
        _roomCodeText.text = "방 코드: " + _net.RoomCode;
        _lobbyLeaveBtn.gameObject.SetActive(true);
        SetLobbyStatus("상대가 나갔습니다. 같은 방 코드로 새 상대를 기다립니다.");
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
        if (_editorLoadStarted || _leaving || State == MatchState.Aborted) return;
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
        if (State == MatchState.Aborted || _leaving) return;
        _data.OpponentMap = map;
        _data.OpponentParTime = parTime;
        _opponentMapReceived = true;
        Debug.Log($"[GameFlow] 상대 맵 수신: 스트로크 {map.Strokes.Count}, 패타임 {parTime:0.00}");
        TryStartExchange();
    }

    void OnOpponentResult(PlayerRecord r)
    {
        if (State == MatchState.Aborted || _leaving) return;
        _data.OpponentResult = r;
        _opponentResultReceived = true;
        TryShowResult();
    }

    void OnOpponentNextRound()
    {
        if (State == MatchState.Aborted || _leaving) return;
        _nextRoundTheirs = true;
        if (State == MatchState.WaitingNextRound) SetResultHint("상대 준비 완료 — 시작합니다");
        else if (State == MatchState.Result) SetResultHint("상대가 다음 라운드를 기다리고 있습니다");
        TryStartNextRound();
    }

    void OnMatchAborted(string reason)
    {
        if (_leaving || State == MatchState.Aborted) return;
        LastError = reason;
        StartCoroutine(AbortRoutine(reason));
    }

    IEnumerator AbortRoutine(string reason)
    {
        SetState(MatchState.Aborted);
        yield return UnloadContentScenes();
        if (_leaving) yield break;
        ShowPanel(_resultPanel);
        _resultTitle.text = "매치 무효";
        _resultBody.text = reason + "\n\n남은 매치는 무효 처리됩니다 (Docs/100 8장).";
        _nextRoundBtn.gameObject.SetActive(false);
        bool canWait = _net.IsHost && _net.InSession && _net.IsNetcodeUp;
        _waitNewBtn.gameObject.SetActive(canWait);
        _resultLeaveBtn.gameObject.SetActive(true);
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
        ShowRoomBar($"라운드 {Round} · 방 {_net.RoomCode} · 상대 {_data.OpponentNickname}");
        Debug.Log($"[GameFlow] 라운드 {Round} 맵 제작 시작 — 상대 {_data.OpponentNickname}, 플레이 시간 제한 {_data.Settings.PlayTimeLimit}s");
    }

    void OnMyMapCompleted(MapData map, byte[] payload)
    {
        if (_mySubmitted || State == MatchState.Aborted || _leaving) return;
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
        if (_transitioning || _leaving) return;
        if (State != MatchState.MapEdit && State != MatchState.WaitingSubmit) return;
        if (!_mySubmitted || !_opponentMapReceived) return;
        StartCoroutine(StartExchange());
    }

    IEnumerator StartExchange()
    {
        _transitioning = true;
        SetState(MatchState.ExchangePlay);
        SetWaitText("양쪽 맵 준비 완료 — 상대의 맵을 플레이합니다");
        if (Editor != null) { Editor.Completed -= OnMyMapCompleted; Editor = null; }
        var me = SceneManager.GetSceneByName(SceneMapEditor);
        if (me.isLoaded) { var un = SceneManager.UnloadSceneAsync(me); while (!un.isDone) yield return null; }
        if (State == MatchState.Aborted || _leaving) { _transitioning = false; yield break; }
        ShowPanel(null);
        if (_roomBar != null) _roomBar.SetActive(false);   // 플레이 중에는 HUD 의 기권 버튼만
        _pendingLoad = SceneManager.LoadSceneAsync(ScenePlay, LoadSceneMode.Additive);
        while (!_pendingLoad.isDone) yield return null;
        _pendingLoad = null;
        _transitioning = false;
        if (State == MatchState.Aborted) { yield return UnloadContentScenes(); yield break; }
        Debug.Log("[GameFlow] 교환 플레이 시작");
    }

    void OnMyPlayFinished(PlayResult r)
    {
        if (_myResultSent || State == MatchState.Aborted || _leaving) return;
        _myResultSent = true;
        _data.MyResult = r.ToRecord();
        _net.SendResult(_data.MyResult);
        SetState(MatchState.WaitingResult);
        if (!_opponentResultReceived) { ShowPanel(_waitPanel); SetWaitText("내 기록 전송 완료. 상대의 플레이가 끝날 때까지 기다립니다..."); }
        TryShowResult();
    }

    void TryShowResult()
    {
        if (_transitioning || _leaving) return;
        if (State != MatchState.ExchangePlay && State != MatchState.WaitingResult) return;
        if (!_myResultSent || !_opponentResultReceived) return;
        StartCoroutine(ShowResult());
    }

    IEnumerator ShowResult()
    {
        _transitioning = true;
        SetState(MatchState.Result);
        yield return UnloadContentScenes();
        _transitioning = false;
        if (_leaving) yield break;
        ShowPanel(_resultPanel);

        var s = _data.Settings;
        var outcome = Ranking.Judge(_data.MyResult, _data.OpponentParTime, _data.OpponentResult, _data.MyParTime, s);
        _resultTitle.text = Ranking.OutcomeText(outcome);
        string par = s.ParTimeMode
            ? $"\n\n패타임 모드: 내 점수 {Ranking.Score(_data.MyResult, _data.OpponentParTime, s):0.00}  /  상대 점수 {Ranking.Score(_data.OpponentResult, _data.MyParTime, s):0.00}"
            : "";
        _resultBody.text =
            $"라운드 {Round}\n\n" +
            $"{_data.MyNickname} (나) — {_data.OpponentNickname}의 맵: {Ranking.RecordText(_data.MyResult, s)}   (맵 패타임 {_data.OpponentParTime:0.00}s)\n" +
            $"{_data.OpponentNickname} — {_data.MyNickname}의 맵: {Ranking.RecordText(_data.OpponentResult, s)}   (맵 패타임 {_data.MyParTime:0.00}s)" + par;
        _nextRoundBtn.gameObject.SetActive(true);
        _nextRoundBtn.interactable = true;
        _nextRoundBtn.GetComponentInChildren<Text>().text = "다음 라운드 (같은 방)";
        _waitNewBtn.gameObject.SetActive(false);
        _resultLeaveBtn.gameObject.SetActive(true);
        SetResultHint(_nextRoundTheirs ? "상대가 다음 라운드를 기다리고 있습니다" : "");
        Debug.Log($"[GameFlow] 결과: {_resultTitle.text} | {_resultBody.text.Replace('\n', ' ')}");
    }

    void TryStartNextRound()
    {
        if (_transitioning || _leaving || State == MatchState.Aborted) return;
        if (!_nextRoundMine || !_nextRoundTheirs) return;
        if (State != MatchState.WaitingNextRound && State != MatchState.Result) return;
        Round++;
        ResetRound();
        _net.ResetForNextRound();
        ShowPanel(null);
        Debug.Log($"[GameFlow] 라운드 {Round} 시작 (같은 방)");
        _editorLoadStarted = true;
        StartCoroutine(LoadMapEditor());
    }

    /// <summary>한 라운드의 진행 플래그·기록 초기화. 세션·상대 정보·방 설정은 유지.</summary>
    void ResetRound()
    {
        _mySubmitted = _opponentMapReceived = _myResultSent = _opponentResultReceived = false;
        _nextRoundMine = _nextRoundTheirs = false;
        _editorLoadStarted = false;
        _transitioning = false;
        if (Editor != null) { Editor.Completed -= OnMyMapCompleted; Editor = null; }
        _data.MyMap = null; _data.OpponentMap = null;
        _data.MyParTime = 0f; _data.OpponentParTime = 0f;
        _data.MyResult = null; _data.OpponentResult = null;
        SetResultHint("");
    }

    /// <summary>방을 나갈 때: 라운드 상태 + 상대 정보 초기화.</summary>
    void ResetAll()
    {
        ResetRound();
        Round = 1;
        _data.ResetMatch();
        _data.OpponentNickname = "";
        LastError = "";
    }

    IEnumerator UnloadContentScenes()
    {
        while (_pendingLoad != null && !_pendingLoad.isDone) yield return null;   // 로드 중이던 씬이 있으면 끝난 뒤 내린다
        _pendingLoad = null;
        var me = SceneManager.GetSceneByName(SceneMapEditor);
        if (me.isLoaded) { var un = SceneManager.UnloadSceneAsync(me); while (!un.isDone) yield return null; }
        var pl = SceneManager.GetSceneByName(ScenePlay);
        if (pl.isLoaded) { var un = SceneManager.UnloadSceneAsync(pl); while (!un.isDone) yield return null; }
        if (_roomBar != null) _roomBar.SetActive(false);
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
        _lobbyLeaveBtn = RuntimeUI.Button(lp, new Vector2(0.40f, 0.08f), new Vector2(0.60f, 0.15f), "방 나가기", LeaveRoom, new Color(0.6f, 0.3f, 0.3f), 24);
        _lobbyLeaveBtn.gameObject.SetActive(false);

        // ---- 대기 오버레이 (MapEditor / Play 위)
        _waitPanel = RuntimeUI.Panel(root, new Vector2(0.2f, 0.40f), new Vector2(0.8f, 0.60f), new Color(0.05f, 0.05f, 0.08f, 0.92f)).gameObject;
        _waitText = RuntimeUI.Label(_waitPanel.transform, new Vector2(0.03f, 0f), new Vector2(0.97f, 1f), "", 30, TextAnchor.MiddleCenter, Color.white);

        // ---- 방 정보 바 (에디터 화면 위, 우상단) — 라운드·방 코드·상대 + 방 나가기
        _roomBar = RuntimeUI.Panel(root, new Vector2(0.60f, 0.93f), new Vector2(1f, 1f), new Color(0.05f, 0.05f, 0.08f, 0.85f)).gameObject;
        _roomBarText = RuntimeUI.Label(_roomBar.transform, new Vector2(0.02f, 0f), new Vector2(0.72f, 1f), "", 20, TextAnchor.MiddleLeft, new Color(0.85f, 0.85f, 0.9f));
        _roomBarLeaveBtn = RuntimeUI.Button(_roomBar.transform, new Vector2(0.74f, 0.12f), new Vector2(0.98f, 0.88f), "방 나가기", LeaveRoom, new Color(0.6f, 0.3f, 0.3f), 18);
        _roomBar.SetActive(false);

        // ---- 결과 / 무효
        _resultPanel = RuntimeUI.Panel(root, Vector2.zero, Vector2.one, new Color(0.10f, 0.11f, 0.14f)).gameObject;
        var rp = _resultPanel.transform;
        _resultTitle = RuntimeUI.Label(rp, new Vector2(0f, 0.72f), new Vector2(1f, 0.92f), "", 96, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
        _resultBody = RuntimeUI.Label(rp, new Vector2(0.08f, 0.38f), new Vector2(0.92f, 0.70f), "", 28, TextAnchor.MiddleCenter, new Color(0.9f, 0.9f, 0.95f));
        _nextRoundBtn = RuntimeUI.Button(rp, new Vector2(0.20f, 0.18f), new Vector2(0.48f, 0.28f), "다음 라운드 (같은 방)", RequestNextRound, new Color(0.20f, 0.65f, 0.40f), 28);
        _waitNewBtn = RuntimeUI.Button(rp, new Vector2(0.20f, 0.18f), new Vector2(0.48f, 0.28f), "같은 방에서 새 상대 기다리기", WaitForNewOpponent, new Color(0.25f, 0.55f, 0.95f), 24);
        _resultLeaveBtn = RuntimeUI.Button(rp, new Vector2(0.52f, 0.18f), new Vector2(0.80f, 0.28f), "방 나가기", LeaveRoom, new Color(0.6f, 0.3f, 0.3f), 28);
        _resultHint = RuntimeUI.Label(rp, new Vector2(0.1f, 0.10f), new Vector2(0.9f, 0.16f), "", 22, TextAnchor.MiddleCenter, new Color(1f, 0.9f, 0.55f));
        _waitNewBtn.gameObject.SetActive(false);

        ShowPanel(_lobbyPanel);
    }

    Text _resultHint;

    void ShowPanel(GameObject panel)
    {
        _lobbyPanel.SetActive(panel == _lobbyPanel);
        _waitPanel.SetActive(panel == _waitPanel);
        _resultPanel.SetActive(panel == _resultPanel);
    }

    void ShowRoomBar(string text)
    {
        if (_roomBar == null) return;
        _roomBarText.text = text;
        _roomBar.SetActive(true);
    }

    void SetLobbyStatus(string s) { if (_lobbyStatus != null) _lobbyStatus.text = s; }
    void SetWaitText(string s) { if (_waitText != null) _waitText.text = s; }
    void SetResultHint(string s) { if (_resultHint != null) _resultHint.text = s; }
    void SetLobbyButtons(bool on) { _createBtn.interactable = on && NetReady; _joinBtn.interactable = on && NetReady; }

    static void EnsureEventSystem()
    {
        if (UnityEngine.EventSystems.EventSystem.current != null || FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() != null) return;
        var go = new GameObject("EventSystem (runtime)");
        go.AddComponent<UnityEngine.EventSystems.EventSystem>();
        go.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
    }
}
